using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace BusyBunny
{
    /// <summary>
    /// The real world for Bunny's world (sweep 8.2 H): the season from the date, the moon phase and sunset worked out
    /// on the laptop (no internet), and the weather from Open-Meteo (free, no key; checked November 2026) for the town
    /// she types in Settings, Fun and tools. Never her location found automatically. Weather is cached for an hour;
    /// any failure just gives "the weather is a mystery today".
    /// </summary>
    public static class RealWorld
    {
        public const string Mystery = "The weather is a mystery today.";

        /// <summary>Winter, Spring, Summer or Fall, by month (northern hemisphere, meteorological seasons).</summary>
        public static string Season(DateTime d) => d.Month switch
        {
            12 or 1 or 2 => "Winter",
            3 or 4 or 5 => "Spring",
            6 or 7 or 8 => "Summer",
            _ => "Fall",
        };

        /// <summary>A well-known day on this date, or null: fixed-date holidays only, so nothing personal.</summary>
        public static string? Holiday(DateTime d) => (d.Month, d.Day) switch
        {
            (1, 1) => "New Year's Day",
            (2, 14) => "Valentine's Day",
            (3, 17) => "Saint Patrick's Day",
            (4, 1) => "April Fools' Day",
            (10, 31) => "Halloween",
            (12, 24) => "Christmas Eve",
            (12, 25) => "Christmas Day",
            (12, 31) => "New Year's Eve",
            _ => null,
        };

        /// <summary>The moon's age in days (0 new, about 14.8 full) and how much of it is lit, 0 to 1.</summary>
        public static (double Age, double Lit) Moon(DateTime when)
        {
            const double synodic = 29.530588853;
            var known = new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc); // a new moon
            double days = (when.ToUniversalTime() - known).TotalDays;
            double age = ((days % synodic) + synodic) % synodic;
            double lit = (1 - Math.Cos(2 * Math.PI * age / synodic)) / 2;
            return (age, lit);
        }

        /// <summary>The moon in words, like a full moon or a waxing crescent.</summary>
        public static string MoonPhase(DateTime when)
        {
            double a = Moon(when).Age;
            return a switch
            {
                < 1.85 => "a new moon",
                < 5.54 => "a waxing crescent",
                < 9.23 => "a first quarter moon",
                < 12.92 => "a waxing gibbous moon",
                < 16.61 => "a full moon",
                < 20.30 => "a waning gibbous moon",
                < 23.99 => "a last quarter moon",
                < 27.68 => "a waning crescent",
                _ => "a new moon",
            };
        }

        /// <summary>
        /// Sunset in local time for a place (latitude north, longitude east), by the standard NOAA sunrise equation.
        /// Null when the sun does not set that day there.
        /// </summary>
        public static DateTime? Sunset(DateTime day, double lat, double lon)
        {
            double n = day.Date.ToUniversalTime().Subtract(new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
            n = Math.Round(n + 0.0008);
            double jStar = n - lon / 360.0;
            double m = (357.5291 + 0.98560028 * jStar) % 360;
            double mr = m * Math.PI / 180;
            double c = 1.9148 * Math.Sin(mr) + 0.02 * Math.Sin(2 * mr) + 0.0003 * Math.Sin(3 * mr);
            double lambda = (m + c + 180 + 102.9372) % 360 * Math.PI / 180;
            double jTransit = 2451545.0 + jStar + 0.0053 * Math.Sin(mr) - 0.0069 * Math.Sin(2 * lambda);
            double decl = Math.Asin(Math.Sin(lambda) * Math.Sin(23.4397 * Math.PI / 180));
            double latR = lat * Math.PI / 180;
            double cosH = (Math.Sin(-0.833 * Math.PI / 180) - Math.Sin(latR) * Math.Sin(decl)) / (Math.Cos(latR) * Math.Cos(decl));
            if (cosH < -1 || cosH > 1) return null;
            double h = Math.Acos(cosH) * 180 / Math.PI;
            double jSet = jTransit + h / 360;
            var utc = new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddDays(jSet - 2451545.0);
            return utc.ToLocalTime();
        }

        // ---- Weather (Open-Meteo) ----

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
        private static (DateTime At, string Words, double Lat, double Lon)? _weather;

        /// <summary>Finds a town she typed, once, when she saves it in Settings. Null when it is not found or offline.</summary>
        public static async Task<(string Name, double Lat, double Lon)?> FindTown(string town)
        {
            try
            {
                string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=en&name=" + Uri.EscapeDataString(town.Trim());
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                if (!doc.RootElement.TryGetProperty("results", out var r) || r.GetArrayLength() == 0) return null;
                var first = r[0];
                string name = first.GetProperty("name").GetString() ?? town;
                if (first.TryGetProperty("admin1", out var region) && region.GetString() is string a && a.Length > 0) name += ", " + a;
                return (name, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException) { return null; }
        }

        /// <summary>The weather now in a few words, like "It is 62 degrees and clear, with a light breeze." Mystery on any failure or with no town.</summary>
        public static async Task<string> Weather(Settings s)
        {
            if (s.WeatherTown.Length == 0 || s.WeatherLat == 0 && s.WeatherLon == 0) return Mystery;
            if (_weather is var (at, words, lat, lon) && lat == s.WeatherLat && lon == s.WeatherLon && DateTime.Now - at < TimeSpan.FromHours(1)) return words;
            try
            {
                string url = "https://api.open-meteo.com/v1/forecast?current=temperature_2m,weather_code,wind_speed_10m&temperature_unit=fahrenheit&wind_speed_unit=mph" +
                    "&latitude=" + s.WeatherLat.ToString(CultureInfo.InvariantCulture) + "&longitude=" + s.WeatherLon.ToString(CultureInfo.InvariantCulture);
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
                var cur = doc.RootElement.GetProperty("current");
                int temp = (int)Math.Round(cur.GetProperty("temperature_2m").GetDouble());
                int code = cur.GetProperty("weather_code").GetInt32();
                double wind = cur.GetProperty("wind_speed_10m").GetDouble();
                string said = "It is " + temp + " degrees and " + Sky(code) + (wind >= 20 ? ", and very windy." : wind >= 8 ? ", with a breeze." : ".");
                _weather = (DateTime.Now, said, s.WeatherLat, s.WeatherLon);
                return said;
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException) { return Mystery; }
        }

        /// <summary>WMO weather codes in plain words.</summary>
        public static string Sky(int code) => code switch
        {
            0 => "clear",
            1 or 2 => "partly cloudy",
            3 => "cloudy",
            45 or 48 => "foggy",
            51 or 53 or 55 or 56 or 57 => "drizzly",
            61 or 63 or 66 or 80 or 81 => "rainy",
            65 or 67 or 82 => "pouring",
            71 or 73 or 75 or 77 or 85 or 86 => "snowing",
            95 or 96 or 99 => "stormy",
            _ => "hard to describe",
        };

        public static void SelfTest()
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("real world: " + what); }
            Need(Season(new DateTime(2026, 1, 15)) == "Winter" && Season(new DateTime(2026, 7, 4)) == "Summer" && Season(new DateTime(2026, 11, 4)) == "Fall", "seasons");
            // 2026-10-26 was a full moon; 2026-11-09 a new moon.
            Need(MoonPhase(new DateTime(2026, 10, 26, 12, 0, 0)) == "a full moon", "full moon: " + MoonPhase(new DateTime(2026, 10, 26, 12, 0, 0)));
            Need(MoonPhase(new DateTime(2026, 11, 9, 12, 0, 0)) == "a new moon", "new moon: " + MoonPhase(new DateTime(2026, 11, 9, 12, 0, 0)));
            // Sunset in New York on the summer solstice is about 8:30 pm Eastern, 00:30 UTC the next day.
            var set = Sunset(new DateTime(2026, 6, 21), 40.71, -74.01);
            Need(set is DateTime s && Math.Abs((s.ToUniversalTime() - new DateTime(2026, 6, 22, 0, 31, 0, DateTimeKind.Utc)).TotalMinutes) < 10, "sunset: " + set?.ToUniversalTime());
            Need(Sunset(new DateTime(2026, 6, 21), 80, 0) == null, "midnight sun");
            Need(Holiday(new DateTime(2026, 10, 31)) == "Halloween" && Holiday(new DateTime(2026, 11, 4)) == null, "holidays");
        }
    }
}
