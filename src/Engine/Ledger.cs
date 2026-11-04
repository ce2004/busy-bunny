using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>A points multiplier (power hour, golden task...): a factor until a time, with the reason said when it starts.</summary>
    public sealed class Multiplier
    {
        public string Id { get; set; } = "";
        public double Factor { get; set; } = 1;
        public DateTime Until { get; set; }
        public string Reason { get; set; } = "";
    }

    /// <summary>How Ledger.Pay(w, ...) tells her about the points.</summary>
    public enum PayVoice
    {
        /// <summary>Part of the line said after the task she just finished (the reason is a fun line of priority Game.Other).</summary>
        AfterTask,
        /// <summary>She just used something that pays (a tool): said now.</summary>
        Asked,
        /// <summary>Nobody asked (a ticker job, a daily bonus): kept in What happened today, said only when the calm rules allow.</summary>
        Unprompted,
        /// <summary>Kept in What happened today, never said.</summary>
        Silent,
    }

    /// <summary>
    /// The points ledger (sweep 8.2 B), built on Data.Log so every old point keeps counting. Pay adds an entry of
    /// kind Fun that counts as Karma, marked with the feature that paid it and, when a task earned it, the task's id:
    /// un-finishing the task takes it back (Engine.Unfinish, the one undo mechanism). Spend takes from points to
    /// spend (PlayTime.Wallet), never from Karma, so spending never lowers her level. Multipliers multiply together
    /// and are capped at 4 times in all.
    /// </summary>
    public static class Ledger
    {
        public const string FunKind = "Fun", SpendKind = "Spend";
        public const double MaxBoost = 4;
        private const string MultipliersId = "ledger.multipliers";

        /// <summary>
        /// Pays points: logs them and runs every check (level, goals, records, badges). Does not save or speak; use
        /// Game.Queue(w, outcome) to have its lines join the one said after a task. remember makes it its own Control Z step
        /// (for points not tied to a status change she just made). Null for nothing paid.
        /// </summary>
        public static Outcome? Pay(Store store, int amount, string reason, string feature, Guid? taskId = null, bool remember = false)
        {
            if (amount <= 0) return null;
            if (remember) store.Remember(reason);
            var t = taskId is Guid id ? store.Data.Tasks.FirstOrDefault(x => x.Id == id) : null;
            var e = new LogEntry { Kind = FunKind, Text = reason, Points = amount, TaskId = taskId, Feature = feature, Tag = t?.Tag ?? "", List = t?.List ?? "" };
            return Engine.Record(store, DateTime.Now, e, null, false);
        }

        /// <summary>
        /// Pays and saves, then tells her as voice says. AfterTask joins the one line said after a finish: the points, then the
        /// most important fun line, where the reason counts as Game.Other (it outranks Chatter lines). Use Pay(store, ...) plus
        /// Game.Queue(w, outcome) when your own Chatter line should be the one heard instead.
        /// </summary>
        public static void Pay(MainWindow w, int amount, string reason, string feature, Guid? taskId = null, bool remember = false, PayVoice voice = PayVoice.AfterTask)
        {
            if (Pay(w.Store, amount, reason, feature, taskId, remember) is not Outcome o) return;
            w.Store.Save();
            string line = o.Gained + (o.Gained == 1 ? " point for " : " points for ") + reason + ". " + PlayTime.SpendLine(w.Store.Data) + ".";
            switch (voice)
            {
                case PayVoice.AfterTask:
                    o.Fun.Insert(0, (Game.Other, reason + "."));
                    Game.Queue(w, o);
                    break;
                case PayVoice.Asked:
                    foreach (var said in o.Said) Game.Note(w.Store, said);
                    w.Say(line + (o.Fun.Count > 0 ? " " + o.Fun.OrderBy(f => f.Priority).First().Text : ""));
                    break;
                case PayVoice.Unprompted:
                    foreach (var said in o.Said) Game.Note(w.Store, said);
                    Calm.SayUnprompted(w, line, noted: true);
                    break;
                default:
                    foreach (var said in o.Said) Game.Note(w.Store, said);
                    break;
            }
        }

        /// <summary>Points she has to spend right now.</summary>
        public static int Wallet(Store store) => PlayTime.Wallet(store.Data);

        /// <summary>
        /// Spends points to spend (the market, the wishing well, a bet). Refuses, changing nothing, when she has too
        /// few. Remembered first so Control Z gives them back. Adds a Spend entry to the log (not Karma) and raises
        /// Play.Spent. Saves. True when it was spent.
        /// </summary>
        public static bool Spend(Store store, int amount, string reason, string feature)
        {
            if (amount <= 0) return true;
            if (Wallet(store) < amount) return false;
            store.Remember(reason);
            store.Data.Play.Spent += amount;
            store.Data.Log.Add(new LogEntry { When = DateTime.Now, Kind = SpendKind, Text = reason, Points = -amount, Feature = feature });
            store.Save();
            return true;
        }

        // ---- Multipliers ----

        /// <summary>
        /// Starts (or replaces, by id) a multiplier until a time. Kept in the world's bookkeeping: it lasts across a restart and
        /// Control Z never takes it away (it is time-boxed, and a promise). End one early with RemoveMultiplier.
        /// </summary>
        public static void AddMultiplier(Store store, string id, double factor, DateTime until, string reason)
        {
            var all = store.World.GetKept<List<Multiplier>>(MultipliersId);
            all.RemoveAll(m => m.Id == id || m.Until <= DateTime.Now);
            all.Add(new Multiplier { Id = id, Factor = Math.Max(0.1, factor), Until = until, Reason = reason });
            store.World.SetKept(MultipliersId, all);
            store.World.Save();
        }

        /// <summary>The same, for the main window's store.</summary>
        public static void AddMultiplier(string id, double factor, DateTime until, string reason)
        {
            if (Engines.Store is Store s) AddMultiplier(s, id, factor, until, reason);
        }

        public static void RemoveMultiplier(Store store, string id)
        {
            var all = store.World.GetKept<List<Multiplier>>(MultipliersId);
            if (all.RemoveAll(m => m.Id == id) == 0) return;
            store.World.SetKept(MultipliersId, all);
            store.World.Save();
        }

        /// <summary>The multipliers running at this time.</summary>
        public static List<Multiplier> Active(Store store, DateTime now) =>
            store.World.GetKept<List<Multiplier>>(MultipliersId).Where(m => m.Until > now).ToList();

        /// <summary>Every running multiplier multiplied together, at most 4.</summary>
        public static double Factor(Store store, DateTime now) =>
            Math.Min(MaxBoost, Active(store, now).Aggregate(1.0, (f, m) => f * m.Factor));

        /// <summary>Base points with the running multipliers applied (capped at 4 times). task is for features that boost only some tasks; the ledger boosts all.</summary>
        public static int Boost(Store store, int basePoints, TaskItem? task = null) =>
            (int)Math.Round(basePoints * Factor(store, DateTime.Now));

        public static void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("ledger: " + what); }
            var until = DateTime.Now.AddHours(1);
            AddMultiplier(store, "test.power", 2, until, "test");
            AddMultiplier(store, "test.dice", 3, until, "test");
            Need(Boost(store, 10) == 40, "two multipliers of 2 and 3 were not capped at 4 times: " + Boost(store, 10));
            RemoveMultiplier(store, "test.dice");
            Need(Boost(store, 10) == 20, "removing a multiplier");
            // Control Z of an earlier step never takes a running multiplier away.
            store.Remember("a step before the multiplier");
            AddMultiplier(store, "test.dice", 1.5, until, "test");
            store.Undo();
            Need(Boost(store, 10) == 30, "undo took a running multiplier away");
            RemoveMultiplier(store, "test.dice");
            AddMultiplier(store, "test.old", 5, DateTime.Now.AddMinutes(-1), "ended");
            Need(Boost(store, 10) == 20, "an ended multiplier still counted");
            RemoveMultiplier(store, "test.power");

            // Paying for a task, then un-finishing it, takes the payment back; Karma counts Fun.
            var t = new TaskItem { Name = "ledger test", List = store.Data.Lists[0].Name, Status = Status.Done, DoneAt = DateTime.Now };
            store.Data.Tasks.Add(t);
            int before = Engine.Total(store.Data);
            Pay(store, 7, "Golden task", "test.golden", t.Id);
            Need(Engine.Total(store.Data) == before + 7, "Fun points do not count as Karma");
            Need(store.Data.Log.Any(e => e.Feature == "test.golden" && e.TaskId == t.Id), "the entry lost its feature or task");
            t.Status = Status.Today;
            Engine.Unfinish(store, t, DateTime.Now);
            Need(Engine.Total(store.Data) == before && !store.Data.Log.Any(e => e.TaskId == t.Id && e.Kind == FunKind), "undoing the task left its ledger entries");

            // Spending refuses when short and never lowers Karma; Control Z gives it back.
            Pay(store, 50, "test pay", "test", remember: true);
            int karma = Engine.Total(store.Data), wallet = Wallet(store);
            Need(!Spend(store, wallet + 1, "too much", "test") && Wallet(store) == wallet, "spending more than she has");
            Need(Spend(store, 20, "test spend", "test") && Wallet(store) == wallet - 20 && Engine.Total(store.Data) == karma, "spending lowered Karma");
            Need(store.Data.Log.Any(e => e.Kind == SpendKind && e.Points == -20 && e.Feature == "test"), "spending left no log entry");
            store.Undo();
            Need(Wallet(store) == wallet, "undo did not give the points back");
            store.Data.Tasks.Remove(t);
        }
    }
}
