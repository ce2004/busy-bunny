using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>The ids of every place in Bunny's world (Stage 7). Other features use these to add things to a place.</summary>
    public static class PlaceIds
    {
        public const string Burrow = "burrow", Garden = "place.garden", Village = "place.village", Pond = "place.pond", Cove = "place.cove",
            Woods = "place.woods", Hills = "place.hills", Map = "place.map";
        public const string Market = "place.market", Bakery = "place.bakery", TeaHouse = "place.teahouse", Farmers = "place.farmers",
            Lemonade = "place.lemonade", Workshop = "place.workshop", ClockTower = "place.clocktower", Station = "place.station",
            Museum = "place.museum", NightOwl = "place.nightowl", Cocoa = "place.cocoa", Well = "place.well", Stable = "place.stable";
        public const string Fireflies = "place.fireflies", Meadow = "place.meadow", Mushrooms = "place.mushrooms", Creek = "place.creek",
            Cave = "place.cave", Waterfall = "place.waterfall", SecretGarden = "place.secretgarden", Hammock = "place.hammock",
            Stretch = "place.stretch", HotSpring = "place.hotspring", Bridges = "place.bridges", Herbs = "place.herbs",
            Orchard = "place.orchard", Hive = "place.hive";
        public const string Stars = "place.stars", Sled = "place.sled", Treehouse = "place.treehouse", Atv = "place.atv", Balloon = "place.balloon";
        public const string Lighthouse = "place.lighthouse", Tunnels = "place.tunnels", HotTub = "place.hottub";

        /// <summary>A burrow room's place id.</summary>
        public static string Room(string roomId) => "place.room." + roomId;

        /// <summary>The rooms from the first burrow (PlayTime), still built and owned as before.</summary>
        public static readonly string[] OldRooms = { "tunnel", "nook", "pantry", "tea", "fireplace", "bath", "garden", "music", "observatory", "library", "throne" };
        /// <summary>The rooms added in Stage 7, bought in the burrow.</summary>
        public static readonly string[] NewRooms = { "bedroom", "guest", "treasure", "art", "pool", "aquarium", "greenhouse", "gameroom", "trophy", "kitchen", "pottery", "gym" };
        public static IEnumerable<string> Rooms => OldRooms.Concat(NewRooms);
    }

    /// <summary>What she has done, worked out from the points log, so an undone task takes its progress back by itself.</summary>
    public sealed class PlaceStats
    {
        public int Tasks, Sprints, Level, WeekGoals, Days, Streak, Bosses, RestDays, Evening, GoalDays, Subtasks, Kindness;

        private static (int Count, DateTime Last, int Tasks, int Days, int RestCount, int Subtasks, int WeekGoals, int GoalDays)? _key;
        private static PlaceStats? _last;

        /// <summary>
        /// Finished tasks from the log, one row per task: a task set to Done not submitted and later to Done logs two Done rows,
        /// and still counts once. Rows without a task id count one each.
        /// </summary>
        public static List<LogEntry> DoneTasks(Data d) =>
            d.Log.Where(e => e.Kind == "Done").GroupBy(e => e.TaskId ?? Guid.NewGuid()).Select(g => g.First()).ToList();

        public static PlaceStats Of(Store s)
        {
            var d = s.Data;
            // Rough and vacation days, ticked subtasks and goals change without a log entry, so they are part of the key too.
            var key = (d.Log.Count, d.Log.Count > 0 ? d.Log[^1].When : DateTime.MinValue, d.Tasks.Count, d.Days.Count,
                d.Days.Values.Count(i => i.Mode is DayMode.RoughDay or DayMode.Vacation), d.Tasks.Sum(t => t.Subtasks.Count(x => x.Done)), d.Game.WeekGoalsMet, d.Game.GoalDays.Count);
            if (_key == key && _last != null && ReferenceEquals(_lastData, d)) return _last;
            var done = DoneTasks(d);
            var st = new PlaceStats
            {
                Tasks = done.Count,
                Sprints = d.Log.Count(e => e.Kind == "Sprint"),
                Level = Engine.LevelFor(Engine.Total(d)),
                WeekGoals = d.Game.WeekGoalsMet,
                Days = done.Select(e => e.When.Date).Distinct().Count(),
                Streak = Math.Max(d.Game.LongestStreak, Engine.Streaks(d, DateTime.Today).Longest),
                Bosses = d.Log.Count(e => e.Kind == "Boss"),
                RestDays = key.Item5,
                Evening = done.Count(e => e.When.Hour >= 18),
                GoalDays = d.Game.GoalDays.Count,
                Subtasks = key.Item6,
                Kindness = d.Log.Count(e => e.Kind == "DidAThing" || e.Kind == Ledger.FunKind && e.Feature.Contains("kind", StringComparison.OrdinalIgnoreCase)),
            };
            _key = key; _last = st; _lastData = d;
            return st;
        }
        private static Data? _lastData;
        public static void Forget() { _key = null; _last = null; }
    }

    /// <summary>What opens a place: a plain goal and how far she is. Goals are suggestions tuned so a usual week opens something every few days.</summary>
    public sealed record PlaceGoal(Func<PlaceStats, bool> Met, Func<PlaceStats, string> Words)
    {
        public static PlaceGoal Count(Func<PlaceStats, int> have, int need, string one, string many) =>
            new(s => have(s) >= need, s => (need == 1 ? one : many.Replace("{n}", need.ToString())) + " (" + Math.Min(have(s), need) + " so far)");
        public static PlaceGoal Tasks(int n) => Count(s => s.Tasks, n, "finish a task", "finish {n} tasks");
        public static PlaceGoal Sprints(int n) => Count(s => s.Sprints, n, "finish a sprint", "finish {n} sprints");
        public static PlaceGoal Level(int n) => new(s => s.Level >= n, s => "reach level " + n + " (you are level " + s.Level + ")");
        public static PlaceGoal WeekGoals(int n) => Count(s => s.WeekGoals, n, "meet a weekly goal", "meet {n} weekly goals");
        public static PlaceGoal Days(int n) => Count(s => s.Days, n, "finish tasks on 1 day", "finish tasks on {n} different days");
        public static PlaceGoal Streak(int n) => Count(s => s.Streak, n, "a 1 day streak", "a {n} day streak");
        public static PlaceGoal Bosses(int n) => Count(s => s.Bosses, n, "beat a boss", "beat {n} bosses");
        public PlaceGoal Or(PlaceGoal other) => new(s => Met(s) || other.Met(s), s => Words(s) + ", or " + other.Words(s));
    }

    /// <summary>One place of Stage 7: where it sits (Parent is the area it is inside, null for the places list), what opens it, what is inside.</summary>
    public sealed class PlaceDef
    {
        public string Id = "", Name = "", Short = "";
        public string? Parent;
        public PlaceGoal? Goal;
        /// <summary>For a burrow room: built when bought, not by a goal.</summary>
        public string? RoomId;
        /// <summary>Things inside, after Feel and listen. The window is the world window.</summary>
        public Func<Store, List<PlaceThing>>? Things;
        /// <summary>Activity word for PlaceEvents.Did when she does the place's main thing.</summary>
        public string Activity = "visit";
        /// <summary>The toolbox switch this place belongs to (the market, the map room, the garden): off closes the place.</summary>
        public string? ToolId;
    }

    /// <summary>Other features add their lines to a place here (neighbors at the tea house, the duck family at the pond).</summary>
    public static class PlaceHooks
    {
        private static readonly Dictionary<string, List<Func<Store, List<PlaceThing>>>> _extra = new();
        public static void AddThings(string placeId, Func<Store, List<PlaceThing>> things)
        {
            if (!_extra.TryGetValue(placeId, out var l)) _extra[placeId] = l = new();
            l.Add(things);
        }
        internal static IEnumerable<PlaceThing> For(string placeId, Store s)
        {
            if (!_extra.TryGetValue(placeId, out var l)) yield break;
            foreach (var f in l)
            {
                List<PlaceThing> got;
                try { got = f(s); } catch (Exception e) { ErrorLog.Write("Place hook for " + placeId + " failed: " + e.Message); continue; }
                foreach (var t in got) yield return t;
            }
        }
    }

    /// <summary>Raised after she does something in a place, for Bunny's traits. Not raised in the self-test.</summary>
    public static class PlaceEvents
    {
        public static event Action<string, string>? Did;
        public static event Action<string>? TreatGiven;
        internal static bool Testing;
        internal static void RaiseDid(string placeId, string activity)
        {
            if (Testing || Did == null) return;
            foreach (Action<string, string> h in Did.GetInvocationList())
                try { h(placeId, activity); } catch (Exception e) { ErrorLog.Write("PlaceEvents.Did handler failed: " + e.Message); }
        }
        internal static void RaiseTreat(string treat)
        {
            if (Testing || TreatGiven == null) return;
            foreach (Action<string> h in TreatGiven.GetInvocationList())
                try { h(treat); } catch (Exception e) { ErrorLog.Write("PlaceEvents.TreatGiven handler failed: " + e.Message); }
        }
    }

    /// <summary>Finds of every kind, for the treasure chest: sonar dives, the pond, the tunnels, train souvenirs, other features' treasure hunts.</summary>
    public sealed class Find
    {
        public string Name { get; set; } = "";
        public string Touch { get; set; } = "";
        public string Where { get; set; } = "";
        public DateTime When { get; set; }
        /// <summary>The task that brought it, so un-finishing that task takes it back.</summary>
        public Guid? TaskId { get; set; }
    }

    public static class Treasure
    {
        public const string Id = "place.treasure";
        /// <summary>Adds a find. Call inside an undo step (after store.Remember, or in a TaskDone handler). Saves the world.</summary>
        public static void Add(Store store, string name, string touch, string where, Guid? taskId = null)
        {
            var l = store.World.Get<List<Find>>(Id);
            l.Add(new Find { Name = name, Touch = touch, Where = where, When = DateTime.Now, TaskId = taskId });
            store.World.Set(Id, l);
            store.World.Save();
        }
        public static List<Find> All(Store store) => store.World.Get<List<Find>>(Id).OrderByDescending(f => f.When).ToList();
        public static void TakeBack(Store store, Guid taskId)
        {
            var l = store.World.Get<List<Find>>(Id);
            if (l.RemoveAll(f => f.TaskId == taskId) == 0) return;
            store.World.Set(Id, l);
            store.World.Save();
        }
    }

    /// <summary>A piece she made, for the art room: pots, finished puzzles, her named stars, crafted things from other features.</summary>
    public sealed class ArtPiece
    {
        public string Name { get; set; } = "";
        public string Touch { get; set; } = "";
        public string Kind { get; set; } = "";
        public DateTime When { get; set; }
        public bool Shown { get; set; } = true;
    }

    public static class Art
    {
        public const string Id = "place.art";
        /// <summary>Adds a piece, on display. Call inside an undo step. Saves the world.</summary>
        public static void Add(Store store, string name, string touch, string kind)
        {
            var l = store.World.Get<List<ArtPiece>>(Id);
            l.Add(new ArtPiece { Name = name, Touch = touch, Kind = kind, When = DateTime.Now });
            store.World.Set(Id, l);
            store.World.Save();
        }
        public static List<ArtPiece> All(Store store) => store.World.Get<List<ArtPiece>>(Id);
    }

    /// <summary>Best scores for the game room. Bookkeeping: never undone.</summary>
    public static class Arcade
    {
        public const string Id = "place.arcade";
        public static void Score(Store store, string game, int score)
        {
            var best = store.World.GetKept<Dictionary<string, int>>(Id);
            if (best.TryGetValue(game, out int had) && had >= score) return;
            best[game] = score;
            store.World.SetKept(Id, best);
            store.World.Save();
        }
        public static Dictionary<string, int> Best(Store store) => store.World.GetKept<Dictionary<string, int>>(Id);
    }

    /// <summary>Small shared helpers for places: speaking, sounds, content entries and the little pick and text windows.</summary>
    public static class PlaceUi
    {
        public static void Say(Window owner, string text) => Speech.Say(owner, text, true);

        /// <summary>A sound she asked for (she used a thing): plays when sounds are on.</summary>
        public static void Play(string? sound)
        {
            if (string.IsNullOrEmpty(sound) || !Sounds.Enabled) return;
            if (!Sounds.All.Contains(sound)) { ErrorLog.Write("Places: no sound named " + sound); return; }
            Sounds.PlayNamed(sound);
        }

        private static readonly Dictionary<string, List<Dictionary<string, string>>> _items = new();
        /// <summary>Structured content entries, cached.</summary>
        public static List<Dictionary<string, string>> Items(string topic) =>
            _items.TryGetValue(topic, out var have) ? have : _items[topic] = Content.Items(topic);

        public static string Get(this Dictionary<string, string> d, string key, string fallback = "") => d.TryGetValue(key, out var v) ? v : fallback;

        /// <summary>Touch, then sound, then looks, as one line.</summary>
        public static string Feel(Dictionary<string, string>? d) =>
            d == null ? "" : string.Join(" ", new[] { d.Get("touch"), d.Get("sound"), d.Get("looks") }.Where(x => x.Length > 0));

        /// <summary>A line from a topic with placeholders filled.</summary>
        public static string Line(Store s, string topic, params (string Key, string Value)[] fill)
        {
            string line = Content.Pick(s, topic);
            foreach (var (k, v) in fill) line = line.Replace("{" + k + "}", v);
            return line;
        }

        /// <summary>A pick menu: a list, Enter picks, Escape cancels. Returns the index or null. Focus goes back to the owner.</summary>
        public static int? Pick(Window owner, string title, string label, IReadOnlyList<string> options, int select = 0)
        {
            if (options.Count == 0) return null;
            var w = Layout.Dialog(owner, title);
            var list = TextViewer.LinesList(options, label, Math.Min(360, 40 + options.Count * 30));
            list.Width = 560;
            int? chosen = null;
            list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) { e.Handled = true; chosen = list.SelectedIndex; w.Close(); }
                else if (e.Key == Key.Escape) { e.Handled = true; w.Close(); }
            };
            list.MouseDoubleClick += (_, _) => { chosen = list.SelectedIndex; w.Close(); };
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new Label { Content = label, Target = list, Padding = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(list);
            var cancel = Layout.Button("Cancel", isCancel: true);
            panel.Children.Add(cancel);
            w.Content = panel;
            w.Loaded += (_, _) => TextViewer.Select(list, select);
            w.ShowDialog();
            return chosen is int i && i >= 0 && i < options.Count ? i : null;
        }

        /// <summary>A one-line text box. Enter or OK gives the text; Escape gives null.</summary>
        public static string? Ask(Window owner, string title, string label, string start = "")
        {
            var w = Layout.Dialog(owner, title);
            var box = new TextBox { Text = start };
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Layout.Row(label, box, 420));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ok = Layout.Button("OK", isDefault: true);
            string? got = null;
            ok.Click += (_, _) => { got = box.Text.Trim(); w.Close(); };
            row.Children.Add(ok);
            row.Children.Add(Layout.Button("Cancel", isCancel: true));
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            w.ShowDialog();
            return string.IsNullOrWhiteSpace(got) ? null : got;
        }

        /// <summary>Lines to read with the arrows.</summary>
        public static void Show(Window owner, string title, IReadOnlyList<string> lines) =>
            TextViewer.Show(owner, title, lines.Count == 0 ? new List<string> { "Nothing here yet." } : lines);

        /// <summary>A friendly count: 1 carrot, 3 carrots.</summary>
        public static string N(int n, string one, string? many = null) => n + " " + (n == 1 ? one : many ?? one + "s");
    }
}
