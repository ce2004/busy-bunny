using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    /// <summary>
    /// Every Stage 7 place (sweep 8.6, 12.1, 12.3 to 12.5, 13.1, 13.3, round 8 item 10): where it is, what opens it and what is
    /// inside. Top places show in Bunny's world; the rest are inside an area (the bakery is in the village) and in the map room,
    /// which lists every place, open or not, with what opens it.
    /// </summary>
    public static class PlaceWorld
    {
        private static readonly List<PlaceDef> _defs = new();
        public static IReadOnlyList<PlaceDef> Defs => _defs;
        /// <summary>A place inside an area shows in the world window only while she is walking into it.</summary>
        private static string? _opening;

        public static PlaceDef? Def(string id) => _defs.FirstOrDefault(d => d.Id == id);
        public static string NameOf(string id) => Def(id)?.Name ?? World.Places.Find(id)?.Name ?? id;

        public static bool HasRoom(Store s, string roomId) =>
            PlayTime.Rooms.Any(r => r.Id == roomId && (r.Cost == 0 || s.Data.Play.Rooms.Contains(r.Id))) || Shop.Bag(s).Rooms.Contains(roomId);

        public static bool IsOpen(Store s, string id)
        {
            var d = Def(id);
            if (d == null)
            {
                if (_adopted.TryGetValue(id, out var a)) return IsOpen(s, a.Parent) && Safe(() => a.Original.Unlocked(s), false);
                return World.Places.Find(id) is Place p && p.Unlocked(s);
            }
            if (d.ToolId != null && !Tools.IsOn(d.ToolId)) return false;
            if (d.RoomId != null) return HasRoom(s, d.RoomId);
            if (d.Parent != null && !IsOpen(s, d.Parent)) return false;
            return d.Goal == null || d.Goal.Met(PlaceStats.Of(s));
        }

        public static string UnlockWords(Store s, string id)
        {
            var d = Def(id);
            if (d == null) return "";
            if (d.ToolId != null && !Tools.IsOn(d.ToolId)) return "turn on " + (Tools.Find(d.ToolId)?.Name ?? d.Name) + " in Settings, Fun and tools";
            if (d.RoomId != null) return PlaceRooms.BuildWords(s, d.RoomId);
            if (d.Parent != null && !IsOpen(s, d.Parent)) return "first open " + NameOf(d.Parent).Replace("The ", "the ") + ": " + UnlockWords(s, d.Parent);
            return d.Goal == null ? "always open" : d.Goal.Words(PlaceStats.Of(s));
        }

        /// <summary>From a thing inside a place: walks into another place (Escape comes back).</summary>
        public static void Open(Window owner, string id)
        {
            if (Engines.Store is Store s && !IsOpen(s, id)) { _opening = null; PlaceUi.Say(owner, ClosedWords(s, id)); return; }
            _opening = id;
            WorldWindow.GoTo(owner, id);
        }

        /// <summary>Opens Bunny's world straight into a place, even one inside an area (the bakery, a room, the round window).</summary>
        public static void Show(MainWindow w, string id)
        {
            _opening = null;
            if (!IsOpen(w.Store, id)) { w.Say(ClosedWords(w.Store, id), true); return; }
            if (Inside(id)) _opening = id;
            try { WorldWindow.Show(w, id); }
            finally { _opening = null; }
        }

        private static string ClosedWords(Store s, string id)
        {
            string how = UnlockWords(s, id);
            return how.Length > 0 ? NameOf(id) + " is not open yet. It opens when you " + how + "." : NameOf(id) + " is closed right now.";
        }

        /// <summary>A place inside an area: it shows in the world window only while she walks into it, never in the top list.</summary>
        public static bool Inside(string id) => _adopted.ContainsKey(id) || Def(id) is PlaceDef d && (d.Parent != null || d.RoomId != null);

        // ---- Places from other features that live inside one of mine ----

        private sealed record Adoption(string Parent, Place Original, Place Wrapper);
        private static readonly Dictionary<string, Adoption> _adopted = new();
        private static readonly Dictionary<string, string> _pending = new();

        /// <summary>
        /// Moves another feature's place inside one of mine (the round window into the burrow, the village green into the
        /// village): it leaves the top list, and the parent lists it with a line that walks in. Its own Unlocked still decides
        /// whether it is there (a switched-off tool hides it). Call any time in Attach; calling again is harmless.
        /// Tools that open it must use PlaceWorld.Show(mainWindow, placeId), not WorldWindow.Show.
        /// </summary>
        public static void Adopt(string placeId, string parentId)
        {
            var current = World.Places.Find(placeId);
            if (current == null) { _pending[placeId] = parentId; return; }
            if (_adopted.TryGetValue(placeId, out var had) && ReferenceEquals(had.Wrapper, current))
            {
                if (had.Parent == parentId) return;
                current = had.Original;
            }
            var original = current;
            var wrapper = original with
            {
                Unlocked = st => !WorldWindow.Listing && _opening == placeId && IsOpen(st, placeId),
                Things = st => { if (_opening == placeId) _opening = null; return original.Things(st); },
            };
            _adopted[placeId] = new Adoption(parentId, original, wrapper);
            _pending.Remove(placeId);
            World.Places.Register(wrapper);
        }

        /// <summary>Applies adoptions again once every feature has attached (for a place registered late, or registered again).</summary>
        internal static void AdoptAgain()
        {
            foreach (var (id, parent) in _pending.ToList()) Adopt(id, parent);
            foreach (var (id, a) in _adopted.ToList())
                if (World.Places.Find(id) is Place now && !ReferenceEquals(now, a.Wrapper)) { _adopted.Remove(id); Adopt(id, a.Parent); }
        }

        /// <summary>The places adopted into a parent (for the self-test and the map).</summary>
        public static IEnumerable<string> AdoptedInto(string parentId) => _adopted.Where(x => x.Value.Parent == parentId).Select(x => x.Key);

        private static T Safe<T>(Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception e) { ErrorLog.Write("Places: " + e.GetType().Name + ": " + e.Message); return fallback; }
        }

        private static Dictionary<string, string> Content(string id) =>
            PlaceUi.Items("place.places").FirstOrDefault(d => d.Get("id") == id) ?? new Dictionary<string, string>();

        /// <summary>Touch first, then sound, then looks.</summary>
        public static string FeelOf(string id) => PlaceUi.Feel(Content(id));
        public static string PlaysOf(string id) => Content(id).Get("plays");

        public static void Add(PlaceDef d)
        {
            _defs.RemoveAll(x => x.Id == d.Id);
            _defs.Add(d);
            World.Places.Register(new Place(d.Id, d.Name, s => Describe(s, d),
                s => d.Parent == null && d.RoomId == null ? IsOpen(s, d.Id) : !WorldWindow.Listing && _opening == d.Id && IsOpen(s, d.Id),
                s => ThingsOf(s, d)));
        }

        private static string Describe(Store s, PlaceDef d) => d.Short;

        private static List<PlaceThing> ThingsOf(Store s, PlaceDef d)
        {
            if (_opening == d.Id) _opening = null;
            var things = new List<PlaceThing>();
            if (d.RoomId != null) things.AddRange(PlaceRooms.RoomThings(s, d));
            else
            {
                if (d.Things != null) things.AddRange(d.Things(s));
                // Feel and listen comes first, except in the burrow, where points to spend are read first (sweep 11.1).
                string feel = FeelOf(d.Id);
                if (feel.Length > 0) things.Insert(d.Id == PlaceIds.Burrow ? Math.Min(1, things.Count) : 0, new PlaceThing("Feel and listen", First(feel), o => { PlaceUi.Play(PlaysOf(d.Id)); PlaceUi.Say(o, feel); }));
            }
            things.AddRange(PlaceHooks.For(d.Id, s));
            // Other features' places that live here (the round window in the burrow, the village green in the village).
            foreach (var (id, a) in _adopted.Where(x => x.Value.Parent == d.Id).ToList())
            {
                if (!Safe(() => a.Original.Unlocked(s), false)) continue;
                string pid = id;
                var orig = a.Original;
                things.Add(new PlaceThing(orig.Name, Safe(() => orig.Describe(s), ""), o => Open(o, pid)));
            }
            // An area lists the places inside it: open ones go in, closed ones say what opens them.
            foreach (var child in _defs.Where(c => c.Parent == d.Id && c.RoomId == null))
            {
                var c = child;
                if (IsOpen(s, c.Id))
                {
                    bool fresh = !Visited(s).Contains(c.Id);
                    things.Add(new PlaceThing(c.Name, (fresh ? "new, " : "") + c.Short, o => { MarkVisited(s, c.Id); Open(o, c.Id); }));
                }
                else things.Add(new PlaceThing(c.Name + ", still closed", "opens when you " + UnlockWords(s, c.Id), o => PlaceUi.Say(o, c.Name + " opens when you " + UnlockWords(s, c.Id) + ".")));
            }
            return things;
        }

        /// <summary>The first sentence, for a list line.</summary>
        public static string First(string text)
        {
            int dot = text.IndexOf(". ", StringComparison.Ordinal);
            return dot > 0 ? text[..(dot + 1)] : text;
        }

        private const string VisitedKey = "place.visited", KnownKey = "place.known", SeenKey = "world.seen";
        private static List<string> Visited(Store s) => s.World.GetKept<List<string>>(VisitedKey);
        private static void MarkVisited(Store s, string id)
        {
            var v = Visited(s);
            if (v.Contains(id)) return;
            v.Add(id);
            s.World.SetKept(VisitedKey, v);
            s.World.Save();
        }

        /// <summary>Places that opened since she last heard about them (bookkeeping, never undone). The first call only learns what is open.</summary>
        public static List<PlaceDef> NewlyOpen(Store s)
        {
            var known = s.World.GetKept<List<string>>(KnownKey);
            bool first = known.Count == 0;
            var open = _defs.Where(d => d.RoomId == null && IsOpen(s, d.Id)).ToList();
            var fresh = open.Where(d => !known.Contains(d.Id)).ToList();
            if (fresh.Count == 0) return fresh;
            known.AddRange(fresh.Select(d => d.Id));
            s.World.SetKept(KnownKey, known);
            // The world window announces new top places too (world.seen), so they share one memory: a top place said here is
            // not said again there, and one the window already said is not said here. Before her first visit to the window
            // it learns everything quietly, so nothing is added then.
            var seen = s.World.GetKept<List<string>>(SeenKey);
            var say = fresh;
            if (seen.Count > 0)
            {
                say = fresh.Where(d => !(d.Parent == null && d.RoomId == null && seen.Contains(d.Id))).ToList();
                foreach (var d in fresh.Where(d => d.Parent == null && d.RoomId == null && !seen.Contains(d.Id))) seen.Add(d.Id);
                s.World.SetKept(SeenKey, seen);
            }
            s.World.Save();
            return first ? new List<PlaceDef>() : say;
        }

        /// <summary>The closed place nearest to opening, for "what opens next".</summary>
        public static List<string> Next(Store s) => _defs.Where(d => d.RoomId == null && d.Goal != null && !IsOpen(s, d.Id) && (d.Parent == null || IsOpen(s, d.Parent)))
            .Select(d => d.Name + ": " + UnlockWords(s, d.Id) + ".").ToList();

        /// <summary>The map room: every place, open or not.</summary>
        public static List<PlaceThing> MapThings(Store s)
        {
            var things = new List<PlaceThing>();
            int open = _defs.Count(d => IsOpen(s, d.Id));
            things.Add(new PlaceThing(PlaceUi.N(open, "place") + " open of " + _defs.Count, "every place in Bunny's world, open or not, with what opens it", o => PlaceUi.Say(o, PlaceUi.N(open, "place") + " are open, of " + _defs.Count + ". Arrow down to hear each one.")));
            foreach (var d in _defs.Where(d => d.Id != PlaceIds.Map))
            {
                var def = d;
                string where = def.Parent != null ? "in " + NameOf(def.Parent).Replace("The ", "the ") : def.RoomId != null ? "a burrow room" : "";
                if (IsOpen(s, def.Id))
                    things.Add(new PlaceThing(def.Name, "open" + (where.Length > 0 ? ", " + where : "") + ". Enter goes there", o => Open(o, def.Id)));
                else
                    things.Add(new PlaceThing(def.Name, "closed" + (where.Length > 0 ? ", " + where : "") + ". Opens when you " + UnlockWords(s, def.Id), o => PlaceUi.Say(o, def.Name + " opens when you " + UnlockWords(s, def.Id) + ".")));
            }
            return things;
        }
    }

    /// <summary>
    /// Stage 7, the places half: registers every place, the market and wish list, the map room, sonar diving, the instruments,
    /// the treasure chest and the carrot garden. Named to attach after PlayTime, so the richer burrow replaces its first one
    /// (every room she built stays built and owned).
    /// </summary>
    public sealed class WorldPlaces : IFeature
    {
        public const string NewPlacesTool = "place.newplaces", GardenTool = "place.garden", ChimesTool = "place.chimes";
        private MainWindow _w = null!;

        public void Attach(MainWindow w)
        {
            _w = w;
            PlaceDefs.RegisterAll();

            Tools.Register(new BunnyTool("shop.market", "The market", Drawers.World, "Aisles of furniture, decor, instruments and more to buy with points to spend.", () => ShopWindow.Market(_w, _w)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool("shop.wishes", "Your wish list", Drawers.World, "The things you are saving for and how close you are.", () => ShopWindow.Wishes(_w, _w)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool("place.map", "The map room", Drawers.World, "Every place in Bunny's world, open or not, and what opens it.", () => PlaceWorld.Show(_w, PlaceIds.Map)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(GardenTool, "The carrot garden", Drawers.World, "Each finished task waters a plot. Harvest carrots for the bakery.", () => PlaceWorld.Show(_w, PlaceIds.Garden)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool("place.sonar", "Sonar diving", Drawers.Games, "Swim with the arrows; the ping rises and quickens near treasure. No timer.", () =>
            {
                if (!PlaceWorld.IsOpen(_w.Store, PlaceIds.Cove) || !PlaceDefs.SonarOpen(_w.Store)) { _w.Say("Sonar diving opens when you " + PlaceDefs.SonarWords(_w.Store) + ".", true); return; }
                var back = _w.KeepFocus(); Sonar.Show(_w, _w.Store); back();
            }) { Since = "0.14.0" });
            Tools.Register(new BunnyTool("place.music", "Play an instrument", Drawers.World, "Play the instruments you bought with the number keys.", () => { var back = _w.KeepFocus(); PlaceRide.Instruments(_w, _w.Store); back(); }) { Since = "0.14.0" });
            Tools.Register(new BunnyTool("place.chest", "The treasure chest", Drawers.Wins, "Every treasure you have found, newest first.", () => { var back = _w.KeepFocus(); PlaceRooms.OpenChest(_w, _w.Store); back(); }) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(NewPlacesTool, "What opens next", Drawers.World, "The places closest to opening. Also says when a new place opens after a task.", () =>
            {
                var next = PlaceWorld.Next(_w.Store);
                _w.Say(next.Count == 0 ? "Every place in Bunny's world is open." : "Next to open: " + string.Join(" ", next.Take(3)), true);
            }) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(ChimesTool, "Clock tower chimes", Drawers.World, "The village clock tower chimes when a power hour starts.", () => { PlaceUi.Play("Singing bells, chord high"); _w.Say("The clock tower chimes when a power hour starts. Turn it off in Settings, Fun and tools.", true); }) { Since = "0.14.0" });
            BunnyTab.Register("The market", "aisles of things to buy for the burrow", () => ShopWindow.Market(_w, _w), "shop.market");
            BunnyTab.Register("The map room", "every place, open or not", () => PlaceWorld.Show(_w, PlaceIds.Map), "place.map");

            GameEvents.TaskDone += t =>
            {
                var s = _w.Store;
                if (Tools.IsOn(GardenTool) && PlaceGarden.Water(s, t, DateTime.Now) is string ready && !Calm.FunPaused)
                    Calm.AfterTaskDone(_w, Game.Chatter, ready);
                if (Tools.IsOn(NewPlacesTool) && !Calm.FunPaused)
                {
                    var fresh = PlaceWorld.NewlyOpen(s);
                    if (fresh.Count > 0) Calm.AfterTaskDone(_w, Game.Other, "New in Bunny's world: " + string.Join(", ", fresh.Select(f => f.Name.Replace("The ", "the "))) + ".");
                }
            };
            // Always, even with a tool off: what a task gave goes back with it.
            GameEvents.TaskUndone += t => { PlaceGarden.Unwater(_w.Store, t); Treasure.TakeBack(_w.Store, t.Id); };
            GameEvents.ToolUsed += id => PlaceRooms.CountGame(_w.Store, id);
            w.Started += () => { PlaceWorld.AdoptAgain(); PlaceWorld.NewlyOpen(_w.Store); PlaceDefs.Chime(DateTime.Now); };
            // Keeps power hour's start sound the chime while the clock tower chimes are on, and its usual sound otherwise.
            Ticker.Every("place.chimes", _ => true, PlaceDefs.Chime);
        }

        public void SelfTest(Store store)
        {
            PlaceEvents.Testing = true;
            try
            {
                Shop.SelfTest(store);
                Sonar.SelfTest();
                PlaceDefs.SelfTest(store);
            }
            finally { PlaceEvents.Testing = false; PlaceStats.Forget(); }
        }
    }
}
