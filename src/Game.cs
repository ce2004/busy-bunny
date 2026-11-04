using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>Everything the game remembers (spec 12.4, 10.5, 19.9, 19.10, 12.6). Points themselves live in Data.Log.</summary>
    public sealed class GameState
    {
        /// <summary>The bunny's name (spec 21.6: chosen later, changed in Options, Settings, Bunny and goals).</summary>
        public string BunnyName { get; set; } = "Bun";
        /// <summary>Daily goal in points, or 0 to work it out from her hours.</summary>
        public int DailyGoal { get; set; }
        /// <summary>Weekly goal in points, or 0 for five usual days.</summary>
        public int WeeklyGoal { get; set; }
        /// <summary>Days (yyyy-MM-dd) on which the daily goal was met.</summary>
        public List<string> GoalDays { get; set; } = new();
        /// <summary>Badge id and when it was earned.</summary>
        public Dictionary<string, DateTime> Badges { get; set; } = new();
        public int BestDay { get; set; }
        public int BestWeek { get; set; }
        public int LongestStreak { get; set; }
        public int RecordsBroken { get; set; }
        public int WeekGoalsMet { get; set; }
        public string RecordDayAnnounced { get; set; } = "";
        public string RecordWeekAnnounced { get; set; } = "";
        public string WeekGoalWeek { get; set; } = "";
        public int BunnyVisits { get; set; }
        // Today's challenge: offered, accepted, skipped or done.
        public string ChallengeDay { get; set; } = "";
        public string ChallengeState { get; set; } = "";
        public int ChallengeId { get; set; }
        public int ChallengeProgress { get; set; }
        public int ChallengeShift { get; set; }
        /// <summary>Tasks that counted toward today's challenge, each once; un-finishing one takes its step back.</summary>
        public List<Guid> ChallengeTasks { get; set; } = new();
        // What happened today (spec 8.1): every fun line, spoken or kept quiet, to hear later.
        public string HappenedDay { get; set; } = "";
        public List<string> Happened { get; set; } = new();
        // Unprompted lines today, for the daily cap in spec 8.1.
        public string UnpromptedDay { get; set; } = "";
        public int UnpromptedCount { get; set; }
        // Said once each, so un-finishing and re-finishing near a boundary never repeats them.
        public int LevelAnnounced { get; set; }
        public string GoalAnnounced { get; set; } = "";
        public string WeekGoalAnnounced { get; set; } = "";
    }

    /// <summary>What happened when points were added: things to say, and the one sound that matters most.</summary>
    public sealed class Outcome
    {
        /// <summary>Everything, in order, for the self-test and for What happened today.</summary>
        public List<string> Said { get; } = new();
        /// <summary>Points this added, bonuses included.</summary>
        public int Gained { get; set; }
        /// <summary>The fun lines with their priority (Game.LevelUp first): only the most important one is spoken.</summary>
        public List<(int Priority, string Text)> Fun { get; } = new();
        public void Add(int priority, string text) { Fun.Add((priority, text)); Said.Add(text); }
        public Sounds.Event? Sound { get; set; }
        public void Ask(Sounds.Event e)
        {
            // One sound at a time plays, so keep the biggest: level up, record, streak, goal.
            if (Sound == null || Rank(e) < Rank(Sound.Value)) Sound = e;
        }
        private static int Rank(Sounds.Event e) => e switch
        {
            Sounds.Event.LevelUp => 0,
            Sounds.Event.PersonalRecord => 1,
            Sounds.Event.StreakExtended => 2,
            Sounds.Event.DailyGoal => 3,
            _ => 4,
        };
    }

    public sealed record Badge(string Id, string Name, string How, Func<BadgeContext, bool> Test);

    public sealed class BadgeContext
    {
        public Store Store = null!;
        public Data Data => Store.Data;
        public GameState Game => Store.Data.Game;
        public DateTime Now;
        /// <summary>The task just finished, or null.</summary>
        public TaskItem? Task;
        public int Streak;
    }

    public sealed record Challenge(string Text, int Target, int Reward, Func<TaskItem, DateTime, Store, bool> Counts);

    /// <summary>The rules, kept apart from the window so the self-test can run them.</summary>
    public static class Engine
    {
        public static readonly HashSet<string> KarmaKinds = new() { "Done", "Bonus", "Challenge", "Comeback", Ledger.FunKind };

        // ---- Points and levels ----

        /// <summary>Effort size times 5, up to 1.75 times that for top priority on the list's own scale (spec 22.11). Done not submitted earns half.</summary>
        public static int PointsFor(TaskItem t, TaskList? list, bool half = false)
        {
            int n = list?.Scale.Length ?? 0;
            double mult = n > 1 && t.Priority >= 1 && t.Priority <= n ? 1 + 0.75 * (n - t.Priority) / (n - 1) : 1;
            int pts = (int)Math.Round(Words.Points(t.Size) * 5 * mult);
            return half ? Math.Max(1, pts / 2) : pts;
        }

        /// <summary>Bonus points for finishing before the planned day (its do date, or else its due date): 3 a day early, up to 15. 0 when not early.</summary>
        public static int EarlyBonus(TaskItem t, DateTime now)
        {
            if ((t.DoDate ?? t.Due) is not DateTime expected) return 0;
            int days = (expected.Date - now.Date).Days;
            return days <= 0 ? 0 : Math.Min(15, 3 * days);
        }

        public static int Total(Data d) => d.Log.Where(e => KarmaKinds.Contains(e.Kind)).Sum(e => e.Points);
        public static int PointsOn(Data d, DateTime day) => d.Log.Where(e => e.When.Date == day.Date && KarmaKinds.Contains(e.Kind)).Sum(e => e.Points);
        public static DateTime WeekStart(DateTime day) => day.Date.AddDays(-(((int)day.DayOfWeek + 6) % 7));
        public static int PointsInWeek(Data d, DateTime day)
        {
            var s = WeekStart(day);
            return d.Log.Where(e => e.When.Date >= s && e.When.Date < s.AddDays(7) && KarmaKinds.Contains(e.Kind)).Sum(e => e.Points);
        }

        public static int Threshold(int level) => 20 * level * (level - 1);
        public static int LevelFor(int points) { int l = 1; while (Threshold(l + 1) <= points) l++; return l; }

        private static readonly string[] LevelNames =
        {
            "Barely Awake Bunny", "Clover Nibbler", "Syllabus Skimmer", "Highlighter Hoarder", "Coffee-Powered Hopper",
            "Procrastination Escapee", "Study Group Legend", "Dean's List Dreamer", "Midterm Menace", "Thesis Tamer",
            "Campus Legend", "Burrow Boss",
        };

        public static string LevelName(int level) => level <= LevelNames.Length ? LevelNames[level - 1] : "Unreasonably Productive Bunny " + (level - LevelNames.Length);

        public static string BunnyStage(int level) => level switch
        {
            < 3 => "a fluffy kit",
            < 6 => "a young bunny with big feet",
            < 10 => "a confident grown bunny",
            _ => "a wise old bun with opinions",
        };

        // ---- Goals and streaks ----

        public static int DailyGoalFor(Store s, DateTime day)
        {
            var g = s.Data.Game;
            if (g.DailyGoal > 0) return g.DailyGoal;
            double goal = s.HoursToday(day) * 6;
            if (s.Data.Days.TryGetValue(s.Key(day), out var info) && info.Mode == DayMode.RoughDay) goal /= 2;
            return Math.Max(10, (int)Math.Round(goal));
        }

        public static int WeeklyGoalFor(Data d) => d.Game.WeeklyGoal > 0 ? d.Game.WeeklyGoal : Math.Max(30, (int)Math.Round(d.HoursAvailable * 6) * 5);

        private static bool RestDay(Data d, DateTime day) =>
            d.Days.TryGetValue(day.ToString("yyyy-MM-dd"), out var i) && i.Mode is DayMode.Vacation or DayMode.RoughDay;

        /// <summary>The streak running now and the longest between two days. Vacation and rough days never break it, and today is not over yet.</summary>
        public static (int Current, int Longest) Streaks(Data d, DateTime today, DateTime? from = null)
        {
            var goals = new HashSet<string>(d.Game.GoalDays);
            var days = d.Game.GoalDays.Select(s => DateTime.TryParse(s, out var x) ? x.Date : DateTime.MaxValue).Where(x => x <= today.Date).ToList();
            if (days.Count == 0) return (0, 0);
            var start = days.Min();
            if (from is DateTime f && f > start) start = f.Date;
            int cur = 0, best = 0;
            for (var day = start; day <= today.Date; day = day.AddDays(1))
            {
                if (goals.Contains(day.ToString("yyyy-MM-dd"))) { cur++; best = Math.Max(best, cur); }
                else if (day == today.Date || RestDay(d, day)) continue;
                else cur = 0;
            }
            return (cur, best);
        }

        /// <summary>Whether the last day that counted (skipping vacation and rough days) had no points, with some earlier points on record.</summary>
        public static bool MissedADay(Data d, DateTime today)
        {
            var day = today.Date.AddDays(-1);
            for (int i = 0; i < 60 && RestDay(d, day); i++) day = day.AddDays(-1);
            if (PointsOn(d, day) > 0) return false;
            return d.Log.Any(e => e.When.Date < day && KarmaKinds.Contains(e.Kind) && e.Points > 0);
        }

        // ---- Recording points ----

        /// <summary>
        /// Adds a log entry that earns points, then checks everything it can change: comeback bonus, challenge, level,
        /// goals, streak, records and badges. Does not save. firstForTask is true when this task has earned nothing yet.
        /// extra is a bonus earned with it (finishing early): it goes in first, so every check sees it.
        /// Every entry a task causes carries its TaskId, so un-finishing it (Unfinish) takes them all back.
        /// </summary>
        public static Outcome Record(Store store, DateTime now, LogEntry e, TaskItem? task, bool firstForTask, LogEntry? extra = null)
        {
            var d = store.Data;
            var g = d.Game;
            var o = new Outcome();
            int totalBefore = Total(d), dayBefore = PointsOn(d, now), weekBefore = PointsInWeek(d, now);
            string dayKey = now.ToString("yyyy-MM-dd");
            // From finished tasks only: a boss bonus logged a moment earlier must not cost her the comeback.
            bool doneToday = d.Log.Any(x => x.When.Date == now.Date && x.Kind is "Done" or "Comeback" && x.Points > 0);
            bool comeback = e.Kind == "Done" && firstForTask && !doneToday && MissedADay(d, now);
            e.When = now;
            d.Log.Add(e);
            int gained = e.Points;
            if (extra != null)
            {
                extra.When = now;
                extra.TaskId ??= e.TaskId;
                d.Log.Add(extra);
                gained += extra.Points;
            }
            if (comeback)
            {
                d.Log.Add(new LogEntry { When = now, Kind = "Comeback", Text = "Comeback bonus", Points = 10, TaskId = e.TaskId });
                gained += 10;
                o.Add(Game.Goals, "Plus 10 for coming back. Welcome back. Nobody was counting the days, except the math.");
            }

            // Challenge progress: each task counts once a day, however often it is finished and un-finished.
            if (task != null && g.ChallengeDay == dayKey && g.ChallengeState == "Accepted" && !g.ChallengeTasks.Contains(task.Id))
            {
                var c = ChallengeToday(g);
                if (c.Counts(task, now, store))
                {
                    g.ChallengeTasks.Add(task.Id);
                    g.ChallengeProgress++;
                    if (g.ChallengeProgress >= c.Target)
                    {
                        g.ChallengeState = "Done";
                        d.Log.Add(new LogEntry { When = now, Kind = "Challenge", Text = c.Text, Points = c.Reward, TaskId = task.Id });
                        gained += c.Reward;
                        o.Add(Game.Goals, "Challenge done: " + c.Text + ". " + c.Reward + " bonus points. Show off.");
                    }
                    else o.Add(Game.Other, "Challenge: " + g.ChallengeProgress + " of " + c.Target + ".");
                }
            }

            int totalAfter = totalBefore + gained, dayAfter = dayBefore + gained, weekAfter = weekBefore + gained;
            o.Gained = gained;
            o.Said.Insert(0, gained + (gained == 1 ? " point, " : " points, ") + totalAfter + " total.");

            int levelBefore = LevelFor(totalBefore), levelAfter = LevelFor(totalAfter);
            if (levelAfter > levelBefore && levelAfter > g.LevelAnnounced)
            {
                g.LevelAnnounced = levelAfter;
                o.Add(Game.LevelUp, "Level up! You are level " + levelAfter + ": " + LevelName(levelAfter) + ".");
                o.Ask(Sounds.Event.LevelUp);
                GameEvents.RaiseLevelUp(levelAfter);
            }

            // Daily goal and streak.
            int goal = DailyGoalFor(store, now);
            if (dayAfter >= goal && !g.GoalDays.Contains(dayKey) && g.GoalAnnounced == dayKey) g.GoalDays.Add(dayKey);
            else if (dayAfter >= goal && !g.GoalDays.Contains(dayKey))
            {
                g.GoalDays.Add(dayKey);
                g.GoalAnnounced = dayKey;
                o.Add(Game.Goals, "Daily goal reached: " + dayAfter + " of " + goal + ". You can stop now, or keep going. The bunny has no opinion.");
                o.Ask(Sounds.Event.DailyGoal);
                var (cur, longest) = Streaks(d, now);
                GameEvents.RaiseStreakChanged(cur);
                if (cur >= 2)
                {
                    o.Add(Game.Goals, "Streak extended: " + cur + " days in a row.");
                    o.Ask(Sounds.Event.StreakExtended);
                }
                if (longest > g.LongestStreak)
                {
                    if (g.LongestStreak >= 2)
                    {
                        o.Add(Game.Record, "New personal record: longest streak, " + longest + " days.");
                        g.RecordsBroken++;
                        o.Ask(Sounds.Event.PersonalRecord);
                    }
                    g.LongestStreak = longest;
                }
            }

            // Weekly goal.
            string weekKey = WeekStart(now).ToString("yyyy-MM-dd");
            if (weekAfter >= WeeklyGoalFor(d) && g.WeekGoalWeek != weekKey && g.WeekGoalAnnounced == weekKey) { g.WeekGoalWeek = weekKey; g.WeekGoalsMet++; }
            else if (weekAfter >= WeeklyGoalFor(d) && g.WeekGoalWeek != weekKey)
            {
                g.WeekGoalWeek = weekKey;
                g.WeekGoalAnnounced = weekKey;
                g.WeekGoalsMet++;
                o.Add(Game.Goals, "Weekly goal reached: " + weekAfter + " points this week.");
                o.Ask(Sounds.Event.DailyGoal);
            }

            // Records: said once a day and once a week, however many tasks beat them.
            if (dayAfter > g.BestDay)
            {
                if (g.BestDay >= 10 && g.RecordDayAnnounced != dayKey)
                {
                    g.RecordDayAnnounced = dayKey;
                    g.RecordsBroken++;
                    o.Add(Game.Record, "New personal record: best day, beating " + g.BestDay + " points.");
                    o.Ask(Sounds.Event.PersonalRecord);
                }
                g.BestDay = dayAfter;
            }
            if (weekAfter > g.BestWeek)
            {
                if (g.BestWeek >= 30 && g.RecordWeekAnnounced != weekKey)
                {
                    g.RecordWeekAnnounced = weekKey;
                    g.RecordsBroken++;
                    o.Add(Game.Record, "New personal record: best week, beating " + g.BestWeek + " points.");
                    o.Ask(Sounds.Event.PersonalRecord);
                }
                g.BestWeek = weekAfter;
            }

            foreach (var b in EarnBadges(store, now, task))
            {
                o.Add(Game.Badge, "Badge earned: " + b.Name + ". " + b.How);
                GameEvents.RaiseBadgeEarned(b.Name);
            }
            return o;
        }

        /// <summary>
        /// A task went back out of finished (spec 8.2 B): takes back every entry finishing it caused, with its side
        /// effects: comeback, challenge step and reward, bet winnings (the bet opens again), mystery box (open again),
        /// quest reward, boss, and the goal day when the day no longer reaches the goal. Returns whether anything changed. Does not save.
        /// </summary>
        public static bool Unfinish(Store store, TaskItem t, DateTime now)
        {
            var d = store.Data;
            var g = d.Game;
            var p = d.Play;
            // A bet won in time whose deadline has passed stays won, winnings and all: un-finishing by mistake
            // at night must not hand Vex a bet she won in the afternoon. Bets still before their deadline reopen.
            var keepWon = p.Bets.Any(b => b.TaskId == t.Id && b.State == "Won" && b.By <= now);
            var mine = d.Log.Where(e => e.TaskId == t.Id && (e.Kind is "Done" or "Bonus" or "Comeback" or "Challenge" or "Boss" or Ledger.FunKind)
                && !(keepWon && e.Kind == "Bonus" && e.Text.StartsWith("Won the bet on "))).ToList();
            bool changed = mine.Count > 0;
            var days = mine.Select(e => e.When.Date).ToHashSet();
            foreach (var e in mine) d.Log.Remove(e);

            // The challenge step, and the reward if the challenge is no longer met.
            if (g.ChallengeTasks.Remove(t.Id))
            {
                changed = true;
                if (g.ChallengeProgress > 0) g.ChallengeProgress--;
                if (g.ChallengeState == "Done" && g.ChallengeProgress < ChallengeToday(g).Target)
                {
                    g.ChallengeState = "Accepted";
                    d.Log.RemoveAll(e => e.Kind == "Challenge" && e.When.Date == now.Date);
                    days.Add(now.Date);
                }
            }

            // Bets won with it open again; the winnings went with the entries above.
            foreach (var b in p.Bets.Where(b => b.TaskId == t.Id && b.State == "Won" && b.By > now)) { b.State = "Open"; b.FinishedAt = null; changed = true; }
            if (mine.Any(e => e.Kind == "Bonus" && e.Text.StartsWith(PlayTime.MysteryLead)) && p.MysteryDay == store.Key(now)) p.MysteryTask = t.Id;

            // The quest is no longer complete: its reward goes, whichever task finished it.
            if (p.QuestTasks.Contains(t.Id) && p.QuestWeek == PlayTime.WeekKey(now))
            {
                var left = p.QuestTasks.Select(id => d.Tasks.FirstOrDefault(x => x.Id == id)).Where(x => x != null && !store.ListOf(x).Archived).ToList();
                p.QuestCleared = left.Count(x => PlayTime.Cleared(x));
                if (p.QuestDone)
                {
                    p.QuestDone = false;
                    changed = true;
                    var start = WeekStart(now);
                    foreach (var e in d.Log.Where(e => e.Kind == "Bonus" && e.Text == PlayTime.QuestLead + p.QuestName && e.When.Date >= start).ToList())
                    {
                        days.Add(e.When.Date);
                        d.Log.Remove(e);
                    }
                }
            }
            if (changed) RecheckGoals(store, days, now);
            return changed;
        }

        /// <summary>Drops goal days (and this week's goal) that the points no longer reach, after points were taken back.</summary>
        /// <summary>Set by streak shields: a goal day a shield covered is kept, whatever its points.</summary>
        public static Func<Store, string, bool>? KeepGoalDay;

        public static void RecheckGoals(Store store, IEnumerable<DateTime> days, DateTime now)
        {
            var d = store.Data;
            var g = d.Game;
            foreach (var day in days)
            {
                string key = day.ToString("yyyy-MM-dd");
                if (KeepGoalDay?.Invoke(store, key) == true) continue;
                if (g.GoalDays.Contains(key) && PointsOn(d, day) < DailyGoalFor(store, day)) g.GoalDays.Remove(key);
            }
            if (g.WeekGoalWeek == WeekStart(now).ToString("yyyy-MM-dd") && PointsInWeek(d, now) < WeeklyGoalFor(d))
            {
                g.WeekGoalWeek = "";
                if (g.WeekGoalsMet > 0) g.WeekGoalsMet--;
            }
        }

        /// <summary>Checks every badge and records the new ones.</summary>
        public static List<Badge> EarnBadges(Store store, DateTime now, TaskItem? task)
        {
            var ctx = new BadgeContext { Store = store, Now = now, Task = task, Streak = Streaks(store.Data, now).Current };
            var won = new List<Badge>();
            foreach (var b in Badges.All)
            {
                if (store.Data.Game.Badges.ContainsKey(b.Id)) continue;
                bool ok;
                try { ok = b.Test(ctx); } catch { ok = false; }
                if (!ok) continue;
                store.Data.Game.Badges[b.Id] = now;
                won.Add(b);
            }
            return won;
        }

        // ---- Challenges (spec 12.6) ----

        private static bool Top(Store s, TaskItem t) => t.Priority == 1 && s.ListOf(t).Scale.Length > 0;

        public static readonly Challenge[] Challenges =
        {
            new("Finish two Homework tasks before lunch", 2, 15, (t, n, s) => n.Hour < 12 && (t.List == "Homework" || !s.Data.Lists.Any(l => l.Name == "Homework"))),
            new("Finish one thing that has been sitting around for a week", 1, 15, (t, n, s) => t.CreatedAt <= n.AddDays(-7)),
            new("Finish three tasks of any kind", 3, 20, (t, n, s) => true),
            new("Finish a top priority task before 3 pm", 1, 15, (t, n, s) => n.Hour < 15 && Top(s, t)),
            new("Knock out two small tasks", 2, 10, (t, n, s) => t.Size == Size.Small),
            new("Finish one task from your Personal lists", 1, 10, (t, n, s) => t.List.StartsWith("Personal", StringComparison.OrdinalIgnoreCase)),
            new("Finish one Medium or Large task", 1, 12, (t, n, s) => t.Size is Size.Medium or Size.Large),
        };

        public static Challenge ChallengeToday(GameState g) => Challenges[((g.ChallengeId % Challenges.Length) + Challenges.Length) % Challenges.Length];

        /// <summary>Makes sure the stored challenge is today's: a new one is offered each morning, and yesterday's is forgotten at no cost.</summary>
        public static void RollChallenge(GameState g, DateTime today)
        {
            string key = today.ToString("yyyy-MM-dd");
            if (g.ChallengeDay == key) return;
            g.ChallengeDay = key;
            g.ChallengeState = "Offered";
            g.ChallengeProgress = 0;
            g.ChallengeTasks = new();
            g.ChallengeShift = 0;
            g.ChallengeId = (today.DayOfYear * 7 + today.Year) % Challenges.Length;
        }
    }

    /// <summary>Badges with funny names (spec 19.10). Each has a plain rule, shown in the list so she knows how to get it.</summary>
    public static class Badges
    {
        private static IEnumerable<LogEntry> DoneLog(BadgeContext c) => c.Data.Log.Where(e => e.Kind == "Done");
        private static int Finished(BadgeContext c) => DoneLog(c).Select(e => e.TaskId ?? Guid.Empty).Distinct().Count();
        private static int MostInADay(BadgeContext c) => DoneLog(c).GroupBy(e => e.When.Date).Select(g => g.Select(e => e.TaskId ?? Guid.NewGuid()).Distinct().Count()).DefaultIfEmpty(0).Max();
        private static bool Named(BadgeContext c, params string[] words) =>
            DoneLog(c).Any(e => words.Any(w => e.Text.Contains(w, StringComparison.OrdinalIgnoreCase)));
        private static int Level(BadgeContext c) => Engine.LevelFor(Engine.Total(c.Data));

        public static readonly List<Badge> All = new()
        {
            new("first", "First Hop", "Finish your first task.", c => Finished(c) >= 1),
            new("ten", "Double Digit Dasher", "Finish 10 tasks.", c => Finished(c) >= 10),
            new("fifty", "Half a Hundred", "Finish 50 tasks.", c => Finished(c) >= 50),
            new("hundred", "Centurion Bun", "Finish 100 tasks.", c => Finished(c) >= 100),
            new("hat", "Hat Trick", "Finish 3 tasks in one day.", c => MostInADay(c) >= 3),
            new("goblin", "Productivity Goblin", "Finish 8 tasks in one day.", c => MostInADay(c) >= 8),
            new("midterms", "Survived Midterms", "Finish a task with test, exam, midterm or quiz in its name.", c => Named(c, "test", "exam", "midterm", "quiz")),
            new("finals", "Finals Week Survivor", "Finish a task with final in its name.", c => Named(c, "final")),
            new("dodger", "Deadline Dodger", "Finish a task on the day it was due, before the due time.",
                c => c.Task?.Due is DateTime d && d.Date == c.Now.Date && (!c.Task.DueHasTime || d >= c.Now)),
            new("early", "Early Bird", "Finish a task before 8 am.", c => c.Task != null && c.Now.Hour < 8),
            new("owl", "Night Owl", "Finish a task after 10 pm.", c => c.Task != null && c.Now.Hour >= 22),
            new("cheese", "Big Cheese", "Finish a top priority task.", c => c.Task != null && c.Store.Data.Lists.FirstOrDefault(l => l.Name == c.Task.List) is TaskList l && l.Scale.Length > 0 && c.Task.Priority == 1),
            new("triple", "Triple Shot Hero", "Finish 3 top priority tasks in one day.",
                c => c.Data.Tasks.Count(t => t.Status == Status.Done && t.DoneAt?.Date == c.Now.Date && t.Priority == 1 && c.Store.ListOf(t).Scale.Length > 0) >= 3),
            new("rock", "Big Rock Crusher", "Finish a Large task.", c => c.Task?.Size == Size.Large),
            new("tiny", "Tiny Triumphs", "Finish 10 Small tasks.", c => c.Data.Tasks.Count(t => t.Status == Status.Done && t.Size == Size.Small) >= 10),
            new("slayer", "Procrastination Slayer", "Finish a task that has been waiting 14 days or more.", c => c.Task != null && c.Task.CreatedAt <= c.Now.AddDays(-14)),
            new("rough", "Rough Day Hero", "Finish something on a day you marked as a rough day.",
                c => c.Task != null && c.Data.Days.TryGetValue(c.Store.Key(c.Now), out var i) && i.Mode == DayMode.RoughDay),
            new("weekend", "Weekend Warrior", "Finish a task on a Saturday or Sunday.", c => c.Task != null && c.Now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday),
            new("tagteam", "Tag Team", "Finish tasks with 3 different tags in one day.",
                c => DoneLog(c).Where(e => e.When.Date == c.Now.Date && e.Tag.Length > 0).Select(e => e.Tag.ToLowerInvariant()).Distinct().Count() >= 3),
            new("sweep", "Clean Sweep", "Finish everything on Today.", c => { var p = c.Store.Progress(c.Now); return c.Task != null && p.Total > 0 && p.Done == p.Total; }),
            new("streak3", "Three-Peat", "Meet your daily goal 3 days in a row.", c => c.Streak >= 3),
            new("streak7", "Week of Wonders", "Meet your daily goal 7 days in a row.", c => c.Streak >= 7),
            new("streak14", "Fortnight Fanatic", "Meet your daily goal 14 days in a row.", c => c.Streak >= 14),
            new("weekly", "Weekly Overachiever", "Reach your weekly goal.", c => c.Game.WeekGoalsMet >= 1),
            new("level5", "Level Five Alive", "Reach level 5.", c => Level(c) >= 5),
            new("level10", "Tenth Level Tyrant", "Reach level 10.", c => Level(c) >= 10),
            new("thousand", "Four Digits of Fabulous", "Earn 1000 points in total.", c => Engine.Total(c.Data) >= 1000),
            new("comeback", "Comeback Kid", "Finish a task after a missed day and get the comeback bonus.", c => c.Data.Log.Any(e => e.Kind == "Comeback")),
            new("challenge", "Challenge Accepted", "Finish a daily challenge.", c => c.Data.Log.Any(e => e.Kind == "Challenge")),
            new("boss", "Boss Slayer", "Beat a boss.", c => c.Data.Log.Any(e => e.Kind == "Boss")),
            new("record", "Record Breaker", "Break one of your personal records.", c => c.Game.RecordsBroken >= 1),
            new("parent", "Proud Parent", "Give your bunny a name other than Bun.", c => c.Game.BunnyName != "Bun" && c.Game.BunnyName.Length > 0),
        };
    }

    /// <summary>For other features: awarding points, and the one line said after a finished task.</summary>
    public static class Game
    {
        // Fun line priorities (spec 8.1): level up, then records, then everything else.
        public const int LevelUp = 0, Record = 1, Goals = 2, Other = 3, Badge = 4, Chatter = 5;

        /// <summary>
        /// Gives points (a boss beaten, say), logs them and runs every check: level, goals, records, badges. The points
        /// join the line said after the finished task, and why is one of its fun lines. taskId ties the points to the task
        /// that earned them, so un-finishing it takes them back. silent keeps everything for What happened today.
        /// </summary>
        public static void Award(MainWindow w, int points, string why, string kind = "Bonus", Guid? taskId = null, bool silent = false)
        {
            if (points <= 0) return;
            var t = taskId is Guid id ? w.Store.Data.Tasks.FirstOrDefault(x => x.Id == id) : null;
            var o = Engine.Record(w.Store, DateTime.Now, new LogEntry { Kind = kind, Text = why, Points = points, TaskId = taskId, Tag = t?.Tag ?? "", List = t?.List ?? "" }, null, false);
            o.Fun.Insert(0, (Other, why + "."));
            w.Store.Save();
            if (silent) { foreach (var line in o.Said) Note(w.Store, line); return; }
            Queue(w, o);
        }

        public static void Announce(MainWindow w, string lead, Outcome o)
        {
            if (lead.Length > 0) o.Fun.Insert(0, (Other, lead + "."));
            Queue(w, o);
        }

        // ---- One line per finished task (spec 8.1) ----

        /// <summary>A fun line that arrives later (the narrator). Chosen says whether it may be spoken.</summary>
        public sealed class LateLine { public int Priority; public bool Chosen; public bool Decided; }

        private static readonly List<(int Priority, string Text)> Fun = new();
        private static readonly List<LateLine> Late = new();
        private static Outcome _sound = new();
        private static int _gained;
        private static bool _points, _scheduled;

        /// <summary>Adds what Record found to the line said once this task (or every task marked at once) is handled.</summary>
        public static void Queue(MainWindow w, Outcome o)
        {
            _points = true;
            _gained += o.Gained;
            if (o.Sound is Sounds.Event e) _sound.Ask(e);
            Fun.AddRange(o.Fun);
            Schedule(w);
        }

        /// <summary>Offers one fun line. Only the most important line of the moment is spoken; the rest go to What happened today.</summary>
        public static void Queue(MainWindow w, int priority, string text)
        {
            if (text.Length == 0) return;
            Fun.Add((priority, text));
            Schedule(w);
        }

        /// <summary>Holds a place for a line that comes later. It is spoken only if nothing more important was.</summary>
        public static LateLine QueueLate(MainWindow w, int priority)
        {
            var l = new LateLine { Priority = priority };
            Late.Add(l);
            Schedule(w);
            return l;
        }

        private static void Schedule(MainWindow? w)
        {
            if (_scheduled || w == null) return;
            _scheduled = true;
            // After every feature has handled the change, and after the app's own Done line.
            w.Dispatcher.BeginInvoke(() => Flush(w), System.Windows.Threading.DispatcherPriority.Background);
        }

        /// <summary>Says the points and the single most important fun line, and keeps the rest.</summary>
        public static string Flush(MainWindow? w, Store? store = null)
        {
            _scheduled = false;
            store ??= w?.Store;
            (int Priority, string Text)? best = Fun.Count == 0 ? null : Fun.OrderBy(f => f.Priority).First();
            var late = Late.OrderBy(l => l.Priority).FirstOrDefault();
            // In Calm mode a late line (the narrator) is never chosen: it is only kept.
            if (late != null && !BusyBunny.Calm.FunPaused && (best == null || late.Priority < best.Value.Priority)) { late.Chosen = true; best = null; }
            foreach (var l in Late) l.Decided = true;
            string text = _points && _gained > 0 && store != null ? _gained + (_gained == 1 ? " point, " : " points, ") + Engine.Total(store.Data) + " total." : "";
            // Calm mode keeps every fun line for What happened today and speaks none; the points line stays.
            if (best != null && !BusyBunny.Calm.FunPaused) text += (text.Length > 0 ? " " : "") + best.Value.Text;
            if (store != null) foreach (var f in Fun) Note(store, f.Text);
            var sound = _sound.Sound;
            Fun.Clear();
            Late.Clear();
            _sound = new Outcome();
            _gained = 0;
            _points = false;
            if (w != null)
            {
                if (sound is Sounds.Event s) Sounds.Play(s);
                if (text.Length > 0) w.Say(text);
            }
            return text;
        }

        /// <summary>Keeps a line for What happened today.</summary>
        public static void Note(Store s, string line)
        {
            var g = s.Data.Game;
            string today = s.Key(DateTime.Now);
            if (g.HappenedDay != today) { g.HappenedDay = today; g.Happened = new(); }
            if (g.Happened.Count >= 200) g.Happened.RemoveAt(0);
            g.Happened.Add(DateTime.Now.ToString("h:mm tt") + ": " + line);
        }

        // ---- Calm rules (spec 8.1): kept for older callers, the rules live in Calm ----

        /// <summary>Whether a line she did not ask for must stay quiet now (Calm.Hush says why).</summary>
        public static bool Calm(Store s, DateTime now) => BusyBunny.Calm.Hush(s, Engines.Settings, now) != null;

        /// <summary>Whether one unprompted line may be spoken now, under today's cap. Counts it when it may.</summary>
        public static bool MaySpeakUnprompted(Store s, DateTime now)
        {
            if (!BusyBunny.Calm.CanSpeakUnprompted(s, Engines.Settings, now)) return false;
            BusyBunny.Calm.Count(s, now);
            return true;
        }
    }
}
