using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace BusyBunny
{
    /// <summary>
    /// Reading what Morgan types, forgivingly: misspellings (tuesda, tomorow, wensday, thrusday),
    /// short forms (tmrw, thurs, sept), and many ways of saying a date, a time or a repeat. Used by
    /// Dates.Parse, RepeatRule.Parse, Paste tasks and Quick add.
    /// </summary>
    public static class Fuzzy
    {
        /// <summary>Edits between two words: letters added, removed, changed, or two swapped.</summary>
        public static int Distance(string a, string b)
        {
            var d = new int[a.Length + 1, b.Length + 1];
            for (int i = 0; i <= a.Length; i++) d[i, 0] = i;
            for (int j = 0; j <= b.Length; j++) d[0, j] = j;
            for (int i = 1; i <= a.Length; i++)
                for (int j = 1; j <= b.Length; j++)
                {
                    int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                    d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                    if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            return d[a.Length, b.Length];
        }

        /// <summary>
        /// The word from the list this one was meant to be, or null when none is close enough or two
        /// are equally close. Short words must match exactly: three letters is too little to guess from.
        /// </summary>
        public static string? Best(string word, IEnumerable<string> words)
        {
            word = word.ToLowerInvariant();
            int allowed = word.Length <= 3 ? 0 : word.Length <= 5 ? 1 : 2;
            string? best = null;
            int bestD = int.MaxValue;
            bool tie = false;
            foreach (var w in words)
            {
                string c = w.ToLowerInvariant();
                if (c == word) return w;
                if (Math.Abs(c.Length - word.Length) > allowed) continue;
                int dist = Distance(word, c);
                if (dist < bestD) { bestD = dist; best = w; tie = false; }
                else if (dist == bestD && !string.Equals(best, w, StringComparison.OrdinalIgnoreCase)) tie = true;
            }
            return bestD <= allowed && !tie ? best : null;
        }
    }

    public static class Language
    {
        private static readonly string[] DayNames = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };
        private static readonly string[] MonthNames = { "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december" };

        // Short forms and common slips, to the word they stand for.
        private static readonly Dictionary<string, string> Aliases = new()
        {
            ["tmrw"] = "tomorrow", ["tmr"] = "tomorrow", ["tmw"] = "tomorrow", ["tmrow"] = "tomorrow", ["2morrow"] = "tomorrow", ["2moro"] = "tomorrow", ["tomoz"] = "tomorrow",
            ["tdy"] = "today", ["tonite"] = "tonight", ["2nite"] = "tonight", ["yday"] = "yesterday",
            ["mon"] = "monday", ["tue"] = "tuesday", ["tues"] = "tuesday", ["wed"] = "wednesday", ["weds"] = "wednesday",
            ["thu"] = "thursday", ["thur"] = "thursday", ["thurs"] = "thursday", ["fri"] = "friday", ["sat"] = "saturday", ["sun"] = "sunday",
            ["jan"] = "january", ["feb"] = "february", ["mar"] = "march", ["apr"] = "april", ["jun"] = "june", ["jul"] = "july",
            ["aug"] = "august", ["sep"] = "september", ["sept"] = "september", ["oct"] = "october", ["nov"] = "november", ["dec"] = "december",
            ["nxt"] = "next", ["wk"] = "week", ["wks"] = "weeks", ["wknd"] = "weekend", ["hr"] = "hours", ["hrs"] = "hours",
            ["mins"] = "minutes", ["min"] = "minutes", ["mo"] = "month", ["mos"] = "months", ["yr"] = "year", ["yrs"] = "years",
            ["evry"] = "every", ["each"] = "every", ["oclock"] = "oclock", ["noonish"] = "noon", ["tonights"] = "tonight",
            ["fortnightly"] = "biweekly", ["bi-weekly"] = "biweekly",
        };

        private static readonly string[] NumberWords = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten", "eleven", "twelve" };

        // Every word a date, time or repeat can be made of; misspelled words are matched against these.
        private static readonly HashSet<string> Vocabulary = new(DayNames.Concat(MonthNames).Concat(NumberWords).Concat(DayNames.Select(d => d + "s")).Concat(new[]
        {
            "today", "tonight", "tomorrow", "yesterday", "day", "days", "week", "weeks", "weekend", "weekends", "weekday", "weekdays",
            "month", "months", "year", "years", "hour", "hours", "minute", "minutes", "next", "this", "coming", "after", "at", "in", "on", "by", "due",
            "from", "now", "noon", "midnight", "morning", "afternoon", "evening", "night", "end", "of", "the", "a", "an", "couple", "few",
            "am", "pm", "every", "other", "and", "before", "until", "oclock", "fortnight", "biweekly", "daily", "weekly", "monthly", "yearly",
            "second", "first", "third", "last", "do", "it", "for", "upcoming",
        }));

        /// <summary>
        /// Lower case, punctuation tidied, times split (3pm to 3 pm), ordinals made plain (20th to 20),
        /// short forms expanded and misspellings corrected. Unknown words stay as typed. corrected says
        /// whether any word was guessed, so Quick add can be careful about names.
        /// </summary>
        public static List<string> Words(string text, out bool corrected, bool guess = true)
        {
            corrected = false;
            string t = " " + text.ToLowerInvariant() + " ";
            t = t.Replace("a.m.", "am").Replace("p.m.", "pm").Replace("o'clock", "oclock").Replace("o clock", "oclock").Replace("’", "'");
            t = Regex.Replace(t, @"(\d)(am|pm|a|p)\b", "$1 $2");
            t = Regex.Replace(t, @"\b(\d{1,2})(st|nd|rd|th)\b", "$1");
            t = Regex.Replace(t, @"[,;!?()""]", " ");
            t = Regex.Replace(t, @"(?<=[a-z])\.(?=\s)", " ");
            var words = new List<string>();
            foreach (var raw in t.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string w = raw.Trim('.', '\'');
                if (w.Length == 0) continue;
                if (w is "a" or "p" && words.Count > 0 && Regex.IsMatch(words[^1], @"^\d")) w += "m";
                if (Aliases.TryGetValue(w, out var a)) { words.Add(a); continue; }
                if (Vocabulary.Contains(w) || !Regex.IsMatch(w, @"^[a-z]+$") || !guess) { words.Add(w); continue; }
                // Day and month names first: wensday is as close to weekday as to Wednesday, and meant Wednesday.
                var fix = Fuzzy.Best(w, DayNames.Concat(MonthNames)) ?? Fuzzy.Best(w, Vocabulary);
                if (fix != null) { corrected = true; words.Add(fix); }
                else words.Add(w);
            }
            return words;
        }

        private static int? Number(string w)
        {
            if (int.TryParse(w, out int n)) return n;
            int i = Array.IndexOf(NumberWords, w);
            return i >= 0 ? i : w is "a" or "an" ? 1 : w == "couple" ? 2 : w == "few" ? 3 : null;
        }

        private static DateTime Coming(DateTime from, DayOfWeek d, bool todayCounts)
        {
            var x = todayCounts ? from.Date : from.Date.AddDays(1);
            while (x.DayOfWeek != d) x = x.AddDays(1);
            return x;
        }

        private static DayOfWeek? Day(string w)
        {
            int i = Array.IndexOf(DayNames, w.EndsWith("s") && w.Length > 6 ? w[..^1] : w);
            return i >= 0 ? (DayOfWeek)i : null;
        }

        private static int Month(string w) => Array.IndexOf(MonthNames, w) + 1;

        /// <summary>
        /// A date, with or without a time, from plain words. Null when any word is not part of a date,
        /// so a typo that is not a date is never quietly read as one.
        /// </summary>
        public static (DateTime? When, bool HasTime) ParseDate(string text, DateTime now, bool guess = true)
        {
            var w = Words(text, out _, guess);
            // Leading and filler words that add nothing: due friday, by 10/20, on the 20th, do it tuesday.
            w.RemoveAll(x => x is "due" or "by" or "on" or "the" or "do" or "it" or "for" or "before" or "until");
            if (w.Count == 0) return (null, false);

            // ---- The time, anywhere: at 3, 3 pm, 3:30, 15:30, noon, in the morning ----
            int? hour = null, minute = null;
            for (int i = 0; i < w.Count && hour == null; i++)
            {
                string x = w[i];
                if (x is "noon") { hour = 12; minute = 0; w.RemoveAt(i); break; }
                if (x is "midnight") { hour = 23; minute = 59; w.RemoveAt(i); break; }
                var m = Regex.Match(x, @"^(\d{1,2})(?::(\d{2}))?$");
                if (!m.Success) continue;
                bool at = i > 0 && w[i - 1] == "at";
                string? ap = i + 1 < w.Count && w[i + 1] is "am" or "pm" or "oclock" ? w[i + 1] : null;
                if (!at && ap == null && !m.Groups[2].Success) continue; // a bare number is not a time
                int h = int.Parse(m.Groups[1].Value), min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                if (ap == "pm" && h < 12) h += 12;
                else if (ap == "am" && h == 12) h = 0;
                else if (ap is null or "oclock" && h >= 1 && h <= 7) h += 12; // at 3 means the afternoon, for a student
                if (h > 23 || min > 59) return (null, false);
                hour = h; minute = min;
                int from = at ? i - 1 : i, count = (at ? 1 : 0) + 1 + (ap != null ? 1 : 0);
                w.RemoveRange(from, count);
            }
            // Parts of the day, when no clock time was given.
            for (int i = 0; i < w.Count; i++)
            {
                int? part = w[i] switch { "morning" => 9, "afternoon" => 14, "evening" => 18, "night" => 20, _ => null };
                if (part == null) continue;
                if (hour == null) { hour = part; minute = 0; }
                w.RemoveAt(i);
                if (i > 0 && w[i - 1] is "in" or "this" or "at") { w.RemoveAt(i - 1); i--; }
                i--;
            }
            if (w.Contains("tonight") && hour == null) { hour = 20; minute = 0; }
            w.RemoveAll(x => x == "at");

            DateTime? day = DayFrom(w, now);
            if (day == null && w.Count == 0 && hour != null) day = now.Date;
            if (day == null) return (null, false);
            return hour == null ? (day, false) : (day.Value.Date.AddHours(hour.Value).AddMinutes(minute ?? 0), true);
        }

        private static DateTime? DayFrom(List<string> w, DateTime now)
        {
            var today = now.Date;
            string s = string.Join(" ", w);
            switch (s)
            {
                case "today" or "tonight" or "now" or "this evening": return today;
                case "tomorrow" or "tomorrow night": return today.AddDays(1);
                case "yesterday": return today.AddDays(-1);
                case "day after tomorrow" or "the day after tomorrow": return today.AddDays(2);
                case "next week": return Coming(now, DayOfWeek.Monday, false).AddDays(today.DayOfWeek == DayOfWeek.Sunday ? 0 : 0);
                case "this week" or "end of week" or "end of this week" or "end of the week" or "by end of week":
                    return Coming(now, DayOfWeek.Friday, true);
                case "weekend" or "this weekend" or "coming weekend": return Coming(now, DayOfWeek.Saturday, true);
                case "next weekend": return Coming(now, DayOfWeek.Saturday, true).AddDays(7);
                case "next month": return new DateTime(today.Year, today.Month, 1).AddMonths(1);
                case "end of month" or "end of the month" or "end of this month":
                    return new DateTime(today.Year, today.Month, 1).AddMonths(1).AddDays(-1);
                case "end of next month": return new DateTime(today.Year, today.Month, 1).AddMonths(2).AddDays(-1);
                case "next year": return new DateTime(today.Year + 1, 1, 1);
            }

            // in 3 days, in a week, in a couple of days, 2 weeks from now, a week from today, a week from friday
            var rel = Regex.Match(s, @"^(?:in )?(\S+) (?:of )?(day|days|week|weeks|month|months|year|years|fortnight)(?: (?:from|after) (.+))?$");
            if (rel.Success && (s.StartsWith("in ") || rel.Groups[3].Success) && Number(rel.Groups[1].Value) is int n)
            {
                DateTime start = today;
                if (rel.Groups[3].Success)
                {
                    string anchor = rel.Groups[3].Value;
                    if (anchor is not ("now" or "today")) { if (DayFrom(anchor.Split(' ').ToList(), now) is DateTime a) start = a; else return null; }
                }
                return rel.Groups[2].Value switch
                {
                    "day" or "days" => start.AddDays(n),
                    "week" or "weeks" => start.AddDays(7 * n),
                    "fortnight" => start.AddDays(14 * n),
                    "month" or "months" => start.AddMonths(n),
                    _ => start.AddYears(n),
                };
            }
            if (s is "in a fortnight" or "fortnight") return today.AddDays(14);

            // tuesday, this tuesday, next tuesday, coming tuesday, tuesday next week, tuesday after next
            var wd = Regex.Match(s, @"^(this |next |coming |upcoming )?([a-z]+)( next week| after next| this week)?$");
            if (wd.Success && Day(wd.Groups[2].Value) is DayOfWeek d)
            {
                string pre = wd.Groups[1].Value.Trim(), post = wd.Groups[3].Value.Trim();
                if (pre == "this" || post == "this week") return Coming(now, d, true);
                var x = Coming(now, d, false);
                if (pre == "next" && (x - today).Days < 7) x = x.AddDays(7);
                if (post == "next week") { var monday = Coming(now, DayOfWeek.Monday, false); x = monday; while (x.DayOfWeek != d) x = x.AddDays(1); }
                if (post == "after next") x = x.AddDays(7);
                return x;
            }

            // october 20, october 20 2027, 20 october, the 20th (of this month, or next if past)
            var md = Regex.Match(s, @"^([a-z]+) (\d{1,2})(?: (\d{4}))?$");
            var dm = Regex.Match(s, @"^(\d{1,2}) (?:of )?([a-z]+)(?: (\d{4}))?$");
            if (md.Success && Month(md.Groups[1].Value) > 0) return MonthDay(Month(md.Groups[1].Value), int.Parse(md.Groups[2].Value), md.Groups[3].Success ? int.Parse(md.Groups[3].Value) : null, today);
            if (dm.Success && Month(dm.Groups[2].Value) > 0) return MonthDay(Month(dm.Groups[2].Value), int.Parse(dm.Groups[1].Value), dm.Groups[3].Success ? int.Parse(dm.Groups[3].Value) : null, today);
            var nth = Regex.Match(s, @"^(\d{1,2})$");
            if (nth.Success && w.Count == 1)
            {
                int dd = int.Parse(nth.Groups[1].Value);
                if (dd < 1 || dd > 31) return null;
                var month = new DateTime(today.Year, today.Month, 1);
                for (int k = 0; k < 13; k++, month = month.AddMonths(1))
                    if (dd <= DateTime.DaysInMonth(month.Year, month.Month) && month.AddDays(dd - 1) >= today) return month.AddDays(dd - 1);
                return null;
            }

            // 10/20, 10/20/2026, 10/20/26, 10-20, 2026-10-20
            if (DateTime.TryParseExact(s, new[] { "yyyy-M-d" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso)) return iso.Date;
            var num = Regex.Match(s, @"^(\d{1,2})[/\-.](\d{1,2})(?:[/\-.](\d{2}|\d{4}))?$");
            if (num.Success)
            {
                int? year = num.Groups[3].Success ? int.Parse(num.Groups[3].Value) : null;
                if (year < 100) year += 2000;
                return MonthDay(int.Parse(num.Groups[1].Value), int.Parse(num.Groups[2].Value), year, today);
            }
            return null;
        }

        private static DateTime? MonthDay(int month, int day, int? year, DateTime today)
        {
            if (month < 1 || month > 12 || day < 1) return null;
            int y = year ?? today.Year;
            if (day > DateTime.DaysInMonth(y, month)) return null;
            var d = new DateTime(y, month, day);
            // No year given and well past: next year's.
            if (year == null && d < today.AddDays(-7)) d = d.AddYears(1);
            return d;
        }

        /// <summary>
        /// Repeat words with typos fixed and other ways of saying them made plain: biweekly to every 2
        /// weeks, mondays to every monday, weekly on tuesday to every tuesday, each to every.
        /// </summary>
        public static string RepeatWords(string text)
        {
            var w = Words(text, out _);
            w.RemoveAll(x => x is "on" or "the" or "do" or "it");
            string s = string.Join(" ", w);
            s = Regex.Replace(s, @"\bbiweekly\b|\bevery fortnight\b|\bfortnight\b|\bevery second week\b|\bevery 2 week\b", "every 2 weeks");
            s = Regex.Replace(s, @"^(?:every )?(daily|every morning|every night|every evening|every afternoon)$", "every day");
            s = Regex.Replace(s, @"^(?:every )?weekly (?:every )?([a-z ]+)$", "every $1");
            s = Regex.Replace(s, @"^every week ([a-z]+day.*)$", "every $1");
            s = Regex.Replace(s, @"^every other ([a-z]+day.*)$", "every 2 weeks on $1");
            s = Regex.Replace(s, @"^every other weeks? ([a-z]+day.*)$", "every 2 weeks on $1");
            s = Regex.Replace(s, @"^every (\d+) weeks? ([a-z]+day.*)$", "every $1 weeks on $2");
            s = Regex.Replace(s, @"^(weekdays|every weekdays)$", "every weekday");
            s = Regex.Replace(s, @"^(weekends|every weekends)$", "every weekend");
            // mondays, mondays and thursdays: every monday
            if (Regex.IsMatch(s, @"^[a-z]+days?(?: and [a-z]+days?)*$") && w.All(x => x == "and" || Day(x) != null)) s = "every " + s;
            if (s is "weekly") s = "every week";
            if (s is "monthly") s = "every month";
            if (s is "yearly" or "annually") s = "every year";
            return s;
        }

        /// <summary>
        /// Quick add: a date, time or repeat typed at the end of the task's name, taken out of the name.
        /// call grandma tomorrow at 3 is call grandma, due tomorrow at 3 pm; take meds every day repeats
        /// every day. Only whole, correctly spelled date words count here, so a name is never cut by a guess.
        /// </summary>
        public static (string Name, DateTime? Due, bool HasTime, string Repeat) TakeFromName(string name, DateTime now)
        {
            var words = name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            string repeat = "";
            DateTime? due = null;
            bool hasTime = false;
            // A repeat first (every Monday), then a date (tomorrow at 3), each at the very end.
            for (int n = Math.Min(7, words.Count - 1); n >= 1; n--)
            {
                string tail = string.Join(" ", words.Skip(words.Count - n));
                string lower = tail.ToLowerInvariant();
                if (!Regex.IsMatch(lower, @"^(every|each|daily|weekly|monthly|biweekly|weekdays)\b")) continue;
                if (RepeatRule.Parse(tail) is RepeatRule r) { repeat = r.Describe(); words.RemoveRange(words.Count - n, n); break; }
            }
            for (int n = Math.Min(7, words.Count - 1); n >= 1; n--)
            {
                string tail = string.Join(" ", words.Skip(words.Count - n));
                if (!StrongDateWords(tail)) continue;
                var (when, t) = ParseDate(tail, now, guess: false);
                if (when == null) continue;
                due = when; hasTime = t;
                words.RemoveRange(words.Count - n, n);
                // A dangling joining word: call grandma on, finish essay by.
                while (words.Count > 1 && words[^1].ToLowerInvariant() is "on" or "by" or "at" or "due" or "for" or "before") words.RemoveAt(words.Count - 1);
                break;
            }
            return (string.Join(" ", words), due, hasTime, repeat);
        }

        // Words that clearly mean a date or time when they end a task's name. A bare number never does.
        private static bool StrongDateWords(string tail)
        {
            string s = " " + tail.ToLowerInvariant() + " ";
            if (Regex.IsMatch(s, @"\d{1,2}/\d{1,2}|\d\s?(am|pm)\b|\bat \d|\bnoon\b|\btonight\b|\btoday\b|\btomorrow\b|\btmrw\b|\bin \S+ (days?|weeks?|months?)\b|\bnext (week|month)\b|\bend of (the )?(week|month)\b")) return true;
            foreach (var d in DayNames) if (s.Contains(" " + d + " ")) return true;
            foreach (var m in MonthNames) if (Regex.IsMatch(s, @"\b" + m + @" \d")) return true;
            return false;
        }

        public static void SelfTest()
        {
            var mon = new DateTime(2026, 10, 12, 9, 0, 0); // a Monday
            void Date(string text, DateTime? want, bool time = false)
            {
                var (got, hasTime) = ParseDate(text, mon);
                if (got != want || want != null && hasTime != time) throw new Exception("words '" + text + "' gave " + got + ", wanted " + want);
            }
            Date("tuesda", new DateTime(2026, 10, 13));
            Date("tomorow", new DateTime(2026, 10, 13));
            Date("wensday", new DateTime(2026, 10, 14));
            Date("thrusday at 3", new DateTime(2026, 10, 15, 15, 0, 0), true);
            Date("frday 3pm", new DateTime(2026, 10, 16, 15, 0, 0), true);
            Date("3 pm tomorrow", new DateTime(2026, 10, 13, 15, 0, 0), true);
            Date("tomorrow at noon", new DateTime(2026, 10, 13, 12, 0, 0), true);
            Date("tomorrow morning", new DateTime(2026, 10, 13, 9, 0, 0), true);
            Date("in 3 days", new DateTime(2026, 10, 15));
            Date("in a week", new DateTime(2026, 10, 19));
            Date("in two weeks", new DateTime(2026, 10, 26));
            Date("a week from friday", new DateTime(2026, 10, 23));
            Date("day after tomorrow", new DateTime(2026, 10, 14));
            Date("this friday", new DateTime(2026, 10, 16));
            Date("next tues", new DateTime(2026, 10, 20));
            Date("next week", new DateTime(2026, 10, 19));
            Date("end of the week", new DateTime(2026, 10, 16));
            Date("end of month", new DateTime(2026, 10, 31));
            Date("this weekend", new DateTime(2026, 10, 17));
            Date("octobr 20th", new DateTime(2026, 10, 20));
            Date("20 oct", new DateTime(2026, 10, 20));
            Date("the 20th", new DateTime(2026, 10, 20));
            Date("the 5th", new DateTime(2026, 11, 5));
            Date("due by friday", new DateTime(2026, 10, 16));
            Date("10-20", new DateTime(2026, 10, 20));
            Date("2026-11-02", new DateTime(2026, 11, 2));
            Date("tonight", new DateTime(2026, 10, 12, 20, 0, 0), true);
            Date("friday 10:30 a.m.", new DateTime(2026, 10, 16, 10, 30, 0), true);
            Date("blorp", null);
            Date("friday blorp", null);
            Date("25:00", null);

            void Rep(string text, string want)
            {
                string got = RepeatRule.Parse(text)?.Describe() ?? "";
                if (got != want) throw new Exception("repeat '" + text + "' gave '" + got + "', wanted '" + want + "'");
            }
            Rep("evry tuesda", "every Tuesday");
            Rep("each monday and wenesday", "every Monday and Wednesday");
            Rep("mondays", "every Monday");
            Rep("biweekly", "every 2 weeks");
            Rep("every other tues", "every 2 weeks on Tuesday");
            Rep("weekly on thursday", "every Thursday");
            Rep("daily", "every day");
            Rep("every morning", "every day");
            Rep("weekdays", "every weekday");
            Rep("evrey week", "every week");

            var (name, due, hasTime, repeat) = TakeFromName("call grandma tomorrow at 3", mon);
            if (name != "call grandma" || due != new DateTime(2026, 10, 13, 15, 0, 0) || !hasTime) throw new Exception("quick add name gave " + name + ", " + due);
            (name, due, _, repeat) = TakeFromName("PSY chapter 9 readings every tuesday", mon);
            if (name != "PSY chapter 9 readings" || repeat != "every Tuesday" || due != null) throw new Exception("quick add repeat gave " + name + ", " + repeat);
            (name, due, _, _) = TakeFromName("lab 3 write-up", mon);
            if (name != "lab 3 write-up" || due != null) throw new Exception("quick add cut a name with a number");
            (name, due, _, _) = TakeFromName("call tom", mon);
            if (name != "call tom" || due != null) throw new Exception("quick add read a name as a date");
            (name, due, _, _) = TakeFromName("finish essay by friday", mon);
            if (name != "finish essay" || due != new DateTime(2026, 10, 16)) throw new Exception("quick add by friday gave " + name);
        }
    }
}
