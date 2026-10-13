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
    /// <summary>The rules behind the focus tools, apart from the screens, so the self-test can check them.</summary>
    public static class FocusLogic
    {
        public static readonly int[] SprintMinutes = { 5, 10, 15, 25, 45 };

        /// <summary>A boss (spec 12.7): three or more subtasks on a Large or Multi-day task.</summary>
        public static bool IsBoss(TaskItem t) => t.Subtasks.Count >= 3 && t.Size is Size.Large or Size.MultiDay;

        public static int SubtasksLeft(TaskItem t) => t.Subtasks.Count(s => !s.Done);

        /// <summary>Health as a percent: the share of subtasks left.</summary>
        public static int Health(TaskItem t) => t.Subtasks.Count == 0 ? 0 : SubtasksLeft(t) * 100 / t.Subtasks.Count;

        public static bool IsQuickWin(TaskItem t) =>
            t.Status != Status.Done && (t.QuickWin || t.Size == Size.Small && t.Energy == Energy.Easy);

        /// <summary>Low-energy mode hides anything that is not small or easy, and anything brain-heavy.</summary>
        public static bool HiddenWhenLow(TaskItem t) =>
            t.Energy == Energy.BrainHeavy || !(t.Size == Size.Small || t.Energy == Energy.Easy || t.QuickWin);

        /// <summary>The same order all day, a new one each day.</summary>
        public static List<TaskItem> Shuffle(List<TaskItem> tasks, DateTime now)
        {
            long day = now.Date.Ticks / TimeSpan.TicksPerDay;
            long Key(TaskItem t)
            {
                ulong h = (ulong)(BitConverter.ToInt64(t.Id.ToByteArray(), 0) ^ BitConverter.ToInt64(t.Id.ToByteArray(), 8)) ^ ((ulong)day * 0x9E3779B97F4A7C15UL);
                h ^= h >> 31; h *= 0xBF58476D1CE4E5B9UL; h ^= h >> 29;
                return (long)(h >> 1);
            }
            return tasks.OrderBy(Key).ToList();
        }

        public static string Duration(int minutes)
        {
            int h = minutes / 60, m = minutes % 60;
            string hs = h + (h == 1 ? " hour" : " hours"), ms = m + (m == 1 ? " minute" : " minutes");
            return h == 0 ? ms : m == 0 ? hs : hs + " " + ms;
        }

        /// <summary>The start of the semester: January 1 up to July, August 1 after.</summary>
        public static DateTime SemesterStart(DateTime now) => new(now.Year, now.Month >= 8 ? 8 : 1, 1);

        public static DateTime WeekStart(DateTime now) => now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));

        /// <summary>Minutes per tag from Sprint log entries since a date, biggest first.</summary>
        public static List<(string Tag, int Minutes)> PerTag(IEnumerable<LogEntry> log, DateTime since) => log
            .Where(e => e.Kind == "Sprint" && e.When >= since)
            .GroupBy(e => e.Tag.Length == 0 ? "no class" : e.Tag)
            .Select(g => (g.Key, g.Sum(e => e.Minutes)))
            .OrderByDescending(x => x.Item2).ToList();

        public static List<string> TimePerClassLines(IEnumerable<LogEntry> log, DateTime now)
        {
            var lines = new List<string>();
            var list = log.ToList();
            void Section(string title, DateTime since)
            {
                lines.Add(title);
                var rows = PerTag(list, since);
                if (rows.Count == 0) lines.Add("No sprints yet.");
                foreach (var (tag, min) in rows) lines.Add(Duration(min) + " on " + tag);
            }
            Section("This semester", SemesterStart(now));
            Section("This week", WeekStart(now));
            if (!list.Any(e => e.Kind == "Sprint")) lines.Add("Start a sprint with Control Shift T and this fills in.");
            return lines;
        }
    }

    /// <summary>
    /// Getting started and staying on task: sprints (12.9), time per class (22.5), boss battles (12.7),
    /// task roulette (12.8), low-energy mode (12.10, 19.2), the Quick wins drawer (22.4), the overwhelm
    /// button (22.9) and shuffle mode (22.10).
    /// </summary>
    public sealed class FocusTools : IFeature
    {
        private MainWindow _w = null!;
        private Store S => _w.Store;
        private Settings Cfg => _w.AppSettings;
        private readonly Random _rng = new();

        // Sprint
        private readonly DispatcherTimer _sprintTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private DateTime _sprintEnd;
        private int _sprintMinutes;
        private TaskItem? _sprintTask;
        private bool Sprinting => _sprintTimer.IsEnabled;

        // Boss
        private readonly Dictionary<Guid, int> _bossLeft = new();
        private bool _inBoss;

        // Low energy
        private MenuItem? _lowMenu, _shuffleMenu;
        private DateTime _autoLowDay;
        private bool _announceLow;

        public void Attach(MainWindow w)
        {
            _w = w;
            w.AddMenuItem("Fun", "_Start a sprint...", StartOrCheckSprint, "Ctrl+Shift+T");
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.T, StartOrCheckSprint, "Control Shift T: start a sprint, or hear the time left");
            w.AddMenuItem("Fun", "Time per _class...", ShowTimePerClass);
            w.AddMenuItem("Fun", "_Pick one for me", Roulette, "Ctrl+Shift+U");
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.U, Roulette, "Control Shift U: pick one task for me at random from the urgent ones");
            w.AddMenuItem("Fun", "_Quick wins", QuickWins, "Ctrl+Shift+Q");
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.Q, QuickWins, "Control Shift Q: the Quick wins drawer, tiny tasks to build momentum");
            w.AddMenuItem("Fun", "Everything is too _much", Overwhelm, "Ctrl+Shift+O");
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.O, Overwhelm, "Control Shift O: everything is too much, show me one small task");
            w.AddMenuItem("Task", "Quic_k win (on or off)", ToggleQuickWin);

            _lowMenu = w.AddMenuItem("View", "_Low-energy mode", () => SetLow(!Cfg.LowEnergy, true), "Ctrl+Shift+L");
            _lowMenu.IsCheckable = true;
            _lowMenu.IsChecked = Cfg.LowEnergy;
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.L, () => SetLow(!Cfg.LowEnergy, true), "Control Shift L: low-energy mode on or off");
            _shuffleMenu = w.AddMenuItem("View", "S_huffle mode", () => SetShuffle(!Cfg.ShuffleMode), "");
            _shuffleMenu.IsCheckable = true;
            _shuffleMenu.IsChecked = Cfg.ShuffleMode;

            w.Store.HideFromToday.Add((t, _) => Cfg.LowEnergy && FocusLogic.HiddenWhenLow(t));
            w.Store.ReorderRules.Add((list, now) => Cfg.ShuffleMode ? FocusLogic.Shuffle(list, now) : list);

            _sprintTimer.Tick += (_, _) => { if (DateTime.Now >= _sprintEnd) FinishSprint(); };
            w.Closed += (_, _) => _sprintTimer.Stop();

            w.DetailParts.Add(BossText);
            w.Refreshed += CheckBosses;
            w.StatusChanged += (t, old) => { if (t.Status == Status.Done && FocusLogic.IsBoss(t)) Defeat(t); };
            w.Started += AutoLow;
            w.NewDay += _ => AutoLow();
        }

        // ---- Sprints (12.9) ----

        private void StartOrCheckSprint()
        {
            if (Sprinting)
            {
                var left = _sprintEnd - DateTime.Now;
                string t = _sprintTask != null ? " on " + _sprintTask.Name : "";
                string time = left.TotalSeconds < 60 ? "less than a minute" : (int)Math.Ceiling(left.TotalMinutes) + " minutes";
                var r = MessageBox.Show(_w, time + " left in your " + _sprintMinutes + " minute sprint" + t + ". Stop the sprint?", "Sprint", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (r == MessageBoxResult.Yes) { _sprintTimer.Stop(); _w.Say("Sprint stopped. No harm done, it just does not count.", true); }
                return;
            }
            var minutes = new ComboBox();
            foreach (int m in FocusLogic.SprintMinutes) minutes.Items.Add(m + " minutes");
            minutes.SelectedIndex = 2;
            var now = DateTime.Now;
            var (urgent, rest) = S.Today(now);
            var pool = urgent.Concat(rest).Concat(S.Data.Tasks.Where(t => t.Status == Status.ThisWeek)).Where(t => t.Status != Status.Done).Distinct().Take(40).ToList();
            var chosen = _w.ChosenTask;
            if (chosen != null && chosen.Status != Status.Done && !pool.Contains(chosen)) pool.Insert(0, chosen);
            var which = new ComboBox();
            which.Items.Add("No particular task");
            foreach (var t in pool) which.Items.Add(S.Line(t, now, true));
            which.SelectedIndex = chosen != null && pool.IndexOf(chosen) >= 0 ? pool.IndexOf(chosen) + 1 : 0;
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Layout.Row("_Minutes", minutes));
            panel.Children.Add(Layout.Row("_Task (optional)", which, 520));
            if (!Dialog(_w, "Start a sprint", panel, "Start")) return;
            _sprintMinutes = FocusLogic.SprintMinutes[Math.Max(0, minutes.SelectedIndex)];
            _sprintTask = which.SelectedIndex > 0 ? pool[which.SelectedIndex - 1] : null;
            _sprintEnd = DateTime.Now.AddMinutes(_sprintMinutes);
            _sprintTimer.Start();
            _w.Say("Sprint started: " + _sprintMinutes + " minutes" + (_sprintTask != null ? " on " + _sprintTask.Name : "") + ". Control Shift T tells you the time left.", true);
        }

        private void FinishSprint()
        {
            _sprintTimer.Stop();
            S.Remember("finishing a sprint");
            var t = _sprintTask;
            S.Data.Log.Add(new LogEntry
            {
                When = DateTime.Now, Kind = "Sprint", Minutes = _sprintMinutes,
                Text = "Sprint, " + _sprintMinutes + " minutes" + (t != null ? " on " + t.Name : ""),
                Tag = t?.Tag ?? "", List = t?.List ?? "", TaskId = t?.Id,
            });
            _w.SaveAndRefresh();
            Sounds.Play(Sounds.Event.SprintFinished);
            _w.Say("Sprint over, " + _sprintMinutes + " minutes. That counts as a win even if" + (t != null ? " " + t.Name + " is" : " the task is") + " not done. Showing up was the hard part.");
        }

        private void ShowTimePerClass()
        {
            TextViewer.Show(_w, "Time per class", FocusLogic.TimePerClassLines(S.Data.Log, DateTime.Now), l => l.StartsWith("This "));
        }

        // ---- Boss battles (12.7) ----

        private string? BossText(TaskItem t)
        {
            if (!FocusLogic.IsBoss(t)) return null;
            int left = FocusLogic.SubtasksLeft(t);
            return left == 0 ? "Boss defeated." : "Boss: " + FocusLogic.Health(t) + " percent health, " + left + " of " + t.Subtasks.Count + " subtasks left.";
        }

        private bool Defeated(TaskItem t) => S.Data.Log.Any(e => e.Kind == "Boss" && e.TaskId == t.Id);

        private void CheckBosses()
        {
            if (_inBoss) return;
            bool first = _bossLeft.Count == 0 && !_seen;
            _seen = true;
            var bosses = S.Data.Tasks.Where(FocusLogic.IsBoss).ToList();
            foreach (var t in bosses)
            {
                int left = FocusLogic.SubtasksLeft(t);
                bool known = _bossLeft.TryGetValue(t.Id, out int before);
                _bossLeft[t.Id] = left;
                if (first || !known || t.Status == Status.Done && Defeated(t)) continue;
                if (left == 0 && before > 0 || t.Status == Status.Done && before > 0) { Defeat(t); continue; }
                if (left < before)
                    _w.Say("Hit. The boss takes " + (before - left == 1 ? "a hit" : (before - left) + " hits") + " and is down to " + FocusLogic.Health(t) + " percent health.");
            }
        }

        private bool _seen;

        private void Defeat(TaskItem t)
        {
            if (Defeated(t)) return;
            _inBoss = true;
            try
            {
                int points = 10;
                var award = typeof(FocusTools).Assembly.GetType("BusyBunny.Game")?.GetMethod("Award", new[] { typeof(MainWindow), typeof(int), typeof(string) });
                bool awarded = false;
                if (award != null && award.IsStatic) { try { award.Invoke(null, new object[] { _w, points, "Boss defeated: " + t.Name }); awarded = true; } catch { } }
                S.Data.Log.Add(new LogEntry { When = DateTime.Now, Kind = "Boss", Text = "Boss defeated: " + t.Name, Tag = t.Tag, List = t.List, TaskId = t.Id, Points = awarded ? 0 : points });
                S.Save();
            }
            finally { _inBoss = false; }
            _bossLeft[t.Id] = 0;
            Sounds.Play(Sounds.Event.BossDefeated);
            _w.Say("Boss defeated: " + t.Name + ". Zero percent health. It never stood a chance, well, it stood a little.");
        }

        // ---- Task roulette (12.8) ----

        private void Roulette()
        {
            var now = DateTime.Now;
            var (urgent, rest) = S.Today(now);
            var pool = urgent.Take(5).ToList();
            if (pool.Count == 0) pool = rest.Take(5).ToList();
            if (pool.Count == 0) pool = S.Data.Tasks.Where(t => t.Status == Status.ThisWeek && S.CanShowToday(t, now)).Take(5).ToList();
            if (pool.Count == 0) { _w.Say("Nothing to pick from. The wheel is as empty as the list.", true); return; }
            var t = pool[_rng.Next(pool.Count)];
            _w.SelectTask(t);
            _w.Say("The wheel says: " + S.Line(t, now, Cfg.ShortMode) + ". No arguing with the wheel.", true);
        }

        // ---- Low-energy mode (12.10, 19.2) and shuffle mode (22.10) ----

        private void SetLow(bool on, bool speak)
        {
            if (Cfg.LowEnergy == on && !_announceLow) { if (_lowMenu != null) _lowMenu.IsChecked = on; if (speak) _w.Say("Low-energy mode is already " + (on ? "on" : "off") + ".", true); return; }
            Cfg.LowEnergy = on;
            Cfg.Save();
            if (_lowMenu != null) _lowMenu.IsChecked = on;
            _w.Refresh();
            if (!on) { _w.Say("Low-energy mode off. Everything is back on Today and Focus.", true); return; }
            var now = DateTime.Now;
            Cfg.LowEnergy = false;
            var hidden = S.Data.Tasks.Where(t => t.Status is Status.Today or Status.ThisWeek && S.CanShowToday(t, now) && FocusLogic.HiddenWhenLow(t)).ToList();
            Cfg.LowEnergy = true;
            string what = hidden.Count == 0 ? "Nothing needed hiding." : "Hiding " + hidden.Count + (hidden.Count == 1 ? " big or brain-heavy task" : " big or brain-heavy tasks") + " from Today and Focus.";
            _w.Say("Low-energy mode on. Only small or easy tasks show. " + what + " They are not gone, just resting.", true);
        }

        private void AutoLow()
        {
            var today = DateTime.Today;
            if (_autoLowDay == today) return;
            _autoLowDay = today;
            if (!S.Data.Days.TryGetValue(S.Key(today), out var d) || !d.Energy.Equals("low", StringComparison.OrdinalIgnoreCase) || Cfg.LowEnergy) return;
            SetLow(true, true);
        }

        private void SetShuffle(bool on)
        {
            Cfg.ShuffleMode = on;
            Cfg.Save();
            if (_shuffleMenu != null) _shuffleMenu.IsChecked = on;
            _w.Refresh();
            _w.Say(on ? "Shuffle mode on. The not-urgent tasks get a new order each day. The urgent ones stay put." : "Shuffle mode off. Back to the boring, sensible order.", true);
        }

        // ---- Quick wins drawer (22.4) ----

        private void ToggleQuickWin()
        {
            if (_w.ChosenTask is not TaskItem t) { _w.Say("Choose a task in a list first.", true); return; }
            S.Remember("marking a quick win");
            t.QuickWin = !t.QuickWin;
            _w.SaveAndRefresh();
            _w.Say(t.QuickWin ? t.Name + " is a quick win." : t.Name + " is not a quick win any more.", true);
        }

        private void QuickWins()
        {
            var now = DateTime.Now;
            var wins = S.Data.Tasks.Where(FocusLogic.IsQuickWin).OrderBy(S.Rank).ToList();
            var win = Layout.Dialog(_w, "Quick wins");
            var list = new ListBox { Width = 640, Height = 260 };
            AutomationProperties.SetName(list, "Quick wins");
            void Fill()
            {
                list.Items.Clear();
                foreach (var t in wins) list.Items.Add(new ListBoxItem { Content = S.Line(t, now, true) });
                if (wins.Count == 0) list.Items.Add(new ListBoxItem { Content = "No quick wins yet. Mark a task with Task, Quick win, or make it Small with Easy energy." });
                AutomationProperties.SetName(list, "Quick wins, " + wins.Count + (wins.Count == 1 ? " task" : " tasks"));
                list.SelectedIndex = 0;
            }
            Fill();
            var done = Layout.Button("_Done", isDefault: true);
            var close = Layout.Button("_Close", isCancel: true);
            done.Click += (_, _) =>
            {
                int i = list.SelectedIndex;
                if (i < 0 || i >= wins.Count) return;
                var t = wins[i];
                _w.SetTaskStatus(t, Status.Done);
                wins.RemoveAt(i);
                Fill();
                list.SelectedIndex = Math.Min(i, list.Items.Count - 1);
                (list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) as ListBoxItem)?.Focus();
            };
            close.Click += (_, _) => win.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(done);
            buttons.Children.Add(close);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = "Tiny tasks, two minutes or less. Pick one, press Done, collect momentum.", Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(list);
            panel.Children.Add(buttons);
            win.Content = panel;
            win.Loaded += (_, _) => TextViewer.Select(list, 0);
            win.ShowDialog();
        }

        // ---- Overwhelm button (22.9) ----

        private void Overwhelm()
        {
            var skipped = new HashSet<Guid>();
            TaskItem? Pick()
            {
                var now = DateTime.Now;
                var open = S.Data.Tasks.Where(t => t.Status is not (Status.Done or Status.Waiting or Status.DoneNotSubmitted) && !skipped.Contains(t.Id)).ToList();
                if (open.Count == 0) return null;
                int Score(TaskItem t) => t.QuickWin ? 0 : t.Size == Size.Small && t.Energy == Energy.Easy ? 1 : t.Size == Size.Small ? 2 : t.Energy == Energy.Easy ? 3 : t.Size == Size.Medium ? 4 : 5;
                int best = open.Min(Score);
                var tied = open.Where(t => Score(t) == best).ToList();
                return tied.OrderBy(t => S.DueSoon(t, now) ? 0 : 1).ThenBy(S.Rank).First();
            }
            var win = Layout.Dialog(_w, "One small thing");
            var text = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, Width = 560, MinHeight = 90, BorderThickness = new Thickness(0), FontSize = 18, Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetName(text, "One small thing");
            var done = Layout.Button("_Done", isDefault: true);
            var other = Layout.Button("Something _else");
            var back = Layout.Button("_Back to everything", isCancel: true);
            TaskItem? cur = null;
            void Show(string lead)
            {
                cur = Pick();
                done.IsEnabled = other.IsEnabled = cur != null;
                text.Text = cur == null
                    ? lead + "There is nothing left to show. That is either very good or very suspicious."
                    : lead + "Just this one. Nothing else exists right now. " + S.Line(cur, DateTime.Now, true) + ".";
                text.CaretIndex = 0;
            }
            done.Click += (_, _) =>
            {
                if (cur == null) return;
                _w.SetTaskStatus(cur, Status.Done);
                Show("Done. That counted. Breathe. ");
                text.Focus();
            };
            other.Click += (_, _) => { if (cur != null) skipped.Add(cur.Id); Show("Fair enough. "); text.Focus(); };
            back.Click += (_, _) => win.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(done);
            buttons.Children.Add(other);
            buttons.Children.Add(back);
            var panel = new StackPanel { Margin = new Thickness(16) };
            panel.Children.Add(text);
            panel.Children.Add(buttons);
            win.Content = panel;
            Show("Okay. Everything else is hidden. ");
            win.Loaded += (_, _) => { text.Focus(); text.CaretIndex = 0; };
            win.ShowDialog();
        }

        // ---- Shared ----

        private static bool Dialog(Window owner, string title, UIElement content, string okText)
        {
            var win = Layout.Dialog(owner, title);
            var ok = Layout.Button(okText, isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            bool result = false;
            ok.Click += (_, _) => { result = true; win.Close(); };
            cancel.Click += (_, _) => win.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(content);
            panel.Children.Add(buttons);
            win.Content = panel;
            win.ShowDialog();
            return result;
        }

        public void SelfTest(Store store)
        {
            var big = new TaskItem { Name = "paper", Size = Size.Large, Subtasks = { new() { Name = "a", Done = true }, new() { Name = "b" }, new() { Name = "c" }, new() { Name = "d" } } };
            if (!FocusLogic.IsBoss(big)) throw new Exception("a large task with 4 subtasks is a boss");
            if (FocusLogic.Health(big) != 75) throw new Exception("boss health was " + FocusLogic.Health(big));
            big.Size = Size.Medium;
            if (FocusLogic.IsBoss(big)) throw new Exception("a medium task is not a boss");
            big.Size = Size.MultiDay; big.Subtasks.RemoveRange(2, 2);
            if (FocusLogic.IsBoss(big)) throw new Exception("two subtasks is not a boss");

            var small = new TaskItem { Size = Size.Small };
            var easy = new TaskItem { Size = Size.Large, Energy = Energy.Easy };
            var heavy = new TaskItem { Size = Size.Small, Energy = Energy.BrainHeavy };
            var hard = new TaskItem { Size = Size.Medium };
            if (FocusLogic.HiddenWhenLow(small) || FocusLogic.HiddenWhenLow(easy)) throw new Exception("low-energy hid a small or easy task");
            if (!FocusLogic.HiddenWhenLow(heavy) || !FocusLogic.HiddenWhenLow(hard)) throw new Exception("low-energy showed a heavy task");
            if (FocusLogic.IsQuickWin(small) || !FocusLogic.IsQuickWin(new TaskItem { Size = Size.Small, Energy = Energy.Easy }) || !FocusLogic.IsQuickWin(new TaskItem { QuickWin = true, Size = Size.Large }))
                throw new Exception("quick win rule is wrong");

            var tasks = Enumerable.Range(0, 12).Select(_ => new TaskItem()).ToList();
            var day = new DateTime(2026, 10, 12, 9, 0, 0);
            var a = FocusLogic.Shuffle(tasks, day).Select(t => t.Id).ToList();
            var b = FocusLogic.Shuffle(tasks, day.AddHours(5)).Select(t => t.Id).ToList();
            var c = FocusLogic.Shuffle(tasks, day.AddDays(1)).Select(t => t.Id).ToList();
            if (!a.SequenceEqual(b)) throw new Exception("shuffle changed within a day");
            if (a.SequenceEqual(c)) throw new Exception("shuffle did not change the next day");
            if (a.Count != 12 || a.Distinct().Count() != 12) throw new Exception("shuffle lost tasks");

            if (FocusLogic.Duration(840) != "14 hours" || FocusLogic.Duration(90) != "1 hour 30 minutes" || FocusLogic.Duration(25) != "25 minutes") throw new Exception("duration words are wrong");
            var log = new List<LogEntry>
            {
                new() { Kind = "Sprint", When = day.AddDays(-1), Minutes = 25, Tag = "STAT" },
                new() { Kind = "Sprint", When = day, Minutes = 15, Tag = "STAT" },
                new() { Kind = "Sprint", When = day, Minutes = 10, Tag = "PSY" },
                new() { Kind = "Sprint", When = day.AddMonths(-3), Minutes = 60, Tag = "OLD" },
                new() { Kind = "Done", When = day, Minutes = 99, Tag = "PSY" },
            };
            var lines = FocusLogic.TimePerClassLines(log, day);
            if (!lines.Contains("40 minutes on STAT")) throw new Exception("time per class: " + string.Join("; ", lines));
            if (lines.Any(l => l.Contains("OLD") && !l.StartsWith("1 hour"))) throw new Exception("time per class counted the wrong thing");
            if (FocusLogic.PerTag(log, FocusLogic.WeekStart(day)).Any(x => x.Tag == "OLD")) throw new Exception("old sprint counted this week");
        }
    }
}
