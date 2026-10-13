using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BusyBunny
{
    /// <summary>
    /// Plays every sound through one mixer, so sounds overlap instead of cutting each other off
    /// (Windows' own player plays one at a time), and the background music runs underneath.
    /// If there is no sound device, sounds just stay quiet.
    /// </summary>
    public static class Mixer
    {
        private static WaveOutEvent? _out;
        private static MixingSampleProvider? _mix;
        private static ISampleProvider? _preview;
        private static readonly object Gate = new();
        public static readonly MusicLoop Music = new();

        private static MixingSampleProvider? Mix()
        {
            lock (Gate)
            {
                if (_mix != null) return _mix;
                try
                {
                    var mix = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Sounds.SampleRate, 2)) { ReadFully = true };
                    mix.AddMixerInput(Music);
                    var o = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3 };
                    o.Init(mix);
                    o.Play();
                    _out = o;
                    _mix = mix;
                }
                catch { _out = null; _mix = null; }
                return _mix;
            }
        }

        /// <summary>Plays a WAV. exclusive: stops the previous exclusive one first (previews in the Sounds window).</summary>
        public static bool Play(byte[] wav, bool exclusive)
        {
            var mix = Mix();
            if (mix == null) return false;
            try
            {
                ISampleProvider sp = new WaveFileReader(new MemoryStream(wav)).ToSampleProvider();
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
                if (sp.WaveFormat.SampleRate != Sounds.SampleRate) sp = new WdlResamplingSampleProvider(sp, Sounds.SampleRate);
                lock (Gate)
                {
                    if (exclusive)
                    {
                        if (_preview != null) mix.RemoveMixerInput(_preview);
                        _preview = sp;
                    }
                    mix.AddMixerInput(sp);
                }
                return true;
            }
            catch { return false; }
        }

        /// <summary>Starts the mixer early, so the music can begin and the first sound has no delay.</summary>
        public static void Start() => Mix();
    }

    /// <summary>
    /// The background music: a quiet three-minute piece that loops, made from the same recordings as
    /// every other sound. Soft bowed strings hold slow chords, a harp picks through them, a soft piano
    /// sings a few notes over the top, and a glockenspiel glints now and then, all in a big room.
    /// It fades in while Busy Bunny is the window in front and fades out the moment it is not.
    /// Notes are played straight from the recordings as the piece goes, so it costs almost no memory.
    /// </summary>
    public sealed class MusicLoop : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Sounds.SampleRate, 2);

        public const double LoopSeconds = 180;
        private static int Rate => Sounds.SampleRate;

        /// <summary>Whether it should be heard now: on in settings, sounds on, and Busy Bunny in front.</summary>
        public volatile bool Wanted;
        /// <summary>Its volume, as a share of full scale before the sound volume: 0.10 is 10 percent.</summary>
        public volatile float Level = 0.10f;

        private double _gain; // follows the target smoothly, for the fades
        private long _pos;     // samples into the loop
        private List<Event>? _events;
        private int _nextEvent;
        private readonly List<Voice> _voices = new();
        private readonly Reverb _room = new(Sounds.SampleRate);
        private int _key = -1;

        private sealed record Event(double At, string Bank, int Midi, double Gain, double Pan, double Ring, double Release, double Swell);

        private sealed class Voice
        {
            public double[] L = Array.Empty<double>(), R = Array.Empty<double>();
            public double Ratio, Gain, Gl, Gr;
            public int I, Length, Ring, Release, Swell;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            double target = Wanted ? Level * Math.Clamp(Sounds.Volume, 10, 100) / 100.0 : 0;
            // Nothing to hear and nothing ringing: stay silent and keep the place in the piece.
            if (target == 0 && _gain < 1e-5)
            {
                Array.Clear(buffer, offset, count);
                _gain = 0;
                return count;
            }
            if (_events == null || _key != Sounds.Key) { _events = Compose(Sounds.Key); _key = Sounds.Key; _nextEvent = 0; _voices.Clear(); FindNext(); }
            // About two seconds to fade in or out.
            double step = 1.0 / (Rate * 2.0) * Math.Max(Level, 0.02);
            for (int n = 0; n < count; n += 2)
            {
                // Start every note due now; the piece loops seamlessly because each note keeps its own place.
                while (_nextEvent < _events.Count && _events[_nextEvent].At * Rate <= _pos) Start(_events[_nextEvent++]);
                double l = 0, r = 0;
                for (int v = _voices.Count - 1; v >= 0; v--)
                {
                    var x = _voices[v];
                    if (x.I >= x.Length) { _voices.RemoveAt(v); continue; }
                    double p = x.I * x.Ratio;
                    int k = (int)p;
                    double f = p - k;
                    double g = x.Gain;
                    if (x.I < x.Swell) g *= Math.Sin(x.I / (double)x.Swell * Math.PI / 2);
                    if (x.I > x.Ring) g *= Math.Pow(Math.Cos((x.I - x.Ring) / (double)x.Release * Math.PI / 2), 2);
                    l += (x.L[k] + (x.L[k + 1] - x.L[k]) * f) * g * x.Gl;
                    r += (x.R[k] + (x.R[k + 1] - x.R[k]) * f) * g * x.Gr;
                    x.I++;
                }
                var (wl, wr) = _room.Process(l, r);
                l = l * 0.72 + wl * 1.6;
                r = r * 0.72 + wr * 1.6;
                if (_gain < target) _gain = Math.Min(target, _gain + step);
                else if (_gain > target) _gain = Math.Max(target, _gain - step);
                buffer[offset + n] = (float)Math.Tanh(l * _gain * Calibration);
                buffer[offset + n + 1] = (float)Math.Tanh(r * _gain * Calibration);
                if (++_pos >= (long)(LoopSeconds * Rate)) { _pos = 0; _nextEvent = 0; }
            }
            return count;
        }

        // Brings the piece's loudest moments to about full scale before Level and the volume.
        private const double Calibration = 4.5;

        private void FindNext()
        {
            _nextEvent = 0;
            while (_nextEvent < _events!.Count && _events[_nextEvent].At * Rate < _pos) _nextEvent++;
        }

        private void Start(Event e)
        {
            try
            {
                var (sl, sr, nearest) = Sounds.RecordingFor(e.Bank, e.Midi);
                double ratio = Math.Pow(2, (e.Midi - nearest) / 12.0);
                int ring = (int)(e.Ring * Rate), release = (int)(e.Release * Rate);
                int length = Math.Min(ring + release, (int)((sl.Length - 2) / ratio));
                if (length <= 0) return;
                // A note cut short by its recording still fades: its release starts in time.
                if (length < ring + release) { release = Math.Min(release, length / 2); ring = length - release; }
                _voices.Add(new Voice
                {
                    L = sl, R = sr, Ratio = ratio, Gain = e.Gain, Length = length, Ring = ring, Release = Math.Max(1, release),
                    Swell = Math.Max(1, (int)(e.Swell * Rate)),
                    Gl = Math.Sqrt(0.5 * (1 - e.Pan)) * 1.414, Gr = Math.Sqrt(0.5 * (1 + e.Pan)) * 1.414,
                });
            }
            catch { }
        }

        /// <summary>
        /// The piece: twenty slow chords, nine seconds each, in the sound key. Strings hold each chord,
        /// in two overlapping layers so it never thins out; a harp picks through it; a soft piano sings
        /// two notes over each chord; a glockenspiel glints every fourth chord. The last chord leads back
        /// to the first, so the loop has no seam.
        /// </summary>
        private static List<Event> Compose(int key)
        {
            int home = 54 + ((key - 54 % 12) % 12 + 12) % 12;
            // A chord: its bass (semitones from home) and three tones above.
            (int Bass, int[] Tones) I = (0, new[] { 4, 7, 12 }), vi = (-3, new[] { 4, 9, 12 }), IV = (-7, new[] { 5, 9, 12 }),
                V = (-5, new[] { 2, 7, 11 }), iii = (-8, new[] { 4, 7, 11 }), ii = (-10, new[] { 2, 5, 9 }),
                iv = (-7, new[] { 5, 8, 12 }), Iadd9 = (0, new[] { 2, 7, 12 }), Vsus = (-5, new[] { 2, 7, 12 });
            var chords = new[] { I, vi, IV, V, I, vi, ii, V, IV, I, iii, vi, ii, V, I, Iadd9, IV, iv, I, Vsus };
            // The piano's two notes over each chord, in semitones from home, an octave up.
            int[][] tune =
            {
                new[] { 16, 19 }, new[] { 16, 21 }, new[] { 17, 21 }, new[] { 19, 14 },
                new[] { 19, 16 }, new[] { 21, 19 }, new[] { 17, 14 }, new[] { 14, 11 },
                new[] { 21, 24 }, new[] { 19, 16 }, new[] { 16, 19 }, new[] { 21, 16 },
                new[] { 17, 21 }, new[] { 19, 23 }, new[] { 24, 19 }, new[] { 14, 16 },
                new[] { 17, 21 }, new[] { 20, 17 }, new[] { 16, 12 }, new[] { 14, 19 },
            };
            const double span = LoopSeconds / 20;
            var e = new List<Event>();
            for (int c = 0; c < chords.Length; c++)
            {
                double t = c * span;
                var (bass, tones) = chords[c];
                foreach (double layer in new[] { 0.0, span / 2 })
                {
                    double g = layer == 0 ? 1 : 0.8;
                    e.Add(new(t + layer, "strings", home - 12 + bass, 0.30 * g, 0, span * 0.75, span * 0.45, 2.6));
                    for (int k = 0; k < tones.Length; k++)
                        e.Add(new(t + layer + 0.3 * k, "strings", home + tones[k], 0.15 * g, -0.55 + 0.55 * k, span * 0.7, span * 0.45, 2.8));
                }
                // The harp picks up through the chord and back, a little differently each time.
                int[] pick = { bass + 12, tones[0], tones[1], tones[2], tones[1] + 12, tones[0] + 12 };
                double[] when = { 0.5, 1.9, 3.3, 4.7, 6.1, 7.5 };
                for (int k = 0; k < pick.Length; k++)
                {
                    if ((c + k) % 5 == 4) continue; // a gap now and then, so it breathes
                    e.Add(new(t + when[k], "harp", home + pick[(k + c) % pick.Length], 0.11, k % 2 == 0 ? -0.35 : 0.35, 2.4, 2.0, 0.01));
                }
                e.Add(new(t + 1.4, "piano", home + tune[c][0], 0.075, 0.15, 3.0, 2.6, 0.02));
                e.Add(new(t + 5.6, "piano", home + tune[c][1], 0.065, -0.15, 2.8, 2.6, 0.02));
                if (c % 4 == 2) e.Add(new(t + 3.1, "glockenspiel", home + 24 + tones[2] % 12, 0.045, 0.45, 1.2, 1.6, 0.01));
            }
            return e.OrderBy(x => x.At).ToList();
        }

        /// <summary>Writes the whole piece at full level to a WAV file, for checking it by ear or by numbers.</summary>
        public static void WriteWav(string path, double seconds)
        {
            var m = new MusicLoop { Wanted = true, Level = 1f };
            m._gain = 1;
            using var w = new WaveFileWriter(path, new WaveFormat(Rate, 16, 2));
            var buf = new float[Rate * 2];
            for (int s = 0; s < (int)Math.Ceiling(seconds); s++)
            {
                m.Read(buf, 0, buf.Length);
                foreach (float x in buf) w.WriteSample(x);
            }
        }

        /// <summary>For the self-test: renders some seconds at full level and gives the loudest sample and the average level.</summary>
        public static (double Peak, double Rms) Measure(double seconds, double from = 0)
        {
            var m = new MusicLoop { Wanted = true, Level = 1f };
            m._gain = 1;
            m._pos = (long)(from * Rate);
            var buf = new float[Rate * 2];
            double peak = 0, sum = 0;
            long n = 0;
            for (int s = 0; s < (int)Math.Ceiling(seconds); s++)
            {
                m.Read(buf, 0, buf.Length);
                foreach (float x in buf) { peak = Math.Max(peak, Math.Abs(x)); sum += x * x; n++; }
            }
            return (peak, Math.Sqrt(sum / Math.Max(1, n)));
        }
    }

    /// <summary>The same room as Sounds.Room (Freeverb style), run sample by sample for the music.</summary>
    public sealed class Reverb
    {
        private readonly Channel _l, _r;

        public Reverb(int rate)
        {
            _l = new Channel(rate, 0);
            _r = new Channel(rate, 23);
        }

        public (double L, double R) Process(double l, double r) => (_l.Process(l), _r.Process(r));

        private sealed class Channel
        {
            private readonly double[][] _comb, _pass;
            private readonly int[] _ci, _pi;
            private readonly double[] _store;

            public Channel(int rate, int spread)
            {
                int[] combs = { 1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617 };
                int[] passes = { 556, 441, 341, 225 };
                double scale = rate / 44100.0;
                _comb = combs.Select(c => new double[(int)((c + spread) * scale)]).ToArray();
                _pass = passes.Select(p => new double[(int)((p + spread) * scale)]).ToArray();
                _ci = new int[combs.Length];
                _pi = new int[passes.Length];
                _store = new double[combs.Length];
            }

            public double Process(double input)
            {
                double output = 0;
                for (int c = 0; c < _comb.Length; c++)
                {
                    var buf = _comb[c];
                    double y = buf[_ci[c]];
                    _store[c] = y * 0.6 + _store[c] * 0.4;
                    buf[_ci[c]] = input * 0.015 + _store[c] * 0.84;
                    _ci[c] = (_ci[c] + 1) % buf.Length;
                    output += y;
                }
                for (int p = 0; p < _pass.Length; p++)
                {
                    var buf = _pass[p];
                    double b = buf[_pi[p]];
                    double y = b - output;
                    buf[_pi[p]] = output + b * 0.5;
                    _pi[p] = (_pi[p] + 1) % buf.Length;
                    output = y;
                }
                return output;
            }
        }
    }
}
