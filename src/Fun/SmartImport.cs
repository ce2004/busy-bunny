using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Reading what she pastes, with Bunny (sweep 3.4 and 8.5).
    /// Import a syllabus with Bunny (File menu): with the key set, Bunny pulls out every due date, test and reading;
    /// without it, or for a private list, Busy Bunny finds the dated lines itself. Either way she approves the list in a
    /// checklist before anything is saved, and the whole import is one Control Z step.
    /// Semester setup with Bunny (Plan): she pastes the class schedule and every syllabus at once; Bunny proposes tags,
    /// class times, weekday plans and due dates, and she approves each group before anything is saved.
    /// Only the text she pasted goes to Claude, never her tasks.
    /// </summary>
    public sealed class SmartImport : IFeature
    {
        private const string SyllabusId = "smartsyllabus", SemesterId = "semestersetup";
        private MainWindow _w = null!;
        private bool _busy;

        public void Attach(MainWindow w)
        {
            _w = w;
            Tools.Register(new BunnyTool(SyllabusId, "Import a syllabus with Bunny", Drawers.Plan, "Paste a syllabus; Bunny finds the due dates, tests and readings, and you approve them.", ImportSyllabus) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(SemesterId, "Semester setup with Bunny", Drawers.Plan, "Paste your class schedule and syllabi; Bunny proposes tags, class times, weekday plans and due dates.", SemesterSetup) { Since = "0.14.0" });
            w.AddMenuItem("Bunny", "Import a s_yllabus with Bunny...", Tools.Guard(SyllabusId, ImportSyllabus));
            Tools.NoteMove("Import a syllabus with Bunny: was in the File menu, now in the Bunny menu.");
        }

        // ---- What Bunny proposes ----

        public sealed class Proposal
        {
            public List<TagInfo> Tags = new();
            public List<ClassTime> Classes = new();
            public List<(DayOfWeek Day, string Plan)> Plans = new();
            public List<TaskItem> Tasks = new();
        }

        private const string Shape =
            "Reply with only one JSON object, no other words: {\"tags\":[{\"name\":\"PSY\",\"description\":\"Intro to Psychology\"}]," +
            "\"classes\":[{\"tag\":\"PSY\",\"days\":[\"Monday\",\"Wednesday\"],\"start\":\"10:00\",\"end\":\"10:50\",\"where\":\"room\"}]," +
            "\"weekday_plans\":[{\"day\":\"Monday\",\"plan\":\"short plan\"}]," +
            "\"tasks\":[{\"tag\":\"PSY\",\"name\":\"Chapter 3 reading\",\"due\":\"2026-09-14\",\"kind\":\"reading\"}]}. " +
            "kind is test, assignment or reading. Times are 24 hour. Dates are YYYY-MM-DD: work out the year from today and the term. " +
            "Tags are short class codes in capitals. Task names are short and plain, without the date or the class code. Leave out anything you are not sure of; never invent dates.";

        private static string SyllabusSystem(DateTime now) =>
            "You read a college syllabus and list every dated item in it: due dates, tests, quizzes, exams and readings. Today is " + now.ToString("dddd yyyy-MM-dd", CultureInfo.InvariantCulture) +
            ". Use empty lists for tags, classes and weekday_plans. " + Shape;

        private static string SemesterSystem(DateTime now) =>
            "You set up a student's semester in her to-do app from her class schedule and syllabi. Today is " + now.ToString("dddd yyyy-MM-dd", CultureInfo.InvariantCulture) +
            ". Propose one tag per class, the class meeting times, a short plan for each weekday that has classes (like which class to prepare for that day), and every dated item as a task. " + Shape;

        /// <summary>Reads Claude's answer into what can be added. Anything it cannot read is left out.</summary>
        public static Proposal Parse(string answer, Store store, TaskList list, string defaultTag, DateTime now)
        {
            var p = new Proposal();
            if (SmartText.Json(answer) is not JsonElement root) return p;
            foreach (var t in SmartText.Array(root, "tags"))
            {
                string name = Tag(SmartText.Str(t, "name"));
                if (name.Length > 0 && !p.Tags.Any(x => x.Name == name)) p.Tags.Add(new TagInfo { Name = name, Description = SmartText.Str(t, "description") });
            }
            foreach (var c in SmartText.Array(root, "classes"))
            {
                var days = SmartText.Array(c, "days").Select(d => d.ValueKind == JsonValueKind.String && Enum.TryParse<DayOfWeek>(d.GetString(), true, out var x) ? (DayOfWeek?)x : null)
                    .Where(d => d != null).Select(d => d!.Value).Distinct().ToList();
                if (days.Count == 0 || !TimeSpan.TryParse(SmartText.Str(c, "start"), CultureInfo.InvariantCulture, out var start) || !TimeSpan.TryParse(SmartText.Str(c, "end"), CultureInfo.InvariantCulture, out var end) || end <= start || end.TotalHours >= 24) continue;
                p.Classes.Add(new ClassTime { Tag = Tag(SmartText.Str(c, "tag")), Days = days, Start = start, End = end, Where = SmartText.Str(c, "where") });
            }
            foreach (var d in SmartText.Array(root, "weekday_plans"))
            {
                string plan = SmartText.Str(d, "plan");
                if (plan.Length > 0 && Enum.TryParse<DayOfWeek>(SmartText.Str(d, "day"), true, out var day) && !p.Plans.Any(x => x.Day == day)) p.Plans.Add((day, plan));
            }
            foreach (var t in SmartText.Array(root, "tasks"))
            {
                string name = SmartText.Str(t, "name");
                // Only the date: Claude sometimes adds a time, like 2026-10-20T23:59.
                string dueText = SmartText.Str(t, "due").Trim();
                if (dueText.Length > 10) dueText = dueText[..10];
                if (name.Length == 0 || !DateTime.TryParseExact(dueText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due)) continue;
                string kind = SmartText.Str(t, "kind").ToLowerInvariant();
                bool test = kind == "test";
                string tag = Tag(SmartText.Str(t, "tag"));
                p.Tasks.Add(new TaskItem
                {
                    Name = name, List = list.Name, Tag = tag.Length > 0 ? tag : defaultTag, Due = due.Date,
                    Type = test ? TaskType.Test : TaskType.Assignment,
                    Size = test ? Size.Large : kind == "reading" ? Size.Small : Size.Medium,
                    Status = due.Date <= Store.WeekEnd(now) ? Status.ThisWeek : Status.NextWeek, StatusSince = now,
                });
            }
            return p;
        }

        private static string Tag(string s)
        {
            s = new string(s.Where(ch => char.IsLetterOrDigit(ch)).ToArray()).Trim();
            if (s.Length > 12) s = s[..12].Trim();
            return s.ToUpperInvariant();
        }

        // ---- Adding what she approved: one undo step ----

        /// <summary>Adds what she kept, as one Control Z step. Tags and classes already there are not added twice; a weekday plan replaces that day's.</summary>
        public static int Apply(Store store, Proposal p, string what)
        {
            int n = p.Tags.Count + p.Classes.Count + p.Plans.Count + p.Tasks.Count;
            if (n == 0) return 0;
            store.Remember(what);
            foreach (var t in p.Tags)
            {
                var have = store.Data.Tags.FirstOrDefault(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase));
                if (have == null) store.Data.Tags.Add(t);
                else if (have.Description.Length == 0) have.Description = t.Description;
            }
            foreach (var c in p.Classes)
            {
                if (!store.Data.Classes.Any(x => x.Tag == c.Tag && x.Start == c.Start && x.End == c.End && x.Days.SequenceEqual(c.Days))) store.Data.Classes.Add(c);
                // A class time names a tag even when she skipped the tags group, so the tag must exist.
                if (c.Tag.Length > 0 && !store.Data.Tags.Any(x => x.Name.Equals(c.Tag, StringComparison.OrdinalIgnoreCase))) store.Data.Tags.Add(new TagInfo { Name = c.Tag });
            }
            foreach (var (day, plan) in p.Plans) store.Data.WeekdayPlans[day.ToString()] = plan;
            foreach (var t in p.Tasks)
            {
                store.Data.Tasks.Add(t);
                if (t.Tag.Length > 0 && !store.Data.Tags.Any(x => x.Name.Equals(t.Tag, StringComparison.OrdinalIgnoreCase))) store.Data.Tags.Add(new TagInfo { Name = t.Tag });
            }
            return n;
        }

        // ---- The windows ----

        private List<TaskList> Lists(bool shareableOnly) => _w.Store.Data.Lists.Where(l => !l.Archived && (!shareableOnly || !SmartBunny.ListPrivate(_w.Store, l))).ToList();

        private static ComboBox Picker(IEnumerable<string> items, string? selected)
        {
            var cb = new ComboBox { ItemsSource = items.ToList() };
            cb.SelectedItem = selected;
            if (cb.SelectedIndex < 0) cb.SelectedIndex = 0;
            return cb;
        }

        /// <summary>Whether she already has this task: same list, same name (any case), same due day. Importing twice adds nothing twice.</summary>
        public static bool HaveAlready(Store store, TaskItem t) =>
            store.Data.Tasks.Any(x => x.List == t.List && x.Name.Trim().Equals(t.Name.Trim(), StringComparison.OrdinalIgnoreCase) && x.Due?.Date == t.Due?.Date);

        /// <summary>Whether a found task starts checked: its date has not passed and she does not have it already.</summary>
        public static bool StartsChecked(Store store, TaskItem t, DateTime now) => t.Due >= now.Date && !HaveAlready(store, t);

        private string TaskText(TaskItem t, DateTime now) => _w.Store.Line(t, now) + (HaveAlready(_w.Store, t) ? ", you have it already" : t.Due < now.Date ? ", that date has passed" : "");

        private async void ImportSyllabus()
        {
            if (_busy) { _w.Say("Bunny is still reading the last one.", true); return; }
            var store = _w.Store;
            var lists = Lists(false);
            if (lists.Count == 0) { _w.Say("There is no list to add to.", true); return; }
            var text = SmartUi.Big("Syllabus text", 640, 220);
            var list = Picker(lists.Select(l => l.Name), ImportExport.DefaultList(store).Name);
            var tag = new ComboBox { IsEditable = true, ItemsSource = store.Data.Tags.Select(t => t.Name).ToList() };
            var back = _w.KeepFocus();
            bool ok = SmartUi.Form(_w, "Import a syllabus with Bunny", "_Read it", p =>
            {
                p.Children.Add(SmartUi.Intro(SmartBunny.Ready
                    ? "Paste the syllabus. Bunny reads it and finds every due date, test and reading; you check the list before anything is added. Only the syllabus goes to Claude, and never for a private list."
                    : "Paste the syllabus. Bunny has no Claude key yet, so Busy Bunny finds the lines with dates itself; you check the list before anything is added."));
                p.Children.Add(Layout.Row("_Syllabus", text, 640));
                p.Children.Add(Layout.Row("Class _tag, like PSY", tag, 240));
                p.Children.Add(Layout.Row("Add them to _list", list, 240));
            }, () => text.Text.Trim().Length == 0 ? "There is nothing to read. Paste the syllabus first." : null);
            if (!ok) { back(); return; }
            var now = DateTime.Now;
            var target = lists.First(l => l.Name == (string)list.SelectedItem);
            string defaultTag = Tag(tag.Text ?? "");
            List<TaskItem> found;
            string how;
            if (SmartBunny.Ready && !SmartBunny.ListPrivate(store, target) && !SmartSpend.CapReached())
            {
                _busy = true;
                try
                {
                    string answer = await SmartUi.Waiting(_w, "Bunny is reading", "Bunny is reading the syllabus. This takes a little while. Stop ends it.", "Bunny is still reading.",
                        stop => SmartBunny.AskAsync(SyllabusSystem(now), text.Text.Trim(), 16000, stop));
                    found = Parse(answer, store, target, defaultTag, now).Tasks;
                    // The tag she typed wins over whatever Claude read off the page.
                    if (defaultTag.Length > 0) foreach (var t in found) t.Tag = defaultTag;
                    how = "Bunny found";
                    if (found.Count == 0)
                    {
                        found = ImportExport.ParseSyllabus(text.Text, defaultTag, target, now).Select(f => f.Task).ToList();
                        how = "Busy Bunny found";
                        if (found.Count > 0) _w.Say("Bunny's answer had no dates in it, so Busy Bunny looked for the dated lines itself.", true);
                    }
                }
                catch (Exception e)
                {
                    string why = e is SmartUi.StoppedByHand ? e.Message : SmartBunny.Problem(e);
                    _w.Say(why + " Busy Bunny looked for the dated lines itself instead.", true);
                    found = ImportExport.ParseSyllabus(text.Text, defaultTag, target, now).Select(f => f.Task).ToList();
                    how = "Busy Bunny found";
                }
                finally { _busy = false; }
            }
            else
            {
                if (SmartBunny.Ready && SmartBunny.ListPrivate(store, target)) _w.Say("That list is private, so Bunny did not read the syllabus. Busy Bunny looked for the dated lines itself.", true);
                found = ImportExport.ParseSyllabus(text.Text, defaultTag, target, now).Select(f => f.Task).ToList();
                how = "Busy Bunny found";
            }
            if (found.Count == 0) { _w.Say("No dates turned up in that syllabus, so nothing was added.", true); back(); return; }
            var picked = SmartUi.Checklist(_w, "Import a syllabus, check the list", how + " these. Uncheck anything you do not want; dates that have passed, and tasks you have already, start unchecked.",
                found.Select(t => (TaskText(t, now), StartsChecked(store, t, now))).ToList(), "_Add checked");
            if (picked.Cancelled || picked.Chosen.Count == 0) { _w.Say("Nothing was added.", true); back(); return; }
            int n = Apply(store, new Proposal { Tasks = picked.Chosen.Select(i => found[i]).ToList() }, "importing a syllabus with Bunny");
            _w.SaveAndRefresh();
            back();
            _w.Say("Added " + SmartUi.Count(n, "task") + " to " + target.Name + ". Control Z undoes all of them at once.");
        }

        private async void SemesterSetup()
        {
            if (_busy) { _w.Say("Bunny is still reading the last one.", true); return; }
            if (!SmartBunny.Ready) { _w.Say("Semester setup needs Bunny's Claude key: Bunny, Smarter Bunny settings. Without it, Bunny, Import a syllabus with Bunny still finds the dated lines.", true); return; }
            if (SmartSpend.CapReached()) { _w.Say(new SmartSpend.CapHit(SmartSpend.CapOf(_w.Store).Dollars).Message, true); return; }
            var store = _w.Store;
            var lists = Lists(true);
            if (lists.Count == 0) { _w.Say("Every list is private, so Bunny has nowhere it may put the due dates.", true); return; }
            var text = SmartUi.Big("Class schedule and syllabi", 640, 260);
            var list = Picker(lists.Select(l => l.Name), lists.Any(l => l.Name == "Homework") ? "Homework" : lists[0].Name);
            var back = _w.KeepFocus();
            bool ok = SmartUi.Form(_w, "Semester setup with Bunny", "_Read it", p =>
            {
                p.Children.Add(SmartUi.Intro("Paste your class schedule and every syllabus, all at once. Bunny proposes tags for your classes, class times, a plan for each weekday and every due date. " +
                    "You check each group before anything is saved. Only what you paste goes to Claude."));
                p.Children.Add(Layout.Row("_Schedule and syllabi", text, 640));
                p.Children.Add(Layout.Row("Put due dates on _list", list, 240));
            }, () => text.Text.Trim().Length == 0 ? "There is nothing to read. Paste your schedule and syllabi first." : null);
            if (!ok) { back(); return; }
            var now = DateTime.Now;
            var target = lists.First(l => l.Name == (string)list.SelectedItem);
            Proposal p;
            _busy = true;
            try
            {
                string answer = await SmartUi.Waiting(_w, "Bunny is reading", "Bunny is reading your semester. A long one can take a few minutes. Stop ends it.", "Bunny is still reading your semester.",
                    stop => SmartBunny.AskAsync(SemesterSystem(now), text.Text.Trim(), 32000, stop));
                p = Parse(answer, store, target, "", now);
            }
            catch (Exception e) { _w.Say((e is SmartUi.StoppedByHand ? e.Message : SmartBunny.Problem(e)) + " Nothing changed.", true); back(); return; }
            finally { _busy = false; }
            if (p.Tags.Count + p.Classes.Count + p.Plans.Count + p.Tasks.Count == 0) { _w.Say("Bunny could not find a schedule or dates in that. Nothing changed. Bunny, Import a syllabus with Bunny can read one syllabus at a time.", true); back(); return; }

            var keep = new Proposal();
            bool Group<T>(string title, string intro, List<T> items, Func<T, string> line, Func<T, bool> on, List<T> into)
            {
                if (items.Count == 0) return true;
                var r = SmartUi.Checklist(_w, title, intro, items.Select(i => (line(i), on(i))).ToList(), "_Use checked", canSkip: true);
                if (r.Cancelled) return false;
                into.AddRange(r.Chosen.Select(i => items[i]));
                return true;
            }
            string ClassLine(ClassTime c) => c.Tag + ", " + string.Join(" and ", c.Days) + ", " + Words.Time(DateTime.Today.Add(c.Start)) + " to " + Words.Time(DateTime.Today.Add(c.End)) + (c.Where.Length > 0 ? ", " + c.Where : "");
            string PlanLine((DayOfWeek Day, string Plan) x) => x.Day + ": " + x.Plan + (store.Data.WeekdayPlans.TryGetValue(x.Day.ToString(), out var old) && old.Length > 0 ? ". Replaces: " + old : "");
            bool done =
                Group("Semester setup, 1 of 4: tags", "Tags for your classes.", p.Tags, t => t.Name + (t.Description.Length > 0 ? ", " + t.Description : "") + (store.Data.Tags.Any(x => x.Name.Equals(t.Name, StringComparison.OrdinalIgnoreCase)) ? ", you have it already" : ""), _ => true, keep.Tags)
                && Group("Semester setup, 2 of 4: class times", "When your classes meet. Bunny's check-ins and fun lines stay quiet during them.", p.Classes, ClassLine, _ => true, keep.Classes)
                && Group("Semester setup, 3 of 4: weekday plans", "A plan for each weekday, said with your morning check-in.", p.Plans, PlanLine, x => !store.Data.WeekdayPlans.TryGetValue(x.Day.ToString(), out var old) || old.Length == 0, keep.Plans)
                && Group("Semester setup, 4 of 4: due dates", "Each becomes a task on " + target.Name + ". Dates that have passed, and tasks you have already, start unchecked.", p.Tasks, t => TaskText(t, now), t => StartsChecked(store, t, now), keep.Tasks);
            if (!done) { _w.Say("Semester setup cancelled. Nothing changed.", true); back(); return; }
            int n = Apply(store, keep, "semester setup with Bunny");
            if (n == 0) { _w.Say("Nothing was checked, so nothing changed.", true); back(); return; }
            _w.SaveAndRefresh();
            back();
            _w.Say("Semester set up: " + string.Join(", ", new[] { (keep.Tags.Count, "tag"), (keep.Classes.Count, "class time"), (keep.Plans.Count, "weekday plan"), (keep.Tasks.Count, "task") }
                .Where(x => x.Item1 > 0).Select(x => SmartUi.Count(x.Item1, x.Item2))) + ". Control Z undoes all of it at once.");
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Semester setup: " + what); }
            var now = new DateTime(2026, 9, 7, 9, 0, 0);
            var list = ImportExport.DefaultList(store);
            string answer = "Here you go: {\"tags\":[{\"name\":\"psy 101\",\"description\":\"Intro **Psych**\"},{\"name\":\"PSY101\"}]," +
                "\"classes\":[{\"tag\":\"PSY101\",\"days\":[\"Monday\",\"wednesday\",\"Funday\"],\"start\":\"10:00\",\"end\":\"10:50\",\"where\":\"Hall 2\"},{\"tag\":\"X\",\"days\":[\"Monday\"],\"start\":\"11:00\",\"end\":\"10:00\"}]," +
                "\"weekday_plans\":[{\"day\":\"Monday\",\"plan\":\"Read for PSY\"}]," +
                "\"tasks\":[{\"tag\":\"PSY101\",\"name\":\"Midterm\",\"due\":\"2026-10-20\",\"kind\":\"test\"},{\"name\":\"Chapter 1\",\"due\":\"2026-09-08\",\"kind\":\"reading\"},{\"name\":\"No date\",\"due\":\"soon\"}]} Enjoy.";
            var p = Parse(answer, store, list, "GEN", now);
            Need(p.Tags.Count == 1 && p.Tags[0].Name == "PSY101" && p.Tags[0].Description == "Intro Psych", "tags " + p.Tags.Count);
            Need(!p.Tags.Any(t => t.Description.Contains((char)42)), "Claude's marks reached a tag");
            Need(p.Classes.Count == 1 && p.Classes[0].Days.Count == 2 && p.Classes[0].Start == new TimeSpan(10, 0, 0), "class times");
            Need(p.Plans.Count == 1 && p.Plans[0].Day == DayOfWeek.Monday, "weekday plans");
            Need(p.Tasks.Count == 2 && p.Tasks[0].Type == TaskType.Test && p.Tasks[0].Size == Size.Large && p.Tasks[1].Tag == "GEN" && p.Tasks[1].Size == Size.Small, "tasks");
            Need(p.Tasks[1].Status == Status.ThisWeek && p.Tasks[0].Status == Status.NextWeek, "statuses by week");
            Need(Parse("no json here", store, list, "", now).Tasks.Count == 0, "an answer with no JSON");
            int tasks = store.Data.Tasks.Count, classes = store.Data.Classes.Count;
            Need(Apply(store, p, "test") == p.Tags.Count + 1 + 1 + 2, "apply count");
            Need(store.Data.Tasks.Count == tasks + 2 && store.Data.Classes.Count == classes + 1 && store.Data.WeekdayPlans["Monday"] == "Read for PSY", "apply");
            Apply(store, new Proposal { Classes = p.Classes.ToList() }, "again");
            Need(store.Data.Classes.Count == classes + 1, "a class was added twice");
            store.Undo();
            store.Undo();
            Need(store.Data.Tasks.Count == tasks && store.Data.Classes.Count == classes, "one Control Z did not take the setup back");
            Need(Apply(store, new Proposal(), "nothing") == 0, "an empty setup made an undo step");
            // A due date with a time still counts.
            var timed = Parse("{\"tasks\":[{\"name\":\"Essay\",\"due\":\"2026-10-20T23:59\"},{\"name\":\"Quiz\",\"due\":\"2026-10-21 09:00\"}]}", store, list, "", now);
            Need(timed.Tasks.Count == 2 && timed.Tasks[0].Due == new DateTime(2026, 10, 20), "a due date with a time was dropped");
            // Class times keep their tag even when she skipped the tags group.
            Apply(store, new Proposal { Classes = new List<ClassTime> { new() { Tag = "BIO205", Days = new List<DayOfWeek> { DayOfWeek.Friday }, Start = new TimeSpan(9, 0, 0), End = new TimeSpan(10, 0, 0) } } }, "classes only");
            Need(store.Data.Tags.Any(t => t.Name == "BIO205"), "a class time named a tag that does not exist");
            store.Undo();
            // Importing twice: what she has already starts unchecked.
            var twice = new TaskItem { Name = "Final paper", List = list.Name, Due = new DateTime(2026, 12, 10) };
            Need(StartsChecked(store, twice, now), "a new task started unchecked");
            Apply(store, new Proposal { Tasks = new List<TaskItem> { twice } }, "first import");
            var again = new TaskItem { Name = "final PAPER", List = list.Name, Due = new DateTime(2026, 12, 10, 23, 0, 0) };
            Need(HaveAlready(store, again) && !StartsChecked(store, again, now), "a task she has already started checked");
            Need(!HaveAlready(store, new TaskItem { Name = "Final paper", List = list.Name, Due = new DateTime(2026, 12, 11) }), "a different date counted as the same task");
            store.Undo();
            Need(Tools.Find(SyllabusId) != null && Tools.Find(SemesterId) != null, "not in the toolbox");
        }
    }
}
