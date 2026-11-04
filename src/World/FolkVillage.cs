using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    /// <summary>
    /// The neighbors and the village's stories (sweep 8.7, Part 10 items 3 and 9, 12.1 item 4, 13.3 item 24): friendship
    /// and a weekly chapter for each neighbor, friends growing over the semester, mail, the pen pal and the mysterious
    /// neighbor, cousins, the duck family, baby bunnies, visitors, the talent show and choir, the mayor election, the
    /// lighthouse keeper's log, fireside and campfire stories, sleepovers, the newspaper, the village anniversary,
    /// Bunny's radio drama, Goldie, the animal shelter and Bunny's band. Progress comes from finished tasks in the
    /// points log, so un-finishing a task (or Control Z) takes it back by itself.
    /// </summary>
    public sealed class FolkVillage : IFeature
    {
        private static MainWindow _w = null!;
        private static Store S => _w.Store;
        private static string Bun => S.Data.Game.BunnyName;

        public void Attach(MainWindow w)
        {
            _w = w;
            Folk.Tool("folk.neighbors", "The neighbors", Drawers.World, "Your village friends, how close you are, and their story chapters.", () => Neighbors(null), "your village friends and their new chapters");
            Folk.Tool("folk.friends", "Friends level up", Drawers.World, "What each neighbor has grown into this semester.", () => Friends(null));
            Folk.Tool("folk.mail", "Bunny's mail", Drawers.World, "Weekly letters from the neighbors, newest first.", () => Mail(null, "mail"), "letters from the neighbors, the pen pal and someone mysterious");
            Folk.Tool("folk.penpal", "Pen pal letters", Drawers.World, "A monthly letter from Juniper, a bunny in another village.", () => Mail(null, "penpal"));
            Folk.Tool("folk.mysterious", "The mysterious neighbor", Drawers.World, "Letters from someone nobody has met, revealed over the semester.", () => Mail(null, "mystery"));
            Folk.Tool("folk.cousins", "Bunny's cousins", Drawers.World, "Bunny's noisy cousins, who visit at holidays.", () => Cousins(null));
            Folk.Tool("folk.ducks", "The duck family", Drawers.World, "Dot's ducklings growing up at the pond over the semester.", () => Ducks(null));
            Folk.Tool("folk.babies", "Baby bunnies", Drawers.World, "Baby bunnies born next door in spring, named by you.", () => Babies(null));
            Folk.Tool("folk.visitors", "Visitors", Drawers.Wins, "Guests who came to the village for your big milestones.", () => Visitors(null));
            Folk.Tool("folk.show", "Talent show and choir", Drawers.World, "Vote for this month's talent show act, and hear the choir at festivals.", () => Show(null));
            Folk.Tool("folk.mayor", "Mayor election", Drawers.World, "Vote for mayor once a semester; the winner changes one village rule.", () => Mayor(null));
            Folk.Tool("folk.keeper", "The lighthouse keeper's log", Drawers.Rest, "Marlow's log of every rough day you got through.", () => Keeper(null));
            Folk.Tool("folk.fireside", "Fireside and campfire stories", Drawers.Rest, "Hazel reads by the fire on winter evenings; a campfire story on Saturday nights.", () => Fireside(null));
            Folk.Tool("folk.sleepover", "Sleepover", Drawers.Rest, "A neighbor stays over and tells a bedtime story, once a week.", () => Sleepover(null));
            Folk.Tool("folk.paper", "The Acorn Gazette", Drawers.Wins, "The village newspaper, with your week's wins as headlines.", () => Paper(null));
            Folk.Tool("folk.anniversary", "Village anniversary", Drawers.Wins, "A recap of your time in the village since your last anniversary.", () => Anniversary(null));
            Folk.Tool("folk.radio", "Bunny's radio drama", Drawers.World, "A weekly episode about the village; old episodes are kept.", () => Radio(null), "this week's episode and the old ones");
            Folk.Tool("folk.shelter", "The animal shelter", Drawers.World, "Foster a rescue animal with your finished tasks until it finds its family.", () => Shelter(null));
            Folk.Tool("folk.band", "Bunny's band", Drawers.World, "The neighbors' band, playing the instruments you bought.", () => Band(null));
            RenameGoldie(w.Store);

            // Things waiting for her are reminders: folded into one short line, each at most once a day.
            Folk.Reminder("folk.mail", "mail", (s, now) => Unread(s, now) is int n and > 0 ? Folk.Plural(n, "new letter", "new letters") + " in Bunny's mail" : null);
            Folk.Reminder("folk.neighbors", "chapters", (s, now) => NewChapters(s, now) is { Count: > 0 } c ? ChaptersWaiting(c) : null);
            Folk.Visit("folk.goldie", (_, now) => Folk.RoughToday(S, now) && Folk.First(S, "goldie.rough." + S.Key(now)) ? new[] { Folk.Fill(S, "{goldie} is lying by the door today, tail thumping softly. When you want, I am stuck has {goldie}, and she will walk you to one thing.") } : Array.Empty<string>());
            Folk.Visit("folk.cousins", (_, now) => Folk.FestivalNear(now) is string f && Folk.First(S, "cousins." + f + now.Year) ? new[] { CousinsLine(S, f, now) } : Array.Empty<string>());
            Folk.Visit("folk.show", (_, now) => Folk.FestivalNear(now) is string f && Folk.First(S, "choir." + f + now.Year) ? new[] { Folk.Pick(S, "folk.choir", ("holiday", f)) } : Array.Empty<string>());
            Folk.Visit("folk.band", (_, now) => Folk.FestivalNear(now) is string f && Folk.First(S, "band." + f + now.Year) ? new[] { "At " + f + ": " + BandLine(S) } : Array.Empty<string>());
            Folk.Reminder("folk.babies", "babies", (s, now) => RealWorld.Season(now) == "Spring" && BabyNames(s, now.Year).Count == 0 ? "baby bunnies next door who need names" : null);
            Folk.Reminder("folk.radio", "radio", (s, now) => !Episodes(s).Any(e => e.Week == Folk.WeekKey(now)) ? "this week's radio episode" : null);

            // The neighbors live in the places stage's village; the ducks, the keeper and Clover in theirs.
            PlaceHooks.AddThings(PlaceIds.Village, Things);
            PlaceHooks.AddThings(PlaceIds.Pond, _ => Tools.IsOn("folk.ducks") ? new List<PlaceThing> { new("Dot and her ducklings", DuckStage(S, DateTime.Now), win => Ducks(win)) } : new());
            PlaceHooks.AddThings(PlaceIds.Lighthouse, _ => Tools.IsOn("folk.keeper") ? new List<PlaceThing> { new("Marlow's log", "the keeper's notes on the rough days you got through", win => Keeper(win)) } : new());
            PlaceHooks.AddThings(PlaceIds.Stable, _ => Tools.IsOn("folk.neighbors") ? new List<PlaceThing> { new("Clover the pony", "your friend at the stable", win => Chapters(win, "clover")) } : new());

            GameEvents.TaskDone += OnDone;
            GameEvents.LevelUp += level => Visitor(S, "level." + level, "you reached level " + level);
            GameEvents.BadgeEarned += badge => Visitor(S, "badge." + badge, "you earned the " + badge + " badge");
        }

        private static List<PlaceThing> Things(Store st)
        {
            var now = DateTime.Now;
            var list = new List<PlaceThing>();
            if (Tools.IsOn("folk.neighbors"))
                foreach (var n in Neighbors())
                    list.Add(new PlaceThing(Folk.Fill(st, n.Get("name")), Folk.Fill(st, n.Get("kind")) + ", " + Folk.Plural(Unlocked(st, n.Get("id"), now), "chapter", "chapters"), win => Chapters(win, n.Get("id"))));
            if (Tools.IsOn("folk.mail")) list.Add(new("The post box", Folk.Plural(Unread(st, now), "new letter", "new letters"), win => Mail(win, "mail")));
            if (Tools.IsOn("folk.paper")) list.Add(new("The newspaper stand", "this week's Acorn Gazette", win => Paper(win)));
            if (Tools.IsOn("folk.show")) list.Add(new("The bandstand", "the talent show and the choir", win => Show(win)));
            if (Tools.IsOn("folk.mayor")) list.Add(new("The town hall", MayorRule(st, now) ?? "this semester's election", win => Mayor(win)));
            if (Tools.IsOn("folk.shelter")) list.Add(new("Mrs. Teasel's animal shelter", "your foster animal", win => Shelter(win)));
            return list;
        }

        // ---- Neighbors, friendship and chapters ----

        public static List<Dictionary<string, string>> Neighbors() => Folk.Items("folk.neighbors");
        public static List<string> Ids => Neighbors().Select(n => n.Get("id")).ToList();
        public static string NameOf(Store s, string id) => Folk.Fill(s, Neighbors().FirstOrDefault(n => n.Get("id") == id)?.Get("name") ?? id);

        /// <summary>Which neighbor a finished task makes friends with (always the same one for a task).</summary>
        public static string FriendFor(Guid task) { var ids = Ids; return ids.Count == 0 ? "" : ids[Folk.Mod(Folk.Hash(task.ToString()), ids.Count)]; }

        /// <summary>Friendship: the finished tasks that went to this neighbor, plus gifts she left them.</summary>
        public static int Friendship(Store s, string id) =>
            Folk.Finished(s).Count(x => FriendFor(x.Id) == id) + s.World.Get<Dictionary<string, int>>("folk.friend.bonus").GetValueOrDefault(id);

        public static void AddFriendship(Store s, string id, int by) => World.Change<Dictionary<string, int>>(s, "folk.friend.bonus", "leaving a gift for " + NameOf(s, id), d => d[id] = d.GetValueOrDefault(id) + by);

        /// <summary>Chapter n (from 1) opens in week n of her time in the village, once her friendship is at least 2 times (n minus 1).</summary>
        public static int Unlocked(Store s, string id, DateTime now)
        {
            int total = Folk.Blocks("folk.chapters." + id).Count, weeks = Folk.Weeks(s, now), friend = Friendship(s, id);
            int n = 0;
            for (int c = 1; c <= total; c++) if (weeks >= c - 1 && friend >= 2 * (c - 1)) n = c;
            return n;
        }

        private static HashSet<string> Read(Store s) => s.World.GetKept<HashSet<string>>("folk.read");
        private static void MarkRead(Store s, string key) { var r = Read(s); if (r.Add(key)) { s.World.SetKept("folk.read", r); s.World.Save(); } }

        public static List<string> NewChapters(Store s, DateTime now) => Ids.Where(id => Unlocked(s, id, now) is int n && n > 0 && !Read(s).Contains("chapter." + id + "." + n)).Select(id => NameOf(s, id)).ToList();

        /// <summary>"a new chapter from Hazel", or "new chapters from Hazel, Otis and 3 more", for the visit's reminder line.</summary>
        public static string ChaptersWaiting(List<string> names)
        {
            if (names.Count == 1) return "a new chapter from " + names[0];
            if (names.Count <= 3) return "new chapters from " + string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
            return "new chapters from " + names[0] + ", " + names[1] + " and " + (names.Count - 2) + " more";
        }

        public static bool NewChapterThisWeek(Store s, DateTime now) =>
            s.World.GetKept<Dictionary<string, string>>("folk.chapter.week").Values.Contains(Folk.WeekKey(now));

        /// <summary>A neighbor's level this semester (1 to 5): grows with the weeks and with friendship.</summary>
        public static int Level(Store s, string id, DateTime now) => Math.Clamp(1 + Math.Min(Folk.SemesterWeek(now) / 3, Friendship(s, id) / 6), 1, 5);

        private static void Neighbors(Window? owner)
        {
            Folk.List(owner, "The neighbors", () =>
            {
                var now = DateTime.Now;
                return Neighbors().Select(n =>
                {
                    string id = n.Get("id");
                    int un = Unlocked(S, id, now), total = Folk.Blocks("folk.chapters." + id).Count;
                    bool fresh = un > 0 && !Read(S).Contains("chapter." + id + "." + un);
                    return new Folk.Row(Folk.Fill(S, n.Get("name") + ", " + n.Get("kind") + ", now " + n.Get("level" + Level(S, id, now)) + ". Friendship " + Friendship(S, id)
                        + ". " + un + " of " + total + " chapters" + (fresh ? ", a new one waiting." : ".")), win => Chapters(win, id));
                }).ToList();
            }, "Enter opens a neighbor's chapters. Finishing tasks grows your friendships, and a new chapter comes each week.");
        }

        private static void Chapters(Window? owner, string id)
        {
            var n = Neighbors().FirstOrDefault(x => x.Get("id") == id);
            if (n == null) return;
            Folk.List(owner, NameOf(S, id), () =>
            {
                var now = DateTime.Now;
                var rows = new List<Folk.Row> { new(NameOf(S, id) + ": " + Folk.Fill(S, Folk.Describe(n))) };
                var chapters = Folk.Blocks("folk.chapters." + id);
                int un = Unlocked(S, id, now);
                for (int c = un; c >= 1; c--)
                {
                    var ch = chapters[c - 1];
                    int cc = c;
                    string title = "Chapter " + c + ": " + ch.Get("title") + (Read(S).Contains("chapter." + id + "." + c) ? "" : ", new");
                    rows.Add(new Folk.Row(title, win => { MarkRead(S, "chapter." + id + "." + cc); Folk.Read(win, NameOf(S, id) + ", chapter " + cc, Folk.Fill(S, ch.Body)); }));
                }
                if (un < chapters.Count) rows.Add(new Folk.Row("Chapter " + (un + 1) + " comes next week" + (Friendship(S, id) < 2 * un ? ", once you have finished a couple more tasks" : "") + "."));
                else rows.Add(new Folk.Row("That is every chapter so far. " + NameOf(S, id) + " is working on more."));
                return rows;
            }, "Newest chapter first. Enter reads it.");
        }

        private static void Friends(Window? owner)
        {
            Folk.List(owner, "Friends level up", () =>
            {
                var now = DateTime.Now;
                return Neighbors().Select(n =>
                {
                    int l = Level(S, n.Get("id"), now);
                    return new Folk.Row(Folk.Fill(S, n.Get("name") + ", level " + l + " of 5: " + n.Get("level" + l) + "." + (l < 5 ? " Next: " + n.Get("level" + (l + 1)) + "." : "")));
                }).ToList();
            }, "Your friends grow over the semester and as your friendships grow.");
        }

        private static void OnDone(TaskItem t)
        {
            if (Calm.FunPaused) return;
            var now = DateTime.Now;
            string id = FriendFor(t.Id);
            if (Tools.IsOn("folk.neighbors") && id.Length > 0)
            {
                int un = Unlocked(S, id, now);
                if (un > 1 && Folk.First(S, "chapter.said." + id + "." + un))
                {
                    var weeks = S.World.GetKept<Dictionary<string, string>>("folk.chapter.week");
                    weeks[id + "." + un] = Folk.WeekKey(now);
                    S.World.SetKept("folk.chapter.week", weeks);
                    S.World.Save();
                    Folk.AfterTask(_w, Folk.Pick(S, "folk.unlock.chapter", ("neighbor", NameOf(S, id))));
                }
            }
            if (Tools.IsOn("folk.shelter") && ShelterLine(S, now) is string line) Folk.AfterTask(_w, line);
        }

        // ---- Goldie (Part 10 item 3) ----

        /// <summary>Goldie's tool carries her name, so it is registered again when she is renamed.</summary>
        public static void RenameGoldie(Store s)
        {
            string g = Folk.Goldie(s);
            Tools.Register(new BunnyTool("folk.goldie", g + " picks one task", Drawers.Stuck, "The golden retriever neighbor walks you to one task, just one.", Goldie) { Since = Folk.Since });
        }

        /// <summary>The one task Goldie walks to: the most urgent for today, else the first for today, else this week's.</summary>
        public static TaskItem? GoldiePick(Store s, DateTime now)
        {
            var (urgent, rest) = s.Today(now);
            return urgent.FirstOrDefault() ?? rest.FirstOrDefault() ?? s.Data.Tasks.FirstOrDefault(t => t.Status == Status.ThisWeek && s.CanShowToday(t, now));
        }

        private static void Goldie()
        {
            var now = DateTime.Now;
            SoundBank.Play("Singing bells, sparkle");
            if (GoldiePick(S, now) is not TaskItem t)
            {
                FolkBunny.Tell(_w, Folk.Fill(S, "{goldie} sniffs all around and finds nothing that needs you right now. She lies down next to {name} instead, warm and heavy, and sighs a happy sigh."));
                return;
            }
            _w.SelectTask(t);
            FolkBunny.Tell(_w, Folk.Pick(S, "folk.goldie.guide", ("task", t.Name)));
        }

        // ---- Mail: neighbors weekly, the pen pal monthly, the mysterious neighbor over a semester ----

        public sealed record Letter(string Key, DateTime When, string From, string Text);

        public static List<Letter> Letters(Store s, DateTime now, string kind)
        {
            var list = new List<Letter>();
            var start = Folk.Start(s);
            if (kind == "mail")
                for (int i = Math.Max(0, Folk.Weeks(s, now) - 29); i <= Folk.Weeks(s, now); i++)
                    if (Folk.NthBlock("folk.letters", i) is Folk.Block b)
                        list.Add(new Letter("letter.mail." + i, Engine.WeekStart(start).AddDays(7 * i), NameOf(s, b.Get("from")), Folk.Fill(s, b.Body)));
            if (kind == "penpal")
                for (int m = Math.Max(0, Folk.Months(s, now) - 11); m <= Folk.Months(s, now); m++)
                    if (Folk.NthBlock("folk.penpal", m) is Folk.Block b)
                        list.Add(new Letter("letter.penpal." + m, new DateTime(start.Year, start.Month, 1).AddMonths(m), "Juniper, your pen pal", Folk.Fill(s, b.Body)));
            if (kind == "mystery")
            {
                var from = Engine.WeekStart(GameFeature.Semester(now).From);
                int sem = Folk.SemesterIndex(now);
                for (int i = 0; i <= Math.Min(Folk.SemesterWeek(now), Folk.Blocks("folk.mystery.letters").Count - 1); i++)
                {
                    var when = from.AddDays(7 * i);
                    if (when < Engine.WeekStart(start)) continue;
                    list.Add(new Letter("letter.mystery." + sem + "." + i, when, "Someone you have not met", Folk.Fill(s, Folk.Blocks("folk.mystery.letters")[i].Body)));
                }
                // A new visitor still gets the first letter in their first week.
                if (list.Count == 0 && Folk.Blocks("folk.mystery.letters").Count > 0)
                    list.Add(new Letter("letter.mystery." + sem + ".0", Engine.WeekStart(now), "Someone you have not met", Folk.Fill(s, Folk.Blocks("folk.mystery.letters")[0].Body)));
            }
            return list.OrderByDescending(l => l.When).ToList();
        }

        private static IEnumerable<string> Kinds() => new[] { ("mail", "folk.mail"), ("penpal", "folk.penpal"), ("mystery", "folk.mysterious") }.Where(k => Tools.IsOn(k.Item2)).Select(k => k.Item1);

        public static int Unread(Store s, DateTime now) => Kinds().SelectMany(k => Letters(s, now, k)).Count(l => !Read(s).Contains(l.Key));

        private static void Mail(Window? owner, string only)
        {
            string title = only == "penpal" ? "Pen pal letters" : only == "mystery" ? "The mysterious neighbor" : "Bunny's mail";
            Folk.List(owner, title, () =>
            {
                var now = DateTime.Now;
                var kinds = only == "mail" ? Kinds().ToList() : new List<string> { only };
                var letters = kinds.SelectMany(k => Letters(S, now, k)).OrderByDescending(l => l.When).ToList();
                var rows = letters.Select(l => new Folk.Row((Read(S).Contains(l.Key) ? "" : "New. ") + "From " + l.From + ", " + l.When.ToString("MMMM d") + ".", win =>
                {
                    MarkRead(S, l.Key);
                    Folk.Read(win, "From " + l.From, l.Text);
                })).ToList();
                if (rows.Count == 0) rows.Add(new Folk.Row("No letters yet. The post comes every week."));
                return rows;
            }, "Newest first. Enter opens a letter.");
        }

        // ---- Cousins, ducks, baby bunnies, visitors ----

        public static string CousinsLine(Store s, string festival, DateTime now) => Folk.Nth(s, "folk.cousins", Folk.Hash(festival + now.Year), ("holiday", festival));

        private static void Cousins(Window? owner)
        {
            var now = DateTime.Now;
            if (Folk.FestivalNear(now) is string f) { Folk.Read(owner, Bun + "'s cousins", CousinsLine(S, f, now)); return; }
            var (day, what) = Folk.NextFestival(now);
            Folk.Say(owner, "The cousins visit at holidays. Next time: " + what + ", " + Folk.Day(day) + ". " + Bun + " is already hiding the breakable things.");
        }

        public static string DuckStage(Store s, DateTime now) => Folk.Nth(s, "folk.ducks", Math.Min(Folk.SemesterWeek(now), Content.Lines("folk.ducks").Count - 1));

        private static void Ducks(Window? owner)
        {
            var now = DateTime.Now;
            int week = Math.Min(Folk.SemesterWeek(now), Content.Lines("folk.ducks").Count - 1);
            var lines = Enumerable.Range(0, week + 1).Reverse().Select(i => (i == week ? "This week: " : "Week " + (i + 1) + ": ") + Folk.Nth(S, "folk.ducks", i)).ToList();
            TextViewerOr(owner, "The duck family", lines);
        }

        private static void TextViewerOr(Window? owner, string title, List<string> lines) => Folk.List(owner, title, () => lines.Select(l => new Folk.Row(l)).ToList(), "Newest first.");

        public sealed class BabyBox { public Dictionary<int, List<string>> Names { get; set; } = new(); }
        private sealed class Born { public Dictionary<int, DateTime> On { get; set; } = new(); }

        public static List<string> BabyNames(Store s, int year) => s.World.Get<BabyBox>("folk.babies").Names.TryGetValue(year, out var n) ? n : new();

        /// <summary>The babies are born the first time she looks in spring; they stay and grow the rest of that year.</summary>
        public static DateTime? BornOn(Store s, DateTime now)
        {
            var b = s.World.GetKept<Born>("folk.babies.born");
            if (b.On.TryGetValue(now.Year, out var d)) return d;
            if (RealWorld.Season(now) != "Spring") return null;
            b.On[now.Year] = now.Date;
            s.World.SetKept("folk.babies.born", b);
            s.World.Save();
            return now.Date;
        }

        public static List<string> ParseNames(string text)
        {
            var names = text.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).Take(3).ToList();
            foreach (var spare in new[] { "Bean", "Pebble", "Sprout" }) if (names.Count < 3 && !names.Contains(spare)) names.Add(spare);
            return names;
        }

        private static void Babies(Window? owner)
        {
            var now = DateTime.Now;
            if (BornOn(S, now) is not DateTime born)
            {
                Folk.Say(owner, "The family in the burrow next door is expecting babies in spring. " + Bun + " has already knitted something lumpy for them.");
                return;
            }
            if (BabyNames(S, now.Year).Count == 0)
            {
                var typed = Folk.Ask(owner, "Name the baby bunnies", "Three baby bunnies were born next door. Their names, separated by commas", "");
                if (typed == null) { Folk.Say(owner, "No rush. They are mostly sleeping anyway. Come back to name them any time."); return; }
                var names = ParseNames(typed);
                World.Change<BabyBox>(S, "folk.babies", "naming the baby bunnies", b => b.Names[now.Year] = names);
                Folk.Say(owner, "Welcome, " + string.Join(", ", names.Take(2)) + " and " + names[2] + ". " + Bun + " is beside itself.");
            }
            var n = BabyNames(S, now.Year);
            int week = Math.Min((int)((now.Date - born).TotalDays / 7), Content.Lines("folk.babies").Count - 1);
            var lines = Enumerable.Range(0, week + 1).Reverse().Select(i => (i == week ? "This week: " : "Week " + (i + 1) + ": ")
                + Folk.Nth(S, "folk.babies", i, ("baby1", n[0]), ("baby2", n[1]), ("baby3", n[2]))).ToList();
            TextViewerOr(owner, "Baby bunnies", lines);
        }

        public sealed class Guest { public DateTime When { get; set; } public string Who { get; set; } = ""; public string Line { get; set; } = ""; public string For { get; set; } = ""; }

        /// <summary>A guest comes for a big milestone (a level up, a badge), once each.</summary>
        public static void Visitor(Store s, string key, string why)
        {
            if (Calm.FunPaused || !Tools.IsOn("folk.visitors") || !Folk.First(s, "visitor." + key)) return;
            var item = Folk.Item("folk.visitors", Folk.Hash(key));
            var list = s.World.GetKept<List<Guest>>("folk.visitors");
            list.Add(new Guest { When = DateTime.Now, Who = item.Get("who"), Line = Folk.Fill(s, item.Get("line")), For = why });
            s.World.SetKept("folk.visitors", list);
            s.World.Save();
            if (_w == null || s != S) return;
            string line = "A visitor came because " + why + ": " + item.Get("who") + ". " + Folk.Fill(s, item.Get("line"));
            // A level can also come from points that are not a finished task (a dig, a solved puzzle): then it is unprompted.
            if (JustFinished(s, DateTime.Now)) Folk.AfterTask(_w, line);
            else Calm.SayUnprompted(_w, line);
        }

        /// <summary>Whether a task was finished in the last few seconds, so a level up or badge belongs to that finish.</summary>
        public static bool JustFinished(Store s, DateTime now) => s.Data.Log.Any(e => e.Kind == "Done" && e.When <= now && (now - e.When).TotalSeconds <= 10);

        private static void Visitors(Window? owner)
        {
            var list = S.World.GetKept<List<Guest>>("folk.visitors");
            var lines = Enumerable.Reverse(list).Select(g => g.When.ToString("MMMM d") + ", because " + g.For + ": " + g.Who + ". " + g.Line).ToList();
            if (lines.Count == 0) lines.Add("No visitors yet. Guests come to the village when you level up or earn a badge.");
            TextViewerOr(owner, "Visitors", lines);
        }

        // ---- Talent show and choir, mayor ----

        public static List<Dictionary<string, string>> Acts(Store s, DateTime now)
        {
            int m = Folk.Months(s, now);
            return Enumerable.Range(0, 3).Select(i => Folk.Item("folk.acts", m * 3 + i)).ToList();
        }

        private static void Show(Window? owner)
        {
            var now = DateTime.Now;
            string month = Folk.MonthKey(now);
            var votes = S.World.Get<Dictionary<string, string>>("folk.show");
            if (!votes.ContainsKey(month))
            {
                var acts = Acts(S, now);
                int pick = Folk.Choose(owner, "Talent show", "This month's talent show. Which act gets your vote?", acts.Select(a => a.Get("act") + ", by " + Folk.Fill(S, a.Get("who")) + ". " + Folk.Fill(S, a.Get("line"))).ToList());
                if (pick < 0) return;
                string act = acts[pick].Get("act");
                World.Change<Dictionary<string, string>>(S, "folk.show", "voting in the talent show", d => d[month] = act);
                SoundBank.Play("Pizzicato strings, bounce");
                Folk.Say(owner, Folk.Pick(S, "folk.show.winner", ("act", act)));
                return;
            }
            var lines = votes.OrderByDescending(v => v.Key).Select(v => DateTime.Parse(v.Key + "-01").ToString("MMMM yyyy") + ": the winner was " + v.Value + ".").ToList();
            lines.Insert(0, Folk.FestivalNear(now) is string f ? "The choir is singing for " + f + ". " + Folk.Pick(S, "folk.choir", ("holiday", f)) : "The choir sings at every festival. Next: " + Folk.NextFestival(now).What + ".");
            TextViewerOr(owner, "Talent show and choir", lines);
        }

        public static List<Dictionary<string, string>> Candidates(DateTime now) =>
            Folk.Items("folk.mayor").Where(i => i.Get("election") == (Folk.Mod(Folk.SemesterIndex(now), 4) + 1).ToString()).ToList();

        /// <summary>The village rule of this semester's mayor, or null before she voted.</summary>
        public static string? MayorRule(Store s, DateTime now)
        {
            var votes = s.World.Get<Dictionary<string, string>>("folk.mayor");
            if (!votes.TryGetValue(Folk.SemesterIndex(now).ToString(), out var who)) return null;
            var c = Candidates(now).FirstOrDefault(x => x.Get("candidate") == who);
            return c == null ? null : "Mayor " + who + "'s rule: " + Folk.End(c.Get("rule"));
        }

        private static void Mayor(Window? owner)
        {
            var now = DateTime.Now;
            if (MayorRule(S, now) is string rule) { Folk.Say(owner, rule + " The next election is next semester."); return; }
            var cs = Candidates(now);
            if (cs.Count == 0) return;
            int pick = Folk.Choose(owner, "Mayor election", "Election day! Once a semester the village picks a mayor, and the winner changes one rule for fun. Your vote decides it.",
                cs.Select(c => c.Get("candidate") + ": " + c.Get("promise")).ToList());
            if (pick < 0) return;
            string who = cs[pick].Get("candidate");
            World.Change<Dictionary<string, string>>(S, "folk.mayor", "voting for mayor", d => d[Folk.SemesterIndex(now).ToString()] = who);
            SoundBank.Play("Singing bells, sparkle");
            Folk.Say(owner, who + " wins! The other candidates are already planning the victory cake. " + MayorRule(S, now));
        }

        // ---- The lighthouse keeper's log, fireside and campfire, sleepovers ----

        public static List<string> KeeperLog(Store s, DateTime now) => s.Data.Days
            .Where(kv => kv.Value?.Mode == DayMode.RoughDay && DateTime.TryParse(kv.Key, out var d) && d.Date <= now.Date)
            .Select(kv => DateTime.Parse(kv.Key)).OrderByDescending(d => d)
            .Select(d => Folk.Nth(s, "folk.keeper", Folk.Hash(s.Key(d)), ("date", Folk.Day(d)))).ToList();

        private static void Keeper(Window? owner)
        {
            var lines = KeeperLog(S, DateTime.Now);
            if (lines.Count == 0) lines.Add("No rough days in the log yet. Marlow keeps the lamp lit anyway, just in case you ever need it.");
            TextViewerOr(owner, "The lighthouse keeper's log", lines);
        }

        private sealed class FireBox { public string Evening { get; set; } = ""; public int Chapters { get; set; } public List<int> Campfires { get; set; } = new(); }

        public static bool FiresideNow(DateTime now) => now.Month is 12 or 1 or 2 && now.Hour >= 17;
        /// <summary>Campfire night is Saturday evening, the same night as the seasons calendar's campfire night.</summary>
        public static bool CampfireNow(DateTime now) => now.DayOfWeek == DayOfWeek.Saturday && now.Hour >= 17;

        private static void Fireside(Window? owner)
        {
            Folk.List(owner, "Fireside and campfire stories", () =>
            {
                var now = DateTime.Now;
                var b = S.World.GetKept<FireBox>("folk.fireside");
                var rows = new List<Folk.Row>();
                var chapters = Folk.Blocks("folk.fireside");
                var stories = Folk.Blocks("folk.campfire");
                if (FiresideNow(now) && b.Evening != S.Key(now) && b.Chapters < chapters.Count)
                {
                    b.Evening = S.Key(now);
                    b.Chapters++;
                    S.World.SetKept("folk.fireside", b);
                    S.World.Save();
                }
                if (CampfireNow(now))
                {
                    int i = Folk.Mod(Folk.Hash(S.Key(now)), Math.Max(1, stories.Count));
                    if (!b.Campfires.Contains(i)) { b.Campfires.Add(i); S.World.SetKept("folk.fireside", b); S.World.Save(); }
                    if (stories.Count > 0) rows.Add(new Folk.Row("Tonight at the campfire: " + stories[i].Get("title"), win => Folk.Read(win, stories[i].Get("title"), Folk.Fill(S, stories[i].Body))));
                }
                if (!FiresideNow(now) && !CampfireNow(now))
                    rows.Add(new Folk.Row("Hazel reads by the fireplace on winter evenings, and the campfire is lit on Saturday nights. The stories you have heard are below."));
                for (int c = b.Chapters; c >= 1; c--)
                {
                    var ch = chapters[c - 1];
                    int cc = c;
                    rows.Add(new Folk.Row((c == b.Chapters && FiresideNow(now) ? "Tonight by the fire, chapter " : "By the fire, chapter ") + c + ": " + ch.Get("title"), win => Folk.Read(win, "Chapter " + cc + ": " + ch.Get("title"), Folk.Fill(S, ch.Body))));
                }
                foreach (int i in Enumerable.Reverse(b.Campfires).Skip(CampfireNow(now) ? 1 : 0))
                    if (i < stories.Count) rows.Add(new Folk.Row("Campfire story: " + stories[i].Get("title"), win => Folk.Read(win, stories[i].Get("title"), Folk.Fill(S, stories[i].Body))));
                return rows;
            }, "Enter reads a story.");
        }

        private sealed class Stay { public string Week { get; set; } = ""; public string Neighbor { get; set; } = ""; public int Story { get; set; } }

        private static void Sleepover(Window? owner)
        {
            var now = DateTime.Now;
            var stay = S.World.GetKept<Stay>("folk.sleepover");
            var stories = Folk.Blocks("folk.bedtime");
            if (stories.Count == 0) return;
            if (stay.Week != Folk.WeekKey(now))
            {
                // Goldie stays over too, but she is not the one who reads aloud.
                var guests = Ids.Where(id => id != "goldie").ToList();
                int pick = Folk.Choose(owner, "Sleepover", "Who should stay over in the guest bedroom this week?", guests.Select(id => NameOf(S, id) + ", " + Folk.Fill(S, Neighbors().First(n => n.Get("id") == id).Get("kind"))).ToList());
                if (pick < 0) return;
                stay = new Stay { Week = Folk.WeekKey(now), Neighbor = guests[pick], Story = Folk.Rng.Next(stories.Count) };
                S.World.SetKept("folk.sleepover", stay);
                S.World.Save();
            }
            string who = NameOf(S, stay.Neighbor);
            Folk.Read(owner, "Sleepover with " + who, who + " is staying over in the guest bedroom, with a blanket and far too many pillows. At bedtime, " + who + " tells a story.\n\n"
                + Folk.Fill(S, stories[Folk.Mod(stay.Story, stories.Count)].Body, ("neighbor", who)));
        }

        // ---- The newspaper and the anniversary ----

        public static string Paper(Store s, DateTime weekOf)
        {
            var from = Engine.WeekStart(weekOf);
            int week = Folk.Weeks(s, from);
            int done = Folk.FinishedBetween(s, from, from.AddDays(7));
            var paras = new List<string>
            {
                "The Acorn Gazette, the week of " + from.ToString("MMMM d") + ".",
                Folk.Nth(s, "folk.headlines", week, ("count", done.ToString())),
            };
            var days = Enumerable.Range(0, 7).Select(i => from.AddDays(i)).Select(d => (d, n: Folk.FinishedBetween(s, d, d.AddDays(1)))).OrderByDescending(x => x.n).First();
            paras.Add("Your wins: " + Folk.Plural(done, "task", "tasks") + " finished" + (days.n > 0 ? ", the most on " + days.d.ToString("dddd") + " with " + days.n : "") + ". "
                + Engine.PointsInWeek(s.Data, from) + " points earned." + (s.Data.Game.Badges.Count(b => b.Value >= from && b.Value < from.AddDays(7)) is int bc and > 0 ? " " + Folk.Plural(bc, "badge", "badges") + " earned." : ""));
            if (MayorRule(s, from.AddDays(6) < DateTime.Now ? from.AddDays(6) : DateTime.Now) is string rule) paras.Add("From the town hall. " + rule);
            paras.Add("At the pond: " + DuckStage(s, from));
            paras.Add("Pip hears things. " + Folk.Nth(s, "folk.gossip", week * 2) + " " + Folk.Nth(s, "folk.gossip", week * 2 + 1));
            return string.Join("\n\n", paras);
        }

        private static void Paper(Window? owner)
        {
            var now = DateTime.Now;
            Folk.List(owner, "The Acorn Gazette", () => new List<Folk.Row>
            {
                new("This week's paper", win => Folk.Read(win, "The Acorn Gazette", Paper(S, now))),
                new("Last week's paper", win => Folk.Read(win, "The Acorn Gazette, last week", Paper(S, now.AddDays(-7)))),
            }, "Enter reads the paper.");
        }

        public static string Recap(Store s, DateTime from, DateTime to)
        {
            int done = Folk.FinishedBetween(s, from, to);
            int chapters = Ids.Sum(id => Unlocked(s, id, to));
            int visitors = s.World.GetKept<List<Guest>>("folk.visitors").Count(g => g.When >= from && g.When < to);
            int adopted = Folk.FinishedBetween(s, Folk.Start(s), to) / PerAnimal;
            return "Since " + from.ToString("MMMM d, yyyy") + ": " + Folk.Plural(done, "task", "tasks") + " finished, " + Folk.Plural(chapters, "story chapter", "story chapters") + " opened, "
                + Folk.Plural(visitors, "visitor", "visitors") + " welcomed and " + Folk.Plural(adopted, "shelter animal", "shelter animals") + " sent home to happy families.";
        }

        /// <summary>The recap of her time in the village. The anniversary itself is celebrated once, by the seasons calendar, on the same day.</summary>
        public static string AnniversaryText(Store s, DateTime now)
        {
            var start = Folk.Start(s);
            int years = now.Year - start.Year - (now.Date < start.AddYears(now.Year - start.Year) ? 1 : 0);
            var last = start.AddYears(Math.Max(0, years));
            string when = years >= 1 && (now.Date - last).TotalDays <= 7
                ? "Your village anniversary was " + Folk.Day(last) + ": " + Folk.Plural(years, "year", "years") + " in the village. The seasons calendar has the party."
                : "Your " + (years < 1 ? "first" : "next") + " village anniversary is on " + start.AddYears(years + 1).ToString("MMMM d, yyyy") + ". The seasons calendar will have the party.";
            return when + "\n\n" + Recap(s, years < 1 ? start : last, now);
        }

        private static void Anniversary(Window? owner) => Folk.Read(owner, "Village anniversary", AnniversaryText(S, DateTime.Now));

        // ---- Bunny's radio drama (Part 10 item 9) ----

        public sealed class Episode { public string Week { get; set; } = ""; public string Title { get; set; } = ""; public string Text { get; set; } = ""; }

        public static List<Episode> Episodes(Store s) => s.World.GetKept<List<Episode>>("folk.radio");

        /// <summary>This week's episode, from the library: an opening, scenes, her week's wins (only lists Bunny may see) and a sign-off.</summary>
        public static Episode Compose(Store s, DateTime now, string? fresh = null)
        {
            int week = Folk.Weeks(s, now), n = Episodes(s).Count + 1;
            int wins = Folk.VisibleWins(s, Engine.WeekStart(now)).Count;
            var scenes = Folk.Blocks("folk.radio.scene");
            var paras = new List<string> { Folk.Pick(s, "folk.radio.open") };
            for (int i = 0; i < 3; i++)
            {
                if (i == 2 && fresh != null) { paras.Add(fresh); continue; }
                if (scenes.Count > 0) paras.Add(Folk.Fill(s, scenes[Folk.Mod(week * 3 + i, scenes.Count)].Body));
                if (i == 1) paras.Add(Folk.Pick(s, "folk.radio.wins", ("count", wins.ToString())));
            }
            paras.Add(Folk.Pick(s, "folk.radio.close"));
            return new Episode { Week = Folk.WeekKey(now), Title = "Episode " + n + ", the week of " + Engine.WeekStart(now).ToString("MMMM d"), Text = string.Join("\n\n", paras) };
        }

        private static async void Radio(Window? owner)
        {
            var now = DateTime.Now;
            if (!Episodes(S).Any(e => e.Week == Folk.WeekKey(now)))
            {
                var wins = Folk.VisibleWins(S, Engine.WeekStart(now));
                if (FolkBunny.WillWrite(S, "folk.radio")) Folk.Say(owner, "Tuning in. The village radio is recording this week's episode.");
                string fresh = await Folk.Write(S, "folk.radio", "Write one scene for this week's Village Radio drama about Bunny's village and its neighbors (Hazel the hedgehog librarian, Otis the night-shift owl, Pip the gossiping squirrel, Rocco the rooster, Clover the pony, Bramble the badger, Fennel the frog). Two or three short paragraphs separated by a blank line, under 900 characters in all, sound effects described in words, gentle and funny."
                    + (wins.Count > 0 ? " The village is cheering her week: she finished " + string.Join(", ", wins.Take(4)) + "." : ""), () => "");
                var ep = Compose(S, now, fresh.Length > 0 ? fresh : null);
                var all = Episodes(S);
                all.Add(ep);
                S.World.SetKept("folk.radio", all);
                S.World.Save();
            }
            Folk.List(owner, "Bunny's radio drama", () => Enumerable.Reverse(Episodes(S)).Select(e => new Folk.Row(e.Title, win => Folk.Read(win, e.Title, e.Text))).ToList(),
                "Newest first. Enter plays an episode: it opens as text, a paragraph at a time.");
        }

        // ---- The animal shelter (sweep 12.1 item 4) ----

        public const int PerAnimal = 12, PerStep = 2;

        /// <summary>Fostering: every two finished tasks is a step (feeding, a walk, a bath, the vet, a trick, meeting the family); after six, a happy adoption.</summary>
        public static (int Animal, int Step) Fostering(Store s, DateTime now)
        {
            int n = Folk.FinishedSince(s, Folk.Start(s));
            return (n / PerAnimal, n % PerAnimal / PerStep);
        }

        private static Dictionary<string, string> Animal(int i) => Folk.Item("folk.shelter.animals", i);
        private static (string, string)[] AnimalWords(int i) => new[] { ("animal", Animal(i).Get("name")), ("kind", Animal(i).Get("kind")) };

        /// <summary>The line for a step just reached by a finished task, once each, or null.</summary>
        public static string? ShelterLine(Store s, DateTime now)
        {
            int n = Folk.FinishedSince(s, Folk.Start(s));
            if (n == 0 || n % PerStep != 0 || !Folk.First(s, "shelter." + n)) return null;
            var (animal, step) = Fostering(s, now);
            if (step == 0) return Folk.Pick(s, "folk.shelter.adopt", AnimalWords(animal - 1)) + " Mrs. Teasel already has a new friend for you to foster: " + Animal(animal).Get("name") + ", a " + Animal(animal).Get("kind") + ".";
            return Folk.Nth(s, "folk.shelter.steps", step, AnimalWords(animal));
        }

        private static void Shelter(Window? owner)
        {
            var now = DateTime.Now;
            var (animal, step) = Fostering(S, now);
            var a = Animal(animal);
            var lines = new List<string> { "You are fostering " + a.Get("name") + ", a " + a.Get("kind") + ". " + Folk.Describe(a) };
            for (int i = step; i >= 0; i--) lines.Add("Step " + (i + 1) + " of 6. " + Folk.Nth(S, "folk.shelter.steps", i, AnimalWords(animal)));
            lines.Add("Finish a couple more tasks for the next step.");
            for (int j = animal - 1; j >= 0; j--) lines.Add("Adopted and happy: " + Animal(j).Get("name") + ". Their thank-you note says: " + Folk.Nth(S, "folk.shelter.thanks", j, AnimalWords(j)));
            TextViewerOr(owner, "The animal shelter", lines);
        }

        // ---- Bunny's band (sweep 13.3 item 24) ----

        public static string BandLine(Store s)
        {
            var inst = FolkShop.OwnedInstruments(s).Select(i => "the " + i.Name).ToList();
            if (inst.Count == 0) inst = new List<string> { "a kazoo", "a washboard", "Bramble's toolbox" };
            string words = inst.Count == 1 ? inst[0] : string.Join(", ", inst.Take(inst.Count - 1)) + " and " + inst[^1];
            return Folk.Pick(s, "folk.band", ("instruments", words));
        }

        private static void Band(Window? owner)
        {
            var sounds = FolkShop.OwnedInstruments(S).Select(i => i.Sound).Where(x => x.Length > 0).Take(3).ToList();
            if (sounds.Count == 0) sounds = new List<string> { "Pizzicato strings, bounce", "Xylophone, rising" };
            foreach (var snd in sounds) SoundBank.Play(snd);
            string line = BandLine(S) + (FolkShop.OwnedInstruments(S).Count == 0 ? " Instruments you buy at the market join the band." : "");
            FolkBunny.Remember(S, line);
            Folk.Read(owner, Bun + "'s band", line + (Folk.FestivalNear(DateTime.Now) is string f ? "\n\nThey are playing for " + f + " today." : "\n\nThey play at every festival. Next: " + Folk.NextFestival(DateTime.Now).What + "."), FolkBunny.Funny(S, line));
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Folk, village: " + what); }
            var ids = Ids;
            Need(ids.Count == 8 && ids.Contains("goldie"), "eight neighbors with Goldie");
            foreach (var id in ids) Need(Folk.Blocks("folk.chapters." + id).Count >= 10 && Folk.Blocks("folk.chapters." + id).All(c => c.Get("title").Length > 0 && c.Body.Length > 100), id + " needs 10 chapters with titles");
            foreach (var (t, n) in new[] { ("folk.letters", 20), ("folk.penpal", 12), ("folk.mystery.letters", 16), ("folk.fireside", 20), ("folk.campfire", 20), ("folk.bedtime", 20), ("folk.radio.scene", 20) })
                Need(Folk.Blocks(t).Count >= n, t + " has fewer than " + n);
            foreach (var t in new[] { "folk.goldie.guide", "folk.keeper", "folk.unlock.chapter", "folk.cousins", "folk.choir", "folk.show.winner", "folk.gossip", "folk.headlines",
                "folk.radio.open", "folk.radio.wins", "folk.radio.close", "folk.shelter.adopt", "folk.shelter.thanks", "folk.band" })
                Need(Content.Lines(t).Count >= 20, t + " has fewer than 20 lines");
            Need(Content.Lines("folk.ducks").Count >= 16 && Content.Lines("folk.babies").Count >= 16 && Content.Lines("folk.shelter.steps").Count == 6, "ducks, babies or shelter steps");
            Need(Folk.Blocks("folk.letters").All(b => ids.Contains(b.Get("from"))), "a letter from nobody");
            Need(Folk.Items("folk.mayor").GroupBy(i => i.Get("election")).Count() == 4 && Folk.Items("folk.mayor").GroupBy(i => i.Get("election")).All(g => g.Count() == 3), "four elections of three");
            Need(Folk.Items("folk.acts").Count >= 20 && Folk.Items("folk.visitors").Count >= 20 && Folk.Items("folk.shelter.animals").Count >= 20, "acts, visitors or animals");
            Need(Content.Lines("folk.goldie.guide").All(l => l.Contains("{task}") && l.Contains("{goldie}")), "Goldie's lines must name her and the task");

            // Chapters: one a week, and only with enough friendship; un-finishing takes friendship back.
            var d = store.Data;
            var keepLog = d.Log.ToList();
            d.Log.Clear();
            var now = new DateTime(2026, 11, 4, 10, 0, 0);
            Folk.SetStart(store, now.AddDays(-70));
            Need(Unlocked(store, "hazel", now) == 1, "only the first chapter with no friendship");
            var tasks = new List<Guid>();
            for (int i = 0; tasks.Count(t => FriendFor(t) == "hazel") < 6; i++) tasks.Add(Guid.NewGuid());
            foreach (var t in tasks) d.Log.Add(new LogEntry { Kind = "Done", When = now.AddDays(-1), Points = 2, TaskId = t });
            Need(Friendship(store, "hazel") == 6 && Unlocked(store, "hazel", now) == 4, "friendship six opens chapter four");
            Folk.SetStart(store, now.AddDays(-7));
            Need(Unlocked(store, "hazel", now) == 2, "chapters wait for their week");
            d.Log.RemoveAll(e => e.TaskId == tasks.First(t => FriendFor(t) == "hazel"));
            Need(Friendship(store, "hazel") == 5, "un-finishing took no friendship back");
            Need(Level(store, "hazel", now) >= 1 && Level(store, "hazel", now) <= 5, "levels");

            // The shelter: two tasks a step, twelve an adoption, every line filled in.
            d.Log.Clear();
            Folk.SetStart(store, now.AddDays(-10));
            for (int i = 0; i < 14; i++) d.Log.Add(new LogEntry { Kind = "Done", When = now.AddDays(-1).AddMinutes(i), Points = 1, TaskId = Guid.NewGuid() });
            Need(Fostering(store, now) == (1, 1), "fostering steps");
            string? line = ShelterLine(store, now);
            Need(line != null && !line.Contains('{') && ShelterLine(store, now) == null, "the shelter step line once");

            // Mail: a letter a week, the mysterious neighbor never past 16, the pen pal monthly.
            Folk.SetStart(store, now.AddDays(-21));
            Need(Letters(store, now, "mail").Count == 4 && Letters(store, now, "penpal").Count >= 1, "weekly and monthly letters");
            Need(Letters(store, new DateTime(2026, 12, 28), "mystery").Count <= 16 && Letters(store, now, "mystery").Count >= 1, "the mysterious letters");
            Need(Unread(store, now) > 0, "new letters are unread");

            // Paper, radio and recap read cleanly, with no placeholder left.
            foreach (var text in new[] { Paper(store, now), Compose(store, now).Text, Recap(store, now.AddDays(-30), now), CousinsLine(store, "Halloween", now), DuckStage(store, now), BandLine(store) })
                Need(text.Length > 20 && !text.Contains('{'), "a line still has a placeholder: " + text);
            Need(Compose(store, now).Text.Split("\n\n").Length >= 6, "the radio episode has paragraphs");
            Need(Folk.Festival(new DateTime(2026, 11, 26)) == "Thanksgiving" && Folk.Festival(new DateTime(2026, 11, 19)) == null, "Thanksgiving");
            Need(ParseNames("Mo, Jo").Count == 3 && ParseNames("Mo, Jo")[1] == "Jo", "baby names");
            d.Day(now.AddDays(-2)).Mode = DayMode.RoughDay;
            Need(KeeperLog(store, now).Count == 1 && KeeperLog(store, now)[0].Contains(now.AddDays(-2).ToString("MMMM")), "the keeper's log");
            d.Day(now.AddDays(-2)).Mode = DayMode.Normal;
            Need(MayorRule(store, now) == null && Candidates(now).Count == 3, "the election");
            store.World.Set("folk.names", new Folk.Names { Goldie = "Sunny" });
            Need(Folk.Fill(store, "{goldie} walks to {task}", ("task", "laundry")) == "Sunny walks to laundry", "Goldie's name");
            Need(GoldiePick(store, now) is null || GoldiePick(store, now) is TaskItem, "Goldie's pick");
            Need(ChaptersWaiting(new List<string> { "Hazel" }) == "a new chapter from Hazel" && ChaptersWaiting(new List<string> { "A", "B", "C", "D", "E" }) == "new chapters from A, B and 3 more", "the chapters reminder");
            Need(CampfireNow(new DateTime(2026, 11, 7, 19, 0, 0)) && !CampfireNow(new DateTime(2026, 11, 6, 19, 0, 0)), "campfire night is Saturday");
            Need(AnniversaryText(store, now).Length > 40 && !AnniversaryText(store, now).Contains('{'), "the anniversary recap");
            Need(!JustFinished(store, now.AddYears(5)), "a level from a dig counted as a finish");
            store.World.Set("folk.names", new Folk.Names());
            d.Log.Clear();
            d.Log.AddRange(keepLog);
        }
    }
}
