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
    /// <summary>Quick add: a name, then pick menus for the rest. Enter adds it.</summary>
    public static class QuickAdd
    {
        public static TaskItem? Show(Window owner, Store store, string list, Status status)
        {
            var w = Layout.Dialog(owner, "Quick add");
            var name = new TextBox();
            var lists = new ComboBox { ItemsSource = store.Data.Lists.Select(l => l.Name).ToList(), SelectedItem = list };
            var tags = new ComboBox { IsEditable = true, ItemsSource = store.Data.Tasks.Select(t => t.Tag).Where(t => t.Length > 0).Distinct().OrderBy(t => t).ToList() };
            var statuses = new ComboBox { ItemsSource = Enum.GetValues<Status>().Where(s => s != Status.Done).Select(Words.Of).ToList(), SelectedIndex = (int)status };
            var priority = new ComboBox();
            var due = new TextBox();
            AutomationProperties.SetHelpText(due, "Like today, tomorrow, Friday, 10/20, or Friday at 3. Leave it empty for no date.");
            var size = new ComboBox { ItemsSource = Enum.GetValues<Size>().Select(Words.Of).ToList(), SelectedIndex = 1 };
            void FillPriority()
            {
                var scale = store.Data.Lists.FirstOrDefault(l => l.Name == lists.SelectedItem as string)?.Scale ?? Array.Empty<string>();
                priority.ItemsSource = new[] { "None" }.Concat(scale.Select((s, i) => s + " " + (i + 1))).ToList();
                priority.SelectedIndex = 0;
            }
            lists.SelectionChanged += (_, _) => FillPriority();
            FillPriority();

            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("Task _name", name));
            panel.Children.Add(Layout.Row("_List", lists));
            panel.Children.Add(Layout.Row("_Tag", tags));
            panel.Children.Add(Layout.Row("_Status", statuses));
            panel.Children.Add(Layout.Row("_Priority", priority));
            panel.Children.Add(Layout.Row("_Due", due));
            panel.Children.Add(Layout.Row("Si_ze", size));
            var ok = Layout.Button("Add", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = panel;

            TaskItem? made = null;
            ok.Click += (_, _) =>
            {
                if (name.Text.Trim().Length == 0) { Speech.Say(name, "Type a name for the task first.", true); name.Focus(); return; }
                var (when, hasTime) = Dates.Parse(due.Text, DateTime.Now);
                if (due.Text.Trim().Length > 0 && when == null) { Speech.Say(due, "Busy Bunny did not understand that date. Try tomorrow, Friday, or 10/20.", true); due.Focus(); return; }
                string listName = lists.SelectedItem as string ?? list;
                made = new TaskItem
                {
                    Name = name.Text.Trim(),
                    List = listName,
                    Tag = (tags.Text ?? "").Trim().ToUpperInvariant(),
                    Status = (Status)Math.Max(0, statuses.SelectedIndex),
                    Priority = Math.Max(0, priority.SelectedIndex),
                    Due = when,
                    DueHasTime = hasTime,
                    Size = (Size)Math.Max(0, size.SelectedIndex),
                    IsMeeting = listName == "Meetings",
                    StatusSince = DateTime.Now,
                };
                w.DialogResult = true;
            };
            w.Loaded += (_, _) => name.Focus();
            return w.ShowDialog() == true ? made : null;
        }
    }

    /// <summary>Plain-language dates for now: today, tomorrow, a weekday, 10/20, with an optional time like at 3 or 3:30 pm.</summary>
    public static class Dates
    {
        public static (DateTime? When, bool HasTime) Parse(string text, DateTime now)
        {
            string t = text.Trim().ToLowerInvariant();
            if (t.Length == 0) return (null, false);
            int? hour = null, minute = null;
            var m = Regex.Match(t, @"\s(?:at\s+)?(\d{1,2})(?::(\d{2}))?\s?(am|pm|a\.m\.|p\.m\.)?$");
            if (m.Success && (t.Contains(" at ") || m.Groups[2].Success || m.Groups[3].Success))
            {
                int h = int.Parse(m.Groups[1].Value);
                minute = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
                string ap = m.Groups[3].Value;
                if (ap.StartsWith("p") && h < 12) h += 12;
                else if (ap.StartsWith("a") && h == 12) h = 0;
                else if (ap.Length == 0 && h >= 1 && h <= 7) h += 12; // at 3 means 3 in the afternoon for a student
                if (h > 23 || minute > 59) return (null, false);
                hour = h;
                t = t[..m.Index].Trim();
            }
            DateTime? day = null;
            if (t is "today" or "tonight") day = now.Date;
            else if (t is "tomorrow" or "tmrw") day = now.Date.AddDays(1);
            else if (t.Length == 0 && hour != null) day = now.Date;
            else
            {
                string w = t.StartsWith("next ") ? t[5..] : t;
                foreach (DayOfWeek d in Enum.GetValues<DayOfWeek>())
                {
                    string full = d.ToString().ToLowerInvariant();
                    if (w == full || w.Length >= 3 && full.StartsWith(w))
                    {
                        var x = now.Date.AddDays(1);
                        while (x.DayOfWeek != d) x = x.AddDays(1);
                        if (t.StartsWith("next ") && (x - now.Date).Days < 7) x = x.AddDays(7);
                        day = x;
                        break;
                    }
                }
                if (day == null && DateTime.TryParseExact(t, new[] { "M/d", "M/d/yyyy", "M/d/yy", "MMMM d", "MMM d" }, CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.AllowWhiteSpaces, out var parsed))
                {
                    day = parsed.Date;
                    if (!Regex.IsMatch(t, @"\d{4}|/\d{2}$") && day < now.Date.AddDays(-7)) day = day.Value.AddYears(1);
                }
            }
            if (day == null) return (null, false);
            return hour == null ? (day, false) : (day.Value.AddHours(hour.Value).AddMinutes(minute ?? 0), true);
        }
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
            var lists = new Dictionary<Sounds.Event, ComboBox>();
            foreach (Sounds.Event e in Enum.GetValues<Sounds.Event>())
            {
                var items = new List<string> { Sounds.DefaultName, Sounds.RandomName };
                items.AddRange(Sounds.All);
                var list = new ComboBox { ItemsSource = items };
                int at = settings.SoundChoices.TryGetValue(e.ToString(), out var c) ? items.IndexOf(c) : -1;
                list.SelectedIndex = at < 0 ? 0 : at;
                var ev = e;
                list.SelectionChanged += (_, _) => { if (ready && list.SelectedItem is string s) Sounds.PlayNamed(Sounds.Resolve(ev, s)); };
                panel.Children.Add(Layout.Row(Sounds.Label(e), list));
                lists[e] = list;
            }
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
                settings.SoundVolume = 100 - volume.SelectedIndex * 10;
                settings.SoundKey = key.SelectedIndex;
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
            var list = new ListBox { ItemsSource = lines, Width = 720, Height = 420 };
            AutomationProperties.SetName(list, title);
            list.PreviewKeyDown += (_, e) =>
            {
                if (heading == null || Keyboard.Modifiers != ModifierKeys.Control || e.Key is not (Key.Down or Key.Up)) return;
                e.Handled = true;
                int step = e.Key == Key.Down ? 1 : -1;
                for (int i = list.SelectedIndex + step; i >= 0 && i < lines.Count; i += step)
                    if (heading(lines[i])) { Select(list, i); return; }
                Speech.Say(list, step > 0 ? "No more headings." : "This is the first heading.", true);
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
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is ListBoxItem item) item.Focus();
            else list.Focus();
        }
    }
}
