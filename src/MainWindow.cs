using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// The main screen (spec 9.8: only what she needs now). From the top: the one-line summary, the
    /// day and week progress, then tabs: Today, Focus, Upcoming, then each list in rank order. Under
    /// the tabs, two pick menus act on the task chosen in whichever list was used last: Status and
    /// Move to list. Everything else is in the menus (Alt).
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        private readonly Store _store;
        private readonly Settings _settings;
        private readonly TextBox _summary = new() { IsReadOnly = true, IsReadOnlyCaretVisible = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 6, 8, 2) };
        private readonly TextBox _progress = new() { IsReadOnly = true, IsReadOnlyCaretVisible = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 2, 8, 2) };
        private readonly QuietProgressBar _day = new() { Height = 10, Margin = new Thickness(8, 2, 8, 2), Focusable = false };
        private readonly QuietProgressBar _week = new() { Height = 10, Margin = new Thickness(8, 2, 8, 6), Focusable = false };
        private readonly TabControl _tabs = new() { Margin = new Thickness(6, 0, 6, 0) };
        // The chosen task as text, to read by character or word with the arrows (and on braille): a list item can't be.
        private readonly TextBox _detail = new() { IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 8, 8, 0), MinHeight = 28 };
        private readonly ComboBox _status = new() { Width = 240, Margin = new Thickness(0, 0, 16, 0) };
        private readonly ComboBox _move = new() { Width = 240 };
        private readonly MenuItem _shortMenu = new() { Header = "_Short mode", IsCheckable = true, InputGestureText = "Ctrl+Shift+S" };
        private readonly MenuItem _soundsOnMenu = new() { Header = "Sounds _on", IsCheckable = true };
        private readonly MenuItem _checkAtStartMenu = new() { Header = "Check for updates _automatically", IsCheckable = true };
        private MenuItem _updateMenu = new();
        private readonly MenuItem _moveMenu = new() { Header = "_Move to list" };

        // Every task list on screen, by a key that stays the same across refreshes, so focus can come back to it.
        private readonly Dictionary<string, ListBox> _lists = new();
        private readonly ListBox _urgent, _alsoToday, _focus;
        private readonly StackPanel _upcoming = new();
        private ListBox? _lastList;
        private readonly HashSet<Guid> _focusSkipped = new();
        private bool _syncingPickers;
        private int _urgentBefore;

        public MainWindow(Store store, Settings settings)
        {
            _store = store;
            _settings = settings;
            Title = "Busy Bunny";
            Width = 900;
            Height = 680;
            FontSize = 15;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            AutomationProperties.SetName(_summary, "Summary");
            AutomationProperties.SetName(_progress, "Progress");
            _urgent = NewList("today-urgent");
            _alsoToday = NewList("today-also");
            _focus = NewList("focus");

            // Today: the urgent group, then everything else on today's plate.
            var today = new StackPanel { Margin = new Thickness(6) };
            today.Children.Add(new Label { Content = "_Urgent", Target = _urgent, Padding = new Thickness(0, 4, 0, 2) });
            today.Children.Add(_urgent);
            today.Children.Add(new Label { Content = "_Also today", Target = _alsoToday, Padding = new Thickness(0, 10, 0, 2) });
            today.Children.Add(_alsoToday);
            _tabs.Items.Add(new TabItem { Header = "Today", Content = new ScrollViewer { Content = today, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            // Focus: two or three tasks, picked by the urgent rules; Swap trades the chosen one for the next.
            var focus = new StackPanel { Margin = new Thickness(6) };
            focus.Children.Add(_focus);
            var swap = Layout.Button("S_wap this one for another");
            swap.HorizontalAlignment = HorizontalAlignment.Left;
            swap.Click += (_, _) => SwapFocus();
            focus.Children.Add(swap);
            _tabs.Items.Add(new TabItem { Header = "Focus", Content = focus });

            _tabs.Items.Add(new TabItem { Header = "Upcoming", Content = new ScrollViewer { Content = _upcoming, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });

            // Left and Right Arrow on the tab row move through the tabs and stay there, like any tab
            // control; Control Tab and Control Page Down jump straight into the new tab's tasks.
            _tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source != _tabs || !_jumpIntoTab) return;
                _jumpIntoTab = false;
                Dispatcher.BeginInvoke(() => FocusTab(announce: false));
            };

            // The pick menus for the chosen task. Arrowing through them changes nothing until Enter or
            // leaving the menu, so passing over Done on the way to Waiting never hides a task by accident.
            _status.ItemsSource = Enum.GetValues<Status>().Select(Words.Of).ToList();
            AutomationProperties.SetName(_status, "Status");
            AutomationProperties.SetName(_move, "Move to list");
            AutomationProperties.SetHelpText(_status, "Arrow to a status, then press Enter.");
            AutomationProperties.SetHelpText(_move, "Arrow to a list, then press Enter.");
            foreach (var c in new[] { _status, _move })
            {
                c.GotKeyboardFocus += (_, _) => SyncPickers();
                c.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && !c.IsDropDownOpen) { e.Handled = true; CommitPicker(c); } };
                c.DropDownClosed += (_, _) => { if (c.IsKeyboardFocusWithin) CommitPicker(c); };
                // Leaving for another window (NewFocus is null then) is not a choice: only moving on inside Busy Bunny is.
                c.LostKeyboardFocus += (_, e) => { if (e.NewFocus != null && !c.IsKeyboardFocusWithin && !c.IsDropDownOpen) CommitPicker(c); };
            }
            var pickers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 10) };
            pickers.Children.Add(new Label { Content = "Stat_us", Target = _status, VerticalAlignment = VerticalAlignment.Center });
            pickers.Children.Add(_status);
            pickers.Children.Add(new Label { Content = "_Move to", Target = _move, VerticalAlignment = VerticalAlignment.Center });
            pickers.Children.Add(_move);

            // Tab goes through these in this order, top to bottom: summary, progress, tabs, Status, Move to.
            // The menu bar is left out of Tab (Alt opens it), or Tab after Move to wandered into File.
            var dock = new DockPanel();
            var menu = BuildMenu();
            KeyboardNavigation.SetTabNavigation(menu, KeyboardNavigationMode.None);
            KeyboardNavigation.SetIsTabStop(menu, false);
            DockPanel.SetDock(menu, Dock.Top);
            dock.Children.Add(menu);
            foreach (var top in new FrameworkElement[] { _summary, _progress, _day, _week })
            {
                DockPanel.SetDock(top, Dock.Top);
                dock.Children.Add(top);
            }
            AutomationProperties.SetName(_detail, "Task");
            AutomationProperties.SetHelpText(_detail, "Read it with the arrows, by character or by word. Escape goes back to the list.");
            _detail.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Escape || _lastList == null) return;
                e.Handled = true;
                TextViewer.Select(_lastList, Math.Max(0, _lastList.SelectedIndex));
            };
            var body = new Grid();
            body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_detail, 1);
            Grid.SetRow(pickers, 2);
            body.Children.Add(_tabs);
            body.Children.Add(_detail);
            body.Children.Add(pickers);
            dock.Children.Add(body);
            KeyboardNavigation.SetTabNavigation(dock, KeyboardNavigationMode.Cycle);
            Content = dock;

            PreviewKeyDown += OnKeys;
            Loaded += (_, _) => { Refresh(); FocusTab(announce: false); };
            Activated += (_, _) => CheckNewDay();
            var minute = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            minute.Tick += (_, _) => { CheckNewDay(); MinuteTick?.Invoke(); };
            minute.Start();
            Closed += (_, _) => minute.Stop();
            foreach (var f in Features.All) f.Attach(this);
            Refresh();
            _urgentBefore = _store.Today(DateTime.Now).Urgent.Count;
        }

        private DateTime _shownDate;
        private bool _jumpIntoTab;

        // ---- For features (see Features.cs) ----

        public Store Store => _store;
        public Settings AppSettings => _settings;
        /// <summary>The task chosen in the list used last, or null.</summary>
        public TaskItem? ChosenTask => Current();
        /// <summary>The list whose tab is open, or the first list when Today, Focus or Upcoming is.</summary>
        public string CurrentListName => _tabs.SelectedItem is TabItem { Tag: string l } ? l : _store.ActiveLists.FirstOrDefault()?.Name ?? "Homework";
        public void Say(string text, bool interrupt = false) => Speech.Say(this, text, interrupt);
        /// <summary>Saves, then rebuilds every list.</summary>
        public void SaveAndRefresh() { Save(); Refresh(); }
        /// <summary>Features add text to the Task field here (like boss health); null adds nothing.</summary>
        public readonly List<Func<TaskItem, string?>> DetailParts = new();
        /// <summary>Moves focus to the task in Today or Focus, if it shows there. Returns whether it did.</summary>
        public bool SelectTask(TaskItem t)
        {
            foreach (var lb in new[] { _urgent, _alsoToday, _focus })
            {
                int at = IndexOf(lb, t.Id);
                if (at < 0) continue;
                _tabs.SelectedIndex = lb == _focus ? 1 : 0;
                _lastList = lb;
                Dispatcher.BeginInvoke(() => { TextViewer.Select(lb, at); ShowDetail(); }, System.Windows.Threading.DispatcherPriority.Input);
                return true;
            }
            return false;
        }
        /// <summary>Changes a task's status as Control 1 to 6 would, with its sounds and words.</summary>
        public void SetTaskStatus(TaskItem t, Status s) { _lastChosen = t; try { SetStatus(s); } finally { _lastChosen = null; } }
        private TaskItem? _lastChosen;
        /// <summary>The list tab's task list for a list name, or null.</summary>
        public ListBox? ListBoxOf(string listName) => _lists.TryGetValue("list-" + listName, out var lb) ? lb : null;
        /// <summary>Features add a short line for the Task field here, like 2 of 5 steps done; null adds nothing.</summary>
        public readonly List<Func<TaskItem, string?>> DetailNotes = new();

        /// <summary>A task changed status: the task, then its old status. Raised after saving.</summary>
        public event Action<TaskItem, Status>? StatusChanged;
        /// <summary>A task was added, by Quick add or any feature calling AddTask.</summary>
        public event Action<TaskItem>? TaskAdded;
        /// <summary>Every list was rebuilt: fill feature lists here.</summary>
        public event Action? Refreshed;
        /// <summary>The window is up after the startup picture: the place for the first-run guide or morning check-in.</summary>
        public event Action? Started;
        /// <summary>Once a minute, for reminders, timers and nudges.</summary>
        public event Action? MinuteTick;
        /// <summary>The date changed since the lists were last shown (overnight, or waking the laptop).</summary>
        public event Action<DateTime>? NewDay;

        public void OnStarted() => Started?.Invoke();
        /// <summary>Quick add and Edit task, for features (reminders, hotkeys, the snooze limit).</summary>
        public void OpenQuickAdd() => QuickAddTask();
        public void OpenEditTask() => EditTask();

        /// <summary>Adds a task as Quick add does: undoable, saved, announced.</summary>
        public void AddTask(TaskItem t, bool announce = true)
        {
            _store.Remember("adding " + t.Name);
            _store.Data.Tasks.Add(t);
            Save();
            Refresh();
            if (announce) Speech.Say(this, "Added " + t.Name + " to " + t.List + ".");
            TaskAdded?.Invoke(t);
            WarnIfOverloaded();
        }

        private readonly Dictionary<string, MenuItem> _menus = new();
        /// <summary>Adds an item to a top menu: File, Task, View, Fun, Options or Help.</summary>
        public MenuItem AddMenuItem(string menu, string header, Action act, string gesture = "")
        {
            var m = new MenuItem { Header = header, InputGestureText = gesture };
            m.Click += (_, _) => act();
            var top = _menus[menu];
            top.Items.Add(m);
            top.Visibility = Visibility.Visible;
            return m;
        }

        private readonly List<(ModifierKeys Mods, Key Key, Action Act)> _keys = new();
        private readonly List<string> _keyHelp = new();
        /// <summary>A key anywhere in the main window, with its line for Help, Keyboard shortcuts.</summary>
        public void AddKey(ModifierKeys mods, Key key, Action act, string help)
        {
            _keys.Add((mods, key, act));
            _keyHelp.Add(help);
        }

        private readonly Dictionary<TabItem, Func<ListBox?>> _firstLists = new();
        /// <summary>A tab after Upcoming. first gives the list Control Tab should land on.</summary>
        public TabItem AddTab(string header, UIElement content, Func<ListBox?> first)
        {
            var tab = new TabItem { Header = header, Content = content };
            int at = _tabs.Items.OfType<TabItem>().TakeWhile(t => t.Tag is not string).Count();
            _tabs.Items.Insert(at, tab);
            _firstLists[tab] = first;
            return tab;
        }

        /// <summary>A task list that works like the others: Enter, F2, the pick menus and the task menu all act on its chosen task.</summary>
        public ListBox NewTaskList(string key) => NewList(key);
        /// <summary>Fills a task list: name is its spoken name, empty what it says with no tasks.</summary>
        public void FillTasks(ListBox lb, IEnumerable<TaskItem> tasks, string name, string empty) => Fill(lb, tasks, name, empty);

        private void CheckNewDay()
        {
            if (DateTime.Now.Date == _shownDate) return;
            Refresh();
            NewDay?.Invoke(DateTime.Now.Date);
        }

        private ListBox NewList(string key)
        {
            var lb = new ListBox { MinHeight = 40, MaxHeight = 420, BorderThickness = new Thickness(1) };
            ScrollViewer.SetCanContentScroll(lb, true);
            lb.GotKeyboardFocus += (_, _) => { _lastList = lb; ShowDetail(); };
            lb.SelectionChanged += (_, _) => { if (lb.IsKeyboardFocusWithin) { _lastList = lb; ShowDetail(); } };
            lb.ContextMenu = new ContextMenu();
            // Start on the first item, so NVDA says Edit task at once instead of just "menu".
            lb.ContextMenu.Opened += (s, _) =>
            {
                if (s is ContextMenu m && m.Items.Count > 0 && m.Items[0] is MenuItem first)
                    m.Dispatcher.BeginInvoke(() => first.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            };
            lb.ContextMenuOpening += (_, e) => { if (!FillContextMenu(lb)) e.Handled = true; };
            _lists[key] = lb;
            return lb;
        }

        // ---- Menus ----

        private Menu BuildMenu()
        {
            MenuItem Item(string header, Action act, string? keys = null)
            {
                var m = new MenuItem { Header = header, InputGestureText = keys ?? "" };
                m.Click += (_, _) => act();
                return m;
            }
            var file = new MenuItem { Header = "_File" };
            file.Items.Add(Item("_Quick add a task...", QuickAddTask, "Ctrl+N"));
            file.Items.Add(Item("_Read my day", () => Speech.Say(this, _store.Summary(DateTime.Now, _settings.ShortMode), true), "Ctrl+R"));
            file.Items.Add(new Separator());
            file.Items.Add(Item("Open the _backups folder", () => Open(Store.BackupFolder)));
            file.Items.Add(Item("Start over with the _sample tasks...", ResetSample));
            file.Items.Add(new Separator());
            file.Items.Add(Item("E_xit", () => { Reminders.Quitting = true; Close(); }, "Alt+F4"));

            var task = new MenuItem { Header = "_Task" };
            task.Items.Add(Item("_Edit task...", EditTask, "F2"));
            task.Items.Add(new Separator());
            task.Items.Add(Item("Mark _done", () => SetStatus(Status.Done), "Ctrl+D"));
            var statuses = new MenuItem { Header = "Set _status" };
            foreach (Status s in Enum.GetValues<Status>())
            {
                var st = s;
                statuses.Items.Add(Item(Words.Of(s), () => SetStatus(st), "Ctrl+" + ((int)s + 1)));
            }
            task.Items.Add(statuses);
            task.Items.Add(_moveMenu); // its lists are filled in by SyncListTabs
            task.Items.Add(Item("D_elete", DeleteTask, "Delete"));
            task.Items.Add(new Separator());
            task.Items.Add(Item("_Undo", Undo, "Ctrl+Z"));

            var view = new MenuItem { Header = "_View" };
            _shortMenu.IsChecked = _settings.ShortMode;
            _shortMenu.Click += (_, _) => { _settings.ShortMode = _shortMenu.IsChecked; _settings.Save(); Refresh(); };
            view.Items.Add(_shortMenu);

            var options = new MenuItem { Header = "_Options" };
            options.Items.Add(Item("_Settings...", () => OpenSettings(null), "Ctrl+Comma"));
            options.Items.Add(Item("_Priority scales...", EditScales));
            options.Items.Add(Item("S_ounds...", () => { SoundsDialog.Show(this, _settings); _soundsOnMenu.IsChecked = _settings.SoundsOn; }));
            _soundsOnMenu.IsChecked = _settings.SoundsOn;
            _soundsOnMenu.Click += (_, _) => { _settings.SoundsOn = _soundsOnMenu.IsChecked; _settings.Save(); _settings.Apply(); };
            options.Items.Add(_soundsOnMenu);
            _checkAtStartMenu.IsChecked = _settings.CheckForUpdatesAtStart;
            _checkAtStartMenu.Click += (_, _) =>
            {
                _settings.CheckForUpdatesAtStart = _checkAtStartMenu.IsChecked;
                _settings.Save();
                if (_settings.CheckForUpdatesAtStart) { StartAutoUpdates(); BackgroundCheck(); }
            };
            options.Items.Add(_checkAtStartMenu);

            var help = new MenuItem { Header = "_Help" };
            _updateMenu = Item("Check for _updates...", CheckForUpdates);
            help.Items.Add(_updateMenu);
            help.Items.Add(Item("View the _changelog...", () => ShowChanges(), "Shift+F1"));
            help.Items.Add(Item("_Keyboard shortcuts...", () => TextViewer.Show(this, "Keyboard shortcuts", Shortcuts())));
            help.Items.Add(new Separator());
            help.Items.Add(Item("_About Busy Bunny...", () => TextViewer.Show(this, "About Busy Bunny", About())));
            help.Items.Add(Item("_Licences and credits...", () => TextViewer.Show(this, "Licences and credits", Credits(), l => l.EndsWith(":"))));
            help.Items.Add(Item("Open the Busy Bunny _website", () => Open(Updater.Website)));
            help.Items.Add(Item("_Report a problem", () => Open(Updater.Website + "/issues/new")));

            var fun = new MenuItem { Header = "F_un", Visibility = Visibility.Collapsed };
            var bar = new Menu();
            foreach (var m in new[] { file, task, view, fun, options, help }) bar.Items.Add(m);
            _menus["File"] = file; _menus["Task"] = task; _menus["View"] = view; _menus["Fun"] = fun; _menus["Options"] = options; _menus["Help"] = help;
            return bar;
        }

        private void OnKeys(object sender, KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool inText = Keyboard.FocusedElement is TextBox tb && !tb.IsReadOnly;
            bool next = mods == ModifierKeys.Control && (key == Key.Tab || key == Key.PageDown);
            bool back = mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.Tab || mods == ModifierKeys.Control && key == Key.PageUp;
            if (next || back)
            {
                e.Handled = true;
                int n = _tabs.Items.Count;
                _jumpIntoTab = true;
                _tabs.SelectedIndex = (_tabs.SelectedIndex + (back ? n - 1 : 1)) % n;
                return;
            }
            if (mods == ModifierKeys.Control)
            {
                switch (key)
                {
                    case Key.N: e.Handled = true; QuickAddTask(); return;
                    case Key.R: e.Handled = true; Speech.Say(this, _store.Summary(DateTime.Now, _settings.ShortMode), true); return;
                    case Key.D: e.Handled = true; SetStatus(Status.Done); return;
                    case Key.Z when !inText: e.Handled = true; Undo(); return;
                }
                if (key >= Key.D1 && key <= Key.D6) { e.Handled = true; SetStatus((Status)(key - Key.D1)); return; }
            }
            if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && key == Key.S)
            {
                e.Handled = true;
                _settings.ShortMode = !_settings.ShortMode;
                _shortMenu.IsChecked = _settings.ShortMode;
                _settings.Save();
                Refresh();
                Speech.Say(this, _settings.ShortMode ? "Short mode on." : "Short mode off.", true);
                return;
            }
            foreach (var (m, k, act) in _keys)
                if (m == mods && k == key) { e.Handled = true; act(); return; }
            if (mods == ModifierKeys.Shift && key == Key.F1) { e.Handled = true; ShowChanges(); return; }
            if (mods == ModifierKeys.None && key == Key.F2) { e.Handled = true; EditTask(); return; }
            if (mods == ModifierKeys.Control && key == Key.OemComma) { e.Handled = true; OpenSettings(null); return; }
            if (mods == ModifierKeys.None && key == Key.Delete && Keyboard.FocusedElement is ListBoxItem) { e.Handled = true; DeleteTask(); }
            if (mods == ModifierKeys.None && key == Key.Enter && Keyboard.FocusedElement is ListBoxItem { Tag: Guid })
            {
                e.Handled = true;
                ShowDetail();
                _detail.Focus();
                _detail.CaretIndex = 0;
            }
        }

        // ---- Showing the tasks ----

        /// <summary>Rebuilds every list from the data, keeping focus on the same task, or the one now in its place.</summary>
        public void Refresh()
        {
            var now = DateTime.Now;
            _shownDate = now.Date;
            SyncListTabs();
            // Tab names follow the lists' scales: Homework (Coffee).
            foreach (TabItem tab in _tabs.Items)
                if (tab.Tag is string name && _store.Data.Lists.FirstOrDefault(l => l.Name == name) is TaskList list && !Equals(tab.Header, list.TabName))
                    tab.Header = list.TabName;
            bool shortMode = _settings.ShortMode;
            string? focusKey = null;
            Guid? focusId = null;
            int focusIndex = 0;
            foreach (var (k, lb) in _lists)
                if (lb.IsKeyboardFocusWithin) { focusKey = k; focusIndex = Math.Max(0, lb.SelectedIndex); focusId = (lb.SelectedItem as ListBoxItem)?.Tag as Guid?; }

            var (urgent, rest) = _store.Today(now);
            Fill(_urgent, urgent, "Today, urgent", "Nothing urgent. Enjoy that while it lasts.");
            Fill(_alsoToday, rest, "Also today", "Nothing else today.");

            var focusPool = urgent.Concat(_store.Reorder(_store.Data.Tasks.Where(t => t.Status is Status.ThisWeek && !urgent.Contains(t) && _store.CanShowToday(t, now)).OrderBy(_store.Rank).ThenBy(t => t.Priority == 0 ? 9 : t.Priority).ToList(), now)).ToList();
            if (focusPool.Count > 0 && focusPool.All(t => _focusSkipped.Contains(t.Id))) _focusSkipped.Clear();
            Fill(_focus, focusPool.Where(t => !_focusSkipped.Contains(t.Id)).Take(3), "Focus", "Nothing to focus on. Suspicious, but okay.");

            // Upcoming: a group per day, like Tuesday, 4 tasks.
            foreach (var key in _lists.Keys.Where(k => k.StartsWith("up-")).ToList()) _lists.Remove(key);
            _upcoming.Children.Clear();
            var dated = _store.Data.Tasks.Where(t => t.Status != Status.Done && t.Due is DateTime d && d.Date >= now.Date)
                .GroupBy(t => t.Due!.Value.Date).OrderBy(g => g.Key).ToList();
            bool first = true;
            foreach (var g in dated)
            {
                var lb = NewList("up-" + g.Key.ToString("yyyy-MM-dd"));
                string day = Words.Day(g.Key, now);
                day = char.ToUpper(day[0]) + day[1..];
                Fill(lb, g.OrderBy(t => t.Due).ThenBy(_store.Rank), (first ? "Upcoming, " : "") + day, "");
                _upcoming.Children.Add(new TextBlock { Text = day + ", " + g.Count() + (g.Count() == 1 ? " task" : " tasks"), Margin = new Thickness(6, 10, 0, 2), FontWeight = FontWeights.SemiBold });
                _upcoming.Children.Add(lb);
                first = false;
            }
            if (dated.Count == 0)
            {
                var lb = NewList("up-none");
                Fill(lb, Array.Empty<TaskItem>(), "Upcoming", "Nothing with a date coming up.");
                _upcoming.Children.Add(lb);
            }

            foreach (var l in _store.ActiveLists.Where(l => _lists.ContainsKey("list-" + l.Name)))
            {
                var tasks = _store.Data.Tasks.Where(t => t.List == l.Name && t.Status != Status.Done)
                    .OrderBy(t => t.Status).ThenBy(t => t.Priority == 0 ? 9 : t.Priority).ThenBy(t => t.Due ?? DateTime.MaxValue);
                Fill(_lists["list-" + l.Name], tasks, l.Name, "Nothing on " + l.Name + ".");
            }

            _summary.Text = _store.Summary(now, shortMode);
            var (done, total, pd, pt) = _store.Progress(now);
            _day.Value = pt == 0 ? 0 : pd * 100.0 / pt;
            var (wd, wt) = Week(now);
            _week.Value = wt == 0 ? 0 : wd * 100.0 / wt;
            Refreshed?.Invoke();
            _progress.Text = "Today: " + done + " of " + total + " tasks, " + (int)_day.Value + " percent of the effort. This week: " + (int)_week.Value + " percent.";

            if (focusKey != null && _lists.TryGetValue(focusKey, out var back))
            {
                int at = focusId is Guid id ? IndexOf(back, id) : -1;
                TextViewer.Select(back, at >= 0 ? at : Math.Min(focusIndex, back.Items.Count - 1));
            }
            ShowDetail();
        }

        private void Fill(ListBox lb, IEnumerable<TaskItem> tasks, string name, string empty)
        {
            var now = DateTime.Now;
            int keep = lb.SelectedIndex;
            lb.Items.Clear();
            foreach (var t in tasks)
                lb.Items.Add(new ListBoxItem { Content = _store.Line(t, now, _settings.ShortMode), Tag = t.Id });
            int count = lb.Items.Count;
            if (count == 0 && empty.Length > 0) lb.Items.Add(new ListBoxItem { Content = empty });
            AutomationProperties.SetName(lb, name + ", " + count + (count == 1 ? " task" : " tasks"));
            if (lb.Items.Count > 0) lb.SelectedIndex = Math.Clamp(keep, 0, lb.Items.Count - 1);
        }

        private static int IndexOf(ListBox lb, Guid id)
        {
            for (int i = 0; i < lb.Items.Count; i++) if ((lb.Items[i] as ListBoxItem)?.Tag is Guid g && g == id) return i;
            return -1;
        }

        /// <summary>This week's effort: finished this week, against that plus what is still open for this week.</summary>
        private (int Done, int Total) Week(DateTime now)
        {
            var monday = now.Date.AddDays(-(((int)now.DayOfWeek + 6) % 7));
            var sunday = monday.AddDays(7);
            int done = _store.Data.Tasks.Where(t => t.Status == Status.Done && t.DoneAt >= monday).Sum(t => Words.Points(t.Size));
            int open = _store.Data.Tasks.Where(t => t.Status is Status.Today or Status.ThisWeek or Status.DoneNotSubmitted || t.Status != Status.Done && t.Status != Status.Waiting && t.Due < sunday).Sum(t => Words.Points(t.Size));
            return (done, done + open);
        }

        private void FocusTab(bool announce)
        {
            if (_tabs.SelectedItem is not TabItem tab) return;
            ListBox? first = _tabs.SelectedIndex switch
            {
                0 => _urgent,
                1 => _focus,
                2 => _upcoming.Children.OfType<ListBox>().FirstOrDefault(),
                _ when _firstLists.TryGetValue(tab, out var f) => f(),
                _ => _lists.TryGetValue("list-" + tab.Tag, out var lb) ? lb : null,
            };
            if (first != null) TextViewer.Select(first, Math.Max(0, first.SelectedIndex));
        }

        // ---- Acting on the chosen task ----

        private TaskItem? Current()
        {
            if (_lastChosen != null) return _lastChosen;
            var lb = _lastList;
            if (lb == null || (lb.SelectedItem as ListBoxItem)?.Tag is not Guid id) return null;
            return _store.Data.Tasks.FirstOrDefault(t => t.Id == id);
        }

        /// <summary>The Task field: the chosen task's line, then where it lives and how big it is.</summary>
        private void ShowDetail()
        {
            var t = Current();
            string text = "";
            if (t != null)
            {
                var list = _store.ListOf(t);
                text = _store.Line(t, DateTime.Now, _settings.ShortMode) + ". List: " + t.List + ". Size: " + Words.Of(t.Size) + ".";
                if (list.Scale.Length > 0)
                    text += " Priority: " + (t.Priority > 0 && t.Priority <= list.Scale.Length ? list.Scale[t.Priority - 1] + ", " + t.Priority + " of " + list.Scale.Length : "none")
                        + ", on the " + list.ScaleName + " scale: " + Scales.Rank(list.Scale);
                foreach (var note in DetailNotes) if (note(t) is string n && n.Length > 0) text += " " + n;
                foreach (var part in DetailParts)
                    if (part(t) is string extra && extra.Length > 0) text += " " + extra;
            }
            if (_detail.Text != text) _detail.Text = text;
        }

        private TaskItem? CurrentOrSay()
        {
            var t = Current();
            if (t == null) Speech.Say(this, "Choose a task in a list first.", true);
            return t;
        }

        private void SetStatus(Status s)
        {
            if (CurrentOrSay() is not TaskItem t) return;
            if (t.Status == s) { Speech.Say(this, "It is already " + Words.Of(s) + ".", true); return; }
            var now = DateTime.Now;
            int doneBefore = _store.Progress(now).Done;
            _store.Remember((s == Status.Done ? "marking " : "changing the status of ") + t.Name);
            var old = t.Status;
            t.Status = s;
            t.TouchedAt = now;
            t.StatusSince = now;
            t.DoneAt = s == Status.Done ? now : null;
            Save();
            Refresh();
            if (s == Status.Done)
            {
                var (done, total, _, _) = _store.Progress(now);
                if (done == total && total > 0 && doneBefore < done)
                {
                    Sounds.Play(Sounds.Event.DailyGoal);
                    Speech.Say(this, "Done: " + t.Name + ". That is everything for today. " + done + " of " + total + ".");
                }
                else
                {
                    Sounds.Play(Sounds.Event.TaskDone);
                    Speech.Say(this, "Done: " + t.Name + ". " + done + " of " + total + " today.");
                }
            }
            else Speech.Say(this, t.Name + ": " + Words.Of(s) + ".");
            StatusChanged?.Invoke(t, old);
            WarnIfOverloaded();
        }

        private void MoveTo(string list)
        {
            if (CurrentOrSay() is not TaskItem t) return;
            if (t.List == list) { Speech.Say(this, "It is already on " + list + ".", true); return; }
            _store.Remember("moving " + t.Name);
            var scale = _store.Data.Lists.FirstOrDefault(l => l.Name == list)?.Scale ?? Array.Empty<string>();
            t.List = list;
            if (t.Priority > scale.Length) t.Priority = scale.Length; // the new list's scale may be shorter
            Save();
            Refresh();
            Speech.Say(this, "Moved " + t.Name + " to " + list + ".");
        }

        private void DeleteTask()
        {
            if (CurrentOrSay() is not TaskItem t) return;
            _store.Remember("deleting " + t.Name);
            _store.Data.Tasks.Remove(t);
            Save();
            Refresh();
            Speech.Say(this, "Deleted " + t.Name + ". Control Z brings it back.");
        }

        private void Undo()
        {
            string? what = _store.Undo();
            Refresh();
            Speech.Say(this, what == null ? "Nothing to undo." : "Undid " + what + ".", true);
        }

        private void QuickAddTask()
        {
            string list = _tabs.SelectedItem is TabItem { Tag: string l } ? l : "Homework";
            var status = _tabs.SelectedIndex <= 1 ? Status.Today : Status.ThisWeek;
            if (QuickAdd.Show(this, _store, list, status) is not TaskItem t) return;
            AddTask(t);
        }

        /// <summary>Edit task (F2): the Quick add form, filled in with the chosen task. Save changes it in place; Control Z undoes it.</summary>
        private void EditTask()
        {
            if (CurrentOrSay() is not TaskItem t) return;
            var back = _lastList;
            if (QuickAdd.Show(this, _store, t.List, t.Status, t) is not TaskItem edited) { if (back != null) TextViewer.Select(back, Math.Max(0, back.SelectedIndex)); return; }
            _store.Remember("editing " + t.Name);
            if (edited.Status != t.Status) t.StatusSince = DateTime.Now;
            QuickAdd.Apply(edited, t);
            Save();
            Refresh();
            // Back on the same task, wherever it now sits; if it moved off this list, the list keeps its place.
            if (back != null && _lists.ContainsValue(back))
            {
                int at = IndexOf(back, t.Id);
                TextViewer.Select(back, at >= 0 ? at : Math.Min(Math.Max(0, back.SelectedIndex), back.Items.Count - 1));
            }
            Speech.Say(this, "Saved " + t.Name + ".");
            WarnIfOverloaded();
        }

        /// <summary>Options, Priority scales: Settings, opened at the list whose tab is open (or the chosen task's list).</summary>
        private void EditScales()
        {
            string? list = _tabs.SelectedItem is TabItem { Tag: string l } ? l : _lastList != null && Current() is TaskItem t ? t.List : _store.Data.Lists.FirstOrDefault()?.Name;
            OpenSettings(list);
        }

        private void OpenSettings(string? list)
        {
            if (!SettingsWindow.Show(this, _store, _settings, list)) { FocusTab(false); return; }
            Save();
            _shortMenu.IsChecked = _settings.ShortMode;
            _soundsOnMenu.IsChecked = _settings.SoundsOn;
            _checkAtStartMenu.IsChecked = _settings.CheckForUpdatesAtStart;
            if (_settings.CheckForUpdatesAtStart) StartAutoUpdates();
            Refresh();
            FocusTab(false);
            Speech.Say(this, "Settings saved.");
        }

        /// <summary>
        /// Makes the list tabs, the Move to pick menu and the Move to list menu match the lists: after
        /// Settings adds, renames or reorders them, and after Undo puts them back.
        /// </summary>
        private void SyncListTabs()
        {
            var names = _store.ActiveLists.Where(l => l.Parent.Length == 0).Select(l => l.Name).ToList();
            var tabs = _tabs.Items.OfType<TabItem>().Where(t => t.Tag is string).ToList();
            if (tabs.Select(t => (string)t.Tag).SequenceEqual(names) && _moveMenu.Items.Count == names.Count) return;
            object? selectedTag = (_tabs.SelectedItem as TabItem)?.Tag;
            int selectedIndex = _tabs.SelectedIndex;
            foreach (var t in tabs) { _tabs.Items.Remove(t); _lists.Remove("list-" + t.Tag); }
            foreach (var l in _store.ActiveLists.Where(l => l.Parent.Length == 0))
            {
                var lb = NewList("list-" + l.Name);
                var panel = new StackPanel { Margin = new Thickness(6) };
                panel.Children.Add(lb);
                _tabs.Items.Add(new TabItem { Header = l.TabName, Content = new ScrollViewer { Content = panel, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, Tag = l.Name });
            }
            var same = _tabs.Items.OfType<TabItem>().FirstOrDefault(t => selectedTag is string s && Equals(t.Tag, s));
            if (same != null) _tabs.SelectedItem = same;
            else _tabs.SelectedIndex = Math.Clamp(selectedIndex, 0, _tabs.Items.Count - 1);
            _move.ItemsSource = names;
            _moveMenu.Items.Clear();
            foreach (var n in names)
            {
                var name = n;
                var m = new MenuItem { Header = name };
                m.Click += (_, _) => MoveTo(name);
                _moveMenu.Items.Add(m);
            }
        }

        private void SwapFocus()
        {
            if ((_focus.SelectedItem as ListBoxItem)?.Tag is not Guid id) { Speech.Say(this, "Nothing to swap.", true); return; }
            _focusSkipped.Add(id);
            Refresh();
            TextViewer.Select(_focus, Math.Max(0, _focus.SelectedIndex));
            Speech.Say(this, "Swapped.");
        }

        /// <summary>The heads-up when the urgent group gets big (spec 6.6): plain, never scolding, once each time it crosses the line.</summary>
        private void WarnIfOverloaded()
        {
            int urgent = _store.Today(DateTime.Now).Urgent.Count;
            int limit = _settings.UrgentWarnAbove;
            if (limit > 0 && urgent > limit && _urgentBefore <= limit)
            {
                Sounds.Play(Sounds.Event.OverloadWarning);
                Speech.Say(this, "Heads up: " + urgent + " urgent tasks. Want to move some to This week? Control 2 does it.");
            }
            _urgentBefore = urgent;
        }

        private void SyncPickers()
        {
            _syncingPickers = true;
            var t = Current();
            _status.SelectedIndex = t == null ? -1 : (int)t.Status;
            _move.SelectedItem = t?.List;
            _syncingPickers = false;
        }

        private void CommitPicker(ComboBox c)
        {
            if (_syncingPickers || Current() is not TaskItem t) return;
            if (c == _status && _status.SelectedIndex >= 0 && (Status)_status.SelectedIndex != t.Status) SetStatus((Status)_status.SelectedIndex);
            else if (c == _move && _move.SelectedItem is string l && l != t.List) MoveTo(l);
        }

        private bool FillContextMenu(ListBox lb)
        {
            _lastList = lb;
            if (Current() is not TaskItem t) return false;
            var menu = lb.ContextMenu!;
            menu.Items.Clear();
            MenuItem Item(string header, Action act, string keys = "")
            {
                var m = new MenuItem { Header = header, InputGestureText = keys };
                m.Click += (_, _) => act();
                return m;
            }
            menu.Items.Add(Item("_Edit task...", EditTask, "F2"));
            menu.Items.Add(new Separator());
            menu.Items.Add(Item("Mark _done", () => SetStatus(Status.Done), "Ctrl+D"));
            var st = new MenuItem { Header = "Set _status" };
            foreach (Status s in Enum.GetValues<Status>()) { var x = s; st.Items.Add(Item(Words.Of(s) + (s == t.Status ? ", now" : ""), () => SetStatus(x), "Ctrl+" + ((int)s + 1))); }
            menu.Items.Add(st);
            var mv = new MenuItem { Header = "_Move to list" };
            foreach (var l in _store.Data.Lists) { var n = l.Name; mv.Items.Add(Item(n + (n == t.List ? ", now" : ""), () => MoveTo(n))); }
            menu.Items.Add(mv);
            menu.Items.Add(Item("D_elete", DeleteTask, "Delete"));
            return true;
        }

        private void Save()
        {
            try { _store.Save(); }
            catch (Exception e) { MessageBox.Show(this, "Busy Bunny could not save: " + e.Message + "\n\nYour change is still on screen. Try again in a moment.", "Busy Bunny", MessageBoxButton.OK, MessageBoxImage.Warning); }
        }

        private void ResetSample()
        {
            if (MessageBox.Show(this, "Replace every task with the sample tasks? Control Z undoes it.", "Start over", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            _store.Remember("starting over");
            _store.Data.Tasks = Sample.Make().Tasks;
            Save();
            Refresh();
            FocusTab(false);
        }

        // ---- Updates ----

        private bool _manualChecking, _backgroundChecking;
        private Updater.Release? _available;
        private Version? _announced;
        private System.Windows.Threading.DispatcherTimer? _updateTimer;

        /// <summary>
        /// Checks for updates every 30 seconds, and again a few seconds after the internet comes back.
        /// No connection, GitHub down or a Wi-Fi sign-in page: nothing is said and nothing breaks; the
        /// next check simply tries again. A new version is announced once, saying how far behind this copy is.
        /// </summary>
        public void StartAutoUpdates()
        {
            if (_updateTimer != null) return;
            _updateTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _updateTimer.Tick += (_, _) => BackgroundCheck();
            _updateTimer.Start();
            void Reconnected()
            {
                // A moment for the connection to settle (names to resolve) before asking.
                Dispatcher.BeginInvoke(async () => { await System.Threading.Tasks.Task.Delay(3000); BackgroundCheck(); });
            }
            System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += (_, e) => { if (e.IsAvailable) Reconnected(); };
            System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += (_, _) => Reconnected();
            BackgroundCheck();
        }

        private async void BackgroundCheck()
        {
            if (_backgroundChecking || _manualChecking || !_settings.CheckForUpdatesAtStart) return;
            _backgroundChecking = true;
            try
            {
                var r = await Updater.CheckAsync(quick: true);
                _available = r;
                _updateMenu.Header = r == null ? "Check for _updates..." : "_Install version " + r.Version + "...";
                if (r != null && r.Version != _announced)
                {
                    _announced = r.Version;
                    Speech.Say(this, "Busy Bunny " + r.Version + " is out. " + Behind(r) + " Help menu, Install version " + r.Version + " shows what's new and installs it.");
                }
            }
            catch { } // offline or GitHub unhappy: try again at the next tick or when the connection comes back
            finally { _backgroundChecking = false; }
        }

        private static string Behind(Updater.Release r) =>
            "You're " + r.Missed.Count + (r.Missed.Count == 1 ? " version" : " versions") + " out of date.";

        public async void CheckForUpdates()
        {
            if (_manualChecking) return;
            _manualChecking = true;
            try
            {
                Speech.Say(this, "Checking for updates.", true);
                var r = await Updater.CheckAsync();
                _available = r;
                _updateMenu.Header = r == null ? "Check for _updates..." : "_Install version " + r.Version + "...";
                if (r == null)
                {
                    MessageBox.Show(this, "You have the newest Busy Bunny, version " + Updater.Current + ".", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }
                _announced = r.Version;
                if (!UpdateDialog.Show(this, r, Behind(r))) return;
                Speech.Say(this, "Downloading the update.", true);
                Save();
                await Updater.InstallAsync(r);
                Application.Current.Shutdown();
            }
            catch (Exception e)
            {
                MessageBox.Show(this, e.Message, "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally { _manualChecking = false; }
        }

        // ---- Help ----

        /// <summary>Every change, a line each, under Version headings (Control Down and Up jump between them). Opens by itself after an update.</summary>
        public void ShowChanges() => ShowChanges(null);

        /// <summary>The changelog; after an update (since given), only the versions newer than the one updated from, each under its own heading.</summary>
        public void ShowChanges(Version? since)
        {
            var lines = new List<string>();
            bool keep = true;
            int versions = 0;
            foreach (var raw in App.Resource("CHANGES.txt").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) continue;
                if (Version.TryParse(line, out var v))
                {
                    keep = since == null || v > since;
                    if (keep) { versions++; lines.Add("Version " + line + (line == Updater.Current.ToString() ? ", this one" : "")); }
                }
                else if (keep) lines.Add(line);
            }
            if (since != null && versions > 0)
                lines.Insert(0, "Updated from " + since + " to " + Updater.Current + ": " + versions + (versions == 1 ? " new version." : " new versions."));
            TextViewer.Show(this, since == null ? "Busy Bunny changelog" : "What's new since " + since, lines, l => l.StartsWith("Version "));
        }

        private List<string> Shortcuts() => new List<string>
        {
            "Open the menus: Alt",
            "Next tab: Control Tab or Control Page Down. Previous tab: Control Shift Tab or Control Page Up. Both go straight to the tab's tasks",
            "The tab row: Shift Tab from a tab's first list, then Left and Right Arrow move through the tabs",
            "View the changelog: Shift F1",
            "Quick add a task: Control N",
            "Read the chosen task by character or word: Enter, then the arrows. Escape goes back to the list",
            "Edit the chosen task, its name or anything else: F2",
            "Settings: Control Comma",
            "Mark the chosen task done: Control D",
            "Set its status: Control 1 Today, 2 This week, 3 Next week, 4 Done not submitted, 5 Waiting on someone, 6 Done",
            "Undo: Control Z",
            "Delete the chosen task: Delete",
            "Read my day: Control R",
            "Short mode on or off: Control Shift S",
            "Task menu for the chosen task: the Applications key or Shift F10",
        }.Concat(_keyHelp).Append("These shortcuts are a first try. Tell Conner which ones feel wrong.").ToList();

        private static List<string> About() => new()
        {
            "Busy Bunny " + Updater.Current + " for " + Updater.Arch + " Windows",
            "Running from " + Environment.ProcessPath,
            "Your tasks are saved in " + Store.FilePath,
            "A copy is kept each day in " + Store.BackupFolder + ", the last 30 days",
            "Website: " + Updater.Website,
        };

        private static List<string> Credits()
        {
            var lines = new List<string>();
            foreach (var (name, title) in new[] { ("Sounds-CREDITS.txt", "Where the sounds come from:"), ("Concentus-LICENSE.txt", "Concentus, which plays the sounds:") })
            {
                lines.Add(title);
                lines.AddRange(App.Resource(name).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            }
            return lines;
        }

        private void Open(string target)
        {
            try
            {
                if (!target.StartsWith("http")) System.IO.Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
            }
            catch (Exception e) { Speech.Say(this, "Could not open it: " + e.Message, true); }
        }
    }

    /// <summary>
    /// A progress bar for the eyes only. NVDA announced every change of a real one ("33 percent",
    /// "0% complete") each time a task changed; the Progress line above says the same thing once, when asked.
    /// </summary>
    public sealed class QuietProgressBar : ProgressBar
    {
        protected override System.Windows.Automation.Peers.AutomationPeer? OnCreateAutomationPeer() => null;
    }
}
