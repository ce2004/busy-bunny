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

        /// <summary>
        /// Base points with the running multipliers applied (capped at 4 times). With a task, the part the multipliers add on
        /// top is kept inside the task's room for extras (see PayExtra); basePoints itself is always paid.
        /// </summary>
        public static int Boost(Store store, int basePoints, TaskItem? task = null)
        {
            int boosted = (int)Math.Round(basePoints * Factor(store, DateTime.Now));
            if (task == null || boosted <= basePoints) return boosted;
            return basePoints + Math.Min(boosted - basePoints, Room(store, task, BaseOf(store, task), DateTime.Now));
        }

        // ---- Extras for a task: one ceiling across every feature ----

        private const string ExtraFeaturesId = "ledger.extrafeatures";
        private static readonly HashSet<string> ExtraIds = new();

        /// <summary>
        /// Marks a feature id as a multiplier-style extra, so its payments use up a task's room. PayExtra does this by itself;
        /// call it in Attach too if you pay that feature some other way. Kept in the world's bookkeeping, so it lasts.
        /// </summary>
        public static void CountAsExtra(Store store, string feature)
        {
            ExtraIds.Add(feature);
            var kept = store.World.GetKept<HashSet<string>>(ExtraFeaturesId);
            if (!kept.Add(feature)) return;
            store.World.SetKept(ExtraFeaturesId, kept);
            store.World.Save();
        }

        /// <summary>Whether payments of this feature count as multiplier-style extras.</summary>
        public static bool IsExtra(Store store, string feature) =>
            ExtraIds.Contains(feature) || store.World.GetKept<HashSet<string>>(ExtraFeaturesId).Contains(feature);

        /// <summary>The task's own Done points as logged (Done not submitted is already halved there), or what it would earn now.</summary>
        public static int BaseOf(Store store, TaskItem task)
        {
            int logged = store.Data.Log.Where(e => e.Kind == "Done" && e.TaskId == task.Id).Sum(e => e.Points);
            return logged > 0 ? logged : Engine.PointsFor(task, store.ListOf(task), half: task.Status == Status.DoneNotSubmitted);
        }

        /// <summary>The multiplier-style extras already paid today for this task, by every feature. Flat prizes (bingo, quests, the marble) do not count.</summary>
        public static int ExtrasToday(Store store, Guid taskId, DateTime now)
        {
            var ids = new HashSet<string>(ExtraIds);
            ids.UnionWith(store.World.GetKept<HashSet<string>>(ExtraFeaturesId));
            return store.Data.Log.Where(e => e.Kind == FunKind && e.TaskId == taskId && e.When.Date == now.Date && ids.Contains(e.Feature)).Sum(e => e.Points);
        }

        /// <summary>How many extra points this task may still get today: base times (MaxBoost minus 1), minus the extras paid already.</summary>
        public static int Room(Store store, TaskItem task, int basePoints, DateTime now) =>
            Math.Max(0, (int)Math.Round(Math.Max(0, basePoints) * (MaxBoost - 1)) - ExtrasToday(store, task.Id, now));

        /// <summary>
        /// Pays a multiplier-style extra for a finished task (running multipliers, the dice roll, a golden task, a combo, doubles,
        /// dread or well-ahead), clamped so base plus every such extra for this task today, by all features together, never
        /// passes 4 times base. Carries the task's id, so un-finishing takes it back. Does not save or speak. Null for nothing paid.
        /// </summary>
        public static Outcome? PayExtra(Store store, TaskItem task, int basePoints, int extra, string reason, string feature)
        {
            CountAsExtra(store, feature);
            int pay = Math.Min(extra, Room(store, task, basePoints, DateTime.Now));
            return pay > 0 ? Pay(store, pay, reason, feature, task.Id) : null;
        }

        /// <summary>The same, then saves and tells her as voice says (see Pay(w, ...)). Returns what was paid, 0 for nothing.</summary>
        public static int PayExtra(MainWindow w, TaskItem task, int basePoints, int extra, string reason, string feature, PayVoice voice = PayVoice.AfterTask)
        {
            CountAsExtra(w.Store, feature);
            int pay = Math.Min(extra, Room(w.Store, task, basePoints, DateTime.Now));
            if (pay <= 0) return 0;
            Pay(w, pay, reason, feature, task.Id, voice: voice);
            return pay;
        }

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

            // The per-task ceiling: power hour, dice, golden and combo style extras together stay at 4 times base at most.
            t.Status = Status.Done;
            store.Data.Log.Add(new LogEntry { When = DateTime.Now, Kind = "Done", Text = t.Name, Points = 10, TaskId = t.Id });
            AddMultiplier(store, "test.power", 2, until, "test");
            int doubled = Boost(store, 10, t);
            Need(doubled == 20, "a boost for a task with room: " + doubled);
            Pay(store, 15, "test flat prize first", "test.prize", t.Id);
            PayExtra(store, t, 10, 10, "test power", "test.power");
            PayExtra(store, t, 10, 10, "test dice", "test.dice");
            PayExtra(store, t, 10, 20, "test golden", "test.golden");
            PayExtra(store, t, 10, 15, "test combo", "test.combo");
            Need(ExtrasToday(store, t.Id, DateTime.Now) == 30, "extras for one task went past 4 times base: " + ExtrasToday(store, t.Id, DateTime.Now));
            Need(PayExtra(store, t, 10, 5, "test more", "test.more") == null && Boost(store, 10, t) == 10, "a full task still got more, or lost its flat points");
            // A flat prize with the task's id uses no room.
            Pay(store, 15, "test bingo", "test.bingo", t.Id);
            Need(ExtrasToday(store, t.Id, DateTime.Now) == 30, "a flat prize counted as an extra");
            RemoveMultiplier(store, "test.power");
            t.Status = Status.Today;
            Engine.Unfinish(store, t, DateTime.Now);
            Need(Engine.Total(store.Data) == before && !store.Data.Log.Any(e => e.TaskId == t.Id), "un-finishing left some extras");
            t.Status = Status.Done;
            store.Data.Log.Add(new LogEntry { When = DateTime.Now, Kind = "Done", Text = t.Name, Points = 10, TaskId = t.Id });
            PayExtra(store, t, 10, 10, "test again", "test.power");
            Need(ExtrasToday(store, t.Id, DateTime.Now) == 10, "done again found no room");
            t.Status = Status.Today;
            Engine.Unfinish(store, t, DateTime.Now);

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
