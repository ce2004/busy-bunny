using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// Shared helpers for the Games and the Wins and records tools (Stage 6): world bags, the points a task earned,
    /// a capped extra for a feature's own multiplier, streak math, and three small windows (a pick menu, a text box and
    /// a turn-by-turn game) so every tool stays short. Every window is standard controls, named, Escape closes.
    /// </summary>
    public static class GamesFunKit
    {
        // ---- State and points ----

        public static T Bag<T>(Store s, string id) where T : new() => s.World.Get<T>(id);
        public static void Put<T>(Store s, string id, T value) { s.World.Set(id, value); s.World.Save(); }

        public static string Plural(int n, string one, string? many = null) => n.ToString("N0") + " " + (n == 1 ? one : many ?? one + "s");

        public static bool Finished(TaskItem t) => t.Status is Status.Done or Status.DoneNotSubmitted;

        /// <summary>The Done points this task earned (half for Done not submitted), the base every bonus works from.</summary>
        public static int BasePoints(Store s, TaskItem t)
        {
            int got = s.Data.Log.Where(e => e.Kind == "Done" && e.TaskId == t.Id).Sum(e => e.Points);
            return got > 0 ? got : Engine.PointsFor(t, s.ListOf(t), half: t.Status == Status.DoneNotSubmitted);
        }

        /// <summary>
        /// The extra points a feature's own multiplier (golden task 3, a combo 1.3) adds to base points, at most
        /// Ledger.MaxBoost (4 times) in all. Pay it with Ledger.PayExtra: that keeps every feature's extras for one task
        /// together under the same 4 times ceiling, so nothing here counts the running multipliers.
        /// </summary>
        public static int Extra(Store s, int basePoints, double mine, DateTime now) =>
            (int)Math.Round(basePoints * (Math.Min(Ledger.MaxBoost, Math.Max(1, mine)) - 1));

        /// <summary>Tasks she may hear about here: on a list that is not archived.</summary>
        public static IEnumerable<TaskItem> Live(Store s) => s.Data.Tasks.Where(t => !s.ListOf(t).Archived);

        /// <summary>Tasks finished on a day (Done or Done not submitted), on lists that are not archived.</summary>
        public static List<TaskItem> DoneOn(Store s, DateTime day) => Live(s).Where(t => Finished(t) && t.DoneAt?.Date == day.Date).ToList();

        public static string Key(DateTime d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        public static DateTime? Day(string key) =>
            DateTime.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;

        public static bool RestDay(Data d, DateTime day) => d.Days.TryGetValue(Key(day), out var i) && i != null && i.Mode is DayMode.Vacation or DayMode.RoughDay;

        /// <summary>
        /// Days in a row from a set of day keys: the run going now and the longest. Today is not over yet, so a day
        /// without one does not break it, and vacation and rough days never do.
        /// </summary>
        public static (int Current, int Best) Streak(ICollection<string> days, Data d, DateTime today)
        {
            var dates = days.Select(Day).OfType<DateTime>().Where(x => x <= today.Date).ToList();
            if (dates.Count == 0) return (0, 0);
            var set = dates.Select(Key).ToHashSet();
            int cur = 0, best = 0;
            for (var day = dates.Min(); day <= today.Date; day = day.AddDays(1))
            {
                if (set.Contains(Key(day))) { cur++; best = Math.Max(best, cur); }
                else if (day == today.Date || RestDay(d, day)) continue;
                else cur = 0;
            }
            return (cur, best);
        }

        /// <summary>Karma points earned on a day up to a time of day.</summary>
        public static int PointsBy(Data d, DateTime day, TimeSpan upTo) =>
            d.Log.Where(e => e.When.Date == day.Date && e.When.TimeOfDay <= upTo && Engine.KarmaKinds.Contains(e.Kind)).Sum(e => e.Points);

        // ---- Windows ----

        private static StackPanel Panel(double width = 460) => new() { Margin = new Thickness(14), Width = width };
        private static TextBlock Words(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) };

        /// <summary>
        /// A pick menu: a list she arrows through, Enter picks, Escape cancels. The intro is said when it opens.
        /// Returns the index picked, or minus 1.
        /// </summary>
        public static int Pick(Window owner, string title, string label, IReadOnlyList<string> choices, int selected = 0, string? intro = null)
        {
            if (choices.Count == 0) return -1;
            var w = Layout.Dialog(owner, title);
            var panel = Panel();
            if (intro != null) panel.Children.Add(Words(intro));
            var list = new ListBox { MaxHeight = 340 };
            foreach (var c in choices) list.Items.Add(new ListBoxItem { Content = c });
            // The question or clue as the list's description, so NVDA can read it again (NVDA plus Tab) if focus speech cut the intro.
            if (intro != null) AutomationProperties.SetHelpText(list, intro);
            panel.Children.Add(Layout.Row(label, list, 460));
            int result = -1;
            void Choose()
            {
                if (list.SelectedIndex < 0) return;
                result = list.SelectedIndex;
                w.DialogResult = true;
            }
            list.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Choose(); } };
            list.MouseDoubleClick += (_, _) => Choose();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ok = Layout.Button("OK", isDefault: true);
            ok.Click += (_, _) => Choose();
            row.Children.Add(ok);
            row.Children.Add(Layout.Button("Cancel", isCancel: true));
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) =>
            {
                TextViewer.Select(list, Math.Clamp(selected, 0, choices.Count - 1));
                if (intro != null) Speech.Say(w, intro);
            };
            w.ShowDialog();
            return result;
        }

        /// <summary>A text box with OK and Cancel. Returns the trimmed text, or null when she cancelled.</summary>
        public static string? Ask(Window owner, string title, string label, string initial = "", string? intro = null, bool allowEmpty = false)
        {
            var w = Layout.Dialog(owner, title);
            var panel = Panel();
            if (intro != null) panel.Children.Add(Words(intro));
            var box = new TextBox { Text = initial };
            panel.Children.Add(Layout.Row(label, box, 460));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var ok = Layout.Button("OK", isDefault: true);
            string? result = null;
            ok.Click += (_, _) =>
            {
                if (!allowEmpty && box.Text.Trim().Length == 0) { Speech.Say(w, "Type something first, or press Escape.", true); box.Focus(); return; }
                result = box.Text.Trim();
                w.DialogResult = true;
            };
            row.Children.Add(ok);
            row.Children.Add(Layout.Button("Cancel", isCancel: true));
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); if (intro != null) Speech.Say(w, intro); };
            w.ShowDialog();
            return result;
        }

        /// <summary>Lines to arrow through (TextViewer), first line first.</summary>
        public static void Read(Window owner, string title, IReadOnlyList<string> lines) => TextViewer.Show(owner, title, lines);

        /// <summary>A move in a turn-by-turn game: its button text, and what it does (the line to say, or null to stay quiet).</summary>
        public sealed record Move(string Label, Func<string?> Act);

        /// <summary>
        /// A small game with no timer: buttons for her moves, and a list of what happened, newest first, that she can
        /// arrow back through. Each move's line is said at once. Nothing happens until she presses something.
        /// </summary>
        public static void Play(Window owner, string title, string intro, IReadOnlyList<Move> moves)
        {
            var w = Layout.Dialog(owner, title);
            var panel = Panel(480);
            panel.Children.Add(Words(intro));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            var history = new ListBox { Height = 180 };
            var first = (Button?)null;
            foreach (var m in moves)
            {
                var b = Layout.Button(m.Label);
                first ??= b;
                b.Click += (_, _) =>
                {
                    string? line;
                    try { line = m.Act(); }
                    catch (Exception e) { ErrorLog.Write(title + " failed: " + e); line = "That did not work just now."; }
                    if (string.IsNullOrEmpty(line)) return;
                    history.Items.Insert(0, new ListBoxItem { Content = line });
                    Speech.Say(w, line, true);
                };
                row.Children.Add(b);
            }
            row.Children.Add(Layout.Button("Close", isCancel: true));
            panel.Children.Add(row);
            panel.Children.Add(Layout.Row("_What happened, newest first", history, 480));
            w.Content = panel;
            w.Loaded += (_, _) => { first?.Focus(); Speech.Say(w, intro); };
            w.ShowDialog();
        }

        /// <summary>The Engine's random numbers for games (a field, so the self-test can seed it).</summary>
        public static Random Rng { get; set; } = new();
    }
}
