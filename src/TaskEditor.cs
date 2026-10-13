using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// Quick add: a name, then pick menus for the rest. Enter adds it. Given a task, the same form
    /// edits it instead (Edit task, F2): everything filled in, and Save keeps the changes. The task
    /// type (spec 8.2) decides which fields show. Given a template (from), it fills in a new task.
    /// </summary>
    public static class QuickAdd
    {
        static readonly string[] TypeWords = { "General task", "Assignment", "Test", "Meeting", "Event", "Personal goal" };
        static readonly string[] EnergyWords = { "Not set", "Easy", "Brain-heavy" };
        /// <summary>What Quick add took from the end of the name, to say after Added, or empty.</summary>
        public static string Taken { get; private set; } = "";

        static readonly string[] ScheduleChoices = { "Does not repeat", "Every day", "Every weekday", "Every week", "Every 2 weeks", "Every month", "Every year", "Something else, in my own words" };
        static readonly DayOfWeek[] WeekOrder = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

        /// <summary>The Repeat pick menu, the day checkboxes (weekly only), the own-words box (something else only) and the rename checkbox (any repeat).</summary>
        static FrameworkElement ScheduleRows(ComboBox repeat, WrapPanel days, TextBox custom, CheckBox rename)
        {
            var panel = new StackPanel();
            panel.Children.Add(Layout.Row("_Repeat", repeat));
            var daysLabel = new Label { Content = "Repeat on these days", Padding = new Thickness(0, 2, 0, 2) };
            panel.Children.Add(daysLabel);
            panel.Children.Add(days);
            var customRow = Layout.Row("Repeat in my o_wn words", custom);
            panel.Children.Add(customRow);
            panel.Children.Add(rename);
            void Show()
            {
                int i = repeat.SelectedIndex;
                var weekly = i is 3 or 4 ? Visibility.Visible : Visibility.Collapsed;
                days.Visibility = daysLabel.Visibility = weekly;
                customRow.Visibility = i == ScheduleChoices.Length - 1 ? Visibility.Visible : Visibility.Collapsed;
                rename.Visibility = i > 0 ? Visibility.Visible : Visibility.Collapsed;
            }
            repeat.SelectionChanged += (_, _) => Show();
            Show();
            return panel;
        }

        static void LoadSchedule(string rule, ComboBox repeat, List<CheckBox> days, TextBox custom)
        {
            var r = RepeatRule.Parse(rule);
            int pick = r switch
            {
                null => 0,
                { Per: RepeatRule.Unit.Day, Every: 1 } => 1,
                { Per: RepeatRule.Unit.Week, Every: 1 } when r.Describe() == "every weekday" => 2,
                { Per: RepeatRule.Unit.Week, Every: 1 } => 3,
                { Per: RepeatRule.Unit.Week, Every: 2 } => 4,
                { Per: RepeatRule.Unit.Month, Every: 1 } => 5,
                { Per: RepeatRule.Unit.Year, Every: 1 } => 6,
                _ => ScheduleChoices.Length - 1,
            };
            if (r != null && pick is 3 or 4)
                for (int k = 0; k < days.Count; k++) days[k].IsChecked = r.Days.Contains(WeekOrder[k]);
            if (pick == ScheduleChoices.Length - 1) custom.Text = rule;
            repeat.SelectedIndex = pick;
        }

        /// <summary>The picker's choice in the plain words RepeatRule reads, or empty for no repeat.</summary>
        static string ScheduleText(ComboBox repeat, List<CheckBox> days, TextBox custom)
        {
            var on = WeekOrder.Where((d, k) => days[k].IsChecked == true).Select(d => d.ToString()).ToList();
            return repeat.SelectedIndex switch
            {
                1 => "every day",
                2 => "every weekday",
                3 => on.Count > 0 ? "every " + string.Join(" and ", on) : "every week",
                4 => on.Count > 0 ? "every 2 weeks on " + string.Join(" and ", on) : "every 2 weeks",
                5 => "every month",
                6 => "every year",
                7 => custom.Text.Trim(),
                _ => "",
            };
        }

        static readonly string[] RepeatChoices = { "", "every day", "every weekday", "every Monday", "every Monday and Wednesday", "every week", "every 2 weeks", "every month" };

        /// <summary>Which optional fields each type shows. Name, type, list, section, tag, status, priority, notes, subtasks and comments always show.</summary>
        static readonly Dictionary<TaskType, string[]> Fields = new()
        {
            [TaskType.General] = new[] { "due", "dodate", "size", "energy", "repeat", "remind" },
            [TaskType.Assignment] = new[] { "due", "dodate", "size", "energy", "weight", "repeat", "remind" },
            [TaskType.Test] = new[] { "due", "dodate", "size", "energy", "weight", "remind" },
            [TaskType.Meeting] = new[] { "due", "repeat", "remind" },
            [TaskType.Event] = new[] { "due", "repeat", "remind" },
            [TaskType.PersonalGoal] = new[] { "due", "dodate", "size", "energy", "repeat", "remind" },
        };

        /// <summary>Suggested priority for a heavy grade weight (spec 22.2): 25 percent or more is the top level, 10 or more the second. 0 means no suggestion.</summary>
        public static int SuggestPriority(int weight, int scaleLength)
        {
            if (scaleLength == 0) return 0;
            int level = weight >= 25 ? 1 : weight >= 10 ? 2 : 0;
            return Math.Min(level, scaleLength);
        }

        static string Show(DateTime? d, bool time) =>
            d is DateTime x ? x.ToString("M/d/yyyy", CultureInfo.InvariantCulture) + (time ? " at " + x.ToString("h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant() : "") : "";

        /// <summary>Copies every field the form edits from one task onto another (Edit task uses this).</summary>
        public static void Apply(TaskItem from, TaskItem to)
        {
            if (from.Status != to.Status) to.DoneAt = from.Status == Status.Done ? DateTime.Now : null;
            to.Name = from.Name; to.List = from.List; to.Tag = from.Tag; to.Status = from.Status; to.Priority = from.Priority;
            to.Due = from.Due; to.DueHasTime = from.DueHasTime; to.Size = from.Size; to.IsMeeting = from.IsMeeting;
            to.Type = from.Type; to.DoDate = from.DoDate; to.Repeat = from.Repeat; to.RemindAt = from.RemindAt; to.RemindRepeat = from.RemindRepeat;
            to.Energy = from.Energy; to.GradeWeight = from.GradeWeight; to.Section = from.Section; to.Notes = from.Notes;
            to.Subtasks = from.Subtasks; to.Comments = from.Comments;
        }

        public static TaskItem? Show(Window owner, Store store, string list, Status status, TaskItem? editing = null, TaskItem? from = null)
        {
            var src = editing ?? from;
            if (src != null) { list = store.Data.Lists.Any(l => l.Name == src.List) ? src.List : list; status = src.Status; }
            var w = Layout.Dialog(owner, editing == null ? "Quick add" : "Edit task");
            var name = new TextBox { Text = src?.Name ?? "" };
            var type = new ComboBox { ItemsSource = TypeWords, SelectedIndex = src != null ? (int)src.Type : list == "Meetings" ? (int)TaskType.Meeting : 0 };
            var lists = new ComboBox { ItemsSource = store.Data.Lists.Select(l => l.Name).ToList(), SelectedItem = list };
            var section = new ComboBox();
            var tagNames = store.Data.Tags.Select(t => t.Name).Concat(store.Data.Tasks.Select(t => t.Tag)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t).ToList();
            var tags = new ComboBox { IsEditable = true, ItemsSource = tagNames };
            AutomationProperties.SetHelpText(tags, "Pick a tag, or type a new one and it is added to your tags.");
            var statuses = new ComboBox { ItemsSource = Enum.GetValues<Status>().Where(s => editing != null || s != Status.Done).Select(Words.Of).ToList(), SelectedIndex = (int)status };
            var priority = new ComboBox();
            var due = new TextBox();
            AutomationProperties.SetHelpText(due, "Like today, tomorrow, Friday, 10/20, or Friday at 3. Leave it empty for no date.");
            var doDate = new TextBox();
            AutomationProperties.SetHelpText(doDate, "The day you plan to do it, apart from when it is due. It shows in Today on that day. Leave it empty for none.");
            var size = new ComboBox { ItemsSource = Enum.GetValues<Size>().Select(Words.Of).ToList(), SelectedIndex = 1 };
            var energy = new ComboBox { ItemsSource = EnergyWords, SelectedIndex = 0 };
            var weight = new TextBox();
            AutomationProperties.SetHelpText(weight, "How many percent of the grade it counts, like 20. Heavy ones suggest a higher priority. Leave it empty for none.");
            // The schedule picker (Morgan's issue 5): how often, then which days for a weekly one, or
            // her own words for anything else, and whether to ask for a new name each time.
            var repeat = new ComboBox { ItemsSource = ScheduleChoices, SelectedIndex = 0 };
            AutomationProperties.SetHelpText(repeat, "How often it repeats. For every week or every 2 weeks, check the days below.");
            var dayBoxes = WeekOrder.Select(d => new CheckBox { Content = d.ToString(), Margin = new Thickness(0, 2, 12, 2) }).ToList();
            var daysPanel = new WrapPanel { Width = 320 };
            foreach (var b in dayBoxes) daysPanel.Children.Add(b);
            AutomationProperties.SetName(daysPanel, "Repeat on");
            var customRepeat = new TextBox();
            AutomationProperties.SetHelpText(customRepeat, "In plain words, like every 3 days, every Monday and Thursday, or every other week on Tuesday.");
            var rename = new CheckBox { Content = "As_k for a new name each time it repeats, like chapter 9 then chapter 10", Margin = new Thickness(0, 4, 0, 6) };
            var remind = new TextBox();
            AutomationProperties.SetHelpText(remind, "A date and time, like tomorrow at 3 or 10/20 at 8:30 am. With no time it is 9 am. Leave it empty for none.");
            var remindRepeat = new ComboBox { IsEditable = true, ItemsSource = RepeatChoices };
            AutomationProperties.SetHelpText(remindRepeat, "How the reminder repeats, in the same words as Repeat. Empty means once.");
            var notes = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 70, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };

            // Subtasks and comments are edited on copies; they only count when the form is saved.
            var subs = (src?.Subtasks ?? new()).Select(s => new Subtask { Name = s.Name, Done = s.Done }).ToList();
            var comments = (src?.Comments ?? new()).Select(c => new Comment { When = c.When, Text = c.Text }).ToList();
            var subList = new ListBox { Height = 90 };
            var newSub = new TextBox();
            var commentList = new ListBox { Height = 90 };
            var newComment = new TextBox();
            foreach (var lb in new[] { subList, commentList }) VirtualizingPanel.SetIsVirtualizing(lb, false);

            void FillList(ListBox lb, IEnumerable<string> lines, int keep)
            {
                lb.Items.Clear();
                foreach (var l in lines)
                {
                    var item = new ListBoxItem { Content = new TextBlock { Text = l, TextWrapping = TextWrapping.Wrap, MaxWidth = 300 } };
                    AutomationProperties.SetName(item, l);
                    lb.Items.Add(item);
                }
                if (lb.Items.Count > 0) lb.SelectedIndex = Math.Clamp(keep, 0, lb.Items.Count - 1);
            }
            void FillSubs(int keep) => FillList(subList, subs.Select(s => s.Name + (s.Done ? ", done" : "")), keep);
            void FillComments() => FillList(commentList, comments.Select(c => c.When.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture).ToLowerInvariant() + ": " + c.Text), comments.Count - 1);
            void AddSub() { string s = newSub.Text.Trim(); if (s.Length == 0) return; subs.Add(new Subtask { Name = s }); newSub.Clear(); FillSubs(subs.Count - 1); Speech.Say(w, "Added subtask " + s + ".", true); }
            void AddComment() { string s = newComment.Text.Trim(); if (s.Length == 0) return; comments.Add(new Comment { When = DateTime.Now, Text = s }); newComment.Clear(); FillComments(); Speech.Say(w, "Comment added.", true); }
            FillSubs(0);
            FillComments();

            void FillSections()
            {
                var l = store.Data.Lists.FirstOrDefault(x => x.Name == lists.SelectedItem as string);
                var items = new List<string> { "None" };
                if (l != null) items.AddRange(l.Sections);
                if (src != null && src.Section.Length > 0 && !items.Contains(src.Section)) items.Add(src.Section);
                section.ItemsSource = items;
                section.SelectedIndex = src != null && items.IndexOf(src.Section) > 0 ? items.IndexOf(src.Section) : 0;
            }
            void FillPriority()
            {
                var scale = store.Data.Lists.FirstOrDefault(l => l.Name == lists.SelectedItem as string)?.Scale ?? Array.Empty<string>();
                priority.ItemsSource = new[] { "None" }.Concat(scale.Select((s, i) => s + " " + (i + 1))).ToList();
                priority.SelectedIndex = 0;
            }
            lists.SelectionChanged += (_, _) => { FillPriority(); FillSections(); };
            FillPriority();
            FillSections();
            if (src != null)
            {
                tags.Text = src.Tag;
                if (src.Priority < priority.Items.Count) priority.SelectedIndex = src.Priority;
                size.SelectedIndex = (int)src.Size;
                energy.SelectedIndex = (int)src.Energy;
                // With the year, so a date already past is not read as next year's.
                due.Text = Show(src.Due, src.DueHasTime);
                doDate.Text = Show(src.DoDate, false);
                remind.Text = Show(src.RemindAt, true);
                LoadSchedule(src.Repeat, repeat, dayBoxes, customRepeat);
                rename.IsChecked = src.RenameOnRepeat;
                remindRepeat.Text = src.RemindRepeat;
                weight.Text = src.GradeWeight > 0 ? src.GradeWeight.ToString() : "";
                notes.Text = src.Notes;
            }

            // A heavy grade weight suggests a higher priority: said aloud and set in the menu, which she can change back.
            string lastWeight = weight.Text;
            weight.LostKeyboardFocus += (_, _) =>
            {
                if (weight.Text == lastWeight) return;
                lastWeight = weight.Text;
                if (!int.TryParse(weight.Text.Trim().TrimEnd('%'), out int wt)) return;
                int level = SuggestPriority(wt, priority.Items.Count - 1);
                if (level > 0 && (priority.SelectedIndex == 0 || priority.SelectedIndex > level))
                {
                    priority.SelectedIndex = level;
                    Speech.Say(w, "Counts " + wt + " percent, so priority is now " + priority.SelectedItem + ". Change Priority if you disagree.");
                }
            };

            var rows = new Dictionary<string, FrameworkElement>
            {
                ["due"] = Layout.Row("_Due", due),
                ["dodate"] = Layout.Row("D_o date", doDate),
                ["size"] = Layout.Row("Si_ze", size),
                ["energy"] = Layout.Row("_Energy", energy),
                ["weight"] = Layout.Row("_Grade weight in percent", weight),
                ["repeat"] = ScheduleRows(repeat, daysPanel, customRepeat, rename),
                ["remind"] = Layout.Row("Re_minder date and time", remind),
                ["remindrepeat"] = Layout.Row("Reminder rep_eat", remindRepeat),
            };
            void ApplyType()
            {
                var shown = Fields[(TaskType)Math.Max(0, type.SelectedIndex)];
                foreach (var (k, row) in rows)
                    row.Visibility = shown.Contains(k == "remindrepeat" ? "remind" : k) ? Visibility.Visible : Visibility.Collapsed;
            }
            type.SelectionChanged += (_, _) => ApplyType();
            ApplyType();

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("Task _name", name));
            panel.Children.Add(Layout.Row("Task t_ype", type));
            panel.Children.Add(Layout.Row("_List", lists));
            panel.Children.Add(Layout.Row("Sec_tion", section));
            panel.Children.Add(Layout.Row("_Tag", tags));
            panel.Children.Add(Layout.Row("_Status", statuses));
            panel.Children.Add(Layout.Row("_Priority", priority));
            foreach (var k in new[] { "due", "dodate", "size", "energy", "weight", "repeat", "remind", "remindrepeat" }) panel.Children.Add(rows[k]);
            panel.Children.Add(Layout.Row("Not_es", notes));

            Button B(string text, Action act) { var b = Layout.Button(text); b.Click += (_, _) => act(); return b; }
            panel.Children.Add(Layout.Row("S_ubtasks", subList));
            panel.Children.Add(Layout.Row("New su_btask", newSub));
            var subButtons = new StackPanel { Orientation = Orientation.Horizontal };
            subButtons.Children.Add(B("Add s_ubtask", AddSub));
            subButtons.Children.Add(B("Rena_me subtask", () =>
            {
                int i = subList.SelectedIndex;
                if (i < 0) { Speech.Say(w, "No subtask to rename.", true); return; }
                var s = Prompt.Ask(w, "Rename subtask", "New _name", subs[i].Name);
                if (s != null && s.Trim().Length > 0) { subs[i].Name = s.Trim(); FillSubs(i); Speech.Say(w, "Renamed.", true); }
                subList.Focus();
            }));
            subButtons.Children.Add(B("Chec_k off or uncheck", () =>
            {
                int i = subList.SelectedIndex;
                if (i < 0) { Speech.Say(w, "No subtask to check off.", true); return; }
                subs[i].Done = !subs[i].Done;
                FillSubs(i);
                Speech.Say(w, subs[i].Name + (subs[i].Done ? " is done." : " is not done."), true);
                if (subList.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem it) it.Focus();
            }));
            subButtons.Children.Add(B("Delete s_ubtask", () =>
            {
                int i = subList.SelectedIndex;
                if (i < 0) { Speech.Say(w, "No subtask to delete.", true); return; }
                string gone = subs[i].Name;
                subs.RemoveAt(i);
                FillSubs(i);
                Speech.Say(w, "Deleted subtask " + gone + ".", true);
            }));
            panel.Children.Add(subButtons);
            newSub.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && newSub.Text.Trim().Length > 0) { e.Handled = true; AddSub(); } };

            panel.Children.Add(Layout.Row("Co_mments", commentList));
            panel.Children.Add(Layout.Row("New comme_nt", newComment));
            var commentButtons = new StackPanel { Orientation = Orientation.Horizontal };
            commentButtons.Children.Add(B("Add c_omment", AddComment));
            panel.Children.Add(commentButtons);
            newComment.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && newComment.Text.Trim().Length > 0) { e.Handled = true; AddComment(); } };

            var ok = Layout.Button(editing == null ? "Add" : "Save", isDefault: true);
            // Control Enter saves from anywhere in the form, Notes and the subtask boxes included.
            AutomationProperties.SetAcceleratorKey(ok, "Control+Enter");
            w.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return;
                e.Handled = true;
                ok.Focus(); // so a field being typed in hands over its text first
                ok.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            };
            var cancel = Layout.Button("Cancel", isCancel: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80, Focusable = false };

            TaskItem? made = null;
            ok.Click += (_, _) =>
            {
                if (name.Text.Trim().Length == 0) { Speech.Say(w, "Type a name for the task first.", true); name.Focus(); return; }
                var (when, hasTime) = Dates.Parse(due.Text, DateTime.Now);
                if (due.Text.Trim().Length > 0 && when == null) { Speech.Say(w, "Busy Bunny did not understand that due date. Try tomorrow, Friday, or 10/20.", true); due.Focus(); return; }
                var (doWhen, _) = Dates.Parse(doDate.Text, DateTime.Now);
                if (doDate.Text.Trim().Length > 0 && doWhen == null) { Speech.Say(w, "Busy Bunny did not understand that do date. Try tomorrow, Friday, or 10/20.", true); doDate.Focus(); return; }
                var (remindWhen, remindTime) = Dates.Parse(remind.Text, DateTime.Now);
                if (remind.Text.Trim().Length > 0 && remindWhen == null) { Speech.Say(w, "Busy Bunny did not understand that reminder. Try tomorrow at 3, or 10/20 at 8:30 am.", true); remind.Focus(); return; }
                if (remindWhen != null && !remindTime) remindWhen = remindWhen.Value.AddHours(9);
                string repeatText = ScheduleText(repeat, dayBoxes, customRepeat);
                // Quick add: a date or repeat typed at the end of the name, like call grandma tomorrow at 3,
                // fills in Due and Repeat when those are empty, and comes out of the name.
                string taskName = name.Text.Trim();
                Taken = "";
                if (editing == null && due.Text.Trim().Length == 0 && repeatText.Length == 0)
                {
                    var (rest, fromName, fromNameTime, fromNameRepeat) = Language.TakeFromName(taskName, DateTime.Now);
                    if (fromName != null || fromNameRepeat.Length > 0)
                    {
                        taskName = rest;
                        if (fromName != null) { when = fromName; hasTime = fromNameTime; Taken += " Due " + Words.Day(fromName.Value, DateTime.Now) + (fromNameTime ? " at " + Words.Time(fromName.Value) : "") + "."; }
                        if (fromNameRepeat.Length > 0) { repeatText = fromNameRepeat; Taken += " Repeats " + fromNameRepeat + "."; }
                    }
                }
                if (!RepeatRule.IsValid(repeatText)) { Speech.Say(w, "Busy Bunny did not understand that repeat. Try every 3 days, every Monday and Thursday, or every other week on Tuesday.", true); customRepeat.Focus(); return; }
                if (!RepeatRule.IsValid(remindRepeat.Text)) { Speech.Say(w, "Busy Bunny did not understand that reminder repeat. Try every day, every Monday, or every 2 weeks.", true); remindRepeat.Focus(); return; }
                int gradeWeight = 0;
                string wt = weight.Text.Trim().TrimEnd('%').Trim();
                if (wt.Length > 0 && (!int.TryParse(wt, out gradeWeight) || gradeWeight < 0 || gradeWeight > 100)) { Speech.Say(w, "Grade weight is a number from 0 to 100.", true); weight.Focus(); return; }
                AddSub();
                AddComment();
                string listName = lists.SelectedItem as string ?? list;
                var kind = (TaskType)Math.Max(0, type.SelectedIndex);
                string tag = (tags.Text ?? "").Trim();
                var known = store.Data.Tags.FirstOrDefault(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase));
                if (tag.Length > 0 && known == null) { tag = tag.ToUpperInvariant(); store.Data.Tags.Add(new TagInfo { Name = tag }); }
                else if (known != null) tag = known.Name;
                made = editing != null ? editing.Copy() : new TaskItem { StatusSince = DateTime.Now };
                made.Name = taskName;
                made.List = listName;
                made.Tag = tag;
                made.Type = kind;
                made.Section = section.SelectedIndex > 0 ? section.SelectedItem as string ?? "" : "";
                made.Status = (Status)Math.Max(0, statuses.SelectedIndex);
                made.Priority = Math.Max(0, priority.SelectedIndex);
                made.Due = when;
                made.DueHasTime = hasTime;
                made.DoDate = doWhen?.Date;
                made.Size = (Size)Math.Max(0, size.SelectedIndex);
                made.Energy = (Energy)Math.Max(0, energy.SelectedIndex);
                made.GradeWeight = gradeWeight;
                made.Repeat = RepeatRule.Parse(repeatText)?.Describe() ?? "";
                made.RenameOnRepeat = made.Repeat.Length > 0 && rename.IsChecked == true;
                made.RemindAt = remindWhen;
                made.RemindRepeat = RepeatRule.Parse(remindRepeat.Text)?.Describe() ?? "";
                made.Notes = notes.Text.Trim();
                made.Subtasks = subs;
                made.Comments = comments;
                made.IsMeeting = kind == TaskType.Meeting || listName == "Meetings";
                w.DialogResult = true;
            };
            w.Loaded += (_, _) => { name.Focus(); name.SelectAll(); };
            return w.ShowDialog() == true ? made : null;
        }
    }

    /// <summary>A small box asking for one line of text.</summary>
    public static class Prompt
    {
        public static string? Ask(Window owner, string title, string label, string initial = "")
        {
            var w = Layout.Dialog(owner, title);
            var box = new TextBox { Text = initial };
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            ok.Click += (_, _) => w.DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row(label, box));
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            return w.ShowDialog() == true ? box.Text : null;
        }
    }

    /// <summary>
    /// Repeating tasks, templates and adding several tasks at once, all around the task editor.
    /// Marking a repeating task Done adds its next copy. Control Shift N adds one task per line.
    /// </summary>
    public sealed class TaskTools : IFeature
    {
        public void Attach(MainWindow w)
        {
            w.AddMenuItem("Task", "Add _several tasks...", () => AddSeveral(w), "Ctrl+Shift+N");
            w.AddMenuItem("Task", "Save as _template...", () => SaveTemplate(w));
            w.AddMenuItem("Task", "New from t_emplate...", () => FromTemplate(w));
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.N, () => AddSeveral(w), "Control Shift N: add several tasks at once, one per line");
            w.StatusChanged += (t, old) => { if (t.Status == Status.Done && old != Status.Done) AddNextCopy(w, t); };
        }

        static void AddNextCopy(MainWindow w, TaskItem t)
        {
            if (t.Repeat.Length == 0) return;
            var start = w.Store.ListOf(t).DefaultStatus;
            if (RepeatRule.NextCopy(t, DateTime.Now, start) is not TaskItem next) return;
            // Marking it Done twice must not make two copies.
            if (w.Store.Data.Tasks.Any(x => x != t && x.Status != Status.Done && x.Name == t.Name && x.List == t.List && x.Due == next.Due && x.DoDate == next.DoDate)) return;
            if (t.RenameOnRepeat)
            {
                // Morgan's issue 5: weekly readings change chapter each week. Suggest the number moved on; Cancel keeps the suggestion.
                string suggested = RepeatRule.NextName(t.Name);
                string? typed = Prompt.Ask(w, "Name for the next one", "Next " + ((next.Due ?? next.DoDate) is DateTime nd ? Words.Day(nd, DateTime.Now) : "time") + ", the task is called", suggested);
                next.Name = string.IsNullOrWhiteSpace(typed) ? suggested : typed.Trim();
            }
            w.AddTask(next, announce: false);
            string when = (next.Due ?? next.DoDate) is DateTime d ? ", " + (next.Due != null ? "due " : "for ") + Words.Day(d, DateTime.Now) : "";
            w.Say("Next one added: " + next.Name + when + ".");
        }

        static void AddSeveral(MainWindow w)
        {
            var dlg = Layout.Dialog(w, "Add several tasks");
            var box = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 200, Width = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            var ok = Layout.Button("Add them all", isDefault: false);
            var cancel = Layout.Button("Cancel", isCancel: true);
            ok.Click += (_, _) => dlg.DialogResult = true;
            string list = w.CurrentListName;
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(new TextBlock { Text = "One task per line. They go on " + list + ".", Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap, MaxWidth = 420 });
            panel.Children.Add(Layout.Row("_Tasks, one per line", box, 420));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.Loaded += (_, _) => box.Focus();
            if (dlg.ShowDialog() != true) return;
            var names = box.Text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
            if (names.Count == 0) { w.Say("No tasks typed, so nothing was added.", true); return; }
            var status = w.Store.Data.Lists.FirstOrDefault(l => l.Name == list)?.DefaultStatus ?? Status.ThisWeek;
            foreach (var n in names)
                w.AddTask(new TaskItem { Name = n, List = list, Status = status, StatusSince = DateTime.Now, IsMeeting = list == "Meetings", Type = list == "Meetings" ? TaskType.Meeting : TaskType.General }, announce: false);
            w.Say("Added " + names.Count + (names.Count == 1 ? " task" : " tasks") + " to " + list + ". Control Z undoes them one at a time.", true);
        }

        static void SaveTemplate(MainWindow w)
        {
            if (w.ChosenTask is not TaskItem t) { w.Say("Pick a task first, then save it as a template.", true); return; }
            string? name = Prompt.Ask(w, "Save as template", "Template _name", t.Name);
            if (name == null || name.Trim().Length == 0) return;
            var tpl = t.Copy();
            tpl.Id = Guid.NewGuid();
            tpl.Name = name.Trim();
            tpl.Status = w.Store.ListOf(t).DefaultStatus;
            tpl.Due = null; tpl.DueHasTime = false; tpl.DoDate = null; tpl.RemindAt = null;
            tpl.DoneAt = null; tpl.Comments = new(); tpl.Snoozes = 0;
            foreach (var s in tpl.Subtasks) s.Done = false;
            w.Store.Remember("saving template " + tpl.Name);
            w.Store.Data.Templates.RemoveAll(x => x.Name.Equals(tpl.Name, StringComparison.OrdinalIgnoreCase));
            w.Store.Data.Templates.Add(tpl);
            w.SaveAndRefresh();
            w.Say("Saved template " + tpl.Name + ". Task, New from template makes a task from it.");
        }

        static void FromTemplate(MainWindow w)
        {
            var store = w.Store;
            if (store.Data.Templates.Count == 0) { w.Say("No templates yet. Pick a task and use Task, Save as template first.", true); return; }
            var dlg = Layout.Dialog(w, "New from template");
            var pick = new ComboBox { ItemsSource = store.Data.Templates.Select(t => t.Name).ToList(), SelectedIndex = 0 };
            var ok = Layout.Button("_Use it", isDefault: true);
            var del = Layout.Button("De_lete template");
            var cancel = Layout.Button("Cancel", isCancel: true);
            bool deleted = false;
            ok.Click += (_, _) => dlg.DialogResult = true;
            del.Click += (_, _) =>
            {
                if (pick.SelectedIndex < 0) return;
                var gone = store.Data.Templates[pick.SelectedIndex];
                store.Remember("deleting template " + gone.Name);
                store.Data.Templates.Remove(gone);
                deleted = true;
                w.SaveAndRefresh();
                w.Say("Deleted template " + gone.Name + ".", true);
                dlg.DialogResult = false;
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(del);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("_Template", pick));
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.Loaded += (_, _) => pick.Focus();
            if (dlg.ShowDialog() != true || deleted || pick.SelectedIndex < 0) return;
            var inst = store.Data.Templates[pick.SelectedIndex].Copy();
            inst.Id = Guid.NewGuid();
            inst.CreatedAt = DateTime.Now;
            if (QuickAdd.Show(w, store, w.CurrentListName, inst.Status, null, inst) is TaskItem made) w.AddTask(made);
        }

        public void SelfTest(Store store)
        {
            RepeatRule.SelfTest();
            void Check(bool ok, string what) { if (!ok) throw new Exception("Task editor: " + what); }
            Check(QuickAdd.SuggestPriority(30, 4) == 1, "30 percent suggests the top level");
            Check(QuickAdd.SuggestPriority(10, 4) == 2, "10 percent suggests the second level");
            Check(QuickAdd.SuggestPriority(5, 4) == 0, "5 percent suggests nothing");
            Check(QuickAdd.SuggestPriority(40, 0) == 0, "no scale suggests nothing");
            Check(QuickAdd.SuggestPriority(10, 1) == 1, "a one-level scale caps the suggestion");
            // Dates the editor relies on.
            var now = new DateTime(2026, 10, 12, 10, 0, 0);
            Check(Dates.Parse("tomorrow at 3", now).When == new DateTime(2026, 10, 13, 15, 0, 0), "tomorrow at 3");
            Check(Dates.Parse("10/20", now).When == new DateTime(2026, 10, 20), "10/20");
            Check(Dates.Parse("", now).When == null, "empty date");
        }
    }
}
