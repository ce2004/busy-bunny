using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BusyBunny
{
    /// <summary>One calendar event (sweep 8.9): the words it comes with on GameEvents.CalendarDay, the tool whose switch it follows, and its one-day prize.</summary>
    public sealed record SeasonEvent(string Id, string Words, string Group, int Prize, string PrizeTopic = "season.prize");

    /// <summary>Her dates for the seasons and festivals: kept with the world's bookkeeping (like a setting, never undone).</summary>
    public sealed class SeasonPrefs
    {
        /// <summary>Her birthday from Settings, month and day, or 0 for none (then About me is asked).</summary>
        public int BirthMonth { get; set; }
        public int BirthDay { get; set; }
        /// <summary>Bunny's birthday: March 21, near the start of spring, unless she picks another.</summary>
        public int BunnyMonth { get; set; } = 3;
        public int BunnyDay { get; set; } = 21;
        public DateTime? SemesterStart { get; set; }
        public DateTime? SemesterEnd { get; set; }
        /// <summary>Off unless she turns it on: then Smart Bunny may read her full moon intention to answer it.</summary>
        public bool MoonToClaude { get; set; }
    }

    /// <summary>
    /// The calendar of Bunny's world (sweep 8.9, 8.6, 6.1, 13.3): which special days fall on a date, raising the ones the
    /// engine does not know as CalendarDay events, keeping today's events, saying them once through Calm when the calm rules
    /// allow, and paying each day's one prize through the Ledger. Holidays are real US holidays worked out from the date;
    /// her birthday only comes from Settings or her About me notes, never from the code.
    /// </summary>
    public static class SeasonCalendar
    {
        public const string Tool = "season", Village = "season.village", Weather = "season.window", Winter = "season.winter",
            Yearly = "season.yearly", Semester = "season.semester", Moon = "season.moon";
        private const string PrefsKey = "season.prefs", TodayKey = "season.today", MarksKey = "season.marks", PrizeBag = "season.prize";

        public static readonly SeasonEvent[] Catalog =
        {
            new("newyear", "New Year's Day", Tool, 5), new("valentine", "Valentine's Day", Tool, 5), new("stpatrick", "Saint Patrick's Day", Tool, 5),
            new("aprilfools", "April Fools' Day", Tool, 5), new("mlk", "Martin Luther King Jr. Day", Tool, 5), new("presidents", "Presidents' Day", Tool, 5),
            new("mothers", "Mother's Day", Tool, 5), new("memorial", "Memorial Day", Tool, 5), new("juneteenth", "Juneteenth", Tool, 5),
            new("july4", "The Fourth of July", Tool, 5), new("fathers", "Father's Day", Tool, 5), new("labor", "Labor Day", Tool, 5),
            new("halloween", "Halloween", Tool, 5), new("veterans", "Veterans Day", Tool, 5), new("thanksgiving", "Thanksgiving", Tool, 5),
            new("christmaseve", "Christmas Eve", Tool, 5), new("christmas", "Christmas Day", Tool, 5), new("newyearseve", "New Year's Eve", Tool, 5),
            new("winter", "Winter begins", Tool, 5), new("spring", "Spring begins", Tool, 5), new("summer", "Summer begins", Tool, 5), new("fall", "Fall begins", Tool, 5),
            new("pumpkin", "The pumpkin patch opens", Tool, 5), new("harvest", "The harvest festival", Tool, 5), new("kite", "The kite festival", Tool, 5),
            new("cleaning", "Spring cleaning day", Tool, 5), new("icecream", "The ice cream truck", Tool, 3, "season.icecream"),
            new("gamesday", "Village games day", Village, 5), new("fishing", "The fishing tournament", Village, 5), new("gardenclub", "The garden club contest", Village, 5),
            new("market", "The Friday night market", Village, 3), new("bonfire", "Bonfire and s'mores", Village, 5), new("campfire", "Campfire night", Village, 3),
            new("fog", "A foggy morning", Weather, 3, "season.fog"), new("rain", "A rainy day", Weather, 3, "season.rainbow"), new("storm", "A thunderstorm", Weather, 3, "season.storm"),
            new("picnic", "A picnic day", Weather, 3), new("clouds", "A cloud watching day", Weather, 2),
            new("snowday", "A snow day", Winter, 5), new("snowball", "A snowball fight", Winter, 5),
            new("herbirthday", "Your birthday", Yearly, 10), new("bunnybirthday", "Bunny's birthday", Yearly, 10),
            new("anniversary", "The village anniversary", Yearly, 10), new("moving", "Moving day", Yearly, 10),
            new("semesterend", "The end of the semester", Semester, 10),
            new("fullmoon", "The full moon", Moon, 3),
        };

        public static SeasonEvent? ByWords(string words) => Catalog.FirstOrDefault(e => e.Words.Equals(words.Trim(), StringComparison.OrdinalIgnoreCase));
        public static SeasonEvent? ById(string id) => Catalog.FirstOrDefault(e => e.Id == id);

        /// <summary>The event's touch and sound words from season.events, with its name, and her bunny's name filled in.</summary>
        public static Dictionary<string, string> Info(string id)
        {
            var item = Content.Items("season.events").FirstOrDefault(i => i.TryGetValue("id", out var x) && x == id);
            if (item == null) return new Dictionary<string, string>();
            string bunny = BunnyName(Engines.Store);
            return item.ToDictionary(kv => kv.Key, kv => kv.Value.Replace("{name}", bunny));
        }

        /// <summary>Her bunny's name for filling in content, or plain Bunny with no store or no name yet.</summary>
        public static string BunnyName(Store? s) => s?.Data.Game.BunnyName is string n && n.Trim().Length > 0 ? n : "Bunny";

        public static string Name(SeasonEvent e) => Info(e.Id).TryGetValue("name", out var n) && n.Length > 0 ? n : e.Words;

        // ---- Her dates ----

        public static SeasonPrefs Prefs(Store s) => s.World.GetKept<SeasonPrefs>(PrefsKey);
        public static void SavePrefs(Store s, SeasonPrefs p) { s.World.SetKept(PrefsKey, p); s.World.Save(); }

        private static readonly string[] Months = CultureInfo.InvariantCulture.DateTimeFormat.MonthNames.Take(12).ToArray();

        /// <summary>A month and day typed like "March 3", "Mar 3rd", "3/3" or "3 March". Null when it is not one.</summary>
        public static (int Month, int Day)? ParseMonthDay(string text)
        {
            string t = text.Trim().ToLowerInvariant().Replace(",", " ").Replace(".", " ");
            if (t.Length == 0) return null;
            var num = Regex.Match(t, @"^(\d{1,2})\s*[/-]\s*(\d{1,2})(\s*[/-]\s*\d{2,4})?$");
            if (num.Success) return Check(int.Parse(num.Groups[1].Value), int.Parse(num.Groups[2].Value));
            for (int m = 0; m < 12; m++)
            {
                string full = Months[m].ToLowerInvariant(), shortName = full[..3];
                var a = Regex.Match(t, @"\b(" + full + "|" + shortName + @")[a-z]*\s+(\d{1,2})(st|nd|rd|th)?\b");
                if (a.Success) return Check(m + 1, int.Parse(a.Groups[2].Value));
                var b = Regex.Match(t, @"\b(\d{1,2})(st|nd|rd|th)?\s+(of\s+)?(" + full + "|" + shortName + @")[a-z]*\b");
                if (b.Success) return Check(m + 1, int.Parse(b.Groups[1].Value));
            }
            return null;
            static (int, int)? Check(int m, int d) => m >= 1 && m <= 12 && d >= 1 && d <= DateTime.DaysInMonth(2024, m) ? (m, d) : null;
        }

        public static string MonthDay(int month, int day) => Months[Math.Clamp(month, 1, 12) - 1] + " " + day;

        /// <summary>Her birthday: from Settings, or else a note she gave Bunny (About me) that names her birthday. Null when neither has one.</summary>
        public static (int Month, int Day)? Birthday(Store s)
        {
            var p = Prefs(s);
            if (p.BirthMonth > 0 && p.BirthDay > 0) return (p.BirthMonth, p.BirthDay);
            foreach (var note in s.Data.BunnyNotes)
                if (BirthdayIn(note) is (int m, int d)) return (m, d);
            return null;
        }

        /// <summary>
        /// Her own birthday in one About me note: only a note that says "my birthday", "her birthday", "I was born" or "born on",
        /// so "my mom's birthday" never counts. Every spot after those words is tried, so "the 5th of May" is found too.
        /// </summary>
        public static (int Month, int Day)? BirthdayIn(string note)
        {
            var own = Regex.Match(note ?? "", @"\b(my|her)\s+birthday\b|\bi\s+was\s+born\b|\bborn\s+on\b", RegexOptions.IgnoreCase);
            if (!own.Success) return null;
            var words = Regex.Matches(note![own.Index..], @"\S+").Select(m => m.Value).ToList();
            for (int i = 0; i < words.Count; i++)
            {
                if (ParseMonthDay(words[i]) is (int m1, int d1)) return (m1, d1);
                if (ParseMonthDay(string.Join(" ", words.Skip(i).Take(3))) is (int m, int d)) return (m, d);
            }
            return null;
        }

        /// <summary>The day she first used Busy Bunny: her oldest log entry, else today.</summary>
        public static DateTime FirstDay(Store s) => s.Data.Log.Count == 0 ? DateTime.Today : s.Data.Log.Min(e => e.When).Date;

        private static bool Is(DateTime d, int month, int day) =>
            d.Month == month && (d.Day == day || month == 2 && day == 29 && !DateTime.IsLeapYear(d.Year) && d.Day == 28);

        // ---- US holidays ----

        /// <summary>The nth weekday of a month (n = 1 is the first).</summary>
        public static DateTime Nth(int year, int month, DayOfWeek dow, int n)
        {
            var d = new DateTime(year, month, 1);
            while (d.DayOfWeek != dow) d = d.AddDays(1);
            return d.AddDays(7 * (n - 1));
        }

        public static DateTime Last(int year, int month, DayOfWeek dow)
        {
            var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
            while (d.DayOfWeek != dow) d = d.AddDays(-1);
            return d;
        }

        /// <summary>
        /// The special days on a date from the calendar alone (weather and streak days come by themselves). With engine true,
        /// also the ones the engine raises (fixed holidays and a new season), for the coming-up list.
        /// </summary>
        public static List<string> DaysOn(Store s, DateTime day, bool engine)
        {
            var d = day.Date;
            int y = d.Year;
            var on = new List<string>();
            if (engine)
            {
                if (RealWorld.Holiday(d) is string h) on.Add(h);
                if (d.Day == 1 && d.Month % 3 == 0) on.Add(RealWorld.Season(d) + " begins");
            }
            if (Is(d, 6, 19)) on.Add("Juneteenth");
            if (Is(d, 7, 4)) on.Add("The Fourth of July");
            if (Is(d, 11, 11)) on.Add("Veterans Day");
            if (d == Nth(y, 1, DayOfWeek.Monday, 3)) on.Add("Martin Luther King Jr. Day");
            if (d == Nth(y, 2, DayOfWeek.Monday, 3)) on.Add("Presidents' Day");
            if (d == Nth(y, 5, DayOfWeek.Sunday, 2)) on.Add("Mother's Day");
            if (d == Last(y, 5, DayOfWeek.Monday)) on.Add("Memorial Day");
            if (d == Nth(y, 6, DayOfWeek.Sunday, 3)) on.Add("Father's Day");
            if (d == Nth(y, 9, DayOfWeek.Monday, 1)) on.Add("Labor Day");
            if (d == Nth(y, 11, DayOfWeek.Thursday, 4)) on.Add("Thanksgiving");
            if (Is(d, 10, 1)) on.Add("The pumpkin patch opens");
            if (d == Last(y, 9, DayOfWeek.Saturday)) on.Add("The harvest festival");
            if (d == Nth(y, 4, DayOfWeek.Saturday, 2)) on.Add("The kite festival");
            if (d == Nth(y, 3, DayOfWeek.Saturday, 3)) on.Add("Spring cleaning day");
            if (RealWorld.Season(d) == "Summer" && d.DayOfWeek == DayOfWeek.Sunday) on.Add("The ice cream truck");
            // Village days: a weekend a month each (Saturday and Sunday), so one busy Saturday never means missing it.
            var sat = d.DayOfWeek == DayOfWeek.Sunday ? d.AddDays(-1) : d;
            if (sat.DayOfWeek == DayOfWeek.Saturday)
            {
                int n = (sat.Day - 1) / 7 + 1;
                if (n == 1) on.Add("Village games day");
                if (n == 2) on.Add("The garden club contest");
                if (n == 3) on.Add("The fishing tournament");
            }
            if (d.DayOfWeek == DayOfWeek.Friday) on.Add("The Friday night market");
            if (d.DayOfWeek == DayOfWeek.Saturday) on.Add("Campfire night");
            if (Birthday(s) is (int bm, int bd) && Is(d, bm, bd)) on.Add("Your birthday");
            var p = Prefs(s);
            if (Is(d, p.BunnyMonth, p.BunnyDay)) on.Add("Bunny's birthday");
            var first = FirstDay(s);
            if (d.Year > first.Year && Is(d, first.Month, first.Day)) on.Add("The village anniversary");
            if (Is(d, 6, 1) && (d - first).TotalDays >= 60) on.Add("Moving day");
            if (p.SemesterEnd is DateTime end && d == end.Date) on.Add("The end of the semester");
            return on;
        }

        /// <summary>A good week so far: her daily goal met on 3 days, or 5 tasks finished since Monday.</summary>
        public static bool GoodWeek(Store s, DateTime now)
        {
            var mon = Engine.WeekStart(now);
            int goals = s.Data.Game.GoalDays.Count(k => DateTime.TryParse(k, out var x) && x >= mon && x <= now.Date);
            int done = s.Data.Tasks.Count(t => t.DoneAt is DateTime at && at >= mon && at <= now);
            return goals >= 3 || done >= 5;
        }

        /// <summary>The semester is over and the last week has not passed: the lantern walk, the train and the butterflies.</summary>
        public static bool SemesterJustEnded(Store s, DateTime now) => Prefs(s).SemesterEnd is DateTime end && now.Date >= end.Date && now.Date <= end.Date.AddDays(7);

        // ---- Today's events ----

        public sealed class Today
        {
            public string Day { get; set; } = "";
            public List<string> On { get; set; } = new();
            /// <summary>Waiting to be said once, when the calm rules allow.</summary>
            public List<string> Waiting { get; set; } = new();
        }

        public sealed class Marks { public Dictionary<string, string> Raised { get; set; } = new(); }

        public sealed class PrizeDay { public string Day { get; set; } = ""; public Guid TaskId { get; set; } public string Event { get; set; } = ""; }

        public static Today TodayOf(Store s, DateTime now)
        {
            var t = s.World.GetKept<Today>(TodayKey);
            return t.Day == s.Key(now) ? t : new Today { Day = s.Key(now) };
        }

        /// <summary>The events on today whose tool is on.</summary>
        public static List<SeasonEvent> OnToday(Store s, DateTime now) =>
            TodayOf(s, now).On.Select(ById).Where(e => e != null && Tools.IsOn(e.Group)).Select(e => e!).ToList();

        public static bool IsOn(Store s, DateTime now, string id) => OnToday(s, now).Any(e => e.Id == id);

        /// <summary>Marks an event as on today. Waits to be said when announce is true. False when it was already on.</summary>
        public static bool Activate(Store s, SeasonEvent e, DateTime now, bool announce)
        {
            var t = TodayOf(s, now);
            if (t.On.Contains(e.Id)) return false;
            t.On.Add(e.Id);
            if (announce) t.Waiting.Add(e.Id);
            s.World.SetKept(TodayKey, t);
            s.World.Save();
            return true;
        }

        /// <summary>Raises a CalendarDay once per occurrence key (a day or a week), so a restart never repeats it.</summary>
        public static bool RaiseOnce(Store s, string words, string occurrence)
        {
            var m = s.World.GetKept<Marks>(MarksKey);
            if (m.Raised.TryGetValue(words, out var was) && was == occurrence) return false;
            m.Raised[words] = occurrence;
            s.World.SetKept(MarksKey, m);
            s.World.Save();
            GameEvents.RaiseCalendarDay(words);
            return true;
        }

        /// <summary>What is said for an event: its name, then how it feels and sounds.</summary>
        public static string Announcement(SeasonEvent e, string where)
        {
            var i = Info(e.Id);
            string touch = i.TryGetValue("touch", out var a) ? a : "", sound = i.TryGetValue("sound", out var b) ? b : "";
            return Sentence(Name(e)) + " " + touch + (touch.Length > 0 && sound.Length > 0 ? " " : "") + sound + (where.Length > 0 ? " " + where : "");
        }

        private static string Sentence(string s) => s.Length == 0 ? s : s.EndsWith('.') ? s : s + ".";

        public static string Where(SeasonEvent e) => e.Group switch
        {
            Weather => "It is at the round window in Bunny's world.",
            Moon => "It is on the moonlit hill in Bunny's world.",
            _ => "It is on the village green in Bunny's world.",
        };

        /// <summary>
        /// Says the first event waiting, once, when the calm rules allow (never in class, quiet hours or a sprint, and under
        /// today's cap). say is Calm.SayUnprompted in the app. The rest wait for the next chance; tomorrow they are dropped
        /// (they were kept in What happened today when they began).
        /// </summary>
        public static bool TrySay(Store s, Settings? cfg, DateTime now, Func<string, bool> say)
        {
            var t = TodayOf(s, now);
            if (t.Waiting.Count == 0 || Calm.FunPaused || !Calm.CanSpeakUnprompted(s, cfg, now)) return false;
            var events = t.Waiting.Select(ById).Where(e => e != null && Tools.IsOn(e.Group)).Select(e => e!).ToList();
            t.Waiting.Clear();
            s.World.SetKept(TodayKey, t);
            s.World.Save();
            if (events.Count == 0) return false;
            string line = Announcement(events[0], Where(events[0]));
            if (events.Count > 1) line += " Also today: " + string.Join(", ", events.Skip(1).Select(Name)) + ".";
            return say(line);
        }

        // ---- The one-day prize ----

        /// <summary>
        /// The first task she finishes on a day with an event on earns that day's prize once, through the Ledger with the
        /// task's id (un-finishing it takes the points back). Returns the event and its outcome, or null.
        /// </summary>
        public static (SeasonEvent Event, Outcome? Outcome)? Prize(Store s, TaskItem task, DateTime now)
        {
            var on = OnToday(s, now);
            if (on.Count == 0) return null;
            var bag = s.World.Get<PrizeDay>(PrizeBag);
            if (bag.Day == s.Key(now)) return null;
            var top = on.OrderByDescending(e => e.Prize).First();
            bag.Day = s.Key(now);
            bag.TaskId = task.Id;
            bag.Event = top.Id;
            s.World.Set(PrizeBag, bag);
            s.World.Save();
            var o = Ledger.Pay(s, top.Prize, "the prize for " + Lower(Name(top)), Tool, task.Id);
            return (top, o);
        }

        private static string Lower(string name) => name.StartsWith("The ") || name.StartsWith("A ") ? char.ToLowerInvariant(name[0]) + name[1..] : name;

        /// <summary>The finished task that won today's prize went back: the prize can be won again (its points went back with it).</summary>
        public static void TakeBack(Store s, TaskItem task)
        {
            var bag = s.World.Get<PrizeDay>(PrizeBag);
            if (bag.TaskId != task.Id || bag.Day.Length == 0) return;
            s.World.Set(PrizeBag, new PrizeDay());
            s.World.Save();
        }

        /// <summary>The line said after the prize: the event's own topic, or a cheer named after it.</summary>
        public static string PrizeLine(Store s, SeasonEvent e)
        {
            string line = Content.Pick(s, e.PrizeTopic);
            return e.PrizeTopic == "season.prize" ? Name(e) + ": " + line : line;
        }

        // ---- Coming up ----

        /// <summary>The special days in the next weeks, soonest first, one line each, like "Thanksgiving, in 22 days".</summary>
        public static List<string> ComingUp(Store s, DateTime now, int days = 45)
        {
            var lines = new List<string>();
            var weekly = new HashSet<string> { "The Friday night market", "Campfire night", "The ice cream truck" };
            for (int i = 0; i <= days && lines.Count < 14; i++)
            {
                var d = now.Date.AddDays(i);
                foreach (var w in DaysOn(s, d, engine: true))
                {
                    if (weekly.Contains(w) && i > 6) continue;
                    if (ByWords(w) is not SeasonEvent e || !Tools.IsOn(e.Group)) continue;
                    // A village weekend is listed once, on its Saturday.
                    if (e.Group == Village && d.DayOfWeek == DayOfWeek.Sunday && i > 0 && !weekly.Contains(w)) continue;
                    lines.Add(Name(e) + ", " + Words.Day(d, now.Date));
                }
            }
            return lines;
        }
    }
}
