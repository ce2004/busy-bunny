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
        /// <summary>Kept private from Smarter Bunny (Morgan's issue 15): its tasks are never sent to Claude.</summary>
        public bool PrivateFromBunny { get; set; }
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
        /// <summary>The day of the month a monthly or yearly repeat is meant for, like 31 for the last day, so short months do not pull it earlier for good. 0 when not known yet.</summary>
        public int RepeatDay { get; set; }
        /// <summary>The same for the reminder's repeat.</summary>
        public int RemindDay { get; set; }
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
        /// <summary>What Smarter Bunny keeps in mind about her: goals, what helps, what to avoid. She can read and clear it.</summary>
        public List<string> BunnyNotes { get; set; } = new();
        /// <summary>What Smarter Bunny learned about her from her tasks and history, written by Claude, refreshed weekly.</summary>
        public string BunnyProfile { get; set; } = "";
        public DateTime BunnyLearned { get; set; }
        /// <summary>The burrow, Vex the rival, bets, quests and collected sound packs.</summary>
        public PlayState Play { get; set; } = new();

        /// <summary>The day's settings, made and stored when missing: for setting something.</summary>
        public DayInfo Day(DateTime d)
        {
            string k = d.ToString("yyyy-MM-dd");
            if (!Days.TryGetValue(k, out var info)) Days[k] = info = new DayInfo();
            return info;
        }

        /// <summary>The day's settings for reading only: a blank one when nothing was set, and nothing is stored.</summary>
        public DayInfo PeekDay(DateTime d) => Days.TryGetValue(d.ToString("yyyy-MM-dd"), out var info) && info != null ? info : new DayInfo();

        /// <summary>
        /// Fills in anything a damaged, hand-edited or older file left out (a missing list or name), so
        /// nothing later trips over a null. Called after every read.
        /// </summary>
        public Data Normalize()
        {
            Lists ??= new(); Tasks ??= new(); Tags ??= new(); Classes ??= new(); AccessRequests ??= new();
            AccessNudges ??= new(); Log ??= new(); Days ??= new(); Templates ??= new(); Parking ??= new();
            WeekdayPlans ??= new(); BunnyNotes ??= new(); BunnyProfile ??= ""; Game ??= new(); Play ??= new();
            Lists.RemoveAll(l => l == null);
            foreach (var l in Lists)
            {
                l.Name ??= ""; l.Scale ??= Array.Empty<string>(); l.ScaleName ??= ""; l.Parent ??= ""; l.Sections ??= new();
                l.FinishSound ??= ""; l.SoundPack ??= ""; l.Types ??= new();
                l.Sections.RemoveAll(s => s == null);
            }
            Tasks.RemoveAll(t => t == null);
            Templates.RemoveAll(t => t == null);
            foreach (var t in Tasks.Concat(Templates))
            {
                t.Name ??= ""; t.List ??= ""; t.Tag ??= ""; t.Repeat ??= ""; t.RemindRepeat ??= ""; t.Section ??= ""; t.Notes ??= "";
                t.Comments ??= new(); t.Subtasks ??= new();
                t.Comments.RemoveAll(c => c == null);
                t.Subtasks.RemoveAll(s => s == null);
                foreach (var c in t.Comments) c.Text ??= "";
                foreach (var s in t.Subtasks) s.Name ??= "";
            }
            Tags.RemoveAll(x => x == null || x.Name == null);
            foreach (var x in Tags) x.Description ??= "";
            Classes.RemoveAll(c => c == null);
            foreach (var c in Classes) { c.Tag ??= ""; c.Days ??= new(); c.Where ??= ""; }
            Log.RemoveAll(e => e == null);
            foreach (var e in Log) { e.Kind ??= ""; e.Text ??= ""; e.Tag ??= ""; e.List ??= ""; }
            AccessNudges.RemoveAll(x => x == null);
            Parking.RemoveAll(x => x == null);
            BunnyNotes.RemoveAll(x => x == null);
            foreach (var k in Days.Where(kv => kv.Value == null).Select(kv => kv.Key).ToList()) Days.Remove(k);
            return this;
        }
    }

    /// <summary>
    /// Reads enum names like JsonStringEnumConverter, but a name this version does not know (written by
    /// a newer Busy Bunny on the other laptop, or after going back a version) becomes a safe default
    /// instead of making the whole file unreadable.
    /// </summary>
    public sealed class TolerantEnumConverter : JsonConverterFactory
    {
        public override bool CanConvert(Type t) => t.IsEnum;
        public override JsonConverter CreateConverter(Type t, JsonSerializerOptions o) =>
            (JsonConverter)Activator.CreateInstance(typeof(Conv<>).MakeGenericType(t))!;

        private sealed class Conv<T> : JsonConverter<T> where T : struct, Enum
        {
            // An unknown status becomes This week: never Today, which would make it urgent.
            private static T Fallback => typeof(T) == typeof(Status) ? (T)(object)Status.ThisWeek : default;

            private static T From(string? s) =>
                s != null && Enum.TryParse<T>(s, true, out var v) && Enum.IsDefined(v) ? v : Fallback;

            public override T Read(ref Utf8JsonReader r, Type type, JsonSerializerOptions o)
            {
                if (r.TokenType == JsonTokenType.String) return From(r.GetString());
                if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out int n))
                {
                    var v = (T)Enum.ToObject(typeof(T), n);
                    return Enum.IsDefined(v) ? v : Fallback;
                }
                r.Skip();
                return Fallback;
            }

            public override void Write(Utf8JsonWriter w, T value, JsonSerializerOptions o) => w.WriteStringValue(value.ToString());
            public override T ReadAsPropertyName(ref Utf8JsonReader r, Type type, JsonSerializerOptions o) => From(r.GetString());
            public override void WriteAsPropertyName(Utf8JsonWriter w, T value, JsonSerializerOptions o) => w.WritePropertyName(value.ToString());
        }
    }

    /// <summary>Busy Bunny's tasks file is in use by another program and stayed that way: it is not damaged, so nothing is replaced.</summary>
    public sealed class StoreLockedException : IOException
    {
        public StoreLockedException(string path, Exception inner) : base("The tasks file " + path + " is in use by another program.", inner) { }
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

        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Converters = { new TolerantEnumConverter() } };

        /// <summary>A deep copy of anything saved in the data file.</summary>
        public static T Clone<T>(T x) => JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(x, Json), Json)!;

        public Data Data { get; private set; } = new();
        // Undo history, newest last, at most 100 steps and about 50 MB: the whole data file as it was,
        // compressed (a year of tasks is a few MB as text, so 100 plain copies ran to a gigabyte).
        private readonly List<(byte[] Data, string What)> _undo = new();
        private long _undoBytes;
        private const long UndoLimit = 50L << 20;

        // ---- Saving safely (sweep 2.6 and 9.2) ----

        /// <summary>True while a change is only in memory because the last save failed. DataSafety keeps retrying.</summary>
        public bool Dirty { get; private set; }
        /// <summary>Why the last save failed, in Windows' words, or null.</summary>
        public string? LastSaveError { get; private set; }
        /// <summary>Raised each time a save fails. The change stays in memory and Dirty is set.</summary>
        public event Action<Exception>? SaveFailed;
        /// <summary>Raised when a save works again after failing.</summary>
        public event Action? SaveRecovered;
        /// <summary>Something to tell her after starting, like a damaged file that was replaced by a backup. Null when all was well.</summary>
        public string? LoadNote { get; set; }
        /// <summary>True when there was no data at all on this PC (no file, no backup): the sample tasks were loaded.</summary>
        public bool FirstRun { get; private set; }
        /// <summary>A second folder for the daily backup, like her Google Drive folder (sweep 2.2), or null. Set from Settings.</summary>
        public static string? ExtraBackupFolder { get; set; }
        /// <summary>The daily backups for a test store; real data uses BackupFolder. Null for a test store means no backups.</summary>
        public string? BackupOverride { get; init; }
        private string? MyBackups => PathOverride == null ? BackupFolder : BackupOverride;

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

        /// <summary>
        /// While set, the summary, progress, overdue and kind-of-day maths only count tasks this says yes to.
        /// Smarter Bunny sets it through SummaryFor so private lists never reach Claude (sweep 2.1).
        /// SummaryParts must use Counts too.
        /// </summary>
        public Func<TaskItem, bool>? SummaryFilter { get; private set; }
        public bool Counts(TaskItem t) => (SummaryFilter == null || SummaryFilter(t)) && !IsArchived(t);
        /// <summary>Whether the task's list is archived: those tasks show nowhere, so summaries leave them out.</summary>
        public bool IsArchived(TaskItem t) => Data.Lists.FirstOrDefault(l => l.Name == t.List)?.Archived == true;
        /// <summary>The summary line built only from tasks include says yes to.</summary>
        public string SummaryFor(DateTime now, Func<TaskItem, bool> include, bool shortMode = false)
        {
            var before = SummaryFilter;
            SummaryFilter = include;
            try { return Summary(now, shortMode); }
            finally { SummaryFilter = before; }
        }
        public string Key(DateTime d) => d.ToString("yyyy-MM-dd");

        public string? PathOverride { get; init; }
        private string MyFile => PathOverride ?? FilePath;

        /// <summary>
        /// Loads her data. A file another program holds open (a sync or backup tool, a virus scan) is
        /// waited for, never treated as damaged: if it stays locked this throws StoreLockedException and
        /// nothing is replaced. A damaged file is kept for rescue and the newest good copy is loaded
        /// instead (the last save's new file, the copy before it, then the daily backups), with LoadNote
        /// saying so. Only a PC with no data at all gets the sample tasks.
        /// </summary>
        public static Store Load(string? path = null, string? backups = null)
        {
            var s = new Store { PathOverride = path, BackupOverride = backups };
            string file = s.MyFile;
            if (File.Exists(file))
            {
                string text = ReadWaiting(file);
                var d = Parse(text);
                if (d != null)
                {
                    s.Data = d;
                    // A save that wrote its new copy but could not swap it in, and Busy Bunny closed after: that copy is newer.
                    try
                    {
                        string fresh = file + ".new";
                        if (File.Exists(fresh) && File.GetLastWriteTimeUtc(fresh) > File.GetLastWriteTimeUtc(file) && Parse(ReadWaiting(fresh, 2000)) is Data newer)
                        {
                            s.Data = newer;
                            s.LoadNote = "Your last change had not been saved properly, so I loaded the newer copy of your tasks.";
                            ErrorLog.Write("Loaded tasks.json.new, newer than tasks.json.");
                        }
                    }
                    catch { }
                }
                else
                {
                    string kept = file + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    try { File.Copy(file, kept, true); }
                    catch { s._keepDamagedAs = kept; } // the first save keeps it instead (as its spare copy)
                    string keptWords = kept.Length > 0 ? " The damaged file is kept as " + Path.GetFileName(kept) + "." : "";
                    var (good, from) = s.Rescue();
                    if (good != null)
                    {
                        s.Data = good;
                        s.LoadNote = "Your tasks file was damaged, so I loaded " + from + "." + keptWords;
                    }
                    else
                    {
                        // Never the sample tasks over a real file: empty lists, and the damaged file kept.
                        s.Data = new Data { Lists = Sample.Lists() };
                        s.LoadNote = "Your tasks file was damaged and I found no good backup, so your lists are empty for now." + keptWords +
                            " Help, Report a problem copies the details, and File, Restore from a backup can bring back a copy you exported.";
                    }
                    ErrorLog.Write("Tasks file was damaged at start. " + (good != null ? "Loaded " + from + "." : "No good backup."));
                }
            }
            else
            {
                var (good, from) = s.Rescue();
                if (good != null)
                {
                    s.Data = good;
                    s.LoadNote = "I could not find your tasks file, so I loaded " + from + ".";
                    ErrorLog.Write("Tasks file was missing at start. Loaded " + from + ".");
                }
                else
                {
                    s.Data = Sample.Make();
                    s.FirstRun = true;
                }
            }
            s.Tidy();
            return s;
        }

        /// <summary>Reads the file, waiting up to about 8 seconds while another program has it open.</summary>
        private static string ReadWaiting(string file, int waitMs = 8000)
        {
            var until = DateTime.UtcNow.AddMilliseconds(waitMs);
            while (true)
            {
                try { return File.ReadAllText(file); }
                catch (Exception e) when (e is (IOException or UnauthorizedAccessException) and not (FileNotFoundException or DirectoryNotFoundException))
                {
                    if (DateTime.UtcNow > until) throw new StoreLockedException(file, e);
                    System.Threading.Thread.Sleep(250);
                }
            }
        }

        /// <summary>The data in this text, or null when it is not readable Busy Bunny data.</summary>
        private static Data? Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text) || text.TrimStart('\0', ' ', '\r', '\n', '\t').Length == 0) return null;
            try { return JsonSerializer.Deserialize<Data>(text, Json)?.Normalize(); }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException or FormatException) { return null; }
        }

        /// <summary>The newest good copy of the data, and words for where it came from; null when there is none.</summary>
        private (Data? Data, string From) Rescue()
        {
            var tries = new List<(string File, Func<string> Words)>
            {
                (MyFile + ".new", () => "the copy from your last save"),
                (MyFile + ".bak", () => "the copy from the save before that"),
            };
            if (MyBackups is string dir && Directory.Exists(dir))
                foreach (var f in new DirectoryInfo(dir).GetFiles("tasks ????-??-??.json").OrderByDescending(f => f.Name))
                {
                    var name = f;
                    tries.Add((f.FullName, () => DateTime.TryParseExact(name.Name[6..16], "yyyy-MM-dd", null, System.Globalization.DateTimeStyles.None, out var day)
                        ? "the backup from " + Words.Day(day, DateTime.Today)
                        : "the newest daily backup"));
                }
            foreach (var (f, words) in tries)
            {
                try
                {
                    if (!File.Exists(f)) continue;
                    var d = Parse(ReadWaiting(f, 2000));
                    if (d != null && (d.Lists.Count > 0 || d.Tasks.Count > 0)) return (d, words());
                }
                catch { }
            }
            return (null, "");
        }

        /// <summary>Fixes up what older files lack: list scale names, and every tag on a task known as a tag.</summary>
        private void Tidy()
        {
            Data.Normalize();
            if (Data.Lists.Count == 0) Data.Lists = Sample.Lists();
            // Saved before scales had names: name them from the library.
            foreach (var l in Data.Lists)
                if (l.Scale.Length > 0 && l.ScaleName.Length == 0) l.ScaleName = Scales.NameFor(l.Scale);
            // Every tag on a task is a known tag.
            foreach (var tag in Data.Tasks.Select(t => t.Tag).Where(t => t.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                if (!Data.Tags.Any(x => x.Name.Equals(tag, StringComparison.OrdinalIgnoreCase))) Data.Tags.Add(new TagInfo { Name = tag });
        }

        private DateTime _failLogged = DateTime.MinValue;
        // A damaged tasks.json that could not be copied at start: the first save keeps it under this name.
        private string? _keepDamagedAs;

        /// <summary>Saves, and never throws: a failure is logged, announced through SaveFailed and retried by DataSafety.</summary>
        public void Save() => TrySave();

        /// <summary>Saves now. False when it failed: the change is still in memory, Dirty is set and SaveFailed was raised.</summary>
        public bool TrySave()
        {
            try
            {
                SaveOrThrow();
                LastSaveError = null;
                if (Dirty)
                {
                    Dirty = false;
                    if (PathOverride == null) ErrorLog.Write("Saving tasks worked again.");
                    _failLogged = DateTime.MinValue;
                    SaveRecovered?.Invoke();
                }
                return true;
            }
            catch (Exception e)
            {
                Dirty = true;
                LastSaveError = e.Message;
                // Once per run of failures (and every 10 minutes while it lasts), so retries every few seconds do not fill the log.
                if (PathOverride == null && DateTime.UtcNow - _failLogged > TimeSpan.FromMinutes(10))
                {
                    _failLogged = DateTime.UtcNow;
                    ErrorLog.Write("Saving tasks failed: " + e.GetType().Name + ": " + e.Message);
                }
                try { SaveFailed?.Invoke(e); } catch { }
                return false;
            }
        }

        /// <summary>
        /// Writes tasks.json.new straight through to the disk, then swaps it in, keeping the one before as
        /// tasks.json.bak. If the swap fails halfway, the newest copy is put back in place at once, so there
        /// is always a tasks.json.
        /// </summary>
        private void SaveOrThrow()
        {
            string file = MyFile, fresh = file + ".new";
            // Quick test stores (no backups folder) keep no spare copy, so a self-test leaves nothing behind.
            string? bak = PathOverride != null && BackupOverride == null ? null : file + ".bak";
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            // A swap that failed last time can leave her data only in the new file: put it in place first, never over it.
            if (!File.Exists(file) && File.Exists(fresh)) { try { File.Move(fresh, file); } catch { } }
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Data, Json);
            using (var fs = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.WriteThrough))
            {
                fs.Write(bytes, 0, bytes.Length);
                fs.Flush(true);
            }
            if (File.Exists(file))
            {
                string? keep = _keepDamagedAs ?? bak;
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Replace(fresh, file, keep, ignoreMetadataErrors: true); _keepDamagedAs = null; break; }
                    catch (Exception) when (attempt < 3 && File.Exists(file) && File.Exists(fresh)) { System.Threading.Thread.Sleep(100); }
                    catch
                    {
                        // Halfway: tasks.json may be gone while the new copy (or the one before) is still there.
                        if (!File.Exists(file))
                        {
                            try { if (File.Exists(fresh)) File.Move(fresh, file); else if (bak != null && File.Exists(bak)) File.Copy(bak, file); } catch { }
                        }
                        throw;
                    }
                }
            }
            else File.Move(fresh, file);
            BackupToday(bytes);
        }

        private DateTime _extraTried = DateTime.MinValue;
        private string _extraDone = "";

        private void BackupToday(byte[] bytes)
        {
            if (MyBackups is not string dir) return;
            string name = "tasks " + DateTime.Now.ToString("yyyy-MM-dd") + ".json";
            try
            {
                Directory.CreateDirectory(dir);
                string today = Path.Combine(dir, name);
                if (!File.Exists(today))
                {
                    File.WriteAllBytes(today, bytes);
                    foreach (var f in new DirectoryInfo(dir).GetFiles("tasks ????-??-??.json").OrderByDescending(f => f.Name).Skip(30)) f.Delete();
                }
            }
            catch (Exception e) { if (PathOverride == null) ErrorLog.Write("Daily backup failed: " + e.GetType().Name + ": " + e.Message); }
            // The copy in her own folder: once a day, and after a failure (Google Drive not running) again ten minutes later.
            if (ExtraBackupFolder is string extra && extra.Length > 0 && PathOverride == null && _extraDone != extra + "|" + name && DateTime.Now - _extraTried > TimeSpan.FromMinutes(10))
            {
                _extraTried = DateTime.Now;
                if (CopyBackupTo(extra, bytes, out _)) _extraDone = extra + "|" + name;
            }
        }

        /// <summary>The subfolder of her chosen folder that holds the copies.</summary>
        public static string ExtraFolderIn(string folder) => Path.Combine(folder, "Busy Bunny backups");

        /// <summary>Writes today's backup into folder's Busy Bunny backups subfolder, keeping the last 30 there. False with the reason when it failed.</summary>
        public bool CopyBackupTo(string folder, byte[]? bytes, out string error)
        {
            error = "";
            try
            {
                if (!Directory.Exists(folder)) { error = "that folder is not there right now"; return false; }
                string dir = ExtraFolderIn(folder);
                Directory.CreateDirectory(dir);
                bytes ??= JsonSerializer.SerializeToUtf8Bytes(Data, Json);
                string target = Path.Combine(dir, "tasks " + DateTime.Now.ToString("yyyy-MM-dd") + ".json");
                string part = target + ".part";
                File.WriteAllBytes(part, bytes);
                File.Move(part, target, true);
                foreach (var f in new DirectoryInfo(dir).GetFiles("tasks ????-??-??.json").OrderByDescending(f => f.Name).Skip(30)) f.Delete();
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                if (PathOverride == null) ErrorLog.Write("Copying the backup to her folder failed: " + e.GetType().Name + ": " + e.Message);
                return false;
            }
        }

        /// <summary>Writes the data as it is now to another file, as Export a backup does; works even while saving to tasks.json fails.</summary>
        public void WriteCopy(string path)
        {
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Data, Json);
            string part = path + ".part";
            File.WriteAllBytes(part, bytes);
            File.Move(part, path, true);
        }

        /// <summary>Call before any change: Undo puts every task back the way it was here.</summary>
        public void Remember(string what)
        {
            byte[] packed = Pack(JsonSerializer.SerializeToUtf8Bytes(Data, Json));
            _undo.Add((packed, what));
            _undoBytes += packed.Length;
            while (_undo.Count > 1 && (_undo.Count > 100 || _undoBytes > UndoLimit))
            {
                _undoBytes -= _undo[0].Data.Length;
                _undo.RemoveAt(0);
            }
        }

        private static byte[] Pack(byte[] raw)
        {
            using var ms = new MemoryStream();
            using (var z = new System.IO.Compression.BrotliStream(ms, System.IO.Compression.CompressionLevel.Fastest, true)) z.Write(raw, 0, raw.Length);
            return ms.ToArray();
        }

        private static byte[] Unpack(byte[] packed)
        {
            using var src = new MemoryStream(packed);
            using var z = new System.IO.Compression.BrotliStream(src, System.IO.Compression.CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            return ms.ToArray();
        }

        /// <summary>Reads a data file for a restore: null when it is not Busy Bunny data.</summary>
        public static Data? ReadFile(string path)
        {
            try
            {
                var d = Parse(File.ReadAllText(path));
                return d != null && d.Lists.Count > 0 ? d : null;
            }
            catch { return null; }
        }

        /// <summary>Swaps in other data, as a restore does. Call Remember first so it can be undone.</summary>
        public void ReplaceData(Data d)
        {
            Data = d;
            Tidy();
            Save();
        }

        /// <summary>How many steps Control Z can go back.</summary>
        public int UndoCount => _undo.Count;

        /// <summary>
        /// Puts the data back as it was before the last change, and says what was undone. A few things
        /// are carried over from now, because they are not hers to undo: a list made private stays private
        /// (sweep R3), a reminder that already went off is not brought back to go off again, and nudges
        /// already given stay given.
        /// </summary>
        public string? Undo()
        {
            if (_undo.Count == 0) return null;
            var (packed, what) = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);
            _undoBytes -= packed.Length;
            var back = JsonSerializer.Deserialize<Data>(Unpack(packed), Json)!.Normalize();
            var current = Data;
            var now = DateTime.Now;
            foreach (var l in back.Lists)
                if (current.Lists.Any(c => c.Name == l.Name && c.PrivateFromBunny)) l.PrivateFromBunny = true;
            var byId = current.Tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var t in back.Tasks)
                if (byId.TryGetValue(t.Id, out var c) && t.RemindAt != c.RemindAt && t.RemindAt is DateTime was && was <= now)
                {
                    t.RemindAt = c.RemindAt;
                    t.RemindRepeat = c.RemindRepeat;
                    t.RemindDay = c.RemindDay;
                }
            foreach (var n in current.AccessNudges) if (!back.AccessNudges.Contains(n)) back.AccessNudges.Add(n);
            Data = back;
            Tidy();
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
            var open = Data.Tasks.Where(t => t.Status != Status.Done && Counts(t) && CanShowToday(t, now)).ToList();
            var planned = open.Where(t => PlannedToday(t, now)).ToList();
            var todo = UrgentOrder(planned.Where(t => IsUrgent(t, now)), now)
                .Concat(Reorder(planned.Where(t => !IsUrgent(t, now)).OrderBy(Rank).ToList(), now)).ToList();
            var due = open.Where(t => t.Status != Status.Waiting && DueToday(t, now)).OrderBy(t => t.Due).ThenBy(Rank).ToList();
            return (todo, due);
        }

        /// <summary>
        /// The Priority tab (Morgan's issue 11): every urgent task from every list, whatever day it is
        /// planned for: on Today, top priority on its list, or due within 24 hours (spec 6.4), most urgent first.
        /// </summary>
        public List<TaskItem> Priority(DateTime now) =>
            UrgentOrder(Data.Tasks.Where(t => t.Status != Status.Done && CanShowToday(t, now) && IsUrgent(t, now)), now).ToList();

        /// <summary>Overdue: open, not waiting, and due before today.</summary>
        public static bool IsOverdue(TaskItem t, DateTime now) =>
            t.Status != Status.Done && t.Status != Status.Waiting && t.Due is DateTime d && d.Date < now.Date;

        /// <summary>
        /// Every overdue task from every list that is not archived, oldest first (Morgan's issue 14). Low-energy
        /// mode, a rough day and a list kept out of Today never hide these: an overdue task must never get lost.
        /// </summary>
        public List<TaskItem> Overdue(DateTime now) => Data.Tasks
            .Where(t => IsOverdue(t, now) && Counts(t) && Data.Lists.FirstOrDefault(l => l.Name == t.List)?.Archived != true)
            .OrderBy(t => t.Due).ThenBy(Rank).ToList();

        /// <summary>Everything on Today once, to do first: planned for today, then overdue, then due today.</summary>
        public List<TaskItem> TodayAll(DateTime now)
        {
            var (todo, due) = Today(now);
            var all = todo.ToList();
            var seen = new HashSet<TaskItem>(all);
            foreach (var t in Overdue(now).Concat(due)) if (seen.Add(t)) all.Add(t);
            return all;
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
            var today = new HashSet<TaskItem>(TodayAll(now));
            return Data.Tasks.Where(t => !today.Contains(t) && CanShowToday(t, now) && (PlanGroup(t, now) == Status.ThisWeek || DueGroup(t, now) == Status.ThisWeek))
                .OrderBy(t => t.DoDate ?? t.Due ?? DateTime.MaxValue).ThenBy(Rank).ToList();
        }

        /// <summary>Hours available today: what she set for the day, or her usual.</summary>
        public double HoursToday(DateTime now) =>
            Data.Days.TryGetValue(Key(now), out var d) && d.Hours > 0 ? d.Hours : Data.HoursAvailable;

        /// <summary>What counts toward today: everything on Today plus what was finished today.</summary>
        public (int Done, int Total, int PointsDone, int PointsTotal) Progress(DateTime now) => Progress(now, TodayAll(now));

        private (int Done, int Total, int PointsDone, int PointsTotal) Progress(DateTime now, List<TaskItem> open)
        {
            var finished = Data.Tasks.Where(t => t.Status == Status.Done && t.DoneAt?.Date == now.Date && Counts(t)).ToList();
            var all = open.Concat(finished).ToList();
            // Done but not submitted earns half (spec 5.4).
            int pointsDone = finished.Sum(t => Words.Points(t.Size)) + open.Where(t => t.Status == Status.DoneNotSubmitted).Sum(t => Words.Points(t.Size)) / 2;
            // I did a thing (spec 12.12): things logged today count as done items and points.
            var things = Data.Log.Where(l => l.Kind == "DidAThing" && l.When.Date == now.Date).ToList();
            return (finished.Count + things.Count, all.Count + things.Count, pointsDone + things.Sum(l => l.Points), all.Sum(t => Words.Points(t.Size)) + things.Sum(l => l.Points));
        }

        /// <summary>The Bunny set for the day as a whole (spec 6.2f), worked out from urgent tasks and hours of work.</summary>
        public string DayType(DateTime now) => DayType(now, null);

        private string DayType(DateTime now, List<TaskItem>? todayAll)
        {
            if (Data.Days.TryGetValue(Key(now), out var set) && set != null && set.DayType.Length > 0) return set.DayType;
            var all = todayAll ?? TodayAll(now);
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
            // Today's list is worked out once here: with thousands of tasks it is the slow part.
            var todayAll = TodayAll(now);
            var (done, total, pd, pt) = Progress(now, todayAll);
            var parts = new List<string> { done + " of " + total + " done today" };
            int late = Overdue(now).Count;
            if (late > 0) parts.Add(late + " overdue");
            if (!shortMode && pt > 0) parts.Add(pd * 100 / pt + " percent of today's effort");
            // A meeting with no time counts for its whole day, and is said without a time (not 12 am).
            var meeting = Data.Tasks.Where(t => t.IsMeeting && t.Status != Status.Done && t.Due is DateTime d && (t.DueHasTime ? d > now : d.Date >= now.Date) && Counts(t))
                .OrderBy(t => t.DueHasTime ? t.Due : t.Due!.Value.Date.AddDays(1).AddTicks(-1)).FirstOrDefault();
            if (meeting?.Due is DateTime md)
            {
                if (meeting.DueHasTime)
                {
                    string when = md.Date == now.Date ? "at " + Words.Time(md) : Words.Day(md, now) + " at " + Words.Time(md);
                    parts.Add("next meeting " + when + (md.Date == now.Date && !shortMode ? ", in " + Gap(md - now) : ""));
                }
                else parts.Add("next meeting " + Words.Day(md, now));
            }
            foreach (var part in SummaryParts)
                if (part(now, shortMode) is string p && p.Length > 0) parts.Add(p);
            if (!shortMode) parts.Add("a " + DayType(now, todayAll) + " day");
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
            if (t.Due is DateTime d && IsOverdue(t, now) && !t.IsMeeting)
                parts.Add(shortMode ? "overdue, due " + Words.Day(d, now) : "overdue, it was due " + Words.Day(d, now));
            else if (t.Due is DateTime d2)
                parts.Add((t.IsMeeting ? "" : "due ") + Words.Day(d2, now) + (t.DueHasTime ? " at " + Words.Time(d2) : ""));
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
