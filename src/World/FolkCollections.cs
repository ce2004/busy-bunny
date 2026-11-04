using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    /// <summary>
    /// Collections (sweep 8.8, 8.7 Gerald, 13.3 item 20): postcards, the travel trunk, treasure maps, crafting, the lost
    /// and found, the scrapbook, the cookbook, the village quilt, snow globes, the gift exchange, migrating birds, Gerald the
    /// snail and Bunny's buried gift. Most are worked out from her days and finished tasks, so nothing is ever lost and
    /// undo takes back what a task gave. What a task adds to a bag, un-finishing it takes away (the marble jar pattern).
    /// </summary>
    public sealed class FolkCollections : IFeature
    {
        private static MainWindow _w = null!;
        private static Store S => _w.Store;
        private static string Bun => S.Data.Game.BunnyName;

        public void Attach(MainWindow w)
        {
            _w = w;
            Folk.Tool("folk.postcards", "Postcards", Drawers.Wins, "A postcard from Bunny's trip on each of your vacation days.", () => Postcards(null));
            Folk.Tool("folk.trunk", "Travel trunk", Drawers.Wins, "Souvenirs from Bunny's postcard trips, by touch and sound.", () => Trunk(null));
            Folk.Tool("folk.treasure", "Treasure hunt", Drawers.Games, "Small tasks you finish turn up map pieces; a whole map leads to a treasure.", () => Treasure(null), "map pieces from your quick wins, and the treasures they led to");
            Folk.Tool("folk.craft", "Crafting", Drawers.World, "Turn the bits Bunny finds into furniture and decor.", () => Craft(null));
            Folk.Tool("folk.lostfound", "Lost and found", Drawers.Games, "Each week a neighbor loses something; the right kind of task finds it.", () => LostFound(null));
            Folk.Tool("folk.scrapbook", "Bunny's scrapbook", Drawers.Wins, "A page for each month you have spent in the village.", () => Scrapbook(null));
            Folk.Tool("folk.cookbook", "The village cookbook", Drawers.World, "Recipes from the bakery, unlocked as you work.", () => Cookbook(null));
            Folk.Tool("folk.quilt", "The village quilt", Drawers.Wins, "A new patch each week, stitched with that week's theme.", () => Quilt(null));
            Folk.Tool("folk.globes", "Snow globes", Drawers.Wins, "One snow globe for each semester you finish; shake it to hear its scene.", () => Globes(null));
            Folk.Tool("folk.gifts", "Gift exchange", Drawers.World, "Small gifts neighbors leave on the doorstep, and a gift you can leave back.", () => Gifts(null));
            Folk.Tool("folk.birds", "Migrating birds", Drawers.World, "This season's visiting birds, their songs and their stories.", () => Birds(null));
            Folk.Tool("folk.gerald", "Gerald the snail", Drawers.World, "Bunny's pet snail, who sometimes wanders off on slow adventures.", () => Gerald(null));
            Folk.Tool("folk.buried", "Bunny's buried gift", Drawers.Games, "Bunny buries a gift each week and leaves a clue a day; dig it up at the weekend.", () => Buried(null), "this week's clues and the dig");

            Folk.Visit("folk.gerald", GeraldVisit, ask: true);
            Folk.Reminder("folk.gerald", "gerald", (s, now) => GeraldHome(s, now) == null && G(s).LostSince != null ? "Gerald, off exploring and perfectly fine" : null);
            Folk.Reminder("folk.buried", "buried", (s, now) => CanDig(s, now) && !Dug(s, now) ? "the buried gift, ready to dig up" : null);
            Folk.Reminder("folk.lostfound", "lost", (s, now) => Found(s).ContainsKey(Folk.WeekKey(now)) ? null : "this week's lost and found, " + LostItem(s, now).Get("item"));

            GameEvents.TaskDone += OnDone;
            GameEvents.TaskUndone += OnUndone;
            GameEvents.DayStarted += day =>
            {
                var now = DateTime.Now;
                if (GeraldHome(S, now) is string home) Game.Note(S, home);
                Wander(S, now, Folk.Rng);
            };
            // Only real bakery visits count toward recipes (once each, not again through the tool event).
            FolkShop.OnPlaceActivity += (place, activity) => { if (place == PlaceIds.Bakery) Bakery(S); };
        }

        private static void OnDone(TaskItem t)
        {
            var now = DateTime.Now;
            if (Tools.IsOn("folk.treasure") && t.Size == Size.Small && TreasureFor(S, t, now) is string tl) { if (!Calm.FunPaused) Folk.AfterTask(_w, tl); }
            if (Tools.IsOn("folk.lostfound") && FindLost(S, t, now) is string ll) { if (!Calm.FunPaused) Folk.AfterTask(_w, ll); }
            if (Tools.IsOn("folk.gifts") && !Calm.FunPaused && Folk.Rng.Next(5) == 0 && GiftFor(S, t, now) is string gl) Folk.AfterTask(_w, gl);
            if (Tools.IsOn("folk.gerald") && GeraldFound(S, now) is string gf && !Calm.FunPaused) Folk.AfterTask(_w, gf);
        }

        // Always, even with a tool off: what a task gave, un-finishing it takes back.
        private static void OnUndone(TaskItem t) => TakeBack(S, t);

        public static void TakeBack(Store s, TaskItem t)
        {
            var S = s;
            var tr = S.World.Get<HashSet<Guid>>("folk.treasure");
            if (tr.Remove(t.Id)) S.World.Set("folk.treasure", tr);
            // The map this task completed is no longer paid, and its find leaves the treasure chest (its points go back by themselves).
            var paid = Paid(S);
            if (paid.Where(kv => kv.Value == t.Id).Select(kv => kv.Key).ToList() is { Count: > 0 } maps)
            {
                foreach (var m in maps) paid.Remove(m);
                S.World.Set("folk.treasure.paid", paid);
                BusyBunny.Treasure.TakeBack(S, t.Id);
            }
            var lost = Found(S);
            foreach (var k in lost.Where(kv => kv.Value == t.Id).Select(kv => kv.Key).ToList()) lost.Remove(k);
            S.World.Set("folk.lost", lost);
            var gifts = GiftsGot(S);
            foreach (var k in gifts.Where(kv => kv.Value.Task == t.Id).Select(kv => kv.Key).ToList()) gifts.Remove(k);
            S.World.Set("folk.gifts", gifts);
            S.World.Save();
        }

        // ---- Postcards and the travel trunk ----

        public static List<DateTime> VacationDays(Store s, DateTime now) => s.Data.Days
            .Where(kv => kv.Value?.Mode == DayMode.Vacation && DateTime.TryParse(kv.Key, out var d) && d.Date <= now.Date)
            .Select(kv => DateTime.Parse(kv.Key).Date).OrderByDescending(d => d).ToList();

        private static void Postcards(Window? owner)
        {
            var now = DateTime.Now;
            var lines = VacationDays(S, now).Select(d =>
            {
                var p = Folk.Item("folk.postcards", Folk.Hash(S.Key(d)));
                return "From " + p.Get("place") + ", " + Folk.Day(d) + ". " + Folk.Describe(p) + " " + Folk.Fill(S, p.Get("text"));
            }).ToList();
            if (lines.Count == 0) lines.Add("No postcards yet. On each vacation day you set, " + Bun + " goes on a little trip and sends one.");
            List(owner, "Postcards", lines);
        }

        /// <summary>Vacation days next to each other are one trip; each trip brings home one souvenir.</summary>
        public static List<(DateTime From, int Days)> Trips(Store s, DateTime now)
        {
            var days = VacationDays(s, now).OrderBy(d => d).ToList();
            var trips = new List<(DateTime, int)>();
            foreach (var d in days)
            {
                if (trips.Count > 0 && trips[^1].Item1.AddDays(trips[^1].Item2) == d) trips[^1] = (trips[^1].Item1, trips[^1].Item2 + 1);
                else trips.Add((d, 1));
            }
            return trips;
        }

        private static void Trunk(Window? owner)
        {
            var lines = Enumerable.Reverse(Trips(S, DateTime.Now)).Select(t =>
            {
                var s = Folk.Item("folk.souvenirs", Folk.Hash("trip" + S.Key(t.From)));
                return s.Get("name") + ", from the trip on " + t.From.ToString("MMMM d") + ". " + Folk.Fill(S, Folk.Describe(s));
            }).ToList();
            if (lines.Count == 0) lines.Add("The trunk is empty and smells faintly of cedar. Every vacation trip brings home a souvenir.");
            List(owner, "Travel trunk", lines);
        }

        private static void List(Window? owner, string title, List<string> lines) => Folk.List(owner, title, () => lines.Select(l => new Folk.Row(l)).ToList(), "Newest first.");

        // ---- Treasure hunts ----

        public const int Pieces = 4;

        /// <summary>Which maps already paid out, and the task that completed each (so un-finishing that task takes the find back).</summary>
        public static Dictionary<int, Guid> Paid(Store s) => s.World.Get<Dictionary<int, Guid>>("folk.treasure.paid");

        /// <summary>A small finished task turns up a map piece; the fourth makes the map whole and leads to a treasure (and 3 points), once per map.</summary>
        public static string? TreasureFor(Store s, TaskItem t, DateTime now)
        {
            var set = s.World.Get<HashSet<Guid>>("folk.treasure");
            if (!set.Add(t.Id)) return null;
            s.World.Set("folk.treasure", set);
            s.World.Save();
            int n = set.Count, map = (n - 1) / Pieces;
            string mapName = Folk.Item("folk.maps", map).Get("name");
            if (n % Pieces != 0) return Folk.Pick(s, "folk.mappiece", ("map", mapName), ("left", (Pieces - n % Pieces).ToString()));
            var paid = Paid(s);
            if (paid.ContainsKey(map)) return mapName + " is whole again. Its treasure is still safe in your treasure chest.";
            paid[map] = t.Id;
            s.World.Set("folk.treasure.paid", paid);
            var tr = Folk.Item("folk.treasures", map);
            FolkShop.AddTreasure(s, tr.Get("name"), tr.Get("touch"), mapName, t.Id);
            if (Ledger.Pay(s, Ledger.Boost(s, 3, t), "A treasure from " + mapName, "folk.treasure", t.Id) is Outcome o && _w != null && s == S) Game.Queue(_w, o);
            s.Save();
            return mapName + " is whole! It led to " + tr.Get("name").ToLowerInvariant() + ". " + Folk.Describe(tr);
        }

        private static void Treasure(Window? owner)
        {
            int n = S.World.Get<HashSet<Guid>>("folk.treasure").Count, maps = n / Pieces;
            var lines = new List<string>();
            var map = Folk.Item("folk.maps", maps);
            lines.Add("Your map now: " + map.Get("name") + ". " + Folk.End(map.Get("story")) + " " + (n % Pieces) + " of " + Pieces + " pieces. Finish small tasks to find the rest.");
            for (int i = maps - 1; i >= 0; i--)
            {
                var tr = Folk.Item("folk.treasures", i);
                lines.Add("Found with " + Folk.Item("folk.maps", i).Get("name") + ": " + tr.Get("name") + ". " + Folk.Describe(tr));
            }
            List(owner, "Treasure hunt", lines);
        }

        // ---- Crafting ----

        public sealed class CraftBox { public Dictionary<string, int> Used { get; set; } = new(); public List<string> Made { get; set; } = new(); }

        /// <summary>Every third finished task brings home a crafting material; what she used is kept apart, so nothing is double-counted.</summary>
        public static Dictionary<string, int> Materials(Store s)
        {
            int n = Folk.Finished(s).Count / 3;
            var have = new Dictionary<string, int>();
            for (int i = 0; i < n; i++) { string m = Folk.Item("folk.materials", i).Get("name"); have[m] = have.GetValueOrDefault(m) + 1; }
            foreach (var (m, used) in s.World.Get<CraftBox>("folk.craft").Used) have[m] = have.GetValueOrDefault(m) - used;
            return have;
        }

        public static List<string> Needs(Dictionary<string, string> recipe) => recipe.Get("needs").Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();

        public static bool CanMake(Store s, Dictionary<string, string> recipe) { var have = Materials(s); return Needs(recipe).All(m => have.GetValueOrDefault(m) > 0); }

        /// <summary>Makes it: one Control Z step that gives the materials back.</summary>
        public static bool Make(Store s, Dictionary<string, string> recipe)
        {
            if (!CanMake(s, recipe)) return false;
            World.Change<CraftBox>(s, "folk.craft", "crafting " + recipe.Get("name"), b =>
            {
                foreach (var m in Needs(recipe)) b.Used[m] = b.Used.GetValueOrDefault(m) + 1;
                b.Made.Add(recipe.Get("name"));
            });
            FolkShop.AddArt(s, recipe.Get("name"), recipe.Get("touch"), "crafted");
            s.World.Save();
            return true;
        }

        private static void Craft(Window? owner)
        {
            Folk.List(owner, "Crafting", () =>
            {
                var have = Materials(S).Where(m => m.Value > 0).ToList();
                var rows = new List<Folk.Row> { new("Your materials: " + (have.Count == 0 ? "none yet. Every third task you finish brings one home." : string.Join(", ", have.Select(m => m.Value + " " + m.Key))) + ".") };
                foreach (var r in Folk.Items("folk.craft"))
                {
                    bool can = CanMake(S, r);
                    rows.Add(new Folk.Row(r.Get("name") + (can ? ", ready to make" : ", needs " + r.Get("needs")) + ". " + Folk.Describe(r), win =>
                    {
                        if (!Make(S, r)) { Folk.Say(win, "Not enough yet. It needs " + r.Get("needs") + "."); return; }
                        SoundBank.Play("Xylophone, rising");
                        Folk.Say(win, Bun + " made " + r.Get("name").ToLowerInvariant() + ", with only a little glue in its fur.");
                    }));
                }
                foreach (var m in Enumerable.Reverse(S.World.Get<CraftBox>("folk.craft").Made)) rows.Add(new Folk.Row("Made: " + m + "."));
                return rows;
            }, "Enter on a ready one makes it. Control Z in the main window gives the materials back.");
        }

        // ---- Lost and found ----

        public static Dictionary<string, string> LostItem(Store s, DateTime now) => Folk.Item("folk.lost", Folk.Weeks(s, now));
        public static Dictionary<string, Guid> Found(Store s) => s.World.Get<Dictionary<string, Guid>>("folk.lost");

        public static bool Matches(string rule, TaskItem t, DateTime when) => rule switch
        {
            "small" => t.Size == Size.Small,
            "large" => t.Size is Size.Large or Size.MultiDay,
            "morning" => when.Hour < 12,
            "evening" => when.Hour >= 18,
            "weekend" => when.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            _ => true,
        };

        public static string? FindLost(Store s, TaskItem t, DateTime now)
        {
            var found = Found(s);
            string week = Folk.WeekKey(now);
            var item = LostItem(s, now);
            if (found.ContainsKey(week) || !Matches(item.Get("rule"), t, now)) return null;
            found[week] = t.Id;
            s.World.Set("folk.lost", found);
            s.World.Save();
            return Folk.Pick(s, "folk.found", ("item", item.Get("item")), ("owner", item.Get("owner")));
        }

        private static void LostFound(Window? owner)
        {
            var now = DateTime.Now;
            var item = LostItem(S, now);
            var found = Found(S);
            var lines = new List<string>
            {
                found.ContainsKey(Folk.WeekKey(now)) ? "This week: " + item.Get("item") + " was found, and " + item.Get("owner") + " is delighted." : "Lost this week: " + item.Get("item") + ". " + Folk.Describe(item) + " " + Folk.End(item.Get("clue")),
            };
            foreach (var week in found.Keys.Where(k => k != Folk.WeekKey(now)).OrderByDescending(k => k))
                if (DateTime.TryParse(week, out var d)) lines.Add("Week of " + d.ToString("MMMM d") + ": found " + LostItem(S, d).Get("item") + ".");
            List(owner, "Lost and found", lines);
        }

        // ---- Scrapbook, cookbook, quilt, snow globes ----

        public static string Page(Store s, DateTime month, DateTime now)
        {
            var from = new DateTime(month.Year, month.Month, 1);
            var to = from.AddMonths(1);
            int done = Folk.FinishedBetween(s, from, to);
            int badges = s.Data.Game.Badges.Count(b => b.Value >= from && b.Value < to);
            int trips = VacationDays(s, now).Count(d => d >= from && d < to);
            var best = Enumerable.Range(0, (to - from).Days).Select(i => from.AddDays(i)).Select(d => (d, n: Folk.FinishedBetween(s, d, d.AddDays(1)))).OrderByDescending(x => x.n).First();
            string page = from.ToString("MMMM yyyy") + (now < to ? ", still being filled in" : "") + ". " + Folk.Plural(done, "task", "tasks") + " finished"
                + (best.n > 0 ? ", the most on " + best.d.ToString("MMMM d") : "") + "." + (badges > 0 ? " " + Folk.Plural(badges, "badge", "badges") + " stuck on." : "")
                + (trips > 0 ? " " + Folk.Plural(trips, "postcard", "postcards") + " tucked in." : "");
            return page + " " + Folk.Nth(s, "folk.scrapbook", Folk.Hash(from.ToString("yyyy-MM")), ("month", from.ToString("MMMM")));
        }

        private static void Scrapbook(Window? owner)
        {
            var now = DateTime.Now;
            var start = Folk.Start(S);
            var lines = new List<string>();
            for (var m = new DateTime(now.Year, now.Month, 1); m >= new DateTime(start.Year, start.Month, 1) && lines.Count < 36; m = m.AddMonths(-1)) lines.Add(Page(S, m, now));
            List(owner, Bun + "'s scrapbook", lines);
        }

        private sealed class Count { public int N { get; set; } }
        private static void Bakery(Store s) { var c = s.World.GetKept<Count>("folk.bakery"); c.N++; s.World.SetKept("folk.bakery", c); s.World.Save(); }

        /// <summary>One recipe to start, one more for every 8 finished tasks, and one for every two visits to the bakery.</summary>
        public static int Recipes(Store s) => Math.Min(Folk.Items("folk.recipes").Count, 1 + Folk.Finished(s).Count / 8 + s.World.GetKept<Count>("folk.bakery").N / 2);

        private static void Cookbook(Window? owner)
        {
            int n = Recipes(S);
            var lines = Enumerable.Range(0, n).Reverse().Select(i => { var r = Folk.Item("folk.recipes", i); return r.Get("name") + ". " + Folk.Describe(r) + " " + Folk.End(r.Get("steps")); }).ToList();
            if (n < Folk.Items("folk.recipes").Count) lines.Add("The next recipe comes after " + (8 - Folk.Finished(S).Count % 8) + " more finished tasks, or a visit or two to the bakery.");
            List(owner, "The village cookbook", lines);
        }

        public static string Patch(Store s, DateTime week)
        {
            string season = RealWorld.Season(week);
            var patches = Folk.Items("folk.quilt").Where(q => q.Get("season") == season).ToList();
            if (patches.Count == 0) return "";
            var p = patches[Folk.Mod(Folk.Weeks(s, week), patches.Count)];
            int done = Folk.FinishedBetween(s, Engine.WeekStart(week), Engine.WeekStart(week).AddDays(7));
            return p.Get("fabric") + ". " + Folk.Describe(p) + " " + Folk.End(p.Get("theme")) + (done > 0 ? " Stitched during a week of " + Folk.Plural(done, "finished task", "finished tasks") + "." : " A restful week's patch, extra soft.");
        }

        private static void Quilt(Window? owner)
        {
            var now = DateTime.Now;
            int weeks = Folk.Weeks(S, now);
            var lines = new List<string>();
            for (int i = weeks; i >= Math.Max(0, weeks - 52); i--)
            {
                var week = Engine.WeekStart(Folk.Start(S)).AddDays(7 * i);
                lines.Add((i == weeks ? "This week's patch, still on the needle: " : "Week of " + week.ToString("MMMM d") + ": ") + Patch(S, week));
            }
            List(owner, "The village quilt, " + Folk.Plural(weeks + 1, "patch", "patches"), lines);
        }

        public static string SemesterName(int index) => ((index % 3) switch { 0 => "Spring ", 1 => "Summer ", _ => "Fall " }) + index / 3;

        /// <summary>One globe for each semester that ended since she came to the village.</summary>
        public static List<int> FinishedSemesters(Store s, DateTime now) =>
            Enumerable.Range(Folk.SemesterIndex(Folk.Start(s)), Math.Max(0, Folk.SemesterIndex(now) - Folk.SemesterIndex(Folk.Start(s)))).Reverse().ToList();

        private static void Globes(Window? owner)
        {
            Folk.List(owner, "Snow globes", () =>
            {
                var rows = FinishedSemesters(S, DateTime.Now).Select(i =>
                {
                    var g = Folk.Item("folk.globes", i);
                    return new Folk.Row("The " + SemesterName(i) + " snow globe: " + Folk.Fill(S, g.Get("scene")) + ".", win =>
                    {
                        SoundBank.Play("Singing bells, sparkle");
                        Folk.Say(win, "You shake it. " + Folk.End(g.Get("touch")) + " " + Folk.End(g.Get("sound")) + " Inside: " + Folk.Fill(S, g.Get("scene")) + ".");
                    });
                }).ToList();
                if (rows.Count == 0) rows.Add(new Folk.Row("Your first snow globe comes when this semester ends, around " + GameFeature.Semester(DateTime.Now).To.ToString("MMMM d") + "."));
                return rows;
            }, "Enter shakes a globe.");
        }

        // ---- Gift exchange ----

        public sealed class Got { public int Index { get; set; } public Guid Task { get; set; } public DateTime When { get; set; } }
        public static Dictionary<string, Got> GiftsGot(Store s) => s.World.Get<Dictionary<string, Got>>("folk.gifts");

        /// <summary>At most one doorstep gift a week, tied to the task that brought it.</summary>
        public static string? GiftFor(Store s, TaskItem t, DateTime now)
        {
            var got = GiftsGot(s);
            string week = Folk.WeekKey(now);
            if (got.ContainsKey(week)) return null;
            int i = Folk.Hash(week + t.Id);
            got[week] = new Got { Index = i, Task = t.Id, When = now };
            s.World.Set("folk.gifts", got);
            s.World.Save();
            var g = Folk.Item("folk.gifts", i);
            return Folk.Pick(s, "folk.gift.arrive", ("gift", g.Get("gift")), ("from", g.Get("from")));
        }

        /// <summary>Leaves this week's gift: the friendship and the "given" mark are one Control Z step, so undo gives the week back.</summary>
        public static void LeaveGift(Store s, string neighbor, DateTime now)
        {
            FolkVillage.AddFriendship(s, neighbor, 2);
            var left = s.World.Get<Dictionary<string, string>>("folk.gifts.left");
            left[Folk.WeekKey(now)] = neighbor;
            s.World.Set("folk.gifts.left", left);
            s.World.Save();
        }

        private static void Gifts(Window? owner)
        {
            Folk.List(owner, "Gift exchange", () =>
            {
                var now = DateTime.Now;
                var rows = new List<Folk.Row>();
                var given = S.World.Get<Dictionary<string, string>>("folk.gifts.left");
                if (!given.ContainsKey(Folk.WeekKey(now)))
                    rows.Add(new Folk.Row("Leave a little gift on a neighbor's doorstep this week. Enter to choose who.", win =>
                    {
                        var ids = FolkVillage.Ids;
                        int pick = Folk.Choose(win, "Leave a gift", "Who gets a gift on their doorstep?", ids.Select(id => FolkVillage.NameOf(S, id)).ToList());
                        if (pick < 0) return;
                        LeaveGift(S, ids[pick], now);
                        Folk.Say(win, Bun + " tiptoes over and leaves a ribbon-tied parcel for " + FolkVillage.NameOf(S, ids[pick]) + ". Your friendship grew a little.");
                    }));
                else rows.Add(new Folk.Row("This week you left a gift for " + FolkVillage.NameOf(S, given[Folk.WeekKey(now)]) + ". Another next week."));
                foreach (var (week, got) in GiftsGot(S).OrderByDescending(g => g.Key))
                {
                    var g = Folk.Item("folk.gifts", got.Index);
                    rows.Add(new Folk.Row(got.When.ToString("MMMM d") + ": " + g.Get("gift") + ", from " + g.Get("from") + ". " + Folk.Describe(g)));
                }
                if (GiftsGot(S).Count == 0) rows.Add(new Folk.Row("No doorstep gifts yet. Now and then, at most once a week, a neighbor leaves one after you finish a task."));
                return rows;
            }, "Enter on the first line leaves a gift for a neighbor.");
        }

        // ---- Migrating birds ----

        public static List<Dictionary<string, string>> BirdsNow(DateTime now)
        {
            string season = RealWorld.Season(now);
            int startMonth = season switch { "Winter" => 12, "Spring" => 3, "Summer" => 6, _ => 9 };
            var start = new DateTime(now.Month < startMonth && startMonth == 12 ? now.Year - 1 : now.Year, startMonth, 1);
            int weeks = (int)((now.Date - start).TotalDays / 7);
            return Folk.Items("folk.birds").Where(b => b.Get("season") == season).Take(weeks + 1).ToList();
        }

        private static void Birds(Window? owner)
        {
            var now = DateTime.Now;
            var lines = Enumerable.Reverse(BirdsNow(now)).Select(b => b.Get("name") + ". " + Folk.End(b.Get("sound")) + " " + Folk.End(b.Get("touch")) + " " + Folk.End(b.Get("story"))).ToList();
            lines.Add("A new bird arrives each week of the " + RealWorld.Season(now).ToLowerInvariant() + ", and new ones come with every season.");
            List(owner, "Migrating birds", lines);
        }

        // ---- Gerald the snail (sweep 8.7) ----

        public const int GeraldLevel = 4;
        public sealed class GeraldBox { public DateTime Adopted { get; set; } public DateTime? LostSince { get; set; } public int Need { get; set; } public int Found { get; set; } }
        public static GeraldBox G(Store s) => s.World.GetKept<GeraldBox>("folk.gerald");
        private static void SetG(Store s, GeraldBox g) { s.World.SetKept("folk.gerald", g); s.World.Save(); }

        /// <summary>
        /// Some ordinary mornings Gerald wanders off (never far, never in danger, never on a rest or rough day). He comes
        /// home by himself within two days; finished tasks only find him sooner, so a slow day never keeps him away.
        /// </summary>
        public static bool Wander(Store s, DateTime now, Random rng)
        {
            var g = G(s);
            if (g.Adopted == default || g.LostSince != null || Calm.FunPaused || !Tools.IsOn("folk.gerald") || Folk.RestToday(s, now) || rng.Next(6) != 0) return false;
            g.LostSince = now;
            g.Need = 2 + rng.Next(2);
            SetG(s, g);
            return true;
        }

        public const double GeraldDays = 2;

        public static string? GeraldFound(Store s, DateTime now)
        {
            var g = G(s);
            if (g.LostSince is not DateTime since || Folk.FinishedSince(s, since) < g.Need) return null;
            g.LostSince = null;
            g.Found++;
            SetG(s, g);
            return Folk.Pick(s, "folk.gerald.found");
        }

        /// <summary>After two days Gerald strolls home by himself, whatever she did. The line to keep, or null.</summary>
        public static string? GeraldHome(Store s, DateTime now)
        {
            var g = G(s);
            if (g.LostSince is not DateTime since || (now - since).TotalDays < GeraldDays) return null;
            g.LostSince = null;
            g.Found++;
            SetG(s, g);
            return "Gerald strolled home by himself after his slow adventure, perfectly fine and a little smug about it.";
        }

        /// <summary>The adoption is news, said once in a visit (no window: the story waits in the Gerald tool).</summary>
        private static IEnumerable<string> GeraldVisit(MainWindow w, DateTime now)
        {
            var g = G(S);
            if (g.Adopted != default || Engine.LevelFor(Engine.Total(S.Data)) < GeraldLevel || Calm.FunPaused || !Folk.TryAsk()) return Array.Empty<string>();
            g.Adopted = now;
            SetG(S, g);
            return new[] { Bun + " adopted a pet snail called Gerald. Gerald the snail, in the toolbox, tells you how they met." };
        }

        private static void Gerald(Window? owner)
        {
            var now = DateTime.Now;
            var g = G(S);
            if (g.Adopted == default) { Folk.Say(owner, "At level " + GeraldLevel + ", " + Bun + " adopts a pet. You are level " + Engine.LevelFor(Engine.Total(S.Data)) + "."); return; }
            if (Folk.First(S, "gerald.story"))
            {
                Folk.Read(owner, Bun + " has a pet", Folk.Fill(S, Folk.NthBlock("folk.gerald.adopt", 0)?.Body ?? ""));
                return;
            }
            if (GeraldHome(S, now) is string home) { Folk.Say(owner, home); return; }
            if (g.LostSince is DateTime since)
            {
                Folk.Read(owner, "Lost pet poster", Folk.Nth(S, "folk.gerald.posters", Folk.Hash(since.ToString("o"))) + "\n\nHe is fine, just exploring, and he comes home by himself in a day or two. There is no rush.");
                return;
            }
            FolkBunny.Tell(_w, Folk.Pick(S, "folk.gerald.doing") + (g.Found > 0 ? " He has been found " + Folk.Plural(g.Found, "time", "times") + " so far, always perfectly fine." : ""));
        }

        // ---- Bunny's buried gift (sweep 13.3 item 20) ----

        public static Dictionary<string, string> BuriedItem(Store s, DateTime now) => Folk.Item("folk.buried", Folk.Weeks(s, now));

        /// <summary>One clue to start the week, and one more for each day she finished something, up to five.</summary>
        public static int Clues(Store s, DateTime now)
        {
            var from = Engine.WeekStart(now);
            int days = Folk.Finished(s).Where(x => x.When >= from && x.When < from.AddDays(7)).Select(x => x.When.Date).Distinct().Count();
            return Math.Min(5, 1 + days);
        }

        public static bool CanDig(Store s, DateTime now) => Clues(s, now) >= 5 || now.DayOfWeek is DayOfWeek.Friday or DayOfWeek.Saturday or DayOfWeek.Sunday;
        public static bool Dug(Store s, DateTime now) => s.World.Get<Dictionary<string, bool>>("folk.buried").ContainsKey(Folk.WeekKey(now));

        /// <summary>Digs it up: 3 points (one Control Z step) and the gift goes in the treasure chest.</summary>
        public static bool Dig(Store s, DateTime now, MainWindow? w)
        {
            if (!CanDig(s, now) || Dug(s, now)) return false;
            var item = BuriedItem(s, now);
            if (w != null) Ledger.Pay(w, 3, "Dug up " + Bun + "'s buried gift", "folk.buried", null, true, PayVoice.Silent);
            else Ledger.Pay(s, 3, "Dug up the buried gift", "folk.buried", null, true);
            var dug = s.World.Get<Dictionary<string, bool>>("folk.buried");
            dug[Folk.WeekKey(now)] = true;
            s.World.Set("folk.buried", dug);
            FolkShop.AddTreasure(s, item.Get("gift"), item.Get("touch"), "Bunny's buried gift");
            s.World.Save();
            s.Save();
            return true;
        }

        private static void Buried(Window? owner)
        {
            Folk.List(owner, Bun + "'s buried gift", () =>
            {
                var now = DateTime.Now;
                var item = BuriedItem(S, now);
                var rows = new List<Folk.Row>();
                if (Dug(S, now)) rows.Add(new Folk.Row("You dug it up: " + item.Get("gift") + ". " + Folk.Describe(item) + " A new one gets buried next week."));
                else
                {
                    int clues = Clues(S, now);
                    for (int i = clues; i >= 1; i--) rows.Add(new Folk.Row("Clue " + i + ": " + Folk.Fill(S, item.Get("clue" + i))));
                    if (CanDig(S, now)) rows.Insert(0, new Folk.Row("Dig it up now. Enter to grab the little shovel.", win =>
                    {
                        if (!Dig(S, now, _w)) return;
                        SoundBank.Play("Bunny, binky");
                        Folk.Say(win, "Scrape, scrape, thunk. You found " + item.Get("gift").ToLowerInvariant() + ". " + Folk.Describe(item) + " Plus 3 points. " + PlayTime.SpendLine(S.Data) + ".");
                    }));
                    else rows.Add(new Folk.Row("Each day you finish something brings a new clue. You can dig at the weekend, or as soon as all five clues are in."));
                }
                return rows;
            }, "Newest clue first. Enter on the first line digs when it is time.");
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Folk, collections: " + what); }
            foreach (var (t, n) in new[] { ("folk.postcards", 20), ("folk.souvenirs", 20), ("folk.maps", 10), ("folk.treasures", 20), ("folk.materials", 10), ("folk.craft", 20),
                ("folk.lost", 20), ("folk.recipes", 20), ("folk.quilt", 20), ("folk.globes", 20), ("folk.gifts", 20), ("folk.birds", 20), ("folk.buried", 10) })
                Need(Folk.Items(t).Count >= n, t + " has fewer than " + n);
            foreach (var t in new[] { "folk.mappiece", "folk.found", "folk.scrapbook", "folk.gift.arrive", "folk.gerald.posters", "folk.gerald.found", "folk.gerald.doing" })
                Need(Content.Lines(t).Count >= 20, t + " has fewer than 20 lines");
            var mats = Folk.Items("folk.materials").Select(m => m.Get("name")).ToHashSet();
            Need(Folk.Items("folk.craft").All(r => Needs(r).Count >= 2 && Needs(r).All(mats.Contains)), "a recipe needs a material that does not exist");
            Need(Folk.Items("folk.lost").All(l => l.Get("rule") is "small" or "large" or "morning" or "evening" or "any" or "weekend"), "a lost item has an unknown rule");
            Need(Folk.Items("folk.quilt").GroupBy(q => q.Get("season")).Count() == 4 && Folk.Items("folk.birds").GroupBy(q => q.Get("season")).All(g => g.Count() >= 6), "quilt patches and birds for every season");

            var d = store.Data;
            var keepLog = d.Log.ToList();
            d.Log.Clear();
            var now = new DateTime(2026, 11, 4, 10, 0, 0);
            Folk.SetStart(store, now.AddDays(-30));

            // Treasure: four small tasks make a whole map; done, back, done again counts once.
            store.World.Set("folk.treasure", new HashSet<Guid>());
            var small = Enumerable.Range(0, 4).Select(i => new TaskItem { Name = "small " + i, List = d.Lists[0].Name, Size = Size.Small }).ToList();
            Need(TreasureFor(store, small[0], now) is string p1 && !p1.Contains('{'), "a map piece line");
            Need(TreasureFor(store, small[0], now) == null, "the same task found two pieces");
            for (int i = 1; i < 3; i++) TreasureFor(store, small[i], now);
            int before = Engine.Total(d);
            Need(TreasureFor(store, small[3], now) is string whole && whole.Contains("whole") && Engine.Total(d) > before, "a whole map pays and says so");
            Need(d.Log.Any(e => e.TaskId == small[3].Id && e.Feature == "folk.treasure"), "the treasure points carry the task, so un-finishing takes them back");
            Need(BusyBunny.Treasure.All(store).Count(f => f.TaskId == small[3].Id) == 1, "the treasure did not reach the treasure chest with its task");
            // Un-finishing another piece and finishing it again must not pay or add a second find.
            int paidBefore = d.Log.Count(e => e.Feature == "folk.treasure");
            TakeBack(store, small[0]);
            Need(TreasureFor(store, small[0], now) is string again && !again.Contains('{'), "the piece came back");
            Need(d.Log.Count(e => e.Feature == "folk.treasure") == paidBefore && BusyBunny.Treasure.All(store).Count(f => f.Where == Folk.Item("folk.maps", 0).Get("name")) == 1, "a whole map paid twice");
            // Un-finishing the task that completed the map takes its find out of the chest.
            TakeBack(store, small[3]);
            Need(BusyBunny.Treasure.All(store).All(f => f.TaskId != small[3].Id) && !Paid(store).ContainsKey(0), "un-finishing did not take the treasure back");
            TreasureFor(store, small[3], now);
            Need(BusyBunny.Treasure.All(store).Count(f => f.TaskId == small[3].Id) == 1, "finishing again did not find the treasure again");

            // A gift left for a neighbor and the friendship it gave are one Control Z step.
            string nb = FolkVillage.Ids[0];
            int fr = FolkVillage.Friendship(store, nb);
            LeaveGift(store, nb, now);
            Need(FolkVillage.Friendship(store, nb) == fr + 2 && store.World.Get<Dictionary<string, string>>("folk.gifts.left").ContainsKey(Folk.WeekKey(now)), "leaving a gift");
            store.Undo();
            d = store.Data;
            Need(FolkVillage.Friendship(store, nb) == fr && !store.World.Get<Dictionary<string, string>>("folk.gifts.left").ContainsKey(Folk.WeekKey(now)), "undo of a gift left the week used up");

            // Lost and found: only the right kind of task, once a week, and un-finishing puts it back.
            store.World.Set("folk.lost", new Dictionary<string, Guid>());
            var item = LostItem(store, now);
            var fits = new TaskItem { Name = "fits", Size = item.Get("rule") == "large" ? Size.Large : Size.Small };
            var when = item.Get("rule") switch { "evening" => now.Date.AddHours(19), "weekend" => now.Date.AddDays(3).AddHours(10), _ => now.Date.AddHours(9) };
            Need(Matches(item.Get("rule"), fits, when), "the test task should match the rule");
            Need(FindLost(store, fits, when) is string fl && !fl.Contains('{') && FindLost(store, fits, when) == null, "found once a week");
            Need(!Matches("small", new TaskItem { Size = Size.Large }, now) && Matches("evening", new TaskItem(), now.Date.AddHours(20)) && !Matches("morning", new TaskItem(), now.Date.AddHours(13)), "lost and found rules");

            // Crafting: materials from tasks, used once, and undo gives them back.
            for (int i = 0; i < 60; i++) d.Log.Add(new LogEntry { Kind = "Done", When = now.AddDays(-1).AddMinutes(i), Points = 1, TaskId = Guid.NewGuid() });
            var recipe = Folk.Items("folk.craft").First(r => CanMake(store, r));
            var mat = Needs(recipe)[0];
            int had = Materials(store)[mat];
            Need(Make(store, recipe) && Materials(store)[mat] == had - 1, "crafting used the materials");
            store.Undo();
            d = store.Data;
            Need(Materials(store)[mat] == had && store.World.Get<CraftBox>("folk.craft").Made.Count == 0, "undo did not give the materials back");

            // Gerald wanders, and finished tasks after that find him.
            store.World.SetKept("folk.gerald", new GeraldBox { Adopted = now.AddDays(-5) });
            var rng = new Random(1);
            int tries = 0;
            while (!Wander(store, now, rng) && tries++ < 100) { }
            Need(G(store).LostSince != null && GeraldFound(store, now) == null, "Gerald wandered and is not found with no tasks");
            for (int i = 0; i < 3; i++) d.Log.Add(new LogEntry { Kind = "Done", When = now.AddMinutes(5 + i), Points = 1, TaskId = Guid.NewGuid() });
            Need(GeraldFound(store, now.AddHours(1)) is string gf && gf.Length > 0 && G(store).LostSince == null, "Gerald found by tasks");
            // He never wanders on a rough day, and comes home by himself after two days with no tasks at all.
            d.Day(now).Mode = DayMode.RoughDay;
            Need(Enumerable.Range(0, 100).All(_ => !Wander(store, now, rng)), "Gerald wandered off on a rough day");
            d.Day(now).Mode = DayMode.Normal;
            store.World.SetKept("folk.gerald", new GeraldBox { Adopted = now.AddDays(-5), LostSince = now.AddDays(30), Need = 3 });
            Need(GeraldHome(store, now.AddDays(31)) == null && GeraldHome(store, now.AddDays(32.5)) is string gh && gh.Length > 0 && G(store).LostSince == null, "Gerald did not come home by himself");

            // The buried gift: clues grow by day, the dig pays once and is one undo step.
            var monday = Engine.WeekStart(now);
            Need(Clues(store, monday.AddHours(9)) >= 1 && Clues(store, monday.AddHours(9)) <= 5, "clues");
            var friday = monday.AddDays(4).AddHours(10);
            int pts = Engine.Total(d);
            Need(Dig(store, friday, null) && Engine.Total(d) == pts + 3 && !Dig(store, friday, null), "digging pays once");
            store.Undo();
            d = store.Data;
            Need(Engine.Total(d) == pts && !Dug(store, friday), "undo of the dig");

            // Trips, pages and patches read cleanly.
            d.Day(now.AddDays(-3)).Mode = DayMode.Vacation;
            d.Day(now.AddDays(-2)).Mode = DayMode.Vacation;
            d.Day(now.AddDays(-9)).Mode = DayMode.Vacation;
            Need(Trips(store, now).Count == 2 && Trips(store, now).Any(t => t.Days == 2), "vacation days make trips");
            foreach (var text in new[] { Page(store, now, now), Patch(store, now), Patch(store, new DateTime(2026, 1, 5)) })
                Need(text.Length > 20 && !text.Contains('{'), "a line has a placeholder: " + text);
            Need(FinishedSemesters(store, now).Count == 0 && FinishedSemesters(store, new DateTime(2027, 2, 1)).Count == 1, "snow globes per finished semester");
            Need(BirdsNow(new DateTime(2026, 12, 2)).Count == 1 && BirdsNow(new DateTime(2026, 2, 27)).Count == 6, "birds arrive weekly through the season");
            foreach (var k in new[] { -3, -2, -9 }) d.Day(now.AddDays(k)).Mode = DayMode.Normal;
            d.Log.Clear();
            d.Log.AddRange(keepLog);
        }
    }
}
