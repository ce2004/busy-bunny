using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BusyBunny
{
    /// <summary>
    /// The Bunny AI hook (sweep 8.2 I): any feature can ask Smart Bunny for a short line. It sends only the text the
    /// caller gives it, so callers build prompts from Visible(store) tasks only (never a private or archived list).
    /// The daily cost limit: each feature may get Settings.BunnyWritePerFeature lines a day (10) and all of them together
    /// Settings.BunnyWriteDaily (40), set on Settings, Fun and tools; only answers that came back count. With no key,
    /// offline, over the limit, in Calm mode or on any error it falls back to Content.Pick(fallbackTopic). Never throws.
    /// </summary>
    public static class BunnyWrite
    {
        private const string CountsKey = "bunnywrite.counts";
        private sealed class Counts { public string Day { get; set; } = ""; public Dictionary<string, int> By { get; set; } = new(); }

        /// <summary>Tasks Bunny may see: not on a private list (or inside one), not archived.</summary>
        public static List<TaskItem> Visible(Store store) =>
            store.Data.Tasks.Where(t => SmartBunny.MayShare(store, t) && !store.ListOf(t).Archived).ToList();

        private static Counts Today(Store store, DateTime now)
        {
            var c = store.World.GetKept<Counts>(CountsKey);
            if (c.Day != store.Key(now)) { c.Day = store.Key(now); c.By = new(); }
            return c;
        }

        /// <summary>Whether this feature may still have Claude write today, under both caps. Counts nothing.</summary>
        public static bool MayAsk(Store store, string featureId, DateTime now, Settings? cfg = null)
        {
            cfg ??= Engines.Settings ?? new Settings();
            var c = Today(store, now);
            return c.By.GetValueOrDefault(featureId) < Math.Max(0, cfg.BunnyWritePerFeature) && c.By.Values.Sum() < Math.Max(0, cfg.BunnyWriteDaily);
        }

        /// <summary>Counts one line Claude did write for this feature (call it only after an answer came back).</summary>
        public static void Counted(Store store, string featureId, DateTime now)
        {
            var c = Today(store, now);
            c.By[featureId] = c.By.GetValueOrDefault(featureId) + 1;
            store.World.SetKept(CountsKey, c);
            store.World.Save();
        }

        /// <summary>How many lines Claude wrote today, all features together.</summary>
        public static int UsedToday(Store store, DateTime now) => Today(store, now).By.Values.Sum();

        /// <summary>A short line from Bunny, or from the content library when Bunny cannot write it now.</summary>
        public static async Task<string> Short(Store store, string featureId, string prompt, string fallbackTopic)
        {
            try
            {
                if (SmartBunny.Ready && !Calm.FunPaused && MayAsk(store, featureId, DateTime.Now)
                    && await SmartBunny.WriteAsync(prompt) is string written && written.Length > 0)
                {
                    Counted(store, featureId, DateTime.Now);
                    return written;
                }
            }
            catch (Exception e) { ErrorLog.Write("Bunny's writing for " + featureId + " failed: " + e.GetType().Name); }
            return Content.Pick(store, fallbackTopic);
        }

        public static void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Bunny's writing: " + what); }
            var cfg = new Settings { BunnyWritePerFeature = 2, BunnyWriteDaily = 3 };
            var now = DateTime.Now;
            Need(MayAsk(store, "test.a", now, cfg), "a first line was refused");
            Counted(store, "test.a", now);
            Need(MayAsk(store, "test.a", now, cfg), "asking without an answer used up the limit");
            Counted(store, "test.a", now);
            Need(!MayAsk(store, "test.a", now, cfg), "the per-feature limit");
            Need(MayAsk(store, "test.b", now, cfg), "another feature was refused under the overall limit");
            Counted(store, "test.b", now);
            Need(!MayAsk(store, "test.c", now, cfg), "the overall limit");
            Need(MayAsk(store, "test.a", now.AddDays(1), cfg), "the limit starts over the next day");
            cfg.BunnyWriteDaily = 0;
            Need(!MayAsk(store, "test.z", now.AddDays(1), cfg), "a limit of none");
        }

        /// <summary>The same, for the main window's store.</summary>
        public static Task<string> Short(string featureId, string prompt, string fallbackTopic) =>
            Engines.Store is Store s ? Short(s, featureId, prompt, fallbackTopic) : Task.FromResult(Content.Lines(fallbackTopic).FirstOrDefault() ?? "");
    }

    /// <summary>
    /// The sound bank (sweep 8.2 J): features name a sound by id. An id is one of the app's events (like LevelUp),
    /// one registered here, or the name of a built-in sound (like "Bunny, hop"). A missing one is skipped silently and
    /// written to the error log once. The sound switches and volume apply as for every sound.
    /// </summary>
    public static class SoundBank
    {
        private static readonly Dictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _missing = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Names an id for a built-in sound, so features can share it (like garden.water for "Harp, glissando").</summary>
        public static void Register(string id, string sound) => _map[id] = sound;

        /// <summary>Whether the id can be played.</summary>
        public static bool Has(string id) => Enum.TryParse<Sounds.Event>(id, true, out _) || _map.ContainsKey(id) || Sounds.All.Contains(id);

        public static void Play(string id)
        {
            try
            {
                if (!Sounds.Enabled || Calm.FunPaused && !Enum.TryParse<Sounds.Event>(id, true, out _)) return;
                if (Enum.TryParse<Sounds.Event>(id, true, out var e)) { Sounds.Play(e); return; }
                string name = _map.TryGetValue(id, out var m) ? m : id;
                if (Sounds.All.Contains(name)) { Sounds.PlayNamed(name); return; }
                if (_missing.Add(id)) ErrorLog.Write("No sound called " + id + "; skipped.");
            }
            catch (Exception x) { ErrorLog.Write("Playing " + id + " failed: " + x.GetType().Name); }
        }
    }
}
