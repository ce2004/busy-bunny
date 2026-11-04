using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>The game's menus, key and settings page: karma, goals, scoreboard, badges, bunny, challenges, Semester Wrapped.</summary>
    public sealed class GameFeature : IFeature
    {
        private MainWindow _w = null!;
        private Store S => _w.Store;
        private GameState G => _w.Store.Data.Game;

        public void Attach(MainWindow w)
        {
            _w = w;
            // Out of the Fun menu (sweep 2.7): the toolbox and the Bunny tab.
            Tools.Register(new BunnyTool("scoreboard", "Scoreboard", Drawers.Wins, "Your level, points, goals, streak and records.", ShowScoreboard) { MovedFrom = "the Fun menu", Keys = "Control Shift G" });
            Tools.Register(new BunnyTool("badges", "Badges", Drawers.Wins, "The badges you have earned, and how to get the rest.", ShowBadges) { MovedFrom = "the Fun menu" });
            Tools.Register(new BunnyTool("happened", "What happened today", Drawers.Wins, "Every win, record and fun line from today, newest last.", ShowHappened) { MovedFrom = "the Fun menu" });
            Tools.Register(new BunnyTool("wrapped", "Semester Wrapped", Drawers.Wins, "A recap of your semester so far.", ShowWrapped) { MovedFrom = "the Fun menu" });
            Tools.Register(new BunnyTool("visitbunny", "Visit your bunny", Drawers.World, "What your bunny did lately, then how it is doing.", VisitBunny) { MovedFrom = "the Fun menu" });
            Tools.Register(new BunnyTool("challenges", "Daily challenge", Drawers.Games, "Today's optional challenge, for bonus points.", ShowChallenge) { MovedFrom = "the Fun menu" });
            BunnyTab.Register("Visit your bunny", "what your bunny did lately, newest first, then how it is doing", VisitBunny, "visitbunny");
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.G, Tools.Guard("scoreboard", ShowScoreboard), "Ctrl+Shift+G: Scoreboard, your records, level and points");
            w.StatusChanged += OnStatus;
            w.Started += () => Engine.RollChallenge(G, DateTime.Now);
            w.NewDay += _ => Engine.RollChallenge(G, DateTime.Now);
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) { yield return new FunPage(store); }

        // ---- Finishing tasks ----

        private void OnStatus(TaskItem t, Status old)
        {
            var now = DateTime.Now;
            if (Finish(S, t, old, now) is Outcome o) Game.Queue(_w, o);
            S.Save();
            // The event bus, once the points are in (or taken back), so every handler sees the totals as they now are.
            bool was = old is Status.Done or Status.DoneNotSubmitted, done = t.Status is Status.Done or Status.DoneNotSubmitted;
            GameEvents.RaiseStatusChanged(t, old);
            if (done && !was) GameEvents.RaiseTaskDone(t);
            else if (was && !done) GameEvents.RaiseTaskUndone(t);
        }

        /// <summary>
        /// Pays a task for its status, or takes back what it paid. Finished pays once (done, undone, done again pays once);
        /// moving out of finished removes every entry finishing caused; Done not submitted earns half, so going there from
        /// Done corrects the points down. Returns what to say, or null. Does not save.
        /// </summary>
        public static Outcome? Finish(Store s, TaskItem t, Status old, DateTime now)
        {
            var d = s.Data;
            bool finished = t.Status is Status.Done or Status.DoneNotSubmitted;
            if (!finished)
            {
                if (old is Status.Done or Status.DoneNotSubmitted) Engine.Unfinish(s, t, now);
                return null;
            }
            var prior = d.Log.Where(e => e.Kind == "Done" && e.TaskId == t.Id).ToList();
            int owed = Engine.PointsFor(t, s.ListOf(t), half: t.Status == Status.DoneNotSubmitted);
            int got = prior.Sum(e => e.Points);
            if (owed < got && prior.Count > 0)
            {
                // A correction, not a penalty: Done not submitted is worth half.
                prior[0].Points = owed;
                foreach (var p in prior.Skip(1)) d.Log.Remove(p);
                Engine.RecheckGoals(s, prior.Select(p => p.When.Date).Distinct().ToList(), now);
                return null;
            }
            if (owed == got) return null;
            var entry = new LogEntry { Kind = "Done", Text = t.Name, Tag = t.Tag, List = t.List, Points = owed - got, TaskId = t.Id };
            // Morgan's issue 10: finishing before the day she planned (or, with no plan, before it is due) earns extra.
            LogEntry? early = null;
            if (got == 0 && Engine.EarlyBonus(t, now) is int bonus and > 0)
                early = new LogEntry { Kind = "Bonus", Text = "Finished early: " + t.Name, Points = bonus, TaskId = t.Id, Tag = t.Tag, List = t.List };
            var o = Engine.Record(s, now, entry, t, got == 0, early);
            if (early != null) o.Add(Game.Other, "Plus " + early.Points + " for finishing early. Overachiever.");
            return o;
        }

        private void ShowHappened()
        {
            string today = S.Key(DateTime.Now);
            var lines = G.HappenedDay == today ? G.Happened.ToList() : new List<string>();
            if (lines.Count == 0) lines.Add("Nothing yet today. Finish something and the wins and records land here.");
            TextViewer.Show(_w, "What happened today", lines);
        }

        // ---- Scoreboard ----

        private void ShowScoreboard()
        {
            var d = S.Data;
            var now = DateTime.Now;
            int total = Engine.Total(d), level = Engine.LevelFor(total);
            var (cur, longest) = Engine.Streaks(d, now);
            longest = Math.Max(longest, G.LongestStreak);
            int today = Engine.PointsOn(d, now), week = Engine.PointsInWeek(d, now);
            var lines = new List<string>
            {
                "Level " + level + ": " + Engine.LevelName(level) + ".",
                "Total points: " + total + ". " + (Engine.Threshold(level + 1) - total) + " to level " + (level + 1) + ".",
                "Today: " + today + " of " + Engine.DailyGoalFor(S, now) + " points" + (today >= Engine.DailyGoalFor(S, now) ? ", goal met." : "."),
                "This week: " + week + " of " + Engine.WeeklyGoalFor(d) + " points" + (week >= Engine.WeeklyGoalFor(d) ? ", goal met." : "."),
                "Current streak: " + cur + (cur == 1 ? " day." : " days.") + " Vacation and rough days never break it.",
                "Best day: " + Math.Max(G.BestDay, today) + " points.",
                "Best week: " + Math.Max(G.BestWeek, week) + " points.",
                "Longest streak: " + longest + (longest == 1 ? " day." : " days."),
                "Badges: " + G.Badges.Count + " of " + Badges.All.Count + ". Records broken: " + G.RecordsBroken + ".",
            };
            TextViewer.Show(_w, "Scoreboard", lines);
        }

        // ---- Badges ----

        private void ShowBadges()
        {
            var lines = new List<string> { "Earned (" + G.Badges.Count + "):" };
            foreach (var b in Badges.All.Where(b => G.Badges.ContainsKey(b.Id)).OrderBy(b => G.Badges[b.Id]))
                lines.Add(b.Name + ", earned " + G.Badges[b.Id].ToString("MMMM d") + ". " + b.How);
            if (G.Badges.Count == 0) lines.Add("None yet. The first one is easy.");
            int locked = Badges.All.Count(b => !G.Badges.ContainsKey(b.Id));
            lines.Add("Still locked (" + locked + "):");
            foreach (var b in Badges.All.Where(b => !G.Badges.ContainsKey(b.Id)))
                lines.Add(b.Name + ". " + b.How);
            TextViewer.Show(_w, "Badges", lines, l => l.EndsWith("):"));
        }

        // ---- Bunny ----

        /// <summary>How the bunny is, from recent activity. It never scolds: quiet days just mean a nap.</summary>
        public static string Mood(Store s, DateTime now)
        {
            var d = s.Data;
            string name = d.Game.BunnyName;
            if (d.Days.TryGetValue(s.Key(now), out var info))
            {
                if (info.Mode == DayMode.Vacation) return name + " is on vacation with you, ears flopped, expecting nothing.";
                if (info.Mode == DayMode.RoughDay) return name + " is curled up against you, quiet. Rough days count as rest days here.";
            }
            int today = Engine.PointsOn(d, now);
            int recent = Enumerable.Range(1, 3).Sum(i => Engine.PointsOn(d, now.AddDays(-i)));
            if (today >= Engine.DailyGoalFor(s, now)) return name + " is doing happy little jumps around the room. You earned that.";
            if (today > 0) return name + " is cheerful and chewing a carrot, watching you work.";
            if (recent > 0) return name + " is relaxed and stretching, ready whenever you are.";
            return name + " is napping in a sunbeam. Naps are its whole thing, so there is nothing to catch up on.";
        }

        private void VisitBunny()
        {
            var now = DateTime.Now;
            int total = Engine.Total(S.Data), level = Engine.LevelFor(total);
            G.BunnyVisits++;
            S.Save();
            // Sweep 11.4: the most recent thing Bunny said or did first, newest first, then how the bunny is.
            var lines = new List<string>();
            if (G.HappenedDay == S.Key(now)) lines.AddRange(Enumerable.Reverse(G.Happened).Take(20));
            lines.AddRange(new List<string>
            {
                G.BunnyName + " is " + Engine.BunnyStage(level) + ", level " + level + ": " + Engine.LevelName(level) + ".",
                Mood(S, now),
                (Engine.Threshold(level + 1) - total) + " more points to level " + (level + 1) + ", when " + G.BunnyName + " grows a little.",
                "Rename your bunny in Options, Settings, Bunny and goals.",
            });
            TextViewer.Show(_w, G.BunnyName, lines);
        }

        // ---- Challenges ----

        private void ShowChallenge()
        {
            Engine.RollChallenge(G, DateTime.Now);
            var w = Layout.Dialog(_w, "Daily challenge");
            var text = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, Width = 520, BorderThickness = new Thickness(0) };
            AutomationProperties.SetName(text, "Today's challenge");
            var accept = Layout.Button("_Accept", isDefault: true);
            var skip = Layout.Button("_Skip");
            var other = Layout.Button("Show a_nother");
            var close = Layout.Button("_Close", isCancel: true);

            void Refresh()
            {
                var c = Engine.ChallengeToday(G);
                string state = G.ChallengeState switch
                {
                    "Accepted" => "Accepted. " + G.ChallengeProgress + " of " + c.Target + " so far.",
                    "Done" => "Done. Nicely handled.",
                    "Skipped" => "Skipped. That costs nothing. Come back tomorrow for a new one.",
                    _ => "Optional. Skipping costs nothing.",
                };
                text.Text = c.Text + ". Worth " + c.Reward + " bonus points. " + state;
                bool offered = G.ChallengeState == "Offered";
                accept.IsEnabled = skip.IsEnabled = other.IsEnabled = offered;
            }
            accept.Click += (_, _) =>
            {
                S.Remember("accepting a challenge");
                G.ChallengeState = "Accepted";
                S.Save();
                _w.Say("Challenge accepted: " + Engine.ChallengeToday(G).Text + ".");
                w.Close();
            };
            skip.Click += (_, _) =>
            {
                S.Remember("skipping a challenge");
                G.ChallengeState = "Skipped";
                S.Save();
                _w.Say("Skipped. No cost.");
                w.Close();
            };
            other.Click += (_, _) =>
            {
                G.ChallengeId++;
                S.Save();
                Refresh();
                // Focus stays on this button, so the new challenge is only announced, not read twice.
                _w.Say(text.Text, true);
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in new[] { accept, skip, other, close }) buttons.Children.Add(b);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(text);
            panel.Children.Add(buttons);
            w.Content = panel;
            Refresh();
            w.Loaded += (_, _) => text.Focus();
            w.ShowDialog();
        }

        // ---- Semester Wrapped (spec 12.13) ----

        /// <summary>August to December is fall, January to May is spring, anything else is summer.</summary>
        public static (string Name, DateTime From, DateTime To) Semester(DateTime now) => now.Month switch
        {
            >= 8 => ("Fall " + now.Year, new DateTime(now.Year, 8, 1), new DateTime(now.Year, 12, 31)),
            <= 5 => ("Spring " + now.Year, new DateTime(now.Year, 1, 1), new DateTime(now.Year, 5, 31)),
            _ => ("Summer " + now.Year, new DateTime(now.Year, 6, 1), new DateTime(now.Year, 7, 31)),
        };

        public static List<string> Wrapped(Store s, DateTime now)
        {
            var d = s.Data;
            var (name, from, to) = Semester(now);
            var log = d.Log.Where(e => e.When.Date >= from && e.When.Date <= to).ToList();
            var done = log.Where(e => e.Kind == "Done").ToList();
            var lines = new List<string> { "Semester Wrapped: " + name + "." };
            if (log.Count == 0)
            {
                lines.Add("Nothing logged yet this semester. The recap will be here when there is something to recap.");
                return lines;
            }
            int points = log.Where(e => Engine.KarmaKinds.Contains(e.Kind)).Sum(e => e.Points);
            lines.Add("Tasks finished: " + done.Select(e => e.TaskId ?? Guid.NewGuid()).Distinct().Count() + ". Points earned: " + points + ".");
            var weeks = log.Where(e => Engine.KarmaKinds.Contains(e.Kind)).GroupBy(e => Engine.WeekStart(e.When)).Select(g => (Week: g.Key, Pts: g.Sum(e => e.Points))).OrderByDescending(x => x.Pts).FirstOrDefault();
            if (weeks.Pts > 0) lines.Add("Biggest week: " + weeks.Pts + " points, the week of " + weeks.Week.ToString("MMMM d") + ".");
            var (_, longest) = Engine.Streaks(d, now < to ? now : to, from);
            lines.Add("Longest streak: " + longest + (longest == 1 ? " day." : " days."));
            var tag = done.Where(e => e.Tag.Length > 0).GroupBy(e => e.Tag, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (tag != null) lines.Add("Most crushed tag: " + tag.Key + ", " + tag.Count() + " tasks.");
            var bosses = log.Where(e => e.Kind == "Boss").ToList();
            lines.Add("Bosses beaten: " + bosses.Count + (bosses.Count > 0 ? ". " + string.Join(", ", bosses.Select(b => b.Text).Where(t => t.Length > 0)) : "") + ".");
            int minutes = log.Sum(e => e.Minutes);
            if (minutes > 0) lines.Add("Focused time logged: " + minutes / 60 + " hours " + minutes % 60 + " minutes.");
            lines.Add("Badges earned this semester: " + d.Game.Badges.Count(b => b.Value.Date >= from && b.Value.Date <= to) + ".");
            int total = Engine.Total(d), level = Engine.LevelFor(total);
            lines.Add("You end it as level " + level + ", " + Engine.LevelName(level) + ". " + d.Game.BunnyName + " is proud and also hungry.");
            return lines;
        }

        private void ShowWrapped() => TextViewer.Show(_w, "Semester Wrapped", Wrapped(S, DateTime.Now));

        // ---- Settings page ----

        private sealed class FunPage : ISettingsPage
        {
            private readonly Store _store;
            private readonly TextBox _name = new(), _daily = new(), _weekly = new();
            private readonly StackPanel _panel = new() { Margin = new Thickness(8) };

            public FunPage(Store store)
            {
                _store = store;
                var g = store.Data.Game;
                _name.Text = g.BunnyName;
                _daily.Text = g.DailyGoal.ToString(CultureInfo.InvariantCulture);
                _weekly.Text = g.WeeklyGoal.ToString(CultureInfo.InvariantCulture);
                _panel.Children.Add(Layout.Row("Bunny's _name", _name));
                _panel.Children.Add(Layout.Row("_Daily goal in points, 0 to work it out from your hours", _daily, 120));
                _panel.Children.Add(Layout.Row("_Weekly goal in points, 0 for five usual days", _weekly, 120));
            }

            public string Name => "Bunny and goals";
            public FrameworkElement Content => _panel;

            public string? Validate()
            {
                if (!int.TryParse(_daily.Text.Trim(), out int a) || a < 0) return "The daily goal must be a number, 0 or more.";
                if (!int.TryParse(_weekly.Text.Trim(), out int b) || b < 0) return "The weekly goal must be a number, 0 or more.";
                return null;
            }

            public void Apply()
            {
                var g = _store.Data.Game;
                g.BunnyName = _name.Text.Trim().Length > 0 ? _name.Text.Trim() : "Bun";
                g.DailyGoal = int.Parse(_daily.Text.Trim());
                g.WeeklyGoal = int.Parse(_weekly.Text.Trim());
            }
        }

        // ---- Self-test ----

        private static void Check(bool ok, string what) { if (!ok) throw new Exception("Game: " + what); }

        public void SelfTest(Store store)
        {
            var d = store.Data;
            d.Log.Clear();
            d.Game = new GameState();
            var hw = d.Lists.First(l => l.Name == "Homework");
            var top = new TaskItem { Name = "midterm review", List = "Homework", Tag = "PSY", Priority = 1, Size = Size.Medium };
            var low = new TaskItem { Name = "x", List = "Homework", Priority = hw.Scale.Length, Size = Size.Medium };
            Check(Engine.PointsFor(top, hw) > Engine.PointsFor(low, hw), "higher priority earns more");
            Check(Engine.PointsFor(top, hw, half: true) * 2 <= Engine.PointsFor(top, hw), "done not submitted earns half");
            Check(Engine.LevelFor(0) == 1 && Engine.LevelFor(39) == 1 && Engine.LevelFor(40) == 2, "level curve");
            Check(Engine.LevelName(30).Length > 0, "level names go on");
            Check(Badges.All.Count >= 20 && Badges.All.Select(b => b.Id).Distinct().Count() == Badges.All.Count, "badges");
            Check(Semester(new DateTime(2026, 10, 12)).Name == "Fall 2026" && Semester(new DateTime(2026, 2, 1)).Name == "Spring 2026" && Semester(new DateTime(2026, 7, 1)).Name == "Summer 2026", "semesters");

            var now = new DateTime(2026, 10, 14, 9, 0, 0);
            top.CreatedAt = now.AddDays(-20);
            top.Status = Status.Done;
            top.DoneAt = now;
            d.Tasks.Add(top);
            var e = new LogEntry { Kind = "Done", Text = top.Name, Tag = top.Tag, List = top.List, Points = Engine.PointsFor(top, hw), TaskId = top.Id };
            var o = Engine.Record(store, now, e, top, true);
            Check(Engine.Total(d) == e.Points, "total counts the entry");
            Check(d.Game.Badges.ContainsKey("first") && d.Game.Badges.ContainsKey("midterms") && d.Game.Badges.ContainsKey("slayer"), "first badges");
            Check(o.Said.Count > 0, "something is said");

            // Streak: goal met Oct 10, 11, 13 (12 is a vacation day), and today the 14th not yet.
            d.Game = new GameState { GoalDays = new List<string> { "2026-10-10", "2026-10-11", "2026-10-13" } };
            d.Day(new DateTime(2026, 10, 12)).Mode = DayMode.Vacation;
            Check(Engine.Streaks(d, now).Current == 3, "vacation day does not break the streak");
            d.Day(new DateTime(2026, 10, 12)).Mode = DayMode.Normal;
            Check(Engine.Streaks(d, now).Current == 1, "an ordinary missed day breaks it");

            // Comeback: points on the 8th, nothing on the 13th, then a task on the 14th.
            d.Log.Clear();
            d.Game = new GameState();
            d.Log.Add(new LogEntry { Kind = "Done", When = new DateTime(2026, 10, 8, 10, 0, 0), Points = 10, TaskId = Guid.NewGuid() });
            Check(Engine.MissedADay(d, now), "missed day seen");
            var o2 = Engine.Record(store, now, new LogEntry { Kind = "Done", Text = "t", Points = 5, TaskId = Guid.NewGuid() }, null, true);
            Check(d.Log.Any(x => x.Kind == "Comeback") && o2.Said.Any(x => x.Contains("Welcome back")), "comeback bonus");
            Check(Wrapped(store, now).Count > 2, "wrapped lines");

            // Done, undone, done again pays once, and undoing takes back everything finishing caused.
            d.Log.Clear();
            d.Game = new GameState();
            d.Play = new PlayState();
            Engine.RollChallenge(d.Game, now);
            d.Game.ChallengeId = Array.FindIndex(Engine.Challenges, c => c.Text.StartsWith("Finish three tasks"));
            d.Game.ChallengeState = "Accepted";
            d.Game.DailyGoal = 1000;
            var a = new TaskItem { Name = "stats homework", List = "Homework", Size = Size.Medium, Status = Status.Today, CreatedAt = now };
            d.Tasks.Add(a);
            int pays = Engine.PointsFor(a, hw);
            void Set(Status st) { var old = a.Status; a.Status = st; a.DoneAt = st == Status.Done ? now : null; Finish(store, a, old, now); }
            Set(Status.Done);
            Check(Engine.Total(d) == pays, "done pays");
            Set(Status.Today);
            Check(Engine.Total(d) == 0 && d.Game.ChallengeProgress == 0, "undone takes the points and the challenge step back");
            Set(Status.Done);
            Set(Status.Done);
            Check(Engine.Total(d) == pays, "done, undone, done again pays once");
            Set(Status.Today); Set(Status.Done); Set(Status.Today); Set(Status.Done);
            Check(d.Game.ChallengeProgress == 1 && d.Game.ChallengeState == "Accepted" && !d.Log.Any(x => x.Kind == "Challenge"), "toggling one task does not farm the challenge");
            Set(Status.DoneNotSubmitted);
            Check(Engine.Total(d) == Engine.PointsFor(a, hw, half: true), "done to done not submitted corrects to half");
            Set(Status.Done);
            Check(Engine.Total(d) == pays, "back to done pays the rest");

            // A bet on a task already done in time wins, even when it is settled after the deadline; undoing opens it again.
            d.Log.Clear();
            d.Log.Add(new LogEntry { Kind = "Done", When = now, Points = 100, TaskId = Guid.NewGuid() });
            d.Play.Spent = 20;
            d.Play.Bets.Add(new Bet { TaskId = a.Id, Name = a.Name, Stake = 20, By = now.AddHours(1) });
            a.Status = Status.Done; a.DoneAt = now;
            var (won, _, lost) = PlayTime.SettleBets(store, now.AddHours(2));
            Check(won.Count == 1 && lost.Count == 0 && d.Log.Any(x => x.TaskId == a.Id && x.Points == 40), "a bet on a task done in time wins");
            Engine.Unfinish(store, a, now);
            Check(d.Play.Bets[0].State == "Open" && !d.Log.Any(x => x.TaskId == a.Id), "undoing reopens the bet and takes the winnings back");
            // Won in time, deadline passed: un-finishing by mistake keeps the bet and its winnings.
            var late = new Bet { TaskId = a.Id, Name = a.Name, Stake = 10, By = now.AddHours(-1), State = "Won", FinishedAt = now.AddHours(-2) };
            d.Play.Bets.Add(late);
            d.Log.Add(new LogEntry { Kind = "Bonus", When = now, Text = "Won the bet on " + a.Name, Points = 20, TaskId = a.Id });
            Engine.Unfinish(store, a, now);
            Check(late.State == "Won" && d.Log.Any(x => x.TaskId == a.Id && x.Points == 20), "a bet won before its deadline stays won after un-finishing");
            d.Play.Bets.Remove(late);
            d.Log.RemoveAll(x => x.TaskId == a.Id);

            // Level up is said once, however often a task near the boundary is toggled.
            var keepLog = d.Log.ToList();
            d.Log.Clear();
            d.Game = new GameState { DailyGoal = 1000 };
            d.Log.Add(new LogEntry { Kind = "Done", When = now, Points = Engine.Threshold(2) - 1, TaskId = Guid.NewGuid() });
            a.Status = Status.Done;
            int ups = 0;
            for (int i = 0; i < 2; i++)
            {
                var oo = Finish(store, a, Status.Today, now);
                ups += oo?.Fun.Count(f => f.Text.StartsWith("Level up")) ?? 0;
                a.Status = Status.Today; Finish(store, a, Status.Done, now); a.Status = Status.Done;
            }
            Check(ups == 1, "level up said once, not on every toggle");
            d.Tasks.Remove(a);
            d.Log.Clear();
            d.Log.AddRange(keepLog);
            var (_, back, _) = PlayTime.SettleBets(store, now.AddHours(2));
            Check(back.Count == 1 && d.Play.Spent == 0 && d.Play.Bets[0].State == "Cancelled", "a bet on a deleted task is refunded");

            // A boss bonus logged first does not cost the comeback, and an early bonus counts toward the goal.
            d.Log.Clear();
            d.Game = new GameState { DailyGoal = pays + 23 };
            d.Log.Add(new LogEntry { Kind = "Done", When = now.AddDays(-6), Points = 10, TaskId = Guid.NewGuid() });
            d.Log.Add(new LogEntry { Kind = "Bonus", When = now, Points = 10, Text = "Boss defeated", TaskId = a.Id });
            var o3 = Engine.Record(store, now, new LogEntry { Kind = "Done", Points = pays, TaskId = a.Id }, a, true,
                new LogEntry { Kind = "Bonus", Text = "Finished early", Points = 3 });
            Check(d.Log.Any(x => x.Kind == "Comeback"), "a boss first still gets the comeback");
            Check(d.Game.GoalDays.Contains(now.ToString("yyyy-MM-dd")), "the early bonus counts toward the daily goal");
            a.Status = Status.Done;
            d.Tasks.Add(a);
            Engine.Unfinish(store, a, now);
            Check(!d.Game.GoalDays.Contains(now.ToString("yyyy-MM-dd")) && !d.Log.Any(x => x.TaskId == a.Id), "undoing drops the goal day and every entry");
            d.Tasks.Remove(a);

            // One line per finish: the level up wins over the rest.
            d.Log.Clear();
            Game.Flush(null, store);
            Game.Queue(null!, new Outcome { Gained = 5 });
            Game.Queue(null!, Game.Chatter, "cheer");
            Game.Queue(null!, Game.LevelUp, "Level up!");
            string said = Game.Flush(null, store);
            Check(said.Contains("Level up!") && !said.Contains("cheer") && d.Game.Happened.Any(x => x.EndsWith("cheer")), "one fun line, chosen by priority");
            d.Log.Clear();
            d.Game = new GameState();
            d.Play = new PlayState();
        }
    }
}
