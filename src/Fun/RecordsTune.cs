using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// Your own victory tune (sweep 8.4): up to 8 notes of the major scale from a pick menu, on one of the sampled
    /// instruments, played back, and saved as the sound for a finished task. The tune is mixed from the app's own
    /// single notes (Sounds.WavOf "Piano, note 4") into a WAV in her Sounds folder, picked like any sound she imported.
    /// </summary>
    public sealed class RecordsTune : IFeature
    {
        public const string Id = "records.tune", FileName = "My victory tune";
        public const int MaxNotes = 8;
        public const double Gap = 0.22;
        private MainWindow _w = null!;

        public sealed class Tune { public List<int> Notes { get; set; } = new() { 0, 4, 7, 12 }; public string Instrument { get; set; } = "Piano"; public bool Saved { get; set; } }

        /// <summary>The notes she can pick: scale steps above the home note of her sound key, and a rest.</summary>
        public static readonly (string Name, int Step)[] Notes =
        {
            ("Do, the home note", 0), ("Re", 2), ("Mi", 4), ("Fa", 5), ("Sol", 7), ("La", 9), ("Ti", 11),
            ("High do", 12), ("High re", 14), ("High mi", 16), ("High sol", 19), ("A rest, a short pause", -1),
        };

        public static string NoteName(int step) => Notes.FirstOrDefault(n => n.Step == step).Name ?? "a note";

        public void Attach(MainWindow w)
        {
            _w = w;
            Tools.Register(new BunnyTool(Id, "Your own victory tune", Drawers.Wins, "Make a little tune of up to 8 notes and use it when you finish a task.", Show) { Since = "0.14.0" });
        }

        /// <summary>Mixes the tune into one WAV (16-bit stereo), or null when no note could be made.</summary>
        public static byte[]? Render(IReadOnlyList<int> notes, string instrument)
        {
            int rate = Sounds.SampleRate;
            var parts = new List<(int At, short[] Pcm)>();
            for (int k = 0; k < notes.Count && k < MaxNotes; k++)
            {
                if (notes[k] < 0) continue;
                if (Sounds.WavOf(instrument + ", note " + notes[k]) is not byte[] wav || wav.Length <= 44) continue;
                var pcm = new short[(wav.Length - 44) / 2];
                Buffer.BlockCopy(wav, 44, pcm, 0, pcm.Length * 2);
                parts.Add(((int)(k * Gap * rate) * 2, pcm));
            }
            if (parts.Count == 0) return null;
            int length = parts.Max(p => p.At + p.Pcm.Length);
            var mix = new double[length];
            foreach (var (at, pcm) in parts) for (int i = 0; i < pcm.Length; i++) mix[at + i] += pcm[i];
            double peak = Math.Max(1, mix.Max(Math.Abs));
            double gain = Math.Min(1, 0.6 * 32767 / peak);
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);
            bw.Write("RIFF"u8); bw.Write(36 + length * 2); bw.Write("WAVE"u8);
            bw.Write("fmt "u8); bw.Write(16); bw.Write((short)1); bw.Write((short)2); bw.Write(rate); bw.Write(rate * 4); bw.Write((short)4); bw.Write((short)16);
            bw.Write("data"u8); bw.Write(length * 2);
            foreach (double v in mix) bw.Write((short)Math.Clamp(Math.Round(v * gain), -32768, 32767));
            bw.Flush();
            return ms.ToArray();
        }

        private static void PlayWav(byte[] wav)
        {
            if (!Sounds.Enabled) return;
            var scaled = Sounds.ScaleWav(wav, Sounds.Volume);
            if (Mixer.Play(scaled, exclusive: true)) return;
            try { new System.Media.SoundPlayer(new MemoryStream(scaled)).Play(); } catch { }
        }

        /// <summary>Saves the tune into her Sounds folder and makes it the finished-task sound. Returns the sound's name, or null.</summary>
        private string? SaveAsDoneSound(byte[] wav, out string error)
        {
            error = "";
            string folder = Path.Combine(Path.GetTempPath(), "Busy Bunny tune " + Environment.ProcessId);
            try
            {
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, FileName + ".wav");
                File.WriteAllBytes(file, wav);
                var name = Sounds.Import(file, out error);
                if (name == null) return null;
                _w.AppSettings.SoundChoices[Sounds.Event.TaskDone.ToString()] = name;
                _w.AppSettings.Save();
                _w.AppSettings.Apply();
                return name;
            }
            catch (Exception e) { error = e.Message; return null; }
            finally { try { Directory.Delete(folder, true); } catch { } }
        }

        private void Show()
        {
            var s = _w.Store;
            var tune = s.World.Get<Tune>(Id);
            var dlg = Layout.Dialog(_w, "Your own victory tune");
            var panel = new StackPanel { Margin = new Thickness(14), Width = 460 };
            panel.Children.Add(new TextBlock { Text = "Add up to " + MaxNotes + " notes, pick an instrument, play it, then save it as the sound for a finished task.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
            var list = new ListBox { Height = 150 };
            AutomationProperties.SetHelpText(list, "Delete takes the note you are on out.");
            var pick = new ComboBox { ItemsSource = Notes.Select(n => n.Name).ToList(), SelectedIndex = 0 };
            var inst = new ComboBox { ItemsSource = Sounds.InstrumentNames.ToList() };
            inst.SelectedIndex = Math.Max(0, Sounds.InstrumentNames.ToList().IndexOf(tune.Instrument));
            void Fill(int select)
            {
                list.Items.Clear();
                for (int k = 0; k < tune.Notes.Count; k++) list.Items.Add(new ListBoxItem { Content = (k + 1) + ": " + NoteName(tune.Notes[k]) });
                if (list.Items.Count == 0) list.Items.Add(new ListBoxItem { Content = "No notes yet" });
                list.SelectedIndex = Math.Clamp(select, 0, list.Items.Count - 1);
                AutomationProperties.SetName(list, "Your tune, " + GamesFunKit.Plural(tune.Notes.Count, "note"));
            }
            void Keep() { tune.Instrument = inst.SelectedItem as string ?? "Piano"; s.World.Set(Id, tune); s.World.Save(); }
            Fill(0);
            panel.Children.Add(Layout.Row("Your _tune", list, 460));
            panel.Children.Add(Layout.Row("_Note to add", pick, 300));
            var add = Layout.Button("_Add the note");
            add.Click += (_, _) =>
            {
                if (tune.Notes.Count >= MaxNotes) { Speech.Say(dlg, "That is " + MaxNotes + " notes, the most a tune can have. Delete one to add another.", true); return; }
                int step = Notes[Math.Max(0, pick.SelectedIndex)].Step;
                tune.Notes.Add(step);
                Keep();
                Fill(tune.Notes.Count - 1);
                if (step >= 0) Sounds.PlayNamed((inst.SelectedItem as string ?? "Piano") + ", note " + step);
                Speech.Say(dlg, "Added " + NoteName(step) + ", note " + tune.Notes.Count + " of " + MaxNotes + ".", true);
            };
            list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Delete || tune.Notes.Count == 0 || list.SelectedIndex < 0 || list.SelectedIndex >= tune.Notes.Count) return;
                e.Handled = true;
                int at = list.SelectedIndex;
                string gone = NoteName(tune.Notes[at]);
                tune.Notes.RemoveAt(at);
                Keep();
                Fill(at);
                TextViewer.Select(list, Math.Min(at, list.Items.Count - 1));
                Speech.Say(dlg, "Took out " + gone + ". " + GamesFunKit.Plural(tune.Notes.Count, "note") + " left.", true);
            };
            panel.Children.Add(add);
            panel.Children.Add(Layout.Row("_Instrument", inst, 300));
            inst.SelectionChanged += (_, _) => Keep();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var play = Layout.Button("_Play it");
            play.Click += (_, _) =>
            {
                if (!Sounds.Enabled) { Speech.Say(dlg, "Sounds are off. Turn them on in Options, Sounds to hear it.", true); return; }
                if (Render(tune.Notes, inst.SelectedItem as string ?? "Piano") is byte[] wav) PlayWav(wav);
                else Speech.Say(dlg, "Add a note first.", true);
            };
            var save = Layout.Button("_Save as my task-done sound");
            save.Click += (_, _) =>
            {
                if (Render(tune.Notes, inst.SelectedItem as string ?? "Piano") is not byte[] wav) { Speech.Say(dlg, "Add a note first.", true); return; }
                if (SaveAsDoneSound(wav, out string error) == null) { Speech.Say(dlg, "Could not save it: " + error, true); return; }
                tune.Saved = true;
                Keep();
                Speech.Say(dlg, "Saved. Finishing a task now plays your tune. A list with its own sound pack still plays the pack's sound.", true);
            };
            var usual = Layout.Button("Use the _usual sound");
            usual.Click += (_, _) =>
            {
                _w.AppSettings.SoundChoices.Remove(Sounds.Event.TaskDone.ToString());
                _w.AppSettings.Save();
                _w.AppSettings.Apply();
                tune.Saved = false;
                Keep();
                Speech.Say(dlg, "Finishing a task plays the usual sound again. Your tune is kept here.", true);
            };
            row.Children.Add(play);
            row.Children.Add(save);
            panel.Children.Add(row);
            var row2 = new StackPanel { Orientation = Orientation.Horizontal };
            row2.Children.Add(usual);
            row2.Children.Add(Layout.Button("Close", isCancel: true));
            panel.Children.Add(row2);
            dlg.Content = panel;
            dlg.Loaded += (_, _) => TextViewer.Select(list, 0);
            dlg.ShowDialog();
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("victory tune: " + what); }
            Need(Notes.Count(n => n.Step >= 0) >= 8, "fewer than 8 notes to pick from");
            var one = Render(new[] { 0 }, "Piano");
            var tune = Render(new[] { 0, 4, -1, 7, 12 }, "Piano");
            Need(one != null && tune != null && tune.Length > one.Length, "the tune did not render longer than one note");
            Need(Render(new[] { -1, -1 }, "Piano") == null, "a tune of rests made a sound");
            Need(Render(Enumerable.Repeat(0, 20).ToList(), "Flute")!.Length <= Render(Enumerable.Repeat(0, MaxNotes).ToList(), "Flute")!.Length, "more than 8 notes were played");
            Need(store.World.Get<Tune>(Id).Notes.Count is > 0 and <= MaxNotes, "the starting tune");
        }
    }
}
