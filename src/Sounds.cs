using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Media;
using Concentus;

namespace BusyBunny
{
    /// <summary>
    /// Short event sounds, chosen per event in the Sounds window, built the way Kova builds its own.
    ///
    /// Patterns: 12 little figures (rising, two up, sigh...) played on real instruments in any major
    /// key: a Steinway (University of Iowa Electronic Music Studios, free for any use), bowed violin
    /// and viola sections, violin pizzicato, harp, glockenspiel and marimba (VSCO 2 Community Edition,
    /// public domain). Each note uses the nearest real recording of it, retuned the rest of the way,
    /// then a little stereo room.
    ///
    /// Recordings are built into the exe as Opus (native\sounds) and decoded the first time they are needed.
    /// Sounds never speak, never play on keystrokes, and have their own volume, apart from NVDA's.
    /// </summary>
    public static class Sounds
    {
        public enum Event { TaskDone, DailyGoal, PersonalRecord, StreakExtended, LevelUp, BossDefeated, SprintFinished, SurpriseReward, OverloadWarning }

        public static string Label(Event e) => e switch
        {
            Event.TaskDone => "_Task done",
            Event.DailyGoal => "_Daily goal reached",
            Event.PersonalRecord => "_Personal record broken",
            Event.StreakExtended => "_Streak extended",
            Event.LevelUp => "_Level up",
            Event.BossDefeated => "_Boss defeated",
            Event.SprintFinished => "Sprint _finished",
            Event.SurpriseReward => "Surprise _reward",
            _ => "_Overload warning",
        };

        private const int Rate = 48000;
        public const string NoSound = "None";
        public const string DefaultName = "Default";
        public const string RandomName = "Random sound";

        public static readonly string[] Keys =
            { "C major", "D flat major", "D major", "E flat major", "E major", "F major", "F sharp major", "G major", "A flat major", "A major", "B flat major", "B major" };

        /// <summary>The key the patterns play in (pitch class, C is 0). B flat until changed.</summary>
        public static volatile int Key = 10;
        /// <summary>Volume from 10 to 100 percent.</summary>
        public static volatile int Volume = 80;
        public static volatile bool Enabled = true;

        // Instruments: display name, the recordings they use (snd.NAME.MIDI.opus), whether rounder and
        // quieter, how far above the piano they sit, and how long each note rings before fading.
        private sealed record Instrument(string Name, string Bank, bool Soft, int Up, double Ring, double Release, string? Under = null);

        private static readonly Instrument[] Instruments =
        {
            new("Piano", "piano", false, 0, 0.42, 0.22),
            new("Soft piano", "piano", true, 0, 0.42, 0.22),
            new("Strings", "strings", false, 0, 0.85, 0.45),
            new("Piano and strings", "piano", false, 0, 0.55, 0.3, Under: "strings"),
            new("Pizzicato strings", "pizzicato", false, 12, 0.42, 0.22),
            new("Harp", "harp", false, 0, 0.5, 0.25),
            new("Glockenspiel", "glockenspiel", false, 24, 0.42, 0.22),
            new("Marimba", "marimba", false, 0, 0.42, 0.22),
        };

        // Patterns: semitones above the key's home note, and when (seconds). Every one is in the major key.
        private static readonly (string Name, (int Step, double At)[] Notes)[] Patterns =
        {
            ("rising", new[] { (0, 0.0), (4, 0.055), (7, 0.11), (12, 0.165) }),
            ("falling", new[] { (7, 0.0), (4, 0.075), (0, 0.15) }),
            ("two up", new[] { (7, 0.0), (12, 0.05) }),
            ("two down", new[] { (12, 0.0), (7, 0.05) }),
            ("four up", new[] { (4, 0.0), (7, 0.05), (12, 0.10), (16, 0.15) }),
            ("three down", new[] { (16, 0.0), (14, 0.07), (12, 0.14) }),
            ("chord high", new[] { (4, 0.0), (7, 0.0), (12, 0.0) }),
            ("chord low", new[] { (0, 0.0), (4, 0.0), (7, 0.0) }),
            ("sigh", new[] { (5, 0.0), (4, 0.13) }),
            ("single", new[] { (12, 0.0) }),
            ("bounce", new[] { (7, 0.0), (12, 0.06), (7, 0.12) }),
            ("sparkle", new[] { (9, 0.0), (11, 0.035), (12, 0.07), (14, 0.105), (16, 0.14) }),
        };

        private static readonly Lazy<string[]> Resources = new(() => typeof(Sounds).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("snd.")).ToArray());

        /// <summary>Every sound by name, None first.</summary>
        public static IReadOnlyList<string> All => _all.Value;
        private static readonly Lazy<List<string>> _all = new(() =>
        {
            var names = new List<string> { NoSound };
            foreach (var i in Instruments) foreach (var p in Patterns) names.Add(i.Name + ", " + p.Name);
            return names;
        });

        public static string Default(Event e) => e switch
        {
            Event.TaskDone => "Piano, two up",
            Event.DailyGoal => "Piano and strings, rising",
            Event.PersonalRecord => "Piano and strings, sparkle",
            Event.StreakExtended => "Piano, four up",
            Event.LevelUp => "Strings, chord high",
            Event.BossDefeated => "Piano and strings, chord high",
            Event.SprintFinished => "Pizzicato strings, bounce",
            Event.SurpriseReward => "Harp, sparkle",
            _ => "Soft piano, sigh",
        };

        /// <summary>The chosen sound for each event, or DefaultName or RandomName.</summary>
        public static Func<Event, string> Choice = _ => DefaultName;

        private static readonly Random Rng = new();
        private static readonly Dictionary<Event, string> Last = new();

        public static string Resolve(Event e, string choice)
        {
            if (choice == DefaultName) return Default(e);
            if (choice != RandomName) return choice;
            lock (Last)
            {
                Last.TryGetValue(e, out var before);
                string pick;
                do pick = All[Rng.Next(1, All.Count)];
                while (pick == before && All.Count > 2);
                Last[e] = pick;
                return pick;
            }
        }

        // Made sounds, newest used first, at most CacheMax of them.
        private const int CacheMax = 32;
        private static readonly Dictionary<string, LinkedListNode<(string Key, byte[] Wav)>> Cache = new();
        private static readonly LinkedList<(string Key, byte[] Wav)> CacheOrder = new();

        // One thread makes and starts every sound. Windows plays one SoundPlayer sound at a time, so
        // only the newest request matters: arrowing quickly through the Sounds lists plays only where you stop.
        private static readonly object SlotGate = new();
        private static (string Name, int Key, int Volume)? _next;
        private static System.Threading.Thread? _soundThread;
        private static SoundPlayer? _current;

        public static void Play(Event e)
        {
            if (!Enabled) return;
            PlayNamed(Resolve(e, Choice(e)));
        }

        /// <summary>Plays a sound by name in the background (the Sounds window previews with it, even when sounds are off).</summary>
        public static void PlayNamed(string name)
        {
            lock (SlotGate)
            {
                _next = (name, Key, Volume);
                if (_soundThread == null)
                {
                    _soundThread = new System.Threading.Thread(SoundLoop) { IsBackground = true, Name = "Busy Bunny sounds" };
                    _soundThread.Start();
                }
                System.Threading.Monitor.Pulse(SlotGate);
            }
        }

        private static void SoundLoop()
        {
            while (true)
            {
                (string Name, int Key, int Volume) want;
                lock (SlotGate)
                {
                    while (_next == null) System.Threading.Monitor.Wait(SlotGate);
                    want = _next.Value;
                    _next = null;
                }
                try
                {
                    if (Get(want.Name, want.Key, want.Volume) is not byte[] wav) continue;
                    lock (SlotGate) if (_next != null) continue;
                    var player = new SoundPlayer(new MemoryStream(wav));
                    player.Play();
                    var before = _current;
                    _current = player;
                    before?.Dispose();
                }
                catch { }
            }
        }

        /// <summary>Decodes the default sounds in the background, so the first ones play at once.</summary>
        public static void Warm() => System.Threading.ThreadPool.QueueUserWorkItem(_ =>
        {
            foreach (Event e in Enum.GetValues<Event>()) try { Get(Default(e), Key, Volume); } catch { }
        });

        /// <summary>For the self-test: makes a sound and says how long it is in seconds (0 if it could not be made).</summary>
        public static double Length(string name) => Get(name, Key, 100) is byte[] wav ? (wav.Length - 44) / 4.0 / Rate : 0;

        private static byte[]? Get(string name, int key, int volume)
        {
            if (name == NoSound) return null;
            string cacheKey = name + "|" + key + "|" + volume;
            lock (Cache)
            {
                if (Cache.TryGetValue(cacheKey, out var have))
                {
                    CacheOrder.Remove(have);
                    CacheOrder.AddFirst(have);
                    return have.Value.Wav;
                }
                var (l, r) = Make(name, key);
                if (l == null) return null;
                byte[] wav = Wav(l, r!, volume);
                Cache[cacheKey] = CacheOrder.AddFirst((cacheKey, wav));
                while (Cache.Count > CacheMax)
                {
                    var oldest = CacheOrder.Last!;
                    CacheOrder.RemoveLast();
                    Cache.Remove(oldest.Value.Key);
                }
                return wav;
            }
        }

        private static (double[]? L, double[]? R) Make(string name, int key)
        {
            int comma = name.IndexOf(", ", StringComparison.Ordinal);
            if (comma < 0) return (null, null);
            var inst = Instruments.FirstOrDefault(i => i.Name == name[..comma]);
            int pattern = Array.FindIndex(Patterns, p => p.Name == name[(comma + 2)..]);
            if (inst == null || pattern < 0) return (null, null);
            return PatternSound(inst, Patterns[pattern].Notes, key);
        }

        private static (double[] L, double[] R) PatternSound(Instrument inst, (int Step, double At)[] notes, int key)
        {
            // The key's home note: the one from G flat 3 (54) up, so every key fits the recordings.
            int home = 54 + ((key - 54 % 12) % 12 + 12) % 12 + inst.Up;
            double last = notes.Max(n => n.At);
            int length = (int)((last + inst.Ring + inst.Release + 0.6) * Rate);
            var left = new double[length];
            var right = new double[length];
            for (int k = 0; k < notes.Length; k++)
            {
                var (step, at) = notes[k];
                double pan = notes.Length == 1 ? 0 : -0.2 + 0.4 * k / (notes.Length - 1); // a slight spread, low to high
                double gl = Math.Sqrt(0.5 * (1 - pan)) * 1.414, gr = Math.Sqrt(0.5 * (1 + pan)) * 1.414;
                int start = (int)(at * Rate);
                Add(Note(inst.Bank, home + step, inst.Ring, inst.Release), 1.0);
                // Piano and strings: the strings hold the same notes underneath, softer and longer.
                if (inst.Under != null) Add(Note(inst.Under, home + step, inst.Ring + 0.35, 0.5), 0.45);

                void Add((double[] L, double[] R) note, double gain)
                {
                    var (nl, nr) = note;
                    int n = Math.Min(nl.Length, length - start);
                    double lpL = 0, lpR = 0, a = inst.Soft ? 0.2 : 1; // the soft piano: rounder and quieter
                    for (int i = 0; i < n; i++)
                    {
                        lpL += a * (nl[i] - lpL); lpR += a * (nr[i] - lpR);
                        left[start + i] += lpL * gl * gain * (inst.Soft ? 0.8 : 1);
                        right[start + i] += lpR * gr * gain * (inst.Soft ? 0.8 : 1);
                    }
                }
            }
            Room(left, right, inst.Soft ? 0.26 : inst.Bank == "strings" ? 0.24 : 0.18);
            return (left, right);
        }

        /// <summary>One note from an instrument's recordings: the nearest one, retuned the rest of the way, ringing then fading.</summary>
        private static (double[] L, double[] R) Note(string bank, int midi, double ringSeconds, double releaseSeconds)
        {
            var have = Resources.Value.Where(r => r.StartsWith("snd." + bank + "."))
                .Select(r => int.Parse(r["snd.".Length..^".opus".Length][(bank.Length + 1)..])).ToArray();
            int nearest = have.OrderBy(m => Math.Abs(m - midi)).First();
            var (sl, sr) = Recording("snd." + bank + "." + nearest + ".opus");
            double ratio = Math.Pow(2, (midi - nearest) / 12.0);
            int ring = (int)(ringSeconds * Rate), release = (int)(releaseSeconds * Rate);
            int length = Math.Min(ring + release, (int)((sl.Length - 2) / ratio));
            var l = new double[Math.Max(0, length)];
            var r = new double[l.Length];
            for (int i = 0; i < l.Length; i++)
            {
                double pos = i * ratio;
                int p = (int)pos;
                double f = pos - p;
                double g = i > ring ? Math.Pow(Math.Cos((i - ring) / (double)release * Math.PI / 2), 2) : 1;
                l[i] = (sl[p] + (sl[p + 1] - sl[p]) * f) * g;
                r[i] = (sr[p] + (sr[p + 1] - sr[p]) * f) * g;
            }
            return (l, r);
        }

        // ---- Recordings ----

        private static readonly Dictionary<string, (double[] L, double[] R)> Recordings = new();

        private static (double[] L, double[] R) Recording(string resource)
        {
            lock (Recordings)
            {
                if (Recordings.TryGetValue(resource, out var have)) return have;
                using var s = typeof(Sounds).Assembly.GetManifestResourceStream(resource) ?? throw new InvalidOperationException("No " + resource);
                using var ms = new MemoryStream();
                s.CopyTo(ms);
                var decoded = OggOpus(ms.ToArray());
                Recordings[resource] = decoded;
                return decoded;
            }
        }

        /// <summary>
        /// Decodes an Ogg Opus file: gathers the packets from the Ogg pages, skips the two header
        /// packets and Opus's pre-skip, and stops at the length the last page gives. Mono files play on both sides.
        /// </summary>
        private static (double[] L, double[] R) OggOpus(byte[] file)
        {
            var packets = new List<byte[]>();
            var current = new MemoryStream();
            long lastGranule = 0;
            int pos = 0;
            while (pos + 27 <= file.Length && file[pos] == 'O' && file[pos + 1] == 'g' && file[pos + 2] == 'g' && file[pos + 3] == 'S')
            {
                long granule = BitConverter.ToInt64(file, pos + 6);
                int segments = file[pos + 26];
                int data = pos + 27 + segments;
                for (int i = 0; i < segments; i++)
                {
                    int len = file[pos + 27 + i];
                    current.Write(file, data, len);
                    data += len;
                    if (len < 255) { packets.Add(current.ToArray()); current.SetLength(0); }
                }
                if (granule > 0) lastGranule = granule;
                pos = data;
            }
            if (packets.Count < 3) return (Array.Empty<double>(), Array.Empty<double>());
            int channels = Math.Max(1, (int)packets[0][9]);
            int preSkip = BitConverter.ToUInt16(packets[0], 10);
            var decoder = OpusCodecFactory.CreateDecoder(Rate, channels);
            var pcm = new short[5760 * channels];
            var all = new List<short>();
            for (int i = 2; i < packets.Count; i++)
            {
                int frames = decoder.Decode(packets[i], pcm, 5760, false);
                for (int k = 0; k < frames * channels; k++) all.Add(pcm[k]);
            }
            int count = all.Count / channels;
            int total = (int)Math.Min(count - preSkip, lastGranule > preSkip ? lastGranule - preSkip : count);
            var l = new double[Math.Max(0, total)];
            var r = new double[l.Length];
            for (int i = 0; i < l.Length; i++)
            {
                int at = (preSkip + i) * channels;
                l[i] = all[at] / 32768.0;
                r[i] = all[at + channels - 1] / 32768.0;
            }
            return (l, r);
        }

        // ---- Output ----

        private static byte[] Wav(double[] left, double[] right, int volume)
        {
            // Trim the silence the room leaves at the end.
            int length = left.Length;
            while (length > Rate / 10 && Math.Abs(left[length - 1]) < 1e-4 && Math.Abs(right[length - 1]) < 1e-4) length--;
            double peak = 1e-9;
            for (int i = 0; i < length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
            double gain = 0.40 / peak * Math.Clamp(volume, 10, 100) / 100.0;
            int fade = Math.Min(length / 4, Rate / 12);
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36 + length * 4); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(Rate); w.Write(Rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(length * 4);
            for (int i = 0; i < length; i++)
            {
                double f = i > length - fade ? (length - i) / (double)fade : 1;
                w.Write((short)Math.Clamp(Math.Round(left[i] * gain * f * 32767), -32768, 32767));
                w.Write((short)Math.Clamp(Math.Round(right[i] * gain * f * 32767), -32768, 32767));
            }
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>A little room in stereo (Freeverb: eight damped echoes and four smears each side).</summary>
        private static void Room(double[] left, double[] right, double wet)
        {
            int[] combs = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
            int[] passes = { 556, 441, 341, 225 };
            double scale = Rate / 44100.0;
            double[] Side(double[] input, int spread)
            {
                var output = new double[input.Length];
                foreach (int c in combs)
                {
                    var buf = new double[(int)((c + spread) * scale)];
                    double store = 0;
                    for (int i = 0, at = 0; i < input.Length; i++)
                    {
                        double y = buf[at];
                        store = y * 0.6 + store * 0.4;
                        buf[at] = input[i] * 0.015 + store * 0.8;
                        at = (at + 1) % buf.Length;
                        output[i] += y;
                    }
                }
                foreach (int p in passes)
                {
                    var buf = new double[(int)((p + spread) * scale)];
                    for (int i = 0, at = 0; i < output.Length; i++)
                    {
                        double b = buf[at];
                        double y = b - output[i];
                        buf[at] = output[i] + b * 0.5;
                        at = (at + 1) % buf.Length;
                        output[i] = y;
                    }
                }
                return output;
            }
            var wl = Side(left, 0);
            var wr = Side(right, 23);
            for (int i = 0; i < left.Length; i++)
            {
                left[i] = left[i] * (1 - wet * 0.5) + wl[i] * wet * 3;
                right[i] = right[i] * (1 - wet * 0.5) + wr[i] * wet * 3;
            }
        }
    }
}
