using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace BusyBunny
{
    /// <summary>
    /// The real sky for Bunny's world (sweep 8.6, 13.3, Part 10 item 8): the weather at the round window from RealWorld
    /// (only for the town she typed; a mystery otherwise), weather days (fog, rain, storms, picnics, clouds, snow), night
    /// after the real sunset, the real moon phase, Bunny's silly weather report and the seasonal soundtrack.
    /// </summary>
    public static class SeasonSky
    {
        /// <summary>The last weather read: sky words like "rainy", and degrees (null when unknown).</summary>
        public static string Sky { get; private set; } = "mystery";
        public static int? Temp { get; private set; }
        public static DateTime ReadAt { get; private set; }

        /// <summary>Reads "It is 62 degrees and clear, with a breeze." into its sky words and degrees. Mystery for anything else.</summary>
        public static (string Sky, int? Temp) Parse(string words)
        {
            var m = Regex.Match(words ?? "", @"It is (-?\d+) degrees and ([a-z ]+?)(,|\.|$)");
            if (!m.Success) return ("mystery", null);
            return (m.Groups[2].Value.Trim(), int.Parse(m.Groups[1].Value));
        }

        public static void Use(string sky, int? temp, DateTime at) { Sky = sky; Temp = temp; ReadAt = at; }

        public static bool Rainy(string sky) => sky is "drizzly" or "rainy" or "pouring";
        public static bool Nice(string sky, int? temp) => sky is "clear" or "partly cloudy" && temp is int t && t >= 55 && t <= 90;

        /// <summary>The weather days the sky makes today (fog only in the morning).</summary>
        public static List<string> WeatherDays(string sky, int? temp, DateTime now)
        {
            var on = new List<string>();
            if (sky == "foggy" && now.Hour < 12) on.Add("A foggy morning");
            if (Rainy(sky)) on.Add("A rainy day");
            if (sky == "stormy") on.Add("A thunderstorm");
            if (Nice(sky, temp) && temp >= 62 && temp <= 88) on.Add("A picnic day");
            if (Nice(sky, temp)) on.Add("A cloud watching day");
            if (sky == "snowing") on.Add("A snow day");
            if (RealWorld.Season(now) == "Summer" && temp >= 80) on.Add("The ice cream truck");
            return on;
        }

        /// <summary>A winter weekday that is a snow day even without real snow: about one in thirteen, the same all day.</summary>
        public static bool SurpriseSnowDay(DateTime d) => RealWorld.Season(d) == "Winter" && d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && (d.DayOfYear * 7 + d.Year) % 13 == 0;

        /// <summary>Asks RealWorld for the weather (cached an hour there) and raises the weather days once a day.</summary>
        public static async Task Check(Store s, Settings cfg)
        {
            string words = await RealWorld.Weather(cfg);
            var (sky, temp) = Parse(words);
            Use(sky, temp, DateTime.Now);
            var now = DateTime.Now;
            foreach (var day in WeatherDays(sky, temp, now))
                if (SeasonCalendar.ByWords(day) is SeasonEvent e && Tools.IsOn(e.Group)) SeasonCalendar.RaiseOnce(s, day, s.Key(now));
        }

        // ---- Night ----

        /// <summary>When the sun sets today: the real sunset for her town, or else 6 pm in fall and winter and 8 pm in spring and summer.</summary>
        public static DateTime SunsetOn(DateTime day, Settings? cfg)
        {
            if (cfg != null && !(cfg.WeatherLat == 0 && cfg.WeatherLon == 0) && RealWorld.Sunset(day.Date, cfg.WeatherLat, cfg.WeatherLon) is DateTime set) return set;
            return day.Date.AddHours(RealWorld.Season(day) is "Spring" or "Summer" ? 20 : 18);
        }

        /// <summary>Nighttime mode: after sunset, until 5 in the morning.</summary>
        public static bool Night(DateTime now, Settings? cfg) => now.Hour < 5 || now >= SunsetOn(now, cfg);

        /// <summary>The moon in Bunny's words: the phase name, then how it feels and sounds on the hill.</summary>
        public static string MoonLine(DateTime now)
        {
            string phase = RealWorld.MoonPhase(now);
            string bare = phase.StartsWith("a ") ? phase[2..] : phase;
            var item = Content.Items("season.moon").FirstOrDefault(i => i.TryGetValue("phase", out var p) && p == bare);
            string feel = item == null ? "" : " " + (item.GetValueOrDefault("touch") ?? "") + " " + (item.GetValueOrDefault("sound") ?? "");
            return "Tonight there is " + phase + "." + feel.TrimEnd();
        }

        /// <summary>Full moon on this date: the moon is full at 9 in the evening. Its lunation number keeps it once a month.</summary>
        public static bool FullMoon(DateTime day) => RealWorld.MoonPhase(day.Date.AddHours(21)) == "a full moon";
        public static int Lunation(DateTime when) => (int)Math.Floor((when.ToUniversalTime() - new DateTime(2000, 1, 6, 18, 14, 0, DateTimeKind.Utc)).TotalDays / 29.530588853);

        /// <summary>The round window: how the weather feels and sounds through it, then the season in the burrow.</summary>
        public static string Window(Store s, DateTime now, Settings? cfg)
        {
            var skies = Content.Items("season.sky").Where(i => i.GetValueOrDefault("sky") == Sky).ToList();
            if (skies.Count == 0) skies = Content.Items("season.sky").Where(i => i.GetValueOrDefault("sky") == "mystery").ToList();
            var pick = skies.Count == 0 ? null : skies[(now.DayOfYear + now.Hour) % skies.Count];
            string season = RealWorld.Season(now);
            var seasons = Content.Items("season.seasons").Where(i => i.GetValueOrDefault("season") == season).ToList();
            var sp = seasons.Count == 0 ? null : seasons[now.DayOfYear % seasons.Count];
            string text = Sky == "mystery" ? RealWorld.Mystery + (cfg?.WeatherTown.Length > 0 ? "" : " Type your town in Settings, Fun and tools, and the window will follow the real weather.")
                : "It is " + (Temp is int t ? t + " degrees and " : "") + Sky + " outside.";
            if (pick != null) text += " " + pick.GetValueOrDefault("touch") + " " + pick.GetValueOrDefault("sound");
            if (sp != null) text += " " + season + " in the burrow: " + sp.GetValueOrDefault("touch");
            if (Night(now, cfg)) text += " The sun has set, so the burrow is in night mode.";
            return text;
        }

        /// <summary>Bunny's silly forecast from real words: a line from season.report with the sky and degrees put in.</summary>
        public static string Report(Store s, string sky, int? temp)
        {
            if (sky == "mystery") return Content.Pick(s, "season.report.mystery");
            string line = Content.Pick(s, "season.report");
            if (temp is not int t) line = line.Replace("{temp} degrees and ", "").Replace("{temp} degrees", "a mystery number of degrees");
            return line.Replace("{sky}", sky).Replace("{temp}", temp?.ToString() ?? "some");
        }

        // ---- The seasonal soundtrack ----

        public const int Usual = 0, RainMood = 1, WinterMood = 2, SpringMood = 3, SummerMood = 4, FallMood = 5, NightMood = 6;

        public static readonly string[] MoodNames =
        {
            "the usual piece", "the rainy day arrangement: soft piano with plucked strings for raindrops",
            "the cozy winter fire arrangement: warm organ and cello with glinting bells",
            "the spring arrangement: flute calls like birdsong over the strings",
            "the summer arrangement: bright xylophone and plucked strings",
            "the fall arrangement: low cello and piano, with a flute now and then",
            "the night arrangement: hushed strings and a few slow bells",
        };

        /// <summary>Which arrangement fits now: rain and storms first, then night, then the season.</summary>
        public static int MoodFor(DateTime now, string sky, Settings? cfg)
        {
            if (Rainy(sky) || sky == "stormy") return RainMood;
            if (sky == "snowing") return WinterMood;
            if (Night(now, cfg)) return NightMood;
            return RealWorld.Season(now) switch { "Winter" => WinterMood, "Spring" => SpringMood, "Summer" => SummerMood, _ => FallMood };
        }

        /// <summary>
        /// Rearranges the usual background piece for a mood, using only the recordings it already plays plus the xylophone and
        /// pizzicato recordings every sound uses: no new audio files. Runs on the audio thread when the mood changes.
        /// </summary>
        public static List<MusicLoop.Event> Arrange(List<MusicLoop.Event> piece, int mood, int home)
        {
            var e = new List<MusicLoop.Event>();
            foreach (var x in piece)
            {
                switch (mood)
                {
                    case RainMood:
                        if (x.Bank is "organ" or "flute") continue;
                        if (x.Bank == "bells") { e.Add(x with { Bank = "pizzicato", Gain = 0.10, Ring = 0.4, Release = 0.6 }); e.Add(x with { At = x.At + 0.7, Bank = "pizzicato", Midi = x.Midi + 5, Gain = 0.07, Ring = 0.3, Release = 0.5 }); continue; }
                        e.Add(x.Bank == "piano" ? x with { Gain = x.Gain * 1.3 } : x.Bank == "strings" ? x with { Gain = x.Gain * 0.8 } : x);
                        break;
                    case WinterMood:
                        if (x.Bank == "flute") continue;
                        e.Add(x.Bank switch { "organ" => x with { Gain = x.Gain * 1.8 }, "cello" => x with { Gain = x.Gain * 1.1 }, "bells" => x with { Gain = x.Gain * 1.2 }, _ => x });
                        break;
                    case SpringMood:
                        e.Add(x.Bank == "organ" ? x with { Gain = x.Gain * 0.5 } : x);
                        break;
                    case SummerMood:
                        if (x.Bank == "organ") continue;
                        e.Add(x.Bank == "bells" ? x with { Bank = "xylo", Gain = 0.08, Ring = 1.0, Release = 1.2 } : x);
                        break;
                    case FallMood:
                        if (x.Bank == "bells" && (int)(x.At / 9) % 2 == 1) continue;
                        e.Add(x.Bank == "cello" ? x with { Gain = x.Gain * 1.2 } : x.Bank == "strings" ? x with { Gain = x.Gain * 0.85 } : x);
                        break;
                    case NightMood:
                        if (x.Bank is "flute" || x.Bank == "piano" && x.Gain < 0.07) continue;
                        e.Add(x with { Gain = x.Gain * (x.Bank == "bells" ? 0.6 : 0.85) });
                        break;
                    default: e.Add(x); break;
                }
            }
            // Spring birds: two short high flute calls over every chord. Fall: one long flute note every other chord.
            const double span = MusicLoop.LoopSeconds / 20;
            for (int c = 0; c < 20; c++)
            {
                double t = c * span;
                if (mood == SpringMood && c % 2 == 0)
                {
                    e.Add(new MusicLoop.Event(t + 2.2, "flute", home + 28, 0.035, 0.6, 0.18, 0.25, 0.02));
                    e.Add(new MusicLoop.Event(t + 2.5, "flute", home + 31, 0.03, 0.6, 0.15, 0.25, 0.02));
                }
                if (mood == FallMood && c % 2 == 1) e.Add(new MusicLoop.Event(t + 4.0, "flute", home + 19, 0.04, -0.4, 2.6, 2.2, 1.0));
                if (mood == SummerMood) e.Add(new MusicLoop.Event(t + 6.8, "pizzicato", home + 7, 0.08, -0.3, 0.4, 0.6, 0.01));
            }
            return e;
        }
    }
}
