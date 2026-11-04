using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// Automatic points (each with its own switch in Settings, Fun and tools): fair points on low-energy days, help
    /// points, self-care counts double on rough days, second wind, well ahead (early finish at 2 or more days, on top of
    /// the app's own early bonus), consistency multiplier, honest snoozing, halfway cheers, planning tomorrow, breaks on
    /// time and the tidy list bonus. Every double goes through the ledger's cap of 4 times.
    /// </summary>
    public sealed class AutoPoints : IFeature
    {
        public sealed class DayCount { public string Day { get; set; } = ""; public int Count { get; set; } }
        public sealed class Paid { public HashSet<Guid> Tasks { get; set; } = new(); }
        public sealed class WeekMark { public string Week { get; set; } = ""; }
        public sealed class SprintMark { public DateTime LastDone { get; set; } public DateTime BreakStart { get; set; } }

        public const string FairId = "auto.fairpoints", HelpId = "auto.helppoints", CareId = "auto.selfcare", WindId = "auto.secondwind",
            EarlyId = "auto.wellahead", SteadyId = "auto.consistency", SnoozeId = "auto.honestsnooze", HalfId = "auto.halfway",
            PlanId = "auto.plantomorrow", BreakId = "auto.breaks", TidyId = "auto.tidy";
        public const int SnoozeCap = 3, BreakCap = 5;

        private MainWindow _w = null!;
        private Store S => _w.Store;
        private bool _primed;
        private int _sweepLeft = -1;

        public void Attach(MainWindow w)
        {
            _w = w;
            void T(string id, string name, string drawer, string what, Func<string> status) =>
                Tools.Register(new BunnyTool(id, name, drawer, what, () => _w.Say(name + " is on. " + status(), true)) { Since = "0.14.0" });
            T(FairId, "Fair points on low-energy days", Drawers.Rest, "Automatic: on a low-energy or Burrow nap day, small tasks pay double.",
                () => LowEnergyDay(S, w.AppSettings, DateTime.Now) ? "Today counts as low energy, so small tasks pay double." : "Today is not a low-energy day, so small tasks pay as usual.");
            T(HelpId, "Help points", Drawers.Wins, "Automatic: asking for help, like emailing a professor or an accessibility request, pays double.",
                () => "Tasks with help, email, ask, office hours or accessibility in the name or tag pay double.");
            T(CareId, "Self-care counts double on rough days", Drawers.Rest, "Automatic: on a rough day, personal care tasks pay double.",
                () => "On a day you mark as rough, tasks like meals, showers, meds, rest and walks pay double.");
            T(WindId, "Second wind bonus", Drawers.Wins, "Automatic: the first task after a slow morning or a long break pays half again.",
                () => "Nothing done by noon, or a break of over 2 hours, and the next task pays 50 percent extra.");
            T(EarlyId, "Well ahead bonus", Drawers.Wins, "Automatic: finishing 2 or more days before it is due pays extra, on top of the early bonus.",
                () => "2 to 6 days ahead pays 5 more, a week or more pays 10 more.");
            T(SteadyId, "Consistency multiplier", Drawers.Wins, "Automatic: working on 5 or more days in a week adds 10 percent for the rest of the week.",
                () => "You have worked on " + PlanKit.Plural(DaysWorked(S, DateTime.Now), "day", "days") + " this week." + (Ledger.Active(S, DateTime.Now).Any(m => m.Id == SteadyId) ? " The 10 percent is running." : ""));
            T(SnoozeId, "Points for honest snoozing", Drawers.Rest, "Automatic: pushing a task back before it is overdue pays 1 point, up to 3 a day.",
                () => "Pushing back on purpose, before it is overdue, is good planning.");
            T(HalfId, "Halfway cheers", Drawers.Wins, "Automatic: a small reward when half of a task's subtasks are done.", () => "Half the subtasks of a task done pays 3 points, once per task.");
            T(PlanId, "Points for planning tomorrow", Drawers.Plan, "Automatic: picking tomorrow's first task before midnight pays 3 points, once a day.", () => "Pick tomorrow's first task, in the Plan drawer, to get them.");
            T(BreakId, "Points for breaks on time", Drawers.Rest, "Automatic: a short break between sprints, then back on time, pays 2 points, up to 5 a day.",
                () => "Finish a sprint, rest 3 to 20 minutes, start the next one: 2 points.");
            T(TidyId, "Tidy list bonus", Drawers.Wins, "Automatic: finishing a backlog sweep pays 10 points, once a week.", () => "View, Backlog sweep is where it starts.");

            GameEvents.TaskDone += OnDone;
            GameEvents.Snoozed += OnSnoozed;
            GameEvents.SprintDone += _ => { var m = S.World.GetKept<SprintMark>(BreakId); m.LastDone = DateTime.Now; S.World.SetKept(BreakId, m); S.World.Save(); };
            GameEvents.SprintStarted += (_, _) =>
            {
                var m = S.World.GetKept<SprintMark>(BreakId);
                if (OnTimeBreak(m.LastDone, DateTime.Now)) PayBreak();
            };
            GameEvents.BreakStarted += () => { var m = S.World.GetKept<SprintMark>(BreakId); m.BreakStart = DateTime.Now; S.World.SetKept(BreakId, m); S.World.Save(); };
            GameEvents.BreakEnded += () =>
            {
                var m = S.World.GetKept<SprintMark>(BreakId);
                if (m.BreakStart > DateTime.MinValue && DateTime.Now - m.BreakStart <= TimeSpan.FromMinutes(20)) PayBreak();
            };
            w.Refreshed += OnRefreshed;
        }

        // ---- Rules, static for the self-test ----

        /// <summary>Extra points for a multiplier on base points: running ledger multipliers apply, and base plus extra stays within 4 times.</summary>
        public static int Extra(Store s, int basePoints, double factor)
        {
            if (factor <= 1 || basePoints <= 0) return 0;
            int extra = Ledger.Boost(s, (int)Math.Round(basePoints * (factor - 1)));
            return Math.Min(extra, (int)Math.Round(basePoints * (Ledger.MaxBoost - 1)));
        }

        public static bool LowEnergyDay(Store s, Settings? cfg, DateTime now) =>
            cfg?.LowEnergy == true || s.Data.PeekDay(now).Energy.Equals("low", StringComparison.OrdinalIgnoreCase) || s.DayType(now) == "Burrow nap";

        private static bool Has(TaskItem t, params string[] words) =>
            words.Any(x => System.Text.RegularExpressions.Regex.IsMatch(t.Name + " " + t.Tag, @"\b" + System.Text.RegularExpressions.Regex.Escape(x), System.Text.RegularExpressions.RegexOptions.IgnoreCase));

        public static bool IsHelp(TaskItem t) => Has(t, "help", "email prof", "email the prof", "professor", "office hours", "accessibility", "accommodation", "ask for", "tutor", "advisor");

        public static bool IsSelfCare(TaskItem t) => Has(t, "self-care", "self care", "shower", "meal", "eat", "lunch", "dinner", "breakfast", "sleep", "nap", "rest", "meds", "medicine",
            "prescription", "doctor", "therapy", "walk", "stretch", "water", "laundry", "groceries", "bath");

        public static bool RoughDay(Store s, DateTime now) => s.Data.PeekDay(now).Mode == DayMode.RoughDay;

        /// <summary>Extra points for well ahead: 5 at 2 to 6 days before it is due (or planned), 10 at a week or more.</summary>
        public static int WellAhead(TaskItem t, DateTime now)
        {
            if ((t.Due ?? t.DoDate) is not DateTime d) return 0;
            int days = (d.Date - now.Date).Days;
            return days >= 7 ? 10 : days >= 2 ? 5 : 0;
        }

        /// <summary>Days this week with something done, logged or a sprint.</summary>
        public static int DaysWorked(Store s, DateTime now)
        {
            var start = Engine.WeekStart(now);
            return s.Data.Log.Where(e => e.When >= start && e.When < start.AddDays(7) && e.Kind is "Done" or "DidAThing" or "Sprint").Select(e => e.When.Date).Distinct().Count();
        }

        /// <summary>Second wind: nothing finished before noon and this is the first, or the last finish today was over 2 hours ago.</summary>
        public static bool SecondWind(Store s, TaskItem t, DateTime now)
        {
            var before = s.Data.Log.Where(e => e.When.Date == now.Date && e.Kind is "Done" or "DidAThing" && e.TaskId != t.Id && e.When < now).Select(e => e.When).ToList();
            if (before.Count == 0) return now.Hour >= 12;
            return now - before.Max() > TimeSpan.FromHours(2);
        }

        /// <summary>A day's counter that stops at a cap. True when it counted one more.</summary>
        public static bool Count(Store s, string id, DateTime now, int cap)
        {
            var c = s.World.GetKept<DayCount>(id);
            if (c.Day != s.Key(now)) c = new DayCount { Day = s.Key(now) };
            if (c.Count >= cap) return false;
            c.Count++;
            s.World.SetKept(id, c);
            s.World.Save();
            return true;
        }

        public static bool OnTimeBreak(DateTime lastSprintDone, DateTime now) =>
            lastSprintDone > DateTime.MinValue && now - lastSprintDone >= TimeSpan.FromMinutes(3) && now - lastSprintDone <= TimeSpan.FromMinutes(20);

        /// <summary>Whether the task was not overdue before being pushed back a day (Nudges moves the do date, or the due date when there is none).</summary>
        public static bool HonestSnooze(TaskItem t, DateTime now)
        {
            if (t.Due is not DateTime due) return true;
            var before = t.DoDate == null ? due.AddDays(-1) : due;
            return before.Date >= now.Date;
        }

        // ---- Handlers ----

        private void OnDone(TaskItem t)
        {
            var s = S;
            var now = DateTime.Now;
            int basePts = Engine.PointsFor(t, s.ListOf(t), half: t.Status == Status.DoneNotSubmitted);
            double factor = 1;
            var why = new List<string>();
            if (Tools.IsOn(FairId) && t.Size == Size.Small && LowEnergyDay(s, _w.AppSettings, now)) { factor *= 2; why.Add("a small task on a low-energy day"); }
            if (Tools.IsOn(HelpId) && IsHelp(t)) { factor *= 2; why.Add("asking for help"); }
            if (Tools.IsOn(CareId) && RoughDay(s, now) && IsSelfCare(t)) { factor *= 2; why.Add("self-care on a rough day"); }
            if (Tools.IsOn(WindId) && SecondWind(s, t, now) && Count(s, WindId, now, 2)) { factor *= 1.5; why.Add("your second wind"); }
            int extra = Extra(s, basePts, factor);
            if (extra > 0 && Ledger.Pay(s, extra, "Extra for " + string.Join(" and ", why), why.Count == 1 && why[0] == "your second wind" ? WindId : FairId, t.Id) is Outcome o)
            {
                o.Fun.Insert(0, (Game.Other, "Plus " + extra + " for " + string.Join(" and ", why) + "."));
                Game.Queue(_w, o);
            }
            if (Tools.IsOn(EarlyId) && WellAhead(t, now) is int ahead and > 0 && Ledger.Pay(s, Ledger.Boost(s, ahead, t), "Well ahead: " + t.Name, EarlyId, t.Id) is Outcome e)
            {
                e.Fun.Insert(0, (Game.Other, "Plus " + e.Gained + " more for being well ahead."));
                Game.Queue(_w, e);
            }
            if (Tools.IsOn(SteadyId) && DaysWorked(s, now) >= 5)
            {
                var m = s.World.GetKept<WeekMark>(SteadyId);
                string week = s.Key(Engine.WeekStart(now));
                if (m.Week != week)
                {
                    s.World.SetKept(SteadyId, new WeekMark { Week = week });
                    Ledger.AddMultiplier(s, SteadyId, 1.1, Store.WeekEnd(now).AddDays(1), "five days of work this week");
                    Calm.AfterTaskDone(_w, Game.Goals, Content.Pick(s, "auto.consistency"));
                }
            }
            s.Save();
        }

        private void OnSnoozed(TaskItem t)
        {
            var now = DateTime.Now;
            if (!Tools.IsOn(SnoozeId) || !HonestSnooze(t, now) || !Count(S, SnoozeId, now, SnoozeCap)) return;
            // Tied to the push back she just made: one Control Z undoes both.
            Ledger.Pay(_w, 1, "honest snoozing", SnoozeId, voice: PayVoice.Silent);
        }

        private void PayBreak()
        {
            if (!Tools.IsOn(BreakId) || !Count(S, BreakId, DateTime.Now, BreakCap)) return;
            Ledger.Pay(_w, Ledger.Boost(S, 2), "a break on time", BreakId, remember: true, voice: PayVoice.Asked);
        }

        /// <summary>Points for planning tomorrow, once a day. Tied to the change she just made (no Remember of its own). Returns the points.</summary>
        public static int PlannedTomorrow(MainWindow w)
        {
            var now = DateTime.Now;
            if (!Tools.IsOn(PlanId) || !Count(w.Store, PlanId, now, 1)) return 0;
            var o = Ledger.Pay(w.Store, Ledger.Boost(w.Store, 3), "planning tomorrow", PlanId);
            w.Store.Save();
            if (o != null) foreach (var l in o.Said) Game.Note(w.Store, l);
            return o?.Gained ?? 0;
        }

        private void OnRefreshed()
        {
            var s = S;
            var now = DateTime.Now;
            // Halfway cheers: once per task. The first look after starting only notes what is already halfway.
            var half = s.Data.Tasks.Where(t => t.Subtasks.Count >= 2 && !PlanKit.Finished(t) && t.Subtasks.Count(x => x.Done) * 2 >= t.Subtasks.Count).ToList();
            var paid = s.World.Get<Paid>(HalfId);
            var fresh = half.Where(t => !paid.Tasks.Contains(t.Id)).ToList();
            if (fresh.Count > 0)
            {
                foreach (var t in fresh) paid.Tasks.Add(t.Id);
                s.World.Set(HalfId, paid);
                s.World.Save();
                if (_primed && Tools.IsOn(HalfId))
                {
                    var t = fresh[0];
                    if (Ledger.Pay(s, Ledger.Boost(s, 3), "Halfway there: " + t.Name, HalfId) is Outcome o) Game.Queue(_w, o);
                    s.Save();
                    Calm.AfterTaskDone(_w, Game.Other, PlanKit.Line(s, "auto.halfway", ("task", t.Name)));
                }
            }
            _primed = true;

            // Tidy list bonus: the backlog sweep went from 3 or more stale tasks to none.
            int left = Review.SweepCandidates(s.Data.Tasks.Where(t => !s.ListOf(t).Archived), now).Count;
            if (_sweepLeft >= 3 && left == 0 && Tools.IsOn(TidyId))
            {
                var m = s.World.GetKept<WeekMark>(TidyId);
                string week = s.Key(Engine.WeekStart(now));
                if (m.Week != week)
                {
                    s.World.SetKept(TidyId, new WeekMark { Week = week });
                    if (Ledger.Pay(s, Ledger.Boost(s, 10), "Tidy list bonus", TidyId) is Outcome o) { o.Fun.Insert(0, (Game.Other, "Tidy list bonus. Every old task looked at.")); Game.Queue(_w, o); }
                    s.Save();
                }
            }
            _sweepLeft = left;
        }

        // ---- Self-test ----

        public void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("auto points: " + what); }
            var now = DateTime.Now;
            // The cap: double of 10 is 10 extra; three doubles stop at 4 times in all; a running multiplier counts.
            Need(Extra(s, 10, 2) == 10 && Extra(s, 10, 8) == 30 && Extra(s, 10, 1) == 0, "extra points");
            Ledger.AddMultiplier(s, "test.boost", 3, now.AddHours(1), "test");
            Need(Extra(s, 10, 2) == 30, "a running multiplier did not apply, or broke the cap: " + Extra(s, 10, 2));
            Ledger.RemoveMultiplier(s, "test.boost");

            var t = new TaskItem { Name = "email professor about accessibility", List = s.Data.Lists[0].Name, Size = Size.Small };
            Need(IsHelp(t) && !IsSelfCare(t) && IsSelfCare(new TaskItem { Name = "eat lunch" }) && !IsSelfCare(new TaskItem { Name = "create slides" }) && !IsHelp(new TaskItem { Name = "finish task list" }), "help and self-care words");
            t.Due = now.Date.AddDays(3); Need(WellAhead(t, now) == 5, "well ahead 3 days");
            t.Due = now.Date.AddDays(9); Need(WellAhead(t, now) == 10, "well ahead 9 days");
            t.Due = now.Date.AddDays(1); Need(WellAhead(t, now) == 0, "one day ahead is the app's own early bonus only");

            // Honest snoozing: pushed from today is honest, pushed from overdue is not; capped at 3 a day.
            Need(HonestSnooze(new TaskItem { Due = now.Date.AddDays(1) }, now) && !HonestSnooze(new TaskItem { Due = now.Date }, now), "honest snooze rule");
            Need(Count(s, "test.cap", now, 3) && Count(s, "test.cap", now, 3) && Count(s, "test.cap", now, 3) && !Count(s, "test.cap", now, 3), "the cap of 3 a day");
            Need(Count(s, "test.cap", now.AddDays(1), 3), "the cap did not reset the next day");

            Need(OnTimeBreak(now.AddMinutes(-10), now) && !OnTimeBreak(now.AddMinutes(-1), now) && !OnTimeBreak(now.AddMinutes(-40), now) && !OnTimeBreak(DateTime.MinValue, now), "break timing");

            // Paid for a task, taken back when it is un-finished.
            s.Data.Tasks.Add(t);
            t.Status = Status.Done;
            int before = Engine.Total(s.Data);
            Ledger.Pay(s, Extra(s, 5, 2), "test fair", FairId, t.Id);
            t.Status = Status.Today;
            Engine.Unfinish(s, t, now);
            Need(Engine.Total(s.Data) == before, "undo left the extra points");
            s.Data.Tasks.Remove(t);
            foreach (var topic in new[] { "auto.halfway", "auto.consistency" }) Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            foreach (var id in new[] { FairId, HelpId, CareId, WindId, EarlyId, SteadyId, SnoozeId, HalfId, PlanId, BreakId, TidyId }) Need(Tools.Find(id) != null, id + " is not in the toolbox");
        }
    }
}
