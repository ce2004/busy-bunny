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
        // The tabs sit in a list down the left side, one under another, so many lists never wrap into a grid.
        private readonly TabControl _tabs = new() { Margin = new Thickness(6, 0, 6, 0), TabStripPlacement = Dock.Left };
        // The chosen task as text, to read by character or word with the arrows (and on braille): a list item can't be.
        private readonly TextBox _detail = new() { IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8, 8, 8, 0), MinHeight = 28 };
        private readonly ComboBox _status = new() { Width = 240, Margin = new Thickness(0, 0, 16, 0) };
        private readonly ComboBox _move = new() { Width = 240 };
        private readonly MenuItem _shortMenu = new() { Header = "_Short mode", IsCheckable = true, InputGestureText = "Ctrl+Shift+S" };
        private readonly MenuItem _soundsOnMenu = new() { Header = "Sounds o_n", IsCheckable = true };
        private readonly MenuItem _checkAtStartMenu = new() { Header = "Check for updates _automatically", IsCheckable = true };
        private MenuItem _updateMenu = new();
        private readonly MenuItem _moveMenu = new() { Header = "_Move to list" };

        // Every task list on screen, by a key that stays the same across refreshes, so focus can come back to it.
        private readonly Dictionary<string, ListBox> _lists = new();
        private readonly ListBox _todo, _overdue, _due, _priority, _focus, _focusOverdue;
        // The bottom of the main screen, under Status and Move to: Talk to Bunny lives here.
        private readonly StackPanel _bottom = new() { Margin = new Thickness(8, 0, 8, 8) };
        private readonly TabItem _todayTab, _priorityTab, _focusTab, _upcomingTab;
        private readonly Button _getAhead = Layout.Button("_Get ahead");
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
            AutomationProperties.SetName(_tabs, "Tabs");
            AutomationProperties.SetName(_progress, "Progress");
            _todo = NewList("today-todo");
            _due = NewList("today-due");
            _priority = NewList("priority");
            _focus = NewList("focus");
            _overdue = NewList("today-overdue");
            _focusOverdue = NewList("focus-overdue");

            // Today (Morgan's issue 10): what she planned to do today, then everything overdue (issue 14),
            // then what is due today, then a button to the Upcoming tab for getting ahead on the week.
            var today = new StackPanel { Margin = new Thickness(6) };
            today.Children.Add(new Label { Content = "To _do today", Target = _todo, Padding = new Thickness(0, 4, 0, 2) });
            today.Children.Add(_todo);
            today.Children.Add(new Label { Content = "Ove_rdue", Target = _overdue, Padding = new Thickness(0, 10, 0, 2) });
            today.Children.Add(_overdue);
            today.Children.Add(new Label { Content = "Du_e today", Target = _due, Padding = new Thickness(0, 10, 0, 2) });
            today.Children.Add(_due);
            // Alt G: Alt A is Status. Every access key in a window needs its own letter, or WPF only cycles focus.
            _getAhead.HorizontalAlignment = HorizontalAlignment.Left;
            _getAhead.Margin = new Thickness(0, 10, 0, 0);
            AutomationProperties.SetHelpText(_getAhead, "Opens the Upcoming tab, to pick something from later this week if you have the energy.");
            _getAhead.Click += (_, _) => GetAhead();
            today.Children.Add(_getAhead);
            _todayTab = new TabItem { Header = "Today", Content = new ScrollViewer { Content = today, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            _tabs.Items.Add(_todayTab);

            // Priority (Morgan's issue 11): everything urgent from every list, so Today and Upcoming stay clean.
            var priority = new StackPanel { Margin = new Thickness(6) };
            priority.Children.Add(_priority);
            _priorityTab = new TabItem { Header = "Priority", Content = new ScrollViewer { Content = priority, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            _tabs.Items.Add(_priorityTab);

            // Focus: two or three tasks, picked by the urgent rules; Swap trades the chosen one for the next.
            // Morgan's issue 14: overdue tasks first, then the two or three for today.
            var focus = new StackPanel { Margin = new Thickness(6) };
            focus.Children.Add(new Label { Content = "Ove_rdue", Target = _focusOverdue, Padding = new Thickness(0, 4, 0, 2) });
            focus.Children.Add(_focusOverdue);
            focus.Children.Add(new Label { Content = "Focus _now", Target = _focus, Padding = new Thickness(0, 10, 0, 2) });
            focus.Children.Add(_focus);
            var swap = Layout.Button("S_wap this one for another");
            swap.HorizontalAlignment = HorizontalAlignment.Left;
            swap.Click += (_, _) => SwapFocus();
            focus.Children.Add(swap);
            _focusTab = new TabItem { Header = "Focus", Content = focus };
            _tabs.Items.Add(_focusTab);

            _upcomingTab = new TabItem { Header = "Upcoming", Content = new ScrollViewer { Content = _upcoming, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } };
            _tabs.Items.Add(_upcomingTab);

            // Left and Right Arrow on the tab row move through the tabs and stay there, like any tab
            // control; Control Tab and Control Page Down jump straight into the new tab's tasks.
            _tabs.SelectionChanged += (s, e) =>
            {
                if (e.Source != _tabs) return;
                // Only the open tab is kept up to date on every change (spec 9.9); a tab is filled as it opens.
                if (_tabs.SelectedItem is TabItem opened)
                {
                    FillIfDirty(opened);
                    TabOpened?.Invoke(opened);
                }
                if (!_jumpIntoTab) return;
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
                // Coming in from outside, the picker takes the chosen task (or tasks) and keeps them until
                // she leaves, so a commit that moves the task off the list never acts on the next one.
                c.GotKeyboardFocus += (_, e) => SyncPickers(capture: e.OldFocus is not ComboBoxItem && e.OldFocus != _status && e.OldFocus != _move);
                c.PreviewKeyDown += (_, e) =>
                {
                    if (c.IsDropDownOpen) return;
                    if (e.Key == Key.Enter) { e.Handled = true; CommitPicker(c); }
                    // Escape backs out: the picker shows the task's own choice again and focus goes back to the list.
                    else if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; SyncPickers(capture: false); BackToTasks(); }
                };
                c.DropDownClosed += (_, _) => { if (c.IsKeyboardFocusWithin) CommitPicker(c); };
                // Leaving for another window (NewFocus is null then) is not a choice: only moving on inside Busy Bunny is.
                c.LostKeyboardFocus += (_, e) => { if (e.NewFocus != null && !c.IsKeyboardFocusWithin && !c.IsDropDownOpen) CommitPicker(c); };
            }
            var pickers = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 8, 8, 10) };
            // Alt A: Alt U is the Fun menu.
            pickers.Children.Add(new Label { Content = "St_atus", Target = _status, VerticalAlignment = VerticalAlignment.Center });
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
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(_detail, 1);
            Grid.SetRow(pickers, 2);
            Grid.SetRow(_bottom, 3);
            body.Children.Add(_tabs);
            body.Children.Add(_detail);
            body.Children.Add(pickers);
            body.Children.Add(_bottom);
            dock.Children.Add(body);
            KeyboardNavigation.SetTabNavigation(dock, KeyboardNavigationMode.Cycle);
            Content = dock;

            PreviewKeyDown += OnKeys;
            Loaded += (_, _) => { Refresh(); FocusTab(announce: false); };
            Activated += (_, _) =>
            {
                CheckNewDay();
                Dispatcher.BeginInvoke(OfferUpdate, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            var minute = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            minute.Tick += (_, _) => { CheckNewDay(); MinuteTick?.Invoke(); OfferUpdate(); };
            minute.Start();
            Closed += (_, _) => { _closing = true; minute.Stop(); };
            AddTabMenus();
            foreach (var f in Features.All) f.Attach(this);
            _newDayFor = DateTime.Now.Date;
            Refresh();
            _urgentBefore = _store.Today(DateTime.Now).ToDo.Count;
            MenuKeyProblems = AccessKeys.MenuProblems(menu);
        }

        // The day NewDay last ran for: only CheckNewDay moves it, so a Refresh just after midnight cannot skip the new day.
        private DateTime _newDayFor;
        private bool _jumpIntoTab;
        /// <summary>Menus with two items on one access key in the main window built last (the self-test reads it).</summary>
        public static List<string> MenuKeyProblems { get; private set; } = new();
        /// <summary>The headers whose access key AddMenuItem had to move, because the letter was taken in that menu.</summary>
        public static readonly List<string> MovedMenuKeys = new();

        // Any key in any Busy Bunny window: the update offer waits for a quiet spell (spec 9.5).
        private static DateTime _lastInput = DateTime.Now;
        static MainWindow()
        {
            EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler((_, _) => _lastInput = DateTime.Now), true);
        }

        // ---- For features (see Features.cs) ----

        public Store Store => _store;
        public Settings AppSettings => _settings;
        /// <summary>The task chosen in the list used last, or null.</summary>
        public TaskItem? ChosenTask => Current();
        /// <summary>The list whose tab is open, or the first list when Today, Focus or Upcoming is.</summary>
        /// <summary>The list whose tab is open, or null when another tab is.</summary>
        public string? OpenListName => _tabs.SelectedItem is TabItem { Tag: string l } ? l : null;
        public string CurrentListName => _tabs.SelectedItem is TabItem { Tag: string l } ? l : _store.ActiveLists.FirstOrDefault()?.Name ?? "Homework";
        public void Say(string text, bool interrupt = false) => Speech.Say(this, text, interrupt);
        /// <summary>Saves, then rebuilds every list.</summary>
        public void SaveAndRefresh() { Save(); Refresh(); }
        /// <summary>Features add text to the Task field here (like boss health); null adds nothing.</summary>
        public readonly List<Func<TaskItem, string?>> DetailParts = new();
        /// <summary>Moves focus to the task in Today or Focus, if it shows there. Returns whether it did.</summary>
        public bool SelectTask(TaskItem t)
        {
            foreach (var lb in new[] { _todo, _overdue, _due, _priority, _focusOverdue, _focus })
            {
                int at = IndexOf(lb, t.Id);
                if (at < 0) continue;
                _tabs.SelectedItem = lb == _focus || lb == _focusOverdue ? _focusTab : lb == _priority ? _priorityTab : _todayTab;
                _lastList = lb;
                Dispatcher.BeginInvoke(() => { TextViewer.Select(lb, at); ShowDetail(); }, System.Windows.Threading.DispatcherPriority.Input);
                return true;
            }
            return false;
        }
        /// <summary>Changes a task's status as Control 1 to 6 would, with its sounds and words.</summary>
        public void SetTaskStatus(TaskItem t, Status s) { _lastChosen = t; try { SetStatus(s); } finally { _lastChosen = null; } ShowDetail(); }
        private TaskItem? _lastChosen;
        /// <summary>The list tab's task list for a list name, or null.</summary>
        public ListBox? ListBoxOf(string listName) => _lists.TryGetValue("list-" + listName, out var lb) ? lb : null;
        /// <summary>Adds something to the bottom of the main screen, after Status and Move to.</summary>
        public void AddBottom(UIElement e) => _bottom.Children.Add(e);
        /// <summary>The names of the tabs, in order, as they show.</summary>
        public List<string> TabNames => _tabs.Items.OfType<TabItem>().Select(t => t.Header?.ToString() ?? "").ToList();
        /// <summary>Opens a tab by its name (Today, Focus, Homework...) and goes into its tasks. False when there is none.</summary>
        public bool OpenTab(string name)
        {
            var tab = _tabs.Items.OfType<TabItem>().FirstOrDefault(t => string.Equals(t.Header?.ToString(), name, StringComparison.OrdinalIgnoreCase) || Equals(t.Tag, name))
                ?? _tabs.Items.OfType<TabItem>().FirstOrDefault(t => (t.Header?.ToString() ?? "").StartsWith(name, StringComparison.OrdinalIgnoreCase));
            if (tab == null) return false;
            _tabs.SelectedItem = tab;
            Dispatcher.BeginInvoke(() => FocusTab(false), System.Windows.Threading.DispatcherPriority.Input);
            return true;
        }
        /// <summary>After settings change outside the Settings window: the menu check marks, sounds and lists follow.</summary>
        public void SettingsChanged()
        {
            _settings.Save();
            _settings.Apply();
            _shortMenu.IsChecked = _settings.ShortMode;
            _soundsOnMenu.IsChecked = _settings.SoundsOn;
            _checkAtStartMenu.IsChecked = _settings.CheckForUpdatesAtStart;
            if (_settings.CheckForUpdatesAtStart) StartAutoUpdates();
            Save();
            Refresh();
        }
        /// <summary>Puts the main screen back on the list used last, or the open tab.</summary>
        public void BackToTasks()
        {
            if (_lastList != null && _lists.ContainsValue(_lastList) && _lastList.IsVisible) TextViewer.Select(_lastList, Math.Max(0, _lastList.SelectedIndex));
            else FocusTab(false);
        }
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

        /// <summary>
        /// Runs each startup feature in turn (setup guide, morning check-in...), stopping once Busy
        /// Bunny is closing: an update installed from inside the setup guide closed the window, and the
        /// check-in after it then failed (Morgan, 0.5.0).
        /// </summary>
        public void OnStarted()
        {
            if (Started == null) return;
            foreach (Action step in Started.GetInvocationList())
            {
                if (_closing) return;
                step();
            }
        }

        private bool _closing;
        /// <summary>Quick add and Edit task, for features (reminders, hotkeys, the snooze limit).</summary>
        public void OpenQuickAdd() => QuickAddTask();
        public void OpenEditTask() => EditTask();

        /// <summary>
        /// Adds a task as Quick add does: undoable, saved, announced. remember false is for a task that
        /// belongs to a change already remembered (the next copy of a repeating task), so one Control Z undoes both.
        /// </summary>
        public void AddTask(TaskItem t, bool announce = true, bool remember = true)
        {
            if (remember) _store.Remember("adding " + t.Name);
            QuickAdd.KeepNewTag(_store);
            _store.Data.Tasks.Add(t);
            Save();
            Refresh();
            Sounds.PlayFor(Sounds.Event.TaskAdded, _store.ListOf(t));
            if (announce) Speech.Say(this, "Added " + t.Name + " to " + t.List + ".");
            TaskAdded?.Invoke(t);
            WarnIfOverloaded();
        }

        /// <summary>Adds several tasks as one change: one undo step, one save, one sound (Add several tasks, spec 9.8).</summary>
        public void AddTasks(IReadOnlyList<TaskItem> tasks)
        {
            if (tasks.Count == 0) return;
            if (tasks.Count == 1) { AddTask(tasks[0], announce: false); return; }
            _store.Remember("adding " + Count(tasks.Count));
            _store.Data.Tasks.AddRange(tasks);
            Save();
            Refresh();
            Sounds.PlayFor(Sounds.Event.TaskAdded, _store.ListOf(tasks[0]));
            foreach (var t in tasks) TaskAdded?.Invoke(t);
            WarnIfOverloaded();
        }

        private readonly Dictionary<string, MenuItem> _menus = new();
        private Separator? _exitSeparator;
        /// <summary>Adds an item to a top menu: File, Task, View, Fun, Options or Help. In File it goes above Exit, which stays last.</summary>
        public MenuItem AddMenuItem(string menu, string header, Action act, string gesture = "")
        {
            var top = _menus[menu];
            // A letter already used in this menu would make both items dead keys (WPF only moves between
            // them), so a clashing access key moves to a free letter of the header.
            var used = top.Items.OfType<MenuItem>().Select(i => AccessKeys.Of(i.Header as string)).Where(k => k != null).Select(k => k!.Value).ToHashSet();
            if (AccessKeys.Of(header) is char letter && used.Contains(letter))
            {
                string moved = AccessKeys.Move(header, used);
                MovedMenuKeys.Add(menu + ": " + header + " became " + moved);
                header = moved;
            }
            var m = new MenuItem { Header = header, InputGestureText = gesture };
            m.Click += (_, _) => act();
            int at = menu == "File" && _exitSeparator != null ? top.Items.IndexOf(_exitSeparator) : -1;
            if (at >= 0) top.Items.Insert(at, m);
            else top.Items.Add(m);
            top.Visibility = Visibility.Visible;
            return m;
        }

        private readonly List<string> _helpLines = new();
        /// <summary>A line for Help, Keyboard shortcuts with no key of its own here, like a key that works in every program.</summary>
        public void AddHelp(string line) => _helpLines.Add(line);

        /// <summary>A tab was opened, by her or by a feature: features fill a tab they let go stale here.</summary>
        public event Action<TabItem>? TabOpened;

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
            var today = DateTime.Now.Date;
            if (today == _newDayFor) return;
            _newDayFor = today;
            Refresh();
            NewDay?.Invoke(today);
        }

        private ListBox NewList(string key)
        {
            // Extended (Morgan's issue 14): Shift with the arrows selects several tasks, or Control with the
            // arrows then Control Space picks them one by one; then the task actions work on all of them.
            var lb = new ListBox { MinHeight = 40, MaxHeight = 420, BorderThickness = new Thickness(1), SelectionMode = SelectionMode.Extended };
            ScrollViewer.SetCanContentScroll(lb, true);
            lb.GotKeyboardFocus += (_, e) => { if (e.OriginalSource is ListBoxItem item) _focusedItem = item; _lastList = lb; ShowDetail(); };
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
            file.Items.Add(Item("Rea_d my day", () => Speech.Say(this, _store.Summary(DateTime.Now, _settings.ShortMode), true), "Ctrl+R"));
            file.Items.Add(new Separator());
            file.Items.Add(Item("Open the backups _folder", () => Open(Store.BackupFolder)));
            file.Items.Add(Item("Start over with the _sample tasks...", ResetSample));
            _exitSeparator = new Separator();
            file.Items.Add(_exitSeparator);
            // No Alt+F4 here: with Keep in tray on, Alt F4 only hides the window, and Exit really quits.
            file.Items.Add(Item("E_xit", () => { Reminders.Quitting = true; Close(); }));

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
            task.Items.Add(Item("De_lete", DeleteTask, "Delete"));
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
            help.Items.Add(Item("Report a problem on _GitHub", () => Open(Updater.Website + "/issues/new")));
            help.Items.Add(Item("Request a _feature", () => RequestFeature()));

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

        /// <summary>
        /// Rebuilds the lists from the data, keeping focus on the same task, or the one now in its place.
        /// Today, Priority and Focus are always filled; Upcoming and the list tabs only when open, and
        /// the others as they open (spec 9.9: a change with 5,000 tasks must not rebuild every tab).
        /// </summary>
        public void Refresh()
        {
            var now = DateTime.Now;
            _refreshing = true;
            try { SyncListTabs(); } finally { _refreshing = false; }
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

            var (todo, dueToday) = _store.Today(now);
            var overdue = _store.Overdue(now);
            Fill(_todo, todo, "Today, to do", "Nothing planned for today. Enjoy that while it lasts.");
            Fill(_overdue, overdue, "Overdue", "Nothing overdue. Look at you.");
            Fill(_due, dueToday.Where(t => !Store.IsOverdue(t, now)), "Due today", "Nothing due today.");
            Fill(_focusOverdue, overdue, "Overdue", "Nothing overdue.");
            Fill(_priority, _store.Priority(now), "Priority", "Nothing urgent. Enjoy that while it lasts.");
            int ahead = _store.GetAhead(now).Count;
            string aheadText = ahead == 0 ? "_Get ahead: nothing else this week" : "_Get ahead: " + ahead + " more this week";
            if (!Equals(_getAhead.Content, aheadText)) _getAhead.Content = aheadText;
            // Focus for today: the overdue ones have their own list just above.
            var overdueSet = overdue.ToHashSet();
            var urgent = _store.TodayAll(now).Where(t => !overdueSet.Contains(t)).ToList();
            var urgentSet = urgent.ToHashSet();
            var focusPool = urgent.Concat(_store.Reorder(_store.Data.Tasks.Where(t => t.Status is Status.ThisWeek && !urgentSet.Contains(t) && _store.CanShowToday(t, now)).OrderBy(_store.Rank).ThenBy(t => t.Priority == 0 ? 9 : t.Priority).ToList(), now)).ToList();
            if (focusPool.Count > 0 && focusPool.All(t => _focusSkipped.Contains(t.Id))) _focusSkipped.Clear();
            Fill(_focus, focusPool.Where(t => !_focusSkipped.Contains(t.Id)).Take(3), "Focus now", "Nothing to focus on. Suspicious, but okay.");

            var open = _tabs.SelectedItem as TabItem;
            if (open == _upcomingTab) FillUpcoming(now); else _upcomingDirty = true;
            foreach (var tab in _tabs.Items.OfType<TabItem>())
                if (tab.Tag is string top)
                {
                    if (tab == open) { _dirtyTabs.Remove(top); FillListTab(top, now); }
                    else _dirtyTabs.Add(top);
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
                // Still on the very same item (it was updated in place): leave focus alone, so NVDA does not read the task again.
                bool same = at >= 0 && Keyboard.FocusedElement is ListBoxItem fi && back.Items.IndexOf(fi) == at;
                if (!same) TextViewer.Select(back, at >= 0 ? at : Math.Min(focusIndex, back.Items.Count - 1));
            }
            else if (focusKey != null) FocusTab(false); // its list went away (an Upcoming day emptied): the tab's first list
            ShowDetail();
        }

        private bool _refreshing, _upcomingDirty = true;
        private readonly HashSet<string> _dirtyTabs = new();
        private string _upcomingSignature = "";

        /// <summary>Fills a tab that went stale while another one was open.</summary>
        private void FillIfDirty(TabItem tab)
        {
            if (_refreshing) return;
            var now = DateTime.Now;
            if (tab == _upcomingTab && _upcomingDirty) FillUpcoming(now);
            else if (tab.Tag is string top && _dirtyTabs.Remove(top)) FillListTab(top, now);
        }

        /// <summary>Upcoming: a group per day, like Tuesday, 4 tasks: due that day or planned for it. Day lists are kept while the days stay the same.</summary>
        private void FillUpcoming(DateTime now)
        {
            _upcomingDirty = false;
            var open = _store.Data.Tasks.Where(t => t.Status != Status.Done).ToList();
            var dated = open.Where(t => t.Due is DateTime d && d.Date >= now.Date).Select(t => (Day: t.Due!.Value.Date, Task: t))
                .Concat(open.Where(t => t.DoDate is DateTime p && p.Date > now.Date && t.DoDate!.Value.Date != t.Due?.Date).Select(t => (Day: t.DoDate!.Value.Date, Task: t)))
                .GroupBy(x => x.Day, x => x.Task).OrderBy(g => g.Key).ToList();
            string signature = dated.Count == 0 ? "none" : string.Join("|", dated.Select(g => g.Key.ToString("yyyy-MM-dd")));
            bool rebuild = signature != _upcomingSignature || _upcoming.Children.Count == 0;
            _upcomingSignature = signature;
            if (rebuild)
            {
                foreach (var key in _lists.Keys.Where(k => k.StartsWith("up-")).ToList()) _lists.Remove(key);
                _upcoming.Children.Clear();
            }
            if (dated.Count == 0)
            {
                var none = rebuild ? NewList("up-none") : (ListBox)_upcoming.Children[0];
                if (rebuild) _upcoming.Children.Add(none);
                Fill(none, Array.Empty<TaskItem>(), "Upcoming", "Nothing with a date coming up.");
                return;
            }
            bool first = true;
            int child = 0;
            foreach (var g in dated)
            {
                TextBlock head;
                ListBox lb;
                if (rebuild)
                {
                    head = new TextBlock { Margin = new Thickness(6, 10, 0, 2), FontWeight = FontWeights.SemiBold };
                    lb = NewList("up-" + g.Key.ToString("yyyy-MM-dd"));
                    _upcoming.Children.Add(head);
                    _upcoming.Children.Add(lb);
                }
                else
                {
                    head = (TextBlock)_upcoming.Children[child];
                    lb = (ListBox)_upcoming.Children[child + 1];
                }
                child += 2;
                string day = Words.Day(g.Key, now);
                day = char.ToUpper(day[0]) + day[1..];
                int n = g.Count();
                string headText = day + ", " + n + (n == 1 ? " task" : " tasks");
                if (head.Text != headText) head.Text = headText;
                Fill(lb, g.OrderBy(t => t.Due?.Date == g.Key ? 0 : 1).ThenBy(t => t.Due).ThenBy(_store.Rank), (first ? "Upcoming, " : "") + day, "");
                first = false;
            }
        }

        /// <summary>A list tab: its own list and each category, then its sub-lists the same way. The filter and sort apply (Views).</summary>
        private void FillListTab(string top, DateTime now)
        {
            foreach (var l in _store.ActiveLists.Where(l => (l.Name == top || l.Parent == top) && _lists.ContainsKey("list-" + l.Name)).ToList())
            {
                var tasks = _store.Data.Tasks.Where(t => t.List == l.Name && t.Status != Status.Done)
                    .OrderBy(t => t.Status).ThenBy(t => t.Priority == 0 ? 9 : t.Priority).ThenBy(t => t.Due ?? DateTime.MaxValue).ToList();
                // Tasks with no section (or one the list no longer has) first, then each section as its own named list.
                Views.FillView(this, _lists["list-" + l.Name], tasks.Where(t => !l.Sections.Contains(t.Section)), l.Name, "Nothing on " + l.Name + ".");
                foreach (var sec in l.Sections)
                    if (_lists.TryGetValue(SectionKey(l, sec), out var slb))
                        Views.FillView(this, slb, tasks.Where(t => t.Section == sec), l.Name + ", " + sec, "Nothing in " + sec + ".");
            }
        }

        /// <summary>
        /// Fills a task list. When the same tasks are in the same order, the items are updated in place,
        /// so the focused one keeps focus and NVDA does not read it all again after every action.
        /// </summary>
        private void Fill(ListBox lb, IEnumerable<TaskItem> tasks, string name, string empty)
        {
            var now = DateTime.Now;
            var rows = tasks.Select(t => (Id: (Guid?)t.Id, Text: _store.Line(t, now, _settings.ShortMode))).ToList();
            int count = rows.Count;
            if (count == 0 && empty.Length > 0) rows.Add((null, empty));
            string listName = name + ", " + count + (count == 1 ? " task" : " tasks");
            if (AutomationProperties.GetName(lb) != listName) AutomationProperties.SetName(lb, listName);
            bool sameRows = lb.Items.Count == rows.Count;
            for (int i = 0; sameRows && i < rows.Count; i++)
                if (lb.Items[i] is not ListBoxItem it || (it.Tag as Guid?) != rows[i].Id) sameRows = false;
            if (sameRows)
            {
                for (int i = 0; i < rows.Count; i++)
                    if (lb.Items[i] is ListBoxItem it && !Equals(it.Content, rows[i].Text)) it.Content = rows[i].Text;
                if (lb.SelectedIndex < 0 && lb.Items.Count > 0) lb.SelectedIndex = 0;
                return;
            }
            int keep = lb.SelectedIndex;
            lb.Items.Clear();
            foreach (var (id, text) in rows)
                lb.Items.Add(id is Guid g ? new ListBoxItem { Content = text, Tag = g } : new ListBoxItem { Content = text });
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
            ListBox? first = tab switch
            {
                _ when tab == _todayTab => _todo,
                _ when tab == _priorityTab => _priority,
                _ when tab == _focusTab => _focusOverdue.Items.OfType<ListBoxItem>().Any(i => i.Tag is Guid) ? _focusOverdue : _focus,
                _ when tab == _upcomingTab => _upcoming.Children.OfType<ListBox>().FirstOrDefault(),
                _ when _firstLists.TryGetValue(tab, out var f) => f(),
                _ => _lists.TryGetValue("list-" + tab.Tag, out var lb) ? lb : null,
            };
            if (first != null) TextViewer.Select(first, Math.Max(0, first.SelectedIndex));
        }

        // ---- Acting on the chosen task ----

        private ListBoxItem? _focusedItem;

        /// <summary>The task the cursor is on in the list used last (with several selected, the one last moved to).</summary>
        private TaskItem? Current()
        {
            if (_lastChosen != null) return _lastChosen;
            var lb = _lastList;
            if (lb == null) return null;
            object? item = _focusedItem != null && lb.Items.Contains(_focusedItem) ? _focusedItem : lb.SelectedItem;
            if ((item as ListBoxItem)?.Tag is not Guid id) return null;
            return _store.Data.Tasks.FirstOrDefault(t => t.Id == id);
        }

        /// <summary>Every selected task in the list used last when there are several, otherwise just the chosen one.</summary>
        private List<TaskItem> ChosenTasks()
        {
            if (_lastChosen != null) return new() { _lastChosen };
            if (_lastList is ListBox lb && lb.SelectedItems.Count > 1)
            {
                var ids = lb.SelectedItems.OfType<ListBoxItem>().Select(i => i.Tag).OfType<Guid>().ToHashSet();
                var many = _store.Data.Tasks.Where(t => ids.Contains(t.Id)).ToList();
                if (many.Count > 1) return many;
            }
            return Current() is TaskItem t ? new() { t } : new();
        }

        private static string Count(int n) => n + (n == 1 ? " task" : " tasks");

        /// <summary>The Task field: the chosen task's line, then where it lives and how big it is.</summary>
        private void ShowDetail()
        {
            var t = Current();
            string text = "";
            if (t != null)
            {
                var list = _store.ListOf(t);
                text = _store.Line(t, DateTime.Now, _settings.ShortMode) + ". List: " + t.List + (t.Section.Length > 0 ? ", category " + t.Section : "") + ". Size: " + Words.Of(t.Size) + ".";
                // Just the task's own level; the whole scale is in View tab settings (Morgan's issue 6).
                if (list.Scale.Length > 0)
                    text += " Priority: " + (t.Priority > 0 && t.Priority <= list.Scale.Length ? list.Scale[t.Priority - 1] + ", " + t.Priority + " of " + list.Scale.Length : "none") + ".";
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

        /// <summary>A status chosen by hand moves the do date along: Today plans it for today, This week or Next week takes it off a do date that has come.</summary>
        private static void ApplyStatus(TaskItem t, Status s, DateTime now)
        {
            t.Status = s;
            t.TouchedAt = now;
            t.StatusSince = now;
            if (s == Status.Today && t.DoDate?.Date > now.Date) t.DoDate = now.Date;
            else if (s is Status.ThisWeek or Status.NextWeek && t.DoDate?.Date <= now.Date) t.DoDate = null;
            t.DoneAt = s == Status.Done ? now : null;
        }

        /// <summary>Several selected tasks at once (Morgan's issue 14): one undo step, one sound, one sentence.</summary>
        private void SetStatusMany(List<TaskItem> tasks, Status s)
        {
            var change = tasks.Where(t => t.Status != s).ToList();
            if (change.Count == 0) { Speech.Say(this, "They are all " + Words.Of(s) + " already.", true); return; }
            var now = DateTime.Now;
            _store.Remember((s == Status.Done ? "marking " : "changing the status of ") + Count(change.Count));
            var olds = change.Select(t => (Task: t, Old: t.Status)).ToList();
            foreach (var t in change) ApplyStatus(t, s, now);
            Save();
            Refresh();
            if (s == Status.Done) Sounds.PlayFor(Sounds.Event.TaskDone, _store.ListOf(change[0]));
            Speech.Say(this, s == Status.Done ? "Done: " + Count(change.Count) + ". " + _store.Progress(now).Done + " of " + _store.Progress(now).Total + " today."
                : Count(change.Count) + " now " + Words.Of(s) + ".");
            foreach (var (t, old) in olds) StatusChanged?.Invoke(t, old);
            WarnIfOverloaded();
        }

        private void SetStatus(Status s, List<TaskItem>? given = null)
        {
            var chosen = given ?? ChosenTasks();
            if (chosen.Count > 1) { SetStatusMany(chosen, s); return; }
            if ((chosen.Count == 1 ? chosen[0] : CurrentOrSay()) is not TaskItem t) return;
            if (t.Status == s) { Speech.Say(this, "It is already " + Words.Of(s) + ".", true); return; }
            var now = DateTime.Now;
            int doneBefore = _store.Progress(now).Done;
            _store.Remember((s == Status.Done ? "marking " : "changing the status of ") + t.Name);
            var old = t.Status;
            ApplyStatus(t, s, now);
            Save();
            Refresh();
            AfterStatusChange(t, old, doneBefore, now);
        }

        /// <summary>
        /// After one task's status changed, saved and shown: its sound and words, then StatusChanged
        /// (points, the next copy of a repeating task, bets) and the overload heads-up. Control 1 to 6,
        /// the Status picker, features and Edit task all end here.
        /// </summary>
        private void AfterStatusChange(TaskItem t, Status old, int doneBefore, DateTime now)
        {
            var s = t.Status;
            if (s == Status.Done)
            {
                var (done, total, _, _) = _store.Progress(now);
                if (done == total && total > 0 && doneBefore < done)
                {
                    Sounds.PlayFor(Sounds.Event.DailyGoal, _store.ListOf(t));
                    int ahead = _store.GetAhead(now).Count;
                    Speech.Say(this, "Done: " + t.Name + ". That is everything for today. " + done + " of " + total + "."
                        + (ahead > 0 ? " " + ahead + " more this week if you have the energy: Get ahead, at the bottom of Today." : ""));
                }
                else
                {
                    string finish = _store.Data.Lists.FirstOrDefault(x => x.Name == t.List)?.FinishSound ?? "";
                    if (finish.Length > 0) { if (Sounds.Enabled) Sounds.PlayNamed(finish); }
                    else Sounds.PlayFor(Sounds.Event.TaskDone, _store.ListOf(t));
                    Speech.Say(this, "Done: " + t.Name + ". " + done + " of " + total + " today.");
                }
            }
            else Speech.Say(this, t.Name + ": " + Words.Of(s) + ".");
            StatusChanged?.Invoke(t, old);
            WarnIfOverloaded();
        }

        private void MoveTo(string list, List<TaskItem>? given = null)
        {
            var chosen = given ?? ChosenTasks();
            if (chosen.Count > 1)
            {
                var move = chosen.Where(x => x.List != list).ToList();
                if (move.Count == 0) { Speech.Say(this, "They are all on " + list + " already.", true); return; }
                _store.Remember("moving " + Count(move.Count));
                var newScale = _store.Data.Lists.FirstOrDefault(l => l.Name == list)?.Scale ?? Array.Empty<string>();
                foreach (var x in move)
                {
                    x.List = list;
                    if (!_store.ListOf(x).Sections.Contains(x.Section)) x.Section = "";
                    if (x.Priority > newScale.Length) x.Priority = newScale.Length;
                }
                Save();
                Refresh();
                Speech.Say(this, "Moved " + Count(move.Count) + " to " + list + ".");
                return;
            }
            if ((chosen.Count == 1 ? chosen[0] : CurrentOrSay()) is not TaskItem t) return;
            if (t.List == list) { Speech.Say(this, "It is already on " + list + ".", true); return; }
            _store.Remember("moving " + t.Name);
            var scale = _store.Data.Lists.FirstOrDefault(l => l.Name == list)?.Scale ?? Array.Empty<string>();
            t.List = list;
            if (!_store.ListOf(t).Sections.Contains(t.Section)) t.Section = ""; // a category the new list does not have
            if (t.Priority > scale.Length) t.Priority = scale.Length; // the new list's scale may be shorter
            Save();
            Refresh();
            Speech.Say(this, "Moved " + t.Name + " to " + list + ".");
        }

        private void DeleteTask()
        {
            var chosen = ChosenTasks();
            if (chosen.Count > 1)
            {
                _store.Remember("deleting " + Count(chosen.Count));
                foreach (var x in chosen) _store.Data.Tasks.Remove(x);
                Save();
                Refresh();
                Speech.Say(this, "Deleted " + Count(chosen.Count) + ". Control Z brings them back.");
                return;
            }
            if (CurrentOrSay() is not TaskItem t) return;
            _store.Remember("deleting " + t.Name);
            _store.Data.Tasks.Remove(t);
            Save();
            Refresh();
            Speech.Say(this, "Deleted " + t.Name + ". Control Z brings it back.");
        }

        private void Undo()
        {
            string? what;
            // Store.Undo saves; a locked or full disk must not turn an undo that already happened into an error box.
            // DataSafety announces a failed save and keeps retrying.
            try { what = _store.Undo(); }
            catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException) { what = "that"; }
            bool saved = !_store.Dirty;
            Refresh();
            Speech.Say(this, what == null ? "Nothing to undo." : "Undid " + what + "." + (saved ? "" : " It is not saved yet; Busy Bunny keeps trying."), true);
        }

        private void QuickAddTask()
        {
            // The open list tab, or the first list that is not archived (never a list that is gone).
            string list = CurrentListName;
            string section = "";
            // From inside a category or sub-list on the open tab, the new task goes there.
            if (_lastList != null && _placeOf.TryGetValue(_lastList, out var place) && _tabs.SelectedItem is TabItem { Tag: string open }
                && (place.List == open || _store.Data.Lists.Any(x => x.Name == place.List && x.Parent == open)))
                (list, section) = place;
            var status = _tabs.SelectedItem == _todayTab || _tabs.SelectedItem == _focusTab ? Status.Today : _store.Data.Lists.FirstOrDefault(x => x.Name == list)?.DefaultStatus ?? Status.ThisWeek;
            if (QuickAdd.Show(this, _store, list, status, startSection: section) is not TaskItem t) return;
            AddTask(t, announce: false);
            Speech.Say(this, "Added " + t.Name + " to " + t.List + "." + QuickAdd.Taken);
        }

        /// <summary>Edit task (F2): the Quick add form, filled in with the chosen task. Save changes it in place; Control Z undoes it.</summary>
        private void EditTask()
        {
            if (CurrentOrSay() is not TaskItem t) return;
            var back = _lastList;
            string? backKey = back == null ? null : ListKeyOf(back);
            if (QuickAdd.Show(this, _store, t.List, t.Status, t) is not TaskItem edited) { if (back != null) TextViewer.Select(back, Math.Max(0, back.SelectedIndex)); return; }
            var now = DateTime.Now;
            int doneBefore = _store.Progress(now).Done;
            var old = t.Status;
            _store.Remember("editing " + t.Name);
            QuickAdd.KeepNewTag(_store);
            if (edited.Status != old) { t.StatusSince = now; t.TouchedAt = now; }
            QuickAdd.Apply(edited, t);
            Save();
            Refresh();
            // Back on the same task, wherever it now sits; if it moved off this list, the list keeps its place.
            if (backKey != null && _lists.TryGetValue(backKey, out var lb))
            {
                int at = IndexOf(lb, t.Id);
                TextViewer.Select(lb, at >= 0 ? at : Math.Min(Math.Max(0, lb.SelectedIndex), lb.Items.Count - 1));
            }
            // A new status goes the same way as Control 1 to 6 (its sound and words, points, the next
            // copy of a repeating task, bets); otherwise it is just saved.
            if (t.Status != old) { AfterStatusChange(t, old, doneBefore, now); return; }
            Sounds.PlayFor(Sounds.Event.TaskSaved, _store.ListOf(t));
            Speech.Say(this, "Saved " + t.Name + ".");
            WarnIfOverloaded();
        }

        /// <summary>The key a task list is kept under, so focus can find it again after it is rebuilt.</summary>
        private string? ListKeyOf(ListBox lb)
        {
            foreach (var (k, l) in _lists) if (l == lb) return k;
            return null;
        }

        /// <summary>
        /// Where focus is now, to go back to after a dialog: a task (by its list and id, as the list may
        /// be rebuilt meanwhile) or any other control. With neither left, the open tab's first list.
        /// </summary>
        private Action RememberFocus()
        {
            var el = Keyboard.FocusedElement as UIElement;
            string? key = null;
            Guid? id = null;
            foreach (var (k, lb) in _lists)
                if (lb.IsKeyboardFocusWithin) { key = k; id = (lb.SelectedItem as ListBoxItem)?.Tag as Guid?; }
            return () =>
            {
                if (key != null && _lists.TryGetValue(key, out var lb) && lb.IsVisible)
                {
                    int at = id is Guid g ? IndexOf(lb, g) : -1;
                    TextViewer.Select(lb, at >= 0 ? at : Math.Max(0, lb.SelectedIndex));
                    return;
                }
                if (key == null && el != null && el.IsVisible && el.Focusable && Window.GetWindow(el) == this && el.Focus()) return;
                FocusTab(false);
            };
        }

        /// <summary>Options, Priority scales: Settings, opened at the list whose tab is open (or the chosen task's list).</summary>
        private void EditScales()
        {
            string? list = _tabs.SelectedItem is TabItem { Tag: string l } ? l : _lastList != null && Current() is TaskItem t ? t.List : _store.Data.Lists.FirstOrDefault()?.Name;
            OpenSettings(list);
        }

        /// <summary>Opens Settings at a page by its name (like Tags) or at a list by its name.</summary>
        public void OpenSettingsAt(string? name) => OpenSettings(name);

        private void OpenSettings(string? list, string? focus = null)
        {
            // Back where she was (the Search box, a category list, the Bunny box), on OK and on Cancel.
            var back = RememberFocus();
            if (!SettingsWindow.Show(this, _store, _settings, list, focus)) { back(); return; }
            Save();
            _shortMenu.IsChecked = _settings.ShortMode;
            _soundsOnMenu.IsChecked = _settings.SoundsOn;
            _checkAtStartMenu.IsChecked = _settings.CheckForUpdatesAtStart;
            if (_settings.CheckForUpdatesAtStart) StartAutoUpdates();
            Refresh();
            back();
            Speech.Say(this, "Settings saved.");
        }

        /// <summary>
        /// Makes the list tabs, the Move to pick menu and the Move to list menu match the lists: after
        /// Settings adds, renames or reorders them, and after Undo puts them back.
        /// </summary>
        private static string SectionKey(TaskList l, string sec) => "list-" + l.Name + "-sec-" + sec;
        private string _tabSignature = "";
        private readonly List<string> _tabKeys = new();

        private void SyncListTabs()
        {
            var active = _store.ActiveLists.ToList();
            // A sub-list shows inside its parent's tab; one whose parent is gone or archived is shown nowhere.
            var tops = active.Where(l => l.Parent.Length == 0).ToList();
            var subsOf = tops.ToDictionary(t => t.Name, t => active.Where(l => l.Parent == t.Name).ToList());
            var names = tops.SelectMany(t => new[] { t.Name }.Concat(subsOf[t.Name].Select(s => s.Name))).ToList();
            string signature = string.Join("|", tops.Select(t => t.Name + "<" + string.Join(",", t.Sections) + ">" +
                string.Join(",", subsOf[t.Name].Select(s => s.Name + "<" + string.Join(",", s.Sections) + ">"))));
            if (signature == _tabSignature && _tabs.Items.OfType<TabItem>().Count(t => t.Tag is string) == tops.Count) return;
            _tabSignature = signature;
            object? selectedTag = (_tabs.SelectedItem as TabItem)?.Tag;
            int selectedIndex = _tabs.SelectedIndex;
            foreach (var t in _tabs.Items.OfType<TabItem>().Where(t => t.Tag is string).ToList()) _tabs.Items.Remove(t);
            foreach (var k in _tabKeys) _lists.Remove(k);
            _tabKeys.Clear();

            _placeOf.Clear();
            ListBox MakeList(StackPanel panel, string key, string? heading, string list, string section)
            {
                var lb = NewList(key);
                _tabKeys.Add(key);
                _placeOf[lb] = (list, section);
                if (heading != null) panel.Children.Add(new TextBlock { Text = heading, Margin = new Thickness(0, 10, 0, 2), FontWeight = FontWeights.SemiBold });
                panel.Children.Add(lb);
                return lb;
            }
            foreach (var l in tops)
            {
                var panel = new StackPanel { Margin = new Thickness(6) };
                MakeList(panel, "list-" + l.Name, null, l.Name, "");
                foreach (var sec in l.Sections) MakeList(panel, SectionKey(l, sec), sec, l.Name, sec);
                foreach (var sub in subsOf[l.Name])
                {
                    MakeList(panel, "list-" + sub.Name, sub.Name, sub.Name, "");
                    foreach (var sec in sub.Sections) MakeList(panel, SectionKey(sub, sec), sub.Name + ", " + sec, sub.Name, sec);
                }
                // Under every list tab (Morgan's issue 6): its settings, priority scale first, and adding a category.
                var tabButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
                var listName = l.Name;
                var viewSettings = Layout.Button("View tab settin_gs"); // Alt S is Bunny's answers
                viewSettings.Click += (_, _) => TabSettings(listName);
                var addCategory = Layout.Button("Add a _category");
                addCategory.Click += (_, _) => AddCategory(listName);
                tabButtons.Children.Add(viewSettings);
                tabButtons.Children.Add(addCategory);
                panel.Children.Add(tabButtons);
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
            int urgent = _store.Today(DateTime.Now).ToDo.Count;
            int limit = _settings.UrgentWarnAbove;
            if (limit > 0 && urgent > limit && _urgentBefore <= limit)
            {
                Sounds.Play(Sounds.Event.OverloadWarning);
                Speech.Say(this, "Heads up: " + urgent + " tasks to do today. Want to move some to This week? Control 2 does it.");
            }
            _urgentBefore = urgent;
        }

        // The tasks the pickers act on, taken as focus came into them (Morgan's choice, not whatever is under the cursor later).
        private List<Guid> _pickerIds = new();

        private List<TaskItem> PickerTasks()
        {
            var ids = _pickerIds.ToHashSet();
            return _store.Data.Tasks.Where(t => ids.Contains(t.Id)).ToList();
        }

        /// <summary>Shows the picked task's status and list. capture takes the chosen tasks first (focus coming in).</summary>
        private void SyncPickers(bool capture)
        {
            if (capture) _pickerIds = ChosenTasks().Select(t => t.Id).ToList();
            _syncingPickers = true;
            var t = PickerTasks().FirstOrDefault(x => x.Id == _pickerIds.LastOrDefault()) ?? PickerTasks().FirstOrDefault();
            _status.SelectedIndex = t == null ? -1 : (int)t.Status;
            _move.SelectedItem = t?.List;
            _shownStatus = _status.SelectedIndex;
            _shownList = _move.SelectedItem as string;
            _syncingPickers = false;
        }
        private int _shownStatus = -1;
        private string? _shownList;

        private void CommitPicker(ComboBox c)
        {
            if (_syncingPickers) return;
            var tasks = PickerTasks();
            if (tasks.Count == 0) { if (_pickerIds.Count == 0 && c.SelectedIndex >= 0) CurrentOrSay(); return; }
            var t = tasks.Count == 1 ? tasks[0] : null;
            // Only a choice she made counts: what the picker showed when she came in changes nothing.
            if (c == _status && _status.SelectedIndex >= 0 && _status.SelectedIndex != _shownStatus && (t == null || (Status)_status.SelectedIndex != t.Status))
            {
                if (t != null) SetTaskStatus(t, (Status)_status.SelectedIndex);
                else SetStatus((Status)_status.SelectedIndex, tasks);
            }
            else if (c == _move && _move.SelectedItem is string l && l != _shownList && (t == null || l != t.List)) MoveTo(l, tasks);
            else return;
            // The same tasks stay picked, now showing their new status and list: Enter again changes nothing.
            SyncPickers(capture: false);
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
            // Only lists that show somewhere: a task moved to an archived list would vanish.
            foreach (var l in _store.ActiveLists) { var n = l.Name; mv.Items.Add(Item(n + (n == t.List ? ", now" : ""), () => MoveTo(n))); }
            menu.Items.Add(mv);
            var sections = _store.ListOf(t).Sections;
            if (sections.Count > 0)
            {
                var cat = new MenuItem { Header = "Put in a _category" };
                foreach (var sec in new[] { "" }.Concat(sections))
                {
                    var x = sec;
                    cat.Items.Add(Item((sec.Length == 0 ? "No category" : sec) + (sec == t.Section ? ", now" : ""), () =>
                    {
                        var all = ChosenTasks();
                        SetCategory(all.All(a => a.List == t.List) ? all : new List<TaskItem> { t }, x);
                    }));
                }
                menu.Items.Add(cat);
            }
            menu.Items.Add(Item("Su_btasks...", Subtasks, "Ctrl+Shift+B"));
            menu.Items.Add(Item("De_lete", DeleteTask, "Delete"));
            return true;
        }

        /// <summary>Saves now. Store.Save never throws: a failed save is announced and retried by DataSafety.</summary>
        private void Save() => _store.Save();

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

        private DateTime _checkStarted;
        private bool _preparing, _offering, _installing;
        private Updater.Release? _ready;
        private DateTime _offerAfter = DateTime.MinValue;

        private async void BackgroundCheck()
        {
            // A check that somehow never came back must not stop every check after it.
            if (_backgroundChecking && DateTime.Now - _checkStarted < TimeSpan.FromMinutes(2)) return;
            if (_manualChecking || _installing || !_settings.CheckForUpdatesAtStart) return;
            // On battery (Morgan's issue 14) every 15 minutes is plenty; plugged in, every 30 seconds as before.
            bool onBattery = false;
            try { onBattery = System.Windows.Forms.SystemInformation.PowerStatus.PowerLineStatus == System.Windows.Forms.PowerLineStatus.Offline; } catch { }
            if (onBattery && DateTime.Now - _checkStarted < TimeSpan.FromMinutes(15)) return;
            _backgroundChecking = true;
            _checkStarted = DateTime.Now;
            try
            {
                var r = await Updater.CheckAsync(quick: true);
                _available = r;
                _updateMenu.Header = r == null ? "Check for _updates..." : "_Install version " + r.Version + "...";
                if (r != null && r.Version != _announced)
                {
                    _announced = r.Version;
                    Reminders.Notify("Busy Bunny " + r.Version + " is out", Behind(r) + " It is getting ready, and opens in Busy Bunny when you are there.");
                }
                if (r != null) Prepare(r);
            }
            catch { } // offline or GitHub unhappy: try again at the next tick or when the connection comes back
            finally { _backgroundChecking = false; }
        }

        /// <summary>Downloads the update in the background, then offers it. A failed download tries again at the next check.</summary>
        private async void Prepare(Updater.Release r)
        {
            if (_preparing || _ready?.Version == r.Version) return;
            _preparing = true;
            try
            {
                await Updater.PrepareAsync(r);
                _ready = r;
                _offerAfter = DateTime.MinValue;
                OfferUpdate();
            }
            catch { }
            finally { _preparing = false; }
        }

        /// <summary>
        /// Puts the update window in front of her when Busy Bunny is open, in front and not in another
        /// window of its own; otherwise it waits for that. Later asks again in an hour, and the update
        /// installs by itself the next time Busy Bunny closes or starts, so forgetting cannot leave her behind.
        /// </summary>
        private async void OfferUpdate()
        {
            if (_ready == null || _offering || _installing || _manualChecking || DateTime.Now < _offerAfter) return;
            if (!IsVisible || !IsActive || OwnedWindows.Count > 0 || _closing) return;
            // Never in the middle of something (spec 9.5): only after a quiet minute with focus on a task,
            // not while she types to Bunny, searches, or has just closed another window.
            if (DateTime.Now - _lastInput < TimeSpan.FromMinutes(1) || !_lists.Values.Any(l => l.IsKeyboardFocusWithin)) return;
            var back = RememberFocus();
            _offering = true;
            bool install;
            try { install = UpdateDialog.Show(this, _ready, Behind(_ready)); }
            finally { _offering = false; }
            if (install) { await InstallNow(_ready); return; }
            _offerAfter = DateTime.Now.AddHours(1);
            back();
            Speech.Say(this, "Okay. It installs by itself the next time Busy Bunny closes or starts.");
        }

        /// <summary>Downloads if needed (with the climbing chords), swaps in the new copy and restarts.</summary>
        private async System.Threading.Tasks.Task InstallNow(Updater.Release r)
        {
            if (_installing) return;
            _installing = true;
            try
            {
                if (!_store.TrySave())
                {
                    Speech.Say(this, "Busy Bunny cannot save your tasks right now, so it will not update yet. It tries again later.", true);
                    _installing = false;
                    return;
                }
                Speech.Say(this, "Updating Busy Bunny.", true);
                // A random instrument climbs in chords at half volume, and Busy Bunny waits for it to
                // land before restarting, so it is never cut off.
                var names = Sounds.InstrumentNames;
                Sounds.PlayUpdateClimb(names[new Random().Next(names.Count)], 0.5);
                var started = DateTime.Now;
                await Updater.InstallAsync(r);
                var left = TimeSpan.FromSeconds(Sounds.ClimbSeconds) - (DateTime.Now - started);
                if (_settings.SoundsOn && left > TimeSpan.Zero) await System.Threading.Tasks.Task.Delay(left);
                _closing = true;
                Reminders.Quitting = true;
                Application.Current.Shutdown();
            }
            catch (Exception e)
            {
                _installing = false;
                MessageBox.Show(this, e.Message + " Busy Bunny will try again by itself.", "Update", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private static string Behind(Updater.Release r) =>
            "You're " + r.Missed.Count + (r.Missed.Count == 1 ? " version" : " versions") + " out of date.";

        public async void CheckForUpdates()
        {
            if (_manualChecking || _installing) return;
            _manualChecking = true;
            Updater.Release? r;
            try
            {
                Speech.Say(this, "Checking for updates.", true);
                r = await Updater.CheckAsync();
            }
            catch (Exception e)
            {
                _manualChecking = false;
                MessageBox.Show(this, e.Message, "Check for updates", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _available = r;
            _updateMenu.Header = r == null ? "Check for _updates..." : "_Install version " + r.Version + "...";
            if (r == null)
            {
                _manualChecking = false;
                MessageBox.Show(this, "You have the newest Busy Bunny, version " + Updater.Current + ".", "Check for updates", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            _announced = r.Version;
            bool install = UpdateDialog.Show(this, r, Behind(r));
            _manualChecking = false;
            if (install) await InstallNow(r);
            else { _offerAfter = DateTime.Now.AddHours(1); Prepare(r); }
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
            "Select several tasks in a list: Shift with Up or Down Arrow. Or move with Control and the arrows, and press Control Space on each one you want. Then Control D, Control 1 to 6, Delete, Move to, and Task, Put in a category work on all of them",
            "Status and Move to pickers: arrow to a choice, then Enter, or just move on with Tab. Escape backs out and goes back to the list",
            "In Quick add and Edit task: Control Enter adds or saves from any field. Control Down and Control Up jump between the sections of the form. Enter in New subtask or New comment adds it",
            "In Subtasks: Space checks one off or back on, Delete removes it, and Enter in New subtask adds one",
            "In the changelog, Licences and credits, and Update available: Control Down and Control Up jump between headings or versions",
            "Close Busy Bunny: File, Exit. With Keep in tray on, Alt F4 only hides it to the tray",
        }.Concat(_keyHelp).Concat(_helpLines).Append("These shortcuts are a first try. Tell Conner which ones feel wrong.").ToList();

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

        /// <summary>Help, Request a feature: a new GitHub issue, its title started for her. Bunny points here too.</summary>
        public void RequestFeature(string idea = "")
        {
            string title = "Feature request: " + idea.Trim();
            Open(Updater.Website + "/issues/new?title=" + Uri.EscapeDataString(title));
            Speech.Say(this, "Opening a new feature request on GitHub in your browser. Say what you would like, then press Submit new issue.", true);
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
    /// Access keys (the underlined letter, Alt plus a letter). Two items in one menu or window with the
    /// same letter make WPF only move between them, so neither ever runs: every letter must be unique.
    /// </summary>
    public static class AccessKeys
    {
        /// <summary>The access key of a header, the letter after a single underscore, in lower case; null for none.</summary>
        public static char? Of(string? header)
        {
            if (header == null) return null;
            for (int i = 0; i < header.Length - 1; i++)
            {
                if (header[i] != '_') continue;
                if (header[i + 1] == '_') { i++; continue; }
                return char.ToLowerInvariant(header[i + 1]);
            }
            return null;
        }

        /// <summary>The header with its access key on a letter not in used: a word's first letter if it can, then any letter. No key when none is free.</summary>
        public static string Move(string header, ICollection<char> used)
        {
            string plain = header;
            for (int i = 0; i < header.Length - 1; i++)
            {
                if (header[i] != '_') continue;
                if (header[i + 1] == '_') { i++; continue; }
                plain = header.Remove(i, 1);
                break;
            }
            var spots = Enumerable.Range(0, plain.Length).Where(i => char.IsLetterOrDigit(plain[i]) && (i == 0 || plain[i - 1] != '_'))
                .OrderBy(i => i == 0 || plain[i - 1] == ' ' ? 0 : 1).ThenBy(i => i);
            foreach (int i in spots)
                if (!used.Contains(char.ToLowerInvariant(plain[i]))) return plain.Insert(i, "_");
            return plain;
        }

        /// <summary>The letters used more than once among these headers.</summary>
        public static List<char> Duplicates(IEnumerable<string?> headers) =>
            headers.Select(Of).Where(k => k != null).GroupBy(k => k!.Value).Where(g => g.Count() > 1).Select(g => g.Key).ToList();

        /// <summary>Every menu, top bar included, that has two items on one letter, like "File: r".</summary>
        public static List<string> MenuProblems(ItemsControl bar)
        {
            var problems = new List<string>();
            void Walk(ItemsControl menu, string name)
            {
                var items = menu.Items.OfType<MenuItem>().ToList();
                foreach (char c in Duplicates(items.Select(i => i.Header as string)))
                    problems.Add(name + ": " + c + " (" + string.Join(", ", items.Where(i => Of(i.Header as string) == c).Select(i => i.Header)) + ")");
                foreach (var i in items.Where(i => i.Items.Count > 0)) Walk(i, (i.Header as string ?? "menu").Replace("_", ""));
            }
            Walk(bar, "Menu bar");
            return problems;
        }

        public static void SelfTest()
        {
            void Check(bool ok, string what) { if (!ok) throw new Exception("Access keys: " + what); }
            Check(Of("E_xit") == 'x' && Of("Rea_d my day") == 'd' && Of("No key") == null && Of("a__b _c") == 'c', "reading the letter");
            Check(Move("_Search tasks", new HashSet<char> { 's' }) == "Search _tasks", "moving to the next word");
            Check(Move("E_xport", new HashSet<char> { 'x', 'e' }) == "Ex_port", "moving inside a word");
            Check(Move("_ab", new HashSet<char> { 'a', 'b' }) == "ab", "no free letter leaves no key");
            Check(Duplicates(new[] { "_Edit", "D_elete", "_Undo" }).SequenceEqual(new[] { 'e' }), "finding a duplicate");
            Check(MainWindow.MenuKeyProblems.Count == 0, "main window menus share letters: " + string.Join("; ", MainWindow.MenuKeyProblems));
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
