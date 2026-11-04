using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    /// <summary>The outdoor places' bag: what she has used up or set, so Control Z puts it back with the points.</summary>
    public sealed class OutdoorBag
    {
        public int Casts { get; set; }
        public string ShellDay { get; set; } = "";
        public string FairyDay { get; set; } = "";
        public string LemonadeDay { get; set; } = "";
        public string StoneDay { get; set; } = "";
        public int HoneyTraded { get; set; }
        public Dictionary<string, int> Tickets { get; set; } = new();
        public List<string> Inventions { get; set; } = new();
        public List<WellWish> Wishes { get; set; } = new();
        public List<string> Echoes { get; set; } = new();
        public List<string> TunnelFinds { get; set; } = new();
        public List<Stone> Stones { get; set; } = new();
        /// <summary>The night owl club's closing hour (0 to 23); 23 means 11 at night.</summary>
        public int NightOwlClose { get; set; } = 23;
    }

    public sealed class WellWish { public string Text { get; set; } = ""; public DateTime When { get; set; } public bool CameTrue { get; set; } }
    public sealed class Stone { public string Name { get; set; } = ""; public string Touch { get; set; } = ""; public DateTime Added { get; set; } }

    /// <summary>The village, the pond, the cove, the woods and the hills (sweep 8.6, 13.1, 13.3): each place's things.</summary>
    public static class PlaceOutdoors
    {
        public const string Id = "place.outdoors", AquariumId = "place.aquarium";
        private static readonly Random Rng = new();

        public static OutdoorBag Bag(Store s) => s.World.Get<OutdoorBag>(Id);
        private static void Change(Store s, string what, Action<OutdoorBag> change) => World.Change(s, Id, what, change);
        private static string Today => DateTime.Now.ToString("yyyy-MM-dd");
        private static PlaceThing Line(Store s, string name, string brief, string topic, string sound, string place, string activity = "visit", Func<string>? more = null) =>
            new(name, brief, o => { PlaceUi.Play(sound); PlaceEvents.RaiseDid(place, activity); PlaceUi.Say(o, PlaceUi.Line(s, topic) + (more?.Invoke() ?? "")); });

        /// <summary>
        /// Points she earned in a place she asked for: through the ledger as its own undo step, then the bag change. Returns
        /// what the payment brought (a level up, a badge) as " and the line", to say after the place's own line because she
        /// asked; empty when nothing. Every line is also kept in What happened today.
        /// </summary>
        private static string Pay(Store s, int points, string reason, string place, Action<OutdoorBag> change)
        {
            string first = "";
            if (Ledger.Pay(s, points, reason, place, remember: true) is Outcome o)
            {
                foreach (var said in o.Said) Game.Note(s, said);
                first = Said(o);
            }
            var b = Bag(s);
            change(b);
            s.World.Set(Id, b);
            s.World.Save();
            s.Save();
            return first;
        }

        /// <summary>The first thing a payment brought (a level up, a badge), with a space before it, or empty.</summary>
        internal static string Said(Outcome o) => o.Said.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) is string l ? " " + l : "";

        private static int DoneToday(Store s, DateTime now) => s.Data.Log.Count(e => e.Kind == "Done" && e.When.Date == now.Date);
        private static bool BigDay(Store s, DateTime now) => Engine.PointsOn(s.Data, now) >= Engine.DailyGoalFor(s, now) || s.Data.Log.Any(e => e.Kind == "Boss" && e.When.Date == now.Date);

        // ---- The burrow's outdoor bits: the tunnels and the hot tub ----

        private static readonly (string Id, PlaceGoal Goal)[] TunnelGoals =
        {
            ("tunnel1", PlaceGoal.Level(3)), ("tunnel2", PlaceGoal.Level(6)), ("tunnel3", PlaceGoal.Level(10)), ("secret1", PlaceGoal.Streak(30)), ("secret2", PlaceGoal.Streak(60)),
        };

        public static List<PlaceThing> Tunnels(Store s)
        {
            var st = PlaceStats.Of(s);
            var items = PlaceUi.Items("place.tunnels");
            var things = new List<PlaceThing>();
            foreach (var (tid, goal) in TunnelGoals)
            {
                var t = items.FirstOrDefault(i => i.Get("id") == tid);
                if (t == null) continue;
                bool secret = tid.StartsWith("secret");
                if (!goal.Met(st)) { things.Add(new PlaceThing((secret ? "A secret room" : "A tunnel") + ", still closed", "opens with " + goal.Words(st), o => PlaceUi.Say(o, "It opens with " + goal.Words(st) + "."))); continue; }
                things.Add(new PlaceThing(t.Get("name"), PlaceWorld.First(t.Get("touch")), o =>
                {
                    PlaceUi.Play(t.Get("plays", secret ? "Singing bells, sparkle" : "Bunny, thump"));
                    PlaceEvents.RaiseDid(PlaceIds.Tunnels, "explore");
                    string said = PlaceUi.Feel(t);
                    if (!Bag(s).TunnelFinds.Contains(tid))
                    {
                        s.Remember("finding something in the tunnels");
                        var b = Bag(s); b.TunnelFinds.Add(tid); s.World.Set(Id, b);
                        Treasure.Add(s, t.Get("find"), "", t.Get("name").Replace("The ", "the "));
                        s.Save();
                        said += " You found " + t.Get("find") + ". It is in the treasure chest.";
                    }
                    PlaceUi.Say(o, said);
                }));
            }
            // The rock tumbler: a rough stone a day from the tunnels, polished after a week of work.
            var bag = Bag(s);
            int polished = bag.Stones.Count(x => Polished(s, x));
            things.Add(new PlaceThing("The rock tumbler", PlaceUi.N(bag.Stones.Count, "stone") + " tumbling" + (polished > 0 ? ", " + polished + " polished" : ""), o =>
            {
                var lines = new List<string>();
                var b = Bag(s);
                bool fresh = b.StoneDay != Today;
                var done = b.Stones.Where(x => Polished(s, x)).ToList();
                if (fresh || done.Count > 0)
                {
                    // One Control Z step for the whole visit: the new stone and the polished ones go back together.
                    s.Remember("using the rock tumbler");
                    b = Bag(s);
                    if (done.Count > 0)
                    {
                        foreach (var x in done) Treasure.Add(s, "Polished " + x.Name.ToLowerInvariant(), x.Touch, "the rock tumbler");
                        b.Stones.RemoveAll(x => Polished(s, x));
                    }
                    if (fresh)
                    {
                        var stones = PlaceUi.Items("place.stones");
                        var pick = stones[Rng.Next(stones.Count)];
                        b.StoneDay = Today;
                        b.Stones.Add(new Stone { Name = pick.Get("name"), Touch = PlaceUi.Feel(pick), Added = DateTime.Now });
                        lines.Add("You found a rough " + pick.Get("name").ToLowerInvariant() + " and dropped it in. The tumbler rumbles.");
                    }
                    s.World.Set(Id, b); s.World.Save(); s.Save();
                    if (done.Count > 0) lines.Add(PlaceUi.N(done.Count, "stone") + " came out shiny and smooth: " + string.Join(", ", done.Select(x => x.Name.ToLowerInvariant())) + ". They are in the treasure chest.");
                }
                lines.Add(b.Stones.Count == 0 ? "The tumbler is empty for now." : PlaceUi.N(b.Stones.Count, "stone") + " still tumbling; each polishes after five days you finish tasks.");
                PlaceUi.Play("Stove, simmer");
                PlaceUi.Say(o, string.Join(" ", lines));
            }));
            return things;
        }

        /// <summary>A stone is polished after five days with a finished task since it went in.</summary>
        public static bool Polished(Store s, Stone x) => PlaceStats.DoneTasks(s.Data).Where(e => e.When.Date > x.Added.Date).Select(e => e.When.Date).Distinct().Count() >= 5;

        public static List<PlaceThing> HotTub(Store s)
        {
            bool winter = RealWorld.Season(DateTime.Now) == "Winter";
            var mine = Shop.InRoom(s, "hottub");
            return new List<PlaceThing>
            {
                Line(s, "Soak", winter ? "snow on the deck, warm water" : "bubbling jets and warm water", winter ? "place.hottub.snow" : "place.hottub.say", "Coffee, percolate", PlaceIds.HotTub, "soak"),
                new("Your hot tub things", mine.Count == 0 ? "jets, lights, a cover and towels are at the market" : string.Join(", ", mine.Select(i => Shop.Find(i.ItemId)?.Name)), o => ShopWindow.Things(o, s, "hottub")),
                new("Fix up the hot tub", "things for the deck", o => ShopWindow.Market(o, Engines.Window!, "hottub")),
            };
        }

        // ---- Garden area ----

        public static List<PlaceThing> Herbs(Store s)
        {
            var herbs = PlaceUi.Items("place.herbs");
            int grown = Math.Min(herbs.Count, 1 + PlaceStats.Of(s).Days / 3);
            var things = new List<PlaceThing>();
            foreach (var h in herbs.Take(grown)) { var herb = h; things.Add(new PlaceThing(h.Get("name"), PlaceWorld.First(h.Get("touch")), o => { PlaceUi.Play("Flute, single"); PlaceUi.Say(o, PlaceUi.Feel(herb) + " The tea house brews it."); })); }
            if (grown < herbs.Count) things.Add(new PlaceThing("A bare patch", "a new herb comes up every three days you finish tasks", o => PlaceUi.Say(o, "A new herb comes up every three days you finish tasks.")));
            return things;
        }

        public static List<PlaceThing> Orchard(Store s)
        {
            var sems = s.Data.Log.Where(e => e.Kind == "Done").Select(e => School.SemesterKey(e.When)).Distinct().ToList();
            string now = School.SemesterKey(DateTime.Now);
            var things = new List<PlaceThing>();
            foreach (var sem in sems)
            {
                bool past = sem != now;
                things.Add(new PlaceThing("The " + sem + " tree", past ? "heavy with fruit" : "growing this semester", o =>
                {
                    PlaceUi.Play(past ? "Bunny, nibble" : "Flute, single");
                    PlaceUi.Say(o, past ? "The " + sem + " tree: rough bark, branches bending low with round, sun-warm fruit that smells like honey. Every task that semester is in there." : "The " + sem + " tree: a slim trunk, smooth bark and new leaves that rustle when you touch them. It fruits when the semester ends.");
                }));
            }
            if (things.Count == 0) things.Add(new PlaceThing("A sapling", "planted for your first semester here", o => PlaceUi.Say(o, "A sapling, a new tree for each semester you work here.")));
            return things;
        }

        public static int Honey(Store s) => Math.Max(0, PlaceStats.Of(s).GoalDays / 3 - Bag(s).HoneyTraded);

        public static List<PlaceThing> Hive(Store s)
        {
            int jars = Honey(s);
            return new List<PlaceThing>
            {
                Line(s, "Listen to the bees", PlaceUi.N(jars, "jar") + " of honey ready, from your goal days", "place.hive.say", "Flute, sparkle", PlaceIds.Hive, "garden",
                    () => " " + PlaceUi.N(Honey(s), "jar") + " of honey ready. Every three days you meet your goal makes a jar; trade them at the market."),
            };
        }

        // ---- The village ----

        public static List<PlaceThing> Market(Store s)
        {
            var things = new List<PlaceThing>
            {
                new("Walk the aisles", PlayTime.SpendLine(s.Data), o => ShopWindow.Market(o, Engines.Window!)),
            };
            if (Tools.IsOn(PlaceRooms.WishTool)) things.Add(new("Your wish list", Shop.Wishes(s).Count == 0 ? "nothing on it yet" : PlaceUi.N(Shop.Wishes(s).Count, "wish", "wishes"), o => ShopWindow.Wishes(o, Engines.Window!)));
            int jars = Honey(s);
            if (jars > 0)
                things.Add(new PlaceThing("Trade a jar of honey", "10 points a jar, " + PlaceUi.N(jars, "jar") + " to trade", o =>
                {
                    string more = Pay(s, 10, "trading a jar of honey at the market", PlaceIds.Hive, b => b.HoneyTraded++);
                    PlaceUi.Play("Coffee, clink");
                    PlaceUi.Say(o, "The stall keeper sniffs the jar and grins. 10 points. " + PlayTime.SpendLine(s.Data) + "." + more);
                }));
            return things;
        }

        public static List<PlaceThing> TeaHouse(Store s)
        {
            var things = new List<PlaceThing> { Line(s, "Have a cup of tea", "a neighbor pours and shares a bit of gossip", "place.tea.gossip", "Coffee, pour", PlaceIds.TeaHouse, "tea") };
            if (PlaceWorld.IsOpen(s, PlaceIds.Herbs))
            {
                var herbs = PlaceUi.Items("place.herbs").Take(Math.Min(PlaceUi.Items("place.herbs").Count, 1 + PlaceStats.Of(s).Days / 3)).ToList();
                things.Add(new PlaceThing("Herb tea from your garden", PlaceUi.N(herbs.Count, "herb") + " to choose from", o =>
                {
                    if (PlaceUi.Pick(o, "Herb tea", "Which herb", herbs.Select(h => h.Get("name")).ToList()) is not int i) return;
                    PlaceUi.Play("Coffee, steam");
                    PlaceUi.Say(o, herbs[i].Get("name") + " tea. " + herbs[i].Get("tea"));
                }));
            }
            return things;
        }

        public static List<PlaceThing> Lemonade(Store s)
        {
            bool summer = RealWorld.Season(DateTime.Now) == "Summer";
            if (!summer) return new List<PlaceThing> { new("The stand is folded up", "it opens in summer", o => PlaceUi.Say(o, "The lemonade stand is folded up under a tarp until summer. The pitcher is waiting.")) };
            if (Bag(s).LemonadeDay == Today) return new List<PlaceThing> { Line(s, "Sell lemonade", "sold out for today, back tomorrow", "place.lemonade.sale", "Coffee, pour", PlaceIds.Lemonade, "sell", () => " That was the last cup today; the stand opens again tomorrow.") };
            return new List<PlaceThing>
            {
                new("Sell lemonade", "5 bonus points, once a summer day", o =>
                {
                    string more = Pay(s, 5, "selling lemonade", PlaceIds.Lemonade, b => b.LemonadeDay = Today);
                    PlaceUi.Play("Coffee, pour");
                    PlaceEvents.RaiseDid(PlaceIds.Lemonade, "sell");
                    PlaceUi.Say(o, PlaceUi.Line(s, "place.lemonade.sale") + " 5 points. " + PlayTime.SpendLine(s.Data) + "." + more);
                }),
            };
        }

        public static List<PlaceThing> Workshop(Store s)
        {
            var all = PlaceUi.Items("place.inventions");
            var mine = Bag(s).Inventions;
            var things = new List<PlaceThing>();
            if (mine.Count > 0) things.Add(new PlaceThing("Your inventions", PlaceUi.N(mine.Count, "invention"), o =>
            {
                var have = all.Where(i => mine.Contains(i.Get("name"))).ToList();
                if (PlaceUi.Pick(o, "Your inventions", "Enter tries one", have.Select(i => i.Get("name")).ToList()) is int k) { PlaceUi.Play(have[k].Get("plays")); PlaceUi.Say(o, PlaceUi.Feel(have[k])); }
            }));
            foreach (var inv in all.Where(i => !mine.Contains(i.Get("name"))).Take(6))
            {
                var i = inv;
                int price = Shop.Price(s, int.TryParse(i.Get("price"), out int p) ? p : 30);
                things.Add(new PlaceThing("Fund " + i.Get("name").ToLowerInvariant(), price + " points. " + PlaceWorld.First(i.Get("touch")), o =>
                {
                    if (!Ledger.Spend(s, price, "funding " + i.Get("name"), PlaceIds.Workshop)) { PlaceUi.Say(o, Shop.Short(s, i.Get("name"), price)); return; }
                    var b = Bag(s); b.Inventions.Add(i.Get("name")); s.World.Set(Id, b); s.World.Save();
                    PlaceUi.Play(i.Get("plays"));
                    PlaceEvents.RaiseDid(PlaceIds.Workshop, "invent");
                    PlaceUi.Say(o, "The inventor whoops and gets to work. " + i.Get("name") + ": " + PlaceUi.Feel(i) + " " + PlayTime.SpendLine(s.Data) + ".");
                }));
            }
            return things;
        }

        public static List<PlaceThing> ClockTower(Store s)
        {
            var st = PlaceStats.Of(s);
            return new List<PlaceThing>
            {
                Line(s, "Ring the bell", "for your wins: level " + st.Level + " and " + PlaceUi.N(s.Data.Game.Badges.Count, "badge"), "place.bell.say", "Singing bells, chord high", PlaceIds.ClockTower),
                new("The clock", Tools.IsOn(WorldPlaces.ChimesTool) ? "chimes when a power hour starts" : "chimes are turned off", o => { PlaceUi.Play("Singing bells, two down"); PlaceUi.Say(o, "Smooth brass hands and a slow tick you can feel through the stones. It is " + DateTime.Now.ToString("h:mm tt") + ". " + (Tools.IsOn(WorldPlaces.ChimesTool) ? "It chimes when a power hour starts." : "Its chimes are off in Settings, Fun and tools.")); }),
            };
        }

        public static List<PlaceThing> Well(Store s)
        {
            var b = Bag(s);
            var things = new List<PlaceThing>();
            foreach (int amount in new[] { 5, 10, 20 })
            {
                int a = amount;
                things.Add(new PlaceThing("Toss in " + a + " points", "and make a wish", o =>
                {
                    if (Ledger.Wallet(s) < a) { PlaceUi.Say(o, "You have " + Ledger.Wallet(s) + " points to spend. Tasks bring more."); return; }
                    if (PlaceUi.Ask(o, "The wishing well", "Your wish, in a few words") is not string wish) return;
                    if (!Ledger.Spend(s, a, "a wish at the wishing well", PlaceIds.Well)) return;
                    var bag = Bag(s); bag.Wishes.Add(new WellWish { Text = wish, When = DateTime.Now }); s.World.Set(Id, bag); s.World.Save();
                    PlaceUi.Play("Singing bells, falling");
                    PlaceEvents.RaiseDid(PlaceIds.Well, "wish");
                    PlaceUi.Say(o, PlaceUi.Line(s, "place.well.say", ("wish", wish)) + " Bunny will ask how it is going next time you visit. " + PlayTime.SpendLine(s.Data) + ".");
                }));
            }
            var open = b.Wishes.Where(w => !w.CameTrue).ToList();
            if (b.Wishes.Count > 0)
                things.Add(new PlaceThing("Your wishes", open.Count > 0 ? "Bunny asks how " + open[^1].Text + " is going" : "every one came true", o =>
                {
                    var lines = b.Wishes.Select(w => w.Text + (w.CameTrue ? ", came true" : ", made " + w.When.ToString("MMMM d") + ". Enter if it came true")).ToList();
                    if (PlaceUi.Pick(o, "Your wishes", "Enter marks one as come true", lines) is not int i || b.Wishes[i].CameTrue) return;
                    Change(s, "a wish came true", x => x.Wishes[i].CameTrue = true);
                    PlaceUi.Play("Singing bells, sparkle");
                    PlaceUi.Say(o, "Bunny does a happy binky. " + b.Wishes[i].Text + " came true!");
                }));
            return things;
        }

        /// <summary>A class's town on the train line: the town at the class's place in her class list, so no two classes share one.</summary>
        public static string TownFor(Store s, string tag) => TownItem(s, tag)?.Get("name") ?? tag + " town";

        private static Dictionary<string, string>? TownItem(Store s, string tag)
        {
            var towns = PlaceUi.Items("place.towns");
            if (towns.Count == 0) return null;
            var tags = School.ClassTags(s.Data);
            int i = tags.FindIndex(x => x.Equals(tag, StringComparison.OrdinalIgnoreCase));
            return towns[(i >= 0 ? i : Math.Abs(tag.ToLowerInvariant().Sum(c => c))) % towns.Count];
        }

        public static List<PlaceThing> Station(Store s)
        {
            var tags = School.ClassTags(s.Data);
            var towns = PlaceUi.Items("place.towns");
            var things = new List<PlaceThing>();
            if (tags.Count == 0 || towns.Count == 0)
            {
                things.Add(new PlaceThing("The timetable", "each class gets its own town", o => PlaceUi.Say(o, "Each class gets a town on the line. Add your classes and their times in Settings, then finishing a class's tasks earns tickets.")));
                return things;
            }
            var b = Bag(s);
            foreach (var tag in tags)
            {
                string t = tag;
                var town = TownItem(s, t)!;
                int earned = PlaceStats.DoneTasks(s.Data).Count(e => e.Tag.Equals(t, StringComparison.OrdinalIgnoreCase)) / 5;
                int left = earned - (b.Tickets.TryGetValue(t, out int used) ? used : 0);
                things.Add(new PlaceThing(town.Get("name") + ", the " + t + " town", left > 0 ? PlaceUi.N(left, "ticket") + ". Enter rides" : "a ticket every 5 " + t + " tasks", o =>
                {
                    if (left <= 0) { PlaceUi.Say(o, town.Get("name") + ": " + PlaceUi.Feel(town) + " A ticket comes with every 5 finished " + t + " tasks."); return; }
                    s.Remember("riding the train");
                    var bag = Bag(s); bag.Tickets[t] = (bag.Tickets.TryGetValue(t, out int u) ? u : 0) + 1; s.World.Set(Id, bag);
                    Treasure.Add(s, town.Get("souvenir"), "", "the train to " + town.Get("name"));
                    s.Save();
                    PlaceUi.Play("Stove, whoosh");
                    PlaceEvents.RaiseDid(PlaceIds.Station, "travel");
                    PlaceUi.Say(o, PlaceUi.Line(s, "place.station.ride", ("town", town.Get("name"))) + " " + PlaceUi.Feel(town) + " Your souvenir: " + town.Get("souvenir") + ". It is in the treasure chest.");
                }));
            }
            return things;
        }

        public static List<PlaceThing> Museum(Store s)
        {
            var bosses = s.Data.Log.Where(e => e.Kind == "Boss").OrderByDescending(e => e.When).ToList();
            return new List<PlaceThing>
            {
                new("Walk the exhibits", PlaceUi.N(bosses.Count, "boss", "bosses") + " beaten", o =>
                {
                    PlaceUi.Play("Organ, chord low");
                    var lines = new List<string> { PlaceUi.Line(s, "place.museum.say") };
                    lines.AddRange(bosses.Select(b => (b.Text.Length > 0 ? b.Text : "A boss") + ", beaten " + b.When.ToString("MMMM d, yyyy") + "."));
                    PlaceUi.Show(o, "Bunny's museum", lines);
                }),
            };
        }

        public static List<PlaceThing> NightOwl(Store s)
        {
            var now = DateTime.Now;
            int close = Bag(s).NightOwlClose;
            bool open = ClubOpen(now, close);
            string closeWords = Hour(close);
            return new List<PlaceThing>
            {
                open ? Line(s, "Sit with the owl", "open until " + closeWords, "place.nightowl.say", "Soft piano, sigh", PlaceIds.NightOwl)
                     : now.Hour >= 20 || now.Hour < 6 ? Line(s, "The club is closed", "it closed at " + closeWords, "place.nightowl.closed", "Soft piano, falling", PlaceIds.NightOwl)
                     : new PlaceThing("The club opens at 8 in the evening", "and closes at " + closeWords, o => PlaceUi.Say(o, "The owl is asleep upstairs. The club opens at 8 in the evening and closes at " + closeWords + ".")),
                new("Set the closing hour", "now " + closeWords, o =>
                {
                    int[] hours = { 21, 22, 23, 0, 1, 2 };
                    if (PlaceUi.Pick(o, "Closing hour", "When the club closes", hours.Select(Hour).ToList(), Array.IndexOf(hours, close)) is int i)
                    {
                        Change(s, "setting the night owl club's hour", b => b.NightOwlClose = hours[i]);
                        PlaceUi.Say(o, "The night owl club closes at " + Hour(hours[i]) + " now.");
                    }
                }),
            };
        }

        public static string Hour(int h) => h == 0 ? "midnight" : h < 12 ? h + " in the morning" : (h - 12) + " at night";
        /// <summary>Open from 8 in the evening until the closing hour (which may be after midnight).</summary>
        public static bool ClubOpen(DateTime now, int close) => close >= 20 ? now.Hour >= 20 && now.Hour < close : now.Hour >= 20 || now.Hour < close;

        public static List<PlaceThing> Cocoa(Store s)
        {
            var now = DateTime.Now;
            if (RealWorld.Season(now) != "Winter") return new List<PlaceThing> { new("The stand is shuttered", "it opens on winter days", o => PlaceUi.Say(o, "The cocoa stand is shuttered until winter. You can still smell a little chocolate in the boards.")) };
            if (DoneToday(s, now) == 0) return new List<PlaceThing> { new("The stand opens after your first task today", "the milk is warming", o => PlaceUi.Say(o, "The milk is just starting to warm. Finish your first task today and the stand opens.")) };
            return new List<PlaceThing> { Line(s, "Get a hot cocoa", "open, after your first task", "place.cocoa.say", "Coffee, stir", PlaceIds.Cocoa) };
        }

        // ---- The pond and the cove ----

        public static int CastsLeft(Store s) { var st = PlaceStats.Of(s); return Math.Max(0, st.Sprints + st.GoalDays - Bag(s).Casts); }

        /// <summary>One cast: a fish for the aquarium or an odd find for the chest. One undo step. Returns what to say.</summary>
        public static string Cast(Store s, int seed)
        {
            var catches = PlaceUi.Items("place.catches");
            var c = catches[seed % catches.Count];
            s.Remember("fishing at the pond");
            var b = Bag(s); b.Casts++; s.World.Set(Id, b);
            bool fish = c.Get("kind") == "fish";
            if (fish)
            {
                var tank = s.World.Get<List<Fish>>(AquariumId);
                tank.Add(new Fish { Name = c.Get("name"), Touch = PlaceUi.Feel(c), When = DateTime.Now });
                s.World.Set(AquariumId, tank);
            }
            else Treasure.Add(s, c.Get("name"), PlaceUi.Feel(c), "the pond");
            s.World.Save();
            s.Save();
            string home = fish ? (PlaceWorld.HasRoom(s, "aquarium") ? " It swims off happily to your aquarium." : " It waits in a roomy bucket of pond water until you build the aquarium.") : " It goes in the treasure chest.";
            return "You caught " + c.Get("name").ToLowerInvariant() + ". " + PlaceUi.Feel(c) + home;
        }

        public static List<PlaceThing> Pond(Store s)
        {
            int casts = CastsLeft(s);
            var boat = Shop.InRoom(s, "boat");
            return new List<PlaceThing>
            {
                new("Cast a line", casts > 0 ? PlaceUi.N(casts, "cast") + " ready" : "a cast for each sprint and each goal day", o =>
                {
                    if (CastsLeft(s) == 0) { PlaceUi.Say(o, "The line is reeled in. Each finished sprint, and each day you meet your goal, gives you a cast."); return; }
                    PlaceUi.Play("Coffee, pour");
                    PlaceEvents.RaiseDid(PlaceIds.Pond, "fish");
                    PlaceUi.Say(o, Cast(s, Rng.Next(1000)) + " " + PlaceUi.N(CastsLeft(s), "cast") + " left.");
                }),
                Line(s, "Row the tiny boat", boat.Count == 0 ? "a little wooden boat at the dock" : "with " + string.Join(", ", boat.Select(i => Shop.Find(i.ItemId)?.Name.ToLowerInvariant())), "place.boat.row", "Coffee, stir", PlaceIds.Pond, "row",
                    () => boat.Count == 0 ? "" : " Your boat has " + string.Join(", ", boat.Select(i => Shop.Find(i.ItemId)?.Name.ToLowerInvariant())) + "."),
                new("Fix up the boat", "oars, a sail, a lantern and more", o => ShopWindow.Market(o, Engines.Window!, "boat")),
            };
        }

        public static bool SemesterBreak(DateTime d) => d.Month is 6 or 7 || d.Month == 5 && d.Day >= 10 || d.Month == 8 && d.Day <= 20 || d.Month == 12 && d.Day >= 15 || d.Month == 1 && d.Day <= 10;

        public static List<PlaceThing> Cove(Store s)
        {
            var st = PlaceStats.Of(s);
            int towers = 1 + st.Sprints / 2;
            var b = Bag(s);
            var things = new List<PlaceThing>
            {
                Line(s, "The sandcastle", PlaceUi.N(towers, "tower") + ", one more every two sprints", "place.sandcastle.say", "Bunny, two hops", PlaceIds.Cove, "beach", () => " It has " + PlaceUi.N(towers, "tower") + " now."),
                new("Walk the tideline", b.ShellDay == Today ? "you found today's shell" : "the tide brought a shell", o =>
                {
                    if (Bag(s).ShellDay == Today) { PlaceUi.Say(o, "Just smooth wet sand and little bubbles today. The tide brings a new shell tomorrow."); return; }
                    var shells = PlaceUi.Items("place.shells");
                    var sh = shells[Rng.Next(shells.Count)];
                    s.Remember("finding a shell");
                    var bag = Bag(s); bag.ShellDay = Today; s.World.Set(Id, bag);
                    Treasure.Add(s, sh.Get("name"), PlaceUi.Feel(sh), "the tideline at the cove");
                    s.Save();
                    PlaceUi.Play("Singing bells, bounce");
                    PlaceEvents.RaiseDid(PlaceIds.Cove, "beach");
                    PlaceUi.Say(o, "You found " + sh.Get("name").ToLowerInvariant() + ". " + PlaceUi.Feel(sh) + " It is in the treasure chest.");
                }),
                SemesterBreak(DateTime.Now)
                    ? Line(s, "Go sailing", "it is semester break", "place.sailing.say", "Flute, rising", PlaceIds.Cove, "sail")
                    : new PlaceThing("The sailboat", "sails at semester break", o => PlaceUi.Say(o, "The sailboat rocks at its mooring, ropes creaking. It sails at semester break: mid May to August, and mid December to early January.")),
            };
            if (Tools.IsOn(PlaceDefs.SonarTool))
                things.Insert(Math.Max(0, things.Count - 1), new PlaceThing("Sonar diving", PlaceDefs.SonarOpen(s) ? "arrows swim, the ping guides you to treasure" : "opens when you " + PlaceDefs.SonarWords(s), o =>
                {
                    if (!PlaceDefs.SonarOpen(s)) { PlaceUi.Say(o, "Sonar diving opens when you " + PlaceDefs.SonarWords(s) + "."); return; }
                    Sonar.Show(o, s);
                }));
            return things;
        }

        /// <summary>The lighthouse beam picks one task: overdue first, then the soonest due, then the top priority.</summary>
        public static TaskItem? Beam(Store s, DateTime now) => s.Data.Tasks
            .Where(t => t.Status is Status.Today or Status.ThisWeek or Status.NextWeek)
            .OrderBy(t => t.Due is DateTime d && d < now ? 0 : 1).ThenBy(t => t.Due ?? t.DoDate ?? DateTime.MaxValue).ThenBy(t => t.Priority == 0 ? 99 : t.Priority).FirstOrDefault();

        public static List<PlaceThing> Lighthouse(Store s) => new()
        {
            new("Turn on the beam", "it lands on one task, just one", o =>
            {
                var t = Beam(s, DateTime.Now);
                PlaceUi.Play("Space, beam");
                PlaceEvents.RaiseDid(PlaceIds.Lighthouse, "visit");
                PlaceUi.Say(o, t == null ? "The beam sweeps the water and finds nothing waiting. Calm seas." : PlaceUi.Line(s, "place.lighthouse.say", ("task", t.Name)));
            }),
        };

        // ---- The woods ----

        public static List<PlaceThing> Hammock(Store s) => new() { Line(s, "Lie in the hammock", "sway and listen to the leaves", "place.hammock.say", "Flute, sigh", PlaceIds.Hammock, "rest") };

        public static List<PlaceThing> Stretch(Store s) => new()
        {
            Line(s, "A gentle stretch", "one seated stretch, skip it any time", "place.stretch.do", "Soft piano, single", PlaceIds.Stretch, "stretch"),
            new("Skip stretching", "that is always fine", o => PlaceUi.Say(o, "That is fine. The mat will be here.")),
        };

        public static List<PlaceThing> Fireflies(Store s)
        {
            int n = PlaceStats.Of(s).Evening;
            return new List<PlaceThing>
            {
                new("Watch the fireflies", PlaceUi.N(n, "firefly", "fireflies") + ", one for each evening task", o => { PlaceUi.Play("Singing bells, sparkle"); PlaceUi.Say(o, PlaceUi.Line(s, "place.fireflies.say", ("count", n.ToString()))); }),
            };
        }

        public static List<PlaceThing> Creek(Store s)
        {
            int stones = PlaceStats.Of(s).Subtasks;
            bool big = BigDay(s, DateTime.Now);
            return new List<PlaceThing>
            {
                Line(s, "Cross on the stepping stones", PlaceUi.N(stones, "stone") + ", one for each subtask you finished", "place.creek.cross", "Bunny, hop", PlaceIds.Creek, "outdoors",
                    () => " " + PlaceUi.N(stones, "stepping stone") + " across so far" + (stones >= 8 ? ", enough to cross " + PlaceUi.N(stones / 8, "time") + " without a wet paw." : ".")),
                big ? Line(s, "The rope swing", "a big win today", "place.ropeswing.say", "Strings, rising", PlaceIds.Creek, "outdoors")
                    : new PlaceThing("The rope swing", "waits for a big win day", o => PlaceUi.Say(o, "The rope sways over the water, knotted and rough. It is for big win days: meet your daily goal or beat a boss, then swing.")),
            };
        }

        public static List<PlaceThing> HotSpring(Store s)
        {
            bool rest = PlaceRooms.RestDay(s, DateTime.Now);
            return new List<PlaceThing>
            {
                rest ? Line(s, "Soak in the hot spring", "open, it is a rest day", "place.hotspring.say", "Coffee, steam", PlaceIds.HotSpring, "soak")
                     : new PlaceThing("The hot spring", "opens on rest days", o => PlaceUi.Say(o, "Warm stones and drifting steam. The spring opens on rest days: a rough day or a vacation day, set from the Day menu whenever you need one.")),
            };
        }

        public static List<PlaceThing> Mushrooms(Store s)
        {
            bool used = Bag(s).FairyDay == Today;
            return new List<PlaceThing>
            {
                new("Step into the fairy ring", used ? "you had today's blessing" : "a small blessing, once a day", o =>
                {
                    if (Bag(s).FairyDay == Today) { PlaceUi.Say(o, "The mushrooms glow softly. They are resting until tomorrow."); return; }
                    int points = 3 + Rng.Next(4);
                    string more = Pay(s, points, "a blessing from the fairy ring", PlaceIds.Mushrooms, b => b.FairyDay = Today);
                    PlaceUi.Play("Singing bells, sparkle");
                    PlaceEvents.RaiseDid(PlaceIds.Mushrooms, "outdoors");
                    PlaceUi.Say(o, PlaceUi.Line(s, "place.fairyring.say") + " " + points + " bonus points. " + PlayTime.SpendLine(s.Data) + "." + more);
                }),
            };
        }

        public static List<PlaceThing> Cave(Store s)
        {
            var echoes = Bag(s).Echoes;
            var things = new List<PlaceThing>
            {
                new("Say a goal", "the cave echoes it back", o =>
                {
                    if (PlaceUi.Ask(o, "The cave of echoes", "Your goal, in your own words") is not string goal) return;
                    Change(s, "a goal for the cave", b => b.Echoes.Add(goal));
                    PlaceUi.Play("Organ, two down");
                    PlaceUi.Say(o, PlaceUi.Line(s, "place.echo.say", ("goal", goal)));
                }),
            };
            if (echoes.Count > 0)
            {
                things.Add(new PlaceThing("Listen to the echoes", PlaceRooms.RestDay(s, DateTime.Now) ? "on a hard day, the cave remembers your goals" : PlaceUi.N(echoes.Count, "goal") + " echoing", o =>
                {
                    PlaceUi.Play("Organ, two down");
                    PlaceUi.Say(o, PlaceUi.Line(s, "place.echo.say", ("goal", echoes[Rng.Next(echoes.Count)])));
                }));
                things.Add(new PlaceThing("All your echoes", PlaceUi.N(echoes.Count, "goal"), o => PlaceUi.Show(o, "Your echoes", echoes.AsEnumerable().Reverse().ToList())));
            }
            return things;
        }

        public static List<PlaceThing> Meadow(Store s)
        {
            int n = PlaceStats.Of(s).Kindness;
            var flowers = PlaceUi.Items("place.flowers");
            var things = new List<PlaceThing> { Line(s, "Walk through the flowers", PlaceUi.N(n, "flower") + ", one for each kind thing and each thing you did", "place.meadow.say", "Flute, sparkle", PlaceIds.Meadow, "outdoors") };
            foreach (var f in flowers.Take(Math.Min(n, flowers.Count))) { var fl = f; things.Add(new PlaceThing(f.Get("name"), PlaceWorld.First(f.Get("touch")), o => PlaceUi.Say(o, PlaceUi.Feel(fl)))); }
            if (n == 0) things.Add(new PlaceThing("Bare green grass", "the first flower is coming", o => PlaceUi.Say(o, "Soft grass for now. Each time you log something with I did a thing, or do something kind, a flower opens here.")));
            return things;
        }

        public static List<string> FinishedClasses(Store s) => School.ClassTags(s.Data)
            .Where(t => PlaceStats.DoneTasks(s.Data).Count(e => e.Tag.Equals(t, StringComparison.OrdinalIgnoreCase)) >= 10
                && !s.Data.Tasks.Any(x => x.Tag.Equals(t, StringComparison.OrdinalIgnoreCase) && x.Status is not (Status.Done or Status.DoneNotSubmitted))).ToList();

        public static List<PlaceThing> Bridges(Store s)
        {
            var bridges = PlaceUi.Items("place.bridges");
            var done = FinishedClasses(s);
            var things = new List<PlaceThing>();
            for (int i = 0; i < done.Count && bridges.Count > 0; i++)
            {
                var br = bridges[i % bridges.Count];
                string cls = done[i];
                things.Add(new PlaceThing(br.Get("name"), "built when you finished " + cls, o => { PlaceUi.Play("Bunny, thump"); PlaceEvents.RaiseDid(PlaceIds.Bridges, "outdoors"); PlaceUi.Say(o, PlaceUi.Feel(br) + " You built it by finishing every " + cls + " task."); }));
            }
            if (things.Count == 0) things.Add(new PlaceThing("The riverbank", "a bridge for every class you finish", o => PlaceUi.Say(o, "A bridge goes up here for each class you finish: at least 10 tasks done and none left open.")));
            return things;
        }

        public static List<PlaceThing> Waterfall(Store s) => new()
        {
            new("Behind the falls", PlaceWorld.IsOpen(s, PlaceIds.SecretGarden) ? "the secret garden" : "a door in the rock, opens at level 10", o =>
            {
                if (PlaceWorld.IsOpen(s, PlaceIds.SecretGarden)) PlaceWorld.Open(o, PlaceIds.SecretGarden);
                else PlaceUi.Say(o, "Cool spray on your face and a little wooden door in the rock behind the water. It opens when you " + PlaceWorld.UnlockWords(s, PlaceIds.SecretGarden) + ".");
            }),
        };

        public static List<PlaceThing> SecretGarden(Store s)
        {
            var plants = PlaceUi.Items("place.rareplants");
            return new List<PlaceThing> { new("Smell the rarest bloom", "something only grows here", o => { var p = plants[DateTime.Now.DayOfYear % plants.Count]; PlaceUi.Play("Flute, sparkle"); PlaceUi.Say(o, p.Get("name") + ". " + PlaceUi.Feel(p)); }) };
        }

        // ---- The hills ----

        /// <summary>Stars she has earned the right to name: a week with five days that met her goal.</summary>
        public static int StarsEarned(Store s) => s.Data.Game.GoalDays.Select(d => DateTime.TryParse(d, out var x) ? Engine.WeekStart(x) : DateTime.MinValue)
            .Where(d => d > DateTime.MinValue).GroupBy(d => d).Count(g => g.Count() >= 5);

        /// <summary>The constellations up in the evening sky this season (season: winter, spring, summer, fall or all year).</summary>
        public static List<Dictionary<string, string>> InSeason(DateTime now)
        {
            string season = RealWorld.Season(now).ToLowerInvariant();
            return PlaceUi.Items("place.constellations").Where(c => c.Get("season") is var w && (w == "all year" || w.Split(',').Select(x => x.Trim()).Contains(season))).ToList();
        }

        public static List<PlaceThing> Stars(Store s)
        {
            var now = DateTime.Now;
            var things = new List<PlaceThing>();
            var cons = InSeason(now);
            if (PlaceRooms.Night(s, now) && cons.Count > 0)
            {
                var c = cons[now.DayOfYear % cons.Count];
                things.Add(new PlaceThing("Tonight's constellation", c.Get("name"), o => { PlaceUi.Play("Space, orbit"); PlaceEvents.RaiseDid(PlaceIds.Stars, "stargaze"); PlaceUi.Say(o, c.Get("name") + ". " + PlaceUi.Feel(c) + " " + c.Get("myth")); }));
            }
            else things.Add(Line(s, "Look up", "the stars come out after dark", "place.stars.wait", "Space, orbit", PlaceIds.Stars));
            int named = Art.All(s).Count(a => a.Kind == "star"), earned = StarsEarned(s);
            things.Add(new PlaceThing("Name a star", earned > named ? "you have earned one" : "a week with five goal days earns one", o =>
            {
                if (StarsEarned(s) <= Art.All(s).Count(a => a.Kind == "star")) { PlaceUi.Say(o, "A week where you meet your goal on five days earns you a star to name."); return; }
                if (PlaceUi.Ask(o, "Name a star", "The star's name") is not string name) return;
                s.Remember("naming a star");
                Art.Add(s, name, "A tiny bright point you can find every clear night, right where you named it.", "star");
                s.Save();
                PlaceUi.Play("Singing bells, sparkle");
                PlaceUi.Say(o, "There it is: " + name + ", twinkling just for you. It is in the observatory and the art room.");
            }));
            return things;
        }

        public static List<PlaceThing> Sled(Store s)
        {
            var now = DateTime.Now;
            if (RealWorld.Season(now) != "Winter") return new List<PlaceThing> { new("The hill is green", "sledding opens in winter", o => PlaceUi.Say(o, "Soft grass and clover on the slope. The sleds wait in the shed until winter brings snow.")) };
            int n = DoneToday(s, now);
            string speed = n == 0 ? "a gentle glide" : n < 3 ? "a quick swoosh" : n < 6 ? "a speedy zoom" : "the fastest run of the day";
            return new List<PlaceThing> { Line(s, "Ride the sled", speed + ", faster with each task today", "place.sled.say", "Stove, whoosh", PlaceIds.Sled, "outdoors", () => " That was " + speed + ".") };
        }

        /// <summary>Next week as a landscape: each day light, medium or heavy by the open tasks planned or due.</summary>
        public static List<string> Landscape(Store s, DateTime now, Func<string, string>? pick = null)
        {
            var lines = new List<string>();
            for (int i = 1; i <= 7; i++)
            {
                var day = now.Date.AddDays(i);
                double load = s.Data.Tasks.Where(t => t.Status is not (Status.Done or Status.DoneNotSubmitted) && ((t.DoDate ?? t.Due)?.Date == day))
                    .Sum(t => t.Size switch { Size.Small => 0.5, Size.Medium => 1, Size.Large => 2, _ => 3 });
                string topic = load <= 1 ? "place.treehouse.light" : load <= 3 ? "place.treehouse.medium" : "place.treehouse.heavy";
                lines.Add((pick ?? (t => PlaceUi.Line(s, t)))(topic).Replace("{day}", day.DayOfWeek.ToString()));
            }
            return lines;
        }

        public static List<PlaceThing> Treehouse(Store s) => new()
        {
            new("Look out over next week", "big hills are heavy days", o => { PlaceUi.Play("Flute, rising"); PlaceUi.Show(o, "From the lookout", Landscape(s, DateTime.Now)); }),
        };

        public static bool YearEnd(DateTime d) => d.Month == 12 && d.Day >= 15 || d.Month == 1 && d.Day <= 7;

        public static List<PlaceThing> Balloon(Store s)
        {
            if (!YearEnd(DateTime.Now)) return new List<PlaceThing> { new("The balloon is folded", "it flies at the year's end", o => PlaceUi.Say(o, "The big basket smells of wicker and the balloon is folded beside it. It flies from mid December to early January.")) };
            return new List<PlaceThing>
            {
                new("Ride over the whole world", "everything you opened this year", o =>
                {
                    PlaceUi.Play("Stove, whoosh");
                    var open = PlaceWorld.Defs.Where(d => d.RoomId == null && d.Parent != null && PlaceWorld.IsOpen(s, d.Id)).Select(d => d.Name.Replace("The ", "the ")).ToList();
                    PlaceUi.Show(o, "The hot air balloon", new List<string> { PlaceUi.Line(s, "place.balloon.say"), "Below you: " + string.Join(", ", open) + "." });
                }),
            };
        }

        public static void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("outdoors: " + what); }
            // Casts come from sprints and goal days; a cast is one undo step.
            s.Data.Log.Add(new LogEntry { Kind = "Sprint", When = DateTime.Now, Minutes = 25 });
            PlaceStats.Forget();
            int casts = CastsLeft(s);
            Need(casts >= 1, "a sprint gave no cast");
            int finds = Treasure.All(s).Count, fish = s.World.Get<List<Fish>>(AquariumId).Count;
            Need(Cast(s, 3).StartsWith("You caught") && CastsLeft(s) == casts - 1, "casting");
            Need(Treasure.All(s).Count + s.World.Get<List<Fish>>(AquariumId).Count == finds + fish + 1, "a catch went nowhere");
            s.Undo();
            Need(CastsLeft(s) == casts, "undo did not give the cast back");
            // The night owl club's hours, including a closing hour after midnight.
            Need(ClubOpen(new DateTime(2026, 11, 4, 21, 0, 0), 23) && !ClubOpen(new DateTime(2026, 11, 4, 23, 30, 0), 23) && ClubOpen(new DateTime(2026, 11, 4, 0, 30, 0), 1) && !ClubOpen(new DateTime(2026, 11, 4, 15, 0, 0), 1), "the club's hours");
            Need(SemesterBreak(new DateTime(2026, 7, 1)) && !SemesterBreak(new DateTime(2026, 10, 1)) && YearEnd(new DateTime(2026, 12, 31)), "seasons for sailing and the balloon");
            // The lighthouse picks an overdue task first.
            var late = new TaskItem { Name = "overdue one", List = s.Data.Lists[0].Name, Status = Status.Today, Due = DateTime.Now.AddDays(-2) };
            s.Data.Tasks.Add(late);
            Need(Beam(s, DateTime.Now)?.Id == late.Id, "the lighthouse did not pick the overdue task");
            s.Data.Tasks.Remove(late);
            Need(Landscape(s, DateTime.Now, t => t + " {day}").Count == 7 && Landscape(s, DateTime.Now, t => "{day}").All(l => !l.Contains('{')), "the lookout landscape");
            // A stone polishes after five working days.
            var stone = new Stone { Name = "test", Added = DateTime.Now.AddDays(-10) };
            Need(!Polished(s, new Stone { Added = DateTime.Now }), "a fresh stone was already polished");
            for (int i = 1; i <= 5; i++) s.Data.Log.Add(new LogEntry { Kind = "Done", When = DateTime.Now.AddDays(-i), TaskId = Guid.NewGuid() });
            Need(Polished(s, stone), "a stone did not polish after five working days");
            // Every place lists its things without failing.
            PlaceStats.Forget();
            foreach (var f in new Func<Store, List<PlaceThing>>[] { Tunnels, HotTub, Herbs, Orchard, Hive, Market, TeaHouse, Lemonade, Workshop, ClockTower, Well, Station, Museum, NightOwl, Cocoa, Pond, Cove, Lighthouse, Hammock, Stretch, Fireflies, Creek, HotSpring, Mushrooms, Cave, Meadow, Bridges, Waterfall, SecretGarden, Stars, Sled, Treehouse, Balloon })
                Need(f(s).Count > 0 && f(s).All(t => t.Name.Length > 0), f.Method.Name + " has nothing inside");
            // Paid places pay through the ledger once a day.
            int wallet = Ledger.Wallet(s);
            Need(Pay(s, 4, "test blessing", PlaceIds.Mushrooms, b => b.FairyDay = Today) is string, "a place payment's words");
            Need(Ledger.Wallet(s) == wallet + 4 && Bag(s).FairyDay == Today, "a place payment");
            s.Undo();
            Need(Ledger.Wallet(s) == wallet && Bag(s).FairyDay != Today, "undo of a place payment");
            // Two classes never share a train town (PSY and SPY used to).
            var classes0 = s.Data.Classes.ToList();
            try
            {
                s.Data.Classes.Clear();
                s.Data.Classes.Add(new ClassTime { Tag = "PSY" });
                s.Data.Classes.Add(new ClassTime { Tag = "SPY" });
                var tags = School.ClassTags(s.Data);
                Need(tags.Count < 2 || TownFor(s, tags[0]) != TownFor(s, tags[1]), "two classes share a train town");
            }
            finally { s.Data.Classes.Clear(); s.Data.Classes.AddRange(classes0); }
        }
    }
}
