using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// The I am stuck drawer (sweep 8.4): two-minute dare, just the first line, rough draft mode, future-you message,
    /// pep talk on demand (Control Shift J), coin flip with a reason, unstick questions, task makeover and pick from
    /// three. The mystery box lives in PlayTime.cs.
    /// </summary>
    public sealed class StuckTools : IFeature
    {
        private const string Dare = "stuck.dare", FirstLine = "stuck.firstline", Draft = "stuck.roughdraft", FutureYou = "stuck.futureyou",
            Pep = "stuck.pep", Coin = "stuck.coin", Unstick = "stuck.unstick", Makeover = "stuck.makeover", Three = "stuck.three", KeepGoing = "stuck.keepgoing";
        public const string OriginalPrefix = "Original name: ";
        private MainWindow _w = null!;
        private Store S => _w.Store;
        // The last dare, first line or rough draft that ended, so using it again soon after offers to keep going.
        private (string Id, Guid? Task, DateTime When)? _ended;
        // Comfort lines asked for lately: a few are kind, an endless stream is not (sweep 12.7).
        private readonly List<DateTime> _comfort = new();

        public void Attach(MainWindow w)
        {
            _w = w;
            FunTimer.Hook();
            const string since = "0.14.0";
            Tools.Register(new BunnyTool(Dare, "Two-minute dare", Drawers.Stuck, "Two minutes on your top task. Stopping then still counts as a win.", () => Challenge(Dare)) { Since = since });
            Tools.Register(new BunnyTool(FirstLine, "Just the first line", Drawers.Stuck, "Five minutes to write only one sentence of a writing task. That counts as a win.", () => Challenge(FirstLine)) { Since = since });
            Tools.Register(new BunnyTool(Draft, "Rough draft mode", Drawers.Stuck, "Fifteen minutes with permission to do it badly first. It counts as a win.", () => Challenge(Draft)) { Since = since });
            Tools.Register(new BunnyTool(FutureYou, "Future-you message", Drawers.Stuck, "Reads back something past you did and was glad about.", ReadFutureYou) { Since = since });
            Tools.Register(new BunnyTool(Pep, "Pep talk", Drawers.Stuck, "A two-sentence pep talk at your roast level.", () => _ = PepTalk()) { Since = since, Keys = "Control Shift J" });
            Tools.Register(new BunnyTool(Coin, "Coin flip with a reason", Drawers.Stuck, "Pick two tasks; the coin picks one and says why.", CoinFlip) { Since = since });
            Tools.Register(new BunnyTool(Unstick, "Unstick questions", Drawers.Stuck, "Three short questions about a task, then one fix to try.", UnstickQuestions) { Since = since });
            Tools.Register(new BunnyTool(Makeover, "Task makeover", Drawers.Stuck, "Renames a scary task as a friendly first step. The old name stays in its notes.", () => _ = DoMakeover(null)) { Since = since });
            Tools.Register(new BunnyTool(Three, "Pick from three", Drawers.Stuck, "Just three tasks to choose from, nothing else.", PickFromThree) { Since = since });
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.J, Tools.Guard(Pep, () => _ = PepTalk()), "Control Shift J: a pep talk when you feel stuck");
        }

        // ---- Two-minute dare, just the first line, rough draft mode ----

        private static (string Name, int Minutes, string Start) About(string id) => id switch
        {
            Dare => ("Two-minute dare", 2, "Two minutes, that is all. Start anywhere."),
            FirstLine => ("Just the first line", 5, "Five minutes to write one sentence. Only one. It can be clumsy."),
            _ => ("Rough draft mode", 15, "Fifteen minutes, and you have permission to do it badly. Messy first, better later."),
        };

        private void Challenge(string id)
        {
            if (FunTimer.Handle(_w, FunTimer.Current?.Id == KeepGoing ? KeepGoing : id)) return;
            var now = DateTime.Now;
            var (name, minutes, start) = About(id);
            if (_ended is { } ended && ended.Id == id && now - ended.When < TimeSpan.FromMinutes(5))
            {
                _ended = null;
                var task0 = FunKit.Find(S, ended.Task);
                int len = FunKit.SprintLength(FunKit.Energy(S, _w.AppSettings, now));
                int pick = FunKit.Pick(_w, name, "Keep going?", new[] { "Keep going: a " + len + " minute sprint" + (task0 != null ? " on " + task0.Name : ""), "Start a new " + name.ToLowerInvariant() });
                if (pick < 0) return;
                if (pick == 0) { StartKeepGoing(task0, len); return; }
            }
            TaskItem? task;
            if (id == Dare) task = FunKit.ChosenOrTop(_w, now);
            else
            {
                var pool = FunKit.Candidates(S, now);
                if (id == FirstLine)
                {
                    var writing = pool.Where(IsWriting).ToList();
                    if (writing.Count > 0) pool = writing;
                }
                if (_w.ChosenTask is TaskItem c && FunKit.Open(c) && !pool.Contains(c)) pool.Insert(0, c);
                task = FunKit.PickTask(_w, name, id == FirstLine ? "Which writing task? Only the first sentence counts today." : "Which task gets a rough draft?", pool, _w.ChosenTask);
                if (task == null && pool.Count > 0) return;
            }
            if (!SkillsTools.BeforeTask(_w)) return;
            FunTimer.Start(_w, id, name, minutes, task, false, (t, _) => ChallengeEnded(id, t.Task));
            _w.Say(name + ": " + start + (task != null ? " On " + task.Name + "." : "") + " I will tell you when the time is up.", true);
        }

        public static bool IsWriting(TaskItem t)
        {
            if (t.Type == TaskType.Assignment) return true;
            string n = t.Name.ToLowerInvariant();
            return new[] { "write", "writing", "essay", "paper", "email", "draft", "report", "reflection", "response", "post", "letter", "summary", "discussion", "journal", "outline" }
                .Any(word => n.Contains(word));
        }

        private void ChallengeEnded(string id, TaskItem? task)
        {
            var (name, _, _) = About(id);
            _ended = (id, task?.Id, DateTime.Now);
            string line = Content.Pick(S, "stuck.win");
            string lead = "Time is up. " + line + " Open " + name + " again in the next five minutes to keep going as a sprint, or stop here.";
            FunKit.Win(_w, id, 2, name, 3, lead);
        }

        private void StartKeepGoing(TaskItem? task, int minutes)
        {
            FunTimer.Start(_w, KeepGoing, "Keep-going sprint", minutes, task, true, (t, counted) =>
                _w.Say("Sprint over, " + FunKit.Plural(counted, "minute", "minutes") + (t.Task != null ? " on " + t.Task.Name : "") + ". It counts. You kept going, and that was the hard part."));
            _w.Say("Keep-going sprint: " + minutes + " minutes" + (task != null ? " on " + task.Name : "") + ". Open the same tool again to hear the time left.", true);
        }

        // ---- Future-you message and pep talk ----

        /// <summary>A kind line asked for again and again stops helping: after three in half an hour, a gentle nudge to act instead.</summary>
        private bool EnoughComfort(DateTime now)
        {
            _comfort.RemoveAll(t => now - t > TimeSpan.FromMinutes(30));
            if (_comfort.Count >= 3)
            {
                _w.Say("You have heard three of these in the last half hour, and you deserved every one. More words will not add much now. Try one tiny step instead: the Two-minute dare is right here in this drawer.", true);
                return true;
            }
            _comfort.Add(now);
            return false;
        }

        /// <summary>Her own past wins, newest first: things she logged with I did a thing (and any proud moment kept in the log).</summary>
        public static List<LogEntry> PastWins(Store s, DateTime now) => s.Data.Log
            .Where(e => e.Kind is "DidAThing" or "Proud" or "Win" && e.Text.Trim().Length > 0 && e.When.Date < now.Date)
            .OrderByDescending(e => e.When).Take(200).ToList();

        private void ReadFutureYou()
        {
            var now = DateTime.Now;
            if (EnoughComfort(now)) return;
            var wins = PastWins(S, now);
            string tail = Content.Pick(S, "stuck.futureyou");
            if (wins.Count == 0)
            {
                _w.Say(tail + " Things you log with I did a thing, Control Shift I, come back here later as notes from past you.", true);
                return;
            }
            var e = wins[FunKit.Rng.Next(Math.Min(wins.Count, 30))];
            _w.Say("From past you, " + Words.Day(e.When, now) + ": " + e.Text.Trim().TrimEnd('.') + ". " + tail, true);
        }

        private async System.Threading.Tasks.Task PepTalk()
        {
            try
            {
                var now = DateTime.Now;
                if (EnoughComfort(now)) return;
                int level = Math.Clamp(_w.AppSettings.RoastLevel, 0, 4);
                string topic = level <= 1 ? "stuck.pep.kind" : "stuck.pep.cheeky";
                string prompt = "Write a pep talk of exactly two short sentences for a college student who feels stuck starting work. "
                    + "Roast dial " + level + " of 4: 0 is gentle and soft, 4 is a playful roast. Always warm: never disappointed, never cold. "
                    + "Do not name any task. Under 40 words. Reply with only the two sentences.";
                string line = await BunnyWrite.Short(S, Pep, prompt, topic);
                _w.Say(line.Length > 0 ? line : Content.Pick(S, topic), true);
            }
            catch (Exception e) { ErrorLog.Write("Pep talk failed: " + e.GetType().Name); }
        }

        // ---- Coin flip with a reason ----

        private void CoinFlip()
        {
            var now = DateTime.Now;
            var pool = FunKit.Candidates(S, now);
            if (pool.Count < 2) { _w.Say("The coin needs two tasks to choose between, and there are not two open ones on Today or this week.", true); return; }
            var a = FunKit.PickTask(_w, "Coin flip", "First task for the coin.", pool, _w.ChosenTask);
            if (a == null) return;
            var b = FunKit.PickTask(_w, "Coin flip", "And the second task.", pool.Where(t => t != a).ToList());
            if (b == null) return;
            bool heads = FunKit.Rng.Next(2) == 0;
            var win = heads ? a : b;
            var other = heads ? b : a;
            _w.SelectTask(win);
            _w.Say((heads ? "Heads" : "Tails") + ". " + win.Name + " first: " + Reason(S, win, other, now) + ".", true);
        }

        /// <summary>A real reason when there is one (due sooner, smaller, higher on its list), otherwise a playful one.</summary>
        public static string Reason(Store s, TaskItem win, TaskItem other, DateTime now)
        {
            if (win.Due is DateTime wd && (other.Due is not DateTime od || wd.Date < od.Date)) return "it is due sooner, so it gets it off your back";
            if (win.Size < other.Size) return "it is the smaller one, a quick win to warm up on";
            if (win.Priority > 0 && (other.Priority == 0 || win.Priority < other.Priority) && win.List == other.List) return "it sits higher on its list";
            string line = Content.Pick(s, "stuck.coin");
            return line.Length > 0 ? line.TrimEnd('.') : "the coin has spoken";
        }

        // ---- Unstick questions ----

        private void UnstickQuestions()
        {
            var now = DateTime.Now;
            var pool = FunKit.Candidates(S, now);
            if (_w.ChosenTask is TaskItem c && FunKit.Open(c) && !pool.Contains(c)) pool.Insert(0, c);
            var task = FunKit.PickTask(_w, "Unstick questions", "Which task feels stuck?", pool, _w.ChosenTask);
            if (task == null) { if (pool.Count == 0) _w.Say("There is no open task to get unstuck on. That is a nice problem to have.", true); return; }
            var answers = AskThree(task);
            if (answers == null) return;
            var (unclear, big, scary) = answers.Value;
            string fix = Fix(unclear, big, scary);
            string say = fix switch
            {
                "step" => "It sounds unclear. One fix: add a first step called Find out exactly what is being asked.",
                "makeover" => "It sounds too big. One fix: a task makeover, so it is named as a small first step.",
                _ => scary ? "It sounds boring or scary. One fix: a two-minute dare. Just two minutes, then you may stop." : "Nothing big in the way, so it may only need a start. One fix: a two-minute dare.",
            };
            int go = FunKit.Pick(_w, "Unstick questions", say, new[] { "Try it now", "Not now" });
            if (go != 0) { _w.Say("Okay. The fix will be here when you want it.", true); return; }
            if (fix == "step")
            {
                S.Remember("adding a first step to " + task.Name);
                task.Subtasks.Insert(0, new Subtask { Name = "Find out exactly what is being asked" });
                task.TouchedAt = now;
                _w.SaveAndRefresh();
                _w.Say("Added a first step to " + task.Name + ": Find out exactly what is being asked. Control Z undoes it.", true);
            }
            else if (fix == "makeover") _ = DoMakeover(task);
            else
            {
                _w.SelectTask(task);
                if (FunTimer.Handle(_w, Dare)) return;
                FunTimer.Start(_w, Dare, "Two-minute dare", 2, task, false, (t, _) => ChallengeEnded(Dare, t.Task));
                _w.Say("Two-minute dare on " + task.Name + ". Two minutes, that is all. I will tell you when the time is up.", true);
            }
        }

        /// <summary>The one fix: unclear first, then too big, then a dare for boring, scary or nothing in particular.</summary>
        public static string Fix(bool unclear, bool big, bool scary) => unclear ? "step" : big ? "makeover" : "dare";

        private (bool Unclear, bool Big, bool Scary)? AskThree(TaskItem task)
        {
            var back = _w.KeepFocus();
            var win = Layout.Dialog(_w, "Unstick questions");
            ComboBox Q() => new() { ItemsSource = new[] { "No", "Yes", "Not sure" }, SelectedIndex = 0 };
            var q1 = Q(); var q2 = Q(); var q3 = Q();
            bool ok = false;
            var okb = Layout.Button("_OK", isDefault: true);
            okb.Click += (_, _) => { ok = true; win.Close(); };
            var cancel = Layout.Button("Cancel", isCancel: true);
            cancel.Click += (_, _) => win.Close();
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "Three quick questions about " + task.Name + ".", TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
            panel.Children.Add(Layout.Row("_1. Is it unclear what to do?", q1));
            panel.Children.Add(Layout.Row("_2. Does it feel too big?", q2));
            panel.Children.Add(Layout.Row("_3. Is it boring or scary?", q3));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(okb);
            row.Children.Add(cancel);
            panel.Children.Add(row);
            win.Content = panel;
            win.Loaded += (_, _) => q1.Focus();
            win.ShowDialog();
            back();
            if (!ok) return null;
            // Not sure counts as yes for unclear (finding out is the fix), and as no for the others.
            return (q1.SelectedIndex >= 1, q2.SelectedIndex == 1, q3.SelectedIndex == 1);
        }

        // ---- Task makeover ----

        private async System.Threading.Tasks.Task DoMakeover(TaskItem? given)
        {
            try
            {
                var now = DateTime.Now;
                var task = given;
                if (task == null)
                {
                    var pool = FunKit.Candidates(S, now);
                    if (_w.ChosenTask is TaskItem c && FunKit.Open(c) && !pool.Contains(c)) pool.Insert(0, c);
                    task = FunKit.PickTask(_w, "Task makeover", "Which task gets a makeover?", pool, _w.ChosenTask);
                    if (task == null) { if (pool.Count == 0) _w.Say("There is no open task to make over.", true); return; }
                }
                if (Original(task) is string old)
                {
                    int i = FunKit.Pick(_w, "Task makeover", task.Name + " was made over before. Its old name was " + old + ".", new[] { "Make it over again", "Put back the old name" });
                    if (i < 0) return;
                    if (i == 1)
                    {
                        S.Remember("putting back the name of " + task.Name);
                        PutBack(task);
                        _w.SaveAndRefresh();
                        _w.Say("Back to " + task.Name + ". Control Z undoes it.", true);
                        return;
                    }
                }
                string idea = Content.Pick(S, "stuck.makeover").Replace("{task}", task.Name);
                if (BunnyWrite.Visible(S).Contains(task))
                {
                    string prompt = "Rewrite this to-do item as a friendly, tiny first step of at most 10 words. Reply with only the new name. Task: " + task.Name;
                    string written = await BunnyWrite.Short(S, Makeover, prompt, "stuck.makeover");
                    if (written.Length > 0 && written.Length < 120) idea = written.Replace("{task}", task.Name).Trim().Trim('"');
                }
                string? name = FunKit.Ask(_w, "Task makeover", "_New name", idea, "A friendly first step. Change it any way you like. The old name goes in the task's notes.");
                if (name == null || name == task.Name) return;
                S.Remember("making over " + task.Name);
                Rename(task, name);
                _w.SaveAndRefresh();
                _w.Say("Renamed to " + name + ". The old name is in its notes, and Control Z undoes this.", true);
            }
            catch (Exception e) { ErrorLog.Write("Task makeover failed: " + e.GetType().Name); }
        }

        public static string? Original(TaskItem t)
        {
            var line = t.Notes.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith(OriginalPrefix));
            return line == null ? null : line[OriginalPrefix.Length..];
        }

        /// <summary>Renames the task, keeping its first name in the notes (a second makeover keeps the very first name).</summary>
        public static void Rename(TaskItem t, string name)
        {
            if (Original(t) == null) t.Notes = OriginalPrefix + t.Name + (t.Notes.Length > 0 ? "\n" + t.Notes : "");
            t.Name = name;
            t.TouchedAt = DateTime.Now;
        }

        public static void PutBack(TaskItem t)
        {
            if (Original(t) is not string old) return;
            t.Name = old;
            t.Notes = string.Join("\n", t.Notes.Split('\n').Where(l => !l.Trim().StartsWith(OriginalPrefix))).Trim();
            t.TouchedAt = DateTime.Now;
        }

        // ---- Pick from three ----

        private void PickFromThree()
        {
            var now = DateTime.Now;
            var three = FunKit.Candidates(S, now).Take(3).ToList();
            if (three.Count == 0) { _w.Say("Nothing open to pick from. Enjoy the quiet.", true); return; }
            var t = FunKit.PickTask(_w, "Pick from three", three.Count == 3 ? "Just these three. Pick one, and the rest can wait." : "Only " + three.Count + " to choose from today.", three);
            if (t == null) return;
            _w.SelectTask(t);
            _w.Say("Good pick: " + t.Name + ". Everything else can wait.", true);
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("stuck: " + what); }
            FunKit.SelfTest(store);
            var t = new TaskItem { Name = "Read chapter 9", List = store.Data.Lists[0].Name, Notes = "pages 200 to 230" };
            store.Data.Tasks.Add(t);
            store.Remember("makeover test");
            Rename(t, "Open the book to chapter 9");
            Rename(t, "Read one page");
            Need(t.Name == "Read one page" && Original(t) == "Read chapter 9" && t.Notes.EndsWith("pages 200 to 230"), "a makeover lost the original name or notes");
            store.Undo();
            var back = store.Data.Tasks.First(x => x.Id == t.Id);
            Need(back.Name == "Read chapter 9" && Original(back) == null, "Control Z did not undo a makeover");
            Rename(back, "Tiny step");
            PutBack(back);
            Need(back.Name == "Read chapter 9" && back.Notes == "pages 200 to 230", "putting back the old name");
            Need(Fix(true, true, true) == "step" && Fix(false, true, true) == "makeover" && Fix(false, false, true) == "dare", "unstick fix order");
            var now = DateTime.Now;
            var a = new TaskItem { Name = "a", Due = now.AddDays(1), Size = Size.Large };
            var b = new TaskItem { Name = "b", Due = now.AddDays(3), Size = Size.Small };
            Need(Reason(store, a, b, now).Contains("due sooner") && Reason(store, b, a, now).Contains("smaller"), "coin flip reasons");
            Need(IsWriting(new TaskItem { Name = "Write the lab report" }) && !IsWriting(new TaskItem { Name = "Buy milk" }), "writing tasks");
            Need(FunTimer.Counted(120, 2) == 2 && FunTimer.Counted(60, 10) == 1, "awake minutes of a timer");
            foreach (var topic in new[] { "stuck.win", "stuck.futureyou", "stuck.pep.kind", "stuck.pep.cheeky", "stuck.coin", "stuck.makeover" })
                Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            Need(Content.Lines("stuck.makeover").All(l => l.Contains("{task}")), "a makeover idea without the task");
            foreach (var id in new[] { Dare, FirstLine, Draft, FutureYou, Pep, Coin, Unstick, Makeover, Three })
                Need(Tools.Find(id) != null, id + " is not in the toolbox");
            store.Data.Log.Add(new LogEntry { When = now.AddDays(-2), Kind = "DidAThing", Text = "called the bank" });
            Need(PastWins(store, now).Any(e => e.Text == "called the bank"), "future-you did not find a past win");
        }
    }
}
