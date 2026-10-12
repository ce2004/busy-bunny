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
        public string TabName => Scale.Length > 0 && ScaleName.Length > 0 ? Name + " (" + ScaleName + ")" : Name;
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

        public TaskItem Copy() => (TaskItem)MemberwiseClone();
    }

    public sealed class Data
    {
        public int Version { get; set; } = 1;
        public List<TaskList> Lists { get; set; } = new();
        public List<TaskItem> Tasks { get; set; } = new();
        public double HoursAvailable { get; set; } = 5;
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

        public Data Data { get; private set; } = new();
        // Undo history, newest last, at most 100 steps.
        private readonly List<(List<TaskItem> Tasks, List<TaskList> Lists, string What)> _undo = new();

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
            _undo.Add((Data.Tasks.Select(t => t.Copy()).ToList(),
                Data.Lists.Select(l => new TaskList { Name = l.Name, Scale = l.Scale.ToArray(), ScaleName = l.ScaleName }).ToList(), what));
            if (_undo.Count > 100) _undo.RemoveAt(0);
        }

        public string? Undo()
        {
            if (_undo.Count == 0) return null;
            var (tasks, lists, what) = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            Data.Tasks = tasks;
            Data.Lists = lists;
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

        /// <summary>Today pulls from every list: anything urgent, plus anything done but not submitted.</summary>
        public (List<TaskItem> Urgent, List<TaskItem> Others) Today(DateTime now)
        {
            var open = Data.Tasks.Where(t => t.Status != Status.Done).ToList();
            var urgent = UrgentOrder(open.Where(t => IsUrgent(t, now)), now).ToList();
            var rest = open.Where(t => !IsUrgent(t, now) && t.Status == Status.DoneNotSubmitted)
                .OrderBy(Rank).ToList();
            return (urgent, rest);
        }

        /// <summary>What counts toward today: everything on Today plus what was finished today.</summary>
        public (int Done, int Total, int PointsDone, int PointsTotal) Progress(DateTime now)
        {
            var (urgent, rest) = Today(now);
            var finished = Data.Tasks.Where(t => t.Status == Status.Done && t.DoneAt?.Date == now.Date).ToList();
            var all = urgent.Concat(rest).Concat(finished).ToList();
            // Done but not submitted earns half (spec 5.4).
            int pointsDone = finished.Sum(t => Words.Points(t.Size)) + rest.Concat(urgent).Where(t => t.Status == Status.DoneNotSubmitted).Sum(t => Words.Points(t.Size)) / 2;
            return (finished.Count, all.Count, pointsDone, all.Sum(t => Words.Points(t.Size)));
        }

        /// <summary>The Bunny set for the day as a whole (spec 6.2f), worked out from urgent tasks and hours of work.</summary>
        public string DayType(DateTime now)
        {
            var (urgent, rest) = Today(now);
            double hours = urgent.Concat(rest).Where(t => t.Status != Status.DoneNotSubmitted).Sum(t => Words.Hours(t.Size));
            int dueSoon = urgent.Count(t => DueSoon(t, now));
            if (hours > Data.HoursAvailable || dueSoon >= 3 || urgent.Count > 5) return "Fox at the door";
            if (hours > Data.HoursAvailable / 2) return "Hop to it";
            if (hours > 0) return "Nibble on it";
            return "Burrow nap";
        }

        /// <summary>One line, braille friendly (spec 9.6): 3 of 8 done today, test Friday, next meeting at 2.</summary>
        public string Summary(DateTime now, bool shortMode = false)
        {
            var (done, total, pd, pt) = Progress(now);
            var parts = new List<string> { done + " of " + total + " done today" };
            if (!shortMode && pt > 0) parts.Add(pd * 100 / pt + " percent of today's effort");
            static bool IsTest(TaskItem t) => t.Name.Contains("test", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("exam", StringComparison.OrdinalIgnoreCase) || t.Name.Contains("quiz", StringComparison.OrdinalIgnoreCase);
            var test = Data.Tasks.Where(t => t.Status != Status.Done && t.Due >= now.Date && IsTest(t)).OrderBy(t => t.Due).FirstOrDefault();
            if (test?.Due is DateTime td) parts.Add("test " + Words.Day(td, now));
            var meeting = Data.Tasks.Where(t => t.IsMeeting && t.Status != Status.Done && t.Due > now).OrderBy(t => t.Due).FirstOrDefault();
            if (meeting?.Due is DateTime md)
            {
                var gap = md - now;
                string when = md.Date == now.Date ? "at " + Words.Time(md) : Words.Day(md, now) + " at " + Words.Time(md);
                parts.Add("next meeting " + when + (md.Date == now.Date && !shortMode ? ", in " + Gap(gap) : ""));
            }
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
            if (t.Due is DateTime d)
                parts.Add((t.IsMeeting ? "" : "due ") + Words.Day(d, now) + (t.DueHasTime ? " at " + Words.Time(d) : ""));
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
