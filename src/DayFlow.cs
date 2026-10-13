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
    /// The shape of the day: the morning check-in (spec 15.4), the kind of day (6.2f), hours available
    /// (10.4), vacation mode (10.5), the rough day button (22.3), the end-of-day reset (10.6) and,
    /// from Rhythm, the weekday plan (section 11).
    /// </summary>
    public sealed class DayFlow : IFeature
    {
        private static readonly string[] Kinds = { "Fox at the door", "Hop to it", "Nibble on it", "Burrow nap" };
        private const string AutoKind = "Let Busy Bunny pick";
        private static readonly string[] Energies = { "Low", "Medium", "High" };
        private static readonly Random Dice = new();

        private static readonly string[] RoughLines =
        {
            "Rough day, noted. Today's pile went to tomorrow, your streak is safe, and nobody is keeping score. Go be a lump.",
            "Okay. Everything that could wait is waiting, and your streak is covered. Drink some water, that is the whole assignment.",
            "Rough day mode on. The non-urgent stuff is out of sight, the streak is protected. You are allowed to just exist today.",
            "Understood. I moved the day's tasks along and kept your streak. The list will still be here, annoyingly loyal.",
        };

        private MainWindow _w = null!;

        public void Attach(MainWindow w)
        {
            _w = w;
            // Vacation hides everything from Today; a rough day hides all but what is due soon or put on Today by hand.
            w.Store.HideFromToday.Add((t, now) =>
            {
                var s = w.Store;
                if (!s.Data.Days.TryGetValue(s.Key(now), out var d)) return false;
                if (d.Mode == DayMode.Vacation) return true;
                return d.Mode == DayMode.RoughDay && t.Status != Status.Today && !s.DueSoon(t, now);
            });

            w.AddMenuItem("File", "_Morning check-in...", () => CheckIn(true), "Ctrl+Shift+M");
            w.AddMenuItem("File", "Weekday _plan for today...", () => Rhythm.Suggest(w, true));
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.M, () => CheckIn(true), "Control Shift M: morning check-in");
            w.AddMenuItem("Fun", "Set the _kind of day...", SetKindOfDay, "Ctrl+Shift+Y");
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.Y, SetKindOfDay, "Control Shift Y: set the kind of day");
            w.AddMenuItem("Fun", "_Rough day", RoughDay);
            w.AddMenuItem("Fun", "_Vacation mode...", Vacation);

            w.Started += OnStarted;
            w.NewDay += OnNewDay;
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings)
        {
            yield return new DayFlowPage(settings);
            yield return new WeekdayPage(store);
        }

        // ---- Starting a day ----

        private void OnStarted()
        {
            var now = DateTime.Now;
            EndOfDay(now);
            if (OnVacation(now)) return;
            var day = _w.Store.Data.Day(now);
            if (_w.AppSettings.MorningCheckIn && !day.CheckedIn) CheckIn(false);
            if (_w.AppSettings.LastRhythmDay != _w.Store.Key(now)) RunRhythm(now);
        }

        private void OnNewDay(DateTime today)
        {
            EndOfDay(today);
            if (_w.AppSettings.MorningCheckIn && !OnVacation(today) && !_w.Store.Data.Day(today).CheckedIn)
                _w.Say("New day. Control Shift M is the morning check-in when you want it.");
        }

        private void RunRhythm(DateTime now)
        {
            _w.AppSettings.LastRhythmDay = _w.Store.Key(now);
            _w.AppSettings.Save();
            Rhythm.Suggest(_w, false);
        }

        private bool OnVacation(DateTime now) =>
            _w.Store.Data.Days.TryGetValue(_w.Store.Key(now), out var d) && d.Mode == DayMode.Vacation;

        // ---- End-of-day reset (10.6) ----

        /// <summary>Today tasks left over from before this day.</summary>
        public static List<TaskItem> LeftOver(Store store, DateTime now) => store.Data.Tasks
            .Where(t => t.Status == Status.Today && (t.StatusSince ?? t.TouchedAt).Date < now.Date)
            .ToList();

        /// <summary>Moves them back to This week (mode week) or leaves them (mode keep). Returns how many were affected; undoable.</summary>
        public static int Reset(Store store, DateTime now, string mode)
        {
            var old = LeftOver(store, now);
            if (old.Count == 0 || mode == "off") return 0;
            if (mode == "week")
            {
                store.Remember("moving yesterday's Today tasks back to This week");
                foreach (var t in old) { t.Status = Status.ThisWeek; t.StatusSince = now; t.TouchedAt = now; }
            }
            return old.Count;
        }

        private void EndOfDay(DateTime now)
        {
            var s = _w.AppSettings;
            string key = _w.Store.Key(now);
            if (s.LastResetDay == key) return;
            s.LastResetDay = key;
            s.Save();
            int n = Reset(_w.Store, now, s.EndOfDay);
            if (n == 0) return;
            string things = n == 1 ? "1 unfinished task" : n + " unfinished tasks";
            if (s.EndOfDay == "week")
            {
                _w.SaveAndRefresh();
                _w.Say(things + " from before went back to This week, so nothing is overdue. Pull back what you want.");
            }
            else _w.Say(things + " stayed on Today from before. Brave.");
        }

        // ---- Morning check-in (15.4) ----

        private void CheckIn(bool manual)
        {
            var store = _w.Store;
            var now = DateTime.Now;
            var day = store.Data.Day(now);

            var dlg = Layout.Dialog(_w, "Morning check-in");
            var energy = new ComboBox { ItemsSource = Energies, SelectedIndex = day.Energy.Length > 0 ? Math.Max(0, Array.FindIndex(Energies, e => e.Equals(day.Energy, StringComparison.OrdinalIgnoreCase))) : 1 };
            var hours = HoursBox(store, now);
            var fresh = new TextBox { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 80 };
            var ok = Layout.Button("OK", isDefault: true);
            var skip = Layout.Button(manual ? "Cancel" : "Not now", isCancel: true);
            bool accepted = false;
            ok.Click += (_, _) => { accepted = true; dlg.Close(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(skip);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("_Energy today", energy, 300));
            panel.Children.Add(Layout.Row("_Hours available today", hours, 300));
            panel.Children.Add(Layout.Row("Anything _new? One task per line", fresh, 420));
            AutomationProperties.SetHelpText(fresh, "After OK, you choose a list, priority and when for each one. They start on Today.");
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.ShowDialog();

            if (!accepted)
            {
                if (!manual) { day.CheckedIn = true; _w.SaveAndRefresh(); }
                return;
            }
            store.Remember("morning check-in");
            day.CheckedIn = true;
            day.Energy = Energies[Math.Max(0, energy.SelectedIndex)].ToLowerInvariant();
            double h = HoursOf(hours);
            day.Hours = Math.Abs(h - store.Data.HoursAvailable) < 0.01 ? 0 : h;
            // Morgan's issue 9: each new task gets its own list, priority and when, starting on Today.
            var names = Lines(fresh.Text);
            var made = names.Count == 0 ? new List<TaskItem>() : NewTasks.Review(_w, store, names, _w.CurrentListName, 0);
            foreach (var t in made ?? new()) _w.AddTask(t, false);
            _w.SaveAndRefresh();
            string said = "Checked in. Energy " + day.Energy + ", " + HoursWords(h) + " available"
                + (made == null ? ", no tasks added" : made.Count > 0 ? ", " + made.Count + (made.Count == 1 ? " task" : " tasks") + " added" : "") + ".";
            if (day.Energy == "low") said += " Low energy noted. Go easy on yourself.";
            _w.Say(said);
        }

        /// <summary>Non-empty trimmed lines, each one a task.</summary>
        public static List<string> Lines(string text) =>
            text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

        private static readonly double[] HourSteps = Enumerable.Range(1, 28).Select(i => i / 2.0).ToArray();

        private static string HoursWords(double h) => h == 1 ? "1 hour" : (h % 1 == 0 ? ((int)h).ToString() : h.ToString("0.0")) + " hours";

        private static ComboBox HoursBox(Store store, DateTime now)
        {
            var box = new ComboBox { ItemsSource = HourSteps.Select(HoursWords).ToList() };
            double cur = store.HoursToday(now);
            int i = Array.FindIndex(HourSteps, x => Math.Abs(x - cur) < 0.26);
            box.SelectedIndex = i < 0 ? 9 : i;
            return box;
        }

        private static double HoursOf(ComboBox box) => HourSteps[Math.Clamp(box.SelectedIndex, 0, HourSteps.Length - 1)];

        // ---- The kind of day (6.2f) and hours (10.4) ----

        private void SetKindOfDay()
        {
            var store = _w.Store;
            var now = DateTime.Now;
            var day = store.Data.Day(now);
            var dlg = Layout.Dialog(_w, "Kind of day");
            var kinds = new[] { AutoKind }.Concat(Kinds).ToList();
            var kind = new ComboBox { ItemsSource = kinds, SelectedIndex = Math.Max(0, kinds.IndexOf(day.DayType)) };
            var hours = HoursBox(store, now);
            var low = new CheckBox { Content = "_Low-energy mode today: short, easy tasks", IsChecked = day.Energy == "low", Margin = new Thickness(0, 6, 0, 6) };
            bool wasLow = day.Energy == "low";
            kind.SelectionChanged += (_, _) =>
            {
                if ((string)kind.SelectedItem == "Burrow nap" && !wasLow) { low.IsChecked = true; _w.Say("Burrow nap. Low-energy mode is ticked, if you want it.", true); }
            };
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            bool accepted = false;
            ok.Click += (_, _) => { accepted = true; dlg.Close(); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("_Kind of day (now " + store.DayType(now) + ")", kind, 300));
            panel.Children.Add(Layout.Row("_Hours available today", hours, 300));
            panel.Children.Add(low);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.ShowDialog();
            if (!accepted) return;

            store.Remember("setting the kind of day");
            string chosen = (string)kind.SelectedItem;
            day.DayType = chosen == AutoKind ? "" : chosen;
            double h = HoursOf(hours);
            day.Hours = Math.Abs(h - store.Data.HoursAvailable) < 0.01 ? 0 : h;
            if (low.IsChecked == true) day.Energy = "low";
            else if (wasLow) day.Energy = "";
            _w.SaveAndRefresh();
            _w.Say("Today is a " + store.DayType(now) + " day, " + HoursWords(store.HoursToday(now)) + " available" + (day.Energy == "low" ? ", low energy" : "") + ".");
        }

        // ---- Rough day (22.3) ----

        private void RoughDay()
        {
            var store = _w.Store;
            var now = DateTime.Now;
            var day = store.Data.Day(now);
            if (day.Mode == DayMode.RoughDay) { _w.Say("Today is already a rough day. Control Z undoes it if you changed your mind.", true); return; }
            store.Remember("rough day");
            day.Mode = DayMode.RoughDay;
            int moved = Push(store, now);
            _w.SaveAndRefresh();
            _w.Say(RoughLines[Dice.Next(RoughLines.Length)] + (moved > 0 ? " " + moved + (moved == 1 ? " task" : " tasks") + " moved along." : ""));
        }

        /// <summary>Moves the day's non-urgent tasks forward: planned for today goes to tomorrow, on Today goes to This week. Due-soon tasks stay.</summary>
        public static int Push(Store store, DateTime now)
        {
            int n = 0;
            foreach (var t in store.Data.Tasks.Where(t => t.Status != Status.Done && !store.DueSoon(t, now)).ToList())
            {
                if (t.Status == Status.Today) { t.Status = Status.ThisWeek; t.StatusSince = now; t.TouchedAt = now; n++; }
                else if (t.DoDate?.Date <= now.Date && t.Status != Status.Waiting && t.Status != Status.DoneNotSubmitted) { t.DoDate = now.Date.AddDays(1); t.TouchedAt = now; n++; }
            }
            return n;
        }

        // ---- Vacation mode (10.5) ----

        private void Vacation()
        {
            var store = _w.Store;
            var now = DateTime.Now;
            bool has = store.Data.Days.Any(kv => kv.Value.Mode == DayMode.Vacation && string.CompareOrdinal(kv.Key, store.Key(now)) >= 0);
            var dlg = Layout.Dialog(_w, "Vacation mode");
            var from = new TextBox { Text = "today" };
            var to = new TextBox();
            var cancelVac = new CheckBox { Content = "_Cancel my vacation days from today on", Margin = new Thickness(0, 6, 0, 6), Visibility = has ? Visibility.Visible : Visibility.Collapsed };
            var problem = new TextBlock { Foreground = System.Windows.Media.Brushes.Firebrick };
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            DateTime start = default, end = default;
            bool cancelled = false;
            ok.Click += (_, _) =>
            {
                if (cancelVac.IsChecked == true) { cancelled = true; dlg.Close(); return; }
                var a = Dates.Parse(from.Text, DateTime.Now).When;
                var b = Dates.Parse(to.Text, DateTime.Now).When;
                string? bad = a == null ? "I could not read the first day." : b == null ? "I could not read the last day." : b < a ? "The last day is before the first." : a.Value.Date < DateTime.Now.Date ? "The first day is in the past." : (b.Value - a.Value).TotalDays > 120 ? "That is a very long vacation. Four months at most." : null;
                if (bad != null) { problem.Text = bad; _w.Say(bad, true); (a == null ? from : to).Focus(); return; }
                start = a!.Value.Date; end = b!.Value.Date;
                dlg.Close();
            };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Layout.Row("_First day, like today or 10/20", from, 300));
            panel.Children.Add(Layout.Row("_Last day, like friday or 10/27", to, 300));
            panel.Children.Add(cancelVac);
            panel.Children.Add(problem);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.ShowDialog();
            if (cancelled)
            {
                store.Remember("cancelling vacation");
                foreach (var kv in store.Data.Days.Where(kv => kv.Value.Mode == DayMode.Vacation && string.CompareOrdinal(kv.Key, store.Key(now)) >= 0)) kv.Value.Mode = DayMode.Normal;
                _w.SaveAndRefresh();
                _w.Say("Vacation cancelled. Back to reality, sorry.");
                return;
            }
            if (start == default) return;
            store.Remember("vacation mode");
            int days = 0;
            for (var d = start; d <= end; d = d.AddDays(1)) { store.Data.Day(d).Mode = DayMode.Vacation; days++; }
            _w.SaveAndRefresh();
            _w.Say("Vacation set, " + (days == 1 ? "1 day" : days + " days") + ", " + Words.Day(start, now) + (days > 1 ? " to " + Words.Day(end, now) : "") + ". Today shows nothing, and your streak is protected. Enjoy it.");
        }

        // ---- Checks ----

        public void SelfTest(Store store)
        {
            var now = new DateTime(2026, 10, 12, 9, 0, 0);
            // Weekday plan lines.
            store.Data.Tags.Add(new TagInfo { Name = "PSY" });
            var blocks = Rhythm.Parse("morning PSY reading\nafternoon, stats problems\n\n Evening: read for fun", store.Data.Tags.Select(t => t.Name));
            if (blocks.Count != 3 || blocks[0].When != "morning" || blocks[0].Tag != "PSY" || blocks[0].Label != "PSY reading") throw new Exception("plan line 1 read wrong");
            if (blocks[1].When != "afternoon" || blocks[1].Tag != "stats") throw new Exception("plan line 2 read wrong");
            if (blocks[2].When != "evening" || blocks[2].Tag != "read") throw new Exception("plan line 3 read wrong");
            var lines = Lines("a\r\n  b  \n\n");
            if (lines.Count != 2 || lines[1] != "b") throw new Exception("check-in lines read wrong");

            // Candidates.
            var psy = new TaskItem { Name = "chapter 4", Tag = "PSY", List = store.Data.Lists[0].Name, Status = Status.ThisWeek };
            var psy2 = new TaskItem { Name = "chapter 5", Tag = "psy", List = store.Data.Lists[0].Name, Status = Status.ThisWeek, Due = now.AddDays(1) };
            var other = new TaskItem { Name = "stats", Tag = "STAT", List = store.Data.Lists[0].Name, Status = Status.ThisWeek };
            store.Data.Tasks.AddRange(new[] { psy, psy2, other });
            var c = Rhythm.Candidates(store, "PSY");
            if (c.Count != 2 || c[0] != psy2) throw new Exception("plan candidates wrong");

            // End-of-day reset.
            var old = new TaskItem { Name = "old", Status = Status.Today, StatusSince = now.AddDays(-1), List = store.Data.Lists[0].Name };
            var fresh = new TaskItem { Name = "fresh", Status = Status.Today, StatusSince = now, List = store.Data.Lists[0].Name };
            store.Data.Tasks.AddRange(new[] { old, fresh });
            if (Reset(store, now, "off") != 0 || old.Status != Status.Today) throw new Exception("reset ran when off");
            if (Reset(store, now, "keep") < 1 || old.Status != Status.Today) throw new Exception("keep changed a task");
            int moved = Reset(store, now, "week");
            if (moved < 1 || old.Status != Status.ThisWeek || fresh.Status != Status.Today) throw new Exception("reset moved the wrong tasks");

            // Rough day push.
            var planned = new TaskItem { Name = "planned", Status = Status.ThisWeek, DoDate = now.Date, List = store.Data.Lists[0].Name };
            var soon = new TaskItem { Name = "soon", Status = Status.Today, Due = now.AddHours(5), DueHasTime = true, List = store.Data.Lists[0].Name };
            store.Data.Tasks.AddRange(new[] { planned, soon });
            Push(store, now);
            if (planned.DoDate != now.Date.AddDays(1) || fresh.Status != Status.ThisWeek || soon.Status != Status.Today) throw new Exception("rough day pushed the wrong tasks");
        }
    }

    /// <summary>Options, Settings, Morning and evening: the check-in switch and the end-of-day reset.</summary>
    public sealed class DayFlowPage : ISettingsPage
    {
        private readonly Settings _s;
        private readonly CheckBox _checkIn;
        private readonly ComboBox _reset = new() { ItemsSource = new[] { "Move them back to This week", "Leave them on Today for tomorrow", "Turn the reset off" } };

        public string Name => "Morning and evening";
        public FrameworkElement Content { get; }

        public DayFlowPage(Settings s)
        {
            _s = s;
            _checkIn = new CheckBox { Content = "Ask the morning _check-in on the first start of each day", IsChecked = s.MorningCheckIn, Margin = new Thickness(0, 4, 0, 8) };
            _reset.SelectedIndex = s.EndOfDay switch { "keep" => 1, "off" => 2, _ => 0 };
            var p = new StackPanel();
            p.Children.Add(_checkIn);
            p.Children.Add(Layout.Row("_Unfinished Today tasks at the start of a new day", _reset, 360));
            Content = p;
        }

        public void Apply()
        {
            _s.MorningCheckIn = _checkIn.IsChecked == true;
            _s.EndOfDay = _reset.SelectedIndex switch { 1 => "keep", 2 => "off", _ => "week" };
        }
    }
}
