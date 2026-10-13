using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace BusyBunny
{
    /// <summary>One task found in pasted text, with notes about anything that was not understood.</summary>
    public sealed class Found
    {
        public TaskItem Task = new();
        public List<string> Notes = new();
        public bool Keep = true;
    }

    /// <summary>
    /// Getting tasks in and out (spec 12.16, 13.3, 19.4, 19.13, 21.4): Paste tasks, the braille list
    /// importer, the syllabus importer, plain-text export, and backup export and restore.
    /// The parsing is plain static code, so the self-test can check it.
    /// </summary>
    public sealed class ImportExport : IFeature
    {
        private MainWindow _w = null!;

        public void Attach(MainWindow w)
        {
            _w = w;
            w.AddMenuItem("File", "_Paste tasks...", PasteTasks, "Ctrl+Shift+V");
            w.AddMenuItem("File", "Import my _braille lists...", ImportBraille);
            w.AddMenuItem("File", "Import a s_yllabus...", ImportSyllabus);
            w.AddMenuItem("File", "Export as p_lain text...", ExportText);
            w.AddMenuItem("File", "E_xport a backup...", ExportBackup);
            w.AddMenuItem("File", "_Restore from a backup...", RestoreBackup);
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.V, PasteTasks, "Control Shift V: paste tasks from a list or from Claude");
        }

        // ---- Parsing ----

        public const string FormatHelp =
            "One task per line. Start with the task's name, then add any of these after it, separated by semicolons or commas: " +
            "list: Homework; tag: PSY; due: friday at 3; status: today; size: large; priority: 1; type: test. " +
            "Everything after the name is optional. Status is today, this week, next week, done not submitted, waiting or done. " +
            "Size is small, medium, large or multi-day. Priority 1 is the top of the list's scale. " +
            "Example: read chapter 4; list: Homework; tag: PSY; due: friday at 3; status: today; size: large; priority: 1";

        public const string ClaudeInstruction =
            "Turn what I tell you into a task list I can paste into Busy Bunny. Write one task per line, plain text only, no bullets, no numbering, no headings. " +
            "Start each line with the task's name, then optional parts separated by semicolons, like this: " +
            "read chapter 4; list: Homework; tag: PSY; due: friday at 3; status: today; size: large; priority: 1. " +
            "The parts are list, tag, due, status, size, priority and type, and you can leave out any of them. " +
            "Status is one of today, this week, next week, done not submitted, waiting, done. Size is small, medium, large or multi-day. " +
            "Type is one of assignment, test, meeting, event. Due is a plain date like friday, tomorrow, 10/20, or oct 20 at 3 pm. " +
            "Priority is a number where 1 is the most important. Here is what I need to get done:";

        private static readonly string[] Keys = { "list", "tag", "due", "status", "size", "priority", "type", "do" };

        public static TaskList DefaultList(Store store) =>
            store.Data.Lists.FirstOrDefault(l => !l.Archived && l.Name == "Homework") ?? store.Data.Lists.FirstOrDefault(l => !l.Archived) ?? store.Data.Lists[0];

        public static TaskList? FindList(Store store, string text)
        {
            text = text.Trim();
            var all = store.Data.Lists;
            return all.FirstOrDefault(l => l.Name.Equals(text, StringComparison.OrdinalIgnoreCase))
                ?? all.FirstOrDefault(l => text.Length >= 3 && l.Name.StartsWith(text, StringComparison.OrdinalIgnoreCase));
        }

        public static Status? ParseStatus(string text)
        {
            string t = Regex.Replace(text.Trim().ToLowerInvariant(), @"[^a-z ]", " ");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t switch
            {
                "today" or "star" => Status.Today,
                "this week" or "week" or "close bracket" => Status.ThisWeek,
                "next week" or "next" or "open bracket" => Status.NextWeek,
                "done not submitted" or "not submitted" or "done but not submitted" or "dns" or "x" => Status.DoneNotSubmitted,
                "waiting" or "waiting on someone" or "waiting on" => Status.Waiting,
                "done" or "finished" or "complete" or "completed" => Status.Done,
                _ => null,
            };
        }

        private static TaskType? ParseType(string text)
        {
            string t = Regex.Replace(text.ToLowerInvariant(), @"[^a-z]", "");
            foreach (TaskType x in Enum.GetValues<TaskType>())
                if (x.ToString().ToLowerInvariant() == t) return x;
            return t switch { "exam" or "quiz" or "midterm" => TaskType.Test, "homework" => TaskType.Assignment, "goal" => TaskType.PersonalGoal, _ => null };
        }

        /// <summary>Reads one line of pasted text. Null when the line is empty.</summary>
        public static Found? ParseLine(string line, Store store, TaskList defaultList, DateTime now)
        {
            line = Regex.Replace(line.Trim(), @"^(?:[-•*]\s+|\d{1,3}[.)]\s+)", "");
            if (line.Length == 0) return null;
            char sep = line.Contains(';') ? ';' : ',';
            var f = new Found();
            var t = f.Task;
            t.List = defaultList.Name;
            var nameParts = new List<string>();
            var parts = line.Split(sep);
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].Trim();
                var m = Regex.Match(part, @"^([A-Za-z]+)\s*:\s*(.*)$");
                if (i == 0 || !m.Success || !Keys.Contains(m.Groups[1].Value.ToLowerInvariant()))
                {
                    if (part.Length > 0) nameParts.Add(part);
                    continue;
                }
                string key = m.Groups[1].Value.ToLowerInvariant(), val = m.Groups[2].Value.Trim();
                switch (key)
                {
                    case "list":
                        var l = FindList(store, val);
                        if (l != null) t.List = l.Name; else f.Notes.Add("no list called " + val + ", using " + defaultList.Name);
                        break;
                    case "tag": t.Tag = val.ToUpperInvariant() == val || val.Length > 6 ? val : val.ToUpperInvariant(); break;
                    case "due":
                        var (when, hasTime) = Dates.Parse(val, now);
                        if (when == null) f.Notes.Add("did not understand the date " + val); else { t.Due = when; t.DueHasTime = hasTime; }
                        break;
                    case "do":
                        var (doDay, _) = Dates.Parse(val, now);
                        if (doDay == null) f.Notes.Add("did not understand the date " + val); else t.DoDate = doDay.Value.Date;
                        break;
                    case "status":
                        var s = ParseStatus(val);
                        if (s == null) f.Notes.Add("did not understand the status " + val); else t.Status = s.Value;
                        break;
                    case "size":
                        string sz = val.ToLowerInvariant();
                        if (sz.StartsWith("s")) t.Size = Size.Small;
                        else if (sz.StartsWith("m") && sz.Contains("day") || sz.StartsWith("multi")) t.Size = Size.MultiDay;
                        else if (sz.StartsWith("m")) t.Size = Size.Medium;
                        else if (sz.StartsWith("l")) t.Size = Size.Large;
                        else f.Notes.Add("did not understand the size " + val);
                        break;
                    case "priority":
                        if (int.TryParse(Regex.Match(val, @"\d+").Value, out int p) && p >= 0) t.Priority = p;
                        else f.Notes.Add("did not understand the priority " + val);
                        break;
                    case "type":
                        var ty = ParseType(val);
                        if (ty == null) f.Notes.Add("did not understand the type " + val); else { t.Type = ty.Value; t.IsMeeting = ty == TaskType.Meeting; }
                        break;
                }
            }
            t.Name = string.Join(", ", nameParts);
            if (t.Name.Length == 0) return null;
            // The priority has to fit the list's scale, which is known only now that the list is.
            int scale = store.Data.Lists.FirstOrDefault(x => x.Name == t.List)?.Scale.Length ?? 0;
            if (t.Priority > scale)
            {
                f.Notes.Add(scale == 0 ? "that list has no priority scale, so no priority" : "priority " + t.Priority + " is past the scale, using " + scale);
                t.Priority = scale;
            }
            t.StatusSince = now;
            return f;
        }

        public static List<Found> ParsePaste(string text, Store store, TaskList defaultList, DateTime now) =>
            text.Replace("\r", "").Split('\n').Select(l => ParseLine(l, store, defaultList, now)).Where(f => f != null).Select(f => f!).ToList();

        /// <summary>The braille symbol at the start of a line, and the rest. Star Today, close bracket This week, open bracket Next week, X done not submitted.</summary>
        public static (Status Status, string Text) BrailleLine(string line)
        {
            line = line.Trim();
            if (line.Length == 0) return (Status.ThisWeek, "");
            char c = line[0];
            string rest = line.Length > 1 ? line[1..].Trim() : "";
            bool spaced = line.Length == 1 || char.IsWhiteSpace(line[1]) || line[1] == '-' || line[1] == '.';
            switch (c)
            {
                case '*': return (Status.Today, rest);
                case ')': return (Status.ThisWeek, rest);
                case '(': return (Status.NextWeek, rest);
                case 'X' when spaced: return (Status.DoneNotSubmitted, rest.TrimStart('-', '.', ' '));
                case 'x' when spaced: return (Status.DoneNotSubmitted, rest.TrimStart('-', '.', ' '));
                case 'W' when spaced: return (Status.Waiting, rest.TrimStart('-', '.', ' '));
                case 'D' when spaced: return (Status.Done, rest.TrimStart('-', '.', ' '));
            }
            return (Status.ThisWeek, line);
        }

        public static List<Found> ParseBraille(string text, TaskList list, DateTime now)
        {
            var found = new List<Found>();
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                var (status, name) = BrailleLine(raw);
                if (name.Length == 0) continue;
                found.Add(new Found { Task = new TaskItem { Name = name, List = list.Name, Status = status, StatusSince = now } });
            }
            return found;
        }

        private static readonly string Months = "(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sept?(?:ember)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)";
        private static readonly Regex DateBit = new(@"\b(?:" + Months + @"\.?\s+\d{1,2}(?:st|nd|rd|th)?\b(?!\s*,?\s*\d{3,4}\b)|\d{1,2}/\d{1,2}(?:/\d{2,4})?)", RegexOptions.IgnoreCase);
        private static readonly Regex TestWords = new(@"\b(test|exam|quiz|midterm|final)\b", RegexOptions.IgnoreCase);

        /// <summary>Finds dated lines in a syllabus. Each becomes a task due that day; tests and exams are Test type.</summary>
        public static List<Found> ParseSyllabus(string text, string tag, TaskList list, DateTime now)
        {
            var found = new List<Found>();
            foreach (string raw in text.Replace("\r", "").Split('\n'))
            {
                string line = raw.Trim();
                var m = DateBit.Match(line);
                if (!m.Success) continue;
                string bit = m.Value.TrimEnd('.');
                bit = Regex.Replace(bit, @"(\d)(?:st|nd|rd|th)$", "$1", RegexOptions.IgnoreCase);
                var mm = Regex.Match(bit, @"^([A-Za-z]+)\.?\s+(\d+)$");
                if (mm.Success) bit = mm.Groups[1].Value[..3] + " " + mm.Groups[2].Value;
                var (when, _) = Dates.Parse(bit, now);
                if (when == null) continue;
                // Dates.Parse rolls old dates into next year; in a syllabus a date months ahead is really last term's.
                if (!Regex.IsMatch(bit, @"\d{4}") && when.Value > now.AddMonths(6)) when = when.Value.AddYears(-1);
                string name = (line[..m.Index] + " " + line[(m.Index + m.Length)..]);
                name = Regex.Replace(name, @"\b(?:due|on|by)\s*[:\-]?\s*$", "", RegexOptions.IgnoreCase);
                name = Regex.Replace(name, @"^\s*(?:due|on|by)\b\s*[:\-]?", "", RegexOptions.IgnoreCase);
                name = Regex.Replace(name, @"\s+", " ").Trim(' ', '-', ':', ',', ';', '.', '|', '\t', '(', ')', '–', '—');
                if (name.Length == 0) name = "Something from the syllabus";
                bool test = TestWords.IsMatch(name);
                var t = new TaskItem
                {
                    Name = name,
                    List = list.Name,
                    Tag = tag,
                    Due = when.Value.Date,
                    Type = test ? TaskType.Test : TaskType.Assignment,
                    Size = test ? Size.Large : Size.Medium,
                    StatusSince = now,
                    Status = (when.Value.Date - now.Date).Days <= 6 ? Status.ThisWeek : Status.NextWeek,
                };
                var f = new Found { Task = t };
                if (when.Value.Date < now.Date) { f.Notes.Add("that date has passed"); f.Keep = false; }
                found.Add(f);
            }
            return found;
        }

        private static string Symbol(Status s) => s switch
        {
            Status.Today => "*",
            Status.ThisWeek => ")",
            Status.NextWeek => "(",
            Status.DoneNotSubmitted => "X",
            Status.Waiting => "W",
            _ => "D",
        };

        /// <summary>Every list as a heading line, then each task in the braille list style with its tag and due date.</summary>
        public static string ToPlainText(Store store, bool includeDone)
        {
            var sb = new StringBuilder();
            foreach (var list in store.Data.Lists)
            {
                var tasks = store.Data.Tasks.Where(t => t.List == list.Name && (includeDone || t.Status != Status.Done)).ToList();
                if (tasks.Count == 0) continue;
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine(list.Name);
                foreach (var t in tasks.OrderBy(t => t.Status).ThenBy(t => t.Priority == 0 ? 99 : t.Priority).ThenBy(t => t.Due))
                {
                    sb.Append(Symbol(t.Status)).Append(' ').Append(t.Name);
                    if (t.Tag.Length > 0) sb.Append(", ").Append(t.Tag);
                    if (t.Due is DateTime d) sb.Append(", due ").Append(d.ToString("dddd, MMMM d")).Append(t.DueHasTime ? " at " + Words.Time(d) : "");
                    sb.AppendLine();
                }
            }
            return sb.ToString();
        }

        // ---- Dialogs ----

        private static TextBox BigBox(string name, string text = "") => BoxFor(name, text, 640, 220);

        private static TextBox BoxFor(string name, string text, double w, double h)
        {
            var b = new TextBox { Text = text, AcceptsReturn = true, AcceptsTab = false, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = w, Height = h };
            AutomationProperties.SetName(b, name);
            return b;
        }

        private static ComboBox ListPicker(Store store)
        {
            var names = store.Data.Lists.Where(l => !l.Archived).Select(l => l.Name).ToList();
            var cb = new ComboBox { ItemsSource = names };
            cb.SelectedItem = DefaultList(store).Name;
            return cb;
        }

        private static TaskList PickedList(Store store, ComboBox cb) => FindList(store, cb.SelectedItem as string ?? "") ?? DefaultList(store);

        /// <summary>Shows a dialog and returns true when OK was pressed. Buttons are OK-like and Cancel, plus any extras given.</summary>
        private bool Ask(string title, string okText, Action<StackPanel> build, Func<string?> validate, params (string Text, Action Act)[] extras)
        {
            var w = Layout.Dialog(_w, title);
            var panel = new StackPanel { Margin = new Thickness(14) };
            build(panel);
            var ok = Layout.Button(okText, isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            foreach (var (text, act) in extras)
            {
                var b = Layout.Button(text);
                b.Click += (_, _) => act();
                buttons.Children.Add(b);
            }
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80, Focusable = false };
            ok.Click += (_, _) =>
            {
                string? problem = validate();
                if (problem != null) { Speech.Say(w, problem, true); return; }
                w.DialogResult = true;
            };
            w.Loaded += (_, _) =>
            {
                var first = panel.Children.OfType<FrameworkElement>().Select(Focusable).FirstOrDefault(x => x != null);
                first?.Focus();
            };
            return w.ShowDialog() == true;
        }

        private static FrameworkElement? Focusable(FrameworkElement e)
        {
            if (e is StackPanel sp) return sp.Children.OfType<Control>().FirstOrDefault(c => c is not Label && c.Focusable);
            return e is Control c && c.Focusable && e is not Label ? e : null;
        }

        /// <summary>The preview: each thing to add as a line with a check box. Returns the ones left checked, or null on Cancel.</summary>
        private List<Found>? Preview(string title, string intro, string addText, List<Found> found, DateTime now)
        {
            if (found.Count == 0) { _w.Say("I found nothing to add."); return null; }
            var list = new ListBox { Width = 720, Height = 340 };
            System.Windows.Controls.VirtualizingPanel.SetIsVirtualizing(list, false);
            AutomationProperties.SetName(list, "Preview. Space checks or unchecks a line");
            foreach (var f in found)
            {
                string text = _w.Store.Line(f.Task, now) + (f.Notes.Count > 0 ? " (" + string.Join("; ", f.Notes) + ")" : "") + ", in " + f.Task.List;
                var cb = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 660 }, IsChecked = f.Keep };
                AutomationProperties.SetName(cb, text);
                var fx = f;
                cb.Checked += (_, _) => fx.Keep = true;
                cb.Unchecked += (_, _) => fx.Keep = false;
                list.Items.Add(new ListBoxItem { Content = cb, Focusable = false, IsTabStop = false });
            }
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            // The check box takes the focus, so the arrows and Space work on it.
            bool go = Ask(title, addText, p =>
            {
                p.Children.Add(new TextBlock { Text = intro + " " + found.Count + (found.Count == 1 ? " line" : " lines") + ".", TextWrapping = TextWrapping.Wrap, MaxWidth = 720, Margin = new Thickness(0, 0, 0, 6) });
                p.Children.Add(list);
            }, () => found.Any(f => f.Keep) ? null : "Nothing is checked.");
            return go ? found.Where(f => f.Keep).ToList() : null;
        }

        private void AddAll(string what, List<Found> chosen)
        {
            var store = _w.Store;
            store.Remember(what);
            foreach (var f in chosen)
            {
                store.Data.Tasks.Add(f.Task);
                if (f.Task.Tag.Length > 0 && !store.Data.Tags.Any(x => x.Name.Equals(f.Task.Tag, StringComparison.OrdinalIgnoreCase)))
                    store.Data.Tags.Add(new TagInfo { Name = f.Task.Tag });
            }
            _w.SaveAndRefresh();
            _w.Say("Added " + chosen.Count + (chosen.Count == 1 ? " task." : " tasks.") + " Control Z undoes all of them at once.");
        }

        private void PasteTasks()
        {
            var store = _w.Store;
            var text = BigBox("Tasks, one per line");
            var list = ListPicker(store);
            var help = BoxFor("How to write the tasks", FormatHelp, 640, 110);
            help.IsReadOnly = true;
            help.IsReadOnlyCaretVisible = true;
            if (!Ask("Paste tasks", "_Preview", p =>
            {
                p.Children.Add(Layout.Row("_Tasks, one per line", text, 640));
                p.Children.Add(Layout.Row("Lines without a list go to _list", list, 240));
                p.Children.Add(Layout.Row("How to write them", help, 640));
            }, () => text.Text.Trim().Length == 0 ? "There is nothing to preview. Paste or type some tasks first." : null,
            ("Copy the format for _Claude", () =>
            {
                try { Clipboard.SetText(ClaudeInstruction); _w.Say("Copied. Paste it into a Claude chat, then tell Claude what you need to do."); }
                catch { _w.Say("The clipboard would not open. Try again."); }
            }))) return;
            var now = DateTime.Now;
            var chosen = Preview("Paste tasks, preview", "This is what will be added.", "_Add all", ParsePaste(text.Text, store, PickedList(store, list), now), now);
            if (chosen != null) AddAll("pasting tasks", chosen);
        }

        private void ImportBraille()
        {
            var store = _w.Store;
            var text = BigBox("Your braille list");
            var list = ListPicker(store);
            var help = new TextBlock
            {
                Text = "A line starting with a star is Today, a close bracket is This week, an open bracket is Next week, and an X is Done, not submitted. A line with no symbol is This week.",
                TextWrapping = TextWrapping.Wrap, MaxWidth = 640, Margin = new Thickness(0, 0, 0, 6),
            };
            if (!Ask("Import my braille lists", "_Preview", p =>
            {
                p.Children.Add(help);
                p.Children.Add(Layout.Row("_Paste your list here", text, 640));
                p.Children.Add(Layout.Row("Add them to _list", list, 240));
            }, () => text.Text.Trim().Length == 0 ? "There is nothing to preview. Paste your list or open a file first." : null,
            ("Open a _file...", () =>
            {
                var d = new OpenFileDialog { Title = "Open a braille list", Filter = "Text or braille files (*.txt;*.brf)|*.txt;*.brf|All files (*.*)|*.*" };
                if (d.ShowDialog(_w) != true) return;
                try { text.Text = File.ReadAllText(d.FileName); text.Focus(); _w.Say("Opened " + Path.GetFileName(d.FileName) + "."); }
                catch (Exception e) { _w.Say("Could not read that file: " + e.Message); }
            }))) return;
            var now = DateTime.Now;
            var chosen = Preview("Import braille lists, preview", "This is what will be added.", "_Add all", ParseBraille(text.Text, PickedList(store, list), now), now);
            if (chosen != null) AddAll("importing braille lists", chosen);
        }

        private void ImportSyllabus()
        {
            var store = _w.Store;
            var text = BigBox("Syllabus text");
            var list = ListPicker(store);
            var tag = new ComboBox { IsEditable = true, ItemsSource = store.Data.Tags.Select(t => t.Name).ToList() };
            if (!Ask("Import a syllabus", "_Preview", p =>
            {
                p.Children.Add(new TextBlock { Text = "Paste the syllabus, or a cleaned up version from Claude. Every line with a date becomes a task, and tests and exams become Test tasks.", TextWrapping = TextWrapping.Wrap, MaxWidth = 640, Margin = new Thickness(0, 0, 0, 6) });
                p.Children.Add(Layout.Row("_Syllabus", text, 640));
                p.Children.Add(Layout.Row("Class _tag, like PSY", tag, 240));
                p.Children.Add(Layout.Row("Add them to _list", list, 240));
            }, () => text.Text.Trim().Length == 0 ? "There is nothing to preview. Paste the syllabus first." : null)) return;
            var now = DateTime.Now;
            var found = ParseSyllabus(text.Text, (tag.Text ?? "").Trim().ToUpperInvariant(), PickedList(store, list), now);
            if (found.Count == 0) { _w.Say("I could not find any lines with dates."); return; }
            var chosen = Preview("Import a syllabus, preview", "Uncheck anything you do not want. Lines with a date that has passed start unchecked.", "_Add checked", found, now);
            if (chosen != null) AddAll("importing a syllabus", chosen);
        }

        private void ExportText()
        {
            bool done = false;
            var box = new CheckBox { Content = "Include _done tasks" };
            if (!Ask("Export as plain text", "_Save as...", p =>
            {
                p.Children.Add(new TextBlock { Text = "Writes every list as a heading, then each task the way your braille lists look: star for Today, close bracket for This week, open bracket for Next week, X for done not submitted, with its tag and due date.", TextWrapping = TextWrapping.Wrap, MaxWidth = 520, Margin = new Thickness(0, 0, 0, 8) });
                p.Children.Add(box);
            }, () => null)) return;
            done = box.IsChecked == true;
            var d = new SaveFileDialog { Title = "Export as plain text", Filter = "Text files (*.txt)|*.txt", FileName = "Busy Bunny tasks " + DateTime.Now.ToString("yyyy-MM-dd") + ".txt" };
            if (d.ShowDialog(_w) != true) return;
            try
            {
                File.WriteAllText(d.FileName, ToPlainText(_w.Store, done), new UTF8Encoding(false));
                _w.Say("Saved your tasks to " + Path.GetFileName(d.FileName) + ".");
            }
            catch (Exception e) { _w.Say("Could not save: " + e.Message); }
        }

        private void ExportBackup()
        {
            var d = new SaveFileDialog { Title = "Export a backup", Filter = "Busy Bunny data (*.json)|*.json", FileName = "Busy Bunny backup " + DateTime.Now.ToString("yyyy-MM-dd") + ".json" };
            if (d.ShowDialog(_w) != true) return;
            try
            {
                _w.Store.Save();
                File.Copy(Store.FilePath, d.FileName, true);
                _w.Say("Saved a backup to " + Path.GetFileName(d.FileName) + ".");
            }
            catch (Exception e) { _w.Say("Could not save the backup: " + e.Message); }
        }

        private void RestoreBackup()
        {
            var d = new OpenFileDialog { Title = "Restore from a backup", Filter = "Busy Bunny data (*.json)|*.json|All files (*.*)|*.*" };
            if (d.ShowDialog(_w) != true) return;
            var data = Store.ReadFile(d.FileName);
            if (data == null) { _w.Say("That file is not Busy Bunny data, so nothing changed."); return; }
            var ask = MessageBox.Show(_w, "Replace everything now with " + data.Tasks.Count + " tasks in " + data.Lists.Count + " lists from " + Path.GetFileName(d.FileName) + "? Control Z undoes it.",
                "Restore from a backup", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
            if (ask != MessageBoxResult.Yes) { _w.Say("Cancelled. Nothing changed."); return; }
            _w.Store.Remember("restoring a backup");
            _w.Store.ReplaceData(data);
            _w.SaveAndRefresh();
            _w.Say("Restored " + data.Tasks.Count + " tasks from the backup. Control Z undoes it.");
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("import/export: " + what); }
            var now = new DateTime(2026, 10, 12, 9, 0, 0);
            var hw = DefaultList(store);
            var f = ParseLine("read chapter 4; list: Homework; tag: psy; due: friday at 3; status: today; size: large; priority: 1", store, hw, now)!;
            Need(f.Task.Name == "read chapter 4" && f.Task.List == "Homework" && f.Task.Tag == "PSY", "paste name, list, tag");
            Need(f.Task.Due == new DateTime(2026, 10, 16, 15, 0, 0) && f.Task.DueHasTime, "paste due");
            Need(f.Task.Status == Status.Today && f.Task.Size == Size.Large && f.Task.Priority == 1 && f.Notes.Count == 0, "paste status, size, priority");
            var c = ParseLine("- email Dr. Lee, then print forms, due: tomorrow, status: next week, size: small", store, hw, now)!;
            Need(c.Task.Name == "email Dr. Lee, then print forms" && c.Task.Status == Status.NextWeek && c.Task.Due == now.Date.AddDays(1), "paste with commas");
            var bad = ParseLine("odd one; due: blorp; status: huh; list: Nowhere", store, hw, now)!;
            Need(bad.Notes.Count == 3 && bad.Task.List == "Homework", "paste notes bad parts");
            Need(ParsePaste("a\n\n  \nb; size: multi-day", store, hw, now).Count == 2, "paste skips blank lines");
            var br = ParseBraille("* call grandma\n) lab report\n( start the lit review\nX stats set 3\nplain task\n\nXenon essay", hw, now);
            Need(br.Select(x => x.Task.Status).SequenceEqual(new[] { Status.Today, Status.ThisWeek, Status.NextWeek, Status.DoneNotSubmitted, Status.ThisWeek, Status.ThisWeek }), "braille symbols");
            Need(br[0].Task.Name == "call grandma" && br[5].Task.Name == "Xenon essay", "braille names");
            var sy = ParseSyllabus("Sept 3 - Syllabus quiz\nWeek 5: Midterm exam on Oct 20\nProblem set 2 due 10/14\nno date here\nOct 1: reading", "PSY", hw, now);
            Need(sy.Count == 4, "syllabus count " + sy.Count);
            Need(sy[1].Task.Type == TaskType.Test && sy[1].Task.Due == new DateTime(2026, 10, 20), "syllabus test");
            Need(sy[2].Task.Name == "Problem set 2" && sy[2].Task.Due == new DateTime(2026, 10, 14) && sy[2].Task.Tag == "PSY", "syllabus assignment");
            Need(!sy[0].Keep && !sy[3].Keep && sy[1].Keep, "syllabus past dates unchecked");
            string text = ToPlainText(store, false);
            Need(text.Contains("Homework") && text.Contains("* chapter 4 reading, PSY, due ") && text.Contains("X lab 3 write-up"), "plain text export");
            string file = Path.Combine(Path.GetTempPath(), "busybunny-restore-" + Guid.NewGuid().ToString("N") + ".json");
            try
            {
                var copy = Store.Load(file);
                copy.Save();
                var back = Store.ReadFile(file);
                Need(back != null && back.Tasks.Count == copy.Data.Tasks.Count, "backup file reads back");
                File.WriteAllText(file, "{ nope");
                Need(Store.ReadFile(file) == null, "a bad file is refused");
            }
            finally { try { File.Delete(file); } catch { } }
        }
    }
}
