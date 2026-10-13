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
        public enum Event { Startup, TaskDone, DailyGoal, PersonalRecord, StreakExtended, LevelUp, BossDefeated, SprintFinished, SurpriseReward, OverloadWarning, TaskAdded, TaskSaved,
            MenuOpened, SubmenuOpened, MenuClosed, WindowOpened, WindowClosed, TabSwitched, CheckedOn, CheckedOff, PickOpened, PickClosed, MovedField }

        /// <summary>Sounds for moving around the app, played softer than the ones for things happening.</summary>
        public static bool IsUi(Event e) => e >= Event.MenuOpened;

        public static string Label(Event e) => e switch
        {
            Event.Startup => "Start_up",
            Event.TaskDone => "_Task done",
            Event.DailyGoal => "_Daily goal reached",
            Event.PersonalRecord => "_Personal record broken",
            Event.StreakExtended => "_Streak extended",
            Event.LevelUp => "_Level up",
            Event.BossDefeated => "_Boss defeated",
            Event.SprintFinished => "Sprint _finished",
            Event.SurpriseReward => "Surprise _reward",
            Event.TaskAdded => "Task a_dded",
            Event.TaskSaved => "Task sa_ved after editing",
            Event.MenuOpened => "Menu opens",
            Event.SubmenuOpened => "Submenu opens",
            Event.MenuClosed => "Menu closes",
            Event.WindowOpened => "Window opens",
            Event.WindowClosed => "Window closes",
            Event.TabSwitched => "Tab changes",
            Event.CheckedOn => "Check box checked",
            Event.CheckedOff => "Check box unchecked",
            Event.PickOpened => "Pick menu opens",
            Event.PickClosed => "Pick menu closes",
            Event.MovedField => "Tab to the next field",
            _ => "_Overload warning",
        };

        private const int Rate = 48000;
        public const string NoSound = "Off";
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
            new("Low strings", "strings", false, -12, 0.9, 0.5),
            new("Bells", "glockenspiel", false, 12, 0.8, 0.45),
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
            // Minor figures, for the Halloween pack.
            ("minor rising", new[] { (0, 0.0), (3, 0.06), (7, 0.12), (12, 0.18) }),
            ("minor falling", new[] { (7, 0.0), (3, 0.08), (0, 0.16) }),
            ("minor chord", new[] { (0, 0.0), (3, 0.0), (7, 0.0) }),
            ("creep", new[] { (0, 0.0), (3, 0.14), (6, 0.28) }),
            ("minor sigh", new[] { (3, 0.0), (2, 0.14) }),
        };

        private static readonly Lazy<string[]> Resources = new(() => typeof(Sounds).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("snd.")).ToArray());

        /// <summary>Every sound by name, None first.</summary>
        public static IReadOnlyList<string> All => _all.Value;
        private static readonly Lazy<List<string>> _all = new(() =>
        {
            var names = new List<string> { NoSound, Angelic };
            foreach (var i in Instruments) foreach (var p in Patterns) names.Add(i.Name + ", " + p.Name);
            return names;
        });

        public static string Default(Event e) => e switch
        {
            Event.Startup => Angelic,
            Event.TaskDone => "Piano, two up",
            Event.DailyGoal => "Piano and strings, rising",
            Event.PersonalRecord => "Piano and strings, sparkle",
            Event.StreakExtended => "Piano, four up",
            Event.LevelUp => "Strings, chord high",
            Event.BossDefeated => "Piano and strings, chord high",
            Event.SprintFinished => "Pizzicato strings, bounce",
            Event.SurpriseReward => "Harp, sparkle",
            Event.TaskAdded => "Marimba, rising",
            Event.TaskSaved => "Harp, two up",
            Event.MenuOpened => "Harp, two up",
            Event.SubmenuOpened => "Harp, single",
            Event.MenuClosed => "Harp, two down",
            Event.WindowOpened => "Soft piano, two up",
            Event.WindowClosed => "Soft piano, two down",
            Event.TabSwitched => "Marimba, single",
            Event.CheckedOn => "Pizzicato strings, two up",
            Event.CheckedOff => "Pizzicato strings, two down",
            Event.PickOpened => "Marimba, two up",
            Event.PickClosed => "Marimba, two down",
            Event.MovedField => "Glockenspiel, single",
            _ => "Soft piano, sigh",
        };

        /// <summary>The chosen sound for each event, or DefaultName or RandomName.</summary>
        public static Func<Event, string> Choice = _ => DefaultName;

        private static readonly Random Rng = new();
        private static readonly Dictionary<Event, string> Last = new();

        public static string Resolve(Event e, string choice)
        {
            if (choice == "None") return NoSound;
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
            string name = Resolve(e, Choice(e));
            int volume = IsUi(e) ? Math.Max(10, Volume * 55 / 100) : Volume;
            int key = Key;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    if (Get(name, key, volume) is byte[] wav && !Mixer.Play(wav, exclusive: false)) PlayNamed(name, volume);
                }
                catch { }
            });
        }

        public static int SampleRate => Rate;

        /// <summary>The recording nearest a note on an instrument's bank, and the note it was recorded at, for the music.</summary>
        public static (double[] L, double[] R, int Midi) RecordingFor(string bank, int midi)
        {
            var have = Resources.Value.Where(r => r.StartsWith("snd." + bank + "."))
                .Select(r => int.Parse(r["snd.".Length..^".opus".Length][(bank.Length + 1)..])).ToArray();
            int nearest = have.OrderBy(m => Math.Abs(m - midi)).First();
            var (l, r) = Recording("snd." + bank + "." + nearest + ".opus");
            return (l, r, nearest);
        }

        /// <summary>Plays a sound by name in the background (the Sounds window previews with it, even when sounds are off).</summary>
        public static void PlayNamed(string name) => PlayNamed(name, Volume);

        /// <summary>The instruments by name, for picking one at random.</summary>
        public static IReadOnlyList<string> InstrumentNames => Instruments.Select(i => i.Name).ToArray();

        /// <summary>
        /// One note of the major scale (0 is the key's home note, 7 an octave up) on an instrument, at a
        /// share of the usual volume: the update download climbs the scale with these. Silent when sounds are off.
        /// </summary>
        public static void PlayScaleNote(string instrument, int degree, double share)
        {
            if (!Enabled) return;
            int[] major = { 0, 2, 4, 5, 7, 9, 11, 12 };
            int step = major[Math.Clamp(degree, 0, 7)];
            PlayNamed(instrument + ", note " + step, Math.Max(1, (int)(Volume * share)));
        }

        private static void PlayNamed(string name, int volume)
        {
            lock (SlotGate)
            {
                _next = (name, Key, volume);
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
                    if (Mixer.Play(wav, exclusive: true)) continue;
                    // No mixer (no sound device it could open): Windows' own player, one sound at a time.
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
        public static byte[]? WavOf(string name) => Get(name, Key, 100);

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
                byte[] wav;
                if (name.StartsWith(MinePrefix, StringComparison.Ordinal))
                {
                    if (Mine(name, volume) is not byte[] mine) return null;
                    wav = mine;
                }
                else
                {
                    var (l, r) = Make(name, key);
                    if (l == null) return null;
                    wav = Wav(l, r!, volume);
                }
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

        // ---- Her own sounds (spec 12b.3): WAV files copied into AppData, Busy Bunny, Sounds ----

        public const string MinePrefix = "My sound, ";
        public static string MyFolder => Path.Combine(Store.Folder, "Sounds");

        /// <summary>The sounds she imported, as names to pick (My sound, file name).</summary>
        public static List<string> Imported()
        {
            try
            {
                if (!Directory.Exists(MyFolder)) return new();
                return Directory.GetFiles(MyFolder, "*.wav").Select(f => MinePrefix + Path.GetFileNameWithoutExtension(f)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch { return new(); }
        }

        /// <summary>Every sound she can pick: the built-in ones, then her own.</summary>
        public static List<string> Choices() => All.Skip(1).Concat(Imported()).ToList();

        /// <summary>Copies a WAV file into her Sounds folder. Returns the name to pick, or null with a problem in error.</summary>
        public static string? Import(string path, out string error)
        {
            error = "";
            try
            {
                var head = new byte[12];
                using (var f = File.OpenRead(path)) if (f.Read(head, 0, 12) < 12) { error = "That file is empty."; return null; }
                if (!(head[0] == 'R' && head[1] == 'I' && head[2] == 'F' && head[3] == 'F' && head[8] == 'W' && head[9] == 'A' && head[10] == 'V' && head[11] == 'E'))
                { error = "That is not a WAV file. Only WAV files play."; return null; }
                Directory.CreateDirectory(MyFolder);
                string name = Path.GetFileNameWithoutExtension(path);
                File.Copy(path, Path.Combine(MyFolder, name + ".wav"), true);
                lock (Cache) { Cache.Clear(); CacheOrder.Clear(); }
                return MinePrefix + name;
            }
            catch (Exception e) { error = "Could not add it: " + e.Message; return null; }
        }

        private static byte[]? Mine(string name, int volume)
        {
            try
            {
                string file = Path.Combine(MyFolder, name[MinePrefix.Length..] + ".wav");
                return File.Exists(file) ? ScaleWav(File.ReadAllBytes(file), volume) : null;
            }
            catch { return null; }
        }

        /// <summary>Scales 16-bit PCM WAV samples by the volume (100 percent leaves them as they are). Anything else plays as is.</summary>
        public static byte[] ScaleWav(byte[] wav, int volume)
        {
            try
            {
                if (volume >= 100 || wav.Length < 44) return wav;
                int pos = 12, bits = 0, format = 0;
                while (pos + 8 <= wav.Length)
                {
                    string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                    int size = BitConverter.ToInt32(wav, pos + 4);
                    if (id == "fmt " && pos + 24 <= wav.Length) { format = BitConverter.ToUInt16(wav, pos + 8); bits = BitConverter.ToUInt16(wav, pos + 22); }
                    if (id == "data")
                    {
                        if (format != 1 || bits != 16) return wav;
                        var copy = (byte[])wav.Clone();
                        int end = (int)Math.Min((long)copy.Length, (long)pos + 8 + (uint)size);
                        double gain = Math.Clamp(volume, 10, 100) / 100.0;
                        for (int i = pos + 8; i + 1 < end; i += 2)
                        {
                            short v = (short)(copy[i] | copy[i + 1] << 8);
                            v = (short)Math.Round(v * gain);
                            copy[i] = (byte)(v & 0xff); copy[i + 1] = (byte)((v >> 8) & 0xff);
                        }
                        return copy;
                    }
                    if (size < 0) break;
                    pos += 8 + size + (size & 1);
                }
            }
            catch { }
            return wav;
        }

        // ---- Sound packs (spec 12b.3 and 19.11): a pack sets the choice for every event ----

        public const string Classic = "Classic";
        public static readonly string[] PackNames = { Classic, "Bunny pack", "Halloween", "Winter holidays", "Valentine's" };

        private static readonly Dictionary<string, string[]> PackSounds = new()
        {
            // In Event order: Startup, TaskDone, DailyGoal, PersonalRecord, StreakExtended, LevelUp, BossDefeated, SprintFinished, SurpriseReward, OverloadWarning.
            ["Bunny pack"] = new[] { "Marimba, rising", "Pizzicato strings, two up", "Marimba, sparkle", "Glockenspiel, sparkle", "Marimba, four up",
                "Glockenspiel, rising", "Marimba, chord high", "Pizzicato strings, bounce", "Glockenspiel, bounce", "Pizzicato strings, sigh" },
            ["Halloween"] = new[] { "Low strings, creep", "Harp, minor rising", "Low strings, minor chord", "Harp, creep", "Harp, minor rising",
                "Strings, minor chord", "Low strings, minor chord", "Harp, minor falling", "Harp, creep", "Low strings, minor sigh" },
            ["Winter holidays"] = new[] { "Bells, rising", "Glockenspiel, two up", "Bells, sparkle", "Bells, sparkle", "Glockenspiel, four up",
                "Bells, chord high", "Bells, rising", "Glockenspiel, bounce", "Glockenspiel, sparkle", "Bells, sigh" },
            ["Valentine's"] = new[] { "Piano and strings, rising", "Soft piano, two up", "Piano and strings, rising", "Soft piano, sparkle", "Soft piano, four up",
                "Strings, chord high", "Piano and strings, chord high", "Soft piano, bounce", "Soft piano, sparkle", "Soft piano, sigh" },
        };

        /// <summary>What a pack picks for an event: Default for Classic, otherwise a sound name.</summary>
        public static string PackChoice(string pack, Event e) =>
            PackSounds.TryGetValue(pack, out var s) && (int)e < s.Length ? s[(int)e] : DefaultName;

        /// <summary>The holiday pack that fits this date, or null: Halloween in October, Winter holidays in December, Valentine's in the first half of February.</summary>
        public static string? SeasonPack(DateTime d) =>
            d.Month == 10 ? "Halloween" : d.Month == 12 ? "Winter holidays" : d.Month == 2 && d.Day <= 14 ? "Valentine's" : null;

        public const string Angelic = "Angelic welcome";

        /// <summary>
        /// The startup sound: high bowed strings swell in on an open major chord, a harp sweeps up
        /// through it, and a glockenspiel shimmers at the top, all in a big airy room. About three seconds.
        /// </summary>
        private static (double[] L, double[] R) AngelicWelcome(int key)
        {
            int home = 54 + ((key - 54 % 12) % 12 + 12) % 12 + 12; // the octave above the patterns: airy, not heavy
            int length = (int)(4.6 * Rate);
            var left = new double[length];
            var right = new double[length];
            void Mix((double[] L, double[] R) note, double at, double gain, double pan, double swell)
            {
                int start = (int)(at * Rate), rise = Math.Max(1, (int)(swell * Rate));
                double gl = Math.Sqrt(0.5 * (1 - pan)) * 1.414, gr = Math.Sqrt(0.5 * (1 + pan)) * 1.414;
                for (int i = 0; i < note.L.Length && start + i < length; i++)
                {
                    double g = gain * (i < rise ? Math.Sin(i / (double)rise * Math.PI / 2) : 1);
                    left[start + i] += note.L[i] * g * gl;
                    right[start + i] += note.R[i] * g * gr;
                }
            }
            // Strings: root, fifth, octave, tenth and the ninth above, swelling in slowly, spread wide.
            int[] chord = { 0, 7, 12, 16, 14 };
            for (int k = 0; k < chord.Length; k++)
                Mix(Note("strings", home + chord[k], 1.15, 0.8), 0.04 * k, 0.55, -0.5 + k / (double)(chord.Length - 1), 0.7);
            // Harp: a sweep up through the chord, two octaves.
            int[] sweep = { 0, 4, 7, 11, 12, 16, 19, 23, 24 };
            for (int k = 0; k < sweep.Length; k++)
                Mix(Note("harp", home + sweep[k], 1.1, 0.8), 0.18 + 0.085 * k, 0.5, -0.4 + 0.8 * k / (sweep.Length - 1), 0.004);
            // Glockenspiel: a soft shimmer at the top as the sweep lands.
            int[] shimmer = { 12, 16, 19, 24 };
            for (int k = 0; k < shimmer.Length; k++)
                Mix(Note("glockenspiel", home + shimmer[k], 0.9, 0.8), 0.95 + 0.11 * k, 0.22, k % 2 == 0 ? -0.35 : 0.35, 0.004);
            Room(left, right, 0.38);
            return (left, right);
        }

        /// <summary>
        /// The update climb: rolled chords rising up the key on one instrument, over soft low strings,
        /// each ringing into the next and fading in the room, landing on a big chord an octave up with a
        /// glockenspiel shimmer. One sound, so nothing is cut off (Windows plays one sound at a time).
        /// </summary>
        private static (double[] L, double[] R) UpdateClimb(int key, Instrument inst)
        {
            int home = 54 + ((key - 54 % 12) % 12 + 12) % 12 + inst.Up;
            int[][] chords =
            {
                new[] { 0, 4, 7 }, new[] { 2, 5, 9 }, new[] { 4, 7, 11 }, new[] { 5, 9, 12 },
                new[] { 7, 11, 14 }, new[] { 9, 12, 16 }, new[] { 5, 9, 12, 14 }, new[] { 7, 11, 14, 17 },
            };
            int[] landing = { 0, 4, 7, 12, 16, 19, 24 };
            const double step = 0.55;
            double end = chords.Length * step;
            int length = (int)((end + 3.4) * Rate);
            var left = new double[length];
            var right = new double[length];
            void Mix((double[] L, double[] R) note, double at, double gain, double pan, double swell)
            {
                int start = (int)(at * Rate), rise = Math.Max(1, (int)(swell * Rate));
                double gl = Math.Sqrt(0.5 * (1 - pan)) * 1.414, gr = Math.Sqrt(0.5 * (1 + pan)) * 1.414;
                for (int i = 0; i < note.L.Length && start + i < length; i++)
                {
                    double g = gain * (i < rise ? Math.Sin(i / (double)rise * Math.PI / 2) : 1);
                    left[start + i] += note.L[i] * g * gl;
                    right[start + i] += note.R[i] * g * gr;
                }
            }
            double ring = Math.Max(inst.Ring, 0.5) + 0.5, release = Math.Max(inst.Release, 0.3) + 0.6;
            double soft = inst.Soft ? 0.7 : 1;
            for (int c = 0; c < chords.Length; c++)
            {
                double at = c * step;
                // The chord rolled upward, a little louder as it climbs.
                for (int k = 0; k < chords[c].Length; k++)
                    Mix(Note(inst.Bank, home + chords[c][k], ring, release), at + 0.04 * k, (0.34 + 0.03 * c) * soft, -0.45 + 0.9 * k / Math.Max(1, chords[c].Length - 1), 0.006);
                // Soft strings underneath: the chord's root an octave down, swelling in.
                Mix(Note("strings", home - 12 + chords[c][0], step + 0.5, 0.6), at, 0.22, 0, 0.18);
            }
            for (int k = 0; k < landing.Length; k++)
                Mix(Note(inst.Bank, home + landing[k], 1.6, 1.4), end + 0.035 * k, 0.4 * soft, -0.6 + 1.2 * k / (landing.Length - 1), 0.006);
            Mix(Note("strings", home - 12, 2.0, 1.2), end, 0.3, -0.3, 0.12);
            Mix(Note("strings", home - 5, 2.0, 1.2), end, 0.24, 0.3, 0.12);
            int[] shimmer = { 24, 28, 31, 36 };
            for (int k = 0; k < shimmer.Length; k++)
                Mix(Note("glockenspiel", home - inst.Up + shimmer[k], 0.9, 0.9), end + 0.25 + 0.09 * k, 0.16, k % 2 == 0 ? -0.4 : 0.4, 0.004);
            Room(left, right, 0.42);
            return (left, right);
        }

        public const string ClimbPrefix = "Update climb, ";

        /// <summary>Plays the update climb on an instrument at a share of the usual volume. Silent when sounds are off.</summary>
        public static void PlayUpdateClimb(string instrument, double share)
        {
            if (!Enabled) return;
            PlayNamed(ClimbPrefix + instrument, Math.Max(1, (int)(Volume * share)));
        }

        /// <summary>How long the update climb lasts, in seconds.</summary>
        public const double ClimbSeconds = 8 * 0.55 + 3.4;

        private static (double[]? L, double[]? R) Make(string name, int key)
        {
            if (name == Angelic) return AngelicWelcome(key);
            if (name.StartsWith(ClimbPrefix) && Instruments.FirstOrDefault(i => i.Name == name[ClimbPrefix.Length..]) is Instrument climber)
                return UpdateClimb(key, climber);
            int comma = name.IndexOf(", ", StringComparison.Ordinal);
            if (comma < 0) return (null, null);
            var inst = Instruments.FirstOrDefault(i => i.Name == name[..comma]);
            if (inst != null && name[(comma + 2)..].StartsWith("note ") && int.TryParse(name[(comma + 7)..], out int step))
                return PatternSound(inst, new[] { (step, 0.0) }, key);
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
