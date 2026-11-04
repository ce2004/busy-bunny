using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// The content library (sweep 8.2 D): wording lives in plain text files in src\Content, one per topic, built
    /// into the exe. A topic like fun.done is the file fun.done.txt. Entries are separated by blank lines; an entry's
    /// lines are joined with spaces. In a structured entry (a place or an item) every line is key: value, like
    /// name: Brass lamp, touch: ..., sound: ..., looks: .... Touch and sound come before looks (Morgan learns by
    /// touch first). Never a pound sign or an asterisk in these files.
    /// </summary>
    public static class Content
    {
        private static readonly Dictionary<string, List<string>> _cache = new();
        private const string RecentKey = "content.recent";
        private const int NoRepeat = 10;

        /// <summary>Every entry of a topic, in file order. Empty when there is no such topic.</summary>
        public static List<string> Lines(string topic)
        {
            if (_cache.TryGetValue(topic, out var have)) return have;
            var entries = new List<string>();
            var current = new List<string>();
            foreach (var raw in App.Resource("content." + topic + ".txt").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { if (current.Count > 0) entries.Add(string.Join(" ", current)); current.Clear(); continue; }
                current.Add(line);
            }
            if (current.Count > 0) entries.Add(string.Join(" ", current));
            return _cache[topic] = entries;
        }

        /// <summary>The structured entries of a topic: each a set of key: value lines (keys in lower case). Lines without a key are left out.</summary>
        public static List<Dictionary<string, string>> Items(string topic)
        {
            var items = new List<Dictionary<string, string>>();
            var current = new Dictionary<string, string>();
            foreach (var raw in App.Resource("content." + topic + ".txt").Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0) { if (current.Count > 0) items.Add(current); current = new(); continue; }
                int colon = line.IndexOf(':');
                if (colon > 0) current[line[..colon].Trim().ToLowerInvariant()] = line[(colon + 1)..].Trim();
            }
            if (current.Count > 0) items.Add(current);
            return items;
        }

        /// <summary>
        /// One entry of a topic, never one of the last 10 used (remembered in the world state, across restarts and
        /// never put back by undo). A topic with 10 or fewer entries avoids as many recent ones as it can. Empty when
        /// the topic has none. The {name} in a line becomes her bunny's name.
        /// </summary>
        public static string Pick(Store store, string topic, Random? rng = null)
        {
            var all = Lines(topic);
            if (all.Count == 0) return "";
            var recent = store.World.GetKept<Dictionary<string, List<int>>>(RecentKey);
            var used = recent.TryGetValue(topic, out var u) ? u : new List<int>();
            int avoid = Math.Min(NoRepeat, all.Count - 1);
            var skip = used.TakeLast(avoid).ToHashSet();
            var choices = Enumerable.Range(0, all.Count).Where(i => !skip.Contains(i)).ToList();
            int pick = choices[(rng ?? Shared).Next(choices.Count)];
            used.Add(pick);
            if (used.Count > NoRepeat) used.RemoveRange(0, used.Count - NoRepeat);
            recent[topic] = used;
            store.World.SetKept(RecentKey, recent);
            store.World.Save();
            return all[pick].Replace("{name}", store.Data.Game.BunnyName);
        }

        /// <summary>The same, for the main window's store (with no window yet, a random entry, or empty).</summary>
        public static string Pick(string topic)
        {
            if (Engines.Store is Store s) return Pick(s, topic);
            var all = Lines(topic);
            return all.Count == 0 ? "" : all[Shared.Next(all.Count)];
        }

        private static readonly Random Shared = new();

        public static void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("content: " + what); }
            var lines = Lines("fun.done");
            Need(lines.Count >= 20, "fun.done has " + lines.Count + " lines, wanted at least 20");
            foreach (var topic in new[] { "fun.done", "tools.tips", "selftest.items" })
                foreach (var l in Lines(topic)) Need(!l.Contains((char)35) && !l.Contains((char)42), topic + " has a pound sign or asterisk");
            var items = Items("selftest.items");
            Need(items.Count == 2 && items[0]["name"] == "Brass lamp" && items[1].ContainsKey("touch"), "structured items");
            // 200 picks: none repeats any of the 10 before it.
            var rng = new Random(7);
            var seen = new List<string>();
            for (int i = 0; i < 200; i++)
            {
                string p = Pick(store, "fun.done", rng);
                Need(!seen.TakeLast(10).Contains(p), "a line repeated within 10 picks: " + p);
                seen.Add(p);
            }
            Need(Pick(store, "no.such.topic") == "", "a missing topic");
        }
    }
}
