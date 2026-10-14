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
    /// The main window's tools for a list tab and its tasks: View tab settings with the priority scale
    /// (Morgan's issue 6), categories inside a list (issue 8), the Subtasks window (issue 7) and Get
    /// ahead on Today (issue 10).
    /// </summary>
    public sealed partial class MainWindow
    {
        // Which list and category each list tab's task list shows, for Quick add and Put in a category.
        private readonly Dictionary<ListBox, (string List, string Section)> _placeOf = new();

        /// <summary>The list for View tab settings and Add a category: the open list tab's, or else the chosen task's.</summary>
        private string? TabListName() =>
            _tabs.SelectedItem is TabItem { Tag: string l } ? l : Current()?.List;

        private void AddTabMenus()
        {
            AddMenuItem("View", "View _tab settings...", () => { if (TabListName() is string l) TabSettings(l); else Say("Open a list's tab first, like Homework, then its settings show.", true); }, "Ctrl+Shift+E");
            AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.E, () => { if (TabListName() is string l) TabSettings(l); else Say("Open a list's tab first, like Homework, then its settings show.", true); },
                "View tab settings, with its priority scale: Control Shift E. The button is also at the bottom of each list tab");
            AddMenuItem("View", "Add a _category to this list...", () => { if (TabListName() is string l) AddCategory(l); else Say("Open a list's tab first, like Homework.", true); });
            AddMenuItem("Task", "_Subtasks...", Subtasks, "Ctrl+Shift+B");
            AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.B, Subtasks, "Subtasks of the chosen task, to check off or add: Control Shift B");
            AddMenuItem("Task", "Put in a _category...", PutInCategory);
        }

        // ---- Get ahead (issue 10) ----

        private void GetAhead()
        {
            var ahead = _store.GetAhead(DateTime.Now);
            _tabs.SelectedItem = _upcomingTab;
            Dispatcher.BeginInvoke(() =>
            {
                FocusTab(false);
                Say(ahead.Count == 0 ? "Nothing else planned or due this week. Upcoming has the rest." :
                    "Upcoming. " + ahead.Count + (ahead.Count == 1 ? " task" : " tasks") + " later this week, if you have the energy. Finishing early earns bonus points.");
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        // ---- View tab settings (issue 6) ----

        /// <summary>A list's settings to read, a line each, the priority scale first; then buttons to change them.</summary>
        public static List<string> TabSettingsLines(Store store, TaskList l)
        {
            var lines = new List<string>();
            if (l.Scale.Length == 0) lines.Add("Priority scale: none.");
            else
            {
                lines.Add("Priority scale: " + l.ScaleName + ", " + l.Scale.Length + " levels, highest first:");
                for (int i = 0; i < l.Scale.Length; i++) lines.Add((i + 1) + ", " + l.Scale[i]);
            }
            lines.Add(l.Sections.Count == 0 ? "Categories: none yet. Add a category makes one." : "Categories: " + string.Join(", ", l.Sections) + ".");
            var subs = store.ActiveLists.Where(x => x.Parent == l.Name).Select(x => x.Name).ToList();
            if (subs.Count > 0) lines.Add("Lists inside it: " + string.Join(", ", subs) + ".");
            if (l.Parent.Length > 0) lines.Add("It sits inside " + l.Parent + ".");
            int place = store.ActiveLists.Where(x => x.Parent.Length == 0).ToList().FindIndex(x => x.Name == l.Name);
            if (place >= 0) lines.Add("Place among the list tabs: " + (place + 1) + " of " + store.ActiveLists.Count(x => x.Parent.Length == 0) + ".");
            lines.Add("New tasks start as: " + Words.Of(l.DefaultStatus) + ".");
            lines.Add(l.InToday ? "Its tasks show in Today and Focus." : "Its tasks stay out of Today and Focus.");
            lines.Add("Sounds for its tasks: " + Sounds.ListPackWords(l) + ".");
            if (l.FinishSound.Length > 0) lines.Add("Finishing sound: " + l.FinishSound + ".");
            if (l.Types.Count > 0) lines.Add("Task types: " + string.Join(", ", l.Types.Select(Views.TypeWords)) + ".");
            int open = store.Data.Tasks.Count(t => t.List == l.Name && t.Status != Status.Done);
            lines.Add(open == 1 ? "1 open task." : open + " open tasks.");
            return lines;
        }

        private void TabSettings(string listName)
        {
            if (_store.Data.Lists.FirstOrDefault(x => x.Name == listName) is not TaskList l) return;
            var w = Layout.Dialog(this, l.Name + " tab settings");
            var lines = TextViewer.LinesList(TabSettingsLines(_store, l), l.Name + " tab settings", 300);
            lines.Width = 460;
            string? then = null;
            var change = Layout.Button("Change _settings");
            var scale = Layout.Button("Change _priority scale");
            var category = Layout.Button("Add a _category");
            var close = Layout.Button("Close", isCancel: true);
            change.Click += (_, _) => { then = "settings"; w.Close(); };
            scale.Click += (_, _) => { then = "scale"; w.Close(); };
            category.Click += (_, _) => { then = "category"; w.Close(); };
            close.Click += (_, _) => w.Close();
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in new[] { change, scale, category, close }) buttons.Children.Add(b);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(lines);
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) => TextViewer.Select(lines, 0);
            w.ShowDialog();
            if (then == "settings") OpenSettings(listName);
            else if (then == "scale") OpenSettings(listName, "scale");
            else if (then == "category") AddCategory(listName);
            else FocusTab(false);
        }

        // ---- Categories (issue 8) ----

        private void AddCategory(string listName)
        {
            if (_store.Data.Lists.FirstOrDefault(x => x.Name == listName) is not TaskList l) return;
            string? typed = Prompt.Ask(this, "Add a category to " + l.Name, "Category _name, like a class");
            string name = (typed ?? "").Trim();
            if (name.Length == 0) { FocusTab(false); return; }
            if (l.Sections.Any(s => s.Equals(name, StringComparison.OrdinalIgnoreCase))) { Say(l.Name + " already has " + name + ".", true); return; }
            _store.Remember("adding category " + name);
            l.Sections.Add(name);
            SaveAndRefresh();
            // Straight into the new category's list, so Control N adds a task to it.
            var tab = _tabs.Items.OfType<TabItem>().FirstOrDefault(t => Equals(t.Tag, l.Parent.Length > 0 ? l.Parent : l.Name));
            if (tab != null) _tabs.SelectedItem = tab;
            Dispatcher.BeginInvoke(() =>
            {
                if (_lists.TryGetValue(SectionKey(l, name), out var lb)) TextViewer.Select(lb, 0);
                Say("Added the category " + name + " to " + l.Name + ". Control N adds a task to it, and Task, Put in a category moves one in.");
            }, System.Windows.Threading.DispatcherPriority.Input);
        }

        private void PutInCategory()
        {
            if (CurrentOrSay() is not TaskItem t) return;
            var l = _store.ListOf(t);
            if (l.Sections.Count == 0) { Say(t.List + " has no categories yet. View, Add a category to this list makes one.", true); return; }
            var choices = new[] { "No category" }.Concat(l.Sections).ToList();
            var w = Layout.Dialog(this, "Put in a category");
            var box = new ComboBox { ItemsSource = choices, SelectedIndex = Math.Max(0, choices.IndexOf(t.Section)) };
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            ok.Click += (_, _) => w.DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("Category for " + t.Name, box));
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) => box.Focus();
            if (w.ShowDialog() == true) SetCategory(t, box.SelectedIndex <= 0 ? "" : choices[box.SelectedIndex]);
            else if (_lastList != null) TextViewer.Select(_lastList, Math.Max(0, _lastList.SelectedIndex));
        }

        private void SetCategory(TaskItem t, string section)
        {
            if (t.Section == section) { Say(section.Length == 0 ? "It has no category already." : "It is already in " + section + ".", true); return; }
            _store.Remember("putting " + t.Name + " in a category");
            t.Section = section;
            t.TouchedAt = DateTime.Now;
            SaveAndRefresh();
            if (_lastList != null) TextViewer.Select(_lastList, Math.Max(0, Math.Min(_lastList.SelectedIndex, _lastList.Items.Count - 1)));
            Say(section.Length == 0 ? t.Name + " has no category now." : "Put " + t.Name + " in " + section + ".");
        }

        // ---- Subtasks (issue 7) ----

        /// <summary>The chosen task's subtasks as check boxes: Space checks one off at once, Delete removes it, and a box adds more.</summary>
        private void Subtasks()
        {
            if (CurrentOrSay() is not TaskItem t) return;
            var back = _lastList;
            var w = Layout.Dialog(this, "Subtasks of " + t.Name);
            var boxes = new StackPanel();
            AutomationProperties.SetName(boxes, "Subtasks");
            var scroll = new ScrollViewer { Content = boxes, MaxHeight = 320, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
            var empty = new TextBlock { Text = "No subtasks yet. Type one below and press Enter.", Margin = new Thickness(0, 0, 0, 6) };
            var fresh = new TextBox();
            var add = Layout.Button("_Add subtask");
            var close = Layout.Button("Close", isCancel: true);
            close.Click += (_, _) => w.Close();

            string Count() => t.Subtasks.Count(s => s.Done) + " of " + t.Subtasks.Count + " done.";
            void Fill(int focus)
            {
                boxes.Children.Clear();
                empty.Visibility = t.Subtasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                for (int i = 0; i < t.Subtasks.Count; i++)
                {
                    var sub = t.Subtasks[i];
                    var cb = new CheckBox { Content = sub.Name, IsChecked = sub.Done, Margin = new Thickness(0, 3, 0, 3) };
                    AutomationProperties.SetHelpText(cb, "Space checks it off. Delete removes it.");
                    cb.Click += (_, _) =>
                    {
                        _store.Remember((cb.IsChecked == true ? "checking off " : "unchecking ") + sub.Name);
                        sub.Done = cb.IsChecked == true;
                        t.TouchedAt = DateTime.Now;
                        SaveAndRefresh();
                        bool all = t.Subtasks.All(s => s.Done);
                        Say((sub.Done ? "Done: " : "Not done: ") + sub.Name + ". " + Count() + (all && sub.Done && t.Status != Status.Done ? " Every step is done. Control D marks the task done." : ""));
                    };
                    cb.PreviewKeyDown += (_, e) =>
                    {
                        if (e.Key != Key.Delete) return;
                        e.Handled = true;
                        int at = t.Subtasks.IndexOf(sub);
                        _store.Remember("deleting subtask " + sub.Name);
                        t.Subtasks.Remove(sub);
                        SaveAndRefresh();
                        Fill(Math.Min(at, t.Subtasks.Count - 1));
                        Say("Deleted " + sub.Name + ". Control Z in the main window brings it back.", true);
                    };
                    boxes.Children.Add(cb);
                }
                if (focus >= 0 && focus < boxes.Children.Count) w.Dispatcher.BeginInvoke(() => ((UIElement)boxes.Children[focus]).Focus(), System.Windows.Threading.DispatcherPriority.Input);
                else if (t.Subtasks.Count == 0) w.Dispatcher.BeginInvoke(() => fresh.Focus(), System.Windows.Threading.DispatcherPriority.Input);
            }
            void Add()
            {
                string name = fresh.Text.Trim();
                if (name.Length == 0) { Say("Type the subtask first.", true); fresh.Focus(); return; }
                _store.Remember("adding subtask " + name);
                t.Subtasks.Add(new Subtask { Name = name });
                t.TouchedAt = DateTime.Now;
                SaveAndRefresh();
                fresh.Clear();
                Fill(-1);
                fresh.Focus();
                Say("Added " + name + ". " + t.Subtasks.Count + (t.Subtasks.Count == 1 ? " subtask." : " subtasks."), true);
            }
            add.Click += (_, _) => Add();
            fresh.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Add(); } };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(add);
            buttons.Children.Add(close);
            var panel = new StackPanel { Margin = new Thickness(14), Width = 420 };
            panel.Children.Add(empty);
            panel.Children.Add(scroll);
            panel.Children.Add(Layout.Row("_New subtask", fresh, 400));
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) =>
            {
                Fill(t.Subtasks.FindIndex(s => !s.Done) is int first && first >= 0 ? first : 0);
                if (t.Subtasks.Count > 0) Say(Count());
            };
            w.ShowDialog();
            if (back != null && _lists.ContainsValue(back))
            {
                int at = IndexOf(back, t.Id);
                TextViewer.Select(back, at >= 0 ? at : Math.Max(0, back.SelectedIndex));
            }
        }
    }
}
