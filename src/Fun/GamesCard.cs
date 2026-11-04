using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// Games drawer, part one (sweep 8.4): weekly bingo, speed runs, the combo meter, the golden task and Challenge
    /// Bunny. Everything a finish adds is keyed by the task's id and taken back when it is un-finished; every point goes
    /// through the ledger with the task's id, so un-finishing takes it back too.
    /// </summary>
    public sealed class GamesCard : IFeature
    {
        public const string BingoId = "games.bingo", RunId = "games.speedrun", ComboId = "games.combo", GoldenId = "games.golden", ChallengeId = "games.challengebunny";
        public const int BingoPrize = 15, RunPrize = 5, ChallengePrize = 15, ComboStepsMax = 5;
        public static readonly TimeSpan ComboGap = TimeSpan.FromMinutes(20);
        private MainWindow _w = null!;
        private Store S => _w.Store;

        // ---- State ----
        public sealed class Square { public string Text { get; set; } = ""; public Guid? Task { get; set; } public string Rule { get; set; } = ""; public bool Free { get; set; } public Guid? DoneBy { get; set; } }
        public sealed class Card { public string Week { get; set; } = ""; public List<Square> Squares { get; set; } = new(); public List<int> Lines { get; set; } = new(); }
        public sealed class Beat { public string Key { get; set; } = ""; public double? Old { get; set; } }
        public sealed class Runs
        {
            public Dictionary<string, double> Best { get; set; } = new();
            public Dictionary<Guid, DateTime> Clock { get; set; } = new();
            public Dictionary<Guid, Beat> Beaten { get; set; } = new();
        }
        public sealed class Hit { public Guid Task { get; set; } public DateTime When { get; set; } public int Step { get; set; } }
        public sealed class Combo { public List<Hit> Hits { get; set; } = new(); }
        public sealed class Golden { public string Day { get; set; } = ""; public Guid? Task { get; set; } public bool Found { get; set; } }
        public sealed class Challenge { public string Day { get; set; } = ""; public int Target { get; set; } public bool Accepted { get; set; } public bool Asked { get; set; } public Guid? WonBy { get; set; } }

        public void Attach(MainWindow w)
        {
            _w = w;
            Tools.Register(new BunnyTool(BingoId, "Weekly bingo", Drawers.Games, "A 5 by 5 card of your tasks and small challenges. Five in a row pays " + BingoPrize + " points.", ShowBingo) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(RunId, "Speed runs", Drawers.Games, "Best times on repeating tasks. Start the clock, finish, and try to beat it.", ShowRuns) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(ComboId, "Combo meter", Drawers.Games, "Tasks finished within 20 minutes of each other add 10 percent each, up to 50.", ShowCombo) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(GoldenId, "Golden task", Drawers.Games, "One task each morning is secretly golden and pays triple. Hear a hint.", ShowGolden) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(ChallengeId, "Challenge Bunny", Drawers.Games, "Bunny names how many tasks you can finish today. Beat it for " + ChallengePrize + " points.", ShowChallenge) { Since = "0.14.0" });
            BunnyTab.Register("Weekly bingo", "this week's card and how close you are", ShowBingo, BingoId);

            GameEvents.DayStarted += day => { if (Tools.IsOn(GoldenId)) RollGolden(S, DateTime.Now); };
            GameEvents.WeekStarted += _ => { if (Tools.IsOn(BingoId)) CardFor(S, DateTime.Now); };
            GameEvents.TaskStarted += t => { if (Tools.IsOn(RunId)) StartClock(S, t, DateTime.Now, onlyIfNew: true); };
            GameEvents.TaskDone += OnDone;
            // Always, even with a tool off or Calm mode on: what a finish gave must go back.
            GameEvents.TaskUndone += t => Undone(S, t, DateTime.Now);
        }

        private void OnDone(TaskItem t)
        {
            var now = DateTime.Now;
            var said = Done(S, t, now);
            S.Save();
            foreach (var (o, priority, line) in said)
            {
                if (o != null) Game.Queue(_w, o);
                Calm.AfterTaskDone(_w, priority, line);
            }
        }

        /// <summary>Everything a finish does here, for the event and the self-test: payments and lines to offer.</summary>
        public static List<(Outcome? O, int Priority, string Line)> Done(Store s, TaskItem t, DateTime now)
        {
            var said = new List<(Outcome?, int, string)>();
            void Try(Action a) { try { a(); } catch (Exception e) { ErrorLog.Write("Games: " + e.GetType().Name + ": " + e.Message); } }
            int basePts = FunKit.BasePoints(s, t);

            // Golden task and combo: one multiplier together, with the ledger's, never past 4 times.
            double golden = 1, combo = 1;
            int step = 0;
            Try(() =>
            {
                if (!Tools.IsOn(GoldenId)) return;
                var g = RollGolden(s, now);
                if (g.Task == t.Id && !g.Found) { g.Found = true; FunKit.Put(s, GoldenId, g); golden = 3; }
            });
            Try(() =>
            {
                if (!Tools.IsOn(ComboId)) return;
                step = AddHit(s, t, now);
                combo = 1 + 0.1 * step;
            });
            int goldExtra = FunKit.Extra(s, basePts, golden, now);
            int comboExtra = FunKit.Extra(s, basePts, golden * combo, now) - goldExtra;
            if (golden > 1)
            {
                var o = Ledger.Pay(s, goldExtra, "Golden task: " + t.Name, GoldenId, t.Id);
                said.Add((o, Game.Goals, Celebration.Line(s, "Golden task! " + t.Name + " was the golden one: triple points, " + FunKit.Plural(goldExtra, "extra point") + ". " + Content.Pick(s, "games.golden"))));
                Celebration.Sound(s, "SurpriseReward");
            }
            if (step > 0)
            {
                var o = Ledger.Pay(s, comboExtra, "Combo times " + (step + 1), ComboId, t.Id);
                said.Add((o, Game.Chatter, "Combo times " + (step + 1) + ": " + step * 10 + " percent extra" + (comboExtra > 0 ? ", " + FunKit.Plural(comboExtra, "point") : "") + ". " + Content.Pick(s, "games.combo")));
            }

            Try(() =>
            {
                if (!Tools.IsOn(BingoId)) return;
                foreach (var (o, line) in MarkBingo(s, t, now)) said.Add((o, Game.Goals, line));
            });
            Try(() =>
            {
                if (!Tools.IsOn(RunId)) return;
                if (EndClock(s, t, now) is (var o, int p, string line)) said.Add((o, p, line));
            });
            Try(() =>
            {
                if (!Tools.IsOn(ChallengeId)) return;
                var c = FunKit.Bag<Challenge>(s, ChallengeId);
                if (c.Day != s.Key(now) || !c.Accepted || c.WonBy != null) return;
                int done = FunKit.DoneOn(s, now).Count;
                if (done < c.Target) { said.Add((null, Game.Chatter, "Bunny's challenge: " + done + " of " + c.Target + ".")); return; }
                c.WonBy = t.Id;
                FunKit.Put(s, ChallengeId, c);
                var o = Ledger.Pay(s, ChallengePrize, "Beat Bunny's challenge", ChallengeId, t.Id);
                said.Add((o, Game.Goals, Celebration.Line(s, "You beat Bunny's challenge: " + c.Target + " tasks today, " + ChallengePrize + " points. " + Content.Pick(s, "games.challenge.won"))));
                Celebration.Sound(s, "SurpriseReward");
            });
            return said;
        }

        /// <summary>Takes back what a finish gave here. Its ledger entries already went with Engine.Unfinish.</summary>
        public static void Undone(Store s, TaskItem t, DateTime now)
        {
            var g = s.World.Get<Golden>(GoldenId);
            if (g.Task == t.Id && g.Found) { g.Found = false; s.World.Set(GoldenId, g); }
            var c = s.World.Get<Combo>(ComboId);
            if (c.Hits.RemoveAll(h => h.Task == t.Id) > 0) s.World.Set(ComboId, c);
            UnmarkBingo(s, t, now);
            var r = s.World.Get<Runs>(RunId);
            if (r.Beaten.Remove(t.Id, out var b))
            {
                if (b.Old is double old) r.Best[b.Key] = old; else r.Best.Remove(b.Key);
                s.World.Set(RunId, r);
            }
            var ch = s.World.Get<Challenge>(ChallengeId);
            if (ch.WonBy == t.Id) { ch.WonBy = null; s.World.Set(ChallengeId, ch); }
            s.World.Save();
        }

        // ---- Combo meter ----

        /// <summary>Adds a finish to the combo and returns its step: 0 for a first task, one more for each within 20 minutes of the last, at most 5.</summary>
        public static int AddHit(Store s, TaskItem t, DateTime now)
        {
            var c = s.World.Get<Combo>(ComboId);
            c.Hits.RemoveAll(h => h.When < now.AddDays(-1) || h.Task == t.Id);
            var last = c.Hits.OrderBy(h => h.When).LastOrDefault();
            int step = last != null && now - last.When <= ComboGap ? Math.Min(ComboStepsMax, last.Step + 1) : 0;
            c.Hits.Add(new Hit { Task = t.Id, When = now, Step = step });
            FunKit.Put(s, ComboId, c);
            return step;
        }

        private void ShowCombo()
        {
            var last = S.World.Get<Combo>(ComboId).Hits.OrderBy(h => h.When).LastOrDefault();
            var now = DateTime.Now;
            if (last == null || now - last.When > ComboGap) { _w.Say("No combo going. Finish two tasks within 20 minutes of each other to start one. Each step adds 10 percent, up to 50.", true); return; }
            int left = (int)Math.Ceiling((ComboGap - (now - last.When)).TotalMinutes);
            _w.Say("Combo times " + (last.Step + 1) + ". Finish another within " + FunKit.Plural(left, "minute") + " for " + Math.Min(50, (last.Step + 1) * 10) + " percent extra.", true);
        }

        // ---- Golden task ----

        /// <summary>Today's golden task, picked once a day from tasks that are not urgent: not overdue and not due today.</summary>
        public static Golden RollGolden(Store s, DateTime now)
        {
            var g = s.World.Get<Golden>(GoldenId);
            if (g.Day == s.Key(now)) return g;
            var pool = FunKit.Live(s).Where(t => !FunKit.Finished(t) && t.Status != Status.Waiting && !t.IsMeeting && !Store.IsOverdue(t, now) && t.Due?.Date != now.Date).ToList();
            var preferred = pool.Where(t => t.Status is Status.Today or Status.ThisWeek).ToList();
            if (preferred.Count > 0) pool = preferred;
            g = new Golden { Day = s.Key(now), Task = pool.Count == 0 ? null : pool[FunKit.Rng.Next(pool.Count)].Id };
            FunKit.Put(s, GoldenId, g);
            return g;
        }

        private void ShowGolden()
        {
            var g = RollGolden(S, DateTime.Now);
            var t = g.Task is Guid id ? S.Data.Tasks.FirstOrDefault(x => x.Id == id) : null;
            if (t == null) { _w.Say("No golden task today: nothing was free to hide it in. Tomorrow morning brings a new one.", true); return; }
            if (g.Found) { _w.Say("You found today's golden task: " + t.Name + ". Triple points, already paid.", true); return; }
            _w.Say("Today's golden task is still hidden. Hint: it is on " + t.List + (t.Tag.Length > 0 ? ", tagged " + t.Tag : "") + ", and its size is " + Words.Of(t.Size).ToLowerInvariant() + ".", true);
        }

        // ---- Weekly bingo ----

        private static readonly Func<TaskItem, DateTime, Store, bool> Never = (_, _, _) => false;

        /// <summary>The challenge squares' rules, by the rule names in games.bingo.txt.</summary>
        public static readonly Dictionary<string, Func<TaskItem, DateTime, Store, bool>> Rules = new()
        {
            ["small"] = (t, n, s) => t.Size == Size.Small,
            ["medium"] = (t, n, s) => t.Size == Size.Medium,
            ["large"] = (t, n, s) => t.Size is Size.Large or Size.MultiDay,
            ["beforenine"] = (t, n, s) => n.Hour < 9,
            ["morning"] = (t, n, s) => n.Hour < 12,
            ["afternoon"] = (t, n, s) => n.Hour >= 12 && n.Hour < 17,
            ["evening"] = (t, n, s) => n.Hour >= 17,
            ["weekend"] = (t, n, s) => n.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            ["monday"] = (t, n, s) => n.DayOfWeek == DayOfWeek.Monday,
            ["friday"] = (t, n, s) => n.DayOfWeek == DayOfWeek.Friday,
            ["top"] = (t, n, s) => t.Priority == 1 && s.ListOf(t).Scale.Length > 0,
            ["early"] = (t, n, s) => t.Due is DateTime d && d.Date > n.Date,
            ["old"] = (t, n, s) => t.CreatedAt <= n.AddDays(-7),
            ["notes"] = (t, n, s) => t.Notes.Trim().Length > 0,
            ["subtasks"] = (t, n, s) => t.Subtasks.Count > 0,
            ["tagged"] = (t, n, s) => t.Tag.Length > 0,
            ["repeat"] = (t, n, s) => t.Repeat.Length > 0,
            ["quickwin"] = (t, n, s) => t.QuickWin || t.Size == Size.Small,
            ["duetoday"] = (t, n, s) => t.Due?.Date == n.Date,
            ["first"] = (t, n, s) => FunKit.DoneOn(s, n).Count == 1,
            ["third"] = (t, n, s) => FunKit.DoneOn(s, n).Count >= 3,
            ["any"] = (t, n, s) => true,
        };

        public static string SquareName(int i) => "BINGO"[i % 5] + (i / 5 + 1).ToString();

        /// <summary>The 12 lines of a card: 5 rows, 5 columns and 2 diagonals, as square numbers, with a name each.</summary>
        public static readonly (string Name, int[] Squares)[] BingoLines = Enumerable.Range(0, 5).Select(r => ("row " + (r + 1), Enumerable.Range(0, 5).Select(c => r * 5 + c).ToArray()))
            .Concat(Enumerable.Range(0, 5).Select(c => ("the " + "BINGO"[c] + " column", Enumerable.Range(0, 5).Select(r => r * 5 + c).ToArray())))
            .Append(("the diagonal from B1", new[] { 0, 6, 12, 18, 24 })).Append(("the diagonal from O1", new[] { 4, 8, 12, 16, 20 })).ToArray();

        /// <summary>This week's card, made the first time it is needed in a week: her open tasks (a large one fills two squares), then challenges.</summary>
        public static Card CardFor(Store s, DateTime now)
        {
            var c = s.World.Get<Card>(BingoId);
            string week = FunKit.Key(Engine.WeekStart(now));
            if (c.Week == week && c.Squares.Count == 25) return c;
            var squares = new List<Square>();
            var open = FunKit.Live(s).Where(t => !FunKit.Finished(t) && !t.IsMeeting && t.Status != Status.Waiting)
                .OrderBy(t => t.Due ?? t.DoDate ?? DateTime.MaxValue).ThenBy(t => t.Priority == 0 ? 99 : t.Priority).ToList();
            foreach (var t in open)
            {
                int fill = t.Size is Size.Large or Size.MultiDay ? 2 : 1;
                if (squares.Count + fill > 12) break;
                for (int k = 0; k < fill; k++) squares.Add(new Square { Text = t.Name + (fill == 2 ? (k == 0 ? ", first square" : ", second square") : ""), Task = t.Id });
            }
            var challenges = Content.Items("games.bingo").Where(i => i.ContainsKey("rule") && i.ContainsKey("text") && Rules.ContainsKey(i["rule"])).OrderBy(_ => FunKit.Rng.Next()).ToList();
            for (int k = 0; squares.Count < 24 && challenges.Count > 0; k++)
            {
                var ch = challenges[k % challenges.Count];
                squares.Add(new Square { Text = ch["text"], Rule = ch["rule"] });
            }
            while (squares.Count < 24) squares.Add(new Square { Text = "Finish any task", Rule = "any" });
            squares = squares.OrderBy(_ => FunKit.Rng.Next()).ToList();
            squares.Insert(12, new Square { Text = "Free square", Free = true });
            c = new Card { Week = week, Squares = squares };
            FunKit.Put(s, BingoId, c);
            return c;
        }

        private static bool Filled(Square q) => q.Free || q.DoneBy != null;

        /// <summary>Marks squares for a finish: the task's own squares (both, for a large task), or else the first challenge it meets. Pays each new line.</summary>
        public static List<(Outcome? O, string Line)> MarkBingo(Store s, TaskItem t, DateTime now)
        {
            var said = new List<(Outcome?, string)>();
            var c = CardFor(s, now);
            var mine = c.Squares.Where(q => q.Task == t.Id && q.DoneBy == null).ToList();
            if (mine.Count == 0 && !c.Squares.Any(q => q.DoneBy == t.Id))
            {
                var ch = c.Squares.FirstOrDefault(q => !q.Free && q.Task == null && q.DoneBy == null && (Rules.GetValueOrDefault(q.Rule) ?? Never)(t, now, s));
                if (ch != null) mine.Add(ch);
            }
            if (mine.Count == 0) return said;
            foreach (var q in mine) q.DoneBy = t.Id;
            string names = string.Join(" and ", mine.Select(q => SquareName(c.Squares.IndexOf(q))));
            int before = said.Count;
            for (int i = 0; i < BingoLines.Length; i++)
            {
                if (c.Lines.Contains(i) || !BingoLines[i].Squares.All(k => Filled(c.Squares[k]))) continue;
                c.Lines.Add(i);
                var o = Ledger.Pay(s, BingoPrize, BingoReason(i), BingoId, t.Id);
                said.Add((o, Celebration.Line(s, "Bingo! " + names + " finished " + BingoLines[i].Name + ". " + BingoPrize + " points.")));
                Celebration.Sound(s, "PersonalRecord");
            }
            if (said.Count == before) said.Add((null, "Bingo square " + names + " filled. " + FunKit.Plural(c.Squares.Count(Filled), "square") + " of 25."));
            FunKit.Put(s, BingoId, c);
            return said;
        }

        private static string BingoReason(int line) => "Bingo, " + BingoLines[line].Name;

        public static void UnmarkBingo(Store s, TaskItem t, DateTime now)
        {
            var c = s.World.Get<Card>(BingoId);
            if (!c.Squares.Any(q => q.DoneBy == t.Id)) return;
            foreach (var q in c.Squares.Where(q => q.DoneBy == t.Id)) q.DoneBy = null;
            var days = new HashSet<DateTime>();
            foreach (int i in c.Lines.ToList())
            {
                if (BingoLines[i].Squares.All(k => Filled(c.Squares[k]))) continue;
                c.Lines.Remove(i);
                // A line another task finished is no longer complete: its prize goes too (this task's own went with it).
                var start = FunKit.Day(c.Week) ?? Engine.WeekStart(now);
                foreach (var e in s.Data.Log.Where(e => e.Feature == BingoId && e.Text == BingoReason(i) && e.When >= start).ToList()) { days.Add(e.When.Date); s.Data.Log.Remove(e); }
            }
            if (days.Count > 0) Engine.RecheckGoals(s, days, now);
            s.World.Set(BingoId, c);
        }

        private void ShowBingo()
        {
            var c = CardFor(S, DateTime.Now);
            var lines = new List<string>
            {
                "This week's bingo: " + FunKit.Plural(c.Squares.Count(Filled), "square") + " of 25 filled, " + FunKit.Plural(c.Lines.Count, "line") + " done. Five in a row pays " + BingoPrize + " points. Columns are B I N G O, rows 1 to 5.",
            };
            for (int i = 0; i < 25; i++)
            {
                var q = c.Squares[i];
                lines.Add(SquareName(i) + ": " + q.Text + (q.Free ? ", always filled" : q.DoneBy != null ? ", done" : ", not yet"));
            }
            FunKit.Read(_w, "Weekly bingo", lines);
        }

        // ---- Speed runs ----

        public static string RunKey(TaskItem t) => (t.List + "|" + t.Name).Trim().ToLowerInvariant();

        public static void StartClock(Store s, TaskItem t, DateTime now, bool onlyIfNew = false)
        {
            if (t.Repeat.Length == 0) return;
            var r = s.World.Get<Runs>(RunId);
            if (onlyIfNew && r.Clock.ContainsKey(t.Id)) return;
            r.Clock[t.Id] = now;
            FunKit.Put(s, RunId, r);
        }

        /// <summary>Stops the clock for a finished repeating task: a first time, or a new best (5 points and a fanfare). Null when no clock ran.</summary>
        public static (Outcome?, int, string)? EndClock(Store s, TaskItem t, DateTime now)
        {
            var r = s.World.Get<Runs>(RunId);
            if (!r.Clock.Remove(t.Id, out var start)) return null;
            double minutes = (now - start).TotalMinutes;
            FunKit.Put(s, RunId, r);
            if (minutes < 0.5 || minutes > 12 * 60) return null;
            string key = RunKey(t), time = Minutes(minutes);
            if (!r.Best.TryGetValue(key, out double best))
            {
                r.Best[key] = minutes;
                r.Beaten[t.Id] = new Beat { Key = key, Old = null };
                FunKit.Put(s, RunId, r);
                return (null, Game.Chatter, "Speed run: " + t.Name + " took " + time + ". That is the time to beat.");
            }
            if (minutes >= best) return (null, Game.Chatter, "Speed run: " + time + ". Your best is still " + Minutes(best) + ".");
            r.Best[key] = minutes;
            r.Beaten[t.Id] = new Beat { Key = key, Old = best };
            FunKit.Put(s, RunId, r);
            var o = Ledger.Pay(s, RunPrize, "Speed run record: " + t.Name, RunId, t.Id);
            Celebration.Sound(s, "PersonalRecord");
            return (o, Game.Record, Celebration.Line(s, "New speed run record! " + t.Name + " in " + time + ", beating " + Minutes(best) + ". " + Content.Pick(s, "games.speedrun")));
        }

        public static string Minutes(double m) => m < 1 ? "under a minute" : m < 60 ? FunKit.Plural((int)Math.Round(m), "minute") : (int)(m / 60) + " hours " + (int)Math.Round(m % 60) + " minutes";

        private void ShowRuns()
        {
            var r = S.World.Get<Runs>(RunId);
            var open = FunKit.Live(S).Where(t => t.Repeat.Length > 0 && !FunKit.Finished(t)).OrderBy(t => t.Name).ToList();
            var choices = open.Select(t => "Start the clock on " + t.Name + (r.Clock.ContainsKey(t.Id) ? ", clock running" : "") + (r.Best.TryGetValue(RunKey(t), out double b) ? ", best " + Minutes(b) : ", no best yet")).ToList();
            choices.Add("Hear every best time");
            string intro = open.Count == 0 ? "Speed runs are for repeating tasks, and you have none open right now." : "Pick a repeating task to start its clock. Finish it to stop the clock. Starting a sprint on one starts it too.";
            int i = FunKit.Pick(_w, "Speed runs", "_Speed runs", choices, 0, intro);
            if (i < 0) return;
            if (i < open.Count)
            {
                StartClock(S, open[i], DateTime.Now);
                _w.Say("Clock started on " + open[i].Name + ". No rush. It stops when you finish.", true);
                return;
            }
            var lines = r.Best.OrderBy(k => k.Key).Select(k => k.Key[(k.Key.IndexOf('|') + 1)..] + ": " + Minutes(k.Value)).ToList();
            FunKit.Read(_w, "Speed run best times", lines.Count == 0 ? new List<string> { "No best times yet." } : lines);
        }

        // ---- Challenge Bunny ----

        /// <summary>What Bunny thinks she can do today: her usual finished tasks a day over the last two weeks, plus one, from 2 to 12.</summary>
        public static int Usual(Store s, DateTime now)
        {
            var days = Enumerable.Range(1, 14).Select(k => now.Date.AddDays(-k)).Select(d => FunKit.DoneOn(s, d).Count).Where(n => n > 0).ToList();
            double avg = days.Count == 0 ? 2 : days.Average();
            return Math.Clamp((int)Math.Round(avg) + 1, 2, 12);
        }

        private void ShowChallenge()
        {
            var now = DateTime.Now;
            var c = FunKit.Bag<Challenge>(S, ChallengeId);
            int done = FunKit.DoneOn(S, now).Count;
            if (c.Day == S.Key(now) && c.Accepted)
            {
                _w.Say(c.WonBy != null ? "You already beat Bunny's challenge today: " + c.Target + " tasks. Bunny is filing a complaint." : "Bunny's challenge: " + done + " of " + c.Target + " tasks today.", true);
                return;
            }
            if (c.Day != S.Key(now)) c = new Challenge { Day = S.Key(now), Target = Math.Max(Usual(S, now), done + 1) };
            string offer = Content.Pick(S, "games.challenge").Replace("{n}", c.Target.ToString());
            int i = FunKit.Pick(_w, "Challenge Bunny", "_Your answer", new[] { "Challenge accepted", "Not today" }, 0, offer + " You have finished " + done + " so far. The prize is " + ChallengePrize + " points.");
            if (i != 0) { _w.Say("No challenge today. Bunny pretends not to be relieved.", true); return; }
            c.Accepted = true;
            c.Asked = true;
            FunKit.Put(S, ChallengeId, c);
            _w.Say("Challenge on: " + c.Target + " tasks today. Bunny is watching, with snacks.", true);
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("games: " + what); }
            foreach (var topic in new[] { "games.golden", "games.combo", "games.speedrun", "games.challenge", "games.challenge.won" })
                Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            var rules = Content.Items("games.bingo");
            Need(rules.Count >= 20 && rules.All(i => i.ContainsKey("rule") && Rules.ContainsKey(i["rule"])), "a bingo square has no rule the game knows");
            var now = new DateTime(2026, 11, 4, 10, 0, 0);
            var list = store.Data.Lists[0];
            TaskItem Make(string name, Size size = Size.Small)
            {
                var t = new TaskItem { Name = name, List = list.Name, Size = size, Status = Status.Today };
                store.Data.Tasks.Add(t);
                return t;
            }
            void Finish(TaskItem t, DateTime when)
            {
                t.Status = Status.Done; t.DoneAt = when;
                Engine.Record(store, when, new LogEntry { Kind = "Done", Text = t.Name, Points = Engine.PointsFor(t, list), TaskId = t.Id }, t, true);
            }
            void Unfinish(TaskItem t) { t.Status = Status.Today; t.DoneAt = null; Engine.Unfinish(store, t, now); Undone(store, t, now); }

            // Combo: steps of 10 percent, capped at 5 steps (50 percent), broken by a gap over 20 minutes.
            var a = Make("combo a");
            Need(AddHit(store, a, now) == 0, "a first task started a combo");
            int last = 0;
            for (int k = 1; k <= 7; k++) last = AddHit(store, Make("combo " + k), now.AddMinutes(10 * k));
            Need(last == ComboStepsMax, "the combo was not capped at 50 percent: " + last);
            Need(AddHit(store, Make("late"), now.AddHours(3)) == 0, "a long gap kept the combo going");
            // The ledger cap: a golden task under a running 2 times multiplier pays at most 4 times in all.
            Need(FunKit.Extra(store, 10, 3, now) == 20, "golden triple of 10 should add 20");
            Ledger.AddMultiplier(store, "test.games", 2, DateTime.Now.AddHours(1), "test");
            Need(FunKit.Extra(store, 10, 3, DateTime.Now) == 20 && FunKit.Extra(store, 10, 3 * 1.5, DateTime.Now) == 20, "golden plus combo went past the 4 times cap");
            Ledger.RemoveMultiplier(store, "test.games");

            // Golden: pays triple once, and un-finishing takes it back.
            store.World.Set(ComboId, new Combo());
            var g = Make("golden one");
            store.World.Set(GoldenId, new Golden { Day = store.Key(DateTime.Now), Task = g.Id });
            int before = Engine.Total(store.Data);
            Finish(g, DateTime.Now);
            int paid = Engine.Total(store.Data) - before, gBase = FunKit.BasePoints(store, g);
            Done(store, g, DateTime.Now);
            Need(Engine.Total(store.Data) - before == paid + 2 * gBase && store.World.Get<Golden>(GoldenId).Found, "the golden task did not pay triple");
            Unfinish(g);
            Need(Engine.Total(store.Data) == before && !store.World.Get<Golden>(GoldenId).Found, "un-finishing the golden task kept its points");
            Finish(g, DateTime.Now);
            paid = Engine.Total(store.Data) - before;
            Done(store, g, DateTime.Now);
            Need(Engine.Total(store.Data) - before == paid + 2 * gBase, "done, back, done again paid the golden task twice");
            Unfinish(g);

            // Bingo: a large task fills two squares; a full row pays once; un-finishing breaks it and takes the prize back.
            store.World.Set(BingoId, new Card());
            var big = Make("big bingo task", Size.Large);
            var allTasks = store.Data.Tasks;
            store.Data.Tasks = new List<TaskItem> { big };
            var card = CardFor(store, DateTime.Now);
            store.Data.Tasks = allTasks;
            Need(card.Squares.Count == 25 && card.Squares[12].Free, "the card is not 5 by 5 with a free center");
            Need(card.Squares.Count(q => q.Task == big.Id) == 2, "a large task did not get two squares");
            Need(SquareName(0) == "B1" && SquareName(12) == "N3" && SquareName(24) == "O5", "square names");
            // The large task's two squares at B1 and I1, the rest of row 1 already filled.
            foreach (var q in card.Squares) { if (q.Task == big.Id) { q.Task = null; q.Rule = "medium"; } q.DoneBy = null; }
            card.Squares[0] = new Square { Text = "big, first square", Task = big.Id };
            card.Squares[1] = new Square { Text = "big, second square", Task = big.Id };
            var row = card.Squares.Skip(2).Take(3).ToList();
            var helper = Guid.NewGuid();
            foreach (var q in row) q.DoneBy = helper;
            store.World.Set(BingoId, card);
            before = Engine.Total(store.Data);
            Finish(big, DateTime.Now);
            int bigPaid = Engine.Total(store.Data) - before;
            var marks = MarkBingo(store, big, DateTime.Now);
            Need(store.World.Get<Card>(BingoId).Squares.Count(q => q.DoneBy == big.Id) == 2, "finishing a large task did not fill both squares");
            Need(Engine.Total(store.Data) - before == bigPaid + BingoPrize && marks.Count == 1, "a full row did not pay " + BingoPrize + " once");
            Unfinish(big);
            Need(Engine.Total(store.Data) == before && store.World.Get<Card>(BingoId).Lines.Count == 0, "un-finishing kept the bingo line");

            // Speed runs: a first time, a slower time, then a record; un-finishing the record puts the old best back.
            var run1 = Make("laundry"); run1.Repeat = "every week";
            StartClock(store, run1, now); Finish(run1, now.AddMinutes(30));
            Need(EndClock(store, run1, now.AddMinutes(30)) != null && store.World.Get<Runs>(RunId).Best[RunKey(run1)] == 30, "a first speed run time");
            var run2 = Make("laundry"); run2.Repeat = "every week";
            StartClock(store, run2, now); Finish(run2, now.AddMinutes(20));
            before = Engine.Total(store.Data);
            var rec = EndClock(store, run2, now.AddMinutes(20));
            Need(rec?.Item2 == Game.Record && Engine.Total(store.Data) == before + RunPrize, "beating the best time");
            Unfinish(run2);
            Need(store.World.Get<Runs>(RunId).Best[RunKey(run1)] == 30, "un-finishing a record kept the new best");

            // Challenge Bunny: her usual plus one, at least 2.
            Need(Usual(store, now) >= 2 && Usual(store, now) <= 12, "Bunny's challenge target");
            foreach (var t in store.Data.Tasks.Where(t => t.Name.StartsWith("combo") || t.Name is "late" or "golden one" or "big bingo task" or "laundry").ToList()) store.Data.Tasks.Remove(t);
        }
    }
}
