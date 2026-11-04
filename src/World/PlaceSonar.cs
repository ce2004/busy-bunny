using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>
    /// Sounds the places make from the app's own real recordings (the bells, xylophone and cello banks), retuned and panned,
    /// played through the app's mixer alongside other sounds: the sonar ping, hoofbeats on different ground, an engine rumble,
    /// and instrument notes that may overlap. Silent when sounds are off.
    /// </summary>
    public static class PlaceAudio
    {
        private static readonly Dictionary<(string, int), (float[] L, float[] R, int Midi)> _recs = new();

        private static (float[] L, float[] R, int Midi) Rec(string bank, int midi)
        {
            lock (_recs)
            {
                if (_recs.TryGetValue((bank, midi), out var have)) return have;
                var (l, r, m) = Sounds.RecordingFor(bank, midi);
                var got = (l.Select(x => (float)(x * Sounds.ToUnit)).ToArray(), r.Select(x => (float)(x * Sounds.ToUnit)).ToArray(), m);
                return _recs[(bank, midi)] = got;
            }
        }

        /// <summary>Adds one retuned note into a stereo buffer: at a time, for a length, panned (minus 1 left to 1 right), with a soft low-pass (1 is none).</summary>
        public static void Note(double[] left, double[] right, string bank, int midi, double at, double seconds, double pan, double gain, double tone = 1, double cents = 0)
        {
            var (sl, sr, rec) = Rec(bank, midi);
            if (sl.Length < 4) return;
            int rate = Sounds.SampleRate;
            double ratio = Math.Pow(2, (midi - rec + cents / 100.0) / 12.0);
            int start = (int)(at * rate), n = (int)(seconds * rate);
            double gl = Math.Sqrt(0.5 * (1 - pan)) * 1.2, gr = Math.Sqrt(0.5 * (1 + pan)) * 1.2;
            double lpL = 0, lpR = 0;
            for (int i = 0; i < n && start + i < left.Length; i++)
            {
                double pos = i * ratio;
                int p = (int)pos;
                if (p + 1 >= sl.Length) break;
                double f = pos - p;
                double env = Math.Exp(-4.0 * i / n) * Math.Min(1, i / (0.004 * rate));
                double l = sl[p] + (sl[p + 1] - sl[p]) * f, r = sr[p] + (sr[p + 1] - sr[p]) * f;
                lpL += tone * (l - lpL); lpR += tone * (r - lpR);
                left[start + i] += lpL * env * gain * gl;
                right[start + i] += lpR * env * gain * gr;
            }
        }

        /// <summary>A stereo buffer as a 16-bit WAV at the app's volume.</summary>
        public static byte[] Wav(double[] left, double[] right, double loudness = 0.5)
        {
            int length = left.Length, rate = Sounds.SampleRate;
            double peak = 1e-9;
            for (int i = 0; i < length; i++) peak = Math.Max(peak, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
            double gain = loudness / peak * Math.Clamp(Sounds.Volume, 5, 100) / 100.0;
            using var ms = new MemoryStream();
            using var w = new BinaryWriter(ms);
            w.Write("RIFF"u8); w.Write(36 + length * 4); w.Write("WAVE"u8);
            w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)2); w.Write(rate); w.Write(rate * 4); w.Write((short)4); w.Write((short)16);
            w.Write("data"u8); w.Write(length * 4);
            int fade = Math.Min(length / 4, rate / 50);
            for (int i = 0; i < length; i++)
            {
                double f = i > length - fade ? (length - i) / (double)fade : 1;
                w.Write((short)Math.Clamp(Math.Round(left[i] * gain * f * 32767), -32768, 32767));
                w.Write((short)Math.Clamp(Math.Round(right[i] * gain * f * 32767), -32768, 32767));
            }
            w.Flush();
            return ms.ToArray();
        }

        /// <summary>Makes and plays a sound in the background, mixed with anything already playing.</summary>
        public static void Play(Func<byte[]?> make)
        {
            if (!Sounds.Enabled) return;
            System.Threading.ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (make() is byte[] wav) Mixer.Play(wav, exclusive: false); }
                catch (Exception e) { ErrorLog.Write("Place sound failed: " + e.Message); }
            });
        }

        /// <summary>The sonar ping: a glass bell, higher when nearer (closeness 0 far to 1 on top of it), panned left or right.</summary>
        public static byte[] PingWav(double closeness, double pan)
        {
            int rate = Sounds.SampleRate;
            double len = 0.45;
            var l = new double[(int)(len * rate)];
            var r = new double[l.Length];
            int midi = 72 + (int)Math.Round(Math.Clamp(closeness, 0, 1) * 19);
            Note(l, r, "bells", midi, 0, len, Math.Clamp(pan, -1, 1), 1.0);
            return Wav(l, r, 0.32 + 0.12 * closeness);
        }

        public static void Ping(double closeness, double pan) => Play(() => PingWav(closeness, pan));

        /// <summary>A soft bump off the reef wall: a low muffled xylophone knock.</summary>
        public static void Bump(double pan) => Play(() =>
        {
            int rate = Sounds.SampleRate;
            var l = new double[rate / 4]; var r = new double[l.Length];
            Note(l, r, "xylo", 48, 0, 0.2, pan, 1, tone: 0.08);
            return Wav(l, r, 0.3);
        });

        /// <summary>Hoofbeats, four strides of a walk, sounding like the ground: hard ground clops, soft ground thuds, a creek splashes.</summary>
        public static byte[] HoovesWav(string ground)
        {
            int rate = Sounds.SampleRate;
            double seconds = 3.2;
            var l = new double[(int)(seconds * rate)]; var r = new double[l.Length];
            bool hard = ground is "gravel" or "bridge" or "dirt";
            bool soft = ground is "grass" or "sand" or "snow" or "leaves";
            double tone = ground switch { "bridge" => 0.9, "gravel" => 0.6, "dirt" => 0.25, "grass" => 0.07, "leaves" => 0.15, "sand" => 0.05, "snow" => 0.04, _ => 0.12 };
            int midi = ground == "bridge" ? 60 : hard ? 52 : 45;
            var rng = new Random(ground.Length);
            for (int stride = 0; stride < 4; stride++)
                for (int foot = 0; foot < 4; foot++)
                {
                    double at = stride * 0.75 + foot * 0.17 + rng.NextDouble() * 0.02;
                    double pan = foot % 2 == 0 ? -0.35 : 0.35;
                    Note(l, r, "xylo", midi + rng.Next(-1, 2), at, soft ? 0.14 : 0.09, pan, soft ? 0.7 : 1, tone);
                    if (ground == "creek") Note(l, r, "bells", 84 + rng.Next(0, 5), at + 0.02, 0.12, -pan, 0.15, 0.3);
                    if (ground == "gravel") Note(l, r, "bells", 90, at + 0.01, 0.05, pan, 0.08, 0.5);
                }
            return Wav(l, r, 0.4);
        }

        public static void Hooves(string ground) => Play(() => HoovesWav(ground));

        /// <summary>An engine rumble: a low cello note, shaken fast, rising as the throttle opens, then settling.</summary>
        public static byte[] RumbleWav()
        {
            int rate = Sounds.SampleRate;
            double seconds = 2.2;
            var l = new double[(int)(seconds * rate)]; var r = new double[l.Length];
            for (int k = 0; k < 6; k++) Note(l, r, "cello", 36 + (k % 2) * 7, k * 0.33, 0.6, 0, 0.8, 0.15, cents: k * 12);
            for (int i = 0; i < l.Length; i++)
            {
                double t = i / (double)rate, shake = 0.55 + 0.45 * Math.Sin(2 * Math.PI * (14 + 6 * Math.Min(1, t)) * t);
                l[i] *= shake; r[i] *= shake;
            }
            return Wav(l, r, 0.45);
        }

        public static void Rumble() => Play(RumbleWav);

        /// <summary>A note on an instrument from the app's sound names (like "Cello, note 7"), mixed so quick notes overlap.</summary>
        public static void Instrument(string soundName) => Play(() => Sounds.WavOf(soundName) is byte[] w ? Sounds.ScaleWav(w, Sounds.Volume) : null);
    }

    /// <summary>
    /// Sonar diving at the beach cove (sweep 12.1, item 9): she swims a grid with the arrow keys; a ping comes again and again,
    /// rising in pitch and coming faster as she gets nearer the treasure, heard to the left or right of her. Enter grabs.
    /// There is no time limit and nothing to lose. Finds go into the treasure chest.
    /// </summary>
    public static class Sonar
    {
        public const int Size = 11;
        public const string Bag = "place.sonar";

        /// <summary>How near (0 far to 1 on it) and which side (minus 1 left, 1 right) the treasure is from a spot.</summary>
        public static (double Closeness, double Pan) Hear(int x, int y, int tx, int ty)
        {
            double d = Math.Sqrt((x - tx) * (x - tx) + (y - ty) * (y - ty));
            double max = Math.Sqrt(2) * (Size - 1);
            return (1 - Math.Min(1, d / max), Math.Clamp((tx - x) / 4.0, -1, 1));
        }

        /// <summary>Seconds between pings: slow far away, quick near.</summary>
        public static double Gap(double closeness) => 1.6 - 1.3 * Math.Clamp(closeness, 0, 1);

        public sealed class Dives { public int Count { get; set; } public int Best { get; set; } }

        /// <summary>Records a find in the chest, as its own undo step. Returns its name.</summary>
        public static string Keep(Store s, int seed)
        {
            var all = PlaceUi.Items("place.treasures");
            var t = all.Count == 0 ? new Dictionary<string, string> { ["name"] = "A smooth sea glass pebble", ["touch"] = "Frosted and cool." } : all[seed % all.Count];
            s.Remember("keeping a sonar find");
            Treasure.Add(s, t.Get("name"), PlaceUi.Feel(t), "sonar diving at the cove");
            s.Save();
            return t.Get("name") + ". " + PlaceUi.Feel(t);
        }

        public static void Show(Window owner, Store s)
        {
            var rng = new Random();
            int x = Size / 2, y = Size - 1, tx, ty, moves = 0;
            do { tx = rng.Next(Size); ty = rng.Next(Size - 2); } while (Math.Abs(tx - x) + Math.Abs(ty - y) < 5);
            var w = Layout.Dialog(owner, "Sonar diving");
            var sea = new ContentControl
            {
                Focusable = true, IsTabStop = true, Width = 420, Height = 120,
                Content = new TextBlock { Text = "Arrows swim. Enter grabs what is under you. H gives a hint. Escape swims back up. No hurry: there is no timer.", TextWrapping = TextWrapping.Wrap },
            };
            AutomationProperties.SetName(sea, "Sonar diving. Arrows swim, Enter grabs, H for a hint, Escape swims up.");
            var timer = new DispatcherTimer();
            void PingNow() { var (c, p) = Hear(x, y, tx, ty); PlaceAudio.Ping(c, p); timer.Interval = TimeSpan.FromSeconds(Gap(c)); }
            timer.Tick += (_, _) => PingNow();
            bool found = false;
            sea.PreviewKeyDown += (_, e) =>
            {
                int dx = e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0, dy = e.Key == Key.Up ? -1 : e.Key == Key.Down ? 1 : 0;
                if (dx != 0 || dy != 0)
                {
                    e.Handled = true;
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= Size || ny >= Size) { PlaceAudio.Bump(dx); Speech.Say(w, ny >= Size ? "The sandy bottom." : "The reef wall.", true); return; }
                    x = nx; y = ny; moves++;
                    timer.Stop(); PingNow(); timer.Start();
                }
                else if (e.Key == Key.H)
                {
                    e.Handled = true;
                    var (c, _) = Hear(x, y, tx, ty);
                    string side = tx < x ? "to your left" : tx > x ? "to your right" : "straight";
                    string ahead = ty < y ? "ahead" : ty > y ? "behind you" : "level with you";
                    Speech.Say(w, (c > 0.85 ? "Very warm, " : c > 0.6 ? "Warm, " : c > 0.35 ? "Cool, " : "Cold, ") + side + " and " + ahead + ".", true);
                }
                else if (e.Key is Key.Enter or Key.Space)
                {
                    e.Handled = true;
                    if (x == tx && y == ty)
                    {
                        timer.Stop();
                        found = true;
                        PlaceUi.Play("Singing bells, sparkle");
                        var d = s.World.GetKept<Dives>(Bag);
                        string what = Keep(s, d.Count + rng.Next(1000));
                        d.Count++;
                        int score = Math.Max(1, 100 - moves);
                        if (score > d.Best) d.Best = score;
                        s.World.SetKept(Bag, d);
                        s.World.Save();
                        Arcade.Score(s, "Sonar diving", score);
                        PlaceEvents.RaiseDid(PlaceIds.Cove, "dive");
                        Speech.Say(w, "Found it: " + what + " It is in the treasure chest. Escape swims back up, or Enter dives again.", true);
                    }
                    else if (found)
                    {
                        found = false; moves = 0;
                        do { tx = rng.Next(Size); ty = rng.Next(Size); } while (Math.Abs(tx - x) + Math.Abs(ty - y) < 5);
                        Speech.Say(w, "A new dive. Listen for the ping.", true);
                        PingNow(); timer.Start();
                    }
                    else Speech.Say(w, "Just sand here. Keep listening for the ping.", true);
                }
                else if (e.Key == Key.Escape) { e.Handled = true; w.Close(); }
            };
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(sea);
            var done = Layout.Button("Swim back up", isCancel: true);
            done.Click += (_, _) => w.Close();
            panel.Children.Add(done);
            w.Content = panel;
            w.Loaded += (_, _) => { sea.Focus(); PingNow(); timer.Start(); };
            w.Closed += (_, _) => timer.Stop();
            w.ShowDialog();
        }

        public static void SelfTest()
        {
            var far = Hear(0, 10, 10, 0);
            var near = Hear(5, 5, 6, 5);
            if (!(near.Closeness > far.Closeness && Gap(near.Closeness) < Gap(far.Closeness))) throw new Exception("sonar: nearer is not higher and quicker");
            if (!(Hear(5, 5, 1, 5).Pan < 0 && Hear(5, 5, 9, 5).Pan > 0)) throw new Exception("sonar: left and right are swapped");
            if (PlaceAudio.PingWav(0.5, -1).Length < 1000 || PlaceAudio.HoovesWav("creek").Length < 1000 || PlaceAudio.RumbleWav().Length < 1000)
                throw new Exception("sonar: a place sound could not be made from the recordings");
        }
    }
}
