using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// A tool in the Bunny Toolbox (sweep Part 4, 8.2 G). Registered once, it shows in the toolbox, the Fun menu (as a
    /// favorite), Settings, Fun and tools (its on and off switch), Help and Talk to Bunny. Run is what Enter does.
    /// Since is the version it arrived in, for Help, New in Busy Bunny. MovedFrom says where it used to be, for Help,
    /// Where things moved, like "the Fun menu"; Keys is its own shortcut, if it has one. State, for a tool that turns
    /// something on or off, gives its state now ("on" or "off"), read right after the name everywhere it is listed.
    /// </summary>
    public sealed record BunnyTool(string Id, string Name, string Drawer, string WhatItDoes, Action Run, bool DefaultOn = true)
    {
        public string Since { get; init; } = "";
        public string MovedFrom { get; init; } = "";
        public string Keys { get; init; } = "";
        public Func<string>? State { get; init; }
        /// <summary>The name as she hears it in lists: with its state for a switch, like "Finals week mode, now off".</summary>
        public string Spoken => State == null ? Name : Name + ", now " + SafeState();
        private string SafeState() { try { return State!(); } catch { return "unknown"; } }
    }

    /// <summary>The toolbox drawers, in the order they show (sweep 4.2, and Skills from 12.7).</summary>
    public static class Drawers
    {
        public const string Favorites = "Favorites", Recent = "Recently used", Stuck = "I am stuck", GetGoing = "Get going", Plan = "Plan",
            Rest = "Rest and rough days", Wins = "Wins and records", World = "Bunny's world", Games = "Games", Notes = "Notes to self", Skills = "Skills";
        /// <summary>The drawers tools go in (Favorites and Recently used fill themselves).</summary>
        public static readonly string[] All = { Stuck, GetGoing, Plan, Rest, Wins, World, Games, Notes, Skills };
    }

    /// <summary>The tool registry. Features call Register in Attach; everything else reads from here.</summary>
    public static class Tools
    {
        private static readonly List<BunnyTool> _all = new();
        private static readonly List<string> _moves = new();
        public static IReadOnlyList<BunnyTool> All => _all;

        /// <summary>Adds a tool, or replaces the one with the same id. A drawer not in Drawers.All goes to the end of the list.</summary>
        public static BunnyTool Register(BunnyTool t)
        {
            int i = _all.FindIndex(x => x.Id == t.Id);
            if (i >= 0) _all[i] = t; else _all.Add(t);
            return t;
        }

        /// <summary>A line for Help, Where things moved, for something that is not a tool (a File menu item now in Settings).</summary>
        public static void NoteMove(string line) { if (!_moves.Contains(line)) _moves.Add(line); }

        public static BunnyTool? Find(string idOrName)
        {
            string q = idOrName.Trim();
            return _all.FirstOrDefault(t => t.Id.Equals(q, StringComparison.OrdinalIgnoreCase))
                ?? _all.FirstOrDefault(t => t.Name.Equals(q, StringComparison.OrdinalIgnoreCase))
                ?? _all.FirstOrDefault(t => t.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                ?? (q.Length >= 3 ? _all.FirstOrDefault(t => t.Name.Contains(q, StringComparison.OrdinalIgnoreCase)) : null);
        }

        public static bool IsOn(Settings s, BunnyTool t) => t.DefaultOn ? !s.ToolsOff.Contains(t.Id) : s.ToolsOn.Contains(t.Id);
        public static void SetOn(Settings s, BunnyTool t, bool on)
        {
            s.ToolsOff.Remove(t.Id);
            s.ToolsOn.Remove(t.Id);
            if (on && !t.DefaultOn) s.ToolsOn.Add(t.Id);
            if (!on && t.DefaultOn) s.ToolsOff.Add(t.Id);
        }
        /// <summary>
        /// Whether the tool with this id is turned on in her settings: features check this before any automatic behaviour
        /// of the tool (lines after tasks, ticker jobs, start-of-day lines). True for an id nobody registered.
        /// </summary>
        public static bool IsOn(string id) => Engines.Settings is not Settings s || _all.FirstOrDefault(t => t.Id == id) is not BunnyTool t || IsOn(s, t);

        /// <summary>
        /// Wraps a tool's own shortcut or menu item: it runs the action while the tool is on, and when it is turned off
        /// says so instead. Use it for every AddKey and AddMenuItem that opens a tool.
        /// </summary>
        public static Action Guard(string id, Action act) => () =>
        {
            if (IsOn(id)) { act(); return; }
            string name = _all.FirstOrDefault(t => t.Id == id)?.Name ?? "That tool";
            Engines.Window?.Say(name + " is turned off. Options, Settings, Fun and tools turns it back on.", true);
        };

        public static IEnumerable<BunnyTool> Enabled(Settings s) => _all.Where(t => IsOn(s, t));
        public static bool IsFavorite(Settings s, BunnyTool t) => s.ToolFavorites.Contains(t.Id);

        /// <summary>
        /// Uses a tool: remembers when, runs it, then raises ToolUsed (after Run returns, so after any window it opened has
        /// closed; not when Run threw). A tool that throws is logged and said, never a crash.
        /// </summary>
        public static void Use(MainWindow w, BunnyTool t)
        {
            w.AppSettings.ToolUsed[t.Id] = DateTime.Now;
            w.AppSettings.Save();
            try { t.Run(); }
            catch (Exception e)
            {
                ErrorLog.Write("The tool " + t.Id + " failed: " + e);
                w.Say(t.Name + " did not work just now. Help, Report a problem copies the details.", true);
                return;
            }
            GameEvents.RaiseToolUsed(t.Id);
        }

        /// <summary>
        /// The tools of a drawer in the order she hears them: favorites first, then the rest as registered, and tools
        /// not used in a month at the end (sweep 13.2 C). Nothing is hidden. Recently used is newest first, the last 10.
        /// </summary>
        public static List<BunnyTool> InDrawer(Settings s, string drawer, DateTime now)
        {
            var on = Enabled(s).ToList();
            if (drawer == Drawers.Favorites) return s.ToolFavorites.Select(id => on.FirstOrDefault(t => t.Id == id)).OfType<BunnyTool>().ToList();
            if (drawer == Drawers.Recent) return on.Where(t => s.ToolUsed.ContainsKey(t.Id)).OrderByDescending(t => s.ToolUsed[t.Id]).Take(10).ToList();
            return Order(s, on.Where(t => t.Drawer == drawer), now);
        }

        public static List<BunnyTool> Order(Settings s, IEnumerable<BunnyTool> tools, DateTime now) => tools
            .Select((t, i) => (t, i))
            .OrderBy(x => IsFavorite(s, x.t) ? 0 : s.ToolUsed.TryGetValue(x.t.Id, out var used) && now - used > TimeSpan.FromDays(30) ? 2 : 1)
            .ThenBy(x => x.i).Select(x => x.t).ToList();

        /// <summary>Tools whose name or words match, from every drawer.</summary>
        public static List<BunnyTool> Search(Settings s, string text, DateTime now)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return Order(s, Enabled(s).Where(t => words.All(w => (t.Name + " " + t.WhatItDoes + " " + t.Drawer).Contains(w, StringComparison.OrdinalIgnoreCase))), now);
        }

        /// <summary>The drawers that have tools, in order, after Favorites and Recently used.</summary>
        public static List<string> DrawerNames(Settings s)
        {
            var on = Enabled(s).ToList();
            var known = Drawers.All.Where(d => on.Any(t => t.Drawer == d));
            var other = on.Select(t => t.Drawer).Where(d => !Drawers.All.Contains(d)).Distinct();
            return new[] { Drawers.Favorites, Drawers.Recent }.Concat(known).Concat(other).ToList();
        }

        /// <summary>What Talk to Bunny is told: each tool she has on, by id and name, with what it does.</summary>
        public static string ForBunny(Settings s) => string.Join("; ", Enabled(s).Select(t => t.Id + " (" + t.Spoken + "): " + t.WhatItDoes));

        /// <summary>Help, Where things moved: every moved tool and every noted move.</summary>
        public static List<string> MoveLines() => _all.Where(t => t.MovedFrom.Length > 0)
            .Select(t => t.Name + ": was in " + t.MovedFrom + ", now in the toolbox, " + t.Drawer + " drawer." + (t.Keys.Length > 0 ? " " + t.Keys + " still works." : ""))
            .Concat(_moves).ToList();

        /// <summary>Help, Every tool: by drawer, each with what it does and whether it is on.</summary>
        public static List<string> HelpLines(Settings s)
        {
            var lines = new List<string>();
            foreach (var d in Drawers.All.Concat(_all.Select(t => t.Drawer).Where(x => !Drawers.All.Contains(x)).Distinct()))
            {
                var tools = _all.Where(t => t.Drawer == d).ToList();
                if (tools.Count == 0) continue;
                lines.Add(d + ":");
                lines.AddRange(tools.Select(t => t.Spoken + ": " + t.WhatItDoes + (t.Keys.Length > 0 ? " " + t.Keys + "." : "") + (IsOn(s, t) ? "" : " Turned off in Settings, Fun and tools.")));
            }
            return lines;
        }

        /// <summary>Help, New in Busy Bunny: tools that came in this version or the one before, newest first.</summary>
        public static List<string> NewLines(Settings s)
        {
            var cur = Updater.Current;
            bool Recent(string since) => Version.TryParse(since, out var v) && v.Major == cur.Major && cur.Minor - v.Minor <= 1;
            return _all.Where(t => Recent(t.Since)).OrderByDescending(t => Version.Parse(t.Since))
                .Select(t => t.Name + ", new in " + t.Since + ": " + t.WhatItDoes + " In the toolbox, " + t.Drawer + " drawer." + (IsOn(s, t) ? " Settings, Fun and tools turns it off." : " It starts off: Settings, Fun and tools turns it on.")).ToList();
        }
    }

    /// <summary>The Bunny Toolbox window (Control Shift H): a Drawer pick menu, a Search box and the list of tools.</summary>
    public static class ToolboxWindow
    {
        private const string NoFavorites = "No favorites yet. Control D on any tool adds it here.";

        /// <summary>Shows the toolbox; returns the tool she chose, or null. Focus goes back where she was on closing.</summary>
        public static BunnyTool? Show(MainWindow owner, string? startDrawer = null)
        {
            var s = owner.AppSettings;
            var now = DateTime.Now;
            var w = Layout.Dialog(owner, "Bunny Toolbox");
            var drawers = new ComboBox();
            var search = new TextBox();
            var list = new ListBox { Height = 300, Width = 520 };
            AutomationProperties.SetHelpText(list, "Enter uses a tool. Control D marks it as a favorite. Type letters to jump to a tool by name.");
            AutomationProperties.SetHelpText(search, "Type words to find a tool in any drawer. Down Arrow goes to the results.");
            var names = Tools.DrawerNames(s);
            drawers.ItemsSource = names;
            List<BunnyTool> shown = new();
            BunnyTool? chosen = null;

            string DrawerNow() => drawers.SelectedItem as string ?? Drawers.Favorites;
            void Fill(int select = 0)
            {
                string q = search.Text.Trim();
                shown = q.Length > 0 ? Tools.Search(s, q, now) : Tools.InDrawer(s, DrawerNow(), now);
                list.Items.Clear();
                foreach (var t in shown)
                {
                    string line = t.Spoken + ": " + t.WhatItDoes + (q.Length > 0 ? " In " + t.Drawer + "." : "") + (Tools.IsFavorite(s, t) && DrawerNow() != Drawers.Favorites ? " Favorite." : "");
                    list.Items.Add(new ListBoxItem { Content = line });
                }
                if (shown.Count == 0)
                    list.Items.Add(new ListBoxItem { Content = q.Length > 0 ? "No tool matches " + q + "." : DrawerNow() == Drawers.Favorites ? NoFavorites : DrawerNow() == Drawers.Recent ? "Nothing used yet. Tools you use show up here." : "Nothing in this drawer yet." });
                string name = (q.Length > 0 ? "Tools matching " + q : DrawerNow()) + ", " + shown.Count + (shown.Count == 1 ? " tool" : " tools");
                AutomationProperties.SetName(list, name);
                if (list.Items.Count > 0) list.SelectedIndex = Math.Clamp(select, 0, list.Items.Count - 1);
            }
            BunnyTool? Current() => list.SelectedIndex >= 0 && list.SelectedIndex < shown.Count ? shown[list.SelectedIndex] : null;
            void UseIt()
            {
                if (Current() is not BunnyTool t) { Speech.Say(w, "Pick a tool first.", true); return; }
                chosen = t;
                w.DialogResult = true;
            }
            void Favorite()
            {
                if (Current() is not BunnyTool t) return;
                int at = list.SelectedIndex;
                bool fav = !s.ToolFavorites.Remove(t.Id);
                if (fav) s.ToolFavorites.Add(t.Id);
                s.Save();
                Fill(at);
                if (list.Items.Count > 0) TextViewer.Select(list, Math.Min(at, list.Items.Count - 1));
                Speech.Say(w, fav ? t.Name + " is a favorite now. It shows first, and in the Fun menu." : t.Name + " is not a favorite any more.", true);
            }

            drawers.SelectionChanged += (_, _) => { if (search.Text.Length > 0) search.Text = ""; else Fill(); };
            search.TextChanged += (_, _) => Fill();
            search.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Down && list.Items.Count > 0) { e.Handled = true; TextViewer.Select(list, Math.Max(0, list.SelectedIndex)); }
                else if (e.Key == Key.Enter) { e.Handled = true; if (shown.Count > 0) { list.SelectedIndex = Math.Max(0, list.SelectedIndex); UseIt(); } }
            };
            list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; UseIt(); } };
            w.PreviewKeyDown += (_, e) => { if (e.Key == Key.D && Keyboard.Modifiers == ModifierKeys.Control) { e.Handled = true; Favorite(); } };

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("_Drawer", drawers, 520));
            panel.Children.Add(Layout.Row("_Search every drawer", search, 520));
            panel.Children.Add(new Label { Content = "_Tools", Target = list, Padding = new Thickness(0, 6, 0, 2) });
            panel.Children.Add(list);
            var use = Layout.Button("_Use it");
            use.Click += (_, _) => UseIt();
            var fav = Layout.Button("_Favorite or not (Control D)");
            fav.Click += (_, _) => { Favorite(); TextViewer.Select(list, Math.Max(0, list.SelectedIndex)); };
            var close = Layout.Button("Close", isCancel: true);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(use);
            row.Children.Add(fav);
            row.Children.Add(close);
            panel.Children.Add(row);
            w.Content = panel;

            string start = startDrawer != null && names.Contains(startDrawer) ? startDrawer
                : s.ToolFavorites.Any(id => Tools.Find(id) is BunnyTool t && Tools.IsOn(s, t)) ? Drawers.Favorites
                : s.ToolUsed.Count > 0 ? Drawers.Recent : names.Skip(2).FirstOrDefault() ?? Drawers.Favorites;
            drawers.SelectedItem = start;
            Fill();
            w.Loaded += (_, _) => TextViewer.Select(list, 0);
            return w.ShowDialog() == true ? chosen : null;
        }
    }

    /// <summary>
    /// Opens the toolbox (Control Shift H, Fun menu), keeps the Fun menu to Open the toolbox plus her favorites,
    /// adds the Day menu's End-of-day reset item, Help's tool lists, Settings, Fun and tools, and the Bunny tab.
    /// </summary>
    public sealed class Toolbox : IFeature
    {
        private MainWindow _w = null!;
        private readonly List<MenuItem> _favoriteItems = new();

        public void Attach(MainWindow w)
        {
            _w = w;
            var open = w.AddMenuItem("Fun", "Open the _toolbox...", () => Open(), "Ctrl+Shift+H");
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.H, () => Open(), "Control Shift H: the Bunny Toolbox, every fun and strategy tool in drawers. Control D in it marks a favorite");
            if (open.Parent is MenuItem fun)
            {
                var sep = new Separator();
                fun.Items.Add(sep);
                fun.SubmenuOpened += (_, e) =>
                {
                    if (e.OriginalSource != fun) return;
                    foreach (var m in _favoriteItems) fun.Items.Remove(m);
                    _favoriteItems.Clear();
                    foreach (var t in Tools.InDrawer(w.AppSettings, Drawers.Favorites, DateTime.Now))
                    {
                        var tool = t;
                        // No access key: her favorites can be anything, and a shared letter would make both dead.
                        var m = new MenuItem { Header = t.Spoken.Replace("_", "__") };
                        m.Click += (_, _) => Tools.Use(w, tool);
                        _favoriteItems.Add(m);
                        fun.Items.Add(m);
                    }
                    sep.Visibility = _favoriteItems.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                };
            }
            var reset = w.AddMenuItem("Day", "E_nd-of-day reset...", () => w.OpenSettingsAt("Morning and evening"));
            // The Day menu in the order of sweep 11.7, whichever feature added each item.
            if (reset.Parent is MenuItem day)
            {
                string[] order = { "rough day", "kind of day", "vacation", "time per class", "low-energy", "finals", "end-of-day", "morning check-in", "weekday plan" };
                int Rank(MenuItem m) { string h = (m.Header as string ?? "").Replace("_", "").ToLowerInvariant(); int i = Array.FindIndex(order, o => h.Contains(o)); return i < 0 ? order.Length : i; }
                var items = day.Items.OfType<MenuItem>().OrderBy(Rank).ToList();
                day.Items.Clear();
                foreach (var m in items) day.Items.Add(m);
            }
            // The Bunny menu (issue 19): talking first, the syllabus import after the talk items, its settings last.
            if (w.AddMenuItem("Bunny", "_Visit your bunny...", Tools.Guard("visitbunny", () => { if (Tools.Find("visitbunny") is BunnyTool v) Tools.Use(w, v); })).Parent is MenuItem bunny)
            {
                string[] order = { "talk to bunny", "check on me", "new talk", "know me", "stop bunny", "changes today", "briefing", "what i finished", "syllabus", "visit your bunny", "settings" };
                int Rank(MenuItem m) { string h = (m.Header as string ?? "").Replace("_", "").ToLowerInvariant(); int i = Array.FindIndex(order, o => h.Contains(o)); return i < 0 ? order.Length - 2 : i; }
                var items = bunny.Items.OfType<MenuItem>().OrderBy(Rank).ToList();
                bunny.Items.Clear();
                // One separator only, before Smarter Bunny settings.
                foreach (var m in items)
                {
                    if (Rank(m) == order.Length - 1 && bunny.Items.Count > 0 && bunny.Items[^1] is not Separator) bunny.Items.Add(new Separator());
                    bunny.Items.Add(m);
                }
            }
            w.AddMenuItem("Help", "_New in Busy Bunny...", () => TextViewer.Show(w, "New in Busy Bunny", NewLines(w.AppSettings)));
            w.AddMenuItem("Help", "Every _tool in the toolbox...", () => TextViewer.Show(w, "Every tool in the toolbox", Tools.HelpLines(w.AppSettings), l => l.EndsWith(":")));
            w.AddMenuItem("Help", "Where things _moved...", () => TextViewer.Show(w, "Where things moved", MovedLines()));

            Tools.NoteMove("Paste tasks, Import my braille lists, Import a syllabus, Export as plain text, Export a backup and Restore from a backup: were in the File menu, now in Options, Settings, Tasks. Control Shift V still pastes tasks.");
            Tools.NoteMove("Open the backups folder and Start over with the sample tasks: were in the File menu, now in Options, Settings, Tasks.");
            Tools.NoteMove("Morning check-in and Weekday plan for today: were in the File menu, now in the Day menu. Control Shift M still opens the check-in.");
            Tools.NoteMove("Low-energy mode: was in the View menu, now in the Day menu. Control Shift L still turns it on or off.");
            Tools.NoteMove("Rough day, Set the kind of day, Vacation mode, Time per class and Finals week mode: also in the new Day menu, as well as the toolbox. Control Shift Y still sets the kind of day.");
            Tools.NoteMove("End-of-day reset: its setting is in the Day menu too, as End-of-day reset.");
            Tools.NoteMove("Setup guide: was in the File menu, now in the Help menu.");
            Tools.NoteMove("The roast dial, emoji and finals week mode settings: the Settings page called Fun is now called Bunny's voice, next to the new Fun and tools page.");
            Tools.NoteMove("View the parking lot: now a room in Bunny's world, the parking lot, also in the toolbox. Control Shift K still parks a thought.");
            Tools.NoteMove("Visit your bunny, Bunny's world, the burrow and the adventures: also on the new Bunny tab, after Upcoming.");
            Tools.NoteMove("The Done list: also in the Bunny menu as What I finished, now with the two weeks before today. Today's briefing is new in the Bunny menu too.");
        }

        private List<string> MovedLines()
        {
            var lines = Tools.MoveLines();
            lines.Insert(0, "Every old shortcut still works. Here is where each thing went when the toolbox arrived.");
            return lines;
        }

        private static List<string> NewLines(Settings s)
        {
            var lines = new List<string>
            {
                "The Bunny menu, Alt B: Talk to Bunny, check on me now, start a new talk, get to know me again, Stop Bunny, Bunny changes today, Today's briefing, What I finished, Import a syllabus with Bunny, Visit your bunny and Smarter Bunny settings, all in one place. They used to be in File and Options, and every shortcut still works.",
                "Bunny, Today's briefing: your daily briefing any time, or type give me my briefing in Talk to Bunny. Bunny, What I finished: everything you finished today and the two weeks before.",
                "The Bunny Toolbox: Control Shift H, or Fun, Open the toolbox. Drawers of tools, a search box, and Control D for favorites.",
                "The Bunny tab, after Upcoming: visit your bunny, Bunny's world, the burrow and the adventures, one line each.",
                "The Day menu: rough day, kind of day, vacation, low-energy and finals week modes, the morning check-in and more.",
                "Settings, Fun and tools: Calm mode pauses every fun feature at once, how many lines Bunny may say by itself each day, quiet hours, and a switch for each tool.",
                "Settings, Tasks: importing, exporting and backups, moved from the File menu.",
            };
            lines.AddRange(Tools.NewLines(s));
            return lines;
        }

        /// <summary>Opens the toolbox; the chosen tool runs once the toolbox has closed and focus is back where she was.</summary>
        public void Open(string? drawer = null)
        {
            var back = _w.KeepFocus();
            var t = ToolboxWindow.Show(_w, drawer);
            back();
            if (t != null) _w.Dispatcher.BeginInvoke(() => Tools.Use(_w, t), System.Windows.Threading.DispatcherPriority.Input);
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) { yield return new FunAndToolsPage(settings, _w); }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("toolbox: " + what); }
            // Every tool that moved out of the Fun menu is registered.
            string[] moved =
            {
                "sprint", "timeperclass", "pickone", "quickwins", "toomuch", "kindofday", "roughday", "vacation", "scoreboard", "badges",
                "visitbunny", "challenges", "wrapped", "finals", "didathing", "parkthought", "parkinglot", "burrow", "rival", "bet",
                "mysterybox", "quest", "packs", "happened", "world", "rivaltalk", "narrator",
            };
            foreach (var id in moved) Need(Tools.Find(id) != null, "no tool " + id);
            Need(Tools.All.All(t => Drawers.All.Contains(t.Drawer)), "a tool is in a drawer that does not exist");
            Need(Tools.All.Select(t => t.Id).Distinct().Count() == Tools.All.Count, "two tools share an id");
            foreach (var t in Tools.All) Need(!(t.Name + t.WhatItDoes).Contains((char)35) && !(t.Name + t.WhatItDoes).Contains((char)42) && !t.Name.Contains('_'), "tool text: " + t.Name);
            // Favorites first, tools not used in a month last, turned off ones gone.
            var s = new Settings();
            var now = DateTime.Now;
            var drawer = Tools.All.GroupBy(t => t.Drawer).First(g => g.Count() >= 3).ToList();
            s.ToolFavorites.Add(drawer[2].Id);
            s.ToolUsed[drawer[0].Id] = now.AddDays(-40);
            var order = Tools.InDrawer(s, drawer[0].Drawer, now);
            Need(order[0] == drawer[2] && order[^1] == drawer[0], "favorites first and stale tools last");
            Tools.SetOn(s, drawer[1], false);
            Need(!Tools.InDrawer(s, drawer[0].Drawer, now).Contains(drawer[1]) && !Tools.IsOn(s, drawer[1]), "a tool turned off still shows");
            Need(Tools.Search(new Settings(), "sprint", now).Any(t => t.Id == "sprint"), "search");
            Need(Tools.MoveLines().Count >= moved.Length, "Where things moved is short");
            // Switches say their state.
            foreach (var id in new[] { "finals", "rivaltalk", "narrator" })
                Need(Tools.Find(id)?.Spoken is string sp && (sp.EndsWith(", now on") || sp.EndsWith(", now off")), id + " does not say whether it is on");
            // A tool turned off stops its shortcut and its Bunny tab line.
            var cfg = Engines.Settings!;
            var bet = Tools.Find("bet")!;
            bool ran = false;
            var guarded = Tools.Guard("bet", () => ran = true);
            Tools.SetOn(cfg, bet, false);
            try
            {
                guarded();
                Need(!ran && !Tools.IsOn("bet"), "a turned-off tool's shortcut still ran");
                Need(!BunnyTab.Visible().Any(e => e.ToolId == "bet"), "a turned-off tool still shows on the Bunny tab");
            }
            finally { Tools.SetOn(cfg, bet, true); }
            guarded();
            Need(ran && Tools.IsOn("bet"), "a tool turned back on did not run");
            Need(Tools.IsOn("no.such.tool"), "an unknown id counts as on");
            // Every menu has its own letters after the moves.
            Need(MainWindow.MenuKeyProblems.Count == 0, "menus share letters: " + string.Join("; ", MainWindow.MenuKeyProblems));
            // The Bunny menu (issue 19) holds every Bunny item; File and Options hold none.
            if (MainWindow.MenuHeaders.TryGetValue("Bunny", out var bunnyItems))
            {
                foreach (var h in new[] { "Talk to Bunny", "Bunny, check on me now", "Start a new talk with Bunny", "Bunny, get to know me again", "Stop Bunny", "Bunny changes today...", "Today's briefing", "What I finished...", "Import a syllabus with Bunny...", "Visit your bunny...", "Smarter Bunny settings..." })
                    Need(bunnyItems.Contains(h), "the Bunny menu has no " + h);
                Need(bunnyItems[0] == "Talk to Bunny" && bunnyItems[^1] == "Smarter Bunny settings...", "the Bunny menu is out of order");
                foreach (var top in new[] { "File", "Options" })
                    Need(!MainWindow.MenuHeaders[top].Any(h => h.Contains("Bunny") && !h.Contains("Busy Bunny")), "a Bunny item is still in " + top);
                Need(Tools.MoveLines().Any(l => l.Contains("Bunny menu")), "Where things moved does not mention the Bunny menu");
                Need(NewLines(new Settings())[0].StartsWith("The Bunny menu"), "New in Busy Bunny does not start with the Bunny menu");
            }
            else Need(false, "there is no Bunny menu");
            Need(MainWindow.MovedMenuKeys.Count == 0, "a menu letter had to move: " + string.Join("; ", MainWindow.MovedMenuKeys));
        }
    }

    /// <summary>Options, Settings, Fun and tools (sweep 8.1): Calm mode, the daily cap, quiet hours, the weather town, and a switch for each tool by drawer.</summary>
    public sealed class FunAndToolsPage : ISettingsPage
    {
        private readonly Settings _s;
        private readonly MainWindow? _w;
        private readonly CheckBox _calm;
        private readonly ComboBox _cap, _start, _end, _write, _writeEach;
        private readonly int[] _limits = { 0, 5, 10, 20, 40, 60, 100 };
        private readonly TextBox _town;
        private readonly Dictionary<BunnyTool, CheckBox> _boxes = new();

        private static string Hour(int h) => h == 0 ? "midnight" : h == 12 ? "noon" : (h % 12) + (h < 12 ? " am" : " pm");

        public FunAndToolsPage(Settings s, MainWindow? w)
        {
            _s = s;
            _w = w;
            var panel = new StackPanel();
            _calm = new CheckBox { Content = "_Calm mode: pause every fun feature at once, keeping all progress", IsChecked = s.CalmMode, Margin = new Thickness(0, 4, 0, 4) };
            panel.Children.Add(_calm);
            _cap = new ComboBox { ItemsSource = Enumerable.Range(0, 11).Select(n => n == 0 ? "None" : n + (n == 1 ? " line" : " lines")).ToList(), SelectedIndex = Math.Clamp(s.UnpromptedCap, 0, 10) };
            panel.Children.Add(Layout.Row("Lines Busy Bunny may say by itself each _day", _cap, 420));
            var hours = Enumerable.Range(0, 24).Select(Hour).ToList();
            _start = new ComboBox { ItemsSource = hours, SelectedIndex = Math.Clamp(s.QuietStart, 0, 23) };
            _end = new ComboBox { ItemsSource = hours, SelectedIndex = Math.Clamp(s.QuietEnd, 0, 23) };
            panel.Children.Add(Layout.Row("_Quiet hours start", _start, 200));
            panel.Children.Add(Layout.Row("Quiet hours e_nd", _end, 200));
            _town = new TextBox { Text = s.WeatherTown };
            AutomationProperties.SetHelpText(_town, "For the weather in Bunny's world. Type a town, like Austin. Busy Bunny never finds where you are by itself. Leave it empty for no weather.");
            panel.Children.Add(Layout.Row("_Town for the weather in Bunny's world", _town, 420));
            // The daily cost limit for Claude writing fun lines (only answers that came back count).
            _write = new ComboBox { ItemsSource = _limits.Select(n => n == 0 ? "None, always use the built-in lines" : n + " a day").ToList() };
            _write.SelectedIndex = Math.Max(0, Array.FindLastIndex(_limits, n => n <= s.BunnyWriteDaily));
            _writeEach = new ComboBox { ItemsSource = _limits.Select(n => n == 0 ? "None" : n + " a day").ToList() };
            _writeEach.SelectedIndex = Math.Max(0, Array.FindLastIndex(_limits, n => n <= s.BunnyWritePerFeature));
            AutomationProperties.SetHelpText(_write, "Only with a Claude API key. Each line uses a little of your credit. Past the limit, Busy Bunny uses its own lines. Only lines Claude really wrote count.");
            panel.Children.Add(Layout.Row("Fun lines Claude may _write each day, all features together", _write, 420));
            panel.Children.Add(Layout.Row("Fun lines Claude may write each day for each _feature", _writeEach, 420));
            panel.Children.Add(new TextBlock { Text = "Each tool in the toolbox, by drawer. Uncheck one to turn it off: it leaves the toolbox, and its shortcut, its Bunny tab line and anything it does by itself stop too.", TextWrapping = TextWrapping.Wrap, MaxWidth = 420, Margin = new Thickness(0, 8, 0, 4) });
            foreach (var d in Drawers.All)
            {
                var tools = Tools.All.Where(t => t.Drawer == d).ToList();
                if (tools.Count == 0) continue;
                var inner = new StackPanel();
                foreach (var t in tools)
                {
                    var cb = new CheckBox { Content = t.Name.Replace("_", "__"), IsChecked = Tools.IsOn(s, t), Margin = new Thickness(0, 2, 0, 2) };
                    AutomationProperties.SetHelpText(cb, t.WhatItDoes);
                    _boxes[t] = cb;
                    inner.Children.Add(cb);
                }
                var g = new GroupBox { Header = d, Content = inner, Margin = new Thickness(0, 4, 0, 4) };
                AutomationProperties.SetName(g, d);
                panel.Children.Add(g);
            }
            Content = new ScrollViewer { Content = panel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        }

        public string Name => "Fun and tools";
        public FrameworkElement Content { get; }

        public void Apply()
        {
            _s.CalmMode = _calm.IsChecked == true;
            _s.UnpromptedCap = Math.Max(0, _cap.SelectedIndex);
            _s.QuietStart = Math.Max(0, _start.SelectedIndex);
            _s.QuietEnd = Math.Max(0, _end.SelectedIndex);
            _s.BunnyWriteDaily = _limits[Math.Max(0, _write.SelectedIndex)];
            _s.BunnyWritePerFeature = _limits[Math.Max(0, _writeEach.SelectedIndex)];
            foreach (var (t, cb) in _boxes) Tools.SetOn(_s, t, cb.IsChecked == true);
            string town = _town.Text.Trim();
            if (town == _s.WeatherTown) return;
            _s.WeatherTown = town;
            _s.WeatherLat = _s.WeatherLon = 0;
            if (town.Length == 0 || _w == null) return;
            var w = _w;
            var s = _s;
            _ = LookUp();
            async System.Threading.Tasks.Task LookUp()
            {
                var found = await RealWorld.FindTown(town);
                if (found == null) { Speech.Say(w, "I could not find the town " + town + " just now, so the weather stays a mystery. Check its spelling in Settings, Fun and tools."); return; }
                s.WeatherTown = found.Value.Name;
                s.WeatherLat = found.Value.Lat;
                s.WeatherLon = found.Value.Lon;
                s.Save();
                Speech.Say(w, "Found " + found.Value.Name + " for the weather in Bunny's world.");
            }
        }
    }

    /// <summary>A line on the Bunny tab: name first, then a few words; Enter runs it.</summary>
    public sealed record BunnyTabEntry(string Name, string Description, Action Run, string? ToolId = null);

    /// <summary>
    /// The Bunny tab (sweep 11.3), after Upcoming: one line per thing, Enter opens it. Only things that exist are
    /// listed; features add theirs with BunnyTab.Register in Attach, and the order is the order registered. An entry
    /// with a toolId leaves the tab while that tool is turned off.
    /// </summary>
    public sealed class BunnyTab : IFeature
    {
        private static readonly List<BunnyTabEntry> _entries = new();
        public static IReadOnlyList<BunnyTabEntry> Entries => _entries;
        private static Action? _changed;

        public static void Register(string name, string description, Action run, string? toolId = null)
        {
            _entries.RemoveAll(e => e.Name == name);
            _entries.Add(new BunnyTabEntry(name, description, run, toolId));
            _changed?.Invoke();
        }

        /// <summary>The entries shown now: those whose tool is on, Visit your bunny first (issue 19: most important first), then in the order registered.</summary>
        public static List<BunnyTabEntry> Visible() => _entries.Where(e => e.ToolId == null || Tools.IsOn(e.ToolId)).OrderBy(e => e.ToolId == "visitbunny" ? 0 : 1).ToList();
        private List<BunnyTabEntry> _shown = new();

        private MainWindow _w = null!;
        private readonly ListBox _list = new() { MinHeight = 60, MaxHeight = 420, BorderThickness = new Thickness(1) };

        public void Attach(MainWindow w)
        {
            _w = w;
            AutomationProperties.SetHelpText(_list, "Enter opens the one you are on.");
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(_list);
            var tab = w.AddTab("Bunny", panel, () => _list);
            _changed = Fill;
            w.Started += Fill;
            w.TabOpened += t => { if (t == tab) Fill(); };
            // After Settings (a tool turned on or off) or Control Z: only when the lines really changed, so focus stays put.
            w.Refreshed += () => { if (!Visible().SequenceEqual(_shown)) Fill(); };
            _list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
                e.Handled = true;
                Run();
            };
            _list.MouseDoubleClick += (_, _) => Run();
            Fill();
        }

        private void Fill()
        {
            int keep = Math.Max(0, _list.SelectedIndex);
            bool had = _list.IsKeyboardFocusWithin;
            _shown = Visible();
            _list.Items.Clear();
            foreach (var e in _shown) _list.Items.Add(new ListBoxItem { Content = e.Name + ": " + e.Description });
            AutomationProperties.SetName(_list, "Bunny, " + _shown.Count + (_shown.Count == 1 ? " thing" : " things"));
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Min(keep, _list.Items.Count - 1);
            if (had && _list.Items.Count > 0) TextViewer.Select(_list, _list.SelectedIndex);
        }

        private void Run()
        {
            int i = _list.SelectedIndex;
            if (i < 0 || i >= _shown.Count) return;
            var entry = _shown[i];
            var back = _w.KeepFocus();
            try { entry.Run(); }
            catch (Exception e) { ErrorLog.Write("The Bunny tab's " + entry.Name + " failed: " + e); _w.Say(entry.Name + " did not work just now.", true); }
            // Back on the same line when what it opened has closed.
            if (_list.IsVisible && _w.IsActive) back();
        }

        public void SelfTest(Store store)
        {
            foreach (var name in new[] { "Visit your bunny", "Bunny's world", "The burrow" })
                if (!_entries.Any(e => e.Name == name)) throw new Exception("Bunny tab: no " + name);
            // Every line that belongs to a tool names one that exists, so turning the tool off takes the line away.
            foreach (var e in _entries)
                if (e.ToolId != null && Tools.Find(e.ToolId) == null) throw new Exception("Bunny tab: " + e.Name + " names a tool that does not exist");
            if (_entries.Count(e => e.ToolId == null) > 0) throw new Exception("Bunny tab: a line has no tool, so it cannot be turned off");
            if (Tools.IsOn("visitbunny") && Visible().FirstOrDefault()?.Name != "Visit your bunny") throw new Exception("Bunny tab: Visit your bunny is not the first line");
        }
    }
}
