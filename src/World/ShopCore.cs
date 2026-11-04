using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>One thing the market sells (content shop.items): touch and sound first, then looks.</summary>
    public sealed class ShopItem
    {
        public string Id = "", Name = "", Aisle = "", Room = "any", Set = "", Season = "", When = "", Instrument = "";
        public string Touch = "", Sound = "", Looks = "", Plays = "";
        public int BasePrice;
        public bool Rare, Bonus;
        public string Feel => string.Join(" ", new[] { Touch, Sound, Looks }.Where(x => x.Length > 0));
        public bool Seasonal => Season.Length > 0 || When.Length > 0;
        /// <summary>Always on the shelf: not seasonal, not rare, not a bonus.</summary>
        public bool Regular => !Seasonal && !Rare && !Bonus;
    }

    /// <summary>A room theme or a room upgrade tier (content shop.themes, shop.tiers).</summary>
    public sealed class ShopTheme { public string Id = "", Name = "", Touch = "", Sound = "", Looks = "", Plays = ""; public int Price; public string Feel => Touch + " " + Sound + " " + Looks; }
    public sealed class ShopTier { public string Room = "", Name = "", Touch = "", Sound = "", Looks = ""; public int Tier, Price; public string Feel => Touch + " " + Sound + " " + Looks; }

    /// <summary>A thing she owns and where it is. Nothing she buys is ever lost: it is in a room or in storage.</summary>
    public sealed class OwnedItem
    {
        public string ItemId { get; set; } = "";
        public string Room { get; set; } = "";
        public DateTime Bought { get; set; }
    }

    /// <summary>The shop's bag in world.json (undone with Control Z along with the points spent).</summary>
    public sealed class ShopBag
    {
        public List<OwnedItem> Items { get; set; } = new();
        /// <summary>New Stage 7 rooms built (the first burrow's rooms stay in Data.Play.Rooms).</summary>
        public List<string> Rooms { get; set; } = new();
        public List<string> ThemesOwned { get; set; } = new();
        public Dictionary<string, string> Theme { get; set; } = new();
        public Dictionary<string, int> Tier { get; set; } = new();
        public List<string> Wishes { get; set; } = new();
        public List<string> SetsDone { get; set; } = new();
    }

    /// <summary>
    /// The shop (sweep 11.2 and 12.5): a catalog for every room, room upgrades in tiers, room themes, weekly market stock with
    /// seasonal, holiday and rare items, sets with a bonus, moving things and storage, prices that grow slowly with her level,
    /// and a wish list. Every purchase goes through Ledger.Spend, so Control Z gives the points and the thing back together.
    /// </summary>
    public static class Shop
    {
        public const string Id = "shop", Storage = "storage", Outside = "outside";
        public static readonly string[] Aisles = { "furniture", "decor", "bedding", "instruments", "food and treats", "clothes and outfits", "garden and seeds", "pool things", "crafts", "books", "seasonal", "rare finds" };

        private static List<ShopItem>? _catalog;
        public static IReadOnlyList<ShopItem> Catalog => _catalog ??= PlaceUi.Items("shop.items").Select(d => new ShopItem
        {
            Id = d.Get("id"), Name = d.Get("name"), Aisle = d.Get("aisle"), Room = d.Get("room", "any"), Set = d.Get("set"), Season = d.Get("season"),
            When = d.Get("when"), Instrument = d.Get("instrument"), Touch = d.Get("touch"), Sound = d.Get("sound"), Looks = d.Get("looks"), Plays = d.Get("plays"),
            BasePrice = int.TryParse(d.Get("price"), out int p) ? p : 20, Rare = d.Get("rare") == "yes", Bonus = d.Get("bonus") == "yes" || d.Get("aisle") == "bonus",
        }).Where(i => i.Id.Length > 0 && i.Name.Length > 0).ToList();

        private static List<ShopTheme>? _themes;
        public static IReadOnlyList<ShopTheme> Themes => _themes ??= PlaceUi.Items("shop.themes").Select(d => new ShopTheme
        {
            Id = d.Get("id"), Name = d.Get("name"), Touch = d.Get("touch"), Sound = d.Get("sound"), Looks = d.Get("looks"), Plays = d.Get("plays"),
            Price = int.TryParse(d.Get("price"), out int p) ? p : 50,
        }).Where(t => t.Id.Length > 0).ToList();

        private static List<ShopTier>? _tiers;
        public static IReadOnlyList<ShopTier> Tiers => _tiers ??= PlaceUi.Items("shop.tiers").Select(d => new ShopTier
        {
            Room = d.Get("room"), Name = d.Get("name"), Touch = d.Get("touch"), Sound = d.Get("sound"), Looks = d.Get("looks"),
            Tier = int.TryParse(d.Get("tier"), out int t) ? t : 1, Price = int.TryParse(d.Get("price"), out int p) ? p : 80,
        }).Where(t => t.Room.Length > 0).ToList();

        private static List<(string Set, string Bonus, string Touch)>? _sets;
        public static IReadOnlyList<(string Set, string Bonus, string Touch)> Sets => _sets ??= PlaceUi.Items("shop.sets").Select(d => (d.Get("set"), d.Get("bonus"), d.Get("touch"))).Where(s => s.Item1.Length > 0).ToList();

        public static ShopItem? Find(string itemId) => Catalog.FirstOrDefault(i => i.Id == itemId);

        public static ShopBag Bag(Store s) => s.World.Get<ShopBag>(Id);
        private static void Put(Store s, ShopBag b) { s.World.Set(Id, b); s.World.Save(); }

        public static bool Owned(Store s, string itemId) => Bag(s).Items.Any(i => i.ItemId == itemId);
        public static int Count(Store s, string itemId) => Bag(s).Items.Count(i => i.ItemId == itemId);
        public static List<OwnedItem> OwnedItems(Store s) => Bag(s).Items;
        public static List<OwnedItem> InRoom(Store s, string room) => Bag(s).Items.Where(i => i.Room == room).ToList();

        // ---- Prices ----

        /// <summary>Base price grown slowly with her level: 4 percent a level, at most double, rounded to 5.</summary>
        public static int Price(Store s, int basePrice)
        {
            if (basePrice <= 0) return 0;
            int level = Engine.LevelFor(Engine.Total(s.Data));
            double grown = basePrice * Math.Min(2.0, 1 + 0.04 * (level - 1));
            return Math.Max(5, (int)Math.Round(grown / 5.0) * 5);
        }
        public static int Price(Store s, ShopItem i) => Price(s, i.BasePrice);

        // ---- What is on sale ----

        /// <summary>
        /// This week's stock: in each aisle up to 5 regular things chosen fresh every Monday (aisles with 5 or fewer show all),
        /// every instrument, the season's things, holiday things from three weeks before the day to three days after, now and
        /// then one rare find, and anything on her wish list, so a wish never disappears from the shelf.
        /// </summary>
        public static List<ShopItem> OnSale(Store s, DateTime now)
        {
            var monday = Engine.WeekStart(now);
            var rng = new Random(monday.Year * 400 + monday.DayOfYear);
            var wishes = Bag(s).Wishes;
            var sale = new List<ShopItem>();
            foreach (var aisle in Catalog.Where(i => i.Regular).GroupBy(i => i.Aisle))
            {
                var all = aisle.OrderBy(i => i.Id).ToList();
                sale.AddRange(aisle.Key == "instruments" || all.Count <= 5 ? all : all.OrderBy(_ => rng.Next()).Take(5));
            }
            string season = RealWorld.Season(monday);
            sale.AddRange(Catalog.Where(i => i.Season.Equals(season, StringComparison.OrdinalIgnoreCase)));
            sale.AddRange(Catalog.Where(i => i.When.Length > 0 && HolidayNear(i.When, monday)));
            var rare = Catalog.Where(i => i.Rare).OrderBy(i => i.Id).ToList();
            if (rare.Count > 0 && rng.NextDouble() < 0.4) sale.Add(rare[rng.Next(rare.Count)]);
            sale.AddRange(Catalog.Where(i => wishes.Contains(i.Id) && !i.Bonus));
            return sale.Distinct().ToList();
        }

        /// <summary>A holiday item's day (MM-DD) is within about three weeks after this Monday or the week before it, so the stock is the same all week.</summary>
        public static bool HolidayNear(string mmdd, DateTime now)
        {
            if (!DateTime.TryParseExact(now.Year + "-" + mmdd, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var day)) return false;
            foreach (var d in new[] { day.AddYears(-1), day, day.AddYears(1) })
            {
                double ahead = (d - now.Date).TotalDays;
                if (ahead <= 24 && ahead >= -6) return true;
            }
            return false;
        }

        /// <summary>Where a bought thing goes first: its own room when she has it, otherwise storage.</summary>
        public static string HomeFor(Store s, ShopItem i) =>
            i.Room is "any" or "" ? Storage : i.Room == Outside || i.Room == "hottub" || i.Room == "boat" ? i.Room : PlaceWorld.HasRoom(s, i.Room) ? i.Room : Storage;

        // ---- Buying ----

        /// <summary>Buys one: Ledger.Spend (one Control Z step), then it goes to its room or storage, completing a set gives the bonus.</summary>
        public static bool Buy(Store s, string itemId, out string said)
        {
            var item = Find(itemId);
            if (item == null || item.Bonus) { said = "That is not for sale."; return false; }
            int price = Price(s, item);
            if (!Ledger.Spend(s, price, "buying " + item.Name, Id)) { said = Short(s, item.Name, price); return false; }
            var b = Bag(s);
            string home = HomeFor(s, item);
            b.Items.Add(new OwnedItem { ItemId = item.Id, Room = home, Bought = DateTime.Now });
            b.Wishes.Remove(item.Id);
            said = PlaceUi.Line(s, "shop.bought", ("item", item.Name));
            if (said.Length == 0) said = "You bought " + item.Name + ".";
            said += " " + (home == Storage ? "It is in storage until you move it to a room." : "It is in " + RoomName(home) + ".");
            if (SetBonus(s, b, item) is string bonus) said += " " + bonus;
            Put(s, b);
            said += " " + PlayTime.SpendLine(s.Data) + ".";
            return true;
        }

        public static string Short(Store s, string name, int price)
        {
            int have = Ledger.Wallet(s);
            return name + " costs " + price + " points. You have " + have + ", so " + (price - have) + " more to go. It will wait for you.";
        }

        /// <summary>When the last piece of a set arrives, the set's bonus thing is added (once). Returns what to say.</summary>
        private static string? SetBonus(Store s, ShopBag b, ShopItem item)
        {
            if (item.Set.Length == 0 || b.SetsDone.Contains(item.Set)) return null;
            var pieces = Catalog.Where(i => i.Set == item.Set && !i.Bonus).ToList();
            if (pieces.Count == 0 || !pieces.All(p => b.Items.Any(o => o.ItemId == p.Id))) return null;
            b.SetsDone.Add(item.Set);
            var set = Sets.FirstOrDefault(x => x.Set == item.Set);
            var bonus = Find(set.Bonus ?? "") ?? Catalog.FirstOrDefault(i => i.Bonus && i.Set == item.Set);
            if (bonus == null) return "That completes the " + item.Set + ".";
            b.Items.Add(new OwnedItem { ItemId = bonus.Id, Room = HomeFor(s, item), Bought = DateTime.Now });
            string line = PlaceUi.Line(s, "shop.set", ("set", item.Set), ("item", bonus.Name));
            return (line.Length > 0 ? line : "That completes the " + item.Set + ", and it came with " + bonus.Name + ".") + " " + bonus.Touch;
        }

        /// <summary>A gift with no points (a wish granted, a surprise). Its own Control Z step.</summary>
        public static void Give(Store s, string itemId, string reason)
        {
            var item = Find(itemId);
            if (item == null) return;
            s.Remember(reason);
            var b = Bag(s);
            b.Items.Add(new OwnedItem { ItemId = item.Id, Room = HomeFor(s, item), Bought = DateTime.Now });
            Put(s, b);
            s.Save();
        }

        // ---- Rooms, themes and tiers ----

        public static string RoomName(string room) => room switch
        {
            Storage => "storage",
            Outside => "the yard outside the burrow",
            "hottub" => "the hot tub deck",
            "boat" => "your tiny boat",
            _ => PlaceWorld.NameOf(PlaceIds.Room(room)).Replace("The ", "the "),
        };

        public static ShopTheme? ThemeOf(Store s, string room) => Bag(s).Theme.TryGetValue(room, out var t) ? Themes.FirstOrDefault(x => x.Id == t) : null;
        public static int TierOf(Store s, string room) => Bag(s).Tier.TryGetValue(room, out int t) ? t : 0;
        public static ShopTier? NextTier(Store s, string room) => Tiers.FirstOrDefault(t => t.Room == room && t.Tier == TierOf(s, room) + 1);
        public static ShopTier? CurrentTier(Store s, string room) => Tiers.FirstOrDefault(t => t.Room == room && t.Tier == TierOf(s, room));

        /// <summary>Buys the room's next upgrade.</summary>
        public static bool Upgrade(Store s, string room, out string said)
        {
            var next = NextTier(s, room);
            if (next == null) { said = "This room is as grand as it gets."; return false; }
            int price = Price(s, next.Price);
            if (!Ledger.Spend(s, price, "upgrading to " + next.Name, Id)) { said = Short(s, next.Name, price); return false; }
            var b = Bag(s);
            b.Tier[room] = next.Tier;
            Put(s, b);
            said = "Upgraded to " + next.Name + ". " + next.Feel + " " + PlayTime.SpendLine(s.Data) + ".";
            return true;
        }

        /// <summary>Buys a theme once; it can then go on any room for free.</summary>
        public static bool BuyTheme(Store s, string themeId, out string said)
        {
            var t = Themes.FirstOrDefault(x => x.Id == themeId);
            if (t == null) { said = "No such theme."; return false; }
            if (Bag(s).ThemesOwned.Contains(themeId)) { said = "You already have the " + t.Name + " theme."; return true; }
            int price = Price(s, t.Price);
            if (!Ledger.Spend(s, price, "the " + t.Name + " theme", Id)) { said = Short(s, "The " + t.Name + " theme", price); return false; }
            var b = Bag(s);
            b.ThemesOwned.Add(themeId);
            Put(s, b);
            said = "The " + t.Name + " theme is yours, for any room. " + PlayTime.SpendLine(s.Data) + ".";
            return true;
        }

        /// <summary>Puts an owned theme on a room, or takes it off (null). Free, one undo step.</summary>
        public static void SetTheme(Store s, string room, string? themeId) =>
            World.Change<ShopBag>(s, Id, themeId == null ? "taking a theme off a room" : "changing a room's theme", b => { if (themeId == null) b.Theme.Remove(room); else b.Theme[room] = themeId; });

        /// <summary>Moves one owned thing (by its place in the owned list) to a room, outside or storage. One undo step.</summary>
        public static void Move(Store s, int index, string room) =>
            World.Change<ShopBag>(s, Id, "moving a thing", b => { if (index >= 0 && index < b.Items.Count) b.Items[index].Room = room; });

        // ---- Wish list ----

        public static List<string> Wishes(Store s) => Bag(s).Wishes;
        public static void AddWish(Store s, string itemId) { if (!Wishes(s).Contains(itemId)) World.Change<ShopBag>(s, Id, "adding to your wish list", b => b.Wishes.Add(itemId)); }
        public static void RemoveWish(Store s, string itemId) { if (Wishes(s).Contains(itemId)) World.Change<ShopBag>(s, Id, "taking a wish off your list", b => b.Wishes.Remove(itemId)); }

        /// <summary>Points a usual day brings lately (the last 14 days), at least 1.</summary>
        public static double Pace(Store s, DateTime now)
        {
            var from = now.Date.AddDays(-14);
            int got = s.Data.Log.Where(e => e.When >= from && Engine.KarmaKinds.Contains(e.Kind)).Sum(e => e.Points);
            return Math.Max(1, got / 14.0);
        }

        /// <summary>How close she is to a wish, plainly.</summary>
        public static string HowClose(Store s, string itemId)
        {
            var i = Find(itemId);
            if (i == null) return "That wish is no longer in the catalog.";
            int price = Price(s, i), have = Ledger.Wallet(s);
            if (have >= price) return i.Name + ": " + price + " points. You have " + have + ", so you can buy it now.";
            int left = price - have;
            int days = (int)Math.Ceiling(left / Pace(s, DateTime.Now));
            return i.Name + ": " + price + " points. You have " + have + ", so " + left + " to go" + (days <= 30 ? ", about " + PlaceUi.N(days, "day") + " at your usual pace." : ". It will wait for you.");
        }

        public static void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("shop: " + what); }
            Need(Catalog.Count >= 100, "the catalog has only " + Catalog.Count + " things");
            Need(Catalog.Select(i => i.Id).Distinct().Count() == Catalog.Count, "two things share an id");
            foreach (var a in Aisles) Need(Catalog.Count(i => i.Aisle == a) >= 3, "the " + a + " aisle is nearly empty");
            foreach (var i in Catalog)
            {
                Need(i.Touch.Length > 0 && i.Sound.Length > 0, i.Id + " has no touch or sound");
                Need(i.Plays.Length == 0 || Sounds.All.Contains(i.Plays), i.Id + " plays a sound that does not exist: " + i.Plays);
                Need(i.Room == "any" || i.Room == Outside || i.Room == "hottub" || i.Room == "boat" || PlaceIds.Rooms.Contains(i.Room), i.Id + " goes in a room that does not exist: " + i.Room);
            }
            foreach (var set in Sets) Need(Find(set.Bonus) is ShopItem bi && bi.Bonus && Catalog.Count(i => i.Set == set.Set && !i.Bonus) >= 2, "the set " + set.Set);
            Need(Themes.Count >= 8 && Themes.All(t => t.Plays.Length == 0 || Sounds.All.Contains(t.Plays)), "themes");
            foreach (var r in PlaceIds.Rooms) Need(Tiers.Any(t => t.Room == r && t.Tier == 1) && Tiers.Any(t => t.Room == r && t.Tier == 2), "no upgrade tiers for " + r);

            // Prices grow slowly, capped at double.
            Need(Price(s, 50) >= 50 && Price(s, 50) <= 100, "price growth");

            // Stock: the same all week, a wish is always on the shelf, holidays only near their day.
            var mon = new DateTime(2026, 11, 2);
            Need(OnSale(s, mon).Select(i => i.Id).SequenceEqual(OnSale(s, mon.AddDays(4)).Select(i => i.Id)), "the stock changed inside a week");
            Need(HolidayNear("10-31", new DateTime(2026, 10, 20)) && !HolidayNear("10-31", new DateTime(2026, 8, 1)) && HolidayNear("01-01", new DateTime(2026, 12, 20)), "holiday weeks");
            var rare = Catalog.First(i => i.Rare);
            AddWish(s, rare.Id);
            Need(OnSale(s, mon).Contains(rare) && OnSale(s, mon.AddDays(70)).Contains(rare), "a wished rare thing left the shelf");
            Need(HowClose(s, rare.Id).Contains("points"), "how close");

            // Buying: refused when short, spends through the ledger, undo gives the points and the thing back.
            int wallet = Ledger.Wallet(s);
            var cheap = Catalog.Where(i => i.Regular).OrderBy(i => i.BasePrice).First();
            if (wallet < Price(s, cheap)) Need(!Buy(s, cheap.Id, out _) && !Owned(s, cheap.Id), "bought with too few points");
            Ledger.Pay(s, 2000, "shop test", "test", remember: true);
            wallet = Ledger.Wallet(s);
            Need(Buy(s, cheap.Id, out var said) && Owned(s, cheap.Id) && Ledger.Wallet(s) == wallet - Price(s, cheap), "buying: " + said);
            Need(s.Data.Log.Any(e => e.Kind == Ledger.SpendKind && e.Feature == Id), "the purchase is not in the ledger");
            s.Undo();
            Need(!Owned(s, cheap.Id) && Ledger.Wallet(s) == wallet, "undo did not give back the points and take the thing");

            // A whole set gives its bonus once; moving and storage never lose a thing.
            var setName = Sets.First().Set;
            foreach (var piece in Catalog.Where(i => i.Set == setName && !i.Bonus)) Need(Buy(s, piece.Id, out var why), "buying a set piece: " + why);
            Need(Owned(s, Sets.First().Bonus), "a full set gave no bonus");
            int count = OwnedItems(s).Count;
            Move(s, 0, Storage);
            Move(s, 0, Outside);
            Need(OwnedItems(s).Count == count && OwnedItems(s)[0].Room == Outside, "moving lost a thing");
            Need(BuyTheme(s, Themes[0].Id, out _), "a theme");
            SetTheme(s, "nook", Themes[0].Id);
            Need(ThemeOf(s, "nook")?.Id == Themes[0].Id, "setting a theme");
            Need(Upgrade(s, "nook", out _) && TierOf(s, "nook") == 1 && Upgrade(s, "nook", out _) && !Upgrade(s, "nook", out _), "room tiers");
            RemoveWish(s, rare.Id);
            Need(Wishes(s).Count == 0, "removing a wish");
            // The bag saves and reads back.
            Need(s.World.Save() && Bag(s).Items.Count == OwnedItems(s).Count, "saving the shop");
        }
    }
}
