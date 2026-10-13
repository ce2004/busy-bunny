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
    /// The weekly review (spec 9.7) and the backlog sweep (spec 19.8): a short guided walk, one task
    /// at a time, each with a pick menu of what to do and a Next button. Both live in the View menu.
    /// The sweep is offered once a week by a spoken line; it never pops up by itself.
    /// </summary>
    public sealed class Review : IFeature
    {
        public const int StaleDays = 21;
        private MainWindow _w = null!;

        public void Attach(MainWindow w)
        {
            _w = w;
            w.AddMenuItem("View", "_Weekly review...", WeeklyReview, "Ctrl+Shift+W");
            w.AddMenuItem("View", "_Backlog sweep...", BacklogSweep);
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.W, WeeklyReview, "Ctrl+Shift+W: Weekly review, a walk through next week and undated tasks");
            w.Started += Offer;
            w.NewDay += _ => Offer();
        }

        // ---- Who comes up (pure, for the self-test) ----

        public static List<TaskItem> WeeklyCandidates(IEnumerable<TaskItem> tasks, Func<TaskItem, int> rank) =>
            tasks.Where(t => t.Status != Status.Done && (t.Status == Status.NextWeek || t.Due == null))
                .OrderBy(t => t.Status).ThenBy(rank).ThenBy(t => t.Priority == 0 ? 9 : t.Priority).ToList();

        public static List<TaskItem> SweepCandidates(IEnumerable<TaskItem> tasks, DateTime now) =>
            tasks.Where(t => t.Status != Status.Done && t.TouchedAt <= now.AddDays(-StaleDays)).OrderBy(t => t.TouchedAt).ToList();

        public static DateTime WeekStart(DateTime d) => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7));

        // ---- The weekly offer ----

        private void Offer()
        {
            var data = _w.Store.Data;
            var week = WeekStart(DateTime.Now);
            if (data.SweepOfferedWeek == week) return;
            int n = SweepCandidates(data.Tasks, DateTime.Now).Count;
            if (n == 0) return;
            data.SweepOfferedWeek = week;
            _w.Store.Save();
            _w.Say(n + (n == 1 ? " task has" : " tasks have") + " sat untouched for three weeks. Whenever you feel like it: View, Backlog sweep. No pressure, they are not going anywhere.");
        }

        // ---- The two walks ----

        private void WeeklyReview()
        {
            var tasks = WeeklyCandidates(_w.Store.Data.Tasks, _w.Store.Rank);
            if (tasks.Count == 0) { _w.Say("Nothing waiting for review. Suspiciously tidy.", true); return; }
            Walk("Weekly review", tasks, new (string, Action<TaskItem>)[]
            {
                ("Keep as it is", _ => { }),
                ("Move to this week", t => Move(t, Status.ThisWeek)),
                ("Move to next week", t => Move(t, Status.NextWeek)),
                ("Mark done", t => Move(t, Status.Done)),
                ("Delete", Delete),
            }, "Review finished.");
        }

        private void BacklogSweep()
        {
            var tasks = SweepCandidates(_w.Store.Data.Tasks, DateTime.Now);
            if (tasks.Count == 0) { _w.Say("Nothing has been sitting untouched for three weeks. Impressive.", true); return; }
            Walk("Backlog sweep", tasks, new (string, Action<TaskItem>)[]
            {
                ("Keep it", t => t.TouchedAt = DateTime.Now),
                ("Reschedule to Today", t => Move(t, Status.Today)),
                ("Reschedule to This week", t => Move(t, Status.ThisWeek)),
                ("Reschedule to Next week", t => Move(t, Status.NextWeek)),
                ("Reschedule to Waiting on someone", t => Move(t, Status.Waiting)),
                ("Let go: mark done", t => Move(t, Status.Done)),
                ("Let go: delete", Delete),
            }, "Sweep finished.");
        }

        private void Move(TaskItem t, Status s)
        {
            if (s == Status.Done) { _w.SetTaskStatus(t, s); return; }
            t.TouchedAt = DateTime.Now;
            if (t.Status == s) return;
            t.Status = s;
            t.StatusSince = DateTime.Now;
            t.DoneAt = null;
        }

        private void Delete(TaskItem t) => _w.Store.Data.Tasks.Remove(t);

        private void Walk(string title, List<TaskItem> tasks, (string Label, Action<TaskItem> Act)[] actions, string finished)
        {
            var store = _w.Store;
            int at = 0, changed = 0;
            bool remembered = false;
            var shown = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, Width = 420, MinHeight = 60, Margin = new Thickness(0, 0, 0, 6) };
            AutomationProperties.SetName(shown, "Task under review");
            var pick = new ComboBox { ItemsSource = actions.Select(a => a.Label).ToList(), SelectedIndex = 0 };
            var next = Layout.Button("_Next", isDefault: true);
            var stop = Layout.Button("_Stop", isCancel: true);

            var w = Layout.Dialog(_w, title);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(shown);
            panel.Children.Add(Layout.Row("_What to do with it", pick, 420));
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(next);
            buttons.Children.Add(stop);
            panel.Children.Add(buttons);
            w.Content = panel;

            void Show()
            {
                var t = tasks[at];
                shown.Text = (at + 1) + " of " + tasks.Count + ". " + store.Line(t, DateTime.Now, false) + ". List: " + t.List + ".";
                pick.SelectedIndex = 0;
                shown.Focus();
                shown.CaretIndex = 0;
            }
            next.Click += (_, _) =>
            {
                int choice = Math.Max(0, pick.SelectedIndex);
                var t = tasks[at];
                if (choice > 0 || actions[0].Label.StartsWith("Keep it"))
                {
                    // Keep as it is changes nothing; the sweep's Keep it refreshes the touched date.
                    if (!remembered) { store.Remember(title); remembered = true; }
                    actions[choice].Act(t);
                    changed++;
                }
                at++;
                if (at >= tasks.Count) { w.DialogResult = true; return; }
                Show();
            };
            w.Loaded += (_, _) => Show();
            bool finishedAll = w.ShowDialog() == true;
            if (changed > 0) _w.SaveAndRefresh();
            _w.Say((finishedAll ? finished : "Stopped at " + (at + 1) + " of " + tasks.Count + ".") + (changed > 0 ? " " + changed + (changed == 1 ? " task changed." : " tasks changed.") + " Control Z undoes it." : " Nothing changed."), true);
        }

        public void SelfTest(Store store)
        {
            var now = new DateTime(2026, 10, 14, 12, 0, 0);
            var tasks = new List<TaskItem>
            {
                new() { Name = "a", Status = Status.NextWeek, Due = now.AddDays(9), TouchedAt = now },
                new() { Name = "b", Status = Status.ThisWeek, Due = null, TouchedAt = now.AddDays(-30) },
                new() { Name = "c", Status = Status.ThisWeek, Due = now.AddDays(1), TouchedAt = now.AddDays(-22) },
                new() { Name = "d", Status = Status.Done, Due = null, TouchedAt = now.AddDays(-40) },
                new() { Name = "e", Status = Status.Today, Due = now, TouchedAt = now.AddDays(-20) },
            };
            if (string.Concat(WeeklyCandidates(tasks, _ => 0).Select(t => t.Name)) != "ba") throw new Exception("weekly review picks the wrong tasks");
            if (string.Concat(SweepCandidates(tasks, now).Select(t => t.Name)) != "bc") throw new Exception("backlog sweep picks the wrong tasks");
            if (WeekStart(now) != new DateTime(2026, 10, 12)) throw new Exception("week start");
        }
    }
}
