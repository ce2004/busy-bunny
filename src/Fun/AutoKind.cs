using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>
    /// Automatic kindness, each with its own switch: tomorrow's treat, hype countdown before tests, your motto read back,
    /// thank-you notes, grace days, how did that feel, the task-hopping note, a soft landing after vacation and an
    /// automatic first step for new tasks.
    /// </summary>
    public sealed class AutoKind : IFeature
    {
        public sealed class Treat { public string ForDay { get; set; } = ""; public string Text { get; set; } = ""; }
        public sealed class Marks { public DateTime MottoSaid { get; set; } public string FeelDay { get; set; } = ""; public string HopDay { get; set; } = ""; public bool HopPending { get; set; } }
        public sealed class Feelings { public Dictionary<Guid, string> Of { get; set; } = new(); public Dictionary<string, Dictionary<string, int>> ByKind { get; set; } = new(); }

        public const string TreatId = "auto.treat", HypeId = "auto.hype", MottoId = "auto.motto", ThanksId = "auto.thanks", GraceId = "auto.grace",
            FeelId = "auto.feel", HopId = "auto.hopping", LandingId = "auto.softlanding", StepId = "auto.firststep";
        private const string MarksId = "auto.kindmarks";
        public static readonly string[] FeelOptions = { "Easy", "Fine", "Draining" };

        private MainWindow _w = null!;
        private Store S => _w.Store;
        private readonly List<(DateTime When, Guid Id)> _starts = new();
        private readonly Random _rng = new();

        public void Attach(MainWindow w)
        {
            _w = w;
            void T(string id, string name, string drawer, string what, Action run) => Tools.Register(new BunnyTool(id, name, drawer, what, run) { Since = "0.14.0" });
            T(TreatId, "Tomorrow's treat", Drawers.Wins, "Automatic: winning today's tiny goal leaves a treat for tomorrow morning.", () => _w.Say("Tomorrow's treat is on. " + (S.World.Get<Treat>(TreatId).ForDay == S.Key(DateTime.Now.AddDays(1)) ? "A treat is waiting for tomorrow." : "Win today's tiny goal to leave one."), true));
            T(HypeId, "Hype countdown before tests", Drawers.Wins, "Automatic: the 3 mornings before a test get a hype line.", () => _w.Say(Hype(S, DateTime.Now) ?? "No tests in the next 3 days. Hype is resting.", true));
            T(MottoId, "Your motto read back", Drawers.Notes, "Automatic: now and then, at most once a week, Bunny reads your motto.", Motto);
            T(ThanksId, "Thank-you notes", Drawers.Wins, "Automatic: after a large or dreaded task, Bunny says thank you.", () => _w.Say("Thank-you notes are on. Bunny thanks you after large tasks and ones you dread 4 or 5. Your name for them is set in Options, Settings, Plans and little extras.", true));
            T(GraceId, "Grace days", Drawers.Wins, "Automatic: streaks in the planning tools forgive missed days, 1 a week unless you change it.", () => _w.Say(GraceLine(S), true));
            T(FeelId, "How did that feel", Drawers.Wins, "Automatic and skippable: after a task, pick easy, fine or draining; once a day unless you change it.", FeelReport);
            T(HopId, "Task-hopping note", Drawers.GetGoing, "Automatic: after lots of switching between tasks, one gentle note to pick one; once a day at most.", () => _w.Say("Task-hopping note is on. Starting more than 4 different tasks in 20 minutes gets one gentle note, once a day at most.", true));
            T(LandingId, "Soft landing after vacation", Drawers.Rest, "Automatic: the first day after vacation is planned light, with half your usual hours.", () => _w.Say("Soft landing is on. The first day back from vacation mode gets half your usual hours. Control Z undoes it that morning.", true));
            T(StepId, "Auto first step", Drawers.GetGoing, "Automatic: a new task gets a suggested first step in its notes, which you can delete.", () => _w.Say("Auto first step is on. New tasks with empty notes get one small first step." + (PlanKit.Prefs(S).BunnyFirstSteps ? " Bunny writes them for lists Bunny may see." : " They come from simple rules; Bunny can write them instead, in Options, Settings, Plans and little extras."), true));

            PlanKit.Morning(LandingId, SoftLanding);
            PlanKit.Morning(TreatId, (s, now) =>
            {
                var t = s.World.Get<Treat>(TreatId);
                if (t.ForDay != s.Key(now) || t.Text.Length == 0) return null;
                s.World.Set(TreatId, new Treat());
                return "A treat from yesterday's tiny goal: " + t.Text;
            });
            PlanKit.Morning(HypeId, (s, now) => Hype(s, now));
            PlanKit.Morning(MottoId, (s, now) =>
            {
                string motto = PlanKit.Prefs(s).Motto;
                var m = s.World.GetKept<Marks>(MarksId);
                if (motto.Length == 0 || now - m.MottoSaid < TimeSpan.FromDays(7) || _rng.Next(3) != 0) return null;
                m.MottoSaid = now;
                s.World.SetKept(MarksId, m);
                return PlanKit.Line(s, "auto.motto", ("motto", motto));
            });

            GameEvents.TaskDone += OnDone;
            GameEvents.TaskStarted += OnStarted;
            Ticker.Every(HopId, _ => S.World.GetKept<Marks>(MarksId).HopPending && !FocusTools.SprintRunning, SayHop);
            w.TaskAdded += OnAdded;
        }

        // ---- For other tools ----

        public static bool IsTest(TaskItem t) => t.Type == TaskType.Test ||
            new[] { "test", "exam", "midterm", "quiz", "final" }.Any(x => (" " + t.Name.ToLowerInvariant() + " ").Contains(" " + x));

        public static void LeaveTreat(Store s, DateTime now)
        {
            s.World.Set(TreatId, new Treat { ForDay = s.Key(now.AddDays(1)), Text = Content.Pick(s, "auto.treat") });
            s.World.Save();
        }

        public static void TakeTreat(Store s, DateTime now)
        {
            if (s.World.Get<Treat>(TreatId).ForDay != s.Key(now.AddDays(1))) return;
            s.World.Set(TreatId, new Treat());
            s.World.Save();
        }

        /// <summary>A hype line for the nearest test due in the next 3 days, or null.</summary>
        public static string? Hype(Store s, DateTime now)
        {
            var test = s.Data.Tasks.Where(t => !PlanKit.Finished(t) && IsTest(t) && !s.ListOf(t).Archived && t.Due is DateTime d && (d.Date - now.Date).Days is >= 1 and <= 3)
                .OrderBy(t => t.Due).FirstOrDefault();
            return test == null ? null : PlanKit.Line(s, "auto.hype", ("task", test.Name), ("when", Words.Day(test.Due!.Value, now)));
        }

        public static string GraceLine(Store s)
        {
            int g = PlanKit.Grace(s);
            return (g == 0 ? "No grace days: a missed day ends a streak in the planning tools." : PlanKit.Plural(g, "grace day", "grace days") + " a week: that many missed days never end a streak in the planning tools, like the daily three.")
                + " Change it in Options, Settings, Plans and little extras.";
        }

        /// <summary>Whether starting this task now makes more than 4 switches in 20 minutes. Keeps the last 20 minutes of starts.</summary>
        public static bool Hopping(List<(DateTime When, Guid Id)> starts, Guid id, DateTime now)
        {
            starts.RemoveAll(x => now - x.When > TimeSpan.FromMinutes(20));
            starts.Add((now, id));
            int switches = starts.Zip(starts.Skip(1)).Count(p => p.First.Id != p.Second.Id);
            return switches > 4;
        }

        /// <summary>A rule-made first step for a task name.</summary>
        public static string FirstStep(string name)
        {
            string n = name.ToLowerInvariant();
            bool Any(params string[] w) => w.Any(x => n.Contains(x));
            if (Any("read", "chapter")) return "Open the reading and read just the first page.";
            if (Any("essay", "paper", "write", "post", "draft", "report")) return "Write one messy sentence about it.";
            if (Any("email", "message", "text ")) return "Open a new message and type the greeting.";
            if (Any("call", "phone")) return "Find the number and put it where you can see it.";
            if (Any("study", "exam", "test", "quiz", "midterm", "final")) return "Gather your notes for five minutes.";
            if (Any("problem set", "homework", "worksheet", "lab")) return "Open it and read the first question.";
            if (Any("clean", "tidy", "laundry", "dishes")) return "Set a five minute timer and start with one small spot.";
            if (Any("buy", "order", "groceries", "shop")) return "Write down the first thing you need.";
            if (Any("meet", "appointment", "office hours")) return "Check the time and where it is.";
            if (Any("project", "presentation", "slides")) return "Write down what done looks like, in one line.";
            return "Spend two minutes deciding what the very first move is.";
        }

        // ---- Handlers ----

        private string? SoftLanding(Store s, DateTime now)
        {
            if (s.Data.PeekDay(now.AddDays(-1)).Mode != DayMode.Vacation || s.Data.PeekDay(now).Mode == DayMode.Vacation || s.Data.PeekDay(now).Hours > 0) return null;
            double usual = s.Data.HoursAvailable, light = Math.Max(1, Math.Round(usual / 2 * 2) / 2);
            if (light >= usual) return null;
            s.Remember("a soft landing after vacation");
            s.Data.Day(now).Hours = light;
            s.Save();
            _w.Dispatcher.BeginInvoke(() => _w.SaveAndRefresh(), DispatcherPriority.Background);
            return "Welcome back. Today is planned light: " + Math.Round(light, 1).ToString() + " hours instead of " + Math.Round(usual, 1).ToString() + ". Control Z undoes it.";
        }

        private void OnDone(TaskItem t)
        {
            var s = S;
            var now = DateTime.Now;
            if (Tools.IsOn(ThanksId) && (t.Size == Size.Large || PlanDay.DreadOf(s, t.Id) >= 4))
            {
                string name = PlanKit.Prefs(s).YourName;
                Calm.AfterTaskDone(_w, Game.Other, PlanKit.Line(s, "auto.thanks", ("you", name.Length > 0 ? ", " + name : "")));
            }
            if (!Tools.IsOn(FeelId) || Calm.FunPaused || Calm.Hush(s, _w.AppSettings, now) is "a rough day" or "class" or "quiet hours") return;
            var m = s.World.GetKept<Marks>(MarksId);
            if (!PlanKit.Prefs(s).FeelEveryTask && m.FeelDay == s.Key(now)) return;
            m.FeelDay = s.Key(now);
            s.World.SetKept(MarksId, m);
            s.World.Save();
            // After the line about the finish has been said, and only when nothing else is open.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
            timer.Tick += (_, _) => { timer.Stop(); AskFeel(t); };
            timer.Start();
        }

        private void AskFeel(TaskItem t)
        {
            if (!_w.IsActive || Application.Current.Windows.OfType<Window>().Any(x => x != _w && x.IsVisible && x.Owner == _w)) return;
            int i = PlanKit.Pick(_w, "How did that feel?", "How did _" + t.Name + " feel?", FeelOptions, 1, "Optional. Escape skips it. It helps spot what drains you.");
            if (i < 0) return;
            Record(S, t, FeelOptions[i]);
            _w.Say("Noted: " + FeelOptions[i].ToLowerInvariant() + ".", true);
        }

        /// <summary>Keeps how a task felt, counted by its tag (or list when it has none). A second answer for the same task replaces the first.</summary>
        public static void Record(Store s, TaskItem t, string feel)
        {
            var f = s.World.Get<Feelings>(FeelId);
            string kind = t.Tag.Length > 0 ? t.Tag : t.List;
            if (!f.ByKind.TryGetValue(kind, out var counts)) f.ByKind[kind] = counts = new();
            if (f.Of.TryGetValue(t.Id, out var old) && counts.TryGetValue(old, out int n) && n > 0) counts[old] = n - 1;
            f.Of[t.Id] = feel;
            counts[feel] = counts.TryGetValue(feel, out int c) ? c + 1 : 1;
            s.World.Set(FeelId, f);
            s.World.Save();
        }

        public static string FeelLine(Store s)
        {
            var f = s.World.Get<Feelings>(FeelId);
            if (f.Of.Count == 0) return "No answers yet. After a task, pick easy, fine or draining, and this fills in.";
            int Of(Dictionary<string, int> d, string k) => d.TryGetValue(k, out int v) ? v : 0;
            var drain = f.ByKind.Where(kv => Of(kv.Value, "Draining") > 0).OrderByDescending(kv => Of(kv.Value, "Draining")).Take(3).Select(kv => kv.Key + " " + Of(kv.Value, "Draining")).ToList();
            var easy = f.ByKind.Where(kv => Of(kv.Value, "Easy") > 0).OrderByDescending(kv => Of(kv.Value, "Easy")).Take(3).Select(kv => kv.Key + " " + Of(kv.Value, "Easy")).ToList();
            return PlanKit.Plural(f.Of.Count, "answer", "answers") + " so far." + (drain.Count > 0 ? " Most draining: " + string.Join(", ", drain) + "." : " Nothing drained you yet.") + (easy.Count > 0 ? " Easiest: " + string.Join(", ", easy) + "." : "");
        }

        private void FeelReport() => _w.Say(FeelLine(S), true);

        private void OnStarted(TaskItem t)
        {
            var now = DateTime.Now;
            if (!Hopping(_starts, t.Id, now) || !Tools.IsOn(HopId) || Calm.FunPaused) return;
            var m = S.World.GetKept<Marks>(MarksId);
            if (m.HopDay == S.Key(now)) return;
            m.HopDay = S.Key(now);
            m.HopPending = true;
            S.World.SetKept(MarksId, m);
            S.World.Save();
        }

        private void SayHop(DateTime now)
        {
            var m = S.World.GetKept<Marks>(MarksId);
            m.HopPending = false;
            S.World.SetKept(MarksId, m);
            S.World.Save();
            if (m.HopDay == S.Key(now) && Tools.IsOn(HopId) && !Calm.FunPaused) Calm.SayUnprompted(_w, Content.Pick(S, "auto.hopping"));
        }

        private void OnAdded(TaskItem t)
        {
            if (!Tools.IsOn(StepId) || t.Notes.Trim().Length > 0 || t.Name.Contains(" (for ")) return;
            var s = S;
            var now = DateTime.Now;
            bool bunny = PlanKit.Prefs(s).BunnyFirstSteps && SmartBunny.Ready && !Calm.FunPaused && BunnyWrite.MayAsk(s, StepId, now) && BunnyWrite.Visible(s).Contains(t);
            if (!bunny) { SetStep(s, t, FirstStep(t.Name)); return; }
            _ = AskBunny(s, t);
        }

        private async System.Threading.Tasks.Task AskBunny(Store s, TaskItem t)
        {
            string step = await BunnyWrite.Short(s, StepId, "Suggest one tiny, concrete first step, under 15 words, for a student starting this task: " + t.Name + ". Answer with the step only.", "auto.firststep");
            if (step.Trim().Length == 0) step = FirstStep(t.Name);
            if (s.Data.Tasks.Contains(t) && t.Notes.Trim().Length == 0) { SetStep(s, t, step.Trim()); _w.Refresh(); }
        }

        /// <summary>Writes the first step into empty notes (part of adding the task, so no Remember of its own).</summary>
        public static void SetStep(Store s, TaskItem t, string step)
        {
            if (t.Notes.Trim().Length > 0) return;
            t.Notes = "First step: " + step;
            s.Save();
        }

        private void Motto()
        {
            string motto = PlanKit.Prefs(S).Motto;
            if (motto.Length > 0) { _w.Say("Your motto: " + motto, true); return; }
            _w.Say("You have no motto yet. Type one on the Plans and little extras page of Settings, and I will read it back now and then.", true);
            _w.OpenSettingsAt("Plans and little extras");
        }

        // ---- Self-test ----

        public void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("auto kind: " + what); }
            var now = DateTime.Now;
            // Treat: left, taken back, and only for tomorrow.
            LeaveTreat(s, now);
            Need(s.World.Get<Treat>(TreatId).ForDay == s.Key(now.AddDays(1)) && s.World.Get<Treat>(TreatId).Text.Length > 0, "no treat was left");
            TakeTreat(s, now);
            Need(s.World.Get<Treat>(TreatId).Text.Length == 0, "the treat stayed after undo");

            // Tests and hype.
            Need(IsTest(new TaskItem { Name = "STAT midterm" }) && IsTest(new TaskItem { Name = "quiz 4" }) && !IsTest(new TaskItem { Name = "contest entry" }), "test names");
            var exam = new TaskItem { Name = "final exam", List = s.Data.Lists[0].Name, Due = now.Date.AddDays(2), Status = Status.ThisWeek };
            s.Data.Tasks.Add(exam);
            var others = s.Data.Tasks.Where(IsTest).ToList(); s.Data.Tasks.RemoveAll(IsTest); s.Data.Tasks.Add(exam);
            Need(Hype(s, now) is string h && h.Contains("final exam") && !h.Contains("{"), "no hype line two days before a test");
            Need(Hype(s, now.AddDays(-5)) == null || !Hype(s, now.AddDays(-5))!.Contains("final exam"), "hype a week early");
            s.Data.Tasks.Remove(exam); s.Data.Tasks.AddRange(others);

            // Task hopping: 5 switches in 20 minutes, not 4, and old starts forgotten.
            var starts = new List<(DateTime, Guid)>();
            var ids = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToList();
            bool hop = false;
            for (int i = 0; i < 5; i++) hop = Hopping(starts, ids[i], now.AddMinutes(i));
            Need(!hop, "4 switches counted as hopping");
            Need(Hopping(starts, ids[5], now.AddMinutes(5)), "5 switches in 20 minutes were not hopping");
            Need(!Hopping(starts, ids[0], now.AddMinutes(60)), "old starts still counted");

            // First steps never overwrite notes.
            Need(FirstStep("chapter 4 reading").Contains("first page") && FirstStep("zzz").Length > 0, "first step rules");
            var t = new TaskItem { Name = "x", Notes = "mine" };
            SetStep(s, t, "step");
            Need(t.Notes == "mine", "a first step overwrote her notes");

            // Feelings: a second answer replaces the first.
            var f = new TaskItem { Name = "feel test", Tag = "TESTFEEL" };
            Record(s, f, "Draining"); Record(s, f, "Easy");
            var fe = s.World.Get<Feelings>(FeelId).ByKind["TESTFEEL"];
            Need(fe["Draining"] == 0 && fe["Easy"] == 1, "feelings counted twice");
            s.World.Set(FeelId, new Feelings());

            foreach (var topic in new[] { "auto.treat", "auto.hype", "auto.motto", "auto.thanks", "auto.hopping", "auto.firststep" }) Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            foreach (var line in Content.Lines("auto.thanks")) Need(line.Contains("{you}"), "a thank-you note with no place for her name: " + line);
            foreach (var id in new[] { TreatId, HypeId, MottoId, ThanksId, GraceId, FeelId, HopId, LandingId, StepId }) Need(Tools.Find(id) != null, id + " is not in the toolbox");
        }
    }
}
