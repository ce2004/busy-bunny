using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    /// <summary>The carrot garden's bag: six plots, carrots harvested, and the bakery's treats.</summary>
    public sealed class GardenBag
    {
        public List<Plot> Plots { get; set; } = new();
        public int Carrots { get; set; }
        public int Harvested { get; set; }
        public Dictionary<string, int> Treats { get; set; } = new();
        public string LemonadeDay { get; set; } = "";
        /// <summary>Tasks whose watering was harvested: finishing one of them again never waters a second time.</summary>
        public HashSet<Guid> HarvestedTasks { get; set; } = new();
    }

    public sealed class Plot
    {
        /// <summary>Each watering: the task that did it and the day.</summary>
        public List<Watering> Waters { get; set; } = new();
    }

    public sealed class Watering { public Guid TaskId { get; set; } public DateTime Day { get; set; } }

    /// <summary>
    /// The carrot garden (sweep 8.6): each finished task waters a plot; a plot grows one stage for each day it was watered
    /// and is ready after three; a plot not watered for a while droops and says so gently, and never dies. Carrots go to
    /// the bakery (treats to give to neighbors), the kitchen and the farmers market (points).
    /// </summary>
    public static class PlaceGarden
    {
        public const string Id = "place.garden";
        public const int Plots = 6, Ready = 3, PerPlot = 3, CarrotPoints = 2;
        private static readonly string[] Stages = { "freshly planted seeds", "tiny green sprouts", "leafy tops as tall as your paw", "ready to pull" };

        public static GardenBag Bag(Store s)
        {
            var b = s.World.Get<GardenBag>(Id);
            while (b.Plots.Count < Plots) b.Plots.Add(new Plot());
            return b;
        }
        private static void Put(Store s, GardenBag b) { s.World.Set(Id, b); s.World.Save(); }

        public static int Stage(Plot p) => Math.Min(Ready, p.Waters.Select(w => w.Day.Date).Distinct().Count());
        public static bool Droopy(Plot p, DateTime now) => Stage(p) < Ready && p.Waters.Count > 0 && p.Waters.Max(w => w.Day).Date < now.Date.AddDays(-1);

        /// <summary>Waters one plot for a finished task (once per task). Returns a line when a plot just became ready, else null.</summary>
        public static string? Water(Store s, TaskItem t, DateTime now)
        {
            var b = Bag(s);
            if (b.HarvestedTasks.Contains(t.Id) || b.Plots.Any(p => p.Waters.Any(w => w.TaskId == t.Id))) return null;
            var growing = b.Plots.Where(p => Stage(p) < Ready).ToList();
            if (growing.Count == 0) return null;
            var plot = growing.FirstOrDefault(p => !p.Waters.Any(w => w.Day.Date == now.Date)) ?? growing[0];
            int before = Stage(plot);
            plot.Waters.Add(new Watering { TaskId = t.Id, Day = now });
            Put(s, b);
            return before < Ready && Stage(plot) == Ready ? "A carrot plot is ready to harvest in the garden." : null;
        }

        public static void Unwater(Store s, TaskItem t)
        {
            var b = Bag(s);
            int n = b.Plots.Sum(p => p.Waters.RemoveAll(w => w.TaskId == t.Id));
            if (n > 0) Put(s, b);
        }

        /// <summary>Pulls every ready plot: carrots in the basket, the plots planted again. One undo step. Returns the count.</summary>
        public static int Harvest(Store s)
        {
            var b = Bag(s);
            int ready = b.Plots.Count(p => Stage(p) >= Ready);
            if (ready == 0) return 0;
            s.Remember("harvesting carrots");
            foreach (var p in b.Plots.Where(p => Stage(p) >= Ready))
            {
                foreach (var w in p.Waters) b.HarvestedTasks.Add(w.TaskId);
                p.Waters.Clear();
            }
            b.Carrots += ready * PerPlot;
            b.Harvested += ready * PerPlot;
            Put(s, b);
            s.Save();
            return ready * PerPlot;
        }

        public static List<PlaceThing> Things(Store s)
        {
            var b = Bag(s);
            var now = DateTime.Now;
            var things = new List<PlaceThing>();
            int ready = b.Plots.Count(p => Stage(p) >= Ready);
            things.Add(new PlaceThing(PlaceUi.N(b.Carrots, "carrot") + " in your basket", PlaceUi.N(b.Harvested, "carrot") + " grown all together", o => PlaceUi.Say(o, PlaceUi.N(b.Carrots, "carrot") + " in your basket. Take them to the bakery, the kitchen or the farmers market.")));
            if (ready > 0)
                things.Add(new PlaceThing("Harvest", PlaceUi.N(ready, "plot") + " ready to pull", o =>
                {
                    int got = Harvest(s);
                    PlaceUi.Play("Bunny, nibble");
                    PlaceEvents.RaiseDid(PlaceIds.Garden, "garden");
                    PlaceUi.Say(o, PlaceUi.Line(s, "place.garden.harvest", ("count", got.ToString())) + " " + PlaceUi.N(Bag(s).Carrots, "carrot") + " in your basket now.");
                }));
            for (int i = 0; i < b.Plots.Count; i++)
            {
                var p = b.Plots[i];
                int stage = Stage(p);
                bool droop = Droopy(p, now);
                bool today = p.Waters.Any(w => w.Day.Date == now.Date);
                string words = Stages[stage] + (droop ? ", a little droopy" : "") + (today ? ", watered today" : "");
                things.Add(new PlaceThing("Plot " + (i + 1), words, o =>
                {
                    string line = droop ? PlaceUi.Line(s, "place.garden.droop") : today ? PlaceUi.Line(s, "place.garden.water") : "";
                    PlaceUi.Say(o, "Plot " + (i + 1) + ": " + words + ". " + (stage >= Ready ? "Harvest it whenever you like." : "Each finished task waters a plot; " + PlaceUi.N(Ready - stage, "more day") + " of watering until it is ready.") + (line.Length > 0 ? " " + line : ""));
                }));
            }
            return things;
        }

        // ---- The bakery ----

        public static List<Dictionary<string, string>> Recipes => PlaceUi.Items("place.bakery.recipes");
        private static readonly string[] Neighbors = { "the hedgehog", "the owl", "the squirrel", "the rooster", "the pony", "Gerald the snail" };

        /// <summary>Bakes a recipe with carrots from the basket. One undo step. Null when there are too few carrots.</summary>
        public static string? Bake(Store s, Dictionary<string, string> recipe)
        {
            int need = int.TryParse(recipe.Get("carrots"), out int n) ? Math.Max(1, n) : 2;
            var b = Bag(s);
            if (b.Carrots < need) return null;
            s.Remember("baking " + recipe.Get("name"));
            b.Carrots -= need;
            b.Treats[recipe.Get("name")] = (b.Treats.TryGetValue(recipe.Get("name"), out int had) ? had : 0) + 1;
            Put(s, b);
            s.Save();
            return PlaceUi.Line(s, "place.bakery.bake", ("treat", recipe.Get("name").ToLowerInvariant())) + " " + PlaceUi.Feel(recipe);
        }

        public static List<PlaceThing> Bakery(Store s)
        {
            var b = Bag(s);
            var things = new List<PlaceThing>
            {
                new(PlaceUi.N(b.Carrots, "carrot") + " to bake with", b.Treats.Values.Sum() == 0 ? "no treats yet" : PlaceUi.N(b.Treats.Values.Sum(), "treat") + " on the cooling rack", o =>
                    PlaceUi.Say(o, PlaceUi.N(b.Carrots, "carrot") + ". " + (b.Treats.Count == 0 ? "No treats yet." : "Treats: " + string.Join(", ", b.Treats.Where(t => t.Value > 0).Select(t => t.Value + " " + t.Key.ToLowerInvariant())) + "."))),
            };
            foreach (var r in Recipes)
            {
                var recipe = r;
                int need = int.TryParse(r.Get("carrots"), out int n) ? n : 2;
                things.Add(new PlaceThing("Bake " + r.Get("name").ToLowerInvariant(), PlaceUi.N(need, "carrot") + ". " + PlaceWorld.First(r.Get("touch")), o =>
                {
                    string? said = Bake(s, recipe);
                    if (said == null) { PlaceUi.Say(o, recipe.Get("name") + " takes " + PlaceUi.N(need, "carrot") + ". You have " + Bag(s).Carrots + ". Finished tasks water the garden, and ripe plots give 3 each."); return; }
                    PlaceUi.Play(recipe.Get("plays", "Stove, light"));
                    PlaceEvents.RaiseDid(PlaceIds.Bakery, "bake");
                    PlaceUi.Say(o, said);
                }));
            }
            if (b.Treats.Values.Sum() > 0)
                things.Add(new PlaceThing("Give a treat to a neighbor", "they always say thank you", o =>
                {
                    var have = Bag(s).Treats.Where(t => t.Value > 0).Select(t => t.Key).ToList();
                    if (PlaceUi.Pick(o, "Give a treat", "Which treat", have) is not int ti) return;
                    if (PlaceUi.Pick(o, "Give a treat", "To whom", Neighbors) is not int ni) return;
                    s.Remember("giving a treat");
                    if (!TakeTreat(s, have[ti])) { PlaceUi.Say(o, "That treat is all gone. Bake another at the bakery."); return; }
                    s.Save();
                    PlaceEvents.RaiseTreat(have[ti]);
                    PlaceUi.Say(o, "You gave " + have[ti].ToLowerInvariant() + " to " + Neighbors[ni] + ". " + PlaceUi.Line(s, "place.treat.thanks", ("treat", have[ti].ToLowerInvariant())));
                }));
            return things;
        }

        /// <summary>Bakery treats by name and count.</summary>
        public static Dictionary<string, int> Treats(Store s) => Bag(s).Treats.Where(t => t.Value > 0).ToDictionary(t => t.Key, t => t.Value);

        /// <summary>Uses one treat. Call inside an undo step.</summary>
        public static bool TakeTreat(Store s, string name)
        {
            var b = Bag(s);
            if (!b.Treats.TryGetValue(name, out int n) || n <= 0) return false;
            b.Treats[name] = n - 1;
            Put(s, b);
            return true;
        }

        // ---- The farmers market ----

        /// <summary>Sells carrots for points (2 each), through the ledger, as one undo step. Returns the points.</summary>
        public static int Sell(Store s, int count) => Sell(s, count, out _);

        /// <summary>Sells carrots; more is what the payment brought (a level up, a badge), to say after the sale, or empty.</summary>
        public static int Sell(Store s, int count, out string more)
        {
            more = "";
            var b = Bag(s);
            count = Math.Min(count, b.Carrots);
            if (count <= 0) return 0;
            if (Ledger.Pay(s, count * CarrotPoints, "selling " + PlaceUi.N(count, "carrot") + " at the farmers market", PlaceIds.Farmers, remember: true) is Outcome o)
            {
                foreach (var said in o.Said) Game.Note(s, said);
                more = PlaceOutdoors.Said(o);
            }
            b = Bag(s);
            b.Carrots -= count;
            Put(s, b);
            s.Save();
            return count * CarrotPoints;
        }

        public static List<PlaceThing> Farmers(Store s)
        {
            var b = Bag(s);
            var things = new List<PlaceThing>();
            if (b.Carrots == 0) { things.Add(new PlaceThing("The stall is empty", "harvest carrots in the garden to sell them here", o => PlaceUi.Say(o, "No carrots to sell yet. Ripe plots in the garden give 3 carrots each."))); return things; }
            foreach (int n in new[] { 1, 5, b.Carrots }.Where(n => n <= b.Carrots).Distinct())
            {
                int count = n;
                things.Add(new PlaceThing(count == b.Carrots && count > 1 ? "Sell all " + PlaceUi.N(count, "carrot") : "Sell " + PlaceUi.N(count, "carrot"), (count * CarrotPoints) + " points", o =>
                {
                    int got = Sell(s, count, out var more);
                    PlaceUi.Play("Coffee, two clinks");
                    PlaceEvents.RaiseDid(PlaceIds.Farmers, "sell");
                    PlaceUi.Say(o, PlaceUi.Line(s, "place.farmers.sale", ("count", count.ToString())) + " " + got + " points. " + PlaceUi.N(Bag(s).Carrots, "carrot") + " left. " + PlayTime.SpendLine(s.Data) + "." + more);
                }));
            }
            return things;
        }

        public static void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("garden: " + what); }
            var day = new DateTime(2026, 11, 2, 10, 0, 0);
            var tasks = Enumerable.Range(0, 3).Select(i => new TaskItem { Name = "garden " + i, List = s.Data.Lists[0].Name }).ToList();
            // Done, back, done again waters once.
            Water(s, tasks[0], day); Unwater(s, tasks[0]); Water(s, tasks[0], day); Water(s, tasks[0], day);
            Need(Bag(s).Plots.Sum(p => p.Waters.Count) == 1, "one task watered more than once");
            // Three days of watering make a plot ready; a missed day droops it but never kills it.
            Need(Stage(Bag(s).Plots[0]) == 1 && !Droopy(Bag(s).Plots[0], day.AddDays(1)) && Droopy(Bag(s).Plots[0], day.AddDays(3)), "growing and drooping");
            Water(s, tasks[1], day.AddDays(4));
            string? ready = Water(s, tasks[2], day.AddDays(5));
            var b = Bag(s);
            Need(b.Plots.Any(p => Stage(p) == Ready) && ready != null, "a plot watered on three days is not ready");
            int carrots = b.Carrots;
            Need(Harvest(s) == PerPlot && Bag(s).Carrots == carrots + PerPlot, "harvest");
            s.Undo();
            Need(Bag(s).Carrots == carrots, "undo did not put the harvest back");
            Harvest(s);
            // After a harvest, un-finishing and finishing a task again never waters a second time.
            Unwater(s, tasks[1]);
            Need(Water(s, tasks[1], day.AddDays(6)) == null && !Bag(s).Plots.Any(p => p.Waters.Any(w => w.TaskId == tasks[1].Id)), "a harvested task watered again");
            // Baking spends carrots; selling pays through the ledger, and undo puts both back.
            var recipe = Recipes.OrderBy(r => int.Parse(r.Get("carrots"))).First();
            b = Bag(s); b.Carrots = 20; s.World.Set(Id, b);
            Need(Bake(s, recipe) != null && Bag(s).Carrots == 20 - int.Parse(recipe.Get("carrots")) && Treats(s).Values.Sum() == 1, "baking");
            int wallet = Ledger.Wallet(s), have = Bag(s).Carrots;
            Need(Sell(s, 5) == 10 && Ledger.Wallet(s) == wallet + 10 && Bag(s).Carrots == have - 5, "selling carrots");
            Need(s.Data.Log.Any(e => e.Feature == PlaceIds.Farmers), "the sale is not in the ledger");
            s.Undo();
            Need(Ledger.Wallet(s) == wallet && Bag(s).Carrots == have, "undo did not take the sale back");
            Need(Things(s).Count >= Plots + 1 && Bakery(s).Count >= Recipes.Count + 1 && Farmers(s).Count >= 1, "garden places list their things");
            foreach (var t in tasks) Unwater(s, t);
        }
    }

    /// <summary>The bakery's treats, for neighbors and stories (WORLD-API).</summary>
    public static class Bakery
    {
        public static Dictionary<string, int> Treats(Store s) => PlaceGarden.Treats(s);
        public static bool TakeTreat(Store s, string name) => PlaceGarden.TakeTreat(s, name);
    }
}
