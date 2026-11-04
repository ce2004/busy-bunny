using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Plan drawer, the day: promise mode, word of the day, tiny goal, daily three, fill the gap, energy-matched order,
    /// mood match, good enough mode, dread score, pre-flight checklist, tomorrow's first task, pairing a boring task with
    /// something fun, habit stacking and the countdown to something fun.
    /// </summary>
    public sealed class PlanDay : IFeature
    {
        public sealed class Promise { public Guid TaskId { get; set; } public string Name { get; set; } = ""; public DateTime At { get; set; } public bool Nudged { get; set; } }
        public sealed class Promises { public List<Promise> Items { get; set; } = new(); }
        public sealed class WordDay { public string Day { get; set; } = ""; public string Word { get; set; } = ""; }
        public sealed class TinyGoal { public string Day { get; set; } = ""; public string Text { get; set; } = ""; public Guid? TaskId { get; set; } public bool Won { get; set; } public List<string> WonDays { get; set; } = new(); }
        public sealed class DailyThree { public string Day { get; set; } = ""; public List<Guid> Tasks { get; set; } = new(); public bool Won { get; set; } public List<string> WonDays { get; set; } = new(); public Guid? PaidBy { get; set; } }
        public sealed class WordCount { public string Day { get; set; } = ""; public int Finishes { get; set; } }
        public sealed class Nudged { public HashSet<string> Keys { get; set; } = new(); }
        public sealed class TaskTexts { public Dictionary<Guid, string> Of { get; set; } = new(); }
        public sealed class TaskScores { public Dictionary<Guid, int> Of { get; set; } = new(); }
        public sealed class Ticks { public Dictionary<Guid, List<int>> Of { get; set; } = new(); }
        public sealed class FirstTask { public string Day { get; set; } = ""; public Guid TaskId { get; set; } public string Name { get; set; } = ""; }
        public sealed class Habit { public string Routine { get; set; } = ""; public List<Guid> Tasks { get; set; } = new(); }
        public sealed class Habits { public List<Habit> Items { get; set; } = new(); }
        public sealed class FunPlan { public string Name { get; set; } = ""; public DateTime When { get; set; } }
        public sealed class FunPlans { public List<FunPlan> Items { get; set; } = new(); }

        public const string PromiseId = "plan.promise", WordId = "plan.word", TinyId = "plan.tinygoal", ThreeId = "plan.daily3", GapId = "plan.fillgap",
            EnergyId = "plan.energyorder", MoodId = "plan.mood", EnoughId = "plan.goodenough", DreadId = "plan.dread", FlightId = "plan.preflight",
            FirstId = "plan.firsttask", PairId = "plan.pair", HabitId = "plan.habits", FunId = "plan.funcount";

        public static readonly string[] FlightItems = { "Rubric checked", "File name is right", "Format is right", "Submitted" };
        public static readonly string[] Moods = { "Tired", "Anxious", "Focused", "Restless", "Low", "Cheerful", "Bored", "Overwhelmed" };

        private MainWindow _w = null!;
        private Store S => _w.Store;

        public void Attach(MainWindow w)
        {
            _w = w;
            void T(string id, string name, string what, Action run) => Tools.Register(new BunnyTool(id, name, Drawers.Plan, what, run) { Since = "0.14.0" });
            T(PromiseId, "Promise mode", "Say when you will start a task; one nudge at that time, never a second.", MakePromise);
            T(WordId, "Word of the day", "Pick a word for today; Bunny's lines lean that way.", PickWord);
            T(TinyId, "Tiny goal, big win", "Name the one thing that would make today a win.", SetTinyGoal);
            T(ThreeId, "Daily three", "Pick three tasks for today; all three done is a win.", SetDailyThree);
            T(GapId, "Fill the gap", "Finds the free time before your next class or meeting and a task that fits.", FillGap);
            T(EnergyId, "Energy-matched order", "Sorts Today by your check-in energy and each task's energy tag.", EnergyOrder);
            T(MoodId, "Mood match", "Pick your mood and get tasks that fit it.", MoodMatch);
            T(EnoughId, "Good enough mode", "Set a good enough bar for a task; points pay when the bar is met.", GoodEnough);
            T(DreadId, "Dread score", "Rate how much you dread a task, 1 to 5; dreaded ones pay more.", RateDread);
            T(FlightId, "Pre-flight checklist", "Before an assignment is done: rubric, file name, format, submitted. Each ticked once.", PreFlight);
            T(FirstId, "Pick tomorrow's first task", "Choose it tonight; you hear it in the morning.", PickFirst);
            T(PairId, "Pair a boring task with something fun", "Link a task with a reward; finishing it reminds you to take it.", Pair);
            T(HabitId, "Habit stacking", "Attach a task to a routine, offered when you do the routine.", HabitStack);
            T(FunId, "Countdown to something fun", "Add a fun plan with a date; the summary line counts down to it.", FunCountdown);

            w.DetailNotes.Add(t =>
            {
                var parts = new List<string>();
                if (Tools.IsOn(EnoughId) && S.World.Get<TaskTexts>(EnoughId).Of.TryGetValue(t.Id, out var bar)) parts.Add("Good enough: " + bar + ".");
                if (Tools.IsOn(DreadId) && DreadOf(S, t.Id) is int d and > 0) parts.Add("Dread " + d + " of 5.");
                if (Tools.IsOn(FlightId) && S.World.Get<Ticks>(FlightId).Of.TryGetValue(t.Id, out var ticks)) parts.Add("Pre-flight: " + ticks.Count + " of " + FlightItems.Length + " checked.");
                if (Tools.IsOn(PairId) && S.World.Get<TaskTexts>(PairId).Of.TryGetValue(t.Id, out var reward)) parts.Add("Reward: " + reward + ".");
                return parts.Count == 0 ? null : string.Join(" ", parts);
            });
            w.Store.ReorderRules.Add((tasks, now) => Tools.IsOn(EnergyId) ? EnergySort(tasks, EnergyToday(S, w.AppSettings, now)) : tasks);
            w.Store.SummaryParts.Add((now, shortMode) => !shortMode && Tools.IsOn(FunId) ? FunLine(S, now) : null);

            Ticker.Every(PromiseId, now => Tools.IsOn(PromiseId) && S.World.Get<Promises>(PromiseId).Items.Any(p => !WasNudged(S, p) && p.At <= now), Nudge);
            PlanKit.Morning(FirstId, (s, now) =>
            {
                var f = s.World.Get<FirstTask>(FirstId);
                if (f.Day != s.Key(now) || s.Data.Tasks.FirstOrDefault(x => x.Id == f.TaskId) is not TaskItem t || PlanKit.Finished(t)) return null;
                return "First up, as you picked last night: " + t.Name + ".";
            });
            PlanKit.Morning(FunId, (s, now) => FunLine(s, now) is string l ? char.ToUpper(l[0]) + l[1..] + "." : null);

            GameEvents.TaskDone += OnDone;
            GameEvents.TaskUndone += OnUndone;
        }

        // ---- After a finished task ----

        private void OnDone(TaskItem t)
        {
            var s = S;
            var now = DateTime.Now;
            if (Tools.IsOn(TinyId) && TinyDone(s, t, now))
            {
                // A fixed prize, not a multiple of the task's points: plain Pay (it still carries the id, so un-finishing takes it back).
                if (Ledger.Pay(s, Ledger.Boost(s, 10), "Tiny goal, big win: " + t.Name, TinyId, t.Id) is Outcome o) Game.Queue(_w, o);
                s.Save();
                if (!Calm.FunPaused) PlanKit.PlayLater("PersonalRecord");
                Calm.AfterTaskDone(_w, Game.Goals, Content.Pick(s, "plan.tinygoal"));
                if (Tools.IsOn(AutoKind.TreatId)) AutoKind.LeaveTreat(s, now);
            }
            if (Tools.IsOn(ThreeId) && ThreeDone(s, t, now))
            {
                // The prize carries the id of the task that finished the three (ThreeDone keeps it as PaidBy).
                if (Ledger.Pay(s, Ledger.Boost(s, 10), "Daily three, all done", ThreeId, t.Id) is Outcome o) Game.Queue(_w, o);
                s.Save();
                Calm.AfterTaskDone(_w, Game.Goals, Content.Pick(s, "plan.daily3"));
            }
            if (Tools.IsOn(EnoughId) && s.World.Get<TaskTexts>(EnoughId).Of.ContainsKey(t.Id))
            {
                if (Ledger.PayExtra(s, t, Ledger.BaseOf(s, t), 5, "Good enough is done: " + t.Name, EnoughId) is Outcome o) Game.Queue(_w, o);
                s.Save();
                Calm.AfterTaskDone(_w, Game.Other, Content.Pick(s, "plan.goodenough"));
            }
            if (Tools.IsOn(DreadId) && DreadBonus(DreadOf(s, t.Id)) is int bonus and > 0)
            {
                if (Ledger.PayExtra(s, t, Ledger.BaseOf(s, t), bonus, "Did a dreaded one: " + t.Name, DreadId) is Outcome o) Game.Queue(_w, o);
                s.Save();
            }
            if (Tools.IsOn(PairId) && s.World.Get<TaskTexts>(PairId).Of.TryGetValue(t.Id, out var reward))
                Calm.AfterTaskDone(_w, Game.Other, PlanKit.Line(s, "plan.pair", ("reward", reward)));
            if (Tools.IsOn(HabitId))
            {
                var stacked = s.World.Get<Habits>(HabitId).Items.Where(h => SameRoutine(h.Routine, t.Name))
                    .SelectMany(h => h.Tasks).Select(id => s.Data.Tasks.FirstOrDefault(x => x.Id == id)).OfType<TaskItem>().Where(x => !PlanKit.Finished(x)).ToList();
                if (stacked.Count > 0) Calm.AfterTaskDone(_w, Game.Other, "Stacked on that: " + string.Join(", ", stacked.Select(x => x.Name)) + ".");
            }
            if (Tools.IsOn(WordId) && WordToday(s, now) is string word && !Calm.FunPaused && WordTurn(s, now))
                Calm.AfterTaskDone(_w, Game.Chatter, PlanKit.Line(s, "plan.word", ("word", word)));
        }

        private void OnUndone(TaskItem t)
        {
            var now = DateTime.Now;
            if (TinyUndone(S, t, now)) AutoKind.TakeTreat(S, now);
            ThreeUndone(S, t, now);
        }

        // ---- Logic, static for the self-test ----

        public static bool TinyDone(Store s, TaskItem t, DateTime now)
        {
            var g = s.World.Get<TinyGoal>(TinyId);
            if (g.Day != s.Key(now) || g.TaskId != t.Id || g.Won) return false;
            g.Won = true;
            if (!g.WonDays.Contains(g.Day)) g.WonDays.Add(g.Day);
            s.World.Set(TinyId, g);
            s.World.Save();
            return true;
        }

        public static bool TinyUndone(Store s, TaskItem t, DateTime now)
        {
            var g = s.World.Get<TinyGoal>(TinyId);
            if (g.Day != s.Key(now) || g.TaskId != t.Id || !g.Won) return false;
            g.Won = false;
            g.WonDays.Remove(g.Day);
            s.World.Set(TinyId, g);
            s.World.Save();
            return true;
        }

        public static bool ThreeDone(Store s, TaskItem t, DateTime now)
        {
            var d = s.World.Get<DailyThree>(ThreeId);
            if (d.Day != s.Key(now) || d.Won || !d.Tasks.Contains(t.Id)) return false;
            var tasks = d.Tasks.Select(id => s.Data.Tasks.FirstOrDefault(x => x.Id == id)).OfType<TaskItem>().ToList();
            if (tasks.Count == 0 || !tasks.All(PlanKit.Finished)) return false;
            d.Won = true;
            d.PaidBy = t.Id;
            if (!d.WonDays.Contains(d.Day)) d.WonDays.Add(d.Day);
            s.World.Set(ThreeId, d);
            s.World.Save();
            return true;
        }

        /// <summary>
        /// One of the three went back: the win is off, and so is its prize. The prize carries the id of the task that finished
        /// the three, so when another of the three goes back, that entry is taken out here (the paying task's own undo takes it by itself).
        /// </summary>
        public static void ThreeUndone(Store s, TaskItem t, DateTime now)
        {
            var d = s.World.Get<DailyThree>(ThreeId);
            if (d.Day != s.Key(now) || !d.Won || !d.Tasks.Contains(t.Id)) return;
            if (d.PaidBy is Guid by && by != t.Id)
                s.Data.Log.RemoveAll(e => e.Kind == Ledger.FunKind && e.Feature == ThreeId && e.TaskId == by && e.When.Date == now.Date);
            d.Won = false;
            d.PaidBy = null;
            d.WonDays.Remove(d.Day);
            s.World.Set(ThreeId, d);
            s.World.Save();
        }

        /// <summary>Whether this finish gets a word of the day line: the first finish of the day, then every third one.</summary>
        public static bool WordTurn(Store s, DateTime now)
        {
            var c = s.World.GetKept<WordCount>(WordId);
            if (c.Day != s.Key(now)) c = new WordCount { Day = s.Key(now) };
            c.Finishes++;
            s.World.SetKept(WordId, c);
            s.World.Save();
            return c.Finishes % 3 == 1;
        }

        public static int? DreadOf(Store s, Guid id) => s.World.Get<TaskScores>(DreadId).Of.TryGetValue(id, out int d) ? d : null;
        /// <summary>Extra points for a dreaded task: 5 for a 4, 10 for a 5, none below.</summary>
        public static int DreadBonus(int? dread) => dread switch { >= 5 => 10, 4 => 5, _ => 0 };

        public static string? WordToday(Store s, DateTime now)
        {
            var w = s.World.Get<WordDay>(WordId);
            return w.Day == s.Key(now) && w.Word.Length > 0 ? w.Word : null;
        }

        private static bool SameRoutine(string routine, string taskName) =>
            routine.Length > 2 && taskName.Trim().Equals(routine.Trim(), StringComparison.OrdinalIgnoreCase);

        /// <summary>Today's energy: low-energy mode counts as low; otherwise the morning check-in, or empty.</summary>
        public static string EnergyToday(Store s, Settings? cfg, DateTime now) =>
            cfg?.LowEnergy == true ? "low" : s.Data.PeekDay(now).Energy.ToLowerInvariant();

        /// <summary>Low energy: easy tasks first, brain-heavy last. High: brain-heavy first. Otherwise as it was.</summary>
        public static List<TaskItem> EnergySort(List<TaskItem> tasks, string energy)
        {
            if (energy == "low") return tasks.OrderBy(t => t.Energy == Energy.Easy ? 0 : t.Energy == Energy.BrainHeavy ? 2 : 1).ToList();
            if (energy == "high") return tasks.OrderBy(t => t.Energy == Energy.BrainHeavy ? 0 : t.Energy == Energy.Easy ? 2 : 1).ToList();
            return tasks;
        }

        /// <summary>The free minutes before the next class or timed meeting today, and what it is. Null when nothing else is on today.</summary>
        public static (int Minutes, string What)? FindGap(Store s, DateTime now)
        {
            var next = new List<(DateTime At, string What)>();
            foreach (var c in s.Data.Classes.Where(c => c.Days.Contains(now.DayOfWeek) && c.Start > now.TimeOfDay))
                next.Add((now.Date + c.Start, (c.Tag.Length > 0 ? c.Tag : "your") + " class at " + Words.Time(now.Date + c.Start)));
            foreach (var t in s.Data.Tasks.Where(t => t.IsMeeting && t.DueHasTime && t.Due is DateTime d && d.Date == now.Date && d > now && !PlanKit.Finished(t) && !s.ListOf(t).Archived))
                next.Add((t.Due!.Value, t.Name + " at " + Words.Time(t.Due.Value)));
            if (next.Count == 0) return null;
            var first = next.OrderBy(n => n.At).First();
            return ((int)(first.At - now).TotalMinutes, first.What);
        }

        /// <summary>The first task in the pool that fits in the minutes, by its size's time estimate.</summary>
        public static TaskItem? Fit(IEnumerable<TaskItem> pool, int minutes) =>
            pool.FirstOrDefault(t => !t.IsMeeting && Words.Hours(t.Size) * 60 <= minutes);

        /// <summary>How well a task suits a mood: higher is better.</summary>
        public static int MoodScore(TaskItem t, string mood)
        {
            bool small = t.Size == Size.Small || t.QuickWin, easy = t.Energy == Energy.Easy, heavy = t.Energy == Energy.BrainHeavy || t.Size is Size.Large or Size.MultiDay;
            return mood switch
            {
                "Tired" or "Low" or "Overwhelmed" => (small ? 2 : 0) + (easy ? 2 : 0) - (heavy ? 3 : 0),
                "Anxious" => (small ? 3 : 0) + (t.Status == Status.Today ? 1 : 0) - (heavy ? 1 : 0),
                "Focused" => (heavy ? 3 : 0) + (t.Priority == 1 ? 2 : 0),
                "Cheerful" => (t.Priority == 1 ? 2 : 0) + (t.Size == Size.Medium ? 1 : 0),
                "Restless" or "Bored" => (small ? 2 : 0) + (t.Energy == Energy.Unset ? 0 : 1),
                _ => 0,
            };
        }

        public static string? FunLine(Store s, DateTime now)
        {
            var f = s.World.Get<FunPlans>(FunId).Items.Where(p => p.When.Date >= now.Date && p.When.Date <= now.Date.AddDays(60)).OrderBy(p => p.When).FirstOrDefault();
            if (f == null) return null;
            int days = (f.When.Date - now.Date).Days;
            return f.Name + (days == 0 ? " today" : days == 1 ? " tomorrow" : " in " + days + " days");
        }

        // ---- The tools ----

        private void MakePromise()
        {
            var t = PlanKit.PickTask(_w, "Promise mode", "_Task you will start", PlanKit.OpenTasks(S, DateTime.Now));
            if (t == null) return;
            var when = PlanKit.AskText(_w, "Promise mode", "_When will you start it?", "in 30 minutes", "Like 3 pm, in 20 minutes, or tomorrow at 10. I nudge you once at that time, never twice.");
            if (when == null) return;
            var at = PlayTime.ByWhen(when, DateTime.Now);
            if (at == null || at <= DateTime.Now) { _w.Say("I could not read that time. Try 3 pm or in 20 minutes.", true); return; }
            PlanKit.Change<Promises>(S, PromiseId, "a promise to start " + t.Name, p =>
            {
                p.Items.RemoveAll(x => WasNudged(S, x) || x.TaskId == t.Id || x.At < DateTime.Now.AddDays(-2));
                p.Items.Add(new Promise { TaskId = t.Id, Name = t.Name, At = at.Value });
            });
            _w.Say("Promise noted: one nudge at " + Words.Time(at.Value) + (at.Value.Date == DateTime.Today ? "" : " " + Words.Day(at.Value, DateTime.Now)) + " to start " + t.Name + ".", true);
        }

        private const string NudgedId = "plan.promise.nudged";
        private static string NudgeKey(Promise p) => p.TaskId.ToString("N") + " " + p.At.ToString("o");

        /// <summary>Whether a promise was nudged. Kept, so a Control Z of something else never brings a nudge back.</summary>
        public static bool WasNudged(Store s, Promise p) => p.Nudged || s.World.GetKept<Nudged>(NudgedId).Keys.Contains(NudgeKey(p));

        /// <summary>Marks due promises as nudged first, so a promise is never nudged twice, then says one line for each that still matters.</summary>
        public static List<Promise> DuePromises(Store s, DateTime now)
        {
            var p = s.World.Get<Promises>(PromiseId);
            var due = p.Items.Where(x => !WasNudged(s, x) && x.At <= now).ToList();
            if (due.Count == 0) return due;
            var kept = s.World.GetKept<Nudged>(NudgedId);
            // Only the last few days' promises need remembering.
            kept.Keys.RemoveWhere(k => DateTime.TryParse(k[(k.IndexOf(' ') + 1)..], null, System.Globalization.DateTimeStyles.RoundtripKind, out var at) && at < now.AddDays(-7));
            foreach (var x in due) { x.Nudged = true; kept.Keys.Add(NudgeKey(x)); }
            s.World.SetKept(NudgedId, kept);
            s.World.Set(PromiseId, p);
            s.World.Save();
            return due.Where(x => s.Data.Tasks.FirstOrDefault(t => t.Id == x.TaskId) is TaskItem t && !PlanKit.Finished(t)).ToList();
        }

        private void Nudge(DateTime now)
        {
            foreach (var p in DuePromises(S, now))
            {
                string line = PlanKit.Line(S, "plan.promise", ("task", p.Name));
                if (Calm.FunPaused) Game.Note(S, line); else Calm.SayUnprompted(_w, line);
            }
        }

        private void PickWord()
        {
            var words = Content.Lines("plan.words");
            var options = words.Concat(new[] { "Type my own word" }).ToList();
            string? now = WordToday(S, DateTime.Now);
            int i = PlanKit.Pick(_w, "Word of the day", "_Today's word", options, now != null ? Math.Max(0, words.IndexOf(now)) : 0, "Pick a word for the tone of today. Bunny's lines lean that way.");
            if (i < 0) return;
            string? word = i < words.Count ? words[i] : PlanKit.AskText(_w, "Word of the day", "_Your word", now ?? "");
            if (string.IsNullOrWhiteSpace(word)) return;
            PlanKit.Change<WordDay>(S, WordId, "the word of the day", w => { w.Day = S.Key(DateTime.Now); w.Word = word.Trim(); });
            _w.Say("Today's word is " + word.Trim() + ". I will lean that way.", true);
        }

        private void SetTinyGoal()
        {
            var now = DateTime.Now;
            var g = S.World.Get<TinyGoal>(TinyId);
            bool today = g.Day == S.Key(now) && (g.Text.Length > 0 || g.TaskId != null);
            if (today && g.Won) { _w.Say("Today's tiny goal is already won: " + g.Text + ". That counts. Rest easy.", true); return; }
            if (today)
            {
                var choices = new List<string> { "Keep it", "Pick a different one" };
                if (g.TaskId == null) choices.Insert(1, "I did it");
                int c = PlanKit.Pick(_w, "Tiny goal, big win", "Today's goal is " + g.Text + ". _What now?", choices);
                if (c <= 0) { if (c == 0) _w.Say("Kept: " + g.Text + ".", true); return; }
                if (choices[c] == "I did it") { WinTextGoal(); return; }
            }
            var pool = PlanKit.OpenTasks(S, now);
            var options = new List<string> { "Type it in my own words" }.Concat(pool.Select(t => S.Line(t, now, true))).ToList();
            int i = PlanKit.Pick(_w, "Tiny goal, big win", "The one _thing that would make today a win", options, pool.Count > 0 ? 1 : 0);
            if (i < 0) return;
            string text; Guid? id = null;
            if (i == 0) { var typed = PlanKit.AskText(_w, "Tiny goal, big win", "_Your tiny goal"); if (typed == null) return; text = typed; }
            else { text = pool[i - 1].Name; id = pool[i - 1].Id; }
            PlanKit.Change<TinyGoal>(S, TinyId, "today's tiny goal", x => { x.Day = S.Key(now); x.Text = text; x.TaskId = id; x.Won = false; });
            _w.Say("Today's tiny goal: " + text + "." + (id == null ? " Open this tool again and choose I did it when it is done." : " Finish it for the big celebration."), true);
        }

        private void WinTextGoal()
        {
            var now = DateTime.Now;
            S.Remember("winning today's tiny goal");
            var g = S.World.Get<TinyGoal>(TinyId);
            g.Won = true;
            if (!g.WonDays.Contains(g.Day)) g.WonDays.Add(g.Day);
            S.World.Set(TinyId, g);
            var o = Ledger.Pay(S, Ledger.Boost(S, 10), "Tiny goal, big win: " + g.Text, TinyId);
            if (Tools.IsOn(AutoKind.TreatId)) AutoKind.LeaveTreat(S, now);
            PlanKit.SaveAll(S);
            SoundBank.Play("PersonalRecord");
            if (o != null) foreach (var l in o.Said) Game.Note(S, l);
            _w.Say((o?.Gained ?? 0) + " points. " + Content.Pick(S, "plan.tinygoal") + " " + PlayTime.SpendLine(S.Data) + ".", true);
        }

        private void SetDailyThree()
        {
            var now = DateTime.Now;
            var d = S.World.Get<DailyThree>(ThreeId);
            bool set = d.Day == S.Key(now) && d.Tasks.Count > 0;
            if (set)
            {
                var picked = d.Tasks.Select(id => S.Data.Tasks.FirstOrDefault(x => x.Id == id)).OfType<TaskItem>().ToList();
                int done = picked.Count(PlanKit.Finished);
                var (streak, _) = PlanKit.GraceStreak(d.WonDays.Select(k => DateTime.TryParse(k, out var x) ? x : DateTime.MinValue).Where(x => x > DateTime.MinValue).ToList(), now, PlanKit.Grace(S));
                string how = done + " of " + picked.Count + " done." + (done < picked.Count ? " Left: " + string.Join(", ", picked.Where(x => !PlanKit.Finished(x)).Select(x => x.Name)) + "." : " That is a win.")
                    + (streak > 1 ? " Daily three streak: " + streak + " days." : "");
                int c = PlanKit.Pick(_w, "Daily three", "_What now?", new[] { "Hear how it is going", "Pick again" }, 0, how);
                if (c == 0) _w.Say(how, true);
                if (c != 1) return;
            }
            var pool = PlanKit.OpenTasks(S, now).Take(60).ToList();
            if (pool.Count == 0) { _w.Say("There are no open tasks to pick from.", true); return; }
            var pre = set ? d.Tasks.Select(id => pool.FindIndex(t => t.Id == id)).Where(i => i >= 0).ToList() : new List<int>();
            var chosen = PlanKit.PickMany(_w, "Daily three", "Pick up to _three tasks for today", pool.Select(t => S.Line(t, now, true)).ToList(), pre, "Finishing all three is a win, even if more tasks exist.", 3);
            if (chosen == null) return;
            if (chosen.Count == 0) { _w.Say("Nothing picked, so no daily three today.", true); return; }
            var ids = chosen.Select(i => pool[i].Id).ToList();
            PlanKit.Change<DailyThree>(S, ThreeId, "today's daily three", x => { x.Day = S.Key(now); x.Tasks = ids; x.Won = false; });
            _w.Say("Your daily three: " + string.Join(", ", chosen.Select(i => pool[i].Name)) + ".", true);
        }

        private void FillGap()
        {
            var now = DateTime.Now;
            var pool = PlanKit.OpenTasks(S, now);
            var gap = FindGap(S, now);
            if (gap is not (int min, string what))
            {
                var any = pool.FirstOrDefault(t => !t.IsMeeting);
                _w.Say("Nothing else is on your calendar today, so any task fits." + (any != null ? " Try " + any.Name + "." : ""), true);
                if (any != null) _w.SelectTask(any);
                return;
            }
            if (min < 10) { _w.Say("Only " + min + " minutes before " + what + ". Take a breath and get ready.", true); return; }
            var fit = Fit(pool, min);
            if (fit == null) { _w.Say(min + " free minutes before " + what + ". Nothing open fits that well; a quick tidy or a break works too.", true); return; }
            _w.Say(FocusLogic.Duration(min) + " free before " + what + ". That fits " + fit.Name + ", about " + FocusLogic.Duration((int)(Words.Hours(fit.Size) * 60)) + ".", true);
            _w.SelectTask(fit);
        }

        private void EnergyOrder()
        {
            string e = EnergyToday(S, _w.AppSettings, DateTime.Now);
            _w.Say(e switch
            {
                "low" => "Your energy is low today, so easy tasks come first in Today and brain-heavy ones last.",
                "high" => "Your energy is high today, so brain-heavy tasks come first in Today.",
                "medium" => "Your energy is medium today, so Today keeps its usual order.",
                _ => "No energy set today yet. The morning check-in on the Day menu sets it, and Today sorts to match.",
            }, true);
        }

        private void MoodMatch()
        {
            int m = PlanKit.Pick(_w, "Mood match", "How are you _feeling?", Moods);
            if (m < 0) return;
            var pool = PlanKit.OpenTasks(S, DateTime.Now).Where(t => !t.IsMeeting).ToList();
            var best = pool.Select((t, i) => (t, i, score: MoodScore(t, Moods[m]))).OrderByDescending(x => x.score).ThenBy(x => x.i).Take(3).Select(x => x.t).ToList();
            var t = PlanKit.PickTask(_w, "Mood match", "Tasks that fit _" + Moods[m].ToLowerInvariant(), best, "Enter goes to the task.");
            if (t == null) return;
            if (!_w.SelectTask(t)) _w.Say(t.Name + " is on the " + t.List + " list.", true);
        }

        private void GoodEnough()
        {
            var t = PlanKit.PickTask(_w, "Good enough mode", "_Task", PlanKit.OpenTasks(S, DateTime.Now));
            if (t == null) return;
            var bars = S.World.Get<TaskTexts>(EnoughId);
            if (bars.Of.TryGetValue(t.Id, out var bar))
            {
                int c = PlanKit.Pick(_w, "Good enough mode", "Good enough is: " + bar + ". _What now?", new[] { "The bar is met, mark it done", "Change the bar", "Remove the bar" });
                if (c < 0) return;
                if (c == 0) { _w.SetTaskStatus(t, Status.Done); return; }
                if (c == 2) { PlanKit.Change<TaskTexts>(S, EnoughId, "removing the good enough bar", x => x.Of.Remove(t.Id)); _w.Say("Bar removed for " + t.Name + ".", true); return; }
            }
            var text = PlanKit.AskText(_w, "Good enough mode", "Good _enough is", bar ?? "", "Like three pages, sources cited. When that is true, it is done.");
            if (text == null) return;
            PlanKit.Change<TaskTexts>(S, EnoughId, "a good enough bar", x => x.Of[t.Id] = text);
            _w.Say("Good enough for " + t.Name + ": " + text + ". Meet that and it is done, with 5 extra points.", true);
        }

        private void RateDread()
        {
            var t = PlanKit.PickTask(_w, "Dread score", "_Task", PlanKit.OpenTasks(S, DateTime.Now));
            if (t == null) return;
            var options = new[] { "1, not at all", "2, a little", "3, some", "4, a lot", "5, dreading it" };
            int i = PlanKit.Pick(_w, "Dread score", "How much do you _dread it?", options, (DreadOf(S, t.Id) ?? 3) - 1, "4 pays 5 extra points when it is done, 5 pays 10.");
            if (i < 0) return;
            PlanKit.Change<TaskScores>(S, DreadId, "a dread score", x => x.Of[t.Id] = i + 1);
            _w.Say("Dread " + (i + 1) + " for " + t.Name + "." + (i >= 3 ? " Doing it anyway pays extra." : ""), true);
        }

        private void PreFlight()
        {
            var now = DateTime.Now;
            var pool = S.Data.Tasks.Where(t => t.Status != Status.Done && !S.ListOf(t).Archived && (t.Type == TaskType.Assignment || t.Status == Status.DoneNotSubmitted))
                .OrderBy(t => t.Status == Status.DoneNotSubmitted ? 0 : 1).ThenBy(t => t.Due ?? DateTime.MaxValue).ToList();
            if (pool.Count == 0) pool = PlanKit.OpenTasks(S, now);
            var t = PlanKit.PickTask(_w, "Pre-flight checklist", "_Assignment", pool);
            if (t == null) return;
            var had = S.World.Get<Ticks>(FlightId).Of.TryGetValue(t.Id, out var h) ? h : new List<int>();
            var boxes = new List<CheckBox>();
            var rows = new List<FrameworkElement>();
            for (int i = 0; i < FlightItems.Length; i++)
            {
                var box = new CheckBox { Content = "_" + FlightItems[i], IsChecked = had.Contains(i), Margin = new Thickness(0, 4, 0, 4) };
                AutomationProperties.SetName(box, FlightItems[i]);
                // Each item is ticked once: no going round in circles. Control Z undoes a real mistake.
                if (had.Contains(i)) box.Unchecked += (_, _) => { box.IsChecked = true; Speech.Say(box, "Already checked. Control Z undoes it if that was a mistake.", true); };
                boxes.Add(box);
                rows.Add(box);
            }
            if (!PlanKit.Form(_w, "Pre-flight checklist: " + t.Name, "Tick each one once it is true. Ticked ones stay ticked.", rows)) return;
            var ticked = Enumerable.Range(0, boxes.Count).Where(i => boxes[i].IsChecked == true).ToList();
            if (ticked.SequenceEqual(had)) { _w.Say(ticked.Count + " of " + FlightItems.Length + " checked for " + t.Name + ".", true); return; }
            bool submitted = ticked.Contains(FlightItems.Length - 1) && !had.Contains(FlightItems.Length - 1);
            if (submitted && t.Status == Status.DoneNotSubmitted)
            {
                // The status change makes the one Control Z step; the ticks join it, so one Control Z undoes both.
                _w.SetTaskStatus(t, Status.Done);
                var x = S.World.Get<Ticks>(FlightId);
                x.Of[t.Id] = ticked;
                S.World.Set(FlightId, x);
                PlanKit.SaveAll(S);
                return;
            }
            PlanKit.Change<Ticks>(S, FlightId, "the pre-flight checklist", x => x.Of[t.Id] = ticked);
            _w.Say(ticked.Count == FlightItems.Length ? "All checked. Ready for takeoff: mark it done whenever you like." : ticked.Count + " of " + FlightItems.Length + " checked for " + t.Name + ".", true);
        }

        private void PickFirst()
        {
            var now = DateTime.Now;
            var t = PlanKit.PickTask(_w, "Pick tomorrow's first task", "Tomorrow's _first task", PlanKit.OpenTasks(S, now));
            if (t == null) return;
            var day = now.Hour < 4 ? now.Date : now.Date.AddDays(1);
            PlanKit.Change<FirstTask>(S, FirstId, "tomorrow's first task", x => { x.Day = S.Key(day); x.TaskId = t.Id; x.Name = t.Name; });
            int pts = AutoPoints.PlannedTomorrow(_w);
            _w.Say("Tomorrow starts with " + t.Name + ". I will remind you in the morning." + (pts > 0 ? " Plus " + pts + " points for planning tomorrow." : ""), true);
        }

        private void Pair()
        {
            var t = PlanKit.PickTask(_w, "Pair a boring task", "Boring _task", PlanKit.OpenTasks(S, DateTime.Now));
            if (t == null) return;
            var have = S.World.Get<TaskTexts>(PairId).Of.TryGetValue(t.Id, out var r) ? r : "";
            var reward = PlanKit.AskText(_w, "Pair a boring task", "Your _reward for finishing it", have, "Like an episode of a show, a snack, or ten minutes outside. Leave it empty to unpair.", allowEmpty: true);
            if (reward == null) return;
            PlanKit.Change<TaskTexts>(S, PairId, "pairing a task with a reward", x => { if (reward.Length == 0) x.Of.Remove(t.Id); else x.Of[t.Id] = reward; });
            _w.Say(reward.Length == 0 ? t.Name + " has no reward now." : "Paired: finish " + t.Name + ", then " + reward + ".", true);
        }

        private void HabitStack()
        {
            var h = S.World.Get<Habits>(HabitId);
            var options = h.Items.Select(x => "I just did: " + x.Routine).Concat(new[] { "Add a routine and a task" }).ToList();
            if (h.Items.Count > 0) options.Add("Remove a routine");
            int i = PlanKit.Pick(_w, "Habit stacking", "_Routine", options, 0, "Choose a routine when you have just done it, and hear the task stacked on it.");
            if (i < 0) return;
            if (i < h.Items.Count)
            {
                var tasks = h.Items[i].Tasks.Select(id => S.Data.Tasks.FirstOrDefault(x => x.Id == id)).OfType<TaskItem>().Where(x => !PlanKit.Finished(x)).ToList();
                if (tasks.Count == 0) { _w.Say("Nothing open is stacked on " + h.Items[i].Routine + " now.", true); return; }
                _w.Say("Stacked on " + h.Items[i].Routine + ": " + string.Join(", ", tasks.Select(x => x.Name)) + ".", true);
                _w.SelectTask(tasks[0]);
                return;
            }
            if (options[i] == "Remove a routine")
            {
                int r = PlanKit.Pick(_w, "Habit stacking", "Routine to _remove", h.Items.Select(x => x.Routine).ToList());
                if (r < 0) return;
                string gone = h.Items[r].Routine;
                PlanKit.Change<Habits>(S, HabitId, "removing a routine", x => x.Items.RemoveAll(y => y.Routine == gone));
                _w.Say("Removed " + gone + ". Its tasks stay as they are.", true);
                return;
            }
            var routine = PlanKit.AskText(_w, "Habit stacking", "The _routine", "", "Like after breakfast, or the name of a repeating task. Finishing a task with that exact name offers the stacked one too.");
            if (routine == null) return;
            var t = PlanKit.PickTask(_w, "Habit stacking", "_Task to stack on it", PlanKit.OpenTasks(S, DateTime.Now));
            if (t == null) return;
            PlanKit.Change<Habits>(S, HabitId, "stacking a task on a routine", x =>
            {
                var hb = x.Items.FirstOrDefault(y => y.Routine.Equals(routine, StringComparison.OrdinalIgnoreCase));
                if (hb == null) x.Items.Add(hb = new Habit { Routine = routine });
                if (!hb.Tasks.Contains(t.Id)) hb.Tasks.Add(t.Id);
            });
            _w.Say("Stacked " + t.Name + " on " + routine + ".", true);
        }

        private void FunCountdown()
        {
            var now = DateTime.Now;
            var plans = S.World.Get<FunPlans>(FunId).Items.Where(p => p.When.Date >= now.Date).OrderBy(p => p.When).ToList();
            var options = new List<string> { "Add a fun thing" }.Concat(plans.Select(p => "Remove " + p.Name + ", " + Words.Day(p.When, now))).ToList();
            int i = PlanKit.Pick(_w, "Countdown to something fun", "_What now?", options, 0, plans.Count == 0 ? null : "Coming up: " + string.Join(", ", plans.Select(p => p.Name + " " + Words.Day(p.When, now))) + ".");
            if (i < 0) return;
            if (i > 0)
            {
                var gone = plans[i - 1];
                PlanKit.Change<FunPlans>(S, FunId, "removing a fun plan", x => x.Items.RemoveAll(p => p.Name == gone.Name && p.When == gone.When));
                _w.Say("Removed " + gone.Name + ".", true);
                return;
            }
            var name = PlanKit.AskText(_w, "Countdown to something fun", "The _fun thing");
            if (name == null) return;
            var date = PlanKit.AskText(_w, "Countdown to something fun", "_When is it?", "", "Like Saturday, December 12, or in 2 weeks.");
            if (date == null) return;
            var (when, _) = Language.ParseDate(date, now);
            if (when is not DateTime d || d.Date < now.Date) { _w.Say("I could not read that date. Try Saturday or December 12.", true); return; }
            PlanKit.Change<FunPlans>(S, FunId, "a fun plan", x => { x.Items.RemoveAll(p => p.When.Date < now.Date); x.Items.Add(new FunPlan { Name = name, When = d.Date }); });
            _w.Say("Added " + name + ", " + Words.Day(d, now) + ". The summary line counts down to it.", true);
        }

        // ---- Self-test ----

        public void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("plan day: " + what); }
            var now = DateTime.Now;
            var a = new TaskItem { Name = "plan test a", List = s.Data.Lists[0].Name, Status = Status.Today };
            var b = new TaskItem { Name = "plan test b", List = s.Data.Lists[0].Name, Status = Status.Today };
            s.Data.Tasks.Add(a); s.Data.Tasks.Add(b);

            // Tiny goal: done, back, done again wins once; the win is kept with the world.
            s.World.Set(TinyId, new TinyGoal { Day = s.Key(now), Text = a.Name, TaskId = a.Id });
            Need(TinyDone(s, a, now) && !TinyDone(s, a, now), "tiny goal won twice");
            Need(TinyUndone(s, a, now) && TinyDone(s, a, now), "tiny goal did not come back after undo");
            Need(s.World.Get<TinyGoal>(TinyId).WonDays.Count == 1, "tiny goal counted a day twice");
            Need(!TinyDone(s, b, now), "another task won the tiny goal");

            // Daily three: only once every one is finished.
            s.World.Set(ThreeId, new DailyThree { Day = s.Key(now), Tasks = new() { a.Id, b.Id } });
            a.Status = Status.Done;
            Need(!ThreeDone(s, a, now), "daily three won with one left");
            b.Status = Status.Done;
            Need(ThreeDone(s, b, now) && !ThreeDone(s, b, now), "daily three not won once");
            b.Status = Status.Today;
            ThreeUndone(s, b, now);
            Need(!s.World.Get<DailyThree>(ThreeId).Won, "daily three stayed won after undo");

            // Daily three paid once: b finished the three and carries the prize; setting a back takes it, so a again pays once.
            b.Status = Status.Done;
            Need(ThreeDone(s, b, now) && s.World.Get<DailyThree>(ThreeId).PaidBy == b.Id, "the daily three does not know who paid");
            Ledger.Pay(s, 10, "test three", ThreeId, b.Id);
            int withPrize = Engine.Total(s.Data);
            a.Status = Status.Today;
            ThreeUndone(s, a, now);
            Need(Engine.Total(s.Data) == withPrize - 10, "setting another of the three back left the prize");
            a.Status = Status.Done;
            Need(ThreeDone(s, a, now), "the daily three did not come back");
            Ledger.Pay(s, 10, "test three", ThreeId, a.Id);
            Need(Engine.Total(s.Data) == withPrize, "the daily three paid twice");
            s.Data.Log.RemoveAll(e => e.Feature == ThreeId && (e.TaskId == a.Id || e.TaskId == b.Id));
            b.Status = Status.Today;

            // Word of the day: the first finish, then every third.
            s.World.SetKept(WordId, new WordCount());
            var turns = Enumerable.Range(0, 7).Select(_ => WordTurn(s, now)).ToList();
            Need(turns.SequenceEqual(new[] { true, false, false, true, false, false, true }), "word of the day on every finish");
            s.World.SetKept(WordId, new WordCount());

            // Points: a dreaded task pays and un-finishing takes it back.
            Need(DreadBonus(5) == 10 && DreadBonus(4) == 5 && DreadBonus(3) == 0 && DreadBonus(null) == 0, "dread bonus");
            int before = Engine.Total(s.Data);
            Ledger.Pay(s, DreadBonus(5), "test dread", DreadId, a.Id);
            Engine.Unfinish(s, a, now);
            Need(Engine.Total(s.Data) == before, "undo left dread points");

            // Promise: nudged once, never a second time.
            s.World.Set(PromiseId, new Promises { Items = { new Promise { TaskId = b.Id, Name = b.Name, At = now.AddMinutes(-1) } } });
            s.Remember("a step before the nudge");
            Need(DuePromises(s, now).Count == 1 && DuePromises(s, now.AddMinutes(5)).Count == 0, "a promise nudged twice");
            // Control Z of an earlier step puts the bag back, but the nudge is kept: never a second.
            s.Undo();
            Need(DuePromises(s, now.AddMinutes(6)).Count == 0, "a promise nudged again after Control Z");

            // Energy order, fitting a gap and mood scores.
            a.Energy = Energy.BrainHeavy; b.Energy = Energy.Easy;
            Need(EnergySort(new() { a, b }, "low")[0] == b && EnergySort(new() { b, a }, "high")[0] == a && EnergySort(new() { a, b }, "")[0] == a, "energy order");
            a.Size = Size.Large; b.Size = Size.Small;
            Need(Fit(new[] { a, b }, 45) == b && Fit(new[] { a, b }, 20) == null && Fit(new[] { a, b }, 120) == a, "fill the gap fit");
            Need(MoodScore(b, "Tired") > MoodScore(a, "Tired") && MoodScore(a, "Focused") > MoodScore(b, "Focused"), "mood scores");

            // Countdown line.
            s.World.Set(FunId, new FunPlans { Items = { new FunPlan { Name = "Concert", When = now.Date.AddDays(4) } } });
            Need(FunLine(s, now) == "Concert in 4 days", "countdown: " + FunLine(s, now));

            s.Data.Tasks.Remove(a); s.Data.Tasks.Remove(b);
            foreach (var topic in new[] { "plan.promise", "plan.word", "plan.words", "plan.tinygoal", "plan.daily3", "plan.goodenough", "plan.pair" })
                Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            foreach (var id in new[] { PromiseId, WordId, TinyId, ThreeId, GapId, EnergyId, MoodId, EnoughId, DreadId, FlightId, FirstId, PairId, HabitId, FunId })
                Need(Tools.Find(id) != null, id + " is not in the toolbox");
        }
    }
}
