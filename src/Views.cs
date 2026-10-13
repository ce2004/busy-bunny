using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    public enum SortBy { Default, Priority, DueDate, Name, Newest }
    public enum GroupBy { None, Tag, Status }

    /// <summary>The filter, sort and group Morgan picked (spec 9.5). Kept while the app runs.</summary>
    public sealed class ViewState
    {
        public string Tag = "";
        public string List = "";
        public TaskType? Type;
        public bool EasyOnly, BrainHeavyOnly;
        public SortBy Sort;
        public GroupBy Group;

        public bool Active => Tag.Length > 0 || List.Length > 0 || Type != null || EasyOnly || BrainHeavyOnly || Sort != SortBy.Default || Group != GroupBy.None;

        public void Clear() { Tag = ""; List = ""; Type = null; EasyOnly = BrainHeavyOnly = false; Sort = SortBy.Default; Group = GroupBy.None; }

        public string Describe()
        {
            if (!Active) return "No filter, sort or grouping applied.";
            var parts = new List<string>();
            if (Tag.Length > 0) parts.Add("tag " + Tag);
            if (List.Length > 0) parts.Add("list " + List);
            if (Type != null) parts.Add("type " + Views.TypeWords(Type.Value));
            if (EasyOnly) parts.Add("easy energy");
            if (BrainHeavyOnly) parts.Add("brain heavy energy");
            string s = parts.Count > 0 ? "Showing only " + string.Join(", ", parts) + "." : "";
            if (Sort != SortBy.Default) s += " Sorted by " + Views.SortWords(Sort) + ".";
            if (Group != GroupBy.None) s += " Grouped by " + Group.ToString().ToLowerInvariant() + ".";
            return s.Trim();
        }
    }

    /// <summary>
    /// The Board tab (spec 9.3): one named list per status, built for a screen reader instead of
    /// columns. Also Filter and sort (spec 9.5), which applies to the Board and the list tabs.
    /// </summary>
    public sealed class Views : IFeature
    {
        public static readonly ViewState State = new();
        private static readonly Status[] BoardStatuses = { Status.Today, Status.ThisWeek, Status.NextWeek, Status.DoneNotSubmitted, Status.Waiting };

        private MainWindow _w = null!;
        private readonly Dictionary<Status, ListBox> _board = new();

        public static string TypeWords(TaskType t) => t == TaskType.PersonalGoal ? "personal goal" : t.ToString().ToLowerInvariant();
        public static string SortWords(SortBy s) => s switch { SortBy.DueDate => "due date", SortBy.Newest => "newest first", _ => s.ToString().ToLowerInvariant() };

        public void Attach(MainWindow w)
        {
            _w = w;
            var panel = new StackPanel { Margin = new Thickness(6) };
            foreach (var s in BoardStatuses)
            {
                var lb = w.NewTaskList("board-" + s);
                _board[s] = lb;
                panel.Children.Add(new Label { Content = Words.Of(s), Target = lb, Padding = new Thickness(0, 8, 0, 2) });
                panel.Children.Add(lb);
            }
            w.AddTab("Board", new ScrollViewer { Content = panel, Focusable = false, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, () => _board[Status.Today]);

            w.AddMenuItem("View", "_Filter and sort...", Open, "Ctrl+Shift+F");
            w.AddMenuItem("View", "_Clear filter", ClearFilter);
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.F, Open, "Ctrl+Shift+F: Filter and sort the list tabs and the Board");
            w.Refreshed += Refill;
            w.DetailNotes.Add(StepsLine);
        }

        private void ClearFilter()
        {
            State.Clear();
            _w.Refresh();
            _w.Say("Filter, sort and grouping cleared.", true);
        }

        public static string? StepsLine(TaskItem t) =>
            t.Subtasks.Count == 0 ? null : t.Subtasks.Count(s => s.Done) + " of " + t.Subtasks.Count + (t.Subtasks.Count == 1 ? " step done." : " steps done.");

        // ---- Filtering and ordering (pure, so the self-test can check it) ----

        public static List<TaskItem> Apply(IEnumerable<TaskItem> tasks, ViewState v, Func<TaskItem, int>? rank = null)
        {
            var q = tasks.Where(t => (v.Tag.Length == 0 || t.Tag.Equals(v.Tag, StringComparison.OrdinalIgnoreCase))
                && (v.List.Length == 0 || t.List == v.List)
                && (v.Type == null || t.Type == v.Type)
                && (!v.EasyOnly || t.Energy == Energy.Easy)
                && (!v.BrainHeavyOnly || t.Energy == Energy.BrainHeavy));
            IOrderedEnumerable<TaskItem> o = v.Sort switch
            {
                SortBy.Priority => q.OrderBy(t => t.Priority == 0 ? 99 : t.Priority).ThenBy(t => t.Due ?? DateTime.MaxValue),
                SortBy.DueDate => q.OrderBy(t => t.Due ?? DateTime.MaxValue).ThenBy(t => t.Priority == 0 ? 99 : t.Priority),
                SortBy.Name => q.OrderBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase),
                SortBy.Newest => q.OrderByDescending(t => t.CreatedAt),
                _ => q.OrderBy(t => rank?.Invoke(t) ?? 0).ThenBy(t => t.Status).ThenBy(t => t.Priority == 0 ? 9 : t.Priority).ThenBy(t => t.Due ?? DateTime.MaxValue),
            };
            // A stable sort of the already ordered tasks keeps the chosen sort inside each group.
            var list = o.ToList();
            if (v.Group == GroupBy.Tag) list = list.OrderBy(t => t.Tag.Length == 0 ? "{" : t.Tag.ToUpperInvariant(), StringComparer.Ordinal).ToList();
            else if (v.Group == GroupBy.Status) list = list.OrderBy(t => t.Status).ToList();
            return list;
        }

        private static string GroupOf(TaskItem t, GroupBy g) => g == GroupBy.Tag ? (t.Tag.Length == 0 ? "No tag" : t.Tag) : Words.Of(t.Status);

        private void Fill(ListBox lb, IEnumerable<TaskItem> source, string name, string empty)
        {
            var tasks = Apply(source, State, _w.Store.Rank);
            _w.FillTasks(lb, tasks, name + (State.Active ? ", filtered" : ""), empty);
            if (State.Group == GroupBy.None || tasks.Count == 0) return;
            // Group headings are plain lines between the tasks.
            for (int i = tasks.Count - 1; i >= 0; i--)
                if (i == 0 || GroupOf(tasks[i], State.Group) != GroupOf(tasks[i - 1], State.Group))
                {
                    string g = GroupOf(tasks[i], State.Group);
                    int n = tasks.Count(t => GroupOf(t, State.Group) == g);
                    lb.Items.Insert(i, new ListBoxItem { Content = g + ", " + n + (n == 1 ? " task" : " tasks") });
                }
        }

        private void Refill()
        {
            var open = _w.Store.Data.Tasks.Where(t => t.Status != Status.Done).ToList();
            foreach (var s in BoardStatuses)
                Fill(_board[s], open.Where(t => t.Status == s), Words.Of(s), "Nothing " + (s == Status.Today ? "for today" : "here") + ".");
            if (!State.Active) return;
            foreach (var l in _w.Store.ActiveLists)
                if (_w.ListBoxOf(l.Name) is ListBox lb)
                    Fill(lb, open.Where(t => t.List == l.Name), l.Name, "Nothing on " + l.Name + " that fits.");
        }

        // ---- The dialog ----

        private static ComboBox Pick(IEnumerable<string> items, int selected)
        {
            var c = new ComboBox { ItemsSource = items.ToList() };
            c.SelectedIndex = Math.Max(0, selected);
            return c;
        }

        private void Open()
        {
            var store = _w.Store;
            var tags = new[] { "Any tag" }.Concat(store.Data.Tags.Select(t => t.Name).OrderBy(t => t)).ToList();
            var lists = new[] { "Any list" }.Concat(store.ActiveLists.Select(l => l.Name)).ToList();
            var types = new[] { "Any type" }.Concat(Enum.GetValues<TaskType>().Select(t => char.ToUpper(TypeWords(t)[0]) + TypeWords(t)[1..])).ToList();
            var energies = new[] { "Any energy", "Easy", "Brain heavy" };
            var sorts = new[] { "Usual order", "Priority", "Due date", "Name", "Newest first" };
            var groups = new[] { "No grouping", "Tag", "Status" };

            var tag = Pick(tags, Math.Max(0, tags.FindIndex(t => t.Equals(State.Tag, StringComparison.OrdinalIgnoreCase))));
            var list = Pick(lists, Math.Max(0, lists.IndexOf(State.List)));
            var type = Pick(types, State.Type == null ? 0 : (int)State.Type + 1);
            var energy = Pick(energies, State.EasyOnly ? 1 : State.BrainHeavyOnly ? 2 : 0);
            var sort = Pick(sorts, (int)State.Sort);
            var group = Pick(groups, (int)State.Group);

            var w = Layout.Dialog(_w, "Filter and sort");
            var panel = new StackPanel { Margin = new Thickness(12) };
            var applied = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Width = 320, Text = "Now: " + State.Describe() };
            System.Windows.Automation.AutomationProperties.SetName(applied, "What is applied now");
            panel.Children.Add(applied);
            panel.Children.Add(Layout.Row("Filter by _tag", tag));
            panel.Children.Add(Layout.Row("Filter by _list", list));
            panel.Children.Add(Layout.Row("Filter by t_ype", type));
            panel.Children.Add(Layout.Row("Filter by _energy", energy));
            panel.Children.Add(Layout.Row("_Sort by", sort));
            panel.Children.Add(Layout.Row("_Group by", group));
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            ok.Click += (_, _) => w.DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) => tag.Focus();
            if (w.ShowDialog() != true) return;

            State.Tag = tag.SelectedIndex > 0 ? tags[tag.SelectedIndex] : "";
            State.List = list.SelectedIndex > 0 ? lists[list.SelectedIndex] : "";
            State.Type = type.SelectedIndex > 0 ? Enum.GetValues<TaskType>()[type.SelectedIndex - 1] : null;
            State.EasyOnly = energy.SelectedIndex == 1;
            State.BrainHeavyOnly = energy.SelectedIndex == 2;
            State.Sort = (SortBy)Math.Max(0, sort.SelectedIndex);
            State.Group = (GroupBy)Math.Max(0, group.SelectedIndex);
            _w.Refresh();
            _w.Say(State.Describe() + (State.Active ? " View, Clear filter, puts it back." : ""), true);
        }

        public void SelfTest(Store store)
        {
            TaskItem T(string n, string tag, int pr, int due, Energy e = Energy.Unset, TaskType ty = TaskType.General) =>
                new() { Name = n, Tag = tag, Priority = pr, Due = due == 0 ? null : new DateTime(2026, 10, due), Energy = e, Type = ty, Status = pr == 1 ? Status.Today : Status.ThisWeek };
            var all = new List<TaskItem> { T("b", "PSY", 2, 20), T("a", "ENG", 1, 0, Energy.Easy), T("c", "PSY", 3, 15, Energy.BrainHeavy, TaskType.Test) };
            var v = new ViewState { Tag = "psy" };
            if (Apply(all, v).Count != 2) throw new Exception("tag filter");
            v = new ViewState { EasyOnly = true };
            if (Apply(all, v).Single().Name != "a") throw new Exception("energy filter");
            v = new ViewState { Type = TaskType.Test };
            if (Apply(all, v).Single().Name != "c") throw new Exception("type filter");
            v = new ViewState { Sort = SortBy.DueDate };
            if (Apply(all, v).Select(t => t.Name).Aggregate("", (a, b) => a + b) != "cba") throw new Exception("due sort");
            v = new ViewState { Sort = SortBy.Name, Group = GroupBy.Tag };
            if (Apply(all, v).Select(t => t.Name).Aggregate("", (a, b) => a + b) != "abc") throw new Exception("group by tag");
            if (StepsLine(new TaskItem { Subtasks = { new Subtask { Done = true }, new Subtask(), new Subtask() } }) != "1 of 3 steps done.") throw new Exception("steps line");
            if (new ViewState().Active) throw new Exception("empty state should be inactive");
        }
    }
}
