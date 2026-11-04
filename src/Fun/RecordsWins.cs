using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// Wins and records drawer (sweep 8.4): the marble jar, share a win, racing your past self, the done list, best
    /// day replay, the personal bests board, the effort receipt, task of the week, the Friday wind-down, kindness
    /// points and boundary points. Trackers are heard on request and speak only at a record or a milestone.
    /// </summary>
    public sealed class RecordsWins : IFeature
    {
        public const string MarbleId = "records.marbles", ShareId = "records.share", RaceId = "records.race", DoneListId = "records.donelist",
            BestDayId = "records.bestday", BestsId = "records.bests", ReceiptId = "records.receipt", WeekTaskId = "records.taskweek",
            WindId = "records.winddown", KindId = "records.kindness", NoId = "records.boundary";
        public const string HelpedFeature = "records.helped", HelpedText = "Helped someone: ";
        public const int JarSize = 50, KindPoints = 10, NoPoints = 5, PaidADay = 3, AwardPoints = 5;
        private MainWindow _w = null!;
        private Store S => _w.Store;

        public sealed class FilledJar { public string Prize { get; set; } = ""; public List<Guid> Tasks { get; set; } = new(); public Guid By { get; set; } public DateTime When { get; set; } }
        public sealed class Jar { public List<Guid> Tasks { get; set; } = new(); public List<FilledJar> Filled { get; set; } = new(); }
        public sealed class Race { public string PassedDay { get; set; } = ""; public Guid? PassedBy { get; set; } }
        public sealed class Bests { public Dictionary<string, string> SaidDay { get; set; } = new(); public Dictionary<string, Guid> SaidBy { get; set; } = new(); public int LongestSprint { get; set; } }
        public sealed class Receipts { public string Day { get; set; } = ""; public List<string> Lines { get; set; } = new(); }
        public sealed class Award { public string Week { get; set; } = ""; public string Task { get; set; } = ""; public string Line { get; set; } = ""; }
        public sealed class Awards { public List<Award> All { get; set; } = new(); }
        public sealed class Recap { public string Week { get; set; } = ""; public string Text { get; set; } = ""; }
        public sealed class Kindness { public List<string> Flowers { get; set; } = new(); public string Day { get; set; } = ""; public int PaidToday { get; set; } public int Total { get; set; } }

        public void Attach(MainWindow w)
        {
            _w = w;
            void Tool(string id, string name, string what, Action run, string keys = "") => Tools.Register(new BunnyTool(id, name, Drawers.Wins, what, run) { Since = "0.14.0", Keys = keys });
            Tool(MarbleId, "The marble jar", "Every finished task drops a marble in. Fifty fills the jar and gives a prize.", ShowJar);
            Tool(ShareId, "Share a win", "Copies a short brag line about today, ready to paste to a friend.", Share, "Control Shift C");
            Tool(RaceId, "Race your past self", "Today's points so far against the same day last week at this time.", ShowRace);
            Tool(DoneListId, "Done list", "What you finished today and the two weeks before, including I did a thing. Also Bunny, What I finished.", ShowDoneList);
            Tool(BestDayId, "Best day replay", "Your best day ever: what you finished and how it went.", ShowBestDay);
            Tool(BestsId, "Personal bests board", "Your records: longest sprint, most before noon, biggest day and more.", ShowBests);
            Tool(ReceiptId, "Effort receipt", "A silly receipt for your day: items, effort and a fake total.", ShowReceipt);
            Tool(WeekTaskId, "Task of the week", "Each Friday the week's biggest finished task gets a tiny award ceremony.", ShowAwards);
            Tool(WindId, "Friday wind-down", "A short recap of your week, with a line from Bunny.", () => _ = WindDown(true));
            Tool(KindId, "I helped someone", "Log a kindness. It pays " + KindPoints + " kindness points and grows a wildflower.", Kind);
            Tool(NoId, "I said no", "Log a time you said no to an extra commitment. Boundaries pay " + NoPoints + " points.", SaidNo);
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.C, Tools.Guard(ShareId, Share), "Control Shift C: share a win, copies a brag line about today");
            BunnyTab.Register("The marble jar", "marbles from your finished tasks", ShowJar, MarbleId);
            BunnyTab.Register("Wildflowers", "a flower for each kindness", ShowFlowers, KindId);

            GameEvents.TaskDone += OnDone;
            GameEvents.TaskUndone += t => Undone(S, t);
            GameEvents.SprintDone += minutes =>
            {
                if (!Tools.IsOn(BestsId) || minutes <= 0) return;
                var b = S.World.GetKept<Bests>(BestsId);
                int before = Math.Max(b.LongestSprint, S.Data.Log.Where(e => e.Kind == "Sprint" && e.When < DateTime.Now.AddMinutes(-1)).Select(e => e.Minutes).DefaultIfEmpty(0).Max());
                b.LongestSprint = Math.Max(before, minutes);
                S.World.SetKept(BestsId, b);
                S.World.Save();
                if (minutes > before && before >= 10)
                {
                    Celebration.Sound(S, "PersonalRecord");
                    Calm.SayUnprompted(_w, Celebration.Line(S, "New personal best: your longest sprint, " + minutes + " minutes, beating " + before + "."));
                }
            };
            GameEvents.DayEnded += day => { if (Tools.IsOn(ReceiptId)) GamesFunKit.Put(S, ReceiptId, new Receipts { Day = S.Key(day), Lines = Receipt(S, day) }); };
            GameEvents.WeekEnded += monday =>
            {
                if (Tools.IsOn(WeekTaskId) && AwardWeek(S, monday) is (_, string line)) { S.Save(); Calm.SayUnprompted(_w, line); }
                if (Tools.IsOn(WindId) && !Calm.FunPaused) _ = WindDown(false, monday);
            };
        }

        private void OnDone(TaskItem t)
        {
            var now = DateTime.Now;
            void Offer(int p, string line) { if (line.Length > 0) Calm.AfterTaskDone(_w, p, line); }
            try
            {
                if (Tools.IsOn(MarbleId) && Drop(S, t, now) is (true, var full))
                {
                    // A flat prize, not a multiple of the task: plain Pay, so it never takes room from the task's extras ceiling.
                    if (Ledger.Pay(S, 1, "A marble for " + t.Name, MarbleId, t.Id) is Outcome o) Game.Queue(_w, o);
                    // The clink only for a full jar: the task-done sound (her tune, a list pack) just played.
                    if (full != null) Celebration.Sound(S, "Coffee, clink");
                    int n = S.World.Get<Jar>(MarbleId).Tasks.Count;
                    if (full != null) Offer(Game.Goals, Celebration.Line(S, "The marble jar is full! Fifty marbles. Your prize: " + full.Prize + ". A new jar starts."));
                    else Offer(Game.Chatter, Content.Pick(S, "records.marbles.drop") + " " + GamesFunKit.Plural(n, "marble") + " of " + JarSize + ".");
                }
            }
            catch (Exception e) { ErrorLog.Write("Marble jar: " + e.Message); }
            try
            {
                if (Tools.IsOn(RaceId) && PassedPastSelf(S, t, now) is string line) { Celebration.Sound(S, "Bunny, two hops"); Offer(Game.Other, Celebration.Line(S, line)); }
            }
            catch (Exception e) { ErrorLog.Write("Race your past self: " + e.Message); }
            try
            {
                if (Tools.IsOn(BestsId) && TaskRecord(S, t, now) is string rec) { Celebration.Sound(S, "PersonalRecord"); Offer(Game.Record, Celebration.Line(S, rec)); }
            }
            catch (Exception e) { ErrorLog.Write("Personal bests: " + e.Message); }
            S.Save();
        }

        public static void Undone(Store s, TaskItem t)
        {
            TakeBack(s, t);
            var r = s.World.Get<Race>(RaceId);
            if (r.PassedBy == t.Id) { s.World.Set(RaceId, new Race()); }
            var b = s.World.Get<Bests>(BestsId + ".said");
            foreach (var k in b.SaidBy.Where(x => x.Value == t.Id).Select(x => x.Key).ToList()) { b.SaidBy.Remove(k); b.SaidDay.Remove(k); }
            s.World.Set(BestsId + ".said", b);
            s.World.Save();
        }

        // ---- The marble jar ----

        /// <summary>One marble per task, however often it is finished. Fifty fill the jar with a prize. Returns whether it dropped, and the jar it filled.</summary>
        public static (bool, FilledJar?) Drop(Store s, TaskItem t, DateTime now)
        {
            var jar = s.World.Get<Jar>(MarbleId);
            if (jar.Tasks.Contains(t.Id) || jar.Filled.Any(f => f.Tasks.Contains(t.Id))) return (false, null);
            jar.Tasks.Add(t.Id);
            FilledJar? full = null;
            if (jar.Tasks.Count >= JarSize)
            {
                var prizes = Content.Items("records.marbles.prizes");
                var p = prizes.Count == 0 ? new Dictionary<string, string>() : prizes[jar.Filled.Count % prizes.Count];
                full = new FilledJar { Prize = p.GetValueOrDefault("name", "a shiny thing") + ". " + string.Join(" ", new[] { "touch", "sound", "looks" }.Where(p.ContainsKey).Select(k => p[k])), Tasks = jar.Tasks, By = t.Id, When = now };
                jar.Filled.Add(full);
                jar.Tasks = new();
            }
            GamesFunKit.Put(s, MarbleId, jar);
            return (true, full);
        }

        public static void TakeBack(Store s, TaskItem t)
        {
            var jar = s.World.Get<Jar>(MarbleId);
            var last = jar.Filled.LastOrDefault();
            if (last != null && last.By == t.Id && jar.Tasks.Count == 0)
            {
                // The marble that filled the jar comes out: the jar is not full after all.
                jar.Filled.Remove(last);
                jar.Tasks = last.Tasks.Where(id => id != t.Id).ToList();
            }
            else if (!jar.Tasks.Remove(t.Id)) return;
            s.World.Set(MarbleId, jar);
            s.World.Save();
        }

        private void ShowJar()
        {
            var jar = S.World.Get<Jar>(MarbleId);
            var lines = new List<string> { GamesFunKit.Plural(jar.Tasks.Count, "marble") + " in the jar, " + (JarSize - jar.Tasks.Count) + " to go. Jars filled: " + jar.Filled.Count + "." };
            lines.AddRange(jar.Filled.AsEnumerable().Reverse().Select(f => "Filled " + f.When.ToString("MMMM d") + ": " + f.Prize));
            if (jar.Filled.Count == 0) lines.Add("The first full jar gives a prize. Every finished task is one marble.");
            GamesFunKit.Read(_w, "The marble jar", lines);
        }

        // ---- Share a win ----

        /// <summary>A short brag line about today: counts, and names only of tasks Bunny may see (never a private list).</summary>
        public static string ShareLine(Store s, DateTime now)
        {
            var done = GamesFunKit.DoneOn(s, now);
            var shown = done.Where(t => SmartBunny.MayShare(s, t)).Select(t => t.Name).Take(3).ToList();
            int things = done.Count + s.Data.Log.Count(e => e.Kind == "DidAThing" && e.When.Date == now.Date);
            int pts = Engine.PointsOn(s.Data, now), streak = Engine.Streaks(s.Data, now).Current;
            string line = things == 0 ? "Today I showed up, and that counts." : "Today I got " + GamesFunKit.Plural(things, "thing") + " done" + (shown.Count > 0 ? ", including " + string.Join(", ", shown) : "") + ".";
            if (pts > 0) line += " " + GamesFunKit.Plural(pts, "point") + " earned.";
            if (streak >= 2) line += " " + streak + " days in a row.";
            return line + " " + Content.Pick(s, "records.share");
        }

        private void Share()
        {
            string line = ShareLine(S, DateTime.Now);
            try { Clipboard.SetText(line); _w.Say("Copied, ready to paste: " + line, true); }
            catch { _w.Say("Could not reach the clipboard just now. Here it is: " + line, true); }
        }

        // ---- Racing your past self ----

        public static (int Now, int Then) PastSelf(Store s, DateTime now) =>
            (GamesFunKit.PointsBy(s.Data, now, now.TimeOfDay), GamesFunKit.PointsBy(s.Data, now.AddDays(-7), now.TimeOfDay));

        /// <summary>The line when this finish passes last week's self, once a day.</summary>
        public static string? PassedPastSelf(Store s, TaskItem t, DateTime now)
        {
            var r = s.World.Get<Race>(RaceId);
            if (r.PassedDay == s.Key(now)) return null;
            var (mine, then) = PastSelf(s, now);
            if (then <= 0 || mine <= then) return null;
            GamesFunKit.Put(s, RaceId, new Race { PassedDay = s.Key(now), PassedBy = t.Id });
            return "You just passed last " + now.ToString("dddd") + "'s you: " + mine + " points to " + then + " at this time. " + Content.Pick(s, "records.race");
        }

        private void ShowRace()
        {
            var now = DateTime.Now;
            var (mine, then) = PastSelf(S, now);
            string day = now.ToString("dddd");
            _w.Say(then == 0 ? "Last " + day + " at this time you had no points yet, so today's you is " + (mine > 0 ? "already ahead, with " + mine + "." : "tied, at zero. A fair start.")
                : mine > then ? "You are ahead of last " + day + "'s you: " + mine + " points to " + then + "."
                : "Last " + day + "'s you had " + then + " points by now; you have " + mine + ". " + (then - mine) + " to catch up, no pressure.", true);
        }

        // ---- Done list and best day ----

        /// <summary>
        /// When a finished task was finished: Done keeps DoneAt; Done not submitted has no DoneAt, so it is when its
        /// status was set. Null for a task that is not finished.
        /// </summary>
        public static DateTime? FinishedAt(TaskItem t) =>
            t.Status == Status.Done ? t.DoneAt ?? t.StatusSince : t.Status == Status.DoneNotSubmitted ? t.StatusSince ?? t.DoneAt ?? t.TouchedAt : null;

        /// <summary>Tasks finished on a day, Done and Done not submitted, on lists that are not archived, earliest first.</summary>
        public static List<TaskItem> FinishedOn(Store s, DateTime day) =>
            GamesFunKit.Live(s).Where(t => FinishedAt(t)?.Date == day.Date).OrderBy(t => FinishedAt(t)).ToList();

        public static List<string> DoneList(Store s, DateTime day)
        {
            var lines = FinishedOn(s, day).Select(t => Words.Time(FinishedAt(t)!.Value) + ": " + t.Name + ", " + t.List + (t.Status == Status.DoneNotSubmitted ? ", done, not submitted yet" : "")).ToList();
            lines.AddRange(s.Data.Log.Where(e => e.Kind == "DidAThing" && e.When.Date == day.Date).Select(e => Words.Time(e.When) + ": " + e.Text + ", I did a thing"));
            return lines;
        }

        /// <summary>How many days the done list looks back, today included.</summary>
        public const int DoneLogDays = 14;

        /// <summary>
        /// The log of what she finished (Morgan's issue 20): today first, then each earlier day with something finished,
        /// back DoneLogDays days. Her own window, so every list she can see is in it.
        /// </summary>
        public static List<string> DoneLog(Store s, DateTime now, int days = DoneLogDays)
        {
            var lines = new List<string>();
            var today = DoneList(s, now);
            lines.Add(today.Count == 0 ? "Today: nothing finished yet. That is fine; the day is not over." : "Today, " + GamesFunKit.Plural(today.Count, "thing") + " done.");
            lines.AddRange(today);
            for (int back = 1; back < days; back++)
            {
                var day = now.Date.AddDays(-back);
                var those = DoneList(s, day);
                if (those.Count == 0) continue;
                lines.Add((back == 1 ? "Yesterday, " : day.ToString("dddd, MMMM d", System.Globalization.CultureInfo.InvariantCulture) + ", ") + GamesFunKit.Plural(those.Count, "thing") + " done.");
                lines.AddRange(those);
            }
            if (lines.Count == 1) lines.Add("Nothing finished in the last " + days + " days yet. The first one is the hardest.");
            return lines;
        }

        /// <summary>The done list window: what she finished today and the days before. Also Bunny, What I finished.</summary>
        public static void ShowDoneLog(MainWindow w) => GamesFunKit.Read(w, "What I finished", DoneLog(w.Store, DateTime.Now));

        private void ShowDoneList() => ShowDoneLog(_w);

        public static DateTime? BestDay(Data d) =>
            d.Log.Where(e => Engine.KarmaKinds.Contains(e.Kind)).GroupBy(e => e.When.Date).Select(g => (Day: g.Key, Pts: g.Sum(e => e.Points))).Where(x => x.Pts > 0).OrderByDescending(x => x.Pts).ThenByDescending(x => x.Day).Select(x => (DateTime?)x.Day).FirstOrDefault();

        private void ShowBestDay()
        {
            var d = S.Data;
            if (BestDay(d) is not DateTime day) { _w.Say("No best day yet. Finish something and today could be it.", true); return; }
            var log = d.Log.Where(e => e.When.Date == day).ToList();
            var done = log.Where(e => e.Kind == "Done").ToList();
            var lines = new List<string>
            {
                "Your best day: " + day.ToString("dddd, MMMM d, yyyy") + ", " + GamesFunKit.Plural(log.Where(e => Engine.KarmaKinds.Contains(e.Kind)).Sum(e => e.Points), "point") + ", " + GamesFunKit.Plural(done.Select(e => e.TaskId).Distinct().Count(), "task") + " finished.",
            };
            int minutes = log.Where(e => e.Kind == "Sprint").Sum(e => e.Minutes);
            if (minutes > 0) lines.Add("Focused for " + GamesCard.Minutes(minutes) + ".");
            lines.AddRange(done.OrderBy(e => e.When).Select(e => Words.Time(e.When) + ": " + (e.Text.Length > 0 ? e.Text : "a task")));
            lines.AddRange(log.Where(e => e.Kind == "DidAThing").Select(e => Words.Time(e.When) + ": " + e.Text + ", I did a thing"));
            lines.Add(Content.Pick(S, "records.bestday"));
            GamesFunKit.Read(_w, "Best day replay", lines);
        }

        // ---- Personal bests board ----

        private static int TasksOn(Data d, DateTime day, int beforeHour = 24) =>
            d.Log.Where(e => e.Kind == "Done" && e.When.Date == day.Date && e.When.Hour < beforeHour).Select(e => e.TaskId).Distinct().Count();

        private static int MostEarlier(Data d, DateTime today, int beforeHour) =>
            d.Log.Where(e => e.Kind == "Done" && e.When.Date < today.Date && e.When.Hour < beforeHour).GroupBy(e => e.When.Date).Select(g => g.Select(e => e.TaskId).Distinct().Count()).DefaultIfEmpty(0).Max();

        /// <summary>A record this finish broke (most tasks in a day, or before noon), once a day each. Null when none.</summary>
        public static string? TaskRecord(Store s, TaskItem t, DateTime now)
        {
            var b = s.World.Get<Bests>(BestsId + ".said");
            string today = s.Key(now);
            foreach (var (key, hour, words) in new[] { ("day", 24, "most tasks in one day"), ("noon", 12, "most tasks before noon") })
            {
                if (now.Hour >= hour || b.SaidDay.GetValueOrDefault(key) == today) continue;
                int before = MostEarlier(s.Data, now, hour), mine = TasksOn(s.Data, now, hour);
                if (before < 2 || mine <= before) continue;
                b.SaidDay[key] = today;
                b.SaidBy[key] = t.Id;
                GamesFunKit.Put(s, BestsId + ".said", b);
                return "New personal best: " + words + ", " + mine + ", beating " + before + ".";
            }
            return null;
        }

        public static List<string> BestsBoard(Store s, DateTime now)
        {
            var d = s.Data;
            var lines = new List<string>();
            int sprint = Math.Max(s.World.GetKept<Bests>(BestsId).LongestSprint, d.Log.Where(e => e.Kind == "Sprint").Select(e => e.Minutes).DefaultIfEmpty(0).Max());
            lines.Add("Longest sprint: " + (sprint > 0 ? GamesCard.Minutes(sprint) : "none yet") + ".");
            var days = d.Log.Where(e => e.Kind == "Done").GroupBy(e => e.When.Date).ToList();
            var most = days.Select(g => (g.Key, N: g.Select(e => e.TaskId).Distinct().Count())).OrderByDescending(x => x.N).FirstOrDefault();
            lines.Add("Most tasks in one day: " + (most.N > 0 ? most.N + ", on " + most.Key.ToString("MMMM d, yyyy") : "none yet") + ".");
            var noon = days.Select(g => (g.Key, N: g.Where(e => e.When.Hour < 12).Select(e => e.TaskId).Distinct().Count())).OrderByDescending(x => x.N).FirstOrDefault();
            lines.Add("Most tasks before noon: " + (noon.N > 0 ? noon.N + ", on " + noon.Key.ToString("MMMM d, yyyy") : "none yet") + ".");
            lines.Add("Biggest day: " + (BestDay(d) is DateTime bd ? GamesFunKit.Plural(Engine.PointsOn(d, bd), "point") + ", on " + bd.ToString("MMMM d, yyyy") : "none yet") + ".");
            lines.Add("Best week: " + GamesFunKit.Plural(d.Game.BestWeek, "point") + ".");
            lines.Add("Longest streak: " + GamesFunKit.Plural(Math.Max(d.Game.LongestStreak, Engine.Streaks(d, now).Longest), "day") + ".");
            var focus = d.Log.Where(e => e.Kind == "Sprint").GroupBy(e => e.When.Date).Select(g => g.Sum(e => e.Minutes)).DefaultIfEmpty(0).Max();
            lines.Add("Most focused time in a day: " + (focus > 0 ? GamesCard.Minutes(focus) : "none yet") + ".");
            lines.Add("Records broken so far: " + d.Game.RecordsBroken + ".");
            return lines;
        }

        private void ShowBests() => GamesFunKit.Read(_w, "Personal bests board", BestsBoard(S, DateTime.Now));

        // ---- Effort receipt ----

        public static List<string> Receipt(Store s, DateTime day)
        {
            var d = s.Data;
            var done = d.Log.Where(e => e.Kind == "Done" && e.When.Date == day.Date).ToList();
            var did = d.Log.Where(e => e.Kind == "DidAThing" && e.When.Date == day.Date).ToList();
            int pts = Engine.PointsOn(d, day), effort = done.Count + did.Count;
            var lines = new List<string> { "Effort receipt, " + day.ToString("dddd, MMMM d") + ". Busy Bunny General Store, thank you for shopping." };
            foreach (var e in done) lines.Add("1 finished task, " + (e.Text.Length > 0 ? e.Text : "a task") + ": " + GamesFunKit.Plural(e.Points, "point"));
            foreach (var e in did) lines.Add("1 thing nobody listed, " + e.Text + ": priceless");
            int sprint = d.Log.Where(e => e.Kind == "Sprint" && e.When.Date == day.Date).Sum(e => e.Minutes);
            if (sprint > 0) lines.Add("Focus, " + GamesCard.Minutes(sprint) + ": paid in full");
            lines.Add("Subtotal: " + GamesFunKit.Plural(effort, "item") + ", " + GamesFunKit.Plural(pts, "point") + ".");
            lines.Add("Tax: " + Math.Max(1, effort) + (effort <= 1 ? " snack" : " snacks") + ". Tip for Bunny: one carrot.");
            lines.Add("Total: " + (effort == 0 ? "one day of being a person, which is plenty" : GamesFunKit.Plural(effort * 3 + pts, "brain cell") + " spent wisely") + ".");
            lines.Add(Content.Pick(s, "records.receipt"));
            return lines;
        }

        private void ShowReceipt()
        {
            var last = S.World.Get<Receipts>(ReceiptId);
            int i = GamesFunKit.Pick(_w, "Effort receipt", "_Which receipt", new[] { "Today so far", last.Lines.Count > 0 ? "The last finished day, " + last.Day : "The last finished day, not printed yet" });
            if (i < 0) return;
            if (i == 1 && last.Lines.Count == 0) { _w.Say("The next receipt prints when today ends.", true); return; }
            GamesFunKit.Read(_w, "Effort receipt", i == 0 ? Receipt(S, DateTime.Now) : last.Lines);
        }

        // ---- Task of the week ----

        /// <summary>The week's most important finished task: grade weight, then size, then top priority.</summary>
        public static TaskItem? WeekTask(Store s, DateTime monday) =>
            GamesFunKit.Live(s).Where(t => GamesFunKit.Finished(t) && t.DoneAt is DateTime d && d.Date >= monday.Date && d.Date < monday.Date.AddDays(7))
                .OrderByDescending(t => t.GradeWeight * 3 + Words.Points(t.Size) * 2 + (t.Priority == 1 && s.ListOf(t).Scale.Length > 0 ? 3 : 0))
                .ThenBy(t => t.DoneAt).FirstOrDefault();

        /// <summary>Gives the week's award once (5 points with the task's id) and returns the ceremony line.</summary>
        public static (Outcome?, string)? AwardWeek(Store s, DateTime monday)
        {
            var a = s.World.Get<Awards>(WeekTaskId);
            string week = GamesFunKit.Key(monday);
            if (a.All.Any(x => x.Week == week) || WeekTask(s, monday) is not TaskItem t) return null;
            string line = Content.Pick(s, "records.ceremony").Replace("{task}", t.Name);
            // Its own Control Z step: paid from WeekEnded, outside any finish, so an undo of an earlier change keeps it.
            s.Remember("task of the week");
            a = s.World.Get<Awards>(WeekTaskId);
            a.All.Add(new Award { Week = week, Task = t.Name, Line = line });
            GamesFunKit.Put(s, WeekTaskId, a);
            var o = Ledger.Pay(s, AwardPoints, "Task of the week: " + t.Name, WeekTaskId, t.Id);
            return (o, "Task of the week: " + t.Name + ". " + line + " " + AwardPoints + " points.");
        }

        private void ShowAwards()
        {
            var a = S.World.Get<Awards>(WeekTaskId).All;
            var lines = a.AsEnumerable().Reverse().Select(x => "Week of " + (GamesFunKit.Day(x.Week)?.ToString("MMMM d") ?? x.Week) + ": " + x.Task + ". " + x.Line).ToList();
            var now = WeekTask(S, Engine.WeekStart(DateTime.Now));
            lines.Insert(0, now == null ? "Nothing finished this week yet. The award is given on Friday afternoon." : "Leading this week: " + now.Name + ". The award is given on Friday afternoon.");
            GamesFunKit.Read(_w, "Task of the week", lines);
        }

        // ---- Friday wind-down ----

        public static string WeekRecap(Store s, DateTime monday)
        {
            var d = s.Data;
            int tasks = d.Log.Where(e => e.Kind == "Done" && e.When.Date >= monday && e.When.Date < monday.AddDays(7)).Select(e => e.TaskId).Distinct().Count();
            int pts = Engine.PointsInWeek(d, monday);
            var best = Enumerable.Range(0, 7).Select(k => monday.AddDays(k)).OrderByDescending(x => Engine.PointsOn(d, x)).First();
            string line = "Your week: " + GamesFunKit.Plural(tasks, "task") + " finished, " + GamesFunKit.Plural(pts, "point") + ".";
            if (Engine.PointsOn(d, best) > 0) line += " Your biggest day was " + best.ToString("dddd") + ".";
            return line;
        }

        /// <summary>
        /// The wind-down prompt for Claude: built only from tasks Bunny may see (BunnyWrite.Visible), finished in that
        /// week. No points and no counts from private or archived lists; the full recap stays local, for what she hears.
        /// </summary>
        public static string WindPrompt(Store s, DateTime monday)
        {
            var seen = BunnyWrite.Visible(s).Where(t => GamesFunKit.Finished(t) && t.DoneAt is DateTime d && d >= monday.Date && d < monday.Date.AddDays(7)).OrderBy(t => t.DoneAt).ToList();
            var names = seen.Select(t => t.Name).Take(8).ToList();
            return "Write one warm, short sentence to wind down her week. She finished " + GamesFunKit.Plural(seen.Count, "task") + " this week that you may hear about." +
                (names.Count > 0 ? " Some of them: " + string.Join("; ", names) + "." : "");
        }

        private async System.Threading.Tasks.Task WindDown(bool asked, DateTime? week = null)
        {
            var monday = Engine.WeekStart(week ?? DateTime.Now);
            string recap = WeekRecap(S, monday);
            string bunny = await BunnyWrite.Short(S, WindId, WindPrompt(S, monday), "records.winddown");
            string text = recap + " " + bunny;
            RecordsStyle.KeepCompliment(S, bunny);
            GamesFunKit.Put(S, WindId, new Recap { Week = GamesFunKit.Key(monday), Text = text });
            if (asked) _w.Say(text, true); else Calm.SayUnprompted(_w, "Friday wind-down. " + text);
        }

        // ---- Kindness and boundary points ----

        /// <summary>Logs a kindness as an I did a thing entry, pays kindness points (3 a day) and grows a wildflower. One Control Z step.</summary>
        public static string LogKindness(Store s, string what, DateTime now)
        {
            s.Remember("a kindness");
            s.Data.Log.Add(new LogEntry { When = now, Kind = "DidAThing", Text = HelpedText + what, Points = 1 });
            var k = s.World.Get<Kindness>(KindId);
            if (k.Day != s.Key(now)) { k.Day = s.Key(now); k.PaidToday = 0; }
            var flowers = Content.Items("records.wildflowers");
            var f = flowers.Count == 0 ? new Dictionary<string, string>() : flowers[k.Total % flowers.Count];
            string flower = f.GetValueOrDefault("name", "a wildflower");
            k.Flowers.Add(flower + ". " + string.Join(" ", new[] { "touch", "sound", "looks" }.Where(f.ContainsKey).Select(x => f[x])));
            k.Total++;
            bool pay = k.PaidToday < PaidADay;
            if (pay) k.PaidToday++;
            GamesFunKit.Put(s, KindId, k);
            // Not "records.kindness": Bunny's world counts Fun entries with "kind" in the feature, and the I did a thing line already counts once.
            if (pay) Ledger.Pay(s, KindPoints, "Kindness points", HelpedFeature);
            s.Save();
            return "Kindness logged. " + (pay ? KindPoints + " kindness points. " : "") + "A new wildflower opens: " + k.Flowers[^1];
        }

        public static string LogNo(Store s, string what, DateTime now)
        {
            s.Remember("a boundary");
            var k = s.World.Get<Kindness>(NoId);
            if (k.Day != s.Key(now)) { k.Day = s.Key(now); k.PaidToday = 0; }
            k.Total++;
            bool pay = k.PaidToday < PaidADay;
            if (pay) k.PaidToday++;
            if (what.Length > 0) k.Flowers.Add(now.ToString("MMMM d") + ": " + what);
            GamesFunKit.Put(s, NoId, k);
            if (pay) Ledger.Pay(s, NoPoints, "Boundary points", NoId);
            s.Save();
            return "Boundary logged. " + (pay ? NoPoints + " boundary points. " : "") + "That is " + GamesFunKit.Plural(k.Total, "time") + " you protected your time. " + Content.Pick(s, "records.boundary");
        }

        private void Kind()
        {
            var what = GamesFunKit.Ask(_w, "I helped someone", "_Who did you help, or how?", intro: "Kindness counts. It goes on your done list and grows a wildflower.");
            if (what == null) return;
            string line = LogKindness(S, what, DateTime.Now);
            _w.SaveAndRefresh();
            Celebration.Sound(S, "Flute, rising");
            _w.Say(Celebration.Line(S, line) + " " + PlayTime.SpendLine(S.Data) + ".", true);
        }

        private void ShowFlowers()
        {
            var k = S.World.Get<Kindness>(KindId);
            GamesFunKit.Read(_w, "Wildflowers", k.Flowers.Count == 0 ? new List<string> { "No wildflowers yet. Each kindness you log with I helped someone grows one." } : k.Flowers.Prepend(GamesFunKit.Plural(k.Flowers.Count, "wildflower") + ", one for each kindness.").ToList());
        }

        private void SaidNo()
        {
            var what = GamesFunKit.Ask(_w, "I said no", "_What did you say no to? You can leave it blank", intro: "Saying no to extra things protects your time. That is a win.", allowEmpty: true);
            if (what == null) return;
            string line = LogNo(S, what, DateTime.Now);
            _w.SaveAndRefresh();
            _w.Say(line + " " + PlayTime.SpendLine(S.Data) + ".", true);
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("wins: " + what); }
            foreach (var topic in new[] { "records.marbles.drop", "records.share", "records.race", "records.receipt", "records.ceremony", "records.winddown", "records.boundary", "records.bestday" })
                Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            Need(Content.Items("records.marbles.prizes").Count >= 20 && Content.Items("records.wildflowers").Count >= 20, "prizes and wildflowers need 20 each");
            Need(Content.Items("records.wildflowers").All(f => f.ContainsKey("touch") && f.ContainsKey("sound")), "a wildflower has no touch or sound");
            var now = DateTime.Now;
            var list = store.Data.Lists[0];
            // Marbles: done, back, done again is one marble; the fiftieth fills the jar, and taking it back un-fills it.
            var tasks = Enumerable.Range(0, JarSize).Select(k => new TaskItem { Name = "marble " + k, List = list.Name, Status = Status.Done, DoneAt = now }).ToList();
            Drop(store, tasks[0], now); TakeBack(store, tasks[0]); Drop(store, tasks[0], now); Drop(store, tasks[0], now);
            Need(store.World.Get<Jar>(MarbleId).Tasks.Count == 1, "done, back, done again gave more than one marble");
            FilledJar? full = null;
            foreach (var t in tasks.Skip(1)) full = Drop(store, t, now).Item2 ?? full;
            Need(full != null && store.World.Get<Jar>(MarbleId).Tasks.Count == 0 && store.World.Get<Jar>(MarbleId).Filled.Count == 1, "fifty marbles did not fill the jar");
            TakeBack(store, tasks[^1]);
            Need(store.World.Get<Jar>(MarbleId).Filled.Count == 0 && store.World.Get<Jar>(MarbleId).Tasks.Count == JarSize - 1, "taking back the fiftieth marble kept the full jar");
            // Share a win never names a task from a private list.
            var secret = new TaskItem { Name = "ZZ secret thing", List = list.Name, Status = Status.Done, DoneAt = now };
            store.Data.Tasks.Add(secret);
            bool was = list.PrivateFromBunny;
            list.PrivateFromBunny = true;
            Need(!ShareLine(store, now).Contains("ZZ secret"), "share a win named a private task");
            // The wind-down prompt for Claude: no private names, and no private tasks in its count.
            var monday = Engine.WeekStart(now);
            string prompt = WindPrompt(store, monday);
            int visible = BunnyWrite.Visible(store).Count(t => GamesFunKit.Finished(t) && t.DoneAt >= monday && t.DoneAt < monday.AddDays(7));
            Need(!prompt.Contains("ZZ secret") && prompt.Contains(GamesFunKit.Plural(visible, "task")) && !prompt.Contains("point"), "the wind-down prompt counted or named a private task");
            list.PrivateFromBunny = was;
            store.Data.Tasks.Remove(secret);
            var later = new TaskItem { Name = "ZZ later thing", List = list.Name, Status = Status.Done, DoneAt = monday.AddDays(-5) };
            store.Data.Tasks.Add(later);
            Need(!WindPrompt(store, monday.AddDays(-14)).Contains("ZZ later") && (list.PrivateFromBunny || list.Archived || WindPrompt(store, monday.AddDays(-7)).Contains("ZZ later")), "a wind-down used the wrong week's tasks");
            store.Data.Tasks.Remove(later);
            // The done log (issue 20): Done not submitted counts, and earlier days are in it, not only today.
            var handIn = new TaskItem { Name = "ZZ hand in later", List = list.Name, Status = Status.DoneNotSubmitted, StatusSince = now };
            var yesterday = new TaskItem { Name = "ZZ yesterday thing", List = list.Name, Status = Status.Done, DoneAt = now.Date.AddDays(-1).AddHours(15) };
            store.Data.Tasks.Add(handIn);
            store.Data.Tasks.Add(yesterday);
            if (!list.Archived)
            {
                var log = DoneLog(store, now);
                Need(log.Any(l => l.Contains("ZZ hand in later") && l.Contains("not submitted")), "the done log left out a task done but not submitted");
                Need(log.Any(l => l.Contains("ZZ yesterday thing")) && !DoneList(store, now).Any(l => l.Contains("ZZ yesterday thing")), "the done log left out yesterday, or today's list had it");
            }
            store.Data.Tasks.Remove(handIn);
            store.Data.Tasks.Remove(yesterday);
            // Kindness: points, a wildflower and a done list line, all undone in one step; 3 paid a day.
            int before = Engine.Total(store.Data);
            LogKindness(store, "carried groceries", now);
            Need(Engine.Total(store.Data) == before + KindPoints && store.World.Get<Kindness>(KindId).Flowers.Count == 1 && DoneList(store, now).Any(l => l.Contains("carried groceries")), "a kindness");
            store.Undo();
            Need(Engine.Total(store.Data) == before && store.World.Get<Kindness>(KindId).Flowers.Count == 0, "Control Z did not undo a kindness");
            for (int k = 0; k < 4; k++) LogNo(store, "", now);
            Need(Engine.Total(store.Data) == before + PaidADay * NoPoints, "boundary points past 3 a day");
            // Racing your past self: passing last week's points says so once a day.
            store.Data.Log.Add(new LogEntry { Kind = "Done", When = now.AddDays(-7).AddSeconds(-1), Points = 3, TaskId = Guid.NewGuid() });
            store.World.Set(RaceId, new Race());
            var racer = new TaskItem { Name = "racer", List = list.Name };
            store.Data.Log.Add(new LogEntry { Kind = "Done", When = now.AddSeconds(-1), Points = 500, TaskId = racer.Id });
            var (mine, then) = PastSelf(store, now);
            Need(mine > then && then >= 3, "past self points");
            Need(PassedPastSelf(store, racer, now) != null && PassedPastSelf(store, racer, now) == null, "passing your past self was not said exactly once");
            Undone(store, racer);
            Need(store.World.Get<Race>(RaceId).PassedDay == "", "un-finishing did not reset the race");
            Need(Receipt(store, now).Count >= 4 && BestsBoard(store, now).Count >= 6, "receipt and board");
        }
    }
}
