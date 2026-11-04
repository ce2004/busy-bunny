using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;

namespace BusyBunny
{
    /// <summary>
    /// What her own history says, worked out on this computer (sweep 3.4, 8.5 and 12.6 items 7 and 10):
    /// learning real times (sprint minutes against the size she picked; after 5 finished tasks of a type or tag it
    /// suggests a better size once), her best hours and days (after two weeks of data), what changed since a date,
    /// and a monthly report to share where she ticks exactly what goes in. Nothing here goes to Claude.
    /// </summary>
    public sealed class SmartInsights : IFeature
    {
        private const string TimesId = "realtimes", PatternsId = "besthours", ChangedId = "whatchanged", ReportId = "monthlyreport", ToldKey = "smart.times.told";
        public const int Enough = 5;
        private MainWindow _w = null!;

        public sealed class Told { public HashSet<string> Keys { get; set; } = new(); }

        public void Attach(MainWindow w)
        {
            _w = w;
            Tools.Register(new BunnyTool(TimesId, "How long things really take", Drawers.Wins, "Your sprint minutes against the sizes you picked. After 5 of a kind, a better guess.", ShowTimes) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(PatternsId, "Your best hours and days", Drawers.Wins, "When you get the most done, from your history, after two weeks.", ShowPatterns) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(ChangedId, "What changed this week", Drawers.Plan, "New, moved and finished tasks since a day you pick, for when you have been away.", ShowChanged) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(ReportId, "Monthly report to share", Drawers.Wins, "A plain text summary of a month; you tick exactly what goes in, then copy or save it.", ShowReport) { Since = "0.14.0" });
            GameEvents.TaskDone += t =>
            {
                if (Calm.FunPaused || !Tools.IsOn(TimesId)) return;
                if (Suggestion(w.Store, t) is string line) Calm.AfterTaskDone(w, Game.Chatter, line);
            };
        }

        // ---- Learning real times ----

        public sealed record Group(string Key, string Label, Size Size, List<int> Minutes)
        {
            public int Median => Minutes.Count == 0 ? 0 : Minutes.OrderBy(m => m).ElementAt(Minutes.Count / 2);
            public int Guess => (int)Math.Round(Words.Hours(Size) * 60);
            /// <summary>Off by a real amount: 30 percent and at least 10 minutes.</summary>
            public bool Off => Minutes.Count >= Enough && Math.Abs(Median - Guess) >= Math.Max(10, Guess * 0.3);
            public Size Better => new[] { Size.Small, Size.Medium, Size.Large }.OrderBy(s => Math.Abs(Words.Hours(s) * 60 - Median)).First();
        }

        private static string Plural(TaskType t) => t switch
        {
            TaskType.Assignment => "assignments", TaskType.Test => "tests", TaskType.Meeting => "meetings", TaskType.Event => "events", TaskType.PersonalGoal => "personal goals", _ => "tasks",
        };

        /// <summary>Finished tasks with sprint time, grouped by size and type and by size and tag. Multi-day tasks are left out.</summary>
        public static List<Group> Groups(Store s)
        {
            var minutes = s.Data.Log.Where(e => e.Kind == "Sprint" && e.TaskId != null).GroupBy(e => e.TaskId!.Value).ToDictionary(g => g.Key, g => g.Sum(e => e.Minutes));
            var done = s.Data.Tasks.Where(t => t.Status == Status.Done && t.Size != Size.MultiDay && minutes.GetValueOrDefault(t.Id) > 0).ToList();
            var groups = new List<Group>();
            foreach (var g in done.GroupBy(t => (t.Size, t.Type)))
                groups.Add(new Group("type:" + g.Key.Size + ":" + g.Key.Type, Words.Of(g.Key.Size).ToLowerInvariant() + " " + Plural(g.Key.Type), g.Key.Size, g.Select(t => minutes[t.Id]).ToList()));
            foreach (var g in done.Where(t => t.Tag.Length > 0).GroupBy(t => (t.Size, Tag: t.Tag.ToUpperInvariant())))
                groups.Add(new Group("tag:" + g.Key.Size + ":" + g.Key.Tag, Words.Of(g.Key.Size).ToLowerInvariant() + " " + g.Key.Tag + " tasks", g.Key.Size, g.Select(t => minutes[t.Id]).ToList()));
            return groups.OrderByDescending(g => g.Minutes.Count).ToList();
        }

        private static string Minutes(int m) => m < 60 ? SmartUi.Count(m, "minute") : m % 60 == 0 ? SmartUi.Count(m / 60, "hour") : m / 60 + " hours " + m % 60 + " minutes";

        public static string Line(Group g) => SmartText.Cap(SmartText.Fill(Content.Lines("smart.estimate")[Math.Abs(g.Key.GetHashCode() % Content.Lines("smart.estimate").Count)],
            ("what", g.Label), ("real", Minutes(g.Median)), ("guess", Minutes(g.Guess)), ("better", Words.Of(g.Better).ToLowerInvariant()), ("count", g.Minutes.Count.ToString())));

        /// <summary>
        /// The suggestion a finished task earns: when a group it belongs to has just enough tasks and is off, once per
        /// group ever (kept, so undo never repeats it). Null otherwise.
        /// </summary>
        public static string? Suggestion(Store s, TaskItem t)
        {
            var told = s.World.GetKept<Told>(ToldKey);
            var g = Groups(s).FirstOrDefault(g => g.Off && !told.Keys.Contains(g.Key) && g.Size == t.Size
                && (g.Key == "type:" + t.Size + ":" + t.Type || g.Key == "tag:" + t.Size + ":" + t.Tag.ToUpperInvariant()));
            if (g == null) return null;
            told.Keys.Add(g.Key);
            s.World.SetKept(ToldKey, told);
            s.World.Save();
            return Content.Pick(s, "smart.estimate") is string tpl && tpl.Length > 0
                ? SmartText.Cap(SmartText.Fill(tpl, ("what", g.Label), ("real", Minutes(g.Median)), ("guess", Minutes(g.Guess)), ("better", Words.Of(g.Better).ToLowerInvariant()), ("count", g.Minutes.Count.ToString())))
                : null;
        }

        private void ShowTimes()
        {
            var groups = Groups(_w.Store);
            var lines = new List<string>();
            if (groups.Count == 0) lines.Add("Nothing to compare yet. Start a sprint on a task (Control Shift T), finish the task, and its sprint minutes count here.");
            else
            {
                lines.Add("Sprint minutes on finished tasks, against the size you picked. Small is about 30 minutes, medium an hour, large two hours.");
                foreach (var g in groups)
                    lines.Add(g.Minutes.Count < Enough
                        ? char.ToUpper(g.Label[0]) + g.Label[1..] + ": " + SmartUi.Count(g.Minutes.Count, "task") + " so far, about " + Minutes(g.Median) + " each. " + (Enough - g.Minutes.Count) + " more before a guess."
                        : g.Off ? Line(g) : char.ToUpper(g.Label[0]) + g.Label[1..] + ": about " + Minutes(g.Median) + " over " + g.Minutes.Count + " tasks, right where the size says.");
            }
            Viewer("How long things really take", lines);
        }

        // ---- Best hours and days ----

        private static string Part(int h) => h >= 5 && h < 12 ? "mornings" : h >= 12 && h < 17 ? "afternoons" : h >= 17 && h < 22 ? "evenings" : "late nights";
        private static string Hour(int h) => Words.Time(DateTime.Today.AddHours(h));

        /// <summary>Her best hours, days and times of the week, or why it has to wait. From every finished task logged here.</summary>
        public static List<string> Patterns(Store s, DateTime now)
        {
            var done = s.Data.Log.Where(e => e.Kind == "Done" && e.When <= now && e.When > now.AddDays(-180)).ToList();
            if (done.Count == 0 || done.Min(e => e.When) > now.AddDays(-14))
            {
                int days = done.Count == 0 ? 0 : (int)(now - done.Min(e => e.When)).TotalDays;
                return new List<string> { "Two weeks of finished tasks make the patterns worth hearing. " + (done.Count == 0 ? "Nothing is logged yet." : "You have " + SmartUi.Count(days, "day") + " so far; check back in " + SmartUi.Count(14 - days, "day") + ".") };
            }
            var lines = new List<string> { SmartText.Fill(Content.Pick(s, "smart.patterns"), ("count", done.Count.ToString())) };
            var hours = done.GroupBy(e => e.When.Hour).OrderByDescending(g => g.Count()).Take(3).ToList();
            lines.Add("Best hours: " + string.Join(", ", hours.Select(g => Hour(g.Key) + " to " + Hour((g.Key + 1) % 24) + ", " + SmartUi.Count(g.Count(), "task"))) + ".");
            // Per weekday, averaged over how many of that weekday there were, so one busy Tuesday does not win.
            var first = done.Min(e => e.When).Date;
            int Weeks(DayOfWeek d) => Math.Max(1, Enumerable.Range(0, (now.Date - first).Days + 1).Count(i => first.AddDays(i).DayOfWeek == d));
            var days2 = done.GroupBy(e => e.When.DayOfWeek).Select(g => (Day: g.Key, Avg: g.Count() / (double)Weeks(g.Key))).OrderByDescending(x => x.Avg).ToList();
            lines.Add("Best days: " + string.Join(", ", days2.Take(3).Select(x => x.Day + ", about " + Math.Round(x.Avg, 1).ToString(CultureInfo.InvariantCulture) + " a day")) + ".");
            if (days2.Count > 3) lines.Add("Quietest day: " + days2[^1].Day + ". Nothing wrong with that; rest counts.");
            var slot = done.GroupBy(e => (e.When.DayOfWeek, Part(e.When.Hour))).OrderByDescending(g => g.Count()).First();
            lines.Add("Your strongest time of the week: " + slot.Key.DayOfWeek + " " + slot.Key.Item2 + ", " + SmartUi.Count(slot.Count(), "task") + ". A good spot for something brain heavy.");
            return lines;
        }

        private void ShowPatterns() => Viewer("Your best hours and days", Patterns(_w.Store, DateTime.Now));

        // ---- What changed ----

        public static List<string> Changes(Store s, DateTime since, DateTime now)
        {
            var live = s.Data.Tasks.Where(t => !s.ListOf(t).Archived).ToList();
            var added = live.Where(t => t.CreatedAt >= since).OrderBy(t => t.CreatedAt).ToList();
            var done = live.Where(t => t.DoneAt >= since && t.Status == Status.Done || t.Status == Status.DoneNotSubmitted && t.StatusSince >= since).OrderBy(t => t.DoneAt ?? t.StatusSince).ToList();
            var moved = live.Where(t => t.StatusSince >= since && t.CreatedAt < since && !done.Contains(t)).OrderBy(t => t.StatusSince).ToList();
            string When(DateTime? d) => d is DateTime x ? Words.Day(x, now) == "today" ? "today" : x.ToString("dddd, MMMM d") : "";
            var lines = new List<string> { "Since " + since.ToString("dddd, MMMM d") + ": " + SmartUi.Count(added.Count, "new task") + ", " + SmartUi.Count(moved.Count, "moved task") + ", " + SmartUi.Count(done.Count, "finished task") + "." };
            lines.Add("New: " + SmartUi.Count(added.Count, "task"));
            lines.AddRange(added.Select(t => t.Name + ", " + t.List + ", added " + When(t.CreatedAt) + ", now " + Words.Of(t.Status) + (t.Due is DateTime d ? ", due " + Words.Day(d, now) : "")));
            lines.Add("Moved: " + SmartUi.Count(moved.Count, "task"));
            lines.AddRange(moved.Select(t => t.Name + ", " + t.List + ", moved to " + Words.Of(t.Status) + " " + When(t.StatusSince) + (t.Due is DateTime d ? ", due " + Words.Day(d, now) : "")));
            lines.Add("Finished: " + SmartUi.Count(done.Count, "task"));
            lines.AddRange(done.Select(t => t.Name + ", " + t.List + ", " + (t.Status == Status.Done ? "done " : "done, not submitted ") + When(t.DoneAt ?? t.StatusSince)));
            return lines;
        }

        private void ShowChanged()
        {
            var now = DateTime.Now;
            var choices = new List<(string Name, DateTime From)>
            {
                ("Since Monday", Engine.WeekStart(now)), ("The last 7 days", now.Date.AddDays(-7)), ("The last 14 days", now.Date.AddDays(-14)), ("The last 30 days", now.Date.AddDays(-30)),
            };
            var pick = new ComboBox { ItemsSource = choices.Select(c => c.Name).Concat(new[] { "Since a day I type below" }).ToList(), SelectedIndex = 0 };
            var day = new TextBox();
            DateTime since = choices[0].From;
            var back = _w.KeepFocus();
            bool ok = SmartUi.Form(_w, "What changed", "Sh_ow", p =>
            {
                p.Children.Add(Layout.Row("_Since", pick, 260));
                p.Children.Add(Layout.Row("_Day, like october 28 or last friday", day, 260));
            }, () =>
            {
                if (pick.SelectedIndex < choices.Count) { since = choices[pick.SelectedIndex].From; return null; }
                var (when, _) = Dates.Parse(day.Text, now);
                if (when == null) return "Type a day, like october 28 or last friday.";
                since = when.Value.Date > now ? when.Value.Date.AddYears(-1) : when.Value.Date;
                return null;
            });
            if (!ok) { back(); return; }
            TextViewer.Show(_w, "What changed", Changes(_w.Store, since, now), l => l.StartsWith("New: ") || l.StartsWith("Moved: ") || l.StartsWith("Finished: "));
            back();
        }

        // ---- Monthly report ----

        public enum Part2 { Finished, ByClass, Names, Streaks, Focus, Hard, Level, Coming }

        public static readonly (Part2 Part, string Label, bool On)[] Parts =
        {
            (Part2.Finished, "How many tasks I finished", true),
            (Part2.ByClass, "Finished, by class or tag", true),
            (Part2.Names, "The names of the tasks I finished", false),
            (Part2.Streaks, "Streaks, counted from all my lists", true),
            (Part2.Focus, "Focused sprint time", true),
            (Part2.Hard, "What got hard: rough days, tasks pushed back, overdue now", true),
            (Part2.Level, "Level and badges, counted from all my lists", false),
            (Part2.Coming, "Big due dates coming next month", false),
        };

        /// <summary>The report as plain text, with only the parts she ticked. Private lists only when she ticked that too.</summary>
        public static string Report(Store s, DateTime month, ISet<Part2> parts, bool includePrivate, DateTime now)
        {
            var from = new DateTime(month.Year, month.Month, 1);
            var to = from.AddMonths(1);
            // A name that matches no list now (a list renamed or deleted since, maybe a private one) stays out unless she ticked private lists.
            bool Ok(string list) => includePrivate || s.Data.Lists.FirstOrDefault(l => l.Name == list) is TaskList l && !SmartBunny.ListPrivate(s, l);
            var tasks = s.Data.Tasks.Where(t => Ok(t.List)).ToList();
            var done = tasks.Where(t => t.DoneAt >= from && t.DoneAt < to && t.Status == Status.Done).ToList();
            var log = s.Data.Log.Where(e => e.When >= from && e.When < to && (e.List.Length == 0 || Ok(e.List))).ToList();
            var sb = new StringBuilder();
            sb.AppendLine("Monthly report, " + from.ToString("MMMM yyyy", CultureInfo.InvariantCulture));
            sb.AppendLine("Made with Busy Bunny on " + now.ToString("MMMM d, yyyy", CultureInfo.InvariantCulture) + ".");
            void Section(string head, IEnumerable<string> lines) { sb.AppendLine(); sb.AppendLine(head); foreach (var l in lines) sb.AppendLine(l); }
            if (parts.Contains(Part2.Finished))
                Section("Finished", new[] { SmartUi.Count(done.Count, "task") + " finished this month, on " + SmartUi.Count(done.Select(t => t.DoneAt!.Value.Date).Distinct().Count(), "day") + "." });
            if (parts.Contains(Part2.ByClass))
            {
                var tags = done.GroupBy(t => t.Tag.Length > 0 ? t.Tag : "No tag", StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Select(g => g.Key + ": " + g.Count());
                Section("By class or tag", done.Count == 0 ? new[] { "Nothing finished yet." } : tags);
            }
            if (parts.Contains(Part2.Names))
                Section("Tasks finished", done.Count == 0 ? new[] { "None yet." } : done.OrderBy(t => t.DoneAt).Select(t => t.DoneAt!.Value.ToString("MMMM d") + ": " + t.Name));
            if (parts.Contains(Part2.Streaks))
            {
                var end = to.AddDays(-1) < now ? to.AddDays(-1) : now;
                var (current, longest) = Engine.Streaks(s.Data, end, from);
                Section("Streaks", new[] { "Longest streak this month: " + SmartUi.Count(longest, "day") + ".", "Streak at the end of it: " + SmartUi.Count(current, "day") + "." });
            }
            if (parts.Contains(Part2.Focus))
            {
                var sprints = log.Where(e => e.Kind == "Sprint").ToList();
                int m = sprints.Sum(e => e.Minutes);
                Section("Focused time", new[] { SmartUi.Count(sprints.Count, "sprint") + ", " + m / 60 + " hours " + m % 60 + " minutes in all." });
            }
            if (parts.Contains(Part2.Hard))
            {
                int rough = s.Data.Days.Count(kv => DateTime.TryParse(kv.Key, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) && d >= from && d < to && kv.Value.Mode == DayMode.RoughDay);
                var pushed = tasks.Where(t => t.Snoozes >= 2 && t.Status != Status.Done).OrderByDescending(t => t.Snoozes).Take(5).ToList();
                int overdue = tasks.Count(t => Store.IsOverdue(t, now));
                var lines = new List<string> { "Rough days marked: " + rough + "." };
                lines.Add(pushed.Count == 0 ? "Nothing was pushed back again and again." : "Pushed back the most: " + string.Join("; ", pushed.Select(t => t.Name + ", " + SmartUi.Count(t.Snoozes, "time"))) + ".");
                lines.Add("Overdue right now: " + SmartUi.Count(overdue, "task") + ".");
                Section("What got hard", lines);
            }
            if (parts.Contains(Part2.Level))
            {
                int total = Engine.Total(s.Data), level = Engine.LevelFor(total);
                int badges = s.Data.Game.Badges.Count(b => b.Value >= from && b.Value < to);
                Section("Level and badges", new[] { "Level " + level + ", " + Engine.LevelName(level) + ". " + SmartUi.Count(badges, "badge") + " earned this month." });
            }
            if (parts.Contains(Part2.Coming))
            {
                var big = tasks.Where(t => t.Status != Status.Done && t.Due >= to && t.Due < to.AddMonths(1) && (t.Type == TaskType.Test || t.Size is Size.Large or Size.MultiDay || t.GradeWeight >= 10)).OrderBy(t => t.Due).Take(10).ToList();
                Section("Coming next month", big.Count == 0 ? new[] { "No big due dates yet." } : big.Select(t => t.Due!.Value.ToString("MMMM d") + ": " + t.Name));
            }
            return sb.ToString().Replace(((char)42).ToString(), "").Replace(((char)35).ToString(), "");
        }

        private void ShowReport()
        {
            var now = DateTime.Now;
            var months = new[] { now, now.AddMonths(-1), now.AddMonths(-2) };
            var month = new ComboBox { ItemsSource = months.Select((m, i) => (i == 0 ? "This month, " : i == 1 ? "Last month, " : "") + m.ToString("MMMM yyyy", CultureInfo.InvariantCulture)).ToList(), SelectedIndex = now.Day <= 7 ? 1 : 0 };
            var boxes = Parts.Select(p => new CheckBox { Content = p.Label, IsChecked = p.On, Margin = new Thickness(0, 3, 0, 3) }).ToList();
            var priv = new CheckBox { Content = "Include my private lists", IsChecked = false, Margin = new Thickness(0, 6, 0, 3) };
            var preview = SmartUi.ReadOnly("The report", "", 600, 220);
            string Make() => Report(_w.Store, months[Math.Max(0, month.SelectedIndex)], Parts.Where((p, i) => boxes[i].IsChecked == true).Select(p => p.Part).ToHashSet(), priv.IsChecked == true, now);
            var back = _w.KeepFocus();
            SmartUi.Form(_w, "Monthly report to share", "Ma_ke the report", p =>
            {
                p.Children.Add(SmartUi.Intro("Tick exactly what goes in, then make the report. You can copy it or save it as a text file to bring to a meeting.", 600));
                p.Children.Add(Layout.Row("_Month", month, 260));
                var group = new StackPanel();
                foreach (var b in boxes) group.Children.Add(b);
                group.Children.Add(priv);
                p.Children.Add(new GroupBox { Header = "What goes in", Content = group, Width = 600, Margin = new Thickness(0, 4, 0, 6) });
                p.Children.Add(Layout.Row("The _report", preview, 600));
            }, () =>
            {
                // Make the report and stay open, so she can read, copy or save it.
                preview.Text = Make();
                preview.Focus();
                preview.CaretIndex = 0;
                Speech.Say(preview, "The report is in the box. Copy or Save as are the next buttons.", true);
                return "";
            },
            ("_Copy", win =>
            {
                if (preview.Text.Length == 0) preview.Text = Make();
                try { Clipboard.SetText(preview.Text); Speech.Say(win, "Copied the report.", true); }
                catch { Speech.Say(win, "The clipboard would not open. Try again.", true); }
            }),
            ("Save _as...", win =>
            {
                if (preview.Text.Length == 0) preview.Text = Make();
                var d = new SaveFileDialog { Title = "Save the monthly report", Filter = "Text files (*.txt)|*.txt", FileName = "Monthly report " + months[Math.Max(0, month.SelectedIndex)].ToString("yyyy-MM", CultureInfo.InvariantCulture) + ".txt" };
                if (d.ShowDialog(win) != true) return;
                try { File.WriteAllText(d.FileName, preview.Text, new UTF8Encoding(false)); Speech.Say(win, "Saved " + Path.GetFileName(d.FileName) + ".", true); }
                catch (Exception e) { Speech.Say(win, "Could not save: " + e.Message, true); }
            }));
            back();
        }

        private void Viewer(string title, List<string> lines)
        {
            var back = _w.KeepFocus();
            TextViewer.Show(_w, title, lines);
            back();
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Insights: " + what); }
            var now = new DateTime(2026, 11, 4, 20, 0, 0);
            // Five small PSY assignments that each took about 50 minutes of sprints: small says 30.
            var made = new List<TaskItem>();
            for (int i = 0; i < Enough; i++)
            {
                var t = new TaskItem { Name = "reading " + i, List = store.Data.Lists[0].Name, Tag = "PSY", Size = Size.Small, Type = TaskType.Assignment, Status = Status.Done, DoneAt = now.AddDays(-i) };
                store.Data.Tasks.Add(t);
                made.Add(t);
                store.Data.Log.Add(new LogEntry { Kind = "Sprint", When = now.AddDays(-i), Minutes = 25, TaskId = t.Id });
                store.Data.Log.Add(new LogEntry { Kind = "Sprint", When = now.AddDays(-i), Minutes = i == 0 ? 15 : 25, TaskId = t.Id });
                if (i == Enough - 2) Need(Suggestion(store, t) == null, "a suggestion came before " + Enough + " tasks");
            }
            var g = Groups(store).First(x => x.Key == "tag:Small:PSY");
            Need(g.Minutes.Count == Enough && g.Median == 50 && g.Guess == 30 && g.Off && g.Better == Size.Medium, "the group gave " + g.Median + " against " + g.Guess);
            string? said = Suggestion(store, made[^1]);
            Need(said != null && !said.Contains('{') && said.Contains("50 minutes"), "the suggestion was " + said);
            // The same tasks are also small assignments: that group is suggested once too, then nothing more.
            Need(Suggestion(store, made[^1]) != null, "the second group was not suggested");
            Need(Suggestion(store, made[^1]) == null, "a group was suggested twice");
            Need(Content.Lines("smart.estimate").All(l => l.Contains("{what}") && l.Contains("{real}")), "estimate lines need {what} and {real}");

            // Patterns wait for two weeks.
            var s2 = new Store { PathOverride = Path.Combine(Path.GetTempPath(), "busybunny-insights-" + Guid.NewGuid().ToString("N") + ".json") };
            try
            {
                s2.Data.Log.Add(new LogEntry { Kind = "Done", When = now.AddDays(-3) });
                Need(Patterns(s2, now).Count == 1 && Patterns(s2, now)[0].Contains("11 days"), "patterns did not wait: " + Patterns(s2, now)[0]);
                for (int i = 0; i < 20; i++) s2.Data.Log.Add(new LogEntry { Kind = "Done", When = new DateTime(2026, 10, 13, 19, 0, 0).AddDays(7 * (i % 3)).AddMinutes(i) });
                var p = Patterns(s2, now);
                Need(p.Any(l => l.StartsWith("Best hours: 7 pm")) && p.Any(l => l.Contains("Tuesday evenings")), "patterns gave " + string.Join(" / ", p));

                // What changed.
                var since = now.Date.AddDays(-2);
                s2.Data.Lists = Sample.Lists();
                s2.Data.Tasks.Add(new TaskItem { Name = "new one", List = s2.Data.Lists[0].Name, CreatedAt = now.AddDays(-1), StatusSince = now.AddDays(-1) });
                s2.Data.Tasks.Add(new TaskItem { Name = "old moved", List = s2.Data.Lists[0].Name, CreatedAt = now.AddDays(-20), StatusSince = now.AddDays(-1), Status = Status.Today });
                s2.Data.Tasks.Add(new TaskItem { Name = "old done", List = s2.Data.Lists[0].Name, CreatedAt = now.AddDays(-20), StatusSince = now, DoneAt = now, Status = Status.Done });
                s2.Data.Tasks.Add(new TaskItem { Name = "old quiet", List = s2.Data.Lists[0].Name, CreatedAt = now.AddDays(-20), StatusSince = now.AddDays(-9) });
                var c = Changes(s2, since, now);
                Need(c[0].Contains("1 new task, 1 moved task, 1 finished task") && !c.Any(l => l.StartsWith("old quiet")), "what changed gave " + string.Join(" / ", c));

                // The report holds only what she ticked, and private lists only when asked.
                s2.Data.Lists.Add(new TaskList { Name = "ZZPRIVATE", PrivateFromBunny = true });
                s2.Data.Tasks.Add(new TaskItem { Name = "ZZSECRET", List = "ZZPRIVATE", Status = Status.Done, DoneAt = now, CreatedAt = now.AddDays(-20) });
                var names = new HashSet<Part2> { Part2.Finished, Part2.Names };
                string r = Report(s2, now, names, false, now);
                Need(r.Contains("1 task finished") && r.Contains("old done") && !r.Contains("ZZSECRET") && !r.Contains("Streaks"), "the report gave " + r);
                Need(Report(s2, now, names, true, now).Contains("ZZSECRET"), "private lists did not go in when ticked");
                // Sprints logged under a list name that no longer exists (a private list renamed later) stay out.
                s2.Data.Log.Add(new LogEntry { Kind = "Sprint", When = now, Minutes = 47, List = "ZZOLDNAME" });
                var focus = new HashSet<Part2> { Part2.Focus };
                Need(!Report(s2, now, focus, false, now).Contains("47 minutes") && Report(s2, now, focus, true, now).Contains("47 minutes"), "a renamed list's sprints went into the report");
                string all = Report(s2, now, Parts.Select(x => x.Part).ToHashSet(), false, now);
                Need(all.Contains("What got hard") && all.Contains("Coming next month") && !all.Contains((char)35), "the full report");
            }
            finally { App.DeleteTestFiles(s2.PathOverride!); }
            foreach (var id in new[] { TimesId, PatternsId, ChangedId, ReportId }) Need(Tools.Find(id) != null, id + " is not in the toolbox");
            Need(Content.Lines("smart.patterns").Count >= 20 && Content.Lines("smart.estimate").Count >= 20, "fewer than 20 lines");
        }
    }
}
