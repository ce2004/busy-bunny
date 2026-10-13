using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BusyBunny
{
    /// <summary>The statuses from Morgan's braille lists, in the order the pick menu shows them.</summary>
    public enum Status { Today, ThisWeek, NextWeek, DoneNotSubmitted, Waiting, Done }

    public enum Size { Small, Medium, Large, MultiDay }

    public static class Words
    {
        public static string Of(Status s) => s switch
        {
            Status.Today => "Today",
            Status.ThisWeek => "This week",
            Status.NextWeek => "Next week",
            Status.DoneNotSubmitted => "Done, not submitted",
            Status.Waiting => "Waiting on someone",
            _ => "Done",
        };

        public static string Of(Size s) => s switch
        {
            Size.Small => "Small",
            Size.Medium => "Medium",
            Size.Large => "Large",
            _ => "Multi-day",
        };

        /// <summary>Effort points (spec 10.3): small 1, medium 2, large 4, a day's work on a multi-day task 2.</summary>
        public static int Points(Size s) => s switch { Size.Small => 1, Size.Medium => 2, Size.Large => 4, _ => 2 };

        /// <summary>Rough hours, for the kind of day: small half an hour, medium one, large two.</summary>
        public static double Hours(Size s) => s switch { Size.Small => 0.5, Size.Medium => 1, Size.Large => 2, _ => 1.5 };

        public static string Day(DateTime d, DateTime today)
        {
            int days = (d.Date - today.Date).Days;
            if (days == 0) return "today";
            if (days == 1) return "tomorrow";
            if (days == -1) return "yesterday";
            if (days > 1 && days < 7) return d.ToString("dddd");
            return d.ToString("dddd, MMMM d");
        }

        public static string Time(DateTime d) => (d.Minute == 0 ? d.ToString("h tt") : d.ToString("h:mm tt")).ToLowerInvariant();
    }

    /// <summary>A list: Homework, Academic and so on, with its rank and its own priority scale (highest first).</summary>
    public sealed class TaskList
    {
        public string Name { get; set; } = "";
        public string[] Scale { get; set; } = Array.Empty<string>();
        /// <summary>The scale's name, like Coffee, shown after the list's name on its tab. Empty when it has no scale.</summary>
        public string ScaleName { get; set; } = "";

        /// <summary>The tab's name: Homework (Coffee), or just the name when there is no scale.</summary>
        [JsonIgnore] public string TabName => Scale.Length > 0 && ScaleName.Length > 0 ? Name + " (" + ScaleName + ")" : Name;

        /// <summary>The list this one sits under (spec 4.9, at most two levels), or empty for a top-level list with its own tab.</summary>
        public string Parent { get; set; } = "";
        /// <summary>Sections inside the list, like one per class (spec 4.9), in order.</summary>
        public List<string> Sections { get; set; } = new();
        /// <summary>The status new tasks on this list start with.</summary>
        public Status DefaultStatus { get; set; } = Status.ThisWeek;
        /// <summary>Whether its tasks can show in Today and Focus.</summary>
        public bool InToday { get; set; } = true;
        /// <summary>Archived lists keep their tasks but have no tab and show nowhere.</summary>
        public bool Archived { get; set; }
        /// <summary>A sound name for finishing its tasks, or empty for the usual Task done sound.</summary>
        public string FinishSound { get; set; } = "";
        /// <summary>A sound pack for adding, saving and finishing its tasks (Morgan's issue 6): empty for the one used everywhere, Sounds.MatchScale for the pack that fits its scale, or a pack name.</summary>
        public string SoundPack { get; set; } = "";
        /// <summary>Task types this list uses; empty means all of them.</summary>
        public List<TaskType> Types { get; set; } = new();
    }

    /// <summary>Task types (spec 8.2): each shows only the fields it needs.</summary>
    public enum TaskType { General, Assignment, Test, Meeting, Event, PersonalGoal }

    /// <summary>Energy tags (spec 19.2): low-energy mode and Burrow nap days use them.</summary>
    public enum Energy { Unset, Easy, BrainHeavy }

    public sealed class Subtask
    {
        public string Name { get; set; } = "";
        public bool Done { get; set; }
    }

    public sealed class Comment
    {
        public DateTime When { get; set; }
        public string Text { get; set; } = "";
    }

    /// <summary>A tag Morgan made, like PSY (spec 7.1). Tasks hold the name; this holds what goes with it.</summary>
    public sealed class TagInfo
    {
        public string Name { get; set; } = "";
        /// <summary>The class's full name, like Intro to Psychology, if it is a class.</summary>
        public string Description { get; set; } = "";
    }

    /// <summary>A class meeting time (spec 19.5), entered once a semester.</summary>
    public sealed class ClassTime
    {
        public string Tag { get; set; } = "";
        public List<DayOfWeek> Days { get; set; } = new();
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public string Where { get; set; } = "";
    }

    /// <summary>Something that happened, for points, records, time per class and Semester Wrapped.</summary>
    public sealed class LogEntry
    {
        public DateTime When { get; set; }
        /// <summary>Done, DidAThing, Sprint, WorkedOn, Badge, Level and so on.</summary>
        public string Kind { get; set; } = "";
        public string Text { get; set; } = "";
        public string Tag { get; set; } = "";
        public string List { get; set; } = "";
        public int Points { get; set; }
        public int Minutes { get; set; }
        public Guid? TaskId { get; set; }
    }

    public enum DayMode { Normal, Vacation, RoughDay }

    /// <summary>What Morgan set for one day: the kind of day, her hours, vacation or a rough day.</summary>
    public sealed class DayInfo
    {
        /// <summary>A Bunny set name she chose for the day (spec 6.2f), or empty to let the app pick.</summary>
        public string DayType { get; set; } = "";
        /// <summary>Hours available that day (spec 10.4), or 0 for the usual.</summary>
        public double Hours { get; set; }
        public DayMode Mode { get; set; }
        /// <summary>From the morning check-in: low, medium, high, or empty.</summary>
        public string Energy { get; set; } = "";
        public bool CheckedIn { get; set; }
    }

    /// <summary>The library of priority scales (spec section 18 and 6.2), highest level first.</summary>
    public static class Scales
    {
        public static readonly (string Name, string[] Levels)[] Library =
        {
            ("Coffee", new[] { "Triple shot", "Double shot", "Single shot", "Decaf" }),
            ("Space", new[] { "Launch", "Orbit", "Deep space" }),
            ("Burner", new[] { "Front burner", "Back burner", "In the freezer" }),
            ("Importance", new[] { "Important", "Not immediate" }),
            ("Attendance", new[] { "Required", "Optional" }),
            ("Todoist", new[] { "P1", "P2", "P3", "P4" }),
            ("Bunny", new[] { "Fox at the door", "Hop to it", "Nibble on it", "Burrow nap" }),
            ("Heat", new[] { "On fire", "Spicy", "Warm", "Chill" }),
            ("Rock", new[] { "Big rock", "Pebble" }),
            ("Speed", new[] { "Sprint", "Jog", "Stroll", "Nap" }),
            ("Volume", new[] { "Blasting", "Loud", "Background", "Muted" }),
            ("Road trip", new[] { "Driving now", "Next exit", "Someday road trip" }),
        };

        /// <summary>The library name for these levels, or Custom.</summary>
        public static string NameFor(string[] levels) =>
            Library.FirstOrDefault(s => s.Levels.SequenceEqual(levels)).Name ?? "Custom";

        /// <summary>The order of a scale, to read: 1 Triple shot, 2 Double shot, 3 Single shot, 4 Decaf.</summary>
        public static string Rank(string[] levels) =>
            levels.Length == 0 ? "No priority scale." : string.Join(", ", levels.Select((l, i) => (i + 1) + " " + l)) + ".";
    }

    public sealed class TaskItem
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
        public string List { get; set; } = "";
        public string Tag { get; set; } = "";
        public Status Status { get; set; } = Status.ThisWeek;
        /// <summary>1 is the top of the list's scale; 0 means no priority.</summary>
        public int Priority { get; set; }
        public DateTime? Due { get; set; }
        public bool DueHasTime { get; set; }
        public Size Size { get; set; } = Size.Medium;
        public bool IsMeeting { get; set; }
        public DateTime? DoneAt { get; set; }
        public DateTime? StatusSince { get; set; }

        public TaskType Type { get; set; } = TaskType.General;
        /// <summary>The day she plans to do it, apart from when it is due (spec 8.3). On that day it shows in Today.</summary>
        public DateTime? DoDate { get; set; }
        /// <summary>A repeat rule in plain words, like every Monday, or empty.</summary>
        public string Repeat { get; set; } = "";
        /// <summary>Ask for a new name each time it repeats, like chapter 9 readings becoming chapter 10 (Morgan's issue 5).</summary>
        public bool RenameOnRepeat { get; set; }
        public DateTime? RemindAt { get; set; }
        /// <summary>A repeat rule for the reminder, like every day, or empty.</summary>
        public string RemindRepeat { get; set; } = "";
        public Energy Energy { get; set; }
        /// <summary>Marked as a quick win (spec 22.4): two minutes or less, shown in the Quick wins drawer.</summary>
        public bool QuickWin { get; set; }
        /// <summary>How much it counts toward the grade, in percent (spec 22.2), or 0.</summary>
        public int GradeWeight { get; set; }
        /// <summary>The section of its list, or empty.</summary>
        public string Section { get; set; } = "";
        public string Notes { get; set; } = "";
        public List<Comment> Comments { get; set; } = new();
        public List<Subtask> Subtasks { get; set; } = new();
        public DateTime CreatedAt { get; set; } = DateTime.Now;
        /// <summary>Last time it was changed, for the backlog sweep.</summary>
        public DateTime TouchedAt { get; set; } = DateTime.Now;
        /// <summary>How many times it has been pushed back (spec 15.6).</summary>
        public int Snoozes { get; set; }

        /// <summary>A full copy, lists inside included.</summary>
        public TaskItem Copy() => Store.Clone(this);
    }

    public sealed class Data
    {
        public int Version { get; set; } = 1;
        public List<TaskList> Lists { get; set; } = new();
        public List<TaskItem> Tasks { get; set; } = new();
        public double HoursAvailable { get; set; } = 5;
        public List<TagInfo> Tags { get; set; } = new();
        public List<ClassTime> Classes { get; set; } = new();
        /// <summary>Accessibility requests per class tag (spec 19.6), and which nudges were already spoken.</summary>
        public List<AccessRequest> AccessRequests { get; set; } = new();
        public List<string> AccessNudges { get; set; } = new();
        public List<LogEntry> Log { get; set; } = new();
        /// <summary>Per-day settings, keyed by yyyy-MM-dd.</summary>
        public Dictionary<string, DayInfo> Days { get; set; } = new();
        public List<TaskItem> Templates { get; set; } = new();
        /// <summary>The parking lot (spec 15.5): stray thoughts caught during focus.</summary>
        public List<string> Parking { get; set; } = new();
        /// <summary>Weekday plans (spec 11): weekday name to plan lines, like morning PSY reading.</summary>
        public Dictionary<string, string> WeekdayPlans { get; set; } = new();
        /// <summary>The Monday of the week the backlog sweep was last offered (spec 19.8), or null.</summary>
        public DateTime? SweepOfferedWeek { get; set; }
        /// <summary>Karma, goals, badges, bunny and challenges (Game.cs).</summary>
        public GameState Game { get; set; } = new();

        public DayInfo Day(DateTime d)
        {
            string k = d.ToString("yyyy-MM-dd");
            if (!Days.TryGetValue(k, out var info)) Days[k] = info = new DayInfo();
            return info;
        }
    }

    /// <summary>
    /// Morgan's data on this PC, in AppData\Roaming\Busy Bunny. Saved after every change by writing a
    /// new file and swapping it in, so a crash or power cut never leaves half a file. The first save
    /// each day also keeps a dated copy in Backups, the last 30 of them.
    /// </summary>
    public sealed class Store
    {
        public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Busy Bunny");
        public static string FilePath => Path.Combine(Folder, "tasks.json");
        public static string BackupFolder => Path.Combine(Folder, "Backups");

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

        /// <summary>A deep copy of anything saved in the data file.</summary>
        public static T Clone<T>(T x) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(x, Json), Json)!;

        public Data Data { get; private set; } = new();
        // Undo history, newest last, at most 100 steps: the whole data file as it was.
        private readonly List<(string Data, string What)> _undo = new();

        /// <summary>
        /// Features add rules here to keep tasks off Today and Focus, like vacation or low-energy mode.
        /// A task is hidden when any rule says true.
        /// </summary>
        public readonly List<Func<TaskItem, DateTime, bool>> HideFromToday = new();
        /// <summary>Features add parts to the summary line here, like time until the next class. Given now and short mode; null adds nothing.</summary>
        public readonly List<Func<DateTime, bool, string?>> SummaryParts = new();
        /// <summary>Features reorder the non-urgent tasks of Also today and Focus here (shuffle mode). Applied in turn.</summary>
        public readonly List<Func<List<TaskItem>, DateTime, List<TaskItem>>> ReorderRules = new();
        public List<TaskItem> Reorder(List<TaskItem> tasks, DateTime now)
        {
            foreach (var r in ReorderRules) tasks = r(tasks, now);
            return tasks;
        }

        public IEnumerable<TaskList> ActiveLists => Data.Lists.Where(l => !l.Archived);
        public string Key(DateTime d) => d.ToString("yyyy-MM-dd");

        public string? PathOverride { get; init; }
        private string MyFile => PathOverride ?? FilePath;

        public static Store Load(string? path = null)
        {
            var s = new Store { PathOverride = path };
            try
            {
                if (File.Exists(s.MyFile))
                    s.Data = JsonSerializer.Deserialize<Data>(File.ReadAllText(s.MyFile), Json) ?? Sample.Make();
                else
                    s.Data = Sample.Make();
            }
            catch
            {
                // A damaged file is kept for rescue, never overwritten; the app starts from the sample.
                try { File.Copy(s.MyFile, s.MyFile + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); } catch { }
                s.Data = Sample.Make();
            }
            if (s.Data.Lists.Count == 0) s.Data.Lists = Sample.Lists();
            // Saved before scales had names: name them from the library.
            foreach (var l in s.Data.Lists)
                if (l.Scale.Length > 0 && l.ScaleName.Length == 0) l.ScaleName = Scales.NameFor(l.Scale);
            // Every tag on a task is a known tag.
            foreach (var tag in s.Data.Tasks.Select(t => t.Tag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!s.Data.Tags.Any(x => x.Name.Equals(tag, StringComparison.OrdinalIgnoreCase))) s.Data.Tags.Add(new TagInfo { Name = tag });
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MyFile)!);
            string text = JsonSerializer.Serialize(Data, Json);
            string fresh = MyFile + ".new";
            File.WriteAllText(fresh, text);
            if (File.Exists(MyFile)) File.Replace(fresh, MyFile, null, ignoreMetadataErrors: true);
            else File.Move(fresh, MyFile);
            if (PathOverride == null) BackupToday(text);
        }

        private static void BackupToday(string text)
        {
            try
            {
                Directory.CreateDirectory(BackupFolder);
                string today = Path.Combine(BackupFolder, "tasks " + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
                if (File.Exists(today)) return;
                File.WriteAllText(today, text);
                var old = new DirectoryInfo(BackupFolder).GetFiles("tasks ????-??-??.json").OrderByDescending(f => f.Name).Skip(30);
                foreach (var f in old) f.Delete();
            }
            catch { }
        }

        /// <summary>Call before any change: Undo puts every task back the way it was here.</summary>
        public void Remember(string what)
        {
            _undo.Add((JsonSerializer.Serialize(Data, Json), what));
            if (_undo.Count > 100) _undo.RemoveAt(0);
        }

        /// <summary>Reads a data file for a restore: null when it is not Busy Bunny data.</summary>
        public static Data? ReadFile(string path)
        {
            try
            {
                var d = JsonSerializer.Deserialize<Data>(File.ReadAllText(path), Json);
                return d != null && d.Lists.Count > 0 ? d : null;
            }
            catch { return null; }
        }

        /// <summary>Swaps in other data, as a restore does. Call Remember first so it can be undone.</summary>
        public void ReplaceData(Data d)
        {
            Data = d;
            foreach (var l in Data.Lists)
                if (l.Scale.Length > 0 && l.ScaleName.Length == 0) l.ScaleName = Scales.NameFor(l.Scale);
            foreach (var tag in Data.Tasks.Select(t => t.Tag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!Data.Tags.Any(x => x.Name.Equals(tag, StringComparison.OrdinalIgnoreCase))) Data.Tags.Add(new TagInfo { Name = tag });
            Save();
        }

        public string? Undo()
        {
            if (_undo.Count == 0) return null;
            var (data, what) = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Data = JsonSerializer.Deserialize<Data>(data, Json)!;
            Save();
            return what;
        }

        public TaskList ListOf(TaskItem t) => Data.Lists.FirstOrDefault(l => l.Name == t.List) ?? new TaskList { Name = t.List };
        public int Rank(TaskItem t) { int i = Data.Lists.FindIndex(l => l.Name == t.List); return i < 0 ? 99 : i; }

        // ---- Spec section 6: what is urgent, and the order of Today ----

        public bool DueSoon(TaskItem t, DateTime now) => t.Due is DateTime d && (t.DueHasTime ? d : d.Date.AddDays(1).AddSeconds(-1)) <= now.AddHours(24);

        public bool IsUrgent(TaskItem t, DateTime now) =>
            t.Status != Status.Done && t.Status != Status.Waiting && t.Status != Status.DoneNotSubmitted &&
            (t.Status == Status.Today || t.Priority == 1 && ListOf(t).Scale.Length > 0 || DueSoon(t, now));

        /// <summary>Urgent order (6.5): due within 24 hours, then Today status, then top priority; list rank breaks ties.</summary>
        public IEnumerable<TaskItem> UrgentOrder(IEnumerable<TaskItem> tasks, DateTime now) => tasks
            .OrderBy(t => DueSoon(t, now) ? 0 : t.Status == Status.Today ? 1 : 2)
            .ThenBy(t => DueSoon(t, now) ? t.Due : null)
            .ThenBy(Rank);

        /// <summary>Whether a task may show in Today and Focus: its list allows it, it is not archived, and no feature hides it.</summary>
        public bool CanShowToday(TaskItem t, DateTime now)
        {
            var l = Data.Lists.FirstOrDefault(x => x.Name == t.List);
            if (l != null && (!l.InToday || l.Archived)) return false;
            foreach (var hide in HideFromToday) if (hide(t, now)) return false;
            return true;
        }

        /// <summary>
        /// Planned for today (Morgan's issue 10): its do date is today or past, or it has no do date and
        /// its status is Today. Done but not submitted is always on the plate. A do date later on wins over
        /// a Today status, and top priority alone no longer puts a task on Today.
        /// </summary>
        public static bool PlannedToday(TaskItem t, DateTime now) =>
            t.Status == Status.DoneNotSubmitted ||
            t.Status != Status.Done && t.Status != Status.Waiting && (t.DoDate is DateTime d ? d.Date <= now.Date : t.Status == Status.Today);

        /// <summary>Due today, or overdue.</summary>
        public static bool DueToday(TaskItem t, DateTime now) => t.Due is DateTime d && d.Date <= now.Date;

        /// <summary>
        /// Today pulls from every list, in two parts (Morgan's issue 10): what she planned to do today,
        /// urgent ones first; and what is due today or overdue. A task can be in both.
        /// </summary>
        public (List<TaskItem> ToDo, List<TaskItem> Due) Today(DateTime now)
        {
            var open = Data.Tasks.Where(t => t.Status != Status.Done && CanShowToday(t, now)).ToList();
            var planned = open.Where(t => PlannedToday(t, now)).ToList();
            var todo = UrgentOrder(planned.Where(t => IsUrgent(t, now)), now)
                .Concat(Reorder(planned.Where(t => !IsUrgent(t, now)).OrderBy(Rank).ToList(), now)).ToList();
            var due = open.Where(t => t.Status != Status.Waiting && DueToday(t, now)).OrderBy(t => t.Due).ThenBy(Rank).ToList();
            return (todo, due);
        }

        /// <summary>Everything on Today once, to do first.</summary>
        public List<TaskItem> TodayAll(DateTime now)
        {
            var (todo, due) = Today(now);
            return todo.Concat(due.Where(t => !todo.Contains(t))).ToList();
        }

        /// <summary>The Sunday that ends this week (weeks start on Monday).</summary>
        public static DateTime WeekEnd(DateTime now) => now.Date.AddDays(6 - (((int)now.DayOfWeek + 6) % 7));

        /// <summary>Which of Today, This week or Next week a task is planned for: by its do date when it has one (later than next week counts as next week), otherwise by its status. Null for any other status.</summary>
        public static Status? PlanGroup(TaskItem t, DateTime now)
        {
            if (t.Status is not (Status.Today or Status.ThisWeek or Status.NextWeek)) return null;
            if (t.DoDate is not DateTime d) return t.Status;
            return d.Date <= now.Date ? Status.Today : d.Date <= WeekEnd(now) ? Status.ThisWeek : Status.NextWeek;
        }

        /// <summary>Which of Today (or overdue), This week or Next week a task is due in, or null.</summary>
        public static Status? DueGroup(TaskItem t, DateTime now)
        {
            if (t.Status is not (Status.Today or Status.ThisWeek or Status.NextWeek) || t.Due is not DateTime d) return null;
            return d.Date <= now.Date ? Status.Today : d.Date <= WeekEnd(now) ? Status.ThisWeek : d.Date <= WeekEnd(now).AddDays(7) ? Status.NextWeek : null;
        }

        /// <summary>Open tasks planned or due later this week, not already on Today: the ones to get ahead on.</summary>
        public List<TaskItem> GetAhead(DateTime now)
        {
            var today = TodayAll(now);
            return Data.Tasks.Where(t => !today.Contains(t) && CanShowToday(t, now) && (PlanGroup(t, now) == Status.ThisWeek || DueGroup(t, now) == Status.ThisWeek))
                .OrderBy(t => t.DoDate ?? t.Due ?? DateTime.MaxValue).ThenBy(Rank).ToList();
        }

        /// <summary>Hours available today: what she set for the day, or her usual.</summary>
        public double HoursToday(DateTime now) =>
            Data.Days.TryGetValue(Key(now), out var d) && d.Hours > 0 ? d.Hours : Data.HoursAvailable;

        /// <summary>What counts toward today: everything on Today plus what was finished today.</summary>
        public (int Done, int Total, int PointsDone, int PointsTotal) Progress(DateTime now)
        {
            var open = TodayAll(now);
            var finished = Data.Tasks.Where(t => t.Status == Status.Done && t.DoneAt?.Date == now.Date).ToList();
            var all = open.Concat(finished).ToList();
            // Done but not submitted earns half (spec 5.4).
            int pointsDone = finished.Sum(t => Words.Points(t.Size)) + open.Where(t => t.Status == Status.DoneNotSubmitted).Sum(t => Words.Points(t.Size)) / 2;
            // I did a thing (spec 12.12): things logged today count as done items and points.
            var things = Data.Log.Where(l => l.Kind == "DidAThing" && l.When.Date == now.Date).ToList();
            return (finished.Count + things.Count, all.Count + things.Count, pointsDone + things.Sum(l => l.Points), all.Sum(t => Words.Points(t.Size)) + things.Sum(l => l.Points));
        }

        /// <summary>The Bunny set for the day as a whole (spec 6.2f), worked out from urgent tasks and hours of work.</summary>
        public string DayType(DateTime now)
        {
            if (Data.Days.TryGetValue(Key(now), out var set) && set.DayType.Length > 0) return set.DayType;
            var all = TodayAll(now);
            var urgent = all.Where(t => IsUrgent(t, now)).ToList();
            double hours = all.Where(t => t.Status != Status.DoneNotSubmitted).Sum(t => Words.Hours(t.Size));
            int dueSoon = urgent.Count(t => DueSoon(t, now));
            double available = HoursToday(now);
            if (hours > available || dueSoon >= 3 || urgent.Count > 5) return "Fox at the door";
            if (hours > available / 2) return "Hop to it";
            if (hours > 0) return "Nibble on it";
            return "Burrow nap";
        }

        /// <summary>One line, braille friendly (spec 9.6): 3 of 8 done today, test Friday, next meeting at 2.</summary>
        public string Summary(DateTime now, bool shortMode = false)
        {
            var (done, total, pd, pt) = Progress(now);
            var parts = new List<string> { done + " of " + total + " done today" };
            if (!shortMode && pt > 0) parts.Add(pd * 100 / pt + " percent of today's effort");
            var meeting = Data.Tasks.Where(t => t.IsMeeting && t.Status != Status.Done && t.Due > now).OrderBy(t => t.Due).FirstOrDefault();
            if (meeting?.Due is DateTime md)
            {
                var gap = md - now;
                string when = md.Date == now.Date ? "at " + Words.Time(md) : Words.Day(md, now) + " at " + Words.Time(md);
                parts.Add("next meeting " + when + (md.Date == now.Date && !shortMode ? ", in " + Gap(gap) : ""));
            }
            foreach (var part in SummaryParts)
                if (part(now, shortMode) is string p && p.Length > 0) parts.Add(p);
            if (!shortMode) parts.Add("a " + DayType(now) + " day");
            return string.Join(", ", parts) + ".";
        }

        private static string Gap(TimeSpan t)
        {
            if (t.TotalMinutes < 60) return (int)t.TotalMinutes + " minutes";
            int h = (int)t.TotalHours, m = t.Minutes;
            return h + (h == 1 ? " hour" : " hours") + (m >= 5 ? " " + m + " minutes" : "");
        }

        /// <summary>One short line, most important first (spec 3.4): tag, name, status, priority, due.</summary>
        public string Line(TaskItem t, DateTime now, bool shortMode = false)
        {
            var parts = new List<string>();
            if (t.Tag.Length > 0) parts.Add(t.Tag);
            parts.Add(t.Name);
            parts.Add(Words.Of(t.Status));
            var scale = ListOf(t).Scale;
            if (t.Priority > 0 && t.Priority <= scale.Length) parts.Add(shortMode ? "P" + t.Priority : scale[t.Priority - 1] + " " + t.Priority);
            if (t.DoDate is DateTime p && t.Status != Status.Done)
                parts.Add((shortMode ? "do " : "planned for ") + Words.Day(p, now));
            if (t.Due is DateTime d)
                parts.Add((t.IsMeeting ? "" : "due ") + Words.Day(d, now) + (t.DueHasTime ? " at " + Words.Time(d) : ""));
            // Subtasks show on the line itself, so they are never out of sight (Morgan's issue 7).
            if (t.Subtasks.Count > 0)
                parts.Add(t.Subtasks.Count(s => s.Done) + " of " + t.Subtasks.Count + (shortMode ? " steps" : t.Subtasks.Count == 1 ? " step done" : " steps done"));
            return string.Join(", ", parts);
        }
    }

    /// <summary>The six lists Morgan chose, in rank order, and fake tasks for the first try (spec 21.2).</summary>
    public static class Sample
    {
        public static List<TaskList> Lists()
        {
            TaskList L(string name, string scale) => new()
            {
                Name = name,
                ScaleName = scale,
                Scale = scale.Length == 0 ? Array.Empty<string>() : Scales.Library.First(s => s.Name == scale).Levels,
            };
            return new()
            {
                L("Homework", "Coffee"),
                L("Academic", "Importance"),
                L("Meetings", "Attendance"),
                L("Projects", "Space"),
                L("Personal Immediate", ""),
                L("Personal", "Burner"),
            };
        }

        public static Data Make()
        {
            var today = DateTime.Today;
            DateTime Next(DayOfWeek d) { var x = today.AddDays(1); while (x.DayOfWeek != d) x = x.AddDays(1); return x; }
            return new Data
            {
                Lists = Lists(),
                Tasks = new List<TaskItem>
                {
                    new() { Name = "chapter 4 reading", List = "Homework", Tag = "PSY", Status = Status.Today, Priority = 1, Due = Next(DayOfWeek.Thursday), Size = Size.Large },
                    new() { Name = "stats problem set 3", List = "Homework", Tag = "STAT", Status = Status.ThisWeek, Priority = 2, Due = today.AddDays(1), Size = Size.Medium },
                    new() { Name = "discussion post", List = "Homework", Tag = "PSY", Status = Status.Today, Priority = 3, Due = today, Size = Size.Small },
                    new() { Name = "lab 3 write-up", List = "Homework", Tag = "BIO", Status = Status.DoneNotSubmitted, Priority = 2, Due = today.AddDays(2), Size = Size.Medium },
                    new() { Name = "midterm test", List = "Academic", Tag = "STAT", Status = Status.ThisWeek, Priority = 1, Due = Next(DayOfWeek.Friday), Size = Size.Large },
                    new() { Name = "email advisor about spring classes", List = "Academic", Status = Status.Waiting, Priority = 2, Size = Size.Small },
                    new() { Name = "study group", List = "Meetings", Tag = "STAT", Status = Status.Today, Priority = 2, Due = today.AddHours(14), DueHasTime = true, IsMeeting = true, Size = Size.Medium },
                    new() { Name = "lit review outline", List = "Projects", Tag = "RESEARCH", Status = Status.NextWeek, Priority = 2, Size = Size.MultiDay },
                    new() { Name = "refill prescription", List = "Personal Immediate", Status = Status.Today, Size = Size.Small },
                    new() { Name = "Goodreads project", List = "Personal", Status = Status.NextWeek, Priority = 2, Size = Size.MultiDay },
                    new() { Name = "call grandma", List = "Personal", Status = Status.ThisWeek, Priority = 1, Size = Size.Small },
                },
            };
        }
    }
}
