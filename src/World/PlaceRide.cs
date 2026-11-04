using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    public sealed class HorseBag
    {
        public string Name { get; set; } = "";
        public string Day { get; set; } = "";
        public int Groomed { get; set; }
        public int Tacked { get; set; }
        public int Rides { get; set; }
    }

    public sealed class GymBag { public int PlatesUsed { get; set; } public int Target { get; set; } = 4; public int Best { get; set; } public int Lifts { get; set; } }
    public sealed class PotteryBag { public int StepsUsed { get; set; } public int Step { get; set; } public int Pots { get; set; } }

    /// <summary>
    /// Her own horse (12.1 item 1), the ATV trails (item 2), Bunny's gym (item 3), the pottery wheel (round 8 item 10) and the
    /// instruments she can play with the number keys (12.5). Steps are short, touch first; nothing has a timer.
    /// </summary>
    public static class PlaceRide
    {
        public const string HorseId = "place.horse", GymId = "place.gym", PotteryId = "place.pottery";
        private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

        // ---- The stable ----

        public static HorseBag Horse(Store s)
        {
            var h = s.World.Get<HorseBag>(HorseId);
            if (h.Day != Today) { h.Day = Today; h.Groomed = 0; h.Tacked = 0; }
            return h;
        }

        public static string HorseName(Store s) => Horse(s).Name.Length > 0 ? Horse(s).Name : "your horse";
        public static int TrailsOpen(Store s) => Math.Min(PlaceUi.Items("place.horse.trails").Count, 1 + PlaceStats.Of(s).WeekGoals);

        /// <summary>Does the next grooming or tacking step (one undo step). Returns what to say.</summary>
        public static string Step(Store s, bool tack)
        {
            var steps = PlaceUi.Items(tack ? "place.horse.tack" : "place.horse.groom");
            var h = Horse(s);
            int at = tack ? h.Tacked : h.Groomed;
            if (at >= steps.Count) return tack ? HorseName(s) + " is tacked up and ready to ride." : HorseName(s) + " is groomed and gleaming already today.";
            s.Remember(tack ? "tacking up" : "grooming");
            if (tack) h.Tacked++; else h.Groomed++;
            s.World.Set(HorseId, h);
            s.World.Save();
            var st = steps[at];
            string left = at + 1 < steps.Count ? " Next: " + steps[at + 1].Get("name").ToLowerInvariant() + "." : tack ? " All tacked up. Time for a ride." : " All groomed. " + HorseName(s) + " leans into you, warm and happy.";
            return st.Get("name") + ". " + PlaceUi.Feel(st) + left;
        }

        public static List<PlaceThing> Stable(Store s)
        {
            var h = Horse(s);
            var groom = PlaceUi.Items("place.horse.groom");
            var tack = PlaceUi.Items("place.horse.tack");
            var trails = PlaceUi.Items("place.horse.trails");
            string name = HorseName(s);
            var things = new List<PlaceThing>
            {
                new(h.Name.Length == 0 ? "Name your horse" : "Your horse, " + h.Name, h.Name.Length == 0 ? "a gentle horse waiting for a name" : "Enter renames", o =>
                {
                    if (PlaceUi.Ask(o, "Your horse", "Your horse's name", h.Name) is not string n) return;
                    World.Change<HorseBag>(s, HorseId, "naming your horse", b => b.Name = n);
                    PlaceUi.Play("Bunny, binky");
                    PlaceUi.Say(o, n + " it is. " + n + " blows a warm breath into your palm and nods.");
                }),
                new("Groom " + name, h.Groomed >= groom.Count ? "done for today" : "step " + (h.Groomed + 1) + " of " + groom.Count + ": " + groom[Math.Min(h.Groomed, groom.Count - 1)].Get("name").ToLowerInvariant(), o =>
                    { PlaceUi.Play("Coffee, stir"); PlaceEvents.RaiseDid(PlaceIds.Stable, "groom"); PlaceUi.Say(o, Step(s, false)); }),
                new("Tack up", h.Tacked >= tack.Count ? "ready to ride" : "step " + (h.Tacked + 1) + " of " + tack.Count + ": " + tack[Math.Min(h.Tacked, tack.Count - 1)].Get("name").ToLowerInvariant(), o =>
                    { PlaceUi.Play("Stove, click"); PlaceUi.Say(o, Step(s, true)); }),
            };
            int open = TrailsOpen(s);
            for (int i = 0; i < trails.Count; i++)
            {
                var t = trails[i];
                if (i >= open) { things.Add(new PlaceThing("More trails", PlaceUi.N(trails.Count - open, "trail") + " open with weekly goals, one each", o => PlaceUi.Say(o, "Each weekly goal you meet opens a new trail."))); break; }
                things.Add(new PlaceThing("Ride " + t.Get("name").ToLowerInvariant(), "on " + t.Get("ground") + ". " + PlaceWorld.First(t.Get("touch")), o =>
                {
                    if (Horse(s).Tacked < tack.Count) { PlaceUi.Say(o, name + " needs tacking up first: " + (tack.Count - Horse(s).Tacked) + " quick steps."); return; }
                    PlaceAudio.Hooves(t.Get("ground"));
                    World.Change<HorseBag>(s, HorseId, "a trail ride", b => b.Rides++);
                    PlaceEvents.RaiseDid(PlaceIds.Stable, "ride");
                    PlaceUi.Say(o, t.Get("name") + ". " + PlaceUi.Feel(t) + " " + PlaceUi.Line(s, "place.horse.ride"));
                }));
            }
            return things;
        }

        // ---- The ATV trails ----

        public static List<PlaceThing> Atv(Store s)
        {
            var now = DateTime.Now;
            var trails = PlaceUi.Items("place.atv.trails");
            int open = Math.Min(trails.Count, 1 + PlaceStats.Of(s).WeekGoals);
            bool weekend = now.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;
            var things = new List<PlaceThing>();
            if (!weekend) things.Add(new PlaceThing("The ATVs are parked", "rides are on the weekend", o => { PlaceUi.Say(o, "Two muddy ATVs sit under the shed roof, helmets on the seats. Bunny rides with you on the weekend."); }));
            for (int i = 0; i < open; i++)
            {
                var t = trails[i];
                things.Add(new PlaceThing((weekend ? "Ride " : "Trail: ") + t.Get("name").ToLowerInvariant(), PlaceWorld.First(t.Get("touch")), o =>
                {
                    if (!weekend) { PlaceUi.Say(o, t.Get("name") + " waits for the weekend. " + PlaceWorld.First(t.Get("looks"))); return; }
                    PlaceAudio.Rumble();
                    PlaceEvents.RaiseDid(PlaceIds.Atv, "ride");
                    PlaceUi.Say(o, t.Get("name") + ". " + PlaceUi.Feel(t) + " " + PlaceUi.Line(s, "place.atv.ride"));
                }));
            }
            if (open < trails.Count) things.Add(new PlaceThing("More trails", "a new one with each weekly goal", o => PlaceUi.Say(o, PlaceUi.N(trails.Count - open, "trail") + " still to open, one with each weekly goal.")));
            return things;
        }

        // ---- Bunny's gym ----

        public static int Plates(Store s) => Math.Max(0, PlaceStats.Of(s).Sprints - s.World.Get<GymBag>(GymId).PlatesUsed);
        public static int Weight(int plates) => 20 + plates * 10;

        public static int MostSprintsInADay(Store s) => s.Data.Log.Where(e => e.Kind == "Sprint").GroupBy(e => e.When.Date).Select(g => g.Count()).DefaultIfEmpty(0).Max();

        /// <summary>Bunny lifts the bar: a record when heavier, then the plates come off for the next build. One undo step.</summary>
        public static string Lift(Store s)
        {
            var g = s.World.Get<GymBag>(GymId);
            int plates = Plates(s), w = Weight(plates);
            s.Remember("Bunny's lift");
            bool record = w > g.Best;
            g.PlatesUsed += plates; g.Lifts++; if (record) g.Best = w;
            s.World.Set(GymId, g);
            s.World.Save();
            Arcade.Score(s, "Bunny's gym, heaviest bar in pounds", w);
            return PlaceUi.Line(s, "place.gym.lift", ("weight", w + " pounds")) + (record ? " A new personal record!" : "") + " The plates go back on the rack for the next build.";
        }

        public static IEnumerable<PlaceThing> Gym(Store s)
        {
            var g = s.World.Get<GymBag>(GymId);
            int plates = Plates(s);
            yield return new PlaceThing("The bar", PlaceUi.N(plates, "plate") + " on, " + Weight(plates) + " pounds. A plate for each sprint", o =>
                { PlaceUi.Play("Coffee, clink"); PlaceUi.Say(o, (plates > 0 ? PlaceUi.Line(s, "place.gym.plate") + " " : "") + PlaceUi.N(plates, "plate") + " on the bar. Bunny lifts at " + PlaceUi.N(g.Target, "plate") + "."); });
            if (plates >= g.Target)
                yield return new PlaceThing("Bunny's lift", "the bar is loaded. Enter to lift", o => { PlaceUi.Play("Strings, chord high"); PlaceEvents.RaiseDid(PlaceIds.Room("gym"), "lift"); PlaceUi.Say(o, Lift(s)); });
            yield return new PlaceThing("Personal records", "heaviest bar " + g.Best + " pounds, most sprints in a day " + MostSprintsInADay(s), o =>
                PlaceUi.Say(o, "Heaviest bar: " + g.Best + " pounds. Lifts: " + g.Lifts + ". Most sprints in one day: " + MostSprintsInADay(s) + "."));
            yield return new PlaceThing("Set when Bunny lifts", "at " + PlaceUi.N(g.Target, "plate"), o =>
            {
                var options = Enumerable.Range(2, 7).Select(n => PlaceUi.N(n, "plate")).ToList();
                if (PlaceUi.Pick(o, "When Bunny lifts", "Plates on the bar", options, g.Target - 2) is int i)
                {
                    World.Change<GymBag>(s, GymId, "setting Bunny's lift", b => b.Target = i + 2);
                    PlaceUi.Say(o, "Bunny lifts when the bar has " + PlaceUi.N(i + 2, "plate") + ".");
                }
            });
        }

        // ---- The pottery wheel ----

        /// <summary>Works one step of the pot (one undo step): a step for each finished sprint; glazing finishes it for the art room.</summary>
        public static string Work(Store s, string? glaze = null, string glazeTouch = "")
        {
            var steps = PlaceUi.Items("place.pottery.steps");
            var p = s.World.Get<PotteryBag>(PotteryId);
            int step = Math.Min(p.Step, steps.Count - 1);
            s.Remember("working the clay");
            p.StepsUsed++;
            string said = steps[step].Get("name") + ". " + PlaceUi.Feel(steps[step]);
            if (step == steps.Count - 1)
            {
                p.Step = 0; p.Pots++;
                string name = "Pot number " + p.Pots + ", " + (glaze ?? "plain").ToLowerInvariant() + " glaze";
                Art.Add(s, name, glazeTouch.Length > 0 ? glazeTouch : "Smooth and glassy, with a finger-sized dip at the rim.", "pot");
                said += " " + PlaceUi.Line(s, "place.pottery.done") + " " + name + " is on the shelf in the art room.";
            }
            else { p.Step++; said += " Next: " + steps[p.Step].Get("name").ToLowerInvariant() + "."; }
            s.World.Set(PotteryId, p);
            s.World.Save();
            s.Save();
            return said;
        }

        public static IEnumerable<PlaceThing> Pottery(Store s)
        {
            var steps = PlaceUi.Items("place.pottery.steps");
            var p = s.World.Get<PotteryBag>(PotteryId);
            int ready = PlaceStats.Of(s).Sprints - p.StepsUsed;
            int step = Math.Min(p.Step, steps.Count - 1);
            yield return new PlaceThing("Work the clay", ready > 0 ? steps[step].Get("name").ToLowerInvariant() + ", " + PlaceUi.N(ready, "step") + " ready" : "each finished sprint moves the pot one step", o =>
            {
                if (PlaceStats.Of(s).Sprints - s.World.Get<PotteryBag>(PotteryId).StepsUsed <= 0) { PlaceUi.Say(o, "The clay is wrapped in a damp cloth, waiting. Each finished sprint moves your pot one step: next, " + steps[step].Get("name").ToLowerInvariant() + "."); return; }
                string? glaze = null, touch = "";
                if (step == steps.Count - 1)
                {
                    var glazes = PlaceUi.Items("place.pottery.glazes");
                    if (PlaceUi.Pick(o, "Pick a glaze", "Glaze", glazes.Select(g => g.Get("name") + ". " + PlaceUi.Feel(g)).ToList()) is not int gi) return;
                    glaze = glazes[gi].Get("name"); touch = PlaceUi.Feel(glazes[gi]);
                }
                PlaceUi.Play("Coffee, stir");
                PlaceEvents.RaiseDid(PlaceIds.Room("pottery"), "craft");
                PlaceUi.Say(o, Work(s, glaze, touch));
            });
            yield return new PlaceThing("Your pots", PlaceUi.N(p.Pots, "pot") + " finished", o => PlaceUi.Show(o, "Your pots", Art.All(s).Where(a => a.Kind == "pot").Select(a => a.Name + ", made " + a.When.ToString("MMMM d") + ". " + a.Touch).ToList()));
        }

        // ---- Instruments ----

        private static readonly string[] DrumSounds = { "Bunny, thump", "Stove, click", "Coffee, clink", "Bunny, hop", "Coffee, two clinks", "Stove, sizzle", "Bunny, two hops", "Space, blip", "Coffee, stir", "Stove, light" };
        private static readonly int[] Notes = { 0, 2, 4, 5, 7, 9, 11, 12, 14, 16 };

        /// <summary>Instruments she can play: the music room's piano, xylophone and cello once it is built, and every one she bought.</summary>
        public static List<(string Name, string Instrument)> OwnedInstruments(Store s)
        {
            var list = new List<(string, string)>();
            if (PlaceWorld.HasRoom(s, "music")) list.AddRange(new[] { ("The music room's old upright piano", "Piano"), ("The music room's xylophone", "Xylophone"), ("The music room's cello", "Cello") });
            foreach (var o in Shop.OwnedItems(s).Select(i => Shop.Find(i.ItemId)).Where(i => i != null && i.Instrument.Length > 0).Distinct())
                list.Add((o!.Name, o.Instrument));
            return list;
        }

        /// <summary>The sound for a number key (0 to 9 as pressed: 1 is the lowest note, 0 the highest).</summary>
        public static string NoteName(string instrument, int key)
        {
            int i = key == 0 ? 9 : key - 1;
            return instrument == "Drums" ? DrumSounds[i] : instrument + ", note " + Notes[i];
        }

        public static void Instruments(Window owner, Store s)
        {
            var have = OwnedInstruments(s);
            if (have.Count == 0) { Speech.Say(owner, "You have no instruments yet. Build the music room in the burrow, or buy one in the market's instruments aisle.", true); return; }
            var w = Layout.Dialog(owner, "Play an instrument");
            var list = TextViewer.LinesList(have.Select(h => h.Name).ToList(), "Instruments: pick one, then number keys 1 to 9 and 0 play notes, low to high", 220);
            list.Width = 480;
            list.PreviewKeyDown += (_, e) =>
            {
                int key = e.Key is >= Key.D0 and <= Key.D9 ? e.Key - Key.D0 : e.Key is >= Key.NumPad0 and <= Key.NumPad9 ? e.Key - Key.NumPad0 : -1;
                if (key < 0 || Keyboard.Modifiers != ModifierKeys.None) { if (e.Key == Key.Escape) { e.Handled = true; w.Close(); } return; }
                e.Handled = true;
                int i = Math.Max(0, list.SelectedIndex);
                if (!Sounds.Enabled) { Speech.Say(w, "Sounds are off. Turn them on in Options, Sounds, to play.", true); return; }
                PlaceAudio.Instrument(NoteName(have[i].Instrument, key));
            };
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new Label { Content = "_Instruments: number keys play notes", Target = list, Padding = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(list);
            panel.Children.Add(new TextBlock { Text = "Arrow to an instrument, then press 1 to 9 and 0 to play, low to high. Escape closes.", TextWrapping = TextWrapping.Wrap, MaxWidth = 480 });
            panel.Children.Add(Layout.Button("Close", isCancel: true));
            w.Content = panel;
            w.Loaded += (_, _) => TextViewer.Select(list, 0);
            PlaceEvents.RaiseDid(PlaceIds.Room("music"), "music");
            w.ShowDialog();
        }

        public static void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("rides: " + what); }
            // Grooming goes step by step and undoes.
            Need(Step(s, false).StartsWith(PlaceUi.Items("place.horse.groom")[0].Get("name")) && Horse(s).Groomed == 1, "the first grooming step");
            s.Undo();
            Need(Horse(s).Groomed == 0, "undo of a grooming step");
            for (int i = 0; i < 10; i++) Step(s, true);
            Need(Horse(s).Tacked == PlaceUi.Items("place.horse.tack").Count, "tacking up stops at the last step");
            Need(TrailsOpen(s) >= 1 && Stable(s).Count >= 4 && Atv(s).Count >= 1, "trails");
            // The gym: plates from sprints, a lift at the target, records kept.
            int sprints = PlaceStats.Of(s).Sprints;
            for (int i = 0; i < 4; i++) s.Data.Log.Add(new LogEntry { Kind = "Sprint", When = DateTime.Now, Minutes = 15 });
            PlaceStats.Forget();
            Need(Plates(s) >= 4, "sprints did not add plates");
            Lift(s);
            Need(Plates(s) == 0 && s.World.Get<GymBag>(GymId).Best >= Weight(4), "the lift");
            s.Undo();
            Need(Plates(s) >= 4, "undo of the lift");
            // The pottery wheel: five steps make a pot for the art room.
            int art = Art.All(s).Count;
            for (int i = 0; i < PlaceUi.Items("place.pottery.steps").Count; i++) Work(s, "Test", "Glassy.");
            Need(Art.All(s).Count == art + 1 && Art.All(s).Last().Kind == "pot", "a finished pot did not reach the art room");
            // Instruments: number keys map to real sounds.
            foreach (var inst in new[] { "Piano", "Cello", "Flute", "Organ", "Xylophone", "Singing bells", "Pizzicato strings", "Strings", "Soft piano" })
                Need(Sounds.WavOf(NoteName(inst, 1)) != null && Sounds.WavOf(NoteName(inst, 0)) != null, "no note for " + inst);
            Need(DrumSounds.All(d => Sounds.All.Contains(d)), "a drum sound is missing");
            foreach (var i in Shop.Catalog.Where(i => i.Instrument.Length > 0)) Need(i.Instrument == "Drums" || Sounds.InstrumentNames.Contains(i.Instrument), "an instrument with no sound: " + i.Instrument);
        }
    }
}
