using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>Changes to tags and lists that touch many tasks, kept apart from the screens so they can be tested (spec 7.1, 7.3, 4.7).</summary>
    public static class ListTools
    {
        private static bool Same(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase);

        /// <summary>Renames a tag on every task, template and class time.</summary>
        public static void RenameTag(Data d, string from, string to)
        {
            foreach (var t in d.Tasks.Concat(d.Templates).Where(t => Same(t.Tag, from))) t.Tag = to;
            foreach (var c in d.Classes.Where(c => Same(c.Tag, from))) c.Tag = to;
        }

        /// <summary>Deletes a tag: its tasks move to another tag, or lose the tag when moveTo is empty.</summary>
        public static void RemoveTag(Data d, string tag, string moveTo)
        {
            RenameTag(d, tag, moveTo);
            d.Tags.RemoveAll(x => Same(x.Name, tag));
        }

        /// <summary>Sections typed one per line or separated by commas: trimmed, no blanks, no repeats.</summary>
        public static List<string> ParseSections(string text)
        {
            var result = new List<string>();
            foreach (var part in text.Split(new[] { '\n', '\r', ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.Trim();
                if (s.Length > 0 && !result.Any(r => Same(r, s))) result.Add(s);
            }
            return result;
        }

        /// <summary>Moves every task of one list to another, carrying its sections over, and drops the first list.</summary>
        public static void MergeList(Data d, string from, string into)
        {
            var src = d.Lists.FirstOrDefault(l => l.Name == from);
            var dst = d.Lists.FirstOrDefault(l => l.Name == into);
            if (src == null || dst == null || src == dst) return;
            foreach (var s in src.Sections) if (!dst.Sections.Any(x => Same(x, s))) dst.Sections.Add(s);
            MoveTasks(d, from, into);
            DropList(d, src);
        }

        /// <summary>Deletes a list: its tasks move to another list, or are deleted when moveTo is empty.</summary>
        public static void DeleteList(Data d, string name, string moveTo)
        {
            var src = d.Lists.FirstOrDefault(l => l.Name == name);
            if (src == null) return;
            if (moveTo.Length > 0) MoveTasks(d, name, moveTo);
            else d.Tasks.RemoveAll(t => t.List == name);
            DropList(d, src);
        }

        private static void MoveTasks(Data d, string from, string to)
        {
            var dst = d.Lists.FirstOrDefault(l => l.Name == to);
            foreach (var t in d.Tasks.Where(t => t.List == from))
            {
                t.List = to;
                if (dst != null && t.Priority > dst.Scale.Length) t.Priority = dst.Scale.Length;
                if (t.Section.Length > 0 && dst != null && !dst.Sections.Contains(t.Section)) t.Section = "";
            }
        }

        // Sub-lists of a dropped list become lists of their own.
        private static void DropList(Data d, TaskList l)
        {
            d.Lists.Remove(l);
            foreach (var sub in d.Lists.Where(x => x.Parent == l.Name)) sub.Parent = "";
        }
    }

    /// <summary>The Tags page in Settings (spec 7): add, rename, describe and delete the classes and topics tasks are tagged with.</summary>
    public sealed class TagsFeature : IFeature
    {
        public void Attach(MainWindow w) { }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) => new ISettingsPage[] { new TagsPage(store) };

        public void SelfTest(Store store)
        {
            var d = new Data();
            d.Lists.Add(new TaskList { Name = "A", Scale = new[] { "x", "y" } });
            d.Lists.Add(new TaskList { Name = "B", Scale = new[] { "x" }, Sections = new() { "S1" } });
            d.Lists.Add(new TaskList { Name = "C", Parent = "B" });
            d.Tags.Add(new TagInfo { Name = "PSY" });
            d.Tags.Add(new TagInfo { Name = "BIO" });
            d.Tasks.Add(new TaskItem { Name = "t1", List = "A", Tag = "PSY", Priority = 2, Section = "S1" });
            d.Tasks.Add(new TaskItem { Name = "t2", List = "B", Tag = "BIO" });
            d.Tasks.Add(new TaskItem { Name = "t3", List = "A", Tag = "psy" });
            d.Classes.Add(new ClassTime { Tag = "PSY" });

            ListTools.RenameTag(d, "PSY", "PSYC");
            Check(d.Tasks.Count(t => t.Tag == "PSYC") == 2 && d.Classes[0].Tag == "PSYC", "tag rename reaches every task and class");
            ListTools.RemoveTag(d, "BIO", "PSYC");
            Check(d.Tasks.All(t => t.Tag == "PSYC") && d.Tags.Count == 1, "tag delete moves its tasks");
            ListTools.RemoveTag(d, "PSYC", "");
            Check(d.Tasks.All(t => t.Tag == ""), "tag delete can just remove the tag");

            var s = ListTools.ParseSections("PSY, STAT\n bio ,\n\nPsy");
            Check(s.SequenceEqual(new[] { "PSY", "STAT", "bio" }), "sections parse from lines and commas without repeats");

            ListTools.MergeList(d, "A", "B");
            var t1 = d.Tasks.First(t => t.Name == "t1");
            Check(d.Lists.Count == 2 && t1.List == "B" && t1.Priority == 1 && t1.Section == "S1", "merge moves tasks, clamps priority, keeps a carried section");
            ListTools.DeleteList(d, "B", "");
            Check(d.Tasks.Count == 0 && d.Lists.Single().Parent == "", "delete removes tasks and promotes sub-lists");
        }

        private static void Check(bool ok, string what) { if (!ok) throw new Exception("Tags: " + what); }

        private sealed class TagsPage : ISettingsPage
        {
            private sealed class Pending { public string? Original; public string Name = ""; public string Description = ""; }

            private const string AddName = "Add a new tag";
            private readonly Store _store;
            private readonly List<Pending> _tags = new();
            private readonly List<(Pending Gone, Pending? MoveTo)> _deleted = new();
            private readonly StackPanel _panel = new();
            private readonly ComboBox _pick = new();
            private readonly TextBox _name = new();
            private readonly TextBox _description = new();
            private readonly Button _delete = Layout.Button("_Delete this tag...");
            private Pending? _current;
            private bool _loading;

            public string Name => "Tags";
            public FrameworkElement Content => _panel;

            public TagsPage(Store store)
            {
                _store = store;
                foreach (var t in store.Data.Tags) _tags.Add(new Pending { Original = t.Name, Name = t.Name, Description = t.Description });
                // Tags only used on tasks count too.
                foreach (var tag in store.Data.Tasks.Select(t => t.Tag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (!_tags.Any(p => p.Name.Equals(tag, StringComparison.OrdinalIgnoreCase))) _tags.Add(new Pending { Original = tag, Name = tag });

                AutomationProperties.SetHelpText(_description, "For a class, its full name, like Intro to Psychology.");
                _delete.HorizontalAlignment = HorizontalAlignment.Left;
                _panel.Children.Add(Layout.Row("_Tag", _pick, 420));
                _panel.Children.Add(Layout.Row("Tag _name", _name));
                _panel.Children.Add(Layout.Row("_Description", _description, 420));
                _panel.Children.Add(_delete);

                _pick.SelectionChanged += (_, _) => { if (!_loading) Choose(); };
                _name.TextChanged += (_, _) => { if (!_loading && _current != null) _current.Name = _name.Text.Trim(); };
                // The menu shows the new name once you move on, not on every letter.
                _name.LostKeyboardFocus += (_, _) => { if (_current != null) Fill(_tags.IndexOf(_current)); };
                _description.TextChanged += (_, _) => { if (!_loading && _current != null) _current.Description = _description.Text.Trim(); };
                _delete.Click += (_, _) => Delete();
                Fill(_tags.Count > 0 ? 0 : _tags.Count);
                Choose();
            }

            private void Fill(int select)
            {
                _loading = true;
                _pick.ItemsSource = _tags.Select(t => t.Name.Length > 0 ? t.Name : "(no name)").Append(AddName).ToList();
                _pick.SelectedIndex = select;
                _loading = false;
            }

            private void Choose()
            {
                int i = _pick.SelectedIndex;
                if (i < 0) return;
                if (i >= _tags.Count)
                {
                    int n = 1;
                    while (_tags.Any(t => t.Name.Equals("TAG" + n, StringComparison.OrdinalIgnoreCase))) n++;
                    _tags.Add(new Pending { Name = "TAG" + n });
                    Fill(_tags.Count - 1);
                    Load(_tags[^1]);
                    Speech.Say(_panel, "Added " + _tags[^1].Name + ". Type its name and description. It is made when you press OK.", true);
                    _panel.Dispatcher.BeginInvoke(() => { _name.Focus(); _name.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
                    return;
                }
                Load(_tags[i]);
            }

            private void Load(Pending p)
            {
                _loading = true;
                _current = p;
                _name.Text = p.Name;
                _description.Text = p.Description;
                _loading = false;
            }

            private int InUse(Pending p) => p.Original == null ? 0 : _store.Data.Tasks.Count(t => t.Tag.Equals(p.Original, StringComparison.OrdinalIgnoreCase));

            private void Delete()
            {
                if (_current == null) return;
                var gone = _current;
                int used = InUse(gone);
                Pending? moveTo = null;
                var others = _tags.Where(t => t != gone && t.Name.Length > 0).ToList();
                if (used > 0)
                {
                    var owner = Window.GetWindow(_panel);
                    var d = Layout.Dialog(owner, "Delete tag " + gone.Name);
                    var choice = new ComboBox { ItemsSource = new[] { "Remove the tag from them" }.Concat(others.Select(o => "Move them to " + o.Name)).ToList(), SelectedIndex = 0 };
                    var panel = new StackPanel { Margin = new Thickness(14), Width = 420 };
                    panel.Children.Add(new TextBlock { Text = gone.Name + " is on " + used + (used == 1 ? " task." : " tasks.") + " What should happen to them?", TextWrapping = TextWrapping.Wrap });
                    panel.Children.Add(Layout.Row("What to _do", choice, 400));
                    var ok = Layout.Button("_Delete tag", isDefault: true);
                    var cancel = Layout.Button("Cancel", isCancel: true);
                    var row = new StackPanel { Orientation = Orientation.Horizontal };
                    row.Children.Add(ok);
                    row.Children.Add(cancel);
                    panel.Children.Add(row);
                    d.Content = panel;
                    ok.Click += (_, _) => d.DialogResult = true;
                    d.Loaded += (_, _) => choice.Focus();
                    if (d.ShowDialog() != true) return;
                    if (choice.SelectedIndex > 0) moveTo = others[choice.SelectedIndex - 1];
                }
                int at = _tags.IndexOf(gone);
                _tags.Remove(gone);
                if (gone.Original != null) _deleted.Add((gone, moveTo));
                // Anything that was to move onto the deleted tag follows it on to where it went.
                for (int i = 0; i < _deleted.Count; i++) if (_deleted[i].MoveTo == gone) _deleted[i] = (_deleted[i].Gone, moveTo);
                Fill(Math.Min(at, _tags.Count));
                Choose();
                Speech.Say(_panel, "Deleted " + gone.Name + ". It goes when you press OK.", true);
                _pick.Focus();
            }

            public string? Validate()
            {
                for (int i = 0; i < _tags.Count; i++)
                {
                    string? problem = _tags[i].Name.Length == 0 ? "A tag has no name. Type one, or delete it."
                        : _tags.Take(i).Any(o => o.Name.Equals(_tags[i].Name, StringComparison.OrdinalIgnoreCase)) ? "Two tags are called " + _tags[i].Name + ". Give one another name."
                        : null;
                    if (problem == null) continue;
                    _pick.SelectedIndex = i;
                    Load(_tags[i]);
                    _name.Focus();
                    return problem;
                }
                return null;
            }

            public void Apply()
            {
                var d = _store.Data;
                foreach (var (gone, moveTo) in _deleted)
                    ListTools.RenameTag(d, gone.Original!, moveTo == null ? "" : moveTo.Original ?? moveTo.Name);
                foreach (var p in _tags)
                    if (p.Original != null && p.Original != p.Name) ListTools.RenameTag(d, p.Original, p.Name);
                d.Tags = _tags.Select(p => new TagInfo { Name = p.Name, Description = p.Description }).ToList();
            }
        }
    }
}
