using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    public enum AccessStatus { ToAsk, Asked, Approved }

    /// <summary>One accessibility request for one class (spec 19.6): accessible materials, extended test time, a note taker, or her own.</summary>
    public sealed class AccessRequest
    {
        public string Tag { get; set; } = "";
        public string Item { get; set; } = "";
        public AccessStatus Status { get; set; }
    }

    /// <summary>
    /// School helpers: the class schedule (spec 19.5) with time until the next class (15.3), the exam
    /// countdown (22.1), the accessibility requests tracker (19.6), and the spoken hint that a holiday
    /// sound pack fits the season (19.11, it never switches by itself).
    /// </summary>
    public sealed class School : IFeature
    {
        public static readonly string[] StandardRequests = { "accessible materials", "extended test time", "note taker" };

        public static string StatusWords(AccessStatus s) => s switch { AccessStatus.ToAsk => "to ask", AccessStatus.Asked => "asked", _ => "approved" };

        // ---- Times and classes ----

        /// <summary>Reads a time typed in plain words: 10:15 am, 10am, noon, 13:30. With no am or pm, 1 to 7 means afternoon.</summary>
        public static TimeSpan? ParseTime(string text)
        {
            string t = text.Trim().ToLowerInvariant();
            if (t == "noon") return TimeSpan.FromHours(12);
            if (t == "midnight") return TimeSpan.Zero;
            var m = Regex.Match(t, @"^(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s*(a\.?m\.?|p\.?m\.?)?$");
            if (!m.Success) return null;
            int h = int.Parse(m.Groups[1].Value), min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            string ap = m.Groups[3].Value;
            if (min > 59 || h > 23) return null;
            if (ap.Length > 0)
            {
                if (h < 1 || h > 12) return null;
                if (ap[0] == 'p' && h < 12) h += 12;
                else if (ap[0] == 'a' && h == 12) h = 0;
            }
            else if (h >= 1 && h <= 7) h += 12;
            return new TimeSpan(h, min, 0);
        }

        public static string Clock(TimeSpan t) => Words.Time(DateTime.Today.Add(t));

        private static readonly DayOfWeek[] WeekOrder =
            { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday };

        public static string ClassLine(ClassTime c) =>
            (c.Tag.Length > 0 ? c.Tag : "Class") + ", " + string.Join(" ", WeekOrder.Where(c.Days.Contains)) + ", " + Clock(c.Start) + " to " + Clock(c.End) +
            (c.Where.Length > 0 ? ", " + c.Where : "");

        private static string Gap(TimeSpan t)
        {
            if (t.TotalMinutes < 60) return Math.Max(1, (int)t.TotalMinutes) + (t.TotalMinutes < 2 ? " minute" : " minutes");
            int h = (int)t.TotalHours, m = t.Minutes;
            return h + (h == 1 ? " hour" : " hours") + (m >= 5 ? " " + m + " minutes" : "");
        }

        /// <summary>Time until the next class today (15.3), or null when there is none, or a meeting comes sooner.</summary>
        public static string? NextClassPart(Data d, DateTime now, bool shortMode)
        {
            var next = d.Classes.Where(c => c.Days.Contains(now.DayOfWeek) && c.Start > now.TimeOfDay).OrderBy(c => c.Start).FirstOrDefault();
            if (next == null) return null;
            var at = now.Date + next.Start;
            if (d.Tasks.Any(t => t.IsMeeting && t.Status != Status.Done && t.Due > now && t.Due < at)) return null;
            string s = "next class " + (next.Tag.Length > 0 ? next.Tag : "") + (next.Tag.Length > 0 ? " " : "") + "at " + Words.Time(at);
            return shortMode ? s : s + ", in " + Gap(at - now);
        }

        // ---- Exam countdown ----

        public static bool IsTest(TaskItem t) => t.Type == TaskType.Test || t.Name.Contains("test", StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains("exam", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("quiz", StringComparison.OrdinalIgnoreCase);

        public static TaskItem? NearestTest(Data d, DateTime now) => d.Tasks
            .Where(t => t.Status != Status.Done && IsTest(t) && t.Due is DateTime due && due.Date >= now.Date && due.Date <= now.Date.AddDays(14) && !(t.DueHasTime && due < now))
            .OrderBy(t => t.Due).FirstOrDefault();

        public static int StudyTasksLeft(Data d, TaskItem test) => test.Tag.Length == 0 || test.Due is not DateTime due ? 0 : d.Tasks
            .Count(t => t != test && t.Id != test.Id && t.Status != Status.Done && !t.IsMeeting && !IsTest(t) &&
                        t.Tag.Equals(test.Tag, StringComparison.OrdinalIgnoreCase) && t.Due is DateTime td && td <= due);

        /// <summary>PSY midterm in 6 days, 3 study tasks left. Short mode drops the words around the count.</summary>
        public static string? ExamPart(Data d, DateTime now, bool shortMode)
        {
            if (NearestTest(d, now) is not TaskItem test || test.Due is not DateTime due) return null;
            int days = (due.Date - now.Date).Days;
            string when = days == 0 ? "today" : days == 1 ? "tomorrow" : "in " + days + " days";
            string name = test.Tag.Length > 0 && !test.Name.StartsWith(test.Tag, StringComparison.OrdinalIgnoreCase) ? test.Tag + " " + test.Name : test.Name;
            string s = name + " " + when;
            if (test.Tag.Length == 0) return s;
            int left = StudyTasksLeft(d, test);
            if (shortMode) return s + ", " + left + " left";
            return s + ", " + (left == 0 ? "no study tasks left" : left + (left == 1 ? " study task left" : " study tasks left"));
        }

        // ---- Accessibility requests ----

        /// <summary>Tags that are classes: in the schedule, or given a full name.</summary>
        public static List<string> ClassTags(Data d) => d.Classes.Select(c => c.Tag).Concat(d.Tags.Where(t => t.Description.Length > 0).Select(t => t.Name))
            .Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The requests for a class; the standard three, still to ask, when none were saved yet.</summary>
        public static List<AccessRequest> RequestsFor(Data d, string tag)
        {
            var have = d.AccessRequests.Where(r => r.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)).ToList();
            return have.Count > 0 ? have : StandardRequests.Select(s => new AccessRequest { Tag = tag, Item = s }).ToList();
        }

        public static string SemesterKey(DateTime now) => now.Year + (now.Month >= 8 ? " fall" : now.Month >= 6 ? " summer" : " spring");

        private static string Join(IEnumerable<string> items)
        {
            var l = items.ToList();
            return l.Count <= 1 ? string.Concat(l) : string.Join(", ", l.Take(l.Count - 1)) + " and " + l[^1];
        }

        /// <summary>
        /// What to say about requests still to ask: once per class each semester (which also covers a class
        /// tag just added), and once per test when it is two weeks away or less. Marks them as said.
        /// </summary>
        public static string? Nudge(Data d, DateTime now)
        {
            var lines = new List<string>();
            foreach (string tag in ClassTags(d))
            {
                var toAsk = RequestsFor(d, tag).Where(r => r.Status == AccessStatus.ToAsk).Select(r => r.Item).ToList();
                if (toAsk.Count == 0) continue;
                string key = tag + "|" + SemesterKey(now);
                if (!d.AccessNudges.Contains(key))
                {
                    d.AccessNudges.Add(key);
                    lines.Add(tag + ", still to ask: " + Join(toAsk));
                }
            }
            foreach (var t in d.Tasks.Where(t => t.Status != Status.Done && IsTest(t) && t.Tag.Length > 0 && t.Due is DateTime due && due >= now.Date && due.Date <= now.Date.AddDays(14)))
            {
                string tag = ClassTags(d).FirstOrDefault(c => c.Equals(t.Tag, StringComparison.OrdinalIgnoreCase)) ?? "";
                if (tag.Length == 0) continue;
                var toAsk = RequestsFor(d, tag).Where(r => r.Status == AccessStatus.ToAsk).Select(r => r.Item).ToList();
                string key = tag + "|test|" + t.Id;
                if (toAsk.Count == 0 || d.AccessNudges.Contains(key)) continue;
                d.AccessNudges.Add(key);
                lines.Add(t.Name + " is " + Words.Day(t.Due!.Value, now) + ". For " + tag + ", still to ask: " + Join(toAsk));
            }
            return lines.Count == 0 ? null : "Accessibility reminder. " + string.Join(". ", lines) + ". Options, Settings, Accessibility has the list. Better to ask early than awkwardly.";
        }

        // ---- The feature ----

        public void Attach(MainWindow w)
        {
            w.Store.SummaryParts.Add((now, shortMode) => NextClassPart(w.Store.Data, now, shortMode));
            w.Store.SummaryParts.Add((now, shortMode) => ExamPart(w.Store.Data, now, shortMode));
            w.Started += () => { Remind(w); SeasonHint(w); };
            w.NewDay += _ => { Remind(w); SeasonHint(w); };
        }

        private static void Remind(MainWindow w)
        {
            if (Nudge(w.Store.Data, DateTime.Now) is string say) { w.Store.Save(); w.Say(say); }
        }

        private static void SeasonHint(MainWindow w)
        {
            var pack = Sounds.SeasonPack(DateTime.Now);
            if (pack == null || w.AppSettings.SoundPack == pack) return;
            string key = pack + " " + DateTime.Now.Year;
            if (w.AppSettings.PackHinted == key) return;
            w.AppSettings.PackHinted = key;
            w.AppSettings.Save();
            w.Say("There is a " + pack + " sound pack for this time of year. Options, Sounds, Sound pack, if you want it. It never switches on its own.");
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings)
        {
            yield return new ClassesPage(store);
            yield return new AccessPage(store);
        }

        public void SelfTest(Store store)
        {
            void Is<T>(T got, T want, string what) { if (!Equals(got, want)) throw new Exception(what + " gave " + got + ", wanted " + want); }
            Is(ParseTime("10:15 am"), (TimeSpan?)new TimeSpan(10, 15, 0), "10:15 am");
            Is(ParseTime("10am"), (TimeSpan?)new TimeSpan(10, 0, 0), "10am");
            Is(ParseTime("2"), (TimeSpan?)new TimeSpan(14, 0, 0), "2");
            Is(ParseTime("9"), (TimeSpan?)new TimeSpan(9, 0, 0), "9");
            Is(ParseTime("12 pm"), (TimeSpan?)new TimeSpan(12, 0, 0), "12 pm");
            Is(ParseTime("12:05 am"), (TimeSpan?)new TimeSpan(0, 5, 0), "12:05 am");
            Is(ParseTime("noon"), (TimeSpan?)new TimeSpan(12, 0, 0), "noon");
            Is(ParseTime("13:30"), (TimeSpan?)new TimeSpan(13, 30, 0), "13:30");
            Is(ParseTime("25:00"), (TimeSpan?)null, "25:00");
            Is(ParseTime("blorp"), (TimeSpan?)null, "blorp");

            var now = new DateTime(2026, 10, 12, 9, 20, 0); // a Monday
            var d = new Data();
            d.Classes.Add(new ClassTime { Tag = "PSY", Days = { DayOfWeek.Monday }, Start = new TimeSpan(10, 0, 0), End = new TimeSpan(11, 15, 0) });
            Is(NextClassPart(d, now, false), "next class PSY at 10 am, in 40 minutes", "next class");
            Is(NextClassPart(d, now, true), "next class PSY at 10 am", "next class short");
            Is(NextClassPart(d, new DateTime(2026, 10, 13, 9, 0, 0), false), null, "no class Tuesday");
            Is(NextClassPart(d, new DateTime(2026, 10, 12, 10, 30, 0), false), null, "class already started");
            d.Tasks.Add(new TaskItem { Name = "advisor", IsMeeting = true, Due = now.AddMinutes(15), DueHasTime = true });
            Is(NextClassPart(d, now, false), null, "meeting sooner");

            d.Tasks.Clear();
            var test = new TaskItem { Name = "midterm", Tag = "PSY", Type = TaskType.Test, Due = new DateTime(2026, 10, 18) };
            d.Tasks.Add(test);
            for (int i = 0; i < 3; i++) d.Tasks.Add(new TaskItem { Name = "chapter " + i, Tag = "PSY", Due = new DateTime(2026, 10, 15) });
            d.Tasks.Add(new TaskItem { Name = "done chapter", Tag = "PSY", Due = new DateTime(2026, 10, 14), Status = Status.Done });
            d.Tasks.Add(new TaskItem { Name = "other class", Tag = "ENG", Due = new DateTime(2026, 10, 15) });
            Is(ExamPart(d, now, false), "PSY midterm in 6 days, 3 study tasks left", "exam");
            Is(ExamPart(d, now, true), "PSY midterm in 6 days, 3 left", "exam short");
            test.Due = new DateTime(2026, 11, 20);
            Is(ExamPart(d, now, false), null, "test too far");
            test.Due = new DateTime(2026, 10, 18);
            test.Type = TaskType.General; test.Name = "PSY exam";
            Is(ExamPart(d, now, false), "PSY exam in 6 days, 3 study tasks left", "exam by name");

            // Accessibility nudges: once a semester per class, once per test inside two weeks.
            d.Classes.Add(new ClassTime { Tag = "ENG", Days = { DayOfWeek.Friday }, Start = new TimeSpan(13, 0, 0), End = new TimeSpan(14, 0, 0) });
            d.AccessRequests.Add(new AccessRequest { Tag = "PSY", Item = "extended test time", Status = AccessStatus.Asked });
            d.AccessRequests.Add(new AccessRequest { Tag = "PSY", Item = "note taker", Status = AccessStatus.ToAsk });
            string? first = Nudge(d, now);
            if (first == null || !first.Contains("PSY, still to ask: note taker") || !first.Contains("ENG, still to ask: accessible materials, extended test time and note taker"))
                throw new Exception("first nudge was " + first);
            if (!first.Contains("PSY exam is Sunday")) throw new Exception("test nudge missing: " + first);
            Is(Nudge(d, now.AddDays(1)), null, "nudge repeats");
            if (Nudge(d, new DateTime(2027, 1, 20)) == null) throw new Exception("no nudge next semester");
            foreach (var r in d.AccessRequests.Where(r => r.Tag == "PSY")) r.Status = AccessStatus.Approved;
            Is(RequestsFor(d, "PSY").Count, 2, "saved requests");
            Is(RequestsFor(d, "BIO").Count, 3, "standard requests");

            // Sounds: packs, seasons, imports.
            foreach (var pack in Sounds.PackNames)
                foreach (Sounds.Event e in Enum.GetValues<Sounds.Event>())
                {
                    string c = Sounds.PackChoice(pack, e);
                    if (c != Sounds.DefaultName && !Sounds.All.Contains(c)) throw new Exception("pack " + pack + " names a missing sound: " + c);
                }
            Is(Sounds.SeasonPack(new DateTime(2026, 10, 12)), "Halloween", "season");
            Is(Sounds.SeasonPack(new DateTime(2026, 7, 4)), null, "no season");
            Is(Sounds.Resolve(Sounds.Event.TaskDone, "None"), Sounds.NoSound, "legacy None");
            var wav = new byte[44 + 4];
            System.Text.Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0); BitConverter.GetBytes(40).CopyTo(wav, 4);
            System.Text.Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(wav, 8); BitConverter.GetBytes(16).CopyTo(wav, 16);
            BitConverter.GetBytes((short)1).CopyTo(wav, 20); BitConverter.GetBytes((short)1).CopyTo(wav, 22);
            BitConverter.GetBytes(8000).CopyTo(wav, 24); BitConverter.GetBytes(16000).CopyTo(wav, 28);
            BitConverter.GetBytes((short)2).CopyTo(wav, 32); BitConverter.GetBytes((short)16).CopyTo(wav, 34);
            System.Text.Encoding.ASCII.GetBytes("data").CopyTo(wav, 36); BitConverter.GetBytes(4).CopyTo(wav, 40);
            BitConverter.GetBytes((short)10000).CopyTo(wav, 44); BitConverter.GetBytes((short)-10000).CopyTo(wav, 46);
            var half = Sounds.ScaleWav(wav, 50);
            Is(BitConverter.ToInt16(half, 44), (short)5000, "scaled wav");
            Is(BitConverter.ToInt16(half, 46), (short)-5000, "scaled wav negative");
            Is(BitConverter.ToInt16(wav, 44), (short)10000, "original wav untouched");
        }
    }

    /// <summary>Settings, Classes (spec 19.5): add, edit and remove class meeting times.</summary>
    internal sealed class ClassesPage : ISettingsPage
    {
        private readonly Store _store;
        private readonly List<ClassTime> _classes;
        private readonly ListBox _list = new();
        private readonly StackPanel _content = new();

        public ClassesPage(Store store)
        {
            _store = store;
            _classes = Store.Clone(store.Data.Classes);
            AutomationProperties.SetName(_list, "Classes");
            _list.MinHeight = 120;
            var label = new Label { Content = "Classes, entered once a semester. Enter edits the chosen one.", Target = _list };
            var add = Layout.Button("Add class...");
            var edit = Layout.Button("Edit class...");
            var remove = Layout.Button("Remove class");
            add.Click += (_, _) => Add();
            edit.Click += (_, _) => Edit();
            remove.Click += (_, _) => Remove();
            _list.KeyDown += (_, e) =>
            {
                if (e.Key == System.Windows.Input.Key.Enter) { e.Handled = true; Edit(); }
                else if (e.Key == System.Windows.Input.Key.Delete) { e.Handled = true; Remove(); }
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(add);
            buttons.Children.Add(edit);
            buttons.Children.Add(remove);
            _content.Children.Add(label);
            _content.Children.Add(_list);
            _content.Children.Add(buttons);
            Fill(0);
        }

        public string Name => "Classes";
        public FrameworkElement Content => _content;

        private Window? Owner => Window.GetWindow(_content);

        private void Fill(int select)
        {
            _list.Items.Clear();
            foreach (var c in _classes) _list.Items.Add(School.ClassLine(c));
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Clamp(select, 0, _list.Items.Count - 1);
        }

        private void Say(string text) { if (Owner != null) Speech.Say(Owner, text, true); }

        private List<string> Tags() => _store.Data.Tags.Select(t => t.Name).Concat(_classes.Select(c => c.Tag)).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        private void Add()
        {
            if (Owner == null) return;
            if (ClassDialog.Show(Owner, Tags(), null) is not ClassTime c) return;
            _classes.Add(c);
            Fill(_classes.Count - 1);
            Say("Added " + School.ClassLine(c) + ".");
        }

        private void Edit()
        {
            if (Owner == null || _list.SelectedIndex < 0) { Say("Choose a class first."); return; }
            int i = _list.SelectedIndex;
            if (ClassDialog.Show(Owner, Tags(), _classes[i]) is not ClassTime c) return;
            _classes[i] = c;
            Fill(i);
            Say("Changed to " + School.ClassLine(c) + ".");
        }

        private void Remove()
        {
            if (_list.SelectedIndex < 0) { Say("Choose a class first."); return; }
            int i = _list.SelectedIndex;
            string line = School.ClassLine(_classes[i]);
            _classes.RemoveAt(i);
            Fill(i);
            Say("Removed " + line + ". Cancel in Settings keeps it.");
        }

        public void Apply()
        {
            _store.Data.Classes = _classes;
            foreach (string tag in _classes.Select(c => c.Tag).Where(t => t.Length > 0))
                if (!_store.Data.Tags.Any(t => t.Name.Equals(tag, StringComparison.OrdinalIgnoreCase))) _store.Data.Tags.Add(new TagInfo { Name = tag });
        }
    }

    /// <summary>The window for one class meeting time: tag, days, start, end and where.</summary>
    internal static class ClassDialog
    {
        public static ClassTime? Show(Window owner, List<string> tags, ClassTime? existing)
        {
            var w = Layout.Dialog(owner, existing == null ? "Add class" : "Edit class");
            var tag = new ComboBox { ItemsSource = tags };
            if (existing != null) tag.SelectedIndex = tags.FindIndex(t => t.Equals(existing.Tag, StringComparison.OrdinalIgnoreCase));
            else if (tags.Count > 0) tag.SelectedIndex = 0;
            var start = new TextBox { Text = existing == null ? "" : School.Clock(existing.Start) };
            var end = new TextBox { Text = existing == null ? "" : School.Clock(existing.End) };
            var where = new TextBox { Text = existing?.Where ?? "" };
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("_Class tag", tag));
            var days = new Dictionary<DayOfWeek, CheckBox>();
            var group = new GroupBox { Header = "Days", Margin = new Thickness(0, 4, 0, 6) };
            AutomationProperties.SetName(group, "Days");
            var dayPanel = new StackPanel { Margin = new Thickness(6) };
            foreach (var d in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday })
            {
                var cb = new CheckBox { Content = d.ToString(), IsChecked = existing?.Days.Contains(d) == true, Margin = new Thickness(0, 2, 0, 2) };
                days[d] = cb;
                dayPanel.Children.Add(cb);
            }
            group.Content = dayPanel;
            panel.Children.Add(group);
            panel.Children.Add(Layout.Row("_Starts at, like 10:15 am", start));
            panel.Children.Add(Layout.Row("_Ends at, like 11:30 am", end));
            panel.Children.Add(Layout.Row("_Where, like Room 204 (optional)", where));
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80, Focusable = false };

            ClassTime? result = null;
            ok.Click += (_, _) =>
            {
                void Problem(string text, Control focus) { Speech.Say(w, text, true); focus.Focus(); }
                if (tag.SelectedItem is not string tg) { Problem(tags.Count == 0 ? "There are no tags yet. Make one in the Tags settings first." : "Pick a class tag.", tag); return; }
                var chosen = days.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();
                if (chosen.Count == 0) { Problem("Check at least one day.", days[DayOfWeek.Monday]); return; }
                if (School.ParseTime(start.Text) is not TimeSpan s) { Problem("I could not read the start time. Try 10:15 am.", start); return; }
                if (School.ParseTime(end.Text) is not TimeSpan e) { Problem("I could not read the end time. Try 11:30 am.", end); return; }
                if (e <= s) { Problem("The class ends before it starts. Check am and pm.", end); return; }
                result = new ClassTime { Tag = tg, Days = chosen, Start = s, End = e, Where = where.Text.Trim() };
                w.DialogResult = true;
            };
            w.Loaded += (_, _) => tag.Focus();
            w.ShowDialog();
            return result;
        }
    }

    /// <summary>Settings, Accessibility (spec 19.6): per class, a checklist of requests and where each stands.</summary>
    internal sealed class AccessPage : ISettingsPage
    {
        private readonly Store _store;
        private readonly List<AccessRequest> _all;
        private readonly ComboBox _tag = new();
        private readonly ListBox _list = new();
        private readonly ComboBox _status = new() { ItemsSource = new[] { "To ask", "Asked", "Approved" } };
        private readonly TextBox _custom = new();
        private readonly StackPanel _content = new();
        private bool _busy;
        private readonly HashSet<string> _seeded = new();

        public AccessPage(Store store)
        {
            _store = store;
            _all = Store.Clone(store.Data.AccessRequests);
            _tag.ItemsSource = School.ClassTags(store.Data).Concat(store.Data.Tags.Select(t => t.Name)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            AutomationProperties.SetName(_list, "Requests");
            _list.MinHeight = 90;
            var addButton = Layout.Button("Add request");
            var removeButton = Layout.Button("Remove request");
            _content.Children.Add(new Label { Content = "Accessibility requests: ask early. Busy Bunny nudges you each semester and two weeks before a test.", Target = _tag });
            _content.Children.Add(Layout.Row("Class _tag", _tag));
            _content.Children.Add(new Label { Content = "Requests", Target = _list });
            _content.Children.Add(_list);
            _content.Children.Add(Layout.Row("_Status of the chosen request", _status));
            _content.Children.Add(Layout.Row("Your own re_quest, like captioned videos", _custom));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(addButton);
            buttons.Children.Add(removeButton);
            _content.Children.Add(buttons);

            _tag.SelectionChanged += (_, _) => Fill(0);
            _list.SelectionChanged += (_, _) =>
            {
                if (_busy || Chosen() is not AccessRequest r) return;
                _busy = true; _status.SelectedIndex = (int)r.Status; _busy = false;
            };
            _status.SelectionChanged += (_, _) =>
            {
                if (_busy || Chosen() is not AccessRequest r) return;
                r.Status = (AccessStatus)_status.SelectedIndex;
                Fill(_list.SelectedIndex);
                Say(r.Item + ": " + School.StatusWords(r.Status) + ".");
            };
            addButton.Click += (_, _) => Add();
            removeButton.Click += (_, _) => Remove();
            if (_tag.Items.Count > 0) _tag.SelectedIndex = 0;
        }

        public string Name => "Accessibility";
        public FrameworkElement Content => _content;

        private void Say(string text) { if (Window.GetWindow(_content) is Window w) Speech.Say(w, text, true); }

        private string? Tag => _tag.SelectedItem as string;
        private List<AccessRequest> Mine() => Tag == null ? new() : _all.Where(r => r.Tag.Equals(Tag, StringComparison.OrdinalIgnoreCase)).ToList();
        private AccessRequest? Chosen() => _list.SelectedIndex >= 0 && _list.SelectedIndex < Mine().Count ? Mine()[_list.SelectedIndex] : null;

        private void Fill(int select)
        {
            if (Tag == null) return;
            if (Mine().Count == 0 && _seeded.Add(Tag.ToLowerInvariant())) _all.AddRange(School.StandardRequests.Select(s => new AccessRequest { Tag = Tag, Item = s }));
            _busy = true;
            _list.Items.Clear();
            foreach (var r in Mine()) _list.Items.Add(r.Item + ", " + School.StatusWords(r.Status));
            if (_list.Items.Count > 0) _list.SelectedIndex = Math.Clamp(select, 0, _list.Items.Count - 1);
            if (Chosen() is AccessRequest c) _status.SelectedIndex = (int)c.Status;
            _busy = false;
        }

        private void Add()
        {
            string item = _custom.Text.Trim();
            if (Tag == null) { Say("Pick a class tag first."); return; }
            if (item.Length == 0) { Say("Type the request first."); _custom.Focus(); return; }
            if (Mine().Any(r => r.Item.Equals(item, StringComparison.OrdinalIgnoreCase))) { Say("That one is already on the list."); return; }
            _all.Add(new AccessRequest { Tag = Tag, Item = item });
            _custom.Text = "";
            Fill(Mine().Count - 1);
            Say("Added " + item + ", to ask.");
        }

        private void Remove()
        {
            if (Chosen() is not AccessRequest r) { Say("Choose a request first."); return; }
            int at = _list.SelectedIndex;
            _all.Remove(r);
            Fill(at);
            Say("Removed " + r.Item + ".");
        }

        public void Apply() => _store.Data.AccessRequests = _all;
    }
}
