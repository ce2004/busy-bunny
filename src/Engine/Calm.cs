using System;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// The calm rules (sweep 8.1), one place for every feature. Calm mode is the master switch that pauses every fun
    /// feature at once without losing progress. Lines nobody asked for are capped each day (3 unless she changes it)
    /// and never come in quiet hours (10 pm to 8 am unless she changes them), during a class, a sprint, vacation or a
    /// rough day. After a finished task only the most important fun line is spoken (level up, then a record, then the
    /// rest); everything else waits in What happened today. Game.Calm and Game.MaySpeakUnprompted ask this class.
    /// </summary>
    public static class Calm
    {
        private static Settings? Cfg => Engines.Settings;

        /// <summary>Calm mode is on: fun features stay quiet and do nothing by themselves. Tools she opens still work.</summary>
        public static bool FunPaused => Cfg?.CalmMode == true;

        /// <summary>Whether it is quiet hours at this time, for these settings (start and end are hours, 0 to 23; start may be after end).</summary>
        public static bool QuietHours(Settings? s, DateTime now)
        {
            int start = Math.Clamp(s?.QuietStart ?? 22, 0, 23), end = Math.Clamp(s?.QuietEnd ?? 8, 0, 23);
            int h = now.Hour;
            if (start == end) return false;
            return start > end ? h >= start || h < end : h >= start && h < end;
        }

        /// <summary>Sprint timers other than Start a sprint (body double mode): while one says true, it is a sprint for the calm rules.</summary>
        public static readonly System.Collections.Generic.List<Func<bool>> OtherSprints = new();
        /// <summary>Other focus timers (a toolbox timer like the two-minute dare) that keep unprompted lines quiet, as a sprint does.</summary>
        public static readonly System.Collections.Generic.List<Func<bool>> FocusTimers = new();

        /// <summary>Why unprompted lines must wait right now, or null when they may come. Does not count anything.</summary>
        public static string? Hush(Store s, Settings? cfg, DateTime now)
        {
            if (cfg?.CalmMode == true) return "Calm mode";
            if (QuietHours(cfg, now)) return "quiet hours";
            if (FocusTools.SprintRunning || OtherSprints.Any(f => f()) || FocusTimers.Any(f => f())) return "a sprint";
            if (s.Data.Days.TryGetValue(s.Key(now), out var day) && day.Mode is DayMode.Vacation or DayMode.RoughDay) return day.Mode == DayMode.Vacation ? "vacation" : "a rough day";
            if (s.Data.Classes.Any(c => c.Days.Contains(now.DayOfWeek) && c.Start <= now.TimeOfDay && now.TimeOfDay < c.End)) return "class";
            return null;
        }

        /// <summary>Whether an unprompted line may be spoken now, under today's cap. Counts nothing; SayUnprompted counts.</summary>
        public static bool CanSpeakUnprompted(Store s, Settings? cfg, DateTime now)
        {
            if (Hush(s, cfg, now) != null) return false;
            var g = s.Data.Game;
            int used = g.UnpromptedDay == s.Key(now) ? g.UnpromptedCount : 0;
            return used < Math.Max(0, cfg?.UnpromptedCap ?? 3);
        }

        /// <summary>The same for the main window's store and her settings.</summary>
        public static bool CanSpeakUnprompted(DateTime now) => Engines.Store is Store s && CanSpeakUnprompted(s, Cfg, now);

        /// <summary>Counts one unprompted line toward today's cap.</summary>
        public static void Count(Store s, DateTime now)
        {
            var g = s.Data.Game;
            string today = s.Key(now);
            if (g.UnpromptedDay != today) { g.UnpromptedDay = today; g.UnpromptedCount = 0; }
            g.UnpromptedCount++;
        }

        /// <summary>
        /// A line nobody asked for: always kept in What happened today, and spoken when the calm rules allow (then it
        /// counts toward today's cap). noted true when the caller already kept it with Game.Note. Returns whether it was spoken.
        /// The count and What happened today are not put back by Control Z.
        /// </summary>
        public static bool SayUnprompted(MainWindow w, string text, bool noted = false)
        {
            if (text.Length == 0) return false;
            var now = DateTime.Now;
            if (!noted) Game.Note(w.Store, text);
            if (!CanSpeakUnprompted(w.Store, w.AppSettings, now)) return false;
            Count(w.Store, now);
            w.Say(text);
            return true;
        }

        /// <summary>
        /// A fun line after a finished task. Every line offered for one finish is weighed together and only the most
        /// important is spoken; the rest go to What happened today. Priority: Game.LevelUp, Game.Record, Game.Goals,
        /// Game.Other, Game.Badge, Game.Chatter (lower is more important). In Calm mode it is only kept.
        /// </summary>
        public static void AfterTaskDone(MainWindow w, int priority, string text)
        {
            if (text.Length == 0) return;
            if (FunPaused) { Game.Note(w.Store, text); return; }
            Game.Queue(w, priority, text);
        }

        public static void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("calm: " + what); }
            var cfg = new Settings();
            var noon = new DateTime(2026, 11, 3, 12, 0, 0); // a Tuesday
            store.Data.Classes.Clear();
            store.Data.Days.Remove(store.Key(noon));
            store.Data.Game.UnpromptedDay = "";
            Need(QuietHours(cfg, noon.Date.AddHours(23)) && QuietHours(cfg, noon.Date.AddHours(7)) && !QuietHours(cfg, noon), "quiet hours, 10 pm to 8 am");
            cfg.QuietStart = 13; cfg.QuietEnd = 15;
            Need(QuietHours(cfg, noon.AddHours(1)) && !QuietHours(cfg, noon), "quiet hours she set");
            cfg.QuietStart = 22; cfg.QuietEnd = 8;
            for (int i = 0; i < 3; i++) { Need(CanSpeakUnprompted(store, cfg, noon), "line " + (i + 1) + " held back"); Count(store, noon); }
            Need(!CanSpeakUnprompted(store, cfg, noon), "the daily cap of 3");
            Need(CanSpeakUnprompted(store, cfg, noon.AddDays(1)), "the cap starts over the next day");
            cfg.UnpromptedCap = 5;
            Need(CanSpeakUnprompted(store, cfg, noon), "a higher cap she chose");
            cfg.CalmMode = true;
            Need(!CanSpeakUnprompted(store, cfg, noon.AddDays(1)), "Calm mode");
            cfg.CalmMode = false;
            store.Data.Classes.Add(new ClassTime { Tag = "PSY", Days = new() { DayOfWeek.Wednesday }, Start = TimeSpan.FromHours(11), End = TimeSpan.FromHours(13) });
            Need(!CanSpeakUnprompted(store, cfg, noon.AddDays(1)), "during class");
            store.Data.Classes.Clear();

            // Control Z never gives back today's unprompted lines or What happened today.
            var today = DateTime.Now;
            store.Data.Game.UnpromptedDay = "";
            store.Remember("a step before three lines");
            for (int i = 0; i < 3; i++) Count(store, today);
            Game.Note(store, "ZZ kept line");
            store.Undo();
            Need(store.Data.Game.UnpromptedCount == 3 && store.Data.Game.Happened.Any(l => l.EndsWith("ZZ kept line")), "undo reset the daily cap or What happened today");

            // Calm mode: the points line is said, the fun line and the narrator are only kept.
            if (Engines.Window is MainWindow w && Engines.Settings is Settings real)
            {
                bool was = real.CalmMode;
                real.CalmMode = true;
                try
                {
                    var o = new Outcome { Gained = 5 };
                    o.Add(Game.LevelUp, "ZZ level up line");
                    Game.Queue(w, o);
                    var late = Game.QueueLate(w, -1);
                    string said = Game.Flush(null, w.Store);
                    Need(said.Contains("5 points") && !said.Contains("ZZ level up line") && !late.Chosen, "Calm mode spoke a fun line: " + said);
                    // Unprompted lines are kept, never said, in Calm mode.
                    int before = w.Store.Data.Game.UnpromptedDay == w.Store.Key(today) ? w.Store.Data.Game.UnpromptedCount : 0;
                    Need(!SayUnprompted(w, "ZZ quiet line") && w.Store.Data.Game.Happened.Any(l => l.EndsWith("ZZ quiet line")), "Calm mode said an unprompted line");
                    Need((w.Store.Data.Game.UnpromptedDay == w.Store.Key(today) ? w.Store.Data.Game.UnpromptedCount : 0) == before, "a line kept quiet counted toward the cap");
                }
                finally { real.CalmMode = was; }
            }
        }
    }
}
