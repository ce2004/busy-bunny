using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// Streaks and seasons in the Wins and records drawer (sweep 8.4): start streaks, the no-overdue streak, the
    /// comeback streak, streak shields, monthly level names, level perks, the season pass, the semester trail and the
    /// year in review. Streaks are gentle: vacation and rough days never break them, and a break only starts a comeback.
    /// </summary>
    public sealed class RecordsStreaks : IFeature
    {
        public const string StartId = "records.startstreak", CleanId = "records.nooverdue", ComebackId = "records.comeback", ShieldId = "records.shields",
            MonthId = "records.monthname", PerkId = "records.perks", PassId = "records.pass", TrailId = "records.trail", YearId = "records.year";
        public const int MaxShields = 3, ShieldEvery = 14, Tiers = 30;
        private MainWindow _w = null!;
        private Store S => _w.Store;

        public sealed class Days { public HashSet<string> Keys { get; set; } = new(); }
        public sealed class Shields { public int Have { get; set; } public List<string> Earned { get; set; } = new(); public List<string> Used { get; set; } = new(); public List<int> PerkLevels { get; set; } = new(); }
        public sealed class Months { public Dictionary<string, string> Titles { get; set; } = new(); public List<string> Badges { get; set; } = new(); public string Settled { get; set; } = ""; }
        public sealed class Pass { public string Semester { get; set; } = ""; public int Announced { get; set; } }
        public sealed class YearNote { public string Said { get; set; } = ""; }

        public void Attach(MainWindow w)
        {
            _w = w;
            void Tool(string id, string name, string what, Action run) => Tools.Register(new BunnyTool(id, name, Drawers.Wins, what, run) { Since = "0.14.0" });
            Tool(StartId, "Start streak", "Days in a row you started something, separate from finishing.", () => Say(StartLine(S, DateTime.Now)));
            Tool(CleanId, "No-overdue streak", "Days in a row with nothing overdue.", () => Say(CleanLine(S, DateTime.Now)));
            Tool(ComebackId, "Comeback streak", "The streak that starts counting the day after a break.", () => Say(ComebackLine(S, DateTime.Now)));
            Tool(ShieldId, "Streak shields", "One shield for every 14 streak days, up to 3. A shield covers a missed day by itself.", () => Say(ShieldLine(S)));
            Tool(MonthId, "Monthly level name", "This month's title, and the badges from months before.", ShowMonths);
            Tool(PerkId, "Level perks", "What each level unlocks, and what comes next.", ShowPerks);
            Tool(PassId, "Season pass", "A " + Tiers + "-tier track for the semester, earned by effort. Every tier gives an item.", ShowPass);
            Tool(TrailId, "Semester trail", "The semester as a trail, one mile marker a week. Hear how far along you are.", () => Say(TrailLine(S, DateTime.Now)));
            Tool(YearId, "Year in review", "Your whole year, like Semester Wrapped. Opens in December.", ShowYear);
            BunnyTab.Register("Season pass", "your tier and the items so far", ShowPass, PassId);

            GameEvents.TaskStarted += _ => MarkStarted(S, DateTime.Now);
            GameEvents.StreakChanged += days => { if (Tools.IsOn(ShieldId)) EarnShield(S, days, DateTime.Now); };
            GameEvents.LevelUp += level => { if (Tools.IsOn(PerkId)) PerkShields(S, level); };
            GameEvents.DayStarted += day =>
            {
                var now = DateTime.Now;
                if (Tools.IsOn(ShieldId) && UseShields(S, now) is string note) { S.Save(); Calm.SayUnprompted(_w, note); }
                if (Tools.IsOn(MonthId) && SettleMonth(S, now) is string badge) Calm.SayUnprompted(_w, badge);
                if (Tools.IsOn(YearId) && now.Month == 12)
                {
                    var y = S.World.GetKept<YearNote>(YearId);
                    if (y.Said != now.Year.ToString()) { y.Said = now.Year.ToString(); S.World.SetKept(YearId, y); S.World.Save(); Calm.SayUnprompted(_w, "Your year in review is ready: toolbox, Wins and records, Year in review."); }
                }
            };
            GameEvents.TaskDone += t =>
            {
                if (!Tools.IsOn(PassId) || PassTier(S, DateTime.Now) is not string line) return;
                S.Save();
                Celebration.Sound(S, "Singing bells, rising");
                Calm.AfterTaskDone(_w, Game.Other, Celebration.Line(S, line));
            };
            GameEvents.TaskUndone += _ => PassBack(S, DateTime.Now);
        }

        private void Say(string text) => _w.Say(text, true);

        // ---- Start streak ----

        public static void MarkStarted(Store s, DateTime now)
        {
            var d = s.World.GetKept<Days>(StartId);
            if (!d.Keys.Add(s.Key(now))) return;
            s.World.SetKept(StartId, d);
            s.World.Save();
        }

        /// <summary>Days she started something: a sprint or a started task, anything finished, or an I did a thing.</summary>
        public static HashSet<string> StartDays(Store s)
        {
            var days = s.World.GetKept<Days>(StartId).Keys.ToHashSet();
            foreach (var e in s.Data.Log.Where(e => e.Kind is "Done" or "Sprint" or "DidAThing" or "WorkedOn")) days.Add(GamesFunKit.Key(e.When));
            return days;
        }

        public static string StartLine(Store s, DateTime now)
        {
            var (cur, best) = GamesFunKit.Streak(StartDays(s), s.Data, now);
            bool today = StartDays(s).Contains(GamesFunKit.Key(now));
            return cur == 0 ? "No start streak running. Starting anything today, even a two minute sprint, begins one." :
                "Start streak: " + GamesFunKit.Plural(cur, "day") + " in a row" + (today ? ", today included" : "; start something today to keep it going") + ". Your longest is " + GamesFunKit.Plural(best, "day") + ".";
        }

        // ---- No-overdue streak ----

        /// <summary>Whether a past day ended with nothing overdue, worked out from the tasks as they are now.</summary>
        public static bool CleanDay(Store s, DateTime day) => !GamesFunKit.Live(s).Any(t => t.Due is DateTime due && due.Date <= day.Date && t.CreatedAt.Date <= day.Date && t.Status != Status.Waiting
            && !(GamesFunKit.Finished(t) && t.DoneAt is DateTime done && done.Date <= day.Date));

        public static int CleanStreak(Store s, DateTime now)
        {
            int n = s.Overdue(now).Count == 0 ? 1 : 0;
            var first = s.Data.Tasks.Select(t => t.CreatedAt.Date).DefaultIfEmpty(now.Date).Min();
            for (var day = now.Date.AddDays(-1); day >= first && day > now.Date.AddDays(-366); day = day.AddDays(-1))
            {
                if (GamesFunKit.RestDay(s.Data, day)) continue;
                if (!CleanDay(s, day)) break;
                n++;
            }
            return n;
        }

        public static string CleanLine(Store s, DateTime now)
        {
            int n = CleanStreak(s, now), over = s.Overdue(now).Count;
            if (over > 0) return "Today has " + GamesFunKit.Plural(over, "overdue task") + ", so the no-overdue streak starts fresh once they are done or moved. " + (n > 0 ? "" : "No guilt; it is just a counter.");
            return "No-overdue streak: " + GamesFunKit.Plural(n, "day") + " in a row with nothing overdue, today included.";
        }

        // ---- Comeback streak ----

        public static string ComebackLine(Store s, DateTime now)
        {
            var (cur, _) = Engine.Streaks(s.Data, now);
            var goals = s.Data.Game.GoalDays.Select(GamesFunKit.Day).OfType<DateTime>().Where(d => d.Date < now.Date.AddDays(-cur)).ToList();
            if (goals.Count == 0) return cur > 0 ? "Your streak is " + GamesFunKit.Plural(cur, "day") + " and has never been broken, so there is nothing to come back from yet." : "No streak yet. Meet your daily goal to start one.";
            if (cur == 0) return "Your streak took a break. Meet your daily goal today and your comeback streak starts at 1.";
            return "Comeback streak: " + GamesFunKit.Plural(cur, "day") + " since your last break. " + Content.Pick(s, "records.comeback");
        }

        // ---- Streak shields ----

        public static void EarnShield(Store s, int days, DateTime now)
        {
            if (days <= 0 || days % ShieldEvery != 0) return;
            var sh = s.World.GetKept<Shields>(ShieldId);
            string key = s.Key(now) + ":" + days;
            if (sh.Earned.Contains(key)) return;
            sh.Earned.Add(key);
            sh.Have = Math.Min(MaxShields, sh.Have + 1);
            s.World.SetKept(ShieldId, sh);
            s.World.Save();
            Game.Note(s, "You earned a streak shield for " + days + " days in a row. You have " + sh.Have + ".");
        }

        public static void PerkShields(Store s, int level)
        {
            var sh = s.World.GetKept<Shields>(ShieldId);
            bool changed = false;
            foreach (var p in Perks().Where(p => p.Level <= level && p.Kind == "shield" && !sh.PerkLevels.Contains(p.Level)))
            {
                sh.PerkLevels.Add(p.Level);
                sh.Have = Math.Min(MaxShields, sh.Have + 1);
                changed = true;
            }
            if (changed) { s.World.SetKept(ShieldId, sh); s.World.Save(); }
        }

        /// <summary>
        /// At the start of a day: missed days just before it (not vacation or rough days) right after a goal day are
        /// covered by shields, one each, when there are enough. Remembered first, so Control Z puts it back. The gentle note, or null.
        /// </summary>
        public static string? UseShields(Store s, DateTime now)
        {
            var sh = s.World.GetKept<Shields>(ShieldId);
            if (sh.Have <= 0) return null;
            var goals = s.Data.Game.GoalDays.ToHashSet();
            var missed = new List<DateTime>();
            var day = now.Date.AddDays(-1);
            for (int k = 0; k < 7; k++, day = day.AddDays(-1))
            {
                if (goals.Contains(GamesFunKit.Key(day))) break;
                if (GamesFunKit.RestDay(s.Data, day)) continue;
                missed.Add(day);
            }
            if (missed.Count == 0 || missed.Count > sh.Have || !goals.Contains(GamesFunKit.Key(day))) return null;
            int streak = Engine.Streaks(s.Data, day).Current;
            if (streak < 2) return null;
            s.Remember("a streak shield");
            foreach (var m in missed) s.Data.Game.GoalDays.Add(GamesFunKit.Key(m));
            sh.Have -= missed.Count;
            sh.Used.AddRange(missed.Select(GamesFunKit.Key));
            s.World.SetKept(ShieldId, sh);
            s.World.Save();
            return Content.Pick(s, "records.shield") + " " + (missed.Count == 1 ? "A streak shield covered yesterday" : GamesFunKit.Plural(missed.Count, "streak shield") + " covered the missed days") +
                ", so your " + GamesFunKit.Plural(streak + missed.Count, "day") + " streak is safe. " + GamesFunKit.Plural(sh.Have, "shield") + " left.";
        }

        public static string ShieldLine(Store s)
        {
            var sh = s.World.GetKept<Shields>(ShieldId);
            int cur = Engine.Streaks(s.Data, DateTime.Now).Current, next = ShieldEvery - cur % ShieldEvery;
            return "You have " + GamesFunKit.Plural(sh.Have, "streak shield") + " of " + MaxShields + ". " + (sh.Have >= MaxShields ? "That is the most you can hold." : "The next comes in " + GamesFunKit.Plural(next, "more streak day") + ".") +
                " A shield steps in by itself when you miss a day." + (sh.Used.Count > 0 ? " Shields have covered " + GamesFunKit.Plural(sh.Used.Count, "day") + " so far." : "");
        }

        // ---- Monthly level names ----

        public static string MonthTitle(Store s, DateTime month)
        {
            var m = s.World.GetKept<Months>(MonthId);
            string key = month.ToString("yyyy-MM");
            if (m.Titles.TryGetValue(key, out var t)) return t;
            t = Content.Pick(s, "records.monthnames");
            m.Titles[key] = t;
            s.World.SetKept(MonthId, m);
            s.World.Save();
            return t;
        }

        /// <summary>Once in a new month: last month's badge, if anything was finished in it. The line, or null.</summary>
        public static string? SettleMonth(Store s, DateTime now)
        {
            var last = new DateTime(now.Year, now.Month, 1).AddMonths(-1);
            var m = s.World.GetKept<Months>(MonthId);
            string key = last.ToString("yyyy-MM");
            if (string.CompareOrdinal(m.Settled, key) >= 0) { MonthTitle(s, now); return null; }
            m.Settled = key;
            s.World.SetKept(MonthId, m);
            s.World.Save();
            bool any = s.Data.Log.Any(e => e.Kind is "Done" or "DidAThing" && e.When.Year == last.Year && e.When.Month == last.Month);
            string title = MonthTitle(s, now);
            if (!any) return null;
            m = s.World.GetKept<Months>(MonthId);
            string name = m.Titles.GetValueOrDefault(key) ?? "The Month That Happened";
            m.Badges.Add(last.ToString("MMMM yyyy") + ": " + name);
            s.World.SetKept(MonthId, m);
            s.World.Save();
            return "Badge earned for " + last.ToString("MMMM") + ": " + name + ". This month you are " + title + ".";
        }

        private void ShowMonths()
        {
            var now = DateTime.Now;
            var lines = new List<string> { "This month's title: " + MonthTitle(S, now) + ". At the end of the month it becomes a badge." };
            lines.AddRange(S.World.GetKept<Months>(MonthId).Badges.AsEnumerable().Reverse().Select(b => "Badge, " + b));
            GamesFunKit.Read(_w, "Monthly level names", lines);
        }

        // ---- Level perks ----

        public sealed record Perk(int Level, string Kind, string Name, string Text, string Sound);

        public static List<Perk> Perks() => Content.Items("records.perks").Where(i => i.ContainsKey("level") && i.ContainsKey("name"))
            .Select(i => new Perk(int.TryParse(i["level"], out int l) ? l : 99, i.GetValueOrDefault("kind") ?? "title", i["name"], i.GetValueOrDefault("text") ?? "", i.GetValueOrDefault("sound") ?? ""))
            .OrderBy(p => p.Level).ToList();

        private void ShowPerks()
        {
            int level = Engine.LevelFor(Engine.Total(S.Data));
            PerkShields(S, level);
            var perks = Perks();
            var have = perks.Where(p => p.Level <= level).ToList();
            var next = perks.FirstOrDefault(p => p.Level > level);
            var choices = have.Select(p => "Level " + p.Level + ": " + p.Name + ". " + p.Text + (p.Sound.Length > 0 ? " Enter plays it." : "")).ToList();
            if (next != null) choices.Add("Next, at level " + next.Level + ": " + next.Name + ". " + GamesFunKit.Plural(Engine.Threshold(next.Level) - Engine.Total(S.Data), "point") + " to go.");
            int i = GamesFunKit.Pick(_w, "Level perks", "_Perks", choices, 0, "You are level " + level + ". " + GamesFunKit.Plural(have.Count, "perk") + " unlocked.");
            if (i >= 0 && i < have.Count && have[i].Sound.Length > 0) SoundBank.Play(have[i].Sound);
        }

        // ---- Season pass ----

        public static int TierSize(Data d, DateTime now)
        {
            var (_, from, to) = GameFeature.Semester(now);
            double weeks = Math.Max(4, (to - from).TotalDays / 7);
            int size = (int)Math.Round(Engine.WeeklyGoalFor(d) * weeks * 0.6 / Tiers / 5) * 5;
            return Math.Max(20, size);
        }

        public static int Tier(Data d, DateTime now)
        {
            var (_, from, to) = GameFeature.Semester(now);
            int pts = d.Log.Where(e => e.When.Date >= from && e.When.Date <= to && Engine.KarmaKinds.Contains(e.Kind)).Sum(e => e.Points);
            return Math.Min(Tiers, Math.Max(0, pts) / TierSize(d, now));
        }

        public static string PassItem(int tier)
        {
            var items = Content.Items("records.pass");
            if (items.Count == 0) return "a small prize";
            var i = items[(tier - 1) % items.Count];
            return i.GetValueOrDefault("name", "a prize") + ". " + string.Join(" ", new[] { "touch", "sound", "looks" }.Where(i.ContainsKey).Select(k => i[k]));
        }

        /// <summary>After a finish: the new tier's line when one was reached, or null.</summary>
        public static string? PassTier(Store s, DateTime now)
        {
            var p = s.World.Get<Pass>(PassId);
            string sem = GameFeature.Semester(now).Name;
            if (p.Semester != sem) p = new Pass { Semester = sem, Announced = Tier(s.Data, now) > 0 ? Tier(s.Data, now) - 1 : 0 };
            int tier = Tier(s.Data, now);
            if (tier <= p.Announced) { GamesFunKit.Put(s, PassId, p); return null; }
            p.Announced = tier;
            GamesFunKit.Put(s, PassId, p);
            return "Season pass, tier " + tier + " of " + Tiers + "! You got " + PassItem(tier);
        }

        public static void PassBack(Store s, DateTime now)
        {
            var p = s.World.Get<Pass>(PassId);
            int tier = Tier(s.Data, now);
            if (p.Semester != GameFeature.Semester(now).Name || tier >= p.Announced) return;
            p.Announced = tier;
            GamesFunKit.Put(s, PassId, p);
        }

        private void ShowPass()
        {
            var now = DateTime.Now;
            int tier = Tier(S.Data, now), size = TierSize(S.Data, now);
            var (name, from, to) = GameFeature.Semester(now);
            int pts = S.Data.Log.Where(e => e.When.Date >= from && e.When.Date <= to && Engine.KarmaKinds.Contains(e.Kind)).Sum(e => e.Points);
            var lines = new List<string> { name + " season pass: tier " + tier + " of " + Tiers + ". Each tier is " + size + " points of effort." + (tier < Tiers ? " " + (size * (tier + 1) - pts) + " more points to the next." : " Every tier is done!") };
            for (int k = 1; k <= tier; k++) lines.Add("Tier " + k + ": " + PassItem(k));
            if (tier < Tiers) lines.Add("Next, tier " + (tier + 1) + ": a surprise.");
            GamesFunKit.Read(_w, "Season pass", lines);
        }

        // ---- Semester trail ----

        public static (int Week, int Weeks) TrailWeek(DateTime now)
        {
            var (_, from, to) = GameFeature.Semester(now);
            int weeks = (int)Math.Ceiling(((to - from).TotalDays + 1) / 7);
            return (Math.Clamp((int)((now.Date - from).TotalDays / 7) + 1, 1, weeks), weeks);
        }

        public static string TrailLine(Store s, DateTime now)
        {
            var (week, weeks) = TrailWeek(now);
            var markers = Content.Items("records.trail");
            var m = markers.Count == 0 ? new Dictionary<string, string>() : markers[(week - 1) % markers.Count];
            string at = string.Join(" ", new[] { "touch", "sound", "looks" }.Where(m.ContainsKey).Select(k => m[k]));
            return GameFeature.Semester(now).Name + " trail: mile " + week + " of " + weeks + ", " + (week * 100 / weeks) + " percent of the way. Mile marker " + week + ", " + m.GetValueOrDefault("name", "a quiet bend") + ". " + at;
        }

        // ---- Year in review ----

        public static List<string> YearReview(Store s, int year, DateTime now)
        {
            var d = s.Data;
            var log = d.Log.Where(e => e.When.Year == year).ToList();
            var lines = new List<string> { "Your " + year + " in review." };
            if (log.Count == 0) { lines.Add("Nothing logged in " + year + " yet. The review fills in as you go."); return lines; }
            var done = log.Where(e => e.Kind == "Done").ToList();
            lines.Add("Tasks finished: " + done.Select(e => e.TaskId).Distinct().Count() + ". Karma earned: " + log.Where(e => Engine.KarmaKinds.Contains(e.Kind)).Sum(e => e.Points).ToString("N0") + ".");
            var month = log.Where(e => Engine.KarmaKinds.Contains(e.Kind)).GroupBy(e => e.When.Month).OrderByDescending(g => g.Sum(e => e.Points)).FirstOrDefault();
            if (month != null) lines.Add("Biggest month: " + new DateTime(year, month.Key, 1).ToString("MMMM") + ", " + GamesFunKit.Plural(month.Sum(e => e.Points), "point") + ".");
            var weekday = done.GroupBy(e => e.When.DayOfWeek).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (weekday != null) lines.Add("Your busiest weekday: " + weekday.Key + "s.");
            var end = now.Year == year ? now : new DateTime(year, 12, 31);
            lines.Add("Longest streak this year: " + GamesFunKit.Plural(Engine.Streaks(d, end, new DateTime(year, 1, 1)).Longest, "day") + ".");
            int minutes = log.Where(e => e.Kind == "Sprint").Sum(e => e.Minutes);
            if (minutes > 0) lines.Add("Focused time: " + GamesCard.Minutes(minutes) + ".");
            int did = log.Count(e => e.Kind == "DidAThing");
            if (did > 0) lines.Add("Things you did that were never on a list: " + did + ".");
            var tag = done.Where(e => e.Tag.Length > 0).GroupBy(e => e.Tag, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (tag != null) lines.Add("Most finished tag: " + tag.Key + ", " + GamesFunKit.Plural(tag.Count(), "task") + ".");
            lines.Add("Badges earned: " + d.Game.Badges.Count(b => b.Value.Year == year) + ". Monthly titles: " + s.World.GetKept<Months>(MonthId).Badges.Count(b => b.Contains(year.ToString())) + ".");
            lines.Add("Kindnesses logged: " + s.World.Get<RecordsWins.Kindness>(RecordsWins.KindId).Total + ". Marble jars filled: " + s.World.Get<RecordsWins.Jar>(RecordsWins.MarbleId).Filled.Count(f => f.When.Year == year) + ".");
            int level = Engine.LevelFor(Engine.Total(d));
            lines.Add("You are level " + level + ", " + Engine.LevelName(level) + ". " + Content.Pick(s, "records.year"));
            return lines;
        }

        private void ShowYear()
        {
            var now = DateTime.Now;
            if (now.Month == 12) { GamesFunKit.Read(_w, "Year in review", YearReview(S, now.Year, now)); return; }
            if (now.Month == 1)
            {
                int i = GamesFunKit.Pick(_w, "Year in review", "_Which year", new[] { "Last year, " + (now.Year - 1), "This year so far" });
                if (i >= 0) GamesFunKit.Read(_w, "Year in review", YearReview(S, i == 0 ? now.Year - 1 : now.Year, now));
                return;
            }
            var lines = YearReview(S, now.Year, now);
            lines.Insert(0, "The full year in review opens on December 1. Here is a sneak peek so far.");
            GamesFunKit.Read(_w, "Year in review", lines);
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("streaks: " + what); }
            foreach (var topic in new[] { "records.shield", "records.monthnames", "records.comeback", "records.year" }) Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            Need(Content.Items("records.pass").Count >= Tiers, "the season pass needs " + Tiers + " items");
            Need(Content.Items("records.trail").Count >= 22 && Content.Items("records.trail").All(m => m.ContainsKey("touch")), "the trail needs a touch-first marker for every week");
            Need(Perks().Count >= 20 && Perks().Any(p => p.Kind == "shield"), "level perks");
            var today = new DateTime(2026, 11, 20, 10, 0, 0);
            var d = store.Data;
            // Streak math: rest days do not break it, today is not over, a gap does.
            var set = new HashSet<string> { "2026-11-16", "2026-11-17", "2026-11-19" };
            d.Days["2026-11-18"] = new DayInfo { Mode = DayMode.RoughDay };
            Need(GamesFunKit.Streak(set, d, today) == (3, 3), "a rough day broke the streak: " + GamesFunKit.Streak(set, d, today));
            d.Days.Remove("2026-11-18");
            Need(GamesFunKit.Streak(set, d, today).Current == 1, "a missed day did not end the run");
            // Shields: one every 14 days, at most 3, and one covers a missed day after a streak.
            store.World.SetKept(ShieldId, new Shields());
            for (int k = 1; k <= 5; k++) EarnShield(store, 14 * k, today.AddDays(k));
            EarnShield(store, 14, today.AddDays(1));
            Need(store.World.GetKept<Shields>(ShieldId).Have == MaxShields, "more than 3 shields, or a shield earned twice");
            d.Game.GoalDays.Clear();
            for (int k = 2; k <= 6; k++) d.Game.GoalDays.Add(GamesFunKit.Key(today.AddDays(-k)));
            string? note = UseShields(store, today);
            Need(note != null && d.Game.GoalDays.Contains(GamesFunKit.Key(today.AddDays(-1))) && store.World.GetKept<Shields>(ShieldId).Have == MaxShields - 1, "a shield did not cover the missed day");
            Need(Engine.Streaks(d, today).Current == 6, "the streak was not kept by the shield");
            store.Undo();
            d = store.Data;
            Need(!d.Game.GoalDays.Contains(GamesFunKit.Key(today.AddDays(-1))), "Control Z did not take the shield's day back");
            d.Game.GoalDays.Clear();
            // Season pass: tiers come from points, never past 30, and un-finishing lowers what was announced.
            Need(Tier(new Data(), today) == 0, "a new semester starts at tier 0");
            var fresh = new Data();
            fresh.Log.Add(new LogEntry { Kind = "Done", When = today, Points = 1000000 });
            Need(Tier(fresh, today) == Tiers, "the season pass went past 30 tiers");
            store.World.Set(PassId, new Pass());
            int size = TierSize(d, DateTime.Now);
            var entry = new LogEntry { Kind = "Done", When = DateTime.Now, Points = size * 3, TaskId = Guid.NewGuid() };
            d.Log.Add(entry);
            PassTier(store, DateTime.Now);
            d.Log.Add(new LogEntry { Kind = "Done", When = DateTime.Now, Points = size, TaskId = Guid.NewGuid() });
            Need(PassTier(store, DateTime.Now) != null, "reaching a new tier was not said");
            int announced = store.World.Get<Pass>(PassId).Announced;
            d.Log.RemoveAt(d.Log.Count - 1);
            PassBack(store, DateTime.Now);
            Need(store.World.Get<Pass>(PassId).Announced < announced, "un-finishing did not lower the season pass");
            d.Log.Remove(entry);
            // Trail, month names and the year.
            var (week, weeks) = TrailWeek(new DateTime(2026, 8, 1));
            Need(week == 1 && weeks >= 20, "the trail starts at mile 1");
            Need(MonthTitle(store, today) == MonthTitle(store, today), "a month's title changed");
            Need(YearReview(store, 2026, today).Count >= 2, "year in review");
            Need(CleanStreak(store, DateTime.Now) >= 0 && StartLine(store, DateTime.Now).Length > 0 && ComebackLine(store, DateTime.Now).Length > 0, "streak lines");
        }
    }
}
