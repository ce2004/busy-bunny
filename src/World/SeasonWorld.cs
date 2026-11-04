using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Stage 8: seasons, festivals and the real sky in Bunny's world. Two places of its own, each living inside a Stage 7 place
    /// (the village green in the village, the round window in the burrow), the moon on Stage 7's stargazing hill, nine toolbox switches, a Settings page for her dates, and the calendar events of
    /// SeasonCalendar with the sky of SeasonSky. Everything is optional, said once through Calm, and pays through the Ledger.
    /// </summary>
    public sealed class SeasonWorld : IFeature
    {
        private const string Since = "0.14.0";
        public const string Green = "season.green", WindowPlace = "season.window";
        private const string MusicTool = "season.music", ReportTool = "season.weather";
        /// <summary>Dates are written in English, the way Dates.Parse reads them back, whatever language Windows uses.</summary>
        private static readonly System.Globalization.CultureInfo English = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        private MainWindow _w = null!;
        private Store S => _w.Store;
        private Settings Cfg => _w.AppSettings;

        public sealed class MoonBag { public string Text { get; set; } = ""; public int Lunation { get; set; } = -1; }
        public sealed class Ribbons { public Dictionary<string, string> Won { get; set; } = new(); }
        public sealed class Home { public int Size { get; set; } public int MovedYear { get; set; } public List<string> Butterflies { get; set; } = new(); public string ButterflySemester { get; set; } = ""; }
        private sealed class Counter { public string Day { get; set; } = ""; public int N { get; set; } }

        public void Attach(MainWindow w)
        {
            _w = w;
            MusicLoop.Arranger = SeasonSky.Arrange;
            void Reg(string id, string name, string drawer, string what, Action run) => Tools.Register(new BunnyTool(id, name, drawer, what, run) { Since = Since });
            Reg(SeasonCalendar.Tool, "Seasons and festivals", Drawers.World, "Holidays, the seasons and festivals on the village green, and what is coming up.", () => PlaceWorld.Show(_w, Green));
            Reg(SeasonCalendar.Village, "Village days", Drawers.World, "Games day, the fishing tournament, the garden club, the Friday market, bonfires and campfires.", SayVillageDays);
            Reg(SeasonCalendar.Weather, "The weather window", Drawers.World, "The real weather at the burrow window, night mode, and fog, rain, storm and picnic days.", () => PlaceWorld.Show(_w, WindowPlace));
            Reg(ReportTool, "Bunny's weather report", Drawers.World, "Bunny tells you the real weather in its own silly way.", () => _ = Report());
            Reg(SeasonCalendar.Moon, "Full moon intentions", Drawers.Notes, "Set one private intention at the full moon; Bunny reads it back at the next one.", MoonTool);
            Reg(MusicTool, "Seasonal soundtrack", Drawers.Rest, "The background music changes with the season, the weather and the night.", SayMusic);
            Reg(SeasonCalendar.Winter, "Snowman and snow days", Drawers.World, "A snowman that grows each day you meet your daily goal, snow days and snowball fights.", SayWinter);
            Reg(SeasonCalendar.Yearly, "Birthdays and big days", Drawers.World, "Your birthday, Bunny's birthday party, the village anniversary and moving day.", SayYearly);
            Reg(SeasonCalendar.Semester, "End of the semester", Drawers.World, "The lantern walk, the grand train ride and the butterfly garden when the semester ends.", SaySemester);
            BunnyTab.Register("Seasons and festivals", "what is on in the village today and coming up", () => PlaceWorld.Show(_w, Green), SeasonCalendar.Tool);
            BunnyTab.Register("The weather window", "the real weather and the night sky from the burrow", () => PlaceWorld.Show(_w, WindowPlace), SeasonCalendar.Weather);

            World.Places.Register(new Place(Green, "The village green", s => GreenSummary(s, DateTime.Now), _ => Tools.IsOn(SeasonCalendar.Tool) || Tools.IsOn(SeasonCalendar.Village) || Tools.IsOn(SeasonCalendar.Winter) || Tools.IsOn(SeasonCalendar.Yearly) || Tools.IsOn(SeasonCalendar.Semester), s => GreenThings(s, DateTime.Now)));
            World.Places.Register(new Place(WindowPlace, "The round window", _ => "It is " + (SeasonSky.Sky == "mystery" ? "hard to say what the weather is" : SeasonSky.Sky) + (SeasonSky.Night(DateTime.Now, Engines.Settings) ? ", and night has fallen" : ""), _ => Tools.IsOn(SeasonCalendar.Weather), s => WindowThings(s, DateTime.Now)));
            // One village, one weather window, one hill: the green lives in Stage 7's village, the round window in the burrow, and
            // the moon and full moon intentions on Stage 7's stargazing hill.
            PlaceWorld.Adopt(Green, PlaceIds.Village);
            PlaceWorld.Adopt(WindowPlace, PlaceIds.Burrow);
            PlaceHooks.AddThings(PlaceIds.Stars, s => HillThings(s, DateTime.Now));
            // The Friday night market is a stall at the village market, not a second market.
            PlaceHooks.AddThings(PlaceIds.Market, s => Tools.IsOn(SeasonCalendar.Village) && DateTime.Now.DayOfWeek == DayOfWeek.Friday
                ? new List<PlaceThing> { Line("Wander the night market", "lanterns and stalls every Friday", s, "season.market") } : new List<PlaceThing>());

            GameEvents.DayStarted += day => OnDay(DateTime.Now);
            GameEvents.CalendarDay += OnCalendarDay;
            GameEvents.TaskDone += OnDone;
            // Always, even with the tools off or Calm mode on: a prize given must be winnable again once its task goes back.
            GameEvents.TaskUndone += t => SeasonCalendar.TakeBack(S, t);
            GameEvents.StreakChanged += OnStreak;
            Ticker.Every("season.say", now => SeasonCalendar.TodayOf(S, now).Waiting.Count > 0, now => SeasonCalendar.TrySay(S, Cfg, now, line => Calm.SayUnprompted(_w, line, noted: true)));
            Ticker.Every("season.hourly", now => now.Minute == 7, now => Hourly(now));
            Ticker.Every("season.music", _ => true, now => Mixer.Music.Mood = Tools.IsOn(MusicTool) && !Calm.FunPaused ? SeasonSky.MoodFor(now, SeasonSky.Sky, Cfg) : SeasonSky.Usual);
        }

        // ---- The calendar ----

        private void OnDay(DateTime now)
        {
            var s = S;
            foreach (var words in SeasonCalendar.DaysOn(s, now, engine: false))
            {
                if (words == "The end of the semester" || SeasonCalendar.ByWords(words) is not SeasonEvent e) continue;
                // A village weekend is announced once, on whichever of its two days she opens first.
                var sat = now.DayOfWeek == DayOfWeek.Sunday ? now.Date.AddDays(-1) : now.Date;
                string when = e.Group == SeasonCalendar.Village && e.Id is "gamesday" or "fishing" or "gardenclub" ? s.Key(sat) : s.Key(now);
                if (!SeasonCalendar.RaiseOnce(s, words, when)) SeasonCalendar.Activate(s, e, now, announce: false);
            }
            if (SeasonCalendar.SemesterJustEnded(s, now) && SeasonCalendar.Prefs(s).SemesterEnd is DateTime end) SeasonCalendar.RaiseOnce(s, "The end of the semester", s.Key(end));
            if (SeasonSky.FullMoon(now)) SeasonCalendar.RaiseOnce(s, "The full moon", SeasonSky.Lunation(now.Date.AddHours(21)).ToString());
            if (SeasonSky.SurpriseSnowDay(now)) SeasonCalendar.RaiseOnce(s, "A snow day", s.Key(now));
            Hourly(now);
        }

        private void Hourly(DateTime now)
        {
            if (now.DayOfWeek == DayOfWeek.Friday && SeasonCalendar.GoodWeek(S, now)) SeasonCalendar.RaiseOnce(S, "Bonfire and s'mores", S.Key(now));
            // With her town cleared, the sky goes back to a mystery (no stale rain music or puddles).
            if (Cfg.WeatherTown.Length == 0 && SeasonSky.Sky != "mystery") SeasonSky.Use("mystery", null, now);
            if (Cfg.WeatherTown.Length > 0 && (Tools.IsOn(SeasonCalendar.Weather) || Tools.IsOn(MusicTool) || Tools.IsOn(SeasonCalendar.Winter) || Tools.IsOn(SeasonCalendar.Tool)))
                _ = CheckWeather();
        }

        private async System.Threading.Tasks.Task CheckWeather()
        {
            try { await SeasonSky.Check(S, Cfg); }
            catch (Exception e) { ErrorLog.Write("Seasons: the weather check failed: " + e.GetType().Name + ": " + e.Message); }
        }

        private void OnCalendarDay(string words)
        {
            if (SeasonCalendar.ByWords(words) is not SeasonEvent e) return;
            var now = DateTime.Now;
            bool announce = Tools.IsOn(e.Group) && !Calm.FunPaused;
            if (!SeasonCalendar.Activate(S, e, now, announce)) return;
            // Kept world changes happen only with their tool on (a switched-off tool never changes anything by itself).
            if (e.Id == "moving" && Tools.IsOn(e.Group)) Move(S, now);
            if (e.Id == "semesterend" && Tools.IsOn(e.Group)) Butterflies(S, now);
            if (announce) Game.Note(S, SeasonCalendar.Announcement(e, SeasonCalendar.Where(e)));
        }

        private void OnDone(TaskItem t)
        {
            var now = DateTime.Now;
            // The prize is paid in Calm mode too; Calm.AfterTaskDone keeps the line quiet there.
            if (SeasonCalendar.Prize(S, t, now) is ({ } e, var o))
            {
                if (o != null) Game.Queue(_w, o);
                S.Save();
                if (e.Id == "icecream" && !Calm.FunPaused) SoundBank.Play("Xylophone, sparkle");
                string gift = WeatherShell(S, t, e.Id, now) is string shell ? " The gift: " + shell + ", now in the treasure chest." : "";
                Calm.AfterTaskDone(_w, Game.Other, SeasonCalendar.PrizeLine(S, e) + gift);
                return;
            }
            // In a real storm, the next few tasks keep the cocoa warm (three a day at most, so it never chatters).
            if (!Calm.FunPaused && SeasonCalendar.IsOn(S, now, "storm"))
            {
                var c = S.World.GetKept<Counter>("season.cocoa");
                if (c.Day != S.Key(now)) c = new Counter { Day = S.Key(now) };
                if (c.N >= 3) return;
                c.N++;
                S.World.SetKept("season.cocoa", c);
                S.World.Save();
                Calm.AfterTaskDone(_w, Game.Chatter, Content.Pick(S, "season.storm"));
            }
        }

        private void OnStreak(int days)
        {
            var now = DateTime.Now;
            if (Calm.FunPaused || RealWorld.Season(now) != "Winter" || !Tools.IsOn(SeasonCalendar.Winter)) return;
            int pieces = SnowmanPieces(S, now);
            // Said only when a piece was really added: the count is kept for the whole winter and starts over the next one.
            string winter = WinterOf(now);
            var said = S.World.GetKept<Counter>("season.snowman.said");
            if (said.Day != winter) said = new Counter { Day = winter };
            if (pieces > said.N && pieces <= Content.Lines("season.snowman").Count)
            {
                S.World.SetKept("season.snowman.said", new Counter { Day = winter, N = pieces });
                S.World.Save();
                Calm.AfterTaskDone(_w, Game.Chatter, "Another daily goal met, so the snowman grows. " + Content.Lines("season.snowman")[pieces - 1].Replace("{name}", SeasonCalendar.BunnyName(S)));
            }
            if (days >= 3) SeasonCalendar.RaiseOnce(S, "A snowball fight", S.Key(Engine.WeekStart(now)));
        }

        // ---- Logic the self-test checks ----

        /// <summary>
        /// A foggy morning or a rainy day prize also leaves a shell for the treasure chest, tied to the task, so un-finishing
        /// it takes the shell back (WorldPlaces' TaskUndone handler). Returns the shell's name, or null for other prizes.
        /// </summary>
        public static string? WeatherShell(Store s, TaskItem t, string eventId, DateTime now)
        {
            if (eventId is not ("fog" or "rain")) return null;
            var shells = PlaceUi.Items("place.shells");
            var gift = shells.Count == 0 ? null : shells[now.DayOfYear % shells.Count];
            string name = gift?.Get("name") ?? "A smooth pebble";
            Treasure.Add(s, name, PlaceUi.Feel(gift), eventId == "fog" ? "the doorstep on a foggy morning" : "under a rainbow", t.Id);
            return name.ToLowerInvariant().StartsWith("a ") || name.ToLowerInvariant().StartsWith("an ") ? char.ToLowerInvariant(name[0]) + name[1..] : name;
        }

        /// <summary>A snowman piece for every day of this winter she met her daily goal, up to the whole snowman.</summary>
        /// <summary>Which winter a date is in, named by the year its December falls in.</summary>
        public static string WinterOf(DateTime now) => "winter " + (now.Month == 12 ? now.Year : now.Year - 1);

        public static int SnowmanPieces(Store s, DateTime now)
        {
            if (RealWorld.Season(now) != "Winter") return 0;
            var start = new DateTime(now.Month == 12 ? now.Year : now.Year - 1, 12, 1);
            int n = s.Data.Game.GoalDays.Count(k => DateTime.TryParse(k, out var d) && d >= start && d <= now.Date);
            return Math.Min(n, Math.Max(1, Content.Lines("season.snowman").Count));
        }

        public static readonly string[] Burrows = { "a snug little burrow", "a roomier burrow", "a burrow with wider tunnels", "a burrow with taller ceilings", "a grand burrow", "a burrow so big it has an echo" };

        /// <summary>Moving day, once a year: the burrow gets bigger and nothing is left behind (no other bag is touched).</summary>
        public static bool Move(Store s, DateTime now)
        {
            var h = s.World.GetKept<Home>("season.home");
            if (h.MovedYear == now.Year) return false;
            h.MovedYear = now.Year;
            h.Size = Math.Min(h.Size + 1, Burrows.Length - 1);
            s.World.SetKept("season.home", h);
            s.World.Save();
            return true;
        }

        /// <summary>Her classes with finished tasks this semester (the towns of the train ride and the butterflies).</summary>
        public static List<string> Classes(Store s, DateTime now)
        {
            var p = SeasonCalendar.Prefs(s);
            var from = p.SemesterStart ?? now.AddDays(-120);
            var tags = s.Data.Classes.Select(c => c.Tag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return tags.Where(tag => s.Data.Tasks.Any(t => t.DoneAt is DateTime at && at >= from && at <= now && t.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>At semester end, each class she finished becomes a butterfly (once per semester).</summary>
        public static int Butterflies(Store s, DateTime now)
        {
            var h = s.World.GetKept<Home>("season.home");
            var end = SeasonCalendar.Prefs(s).SemesterEnd;
            string sem = end?.ToString("yyyy-MM-dd") ?? "";
            if (sem.Length == 0 || h.ButterflySemester == sem) return 0;
            h.ButterflySemester = sem;
            string season = RealWorld.Season(end!.Value).ToLowerInvariant() + " " + end.Value.Year;
            var fresh = Classes(s, now).Select(c => c + ", " + season).ToList();
            h.Butterflies.AddRange(fresh);
            s.World.SetKept("season.home", h);
            s.World.Save();
            return fresh.Count;
        }

        /// <summary>
        /// A ribbon once per occasion: through the Ledger as its own Control Z step, or with remember false as part of the
        /// step just taken (spring cleaning joins the backlog sweep's own step, so one Control Z takes both back).
        /// </summary>
        public static bool Ribbon(Store s, string contest, string occasion, int points, string reason, bool remember = true)
        {
            var r = s.World.Get<Ribbons>("season.ribbons");
            if (r.Won.TryGetValue(contest, out var was) && was == occasion) return false;
            if (Ledger.Pay(s, points, reason, SeasonCalendar.Tool, remember: remember) == null) return false;
            r.Won[contest] = occasion;
            s.World.Set("season.ribbons", r);
            s.World.Save();
            s.Save();
            return true;
        }

        /// <summary>What Smart Bunny may be sent for her intention: nothing at all unless she turned it on.</summary>
        public static string? MoonPrompt(SeasonPrefs p, string intention, string how) =>
            !p.MoonToClaude || intention.Length == 0 ? null :
            "At last month's full moon she set this intention: \"" + intention + "\". She says " + how + ". Reply in one or two warm, short sentences as Bunny, with no advice and no judgment.";

        // ---- Places ----

        private static PlaceThing Line(string name, string what, Store s, string topic, string before = "") =>
            new(name, what, owner => Speech.Say(owner, before + Content.Pick(s, topic), true));

        private static string GreenSummary(Store s, DateTime now)
        {
            var on = SeasonCalendar.OnToday(s, now);
            string season = RealWorld.Season(now);
            return on.Count == 0 ? season + ", and a quiet day in the village" : season + ". Today: " + string.Join(", ", on.Select(SeasonCalendar.Name));
        }

        private List<PlaceThing> GreenThings(Store s, DateTime now)
        {
            var things = new List<PlaceThing>();
            bool fest = Tools.IsOn(SeasonCalendar.Tool), village = Tools.IsOn(SeasonCalendar.Village), winter = Tools.IsOn(SeasonCalendar.Winter);
            string season = RealWorld.Season(now);
            things.Add(new("Coming up", "special days in the next few weeks", owner =>
            {
                var lines = SeasonCalendar.ComingUp(s, now);
                if (lines.Count == 0) Speech.Say(owner, "Nothing special in the next few weeks. A quiet stretch.", true);
                else TextViewer.Show(owner, "Coming up in the village", lines);
            }));
            foreach (var e in SeasonCalendar.OnToday(s, now))
            {
                var ev = e;
                things.Add(new(SeasonCalendar.Name(ev), "on today", owner => Speech.Say(owner, SeasonCalendar.Announcement(ev, ""), true)));
            }
            bool On(string id) => SeasonCalendar.IsOn(s, now, id);
            if (fest)
            {
                if (now.Month == 10)
                {
                    things.Add(Line("Pick a pumpkin", "the October pumpkin patch", s, "season.pumpkin"));
                    things.Add(Line("Walk the haunted tunnel", "cozy, not scary", s, "season.haunted"));
                }
                if (season == "Spring")
                {
                    things.Add(Line("Fly a kite", "on the windy hill", s, "season.kite"));
                    things.Add(new("Spring cleaning", "sort tasks that sat for three weeks, and Bunny cleans the burrow", owner => SpringCleaning(owner, s)));
                }
                if (season == "Summer" && now.Hour >= 12 && now.Hour < 19) things.Add(Line("Wave down the ice cream truck", "it comes on summer afternoons", s, "season.icecream"));
                if (On("newyear") || On("newyearseve") || On("july4")) things.Add(Line("Watch the fireworks", "described by sound and feel, with earmuffs", s, "season.fireworks"));
                bool holiday = SeasonCalendar.OnToday(s, now).Any(x => x.Group == SeasonCalendar.Tool && x.Id is not ("icecream" or "cleaning" or "kite" or "pumpkin" or "winter" or "spring" or "summer" or "fall") || x.Group == SeasonCalendar.Yearly);
                if (holiday)
                {
                    things.Add(new("Join the festival", "music, stalls and neighbors", owner => Speech.Say(owner, Content.Pick(s, "season.festival") + " " + Content.Pick(s, "season.holiday"), true)));
                    things.Add(new("Decorate the burrow door", "for today's decorating contest; a ribbon once a day", owner => Decorate(owner, s, now)));
                }
                if (On("harvest") || now.Date >= SeasonCalendar.Last(now.Year, 9, DayOfWeek.Saturday) && now.Date <= SeasonCalendar.Last(now.Year, 9, DayOfWeek.Saturday).AddDays(7))
                    things.Add(new("Have your garden judged", "the harvest festival", owner =>
                    {
                        var garden = PlaceGarden.Bag(s);
                        int growing = garden.Plots.Count(p => PlaceGarden.Stage(p) > 0);
                        string text = "The judges walk your carrot garden: " + PlaceUi.N(garden.Harvested, "carrot") + " grown so far, and " + PlaceUi.N(growing, "plot") + " growing now. " + Content.Pick(s, "season.harvest");
                        if (Ribbon(s, "harvest", now.Year.ToString(), 5, "a harvest festival ribbon")) text += " " + PlayTime.SpendLine(s.Data) + ".";
                        Speech.Say(owner, text, true);
                    }));
            }
            if (village)
            {
                if (On("gamesday")) things.Add(Line("Join a race", "sack race, three-legged race and more", s, "season.games"));
                if (On("fishing")) things.Add(Line("Cast a line", "the fishing tournament; every fish goes back", s, "season.fishing"));
                if (On("gardenclub")) things.Add(new("Enter the garden club contest", "a ribbon once this month", owner =>
                {
                    var garden = PlaceGarden.Bag(s);
                    string text = Content.Pick(s, "season.garden") + " Your carrot garden has " + PlaceUi.N(garden.Plots.Count(p => PlaceGarden.Stage(p) > 0), "plot") + " growing.";
                    if (Ribbon(s, "gardenclub", now.ToString("yyyy-MM"), 3, "a garden club ribbon")) text += " 3 points for the ribbon. " + PlayTime.SpendLine(s.Data) + ".";
                    Speech.Say(owner, text, true);
                }));
                if (On("bonfire")) things.Add(Line("Toast a marshmallow", "the Friday bonfire after a good week", s, "season.bonfire"));
                if (now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday && now.Hour >= 17) things.Add(Line("Hear a campfire story", "a little spooky, always cozy", s, "season.campfire"));
            }
            if (winter && season == "Winter")
            {
                things.Add(Line("Skate on the pond", "the ice is thick all winter", s, "season.skating"));
                things.Add(new("Ride the sled", "every task you finish today makes the next run faster", owner =>
                {
                    int done = s.Data.Tasks.Count(t => t.DoneAt is DateTime at && at.Date == now.Date);
                    string speed = done == 0 ? "A slow, wobbly run. Finish a task and the next one goes faster. " : done < 3 ? "A quick run, thanks to your tasks today. " : done < 6 ? "A fast run, you have been busy. " : "A rocket of a run. The neighbors duck. ";
                    Speech.Say(owner, speed + Content.Pick(s, "season.sled"), true);
                }));
                things.Add(new("Your snowman", "a piece for each day you meet your daily goal this winter", owner => Speech.Say(owner, SnowmanWords(s, now), true)));
                if (On("snowday")) things.Add(Line("Play in the snow", "it is a snow day", s, "season.snowday"));
                if (On("snowball")) things.Add(Line("Throw a snowball", "a friendly snowball fight", s, "season.snowball"));
            }
            if (Tools.IsOn(SeasonCalendar.Yearly))
            {
                if (On("herbirthday")) things.Add(Line("Your birthday party", "the village throws you a party", s, "season.herbirthday"));
                if (On("bunnybirthday")) things.Add(new("Bunny's birthday party", "cake, and gifts from your semester", owner => Speech.Say(owner, Content.Pick(s, "season.bunnybirthday") + " " + Gift(s, now), true)));
                if (On("anniversary")) things.Add(new("The village anniversary", "your years in the village", owner =>
                {
                    int years = now.Year - SeasonCalendar.FirstDay(s).Year;
                    int done = s.Data.Tasks.Count(t => t.DoneAt != null);
                    Speech.Say(owner, years + (years == 1 ? " year" : " years") + " in the village, and " + done + " finished tasks still on record. " + Content.Pick(s, "season.anniversary"), true);
                }));
                // Moving day turned on after it began: the move happens now, once this year.
                if (On("moving")) Move(s, now);
                if (On("moving") || s.World.GetKept<Home>("season.home").MovedYear == now.Year && now.Month == 6 && now.Day <= 7)
                    things.Add(new("Tour the new burrow", "moving day: everything came along", owner =>
                        Speech.Say(owner, "Bunny now lives in " + Burrows[s.World.GetKept<Home>("season.home").Size] + ". Every room you built and every thing you collected came too. " + Content.Pick(s, "season.moving"), true)));
            }
            if (Tools.IsOn(SeasonCalendar.Semester))
            {
                if (SeasonCalendar.SemesterJustEnded(s, now))
                {
                    // The tool was off when the semester ended: the butterflies come out now (once a semester).
                    Butterflies(s, now);
                    things.Add(Line("The lantern walk", "the semester is over", s, "season.lantern"));
                    things.Add(new("The grand train ride", "through a town for each class you worked on", owner =>
                    {
                        var towns = Classes(s, now);
                        string route = towns.Count == 0 ? "The train takes the scenic loop around the village. " : "All aboard. The train rolls through " + string.Join(", ", towns.Select(t => PlaceOutdoors.TownFor(s, t) + ", the " + t + " town")) + ". ";
                        Speech.Say(owner, route + Content.Pick(s, "season.train"), true);
                    }));
                }
                if (s.Data.Classes.Count > 0 || s.World.GetKept<Home>("season.home").Butterflies.Count > 0)
                    things.Add(new("The butterfly garden", "a caterpillar for each class becomes a butterfly when the semester ends", owner => Speech.Say(owner, ButterflyWords(s, now), true)));
            }
            return things;
        }

        private List<PlaceThing> WindowThings(Store s, DateTime now)
        {
            var cfg = Engines.Settings;
            var things = new List<PlaceThing>
            {
                new("Look out the window", "the real weather, by touch and sound", owner => Speech.Say(owner, SeasonSky.Window(s, now, cfg), true)),
            };
            if (Tools.IsOn(ReportTool)) things.Add(new("Bunny's weather report", "the real forecast, Bunny style", owner => _ = Report(owner)));
            string sky = SeasonSky.Sky;
            if (SeasonSky.Nice(sky, SeasonSky.Temp) && !SeasonSky.Night(now, cfg)) things.Add(Line("Watch the clouds", "lie in the grass with Bunny", s, "season.clouds", "Bunny points up. "));
            if (SeasonSky.Nice(sky, SeasonSky.Temp) && SeasonSky.Temp is >= 62 and <= 88 && !SeasonSky.Night(now, cfg)) things.Add(Line("Have a picnic", "a blanket on the green", s, "season.picnic"));
            if (SeasonSky.Rainy(sky)) things.Add(Line("Jump in puddles", "boots on", s, "season.puddles"));
            if (sky == "stormy") things.Add(Line("Shelter from the storm", "the neighbors are inside, and the cocoa is warm", s, "season.storm.enter"));
            if (SeasonSky.Night(now, cfg)) things.Add(Line("Listen to the night", "the burrow after sunset", s, "season.night"));
            return things;
        }

        private List<PlaceThing> HillThings(Store s, DateTime now)
        {
            var things = new List<PlaceThing>();
            if (Tools.IsOn(SeasonCalendar.Weather)) things.Add(new("The moon tonight", "the real moon phase", owner => Speech.Say(owner, SeasonSky.MoonLine(now.Date.AddHours(21)), true)));
            if (!Tools.IsOn(SeasonCalendar.Moon)) return things;
            var bag = s.World.Get<MoonBag>(SeasonCalendar.Moon);
            int lun = SeasonSky.Lunation(now.Date.AddHours(21));
            bool full = SeasonSky.FullMoon(now);
            if (bag.Text.Length > 0 && full && bag.Lunation < lun)
                things.Add(new("How did your intention go?", "Bunny reads last month's intention back", owner => AnswerIntention(owner, s, bag)));
            else if (bag.Text.Length > 0)
                things.Add(new("Hear your intention", "private, only for you", owner => Speech.Say(owner, "Your intention: " + bag.Text + ". Bunny will ask about it at the next full moon.", true)));
            if (full && bag.Text.Length == 0)
                things.Add(new("Set a full moon intention", "one private intention for the month", owner => SetIntention(owner, s, lun)));
            if (!full && bag.Text.Length == 0)
                things.Add(new("About intentions", "at the full moon you can set one", owner => Speech.Say(owner, "At the next full moon you can set one private intention here. It stays on this PC, and Bunny reads it back at the full moon after.", true)));
            return things;
        }

        // ---- Things to do ----

        private static string SnowmanWords(Store s, DateTime now)
        {
            int n = SnowmanPieces(s, now);
            if (n == 0) return "Just a patch of smooth snow for now. Each day you meet your daily goal this winter adds a piece, and the snowman never melts while it waits.";
            var pieces = Content.Lines("season.snowman");
            return "Your snowman has " + n + " of " + pieces.Count + " pieces. The newest: " + pieces[n - 1].Replace("{name}", s.Data.Game.BunnyName);
        }

        private static string ButterflyWords(Store s, DateTime now)
        {
            var h = s.World.GetKept<Home>("season.home");
            string text = h.Butterflies.Count == 0 ? "No butterflies yet. " : "Butterflies in the garden: " + string.Join("; ", h.Butterflies) + ". ";
            var p = SeasonCalendar.Prefs(s);
            if (p.SemesterEnd is DateTime end && end.Date >= now.Date)
            {
                var growing = s.Data.Classes.Select(c => c.Tag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                if (growing.Count > 0) text += "Caterpillars munching away: " + string.Join(", ", growing) + ". They open on " + end.ToString("MMMM d", English) + ".";
            }
            else if (p.SemesterEnd == null) text += "Set your semester dates in Settings, Seasons and festivals, and each class becomes a butterfly when it ends.";
            if (h.Butterflies.Count > 0 && SeasonCalendar.SemesterJustEnded(s, now)) text += " " + Content.Pick(s, "season.butterfly");
            return text.Trim();
        }

        private static string Gift(Store s, DateTime now)
        {
            var from = SeasonCalendar.Prefs(s).SemesterStart ?? now.AddDays(-120);
            var done = s.Data.Tasks.Where(t => t.DoneAt is DateTime at && at >= from && at <= now).ToList();
            if (done.Count == 0) return "Your gift to Bunny is just being here, and Bunny says that is plenty.";
            var top = done.Where(t => t.Tag.Length > 0).GroupBy(t => t.Tag).OrderByDescending(g => g.Count()).FirstOrDefault();
            return "Your gift to Bunny is your semester: " + done.Count + " finished tasks" + (top != null ? ", most of them for " + top.Key + ", so Bunny gets a tiny " + top.Key + " notebook" : "") + ".";
        }

        private void Decorate(Window owner, Store s, DateTime now)
        {
            var items = Content.Items("season.decor");
            if (items.Count == 0) return;
            int i = SeasonAsk.Pick(owner, "Decorate the burrow door", "_Decoration", items.Select(x => x.GetValueOrDefault("name") ?? "").ToList());
            if (i < 0) return;
            var d = items[i];
            string text = d.GetValueOrDefault("name") + " goes up on the door. " + d.GetValueOrDefault("touch") + " " + d.GetValueOrDefault("sound") + " " + Content.Pick(s, "season.decorate");
            if (Ribbon(s, "decorate", s.Key(now), 3, "a ribbon at the decorating contest")) text += " 3 points for the ribbon. " + PlayTime.SpendLine(s.Data) + ".";
            Speech.Say(owner, text, true);
        }

        private void SpringCleaning(Window owner, Store s)
        {
            var now = DateTime.Now;
            List<TaskItem> Stale() => Review.SweepCandidates(s.Data.Tasks.Where(t => !s.ListOf(t).Archived), DateTime.Now);
            int before = Stale().Count;
            if (before == 0) { Speech.Say(owner, "Nothing has sat untouched for three weeks, so the burrow is already spotless. Bunny dusts one shelf anyway.", true); return; }
            var review = Features.Get<Review>();
            if (review == null) { Speech.Say(owner, "Spring cleaning uses the backlog sweep: View, Backlog sweep.", true); return; }
            Speech.Say(owner, "Spring cleaning: " + before + (before == 1 ? " old task to sort" : " old tasks to sort") + ". For each one you sort, Bunny sweeps out a room.", true);
            int sorted = review.Sweep(owner);
            if (sorted <= 0) return;
            string text = Content.Pick(s, "season.cleaning");
            // Part of the sweep's own Control Z step (the sweep remembers before its first change), so one Control Z undoes both.
            if (Ribbon(s, "cleaning", s.Key(now), 5, "spring cleaning the burrow", remember: false)) text += " 5 points for the clean burrow. " + PlayTime.SpendLine(s.Data) + ".";
            // Queued after the sweep's own summary, so that is not cut off.
            Speech.Say(owner, text, false);
        }

        private void MoonTool()
        {
            if (PlaceWorld.IsOpen(S, PlaceIds.Stars)) { PlaceWorld.Show(_w, PlaceIds.Stars); return; }
            var line = HillThings(S, DateTime.Now).FirstOrDefault(x => x.Name != "The moon tonight");
            if (line == null) return;
            var back = _w.KeepFocus();
            try { line.Run(_w); }
            finally { back(); }
        }

        private void SetIntention(Window owner, Store s, int lunation)
        {
            string? text = SeasonAsk.Text(owner, "Full moon intention", "Your _intention for this month (private, kept on this PC)");
            if (string.IsNullOrWhiteSpace(text)) return;
            World.Change<MoonBag>(s, SeasonCalendar.Moon, "setting a full moon intention", b => { b.Text = text.Trim(); b.Lunation = lunation; });
            Speech.Say(owner, Content.Pick(s, "season.moon.set"), true);
        }

        private void AnswerIntention(Window owner, Store s, MoonBag bag)
        {
            var hows = new List<string> { "It went well", "I am still working on it", "I am letting it go" };
            // What Smart Bunny is told, ready to drop into "She says ...".
            var told = new[] { "it went well", "she is still working on it", "she is letting it go" };
            // The intention is in the dialog's own label, so the focus announcement reads it and she can hear it again there.
            string label = ("Last full moon you set this intention: " + bag.Text.TrimEnd('.') + ". " + Content.Pick(s, "season.moon.back")).Replace("_", " ") + " _How did it go";
            int i = SeasonAsk.Pick(owner, "How did it go?", label, hows);
            if (i < 0) return;
            string intention = bag.Text;
            World.Change<MoonBag>(s, SeasonCalendar.Moon, "answering a full moon intention", b => { b.Text = ""; b.Lunation = -1; });
            string? prompt = MoonPrompt(SeasonCalendar.Prefs(s), intention, told[i]);
            if (prompt == null) { Speech.Say(owner, Content.Pick(s, "season.moon.answer") + " You can set a new one now.", true); return; }
            _ = Reply(owner, s, prompt);
        }

        private static async System.Threading.Tasks.Task Reply(Window owner, Store s, string prompt)
        {
            string line = await BunnyWrite.Short(s, SeasonCalendar.Moon, prompt, "season.moon.answer");
            UIElement from = owner.IsVisible || Engines.Window == null ? owner : Engines.Window;
            Speech.Say(from, line + " You can set a new one now.", true);
        }

        // ---- Tools ----

        private async System.Threading.Tasks.Task Report(Window? owner = null)
        {
            string words = await RealWorld.Weather(Cfg);
            // The answer can take a few seconds: if she has left that window by then, it is said from the main window.
            UIElement from = owner is Window o && o.IsVisible ? o : _w;
            var (sky, temp) = SeasonSky.Parse(words);
            if (sky != "mystery") SeasonSky.Use(sky, temp, DateTime.Now);
            string text = "Bunny's weather report. " + SeasonSky.Report(S, sky, temp);
            if (sky == "mystery" && Cfg.WeatherTown.Length == 0) text += " To get the real weather, type your town in Settings, Fun and tools.";
            Speech.Say(from, text, true);
        }

        private void SayVillageDays()
        {
            var now = DateTime.Now;
            var lines = SeasonCalendar.ComingUp(S, now, 40).Where(l => SeasonCalendar.Catalog.Any(e => e.Group == SeasonCalendar.Village && l.StartsWith(SeasonCalendar.Name(e)))).Take(5).ToList();
            _w.Say(lines.Count == 0 ? "No village days coming up soon." : "Village days: " + string.Join(". ", lines) + ". Bonfires are on Fridays after a good week. They are on the village green, in the village in Bunny's world.", true);
        }

        private void SayMusic()
        {
            var now = DateTime.Now;
            if (!Cfg.MusicOn || !Cfg.SoundsOn) { _w.Say("The background music is off. Turn it on in Options, Sounds, and it will follow the season.", true); return; }
            if (Calm.FunPaused) { _w.Say("Calm mode is on, so the usual piece plays.", true); return; }
            _w.Say("Playing " + SeasonSky.MoodNames[SeasonSky.MoodFor(now, SeasonSky.Sky, Cfg)] + ". It uses your music volume and switch. Turn the seasonal part off in Settings, Fun and tools.", true);
        }

        private void SayWinter()
        {
            var now = DateTime.Now;
            if (RealWorld.Season(now) != "Winter") { _w.Say("Winter fun starts in December: skating, sledding, snow days, snowball fights, and a snowman that grows each day you meet your daily goal.", true); return; }
            _w.Say(SnowmanWords(S, now) + (SeasonCalendar.IsOn(S, now, "snowday") ? " Today is a snow day." : ""), true);
        }

        private void SayYearly()
        {
            var now = DateTime.Now;
            var p = SeasonCalendar.Prefs(S);
            string Next(int m, int d) { var x = new DateTime(now.Year, m, Math.Min(d, DateTime.DaysInMonth(now.Year, m))); if (x < now.Date) x = x.AddYears(1); return Words.Day(x, now.Date); }
            var first = SeasonCalendar.FirstDay(S);
            string her = SeasonCalendar.Birthday(S) is (int bm, int bd) ? "Your birthday: " + SeasonCalendar.MonthDay(bm, bd) + ", " + Next(bm, bd) + ". " : "Your birthday is not set; add it in Settings, Seasons and festivals if you like. ";
            _w.Say(her + "Bunny's birthday: " + SeasonCalendar.MonthDay(p.BunnyMonth, p.BunnyDay) + ", " + Next(p.BunnyMonth, p.BunnyDay) + ". The village anniversary: " + SeasonCalendar.MonthDay(first.Month, first.Day) + ", " + Next(first.Month, first.Day) +
                ". Moving day: June 1, when Bunny moves to " + Burrows[Math.Min(S.World.GetKept<Home>("season.home").Size + 1, Burrows.Length - 1)] + ".", true);
        }

        private void SaySemester()
        {
            var p = SeasonCalendar.Prefs(S);
            if (p.SemesterEnd == null) { _w.Say("Your semester dates are not set, so the semester-end events stay quiet. Add them in Settings, Seasons and festivals.", true); return; }
            _w.Say("The semester ends " + Words.Day(p.SemesterEnd.Value, DateTime.Today) + ", on " + p.SemesterEnd.Value.ToString("MMMM d", English) + ". Then come the lantern walk, the grand train ride and the butterflies. " + ButterflyWords(S, DateTime.Now), true);
        }

        // ---- Settings ----

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) { yield return new Page(store); }

        private sealed class Page : ISettingsPage
        {
            private readonly Store _s;
            private readonly TextBox _birth = new(), _bunny = new(), _start = new(), _end = new();
            private readonly CheckBox _moon;
            private (int, int)? _b, _bb;
            private DateTime? _st, _en;

            public Page(Store s)
            {
                _s = s;
                var p = SeasonCalendar.Prefs(s);
                _birth.Text = p.BirthMonth > 0 ? SeasonCalendar.MonthDay(p.BirthMonth, p.BirthDay) : "";
                _bunny.Text = SeasonCalendar.MonthDay(p.BunnyMonth, p.BunnyDay);
                _start.Text = p.SemesterStart?.ToString("MMMM d, yyyy", English) ?? "";
                _end.Text = p.SemesterEnd?.ToString("MMMM d, yyyy", English) ?? "";
                _moon = new CheckBox { Content = "Let Smart Bunny read my full _moon intention to answer it", IsChecked = p.MoonToClaude, Margin = new Thickness(0, 6, 0, 4) };
                var panel = new StackPanel();
                panel.Children.Add(Layout.Row("Your _birthday, month and day, like March 3 (blank for none)", _birth, 420));
                panel.Children.Add(Layout.Row("Bunny's bi_rthday", _bunny, 420));
                panel.Children.Add(Layout.Row("Semester _starts, like January 20, 2027 (blank for none)", _start, 420));
                panel.Children.Add(Layout.Row("Semester _ends", _end, 420));
                panel.Children.Add(_moon);
                string? about = p.BirthMonth == 0 && SeasonCalendar.Birthday(s) is (int m, int d) ? "From what you told Bunny about yourself, your birthday is " + SeasonCalendar.MonthDay(m, d) + ". " : "";
                panel.Children.Add(new TextBlock
                {
                    Text = about + "Your birthday stays on this PC and is never sent anywhere. With no birthday here, Busy Bunny looks for one in what you told Bunny about yourself. Without semester dates, the end-of-semester events stay quiet. Your full moon intentions are never sent to Claude unless you check the box.",
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 8, 0, 0),
                });
                Content = panel;
            }

            public string Name => "Seasons and festivals";
            public FrameworkElement Content { get; }

            public string? Validate()
            {
                _b = null; _bb = null; _st = null; _en = null;
                if (_birth.Text.Trim().Length > 0 && (_b = SeasonCalendar.ParseMonthDay(_birth.Text)) == null) return "I could not read your birthday. Type a month and day, like March 3.";
                if ((_bb = SeasonCalendar.ParseMonthDay(_bunny.Text)) == null) return "I could not read Bunny's birthday. Type a month and day, like March 21.";
                DateTime? Date(TextBox t) => t.Text.Trim().Length == 0 ? null : Dates.Parse(t.Text, DateTime.Now).When?.Date;
                if (_start.Text.Trim().Length > 0 && (_st = Date(_start)) == null) return "I could not read the semester start. Type a date, like January 20, 2027.";
                if (_end.Text.Trim().Length > 0 && (_en = Date(_end)) == null) return "I could not read the semester end. Type a date, like May 8, 2027.";
                if (_st is DateTime a && _en is DateTime b && b <= a) return "The semester ends before it starts. Check the two dates.";
                return null;
            }

            public void Apply()
            {
                var p = SeasonCalendar.Prefs(_s);
                (p.BirthMonth, p.BirthDay) = _b ?? (0, 0);
                if (_bb is (int m, int d)) (p.BunnyMonth, p.BunnyDay) = (m, d);
                p.SemesterStart = _st;
                p.SemesterEnd = _en;
                p.MoonToClaude = _moon.IsChecked == true;
                SeasonCalendar.SavePrefs(_s, p);
            }
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("seasons: " + what); }
            // US holidays worked out from the date.
            Need(SeasonCalendar.Nth(2026, 11, DayOfWeek.Thursday, 4) == new DateTime(2026, 11, 26), "Thanksgiving 2026");
            Need(SeasonCalendar.Nth(2027, 1, DayOfWeek.Monday, 3) == new DateTime(2027, 1, 18), "Martin Luther King Jr. Day 2027");
            Need(SeasonCalendar.Last(2026, 5, DayOfWeek.Monday) == new DateTime(2026, 5, 25), "Memorial Day 2026");
            Need(SeasonCalendar.Nth(2026, 9, DayOfWeek.Monday, 1) == new DateTime(2026, 9, 7), "Labor Day 2026");
            Need(SeasonCalendar.DaysOn(store, new DateTime(2026, 11, 26), false).Contains("Thanksgiving"), "Thanksgiving is a special day");
            Need(SeasonCalendar.DaysOn(store, new DateTime(2026, 7, 4), false).Contains("The Fourth of July"), "the Fourth of July");
            Need(SeasonCalendar.DaysOn(store, new DateTime(2026, 11, 6), false).Contains("The Friday night market"), "the Friday market");
            var gd = SeasonCalendar.DaysOn(store, new DateTime(2026, 11, 8), false);
            Need(gd.Contains("Village games day") && SeasonCalendar.DaysOn(store, new DateTime(2026, 11, 7), false).Contains("Village games day"), "games day on the first weekend");
            Need(SeasonCalendar.DaysOn(store, new DateTime(2026, 11, 14), false).Contains("The garden club contest") && SeasonCalendar.DaysOn(store, new DateTime(2026, 11, 21), false).Contains("The fishing tournament"), "garden club and fishing weekends");
            Need(SeasonCalendar.DaysOn(store, new DateTime(2026, 10, 31), true).Contains("Halloween") && !SeasonCalendar.DaysOn(store, new DateTime(2026, 10, 31), false).Contains("Halloween"), "Halloween comes from the engine only");
            foreach (var w in SeasonCalendar.DaysOn(store, new DateTime(2026, 12, 1), true).Concat(SeasonCalendar.DaysOn(store, new DateTime(2027, 6, 1), true)))
                Need(SeasonCalendar.ByWords(w) != null, "no event for " + w);

            // Her birthday only from her settings or her About me notes; semester events only with dates.
            var prefs0 = SeasonCalendar.Prefs(store);
            var notes0 = store.Data.BunnyNotes.ToList();
            try
            {
                SeasonCalendar.SavePrefs(store, new SeasonPrefs());
                store.Data.BunnyNotes.Clear();
                Need(SeasonCalendar.Birthday(store) == null && !Enumerable.Range(0, 366).Any(i => SeasonCalendar.DaysOn(store, new DateTime(2027, 1, 1).AddDays(i), false).Contains("Your birthday")), "a birthday with none set");
                Need(!Enumerable.Range(0, 366).Any(i => SeasonCalendar.DaysOn(store, new DateTime(2027, 1, 1).AddDays(i), false).Contains("The end of the semester")), "a semester end with none set");
                store.Data.BunnyNotes.Add("Her birthday is on April 9th.");
                Need(SeasonCalendar.Birthday(store) == (4, 9), "the birthday from About me");
                SeasonCalendar.SavePrefs(store, new SeasonPrefs { BirthMonth = 2, BirthDay = 29, SemesterEnd = new DateTime(2027, 5, 8) });
                Need(SeasonCalendar.DaysOn(store, new DateTime(2027, 2, 28), false).Contains("Your birthday"), "a leap day birthday in a common year");
                Need(SeasonCalendar.DaysOn(store, new DateTime(2027, 5, 8), false).Contains("The end of the semester") && SeasonCalendar.SemesterJustEnded(store, new DateTime(2027, 5, 12)), "the semester end");
                Need(SeasonCalendar.ParseMonthDay("March 3") == (3, 3) && SeasonCalendar.ParseMonthDay("3/14") == (3, 14) && SeasonCalendar.ParseMonthDay("9 of Dec") == (12, 9) && SeasonCalendar.ParseMonthDay("Feb 30") == null, "month and day typing");
                Need(SeasonCalendar.Prefs(store).BirthMonth == 2, "her dates were not kept");
                // Full moon intentions never go to Claude unless she turned it on.
                Need(MoonPrompt(new SeasonPrefs(), "run more", "it went well") == null && MoonPrompt(new SeasonPrefs { MoonToClaude = true }, "run more", "it went well")!.Contains("run more"), "the intention privacy switch");
                Need(MoonPrompt(new SeasonPrefs { MoonToClaude = true }, "run more", "she is still working on it")!.Contains("She says she is still working on it."), "the intention answer reads well");
                // Her own birthday from About me, never someone else's, and a date anywhere after the words.
                Need(SeasonCalendar.BirthdayIn("My mom's birthday is May 2.") == null && SeasonCalendar.BirthdayIn("Bunny's birthday is in spring, March 3.") == null, "someone else's birthday counted as hers");
                Need(SeasonCalendar.BirthdayIn("My birthday is on the 5th of May.") == (5, 5), "the 5th of May birthday: " + SeasonCalendar.BirthdayIn("My birthday is on the 5th of May."));
                Need(SeasonCalendar.BirthdayIn("I was born on 3/14 in Ohio.") == (3, 14) && SeasonCalendar.BirthdayIn("My dad's birthday is June 1 and my birthday is October 12th.") == (10, 12), "birthday phrasing");
            }
            finally { SeasonCalendar.SavePrefs(store, prefs0); store.Data.BunnyNotes.Clear(); store.Data.BunnyNotes.AddRange(notes0); }

            // One prize a day, through the ledger with the task's id; un-finishing lets it be won again; Control Z puts the bag back.
            var now = DateTime.Now;
            var market = SeasonCalendar.ByWords("The Friday night market")!;
            SeasonCalendar.Activate(store, market, now, announce: false);
            Need(SeasonCalendar.IsOn(store, now, "market"), "an event did not turn on");
            var a = new TaskItem { Name = "season test a", List = store.Data.Lists[0].Name };
            var b = new TaskItem { Name = "season test b", List = store.Data.Lists[0].Name };
            store.Data.Tasks.Add(a); store.Data.Tasks.Add(b);
            store.World.Set("season.prize", new SeasonCalendar.PrizeDay());
            int before = store.Data.Log.Count(e => e.Feature == SeasonCalendar.Tool);
            Need(SeasonCalendar.Prize(store, a, now) != null && SeasonCalendar.Prize(store, b, now) == null, "more than one prize a day");
            var paid = store.Data.Log.Where(e => e.Feature == SeasonCalendar.Tool).ToList();
            Need(paid.Count == before + 1 && paid.Last().TaskId == a.Id && paid.Last().Points == market.Prize, "the prize did not go through the ledger with the task");
            SeasonCalendar.TakeBack(store, a);
            Need(SeasonCalendar.Prize(store, b, now) != null, "the prize could not be won again after its task went back");
            store.Remember("a season test step");
            store.World.Set("season.prize", new SeasonCalendar.PrizeDay());
            store.Undo();
            Need(store.World.Get<SeasonCalendar.PrizeDay>("season.prize").TaskId == b.Id, "Control Z did not put the prize bag back");
            store.Data.Tasks.Remove(a); store.Data.Tasks.Remove(b);

            // Said once, only when the calm rules allow.
            var cfg = new Settings();
            var e2 = SeasonCalendar.ByWords("The fishing tournament")!;
            var noon = DateTime.Today.AddHours(12);
            SeasonCalendar.Activate(store, e2, noon, announce: true);
            int said = 0;
            store.Data.Classes.Clear();
            store.Data.Game.UnpromptedDay = "";
            Need(!SeasonCalendar.TrySay(store, cfg, DateTime.Today.AddHours(23), _ => { said++; return true; }) && said == 0, "an event spoke in quiet hours");
            if (!Calm.FunPaused)
            {
                Need(SeasonCalendar.TrySay(store, cfg, noon, l => { said++; return l.Contains(SeasonCalendar.Name(e2)); }) && said == 1, "an event was not said when allowed");
                Need(!SeasonCalendar.TrySay(store, cfg, noon, _ => { said++; return true; }) && said == 1, "an event was said twice");
            }

            // One village, one weather window, one hill: Stage 8's places live inside Stage 7's.
            Need(World.Places.Find("season.hill") == null, "the moonlit hill is a second hill");
            Need(PlaceWorld.Inside(Green) && PlaceWorld.Inside(WindowPlace), "the village green or the round window is in the top list");
            Need(World.Places.Find(Green) is Place gp && !gp.Unlocked(store) && World.Places.Find(WindowPlace) is Place wp && !wp.Unlocked(store), "an adopted place shows in the top list");
            Need(PlaceWorld.AdoptedInto(PlaceIds.Village).Contains(Green) && PlaceWorld.AdoptedInto(PlaceIds.Burrow).Contains(WindowPlace), "the green or the window has no way in");
            // The fog prize brings a shell for the chest, tied to its task; un-finishing takes it back.
            var fogTask = new TaskItem { Name = "season fog test", List = store.Data.Lists[0].Name };
            int chest0 = Treasure.All(store).Count;
            Need(WeatherShell(store, fogTask, "fog", now) != null && Treasure.All(store).Count == chest0 + 1 && WeatherShell(store, fogTask, "market", now) == null, "the fog prize shell");
            Treasure.TakeBack(store, fogTask.Id);
            Need(Treasure.All(store).Count == chest0, "the fog shell stayed after its task went back");
            Need(Burrows.All(b => !b.Contains("kitchen") && !b.Contains("guest")), "moving day names rooms she may not have built");

            // Ribbons: once per occasion, its own undo step.
            int wallet = Ledger.Wallet(store);
            Need(Ribbon(store, "selftest", "x", 3, "a test ribbon") && !Ribbon(store, "selftest", "x", 3, "a test ribbon") && Ledger.Wallet(store) == wallet + 3, "a ribbon paid twice");
            store.Undo();
            Need(Ledger.Wallet(store) == wallet && store.World.Get<Ribbons>("season.ribbons").Won.GetValueOrDefault("selftest") != "x", "Control Z did not take the ribbon back");

            // Snowman, moving day, weather words.
            var goals0 = store.Data.Game.GoalDays.ToList();
            store.Data.Game.GoalDays.Clear();
            store.Data.Game.GoalDays.AddRange(new[] { "2026-11-30", "2026-12-01", "2026-12-02", "2027-01-05" });
            Need(SnowmanPieces(store, new DateTime(2027, 1, 10)) == 3 && SnowmanPieces(store, new DateTime(2027, 4, 1)) == 0, "snowman pieces");
            store.Data.Game.GoalDays.Clear(); store.Data.Game.GoalDays.AddRange(goals0);
            var home0 = store.World.GetKept<Home>("season.home");
            Need(Move(store, new DateTime(2030, 6, 1)) && !Move(store, new DateTime(2030, 6, 2)), "moving day once a year");
            store.World.SetKept("season.home", home0);
            Need(SeasonSky.Parse("It is 62 degrees and partly cloudy, with a breeze.") == ("partly cloudy", 62) && SeasonSky.Parse(RealWorld.Mystery).Sky == "mystery", "reading the weather");
            Need(SeasonSky.WeatherDays("foggy", 50, noon.AddHours(-3)).Contains("A foggy morning") && !SeasonSky.WeatherDays("foggy", 50, noon.AddHours(2)).Contains("A foggy morning"), "fog is a morning thing");
            Need(SeasonSky.WeatherDays("stormy", 70, noon).Contains("A thunderstorm") && SeasonSky.WeatherDays("rainy", 55, noon).Contains("A rainy day"), "storm and rain days");
            Need(SeasonSky.MoodFor(noon, "rainy", cfg) == SeasonSky.RainMood && SeasonSky.MoodFor(new DateTime(2027, 4, 2, 12, 0, 0), "clear", cfg) == SeasonSky.SpringMood, "soundtrack moods");
            Need(SeasonSky.Report(store, "clear", null).Length > 0 && !SeasonSky.Report(store, "rainy", 50).Contains("{"), "the weather report");
            Need(SeasonSky.FullMoon(new DateTime(2026, 10, 26)) && !SeasonSky.FullMoon(new DateTime(2026, 11, 9)), "full moons");

            // Every arrangement of the soundtrack plays, and none clips.
            for (int m = 1; m < SeasonSky.MoodNames.Length; m++)
            {
                var (peak, rms) = MusicLoop.Measure(10, 0, m);
                Need(peak > 0.02 && rms > 0.002 && peak < 0.99, "soundtrack " + m + ": peak " + peak + ", average " + rms);
            }

            // Content: every event described, every repeating line type has 20, nothing forbidden.
            foreach (var ev in SeasonCalendar.Catalog) Need(SeasonCalendar.Info(ev.Id).ContainsKey("touch"), "no description for " + ev.Id);
            // Her bunny's name is filled in everywhere: no brace is ever spoken.
            foreach (var ev in SeasonCalendar.Catalog)
            {
                string words = SeasonCalendar.Announcement(ev, SeasonCalendar.Where(ev)) + " " + SeasonCalendar.Name(ev);
                Need(!words.Contains('{') && !words.Contains('}'), "a brace in the words for " + ev.Id);
            }
            var (sky0, temp0, at0) = (SeasonSky.Sky, SeasonSky.Temp, SeasonSky.ReadAt);
            try
            {
                foreach (var sky in Content.Items("season.sky").Select(i => i.GetValueOrDefault("sky") ?? "mystery").Distinct())
                {
                    SeasonSky.Use(sky, 60, now);
                    for (int month = 1; month <= 12; month++)
                        for (int day = 1; day <= 28; day += 3)
                            for (int hour = 0; hour < 24; hour += 5)
                                Need(!SeasonSky.Window(store, new DateTime(2027, month, day, hour, 0, 0), cfg).Contains('{'), "a brace at the window: " + sky + ", " + month + "/" + day);
                }
            }
            finally { SeasonSky.Use(sky0, temp0, at0); }
            // The arrangements stay in their recordings' range: rain's pizzicato tops out at midi 74, spring's flute at 84.
            for (int home = 55; home <= 70; home++)
            {
                var piece = new List<MusicLoop.Event> { new(0, "bells", home + 33, 0.05, 0, 1, 1, 0.01) };
                Need(SeasonSky.Arrange(piece, SeasonSky.RainMood, home).All(x => x.Bank != "pizzicato" || x.Midi <= 74), "rain notes too high");
                Need(SeasonSky.Arrange(new List<MusicLoop.Event>(), SeasonSky.SpringMood, home).All(x => x.Bank != "flute" || x.Midi <= home + 19), "spring calls too high");
            }
            foreach (var topic in new[] { "season.report", "season.report.mystery", "season.clouds", "season.fog", "season.rainbow", "season.storm", "season.storm.enter", "season.night", "season.puddles", "season.picnic", "season.moon.set", "season.moon.back", "season.moon.answer",
                "season.pumpkin", "season.haunted", "season.skating", "season.sled", "season.snowball", "season.snowday", "season.kite", "season.icecream", "season.bonfire", "season.campfire", "season.fireworks",
                "season.market", "season.games", "season.fishing", "season.garden", "season.decorate", "season.festival", "season.prize", "season.cleaning", "season.holiday" })
                Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            foreach (var topic in new[] { "season.events", "season.sky", "season.moon", "season.seasons", "season.decor", "season.snowman", "season.harvest", "season.lantern", "season.train", "season.moving", "season.anniversary", "season.bunnybirthday", "season.herbirthday", "season.butterfly", "season.sledhill" })
                Need(Content.Lines(topic).Count > 0, topic + " is missing");
            Need(Content.Lines("season.report").All(l => l.Contains("{sky}")), "a weather report line has no sky");
            Need(Content.Items("season.moon").Count == 8 && Content.Items("season.sky").Select(i => i.GetValueOrDefault("sky")).Distinct().Count() == 10, "moon phases and skies");
            foreach (var id in new[] { SeasonCalendar.Tool, SeasonCalendar.Village, SeasonCalendar.Weather, ReportTool, SeasonCalendar.Moon, MusicTool, SeasonCalendar.Winter, SeasonCalendar.Yearly, SeasonCalendar.Semester })
                Need(Tools.Find(id) != null, id + " is not in the toolbox");
        }
    }

    /// <summary>Small shared dialogs for the season places: a pick menu and a text box, Escape cancels, focus goes back to the owner.</summary>
    public static class SeasonAsk
    {
        /// <summary>The index she picked, or -1.</summary>
        public static int Pick(Window owner, string title, string label, IReadOnlyList<string> choices)
        {
            var w = Layout.Dialog(owner, title);
            var pick = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };
            int result = -1;
            Finish(w, Layout.Row(label, pick, 380), () => result = pick.SelectedIndex, pick);
            return result;
        }

        /// <summary>What she typed, or null.</summary>
        public static string? Text(Window owner, string title, string label)
        {
            var w = Layout.Dialog(owner, title);
            var box = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
            string? result = null;
            Finish(w, Layout.Row(label, box, 420), () => result = box.Text, box);
            return result;
        }

        private static void Finish(Window w, FrameworkElement row, Action ok, Control first)
        {
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(row);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            var okButton = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            okButton.Click += (_, _) => { ok(); w.Close(); };
            cancel.Click += (_, _) => w.Close();
            buttons.Children.Add(okButton);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) => first.Focus();
            var back = w.Owner;
            w.ShowDialog();
            back?.Activate();
        }
    }
}
