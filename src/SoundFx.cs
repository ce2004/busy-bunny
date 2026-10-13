using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// Themed sounds for the tab sound packs (Morgan's issue 6): bunny hops and nibbles, space blips
    /// and launches, coffee clinks and pours, and a gas stove lighting. Made here from sine waves and
    /// filtered noise rather than recordings, a few hundredths of a second at a time, then put in the
    /// same little room as the instruments. Each is the same every time (fixed random seeds).
    /// </summary>
    public static partial class Sounds
    {
        /// <summary>The list setting that picks the pack fitting its priority scale.</summary>
        public const string MatchScale = "Match the priority scale";

        /// <summary>The pack that fits a priority scale: Coffee, Space, Stove for Burner and Heat, Bunny for Bunny. Null for the rest.</summary>
        public static string? PackForScale(string scaleName) => scaleName switch
        {
            "Coffee" => "Coffee",
            "Space" => "Space",
            "Burner" or "Heat" => "Stove",
            "Bunny" => "Bunny pack",
            _ => null,
        };

        /// <summary>The pack a list's tasks use, or null for the sounds chosen everywhere.</summary>
        public static string? ListPack(TaskList? l) =>
            l == null || l.SoundPack.Length == 0 ? null : l.SoundPack == MatchScale ? PackForScale(l.ScaleName) : l.SoundPack;

        /// <summary>How a list's pack reads in Settings and View tab settings.</summary>
        public static string ListPackWords(TaskList l)
        {
            if (l.SoundPack.Length == 0) return "Same as everywhere";
            if (l.SoundPack != MatchScale) return l.SoundPack;
            return PackForScale(l.ScaleName) is string p ? MatchScale + ", " + p : MatchScale + ", none fits " + (l.ScaleName.Length == 0 ? "no scale" : l.ScaleName) + " so the usual sounds";
        }

        /// <summary>The sound an event plays for a task on a list: its pack's sound, or the one chosen everywhere.</summary>
        public static string ListSound(Event e, TaskList? list)
        {
            if (ListPack(list) is not string pack) return Resolve(e, Choice(e));
            string s = PackChoice(pack, e);
            return s == DefaultName ? Default(e) : s;
        }

        // ---- The sounds ----

        private static readonly Dictionary<string, Func<(double[] L, double[] R)>> Effects = new()
        {
            ["Bunny, hop"] = () => Fx(0.45, 0.12, m => Hop(m, 0, 1.0)),
            ["Bunny, two hops"] = () => Fx(0.6, 0.12, m => { Hop(m, 0, 1.0); Hop(m, 0.17, 1.5); }),
            ["Bunny, binky"] = () => Fx(1.6, 0.2, m =>
            {
                Hop(m, 0, 1.0); Hop(m, 0.14, 1.26); Hop(m, 0.28, 1.5);
                int[] up = { 84, 88, 91, 96 };
                for (int k = 0; k < up.Length; k++) m.Add(Note("bells", up[k], 0.5, 0.6), 0.42 + 0.06 * k, 0.32, -0.4 + 0.8 * k / 3);
            }),
            ["Bunny, nibble"] = () => Fx(0.5, 0.06, m =>
            {
                double[] at = { 0, 0.065, 0.115, 0.19, 0.24 };
                for (int k = 0; k < at.Length; k++) Crunch(m, at[k], 0.7 + 0.3 * ((k * 37) % 5) / 4.0, 1000 + k);
            }, drive: 3),
            ["Bunny, thump"] = () => Fx(0.6, 0.1, m => { Thud(m, 0, 140, 45, 0.09, 1.0); Thud(m, 0.22, 140, 45, 0.09, 0.9); }),

            ["Space, blip"] = () => Fx(0.25, 0.25, m => Blip(m, 0, 1320)),
            ["Space, two blips"] = () => Fx(0.35, 0.25, m => { Blip(m, 0, 1320); Blip(m, 0.08, 1760); }),
            ["Space, launch"] = () => Fx(1.0, 0.22, m =>
            {
                m.Noise(0, 0.9, t => Ramp(t, 0.35, 0.5) * 0.9, _ => 110, 0.7, 0, 7);
                m.Tone(0.05, 0.75, t => 180 * Math.Pow(10, t / 0.75) * (1 + 0.01 * Math.Sin(2 * Math.PI * 7 * t)), t => Ramp(t, 0.2, 0.3) * 0.5, t => -0.5 + t / 0.75, 0.15);
            }),
            ["Space, beam"] = () => Fx(0.8, 0.3, m =>
            {
                double[] f = { 880, 1108.7, 1318.5, 1760 };
                for (int k = 0; k < f.Length; k++)
                {
                    double fk = f[k];
                    m.Tone(0.045 * k, 0.6, t => fk * (1 + 0.03 * t), t => Decay(t, 0.004, 0.22) * 0.3 * (0.6 + 0.4 * Math.Sin(2 * Math.PI * 18 * t)), _ => -0.45 + 0.3 * k, 0);
                }
            }),
            ["Space, orbit"] = () => Fx(1.0, 0.3, m =>
            {
                foreach (var (f, g) in new[] { (660.0, 0.4), (663.0, 0.4), (990.0, 0.22) })
                    m.Tone(0, 0.9, _ => f, t => Ramp(t, 0.1, 0.55) * g, t => 0.8 * Math.Sin(2 * Math.PI * 1.4 * t), 0);
            }),
            ["Space, pew"] = () => Fx(0.35, 0.2, m => m.Tone(0, 0.2, t => 2400 * Math.Pow(0.125, t / 0.2), t => Decay(t, 0.002, 0.09) * 0.6, _ => 0, 0.3)),
            ["Space, warble"] = () => Fx(0.6, 0.15, m =>
            {
                for (int k = 0; k < 4; k++)
                {
                    double f = k % 2 == 0 ? 520 : 390;
                    m.Tone(0.11 * k, 0.1, _ => f, t => Decay(t, 0.008, 0.06) * 0.4, _ => 0, 0.1);
                }
            }),

            ["Coffee, clink"] = () => Fx(0.4, 0.14, m => Clink(m, 0, 1.0, 1.0)),
            ["Coffee, two clinks"] = () => Fx(0.5, 0.14, m => { Clink(m, 0, 1.0, 1.0); Clink(m, 0.11, 1.06, 0.75); }),
            ["Coffee, stir"] = () => Fx(0.75, 0.14, m =>
            {
                double[] f = { 1.0, 1.04, 0.98, 1.06, 1.02 };
                for (int k = 0; k < f.Length; k++) Clink(m, 0.09 * k, f[k], 0.45 + 0.1 * (k % 2));
            }),
            ["Coffee, pour"] = () => Fx(1.0, 0.12, m =>
            {
                var rng = new Random(11);
                double wobble = 0;
                m.Noise(0, 0.72, t => { wobble += (rng.NextDouble() - 0.5) * 0.02; wobble *= 0.995; return Ramp(t, 0.05, 0.12) * (0.75 + 0.25 * Math.Sin(2 * Math.PI * 23 * t) + wobble); },
                    t => 350 * Math.Pow(4, t / 0.72), 4, 0, 12);
                Clink(m, 0.74, 1.0, 0.6);
            }),
            ["Coffee, percolate"] = () => Fx(0.9, 0.12, m =>
            {
                var rng = new Random(21);
                for (int k = 0; k < 11; k++) Bubble(m, 0.06 * k + rng.NextDouble() * 0.03, 600 + rng.NextDouble() * 800, 0.025 + rng.NextDouble() * 0.02, 0.5 + rng.NextDouble() * 0.3, rng.NextDouble() - 0.5);
            }),
            ["Coffee, steam"] = () => Fx(0.75, 0.1, m => m.Noise(0, 0.65, t => Ramp(t, 0.06, 0.25) * 0.5, _ => 5000, 0.7, 0, 31)),

            ["Stove, click"] = () => Fx(0.3, 0.08, m => { Click(m, 0); Click(m, 0.07); }, drive: 5),
            ["Stove, light"] = () => Fx(1.0, 0.12, m =>
            {
                Click(m, 0); Click(m, 0.11);
                m.Noise(0.2, 0.7, t => Decay(t, 0.03, 0.22) * 1.0, t => 250 + 500 * Math.Min(1, t / 0.15), 0.8, 0, 41);
                Thud(m, 0.2, 110, 70, 0.08, 0.5);
            }),
            ["Stove, whoosh"] = () => Fx(0.8, 0.15, m => m.Noise(0, 0.65, t => Math.Sin(Math.PI * Math.Min(1, t / 0.65)) * 0.8, t => 300 * Math.Pow(8, Math.Sin(Math.PI * Math.Min(1, t / 0.65))), 1.2, 0, 51, t => -0.6 + 1.2 * t / 0.65)),
            ["Stove, sizzle"] = () => Fx(0.75, 0.08, m =>
            {
                m.Noise(0, 0.6, t => Ramp(t, 0.02, 0.3) * 0.35, _ => 4500, 0.5, 0, 61);
                var rng = new Random(62);
                for (int k = 0; k < 18; k++) m.Noise(rng.NextDouble() * 0.55, 0.012, t => Decay(t, 0.0005, 0.003) * (0.6 + rng.NextDouble() * 0.4), _ => 3000 + rng.NextDouble() * 3000, 2, rng.NextDouble() - 0.5, 63 + k);
            }),
            ["Stove, simmer"] = () => Fx(0.9, 0.12, m =>
            {
                m.Noise(0, 0.8, t => Ramp(t, 0.1, 0.3) * 0.12, _ => 3500, 0.6, 0, 71);
                var rng = new Random(72);
                for (int k = 0; k < 6; k++) Bubble(m, 0.12 * k + rng.NextDouble() * 0.05, 160 + rng.NextDouble() * 180, 0.05 + rng.NextDouble() * 0.03, 0.8, rng.NextDouble() * 0.6 - 0.3);
            }),
        };

        // Every sound is made as loud as it can go, so pure tones and steady noise came out much louder
        // than the instruments (about 20 dB below full at their loudest moment). These bring them in line.
        private static readonly Dictionary<string, double> FxTrim = new()
        {
            ["Space, blip"] = 0.47, ["Space, two blips"] = 0.6, ["Space, launch"] = 0.62, ["Space, beam"] = 0.72, ["Space, orbit"] = 0.51,
            ["Space, pew"] = 0.52, ["Space, warble"] = 0.67, ["Bunny, thump"] = 0.54, ["Bunny, hop"] = 0.82, ["Bunny, two hops"] = 0.82,
            ["Bunny, binky"] = 0.83, ["Coffee, steam"] = 0.88, ["Stove, whoosh"] = 0.83,
        };

        // ---- Pieces ----

        /// <summary>A hop: a soft thud on the grass and a little rising chirp. pitch scales the chirp.</summary>
        private static void Hop(FxMix m, double at, double pitch)
        {
            Thud(m, at, 120, 60, 0.05, 0.7);
            m.Noise(at, 0.05, t => Decay(t, 0.002, 0.012) * 0.25, _ => 700, 0.7, 0, (int)(at * 1000) + 1);
            m.Tone(at + 0.01, 0.09, t => 500 * pitch * Math.Pow(1.8, t / 0.09), t => Decay(t, 0.006, 0.04) * 0.35, _ => -0.15 + 0.3 * Math.Min(1, at * 4), 0);
        }

        /// <summary>A crunch, like a bite of carrot: a burst of bright noise over a duller one.</summary>
        private static void Crunch(FxMix m, double at, double gain, int seed)
        {
            m.Noise(at, 0.035, t => Decay(t, 0.001, 0.009) * gain, _ => 3000, 1.2, 0.1, seed);
            m.Noise(at, 0.03, t => Decay(t, 0.001, 0.008) * gain * 0.6, _ => 900, 1.5, -0.1, seed + 500);
        }

        /// <summary>A low thud: a sine falling in pitch, dying away.</summary>
        private static void Thud(FxMix m, double at, double from, double to, double tau, double gain) =>
            m.Tone(at, tau * 5, t => to + (from - to) * Math.Exp(-t / 0.03), t => Decay(t, 0.002, tau) * gain, _ => 0, 0);

        /// <summary>A computer blip: a short pure tone with a touch of edge.</summary>
        private static void Blip(FxMix m, double at, double f) =>
            m.Tone(at, 0.07, _ => f, t => (t < 0.002 ? t / 0.002 : 1) * (t > 0.055 ? Math.Max(0, (0.07 - t) / 0.015) : 1) * 0.4, _ => 0, 0.15);

        /// <summary>A spoon on a china cup: a few out-of-tune partials that ring briefly.</summary>
        private static void Clink(FxMix m, double at, double pitch, double gain)
        {
            foreach (var (f, g, tau) in new[] { (2350.0, 1.0, 0.12), (3410.0, 0.6, 0.08), (5160.0, 0.4, 0.05), (6830.0, 0.25, 0.035), (1180.0, 0.2, 0.1) })
                m.Tone(at, tau * 5, _ => f * pitch, t => Decay(t, 0.0008, tau) * g * gain * 0.3, _ => 0.1, 0);
        }

        /// <summary>A bubble popping up: a quick rising pitch that dies at once.</summary>
        private static void Bubble(FxMix m, double at, double f, double length, double gain, double pan) =>
            m.Tone(at, length * 2, t => f * (1 + 0.8 * Math.Min(1, t / length)), t => Decay(t, 0.002, length * 0.45) * gain * 0.4, _ => pan, 0);

        /// <summary>The igniter's click: a sharp tick ringing for an instant.</summary>
        private static void Click(FxMix m, double at)
        {
            m.Noise(at, 0.03, t => Decay(t, 0.0002, 0.005), _ => 3500, 6, 0, (int)(at * 1000) + 81);
            m.Noise(at, 0.01, t => Decay(t, 0.0001, 0.0008) * 0.5, _ => 8000, 1, 0, (int)(at * 1000) + 91);
        }

        private static double Decay(double t, double attack, double tau) => t < attack ? t / attack : Math.Exp(-(t - attack) / tau);

        /// <summary>Up over rise seconds, then dying away, by fall seconds for each step of the way down.</summary>
        private static double Ramp(double t, double rise, double fall) => Math.Min(1, t / rise) * Math.Exp(-Math.Max(0, t - rise) / Math.Max(0.01, fall));

        /// <summary>Makes a sound. drive above 1 rounds off the sharpest peaks, so short clicks and crunches are heard as well as the rest.</summary>
        private static (double[] L, double[] R) Fx(double seconds, double room, Action<FxMix> build, double drive = 1)
        {
            var m = new FxMix((int)(seconds * Rate));
            build(m);
            if (drive > 1)
            {
                double peak = 1e-9;
                for (int i = 0; i < m.L.Length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(m.L[i]), Math.Abs(m.R[i])));
                for (int i = 0; i < m.L.Length; i++) { m.L[i] = Math.Tanh(drive * m.L[i] / peak); m.R[i] = Math.Tanh(drive * m.R[i] / peak); }
            }
            if (room > 0) Room(m.L, m.R, room);
            return (m.L, m.R);
        }

        /// <summary>A stereo buffer that pieces are added into.</summary>
        private sealed class FxMix
        {
            public readonly double[] L, R;
            public FxMix(int length) { L = new double[length]; R = new double[length]; }

            private void Put(int i, double v, double pan)
            {
                if (i < 0 || i >= L.Length) return;
                pan = Math.Clamp(pan, -1, 1);
                L[i] += v * Math.Sqrt(0.5 * (1 - pan)) * 1.414;
                R[i] += v * Math.Sqrt(0.5 * (1 + pan)) * 1.414;
            }

            /// <summary>Adds an instrument note (from Note), at a time, gain and pan.</summary>
            public void Add((double[] L, double[] R) note, double at, double gain, double pan)
            {
                int start = (int)(at * Rate);
                double gl = Math.Sqrt(0.5 * (1 - pan)) * 1.414, gr = Math.Sqrt(0.5 * (1 + pan)) * 1.414;
                for (int i = 0; i < note.L.Length && start + i < L.Length; i++)
                {
                    L[start + i] += note.L[i] * gain * gl;
                    R[start + i] += note.R[i] * gain * gr;
                }
            }

            /// <summary>A sine whose pitch, loudness and pan follow functions of time; edge adds odd harmonics for a brighter, buzzier tone.</summary>
            public void Tone(double at, double seconds, Func<double, double> freq, Func<double, double> amp, Func<double, double> pan, double edge)
            {
                int start = (int)(at * Rate), n = (int)(seconds * Rate);
                double phase = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)Rate;
                    phase += 2 * Math.PI * freq(t) / Rate;
                    double v = Math.Sin(phase) + edge * (Math.Sin(3 * phase) / 3 + Math.Sin(5 * phase) / 5);
                    // A tiny fade at the very end so nothing clicks off.
                    double tail = Math.Min(1, (n - i) / (0.003 * Rate));
                    Put(start + i, v * amp(t) * tail, pan(t));
                }
            }

            /// <summary>Noise through a band-pass filter whose centre follows a function of time.</summary>
            public void Noise(double at, double seconds, Func<double, double> amp, Func<double, double> centre, double q, double pan, int seed, Func<double, double>? panOver = null)
            {
                var rng = new Random(seed);
                int start = (int)(at * Rate), n = (int)(seconds * Rate);
                double x1 = 0, x2 = 0, y1 = 0, y2 = 0, b0 = 0, b2 = 0, a1 = 0, a2 = 0;
                for (int i = 0; i < n; i++)
                {
                    double t = i / (double)Rate;
                    if (i % 32 == 0)
                    {
                        double w0 = 2 * Math.PI * Math.Clamp(centre(t), 20, Rate * 0.45) / Rate;
                        double alpha = Math.Sin(w0) / (2 * q), a0 = 1 + alpha;
                        b0 = alpha / a0; b2 = -alpha / a0; a1 = -2 * Math.Cos(w0) / a0; a2 = (1 - alpha) / a0;
                    }
                    double x = rng.NextDouble() * 2 - 1;
                    double y = b0 * x + b2 * x2 - a1 * y1 - a2 * y2;
                    x2 = x1; x1 = x; y2 = y1; y1 = y;
                    double tail = Math.Min(1, (n - i) / (0.003 * Rate));
                    Put(start + i, y * amp(t) * tail * 2.5, panOver?.Invoke(t) ?? pan);
                }
            }
        }

        /// <summary>For the self-test: every themed sound is made, has sound in it, and every pack names only real sounds.</summary>
        public static void FxSelfTest()
        {
            foreach (var (name, make) in Effects)
            {
                var (l, r) = make();
                double peak = l.Concat(r).Max(Math.Abs);
                if (l.Length == 0 || peak < 1e-4 || double.IsNaN(peak)) throw new Exception("the sound " + name + " is silent");
            }
            foreach (var p in PackNames)
                foreach (Event e in Enum.GetValues<Event>())
                    if (PackChoice(p, e) is string s && s != DefaultName && !All.Contains(s)) throw new Exception(p + " names a sound that does not exist: " + s);
            var coffee = new TaskList { ScaleName = "Coffee", SoundPack = MatchScale };
            if (ListSound(Event.TaskDone, coffee) != "Coffee, pour") throw new Exception("matching the Coffee scale did not pick the Coffee pack");
            if (ListPack(new TaskList { ScaleName = "Importance", SoundPack = MatchScale }) != null) throw new Exception("a scale with no pack should use the usual sounds");
        }
    }
}
