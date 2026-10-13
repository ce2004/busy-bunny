using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Options, Settings (Morgan's issue 4): one window, a Section pick menu at the top. General first,
    /// then a section for each list in tab order (its name, its place among the tabs, its priority
    /// scale), then Add a new list. Nothing changes until OK; Cancel forgets it all; Control Z in the
    /// main window undoes the lists' changes.
    /// </summary>
    public static class SettingsWindow
    {
        private const string GeneralName = "General";
        private const string AddName = "Add a new list";
        private const string NoneName = "None";
        private const string CustomName = "Custom";

        private sealed class Pending
        {
            public string? Original; // null for a list added here
            public string Name = "";
            public string ScaleName = "";
            public string[] Levels = Array.Empty<string>();
            public Pending? Parent; // the list it sits under (spec 4.9), or null
            public string Sections = "";
            public Status DefaultStatus = Status.ThisWeek;
            public bool InToday = true;
            public bool Archived;
            public string FinishSound = "";
            public string SoundPack = "";
            public HashSet<TaskType> Types = new(Enum.GetValues<TaskType>());
            public TaskList? Real; // the saved list, made at OK for a new one
        }

        private static string TypeName(TaskType t) => t switch
        {
            TaskType.General => "General task",
            TaskType.Assignment => "Assignment",
            TaskType.Test => "Test",
            TaskType.Meeting => "Meeting",
            TaskType.Event => "Event",
            _ => "Personal goal",
        };

        /// <summary>A small pick dialog: returns the chosen index, or -1 for Cancel.</summary>
        private static int Pick(Window owner, string title, string text, string label, IList<string> choices, string okText)
        {
            var d = Layout.Dialog(owner, title);
            var box = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };
            var panel = new StackPanel { Margin = new Thickness(14), Width = 440 };
            panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(Layout.Row(label, box, 420));
            var ok = Layout.Button(okText, isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            panel.Children.Add(row);
            d.Content = panel;
            ok.Click += (_, _) => d.DialogResult = true;
            d.Loaded += (_, _) => box.Focus();
            return d.ShowDialog() == true ? box.SelectedIndex : -1;
        }

        /// <summary>Shows Settings, opened at a list's section when one is given; focus "scale" starts on its priority scale. True when OK was pressed.</summary>
        public static bool Show(Window owner, Store store, Settings settings, string? startList = null, string? focus = null)
        {
            var w = Layout.Dialog(owner, "Settings");
            var pages = Features.All.SelectMany(f => f.SettingsPages(store, settings)).ToList();
            int P = pages.Count;
            var lists = store.Data.Lists.Select(l => new Pending
            {
                Original = l.Name, Name = l.Name, ScaleName = l.Scale.Length == 0 ? "" : l.ScaleName, Levels = l.Scale,
                Sections = string.Join(Environment.NewLine, l.Sections), DefaultStatus = l.DefaultStatus, InToday = l.InToday,
                Archived = l.Archived, FinishSound = l.FinishSound, SoundPack = l.SoundPack, Types = l.Types.Count == 0 ? new(Enum.GetValues<TaskType>()) : new(l.Types), Real = l,
            }).ToList();
            foreach (var p in lists) p.Parent = lists.FirstOrDefault(o => o.Name == p.Real!.Parent && o != p);
            // Lists taken out here (merged or deleted), with where their tasks go (null: deleted with them). Done at OK.
            var removed = new List<(Pending Gone, Pending? Into)>();

            // ---- General ----
            var soundsOn = new CheckBox { Content = "_Play sounds", IsChecked = settings.SoundsOn, Margin = new Thickness(0, 4, 0, 4) };
            var soundsButton = Layout.Button("Choose s_ounds...");
            soundsButton.HorizontalAlignment = HorizontalAlignment.Left;
            soundsButton.Click += (_, _) => { SoundsDialog.Show(w, settings); soundsOn.IsChecked = settings.SoundsOn; };
            var shortMode = new CheckBox { Content = "S_hort mode: shorter task lines", IsChecked = settings.ShortMode, Margin = new Thickness(0, 10, 0, 4) };
            var autoUpdate = new CheckBox { Content = "Check for _updates automatically", IsChecked = settings.CheckForUpdatesAtStart, Margin = new Thickness(0, 4, 0, 4) };
            var hours = new ComboBox { ItemsSource = Enumerable.Range(1, 14).Select(h => h + (h == 1 ? " hour" : " hours")).ToList() };
            hours.SelectedIndex = Math.Clamp((int)Math.Round(store.Data.HoursAvailable) - 1, 0, 13);
            var warn = new ComboBox { ItemsSource = new[] { "Never" }.Concat(Enumerable.Range(3, 13).Select(n => "More than " + n)).ToList() };
            warn.SelectedIndex = settings.UrgentWarnAbove <= 0 ? 0 : Math.Clamp(settings.UrgentWarnAbove - 2, 1, 13);
            var general = new StackPanel();
            general.Children.Add(soundsOn);
            general.Children.Add(soundsButton);
            general.Children.Add(shortMode);
            general.Children.Add(autoUpdate);
            var music = new CheckBox { Content = "Background _music while Busy Bunny is in front", IsChecked = settings.MusicOn, Margin = new Thickness(0, 4, 0, 4) };
            general.Children.Add(music);
            general.Children.Add(Layout.Row("Hours _available on a typical day", hours));
            general.Children.Add(Layout.Row("_Warn me when urgent tasks are", warn));

            // ---- A list ----
            var name = new TextBox();
            var position = new ComboBox();
            var scales = new ComboBox { ItemsSource = new[] { NoneName }.Concat(Scales.Library.Select(s => s.Name)).Append(CustomName).ToList() };
            var levels = new TextBox { TextWrapping = TextWrapping.Wrap, IsReadOnlyCaretVisible = true };
            var customName = new TextBox();
            AutomationProperties.SetHelpText(position, "1 is the first list tab, after Today, Focus and Upcoming.");
            AutomationProperties.SetHelpText(levels, "For a custom scale, type the levels highest first, separated by commas.");
            var listPanel = new StackPanel();
            listPanel.Children.Add(Layout.Row("List _name", name));
            listPanel.Children.Add(Layout.Row("Place among the _tabs", position));
            listPanel.Children.Add(Layout.Row("Priority _scale", scales));
            listPanel.Children.Add(Layout.Row("Le_vels, highest first", levels, 420));
            listPanel.Children.Add(Layout.Row("Custom scale na_me", customName));

            var parent = new ComboBox();
            var promote = Layout.Button("_Promote to its own list");
            promote.HorizontalAlignment = HorizontalAlignment.Left;
            var defStatus = new ComboBox { ItemsSource = Enum.GetValues<Status>().Where(s => s != Status.Done).Select(Words.Of).ToList() };
            var inToday = new CheckBox { Content = "Show its tasks in _Today and Focus", Margin = new Thickness(0, 4, 0, 4) };
            const string UsualSound = "Usual Task done sound";
            var finish = new ComboBox { ItemsSource = new[] { UsualSound }.Concat(Sounds.All).ToList() };
            // Morgan's issue 6: a sound pack for each tab, for adding, saving and finishing its tasks.
            var packChoices = new[] { "", Sounds.MatchScale }.Concat(Sounds.PackNames).ToList();
            var pack = new ComboBox();
            AutomationProperties.SetHelpText(pack, "The sounds for adding, saving and finishing this list's tasks. Arrow through them to hear each one finish a task.");
            var typeBoxes = Enum.GetValues<TaskType>().ToDictionary(t => t, t => new CheckBox { Content = TypeName(t), Margin = new Thickness(0, 2, 0, 2) });
            var types = new StackPanel();
            foreach (var cb in typeBoxes.Values) types.Children.Add(cb);
            var group = new GroupBox { Header = "Task types it uses", Content = types, Margin = new Thickness(0, 4, 0, 6) };
            AutomationProperties.SetName(group, "Task types it uses");
            var sections = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinLines = 3, MaxLines = 6, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            AutomationProperties.SetHelpText(sections, "Like drawers inside the list, for example one per class. One per line, or separated by commas. Each shows in the list's tab as its own named list.");
            var archived = new CheckBox { Content = "_Archive this list: no tab, and its tasks show nowhere", Margin = new Thickness(0, 4, 0, 4) };
            var merge = Layout.Button("_Merge into another list...");
            var delete = Layout.Button("De_lete this list...");
            merge.HorizontalAlignment = delete.HorizontalAlignment = HorizontalAlignment.Left;
            AutomationProperties.SetHelpText(parent, "Choose a list to put this one inside, or None. A list with sub-lists of its own can't go inside another.");
            listPanel.Children.Add(Layout.Row("Sits _inside", parent));
            listPanel.Children.Add(promote);
            listPanel.Children.Add(Layout.Row("Default status for _new tasks", defStatus));
            listPanel.Children.Add(inToday);
            listPanel.Children.Add(Layout.Row("Categories, one per _line", sections, 420));
            listPanel.Children.Add(Layout.Row("Soun_d pack for this tab", pack, 420));
            listPanel.Children.Add(Layout.Row("_Finishing sound", finish, 420));
            listPanel.Children.Add(group);
            listPanel.Children.Add(archived);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            actions.Children.Add(merge);
            actions.Children.Add(delete);
            listPanel.Children.Add(actions);

            var listScroll = new ScrollViewer { Content = listPanel, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            var statuses = Enum.GetValues<Status>().Where(s => s != Status.Done).ToArray();
            var parentChoices = new List<Pending>();
            var section = new ComboBox();
            var body = new ContentControl { Focusable = false, MinHeight = 300 };
            Pending? current = null;
            bool loading = false;

            List<string> SectionNames() => new[] { GeneralName }.Concat(pages.Select(p => p.Name)).Concat(lists.Select(l => l.Name + (l.Archived ? " list, archived" : " list"))).Append(AddName).ToList();
            void FillSections(int select)
            {
                loading = true;
                section.ItemsSource = SectionNames();
                section.SelectedIndex = select;
                loading = false;
            }
            bool IsCustom() => scales.SelectedItem as string == CustomName;
            void ShowLevels()
            {
                if (current == null) return;
                levels.IsReadOnly = !IsCustom();
                levels.Text = IsCustom() ? string.Join(", ", current.Levels) : Scales.Rank(current.Levels);
                customName.IsEnabled = IsCustom();
            }
            void LoadList(Pending p)
            {
                loading = true;
                current = p;
                name.Text = p.Name;
                position.ItemsSource = Enumerable.Range(1, lists.Count).Select(i => i + " of " + lists.Count).ToList();
                position.SelectedIndex = lists.IndexOf(p);
                scales.SelectedItem = p.Levels.Length == 0 ? NoneName
                    : Scales.Library.Any(s => s.Name == p.ScaleName && s.Levels.SequenceEqual(p.Levels)) ? p.ScaleName : CustomName;
                customName.Text = scales.SelectedItem as string == CustomName ? p.ScaleName : "";
                ShowLevels();
                parentChoices.Clear();
                bool hasSubs = lists.Any(o => o.Parent == p);
                if (!hasSubs) parentChoices.AddRange(lists.Where(o => o != p && o.Parent == null));
                parent.ItemsSource = new[] { NoneName }.Concat(parentChoices.Select(o => o.Name)).ToList();
                parent.SelectedIndex = p.Parent == null ? 0 : Math.Max(0, parentChoices.IndexOf(p.Parent) + 1);
                parent.IsEnabled = !hasSubs;
                promote.IsEnabled = p.Parent != null;
                defStatus.SelectedIndex = Math.Max(0, Array.IndexOf(statuses, p.DefaultStatus));
                inToday.IsChecked = p.InToday;
                finish.SelectedIndex = p.FinishSound.Length == 0 ? 0 : Math.Max(0, Sounds.All.ToList().IndexOf(p.FinishSound) + 1);
                pack.ItemsSource = packChoices.Select(c => Sounds.ListPackWords(new TaskList { SoundPack = c, ScaleName = p.Levels.Length == 0 ? "" : p.ScaleName })).ToList();
                pack.SelectedIndex = Math.Max(0, packChoices.IndexOf(p.SoundPack));
                foreach (var (t, cb) in typeBoxes) cb.IsChecked = p.Types.Contains(t);
                sections.Text = p.Sections;
                archived.IsChecked = p.Archived;
                merge.IsEnabled = delete.IsEnabled = lists.Count > 1;
                loading = false;
            }
            void ShowSection()
            {
                int i = section.SelectedIndex;
                if (i <= 0) { current = null; body.Content = general; return; }
                if (i <= P) { current = null; body.Content = pages[i - 1].Content; return; }
                i -= P;
                if (i > lists.Count)
                {
                    // Add a new list: made at once, at the end, and its section opened on the name.
                    int n = 1;
                    while (lists.Any(l => l.Name.Equals("New list " + n, StringComparison.OrdinalIgnoreCase))) n++;
                    var p = new Pending { Name = "New list " + n };
                    lists.Add(p);
                    FillSections(lists.Count + P);
                    LoadList(p);
                    body.Content = listScroll;
                    Speech.Say(w, "Added " + p.Name + ". Type its name, then choose its settings. It is made when you press OK.", true);
                    w.Dispatcher.BeginInvoke(() => { name.Focus(); name.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
                    return;
                }
                LoadList(lists[i - 1]);
                body.Content = listScroll;
            }
            int ListSection(Pending p) => lists.IndexOf(p) + 1 + P;
            section.SelectionChanged += (_, _) => { if (!loading) ShowSection(); };

            name.TextChanged += (_, _) => { if (!loading && current != null) current.Name = name.Text.Trim(); };
            // The section menu shows the new name once you move on, not on every letter.
            name.LostKeyboardFocus += (_, _) => { if (current != null) FillSections(ListSection(current)); };
            position.SelectionChanged += (_, _) =>
            {
                if (loading || current == null || position.SelectedIndex < 0) return;
                lists.Remove(current);
                lists.Insert(position.SelectedIndex, current);
                FillSections(ListSection(current));
            };
            scales.SelectionChanged += (_, _) =>
            {
                if (loading || current == null || scales.SelectedItem is not string s) return;
                if (s == NoneName) { current.ScaleName = ""; current.Levels = Array.Empty<string>(); }
                else if (s == CustomName) current.ScaleName = customName.Text.Trim().Length > 0 ? customName.Text.Trim() : CustomName;
                else { current.ScaleName = s; current.Levels = Scales.Library.First(x => x.Name == s).Levels; }
                ShowLevels();
            };
            levels.TextChanged += (_, _) =>
            {
                if (loading || current == null || !IsCustom()) return;
                current.Levels = levels.Text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            };
            customName.TextChanged += (_, _) =>
            {
                if (loading || current == null || !IsCustom()) return;
                current.ScaleName = customName.Text.Trim().Length > 0 ? customName.Text.Trim() : CustomName;
            };

            parent.SelectionChanged += (_, _) =>
            {
                if (loading || current == null || parent.SelectedIndex < 0) return;
                current.Parent = parent.SelectedIndex == 0 ? null : parentChoices[parent.SelectedIndex - 1];
                promote.IsEnabled = current.Parent != null;
            };
            promote.Click += (_, _) =>
            {
                if (current == null) return;
                current.Parent = null;
                LoadList(current);
                Speech.Say(w, current.Name + " is a list of its own now. It gets a tab when you press OK.", true);
                parent.Focus();
            };
            defStatus.SelectionChanged += (_, _) => { if (!loading && current != null && defStatus.SelectedIndex >= 0) current.DefaultStatus = statuses[defStatus.SelectedIndex]; };
            inToday.Click += (_, _) => { if (current != null) current.InToday = inToday.IsChecked == true; };
            finish.SelectionChanged += (_, _) =>
            {
                if (loading || current == null || finish.SelectedIndex < 0) return;
                current.FinishSound = finish.SelectedIndex == 0 ? "" : (string)finish.SelectedItem;
                if (finish.SelectedIndex > 0) Sounds.PlayNamed(current.FinishSound);
            };
            pack.SelectionChanged += (_, _) =>
            {
                if (loading || current == null || pack.SelectedIndex < 0) return;
                current.SoundPack = packChoices[pack.SelectedIndex];
                Sounds.PlayNamed(Sounds.ListSound(Sounds.Event.TaskDone, new TaskList { SoundPack = current.SoundPack, ScaleName = current.Levels.Length == 0 ? "" : current.ScaleName }));
            };
            foreach (var (t, cb) in typeBoxes)
            {
                var type = t;
                var box = cb;
                box.Click += (_, _) =>
                {
                    if (current == null) return;
                    if (box.IsChecked == true) current.Types.Add(type);
                    else current.Types.Remove(type);
                };
            }
            sections.TextChanged += (_, _) => { if (!loading && current != null) current.Sections = sections.Text; };
            archived.Click += (_, _) => { if (current != null) current.Archived = archived.IsChecked == true; };

            // Merge and delete are remembered here and carried out at OK, so Cancel forgets them and Control Z undoes them.
            void TakeOut(Pending p, Pending? into)
            {
                int at = lists.IndexOf(p);
                lists.Remove(p);
                removed.Add((p, into));
                foreach (var o in lists.Where(o => o.Parent == p)) o.Parent = null;
                // Anything that was to land on the removed list lands where it went.
                for (int i = 0; i < removed.Count; i++) if (removed[i].Into == p) removed[i] = (removed[i].Gone, into);
                var next = lists[Math.Min(at, lists.Count - 1)];
                FillSections(ListSection(next));
                LoadList(next);
                body.Content = listScroll;
                section.Focus();
            }
            merge.Click += (_, _) =>
            {
                if (current == null || lists.Count < 2) return;
                var gone = current;
                var targets = lists.Where(o => o != gone).ToList();
                int n = gone.Original == null ? 0 : store.Data.Tasks.Count(t => t.List == gone.Original);
                int i = Pick(w, "Merge " + gone.Name, "All " + n + (n == 1 ? " task" : " tasks") + " on " + gone.Name + " move to the list you pick, and " + gone.Name + " goes away. Its categories come along.",
                    "Merge into", targets.Select(o => o.Name).ToList(), "_Merge");
                if (i < 0) return;
                var into = targets[i];
                var secs = ListTools.ParseSections(into.Sections);
                foreach (var s in ListTools.ParseSections(gone.Sections)) if (!secs.Any(x => x.Equals(s, StringComparison.OrdinalIgnoreCase))) secs.Add(s);
                into.Sections = string.Join(Environment.NewLine, secs);
                string goneName = gone.Name;
                TakeOut(gone, into);
                Speech.Say(w, goneName + " will merge into " + into.Name + " when you press OK. Control Z undoes it.", true);
            };
            delete.Click += (_, _) =>
            {
                if (current == null || lists.Count < 2) return;
                var gone = current;
                var targets = lists.Where(o => o != gone).ToList();
                int n = gone.Original == null ? 0 : store.Data.Tasks.Count(t => t.List == gone.Original);
                string text = n == 0 ? gone.Name + " has no tasks. It goes away when you press OK."
                    : gone.Name + " has " + n + (n == 1 ? " task" : " tasks") + ". Delete them with it, or move them to another list. Control Z in the main window undoes either one.";
                int i = Pick(w, "Delete " + gone.Name, text, "What to _do", new[] { "Delete the tasks too" }.Concat(targets.Select(o => "Move the tasks to " + o.Name)).ToList(), "_Delete list");
                if (i < 0) return;
                string goneName = gone.Name;
                TakeOut(gone, i == 0 ? null : targets[i - 1]);
                Speech.Say(w, goneName + " will be deleted when you press OK.", true);
            };

            var panel = new StackPanel { Margin = new Thickness(14), Width = 460 };
            panel.Children.Add(Layout.Row("S_ection", section, 420));
            panel.Children.Add(body);
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = panel;

            ok.Click += (_, _) =>
            {
                if (lists.All(l => l.Archived))
                {
                    FillSections(ListSection(lists[0]));
                    LoadList(lists[0]);
                    body.Content = listScroll;
                    Speech.Say(w, "Every list is archived, and then there is nowhere to put a task. Unarchive one, or press Cancel.", true);
                    archived.Focus();
                    return;
                }
                // Every list needs a name of its own, and a custom scale needs levels.
                for (int i = 0; i < lists.Count; i++)
                {
                    var p = lists[i];
                    string? problem = p.Name.Length == 0 ? "A list has no name. Type one, or press Cancel."
                        : lists.Take(i).Any(o => o.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) ? "Two lists are called " + p.Name + ". Give one another name."
                        : p.ScaleName.Length > 0 && p.Levels.Length == 0 ? p.Name + " has a custom scale with no levels. Type them in Levels, separated by commas, or choose None."
                        : null;
                    if (problem == null) continue;
                    FillSections(i + 1 + P);
                    LoadList(p);
                    body.Content = listScroll;
                    Speech.Say(w, problem, true);
                    (p.Name.Length == 0 || problem.StartsWith("Two") ? name : levels).Focus();
                    return;
                }

                for (int i = 0; i < P; i++)
                {
                    if (pages[i].Validate() is not string problem) continue;
                    FillSections(i + 1);
                    body.Content = pages[i].Content;
                    Speech.Say(w, problem, true);
                    return;
                }

                settings.SoundsOn = soundsOn.IsChecked == true;
                settings.MusicOn = music.IsChecked == true;
                settings.ShortMode = shortMode.IsChecked == true;
                settings.CheckForUpdatesAtStart = autoUpdate.IsChecked == true;
                settings.UrgentWarnAbove = warn.SelectedIndex <= 0 ? 0 : warn.SelectedIndex + 2;
                settings.Save();
                settings.Apply();

                store.Remember("changing settings");
                store.Data.HoursAvailable = hours.SelectedIndex + 1;
                // New lists exist under a temporary name first, so merged or moved tasks have somewhere to land.
                int fresh = 0;
                foreach (var p in lists.Where(p => p.Real == null))
                {
                    p.Real = new TaskList { Name = "~new" + fresh++ };
                    store.Data.Lists.Add(p.Real);
                }
                foreach (var (gone, into) in removed.Where(r => r.Gone.Original != null))
                {
                    if (into == null) ListTools.DeleteList(store.Data, gone.Original!, "");
                    else ListTools.MergeList(store.Data, gone.Original!, into.Real!.Name);
                }
                var made = new List<TaskList>();
                foreach (var p in lists)
                {
                    var list = p.Real!;
                    if (list.Name != p.Name)
                        foreach (var t in store.Data.Tasks.Where(t => t.List == list.Name)) t.List = p.Name;
                    list.Name = p.Name;
                    list.Scale = p.Levels;
                    list.ScaleName = p.Levels.Length == 0 ? "" : p.ScaleName;
                    list.DefaultStatus = p.DefaultStatus;
                    list.InToday = p.InToday;
                    list.FinishSound = p.FinishSound;
                    list.SoundPack = p.SoundPack;
                    list.Archived = p.Archived;
                    list.Types = p.Types.Count == Enum.GetValues<TaskType>().Length || p.Types.Count == 0 ? new() : p.Types.OrderBy(t => t).ToList();
                    list.Sections = ListTools.ParseSections(p.Sections);
                    // A shorter scale: tasks past its end take its lowest level; no scale clears them.
                    foreach (var t in store.Data.Tasks.Where(t => t.List == p.Name && t.Priority > p.Levels.Length)) t.Priority = p.Levels.Length;
                    made.Add(list);
                }
                // Parents by their final names. An archived parent's sub-lists become lists of their own.
                foreach (var p in lists)
                    p.Real!.Parent = p.Parent == null || p.Parent.Archived ? "" : p.Parent.Name;
                store.Data.Lists = made;
                foreach (var page in pages) page.Apply();
                settings.Save();
                w.DialogResult = true;
            };

            int pageAt = startList == null ? -1 : pages.FindIndex(p => p.Name == startList);
            int start = startList == null ? 0 : pageAt >= 0 ? pageAt + 1 : lists.FindIndex(l => l.Name == startList) + 1 + P;
            FillSections(Math.Max(0, start));
            ShowSection();
            w.Loaded += (_, _) =>
            {
                if (focus == "scale" && current != null) { scales.Focus(); Speech.Say(w, current.Name + ": " + Scales.Rank(current.Levels), false); }
                else section.Focus();
            };
            return w.ShowDialog() == true;
        }
    }
}
