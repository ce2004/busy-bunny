using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// The voice of the app: encouragement after a finished task (spec 12.1) with the roast dial (12.2),
    /// emoji at big moments (12.3), surprise rewards (12.5), finals week and holiday phrases (19.11),
    /// I did a thing (12.12) and the parking lot (15.5). Other features call Tone.Big(text) to add an
    /// emoji to a big moment of their own, like a streak or a level up.
    /// </summary>
    public sealed class Tone : IFeature
    {
        public static readonly string[] RoastNames = { "Off", "Gentle", "Teasing", "Spicy", "Savage" };

        /// <summary>The settings in use; the main window's once attached.</summary>
        public static Settings Cfg { get; set; } = new();
        private static readonly Random Rng = new();

        private static readonly string[] PartyEmoji = { "\U0001F389", "\U0001F973", "✨", "\U0001F38A" };
        private static readonly Dictionary<string, string[]> EmojiSets = new()
        {
            ["Emoji:Animals"] = new[] { "\U0001F430", "\U0001F98A", "\U0001F422", "\U0001F984", "\U0001F43C" },
            ["Emoji:Food"] = new[] { "\U0001F369", "\U0001F355", "\U0001F9C1", "\U0001F36A" },
            ["Emoji:Space"] = new[] { "\U0001F680", "\U0001F31F", "\U0001FA90", "\U0001F30C" },
        };

        private static readonly (string Key, string Label)[] Unlockables =
        {
            ("Set:Drama", "the drama queen phrase set"),
            ("Set:Sports", "the sports announcer phrase set"),
            ("Set:Wizard", "the wizard phrase set"),
            ("Set:Space", "the space captain phrase set"),
            ("Set:Pirate", "the pirate phrase set"),
            ("Emoji:Animals", "the animal emoji set"),
            ("Emoji:Food", "the snack emoji set"),
            ("Emoji:Space", "the outer space emoji set"),
        };

        /// <summary>Adds an emoji to the text for a big moment (all done, streak, level up) when emoji are on. Never use it in task lines.</summary>
        public static string Big(string text)
        {
            if (!Cfg.EmojisOn) return text;
            var pool = PartyEmoji.Concat(Cfg.Unlocked.Where(EmojiSets.ContainsKey).SelectMany(k => EmojiSets[k])).ToArray();
            return text + " " + pool[Rng.Next(pool.Length)];
        }

        // ---- Moments and seasons (pure, so the self-test can check them) ----

        public static string Moment(int done, int total, bool haunting, bool comeback)
        {
            if (total > 0 && done >= total) return "All";
            if (haunting) return "Haunting";
            if (done == 1) return comeback ? "Comeback" : "First";
            if (done > 1 && total > 0 && done * 2 >= total && (done - 1) * 2 < total) return "Half";
            return "Done";
        }

        /// <summary>A task that has hung around: old, or pushed back again and again.</summary>
        public static bool Haunting(TaskItem t, DateTime now) => t.CreatedAt <= now.AddDays(-14) || t.Snoozes >= 3;

        /// <summary>Back after a missed day: nothing finished yesterday (and it was not a vacation or rough day), but things were finished before.</summary>
        public static bool Comeback(Store s, DateTime now)
        {
            var y = now.Date.AddDays(-1);
            if (s.Data.Days.TryGetValue(s.Key(y), out var info) && info.Mode != DayMode.Normal) return false;
            bool Did(DateTime d) => s.Data.Tasks.Any(t => t.DoneAt?.Date == d) || s.Data.Log.Any(l => l.Kind == "DidAThing" && l.When.Date == d);
            if (Did(y)) return false;
            return s.Data.Tasks.Any(t => t.DoneAt != null && t.DoneAt.Value.Date < y) || s.Data.Log.Any(l => l.Kind == "DidAThing" && l.When.Date < y);
        }

        /// <summary>The holiday phrase set for a date, or null.</summary>
        public static string? SeasonOf(DateTime d)
        {
            if (d.Month == 10 && d.Day >= 24) return "Halloween";
            if (d.Month == 11)
            {
                var t = new DateTime(d.Year, 11, 1);
                while (t.DayOfWeek != DayOfWeek.Thursday) t = t.AddDays(1);
                t = t.AddDays(21); // the fourth Thursday
                if (d.Date >= t.AddDays(-5) && d.Date <= t.AddDays(1)) return "Thanksgiving";
            }
            if (d.Month == 12 && d.Day >= 18 || d.Month == 1 && d.Day <= 2) return "Winter";
            if (d.Month == 2 && d.Day >= 10 && d.Day <= 14) return "Valentine";
            return null;
        }

        private static bool IsTestLike(TaskItem t) =>
            t.Type == TaskType.Test || new[] { "test", "exam", "quiz", "final" }.Any(w => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase));

        /// <summary>Early December or late April, with a test due within 7 days: time to offer finals week mode.</summary>
        public static bool FinalsDue(Store s, DateTime now)
        {
            bool season = now.Month == 12 && now.Day <= 15 || now.Month == 4 && now.Day >= 20;
            if (!season) return false;
            return s.Data.Tasks.Any(t => t.Status != Status.Done && IsTestLike(t) && t.Due is DateTime d && d.Date >= now.Date && d.Date <= now.Date.AddDays(7));
        }

        /// <summary>The line to say for a moment, or null when the dial is off. Mixes in holiday, finals and unlocked sets now and then.</summary>
        public static string? Compose(string moment, DateTime now, Settings s, Random rng)
        {
            int dial = s.RoastLevel - 1;
            if (dial < 0) return null;
            string group = moment;
            string? season = SeasonOf(now);
            var sets = s.Unlocked.Where(k => k.StartsWith("Set:")).ToList();
            bool plain = moment is "Done" or "Half" or "First" or "All";
            if (season != null && plain && rng.NextDouble() < (moment == "All" ? 0.5 : 0.35)) group = season;
            else if (s.FinalsMode && moment != "Haunting" && rng.NextDouble() < 0.4) group = "Finals";
            else if (sets.Count > 0 && moment == "Done" && rng.NextDouble() < 0.25) group = sets[rng.Next(sets.Count)];
            string? text = Phrases.Pick(group, dial, s.PhraseUsed, rng) ?? Phrases.Pick(moment, dial, s.PhraseUsed, rng);
            return text != null && moment == "All" ? Big(text) : text;
        }

        // ---- Surprise rewards (spec 12.5) ----

        /// <summary>About 1 in 8 finished tasks.</summary>
        public static bool Lucky(Random rng) => rng.Next(8) == 0;

        /// <summary>kind 0 is a fun fact, 1 a funny message or roast, 2 an unlock. Falls back to a fact when there is nothing else.</summary>
        public static string MakeSurprise(Settings s, Random rng, int kind)
        {
            if (kind == 2)
            {
                var locked = Unlockables.Where(u => !s.Unlocked.Contains(u.Key)).ToList();
                if (locked.Count > 0)
                {
                    var u = locked[rng.Next(locked.Count)];
                    s.Unlocked.Add(u.Key);
                    return "Surprise reward! You unlocked " + u.Label + ". " + (u.Key.StartsWith("Set:") ? "Its phrases will turn up in your cheers now and then." : "It will turn up at the big moments.");
                }
                kind = 0;
            }
            if (kind == 1 && s.RoastLevel > 0)
            {
                string? roast = Phrases.Pick("Surprise", s.RoastLevel - 1, s.PhraseUsed, rng);
                if (roast != null) return roast;
            }
            return "Surprise reward! Fun fact: " + Phrases.PickFact(s.PhraseUsed, rng);
        }

        // ---- The feature ----

        private MainWindow? _w;
        private MenuItem? _finalsMenu;

        public void Attach(MainWindow w)
        {
            _w = w;
            Cfg = w.AppSettings;

            w.StatusChanged += (t, old) => { if (t.Status == Status.Done && old != Status.Done) Cheer(w, t); };

            _finalsMenu = w.AddMenuItem("Fun", "Finals _week mode", () => SetFinals(w, _finalsMenu!.IsChecked));
            _finalsMenu.IsCheckable = true;
            _finalsMenu.IsChecked = Cfg.FinalsMode;
            if (_finalsMenu.Parent is MenuItem top) top.SubmenuOpened += (_, _) => _finalsMenu.IsChecked = Cfg.FinalsMode;
            w.AddMenuItem("Fun", "I _did a thing...", () => DidAThing(w), "Ctrl+Shift+I");
            w.AddMenuItem("Fun", "_Park a thought...", () => ParkAThought(w), "Ctrl+Shift+K");
            w.AddMenuItem("Fun", "View the p_arking lot...", () => ViewParkingLot(w));
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.I, () => DidAThing(w), "Ctrl+Shift+I: I did a thing, log something done that was never on a list");
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.K, () => ParkAThought(w), "Ctrl+Shift+K: park a stray thought without leaving where you are");

            w.Started += () =>
            {
                if (Cfg.FinalsMode)
                {
                    string? line = Phrases.Pick("Finals", Math.Max(0, Cfg.RoastLevel - 1), Cfg.PhraseUsed, Rng);
                    if (Cfg.RoastLevel > 0 && line != null) w.Say("Finals week mode is on. " + line);
                    return;
                }
                OfferFinals(w);
            };
            w.NewDay += _ => { if (_finalsMenu != null) _finalsMenu.IsChecked = Cfg.FinalsMode; };
        }

        private static void SetFinals(MainWindow w, bool on)
        {
            Cfg.FinalsMode = on;
            Cfg.Save();
            w.Say(on ? "Finals week mode is on. Extra encouragement, as ordered." : "Finals week mode is off.");
        }

        private void OfferFinals(MainWindow w)
        {
            var now = DateTime.Now;
            string season = now.Year + "-" + now.Month;
            if (Cfg.FinalsOffered == season || !FinalsDue(w.Store, now)) return;
            Cfg.FinalsOffered = season;
            Cfg.Save();
            // Spoken, never a pop-up: the Fun menu has the switch.
            w.Say("A test is due within the week, and it is finals season. Fun menu, Finals week mode turns on extra encouragement.");
        }

        /// <summary>Says the line for a finished task, after the app's own Done line, and now and then a surprise.</summary>
        private static void Cheer(MainWindow w, TaskItem? t)
        {
            var now = DateTime.Now;
            var (done, total, _, _) = w.Store.Progress(now);
            string moment = Moment(done, total, t != null && Haunting(t, now), Comeback(w.Store, now));
            string? line = Compose(moment, now, Cfg, Rng);
            if (line != null) w.Say(line);
            if (Lucky(Rng))
            {
                int roll = Rng.Next(10);
                string reward = MakeSurprise(Cfg, Rng, roll < 5 ? 0 : roll < 8 ? 1 : 2);
                Sounds.Play(Sounds.Event.SurpriseReward);
                w.Say(reward);
            }
            Cfg.Save();
        }

        // ---- I did a thing (spec 12.12) ----

        private static void DidAThing(MainWindow w)
        {
            var dlg = Layout.Dialog(w, "I did a thing");
            var what = new TextBox();
            var size = new ComboBox { ItemsSource = new[] { "Small, 1 point", "Medium, 2 points", "Large, 4 points" }, SelectedIndex = 0 };
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            ok.Click += (_, _) =>
            {
                if (what.Text.Trim().Length == 0) { Speech.Say(dlg, "Type what you did first.", true); what.Focus(); return; }
                dlg.DialogResult = true;
            };
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Layout.Row("_What did you do?", what, 420));
            panel.Children.Add(Layout.Row("How _big was it?", size, 420));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.Loaded += (_, _) => what.Focus();
            if (dlg.ShowDialog() != true) return;

            var sz = (Size)Math.Clamp(size.SelectedIndex, 0, 2);
            string text = what.Text.Trim();
            w.Store.Remember("logging " + text);
            w.Store.Data.Log.Add(new LogEntry { When = DateTime.Now, Kind = "DidAThing", Text = text, Points = Words.Points(sz) });
            w.SaveAndRefresh();
            Sounds.Play(Sounds.Event.TaskDone);
            var (done, total, _, _) = w.Store.Progress(DateTime.Now);
            w.Say("Logged: " + text + ". " + done + " of " + total + " today.");
            Cheer(w, null);
        }

        // ---- Parking lot (spec 15.5) ----

        private static void ParkAThought(MainWindow w)
        {
            var before = Keyboard.FocusedElement;
            var dlg = Layout.Dialog(w, "Park a thought");
            var thought = new TextBox();
            var ok = Layout.Button("Park it", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            ok.Click += (_, _) =>
            {
                if (thought.Text.Trim().Length == 0) { dlg.DialogResult = false; return; }
                dlg.DialogResult = true;
            };
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Layout.Row("_Stray thought", thought, 420));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.Loaded += (_, _) => thought.Focus();
            bool saved = dlg.ShowDialog() == true;
            if (saved)
            {
                w.Store.Remember("parking a thought");
                w.Store.Data.Parking.Add(thought.Text.Trim());
                w.Store.Save();
            }
            // Back to wherever she was, as if nothing happened.
            w.Dispatcher.BeginInvoke(() => { if (before is UIElement u) u.Focus(); else Keyboard.Focus(before); }, System.Windows.Threading.DispatcherPriority.Input);
            if (saved) w.Say("Parked. " + w.Store.Data.Parking.Count + (w.Store.Data.Parking.Count == 1 ? " thought" : " thoughts") + " in the parking lot.");
        }

        private static void ViewParkingLot(MainWindow w)
        {
            var store = w.Store;
            if (store.Data.Parking.Count == 0) { w.Say("The parking lot is empty. Control Shift K parks a thought.", true); return; }
            var dlg = Layout.Dialog(w, "Parking lot");
            var list = new ListBox { Width = 560, Height = 260 };
            AutomationProperties.SetName(list, "Parked thoughts");
            var lists = new ComboBox { ItemsSource = store.ActiveLists.Select(l => l.Name).ToList() };
            lists.SelectedItem = store.ActiveLists.Any(l => l.Name == "Personal Immediate") ? "Personal Immediate" : store.ActiveLists.FirstOrDefault()?.Name;

            void Fill(int select)
            {
                list.Items.Clear();
                foreach (var p in store.Data.Parking)
                {
                    var item = new ListBoxItem { Content = new TextBlock { Text = p, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 } };
                    AutomationProperties.SetName(item, p);
                    list.Items.Add(item);
                }
                if (list.Items.Count == 0) return;
                TextViewer.Select(list, select);
            }

            void LetGo()
            {
                int i = list.SelectedIndex;
                if (i < 0) return;
                store.Remember("letting go of a parked thought");
                string gone = store.Data.Parking[i];
                store.Data.Parking.RemoveAt(i);
                w.SaveAndRefresh();
                w.Say("Let go: " + gone + ".");
                AfterChange(i);
            }

            void MakeTask()
            {
                int i = list.SelectedIndex;
                if (i < 0 || lists.SelectedItem is not string listName) return;
                string text = store.Data.Parking[i];
                var target = store.Data.Lists.FirstOrDefault(l => l.Name == listName);
                // AddTask remembers first, so one Control Z puts the thought back in the lot and removes the task.
                w.AddTask(new TaskItem { Name = text, List = listName, Status = target?.DefaultStatus ?? Status.ThisWeek });
                store.Data.Parking.Remove(text);
                w.SaveAndRefresh();
                AfterChange(i);
            }

            void AfterChange(int i)
            {
                if (store.Data.Parking.Count == 0) { dlg.Close(); w.Say("The parking lot is empty now."); return; }
                Fill(Math.Min(i, store.Data.Parking.Count - 1));
            }

            list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Delete) { e.Handled = true; LetGo(); } };
            var task = Layout.Button("Turn into a _task");
            task.Click += (_, _) => MakeTask();
            var go = Layout.Button("Let _go");
            go.Click += (_, _) => LetGo();
            var close = Layout.Button("_Close", isDefault: true, isCancel: true);
            close.Click += (_, _) => dlg.Close();
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new Label { Content = "_Parked thoughts", Target = list, Padding = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(list);
            panel.Children.Add(Layout.Row("Put a new task on _list", lists, 320));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(task);
            buttons.Children.Add(go);
            buttons.Children.Add(close);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.Loaded += (_, _) => Fill(0);
            dlg.ShowDialog();
        }

        // ---- Options, Settings, Fun ----

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) => new ISettingsPage[] { new FunPage(settings) };

        private sealed class FunPage : ISettingsPage
        {
            private readonly Settings _s;
            private readonly ComboBox _roast;
            private readonly CheckBox _emoji;
            private readonly CheckBox _finals;
            private readonly StackPanel _panel = new();

            public FunPage(Settings s)
            {
                _s = s;
                _roast = new ComboBox { ItemsSource = RoastNames, SelectedIndex = Math.Clamp(s.RoastLevel, 0, 4) };
                AutomationProperties.SetHelpText(_roast, "Off says nothing after a task. Gentle is kind, Teasing is dry, Spicy bites a little, Savage is full banter.");
                _emoji = new CheckBox { Content = "_Emoji at big moments", IsChecked = s.EmojisOn, Margin = new Thickness(0, 8, 0, 4) };
                _finals = new CheckBox { Content = "_Finals week mode: extra encouragement", IsChecked = s.FinalsMode, Margin = new Thickness(0, 4, 0, 4) };
                string unlocked = s.Unlocked.Count == 0 ? "Nothing yet. Finish tasks and keep your eyes open."
                    : string.Join(", ", s.Unlocked.Select(k => Unlockables.FirstOrDefault(u => u.Key == k).Label ?? k)) + ".";
                var box = new TextBox { Text = unlocked, IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, Width = 420 };
                AutomationProperties.SetName(box, "Surprises unlocked");
                _panel.Children.Add(Layout.Row("_Roast level", _roast, 200));
                _panel.Children.Add(_emoji);
                _panel.Children.Add(_finals);
                _panel.Children.Add(new Label { Content = "_Surprises unlocked", Target = box, Padding = new Thickness(0, 8, 0, 2) });
                _panel.Children.Add(box);
            }

            public string Name => "Fun";
            public FrameworkElement Content => _panel;

            public void Apply()
            {
                _s.RoastLevel = Math.Clamp(_roast.SelectedIndex, 0, 4);
                _s.EmojisOn = _emoji.IsChecked == true;
                _s.FinalsMode = _finals.IsChecked == true;
                _s.Save();
            }
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            var rng = new Random(7);
            char pound = (char)35, star = (char)42;
            var normal = Phrases.All.Where(p => !p.Group.StartsWith("Set:")).ToList();
            if (normal.Count < 150) throw new Exception("only " + normal.Count + " phrases");
            if (Phrases.Facts.Count < 60) throw new Exception("only " + Phrases.Facts.Count + " fun facts");
            foreach (string text in Phrases.All.Select(p => p.Text).Concat(Phrases.Facts))
                if (text.IndexOf(pound) >= 0 || text.IndexOf(star) >= 0 || text.Any(c => char.IsSurrogate(c) || c > 0x2500)) throw new Exception("bad character in: " + text);
            foreach (string g in new[] { "Done", "Half", "All", "Haunting", "First", "Comeback", "Finals" })
                for (int lvl = 0; lvl < 4; lvl++)
                    if (Phrases.All.Count(p => p.Group == g && p.Level == lvl) < 2) throw new Exception("group " + g + " is thin at level " + lvl);

            // No repeats until most of a group has been used.
            var used = new Dictionary<string, List<int>>();
            var seen = new HashSet<string>();
            for (int i = 0; i < 6; i++)
                if (!seen.Add(Phrases.Pick("Half", 1, used, rng)!)) throw new Exception("a phrase repeated too soon");
            for (int i = 0; i < 40; i++) if (Phrases.Pick("Half", 1, used, rng) == null) throw new Exception("ran dry");

            if (Moment(8, 8, false, false) != "All" || Moment(3, 8, true, false) != "Haunting" || Moment(1, 8, false, true) != "Comeback"
                || Moment(1, 8, false, false) != "First" || Moment(4, 8, false, false) != "Half" || Moment(2, 8, false, false) != "Done")
                throw new Exception("moments are wrong");
            if (SeasonOf(new DateTime(2026, 10, 31)) != "Halloween" || SeasonOf(new DateTime(2026, 11, 26)) != "Thanksgiving" || SeasonOf(new DateTime(2026, 12, 25)) != "Winter"
                || SeasonOf(new DateTime(2027, 2, 14)) != "Valentine" || SeasonOf(new DateTime(2026, 3, 3)) != null)
                throw new Exception("seasons are wrong");

            var test = new TaskItem { Name = "stats final", Type = TaskType.Test, Due = new DateTime(2026, 12, 7), List = store.Data.Lists[0].Name };
            store.Data.Tasks.Add(test);
            if (!FinalsDue(store, new DateTime(2026, 12, 3)) || FinalsDue(store, new DateTime(2026, 10, 3)) || FinalsDue(store, new DateTime(2026, 12, 20)))
                throw new Exception("finals offer is wrong");

            // Emoji only when on.
            var keep = Cfg;
            try
            {
                Cfg = new Settings { EmojisOn = false };
                if (Big("Streak") != "Streak") throw new Exception("emoji used while off");
                Cfg = new Settings { EmojisOn = true };
                if (Big("Streak").Length <= 6) throw new Exception("no emoji while on");

                // Roast off says nothing; each level says something; All gets the emoji.
                var s = new Settings { RoastLevel = 0 };
                if (Compose("Done", DateTime.Today, s, rng) != null) throw new Exception("spoke while off");
                for (int lvl = 1; lvl <= 4; lvl++)
                    if (string.IsNullOrEmpty(Compose("All", new DateTime(2026, 3, 3), new Settings { RoastLevel = lvl }, rng))) throw new Exception("no line at level " + lvl);

                // Surprises: a fact, a roast, then unlocks until none are left, then facts again.
                var sp = new Settings { RoastLevel = 3 };
                if (!MakeSurprise(sp, rng, 0).Contains("Fun fact")) throw new Exception("no fact");
                if (MakeSurprise(sp, rng, 1).Length == 0) throw new Exception("no roast");
                for (int i = 0; i < Unlockables.Length; i++) MakeSurprise(sp, rng, 2);
                if (sp.Unlocked.Count != Unlockables.Length || sp.Unlocked.Distinct().Count() != Unlockables.Length) throw new Exception("unlocks are wrong");
                if (!MakeSurprise(sp, rng, 2).Contains("Fun fact")) throw new Exception("nothing left to unlock should give a fact");
                int lucky = Enumerable.Range(0, 8000).Count(_ => Lucky(rng));
                if (lucky < 700 || lucky > 1300) throw new Exception("surprise odds are off: " + lucky);
            }
            finally { Cfg = keep; }

            // I did a thing counts toward the day.
            var now = DateTime.Now;
            var before = store.Progress(now);
            store.Data.Log.Add(new LogEntry { When = now, Kind = "DidAThing", Text = "laundry", Points = Words.Points(Size.Medium) });
            var after = store.Progress(now);
            if (after.Done != before.Done + 1 || after.Total != before.Total + 1 || after.PointsDone != before.PointsDone + 2 || after.PointsTotal != before.PointsTotal + 2)
                throw new Exception("I did a thing does not count toward the day");
        }
    }
}
