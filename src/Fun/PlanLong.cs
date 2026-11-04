using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// Plan drawer, the longer view: goal ladders, one goal this week (the weekly quest), weekend freedom meter, Sunday
    /// reset, Monday preview, monthly fresh start, monthly goals board, reverse planner and the monthly semester-goals check-in.
    /// </summary>
    public sealed class PlanLong : IFeature
    {
        public sealed class Ladders { public HashSet<Guid> Tasks { get; set; } = new(); }
        public sealed class LadderSeen { public Dictionary<Guid, int> Done { get; set; } = new(); }
        public sealed class WeekQuest { public string Week { get; set; } = ""; public string Text { get; set; } = ""; public Guid? TaskId { get; set; } public bool Done { get; set; } public bool Paid { get; set; } }
        public sealed class MonthGoal { public string Text { get; set; } = ""; public bool Met { get; set; } public bool Checked { get; set; } }
        public sealed class Board { public Dictionary<string, List<MonthGoal>> Months { get; set; } = new(); }
        public sealed class FreshStart { public string Month { get; set; } = ""; public string Carry { get; set; } = ""; }
        public sealed class SemCheck { public Dictionary<string, List<string>> Months { get; set; } = new(); }
        public sealed class PreviewMark { public string Week { get; set; } = ""; }

        public const string LadderId = "plan.ladder", QuestId = "plan.weekquest", WeekendId = "plan.weekend", SundayId = "plan.sundayreset",
            MondayId = "plan.monday", FreshId = "plan.freshstart", BoardId = "plan.goalsboard", ReverseId = "plan.reverse", SemId = "plan.semcheck";
        public const int QuestPrize = 25, GoalPoints = 5, MaxGoals = 5;

        private MainWindow _w = null!;
        private Store S => _w.Store;

        public void Attach(MainWindow w)
        {
            _w = w;
            void T(string id, string name, string what, Action run) => Tools.Register(new BunnyTool(id, name, Drawers.Plan, what, run) { Since = "0.14.0" });
            T(LadderId, "Goal ladders", "Turn a personal goal into rungs; each rung climbs a tone.", Ladder);
            T(QuestId, "One goal this week", "Pick one meaningful goal for the week; a prize at the week's end if you did it.", Quest);
            T(WeekendId, "Weekend freedom meter", "How much of your weekend is free, from what is left this week.", () => _w.Say(WeekendLine(S, DateTime.Now), true));
            T(SundayId, "Sunday reset", "Look back at the week, pick next week's goal, look ahead.", SundayReset);
            T(MondayId, "Monday preview", "A short look at the week ahead, with a pep line.", () => _w.Say(Preview(S, DateTime.Now, pep: true), true));
            T(FreshId, "Monthly fresh start", "A short reset: what to let go of, what to carry, one goal.", FreshStartWalk);
            T(BoardId, "Monthly goals board", "Up to 5 goals a month, checked at the month's end.", GoalsBoard);
            T(ReverseId, "Reverse planner", "Works back from a due date into steps with dates.", Reverse);
            T(SemId, "Semester goals check-in", "Once a month, check how your time capsule goals are going.", SemesterCheck);

            w.Refreshed += WatchLadders;
            GameEvents.WeekEnded += monday => PayQuest(monday);
            PlanKit.Morning(MondayId, (s, now) =>
            {
                var m = s.World.GetKept<PreviewMark>(MondayId);
                string week = s.Key(Engine.WeekStart(now));
                if (m.Week == week || now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return null;
                s.World.SetKept(MondayId, new PreviewMark { Week = week });
                return Preview(s, now, pep: true);
            });
            PlanKit.Morning(SundayId, (s, now) => now.DayOfWeek == DayOfWeek.Sunday ? "It is Sunday: the Sunday reset in the Plan drawer looks back, then ahead." : null);
            PlanKit.Morning(BoardId, (s, now) =>
            {
                if (now.Day != 1) return null;
                bool last = LastMonthOpen(s, now).Count > 0;
                return "A new month." + (last ? " Last month's goals are waiting to be checked on the goals board." : "") + " Monthly fresh start is in the Plan drawer when you want it.";
            });
            PlanKit.Morning(SemId, (s, now) => now.Day == 1 && NotesTools.CapsuleGoals(s).Count > 0 ? "Your monthly check-in on your semester goals is ready too." : null);
        }

        private static string MonthKey(DateTime d) => d.ToString("yyyy-MM");

        // ---- Goal ladders ----

        /// <summary>The ladder step for rung i of n, spread over the whole tone ladder.</summary>
        public static int Step(int i, int n) => n <= 1 ? PlanKit.Ladder.Length - 1 : i * (PlanKit.Ladder.Length - 1) / (n - 1);

        private void WatchLadders()
        {
            if (!Tools.IsOn(LadderId)) return;
            var ladders = S.World.Get<Ladders>(LadderId).Tasks;
            if (ladders.Count == 0) return;
            var seen = S.World.GetKept<LadderSeen>(LadderId);
            bool changed = false;
            foreach (var id in ladders)
            {
                if (S.Data.Tasks.FirstOrDefault(t => t.Id == id) is not TaskItem t || t.Subtasks.Count == 0) continue;
                int done = t.Subtasks.Count(x => x.Done);
                seen.Done.TryGetValue(id, out int was);
                if (done == was) continue;
                if (done > was && !Calm.FunPaused) PlanKit.PlayLater(PlanKit.RungId(Step(done - 1, t.Subtasks.Count)), 300);
                seen.Done[id] = done;
                changed = true;
            }
            if (changed) { S.World.SetKept(LadderId, seen); S.World.Save(); }
        }

        private void Ladder()
        {
            var now = DateTime.Now;
            var pool = PlanKit.OpenTasks(S, now).OrderBy(t => t.Type == TaskType.PersonalGoal ? 0 : 1).ToList();
            var t = PlanKit.PickTask(_w, "Goal ladders", "_Goal", pool);
            if (t == null) return;
            if (t.Subtasks.Count == 0)
            {
                var rungs = PlanKit.AskText(_w, "Goal ladders", "_Rungs, one per line, lowest first", "", "Each rung is a small step toward " + t.Name + ". Each one you climb plays a higher tone.", lines: true);
                if (rungs == null) return;
                var names = rungs.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (names.Count == 0) return;
                S.Remember("making a goal ladder for " + t.Name);
                t.Subtasks.AddRange(names.Select(n => new Subtask { Name = n }));
                var l = S.World.Get<Ladders>(LadderId); l.Tasks.Add(t.Id); S.World.Set(LadderId, l);
                PlanKit.SaveAll(S);
                _w.SaveAndRefresh();
                _w.Say(PlanKit.Plural(names.Count, "rung", "rungs") + " on the ladder for " + t.Name + ". Open Goal ladders again to climb one.", true);
                return;
            }
            var lad = S.World.Get<Ladders>(LadderId);
            if (lad.Tasks.Add(t.Id)) { S.World.Set(LadderId, lad); S.World.Save(); }
            var lines = t.Subtasks.Select((x, i) => "Rung " + (i + 1) + ": " + x.Name + (x.Done ? ", climbed" : "")).ToList();
            int next = Math.Max(0, t.Subtasks.FindIndex(x => !x.Done));
            int pick = PlanKit.Pick(_w, "Goal ladder: " + t.Name, "_Rung to climb", lines, next, t.Subtasks.Count(x => x.Done) + " of " + t.Subtasks.Count + " climbed. Enter climbs the rung you are on.");
            if (pick < 0) return;
            var rung = t.Subtasks[pick];
            if (rung.Done) { _w.Say("Rung " + (pick + 1) + " is already climbed. Control Z undoes a climb by mistake.", true); return; }
            S.Remember("climbing a rung of " + t.Name);
            rung.Done = true;
            var seen = S.World.GetKept<LadderSeen>(LadderId); seen.Done[t.Id] = t.Subtasks.Count(x => x.Done); S.World.SetKept(LadderId, seen);
            PlanKit.SaveAll(S);
            _w.SaveAndRefresh();
            SoundBank.Play(PlanKit.RungId(Step(seen.Done[t.Id] - 1, t.Subtasks.Count)));
            int left = t.Subtasks.Count(x => !x.Done);
            _w.Say("Climbed: " + rung.Name + ". " + (left == 0 ? "Top of the ladder. Mark " + t.Name + " done when you are ready." : PlanKit.Plural(left, "rung", "rungs") + " to go. " + Content.Pick(S, "plan.ladder")), true);
        }

        // ---- One goal this week ----

        public static bool QuestMet(Store s, WeekQuest q) =>
            q.TaskId is Guid id ? s.Data.Tasks.FirstOrDefault(t => t.Id == id) is TaskItem t && PlanKit.Finished(t) : q.Done;

        /// <summary>Pays the week's prize once, if the goal was met. Returns the points, or 0.</summary>
        public static int QuestPrizeFor(Store s, DateTime monday)
        {
            var q = s.World.Get<WeekQuest>(QuestId);
            if (q.Week != s.Key(monday) || q.Paid || !QuestMet(s, q)) return 0;
            s.Remember("this week's goal prize");
            q.Paid = true;
            s.World.Set(QuestId, q);
            int pts = Ledger.Boost(s, QuestPrize);
            Ledger.Pay(s, pts, "Weekly goal met: " + q.Text, QuestId);
            PlanKit.SaveAll(s);
            return pts;
        }

        private void PayQuest(DateTime monday)
        {
            if (!Tools.IsOn(QuestId)) return;
            int pts = QuestPrizeFor(S, monday);
            if (pts > 0) Calm.SayUnprompted(_w, pts + " points: " + Content.Pick(S, "plan.quest") + " " + PlayTime.SpendLine(S.Data) + ".");
        }

        private void Quest() => QuestFor(Engine.WeekStart(DateTime.Now));

        private void QuestFor(DateTime monday)
        {
            var now = DateTime.Now;
            string week = S.Key(monday);
            var q = S.World.Get<WeekQuest>(QuestId);
            bool thisOne = q.Week == week && q.Text.Length > 0;
            string label = monday > now ? "next week" : "this week";
            if (thisOne)
            {
                bool met = QuestMet(S, q);
                var options = new List<string> { "Keep it", "Pick a different goal" };
                if (!met && q.TaskId == null) options.Insert(1, "I did it");
                int c = PlanKit.Pick(_w, "One goal " + label, "Your goal " + label + ": " + q.Text + (met ? ", done" : "") + ". _What now?", options);
                if (c < 0) return;
                if (options[c] == "Keep it") { _w.Say("Kept: " + q.Text + "." + (met ? " Done, and the prize comes at the week's end." : ""), true); return; }
                if (options[c] == "I did it")
                {
                    PlanKit.Change<WeekQuest>(S, QuestId, "this week's goal done", x => x.Done = true);
                    _w.Say("Done: " + q.Text + ". The prize, " + QuestPrize + " points, comes at the week's end.", true);
                    return;
                }
            }
            var pool = PlanKit.OpenTasks(S, now);
            var choices = new List<string> { "Type it in my own words" }.Concat(pool.Select(t => S.Line(t, now, true))).ToList();
            int i = PlanKit.Pick(_w, "One goal " + label, "One meaningful _goal for " + label, choices, 0, "One goal only. Meet it and the week's end pays " + QuestPrize + " points.");
            if (i < 0) return;
            string text; Guid? id = null;
            if (i == 0) { var typed = PlanKit.AskText(_w, "One goal " + label, "_Your goal"); if (typed == null) return; text = typed; }
            else { text = pool[i - 1].Name; id = pool[i - 1].Id; }
            PlanKit.Change<WeekQuest>(S, QuestId, "the goal for " + label, x => { x.Week = week; x.Text = text; x.TaskId = id; x.Done = false; x.Paid = false; });
            _w.Say("Your goal " + label + ": " + text + ".", true);
        }

        // ---- Weekend freedom meter ----

        /// <summary>The share of the weekend still free (0 to 100) and the hours of work left this week.</summary>
        public static (int Free, double Left) Weekend(Store s, DateTime now)
        {
            var sunday = Store.WeekEnd(now);
            var open = s.Data.Tasks.Where(t => !PlanKit.Finished(t) && t.Status != Status.Waiting && !t.IsMeeting && !s.ListOf(t).Archived &&
                ((t.DoDate ?? t.Due) is DateTime d ? d.Date <= sunday : t.Status is Status.Today or Status.ThisWeek)).ToList();
            double left = open.Sum(t => Words.Hours(t.Size));
            double perDay = Math.Max(0.5, s.Data.HoursAvailable);
            double weekdays = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday ? 0 : (DayOfWeek.Friday - now.DayOfWeek) + 0.5;
            double weekendDays = now.DayOfWeek == DayOfWeek.Sunday ? 1 : 2;
            double spill = Math.Max(0, left - weekdays * perDay);
            int free = (int)Math.Round(Math.Clamp(100 - spill / (weekendDays * perDay) * 100, 0, 100));
            return (free, left);
        }

        public static string WeekendLine(Store s, DateTime now)
        {
            var (free, left) = Weekend(s, now);
            if (left == 0) return "Your whole weekend is free. Nothing is left for this week.";
            string hours = left == 1 ? "1 hour" : Math.Round(left, 1).ToString() + " hours";
            if (free >= 100) return "Your whole weekend is free right now, with about " + hours + " of work left this week.";
            return "About " + free + " percent of your weekend is free, with about " + hours + " of work left this week. Every task done by Friday frees more.";
        }

        // ---- Monday preview and Sunday reset ----

        public static string Preview(Store s, DateTime now, bool pep)
        {
            var start = now.DayOfWeek == DayOfWeek.Sunday ? now.Date.AddDays(1) : now.Date;
            var end = Store.WeekEnd(start);
            var week = s.Data.Tasks.Where(t => !PlanKit.Finished(t) && !s.ListOf(t).Archived && ((t.DoDate ?? t.Due) is DateTime d && d.Date >= start && d.Date <= end)).ToList();
            var due = week.Where(t => t.Due is DateTime d && d.Date >= start && d.Date <= end).OrderBy(t => t.Due).ToList();
            var tests = due.Where(AutoKind.IsTest).ToList();
            string line = week.Count == 0 ? "The week ahead looks open: nothing planned or due yet." :
                (start == now.Date ? "This week: " : "Next week: ") + PlanKit.Plural(week.Count, "task", "tasks") + " planned or due, " + due.Count + " due" +
                (tests.Count > 0 ? ", " + string.Join(", ", tests.Take(2).Select(t => t.Name + " " + Words.Day(t.Due!.Value, now))) : "") + ".";
            return pep && s.World != null ? line + " " + Content.Pick(s, "plan.monday") : line;
        }

        private void SundayReset()
        {
            var now = DateTime.Now;
            var monday = Engine.WeekStart(now);
            var done = S.Data.Tasks.Where(t => t.Status == Status.Done && t.DoneAt is DateTime d && d >= monday).OrderByDescending(t => Words.Points(t.Size)).ToList();
            var lines = new List<string>
            {
                "Looking back: " + PlanKit.Plural(done.Count, "task", "tasks") + " finished this week, " + Engine.PointsInWeek(S.Data, now) + " points.",
                done.Count > 0 ? "Biggest wins: " + string.Join(", ", done.Take(3).Select(t => t.Name)) + "." : "A quiet week. Rest counts too.",
                "Looking ahead: " + Preview(S, now.DayOfWeek == DayOfWeek.Sunday ? now : monday.AddDays(6), pep: false),
            };
            var next = monday.AddDays(7);
            var q = S.World.Get<WeekQuest>(QuestId);
            lines.Add(q.Week == S.Key(next) ? "Next week's goal: " + q.Text + "." : "Next week has no goal yet.");
            int c = PlanKit.Pick(_w, "Sunday reset", "_What now?", new[] { "Read the review", "Pick next week's goal", "Close" }, 0, string.Join(" ", lines));
            if (c == 0) PlanKit.Show(_w, "Sunday reset", lines);
            if (c == 1) QuestFor(next);
        }

        // ---- Monthly fresh start and goals board ----

        private void FreshStartWalk()
        {
            var now = DateTime.Now;
            var stale = S.Data.Tasks.Where(t => !PlanKit.Finished(t) && t.Status is Status.Today or Status.ThisWeek && !S.ListOf(t).Archived && t.TouchedAt <= now.AddDays(-21)).OrderBy(t => t.TouchedAt).Take(40).ToList();
            List<int>? drop = new();
            if (stale.Count > 0)
            {
                drop = PlanKit.PickMany(_w, "Monthly fresh start, 1 of 3", "Tasks to _let go of for now", stale.Select(t => S.Line(t, now, true)).ToList(), Array.Empty<int>(),
                    "These have sat for three weeks. Picked ones move to Next week with no do date. Nothing is deleted.");
                if (drop == null) return;
            }
            var carry = PlanKit.AskText(_w, "Monthly fresh start, 2 of 3", "What to _carry into this month", S.World.Get<FreshStart>(FreshId).Month == MonthKey(now) ? S.World.Get<FreshStart>(FreshId).Carry : "",
                "A habit, a feeling, a lesson. Optional.", allowEmpty: true);
            if (carry == null) return;
            var goal = PlanKit.AskText(_w, "Monthly fresh start, 3 of 3", "_One goal for this month", "", "It goes on your monthly goals board. Optional.", allowEmpty: true);
            if (goal == null) return;
            S.Remember("the monthly fresh start");
            foreach (int i in drop) { var t = stale[i]; t.Status = Status.NextWeek; t.DoDate = null; t.StatusSince = now; t.TouchedAt = now; }
            S.World.Set(FreshId, new FreshStart { Month = MonthKey(now), Carry = carry });
            bool added = goal.Length > 0 && AddGoal(S, now, goal);
            PlanKit.SaveAll(S);
            _w.SaveAndRefresh();
            _w.Say("Fresh start done." + (drop.Count > 0 ? " " + PlanKit.Plural(drop.Count, "task", "tasks") + " moved to Next week." : "") + (carry.Length > 0 ? " Carrying: " + carry + "." : "") +
                (added ? " Goal on the board: " + goal + "." : goal.Length > 0 ? " The board is full for this month." : "") + " Control Z undoes it.", true);
        }

        /// <summary>Adds a goal to this month's board (no Remember). False when the board already has 5.</summary>
        public static bool AddGoal(Store s, DateTime now, string text)
        {
            var b = s.World.Get<Board>(BoardId);
            var list = b.Months.TryGetValue(MonthKey(now), out var l) ? l : b.Months[MonthKey(now)] = new();
            if (list.Count >= MaxGoals) return false;
            list.Add(new MonthGoal { Text = text });
            s.World.Set(BoardId, b);
            return true;
        }

        public static List<MonthGoal> LastMonthOpen(Store s, DateTime now) =>
            s.World.Get<Board>(BoardId).Months.TryGetValue(MonthKey(now.AddMonths(-1)), out var l) ? l.Where(g => !g.Checked && !g.Met).ToList() : new();

        /// <summary>Marks goals met (by index in the month) and pays 5 each, boosted. No Remember. Returns the points.</summary>
        public static int MeetGoals(Store s, string month, IEnumerable<int> met, bool closeMonth)
        {
            var b = s.World.Get<Board>(BoardId);
            if (!b.Months.TryGetValue(month, out var list)) return 0;
            int pts = 0;
            var picked = met.ToHashSet();
            for (int i = 0; i < list.Count; i++)
            {
                if (picked.Contains(i) && !list[i].Met)
                {
                    list[i].Met = true;
                    int p = Ledger.Boost(s, GoalPoints);
                    Ledger.Pay(s, p, "Monthly goal met: " + list[i].Text, BoardId);
                    pts += p;
                }
                if (closeMonth) list[i].Checked = true;
            }
            s.World.Set(BoardId, b);
            return pts;
        }

        private void GoalsBoard()
        {
            var now = DateTime.Now;
            string month = MonthKey(now), last = MonthKey(now.AddMonths(-1));
            var b = S.World.Get<Board>(BoardId);
            var lastOpen = b.Months.TryGetValue(last, out var lo) ? lo : new();
            if (lastOpen.Any(g => !g.Checked && !g.Met))
            {
                var chosen = PlanKit.PickMany(_w, "Last month's goals", "Goals you _met last month", lastOpen.Select(g => g.Text + (g.Met ? ", met" : "")).ToList(),
                    Enumerable.Range(0, lastOpen.Count).Where(i => lastOpen[i].Met), "Pick the ones you met. Each pays " + GoalPoints + " points. The rest just close, no harm done.");
                if (chosen == null) return;
                S.Remember("checking last month's goals");
                int pts = MeetGoals(S, last, chosen, closeMonth: true);
                PlanKit.SaveAll(S);
                _w.Say("Last month: " + lastOpen.Count(g => g.Met) + " of " + lastOpen.Count + " goals met." + (pts > 0 ? " " + pts + " points. " + PlayTime.SpendLine(S.Data) + "." : ""), true);
                return;
            }
            var goals = b.Months.TryGetValue(month, out var gl) ? gl : new();
            var options = goals.Select(g => (g.Met ? "Met: " : "Mark met: ") + g.Text).ToList();
            if (goals.Count < MaxGoals) options.Add("Add a goal");
            int c = PlanKit.Pick(_w, "Monthly goals board", "_Goals for " + now.ToString("MMMM"), options, 0, goals.Count == 0 ? "No goals yet this month." : goals.Count(g => g.Met) + " of " + goals.Count + " met so far.");
            if (c < 0) return;
            if (c >= goals.Count)
            {
                var text = PlanKit.AskText(_w, "Monthly goals board", "A _goal for " + now.ToString("MMMM"));
                if (text == null) return;
                S.Remember("a monthly goal");
                AddGoal(S, now, text);
                PlanKit.SaveAll(S);
                _w.Say("On the board: " + text + ". " + (goals.Count + 1) + " of " + MaxGoals + ".", true);
                return;
            }
            if (goals[c].Met) { _w.Say(goals[c].Text + " is already met. Control Z undoes it if that was a mistake.", true); return; }
            S.Remember("meeting a monthly goal");
            int got = MeetGoals(S, month, new[] { c }, closeMonth: false);
            PlanKit.SaveAll(S);
            _w.Say("Met: " + goals[c].Text + ". " + got + " points. " + PlayTime.SpendLine(S.Data) + ".", true);
        }

        // ---- Reverse planner ----

        /// <summary>Do dates for n steps between today and the due date: spread out, the last one before the due day.</summary>
        public static List<DateTime> StepDates(DateTime today, DateTime due, int n)
        {
            int days = Math.Max(1, (due.Date - today.Date).Days);
            return Enumerable.Range(1, n).Select(k => today.Date.AddDays(Math.Min(days - 1, (int)Math.Floor((double)k * days / (n + 1))))).ToList();
        }

        private void Reverse()
        {
            var now = DateTime.Now;
            var pool = PlanKit.OpenTasks(S, now).Where(t => t.Due is DateTime d && d.Date > now.Date.AddDays(1)).ToList();
            var t = PlanKit.PickTask(_w, "Reverse planner", "_Big project", pool, empty: "Nothing open is due more than a day from now. Give a big task a due date first.");
            if (t == null) return;
            var text = PlanKit.AskText(_w, "Reverse planner", "_Steps, one per line, in order", string.Join("\n", Content.Lines("plan.steps").Take(5)),
                "Each step becomes a small task with its own do date, working back from " + Words.Day(t.Due!.Value, now) + ". Change or delete any line.", lines: true);
            if (text == null) return;
            var steps = text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).Take(12).ToList();
            if (steps.Count == 0) return;
            var dates = StepDates(now, t.Due.Value, steps.Count);
            S.Remember("planning back from " + t.Name);
            for (int i = 0; i < steps.Count; i++)
                _w.AddTask(new TaskItem { Name = steps[i] + " (for " + t.Name + ")", List = t.List, Tag = t.Tag, Section = t.Section, Status = Status.ThisWeek, DoDate = dates[i], Size = Size.Small }, announce: false, remember: false);
            _w.SaveAndRefresh();
            _w.Say(PlanKit.Plural(steps.Count, "step", "steps") + " added for " + t.Name + ", the first " + Words.Day(dates[0], now) + ", the last " + Words.Day(dates[^1], now) + ". Control Z undoes it.", true);
        }

        // ---- Semester goals check-in ----

        private void SemesterCheck()
        {
            var goals = NotesTools.CapsuleGoals(S);
            if (goals.Count == 0) { _w.Say("No semester goals yet. Write them in the time capsule, in Notes to self, and check in here each month.", true); return; }
            var options = new[] { "On track", "Some progress", "Not yet, and that is fine" };
            var answers = new List<string>();
            for (int i = 0; i < goals.Count; i++)
            {
                int c = PlanKit.Pick(_w, "Semester goals check-in, " + (i + 1) + " of " + goals.Count, "_How is it going: " + goals[i], options);
                if (c < 0) return;
                answers.Add(goals[i] + ": " + options[c]);
            }
            string month = MonthKey(DateTime.Now);
            PlanKit.Change<SemCheck>(S, SemId, "the semester goals check-in", x => x.Months[month] = answers);
            int onTrack = answers.Count(a => a.EndsWith(options[0]));
            _w.Say("Checked in. " + onTrack + " of " + goals.Count + " on track. Every bit of progress counts.", true);
        }

        // ---- Self-test ----

        public void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("plan long: " + what); }
            var now = DateTime.Now;
            Need(Step(0, 4) == 0 && Step(3, 4) == PlanKit.Ladder.Length - 1 && Step(1, 4) < Step(2, 4), "ladder steps do not climb");

            var d = StepDates(new DateTime(2026, 11, 2), new DateTime(2026, 11, 12), 4);
            Need(d.Count == 4 && d[0] == new DateTime(2026, 11, 4) && d[3] < new DateTime(2026, 11, 12) && d.Zip(d.Skip(1)).All(p => p.First <= p.Second), "reverse planner dates: " + string.Join(",", d));
            Need(StepDates(new DateTime(2026, 11, 2), new DateTime(2026, 11, 3), 3).All(x => x == new DateTime(2026, 11, 2)), "a due date tomorrow puts every step today");

            // Weekend meter: nothing left is a free weekend; a pile of work on a Friday fills some of it.
            var keep = s.Data.Tasks.ToList();
            s.Data.Tasks.Clear();
            var friday = new DateTime(2026, 11, 6, 12, 0, 0);
            Need(Weekend(s, friday) == (100, 0), "an empty week did not leave the weekend free");
            for (int i = 0; i < 6; i++) s.Data.Tasks.Add(new TaskItem { Name = "w" + i, List = s.Data.Lists[0].Name, Status = Status.ThisWeek, Size = Size.Large, Due = friday.Date.AddDays(1) });
            var (free, left) = Weekend(s, friday);
            Need(left == 12 && free < 100 && free >= 0, "weekend meter: " + free + " free, " + left + " left");

            // Weekly goal: paid once, only when met, and undo takes it back.
            var monday = Engine.WeekStart(now);
            s.World.Set(QuestId, new WeekQuest { Week = s.Key(monday), Text = "test goal" });
            Need(QuestPrizeFor(s, monday) == 0, "an unmet weekly goal paid");
            s.World.Set(QuestId, new WeekQuest { Week = s.Key(monday), Text = "test goal", Done = true });
            int before = Engine.Total(s.Data);
            Need(QuestPrizeFor(s, monday) > 0 && QuestPrizeFor(s, monday) == 0, "the weekly goal paid twice");
            s.Undo();
            Need(Engine.Total(s.Data) == before && !s.World.Get<WeekQuest>(QuestId).Paid, "undo left the weekly goal prize");

            // Goals board: at most 5, met pays once.
            s.World.Set(BoardId, new Board());
            for (int i = 0; i < 6; i++) AddGoal(s, now, "goal " + i);
            Need(s.World.Get<Board>(BoardId).Months[MonthKey(now)].Count == MaxGoals, "more than 5 goals on the board");
            Need(MeetGoals(s, MonthKey(now), new[] { 0 }, false) > 0 && MeetGoals(s, MonthKey(now), new[] { 0 }, false) == 0, "a goal paid twice");
            s.World.Set(BoardId, new Board());
            s.Data.Tasks.Clear();
            s.Data.Tasks.AddRange(keep);

            foreach (var topic in new[] { "plan.ladder", "plan.quest", "plan.monday", "plan.steps" }) Need(Content.Lines(topic).Count >= (topic == "plan.steps" ? 5 : 20), topic + " is too short");
            foreach (var id in new[] { LadderId, QuestId, WeekendId, SundayId, MondayId, FreshId, BoardId, ReverseId, SemId }) Need(Tools.Find(id) != null, id + " is not in the toolbox");
        }
    }
}
