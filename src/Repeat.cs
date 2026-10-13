using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace BusyBunny
{
    /// <summary>
    /// A repeat rule in plain words (spec 8.3): every day, every weekday, every Monday, every 2 weeks,
    /// every month, every Monday and Wednesday. Parse reads the words; Next finds the following date.
    /// </summary>
    public sealed class RepeatRule
    {
        public enum Unit { Day, Week, Month, Year }

        public Unit Per { get; init; }
        public int Every { get; init; } = 1;
        /// <summary>Days of the week it lands on (weekly rules only), or empty for the same weekday as before.</summary>
        public List<DayOfWeek> Days { get; init; } = new();

        static readonly DayOfWeek[] Weekdays = { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };

        /// <summary>Reads the words, or null when they are not a repeat rule. Empty text is not a rule.</summary>
        public static RepeatRule? Parse(string? text)
        {
            string t = (text ?? "").Trim().ToLowerInvariant().Replace(",", " ").Replace("&", " and ");
            t = Regex.Replace(t, @"\s+", " ");
            if (t.Length == 0) return null;
            switch (t)
            {
                case "daily": t = "every day"; break;
                case "weekly": t = "every week"; break;
                case "monthly": t = "every month"; break;
                case "yearly" or "annually": t = "every year"; break;
                case "weekdays": t = "every weekday"; break;
            }
            if (!t.StartsWith("every ")) return null;
            string rest = t[6..].Trim();
            if (rest == "weekday") return new RepeatRule { Per = Unit.Week, Days = Weekdays.ToList() };
            if (rest == "weekend") return new RepeatRule { Per = Unit.Week, Days = { DayOfWeek.Saturday, DayOfWeek.Sunday } };

            // every day / every 3 days / every other week / every 2 weeks
            var m = Regex.Match(rest, @"^(?:(other|\d{1,3})\s+)?(day|week|month|year)s?$");
            if (m.Success)
            {
                int n = 1;
                if (m.Groups[1].Value == "other") n = 2;
                else if (m.Groups[1].Success) n = int.Parse(m.Groups[1].Value);
                if (n < 1) return null;
                var per = m.Groups[2].Value switch { "day" => Unit.Day, "week" => Unit.Week, "month" => Unit.Month, _ => Unit.Year };
                return new RepeatRule { Per = per, Every = n };
            }

            // every 2 weeks on Tuesday / every other week on Monday and Thursday
            int everyWeeks = 1;
            var on = Regex.Match(rest, @"^(other|\d{1,3})\s+weeks?\s+on\s+(.+)$");
            if (on.Success)
            {
                everyWeeks = on.Groups[1].Value == "other" ? 2 : int.Parse(on.Groups[1].Value);
                if (everyWeeks < 1) return null;
                rest = on.Groups[2].Value;
            }
            else if (rest.StartsWith("week on ")) rest = rest[8..];

            // every Monday / every Monday and Wednesday / every mon wed fri
            var days = new List<DayOfWeek>();
            foreach (string word in rest.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (word == "and") continue;
                string w = word.TrimEnd('s');
                var d = Enum.GetValues<DayOfWeek>().Cast<DayOfWeek?>().FirstOrDefault(x =>
                    x.ToString()!.ToLowerInvariant() is string full && (w == full || w.Length >= 3 && full.StartsWith(w)));
                if (d == null) return null;
                if (!days.Contains(d.Value)) days.Add(d.Value);
            }
            if (days.Count == 0) return null;
            return new RepeatRule { Per = Unit.Week, Days = days, Every = everyWeeks };
        }

        /// <summary>The next date after from (time of day kept), following the rule.</summary>
        public DateTime Next(DateTime from)
        {
            switch (Per)
            {
                case Unit.Day: return from.AddDays(Every);
                case Unit.Month: return from.AddMonths(Every);
                case Unit.Year: return from.AddYears(Every);
            }
            if (Days.Count == 0) return from.AddDays(7 * Every);
            // Weekly on named days: the next such day, later in this week, or the first of them Every weeks on.
            for (int i = 1; i <= 7; i++)
            {
                var d = from.AddDays(i);
                if (!Days.Contains(d.DayOfWeek)) continue;
                // Past the end of the week it started in (Sunday to Monday), skip ahead whole weeks.
                bool wrapped = d.DayOfWeek == DayOfWeek.Monday || WeekIndex(d) != WeekIndex(from);
                return wrapped && Every > 1 ? d.AddDays(7 * (Every - 1)) : d;
            }
            return from.AddDays(7 * Every);
        }

        private static int WeekIndex(DateTime d) => (int)Math.Floor(((d.Date - new DateTime(2000, 1, 3)).TotalDays) / 7);

        /// <summary>The first date on or after floor that the rule gives, starting from from.</summary>
        public DateTime NextOnOrAfter(DateTime from, DateTime floor)
        {
            var d = Next(from);
            for (int guard = 0; d.Date < floor.Date && guard < 5000; guard++) d = Next(d);
            return d;
        }

        public string Describe()
        {
            if (Per == Unit.Week && Days.Count > 0)
            {
                string names = string.Join(" and ", Days.OrderBy(d => ((int)d + 6) % 7).Select(d => d.ToString()));
                if (Every > 1) return "every " + Every + " weeks on " + names;
                if (Days.OrderBy(x => x).SequenceEqual(Weekdays.OrderBy(x => x))) return "every weekday";
                return "every " + names;
            }
            string unit = Per.ToString().ToLowerInvariant();
            return Every == 1 ? "every " + unit : "every " + Every + " " + unit + "s";
        }

        /// <summary>
        /// The name for the next copy: the last number in the name moved on by one, like
        /// PSY chapter 9 readings to PSY chapter 10 readings. Unchanged when it has no number.
        /// </summary>
        public static string NextName(string name)
        {
            var m = Regex.Match(name, @"\d+(?!.*\d)");
            if (!m.Success || !int.TryParse(m.Value, out int n)) return name;
            return name[..m.Index] + (n + 1) + name[(m.Index + m.Length)..];
        }

        /// <summary>Whether this text is blank or a rule: for checking what was typed.</summary>
        public static bool IsValid(string? text) => string.IsNullOrWhiteSpace(text) || Parse(text) != null;

        /// <summary>The next copy of a repeating task: dates moved on, subtasks unchecked, comments and finish marks cleared. Null when it has no repeat.</summary>
        public static TaskItem? NextCopy(TaskItem t, DateTime now, Status startStatus)
        {
            var rule = Parse(t.Repeat);
            if (rule == null) return null;
            var anchor = t.Due ?? t.DoDate ?? now.Date;
            var next = rule.NextOnOrAfter(anchor, now);
            var shift = next - anchor;
            var c = t.Copy();
            c.Id = Guid.NewGuid();
            if (t.Due != null) c.Due = next;
            if (t.DoDate != null) c.DoDate = t.DoDate.Value + shift;
            if (t.RemindAt != null) c.RemindAt = t.RemindAt.Value + shift;
            if (t.Due == null && t.DoDate == null) c.DoDate = next;
            c.Status = startStatus;
            c.DoneAt = null;
            c.StatusSince = now;
            c.CreatedAt = now;
            c.TouchedAt = now;
            c.Snoozes = 0;
            c.Comments = new();
            foreach (var s in c.Subtasks) s.Done = false;
            return c;
        }

        public static void SelfTest()
        {
            void Same<T>(T got, T want, string what) { if (!Equals(got, want)) throw new Exception("Repeat: " + what + ": got " + got + ", wanted " + want); }
            var mon = new DateTime(2026, 10, 12); // a Monday
            Same(mon.DayOfWeek, DayOfWeek.Monday, "test date is a Monday");
            Same(Parse("every day")!.Next(mon), mon.AddDays(1), "every day");
            Same(Parse("Daily")!.Next(mon), mon.AddDays(1), "daily");
            Same(Parse("every 3 days")!.Next(mon), mon.AddDays(3), "every 3 days");
            Same(Parse("every weekday")!.Next(new DateTime(2026, 10, 16)), new DateTime(2026, 10, 19), "weekday from Friday");
            Same(Parse("every weekday")!.Next(mon), mon.AddDays(1), "weekday from Monday");
            Same(Parse("every Monday")!.Next(mon), mon.AddDays(7), "every Monday");
            Same(Parse("every monday and wednesday")!.Next(mon), mon.AddDays(2), "Mon and Wed from Mon");
            Same(Parse("every Monday and Wednesday")!.Next(mon.AddDays(2)), mon.AddDays(7), "Mon and Wed from Wed");
            Same(Parse("every 2 weeks")!.Next(mon), mon.AddDays(14), "every 2 weeks");
            Same(Parse("every 2 weeks on Tuesday")!.Next(mon.AddDays(1)), mon.AddDays(15), "every 2 weeks on Tuesday");
            if (Parse("every other week on monday and thursday")!.Describe() != "every 2 weeks on Monday and Thursday") throw new Exception("every other week on days reads wrong");
            if (Parse(Parse("every 2 weeks on Tuesday")!.Describe())!.Every != 2) throw new Exception("every 2 weeks on Tuesday does not round-trip");
            if (NextName("PSY chapter 9 readings") != "PSY chapter 10 readings") throw new Exception("next name did not count on");
            if (NextName("weekly reading") != "weekly reading") throw new Exception("next name changed a name with no number");
            Same(Parse("every other week")!.Every, 2, "every other week");
            Same(Parse("every month")!.Next(new DateTime(2026, 1, 31)), new DateTime(2026, 2, 28), "month end clamps");
            Same(Parse("every year")!.Next(mon), mon.AddYears(1), "every year");
            Same(Parse("every mon, wed, fri")!.Days.Count, 3, "short names with commas");
            Same(Parse("every Tuesday")!.Describe(), "every Tuesday", "describe day");
            Same(Parse("every 2 weeks")!.Describe(), "every 2 weeks", "describe weeks");
            Same(Parse("every weekday")!.Describe(), "every weekday", "describe weekday");
            if (Parse("whenever") != null || Parse("") != null || Parse("every blorp") != null || Parse("every 0 days") != null) throw new Exception("Repeat: nonsense was accepted");
            Same(IsValid(""), true, "blank is fine");

            var t = new TaskItem
            {
                Name = "weekly reading", Repeat = "every Monday", Due = mon.AddHours(15), DueHasTime = true, Status = Status.Done,
                RemindAt = mon.AddHours(9), Subtasks = { new Subtask { Name = "a", Done = true } }, Comments = { new Comment { Text = "hi" } },
            };
            var c = NextCopy(t, mon.AddHours(16), Status.ThisWeek)!;
            Same(c.Due, mon.AddDays(7).AddHours(15), "next due");
            Same(c.RemindAt, mon.AddDays(7).AddHours(9), "next reminder");
            Same(c.Subtasks[0].Done, false, "subtasks unchecked");
            Same(c.Comments.Count, 0, "comments cleared");
            Same(c.Status, Status.ThisWeek, "start status");
            if (c.Id == t.Id) throw new Exception("Repeat: copy kept the same id");
            // Finished weeks late: the next one is not in the past.
            var late = NextCopy(t, mon.AddDays(30), Status.ThisWeek)!;
            if (late.Due!.Value.Date < mon.AddDays(30)) throw new Exception("Repeat: next copy landed in the past");
            if (NextCopy(new TaskItem(), mon, Status.ThisWeek) != null) throw new Exception("Repeat: copy of a non-repeating task");
        }
    }
}
