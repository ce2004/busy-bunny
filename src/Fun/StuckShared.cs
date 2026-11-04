using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>
    /// Shared helpers for the Stage 5 toolbox tools (I am stuck, Get going, Rest and rough days, Skills): pick menus,
    /// a text box, task picks, capped small wins through the ledger, and the energy of the day. Every window here
    /// is a standard list or text box, Escape closes it, and focus goes back where she was.
    /// </summary>
    public static class FunKit
    {
        public static readonly Random Rng = new();

        // ---- Windows ----

        /// <summary>A pick menu: intro is read as the list's name, Enter picks. Returns the index, or -1 for Escape.</summary>
        public static int Pick(MainWindow w, string title, string intro, IReadOnlyList<string> options, int start = 0, string ok = "_OK")
        {
            if (options.Count == 0) return -1;
            var back = w.KeepFocus();
            var win = Layout.Dialog(w, title);
            var list = TextViewer.LinesList(options, intro, Math.Min(330, 46 + options.Count * 36));
            list.Width = 620;
            int result = -1;
            void Choose() { if (list.SelectedIndex >= 0) { result = list.SelectedIndex; win.Close(); } }
            list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Choose(); } };
            var okb = Layout.Button(ok, isDefault: true);
            okb.Click += (_, _) => Choose();
            var cancel = Layout.Button("Cancel", isCancel: true);
            cancel.Click += (_, _) => win.Close();
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, MaxWidth = 620, Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(list);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(okb);
            row.Children.Add(cancel);
            panel.Children.Add(row);
            win.Content = panel;
            win.Loaded += (_, _) => TextViewer.Select(list, Math.Clamp(start, 0, options.Count - 1));
            win.ShowDialog();
            back();
            return result;
        }

        /// <summary>Picks a task from a list of tasks, each read as its short line. Null for Escape or no tasks.</summary>
        public static TaskItem? PickTask(MainWindow w, string title, string intro, IReadOnlyList<TaskItem> tasks, TaskItem? start = null)
        {
            if (tasks.Count == 0) return null;
            var now = DateTime.Now;
            int at = start == null ? 0 : Math.Max(0, tasks.ToList().IndexOf(start));
            int i = Pick(w, title, intro, tasks.Select(t => w.Store.Line(t, now, true)).ToList(), at);
            return i < 0 ? null : tasks[i];
        }

        /// <summary>A text box with OK and Cancel. Null for Cancel or nothing typed.</summary>
        public static string? Ask(MainWindow w, string title, string label, string initial = "", string help = "")
        {
            var back = w.KeepFocus();
            var win = Layout.Dialog(w, title);
            var box = new TextBox { Text = initial, TextWrapping = TextWrapping.Wrap, AcceptsReturn = false };
            if (help.Length > 0) AutomationProperties.SetHelpText(box, help);
            string? result = null;
            var ok = Layout.Button("_OK", isDefault: true);
            ok.Click += (_, _) => { result = box.Text.Trim(); win.Close(); };
            var cancel = Layout.Button("Cancel", isCancel: true);
            cancel.Click += (_, _) => win.Close();
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Layout.Row(label, box, 520));
            if (help.Length > 0) panel.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 });
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            panel.Children.Add(row);
            win.Content = panel;
            win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            win.ShowDialog();
            back();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }

        /// <summary>Lines to read in a list, with focus back afterwards.</summary>
        public static void Read(MainWindow w, string title, IReadOnlyList<string> lines)
        {
            var back = w.KeepFocus();
            TextViewer.Show(w, title, lines);
            back();
        }

        // ---- Tasks ----

        public static bool Open(TaskItem t) => t.Status is not (Status.Done or Status.DoneNotSubmitted or Status.Waiting);

        /// <summary>Tasks to choose from: today's open ones first, then the rest of this week, at most 40.</summary>
        public static List<TaskItem> Candidates(Store s, DateTime now)
        {
            var today = s.TodayAll(now).Where(Open);
            var week = s.Data.Tasks.Where(t => t.Status == Status.ThisWeek && s.CanShowToday(t, now) && !s.IsArchived(t)).OrderBy(s.Rank);
            return today.Concat(week).Distinct().Take(40).ToList();
        }

        /// <summary>The chosen task if it is still open, otherwise the first of Candidates.</summary>
        public static TaskItem? ChosenOrTop(MainWindow w, DateTime now) =>
            w.ChosenTask is TaskItem c && Open(c) ? c : Candidates(w.Store, now).FirstOrDefault();

        public static TaskItem? Find(Store s, Guid? id) => id is Guid g ? s.Data.Tasks.FirstOrDefault(t => t.Id == g) : null;

        public static string Plural(int n, string one, string many) => n + " " + (n == 1 ? one : many);

        // ---- Points ----

        /// <summary>How many payments this feature made today.</summary>
        public static int PaidToday(Store s, string feature, DateTime now) =>
            s.Data.Log.Count(e => e.Kind == Ledger.FunKind && e.Feature == feature && e.When.Date == now.Date);

        /// <summary>
        /// A small win that pays at most cap times a day, so nothing pays for doing the same thing over and over. Its own
        /// Control Z step. Returns what was paid (null when the cap was reached). Does not speak.
        /// </summary>
        public static Outcome? WinPoints(Store s, string feature, int points, string reason, int cap, DateTime now)
        {
            if (PaidToday(s, feature, now) >= cap) return null;
            var o = Ledger.Pay(s, points, reason, feature, remember: true);
            s.Save();
            if (o != null) foreach (var said in o.Said) Game.Note(s, said);
            return o;
        }

        /// <summary>Pays a capped win and says lead first, then the points.</summary>
        public static void Win(MainWindow w, string feature, int points, string reason, int cap, string lead)
        {
            var o = WinPoints(w.Store, feature, points, reason, cap, DateTime.Now);
            string tail = o == null ? " It counts. The points for this one are used up for today, so this one is just for you."
                : " " + Plural(o.Gained, "point", "points") + " for that. " + PlayTime.SpendLine(w.Store.Data) + ".";
            w.Say(lead + tail, true);
        }

        // ---- Energy ----

        /// <summary>Today's energy: low (low-energy mode or the morning check-in), medium, high, or empty when not set.</summary>
        public static string Energy(Store s, Settings? cfg, DateTime now)
        {
            if (cfg?.LowEnergy == true) return "low";
            return s.Data.PeekDay(now).Energy.ToLowerInvariant();
        }

        /// <summary>Sprint length by energy (sweep 8.4): 10 minutes on low energy, 25 on high, otherwise 15.</summary>
        public static int SprintLength(string energy) => energy == "low" ? 10 : energy == "high" ? 25 : 15;

        /// <summary>The lists Morgan can pick from: open, shown on Today.</summary>
        public static List<TaskList> TodayLists(Store s) => s.ActiveLists.Where(l => l.InToday).ToList();

        // ---- Shared tests ----

        public static void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("fun kit: " + what); }
            var now = DateTime.Now;
            int Paid() => s.Data.Log.Where(e => e.Feature == "test.kit").Sum(e => e.Points);
            for (int i = 0; i < 5; i++) WinPoints(s, "test.kit", 2, "test win", 3, now);
            Need(Paid() == 6, "a capped win paid more than 3 times");
            s.Undo();
            Need(Paid() == 4, "Control Z did not take back the last win");
            Need(SprintLength("low") == 10 && SprintLength("high") == 25 && SprintLength("") == 15, "sprint length by energy");
        }
    }

    /// <summary>
    /// One toolbox timer at a time (the two-minute dare, the urge timer, a dance break and the rest). It counts only time
    /// the laptop was awake. A timer marked as a sprint logs its minutes as a sprint and raises the sprint events, so
    /// time per class counts it. While it runs, lines nobody asked for wait, as in a sprint.
    /// </summary>
    public sealed class FunTimer
    {
        public static FunTimer? Current { get; private set; }
        private static readonly DispatcherTimer Clock = new() { Interval = TimeSpan.FromSeconds(1) };
        private static bool _hooked;

        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public int Minutes { get; init; }
        public bool Sprint { get; init; }
        public TaskItem? Task { get; init; }
        /// <summary>A count the tool keeps while it runs, like small tasks cleared.</summary>
        public int Count { get; set; }
        private DateTime _endUtc, _lastUtc;
        private double _awake;
        private Action<FunTimer, int>? _done;
        private Action? _stopped;
        private MainWindow _w = null!;

        public TimeSpan Left => _endUtc - DateTime.UtcNow;
        public string LeftWords => Left.TotalSeconds < 60 ? "less than a minute" : FunKit.Plural((int)Math.Ceiling(Left.TotalMinutes), "minute", "minutes");

        public static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            Clock.Tick += (_, _) => Current?.Tick();
            Calm.FocusTimers.Add(() => Current != null);
        }

        /// <summary>
        /// Call first in a tool that starts a timer. True when it handled the moment: a sprint is running (wait for it),
        /// this tool's timer is running (time left, with a choice to stop it), or another timer is (said, with its name).
        /// </summary>
        public static bool Handle(MainWindow w, string id)
        {
            if (FocusTools.SprintRunning) { w.Say("A sprint is running. Control Shift T tells you the time left. Try this once it is over.", true); return true; }
            if (Current is not FunTimer t) return false;
            if (t.Id != id) { w.Say(t.Name + " is still running, " + t.LeftWords + " left. Open it again from the toolbox to stop it.", true); return true; }
            int i = FunKit.Pick(w, t.Name, t.Name + ": " + t.LeftWords + " left.", new[] { "Keep it going", "Stop it now" });
            if (i == 1 && Current == t)
            {
                Stop();
                try { t._stopped?.Invoke(); }
                catch (Exception e) { ErrorLog.Write("The timer " + t.Id + " failed while stopping: " + e); }
                w.Say(t.Name + " stopped. No harm done.", true);
            }
            return true;
        }

        /// <summary>Starts a timer. stopped runs when she stops it early with Stop it now (end a break, stop a sound), never at the normal end.</summary>
        public static FunTimer Start(MainWindow w, string id, string name, int minutes, TaskItem? task, bool sprint, Action<FunTimer, int> done, Action? stopped = null)
        {
            Hook();
            Stop();
            var t = new FunTimer { Id = id, Name = name, Minutes = minutes, Task = task, Sprint = sprint };
            t._w = w;
            t._done = done;
            t._stopped = stopped;
            t._endUtc = DateTime.UtcNow.AddMinutes(minutes);
            t._lastUtc = DateTime.UtcNow;
            Current = t;
            Clock.Start();
            if (sprint)
            {
                GameEvents.RaiseSprintStarted(minutes, task);
                if (task != null) GameEvents.RaiseTaskStarted(task);
            }
            return t;
        }

        public static void Stop()
        {
            Clock.Stop();
            Current = null;
        }

        private void Tick()
        {
            var now = DateTime.UtcNow;
            double gap = (now - _lastUtc).TotalSeconds;
            _lastUtc = now;
            if (gap > 0) _awake += Math.Min(gap, 5);
            if (now >= _endUtc) Finish();
        }

        private void Finish()
        {
            Stop();
            int minutes = Counted(_awake, Minutes);
            if (Sprint && minutes > 0)
            {
                var s = _w.Store;
                s.Remember("finishing a sprint");
                s.Data.Log.Add(new LogEntry
                {
                    When = DateTime.Now, Kind = "Sprint", Minutes = minutes, Text = Name + ", " + minutes + " minutes" + (Task != null ? " on " + Task.Name : ""),
                    Tag = Task?.Tag ?? "", List = Task?.List ?? "", TaskId = Task?.Id,
                });
                _w.SaveAndRefresh();
                GameEvents.RaiseSprintDone(minutes);
            }
            Sounds.Play(Sounds.Event.SprintFinished);
            try { _done?.Invoke(this, minutes); }
            catch (Exception e) { ErrorLog.Write("The timer " + Id + " failed at the end: " + e); }
        }

        /// <summary>Minutes that count: all of them, unless the laptop slept for more than a minute of it.</summary>
        public static int Counted(double awakeSeconds, int minutes)
        {
            int awake = Math.Clamp((int)Math.Round(awakeSeconds / 60), 0, minutes);
            return awake < minutes - 1 ? awake : minutes;
        }
    }
}
