using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// The event bus (sweep 8.2 A). Features subscribe here instead of editing core code. Every handler runs on
    /// its own: one that throws is written to the error log and the others still run. All are raised on the UI thread.
    /// </summary>
    public static class GameEvents
    {
        /// <summary>A task became finished (Done, or Done not submitted) from not finished. Its Done points are already in the log.</summary>
        public static event Action<TaskItem>? TaskDone;
        /// <summary>A finished task went back to not finished. Its points (and every ledger entry with its id) are already taken back.</summary>
        public static event Action<TaskItem>? TaskUndone;
        /// <summary>Work began on a task: a sprint started on it.</summary>
        public static event Action<TaskItem>? TaskStarted;
        /// <summary>A sprint started: its minutes, and its task or null.</summary>
        public static event Action<int, TaskItem?>? SprintStarted;
        /// <summary>A sprint finished and counted: the minutes that counted.</summary>
        public static event Action<int>? SprintDone;
        /// <summary>A break began and ended. Raised by whichever feature runs breaks (none built yet).</summary>
        public static event Action? BreakStarted;
        public static event Action? BreakEnded;
        /// <summary>The first open of a day, or the morning check-in, whichever comes first. Once a day.</summary>
        public static event Action<DateTime>? DayStarted;
        /// <summary>A day ended: raised once when a new day starts (the first open of a day, or midnight with Busy Bunny open). Gives the day before the new one.</summary>
        public static event Action<DateTime>? DayEnded;
        /// <summary>The first day opened in a new week (Monday's date given).</summary>
        public static event Action<DateTime>? WeekStarted;
        /// <summary>The week is over: Friday from 3 pm, or the first open on the weekend or later. Once a week (Monday's date given).</summary>
        public static event Action<DateTime>? WeekEnded;
        /// <summary>Any status change: the task, then its old status. Raised after the points, before TaskDone or TaskUndone.</summary>
        public static event Action<TaskItem, Status>? StatusChanged;
        /// <summary>A task was pushed back a day.</summary>
        public static event Action<TaskItem>? Snoozed;
        /// <summary>A toolbox tool was used, by its id.</summary>
        public static event Action<string>? ToolUsed;
        /// <summary>The streak grew: the days in a row now.</summary>
        public static event Action<int>? StreakChanged;
        /// <summary>A new level was reached.</summary>
        public static event Action<int>? LevelUp;
        /// <summary>A badge was earned, by its name.</summary>
        public static event Action<string>? BadgeEarned;
        /// <summary>A special day (a holiday, or a new season like Winter begins): its words.</summary>
        public static event Action<string>? CalendarDay;

        private static void Each(Delegate? d, Action<Delegate> call, string name)
        {
            if (d == null) return;
            foreach (var h in d.GetInvocationList())
            {
                try { call(h); }
                catch (Exception e) { ErrorLog.Write("A feature failed on " + name + ": " + e.GetType().Name + ": " + e.Message); }
            }
        }

        public static void RaiseTaskDone(TaskItem t) => Each(TaskDone, h => ((Action<TaskItem>)h)(t), "TaskDone");
        public static void RaiseTaskUndone(TaskItem t) => Each(TaskUndone, h => ((Action<TaskItem>)h)(t), "TaskUndone");
        public static void RaiseTaskStarted(TaskItem t) => Each(TaskStarted, h => ((Action<TaskItem>)h)(t), "TaskStarted");
        public static void RaiseSprintStarted(int minutes, TaskItem? t) => Each(SprintStarted, h => ((Action<int, TaskItem?>)h)(minutes, t), "SprintStarted");
        public static void RaiseSprintDone(int minutes) => Each(SprintDone, h => ((Action<int>)h)(minutes), "SprintDone");
        public static void RaiseBreakStarted() => Each(BreakStarted, h => ((Action)h)(), "BreakStarted");
        public static void RaiseBreakEnded() => Each(BreakEnded, h => ((Action)h)(), "BreakEnded");
        public static void RaiseStatusChanged(TaskItem t, Status old) => Each(StatusChanged, h => ((Action<TaskItem, Status>)h)(t, old), "StatusChanged");
        public static void RaiseSnoozed(TaskItem t) => Each(Snoozed, h => ((Action<TaskItem>)h)(t), "Snoozed");
        public static void RaiseToolUsed(string id) => Each(ToolUsed, h => ((Action<string>)h)(id), "ToolUsed");
        public static void RaiseStreakChanged(int days) => Each(StreakChanged, h => ((Action<int>)h)(days), "StreakChanged");
        public static void RaiseLevelUp(int level) => Each(LevelUp, h => ((Action<int>)h)(level), "LevelUp");
        public static void RaiseBadgeEarned(string name) => Each(BadgeEarned, h => ((Action<string>)h)(name), "BadgeEarned");
        public static void RaiseCalendarDay(string what) => Each(CalendarDay, h => ((Action<string>)h)(what), "CalendarDay");

        // Once-a-day and once-a-week events remember what they last raised (in memory and in world.json), so a restart never repeats them.
        private const string Marks = "events.marks";
        private sealed class EventMarks { public string Day { get; set; } = ""; public string Ended { get; set; } = ""; public string Week { get; set; } = ""; public string WeekEnd { get; set; } = ""; public string Season { get; set; } = ""; }

        /// <summary>Raises DayStarted for today once, then WeekStarted and CalendarDay when they are due. Safe to call often.</summary>
        public static void RaiseDayStarted(Store store, DateTime now)
        {
            var m = store.World.GetKept<EventMarks>(Marks);
            string day = store.Key(now);
            if (m.Day == day) return;
            m.Day = day;
            string week = store.Key(Engine.WeekStart(now));
            bool newWeek = m.Week != week;
            m.Week = week;
            string season = RealWorld.Season(now);
            bool newSeason = m.Season.Length > 0 && m.Season != season;
            m.Season = season;
            store.World.SetKept(Marks, m);
            store.World.Save();
            Each(DayStarted, h => ((Action<DateTime>)h)(now.Date), "DayStarted");
            if (newWeek) Each(WeekStarted, h => ((Action<DateTime>)h)(Engine.WeekStart(now)), "WeekStarted");
            if (newSeason) RaiseCalendarDay(season + " begins");
            if (RealWorld.Holiday(now) is string holiday) RaiseCalendarDay(holiday);
        }

        /// <summary>Raises DayEnded once for the day given (the end-of-day reset calls it).</summary>
        public static void RaiseDayEnded(Store store, DateTime day)
        {
            var m = store.World.GetKept<EventMarks>(Marks);
            string key = store.Key(day);
            if (string.CompareOrdinal(m.Ended, key) >= 0) return;
            m.Ended = key;
            store.World.SetKept(Marks, m);
            store.World.Save();
            Each(DayEnded, h => ((Action<DateTime>)h)(day.Date), "DayEnded");
        }

        /// <summary>Raises WeekEnded once a week: Friday from 3 pm, or any time on the weekend, or for a week that ended while Busy Bunny was closed.</summary>
        public static void CheckWeekEnded(Store store, DateTime now)
        {
            var monday = Engine.WeekStart(now);
            bool over = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || now.DayOfWeek == DayOfWeek.Friday && now.Hour >= 15;
            var which = over ? monday : monday.AddDays(-7);
            var m = store.World.GetKept<EventMarks>(Marks);
            string key = store.Key(which);
            if (string.CompareOrdinal(m.WeekEnd, key) >= 0) return;
            // A brand new world has not lived through last week: start counting from this one.
            if (m.WeekEnd.Length == 0 && !over) { m.WeekEnd = key; store.World.SetKept(Marks, m); return; }
            m.WeekEnd = key;
            store.World.SetKept(Marks, m);
            store.World.Save();
            Each(WeekEnded, h => ((Action<DateTime>)h)(which), "WeekEnded");
        }
    }

    /// <summary>
    /// Ties the engines to the main window once: the event bus to status changes and new days, the ticker to the
    /// minute timer, the calm rules to her settings, and says once if world.json had to be rescued.
    /// </summary>
    public sealed class EngineHooks : IFeature
    {
        public void Attach(MainWindow w)
        {
            var store = w.Store;
            Engines.Use(w);
            _ = store.World; // loaded now, so every undo step from here on holds it too
            w.Started += () =>
            {
                if (store.World.LoadNote is string note) { store.World.LoadNote = null; w.Say(note); }
                GameEvents.RaiseDayStarted(store, DateTime.Now);
                GameEvents.CheckWeekEnded(store, DateTime.Now);
            };
            w.NewDay += _ => GameEvents.RaiseDayStarted(store, DateTime.Now);
            Ticker.Attach(w);
            Ticker.Every("events.weekended", _ => true, now => GameEvents.CheckWeekEnded(store, now));
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("engines: " + what); }
            // One failing handler never stops the next one.
            int ran = 0;
            Action<string> bad = _ => throw new Exception("boom"), good = _ => ran++;
            GameEvents.ToolUsed += bad;
            GameEvents.ToolUsed += good;
            try { GameEvents.RaiseToolUsed("x"); }
            finally { GameEvents.ToolUsed -= bad; GameEvents.ToolUsed -= good; }
            Need(ran == 1, "a failing handler stopped the others");
            // Once a day.
            int days = 0;
            Action<DateTime> count = _ => days++;
            GameEvents.DayStarted += count;
            try
            {
                var monday = new DateTime(2026, 11, 2, 9, 0, 0);
                GameEvents.RaiseDayStarted(store, monday);
                GameEvents.RaiseDayStarted(store, monday.AddHours(3));
                GameEvents.RaiseDayStarted(store, monday.AddDays(1));
            }
            finally { GameEvents.DayStarted -= count; }
            Need(days == 2, "DayStarted ran " + days + " times for two days");

            Ledger.SelfTest(store);
            BunnyWrite.SelfTest(store);
            Calm.SelfTest(store);
            Content.SelfTest(store);
            Ticker.SelfTest();
            RealWorld.SelfTest();
            WorldWindow.SelfTest(store);
        }
    }

    /// <summary>The main window, store and settings the engines work with, set once when the window is made.</summary>
    public static class Engines
    {
        public static MainWindow? Window { get; private set; }
        public static Store? Store => Window?.Store;
        public static Settings? Settings => Window?.AppSettings;
        public static void Use(MainWindow w) => Window = w;
    }
}
