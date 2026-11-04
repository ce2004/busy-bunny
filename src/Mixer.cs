using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace BusyBunny
{
    /// <summary>
    /// Plays every sound through one mixer, so sounds overlap instead of cutting each other off
    /// (Windows' own player plays one at a time), and the background music runs underneath.
    /// If there is no sound device, sounds just stay quiet.
    ///
    /// Morgan's issue 13: sounds stopped for good after a while. When Windows stops or stalls the
    /// sound output (sleep, headphones or Bluetooth coming and going, the audio service restarting),
    /// the mixer used to stay dead until Busy Bunny restarted. Now it notices, by the output stopping
    /// or by its position not moving, and starts a fresh one; a check every few seconds brings the music back too.
    ///
    /// Every call into the sound driver (open, play, position, close) happens on one background
    /// thread of its own, never while holding a lock the window's thread takes, so a hung driver can
    /// only make the sounds go quiet, never freeze the app.
    /// </summary>
    public static class Mixer
    {
        private static volatile MixingSampleProvider? _mix;
        private static ISampleProvider? _preview;
        // Short: only the list of waiting sounds and the preview. Never held around a driver call.
        private static readonly object Gate = new();
        private static readonly List<ISampleProvider> Waiting = new();
        public static readonly MusicLoop Music = new();
        private static readonly AutoResetEvent Kick = new(false);
        private static Thread? _thread;
        // Bumped when a hung sound thread is given up on; the old thread notices when (if ever) the driver lets go, and quits.
        private static volatile int _generation;
        private static long _driverSince; // UTC ticks when the sound thread went into the driver, 0 when out
        private static volatile bool _noDevice;
        private static volatile object? _stopped;
        private static long _lastSound = DateTime.UtcNow.Ticks;

        private static void Touch() => Interlocked.Exchange(ref _lastSound, DateTime.UtcNow.Ticks);

        /// <summary>Whether the sound thread has been inside the driver for more than five seconds.</summary>
        private static bool Hung
        {
            get { long t = Interlocked.Read(ref _driverSince); return t != 0 && DateTime.UtcNow.Ticks - t > TimeSpan.TicksPerSecond * 5; }
        }

        private static void EnsureThread()
        {
            lock (Gate)
            {
                long since = Interlocked.Read(ref _driverSince);
                // A driver call stuck for 15 seconds is not coming back soon: leave that thread behind and start a fresh
                // one, so the sounds and the music return without a restart.
                if (_thread != null && since != 0 && DateTime.UtcNow.Ticks - since > TimeSpan.TicksPerSecond * 15)
                {
                    _generation++;
                    _thread = null;
                    try { _mix?.RemoveMixerInput(Music); } catch { }
                    _mix = null;
                    _preview = null;
                    Interlocked.Exchange(ref _driverSince, 0);
                }
                if (_thread != null) return;
                int gen = _generation;
                _thread = new Thread(() => Run(gen)) { IsBackground = true, Name = "Busy Bunny sound output" };
                _thread.Start();
            }
        }

        private static void Run(int gen)
        {
            WaveOutEvent? output = null;
            MixingSampleProvider? mix = null;
            long lastPos = -1;
            var lastMoved = DateTime.UtcNow;
            while (gen == _generation)
            {
                Kick.WaitOne(5000);
                if (gen != _generation) break;
                try
                {
                    bool busy;
                    lock (Gate) busy = Waiting.Count > 0;
                    busy |= Music.Audible || DateTime.UtcNow.Ticks - Interlocked.Read(ref _lastSound) < TimeSpan.TicksPerSecond * 20
                        || (mix != null && mix.MixerInputs.Skip(1).Any());
                    // Issue 14, battery: close the output once nothing has played for 20 seconds.
                    if (!busy) { Close(ref output, ref mix); continue; }

                    bool stuck = output == null || ReferenceEquals(_stopped, output) || output.PlaybackState != PlaybackState.Playing;
                    if (!stuck && output != null)
                    {
                        long pos;
                        Interlocked.Exchange(ref _driverSince, DateTime.UtcNow.Ticks);
                        try { pos = output.GetPosition(); } catch { pos = -2; }
                        finally { if (gen == _generation) Interlocked.Exchange(ref _driverSince, 0); }
                        if (gen != _generation) break;
                        var now = DateTime.UtcNow;
                        if (pos == -2) stuck = true;
                        else if (pos != lastPos) { lastPos = pos; lastMoved = now; }
                        else stuck = (now - lastMoved).TotalSeconds > 2;
                    }
                    if (stuck)
                    {
                        Close(ref output, ref mix);
                        var m = new MixingSampleProvider(WaveFormat.CreateIeeeFloatWaveFormat(Sounds.SampleRate, 2)) { ReadFully = true };
                        m.AddMixerInput(Music);
                        // Made on this thread, which has no dispatcher, so PlaybackStopped never runs on the window's thread.
                        var o = new WaveOutEvent { DesiredLatency = 150, NumberOfBuffers = 3 };
                        o.PlaybackStopped += (s, _) => { _stopped = s; Kick.Set(); };
                        Interlocked.Exchange(ref _driverSince, DateTime.UtcNow.Ticks);
                        bool opened;
                        try { o.Init(m); o.Play(); opened = true; }
                        catch { opened = false; }
                        finally { if (gen == _generation) Interlocked.Exchange(ref _driverSince, 0); }
                        if (gen != _generation)
                        {
                            // Given up on while it hung: let this output go without ever reading the music.
                            try { m.RemoveMixerInput(Music); } catch { }
                            DisposeLater(o);
                            break;
                        }
                        if (!opened)
                        {
                            m.RemoveMixerInput(Music);
                            DisposeLater(o);
                            _noDevice = true;
                            lock (Gate) Waiting.Clear();
                            continue;
                        }
                        _noDevice = false;
                        output = o;
                        mix = m;
                        lastPos = -1;
                        lastMoved = DateTime.UtcNow;
                        lock (Gate) { _mix = m; _preview = null; }
                    }
                    lock (Gate)
                    {
                        if (mix != null) foreach (var w in Waiting) mix.AddMixerInput(w);
                        Waiting.Clear();
                    }
                }
                catch { Close(ref output, ref mix); }
            }
            // Replaced by a fresh thread: the music leaves this mix, so the two outputs never both read it.
            Close(ref output, ref mix);
        }

        /// <summary>Lets go of the output: the music leaves the old mix first, so two outputs never read it at once, and the close happens in the background.</summary>
        private static void Close(ref WaveOutEvent? output, ref MixingSampleProvider? mix)
        {
            if (mix != null)
            {
                lock (Gate) { if (ReferenceEquals(_mix, mix)) { _mix = null; _preview = null; } }
                try { mix.RemoveMixerInput(Music); } catch { }
            }
            if (output != null) DisposeLater(output);
            output = null;
            mix = null;
        }

        private static void DisposeLater(WaveOutEvent o) => ThreadPool.QueueUserWorkItem(_ => { try { o.Dispose(); } catch { } });

        /// <summary>Plays a WAV. exclusive: stops the previous exclusive one first (previews in the Sounds window). False when there is no output to use.</summary>
        public static bool Play(byte[] wav, bool exclusive)
        {
            Touch();
            EnsureThread();
            if (Hung || _noDevice && _mix == null) { Kick.Set(); return false; }
            try
            {
                ISampleProvider sp = new WaveFileReader(new MemoryStream(wav)).ToSampleProvider();
                if (sp.WaveFormat.Channels == 1) sp = new MonoToStereoSampleProvider(sp);
                if (sp.WaveFormat.SampleRate != Sounds.SampleRate) sp = new WdlResamplingSampleProvider(sp, Sounds.SampleRate);
                lock (Gate)
                {
                    var mix = _mix;
                    if (exclusive)
                    {
                        if (_preview != null) { mix?.RemoveMixerInput(_preview); Waiting.Remove(_preview); }
                        _preview = sp;
                    }
                    if (mix != null) { mix.AddMixerInput(sp); }
                    else
                    {
                        if (Waiting.Count >= 8) Waiting.RemoveAt(0);
                        Waiting.Add(sp);
                    }
                }
                Kick.Set();
                return true;
            }
            catch { return false; }
        }

        /// <summary>Starts the mixer early, so the music can begin and the first sound has no delay. Never waits for the driver.</summary>
        public static void Start()
        {
            EnsureThread();
            Kick.Set();
        }

        /// <summary>Opens the output again when the music is wanted (Busy Bunny came to the front). Only asks the sound thread; never waits.</summary>
        public static void Wake()
        {
            Touch();
            EnsureThread();
            Kick.Set();
        }
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
        /// <summary>Wanted, or still fading out.</summary>
        public bool Audible => Wanted || _gain > 0.0001;
        /// <summary>Its volume, as a share of full scale before the sound volume: 0.10 is 10 percent.</summary>
        public volatile float Level = 0.10f;

        private double _gain; // follows the target smoothly, for the fades
        private long _pos;     // samples into the loop
        private List<Event>? _events;
        private int _nextEvent;
        private readonly List<Voice> _voices = new();
        private readonly Reverb _room = new(Sounds.SampleRate);
        private int _key = -1, _mood;

        /// <summary>Which arrangement plays (the seasonal soundtrack sets it): 0 is the usual piece. A change starts the new one at the same place.</summary>
        public volatile int Mood;
        /// <summary>For the seasonal soundtrack: rearranges the usual piece for a mood (events, mood, home note). Null or 0 plays it as written.</summary>
        public static Func<List<Event>, int, int, List<Event>>? Arranger;

        public sealed record Event(double At, string Bank, int Midi, double Gain, double Pan, double Ring, double Release, double Swell);

        private sealed class Voice
        {
            public short[] L = Array.Empty<short>(), R = Array.Empty<short>();
            public double Ratio, Gain, Gl, Gr;
            public int I, Length, Ring, Release, Swell;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            // Never let the music break the mixer: on any problem, a moment of silence and a fresh start.
            try { return ReadMusic(buffer, offset, count); }
            catch
            {
                Array.Clear(buffer, offset, count);
                _voices.Clear();
                _events = null;
                return count;
            }
        }

        private int ReadMusic(float[] buffer, int offset, int count)
        {
            double target = Wanted ? Level * Math.Clamp(Sounds.Volume, 10, 100) / 100.0 : 0;
            // Nothing to hear and nothing ringing: stay silent and keep the place in the piece.
            if (target == 0 && _gain < 1e-5)
            {
                Array.Clear(buffer, offset, count);
                _gain = 0;
                return count;
            }
            if (_events == null || _key != Sounds.Key || _mood != Mood) { _mood = Mood; _events = Compose(Sounds.Key, _mood); _key = Sounds.Key; _nextEvent = 0; _voices.Clear(); FindNext(); }
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
                    double g = x.Gain * Sounds.ToUnit;
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
        /// in two overlapping layers so it never thins out; singing bells ring through it; a soft piano sings
        /// two notes over each chord; a flute breathes a long note every fourth chord. The last chord leads back
        /// to the first, so the loop has no seam.
        /// </summary>
        private static List<Event> Compose(int key, int mood = 0)
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
                    e.Add(new(t + layer, "cello", home - 12 + bass, 0.30 * g, 0, span * 0.75, span * 0.45, 2.6));
                    e.Add(new(t + layer + 0.5, "organ", home + bass + 12, 0.07 * g, 0.2, span * 0.7, span * 0.45, 3.0));
                    for (int k = 0; k < tones.Length; k++)
                        e.Add(new(t + layer + 0.3 * k, "strings", home + tones[k], 0.15 * g, -0.55 + 0.55 * k, span * 0.7, span * 0.45, 2.8));
                }
                // The harp picks up through the chord and back, a little differently each time.
                int[] pick = { bass + 12, tones[0], tones[1], tones[2], tones[1] + 12, tones[0] + 12 };
                double[] when = { 0.5, 1.9, 3.3, 4.7, 6.1, 7.5 };
                for (int k = 0; k < pick.Length; k++)
                {
                    if ((c + k) % 5 == 4) continue; // a gap now and then, so it breathes
                    if (k % 2 == 1) continue; // the bells ring long: three to a chord is plenty
                    e.Add(new(t + when[k], "bells", home + 12 + pick[(k + c) % pick.Length], 0.07, k % 4 == 0 ? -0.4 : 0.4, 3.4, 2.4, 0.01));
                }
                e.Add(new(t + 1.4, "piano", home + tune[c][0], 0.075, 0.15, 3.0, 2.6, 0.02));
                e.Add(new(t + 5.6, "piano", home + tune[c][1], 0.065, -0.15, 2.8, 2.6, 0.02));
                if (c % 4 == 2) e.Add(new(t + 3.1, "flute", home + 12 + tones[2], 0.05, 0.45, 3.0, 2.4, 1.2));
            }
            var piece = e.OrderBy(x => x.At).ToList();
            if (mood == 0 || Arranger == null) return piece;
            try { return Arranger(piece, mood, home).OrderBy(x => x.At).ToList(); }
            catch { return piece; }
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
        public static (double Peak, double Rms) Measure(double seconds, double from = 0, int mood = 0)
        {
            var m = new MusicLoop { Wanted = true, Level = 1f, Mood = mood };
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
