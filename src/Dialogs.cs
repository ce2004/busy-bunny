using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// Update available: how far behind this copy is, then the changelog of every version it is
    /// missing, newest first, a line each (Control Down and Up jump between versions). Install now or Later.
    /// </summary>
    public static class UpdateDialog
    {
        public static bool Show(Window owner, Updater.Release r, string behind)
        {
            var w = Layout.Dialog(owner, "Update available");
            var lines = new List<string> { "Busy Bunny " + r.Version + " is out. " + behind + " You have " + Updater.Current + "." };
            foreach (var (v, notes) in r.Missed)
            {
                lines.Add("Version " + v);
                lines.AddRange(notes.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            }
            var list = TextViewer.LinesList(lines, "What's new", 380);
            list.PreviewKeyDown += (_, e) =>
            {
                if (Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.Down or Key.Up)) return;
                e.Handled = true;
                int step = e.Key == Key.Down ? 1 : -1;
                for (int i = list.SelectedIndex + step; i >= 0 && i < lines.Count; i += step)
                    if (lines[i].StartsWith("Version ")) { TextViewer.Select(list, i); return; }
                Speech.Say(w, step > 0 ? "No more versions." : "This is the first version.", true);
            };
            var install = Layout.Button("_Install now", isDefault: true);
            var later = Layout.Button("_Later", isCancel: true);
            install.Click += (_, _) => w.DialogResult = true;
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(install);
            buttons.Children.Add(later);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(list);
            panel.Children.Add(new TextBlock { Text = "Busy Bunny restarts by itself and keeps all your tasks.", Margin = new Thickness(0, 8, 0, 0) });
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) => TextViewer.Select(list, 0);
            return w.ShowDialog() == true;
        }
    }

    /// <summary>Plain-language dates, typos forgiven: see Language.ParseDate.</summary>
    public static class Dates
    {
        public static (DateTime? When, bool HasTime) Parse(string text, DateTime now) => Language.ParseDate(text, now);
    }

    /// <summary>
    /// Sounds: which sound each event makes, like Kova's Sounds window. One pick menu per event:
    /// Default, Random sound, None, then every sound. Arrowing through a menu plays each one.
    /// </summary>
    public static class SoundsDialog
    {
        public static void Show(Window owner, Settings settings)
        {
            var w = Layout.Dialog(owner, "Sounds");
            int keyBefore = Sounds.Key, volumeBefore = Sounds.Volume;
            bool ready = false, saved = false;
            var on = new CheckBox { Content = "_Play sounds", IsChecked = settings.SoundsOn, Margin = new Thickness(0, 0, 0, 8) };
            var volume = new ComboBox { ItemsSource = Enumerable.Range(1, 10).Select(i => (110 - i * 10) + " percent").ToList() };
            volume.SelectedIndex = Math.Clamp((100 - settings.SoundVolume) / 10, 0, 9);
            var key = new ComboBox { ItemsSource = Sounds.Keys, SelectedIndex = Math.Clamp(settings.SoundKey, 0, 11) };
            volume.SelectionChanged += (_, _) => { if (!ready) return; Sounds.Volume = 100 - volume.SelectedIndex * 10; Sounds.PlayNamed(Sounds.Default(Sounds.Event.TaskDone)); };
            key.SelectionChanged += (_, _) => { if (!ready) return; Sounds.Key = key.SelectedIndex; Sounds.PlayNamed(Sounds.Default(Sounds.Event.TaskDone)); };

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(on);
            panel.Children.Add(Layout.Row("_Volume", volume));
            panel.Children.Add(Layout.Row("_Key", key));
            var music = new CheckBox { Content = "Background _music while Busy Bunny is in front", IsChecked = settings.MusicOn, Margin = new Thickness(0, 6, 0, 4) };
            var musicVolume = new ComboBox { ItemsSource = new[] { 3, 5, 10, 15, 20, 30 }.Select(v => v + " percent").ToList() };
            musicVolume.SelectedIndex = Math.Max(0, Array.IndexOf(new[] { 3, 5, 10, 15, 20, 30 }, settings.MusicVolume) is int mi && mi >= 0 ? mi : 2);
            panel.Children.Add(music);
            panel.Children.Add(Layout.Row("Music v_olume", musicVolume));
            var pack = new ComboBox { ItemsSource = Sounds.PackNames };
            pack.SelectedIndex = Math.Max(0, Array.IndexOf(Sounds.PackNames, settings.SoundPack));
            panel.Children.Add(Layout.Row("Sound pac_k (sets every sound below)", pack));
            var add = Layout.Button("Add my own sound _file...");
            add.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(add);
            var lists = new Dictionary<Sounds.Event, ComboBox>();
            bool settingPack = false;
            foreach (Sounds.Event e in Enum.GetValues<Sounds.Event>())
            {
                var items = new System.Collections.ObjectModel.ObservableCollection<string>(new[] { Sounds.DefaultName, Sounds.RandomName, Sounds.NoSound }.Concat(Sounds.Choices()));
                var list = new ComboBox { ItemsSource = items };
                string? c = settings.SoundChoices.TryGetValue(e.ToString(), out var saved0) ? saved0 : null;
                if (c == "None") c = Sounds.NoSound;
                int at = c == null ? -1 : items.IndexOf(c);
                list.SelectedIndex = at < 0 ? 0 : at;
                var ev = e;
                list.SelectionChanged += (_, _) => { if (ready && !settingPack && list.SelectedItem is string s) Sounds.PlayNamed(Sounds.Resolve(ev, s)); };
                panel.Children.Add(Layout.Row(Sounds.Label(e), list));
                lists[e] = list;
            }
            pack.SelectionChanged += (_, _) =>
            {
                if (!ready || pack.SelectedItem is not string p) return;
                settingPack = true;
                foreach (var (e, list) in lists)
                {
                    int at = list.Items.IndexOf(Sounds.PackChoice(p, e));
                    list.SelectedIndex = at < 0 ? 0 : at;
                }
                settingPack = false;
                Sounds.PlayNamed(Sounds.Resolve(Sounds.Event.TaskDone, (string)lists[Sounds.Event.TaskDone].SelectedItem));
                Speech.Say(w, p + " pack picked. OK keeps it.");
            };
            add.Click += (_, _) =>
            {
                var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Add my own sound", Filter = "WAV sound files (*.wav)|*.wav" };
                if (dlg.ShowDialog(w) != true) return;
                var name = Sounds.Import(dlg.FileName, out var problem);
                if (name == null) { Speech.Say(w, problem, true); return; }
                foreach (var list in lists.Values)
                    if (list.ItemsSource is System.Collections.ObjectModel.ObservableCollection<string> oc && !oc.Contains(name)) oc.Add(name);
                Speech.Say(w, "Added " + name["My sound, ".Length..] + ". Pick it from any sound's menu.", true);
            };
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80, Focusable = false };

            ok.Click += (_, _) =>
            {
                settings.SoundsOn = on.IsChecked == true;
                settings.MusicOn = music.IsChecked == true;
                settings.MusicVolume = new[] { 3, 5, 10, 15, 20, 30 }[Math.Max(0, musicVolume.SelectedIndex)];
                settings.SoundVolume = 100 - volume.SelectedIndex * 10;
                settings.SoundKey = key.SelectedIndex;
                settings.SoundPack = pack.SelectedItem as string ?? Sounds.Classic;
                settings.SoundChoices = new Dictionary<string, string>();
                foreach (var (e, list) in lists)
                    if (list.SelectedItem is string s && s != Sounds.DefaultName) settings.SoundChoices[e.ToString()] = s;
                settings.Save();
                settings.Apply();
                saved = true;
                w.DialogResult = true;
            };
            w.Closed += (_, _) => { if (!saved) { Sounds.Key = keyBefore; Sounds.Volume = volumeBefore; } };
            w.Loaded += (_, _) => { on.Focus(); ready = true; };
            w.ShowDialog();
        }
    }

    /// <summary>Something to read, one line per item: arrows go through it, Control Down and Up jump between headings.</summary>
    public static class TextViewer
    {
        public static void Show(Window owner, string title, IReadOnlyList<string> lines, Func<string, bool>? heading = null)
        {
            var w = Layout.Dialog(owner, title);
            w.ResizeMode = ResizeMode.CanResize;
            // Each line its own item: plain strings that repeat (two "Bug fixed" lines alike) made the arrows jump back to the first one.
            var list = LinesList(lines, title, 420);
            list.PreviewKeyDown += (_, e) =>
            {
                if (heading == null || Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.Down or Key.Up)) return;
                e.Handled = true;
                int step = e.Key == Key.Down ? 1 : -1;
                for (int i = list.SelectedIndex + step; i >= 0 && i < lines.Count; i += step)
                    if (heading(lines[i])) { Select(list, i); return; }
                Speech.Say(w, step > 0 ? "No more headings." : "This is the first heading.", true);
            };
            var ok = Layout.Button("OK", isDefault: true, isCancel: true);
            ok.Click += (_, _) => w.Close();
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(list);
            panel.Children.Add(ok);
            w.Content = panel;
            w.Loaded += (_, _) => Select(list, 0);
            w.ShowDialog();
        }

        public static void Select(ListBox list, int i)
        {
            if (list.Items.Count == 0) { list.Focus(); return; }
            i = Math.Clamp(i, 0, list.Items.Count - 1);
            list.SelectedIndex = i;
            list.ScrollIntoView(list.Items[i]);
            list.UpdateLayout();
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item) { item.Focus(); return; }
            // Not drawn yet (far down a long list): focus it once the list has caught up. Focusing the
            // list instead left the arrows starting from the old line, so Control Down seemed to do nothing.
            list.Focus();
            list.Dispatcher.BeginInvoke(() =>
            {
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem later) later.Focus();
            }, System.Windows.Threading.DispatcherPriority.Loaded);
        }

        /// <summary>A list of lines to read: every line made up front (they are few), so any line can take focus at once.</summary>
        public static ListBox LinesList(IEnumerable<string> lines, string name, double height)
        {
            var list = new ListBox { Width = 720, Height = height };
            VirtualizingPanel.SetIsVirtualizing(list, false);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            foreach (var l in lines)
            {
                var item = new ListBoxItem { Content = new TextBlock { Text = l, TextWrapping = TextWrapping.Wrap, MaxWidth = 680 } };
                AutomationProperties.SetName(item, l);
                list.Items.Add(item);
            }
            AutomationProperties.SetName(list, name);
            if (list.Items.Count > 0) list.SelectedIndex = 0;
            return list;
        }
    }
}
