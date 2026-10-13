using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// Gentle nudges, each spoken once a day at most: tasks sitting in Done, not submitted for over a
    /// day (spec 15.8) and tasks Waiting on someone for over N days (spec 19.7). Also Push back a day
    /// (spec 15.6), which counts the pushes and on the third asks what is really going on.
    /// </summary>
    public sealed class Nudges : IFeature
    {
        private MainWindow? _w;

        public void Attach(MainWindow w)
        {
            _w = w;
            w.AddMenuItem("Task", "Push back a _day", PushBackChosen, "Ctrl+Shift+P");
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.P, PushBackChosen, "Push the chosen task back a day: Control Shift P");
            w.Started += () => w.Dispatcher.BeginInvoke(Nudge, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            w.NewDay += _ => Nudge();
        }

        // ---- The rules, apart from the screen so the self-test can check them ----

        public static List<TaskItem> DoneNotSubmittedLate(IEnumerable<TaskItem> tasks, DateTime now) =>
            tasks.Where(t => t.Status == Status.DoneNotSubmitted && t.StatusSince is DateTime s && now - s > TimeSpan.FromDays(1)).ToList();

        public static List<TaskItem> WaitingLate(IEnumerable<TaskItem> tasks, DateTime now, int days) =>
            tasks.Where(t => t.Status == Status.Waiting && t.StatusSince is DateTime s && now - s > TimeSpan.FromDays(Math.Max(1, days))).ToList();

        /// <summary>Moves the do date, else the due date, else sets a do date for tomorrow; counts the push.</summary>
        public static void PushBack(TaskItem t, DateTime now)
        {
            if (t.DoDate is DateTime d) t.DoDate = d.AddDays(1);
            else if (t.Due is DateTime due) t.Due = due.AddDays(1);
            else t.DoDate = now.Date.AddDays(1);
            t.Snoozes++;
            t.TouchedAt = now;
        }

        private static string Names(List<TaskItem> l) =>
            string.Join(", ", l.Take(3).Select(t => t.Name)) + (l.Count > 3 ? " and " + (l.Count - 3) + " more" : "");

        // ---- Nudging ----

        private void Nudge()
        {
            if (_w == null) return;
            var now = DateTime.Now;
            string today = now.ToString("yyyy-MM-dd");
            var dates = _w.AppSettings.NudgeDates;
            bool changed = false;
            foreach (var k in dates.Where(kv => kv.Value != today).Select(kv => kv.Key).ToList()) { dates.Remove(k); changed = true; }

            var tasks = _w.Store.Data.Tasks;
            var late = DoneNotSubmittedLate(tasks, now);
            if (late.Count > 0 && !dates.ContainsKey("done"))
            {
                dates["done"] = today;
                changed = true;
                _w.Say(late.Count == 1
                    ? "Nudge: " + late[0].Name + " has been done but not submitted for over a day. Submitting is the last, small step."
                    : "Nudge: " + late.Count + " tasks are done but not submitted for over a day, like " + Names(late) + ". Submitting is the last, small step.");
            }

            var waiting = WaitingLate(tasks, now, _w.AppSettings.FollowUpDays).Where(t => !dates.ContainsKey("wait:" + t.Id)).ToList();
            if (waiting.Count > 0)
            {
                foreach (var t in waiting) dates["wait:" + t.Id] = today;
                changed = true;
                _w.Say(waiting.Count == 1
                    ? "Nudge: " + waiting[0].Name + " has been waiting on someone for over " + _w.AppSettings.FollowUpDays + " days. Time to follow up, politely."
                    : "Nudge: " + waiting.Count + " tasks have been waiting on someone for over " + _w.AppSettings.FollowUpDays + " days, like " + Names(waiting) + ". Time to follow up, politely.");
            }
            if (changed) _w.AppSettings.Save();
        }

        // ---- Push back a day ----

        private void PushBackChosen()
        {
            if (_w == null) return;
            if (_w.ChosenTask is not TaskItem t) { _w.Say("Choose a task first, then push it back.", true); return; }
            if (t.Status == Status.Done) { _w.Say(t.Name + " is already done. Nothing to push back, which is the good kind of nothing.", true); return; }
            _w.Store.Remember("pushing back " + t.Name);
            PushBack(t, DateTime.Now);
            string when = t.DoDate is DateTime d ? "do date " + Words.Day(d, DateTime.Now) : "due " + Words.Day(t.Due!.Value, DateTime.Now);
            _w.SaveAndRefresh();
            _w.Say("Pushed " + t.Name + " back a day, now " + when + ". Pushed " + t.Snoozes + (t.Snoozes == 1 ? " time." : " times."), true);
            if (t.Snoozes >= 3) Ask(t);
        }

        private void Ask(TaskItem t)
        {
            if (_w == null) return;
            var w = Layout.Dialog(_w, "Pushed back again");
            var choices = new[] { "Break it down into smaller steps", "Change it", "Drop it", "Keep it as it is" };
            var pick = new ComboBox { ItemsSource = choices, SelectedIndex = 0 };
            var ok = Layout.Button("OK", isDefault: true);
            var cancel = Layout.Button("Cancel", isCancel: true);
            ok.Click += (_, _) => w.DialogResult = true;
            var text = new TextBlock { Text = t.Name + " has now been pushed back " + t.Snoozes + " times. That usually means it is too big or too vague, not that you are lazy. What now?", TextWrapping = TextWrapping.Wrap, MaxWidth = 440, Margin = new Thickness(0, 0, 0, 6) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            var body = new StackPanel { Margin = new Thickness(14) };
            body.Children.Add(text);
            body.Children.Add(Layout.Row("What _now", pick, 320));
            body.Children.Add(buttons);
            w.Content = body;
            w.Loaded += (_, _) => { _w.Say(text.Text, true); pick.Focus(); };
            if (w.ShowDialog() != true) return;
            switch (pick.SelectedIndex)
            {
                case 0:
                case 1:
                    _w.OpenEditTask();
                    break;
                case 2:
                    _w.Store.Remember("dropping " + t.Name);
                    _w.Store.Data.Tasks.Remove(t);
                    _w.SaveAndRefresh();
                    _w.Say("Dropped " + t.Name + ". Control Z brings it back if you change your mind.", true);
                    break;
                default:
                    _w.Say("Kept " + t.Name + " as it is.", true);
                    break;
            }
        }

        public void SelfTest(Store store)
        {
            var now = new DateTime(2026, 10, 12, 9, 0, 0);
            var a = new TaskItem { Name = "a", Status = Status.DoneNotSubmitted, StatusSince = now.AddDays(-2) };
            var b = new TaskItem { Name = "b", Status = Status.DoneNotSubmitted, StatusSince = now.AddHours(-5) };
            var c = new TaskItem { Name = "c", Status = Status.Waiting, StatusSince = now.AddDays(-4) };
            var d = new TaskItem { Name = "d", Status = Status.Waiting, StatusSince = now.AddDays(-2) };
            var all = new[] { a, b, c, d };
            if (DoneNotSubmittedLate(all, now).Single() != a) throw new Exception("done but not submitted nudge picked the wrong tasks");
            if (WaitingLate(all, now, 3).Single() != c) throw new Exception("waiting nudge picked the wrong tasks");
            if (WaitingLate(all, now, 1).Count != 2) throw new Exception("waiting nudge ignored the day setting");
            var t = new TaskItem { Due = now };
            PushBack(t, now);
            if (t.Due != now.AddDays(1) || t.Snoozes != 1) throw new Exception("push back did not move the due date");
            t.DoDate = now.Date;
            PushBack(t, now);
            if (t.DoDate != now.Date.AddDays(1) || t.Due != now.AddDays(1) || t.Snoozes != 2) throw new Exception("push back did not move the do date");
            var n = new TaskItem();
            PushBack(n, now);
            if (n.DoDate != now.Date.AddDays(1)) throw new Exception("push back without dates did not set tomorrow");
        }
    }
}
