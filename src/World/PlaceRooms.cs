using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    /// <summary>A fish living in the aquarium, from the pond.</summary>
    public sealed class Fish { public string Name { get; set; } = ""; public string Touch { get; set; } = ""; public DateTime When { get; set; } }

    /// <summary>
    /// The burrow and its rooms (sweep 8.6, 11.1, 11.2, 12.3, 12.4): points to spend first, every room she built (the first
    /// burrow's and the new ones), the deck, the tunnels, the weather window, secret passages and the puzzle table. Inside a
    /// room: how it feels with its upgrade and theme, her things there, moving them, the room's shop, and the room's own fun.
    /// </summary>
    public static class PlaceRooms
    {
        public static readonly (string Id, string Name, int Price, string Brief)[] New =
        {
            ("kitchen", "The kitchen", 90, "Bunny cooks and leaves you a morning snack"),
            ("treasure", "The treasure room", 100, "the chest with everything you have found"),
            ("trophy", "The trophy room", 110, "your badges and records on display"),
            ("bedroom", "Bunny's bedroom", 120, "Bunny's own bed, blankets and pillows"),
            ("art", "The art room", 120, "everything you made, and the photo booth album"),
            ("gym", "Bunny's gym", 120, "a plate on the bar for every sprint"),
            ("pottery", "The pottery room", 130, "a pot shaped one step with each sprint"),
            ("guest", "The guest bedroom", 140, "where friends stay over"),
            ("gameroom", "The game room", 150, "an arcade of your best scores"),
            ("greenhouse", "The greenhouse", 160, "rare plants growing all year"),
            ("aquarium", "The aquarium", 180, "the fish you caught at the pond"),
            ("pool", "The pool", 260, "laps after big days, floating on rest days"),
        };

        public static int Cost(Store s, string room) =>
            PlayTime.Rooms.FirstOrDefault(r => r.Id == room) is Room old ? old.Cost : Shop.Price(s, New.FirstOrDefault(n => n.Id == room).Price);

        public static string BuildWords(Store s, string room) => "build it in the burrow for " + Cost(s, room) + " points";

        /// <summary>Builds a room through Ledger.Spend; the first burrow's rooms still go in Data.Play.Rooms with their sound packs.</summary>
        public static bool Build(Store s, string room, out string said)
        {
            string name = PlaceWorld.NameOf(PlaceIds.Room(room));
            if (PlaceWorld.HasRoom(s, room)) { said = name + " is already built."; return false; }
            int cost = Cost(s, room);
            if (!Ledger.Spend(s, cost, "building " + name, Shop.Id)) { said = Shop.Short(s, name, cost); return false; }
            var old = PlayTime.Rooms.FirstOrDefault(r => r.Id == room);
            if (old != null)
            {
                s.Data.Play.Rooms.Add(room);
                said = "Built " + old.Name + ". " + old.Feel;
                if (old.Pack != null && !s.Data.Play.Packs.Contains(old.Pack)) { s.Data.Play.Packs.Add(old.Pack); said += " It came with a new sound pack: " + old.Pack + ". Pick it in Options, Sounds."; }
                s.Save();
            }
            else
            {
                var b = Shop.Bag(s);
                b.Rooms.Add(room);
                // Things she bought for this room while it was not built yet move in from storage.
                foreach (var i in b.Items.Where(i => i.Room == Shop.Storage && Shop.Find(i.ItemId)?.Room == room)) i.Room = room;
                s.World.Set(Shop.Id, b);
                s.World.Save();
                said = "Built " + name + ". " + PlaceWorld.FeelOf(PlaceIds.Room(room));
            }
            said += " " + PlayTime.SpendLine(s.Data) + ".";
            return true;
        }

        // ---- The burrow ----

        public static List<PlaceThing> Burrow(Store s)
        {
            var now = DateTime.Now;
            var things = new List<PlaceThing> { new(PlayTime.SpendLine(s.Data), "Spending never lowers your level", o => PlaceUi.Say(o, PlayTime.SpendLine(s.Data) + ".")) };
            foreach (var room in PlaceIds.Rooms.Where(r => PlaceWorld.HasRoom(s, r)))
            {
                var id = PlaceIds.Room(room);
                var def = PlaceWorld.Def(id)!;
                string extra = Shop.ThemeOf(s, room) is ShopTheme th ? ", " + th.Name.ToLowerInvariant() + " theme" : "";
                things.Add(new PlaceThing(Shop.CurrentTier(s, room)?.Name ?? def.Name, def.Short + extra, o => PlaceWorld.Open(o, id)));
            }
            things.Add(new PlaceThing("Build a room", "rooms you can build and what they cost", o =>
            {
                var left = PlaceIds.Rooms.Where(r => !PlaceWorld.HasRoom(s, r)).OrderBy(r => Cost(s, r)).ToList();
                if (left.Count == 0) { PlaceUi.Say(o, "Every room is built. The burrow is complete."); return; }
                var lines = left.Select(r => PlaceWorld.NameOf(PlaceIds.Room(r)) + ", " + Cost(s, r) + " points. " + PlaceWorld.Def(PlaceIds.Room(r))!.Short).ToList();
                if (PlaceUi.Pick(o, "Build a room", PlayTime.SpendLine(s.Data) + ". Enter builds", lines) is not int i) return;
                if (Build(s, left[i], out var said)) { Sounds.Play(Sounds.Event.SurpriseReward); PlaceWorld.Open(o, PlaceIds.Room(left[i])); }
                PlaceUi.Say(o, said);
            }));
            things.Add(new PlaceThing("The market", "things to buy for your rooms", o => ShopWindow.Market(o, Engines.Window!)));
            things.Add(new PlaceThing("Your wish list", Shop.Wishes(s).Count == 0 ? "nothing on it yet" : PlaceUi.N(Shop.Wishes(s).Count, "wish", "wishes"), o => ShopWindow.Wishes(o, Engines.Window!)));
            things.Add(new PlaceThing("Your things in storage", PlaceUi.N(Shop.InRoom(s, Shop.Storage).Count, "thing"), o => ShopWindow.Things(o, s, Shop.Storage)));
            things.Add(new PlaceThing("The weather window", "the real weather outside, and the moon at night", o => Window(o, s)));
            if (Night(s, now)) things.Add(new PlaceThing("Night in the burrow", RealWorld.MoonPhase(now) + " tonight", o => { PlaceUi.Play("Soft piano, sigh"); PlaceUi.Say(o, PlaceUi.Line(s, "place.night.say") + " Tonight there is " + RealWorld.MoonPhase(now) + "."); }));
            if (PlaceDefs.Stormy)
                things.Add(new PlaceThing("Storm shelter", "the neighbors are in from the storm", o =>
                {
                    int done = s.Data.Log.Count(e => e.Kind == "Done" && e.When.Date == now.Date);
                    PlaceUi.Say(o, PlaceUi.Line(s, "place.storm.say") + " " + (done == 0 ? "The cocoa is warming up." : "Your " + PlaceUi.N(done, "task") + " today kept the cocoa " + (done >= 4 ? "piping hot." : "nice and warm.")));
                }));
            if (RestDay(s, now)) things.Add(new PlaceThing("The pillow fort", "built for a rest day", o => { PlaceUi.Play("Bunny, two hops"); PlaceUi.Say(o, PlaceUi.Line(s, "place.pillowfort.say")); }));
            things.Add(Puzzle(s));
            var passages = Passages(s);
            if (passages.Count > 0) things.Add(new PlaceThing("Secret passages", PlaceUi.N(passages.Count, "passage") + " found", o =>
            {
                var lines = passages.Select(p => p.Get("name") + ": from " + PlaceWorld.NameOf(PlaceIds.Room(p.Get("from"))).Replace("The ", "the ") + " to " + PlaceWorld.NameOf(PlaceIds.Room(p.Get("to"))).Replace("The ", "the ") + ". " + PlaceWorld.First(p.Get("touch"))).ToList();
                if (PlaceUi.Pick(o, "Secret passages", "Enter takes the passage", lines) is int i)
                {
                    PlaceUi.Play(passages[i].Get("plays", "Bunny, hop"));
                    PlaceUi.Say(o, PlaceUi.Feel(passages[i]));
                    PlaceWorld.Open(o, PlaceIds.Room(passages[i].Get("to")));
                }
            }));
            things.Add(new PlaceThing("The parking lot", s.Data.Parking.Count + (s.Data.Parking.Count == 1 ? " thought parked" : " thoughts parked") + ". Enter goes in", o => WorldWindow.GoTo(o, "parking")));
            return things;
        }

        public static bool Night(Store s, DateTime now)
        {
            var cfg = Engines.Settings;
            if (cfg != null && (cfg.WeatherLat != 0 || cfg.WeatherLon != 0) && RealWorld.Sunset(now.Date, cfg.WeatherLat, cfg.WeatherLon) is DateTime set) return now >= set || now.Hour < 5;
            return now.Hour >= 19 || now.Hour < 5;
        }

        public static bool RestDay(Store s, DateTime now) =>
            s.Data.Days.TryGetValue(now.ToString("yyyy-MM-dd"), out var i) && i.Mode is DayMode.RoughDay or DayMode.Vacation;

        private static async void Window(Window o, Store s)
        {
            PlaceUi.Say(o, "Looking out the window.");
            string w = Engines.Settings is Settings cfg ? await RealWorld.Weather(cfg) : RealWorld.Mystery;
            var now = DateTime.Now;
            string night = Night(s, now) ? " It is dark out, with " + RealWorld.MoonPhase(now) + "." : "";
            string season = " It is " + RealWorld.Season(now).ToLowerInvariant() + " in the village.";
            if (o.IsVisible) PlaceUi.Say(o, w + night + season);
        }

        /// <summary>Secret passages found over time: one every 15 finished tasks, between rooms she has built.</summary>
        public static List<Dictionary<string, string>> Passages(Store s)
        {
            int found = PlaceStats.Of(s).Tasks / 15;
            return PlaceUi.Items("place.passages").Take(found).Where(p => PlaceWorld.HasRoom(s, p.Get("from")) && PlaceWorld.HasRoom(s, p.Get("to"))).ToList();
        }

        /// <summary>The puzzle table (13.3): a piece for each finished task, 50 pieces a puzzle; finished ones go in the art room.</summary>
        public const int PuzzlePieces = 50;
        public static PlaceThing Puzzle(Store s)
        {
            var puzzles = PlaceUi.Items("place.puzzles");
            int tasks = PlaceStats.Of(s).Tasks, done = tasks / PuzzlePieces, pieces = tasks % PuzzlePieces;
            var now = puzzles.Count == 0 ? null : puzzles[done % puzzles.Count];
            return new PlaceThing("The puzzle table", (now?.Get("name") ?? "A jigsaw") + ", " + pieces + " of " + PuzzlePieces + " pieces", o =>
            {
                PlaceUi.Play("Coffee, clink");
                PlaceUi.Say(o, "The puzzle table: " + (now?.Get("name") ?? "a jigsaw") + ". " + pieces + " of " + PuzzlePieces + " pieces are in; each finished task adds one. " +
                    (pieces > PuzzlePieces / 2 ? "You can feel the picture coming together: " + PlaceWorld.First(now?.Get("touch") ?? "") : "The edges are done, and the middle is a happy jumble.") +
                    (done > 0 ? " " + PlaceUi.N(done, "finished puzzle") + " hang in the art room." : ""));
            });
        }

        public static List<string> FinishedPuzzles(Store s)
        {
            var puzzles = PlaceUi.Items("place.puzzles");
            int done = PlaceStats.Of(s).Tasks / PuzzlePieces;
            return Enumerable.Range(0, puzzles.Count == 0 ? 0 : done).Select(i => "Puzzle: " + puzzles[i % puzzles.Count].Get("name") + ". " + puzzles[i % puzzles.Count].Get("touch")).ToList();
        }

        // ---- Inside a room ----

        public static List<PlaceThing> RoomThings(Store s, PlaceDef d)
        {
            string room = d.RoomId!;
            var things = new List<PlaceThing>();
            var old = PlayTime.Rooms.FirstOrDefault(r => r.Id == room);
            var tier = Shop.CurrentTier(s, room);
            var theme = Shop.ThemeOf(s, room);
            string feel = (tier?.Feel ?? (old != null ? old.Feel : PlaceWorld.FeelOf(d.Id))) + (theme != null ? " The " + theme.Name.ToLowerInvariant() + " theme: " + theme.Feel : "");
            string plays = theme?.Plays is { Length: > 0 } tp ? tp : old?.Sound ?? PlaceWorld.PlaysOf(d.Id);
            things.Add(new PlaceThing("Feel and listen", PlaceWorld.First(feel), o => { PlaceUi.Play(plays); PlaceUi.Say(o, feel); }));
            things.AddRange(Special(s, room));
            var mine = Shop.InRoom(s, room);
            things.Add(new PlaceThing("Your things here", mine.Count == 0 ? "nothing yet" : string.Join(", ", mine.Take(4).Select(i => Shop.Find(i.ItemId)?.Name ?? i.ItemId)) + (mine.Count > 4 ? " and more" : ""), o => ShopWindow.Things(o, s, room)));
            things.Add(new PlaceThing("Shop for this room", Shop.Catalog.Count(i => i.Room == room && !i.Bonus) + " things made for it, and anything from the market", o => ShopWindow.Market(o, Engines.Window!, room)));
            var next = Shop.NextTier(s, room);
            if (next != null)
                things.Add(new PlaceThing("Upgrade to " + next.Name, Shop.Price(s, next.Price) + " points. " + PlaceWorld.First(next.Touch), o =>
                {
                    bool ok = Shop.Upgrade(s, room, out var said);
                    if (ok) PlaceUi.Play("Singing bells, sparkle");
                    PlaceUi.Say(o, said);
                }));
            things.Add(new PlaceThing("Theme", theme == null ? "none yet. Cabin, seaside, starry night and more" : theme.Name, o => ShopWindow.PickTheme(o, s, room)));
            return things;
        }

        /// <summary>What each room does beyond its things.</summary>
        private static IEnumerable<PlaceThing> Special(Store s, string room)
        {
            var now = DateTime.Now;
            switch (room)
            {
                case "treasure":
                    yield return new PlaceThing("Open the treasure chest", PlaceUi.N(Chest(s).Count, "treasure") + " inside", o => OpenChest(o, s));
                    break;
                case "art":
                    var art = Art.All(s);
                    yield return new PlaceThing("The pieces on display", PlaceUi.N(art.Count(a => a.Shown) + FinishedPuzzles(s).Count, "piece"), o =>
                        PlaceUi.Show(o, "The art room", art.Where(a => a.Shown).OrderByDescending(a => a.When).Select(a => a.Name + ", made " + a.When.ToString("MMMM d, yyyy") + ". " + a.Touch).Concat(FinishedPuzzles(s)).ToList()));
                    if (art.Count > 0) yield return new PlaceThing("Choose what is on display", "Enter puts a piece up or away", o =>
                    {
                        var lines = art.Select(a => a.Name + (a.Shown ? ", on display" : ", put away")).ToList();
                        if (PlaceUi.Pick(o, "On display", "Enter puts it up or away", lines) is not int i) return;
                        World.Change<List<ArtPiece>>(s, Art.Id, "changing what is on display", l => l[i].Shown = !l[i].Shown);
                        PlaceUi.Say(o, art[i].Name + (art[i].Shown ? " is put away safely." : " is on display."));
                    });
                    var photos = Photos(s);
                    yield return new PlaceThing("The photo booth album", PlaceUi.N(photos.Count, "snapshot") + " of your big days", o => PlaceUi.Show(o, "The photo booth album", photos));
                    break;
                case "trophy":
                    yield return new PlaceThing("The trophy shelves", PlaceUi.N(s.Data.Game.Badges.Count, "badge") + " and your records", o => PlaceUi.Show(o, "The trophy room", Trophies(s)));
                    break;
                case "aquarium":
                    var fish = s.World.Get<List<Fish>>(PlaceOutdoors.AquariumId);
                    yield return new PlaceThing("The tanks", fish.Count == 0 ? "waiting for fish from the pond" : PlaceUi.N(fish.Count, "fish", "fish"), o =>
                    {
                        PlaceUi.Play("Coffee, pour");
                        PlaceUi.Show(o, "The aquarium", fish.Select(f => f.Name + ", caught " + f.When.ToString("MMMM d") + ". " + f.Touch).ToList());
                    });
                    break;
                case "greenhouse":
                    var plants = PlaceUi.Items("place.rareplants");
                    int grown = Math.Min(plants.Count, 1 + PlaceStats.Of(s).Days / 2);
                    yield return new PlaceThing("The rare plants", PlaceUi.N(grown, "plant") + " growing, a new one every two working days", o =>
                        PlaceUi.Show(o, "The greenhouse", plants.Take(grown).Select(p => p.Get("name") + ". " + PlaceUi.Feel(p)).ToList()));
                    break;
                case "gameroom":
                    var best = Arcade.Best(s);
                    string fav = FavoriteGame(s);
                    yield return new PlaceThing("The arcade", (best.Count == 0 ? "no high scores yet" : PlaceUi.N(best.Count, "high score")) + (fav.Length > 0 ? ". Your favorite game: " + fav : ""), o =>
                    {
                        PlaceUi.Play("Space, two blips");
                        var lines = best.Select(b => b.Key + ": best score " + b.Value).ToList();
                        if (fav.Length > 0) lines.Insert(0, "The game you play most: " + fav + ".");
                        PlaceUi.Show(o, "The game room", lines);
                    });
                    break;
                case "kitchen":
                    yield return new PlaceThing("Today's snack", "Bunny cooked something for you", o => { PlaceUi.Play("Stove, sizzle"); PlaceUi.Say(o, Snack(s, now)); });
                    break;
                case "bedroom":
                    bool rest = RestDay(s, now);
                    yield return new PlaceThing("Bunny", rest ? "napping, it is a rest day" : "fluffing the pillows", o =>
                    {
                        PlaceUi.Play(rest ? "Soft piano, sigh" : "Bunny, hop");
                        PlaceUi.Say(o, rest ? "Bunny is curled up under the blankets for a rest day nap, breathing slow and soft. Zzz, very quietly." : "Bunny pats the pillows into a fluffy heap and bounces once on the bed, just to check it still bounces.");
                    });
                    var outfits = Shop.OwnedItems(s).Where(i => Shop.Find(i.ItemId)?.Aisle == "clothes and outfits").ToList();
                    if (outfits.Count > 0) yield return new PlaceThing("Bunny's wardrobe", PlaceUi.N(outfits.Count, "outfit"), o => PlaceUi.Show(o, "Bunny's wardrobe", outfits.Select(i => Shop.Find(i.ItemId)!).Select(i => i.Name + ". " + i.Feel).ToList()));
                    break;
                case "pool":
                    bool big = s.Data.Log.Where(e => e.When.Date == now.Date && Engine.KarmaKinds.Contains(e.Kind)).Sum(e => e.Points) >= Engine.DailyGoalFor(s, now);
                    yield return new PlaceThing(big ? "Bunny swims laps" : "Swim", big ? "a big day deserves laps" : "a splash in the pool", o => { PlaceUi.Play("Coffee, pour"); PlaceEvents.RaiseDid(PlaceIds.Room("pool"), "swim"); PlaceUi.Say(o, PlaceUi.Line(s, "place.pool.laps")); });
                    yield return new PlaceThing("Float", "lie back on the water", o => { PlaceUi.Play("Soft piano, sigh"); PlaceUi.Say(o, PlaceUi.Line(s, "place.pool.float")); });
                    if (RealWorld.Season(now) == "Summer") yield return new PlaceThing("Pool party", "the neighbors are here", o => { PlaceUi.Play("Pizzicato strings, bounce"); PlaceUi.Say(o, PlaceUi.Line(s, "place.pool.party")); });
                    break;
                case "music":
                    yield return new PlaceThing("Play an instrument", PlaceUi.N(PlaceRide.OwnedInstruments(s).Count, "instrument") + ", number keys play notes", o => PlaceRide.Instruments(o, s));
                    break;
                case "observatory":
                    var stars = Art.All(s).Where(a => a.Kind == "star").ToList();
                    yield return new PlaceThing("Your named stars", stars.Count == 0 ? "name one at the stargazing hill" : PlaceUi.N(stars.Count, "star"), o => PlaceUi.Show(o, "Your named stars", stars.Select(a => a.Name + ", named " + a.When.ToString("MMMM d, yyyy") + ".").ToList()));
                    break;
                case "gym":
                    foreach (var t in PlaceRide.Gym(s)) yield return t;
                    break;
                case "pottery":
                    foreach (var t in PlaceRide.Pottery(s)) yield return t;
                    break;
            }
        }

        // ---- The treasure chest ----

        /// <summary>Every treasure, newest first: finds kept in the world, and mystery boxes she cleared.</summary>
        public static List<string> Chest(Store s)
        {
            var finds = Treasure.All(s).Select(f => (f.When, f.Name + ", found " + f.When.ToString("MMMM d") + " at " + f.Where + ". " + f.Touch));
            var boxes = s.Data.Log.Where(e => e.Text.StartsWith(PlayTime.MysteryLead, StringComparison.Ordinal))
                .Select(e => (e.When, "A mystery box prize, opened " + e.When.ToString("MMMM d") + ". The ribbon is still curled and the lid clicks shut."));
            return finds.Concat(boxes).OrderByDescending(x => x.When).Select(x => x.Item2).ToList();
        }

        public static void OpenChest(Window owner, Store s)
        {
            var all = Chest(s);
            PlaceUi.Play("Stove, click");
            var lines = new List<string> { PlaceUi.N(all.Count, "treasure") + " in the chest, newest first." };
            lines.AddRange(all);
            PlaceUi.Show(owner, "The treasure chest", lines);
        }

        // ---- Trophies, photos, snacks, games ----

        public static List<string> Trophies(Store s)
        {
            var g = s.Data.Game;
            var lines = new List<string>
            {
                "Records: best day " + g.BestDay + " points, best week " + g.BestWeek + " points, longest streak " + PlaceUi.N(g.LongestStreak, "day") + ", " + PlaceUi.N(g.WeekGoalsMet, "weekly goal") + " met.",
            };
            foreach (var (id, when) in g.Badges.OrderByDescending(b => b.Value))
                lines.Add((Badges.All.FirstOrDefault(b => b.Id == id)?.Name ?? id) + ", earned " + when.ToString("MMMM d, yyyy") + ".");
            return lines;
        }

        /// <summary>The photo booth (13.3): a snapshot in words of every day she met her daily goal, newest first.</summary>
        public static List<string> Photos(Store s)
        {
            var lines = Content.Lines("place.photo.snap");
            var days = s.Data.Game.GoalDays.Select(d => DateTime.TryParse(d, out var x) ? x : DateTime.MinValue).Where(d => d > DateTime.MinValue).OrderByDescending(d => d).ToList();
            return days.Select(d =>
            {
                int count = s.Data.Log.Count(e => e.Kind == "Done" && e.When.Date == d.Date);
                string line = lines.Count == 0 ? "A snapshot of {day}." : lines[d.DayOfYear % lines.Count];
                return line.Replace("{day}", d.ToString("MMMM d")).Replace("{count}", count.ToString());
            }).ToList();
        }

        private const string SnackKey = "place.kitchen";
        public sealed class SnackDay { public string Day { get; set; } = ""; public string Line { get; set; } = ""; }

        /// <summary>One snack line a day, the same all day.</summary>
        public static string Snack(Store s, DateTime now)
        {
            var k = s.World.GetKept<SnackDay>(SnackKey);
            string day = now.ToString("yyyy-MM-dd");
            if (k.Day != day)
            {
                k = new SnackDay { Day = day, Line = PlaceUi.Line(s, "place.kitchen.snack") };
                s.World.SetKept(SnackKey, k);
                s.World.Save();
            }
            int carrots = PlaceGarden.Bag(s).Carrots;
            return k.Line + (carrots > 0 ? " Bunny used a carrot from your basket, and there are plenty left." : "");
        }

        private const string GamesKey = "place.games";
        public static void CountGame(Store s, string toolId)
        {
            var tool = Tools.Find(toolId);
            if (tool == null || tool.Drawer != Drawers.Games) return;
            var counts = s.World.GetKept<Dictionary<string, int>>(GamesKey);
            counts[tool.Name] = (counts.TryGetValue(tool.Name, out int n) ? n : 0) + 1;
            s.World.SetKept(GamesKey, counts);
            s.World.Save();
        }

        public static string FavoriteGame(Store s) =>
            s.World.GetKept<Dictionary<string, int>>(GamesKey).OrderByDescending(c => c.Value).Select(c => c.Key).FirstOrDefault() ?? "";

        public static void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("rooms: " + what); }
            // The first burrow's rooms stay built and owned; building a room spends through the ledger and undo takes it back.
            Need(PlaceWorld.HasRoom(s, "tunnel"), "the front tunnel is not built");
            s.Data.Play.Rooms.Add("nook");
            Need(PlaceWorld.HasRoom(s, "nook") && PlaceWorld.IsOpen(s, PlaceIds.Room("nook")), "a room built in the first burrow is not owned");
            Ledger.Pay(s, 3000, "rooms test", "test", remember: true);
            int wallet = Ledger.Wallet(s);
            Need(Build(s, "kitchen", out var said) && PlaceWorld.HasRoom(s, "kitchen") && Ledger.Wallet(s) == wallet - Cost(s, "kitchen"), "building the kitchen: " + said);
            Need(s.Data.Log.Any(e => e.Kind == Ledger.SpendKind && e.Text == "building The kitchen"), "the room is not in the ledger");
            s.Undo();
            Need(!PlaceWorld.HasRoom(s, "kitchen") && Ledger.Wallet(s) == wallet, "undo did not take the room back");
            Need(Build(s, "fireplace", out _) && s.Data.Play.Rooms.Contains("fireplace") && s.Data.Play.Packs.Contains("Fireside"), "an old room through the new burrow");
            foreach (var (id, _, _, _) in New) Build(s, id, out _);
            foreach (var r in PlaceIds.Rooms.Where(r => PlaceWorld.HasRoom(s, r)))
            {
                var def = PlaceWorld.Def(PlaceIds.Room(r))!;
                var things = RoomThings(s, def);
                Need(things.Count >= 4 && things[0].Name == "Feel and listen" && things[0].Description.Length > 0, r + " has no feel line");
            }
            // The chest lists finds newest first; a find tied to a task goes when the task is un-finished.
            var t = new TaskItem { Name = "chest test", List = s.Data.Lists[0].Name };
            Treasure.Add(s, "Old find", "Rough.", "the tunnels");
            Treasure.Add(s, "New find", "Smooth.", "the cove", t.Id);
            Need(Chest(s)[0].StartsWith("New find"), "the chest is not newest first");
            Treasure.TakeBack(s, t.Id);
            Need(!Chest(s).Any(c => c.StartsWith("New find")), "an un-finished task's find stayed");
            Need(Snack(s, DateTime.Now) == Snack(s, DateTime.Now), "the snack changed within a day");
            Need(Trophies(s).Count >= 1 && Burrow(s).Count >= 6, "the trophy room or the burrow");
        }
    }
}
