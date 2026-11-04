using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>What the adventures remember (Morgan's issue 17). Spending never lowers her level: levels come from points earned.</summary>
    public sealed class PlayState
    {
        /// <summary>Points spent on burrow rooms and bets.</summary>
        public int Spent { get; set; }
        /// <summary>Rooms built, by id. The front tunnel is always there.</summary>
        public List<string> Rooms { get; set; } = new();
        public List<Bet> Bets { get; set; } = new();
        /// <summary>Sound packs collected.</summary>
        public List<string> Packs { get; set; } = new();

        // Vex, the rival.
        public string RivalWeek { get; set; } = "";
        /// <summary>Points Vex won from lost bets in RivalWeek (older saves; new losses go in RivalExtraByWeek).</summary>
        public int RivalExtra { get; set; }
        /// <summary>Points Vex won from lost bets, by the week each bet's deadline was in.</summary>
        public Dictionary<string, int> RivalExtraByWeek { get; set; } = new();
        public string RivalSettled { get; set; } = "";
        public string RivalPassed { get; set; } = "";
        public int RivalWins { get; set; }
        public int RivalLosses { get; set; }
        public DateTime RivalLastTaunt { get; set; }
        public string RivalLetter { get; set; } = "";

        // This week's quest.
        public string QuestWeek { get; set; } = "";
        public string QuestName { get; set; } = "";
        public List<Guid> QuestTasks { get; set; } = new();
        public int QuestCleared { get; set; }
        public bool QuestDone { get; set; }

        // The mystery box.
        public Guid? MysteryTask { get; set; }
        public string MysteryDay { get; set; } = "";
    }

    public sealed class Bet
    {
        public Guid TaskId { get; set; }
        public string Name { get; set; } = "";
        public int Stake { get; set; }
        public DateTime By { get; set; }
        /// <summary>When the task was finished for a won bet. Kept so a won bet stays won after its deadline.</summary>
        public DateTime? FinishedAt { get; set; }
        /// <summary>Open, Won, Lost, or Cancelled (its task was deleted or archived, and the stake came back).</summary>
        public string State { get; set; } = "Open";
    }

    public sealed record Room(string Id, string Name, int Cost, string Feel, string Sound, string? Pack = null);

    /// <summary>
    /// Fun, Adventures (Morgan's issue 17): the burrow she builds with points, Vex the rival bunny, bets,
    /// mystery boxes, quest weeks, the dramatic narrator and collectible sound packs.
    /// </summary>
    public sealed class PlayTime : IFeature
    {
        private MainWindow _w = null!;
        private Store S => _w.Store;
        private Settings Cfg => _w.AppSettings;
        private PlayState P => _w.Store.Data.Play;
        private readonly Random _rng = new();
        private MenuItem? _rivalMenu, _narratorMenu;

        public const string Rival = "Vex";
        public const string MysteryLead = "Mystery box cleared: ", QuestLead = "Quest cleared: ";

        // ---- Points to spend ----

        public static int Wallet(Data d) => Math.Max(0, Engine.Total(d) - d.Play.Spent);

        // ---- The burrow ----

        public static readonly Room[] Rooms =
        {
            new("tunnel", "The front tunnel", 0, "Cool packed earth under your paws, roots brushing the ceiling, and the smell of rain drifting in from outside.", "Bunny, hop"),
            new("nook", "The mossy reading nook", 60, "Moss as thick as a carpet, a heap of squashy cushions, a little lamp humming warm, and pages rustling when you turn over.", "Soft piano, sigh"),
            new("pantry", "The carrot pantry", 80, "Shelves lined with crunchy carrots and bundles of clover. It smells green and sweet, and every shelf crunches when you reach in.", "Bunny, nibble"),
            new("tea", "The tea corner", 100, "A round wooden table, a kettle starting to sing, and the clink of two mugs, one for you and one for whoever visits.", "Coffee, two clinks"),
            new("fireplace", "The crackling fireplace", 120, "Warm smooth stones, a fire that crackles and pops, and a woolly blanket that smells faintly of cedar.", "Stove, light", "Fireside"),
            new("bath", "The bubble bath grotto", 120, "Warm water dripping from the rock, bubbles popping against your fur, and an echo that makes humming sound amazing.", "Singing bells, bounce"),
            new("garden", "The clover garden", 140, "Cool damp clover under a skylight, bees buzzing lazily, and a flute somewhere playing one long note.", "Flute, single"),
            new("music", "The music room", 160, "An old upright piano with one sticky key, a xylophone with felt mallets, and a cello leaning in the corner.", "Piano, four up", "Music room"),
            new("observatory", "The star window", 200, "A round window in the hill, a telescope cold to the touch, and the hum of the night sky.", "Space, orbit", "Starlight"),
            new("library", "The grand library", 250, "Tall shelves you can feel the spines of, a rolling ladder that squeaks, and the hush of a room full of books.", "Organ, two up", "Library hush"),
            new("throne", "The Burrow Boss throne room", 400, "Velvet walls, a tiny crown on a cushion, and strings that swell every time you sit down.", "Strings, chord high"),
        };

        public static IEnumerable<Room> Built(PlayState p) => Rooms.Where(r => r.Cost == 0 || p.Rooms.Contains(r.Id));

        /// <summary>Builds a room if she has the points. Returns what to say.</summary>
        public static string Build(Store s, Room r)
        {
            var p = s.Data.Play;
            if (Built(p).Contains(r)) return r.Name + " is already built.";
            int have = Wallet(s.Data);
            if (have < r.Cost) return r.Name + " costs " + r.Cost + " points. You have " + have + ". " + (r.Cost - have) + " more to go.";
            s.Remember("building " + r.Name);
            p.Spent += r.Cost;
            p.Rooms.Add(r.Id);
            string said = "Built " + r.Name + ". " + r.Feel;
            if (r.Pack != null && !p.Packs.Contains(r.Pack)) { p.Packs.Add(r.Pack); said += " It came with a new sound pack: " + r.Pack + ". Pick it in Options, Sounds."; }
            return said;
        }

        // ---- Collectible sound packs ----

        public static readonly (string Pack, int Streak)[] StreakPacks = { ("Pizzicato party", 3), ("Flute meadow", 7), ("Organ hall", 14), ("Cello cavern", 21) };
        public static IEnumerable<string> Collectible => StreakPacks.Select(x => x.Pack).Concat(Rooms.Where(r => r.Pack != null).Select(r => r.Pack!));

        /// <summary>Packs earned by streaks that she does not have yet; adds them.</summary>
        public static List<string> CollectStreakPacks(Data d, DateTime now)
        {
            int best = Math.Max(d.Game.LongestStreak, Engine.Streaks(d, now).Current);
            var fresh = StreakPacks.Where(x => best >= x.Streak && !d.Play.Packs.Contains(x.Pack)).Select(x => x.Pack).ToList();
            d.Play.Packs.AddRange(fresh);
            return fresh;
        }

        // ---- Vex ----

        public static string WeekKey(DateTime d) => Engine.WeekStart(d).ToString("yyyy-MM-dd");

        /// <summary>Vex's points for the week holding day, up to now: 90 percent of her weekly goal, spread over the days, resting when she rests.</summary>
        public static int RivalScore(Data d, DateTime now, DateTime? weekOf = null)
        {
            var start = Engine.WeekStart(weekOf ?? now);
            double perDay = Engine.WeeklyGoalFor(d) * 0.9 / 7;
            double total = 0;
            for (var day = start; day < start.AddDays(7) && day <= now.Date; day = day.AddDays(1))
            {
                if (d.Days.TryGetValue(day.ToString("yyyy-MM-dd"), out var i) && i.Mode is DayMode.Vacation or DayMode.RoughDay) continue;
                double share = day < now.Date ? 1 : Math.Clamp((now.Hour + now.Minute / 60.0 - 8) / 14, 0, 1);
                total += perDay * share;
            }
            string key = start.ToString("yyyy-MM-dd");
            int extra = (d.Play.RivalWeek == key ? d.Play.RivalExtra : 0) + d.Play.RivalExtraByWeek.GetValueOrDefault(key);
            return (int)Math.Round(total) + extra;
        }

        private static readonly string[] Taunts =
        {
            "Oh, you did {task}? Cute. I did three before breakfast.",
            "{task}, done. Adorable. I finished my whole week's reading while you were picking a font.",
            "Nice. Truly. I'd clap, but my paws are busy finishing things.",
            "{task}? I did that in my sleep. Literally. I have a dream journal about it.",
            "Look at you, being productive. Does it hurt? It looks like it hurts.",
            "One task. Wow. Should I alert the press, or just the fan club of one?",
            "I see you finished {task}. I also see the {left} things you didn't. Just saying.",
            "That was fine. Not great. Fine. I'll allow it.",
        };
        private static readonly string[] Grudges =
        {
            "Ugh. You're ahead of me this week. Enjoy it. It won't last.",
            "Fine. FINE. You're winning. I'm only letting you because I'm generous.",
            "You passed me. I'm not upset. I'm writing about it in my diary, which is different.",
        };
        private static readonly string[] Gloats =
        {
            "Time's up on {task}. Your {stake} points are mine now. They look better on me.",
            "You bet {stake} points on {task}, and lost. I'm framing them.",
            "{task} didn't happen in time, so thanks for the {stake} points. Very generous of you.",
        };

        public static string Fill(string line, string task = "", int left = 0, int stake = 0) =>
            line.Replace("{task}", task).Replace("{left}", left.ToString()).Replace("{stake}", stake.ToString());

        public static string DefeatLetter(string name, int hers, int vex) =>
            "Dear " + name + ", I am writing this under protest. Last week you earned " + hers + " points and I earned " + vex + ". " +
            "That means, and I cannot believe I have to put this in writing, you won. You beat me. I have checked the math four times. " +
            "I will be back this week, stronger, smugger and better rested. Do not get used to this. Grudgingly yours, " + Rival + ".";

        // ---- Quests ----

        private static readonly string[] QuestStory =
        {
            "Your bunny packs a carrot sandwich and sets off toward {place}.",
            "The path gets steep. Your bunny finds a good walking stick and keeps going.",
            "Halfway up {place}, a fox asks for a riddle. Your bunny answers with a deadline, and the fox runs away.",
            "A storm rolls in. Your bunny sheltered under a mushroom and did one more thing anyway.",
            "The trail opens onto a meadow of clover. Snack break. Earned.",
            "A rickety rope bridge sways over the valley. Your bunny does not look down.",
            "The top of {place} is in sight, glowing a little, like a finished to-do list.",
            "One more ridge. Your bunny can smell victory, and also carrots.",
        };

        /// <summary>A name for a big week, from what is due in it.</summary>
        public static string QuestNameFor(List<TaskItem> tasks)
        {
            bool Has(params string[] w) => tasks.Any(t => w.Any(x => t.Name.Contains(x, StringComparison.OrdinalIgnoreCase)));
            if (tasks.Any(t => t.Type == TaskType.Test) || Has("exam", "midterm", "quiz", "test")) return Has("final") ? "Finals Fortress" : "Midterm Mountain";
            if (Has("paper", "essay", "report", "lit review")) return "Paper Peaks";
            if (Has("read", "chapter")) return "The Reading Rapids";
            if (tasks.Count(t => t.Type == TaskType.Assignment) >= 3) return "Homework Highlands";
            return "Deadline Desert";
        }

        /// <summary>Starts this week's quest when four or more things are due in it. Returns true when a new one began.</summary>
        public static bool StartQuest(Store s, DateTime now)
        {
            var p = s.Data.Play;
            string week = WeekKey(now);
            if (p.QuestWeek == week) return false;
            var start = Engine.WeekStart(now);
            var due = s.Data.Tasks.Where(t => t.Due is DateTime d && d >= start && d < start.AddDays(7) && !s.ListOf(t).Archived
                && (t.Status != Status.Done || t.DoneAt >= start)).OrderBy(t => t.Due).ToList();
            p.QuestWeek = week;
            p.QuestDone = false;
            p.QuestCleared = 0;
            if (due.Count < 4) { p.QuestName = ""; p.QuestTasks = new(); return false; }
            p.QuestName = QuestNameFor(due);
            p.QuestTasks = due.Select(t => t.Id).ToList();
            return true;
        }

        public static bool Cleared(TaskItem? t) => t != null && t.Status is Status.Done or Status.DoneNotSubmitted;

        public static string StoryBeat(int chapter, string place) => QuestStory[(chapter - 1) % QuestStory.Length].Replace("{place}", place);

        // ---- The narrator ----

        public static bool Big(TaskItem t) => t.Size is Size.Large or Size.MultiDay || t.Type == TaskType.Test || t.GradeWeight >= 15;

        public static string Trailer(TaskItem t, DateTime now, string last, int pick)
        {
            string when = t.Due is DateTime d ? Words.Day(d, now) : "someday";
            int early = t.Due is DateTime dd ? (dd.Date - now.Date).Days : 0;
            string finish = t.Due == null ? "with no deadline at all, purely out of spite"
                : early > 1 ? early + " days early" : early == 1 ? "a whole day early" : early == 0 ? "on the very day it was due" : "anyway, because legends finish what they start";
            string kind = t.Type == TaskType.Test ? "test" : t.Type == TaskType.Assignment ? "assignment" : "task";
            string[] lines =
            {
                "In a world where " + t.Name + " was due " + when + ", one woman dared to finish it " + finish + ".",
                "This semester. One bunny. One " + kind + ". No extensions. " + t.Name + ": finished.",
                "They said it couldn't be done. They said " + t.Name + " was too big. They were wrong. Coming soon to a transcript near you.",
                last.Length > 0 ? "From the student who brought you " + last + " comes her boldest work yet: " + t.Name + ". Rated A, for absolutely done." : "Some heroes wear capes. This one finished " + t.Name + ". Rated A, for absolutely done.",
            };
            return lines[pick % lines.Length];
        }

        // ---- Attaching ----

        public void Attach(MainWindow w)
        {
            _w = w;
            Sounds.PackOpen = p => !Collectible.Contains(p) || P.Packs.Contains(p);
            var top = w.AddMenuItem("Fun", "Adve_ntures", () => { });
            MenuItem Item(string header, Action act)
            {
                var m = new MenuItem { Header = header };
                m.Click += (_, _) => act();
                top.Items.Add(m);
                return m;
            }
            Item("The _burrow...", ShowBurrow);
            Item("_Rival: " + Rival + "...", ShowRival);
            Item("Make a b_et...", MakeBet);
            Item("Open a _mystery box...", MysteryBox);
            Item("This week's _quest...", ShowQuest);
            Item("Sound pack _collection...", ShowCollection);
            top.Items.Add(new Separator());
            _rivalMenu = Item(Rival + " _trash talks", () => { Cfg.RivalOn = _rivalMenu!.IsChecked; Cfg.Save(); _w.Say(Cfg.RivalOn ? Rival + " is back, and already smug." : Rival + " is quiet now. You can still check the score in Adventures.", true); });
            _rivalMenu.IsCheckable = true;
            _narratorMenu = Item("Dramatic _narrator", () => { Cfg.NarratorOn = _narratorMenu!.IsChecked; Cfg.Save(); _w.Say(Cfg.NarratorOn ? "The narrator clears its throat." : "The narrator takes a vow of silence.", true); });
            _narratorMenu.IsCheckable = true;
            top.SubmenuOpened += (_, _) => { _rivalMenu.IsChecked = Cfg.RivalOn; _narratorMenu.IsChecked = Cfg.NarratorOn; };

            w.StatusChanged += OnStatus;
            w.Started += () => { Daily(DateTime.Now, quiet: true); };
            w.NewDay += _ => Daily(DateTime.Now, quiet: false);
            w.MinuteTick += () => CheckBets(DateTime.Now);
        }

        /// <summary>Once a day: settle last week with Vex, start the week's quest, collect packs from streaks already earned.</summary>
        private void Daily(DateTime now, bool quiet)
        {
            var said = new List<string>();
            string thisWeek = WeekKey(now);
            // Last week's score is settled once, whatever ran first this week.
            var lastWeek = Engine.WeekStart(now).AddDays(-1);
            string lastKey = WeekKey(lastWeek);
            if (P.RivalSettled != lastKey)
            {
                int hers = Engine.PointsInWeek(S.Data, lastWeek);
                int vex = RivalScore(S.Data, lastWeek.AddHours(23.99), lastWeek);
                if (hers > 0)
                {
                    if (hers > vex)
                    {
                        P.RivalWins++;
                        P.RivalLetter = DefeatLetter(S.Data.Game.BunnyName == "Bun" ? "rival" : S.Data.Game.BunnyName + "'s human", hers, vex);
                        said.Add(Rival + " left you a letter. You won last week, " + hers + " to " + vex + ". Read it in Fun, Adventures, Rival.");
                        Game.Award(_w, 15, "Beat " + Rival + " last week", silent: true);
                    }
                    else { P.RivalLosses++; said.Add(Rival + " won last week, " + vex + " to " + hers + ", and will not shut up about it."); }
                }
                P.RivalSettled = lastKey;
                if (P.RivalWeek != thisWeek) { P.RivalWeek = thisWeek; P.RivalExtra = 0; }
                foreach (var old in P.RivalExtraByWeek.Keys.Where(k => string.CompareOrdinal(k, WeekKey(now.AddDays(-21))) < 0).ToList()) P.RivalExtraByWeek.Remove(old);
            }
            if (StartQuest(S, now)) said.Add("This is a big week. Your quest: " + P.QuestName + ", " + P.QuestTasks.Count + " chapters. Fun, Adventures, This week's quest tells the story.");
            foreach (var pack in CollectStreakPacks(S.Data, now)) said.Add("You collected the " + pack + " sound pack. Pick it in Options, Sounds.");
            S.Save();
            foreach (var line in said) Game.Note(S, line);
            if (said.Count > 0 && !quiet) _w.Say(string.Join(" ", said));
            else if (said.Count > 0) _w.Dispatcher.BeginInvoke(() => _w.Say(string.Join(" ", said)), DispatcherPriority.ApplicationIdle);
        }

        // ---- When a task is finished ----

        /// <summary>
        /// A finished task wins its bets, clears the mystery box and moves the quest on. Every award carries the task's id,
        /// so un-finishing it takes them back (Engine.Unfinish). Lines go through Game.Queue: one is spoken per finish.
        /// </summary>
        private void OnStatus(TaskItem t, Status old)
        {
            if (!Cleared(t) || Cleared(new TaskItem { Status = old })) return;
            var now = DateTime.Now;

            // Bets made in time.
            foreach (var b in P.Bets.Where(b => b.TaskId == t.Id && b.State == "Open" && now <= b.By).ToList())
            {
                b.State = "Won";
                b.FinishedAt = now;
                Game.Award(_w, b.Stake * 2, "Won the bet on " + t.Name, taskId: t.Id);
            }

            // The mystery box.
            if (P.MysteryTask == t.Id && P.MysteryDay == S.Key(now))
            {
                P.MysteryTask = null;
                Game.Award(_w, 5, MysteryLead + t.Name, taskId: t.Id);
            }

            // The quest. Chapters that were deleted or archived no longer count, so it can still be finished.
            if (P.QuestWeek == WeekKey(now) && !P.QuestDone && P.QuestTasks.Contains(t.Id))
            {
                var tasks = QuestChapters(S);
                int cleared = tasks.Count(Cleared);
                if (cleared > P.QuestCleared || cleared >= tasks.Count)
                {
                    P.QuestCleared = cleared;
                    if (cleared >= tasks.Count)
                    {
                        P.QuestDone = true;
                        Game.Note(S, P.QuestName + ": every chapter cleared. Your bunny plants a tiny flag at the top. Quest complete.");
                        Game.Award(_w, 25, QuestLead + P.QuestName, taskId: t.Id);
                    }
                    else Game.Queue(_w, Game.Other, P.QuestName + ", chapter " + cleared + " of " + tasks.Count + " cleared. " + StoryBeat(cleared, P.QuestName));
                }
            }

            // Packs from a new streak.
            foreach (var pack in CollectStreakPacks(S.Data, now))
            {
                Game.Queue(_w, Game.Goals, "New sound pack collected: " + pack + ". Pick it in Options, Sounds.");
                Sounds.Play(Sounds.Event.SurpriseReward);
            }

            // Vex.
            if (Cfg.RivalOn)
            {
                string week = WeekKey(now);
                int hers = Engine.PointsInWeek(S.Data, now), vex = RivalScore(S.Data, now);
                if (hers > vex && P.RivalPassed != week) { P.RivalPassed = week; Game.Queue(_w, Game.Chatter, Rival + ": " + Grudges[_rng.Next(Grudges.Length)]); }
                else if (Cfg.RoastLevel > 0 && (now - P.RivalLastTaunt).TotalMinutes >= 20 && _rng.Next(3) == 0)
                {
                    P.RivalLastTaunt = now;
                    int left = S.TodayAll(now).Count(x => !Cleared(x));
                    Game.Queue(_w, Game.Chatter, Rival + ": " + Fill(Taunts[_rng.Next(Taunts.Length)], t.Name, left));
                }
            }
            S.Save();

            // The narrator waits its turn: it speaks only when nothing more important was said for this finish.
            if (Cfg.NarratorOn && Big(t)) _ = Narrate(t, now, Game.QueueLate(_w, Game.Goals));
        }

        /// <summary>The quest's tasks that still exist and are not archived.</summary>
        public static List<TaskItem> QuestChapters(Store s) =>
            s.Data.Play.QuestTasks.Select(id => s.Data.Tasks.FirstOrDefault(x => x.Id == id)).Where(x => x != null && !s.ListOf(x).Archived).Select(x => x!).ToList();

        /// <summary>Whether the narrator may send this task to Claude: never for a private or archived list, or one inside a private or archived list (rule 5).</summary>
        public static bool MayNarrateWithClaude(Store s, TaskItem t)
        {
            // SmartBunny's own check walks the whole chain of parent lists.
            var list = s.Data.Lists.FirstOrDefault(l => l.Name == t.List);
            return list != null && !list.Archived && SmartBunny.MayShare(s, t);
        }

        private async Task Narrate(TaskItem t, DateTime now, Game.LateLine slot)
        {
            string last = S.Data.Log.Where(e => e.Kind == "Done" && e.TaskId != t.Id).OrderByDescending(e => e.When).FirstOrDefault()?.Text ?? "";
            string line = Trailer(t, now, last, _rng.Next(4));
            if (MayNarrateWithClaude(S, t)
                && await SmartBunny.WriteAsync("Write one dramatic movie trailer announcement, under 45 words, for Morgan finishing this: " + S.Line(t, now) +
                ". Start like a trailer voice, such as In a world where. Make it funny and proud.", about: t) is string written) line = written;
            await Task.Delay(400);
            if (!slot.Decided) await Task.Delay(1500);
            if (slot.Chosen) _w.Say("The narrator: " + line);
            else Game.Note(S, "The narrator: " + line);
        }

        /// <summary>Settles open bets as of now (see CheckBets). Winnings carry the task's id. Does not save or speak.</summary>
        public static (List<Bet> Won, List<Bet> Back, List<Bet> Lost) SettleBets(Store s, DateTime now)
        {
            var p = s.Data.Play;
            var lost = new List<Bet>();
            var back = new List<Bet>();
            var won = new List<Bet>();
            foreach (var b in p.Bets.Where(b => b.State == "Open").ToList())
            {
                var t = s.Data.Tasks.FirstOrDefault(x => x.Id == b.TaskId);
                if (t == null || s.ListOf(t).Archived)
                {
                    b.State = "Cancelled";
                    p.Spent = Math.Max(0, p.Spent - b.Stake);
                    back.Add(b);
                    continue;
                }
                if (Cleared(t) && (t.DoneAt ?? t.StatusSince ?? now) <= b.By)
                {
                    b.State = "Won";
                    b.FinishedAt = t.DoneAt ?? t.StatusSince ?? now;
                    var o = Engine.Record(s, now, new LogEntry { Kind = "Bonus", Text = "Won the bet on " + t.Name, Points = b.Stake * 2, TaskId = t.Id, Tag = t.Tag, List = t.List }, null, false);
                    foreach (var line in o.Said) Game.Note(s, line);
                    won.Add(b);
                    continue;
                }
                if (b.By >= now) continue;
                b.State = "Lost";
                string key = WeekKey(b.By);
                p.RivalExtraByWeek[key] = p.RivalExtraByWeek.GetValueOrDefault(key) + b.Stake;
                lost.Add(b);
            }
            return (won, back, lost);
        }

        /// <summary>
        /// Once a minute: settles bets. A task finished in time wins (even if it was finished some other way, like Edit
        /// task); a deleted or archived task gives the stake back; a deadline passed loses it to Vex, credited to the week
        /// of the deadline. Everything is said in one line at most, and only when the calm rules allow.
        /// </summary>
        private void CheckBets(DateTime now)
        {
            if (!P.Bets.Any(b => b.State == "Open")) return;
            var (won, back, lost) = SettleBets(S, now);
            if (lost.Count + back.Count + won.Count == 0) return;
            var parts = new List<string>();
            if (won.Count > 0) parts.Add(won.Count == 1 ? "You won the bet on " + won[0].Name + ", " + won[0].Stake * 2 + " points." : "You won " + won.Count + " bets, " + won.Sum(b => b.Stake * 2) + " points.");
            if (back.Count > 0) parts.Add(back.Count == 1 ? "The bet on " + back[0].Name + " is off, since that task is gone, so your " + back[0].Stake + " points are back."
                : back.Count + " bets are off, since their tasks are gone, so your " + back.Sum(b => b.Stake) + " points are back.");
            if (lost.Count > 0)
            {
                bool gloat = Cfg.RivalOn && Cfg.RoastLevel > 0;
                parts.Add(lost.Count == 1
                    ? (gloat ? Rival + ": " + Fill(Gloats[_rng.Next(Gloats.Length)], lost[0].Name, stake: lost[0].Stake) : "The bet on " + lost[0].Name + " ran out of time, so " + Rival + " gets the " + lost[0].Stake + " points.")
                    : lost.Count + " bets ran out of time, " + lost.Sum(b => b.Stake) + " points to " + Rival + (gloat ? ", who is insufferable about it." : "."));
            }
            string line = string.Join(" ", parts);
            Game.Note(S, line);
            // Lost bets speak only with Vex's trash talk on; nothing speaks in quiet hours, class, a sprint, vacation or a rough day.
            bool speak = (lost.Count == 0 || Cfg.RivalOn) && Game.MaySpeakUnprompted(S, now);
            S.Save();
            if (speak) _w.Say(line);
        }

        // ---- Windows ----

        private void ShowBurrow()
        {
            var w = Layout.Dialog(_w, "The burrow");
            var panel = new StackPanel { Margin = new Thickness(14), Width = 480 };
            var wallet = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) };
            var rooms = new ListBox { Height = 200 };
            var shop = new ListBox { Height = 160 };
            void Fill()
            {
                wallet.Text = "You have " + Wallet(S.Data) + " points to spend. Spending never lowers your level.";
                rooms.ItemsSource = Built(P).Select(r => r.Name + ". " + r.Feel).ToList();
                var left = Rooms.Where(r => !Built(P).Contains(r)).ToList();
                shop.ItemsSource = left.Count == 0 ? new List<string> { "Every room is built. The burrow is complete." }
                    : left.Select(r => r.Name + ", " + r.Cost + " points" + (r.Pack != null ? ", comes with the " + r.Pack + " sound pack" : "") + ". " + r.Feel).ToList();
            }
            Fill();
            rooms.SelectionChanged += (_, _) => { if (rooms.SelectedIndex >= 0 && Sounds.Enabled) Sounds.PlayNamed(Built(P).ElementAt(rooms.SelectedIndex).Sound); };
            void BuildChosen()
            {
                var left = Rooms.Where(r => !Built(P).Contains(r)).ToList();
                int i = shop.SelectedIndex;
                if (i < 0 || i >= left.Count) { Speech.Say(w, "Pick a room to build first.", true); return; }
                bool had = Built(P).Contains(left[i]);
                string said = Build(S, left[i]);
                if (!had && Built(P).Contains(left[i]))
                {
                    _w.SaveAndRefresh();
                    Sounds.Play(Sounds.Event.SurpriseReward);
                    Fill();
                    // The focused item went with the old list: put focus back on the shop, near where she was.
                    int count = shop.Items.Count;
                    if (count > 0) TextViewer.Select(shop, Math.Min(i, count - 1));
                }
                Speech.Say(w, said, true);
            }
            shop.KeyDown += (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; BuildChosen(); } };
            panel.Children.Add(wallet);
            panel.Children.Add(Layout.Row("_Rooms you have: arrows to walk through them", rooms, 480));
            panel.Children.Add(Layout.Row("Rooms to _build: Enter builds one", shop, 480));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var build = Layout.Button("B_uild this room");
            build.Click += (_, _) => BuildChosen();
            row.Children.Add(build);
            row.Children.Add(Layout.Button("Close", isCancel: true));
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) =>
            {
                rooms.SelectedIndex = 0;
                (rooms.ItemContainerGenerator.ContainerFromIndex(0) as ListBoxItem)?.Focus();
                Speech.Say(w, wallet.Text);
            };
            w.ShowDialog();
        }

        private void ShowRival()
        {
            var now = DateTime.Now;
            int hers = Engine.PointsInWeek(S.Data, now), vex = RivalScore(S.Data, now);
            var lines = new List<string>
            {
                "This week:",
                "You: " + hers + " points. " + Rival + ": " + vex + " points.",
                hers > vex ? "You are ahead by " + (hers - vex) + ". " + Rival + " is pretending not to care."
                    : hers == vex ? "Tied. " + Rival + " is sharpening a pencil menacingly."
                    : "You are behind by " + (vex - hers) + ". " + (Cfg.RivalOn && Cfg.RoastLevel > 0
                        ? Rival + ": " + Fill(Taunts[_rng.Next(Taunts.Length)], "one thing", S.TodayAll(now).Count(x => !Cleared(x)))
                        : "Plenty of week left."),
                Rival + " earns a little every day, and rests on your rough days and vacations. Lost bets go to " + Rival + ".",
                "Record:",
                "Weeks you won: " + P.RivalWins + ". Weeks " + Rival + " won: " + P.RivalLosses + ".",
            };
            if (P.RivalLetter.Length > 0) { lines.Add("The last letter from " + Rival + ":"); lines.Add(P.RivalLetter); }
            var open = P.Bets.Where(b => b.State == "Open").ToList();
            if (open.Count > 0)
            {
                lines.Add("Open bets:");
                foreach (var b in open) lines.Add(b.Stake + " points on " + b.Name + ", by " + Words.Day(b.By, now) + " at " + Words.Time(b.By) + ".");
            }
            var settled = P.Bets.Where(b => b.State != "Open" && b.By >= now.AddDays(-7)).OrderByDescending(b => b.By).ToList();
            if (settled.Count > 0)
            {
                lines.Add("Recent bets:");
                foreach (var b in settled) lines.Add(b.Stake + " points on " + b.Name + ": " + (b.State == "Won" ? "won." : b.State == "Lost" ? "lost to " + Rival + "." : "called off, points back."));
            }
            TextViewer.Show(_w, "Rival: " + Rival, lines, l => l.EndsWith(":") && !l.Contains(". "));
        }

        private void MakeBet()
        {
            var now = DateTime.Now;
            var open = S.Data.Tasks.Where(t => !Cleared(t) && !S.ListOf(t).Archived && !P.Bets.Any(b => b.TaskId == t.Id && b.State == "Open"))
                .OrderBy(t => t.Status).ThenBy(t => t.Due ?? DateTime.MaxValue).ToList();
            if (open.Count == 0) { _w.Say("There is nothing open to bet on.", true); return; }
            int have = Wallet(S.Data);
            var stakes = new[] { 5, 10, 20, 30, 50 }.Where(x => x <= have).ToList();
            if (stakes.Count == 0) { _w.Say("You need at least 5 points to spend to make a bet. You have " + have + ".", true); return; }

            var w = Layout.Dialog(_w, "Make a bet");
            var panel = new StackPanel { Margin = new Thickness(14), Width = 440 };
            var task = new ComboBox { ItemsSource = open.Select(t => S.Line(t, now)).ToList() };
            task.SelectedIndex = _w.ChosenTask is TaskItem c && open.IndexOf(c) is int ci && ci >= 0 ? ci : 0;
            var by = new TextBox { Text = "in 1 hour" };
            AutomationProperties.SetHelpText(by, "Like 3 pm, in 2 hours, tonight at 9, or tomorrow at noon.");
            var stake = new ComboBox { ItemsSource = stakes.Select(x => x + " points").ToList(), SelectedIndex = Math.Min(1, stakes.Count - 1) };
            panel.Children.Add(new TextBlock { Text = "Win and you get double back. Lose and " + Rival + " takes them. You have " + have + " points to spend.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(Layout.Row("I'll _finish", task, 440));
            panel.Children.Add(Layout.Row("_By", by, 440));
            panel.Children.Add(Layout.Row("_Stake", stake, 200));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ok = Layout.Button("_Place the bet", isDefault: true);
            row.Children.Add(ok);
            row.Children.Add(Layout.Button("Cancel", isCancel: true));
            panel.Children.Add(row);
            w.Content = panel;
            ok.Click += (_, _) =>
            {
                var when = ByWhen(by.Text, DateTime.Now);
                if (when == null) { Speech.Say(w, "I could not read that time. Try 3 pm, or in 2 hours.", true); by.Focus(); return; }
                if (when <= DateTime.Now.AddMinutes(5)) { Speech.Say(w, "Give yourself more than five minutes.", true); by.Focus(); return; }
                var t = open[Math.Max(0, task.SelectedIndex)];
                int pts = stakes[Math.Max(0, stake.SelectedIndex)];
                S.Remember("betting on " + t.Name);
                P.Spent += pts;
                P.Bets.Add(new Bet { TaskId = t.Id, Name = t.Name, Stake = pts, By = when.Value });
                P.Bets.RemoveAll(b => b.State != "Open" && b.By < DateTime.Now.AddDays(-30));
                _w.SaveAndRefresh();
                w.DialogResult = true;
                _w.Say("Bet placed: " + pts + " points that you finish " + t.Name + " by " + Words.Time(when.Value) + (when.Value.Date == DateTime.Today ? "" : " " + Words.Day(when.Value, DateTime.Now)) +
                    ". " + Rival + ": I'll start spending them now.", true);
            };
            w.Loaded += (_, _) => task.Focus();
            w.ShowDialog();
        }

        /// <summary>A time for a bet: in 2 hours, 3 pm, tomorrow at noon. A day with no time means the end of that day.</summary>
        public static DateTime? ByWhen(string text, DateTime now)
        {
            text = text.Trim().ToLowerInvariant();
            var m = System.Text.RegularExpressions.Regex.Match(text, "^in (an?|one|two|three|four|five|six|[0-9]+) (hour|hours|minute|minutes|min|mins)$");
            if (m.Success)
            {
                int n = m.Groups[1].Value switch { "a" or "an" or "one" => 1, "two" => 2, "three" => 3, "four" => 4, "five" => 5, "six" => 6, var x => int.TryParse(x, out int v) ? v : -1 };
                bool hours = m.Groups[2].Value.StartsWith("h");
                // Two weeks at most, so a huge number is just not understood instead of an error.
                if (n < 0 || n > (hours ? 14 * 24 : 14 * 24 * 60)) return null;
                return hours ? now.AddHours(n) : now.AddMinutes(n);
            }
            if (text == "tonight") return now.Date.AddHours(21);
            var (when, hasTime) = Language.ParseDate(text, now);
            if (when is not DateTime d) return null;
            if (!hasTime) return d.Date.AddDays(1).AddMinutes(-1);
            // A bare time already past today means tomorrow.
            return d <= now && d.Date == now.Date && !text.Contains("today") ? d.AddDays(1) : d;
        }

        private void MysteryBox()
        {
            var now = DateTime.Now;
            var pool = S.Data.Tasks.Where(t => !Cleared(t) && !S.ListOf(t).Archived && (t.QuickWin || t.Size == Size.Small)).ToList();
            if (pool.Count == 0) { _w.Say("No quick wins to hide in a box. Mark a task as a quick win, or give one the size Small.", true); return; }
            var pick = pool[_rng.Next(pool.Count)];
            var w = Layout.Dialog(_w, "Mystery box");
            var panel = new StackPanel { Margin = new Thickness(14), Width = 420 };
            var text = new TextBox { Text = "A box with a big bow on it. Something small and quick is inside. Open it to find out what.", IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Margin = new Thickness(0, 0, 0, 8) };
            AutomationProperties.SetName(text, "Mystery box");
            panel.Children.Add(text);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var open = Layout.Button("_Open it", isDefault: true);
            var go = Layout.Button("_Go to it") ;
            go.Visibility = Visibility.Collapsed;
            row.Children.Add(open);
            row.Children.Add(go);
            row.Children.Add(Layout.Button("Close", isCancel: true));
            panel.Children.Add(row);
            w.Content = panel;
            open.Click += (_, _) =>
            {
                P.MysteryTask = pick.Id;
                P.MysteryDay = S.Key(now);
                S.Save();
                Sounds.Play(Sounds.Event.SurpriseReward);
                text.Text = "You tear off the bow. Inside: " + S.Line(pick, now) + ". Finish it today for 5 bonus points.";
                open.Visibility = Visibility.Collapsed;
                go.Visibility = Visibility.Visible;
                go.IsDefault = true;
                go.Focus();
                Speech.Say(w, text.Text, true);
            };
            go.Click += (_, _) => { w.DialogResult = true; _w.SelectTask(pick); };
            // Focus reads the box; saying it too would read it twice.
            w.Loaded += (_, _) => text.Focus();
            w.ShowDialog();
        }

        private void ShowQuest()
        {
            var now = DateTime.Now;
            if (StartQuest(S, now)) S.Save();
            if (P.QuestName.Length == 0)
            {
                _w.Say("No quest this week. A week becomes a quest when four or more things are due in it. Enjoy the quiet.", true);
                return;
            }
            var tasks = P.QuestTasks.Select(id => S.Data.Tasks.FirstOrDefault(x => x.Id == id)).ToList();
            int cleared = tasks.Count(Cleared);
            var lines = new List<string> { P.QuestName + ":", cleared + " of " + tasks.Count + " chapters cleared." + (P.QuestDone ? " Quest complete. The flag is up." : " Clearing them all earns 25 points.") };
            for (int i = 0; i < tasks.Count; i++)
            {
                var t = tasks[i];
                string name = t?.Name ?? "a task that was deleted";
                lines.Add("Chapter " + (i + 1) + ", " + name + (t?.Due is DateTime d ? ", due " + Words.Day(d, now) : "") + (Cleared(t) ? ": cleared." : ": still ahead."));
            }
            lines.Add("The story so far:");
            if (cleared == 0) lines.Add(StoryBeat(1, P.QuestName));
            for (int i = 1; i <= cleared && i <= tasks.Count; i++) lines.Add(i == tasks.Count ? "Your bunny plants a tiny flag at the top of " + P.QuestName + ". Quest complete." : StoryBeat(i, P.QuestName));
            TextViewer.Show(_w, "This week's quest", lines, l => l.EndsWith(":") && !l.StartsWith("Chapter"));
        }

        private void ShowCollection()
        {
            var now = DateTime.Now;
            var have = Collectible.Where(P.Packs.Contains).ToList();
            var lines = new List<string> { "Collected (" + have.Count + " of " + Collectible.Count() + "):" };
            if (have.Count == 0) lines.Add("None yet. A 3 day streak gets the first one.");
            lines.AddRange(have.Select(p => p + ". Pick it in Options, Sounds."));
            lines.Add("Still to collect:");
            int streak = Math.Max(S.Data.Game.LongestStreak, Engine.Streaks(S.Data, now).Current);
            foreach (var (pack, days) in StreakPacks.Where(x => !P.Packs.Contains(x.Pack)))
                lines.Add(pack + ": reach a " + days + " day streak. Your best is " + streak + ".");
            foreach (var r in Rooms.Where(r => r.Pack != null && !P.Packs.Contains(r.Pack!)))
                lines.Add(r.Pack + ": build " + r.Name + " in the burrow, " + r.Cost + " points.");
            TextViewer.Show(_w, "Sound pack collection", lines, l => l.EndsWith(":"));
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            void Check(bool ok, string what) { if (!ok) throw new Exception("Adventures: " + what); }
            var all = new HashSet<string>(Sounds.All);
            foreach (var r in Rooms) Check(all.Contains(r.Sound), "room sound " + r.Sound);
            foreach (var p in Collectible) Check(Sounds.PackNames.Contains(p), "pack " + p);
            foreach (var p in Collectible) foreach (Sounds.Event e in Enum.GetValues<Sounds.Event>())
                    if (!Sounds.IsUi(e)) { var c = Sounds.PackChoice(p, e); Check(c == Sounds.DefaultName || all.Contains(c), p + " sound " + c); }
            var now = new DateTime(2026, 11, 4, 15, 0, 0);
            var st = new Store { PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "busybunny-play-test.json") };
            st.Data.Lists = Sample.Lists();
            st.Data.Log.Add(new LogEntry { When = now, Kind = "Done", Points = 100 });
            Check(Wallet(st.Data) == 100, "wallet");
            Check(Build(st, Rooms[1]).StartsWith("Built") && Wallet(st.Data) == 40 && Engine.Total(st.Data) == 100, "building spends without lowering points");
            Check(Build(st, Rooms[2]).Contains("more to go"), "too dear");
            st.Data.Play.Spent = 0;
            st.Data.Log.Add(new LogEntry { When = now, Kind = "Done", Points = 100 });
            Check(Build(st, Rooms.First(r => r.Id == "fireplace")).Contains("Fireside") && st.Data.Play.Packs.Contains("Fireside"), "room pack");
            st.Data.Game.LongestStreak = 7;
            Check(CollectStreakPacks(st.Data, now).SequenceEqual(new[] { "Pizzicato party", "Flute meadow" }), "streak packs");
            Check(RivalScore(st.Data, now) > 0, "Vex scores");
            Check(ByWhen("in 2 hours", now) == now.AddHours(2) && ByWhen("3 pm", now.Date.AddHours(10)) == now.Date.AddHours(15), "bet times");
            Check(ByWhen("3 pm", now.Date.AddHours(16)) == now.Date.AddDays(1).AddHours(15), "a past time means tomorrow");
            for (int i = 0; i < 4; i++) st.Data.Tasks.Add(new TaskItem { Name = "PSY midterm part " + i, List = "Homework", Due = now.Date.AddDays(i % 3), Status = Status.ThisWeek });
            Check(StartQuest(st, now) && st.Data.Play.QuestName == "Midterm Mountain" && st.Data.Play.QuestTasks.Count == 4, "quest");
            var big = new TaskItem { Name = "lit review", Size = Size.Large, Due = now.Date.AddDays(2) };
            Check(Big(big), "big task");
            for (int i = 0; i < 4; i++) { var line = Trailer(big, now, "stats homework", i); Check(!line.Contains('*') && !line.Contains('#') && line.Contains("lit review"), "trailer " + i); }
            Check(Trailer(big, now, "", 0).Contains("2 days early"), "trailer timing");
            foreach (var l in Taunts.Concat(Grudges).Concat(Gloats).Concat(QuestStory)) Check(!l.Contains('*') && !l.Contains('#'), "line " + l);
            Check(DefeatLetter("rival", 120, 90).Contains("you won"), "letter");
            Check(ByWhen("in 99999999999 hours", now) == null && ByWhen("in 99999999 hours", now) == null && ByWhen("in 300 hours", now) == now.AddHours(300), "huge bet times are refused, not errors");
            var secret = new TaskList { Name = "Diary", PrivateFromBunny = true };
            st.Data.Lists.Add(secret);
            st.Data.Lists.Add(new TaskList { Name = "Diary notes", Parent = "Diary" });
            Check(!MayNarrateWithClaude(st, new TaskItem { List = "Diary" }) && !MayNarrateWithClaude(st, new TaskItem { List = "Diary notes" }) && MayNarrateWithClaude(st, new TaskItem { List = "Homework" }), "the narrator never sends private lists to Claude");
            // A lost bet counts for the week of its deadline, so Monday's settling sees it in last week.
            var lastSunday = Engine.WeekStart(now).AddHours(-2);
            st.Data.Tasks.Add(new TaskItem { Name = "late", List = "Homework", Status = Status.Today });
            st.Data.Play.Bets.Add(new Bet { TaskId = st.Data.Tasks[^1].Id, Name = "late", Stake = 10, By = lastSunday });
            var (_, _, gone) = SettleBets(st, now);
            Check(gone.Count == 1 && RivalScore(st.Data, lastSunday, lastSunday) >= 10 && st.Data.Play.RivalExtraByWeek.ContainsKey(WeekKey(lastSunday)), "lost stake credited to its own week");
        }
    }
}
