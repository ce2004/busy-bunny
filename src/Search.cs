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
    /// Search (Morgan's issue 13): Control F opens the Search tab. Type words, pick one list or every
    /// list, and press Enter. The results are a task list like any other, so F2, the Task menu, the
    /// pick menus and Control 1 to 6 all act on the task found. Finished tasks are left out (spec 22.8).
    /// </summary>
    public sealed class Search : IFeature
    {
        public const string EveryList = "Every list";

        private MainWindow _w = null!;
        private TabItem _tab = null!;
        private readonly TextBox _box = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        private readonly ComboBox _scope = new() { Width = 320, HorizontalAlignment = HorizontalAlignment.Left };
        private ListBox _results = null!;

        public void Attach(MainWindow w)
        {
            _w = w;
            _results = w.NewTaskList("search");
            AutomationProperties.SetHelpText(_box, "Type words from a task, then press Enter.");
            var panel = new StackPanel { Margin = new Thickness(6) };
            panel.Children.Add(Layout.Row("_Search for", _box));
            panel.Children.Add(Layout.Row("Search _in", _scope));
            panel.Children.Add(new Label { Content = "_Results", Target = _results, Padding = new Thickness(0, 8, 0, 2) });
            panel.Children.Add(_results);
            _tab = w.AddTab("Search", new ScrollViewer { Content = panel, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
                () => { FocusBox(); return null; }); // Control Tab into this tab lands in the box, ready to type

            FillScope(EveryList);
            _box.TextChanged += (_, _) => Refill();
            _scope.SelectionChanged += (_, _) => { if (_scope.IsKeyboardFocusWithin) Refill(); };
            _box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; Go(); } };
            _scope.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && !_scope.IsDropDownOpen) { e.Handled = true; Go(); } };
            // Escape in the results goes back to the box for another search.
            _results.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; FocusBox(); } };

            w.AddMenuItem("View", "_Search tasks...", Open, "Ctrl+F");
            w.AddKey(ModifierKeys.Control, Key.F, Open, "Ctrl+F: Search tasks, in the list you are on or every list. Escape in the results goes back to the search box");
            w.Refreshed += () => { FillScope(Scope); Refill(); };
            Refill();
        }

        private string Scope => _scope.SelectedItem as string ?? EveryList;

        private void FillScope(string want)
        {
            var names = new List<string> { EveryList };
            names.AddRange(_w.Store.ActiveLists.Select(l => l.Name));
            if (_scope.Items.Count == names.Count && _scope.Items.Cast<string>().SequenceEqual(names) && Scope == want) return;
            _scope.ItemsSource = names;
            _scope.SelectedIndex = Math.Max(0, names.IndexOf(want));
        }

        /// <summary>Control F: the Search tab, searching the list she was on, or every list from Today and the others.</summary>
        private void Open()
        {
            string want = _w.OpenListName ?? (_tab.IsSelected ? Scope : EveryList);
            FillScope(want);
            _tab.IsSelected = true;
            Refill();
            FocusBox();
        }

        private void FocusBox() => _w.Dispatcher.BeginInvoke(() => { _box.Focus(); _box.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);

        private void Refill()
        {
            string words = _box.Text.Trim();
            string where = Scope == EveryList ? "every list" : Scope;
            if (words.Length == 0) { _w.FillTasks(_results, Array.Empty<TaskItem>(), "Results", "Type words to search for, then press Enter."); return; }
            var found = Find(_w.Store, words, Scope == EveryList ? null : Scope);
            _w.FillTasks(_results, found, "Results for " + words + " in " + where, "No tasks match " + words + " in " + where + ".");
            // Searching every list: say which list each task is on.
            if (Scope == EveryList)
                foreach (ListBoxItem item in _results.Items)
                    if (item.Tag is Guid id && found.FirstOrDefault(t => t.Id == id) is TaskItem t) item.Content += ", on " + t.List;
        }

        private void Go()
        {
            Refill();
            string words = _box.Text.Trim();
            if (words.Length == 0) { _w.Say("Type words to search for first.", true); return; }
            int n = _results.Items.OfType<ListBoxItem>().Count(i => i.Tag is Guid);
            if (n == 0) { _w.Say("No tasks match " + words + (Scope == EveryList ? "." : " in " + Scope + "."), true); return; }
            _w.Say(n + (n == 1 ? " task found." : " tasks found."), true);
            TextViewer.Select(_results, 0);
        }

        /// <summary>Open tasks with every word somewhere in their name, tag, list, section, notes, subtasks or comments. list null means every list.</summary>
        public static List<TaskItem> Find(Store store, string text, string? list)
        {
            var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            bool Has(TaskItem t, string w) =>
                (t.Name ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || (t.Tag ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || (t.List ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || (t.Section ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || (t.Notes ?? "").Contains(w, StringComparison.CurrentCultureIgnoreCase)
                || t.Subtasks.Any(s => s.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase))
                || t.Comments.Any(c => c.Text.Contains(w, StringComparison.CurrentCultureIgnoreCase));
            var open = store.Data.Tasks.Where(t => t.Status != Status.Done && (list == null || t.List == list) && words.All(w => Has(t, w)));
            return Views.Apply(open, new ViewState(), store.Rank);
        }

        public void SelfTest(Store store)
        {
            var st = new Store();
            st.Data.Lists = Sample.Lists();
            TaskItem T(string n, string list, string notes = "", Status s = Status.ThisWeek) => new() { Name = n, List = list, Notes = notes, Status = s };
            var lab = T("lab 3 report", "Homework");
            var reading = T("chapter 9 reading", "Homework", "for the PSY exam");
            var errand = T("buy lab goggles", "Errands");
            var done = T("lab 2 report", "Homework", s: Status.Done);
            st.Data.Tasks.AddRange(new[] { lab, reading, errand, done });
            string Names(IEnumerable<TaskItem> ts) => string.Join(" | ", ts.Select(t => t.Name).OrderBy(n => n));
            if (Names(Find(st, "LAB", null)) != "buy lab goggles | lab 3 report") throw new Exception("search every list found " + Names(Find(st, "lab", null)));
            if (Names(Find(st, "lab", "Homework")) != "lab 3 report") throw new Exception("search one list found " + Names(Find(st, "lab", "Homework")));
            if (Names(Find(st, "psy  exam", null)) != "chapter 9 reading") throw new Exception("search misses notes");
            if (Find(st, "lab report 2", null).Count != 0) throw new Exception("search found a finished task");
        }
    }
}
