using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace BusyBunny
{
    /// <summary>What world.json holds: a bag per feature, so a feature never needs a change here to keep its things.</summary>
    public sealed class WorldData
    {
        public int Version { get; set; } = 1;
        /// <summary>Each feature's own saved value, by feature id. Control Z puts these back with the tasks.</summary>
        public Dictionary<string, JsonElement> Bags { get; set; } = new();
        /// <summary>Bookkeeping undo leaves alone: lines used lately, Bunny's writing counts, places already announced.</summary>
        public Dictionary<string, JsonElement> Kept { get; set; } = new();
    }

    /// <summary>
    /// Bunny's world (sweep 8.2 C): world.json next to tasks.json, saved the same safe way (a new file written
    /// straight to the disk, then swapped in, the one before kept as world.json.bak) with 30 daily backups
    /// named world yyyy-MM-dd.json. A damaged file loads the newest good copy and says so in LoadNote. It never
    /// touches the tasks. Reach it through store.World, or the World helpers.
    /// </summary>
    public sealed class WorldState
    {
        private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
        private readonly string _file;
        private readonly string? _backups;
        private string _backedUp = "";
        public WorldData Data { get; private set; } = new();
        /// <summary>Something to tell her, like a damaged world.json replaced by its backup. Null when all was well.</summary>
        public string? LoadNote { get; set; }
        /// <summary>The last save failed; the next save tries again.</summary>
        public bool Dirty { get; private set; }

        private WorldState(string file, string? backups) { _file = file; _backups = backups; }

        /// <summary>
        /// True when world.json was there but another program kept it locked at start: Bunny's world is empty for now and
        /// nothing is saved over the real file until Busy Bunny starts again (a lock is not damage).
        /// </summary>
        public bool Locked { get; private set; }

        /// <summary>Loads world.json. waitTries is how many times (a fifth of a second apart) a locked file is tried again.</summary>
        public static WorldState Load(string file, string? backups, int waitTries = 20)
        {
            var w = new WorldState(file, backups);
            if (File.Exists(file))
            {
                var (text, locked) = Read(file, waitTries);
                if (locked)
                {
                    w.Locked = true;
                    w.LoadNote = "Another program was using Bunny's world file, so Bunny's world is paused until Busy Bunny starts again. Nothing in it was lost, and your tasks are fine.";
                    if (backups != null) ErrorLog.Write("world.json was locked at start; it will not be saved over this time.");
                    return w;
                }
                var d = Parse(text);
                if (d != null) { w.Data = d; return w; }
                try { File.Copy(file, file + ".damaged-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"), true); } catch { }
                var (good, from) = w.Rescue();
                w.Data = good ?? new WorldData();
                w.LoadNote = good != null
                    ? "Bunny's world file was damaged, so I loaded " + from + ". Your tasks were not touched."
                    : "Bunny's world file was damaged and I found no good backup, so Bunny's world starts fresh. Your tasks and points were not touched.";
                if (backups != null) ErrorLog.Write("world.json was damaged at start. " + (good != null ? "Loaded " + from + "." : "No good backup."));
            }
            else if (w.Rescue() is (WorldData found, string from2) && found.Bags.Count + found.Kept.Count > 0)
            {
                w.Data = found;
                w.LoadNote = "I could not find Bunny's world file, so I loaded " + from2 + ".";
            }
            return w;
        }

        /// <summary>The text, or Locked when another program kept the file open the whole time.</summary>
        private static (string? Text, bool Locked) Read(string f, int tries = 20)
        {
            for (int i = 0; i < Math.Max(1, tries); i++)
            {
                try { return (File.ReadAllText(f), false); }
                catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException) { return (null, false); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Threading.Thread.Sleep(200); }
            }
            return (null, true);
        }

        private static WorldData? Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            try
            {
                var d = JsonSerializer.Deserialize<WorldData>(text, Json);
                if (d == null) return null;
                d.Bags ??= new();
                d.Kept ??= new();
                return d;
            }
            catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException or ArgumentException) { return null; }
        }

        private (WorldData?, string) Rescue()
        {
            var tries = new List<(string, string)> { (_file + ".new", "the copy from the last save"), (_file + ".bak", "the copy from the save before that") };
            if (_backups != null && Directory.Exists(_backups))
                foreach (var f in new DirectoryInfo(_backups).GetFiles("world ????-??-??.json").OrderByDescending(f => f.Name))
                    tries.Add((f.FullName, "the backup from " + f.Name[6..16]));
            foreach (var (f, words) in tries)
                if (File.Exists(f) && Parse(Read(f, 5).Text) is WorldData d) return (d, words);
            return (null, "");
        }

        /// <summary>Saves now; never throws. A failure is logged and the next save tries again.</summary>
        public bool Save()
        {
            if (Locked) return false;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(Data, Json);
                string fresh = _file + ".new";
                using (var fs = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 14, FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                if (File.Exists(_file)) File.Replace(fresh, _file, _backups == null ? null : _file + ".bak", ignoreMetadataErrors: true);
                else File.Move(fresh, _file);
                Backup(bytes);
                Dirty = false;
                return true;
            }
            catch (Exception e)
            {
                if (!Dirty && _backups != null) ErrorLog.Write("Saving world.json failed: " + e.GetType().Name + ": " + e.Message);
                Dirty = true;
                return false;
            }
        }

        private void Backup(byte[] bytes)
        {
            if (_backups == null) return;
            string name = "world " + DateTime.Now.ToString("yyyy-MM-dd") + ".json";
            if (_backedUp == name) return;
            try
            {
                Directory.CreateDirectory(_backups);
                string today = Path.Combine(_backups, name);
                if (!File.Exists(today)) File.WriteAllBytes(today, bytes);
                foreach (var f in new DirectoryInfo(_backups).GetFiles("world ????-??-??.json").OrderByDescending(f => f.Name).Skip(30)) f.Delete();
                _backedUp = name;
            }
            catch (Exception e) { ErrorLog.Write("World backup failed: " + e.GetType().Name + ": " + e.Message); }
        }

        // ---- Undo (Store.Remember keeps a copy of this with every step) ----

        /// <summary>The bags as they are now, for an undo step.</summary>
        public byte[] Snapshot() => JsonSerializer.SerializeToUtf8Bytes(Data.Bags, Json);

        /// <summary>Puts the bags back as they were in an undo step, keeping the bookkeeping, and saves.</summary>
        public void Restore(byte[] snapshot)
        {
            try { Data.Bags = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(snapshot, Json) ?? new(); }
            catch (JsonException) { return; }
            Save();
        }

        // ---- Typed helpers ----

        /// <summary>A feature's saved value, or a new one when it has none (or it no longer reads). A copy: Set it back after changing it.</summary>
        public T Get<T>(string featureId) where T : new() => Read<T>(Data.Bags, featureId);
        /// <summary>Stores a feature's value. Call store.Remember first when Control Z should undo it, then Save.</summary>
        public void Set<T>(string featureId, T value) => Data.Bags[featureId] = JsonSerializer.SerializeToElement(value, Json);
        /// <summary>Bookkeeping undo never puts back (lines used lately, counters).</summary>
        public T GetKept<T>(string key) where T : new() => Read<T>(Data.Kept, key);
        public void SetKept<T>(string key, T value) => Data.Kept[key] = JsonSerializer.SerializeToElement(value, Json);

        private static T Read<T>(Dictionary<string, JsonElement> bag, string key) where T : new()
        {
            if (!bag.TryGetValue(key, out var e)) return new T();
            try { return e.Deserialize<T>(Json) ?? new T(); }
            catch (Exception x) when (x is JsonException or NotSupportedException or InvalidOperationException) { return new T(); }
        }
    }

    /// <summary>A thing to do or look at inside a place: name first, then a few words. Run gets the world window, to own any window it opens.</summary>
    public sealed record PlaceThing(string Name, string Description, Action<System.Windows.Window> Run);

    /// <summary>A place in Bunny's world. Unlocked says whether it shows yet; Things lists what is inside, worked out fresh each visit.</summary>
    public sealed record Place(string Id, string Name, Func<Store, string> Describe, Func<Store, bool> Unlocked, Func<Store, List<PlaceThing>> Things);

    /// <summary>Short ways to the world state, and the places in the world window.</summary>
    public static class World
    {
        public static T Get<T>(Store store, string featureId) where T : new() => store.World.Get<T>(featureId);
        public static void Set<T>(Store store, string featureId, T value) => store.World.Set(featureId, value);
        public static bool Save(Store store) => store.World.Save();
        // The same for the main window's store.
        private static Store Main => Engines.Store ?? throw new InvalidOperationException("The main window is not up yet.");
        public static T Get<T>(string featureId) where T : new() => Main.World.Get<T>(featureId);
        public static void Set<T>(string featureId, T value) => Main.World.Set(featureId, value);
        public static bool Save() => Main.World.Save();

        /// <summary>Undoable change: remembers the tasks and the world as one Control Z step, then changes the value and saves.</summary>
        public static void Change<T>(Store store, string featureId, string what, Action<T> change) where T : new()
        {
            store.Remember(what);
            var v = store.World.Get<T>(featureId);
            change(v);
            store.World.Set(featureId, v);
            store.World.Save();
            store.Save();
        }

        /// <summary>Same as store.Remember: one undo step holds the tasks and the world together.</summary>
        public static void Remember(Store store, string what) => store.Remember(what);

        /// <summary>Every place, in the order registered. Register again with the same id to replace one.</summary>
        public static class Places
        {
            private static readonly List<Place> _all = new();
            public static IReadOnlyList<Place> All => _all;
            public static void Register(Place p)
            {
                int i = _all.FindIndex(x => x.Id == p.Id);
                if (i >= 0) _all[i] = p; else _all.Add(p);
            }
            public static Place? Find(string id) => _all.FirstOrDefault(p => p.Id == id);
        }
    }
}
