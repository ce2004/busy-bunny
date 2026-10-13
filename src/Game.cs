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
    }

    /// <summary>What happened when points were added: things to say, and the one sound that matters most.</summary>
    public sealed class Outcome
    {
        public List<string> Said { get; } = new();
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
        public static readonly HashSet<string> KarmaKinds = new() { "Done", "Bonus", "Challenge", "Comeback" };

        // ---- Points and levels ----

        /// <summary>Effort size times 5, up to 1.75 times that for top priority on the list's own scale (spec 22.11). Done not submitted earns half.</summary>
        public static int PointsFor(TaskItem t, TaskList? list, bool half = false)
        {
            int n = list?.Scale.Length ?? 0;
            double mult = n > 1 && t.Priority >= 1 && t.Priority <= n ? 1 + 0.75 * (n - t.Priority) / (n - 1) : 1;
            int pts = (int)Math.Round(Words.Points(t.Size) * 5 * mult);
            return half ? Math.Max(1, pts / 2) : pts;
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
        /// </summary>
        public static Outcome Record(Store store, DateTime now, LogEntry e, TaskItem? task, bool firstForTask)
        {
            var d = store.Data;
            var g = d.Game;
            var o = new Outcome();
            int totalBefore = Total(d), dayBefore = PointsOn(d, now), weekBefore = PointsInWeek(d, now);
            string dayKey = now.ToString("yyyy-MM-dd");
            bool comeback = e.Kind == "Done" && firstForTask && dayBefore == 0 && MissedADay(d, now);
            e.When = now;
            d.Log.Add(e);
            int gained = e.Points;
            if (comeback)
            {
                d.Log.Add(new LogEntry { When = now, Kind = "Comeback", Text = "Comeback bonus", Points = 10 });
                gained += 10;
            }
            o.Said.Add(comeback
                ? e.Points + " points, plus 10 for coming back. Welcome back. Nobody was counting the days, except the math."
                : e.Points + (e.Points == 1 ? " point." : " points."));

            // Challenge progress.
            if (task != null && firstForTask && g.ChallengeDay == dayKey && g.ChallengeState == "Accepted")
            {
                var c = ChallengeToday(g);
                if (c.Counts(task, now, store))
                {
                    g.ChallengeProgress++;
                    if (g.ChallengeProgress >= c.Target)
                    {
                        g.ChallengeState = "Done";
                        d.Log.Add(new LogEntry { When = now, Kind = "Challenge", Text = c.Text, Points = c.Reward });
                        gained += c.Reward;
                        o.Said.Add("Challenge done: " + c.Text + ". " + c.Reward + " bonus points. Show off.");
                    }
                    else o.Said.Add("Challenge: " + g.ChallengeProgress + " of " + c.Target + ".");
                }
            }

            int totalAfter = totalBefore + gained, dayAfter = dayBefore + gained, weekAfter = weekBefore + gained;
            o.Said.Add(totalAfter + " total.");

            int levelBefore = LevelFor(totalBefore), levelAfter = LevelFor(totalAfter);
            if (levelAfter > levelBefore)
            {
                o.Said.Add("Level up! You are level " + levelAfter + ": " + LevelName(levelAfter) + ".");
                o.Ask(Sounds.Event.LevelUp);
            }

            // Daily goal and streak.
            int goal = DailyGoalFor(store, now);
            if (dayAfter >= goal && !g.GoalDays.Contains(dayKey))
            {
                g.GoalDays.Add(dayKey);
                o.Said.Add("Daily goal reached: " + dayAfter + " of " + goal + ". You can stop now, or keep going. The bunny has no opinion.");
                o.Ask(Sounds.Event.DailyGoal);
                var (cur, longest) = Streaks(d, now);
                if (cur >= 2)
                {
                    o.Said.Add("Streak extended: " + cur + " days in a row.");
                    o.Ask(Sounds.Event.StreakExtended);
                }
                if (longest > g.LongestStreak)
                {
                    if (g.LongestStreak >= 2)
                    {
                        o.Said.Add("New personal record: longest streak, " + longest + " days.");
                        g.RecordsBroken++;
                        o.Ask(Sounds.Event.PersonalRecord);
                    }
                    g.LongestStreak = longest;
                }
            }

            // Weekly goal.
            string weekKey = WeekStart(now).ToString("yyyy-MM-dd");
            if (weekAfter >= WeeklyGoalFor(d) && g.WeekGoalWeek != weekKey)
            {
                g.WeekGoalWeek = weekKey;
                g.WeekGoalsMet++;
                o.Said.Add("Weekly goal reached: " + weekAfter + " points this week.");
                o.Ask(Sounds.Event.DailyGoal);
            }

            // Records: said once a day and once a week, however many tasks beat them.
            if (dayAfter > g.BestDay)
            {
                if (g.BestDay >= 10 && g.RecordDayAnnounced != dayKey)
                {
                    g.RecordDayAnnounced = dayKey;
                    g.RecordsBroken++;
                    o.Said.Add("New personal record: best day, beating " + g.BestDay + " points.");
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
                    o.Said.Add("New personal record: best week, beating " + g.BestWeek + " points.");
                    o.Ask(Sounds.Event.PersonalRecord);
                }
                g.BestWeek = weekAfter;
            }

            foreach (var b in EarnBadges(store, now, task))
            {
                o.Said.Add("Badge earned: " + b.Name + ". " + b.How);
            }
            return o;
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

    /// <summary>For other features: awarding points.</summary>
    public static class Game
    {
        /// <summary>
        /// Gives points (a boss beaten, say), logs them and runs every check: level, goals, records, badges. Says the result
        /// and plays the one sound that matters. kind is the Data.Log kind; leave it as Bonus so the points count.
        /// </summary>
        public static void Award(MainWindow w, int points, string why, string kind = "Bonus")
        {
            if (points <= 0) return;
            var o = Engine.Record(w.Store, DateTime.Now, new LogEntry { Kind = kind, Text = why, Points = points }, null, false);
            w.Store.Save();
            Announce(w, why, o);
        }

        public static void Announce(MainWindow w, string lead, Outcome o)
        {
            if (o.Sound is Sounds.Event s) Sounds.Play(s);
            w.Say((lead.Length > 0 ? lead + ". " : "") + string.Join(" ", o.Said));
        }
    }
}
