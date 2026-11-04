using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// Bunny's world (sweep 8.2 F): one window with a standard list of places, name first, then a few words. Enter goes
    /// into a place, where a second list holds the things to do or look at there; Enter does one. Escape goes back a
    /// level, and from the places closes the window; focus then returns where she was. Places come from
    /// World.Places.Register. A place she has not seen before is announced once when the window opens.
    /// </summary>
    public static class WorldWindow
    {
        private const string SeenKey = "world.seen";
        private static readonly Dictionary<Window, string> _goTo = new();

        /// <summary>For a thing inside a place: goes into another place (like the parking lot room inside the burrow). Escape there comes back.</summary>
        public static void GoTo(Window worldWindow, string placeId) => _goTo[worldWindow] = placeId;

        /// <summary>Opens the world; startPlace opens straight into that place (Escape then goes to the places).</summary>
        public static void Show(MainWindow owner, string? startPlace = null)
        {
            var store = owner.Store;
            var back = owner.KeepFocus();
            var w = Layout.Dialog(owner, "Bunny's world");
            var list = new ListBox { Width = 560, Height = 320 };
            var label = new Label { Target = list, Padding = new Thickness(0, 0, 0, 2) };
            var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 560, Margin = new Thickness(0, 6, 0, 0) };
            Place? inside = null;
            var cameFrom = new Stack<(Place Place, int At)>();
            List<Place> places = new();
            List<PlaceThing> things = new();
            int placeAt = 0;

            void ShowPlaces(int select)
            {
                inside = null;
                places = World.Places.All.Where(p => Safe(() => p.Unlocked(store), false)).ToList();
                list.Items.Clear();
                foreach (var p in places) list.Items.Add(new ListBoxItem { Content = p.Name + ": " + Safe(() => p.Describe(store), "") });
                if (places.Count == 0) list.Items.Add(new ListBoxItem { Content = "Nowhere to go yet." });
                label.Content = "_Places";
                AutomationProperties.SetName(list, "Places, " + places.Count);
                hint.Text = "Enter goes into a place. Escape closes Bunny's world.";
                w.Title = "Bunny's world";
                TextViewer.Select(list, Math.Clamp(select, 0, Math.Max(0, list.Items.Count - 1)));
            }

            void Enter(Place p, int select = 0)
            {
                inside = p;
                things = Safe(() => p.Things(store), new List<PlaceThing>());
                list.Items.Clear();
                foreach (var t in things) list.Items.Add(new ListBoxItem { Content = t.Name + (t.Description.Length > 0 ? ": " + t.Description : "") });
                if (things.Count == 0) list.Items.Add(new ListBoxItem { Content = "Nothing here right now." });
                label.Content = "_Things here";
                AutomationProperties.SetName(list, p.Name + ", " + things.Count + (things.Count == 1 ? " thing" : " things"));
                hint.Text = "Enter does the one you are on. Escape goes back to the places.";
                w.Title = p.Name;
                TextViewer.Select(list, Math.Clamp(select, 0, Math.Max(0, list.Items.Count - 1)));
            }

            void Activate()
            {
                int i = list.SelectedIndex;
                if (inside == null)
                {
                    if (i < 0 || i >= places.Count) return;
                    placeAt = i;
                    Enter(places[i]);
                    return;
                }
                if (i < 0 || i >= things.Count) return;
                var thing = things[i];
                var place = inside;
                try { thing.Run(w); }
                catch (Exception e) { ErrorLog.Write("World: " + place.Id + ", " + thing.Name + " failed: " + e); Speech.Say(w, "That did not work just now.", true); }
                if (!w.IsVisible) return;
                if (_goTo.Remove(w, out var id) && World.Places.Find(id) is Place next && Safe(() => next.Unlocked(store), false))
                {
                    cameFrom.Push((place, i));
                    Enter(next);
                    return;
                }
                // What is inside may have changed (a thought made into a task, a room built): show it fresh, near where she was.
                Enter(place, i);
            }

            list.PreviewKeyDown += (_, e) =>
            {
                // Control Z works here as in the main window, and the place shows its things again.
                if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    e.Handled = true;
                    int at = Math.Max(0, list.SelectedIndex);
                    owner.UndoLastChange();
                    if (inside != null) Enter(inside, at); else ShowPlaces(at);
                    return;
                }
                if (Keyboard.Modifiers != ModifierKeys.None) return;
                if (e.Key == Key.Enter) { e.Handled = true; Activate(); }
                else if (e.Key == Key.Escape)
                {
                    e.Handled = true;
                    if (inside != null && cameFrom.Count > 0) { var (p, at) = cameFrom.Pop(); Enter(p, at); }
                    else if (inside != null) ShowPlaces(placeAt);
                    else w.Close();
                }
            };
            list.MouseDoubleClick += (_, _) => Activate();

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(label);
            panel.Children.Add(list);
            panel.Children.Add(hint);
            var close = Layout.Button("Close");
            close.Click += (_, _) => w.Close();
            panel.Children.Add(close);
            w.Content = panel;
            w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape && !list.IsKeyboardFocusWithin) { e.Handled = true; w.Close(); } };

            w.Loaded += (_, _) =>
            {
                var start = startPlace == null ? null : World.Places.Find(startPlace);
                ShowPlaces(0);
                if (start != null && places.Contains(start)) { placeAt = places.IndexOf(start); Enter(start); }
                // New places, said once each, then they wait quietly in the list.
                var seen = store.World.GetKept<List<string>>(SeenKey);
                var fresh = places.Where(p => !seen.Contains(p.Id)).ToList();
                if (fresh.Count > 0)
                {
                    bool firstVisit = seen.Count == 0;
                    seen.AddRange(fresh.Select(p => p.Id));
                    store.World.SetKept(SeenKey, seen);
                    store.World.Save();
                    // After focus has landed and been read, so the line is not cut off.
                    if (!firstVisit) w.Dispatcher.BeginInvoke(() => Speech.Say(w, "New in Bunny's world: " + string.Join(", ", fresh.Select(p => p.Name)) + "."), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                }
            };
            w.ShowDialog();
            _goTo.Remove(w);
            back();
        }

        private static T Safe<T>(Func<T> f, T fallback)
        {
            try { return f(); }
            catch (Exception e) { ErrorLog.Write("World: a place failed: " + e.GetType().Name + ": " + e.Message); return fallback; }
        }

        public static void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("world: " + what); }
            // Registered places all describe themselves and list their things without throwing.
            Need(World.Places.Find("burrow") != null && World.Places.Find("parking") != null, "the burrow and the parking lot are places");
            foreach (var p in World.Places.All)
            {
                p.Unlocked(store);
                Need(p.Name.Length > 0 && p.Describe(store) is string, p.Id + " has no name");
                foreach (var t in p.Things(store)) Need(t.Name.Length > 0 && !t.Name.Contains((char)35) && !t.Name.Contains((char)42), p.Id + " has a thing with no name");
            }

            // world.json: saved, a damaged file loads the newest good backup and says so, the tasks are never touched.
            string dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "busybunny-world-" + Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(dir);
            try
            {
                string file = System.IO.Path.Combine(dir, "world.json"), backups = System.IO.Path.Combine(dir, "Backups");
                var a = WorldState.Load(file, backups);
                a.Set("test.garden", new List<string> { "carrot" });
                Need(a.Save() && System.IO.Directory.GetFiles(backups).Count(f => System.IO.Path.GetFileName(f).StartsWith("world ")) == 1, "the daily world backup");
                System.IO.File.WriteAllText(file, "{ \"Bags\": { \"test.garden\": [ \"car");
                System.IO.File.Delete(file + ".bak");
                System.IO.File.Delete(file + ".new");
                var b = WorldState.Load(file, backups);
                Need(b.Get<List<string>>("test.garden").Contains("carrot"), "a damaged world.json did not load the backup");
                Need(b.LoadNote != null && b.LoadNote.Contains("damaged"), "a damaged world.json was not announced");
                Need(System.IO.Directory.GetFiles(dir).Count(f => System.IO.Path.GetFileName(f).StartsWith("world.json.damaged-")) == 1, "the damaged world.json was not kept");
                Need(b.Get<List<int>>("no.such.feature").Count == 0, "a missing bag");

                // Locked at start is not damage: nothing is rescued over it and nothing is saved over it.
                Need(b.Save(), "saving after the rescue");
                string before = System.IO.File.ReadAllText(file);
                using (new System.IO.FileStream(file, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
                {
                    var c = WorldState.Load(file, backups, waitTries: 2);
                    Need(c.Locked && c.LoadNote != null && !c.Save(), "a locked world.json was treated as damaged or saved over");
                }
                Need(System.IO.File.ReadAllText(file) == before, "a locked world.json changed");
            }
            finally { try { System.IO.Directory.Delete(dir, true); } catch { } }

            // One Control Z undoes the tasks and the world together; bookkeeping stays.
            store.World.Set("test.marbles", 1);
            store.World.SetKept("test.kept", 1);
            World.Change<List<int>>(store, "test.jar", "adding a marble", l => l.Add(5));
            store.World.SetKept("test.kept", 2);
            Need(World.Get<List<int>>(store, "test.jar").Count == 1, "World.Change");
            store.Undo();
            Need(World.Get<List<int>>(store, "test.jar").Count == 0, "undo did not put the world back");
            Need(World.Get<int>(store, "test.marbles") == 1, "undo lost a bag that was there before the step");
            Need(store.World.GetKept<int>("test.kept") == 2, "undo put back bookkeeping it should keep");
        }
    }
}
