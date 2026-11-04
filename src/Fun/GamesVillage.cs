using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    /// <summary>
    /// Games drawer, part two: village quests (posted weekly at a simple village board, a place in Bunny's world, until
    /// the full village with neighbors is built), snail racing, mini golf, bowling night and a scavenger hunt in her
    /// burrow rooms (sweep 8.4 and 13.3 items 2, 11, 12 and 19). The little games are turn by turn: nothing moves until
    /// she presses a button, so there is never a timer to beat.
    /// </summary>
    public sealed class GamesVillage : IFeature
    {
        public const string QuestId = "games.village", SnailId = "games.snail", GolfId = "games.golf", BowlId = "games.bowling", HuntId = "games.hunt";
        public const string PlaceId = "games.village";
        public const int SnailPrize = 5, AcePrize = 5, HuntPrize = 5, BowlMax = 30, QuestsAWeek = 3;
        private MainWindow _w = null!;
        private Store S => _w.Store;

        public sealed class Quest { public int Index { get; set; } public List<Guid> Tasks { get; set; } = new(); public int Sprints { get; set; } public bool Paid { get; set; } public Guid? PaidBy { get; set; } }
        public sealed class Board { public string Week { get; set; } = ""; public List<Quest> Quests { get; set; } = new(); }
        public sealed class Prizes { public string SnailWeekend { get; set; } = ""; public string AceDay { get; set; } = ""; public string BowlMonth { get; set; } = ""; public string HuntDay { get; set; } = ""; public Dictionary<string, int> GolfBest { get; set; } = new(); public int BowlBest { get; set; } }
        public sealed class Hidden { public int Item { get; set; } public string Spot { get; set; } = ""; public bool Found { get; set; } }
        public sealed class Hunt { public string Day { get; set; } = ""; public List<Hidden> Things { get; set; } = new(); }

        public void Attach(MainWindow w)
        {
            _w = w;
            Tools.Register(new BunnyTool(QuestId, "Village quests", Drawers.Games, "Small challenges the neighbors post each week at the village board.", () => ShowQuests(_w)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(SnailId, "Snail racing", Drawers.Games, "Cheer a snail down the track. Weekend races pay a little.", () => Snails(_w)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(GolfId, "Mini golf", Drawers.Games, "A putt-putt course in the village. Each weekly goal you meet opens a new hole.", () => Golf(_w)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(BowlId, "Bowling night", Drawers.Games, "Bowl ten frames with the neighbors. Every task you finish this month is a strike.", () => Bowling(_w)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(HuntId, "Scavenger hunt", Drawers.Games, "Three things hidden around your burrow, with clues.", () => ScavengerHunt(_w)) { Since = "0.14.0" });
            BunnyTab.Register("Village quests", "what the neighbors posted this week", () => ShowQuests(_w), QuestId);
            // Inside the village of Bunny's world (Stage 7), after its own things: one village, not two.
            PlaceHooks.AddThings(PlaceIds.Village, Things);

            GameEvents.WeekStarted += _ => { if (Tools.IsOn(QuestId)) BoardFor(S, DateTime.Now); };
            GameEvents.TaskDone += t =>
            {
                if (!Tools.IsOn(QuestId)) return;
                foreach (var (o, line) in QuestDone(S, t, DateTime.Now))
                {
                    if (o != null) Game.Queue(_w, o);
                    Calm.AfterTaskDone(_w, line.StartsWith("Quest done") ? Game.Goals : Game.Chatter, line);
                }
                S.Save();
            };
            GameEvents.TaskUndone += t => QuestUndone(S, t);
            GameEvents.SprintDone += minutes =>
            {
                if (!Tools.IsOn(QuestId) || minutes <= 0) return;
                foreach (var line in QuestSprint(S, DateTime.Now)) { S.Save(); Calm.SayUnprompted(_w, line); }
            };
        }

        /// <summary>The village's lines from these games: only the ones she has turned on.</summary>
        public List<PlaceThing> Things(Store s)
        {
            var things = new List<PlaceThing>();
            if (Tools.IsOn(QuestId))
            {
                var b = BoardFor(s, DateTime.Now);
                things.Add(new("The quest board", "rough wood under your paws, pins that click when you touch them, paper that crinkles in the breeze", o => Speech.Say(o, BoardLine(s, DateTime.Now) + ".", true)));
                foreach (var q in b.Quests) { var line = QuestLine(s, q); things.Add(new(line, "Enter reads it", o => Speech.Say(o, line, true))); }
            }
            if (Tools.IsOn(SnailId)) things.Add(new("Snail racing", "the snail track behind the quest board", o => Snails(o)));
            if (Tools.IsOn(GolfId)) things.Add(new("Mini golf", "the little course by the pond", o => Golf(o)));
            if (Tools.IsOn(BowlId)) things.Add(new("Bowling night", "the old lanes at the village hall", o => Bowling(o)));
            return things;
        }

        // ---- Village quests ----

        public static List<Dictionary<string, string>> QuestItems() =>
            Content.Items("games.village.quests").Where(i => i.ContainsKey("who") && i.ContainsKey("text") && i.ContainsKey("rule") && (i["rule"] == "sprint" || GamesCard.Rules.ContainsKey(i["rule"]))).ToList();

        private static int Num(Dictionary<string, string> i, string key, int fallback) => int.TryParse(i.GetValueOrDefault(key), out int n) ? n : fallback;

        /// <summary>This week's board: three quests from different neighbors, posted the first time the week is seen.</summary>
        public static Board BoardFor(Store s, DateTime now)
        {
            var b = s.World.Get<Board>(QuestId);
            string week = GamesFunKit.Key(Engine.WeekStart(now));
            if (b.Week == week) return b;
            // The posted quests are kept apart from Control Z: an undo of an earlier change brings back the same board.
            var made = s.World.GetKept<Board>(QuestId + ".made");
            if (made.Week == week)
            {
                b = new Board { Week = week, Quests = made.Quests.Select(q => new Quest { Index = q.Index }).ToList() };
                GamesFunKit.Put(s, QuestId, b);
                return b;
            }
            var items = QuestItems();
            var picks = new List<int>();
            foreach (int i in Enumerable.Range(0, items.Count).OrderBy(_ => GamesFunKit.Rng.Next()))
            {
                if (picks.Count >= QuestsAWeek) break;
                if (picks.Any(p => items[p]["who"] == items[i]["who"])) continue;
                picks.Add(i);
            }
            b = new Board { Week = week, Quests = picks.Select(i => new Quest { Index = i }).ToList() };
            s.World.SetKept(QuestId + ".made", new Board { Week = week, Quests = picks.Select(i => new Quest { Index = i }).ToList() });
            GamesFunKit.Put(s, QuestId, b);
            return b;
        }

        public static string QuestLine(Store s, Quest q)
        {
            var items = QuestItems();
            if (q.Index >= items.Count) return "A quest note too smudged to read.";
            var i = items[q.Index];
            int target = Num(i, "target", 1), have = i["rule"] == "sprint" ? q.Sprints : q.Tasks.Count;
            return i["who"] + ": " + i["text"] + (q.Paid ? " Done, thank you!" : " " + Math.Min(have, target) + " of " + target + ", pays " + Num(i, "reward", 10) + " points.");
        }

        private static string BoardLine(Store s, DateTime now)
        {
            var b = BoardFor(s, now);
            return GamesFunKit.Plural(b.Quests.Count, "quest") + " posted this week, " + b.Quests.Count(q => q.Paid) + " done";
        }

        public static List<(Outcome? O, string Line)> QuestDone(Store s, TaskItem t, DateTime now)
        {
            var said = new List<(Outcome?, string)>();
            var b = BoardFor(s, now);
            var items = QuestItems();
            foreach (var q in b.Quests.Where(q => !q.Paid && q.Index < items.Count))
            {
                var i = items[q.Index];
                if (i["rule"] == "sprint" || q.Tasks.Contains(t.Id) || !GamesCard.Rules[i["rule"]](t, now, s)) continue;
                q.Tasks.Add(t.Id);
                if (q.Tasks.Count < Num(i, "target", 1)) { said.Add((null, i["who"] + "'s quest: " + q.Tasks.Count + " of " + Num(i, "target", 1) + ".")); continue; }
                q.Paid = true;
                q.PaidBy = t.Id;
                var o = Ledger.Pay(s, Num(i, "reward", 10), i["who"] + "'s village quest", QuestId, t.Id);
                said.Add((o, Celebration.Line(s, "Quest done for " + i["who"] + ": " + i["text"] + " " + Num(i, "reward", 10) + " points. " + (i.GetValueOrDefault("thanks") ?? ""))));
            }
            GamesFunKit.Put(s, QuestId, b);
            return said;
        }

        public static void QuestUndone(Store s, TaskItem t)
        {
            var b = s.World.Get<Board>(QuestId);
            var items = QuestItems();
            bool changed = false;
            var days = new HashSet<DateTime>();
            foreach (var q in b.Quests)
            {
                changed |= q.Tasks.Remove(t.Id);
                if (q.PaidBy == t.Id) { q.Paid = false; q.PaidBy = null; changed = true; continue; }
                // An earlier task of a finished quest went back: the quest is short again, and its reward goes (re-paid at the next finish that counts).
                if (!q.Paid || q.PaidBy is not Guid by || q.Index >= items.Count || items[q.Index]["rule"] == "sprint" || q.Tasks.Count >= Num(items[q.Index], "target", 1)) continue;
                string reason = items[q.Index]["who"] + "'s village quest";
                foreach (var e in s.Data.Log.Where(e => e.Kind == Ledger.FunKind && e.Feature == QuestId && e.TaskId == by && e.Text == reason).ToList()) { days.Add(e.When.Date); s.Data.Log.Remove(e); }
                q.Paid = false; q.PaidBy = null; changed = true;
            }
            if (days.Count > 0) Engine.RecheckGoals(s, days, DateTime.Now);
            if (changed) { s.World.Set(QuestId, b); s.World.Save(); }
        }

        /// <summary>A finished sprint counts toward sprint quests; a finished one pays (kept in What happened today).</summary>
        public static List<string> QuestSprint(Store s, DateTime now)
        {
            var said = new List<string>();
            var b = BoardFor(s, now);
            var items = QuestItems();
            foreach (var q in b.Quests.Where(q => !q.Paid && q.Index < items.Count && items[q.Index]["rule"] == "sprint"))
            {
                var i = items[q.Index];
                q.Sprints++;
                if (q.Sprints < Num(i, "target", 1)) continue;
                q.Paid = true;
                Ledger.Pay(s, Num(i, "reward", 10), i["who"] + "'s village quest", QuestId);
                said.Add("Quest done for " + i["who"] + ": " + i["text"] + " " + Num(i, "reward", 10) + " points.");
            }
            GamesFunKit.Put(s, QuestId, b);
            return said;
        }

        private void ShowQuests(Window owner)
        {
            var b = BoardFor(S, DateTime.Now);
            var lines = new List<string> { "The village board. " + BoardLine(S, DateTime.Now) + ". New quests go up every Monday; nothing is lost if one waits." };
            lines.AddRange(b.Quests.Select(q => QuestLine(S, q)));
            GamesFunKit.Read(owner, "Village quests", lines);
        }

        // ---- Snail racing ----

        public const int Track = 10;

        /// <summary>One moment of a race: every snail creeps 0 to 2; hers gets one more when she cheers. Returns the winner, or null.</summary>
        public static int? Step(int[] at, int hers, bool cheer, Random rng)
        {
            for (int k = 0; k < at.Length; k++) at[k] += rng.Next(0, 3) + (k == hers && cheer && rng.Next(2) == 0 ? 1 : 0);
            int best = at.Max();
            if (best < Track) return null;
            var leaders = Enumerable.Range(0, at.Length).Where(k => at[k] == best).ToList();
            return leaders.Contains(hers) ? hers : leaders[0];
        }

        private void Snails(Window owner)
        {
            var names = Content.Lines("games.snail.names").OrderBy(_ => GamesFunKit.Rng.Next()).Take(2).Prepend("Gerald").ToList();
            int hers = GamesFunKit.Pick(owner, "Snail racing", "_Cheer for", names, 0, "Three snails at the starting leaf. Pick the one you cheer for.");
            if (hers < 0) return;
            var at = new int[names.Count];
            bool over = false;
            bool weekend = DateTime.Now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            string? Moment(bool cheer)
            {
                if (over) return "The race is over. Close and come back for another one.";
                int? won = Step(at, hers, cheer, GamesFunKit.Rng);
                string where = string.Join(", ", names.Select((n, k) => n + " " + Math.Min(Track, at[k])));
                if (won == null) return (cheer ? Content.Pick(S, "games.snail") + " " : "") + where + ", out of " + Track + ".";
                over = true;
                if (won != hers) return names[won.Value] + " wins by a slime trail! " + names[hers] + " says it was a warm-up. " + where + ".";
                string line = names[hers] + " wins! " + where + ".";
                var p = S.World.Get<Prizes>(SnailId);
                string weekendKey = GamesFunKit.Key(Engine.WeekStart(DateTime.Now));
                if (weekend && p.SnailWeekend != weekendKey)
                {
                    S.Remember("a snail race prize");
                    p.SnailWeekend = weekendKey;
                    GamesFunKit.Put(S, SnailId, p);
                    Ledger.Pay(S, SnailPrize, "Snail race win", SnailId);
                    _w.SaveAndRefresh();
                    line += " Weekend race prize: " + SnailPrize + " points. " + PlayTime.SpendLine(S.Data) + ".";
                }
                else if (!weekend) line += " Races with prizes are on the weekend; this was a practice lap.";
                Celebration.Sound(S, "Bunny, binky");
                return Celebration.Line(S, line);
            }
            GamesFunKit.Play(owner, "Snail racing", "You cheer for " + names[hers] + ". Press Cheer to shout, or Watch to let them creep. Cheering helps, slowly.",
                new[] { new GamesFunKit.Move("_Cheer", () => Moment(true)), new GamesFunKit.Move("_Watch", () => Moment(false)) });
        }

        // ---- Mini golf ----

        public static List<Dictionary<string, string>> Holes() => Content.Items("games.golf").Where(i => i.ContainsKey("name")).ToList();
        public static int OpenHoles(Data d, int count) => Math.Min(count, 3 + d.Game.WeekGoalsMet);

        /// <summary>
        /// One putt: soft rolls about 2, medium 4, firm 8, give or take one; now and then a firm putt catches a lucky
        /// bounce and rolls 3 farther. Holes are 11 steps or longer, so a hole in one takes that bounce: rare, not free.
        /// Returns the distance left (within 1 is in the cup).
        /// </summary>
        public static int Putt(int left, int power, Random rng)
        {
            int p = Math.Clamp(power, 0, 2);
            int roll = new[] { 2, 4, 8 }[p] + rng.Next(-1, 2) + (p == 2 && rng.Next(10) == 0 ? 3 : 0);
            int after = Math.Abs(left - roll);
            return after <= 1 ? 0 : after;
        }

        private void Golf(Window owner)
        {
            var holes = Holes();
            int open = OpenHoles(S.Data, holes.Count);
            var p = S.World.Get<Prizes>(GolfId);
            var choices = holes.Take(open).Select((h, k) => "Hole " + (k + 1) + ", " + h["name"] + ", par " + Num(h, "par", 3) + (p.GolfBest.TryGetValue(h["name"], out int b) ? ", your best " + b : "")).ToList();
            int pick = GamesFunKit.Pick(owner, "Mini golf", "_Hole", choices, 0,
                GamesFunKit.Plural(open, "hole") + " open of " + holes.Count + ". Each weekly goal you meet opens another.");
            if (pick < 0) return;
            var h = holes[pick];
            int left = Num(h, "length", 8), strokes = 0, par = Num(h, "par", 3);
            bool done = false;
            string? Shot(int power)
            {
                if (done) return "You are in the cup. Close to pick another hole.";
                strokes++;
                left = Putt(left, power, GamesFunKit.Rng);
                if (left > 0) return Content.Pick(S, "games.golf.shots") + " " + GamesFunKit.Plural(left, "step") + " to the cup.";
                done = true;
                var pr = S.World.Get<Prizes>(GolfId);
                string score = strokes == 1 ? "Hole in one!" : strokes < par ? GamesFunKit.Plural(strokes, "stroke") + ", under par!" : strokes == par ? GamesFunKit.Plural(strokes, "stroke") + ", right on par." : GamesFunKit.Plural(strokes, "stroke") + ". Par is " + par + ", and the windmill is smug.";
                bool best = !pr.GolfBest.TryGetValue(h["name"], out int old) || strokes < old;
                string line = "Plunk, it drops in the cup. " + score + (best ? " Your best on this hole." : "");
                if (strokes == 1 && pr.AceDay != S.Key(DateTime.Now))
                {
                    S.Remember("a hole in one");
                    pr.AceDay = S.Key(DateTime.Now);
                    if (best) pr.GolfBest[h["name"]] = strokes;
                    GamesFunKit.Put(S, GolfId, pr);
                    Ledger.Pay(S, AcePrize, "Hole in one", GolfId);
                    _w.SaveAndRefresh();
                    line += " " + AcePrize + " points. " + PlayTime.SpendLine(S.Data) + ".";
                }
                else if (best) { pr.GolfBest[h["name"]] = strokes; GamesFunKit.Put(S, GolfId, pr); }
                if (strokes <= par) Celebration.Sound(S, "Singing bells, sparkle");
                return strokes <= par ? Celebration.Line(S, line) : line;
            }
            string intro = h["name"] + ". " + string.Join(" ", new[] { "touch", "sound", "looks" }.Where(h.ContainsKey).Select(k => h[k])) + " The cup is " + left + " steps away. Pick how hard to putt.";
            GamesFunKit.Play(owner, "Mini golf", intro, new[] { new GamesFunKit.Move("_Soft putt", () => Shot(0)), new GamesFunKit.Move("_Medium putt", () => Shot(1)), new GamesFunKit.Move("_Firm putt", () => Shot(2)) });
        }

        // ---- Bowling night ----

        /// <summary>Ten-pin scoring for a list of rolls (pins each), the usual way: strikes and spares add the next balls.</summary>
        public static int BowlScore(IReadOnlyList<int> rolls)
        {
            int score = 0, i = 0;
            for (int frame = 0; frame < 10 && i < rolls.Count; frame++)
            {
                int At(int k) => k < rolls.Count ? rolls[k] : 0;
                if (At(i) == 10) { score += 10 + At(i + 1) + At(i + 2); i += 1; }
                else if (At(i) + At(i + 1) == 10) { score += 10 + At(i + 2); i += 2; }
                else { score += At(i) + At(i + 1); i += 2; }
            }
            return score;
        }

        /// <summary>The balls of one frame: a strike when she has one banked from a finished task, otherwise two honest rolls.</summary>
        public static List<int> Frame(bool strike, Random rng)
        {
            if (strike) return new List<int> { 10 };
            int first = rng.Next(3, 10);
            return new List<int> { first, rng.Next(0, 10 - first + 1) };
        }

        private void Bowling(Window owner)
        {
            var now = DateTime.Now;
            string month = now.ToString("yyyy-MM");
            int banked = Math.Min(12, S.Data.Tasks.Count(t => GamesFunKit.Finished(t) && t.DoneAt is DateTime d && d.Year == now.Year && d.Month == now.Month));
            var rival = Content.Lines("games.snail.names").Concat(new[] { "Gerald" }).OrderBy(_ => GamesFunKit.Rng.Next()).First();
            int rivalScore = GamesFunKit.Rng.Next(90, 181);
            var rolls = new List<int>();
            int frame = 0;
            string? Bowl()
            {
                if (frame >= 10) return "That was the tenth frame. Close the lanes and come back next time.";
                frame++;
                bool strike = banked > 0;
                if (strike) banked--;
                var balls = Frame(strike, GamesFunKit.Rng);
                // The tenth frame's strikes and spares get their bonus balls, from the bank first.
                if (frame == 10 && (balls[0] == 10 || balls.Sum() == 10))
                    for (int k = 0; k < (balls[0] == 10 ? 2 : 1); k++) { bool bs = banked > 0; if (bs) banked--; balls.Add(bs ? 10 : GamesFunKit.Rng.Next(0, 11)); }
                rolls.AddRange(balls);
                string what = balls[0] == 10 ? "Strike! One of your finished tasks rolls right down the middle." : balls.Take(2).Sum() == 10 ? "Spare. The last pin wobbles and goes down." : balls[0] + " then " + balls[1] + ". " + Content.Pick(S, "games.bowling");
                string line = "Frame " + frame + ": " + what + " Score " + BowlScore(rolls) + ".";
                if (frame < 10) return line;
                int mine = BowlScore(rolls);
                line += " Final score " + mine + ". " + rival + " bowled " + rivalScore + ". " + (mine > rivalScore ? "You win the night!" : "Good game. " + rival + " buys the fries.");
                var p = S.World.Get<Prizes>(BowlId);
                if (p.BowlMonth != month)
                {
                    int pay = Math.Clamp(mine / 10, 1, BowlMax);
                    S.Remember("bowling night");
                    p.BowlMonth = month;
                    p.BowlBest = Math.Max(p.BowlBest, mine);
                    GamesFunKit.Put(S, BowlId, p);
                    Ledger.Pay(S, pay, "Bowling night", BowlId);
                    _w.SaveAndRefresh();
                    line += " This month's bowling night pays " + pay + " points. " + PlayTime.SpendLine(S.Data) + ".";
                }
                else if (mine > p.BowlBest) { p.BowlBest = mine; GamesFunKit.Put(S, BowlId, p); line += " Your best game yet."; }
                Celebration.Sound(S, "Pizzicato strings, four up");
                return Celebration.Line(S, line);
            }
            GamesFunKit.Play(owner, "Bowling night", "Bowling night with " + rival + ". You have " + GamesFunKit.Plural(banked, "strike") + " banked from tasks finished this month. The first game each month pays points.",
                new[] { new GamesFunKit.Move("_Bowl the next frame", Bowl) });
        }

        // ---- Scavenger hunt ----

        public sealed record Spot(string Id, string Name, List<string> Clues);

        /// <summary>Where things can hide: the rooms she has built, and a few spots every burrow has.</summary>
        public static List<Spot> Spots(Store s)
        {
            var clues = Content.Items("games.hunt.clues").Where(i => i.ContainsKey("spot") && i.ContainsKey("clue")).ToList();
            List<string> For(string id) => clues.Where(c => c["spot"] == id).Select(c => c["clue"]).ToList();
            var spots = PlayTime.Built(s.Data.Play).Select(r => new Spot(r.Id, r.Name, For(r.Id))).Where(x => x.Clues.Count > 0).ToList();
            foreach (var c in clues.Where(c => c.ContainsKey("name")).GroupBy(c => c["spot"]))
                if (!spots.Any(x => x.Id == c.Key)) spots.Add(new Spot(c.Key, c.First()["name"], For(c.Key)));
            return spots;
        }

        public static Hunt HuntFor(Store s, DateTime now)
        {
            // Kept apart from Control Z: an undo of an earlier change never hides a new set of things mid-hunt.
            var h = s.World.GetKept<Hunt>(HuntId);
            if (h.Day == s.Key(now)) return h;
            var spots = Spots(s).OrderBy(_ => GamesFunKit.Rng.Next()).Take(3).ToList();
            var items = Enumerable.Range(0, Content.Items("games.hunt.items").Count).OrderBy(_ => GamesFunKit.Rng.Next()).Take(spots.Count).ToList();
            h = new Hunt { Day = s.Key(now), Things = spots.Select((sp, k) => new Hidden { Item = items[k], Spot = sp.Id }).ToList() };
            s.World.SetKept(HuntId, h); s.World.Save();
            return h;
        }

        /// <summary>Searches a spot for the thing she is looking for now. Returns what she finds, and whether the hunt is complete.</summary>
        public static (string Line, bool Complete) Search(Store s, string spotId, DateTime now)
        {
            var h = HuntFor(s, now);
            var next = h.Things.FirstOrDefault(x => !x.Found);
            if (next == null) return ("You found everything today. A new hunt starts tomorrow.", true);
            var items = Content.Items("games.hunt.items");
            if (next.Spot != spotId) return ("Not here. " + Content.Pick(s, "games.hunt.miss"), false);
            next.Found = true;
            s.World.SetKept(HuntId, h); s.World.Save();
            var it = items[Math.Min(next.Item, items.Count - 1)];
            string found = "Found it: " + it.GetValueOrDefault("name") + ". " + string.Join(" ", new[] { "touch", "sound", "looks" }.Where(it.ContainsKey).Select(k => it[k]));
            return (found, h.Things.All(x => x.Found));
        }

        private void ScavengerHunt(Window owner)
        {
            // What the last search found goes at the start of the next window's intro, so its focus speech never cuts it.
            string? last = null;
            while (true)
            {
                var now = DateTime.Now;
                var h = HuntFor(S, now);
                var spots = Spots(S);
                var next = h.Things.FirstOrDefault(x => !x.Found);
                if (next == null) { _w.Say((last != null ? last + " " : "") + "You found all " + h.Things.Count + " things today. Bunny is hiding new ones tomorrow.", true); return; }
                var spot = spots.FirstOrDefault(x => x.Id == next.Spot);
                string clue = spot == null ? "It is somewhere in the burrow." : spot.Clues[h.Things.IndexOf(next) % spot.Clues.Count];
                int i = GamesFunKit.Pick(owner, "Scavenger hunt", "_Search", spots.Select(x => "Search " + x.Name).ToList(), 0,
                    (last != null ? last + " " : "") + "Thing " + (h.Things.IndexOf(next) + 1) + " of " + h.Things.Count + ". Clue: " + clue + " Pick where to search. Escape stops; the hunt waits for you.");
                if (i < 0) return;
                var (line, complete) = Search(S, spots[i].Id, now);
                if (complete && S.World.Get<Prizes>(HuntId) is Prizes p && p.HuntDay != S.Key(now))
                {
                    S.Remember("the scavenger hunt");
                    p.HuntDay = S.Key(now);
                    GamesFunKit.Put(S, HuntId, p);
                    Ledger.Pay(S, HuntPrize, "Scavenger hunt", HuntId);
                    _w.SaveAndRefresh();
                    Celebration.Sound(S, "SurpriseReward");
                    _w.Say(Celebration.Line(S, line + " That was the last one! " + HuntPrize + " points. " + PlayTime.SpendLine(S.Data) + "."), true);
                    return;
                }
                last = line;
            }
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("village games: " + what); }
            Need(QuestItems().Count >= 20, "fewer than 20 village quests the game can check");
            foreach (var topic in new[] { "games.snail", "games.golf.shots", "games.bowling", "games.hunt.miss" }) Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            Need(Holes().Count >= 9 && Holes().All(h => h.ContainsKey("touch") && h.ContainsKey("sound")), "mini golf holes need a touch and a sound");
            Need(Content.Items("games.hunt.items").Count >= 20 && Content.Items("games.hunt.items").All(h => h.ContainsKey("touch")), "scavenger hunt things need a touch description");
            // One village: these games live inside Stage 7's village, and each shows only while its tool is on.
            Need(World.Places.Find(PlaceId) == null, "a second village board place is still in Bunny's world");
            var lines = Things(store).Select(x => x.Name).ToList();
            Need(lines.Contains("Snail racing") == Tools.IsOn(SnailId) && lines.Contains("Mini golf") == Tools.IsOn(GolfId) && lines.Contains("Bowling night") == Tools.IsOn(BowlId)
                && lines.Contains("The quest board") == Tools.IsOn(QuestId), "the village shows a game that is turned off, or hides one that is on");
            // A hole in one is rare: no hole is short enough for a plain firm putt to drop.
            Need(Holes().All(h => Num(h, "length", 8) >= 11), "a mini golf hole is short enough for an easy hole in one");
            var golfRng = new Random(4);
            int aces = Enumerable.Range(0, 1000).Count(_ => Putt(11, 2, golfRng) == 0);
            Need(aces > 0 && aces < 200, "holes in one came " + aces + " times in 1000 firm putts");
            // Bowling scores the usual way.
            Need(BowlScore(Enumerable.Repeat(10, 12).ToList()) == 300, "a perfect game is not 300");
            Need(BowlScore(Enumerable.Repeat(5, 21).ToList()) == 150, "all spares of 5 is not 150");
            Need(BowlScore(Enumerable.Repeat(0, 20).ToList()) == 0, "a gutter game");
            // Snails always finish; golf always reaches the cup.
            var rng = new Random(9);
            var at = new int[3];
            int? won = null;
            for (int k = 0; k < 100 && won == null; k++) won = Step(at, 0, true, rng);
            Need(won != null, "a snail race never ended");
            int left = 9, strokes = 0;
            while (left > 0 && strokes < 50) { left = Putt(left, left > 6 ? 2 : left > 3 ? 1 : 0, rng); strokes++; }
            Need(left == 0, "a golf hole could not be finished");
            Need(OpenHoles(new Data(), 18) == 3, "three holes open at first");
            // Quests: a finish counts once, pays at the target, and un-finishing takes it back.
            var b = BoardFor(store, DateTime.Now);
            Need(b.Quests.Count == QuestsAWeek && b.Quests.Select(q => QuestItems()[q.Index]["who"]).Distinct().Count() == QuestsAWeek, "three quests from three neighbors");
            int any = QuestItems().FindIndex(i => i["rule"] == "any");
            Need(any >= 0, "no quest for finishing any task");
            b.Quests = new List<Quest> { new() { Index = any } };
            store.World.Set(QuestId, b);
            int target = Num(QuestItems()[any], "target", 1), reward = Num(QuestItems()[any], "reward", 10);
            var tasks = Enumerable.Range(0, target).Select(k => new TaskItem { Name = "quest " + k, List = store.Data.Lists[0].Name, Status = Status.Done, DoneAt = DateTime.Now }).ToList();
            store.Data.Tasks.AddRange(tasks);
            int before = Engine.Total(store.Data);
            foreach (var t in tasks) { QuestDone(store, t, DateTime.Now); QuestDone(store, t, DateTime.Now); }
            Need(Engine.Total(store.Data) == before + reward && store.World.Get<Board>(QuestId).Quests[0].Paid, "a quest did not pay once at its target");
            var lastTask = tasks[^1];
            lastTask.Status = Status.Today;
            Engine.Unfinish(store, lastTask, DateTime.Now);
            QuestUndone(store, lastTask);
            Need(Engine.Total(store.Data) == before && !store.World.Get<Board>(QuestId).Quests[0].Paid, "un-finishing kept the quest reward");
            if (target > 1)
            {
                // Paid by the last task, then an earlier one goes back: the quest is short again and its reward goes.
                lastTask.Status = Status.Done;
                QuestDone(store, lastTask, DateTime.Now);
                Need(Engine.Total(store.Data) == before + reward, "the quest did not pay again when finished again");
                var firstTask = tasks[0];
                firstTask.Status = Status.Today;
                Engine.Unfinish(store, firstTask, DateTime.Now);
                QuestUndone(store, firstTask);
                Need(Engine.Total(store.Data) == before && !store.World.Get<Board>(QuestId).Quests[0].Paid, "un-finishing an earlier task kept a quest that is short again");
            }
            foreach (var t in tasks) store.Data.Tasks.Remove(t);
            // The posted board survives an undo that puts back an older bag.
            store.World.Set(QuestId, new Board());
            store.World.SetKept(QuestId + ".made", new Board());
            var posted = BoardFor(store, DateTime.Now).Quests.Select(q => q.Index).ToList();
            store.World.Set(QuestId, new Board());
            Need(BoardFor(store, DateTime.Now).Quests.Select(q => q.Index).SequenceEqual(posted), "losing the bag posted a new village board");
            // The hunt: three spots even in a new burrow, and searching the right one finds it.
            var h = HuntFor(store, DateTime.Now);
            Need(h.Things.Count == 3, "the hunt hid " + h.Things.Count + " things");
            foreach (var x in h.Things.ToList()) Search(store, x.Spot, DateTime.Now);
            Need(store.World.GetKept<Hunt>(HuntId).Things.All(x => x.Found), "searching the right spots did not find everything");
        }
    }
}
