using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Options, Settings (Morgan's issue 4): one window, a Section pick menu at the top. General first,
    /// then a section for each list in tab order (its name, its place among the tabs, its priority
    /// scale), then Add a new list. Nothing changes until OK; Cancel forgets it all; Control Z in the
    /// main window undoes the lists' changes.
    /// </summary>
    public static class SettingsWindow
    {
        private const string GeneralName = "General";
        private const string AddName = "Add a new list";
        private const string NoneName = "None";
        private const string CustomName = "Custom";

        private sealed class Pending
        {
            public string? Original; // null for a list added here
            public string Name = "";
            public string ScaleName = "";
            public string[] Levels = Array.Empty<string>();
        }

        /// <summary>Shows Settings, opened at a list's section when one is given. True when OK was pressed.</summary>
        public static bool Show(Window owner, Store store, Settings settings, string? startList = null)
        {
            var w = Layout.Dialog(owner, "Settings");
            var pages = Features.All.SelectMany(f => f.SettingsPages(store, settings)).ToList();
            int P = pages.Count;
            var lists = store.Data.Lists.Select(l => new Pending { Original = l.Name, Name = l.Name, ScaleName = l.Scale.Length == 0 ? "" : l.ScaleName, Levels = l.Scale }).ToList();

            // ---- General ----
            var soundsOn = new CheckBox { Content = "_Play sounds", IsChecked = settings.SoundsOn, Margin = new Thickness(0, 4, 0, 4) };
            var soundsButton = Layout.Button("Choose s_ounds...");
            soundsButton.HorizontalAlignment = HorizontalAlignment.Left;
            soundsButton.Click += (_, _) => { SoundsDialog.Show(w, settings); soundsOn.IsChecked = settings.SoundsOn; };
            var shortMode = new CheckBox { Content = "S_hort mode: shorter task lines", IsChecked = settings.ShortMode, Margin = new Thickness(0, 10, 0, 4) };
            var autoUpdate = new CheckBox { Content = "Check for _updates automatically", IsChecked = settings.CheckForUpdatesAtStart, Margin = new Thickness(0, 4, 0, 4) };
            var hours = new ComboBox { ItemsSource = Enumerable.Range(1, 14).Select(h => h + (h == 1 ? " hour" : " hours")).ToList() };
            hours.SelectedIndex = Math.Clamp((int)Math.Round(store.Data.HoursAvailable) - 1, 0, 13);
            var warn = new ComboBox { ItemsSource = new[] { "Never" }.Concat(Enumerable.Range(3, 13).Select(n => "More than " + n)).ToList() };
            warn.SelectedIndex = settings.UrgentWarnAbove <= 0 ? 0 : Math.Clamp(settings.UrgentWarnAbove - 2, 1, 13);
            var general = new StackPanel();
            general.Children.Add(soundsOn);
            general.Children.Add(soundsButton);
            general.Children.Add(shortMode);
            general.Children.Add(autoUpdate);
            general.Children.Add(Layout.Row("Hours _available on a typical day", hours));
            general.Children.Add(Layout.Row("_Warn me when urgent tasks are", warn));

            // ---- A list ----
            var name = new TextBox();
            var position = new ComboBox();
            var scales = new ComboBox { ItemsSource = new[] { NoneName }.Concat(Scales.Library.Select(s => s.Name)).Append(CustomName).ToList() };
            var levels = new TextBox { TextWrapping = TextWrapping.Wrap, IsReadOnlyCaretVisible = true };
            var customName = new TextBox();
            AutomationProperties.SetHelpText(position, "1 is the first list tab, after Today, Focus and Upcoming.");
            AutomationProperties.SetHelpText(levels, "For a custom scale, type the levels highest first, separated by commas.");
            var listPanel = new StackPanel();
            listPanel.Children.Add(Layout.Row("List _name", name));
            listPanel.Children.Add(Layout.Row("Place among the _tabs", position));
            listPanel.Children.Add(Layout.Row("Priority _scale", scales));
            listPanel.Children.Add(Layout.Row("Le_vels, highest first", levels, 420));
            listPanel.Children.Add(Layout.Row("Custom scale na_me", customName));

            var section = new ComboBox();
            var body = new ContentControl { Focusable = false, MinHeight = 300 };
            Pending? current = null;
            bool loading = false;

            List<string> SectionNames() => new[] { GeneralName }.Concat(pages.Select(p => p.Name)).Concat(lists.Select(l => l.Name + " list")).Append(AddName).ToList();
            void FillSections(int select)
            {
                loading = true;
                section.ItemsSource = SectionNames();
                section.SelectedIndex = select;
                loading = false;
            }
            bool IsCustom() => scales.SelectedItem as string == CustomName;
            void ShowLevels()
            {
                if (current == null) return;
                levels.IsReadOnly = !IsCustom();
                levels.Text = IsCustom() ? string.Join(", ", current.Levels) : Scales.Rank(current.Levels);
                customName.IsEnabled = IsCustom();
            }
            void LoadList(Pending p)
            {
                loading = true;
                current = p;
                name.Text = p.Name;
                position.ItemsSource = Enumerable.Range(1, lists.Count).Select(i => i + " of " + lists.Count).ToList();
                position.SelectedIndex = lists.IndexOf(p);
                scales.SelectedItem = p.Levels.Length == 0 ? NoneName
                    : Scales.Library.Any(s => s.Name == p.ScaleName && s.Levels.SequenceEqual(p.Levels)) ? p.ScaleName : CustomName;
                customName.Text = scales.SelectedItem as string == CustomName ? p.ScaleName : "";
                ShowLevels();
                loading = false;
            }
            void ShowSection()
            {
                int i = section.SelectedIndex;
                if (i <= 0) { current = null; body.Content = general; return; }
                if (i <= P) { current = null; body.Content = pages[i - 1].Content; return; }
                i -= P;
                if (i > lists.Count)
                {
                    // Add a new list: made at once, at the end, and its section opened on the name.
                    int n = 1;
                    while (lists.Any(l => l.Name.Equals("New list " + n, StringComparison.OrdinalIgnoreCase))) n++;
                    var p = new Pending { Name = "New list " + n };
                    lists.Add(p);
                    FillSections(lists.Count + P);
                    LoadList(p);
                    body.Content = listPanel;
                    Speech.Say(w, "Added " + p.Name + ". Type its name, then choose its settings. It is made when you press OK.", true);
                    w.Dispatcher.BeginInvoke(() => { name.Focus(); name.SelectAll(); }, System.Windows.Threading.DispatcherPriority.Input);
                    return;
                }
                LoadList(lists[i - 1]);
                body.Content = listPanel;
            }
            int ListSection(Pending p) => lists.IndexOf(p) + 1 + P;
            section.SelectionChanged += (_, _) => { if (!loading) ShowSection(); };

            name.TextChanged += (_, _) => { if (!loading && current != null) current.Name = name.Text.Trim(); };
            // The section menu shows the new name once you move on, not on every letter.
            name.LostKeyboardFocus += (_, _) => { if (current != null) FillSections(ListSection(current)); };
            position.SelectionChanged += (_, _) =>
            {
                if (loading || current == null || position.SelectedIndex < 0) return;
                lists.Remove(current);
                lists.Insert(position.SelectedIndex, current);
                FillSections(ListSection(current));
            };
            scales.SelectionChanged += (_, _) =>
            {
                if (loading || current == null || scales.SelectedItem is not string s) return;
                if (s == NoneName) { current.ScaleName = ""; current.Levels = Array.Empty<string>(); }
                else if (s == CustomName) current.ScaleName = customName.Text.Trim().Length > 0 ? customName.Text.Trim() : CustomName;
                else { current.ScaleName = s; current.Levels = Scales.Library.First(x => x.Name == s).Levels; }
                ShowLevels();
            };
            levels.TextChanged += (_, _) =>
            {
                if (loading || current == null || !IsCustom()) return;
                current.Levels = levels.Text.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
            };
            customName.TextChanged += (_, _) =>
            {
                if (loading || current == null || !IsCustom()) return;
                current.ScaleName = customName.Text.Trim().Length > 0 ? customName.Text.Trim() : CustomName;
            };

            var panel = new StackPanel { Margin = new Thickness(14), Width = 460 };
            panel.Children.Add(Layout.Row("S_ection", section, 420));
            panel.Children.Add(body);
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            w.Content = panel;

            ok.Click += (_, _) =>
            {
                // Every list needs a name of its own, and a custom scale needs levels.
                for (int i = 0; i < lists.Count; i++)
                {
                    var p = lists[i];
                    string? problem = p.Name.Length == 0 ? "A list has no name. Type one, or press Cancel."
                        : lists.Take(i).Any(o => o.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)) ? "Two lists are called " + p.Name + ". Give one another name."
                        : p.ScaleName.Length > 0 && p.Levels.Length == 0 ? p.Name + " has a custom scale with no levels. Type them in Levels, separated by commas, or choose None."
                        : null;
                    if (problem == null) continue;
                    FillSections(i + 1 + P);
                    LoadList(p);
                    body.Content = listPanel;
                    Speech.Say(w, problem, true);
                    (p.Name.Length == 0 || problem.StartsWith("Two") ? name : levels).Focus();
                    return;
                }

                for (int i = 0; i < P; i++)
                {
                    if (pages[i].Validate() is not string problem) continue;
                    FillSections(i + 1);
                    body.Content = pages[i].Content;
                    Speech.Say(w, problem, true);
                    return;
                }

                settings.SoundsOn = soundsOn.IsChecked == true;
                settings.ShortMode = shortMode.IsChecked == true;
                settings.CheckForUpdatesAtStart = autoUpdate.IsChecked == true;
                settings.UrgentWarnAbove = warn.SelectedIndex <= 0 ? 0 : warn.SelectedIndex + 2;
                settings.Save();
                settings.Apply();

                store.Remember("changing settings");
                store.Data.HoursAvailable = hours.SelectedIndex + 1;
                var made = new List<TaskList>();
                foreach (var p in lists)
                {
                    var list = p.Original != null ? store.Data.Lists.First(l => l.Name == p.Original) : new TaskList();
                    if (p.Original != null && p.Original != p.Name)
                        foreach (var t in store.Data.Tasks.Where(t => t.List == p.Original)) t.List = p.Name;
                    list.Name = p.Name;
                    list.Scale = p.Levels;
                    list.ScaleName = p.Levels.Length == 0 ? "" : p.ScaleName;
                    // A shorter scale: tasks past its end take its lowest level; no scale clears them.
                    foreach (var t in store.Data.Tasks.Where(t => t.List == p.Name && t.Priority > p.Levels.Length)) t.Priority = p.Levels.Length;
                    made.Add(list);
                }
                store.Data.Lists = made;
                foreach (var page in pages) page.Apply();
                settings.Save();
                w.DialogResult = true;
            };

            int start = startList == null ? 0 : lists.FindIndex(l => l.Name == startList) + 1 + P;
            FillSections(Math.Max(0, start));
            ShowSection();
            w.Loaded += (_, _) => section.Focus();
            return w.ShowDialog() == true;
        }
    }
}
