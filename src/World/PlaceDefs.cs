using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using G = BusyBunny.PlaceGoal;
using P = BusyBunny.PlaceIds;

namespace BusyBunny
{
    /// <summary>
    /// The table of places. Goals are tuned for a usual student week (about 4 tasks a day, a sprint now and then, a weekly
    /// goal most weeks): the first days open the pond, the tea house and the woods, then something new every two or three
    /// days for the first months, with a few long-term surprises (secret rooms at 30 and 60 day streaks). Nothing is ever
    /// bought to progress: points only buy rooms and things.
    /// </summary>
    public static class PlaceDefs
    {
        private static void Add(string id, string name, string brief, string? parent, G? goal, Func<Store, List<PlaceThing>>? things = null, string activity = "visit", string? tool = null) =>
            PlaceWorld.Add(new PlaceDef { Id = id, Name = name, Short = brief, Parent = parent, Goal = goal, Things = things, Activity = activity, ToolId = tool });

        public static void RegisterAll()
        {
            // The burrow: the first burrow's place, now with every room, the deck, the tunnels and more.
            Add(P.Burrow, "The burrow", "your rooms, points to spend, and rooms to build", null, null, PlaceRooms.Burrow);
            foreach (var r in PlayTime.Rooms)
                PlaceWorld.Add(new PlaceDef { Id = P.Room(r.Id), Name = r.Name, Short = PlaceWorld.First(r.Feel), Parent = P.Burrow, RoomId = r.Id });
            foreach (var (id, name, _, brief) in PlaceRooms.New)
                PlaceWorld.Add(new PlaceDef { Id = P.Room(id), Name = name, Short = brief, Parent = P.Burrow, RoomId = id });
            Add(P.Tunnels, "The tunnels", "tunnels that open as you level up, and secret rooms", P.Burrow, G.Level(3), PlaceOutdoors.Tunnels, "explore");
            Add(P.HotTub, "The hot tub", "a bubbling tub on the deck outside", P.Burrow, G.Count(s => s.RestDays, 1, "have a rough day or a vacation day", "").Or(G.WeekGoals(1)), PlaceOutdoors.HotTub, "soak");

            Add(P.Garden, "The carrot garden", "plots your finished tasks water", null, null, PlaceGarden.Things, "garden", WorldPlaces.GardenTool);
            Add(P.Herbs, "The herb garden", "herbs for the tea house", P.Garden, G.Tasks(25), PlaceOutdoors.Herbs, "garden");
            Add(P.Orchard, "The orchard", "a tree for every semester", P.Garden, G.Tasks(150), PlaceOutdoors.Orchard, "garden");
            Add(P.Hive, "The bee hive", "honey from your streak days", P.Garden, G.Days(10), PlaceOutdoors.Hive, "garden");

            Add(P.Village, "The village", "the market, the bakery, the tea house and more", null, null, null);
            Add(P.Market, "The market", "aisles of things to buy, new stock every Monday", P.Village, null, PlaceOutdoors.Market, "shop", "shop.market");
            Add(P.TeaHouse, "The tea house", "a cup of tea and a bit of gossip", P.Village, G.Tasks(6), PlaceOutdoors.TeaHouse, "tea");
            Add(P.Bakery, "The bakery", "bake your carrots into treats", P.Village, G.Tasks(14), PlaceGarden.Bakery, "bake");
            Add(P.Farmers, "The farmers market", "sell your garden carrots for points", P.Village, G.Days(4), PlaceGarden.Farmers, "sell");
            Add(P.Stable, "The stable", "your own horse, grooming and trail rides", P.Village, G.WeekGoals(1).Or(G.Tasks(45)), PlaceRide.Stable, "ride");
            Add(P.Lemonade, "The lemonade stand", "lemonade for the neighbors in summer", P.Village, G.Tasks(40), PlaceOutdoors.Lemonade, "sell");
            Add(P.Workshop, "The inventor workshop", "fund silly inventions with points", P.Village, G.Tasks(45), PlaceOutdoors.Workshop, "invent");
            Add(P.ClockTower, "The clock tower", "the village bell and the clock that chimes for power hour", P.Village, G.Level(5), PlaceOutdoors.ClockTower);
            Add(P.Well, "The wishing well", "toss in points and make a wish", P.Village, G.Tasks(70), PlaceOutdoors.Well, "wish");
            Add(P.Station, "The train station", "a town for each class, reached with tickets", P.Village, G.Tasks(110), PlaceOutdoors.Station, "travel");
            Add(P.Museum, "Bunny's museum", "the bosses you have beaten, on display", P.Village, G.Bosses(1), PlaceOutdoors.Museum);
            Add(P.NightOwl, "The night owl club", "a quiet late spot that closes at the hour you set", P.Village, G.Tasks(120), PlaceOutdoors.NightOwl);
            Add(P.Cocoa, "The hot cocoa stand", "winter days, open after your first task", P.Village, G.Tasks(20), PlaceOutdoors.Cocoa);

            Add(P.Pond, "The pond", "cast a line after a sprint, row the tiny boat", null, G.Tasks(2), PlaceOutdoors.Pond, "fish");
            Add(P.Cove, "The beach cove", "the sandcastle, shells and sonar diving", null, G.Tasks(30), PlaceOutdoors.Cove, "beach");
            Add(P.Lighthouse, "The lighthouse", "its beam picks one task for you", P.Cove, G.Tasks(100), PlaceOutdoors.Lighthouse);

            Add(P.Woods, "The meadows and woods", "the hammock, the stretch nook, the creek and more", null, G.Tasks(10), null);
            Add(P.Hammock, "The hammock", "lie back on a break", P.Woods, null, PlaceOutdoors.Hammock, "rest");
            Add(P.Stretch, "The stretch nook", "gentle seated stretches, always skippable", P.Woods, null, PlaceOutdoors.Stretch, "stretch");
            Add(P.Fireflies, "The firefly meadow", "a firefly for each evening task", P.Woods, G.Tasks(35), PlaceOutdoors.Fireflies);
            Add(P.Creek, "The creek", "stepping stones for your subtasks, and a rope swing", P.Woods, G.Tasks(50), PlaceOutdoors.Creek, "outdoors");
            Add(P.HotSpring, "The hot spring", "a warm soak on rest days", P.Woods, G.Count(s => s.RestDays, 1, "have a rough day or a vacation day", "").Or(G.Tasks(55)), PlaceOutdoors.HotSpring, "soak");
            Add(P.Mushrooms, "The mushroom forest", "a fairy ring with a small blessing once a day", P.Woods, G.Tasks(60), PlaceOutdoors.Mushrooms, "outdoors");
            Add(P.Cave, "The cave of echoes", "say a goal and hear it echo back", P.Woods, G.Tasks(80), PlaceOutdoors.Cave);
            Add(P.Meadow, "The wildflower meadow", "a flower for each kind thing you do", P.Woods, G.Tasks(130), PlaceOutdoors.Meadow, "outdoors");
            Add(P.Bridges, "The bridges", "a bridge for every class you finish", P.Woods, G.Tasks(160), PlaceOutdoors.Bridges, "outdoors");
            Add(P.Waterfall, "The hidden waterfall", "found at level 8", P.Woods, G.Level(8), PlaceOutdoors.Waterfall, "outdoors");
            Add(P.SecretGarden, "The secret garden", "behind the waterfall", P.Woods, G.Level(10), PlaceOutdoors.SecretGarden, "outdoors");

            Add(P.Hills, "The hills", "the stargazing hill, the treehouse, the trails and more", null, G.Tasks(18), null);
            Add(P.Stars, "The stargazing hill", "a constellation each night, and a star to name", P.Hills, null, PlaceOutdoors.Stars, "stargaze");
            Add(P.Sled, "The sledding hill", "winter runs that get faster with each task", P.Hills, G.Tasks(40), PlaceOutdoors.Sled, "outdoors");
            Add(P.Atv, "The ATV trails", "weekend rides with Bunny, a new trail with each weekly goal", P.Hills, G.WeekGoals(2).Or(G.Tasks(90)), PlaceRide.Atv, "ride");
            Add(P.Treehouse, "The lookout treehouse", "next week, seen as a landscape", P.Hills, G.Tasks(90), PlaceOutdoors.Treehouse);
            Add(P.Balloon, "The hot air balloon", "a ride over everything at the year's end", P.Hills, G.Tasks(180), PlaceOutdoors.Balloon, "outdoors");

            Add(P.Map, "The map room", "every place, open or not, and what opens it", null, null, PlaceWorld.MapThings, tool: "place.map");
        }

        // ---- Sonar diving ----

        public const string SonarTool = "place.sonar";
        public static bool SonarOpen(Store s) => PlaceWorld.IsOpen(s, P.Cove) && SonarGoal.Met(PlaceStats.Of(s));
        private static readonly G SonarGoal = G.Sprints(1).Or(G.Tasks(60));
        public static string SonarWords(Store s) => PlaceWorld.IsOpen(s, P.Cove) ? SonarGoal.Words(PlaceStats.Of(s)) : PlaceWorld.UnlockWords(s, P.Cove) + ", then " + SonarGoal.Words(PlaceStats.Of(s));

        // ---- Real weather: Stage 8's sky (SeasonSky, checked every hour) is the only weather ----

        public static bool Foggy => SeasonSky.Sky == "foggy";
        public static bool Rainy => SeasonSky.Rainy(SeasonSky.Sky);

        // ---- The clock tower ----

        /// <summary>Power hour's own start sound (GetGoingTools registers it under this id).</summary>
        public const string PowerStartSound = "go.power.start", PowerUsual = "Singing bells, rising", ChimeSound = "Singing bells, chord high";

        /// <summary>Whether the clock tower chime is power hour's start sound now: the tower is open and the chimes are on.</summary>
        public static bool ChimeForPower(Store s) => Tools.IsOn(WorldPlaces.ChimesTool) && PlaceWorld.IsOpen(s, P.ClockTower);

        /// <summary>
        /// One sound when a power hour starts, never two: while the clock tower is open and its chimes are on, the chime is
        /// power hour's start sound (played by power hour itself, with its own calm checks, only when a power hour really
        /// starts). Otherwise power hour plays its usual sound. Run each minute and at start.
        /// </summary>
        public static void Chime(DateTime now)
        {
            if (Engines.Store is not Store s) return;
            SoundBank.Register(PowerStartSound, ChimeForPower(s) ? ChimeSound : PowerUsual);
        }

        // ---- Self-test ----

        public static void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("places: " + what); }
            var ids = PlaceWorld.Defs.Select(d => d.Id).ToList();
            Need(ids.Distinct().Count() == ids.Count, "two places share an id");
            // Every place has touch and sound first in its content, and real sounds.
            foreach (var d in PlaceWorld.Defs)
            {
                if (d.RoomId != null && PlayTime.Rooms.Any(r => r.Id == d.RoomId)) continue;
                var c = PlaceUi.Items("place.places").FirstOrDefault(x => x.Get("id") == d.Id);
                Need(c != null && c.Get("touch").Length > 0 && c.Get("sound").Length > 0, d.Id + " has no touch and sound description");
                Need(c!.Keys.ToList().IndexOf("touch") < c.Keys.ToList().IndexOf("looks"), d.Id + " describes looks before touch");
                Need(c.Get("plays").Length == 0 || Sounds.All.Contains(c.Get("plays")), d.Id + " plays a sound that does not exist");
            }
            // Repeating lines: at least 20 of each.
            foreach (var topic in new[]
            {
                "place.hammock.say", "place.hottub.say", "place.hottub.snow", "place.pool.laps", "place.pool.float", "place.pool.party", "place.tea.gossip",
                "place.lemonade.sale", "place.farmers.sale", "place.boat.row", "place.gym.lift", "place.gym.plate", "place.stretch.do", "place.echo.say",
                "place.sled.say", "place.cocoa.say", "place.nightowl.say", "place.nightowl.closed", "place.fireflies.say", "place.sandcastle.say",
                "place.ropeswing.say", "place.bell.say", "place.fairyring.say", "place.lighthouse.say", "place.hotspring.say", "place.pillowfort.say",
                "place.kitchen.snack", "place.sailing.say", "place.balloon.say",
                "place.well.say", "place.garden.water", "place.garden.droop", "place.garden.harvest", "place.bakery.bake", "place.treat.thanks",
                "place.hive.say", "place.stars.wait", "place.pottery.done", "place.shop.bought", "place.shop.set", "place.passage.found",
                "place.museum.say", "place.station.ride", "place.creek.cross", "place.meadow.say", "place.treehouse.light", "place.treehouse.medium",
                "place.treehouse.heavy", "place.photo.snap", "place.horse.ride", "place.atv.ride",
            })
            {
                var lines = Content.Lines(topic);
                Need(lines.Count >= 20, topic + " has " + lines.Count + " lines, wanted 20");
                foreach (var l in lines) Need(!l.Contains((char)35) && !l.Contains((char)42), topic + " has a pound sign or asterisk");
            }
            foreach (var (topic, least) in new[] { ("place.catches", 20), ("place.constellations", 20), ("place.inventions", 20), ("place.towns", 20), ("place.bridges", 8),
                ("place.tunnels", 5), ("place.stones", 20), ("place.flowers", 20), ("place.herbs", 10), ("place.rareplants", 12), ("place.treasures", 20), ("place.shells", 20),
                ("place.puzzles", 12), ("place.horse.groom", 6), ("place.horse.tack", 4), ("place.horse.trails", 8), ("place.atv.trails", 8), ("place.pottery.steps", 5),
                ("place.pottery.glazes", 8), ("place.bakery.recipes", 6), ("place.passages", 8) })
            {
                var items = PlaceUi.Items(topic);
                Need(items.Count >= least, topic + " has " + items.Count + " entries");
                foreach (var i in items)
                {
                    Need(i.Get("name").Length > 0 || i.Get("id").Length > 0, topic + " has an entry with no name");
                    Need(!i.ContainsKey("plays") || Sounds.All.Contains(i.Get("plays")), topic + " plays a sound that does not exist: " + i.Get("plays"));
                    Need(!i.ContainsKey("touch") || !i.ContainsKey("looks") || i.Keys.ToList().IndexOf("touch") < i.Keys.ToList().IndexOf("looks"), topic + " has looks before touch");
                }
            }
            Need(PlaceUi.Items("place.horse.trails").All(t => new[] { "grass", "dirt", "gravel", "sand", "creek", "bridge", "snow", "leaves" }.Contains(t.Get("ground"))), "a trail has unknown ground");
            Need(PlaceUi.Items("place.passages").All(p => PlaceIds.OldRooms.Contains(p.Get("from")) && PlaceIds.OldRooms.Contains(p.Get("to"))), "a passage joins rooms that do not exist");

            // Unlock rules: a fresh store has the burrow, garden, village, market and map open, the woods closed until tasks count.
            PlaceStats.Forget();
            var st = new PlaceStats { Tasks = 9 };
            Need(!PlaceWorld.Def(P.Woods)!.Goal!.Met(st) && PlaceWorld.Def(P.Woods)!.Goal!.Met(new PlaceStats { Tasks = 10 }), "the woods goal");
            Need(PlaceWorld.Def(P.HotTub)!.Goal!.Met(new PlaceStats { RestDays = 1 }) && PlaceWorld.Def(P.HotTub)!.Goal!.Met(new PlaceStats { WeekGoals = 1 }), "the hot tub opens after a hard day or a big week");
            Need(PlaceWorld.Def(P.Woods)!.Goal!.Words(st).Contains("9 so far"), "goal words show progress");
            Need(PlaceWorld.IsOpen(s, P.Burrow) && PlaceWorld.IsOpen(s, P.Market) && PlaceWorld.IsOpen(s, P.Map), "always-open places");
            Need(PlaceWorld.Def(P.Room("gym"))?.RoomId == "gym", "the gym is a room");
            // A normal student week (about 25 tasks, 3 sprints, 1 weekly goal) opens several places, and every week of the first six opens something.
            int lastOpen = 0;
            for (int week = 1; week <= 6; week++)
            {
                var wk = new PlaceStats { Tasks = 25 * week, Sprints = 3 * week, WeekGoals = week, Days = 5 * week, Level = Engine.LevelFor(250 * week), GoalDays = 4 * week };
                int open = PlaceWorld.Defs.Count(d => d.RoomId == null && (d.Goal == null || d.Goal.Met(wk)));
                Need(open > lastOpen, "week " + week + " of usual work opened nothing new");
                lastOpen = open;
            }

            // The map room lists every place, open or not, with what opens it; places inside areas are hidden from the top list.
            var map = PlaceWorld.MapThings(s);
            Need(map.Count == PlaceWorld.Defs.Count, "the map room does not list every place");
            Need(map.Any(m => m.Description.StartsWith("closed") && m.Description.Contains("Opens when you")), "a closed place does not say what opens it");
            Need(World.Places.Find(P.Bakery) is Place bakery && !bakery.Unlocked(s), "a place inside the village shows in the top list");
            Need(World.Places.Find(P.Burrow)?.Things(s).First().Name == PlayTime.SpendLine(s.Data), "the burrow does not start with points to spend");

            // One burrow: the full burrow is the place, with every room and room building; the old first burrow's tool opens it.
            Need(World.Places.Find(P.Burrow)!.Things(s).Any(x => x.Name == "Build a room"), "the burrow place is not the full burrow");
            Need(!World.Places.Find(P.Burrow)!.Things(s).Any(x => x.Name is "The weather window" or "Night in the burrow" or "Storm shelter"), "the burrow still has its own weather window, night or storm line");
            Need(Content.Lines("place.fog.say").Count == 0 && Content.Lines("place.night.say").Count == 0, "Stage 7's own fog or night lines came back");
            // Inside places never show in the top list, even while one is being opened (WorldWindow.Listing).
            Need(PlaceWorld.Inside(P.Bakery) && !PlaceWorld.Inside(P.Village), "which places are inside an area");
            // A place whose tool is off is closed, and says how to turn it on.
            var map0 = Tools.Find("place.map");
            if (map0 != null && Engines.Settings is Settings set0)
            {
                bool wasOn = Tools.IsOn("place.map");
                try
                {
                    Tools.SetOn(set0, map0, false);
                    Need(!PlaceWorld.IsOpen(s, P.Map) && PlaceWorld.UnlockWords(s, P.Map).Contains("Settings"), "the map room stayed open with its tool off");
                }
                finally { Tools.SetOn(set0, map0, wasOn); }
            }
            // The constellation follows the season: Orion is never up on a summer night, Scorpius never on a winter one.
            Need(PlaceOutdoors.InSeason(new DateTime(2027, 7, 10, 22, 0, 0)).All(c => !c.Get("name").StartsWith("Orion")) && PlaceOutdoors.InSeason(new DateTime(2027, 1, 10, 22, 0, 0)).Any(c => c.Get("name").StartsWith("Orion")), "Orion in summer");
            Need(PlaceOutdoors.InSeason(new DateTime(2027, 1, 10, 22, 0, 0)).All(c => c.Get("name") != "Scorpius") && PlaceOutdoors.InSeason(new DateTime(2027, 4, 10, 22, 0, 0)).Count > 0 && PlaceOutdoors.InSeason(new DateTime(2027, 10, 10, 22, 0, 0)).Count > 0, "constellations by season");
            Need(PlaceUi.Items("place.constellations").All(c => c.Get("season").Length > 0), "a constellation has no season");
            // Two Done rows for one task (Done not submitted, then Done) count as one finished task.
            PlaceStats.Forget();
            int tasks0 = PlaceStats.Of(s).Tasks;
            var twice = Guid.NewGuid();
            s.Data.Log.Add(new LogEntry { Kind = "Done", When = DateTime.Now, TaskId = twice });
            s.Data.Log.Add(new LogEntry { Kind = "Done", When = DateTime.Now, TaskId = twice });
            Need(PlaceStats.Of(s).Tasks == tasks0 + 1, "one task with two Done rows counted twice");
            s.Data.Log.RemoveAll(e => e.TaskId == twice);
            // A rough day counts at once, with no new log entry (the stats cache sees it).
            int rest0 = PlaceStats.Of(s).RestDays;
            string roughKey = new DateTime(2001, 1, 2).ToString("yyyy-MM-dd");
            bool hadDay = s.Data.Days.TryGetValue(roughKey, out var day0);
            s.Data.Days[roughKey] = new DayInfo { Mode = DayMode.RoughDay };
            Need(PlaceStats.Of(s).RestDays == rest0 + 1, "a rough day did not count until the next task");
            if (hadDay) s.Data.Days[roughKey] = day0!; else s.Data.Days.Remove(roughKey);
            PlaceStats.Forget();
            // New places are said once: the world window's memory (world.seen) learns what was said after a task.
            var known0 = s.World.GetKept<List<string>>("place.known");
            var seen0 = s.World.GetKept<List<string>>("world.seen");
            try
            {
                s.World.SetKept("place.known", new List<string> { P.Burrow });
                s.World.SetKept("world.seen", new List<string> { P.Burrow });
                var fresh = PlaceWorld.NewlyOpen(s);
                var seen1 = s.World.GetKept<List<string>>("world.seen");
                Need(fresh.Any(f => f.Id == P.Village) && seen1.Contains(P.Village) && !seen1.Contains(P.Market), "a new top place is not shared with the world window");
                s.World.SetKept("place.known", new List<string> { P.Burrow });
                Need(!PlaceWorld.NewlyOpen(s).Any(f => f.Id == P.Village), "a top place the world window already said was said again");
            }
            finally { s.World.SetKept("place.known", known0); s.World.SetKept("world.seen", seen0); }

            PlaceGarden.SelfTest(s);
            PlaceRooms.SelfTest(s);
            PlaceOutdoors.SelfTest(s);
            PlaceRide.SelfTest(s);
        }
    }
}
