using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    /// <summary>
    /// The bridge to the places and the shop (src\World\Place, Shop): direct calls, so a rename there is a build error,
    /// never a silent failure. With the market tool off, Bunny's wishes come from the content library and are paid
    /// through the Ledger directly.
    /// </summary>
    public static class FolkShop
    {
        /// <summary>She did something in a place (PlaceEvents.Did): place id, activity words. For Bunny's traits.</summary>
        public static event Action<string, string>? OnPlaceActivity;
        private static bool _wired;

        public static void Wire()
        {
            if (_wired) return;
            _wired = true;
            PlaceEvents.Did += (p, a) => OnPlaceActivity?.Invoke(p, a);
        }

        /// <summary>A thing Bunny can wish for: from this week's market stock, or the content library.</summary>
        public sealed record Ware(string Id, string Name, string Aisle, int Price, string Season, string Why, string Touch, string Sound, bool FromShop);

        /// <summary>Bunny's own wish list from the content library (used when the market is off, and for the reasons).</summary>
        public static List<Ware> ContentWares() => Folk.Items("folk.wishes").Select(i => new Ware(i.Get("id"), i.Get("name"), i.Get("aisle"), int.TryParse(i.Get("price"), out int p) ? p : 50,
            i.Get("season"), i.Get("why"), i.Get("touch"), i.Get("sound"), false)).ToList();

        public static bool MarketOn => Tools.IsOn("shop.market");

        public static List<Ware> Wares(Store s, DateTime now)
        {
            var mine = ContentWares();
            if (!MarketOn) return mine;
            var list = new List<Ware>();
            foreach (var item in Shop.OnSale(s, now))
            {
                if (item.Bonus || Shop.Owned(s, item.Id)) continue;
                string aisle = item.Aisle.ToLowerInvariant();
                // The shop has no reasons: borrow one Bunny wrote for the same aisle.
                var why = mine.Where(m => m.Aisle.Length > 0 && (aisle.Contains(m.Aisle) || m.Aisle.Contains(aisle))).Select(m => m.Why).DefaultIfEmpty("I have been thinking about it all week.").First();
                list.Add(new Ware(item.Id, item.Name, aisle, Shop.Price(s, item), item.Season.Length > 0 ? item.Season : "any", why, item.Touch, item.Sound, true));
            }
            return list.Count > 0 ? list : mine;
        }

        /// <summary>A saved wish by id, even when it is not in this week's stock.</summary>
        public static Ware? Saved(Store s, string id) => Shop.Find(id) is ShopItem i
            ? new Ware(i.Id, i.Name, i.Aisle.ToLowerInvariant(), Shop.Price(s, i), i.Season.Length > 0 ? i.Season : "any", "", i.Touch, i.Sound, true) : null;

        /// <summary>A ware by id: the market's when it came from there, else the content library's.</summary>
        public static Ware? Of(Store s, string id, bool fromShop) => fromShop ? Saved(s, id) : ContentWares().FirstOrDefault(w => w.Id == id);

        /// <summary>The name of a wish, wherever it came from.</summary>
        public static string NameOf(string id) => Shop.Find(id)?.Name ?? Folk.Items("folk.wishes").FirstOrDefault(i => i.Get("id") == id)?.Get("name") ?? id;

        /// <summary>Buys it: through the shop (it goes in her burrow) or the Ledger. Either is one Control Z step.</summary>
        public static bool Buy(Store s, Ware w, out string said)
        {
            if (w.FromShop) return Shop.Buy(s, w.Id, out said);
            int have = Ledger.Wallet(s);
            if (!Ledger.Spend(s, w.Price, "A wish for Bunny: " + w.Name, "folk.wish")) { said = "You have " + have + " of the " + w.Price + " points."; return false; }
            said = PlayTime.SpendLine(s.Data) + ".";
            return true;
        }

        public static void AddWish(Store s, Ware w) { if (w.FromShop) Shop.AddWish(s, w.Id); }

        public static string HowClose(Store s, Ware w)
        {
            if (w.FromShop) return Shop.HowClose(s, w.Id);
            int have = Ledger.Wallet(s);
            return w.Name + ": " + w.Price + " points. " + (have >= w.Price ? "You have " + have + ", enough to get it now." : "You have " + have + ", so " + (w.Price - have) + " to go.");
        }

        /// <summary>Instruments she owns from the shop, with the sound each plays (empty with the market off).</summary>
        public static List<(string Name, string Sound)> OwnedInstruments(Store s) => !MarketOn ? new() : Shop.OwnedItems(s)
            .Select(o => Shop.Find(o.ItemId)).Where(i => i != null && i.Instrument.Length > 0)
            .Select(i => (i!.Name.ToLowerInvariant(), i.Plays)).GroupBy(x => x.Item1).Select(g => g.First()).ToList();

        /// <summary>A find for the treasure chest. Call inside an undo step; with a task, un-finishing it takes the find back.</summary>
        public static void AddTreasure(Store s, string name, string touch, string where, Guid? taskId = null) => Treasure.Add(s, name, touch, where, taskId);
        /// <summary>Something she made, for the art room.</summary>
        public static void AddArt(Store s, string name, string touch, string kind) => Art.Add(s, name, touch, kind);
    }

    /// <summary>
    /// Bunny asks for things (sweep 12.2), Bunny invents small things (13.2 B), and a partner for Bunny (12.5). The asks
    /// are a line in Visit your bunny, at most one a visit and at most once a week (an idea once a month), never a pop-up;
    /// she answers in the tool, and every answer is fine. Saving toward a wish uses the shop's one wish list.
    /// </summary>
    public sealed class FolkWish : IFeature
    {
        private MainWindow _w = null!;
        private Store S => _w.Store;
        private string Bun => S.Data.Game.BunnyName;
        public const int PartnerLevel = 6;

        /// <summary>What she answered to Bunny's wishes. Saving toward one is the shop's wish list (Shop.Wishes).</summary>
        public sealed class WishBox
        {
            public Dictionary<string, DateTime> Granted { get; set; } = new();
            public Dictionary<string, DateTime> NotNow { get; set; } = new();
        }

        public sealed class Pending
        {
            public string Key { get; set; } = "";
            public string Id { get; set; } = "";
            public string Text { get; set; } = "";
            public bool Done { get; set; }
            public DateTime When { get; set; }
            public bool Shop { get; set; }
        }

        public sealed class Idea { public string Kind { get; set; } = ""; public string Name { get; set; } = ""; public string Text { get; set; } = ""; public DateTime When { get; set; } }
        private sealed class IdeaBox { public string Month { get; set; } = ""; public Idea? Idea { get; set; } public bool Shown { get; set; } public bool Decided { get; set; } }
        private sealed class PartnerBox { public DateTime Met { get; set; } public int Trait { get; set; } }

        public void Attach(MainWindow w)
        {
            _w = w;
            FolkShop.Wire();
            Folk.Tool("folk.wish", "Bunny's wishes", Drawers.World, "What your bunny wished for, and how close you are to getting it.", Wishes);
            Folk.Tool("folk.ideas", "Bunny's ideas", Drawers.World, "Small new things your bunny thought up; keep or toss each one.", Ideas);
            Folk.Tool("folk.partner", "Bunny's partner", Drawers.World, "The bunny your bunny met, and their little outings together.", Partner);
            Folk.Visit("folk.wish", AskWish, ask: true);
            Folk.Visit("folk.ideas", AskIdea, ask: true);
            Folk.Visit("folk.partner", PartnerVisit, ask: true);
            Folk.Reminder("folk.wish", "wish", (s, now) => WaitingWish(s, now) != null ? s.Data.Game.BunnyName + "'s wish, in Bunny's wishes" : null);
            Folk.Reminder("folk.ideas", "idea", (s, now) => IdeaWaiting(s) ? s.Data.Game.BunnyName + "'s idea, in Bunny's ideas" : null);
            w.Started += () => { PrewriteWish(); PrewriteIdea(); };
            GameEvents.WeekStarted += _ => PrewriteWish();
        }

        // ---- Wishes ----

        public static WishBox Box(Store s) => s.World.Get<WishBox>("folk.wish");

        public static bool GrantedLately(Store s, DateTime now) => Box(s).Granted.Values.Any(d => (now - d).TotalDays <= 2 && d <= now);

        /// <summary>The thing Bunny would ask for this week: not had, not on the wish list, not turned down lately; its season, likes and traits first.</summary>
        public static FolkShop.Ware? Choose(Store s, DateTime now, List<FolkShop.Ware>? wares = null)
        {
            wares ??= FolkShop.Wares(s, now);
            var b = Box(s);
            var wished = Shop.Wishes(s).ToHashSet();
            string season = RealWorld.Season(now);
            double outdoorsy = FolkBunny.Traits(s).V.GetValueOrDefault("outdoorsy");
            var rng = new Random(Folk.Hash(Folk.WeekKey(now)));
            bool Aisle(FolkShop.Ware x, params string[] words) => words.Any(w => x.Aisle.Contains(w, StringComparison.OrdinalIgnoreCase));
            return wares.Where(x => x.Id.Length > 0 && !b.Granted.ContainsKey(x.Id) && !wished.Contains(x.Id) && !(x.FromShop && Shop.Owned(s, x.Id))
                    && !(b.NotNow.TryGetValue(x.Id, out var no) && (now - no).TotalDays < 28)
                    && (x.Season.Length == 0 || x.Season.Equals("any", StringComparison.OrdinalIgnoreCase) || x.Season.Equals(season, StringComparison.OrdinalIgnoreCase)))
                .Select(x => (x, score: (x.Season.Equals(season, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                    + (FolkBunny.Likes(s, x.Name + " " + x.Aisle) ? 3 : 0)
                    + (outdoorsy > 2 && Aisle(x, "garden", "pool", "trip") ? 2 : 0)
                    + (outdoorsy < -2 && Aisle(x, "bedding", "books", "decor", "furniture") ? 2 : 0)
                    + rng.NextDouble()))
                .OrderByDescending(x => x.score).Select(x => x.x).FirstOrDefault();
        }

        private async void PrewriteWish()
        {
            try
            {
                var now = DateTime.Now;
                string week = Folk.WeekKey(now);
                var p = S.World.GetKept<Pending>("folk.wish.pending");
                if (p.Key == week || !SmartBunny.Ready || Calm.FunPaused || !Tools.IsOn("folk.wish") || Choose(S, now) is not FolkShop.Ware ware) return;
                p = new Pending { Key = week, Id = ware.Id };
                S.World.SetKept("folk.wish.pending", p);
                p.Text = await Folk.Write(S, "folk.wish", "Bunny asks her for one thing from the village shop: " + ware.Name + " (" + ware.Aisle + "). Write Bunny's request in its own voice, one or two sentences, saying why it wants it from what it has been doing lately, and that no is completely fine.", () => "");
                S.World.SetKept("folk.wish.pending", p);
                S.World.Save();
            }
            catch (Exception e) { ErrorLog.Write("Folk: writing Bunny's wish failed: " + e.GetType().Name); }
        }

        /// <summary>The wish Bunny asked for that she has not answered yet (Escape leaves it waiting), or null.</summary>
        public static (Pending Ask, FolkShop.Ware Ware)? WaitingWish(Store s, DateTime now)
        {
            var a = s.World.GetKept<Pending>("folk.wish.asked");
            if (a.Id.Length == 0 || a.Text.Length == 0) return null;
            var b = Box(s);
            bool answered = b.Granted.TryGetValue(a.Id, out var g) && g >= a.When || b.NotNow.TryGetValue(a.Id, out var n) && n >= a.When
                || a.Shop && (Shop.Wishes(s).Contains(a.Id) || Shop.Owned(s, a.Id));
            if (answered || FolkShop.Of(s, a.Id, a.Shop) is not FolkShop.Ware w) return null;
            return (a, w);
        }

        /// <summary>Once a week Bunny asks for one thing: a line in the visit, answered in Bunny's wishes.</summary>
        private IEnumerable<string> AskWish(MainWindow w, DateTime now)
        {
            string week = Folk.WeekKey(now);
            if (Calm.FunPaused || S.World.GetKept<Pending>("folk.wish.asked").Key == week || Choose(S, now) is not FolkShop.Ware ware || !Folk.TryAsk()) return Array.Empty<string>();
            var pending = S.World.GetKept<Pending>("folk.wish.pending");
            string ask = pending.Key == week && pending.Id == ware.Id && pending.Text.Length > 0 ? pending.Text
                : Folk.Pick(S, "folk.wish.ask", ("item", Lower(ware.Name)), ("why", Folk.End(ware.Why)));
            S.World.SetKept("folk.wish.asked", new Pending { Key = week, Id = ware.Id, Text = ask, When = now, Shop = ware.FromShop });
            S.World.Save();
            FolkBunny.Remember(S, ask);
            return new[] { Bun + " has a wish for you. Bunny's wishes, in the toolbox, to hear it and answer." };
        }

        /// <summary>The answers (no wish list for a wish from Bunny's own list). Returns what Bunny says back; empty when she pressed Escape, so it stays waiting.</summary>
        private string Answer(Window owner, FolkShop.Ware ware, string ask, DateTime now)
        {
            int have = Ledger.Wallet(S);
            var options = ware.FromShop ? new[] { "Yes, get it now", "Save toward it, put it on my wish list", "Not now" } : new[] { "Yes, get it now", "Not now" };
            int pick = Folk.Choose(owner, Bun + " has a wish", ask + " It costs " + ware.Price + " points, and you have " + have + ".", options);
            if (pick < 0) return "";
            if (pick == 0) return Grant(ware, now);
            if (ware.FromShop && pick == 1)
            {
                Shop.AddWish(S, ware.Id);
                return Folk.Pick(S, "folk.wish.save", ("item", Lower(ware.Name))) + " " + FolkShop.HowClose(S, ware);
            }
            World.Change<WishBox>(S, "folk.wish", "telling Bunny not now", b => b.NotNow[ware.Id] = now);
            return Folk.Pick(S, "folk.wish.notnow");
        }

        /// <summary>Spends for a wish (one Control Z step gives the points back) and says thank you with a small surprise.</summary>
        public string Grant(FolkShop.Ware ware, DateTime now)
        {
            if (!FolkShop.Buy(S, ware, out string said))
            {
                if (!ware.FromShop) return said + " It can wait in Bunny's wishes until you have enough.";
                Shop.AddWish(S, ware.Id);
                return said + " I put " + Lower(ware.Name) + " on your wish list, so you can save toward it.";
            }
            MarkGranted(S, ware.Id, now);
            SoundBank.Play("Bunny, binky");
            string thanks = Folk.Pick(S, "folk.thanks", ("item", Lower(ware.Name)));
            if (FolkBunny.Likes(S, ware.Name + " " + ware.Aisle)) thanks += " It is one of its favorite kinds of thing, so it is extra happy.";
            return thanks + " " + Folk.Pick(S, "folk.surprise") + " " + said;
        }

        /// <summary>After the spend's own undo step, so Control Z takes both back together.</summary>
        public static void MarkGranted(Store s, string id, DateTime now)
        {
            var b = Box(s);
            b.Granted[id] = now;
            s.World.Set("folk.wish", b);
            s.World.Save();
        }

        private static string Lower(string name) => name.Length > 1 && char.IsUpper(name[0]) && !char.IsUpper(name[1]) && name.Split(' ')[0] is "A" or "An" or "The" or "Some" ? char.ToLowerInvariant(name[0]) + name[1..] : name;

        private void Wishes()
        {
            Folk.List(_w, Bun + "'s wishes", () =>
            {
                var now = DateTime.Now;
                var b = Box(S);
                var rows = new List<Folk.Row> { new(PlayTime.SpendLine(S.Data) + ".") };
                if (WaitingWish(S, now) is { } wait)
                    rows.Add(new Folk.Row("Waiting for your answer: " + wait.Ask.Text + " Enter to answer.", win => { if (Answer(win, wait.Ware, wait.Ask.Text, DateTime.Now) is { Length: > 0 } said) Folk.Say(win, said); }));
                foreach (var id in Shop.Wishes(S).ToList())
                {
                    if (Shop.Owned(S, id) || FolkShop.Saved(S, id) is not FolkShop.Ware x) continue;
                    rows.Add(new Folk.Row("Saving toward " + FolkShop.HowClose(S, x) + " Enter to get it or take it off the list.", win =>
                    {
                        int pick = Folk.Choose(win, x.Name, x.Name + ". " + Folk.End(x.Touch), new[] { "Get it now, " + x.Price + " points", "Take it off the wish list", "Keep saving" });
                        if (pick == 0) Folk.Say(win, Grant(x, DateTime.Now));
                        else if (pick == 1) { Shop.RemoveWish(S, id); Folk.Say(win, Folk.Pick(S, "folk.wish.notnow")); }
                    }));
                }
                foreach (var (id, when) in b.Granted.OrderByDescending(g => g.Value))
                    rows.Add(new Folk.Row("Wish granted " + when.ToString("MMMM d") + ": " + FolkShop.NameOf(id) + "."));
                if (rows.Count == 1) rows.Add(new Folk.Row(Bun + " has not asked for anything yet. It asks at most once a week, in Visit your bunny."));
                return rows;
            }, "Enter on Bunny's wish answers it. Enter on a wish you are saving for gets it or takes it off the list.");
        }

        // ---- Bunny's ideas (sweep 13.2 B) ----

        private Idea FromLibrary(DateTime now)
        {
            string season = RealWorld.Season(now);
            var options = Folk.Items("folk.ideas").Where(i => i.Get("season") is "any" or "" || i.Get("season") == season).ToList();
            var kept = S.World.Get<List<Idea>>("folk.ideas").Select(i => i.Name).ToHashSet();
            var fresh = options.Where(i => !kept.Contains(i.Get("name"))).ToList();
            if (fresh.Count == 0) fresh = options;
            var liked = fresh.Where(i => FolkBunny.Likes(S, i.Get("name") + " " + i.Get("text"))).ToList();
            var pool = liked.Count > 0 && Folk.Rng.Next(2) == 0 ? liked : fresh;
            var pick = pool[Folk.Rng.Next(pool.Count)];
            return new Idea { Kind = pick.Get("kind"), Name = pick.Get("name"), Text = Folk.Fill(S, pick.Get("text")), When = now };
        }

        private async void PrewriteIdea()
        {
            try
            {
                var now = DateTime.Now;
                var box = S.World.GetKept<IdeaBox>("folk.idea");
                if (box.Month == Folk.MonthKey(now) || !Tools.IsOn("folk.ideas") || Calm.FunPaused) return;
                box = new IdeaBox { Month = Folk.MonthKey(now), Idea = FromLibrary(now) };
                S.World.SetKept("folk.idea", box);
                S.World.Save();
                if (!SmartBunny.Ready) return;
                string text = await Folk.Write(S, "folk.ideas", "Invent one small new thing for Bunny's village, fitted to Bunny's likes and personality and the season (" + RealWorld.Season(now)
                    + "): an event, a shop item, a neighbor story or a new spot to explore. First line: just its short name. Then 2 or 3 sentences in Bunny's voice pitching it, touch and sound first.", () => "");
                var parts = text.Split('\n', 2, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) return;
                box = S.World.GetKept<IdeaBox>("folk.idea");
                if (box.Shown) return;
                box.Idea = new Idea { Kind = "idea", Name = parts[0].Trim().TrimEnd('.', ':'), Text = parts[1].Trim(), When = now };
                S.World.SetKept("folk.idea", box);
                S.World.Save();
            }
            catch (Exception e) { ErrorLog.Write("Folk: Bunny's idea failed: " + e.GetType().Name); }
        }

        /// <summary>The month's idea is told and not yet kept or tossed. Undoing a keep puts it back here.</summary>
        private static bool IdeaWaiting(Store s)
        {
            var box = s.World.GetKept<IdeaBox>("folk.idea");
            return box.Idea is Idea i && box.Shown && !box.Decided && !s.World.Get<List<Idea>>("folk.ideas").Any(k => k.Name == i.Name);
        }

        private IEnumerable<string> AskIdea(MainWindow w, DateTime now)
        {
            var box = S.World.GetKept<IdeaBox>("folk.idea");
            if (Calm.FunPaused) return Array.Empty<string>();
            if (box.Month != Folk.MonthKey(now)) box = new IdeaBox { Month = Folk.MonthKey(now), Idea = FromLibrary(now) };
            if (box.Shown || box.Idea == null || !Folk.TryAsk()) return Array.Empty<string>();
            box.Shown = true;
            S.World.SetKept("folk.idea", box);
            S.World.Save();
            return new[] { Bun + " had an idea for the village. Bunny's ideas, in the toolbox, to hear it and keep or toss it." };
        }

        /// <summary>Keep or toss; Escape leaves it waiting in Bunny's ideas. Returns what Bunny says back.</summary>
        private string Decide(Window owner, Idea idea)
        {
            int pick = Folk.Choose(owner, Bun + " had an idea", idea.Name + ". " + idea.Text, new[] { "Keep it", "Toss it" });
            if (pick < 0) return "";
            if (pick == 1)
            {
                var box = S.World.GetKept<IdeaBox>("folk.idea");
                if (box.Idea?.Name == idea.Name) { box.Decided = true; S.World.SetKept("folk.idea", box); }
                S.World.Save();
                return Folk.Pick(S, "folk.ideas.toss");
            }
            // Kept: the list itself says so, so Control Z puts the idea back to waiting.
            World.Change<List<Idea>>(S, "folk.ideas", "keeping Bunny's idea", l => { if (!l.Any(i => i.Name == idea.Name)) l.Add(idea); });
            return Folk.Pick(S, "folk.ideas.keep");
        }

        private void Ideas()
        {
            Folk.List(_w, Bun + "'s ideas", () =>
            {
                var rows = new List<Folk.Row>();
                var box = S.World.GetKept<IdeaBox>("folk.idea");
                if (IdeaWaiting(S) && box.Idea is Idea open)
                    rows.Add(new Folk.Row("Waiting for your answer: " + open.Name + ". " + open.Text, win => { if (Decide(win, open) is { Length: > 0 } said) Folk.Say(win, said); }));
                foreach (var idea in Enumerable.Reverse(S.World.Get<List<Idea>>("folk.ideas")))
                    rows.Add(new Folk.Row("Kept " + idea.When.ToString("MMMM yyyy") + ": " + idea.Name + ". " + idea.Text, win =>
                    {
                        if (Folk.Choose(win, idea.Name, "Toss this idea?", new[] { "Keep it", "Toss it" }) != 1) return;
                        World.Change<List<Idea>>(S, "folk.ideas", "tossing Bunny's idea", l => l.RemoveAll(i => i.Name == idea.Name));
                        Folk.Say(win, Folk.Pick(S, "folk.ideas.toss"));
                    }));
                if (rows.Count == 0) rows.Add(new Folk.Row("No ideas kept yet. About once a month " + Bun + " thinks of something new and tells you in Visit your bunny."));
                return rows;
            }, "Enter on a kept idea can toss it.");
        }

        // ---- A partner for Bunny (sweep 12.5) ----

        private PartnerBox PBox => S.World.GetKept<PartnerBox>("folk.partner");
        public static bool Met(Store s) => s.World.GetKept<PartnerBox>("folk.partner").Met != default;

        private IEnumerable<string> PartnerVisit(MainWindow w, DateTime now)
        {
            if (!Met(S))
            {
                if (Engine.LevelFor(Engine.Total(S.Data)) < PartnerLevel || Calm.FunPaused || !Folk.TryAsk()) return Array.Empty<string>();
                S.World.SetKept("folk.partner", new PartnerBox { Met = now, Trait = Folk.Rng.Next(Math.Max(1, Folk.Items("folk.partner").Count)) });
                S.World.Save();
                return new[] { Bun + " met someone special in the village. Bunny's partner, in the toolbox, tells you how they met, and you can name them there." };
            }
            if (!Folk.First(S, "partner.visit." + Folk.WeekKey(now))) return Array.Empty<string>();
            return new[] { Folk.Fill(S, "{partner} came by this week. ") + Outing(now) };
        }

        private void NameIt(Window owner)
        {
            var name = Folk.Ask(owner, "Name Bunny's partner", "What should " + Bun + "'s partner be called?", Folk.NamesOf(S).Partner is { Length: > 0 } n ? n : "Maple");
            if (name == null) return;
            World.Change<Folk.Names>(S, "folk.names", "naming Bunny's partner", x => x.Partner = name);
            Folk.Say(owner, name + " it is. " + Bun + " approves and has already told the whole village.");
        }

        private string Outing(DateTime now) => Folk.Nth(S, "folk.outings", (int)((now.Date - PBox.Met.Date).TotalDays / 7));

        private void Partner()
        {
            if (!Met(S))
            {
                int level = Engine.LevelFor(Engine.Total(S.Data));
                _w.Say(Bun + " has a feeling it will meet someone special at level " + PartnerLevel + ". You are level " + level + ".", true);
                return;
            }
            Folk.List(_w, Bun + " and " + Folk.Fill(S, "{partner}"), () =>
            {
                var p = PBox;
                var t = Folk.Item("folk.partner", p.Trait);
                bool named = Folk.NamesOf(S).Partner.Length > 0;
                var rows = new List<Folk.Row>
                {
                    new(Folk.Fill(S, "{partner}: " + t.Get("trait") + ". " + Folk.End(t.Get("line")) + " Likes " + t.Get("like") + ". Not a fan of " + t.Get("dislike") + ".")),
                    new("How they met, " + p.Met.ToString("MMMM d") + ". Enter to read.", win => Folk.Read(win, Bun + " met someone", Folk.Fill(S, Folk.NthBlock("folk.partner.meet", 0)?.Body ?? ""))),
                    new((named ? "Rename " : "Name ") + Bun + "'s partner. Enter to choose a name.", NameIt),
                };
                int weeks = (int)((DateTime.Now.Date - p.Met.Date).TotalDays / 7);
                for (int i = weeks; i >= 0; i--) rows.Add(new Folk.Row((i == weeks ? "This week: " : p.Met.AddDays(7 * i).ToString("MMMM d") + ": ") + Folk.Nth(S, "folk.outings", i)));
                return rows;
            }, "Their outings, newest first. They stay in the guest bedroom when they visit.");
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Folk, wishes: " + what); }
            foreach (var t in new[] { "folk.wish.ask", "folk.wish.notnow", "folk.wish.save", "folk.ideas.toss", "folk.ideas.keep", "folk.outings" })
                Need(Content.Lines(t).Count >= 20, t + " has fewer than 20 lines");
            Need(Folk.Items("folk.wishes").Count >= 20 && Folk.Items("folk.ideas").Count >= 20 && Folk.Items("folk.partner").Count >= 5, "wishes, ideas or partner items");
            var now = new DateTime(2026, 11, 4, 10, 0, 0);
            var mine = FolkShop.ContentWares();
            Need(mine.Count >= 20 && mine.All(x => x.Price > 0 && x.Name.Length > 0 && x.Id.Length > 0), "the wish catalog");

            // The market's stock reaches Bunny's wishes with real ids, names and prices (no silent empty values).
            var wares = FolkShop.Wares(store, now);
            Need(wares.Count > 0 && wares.All(x => x.Id.Length > 0 && x.Name.Length > 0 && x.Price > 0), "wares from the shop are empty");
            if (FolkShop.MarketOn && Shop.OnSale(store, now).Any(i => !i.Bonus)) Need(wares.Any(x => x.FromShop), "the market's stock never reaches Bunny's wishes");
            if (wares.FirstOrDefault(x => x.FromShop) is FolkShop.Ware sw)
            {
                Need(FolkShop.Saved(store, sw.Id)?.Name == sw.Name && FolkShop.NameOf(sw.Id) == sw.Name, "a saved shop wish cannot be found by id");
                Shop.AddWish(store, sw.Id);
                Need(Choose(store, now, new List<FolkShop.Ware> { sw }) == null, "Bunny wished for something already on the wish list");
                store.Undo();
            }
            if (Shop.Catalog.FirstOrDefault(i => i.Instrument.Length > 0 && !i.Bonus) is ShopItem inst && FolkShop.MarketOn)
            {
                Shop.Give(store, inst.Id, "test");
                Need(FolkShop.OwnedInstruments(store).Any(x => x.Name == inst.Name.ToLowerInvariant()), "a bought instrument does not join the band");
                store.Undo();
            }

            // In summer a winter wish never comes.
            var summer = new DateTime(2026, 7, 1);
            store.World.Set("folk.wish", new WishBox());
            for (int i = 0; i < 10; i++)
            {
                var pick = Choose(store, summer.AddDays(7 * i), mine);
                if (RealWorld.Season(summer.AddDays(7 * i)) != "Summer") break;
                Need(pick != null && pick.Season is "any" or "Summer" or "", "a wish out of season");
            }
            // Saying not now keeps it from being asked again for four weeks.
            var first = Choose(store, now, mine)!;
            World.Change<WishBox>(store, "folk.wish", "test", b => b.NotNow[first.Id] = now);
            Need(Choose(store, now, mine)?.Id != first.Id, "not now did not count");
            store.Undo();

            // A wish she has not answered stays waiting (Escape is not "not now"); answering ends it, and Control Z brings it back.
            store.World.SetKept("folk.wish.asked", new Pending { Key = Folk.WeekKey(now), Id = first.Id, Text = "May I?", When = now, Shop = false });
            Need(WaitingWish(store, now)?.Ware.Id == first.Id, "the wish Bunny asked for is not waiting");
            World.Change<WishBox>(store, "folk.wish", "test", b => b.NotNow[first.Id] = now.AddMinutes(1));
            Need(WaitingWish(store, now) == null, "an answered wish still waits");
            store.Undo();
            Need(WaitingWish(store, now) != null, "undo did not bring the wish back to waiting");
            store.World.SetKept("folk.wish.asked", new Pending());

            // Granting spends through the ledger, and one Control Z gives the points back and the wish too.
            store.Data.Log.Add(new LogEntry { Kind = "Done", When = now, Points = 1000, TaskId = Guid.NewGuid() });
            int before = Ledger.Wallet(store);
            var ware = mine.First(x => x.Id != first.Id);
            Need(FolkShop.Buy(store, ware, out _), "buying a wish with enough points");
            MarkGranted(store, ware.Id, now);
            Need(Ledger.Wallet(store) == before - ware.Price && Box(store).Granted.ContainsKey(ware.Id), "the wish was not paid and granted");
            Need(GrantedLately(store, now.AddDays(1)) && !GrantedLately(store, now.AddDays(5)), "a happy mood after a wish");
            store.Undo();
            Need(Ledger.Wallet(store) == before && !Box(store).Granted.ContainsKey(ware.Id), "undo did not give the points and the wish back");
            Need(!FolkShop.Buy(store, ware with { Price = before + 1 }, out string said) && said.Length > 0 && Ledger.Wallet(store) == before, "buying with too few points");
            store.Data.Log.RemoveAll(e => e.Points == 1000);
            Need(Lower("A small aquarium") == "a small aquarium" && Lower("Grandma's quilt") == "Grandma's quilt", "lower case names");
            Need(Folk.NthBlock("folk.partner.meet", 0)?.Body.Length > 20, "the partner's first meeting");

            // Visit your bunny stays short: one ask a visit, at most three extra lines, reminders folded into one line once a day.
            GameFeature.StartVisit();
            Need(Folk.TryAsk() && !Folk.TryAsk(), "two asks in one visit");
            Folk.Remind(store, "test", now, "2 letters");
            Folk.Remind(store, "test2", now, "this week's radio episode");
            Folk.Remind(store, "test", now, "2 letters");
            Need(GameFeature.VisitReminders.Count == 2, "a reminder came twice in a day");
            GameFeature.StartVisit();
            Folk.Remind(store, "test", now, "2 letters");
            Need(GameFeature.VisitReminders.Count == 0, "a reminder was said again the same day");
            Folk.Remind(store, "test", now.AddDays(1), "2 letters");
            Need(GameFeature.VisitReminders.Count == 1, "a reminder never came back the next day");
            var lines = GameFeature.ComposeVisit(new List<string> { "newest" }, new List<string> { "a", "b", "c", "d", "e" }, new List<string> { "status" }, new List<string> { "2 letters", "a new chapter from Hazel" });
            Need(lines.Count == 6 && lines[0] == "newest" && lines[4] == "status" && lines[5] == "Waiting for you: 2 letters and a new chapter from Hazel.", "the visit lines: " + string.Join(" | ", lines));
            GameFeature.StartVisit();
        }
    }
}
