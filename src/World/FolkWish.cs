using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;

namespace BusyBunny
{
    /// <summary>
    /// The bridge to the places and the shop (src\World\Place, Shop), built by another stage at the same time. It finds
    /// their public API by name at run time (WORLD-API draft 1), so this code works with or without them: with no shop,
    /// Bunny's wishes come from the content library and are paid through the Ledger directly.
    /// </summary>
    public static class FolkShop
    {
        private static Type? T(string name) => typeof(App).Assembly.GetType("BusyBunny." + name);
        private static object? Call(string type, string method, params object?[] args)
        {
            var m = T(type)?.GetMethods(BindingFlags.Public | BindingFlags.Static).FirstOrDefault(x => x.Name == method && x.GetParameters().Length == args.Length);
            return m?.Invoke(null, args);
        }
        private static bool Has(string type, string method, int args) =>
            T(type)?.GetMethods(BindingFlags.Public | BindingFlags.Static).Any(x => x.Name == method && x.GetParameters().Length == args) == true;
        private static object? Prop(object? o, string name) => o?.GetType().GetProperty(name)?.GetValue(o);
        private static string Str(object? o, string name) => Prop(o, name)?.ToString() ?? "";

        /// <summary>She did something in a place (PlaceEvents.Did): place id, activity words. For Bunny's traits.</summary>
        public static event Action<string, string>? OnPlaceActivity;
        private static bool _wired;

        public static void Wire()
        {
            if (_wired) return;
            _wired = true;
            try
            {
                var ev = T("PlaceEvents")?.GetEvent("Did", BindingFlags.Public | BindingFlags.Static);
                if (ev?.EventHandlerType == typeof(Action<string, string>)) ev.AddEventHandler(null, new Action<string, string>((p, a) => OnPlaceActivity?.Invoke(p, a)));
            }
            catch (Exception e) { ErrorLog.Write("Folk: could not listen to the places: " + e.GetType().Name); }
        }

        public static bool HasShop => Has("Shop", "Buy", 3) && Has("Shop", "OnSale", 2);

        /// <summary>A thing Bunny can wish for: from this week's shop stock when there is a shop, else the content library.</summary>
        public sealed record Ware(string Id, string Name, string Aisle, int Price, string Season, string Why, string Touch, string Sound, bool FromShop);

        public static List<Ware> Wares(Store s, DateTime now)
        {
            var mine = Folk.Items("folk.wishes").Select(i => new Ware(i.Get("id"), i.Get("name"), i.Get("aisle"), int.TryParse(i.Get("price"), out int p) ? p : 50,
                i.Get("season"), i.Get("why"), i.Get("touch"), i.Get("sound"), false)).ToList();
            if (!HasShop) return mine;
            try
            {
                var list = new List<Ware>();
                if (Call("Shop", "OnSale", s, now) is IEnumerable sale)
                    foreach (var item in sale)
                    {
                        if (item == null) continue;
                        int price = Call("Shop", "Price", s, item) is int pr ? pr : Prop(item, "BasePrice") is int bp ? bp : 50;
                        string aisle = Str(item, "Aisle").ToLowerInvariant();
                        // The shop has no reasons: borrow one Bunny wrote for the same aisle.
                        var why = mine.Where(m => aisle.Contains(m.Aisle) || m.Aisle.Contains(aisle)).Select(m => m.Why).DefaultIfEmpty("I have been thinking about it all week.").First();
                        list.Add(new Ware(Str(item, "Id"), Str(item, "Name"), aisle, price, Str(item, "Season") is { Length: > 0 } se ? se : "any", why, Str(item, "Touch"), Str(item, "Sound"), true));
                    }
                return list.Count > 0 ? list : mine;
            }
            catch (Exception e) { ErrorLog.Write("Folk: the shop's stock could not be read: " + e.GetType().Name); return mine; }
        }

        /// <summary>Buys it: through the shop (it goes in her burrow) or the Ledger. Either is one Control Z step.</summary>
        public static bool Buy(Store s, Ware w, out string said)
        {
            if (w.FromShop && HasShop)
            {
                try
                {
                    var args = new object?[] { s, w.Id, null };
                    bool ok = T("Shop")!.GetMethods().First(x => x.Name == "Buy" && x.GetParameters().Length == 3).Invoke(null, args) is true;
                    said = args[2] as string ?? "";
                    return ok;
                }
                catch (Exception e) { ErrorLog.Write("Folk: buying from the shop failed: " + e.GetType().Name); }
            }
            int have = Ledger.Wallet(s);
            if (!Ledger.Spend(s, w.Price, "A wish for Bunny: " + w.Name, "folk.wish"))
            {
                said = "You have " + have + " of the " + w.Price + " points.";
                return false;
            }
            said = PlayTime.SpendLine(s.Data) + ".";
            return true;
        }

        public static void AddWish(Store s, Ware w) { if (w.FromShop) try { Call("Shop", "AddWish", s, w.Id); } catch { } }

        public static string HowClose(Store s, Ware w)
        {
            if (w.FromShop) try { if (Call("Shop", "HowClose", s, w.Id) is string line && line.Length > 0) return line; } catch { }
            int have = Ledger.Wallet(s);
            return w.Name + ": " + w.Price + " points. " + (have >= w.Price ? "You have " + have + ", enough to get it now." : "You have " + have + ", so " + (w.Price - have) + " to go.");
        }

        /// <summary>Instruments she owns from the shop, with the sound each plays (empty with no shop).</summary>
        public static List<(string Name, string Sound)> OwnedInstruments(Store s)
        {
            var list = new List<(string, string)>();
            try
            {
                if (Call("Shop", "OwnedItems", s) is not IEnumerable owned) return list;
                foreach (var o in owned)
                {
                    var item = Call("Shop", "Find", Str(o, "ItemId"));
                    var inst = Prop(item, "Instrument");
                    if (inst is true || inst is string { Length: > 0 }) list.Add((Str(item, "Name").ToLowerInvariant(), Str(item, "Plays")));
                }
            }
            catch (Exception e) { ErrorLog.Write("Folk: instruments could not be read: " + e.GetType().Name); }
            return list.GroupBy(x => x.Item1).Select(g => g.First()).ToList();
        }

        /// <summary>A find for the treasure chest room, when the places have one. Call inside an undo step.</summary>
        public static void AddTreasure(Store s, string name, string touch, string where) { try { Call("Treasure", "Add", s, name, touch, where); } catch (Exception e) { ErrorLog.Write("Folk: treasure: " + e.GetType().Name); } }
        /// <summary>Something she made, for the art room, when the places have one.</summary>
        public static void AddArt(Store s, string name, string touch, string kind) { try { Call("Art", "Add", s, name, touch, kind); } catch (Exception e) { ErrorLog.Write("Folk: art: " + e.GetType().Name); } }

        /// <summary>Puts lines into one of the places' places (PlaceHooks.AddThings). False when there is no such hook.</summary>
        public static bool AddThings(string placeField, Func<Store, List<PlaceThing>> things)
        {
            try
            {
                if (T("PlaceIds")?.GetField(placeField)?.GetValue(null) is not string id || !Has("PlaceHooks", "AddThings", 2)) return false;
                Call("PlaceHooks", "AddThings", id, things);
                return true;
            }
            catch (Exception e) { ErrorLog.Write("Folk: place hook " + placeField + ": " + e.GetType().Name); return false; }
        }
    }

    /// <summary>
    /// Bunny asks for things (sweep 12.2), Bunny invents small things (13.2 B), and a partner for Bunny (12.5). The asks
    /// come in Visit your bunny, at most once a week (an idea once a month), never as a pop-up; every answer is fine.
    /// </summary>
    public sealed class FolkWish : IFeature
    {
        private MainWindow _w = null!;
        private Store S => _w.Store;
        public const int PartnerLevel = 6;

        public sealed class WishBox
        {
            public List<string> Saving { get; set; } = new();
            public Dictionary<string, DateTime> Granted { get; set; } = new();
            public Dictionary<string, DateTime> NotNow { get; set; } = new();
        }

        private sealed class Pending { public string Key { get; set; } = ""; public string Id { get; set; } = ""; public string Text { get; set; } = ""; public bool Done { get; set; } }

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
            Folk.Visit("folk.wish", AskWish);
            Folk.Visit("folk.ideas", AskIdea);
            Folk.Visit("folk.partner", PartnerVisit);
            w.Started += () => { PrewriteWish(); PrewriteIdea(); };
            GameEvents.WeekStarted += _ => PrewriteWish();
        }

        // ---- Wishes ----

        public static WishBox Box(Store s) => s.World.Get<WishBox>("folk.wish");

        public static bool GrantedLately(Store s, DateTime now) => Box(s).Granted.Values.Any(d => (now - d).TotalDays <= 2 && d <= now);

        /// <summary>The thing Bunny would ask for this week: not had, not being saved for, not turned down lately; its season, likes and traits first.</summary>
        public static FolkShop.Ware? Choose(Store s, DateTime now, List<FolkShop.Ware>? wares = null)
        {
            wares ??= FolkShop.Wares(s, now);
            var b = Box(s);
            string season = RealWorld.Season(now);
            double outdoorsy = FolkBunny.Traits(s).V.GetValueOrDefault("outdoorsy");
            var rng = new Random(Folk.Hash(Folk.WeekKey(now)));
            return wares.Where(x => x.Id.Length > 0 && !b.Granted.ContainsKey(x.Id) && !b.Saving.Contains(x.Id)
                    && !(b.NotNow.TryGetValue(x.Id, out var no) && (now - no).TotalDays < 28)
                    && (x.Season.Length == 0 || x.Season.Equals("any", StringComparison.OrdinalIgnoreCase) || x.Season.Equals(season, StringComparison.OrdinalIgnoreCase)))
                .Select(x => (x, score: (x.Season.Equals(season, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                    + (FolkBunny.Likes(s, x.Name + " " + x.Aisle) ? 3 : 0)
                    + (outdoorsy > 2 && x.Aisle is "garden" or "pool" or "trip" ? 2 : 0)
                    + (outdoorsy < -2 && x.Aisle is "bedding" or "books" or "decor" or "furniture" ? 2 : 0)
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

        private IEnumerable<string> AskWish(MainWindow w, DateTime now)
        {
            var lines = new List<string>();
            var wares = FolkShop.Wares(S, now);
            string week = Folk.WeekKey(now);
            if (!Calm.FunPaused && S.World.GetKept<Pending>("folk.wish.asked").Key != week && Choose(S, now, wares) is FolkShop.Ware ware)
            {
                S.World.SetKept("folk.wish.asked", new Pending { Key = week, Id = ware.Id });
                S.World.Save();
                var pending = S.World.GetKept<Pending>("folk.wish.pending");
                string ask = pending.Key == week && pending.Id == ware.Id && pending.Text.Length > 0 ? pending.Text
                    : Folk.Pick(S, "folk.wish.ask", ("item", Lower(ware.Name)), ("why", Folk.End(ware.Why)));
                FolkBunny.Remember(S, ask);
                string said = Answer(w, ware, ask, now);
                if (said.Length > 0) { Game.Note(S, said); S.Save(); w.Say(said, true); }
            }
            var b = Box(S);
            foreach (var id in b.Saving)
                if (wares.FirstOrDefault(x => x.Id == id) is FolkShop.Ware x) lines.Add("Saving toward " + FolkShop.HowClose(S, x));
            return lines;
        }

        /// <summary>The three answers. Returns what Bunny says back.</summary>
        private string Answer(MainWindow w, FolkShop.Ware ware, string ask, DateTime now)
        {
            int have = Ledger.Wallet(S);
            int pick = Folk.Choose(w, S.Data.Game.BunnyName + " has a wish", ask + " It costs " + ware.Price + " points, and you have " + have + ".",
                new[] { "Yes, get it now", "Save toward it, put it on my wish list", "Not now" });
            if (pick == 0) return Grant(ware, now);
            if (pick == 1)
            {
                World.Change<WishBox>(S, "folk.wish", "saving toward Bunny's wish", b => { if (!b.Saving.Contains(ware.Id)) b.Saving.Add(ware.Id); });
                FolkShop.AddWish(S, ware);
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
                World.Change<WishBox>(S, "folk.wish", "saving toward Bunny's wish", b => { if (!b.Saving.Contains(ware.Id)) b.Saving.Add(ware.Id); });
                FolkShop.AddWish(S, ware);
                return said + " I put " + Lower(ware.Name) + " on the wish list instead, so you can save toward it.";
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
            b.Saving.Remove(id);
            s.World.Set("folk.wish", b);
            s.World.Save();
        }

        private static string Lower(string name) => name.Length > 1 && char.IsUpper(name[0]) && !char.IsUpper(name[1]) && name.Split(' ')[0] is "A" or "An" or "The" or "Some" ? char.ToLowerInvariant(name[0]) + name[1..] : name;

        private void Wishes()
        {
            Folk.List(_w, S.Data.Game.BunnyName + "'s wishes", () =>
            {
                var now = DateTime.Now;
                var wares = FolkShop.Wares(S, now);
                var b = Box(S);
                var rows = new List<Folk.Row> { new(PlayTime.SpendLine(S.Data) + ".") };
                foreach (var id in b.Saving.ToList())
                {
                    if (wares.FirstOrDefault(x => x.Id == id) is not FolkShop.Ware x) continue;
                    rows.Add(new Folk.Row("Saving toward " + FolkShop.HowClose(S, x) + " Enter to get it or take it off the list.", win =>
                    {
                        int pick = Folk.Choose(win, x.Name, x.Name + ". " + Folk.End(x.Why), new[] { "Get it now, " + x.Price + " points", "Take it off the wish list", "Keep saving" });
                        if (pick == 0) Folk.Say(win, Grant(x, DateTime.Now));
                        else if (pick == 1) { World.Change<WishBox>(S, "folk.wish", "taking a wish off the list", bb => bb.Saving.Remove(id)); Folk.Say(win, Folk.Pick(S, "folk.wish.notnow")); }
                    }));
                }
                foreach (var (id, when) in b.Granted.OrderByDescending(g => g.Value))
                    rows.Add(new Folk.Row("Wish granted " + when.ToString("MMMM d") + ": " + (wares.FirstOrDefault(x => x.Id == id)?.Name ?? Folk.Items("folk.wishes").FirstOrDefault(i => i.Get("id") == id)?.Get("name") ?? id) + "."));
                if (rows.Count == 1) rows.Add(new Folk.Row(S.Data.Game.BunnyName + " has not asked for anything yet. It asks at most once a week, in Visit your bunny."));
                return rows;
            }, "Enter on a wish you are saving for gets it or takes it off the list.");
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

        private IEnumerable<string> AskIdea(MainWindow w, DateTime now)
        {
            var box = S.World.GetKept<IdeaBox>("folk.idea");
            if (Calm.FunPaused) return Array.Empty<string>();
            if (box.Month != Folk.MonthKey(now)) box = new IdeaBox { Month = Folk.MonthKey(now), Idea = FromLibrary(now) };
            if (box.Shown || box.Idea == null) return Array.Empty<string>();
            box.Shown = true;
            S.World.SetKept("folk.idea", box);
            S.World.Save();
            string said = Decide(w, box.Idea);
            if (said.Length > 0) { Game.Note(S, said); S.Save(); w.Say(said, true); }
            return Array.Empty<string>();
        }

        /// <summary>Keep or toss; Escape leaves it waiting in Bunny's ideas. Returns what Bunny says back.</summary>
        private string Decide(Window owner, Idea idea)
        {
            int pick = Folk.Choose(owner, S.Data.Game.BunnyName + " had an idea", idea.Name + ". " + idea.Text, new[] { "Keep it", "Toss it" });
            if (pick < 0) return "";
            var box = S.World.GetKept<IdeaBox>("folk.idea");
            if (box.Idea?.Name == idea.Name) { box.Decided = true; S.World.SetKept("folk.idea", box); }
            if (pick == 1) { S.World.Save(); return Folk.Pick(S, "folk.ideas.toss"); }
            World.Change<List<Idea>>(S, "folk.ideas", "keeping Bunny's idea", l => { if (!l.Any(i => i.Name == idea.Name)) l.Add(idea); });
            return Folk.Pick(S, "folk.ideas.keep");
        }

        private void Ideas()
        {
            Folk.List(_w, S.Data.Game.BunnyName + "'s ideas", () =>
            {
                var rows = new List<Folk.Row>();
                var box = S.World.GetKept<IdeaBox>("folk.idea");
                if (box.Idea is Idea open && box.Shown && !box.Decided)
                    rows.Add(new Folk.Row("Waiting for your answer: " + open.Name + ". " + open.Text, win => Folk.Say(win, Decide(win, open))));
                foreach (var idea in Enumerable.Reverse(S.World.Get<List<Idea>>("folk.ideas")))
                    rows.Add(new Folk.Row("Kept " + idea.When.ToString("MMMM yyyy") + ": " + idea.Name + ". " + idea.Text, win =>
                    {
                        if (Folk.Choose(win, idea.Name, "Toss this idea?", new[] { "Keep it", "Toss it" }) != 1) return;
                        World.Change<List<Idea>>(S, "folk.ideas", "tossing Bunny's idea", l => l.RemoveAll(i => i.Name == idea.Name));
                        Folk.Say(win, Folk.Pick(S, "folk.ideas.toss"));
                    }));
                if (rows.Count == 0) rows.Add(new Folk.Row("No ideas kept yet. About once a month " + S.Data.Game.BunnyName + " thinks of something new and tells you in Visit your bunny."));
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
                if (Engine.LevelFor(Engine.Total(S.Data)) < PartnerLevel || Calm.FunPaused) return Array.Empty<string>();
                S.World.SetKept("folk.partner", new PartnerBox { Met = now, Trait = Folk.Rng.Next(Math.Max(1, Folk.Items("folk.partner").Count)) });
                S.World.Save();
                Folk.Read(w, S.Data.Game.BunnyName + " met someone", Folk.Fill(S, Folk.NthBlock("folk.partner.meet", 0)?.Body ?? ""));
                NameIt(w);
                return new[] { Folk.Fill(S, "{name} and {partner} are already planning their first outing.") };
            }
            if (!Folk.First(S, "partner.visit." + Folk.WeekKey(now))) return Array.Empty<string>();
            return new[] { Folk.Fill(S, "{partner} came by this week. ") + Outing(now) };
        }

        private void NameIt(Window owner)
        {
            var name = Folk.Ask(owner, "Name Bunny's partner", "What should " + S.Data.Game.BunnyName + "'s partner be called?", Folk.NamesOf(S).Partner is { Length: > 0 } n ? n : "Maple");
            if (name == null) return;
            World.Change<Folk.Names>(S, "folk.names", "naming Bunny's partner", x => x.Partner = name);
            Folk.Say(owner, name + " it is. " + S.Data.Game.BunnyName + " approves and has already told the whole village.");
        }

        private string Outing(DateTime now) => Folk.Nth(S, "folk.outings", (int)((now.Date - PBox.Met.Date).TotalDays / 7));

        private void Partner()
        {
            if (!Met(S))
            {
                int level = Engine.LevelFor(Engine.Total(S.Data));
                _w.Say(S.Data.Game.BunnyName + " has a feeling it will meet someone special at level " + PartnerLevel + ". You are level " + level + ".", true);
                return;
            }
            Folk.List(_w, S.Data.Game.BunnyName + " and " + Folk.Fill(S, "{partner}"), () =>
            {
                var p = PBox;
                var t = Folk.Item("folk.partner", p.Trait);
                var rows = new List<Folk.Row>
                {
                    new(Folk.Fill(S, "{partner}: " + t.Get("trait") + ". " + Folk.End(t.Get("line")) + " Likes " + t.Get("like") + ". Not a fan of " + t.Get("dislike") + ".")),
                    new("Name " + S.Data.Game.BunnyName + "'s partner. Enter to choose a name.", NameIt),
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
            var wares = FolkShop.Wares(store, now);
            Need(wares.Count >= 20 && wares.All(x => x.Price > 0 && x.Name.Length > 0), "the wish catalog");
            // In winter a winter wish can come; in summer it never does.
            var summer = new DateTime(2026, 7, 1);
            store.World.Set("folk.wish", new WishBox());
            for (int i = 0; i < 10; i++)
            {
                var pick = Choose(store, summer.AddDays(7 * i), wares);
                if (RealWorld.Season(summer.AddDays(7 * i)) != "Summer") break;
                Need(pick != null && pick.Season is "any" or "Summer" or "", "a wish out of season");
            }
            // Saying not now keeps it from being asked again for four weeks; a granted wish is never asked again.
            var first = Choose(store, now, wares)!;
            World.Change<WishBox>(store, "folk.wish", "test", b => b.NotNow[first.Id] = now);
            Need(Choose(store, now, wares)?.Id != first.Id, "not now did not count");
            // Granting spends through the ledger, and one Control Z gives the points back and the wish too.
            store.Data.Log.Add(new LogEntry { Kind = "Done", When = now, Points = 1000, TaskId = Guid.NewGuid() });
            int before = Ledger.Wallet(store);
            var ware = wares.First(x => x.Id != first.Id);
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
        }
    }
}
