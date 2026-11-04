using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>Her choices for the Plan, Notes to self and automatic tools, from Options, Settings, Plans and little extras.</summary>
    public sealed class PlanPrefs
    {
        /// <summary>Her motto, read back now and then. Empty until she types one.</summary>
        public string Motto { get; set; } = "";
        /// <summary>What Bunny calls her in thank-you notes. Empty means no name at all.</summary>
        public string YourName { get; set; } = "";
        /// <summary>Missed days forgiven each week in the streaks these tools keep.</summary>
        public int GraceDays { get; set; } = 1;
        /// <summary>How did that feel: false asks once a day, true after every task.</summary>
        public bool FeelEveryTask { get; set; }
        /// <summary>Let Bunny write first steps (uses Bunny's daily limit). Off by default.</summary>
        public bool BunnyFirstSteps { get; set; }
    }

    /// <summary>
    /// Shared helpers for the Stage 5 tools (Plan, Notes to self, automatic): small dialogs, task pools, the one morning
    /// line, grace-day streaks, the rising tone ladder and sounds played a moment after the app's own.
    /// </summary>
    public static class PlanKit
    {
        public const string PrefsId = "plan.prefs";
        public static PlanPrefs Prefs(Store s) => s.World.Get<PlanPrefs>(PrefsId);

        // ---- Dialogs (standard controls, named, Escape closes, focus goes back) ----

        /// <summary>A small form: an optional intro, the rows, OK and Cancel. True on OK once validate says nothing is wrong.</summary>
        public static bool Form(MainWindow w, string title, string? intro, IList<FrameworkElement> rows, string ok = "OK", Func<string?>? validate = null)
        {
            var back = w.KeepFocus();
            var dlg = Layout.Dialog(w, title);
            var panel = new StackPanel { Margin = new Thickness(14), MinWidth = 440 };
            if (!string.IsNullOrEmpty(intro)) panel.Children.Add(new TextBlock { Text = intro, TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 8) });
            foreach (var r in rows) panel.Children.Add(r);
            var okB = Layout.Button(ok, isDefault: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(okB);
            buttons.Children.Add(Layout.Button("Cancel", isCancel: true));
            panel.Children.Add(buttons);
            dlg.Content = panel;
            var first = rows.Select(FirstControl).FirstOrDefault(c => c != null);
            if (first != null && !string.IsNullOrEmpty(intro)) AutomationProperties.SetHelpText(first, intro);
            okB.Click += (_, _) =>
            {
                if (validate?.Invoke() is string problem) { Speech.Say(dlg, problem, true); return; }
                dlg.DialogResult = true;
            };
            dlg.Loaded += (_, _) => { if (first != null) first.Focus(); else okB.Focus(); };
            bool done = dlg.ShowDialog() == true;
            back();
            return done;
        }

        private static Control? FirstControl(FrameworkElement e) => e switch
        {
            Label => null,
            Control c => c,
            Panel p => p.Children.OfType<FrameworkElement>().Select(FirstControl).FirstOrDefault(c => c != null),
            _ => null,
        };

        /// <summary>A text box row.</summary>
        public static (FrameworkElement Row, TextBox Box) TextRow(string label, string text = "", bool lines = false, double width = 440)
        {
            var box = new TextBox { Text = text };
            if (lines) { box.AcceptsReturn = true; box.TextWrapping = TextWrapping.Wrap; box.Height = 110; box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto; }
            return (Layout.Row(label, box, width), box);
        }

        /// <summary>A pick menu row.</summary>
        public static (FrameworkElement Row, ComboBox Box) PickRow(string label, IEnumerable<string> options, int selected = 0, double width = 440)
        {
            var list = options.ToList();
            var box = new ComboBox { ItemsSource = list, SelectedIndex = list.Count == 0 ? -1 : Math.Clamp(selected, 0, list.Count - 1) };
            return (Layout.Row(label, box, width), box);
        }

        /// <summary>Asks for a line (or lines) of text. Null when cancelled.</summary>
        public static string? AskText(MainWindow w, string title, string label, string text = "", string? intro = null, bool lines = false, bool allowEmpty = false)
        {
            var (row, box) = TextRow(label, text, lines);
            box.Loaded += (_, _) => box.SelectAll();
            return Form(w, title, intro, new[] { row }, validate: () => allowEmpty || box.Text.Trim().Length > 0 ? null : "Type something first, or press Escape.") ? box.Text.Trim() : null;
        }

        /// <summary>One choice from a pick menu: its index, or -1 when cancelled.</summary>
        public static int Pick(MainWindow w, string title, string label, IList<string> options, int selected = 0, string? intro = null)
        {
            if (options.Count == 0) return -1;
            var (row, box) = PickRow(label, options, selected);
            return Form(w, title, intro, new[] { row }) ? Math.Max(0, box.SelectedIndex) : -1;
        }

        /// <summary>Open tasks she can see in the app: Today first, then the rest by status and due date.</summary>
        public static List<TaskItem> OpenTasks(Store s, DateTime now)
        {
            var today = s.TodayAll(now).Where(t => !Finished(t)).ToList();
            var rest = s.Data.Tasks.Where(t => !Finished(t) && !s.ListOf(t).Archived && !today.Contains(t))
                .OrderBy(t => t.Status).ThenBy(t => t.Due ?? DateTime.MaxValue).ThenBy(s.Rank);
            return today.Concat(rest).ToList();
        }

        public static bool Finished(TaskItem t) => t.Status is Status.Done or Status.DoneNotSubmitted;

        /// <summary>Picks a task from a pool, starting on the chosen one. Null when cancelled or the pool is empty (it says so).</summary>
        public static TaskItem? PickTask(MainWindow w, string title, string label, List<TaskItem> pool, string? intro = null, string empty = "There are no open tasks to pick from.")
        {
            if (pool.Count == 0) { w.Say(empty, true); return null; }
            var now = DateTime.Now;
            int at = w.ChosenTask is TaskItem c ? Math.Max(0, pool.IndexOf(c)) : 0;
            int i = Pick(w, title, label, pool.Select(t => w.Store.Line(t, now, true)).ToList(), at, intro);
            return i < 0 ? null : pool[i];
        }

        /// <summary>Several picks from a list: Space picks or unpicks a line. Null when cancelled.</summary>
        public static List<int>? PickMany(MainWindow w, string title, string label, IList<string> lines, IEnumerable<int> picked, string? intro = null, int max = 0)
        {
            var list = new ListBox { SelectionMode = SelectionMode.Multiple, Height = 260 };
            foreach (var l in lines)
            {
                var item = new ListBoxItem { Content = l };
                AutomationProperties.SetName(item, l);
                list.Items.Add(item);
            }
            foreach (int i in picked) if (i >= 0 && i < list.Items.Count) ((ListBoxItem)list.Items[i]).IsSelected = true;
            AutomationProperties.SetHelpText(list, "Space picks or unpicks the line you are on.");
            list.Loaded += (_, _) => TextViewer.Select(list, Math.Max(0, picked.DefaultIfEmpty(0).First()));
            var row = Layout.Row(label, list, 560);
            if (!Form(w, title, intro, new[] { row }, validate: () => max > 0 && list.SelectedItems.Count > max ? "Pick " + max + " at most." : null)) return null;
            return Enumerable.Range(0, list.Items.Count).Where(i => ((ListBoxItem)list.Items[i]).IsSelected).ToList();
        }

        /// <summary>Lines to read in the text viewer, focus back after.</summary>
        public static void Show(MainWindow w, string title, IReadOnlyList<string> lines)
        {
            var back = w.KeepFocus();
            TextViewer.Show(w, title, lines);
            back();
        }

        /// <summary>Saves the tasks and Bunny's world together.</summary>
        public static void SaveAll(Store s) { s.Save(); s.World.Save(); }

        /// <summary>Remember (one Control Z step), change a bag, save both files.</summary>
        public static void Change<T>(Store s, string id, string what, Action<T> change) where T : new() => World.Change(s, id, what, change);

        /// <summary>A line from the library with {task} and other words filled in.</summary>
        public static string Line(Store s, string topic, params (string Key, string Value)[] fill)
        {
            string line = Content.Pick(s, topic);
            foreach (var (k, v) in fill) line = line.Replace("{" + k + "}", v);
            return line;
        }

        public static string Plural(int n, string one, string many) => n + " " + (n == 1 ? one : many);

        // ---- Streaks with grace days (default 1 a week) ----

        /// <summary>
        /// Days in a row with something done, counting back from today (today does not break it, the day is not over).
        /// Up to gracePerWeek missed days in each Monday to Sunday week are forgiven: they keep it going but do not add to it.
        /// </summary>
        public static (int Days, int GraceUsed) GraceStreak(ICollection<DateTime> done, DateTime today, int gracePerWeek)
        {
            var days = new HashSet<DateTime>(done.Select(d => d.Date));
            if (days.Count == 0) return (0, 0);
            var first = days.Min();
            int count = 0, used = 0;
            var perWeek = new Dictionary<DateTime, int>();
            var d0 = today.Date;
            if (!days.Contains(d0)) d0 = d0.AddDays(-1);
            for (var d = d0; d >= first; d = d.AddDays(-1))
            {
                if (days.Contains(d)) { count++; continue; }
                var week = Engine.WeekStart(d);
                perWeek.TryGetValue(week, out int u);
                if (u >= gracePerWeek) break;
                perWeek[week] = u + 1;
                used++;
            }
            return (count, count == 0 ? 0 : used);
        }

        /// <summary>The grace days a week she chose (0 to 3).</summary>
        public static int Grace(Store s) => Math.Clamp(Prefs(s).GraceDays, 0, 3);

        // ---- Sounds: a rising ladder of tones, and playing after the app's own sound ----

        /// <summary>Built-in sounds from low to high, for rungs and climbing class tones.</summary>
        public static readonly string[] Ladder =
        {
            "Cello, single", "Piano, chord low", "Piano, single", "Piano, chord high", "Piano, four up",
            "Flute, single", "Flute, chord high", "Singing bells, four up", "Singing bells, sparkle",
        };

        public static string RungId(int step) => "plan.rung" + Math.Clamp(step, 0, Ladder.Length - 1);

        /// <summary>Plays a sound bank id a moment later, so it follows the task done sound instead of replacing it.</summary>
        public static void PlayLater(string id, int ms = 700)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            timer.Tick += (_, _) => { timer.Stop(); SoundBank.Play(id); };
            timer.Start();
        }

        // ---- The morning line: every tool's start-of-day news in one unprompted line ----

        private static readonly List<(string ToolId, Func<Store, DateTime, string?> Part)> _morning = new();

        /// <summary>Adds a part to the one line said at the start of the day (only while its tool is on).</summary>
        public static void Morning(string toolId, Func<Store, DateTime, string?> part)
        {
            _morning.RemoveAll(m => m.ToolId == toolId);
            _morning.Add((toolId, part));
        }

        public sealed class MorningNote { public string Day { get; set; } = ""; public string Text { get; set; } = ""; public bool Pending { get; set; } }
        public const string MorningId = "plan.morning";

        /// <summary>Builds today's morning line from every part that has news. Parts may change their own bags.</summary>
        public static string BuildMorning(Store s, DateTime now)
        {
            var parts = new List<string>();
            foreach (var (id, part) in _morning.ToList())
            {
                if (!Tools.IsOn(id)) continue;
                try { if (part(s, now) is string p && p.Trim().Length > 0) parts.Add(p.Trim()); }
                catch (Exception e) { ErrorLog.Write("Morning part " + id + " failed: " + e.Message); }
            }
            return string.Join(" ", parts);
        }

        /// <summary>Says the waiting morning line once the main window is free and the calm rules allow; otherwise keeps it.</summary>
        public static void TrySayMorning(MainWindow w, DateTime now)
        {
            var s = w.Store;
            var m = s.World.GetKept<MorningNote>(MorningId);
            if (!m.Pending) return;
            if (m.Day != s.Key(now) || m.Text.Length == 0) { m.Pending = false; s.World.SetKept(MorningId, m); s.World.Save(); return; }
            string? hush = Calm.Hush(s, w.AppSettings, now);
            bool waitable = hush is "quiet hours" or "class" or "a sprint";
            if (waitable && now.Hour < 21) return;
            if (hush == null && !WindowFree(w)) return;
            m.Pending = false;
            s.World.SetKept(MorningId, m);
            s.World.Save();
            Calm.SayUnprompted(w, m.Text);
        }

        private static bool WindowFree(MainWindow w) =>
            w.IsActive && !Application.Current.Windows.OfType<Window>().Any(x => x != w && x.IsVisible && x.Owner == w);
    }

    /// <summary>Ties the shared Stage 5 helpers to the app: the morning line, the tone ladder sounds and the settings page.</summary>
    public sealed class PlanKitFeature : IFeature
    {
        private MainWindow _w = null!;

        public void Attach(MainWindow w)
        {
            _w = w;
            for (int i = 0; i < PlanKit.Ladder.Length; i++) SoundBank.Register(PlanKit.RungId(i), PlanKit.Ladder[i]);
            Tools.Register(new BunnyTool("plan.morningnote", "This morning's notes", Drawers.Plan, "Hear again what the Plan and Notes tools said this morning.", Hear) { Since = "0.14.0" });
            GameEvents.DayStarted += day =>
            {
                var now = DateTime.Now;
                var s = w.Store;
                string text = PlanKit.BuildMorning(s, now);
                s.World.SetKept(PlanKit.MorningId, new PlanKit.MorningNote { Day = s.Key(now), Text = text, Pending = text.Length > 0 && Tools.IsOn("plan.morningnote") });
                PlanKit.SaveAll(s);
                w.Dispatcher.BeginInvoke(() => PlanKit.TrySayMorning(w, DateTime.Now), DispatcherPriority.ApplicationIdle);
            };
            Ticker.Every("plan.morning", _ => true, now => PlanKit.TrySayMorning(w, now));
        }

        private void Hear()
        {
            var m = _w.Store.World.GetKept<PlanKit.MorningNote>(PlanKit.MorningId);
            _w.Say(m.Day == _w.Store.Key(DateTime.Now) && m.Text.Length > 0 ? m.Text : "No morning notes today. The Plan and Notes tools had nothing new to say.", true);
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) { yield return new PrefsPage(store); }

        private sealed class PrefsPage : ISettingsPage
        {
            private readonly Store _store;
            private readonly TextBox _motto = new(), _name = new();
            private readonly ComboBox _grace = new() { ItemsSource = new[] { "None", "1 a week", "2 a week", "3 a week" } };
            private readonly ComboBox _feel = new() { ItemsSource = new[] { "Once a day", "After every task" } };
            private readonly CheckBox _bunny = new() { Content = "Let Bunny write _first steps for new tasks (uses Bunny's daily limit; private lists never)" };
            private readonly StackPanel _panel = new() { Margin = new Thickness(8) };

            public PrefsPage(Store store)
            {
                _store = store;
                var p = PlanKit.Prefs(store);
                _motto.Text = p.Motto;
                _name.Text = p.YourName;
                _grace.SelectedIndex = Math.Clamp(p.GraceDays, 0, 3);
                _feel.SelectedIndex = p.FeelEveryTask ? 1 : 0;
                _bunny.IsChecked = p.BunnyFirstSteps;
                AutomationProperties.SetName(_bunny, "Let Bunny write first steps for new tasks, uses Bunny's daily limit, private lists never");
                _panel.Children.Add(Layout.Row("Your _motto, read back now and then (leave empty for none)", _motto, 420));
                _panel.Children.Add(Layout.Row("_Your name for thank-you notes (leave empty for none)", _name, 300));
                _panel.Children.Add(Layout.Row("_Grace days in streaks", _grace, 200));
                _panel.Children.Add(Layout.Row("How did that feel: _ask", _feel, 200));
                _panel.Children.Add(_bunny);
            }

            public string Name => "Plans and little extras";
            public FrameworkElement Content => _panel;

            public void Apply()
            {
                var p = PlanKit.Prefs(_store);
                p.Motto = _motto.Text.Trim();
                p.YourName = _name.Text.Trim();
                p.GraceDays = Math.Max(0, _grace.SelectedIndex);
                p.FeelEveryTask = _feel.SelectedIndex == 1;
                p.BunnyFirstSteps = _bunny.IsChecked == true;
                _store.World.Set(PlanKit.PrefsId, p);
                _store.World.Save();
            }
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("plan kit: " + what); }
            var mon = new DateTime(2026, 11, 2);
            // Streak math: Mon to Thu done, Fri missed, Sat and Sun done: one grace day keeps it at 6.
            var done = new List<DateTime> { mon, mon.AddDays(1), mon.AddDays(2), mon.AddDays(3), mon.AddDays(5), mon.AddDays(6) };
            Need(PlanKit.GraceStreak(done, mon.AddDays(6), 1) == (6, 1), "one grace day did not bridge one miss");
            Need(PlanKit.GraceStreak(done, mon.AddDays(6), 0).Days == 2, "no grace days still bridged a miss");
            // Two misses in one week with one grace day: broken at the second.
            var two = new List<DateTime> { mon, mon.AddDays(3), mon.AddDays(4) };
            Need(PlanKit.GraceStreak(two, mon.AddDays(4), 1).Days == 2, "two misses in a week were both forgiven");
            // Today not done yet does not break it.
            Need(PlanKit.GraceStreak(done, mon.AddDays(7), 1).Days == 6, "today, not over yet, broke the streak");
            Need(PlanKit.GraceStreak(new List<DateTime>(), mon, 1) == (0, 0), "empty streak");
            foreach (var name in PlanKit.Ladder) Need(Sounds.All.Contains(name), "the tone ladder names a missing sound: " + name);
            // Prefs save and load, empty motto and name by default.
            var p = PlanKit.Prefs(store);
            Need(p.Motto == "" && p.YourName == "" && p.GraceDays == 1 && !p.BunnyFirstSteps, "the defaults changed");
            p.Motto = "test";
            store.World.Set(PlanKit.PrefsId, p);
            Need(PlanKit.Prefs(store).Motto == "test", "prefs were not kept");
            store.World.Set(PlanKit.PrefsId, new PlanPrefs());
            Need(Tools.Find("plan.morningnote") != null, "morning notes are not in the toolbox");
        }
    }
}
