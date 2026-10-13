using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Reminders (spec 8.4) and the notification area icon that keeps them, and the hotkeys, working
    /// when the window is closed. A reminder is a balloon from the tray icon, plus a spoken line when
    /// Busy Bunny has focus; then the repeat moves it forward, or clears it.
    /// </summary>
    public sealed class Reminders : IFeature
    {
        /// <summary>True when Exit was chosen (or Windows is shutting down): closing then really closes.</summary>
        public static bool Quitting;

        private MainWindow? _w;
        private System.Windows.Forms.NotifyIcon? _tray;

        public void Attach(MainWindow w)
        {
            _w = w;
            w.MinuteTick += Tick;
            w.SourceInitialized += (_, _) => MakeTray();
            w.Closing += OnClosing;
            w.Closed += (_, _) => { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); _tray = null; } };
            if (Application.Current != null) Application.Current.SessionEnding += (_, _) => Quitting = true;
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings)
        {
            var keep = new CheckBox { Content = "Keep running in the notification area when closed", IsChecked = settings.KeepInTray, Margin = new Thickness(0, 4, 0, 4) };
            var days = new ComboBox { ItemsSource = Enumerable.Range(1, 14).Select(n => n + (n == 1 ? " day" : " days")).ToList(), SelectedIndex = Math.Clamp(settings.FollowUpDays, 1, 14) - 1 };
            var panel = new StackPanel();
            panel.Children.Add(keep);
            panel.Children.Add(Layout.Row("Nudge me to follow up on waiting tasks after", days, 200));
            yield return new Page("Reminders", panel, () =>
            {
                settings.KeepInTray = keep.IsChecked == true;
                settings.FollowUpDays = Math.Max(1, days.SelectedIndex + 1);
            });
        }

        // ---- The icon ----

        private void MakeTray()
        {
            if (_tray != null || _w == null) return;
            try
            {
                System.Drawing.Icon icon;
                try { icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? "") ?? System.Drawing.SystemIcons.Application; }
                catch { icon = System.Drawing.SystemIcons.Application; }
                var menu = new System.Windows.Forms.ContextMenuStrip();
                menu.Items.Add("Open", null, (_, _) => Open());
                menu.Items.Add("Exit", null, (_, _) => { Quitting = true; _w?.Close(); });
                _tray = new System.Windows.Forms.NotifyIcon { Icon = icon, Text = "Busy Bunny", ContextMenuStrip = menu, Visible = true };
                _tray.DoubleClick += (_, _) => Open();
                _tray.BalloonTipClicked += (_, _) => Open();
            }
            catch { _tray = null; }
        }

        /// <summary>A notification (toast-style balloon) from the tray icon. False when there is no icon.</summary>
        public static bool Notify(string title, string text)
        {
            var me = Features.Get<Reminders>();
            if (me?._tray == null) return false;
            try { me._tray.ShowBalloonTip(10000, title, text, System.Windows.Forms.ToolTipIcon.None); return true; }
            catch { return false; }
        }

        /// <summary>Brings the window up, from the tray or hidden.</summary>
        public static void Open()
        {
            var w = Features.Get<Reminders>()?._w;
            if (w == null) return;
            if (!w.IsVisible) w.Show();
            if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
            w.Activate();
            w.Topmost = true; // a plain Activate from the background only flashes the taskbar
            w.Topmost = false;
            w.Focus();
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (Quitting || _tray == null || _w == null || !_w.AppSettings.KeepInTray) return;
            e.Cancel = true;
            _w.Hide();
            if (!_w.AppSettings.TrayHintShown)
            {
                _w.AppSettings.TrayHintShown = true;
                _w.AppSettings.Save();
                Notify("Busy Bunny is still running", "It stays in the notification area so reminders and Control Alt Shift A keep working. Use the tray icon, Exit, to really quit. You can change this in Options, Settings, Reminders.");
            }
        }

        // ---- Reminders ----

        private void Tick()
        {
            if (_w == null) return;
            var now = DateTime.Now;
            var due = _w.Store.Data.Tasks.Where(t => t.RemindAt is DateTime r && r <= now).ToList();
            if (due.Count == 0) return;
            var shout = new List<TaskItem>();
            foreach (var t in due)
            {
                if (t.Status != Status.Done) shout.Add(t);
                Advance(t, now);
            }
            _w.Store.Save();
            if (shout.Count == 0) return;
            string text = shout.Count == 1 ? "Reminder: " + shout[0].Name + "." : shout.Count + " reminders: " + string.Join("; ", shout.Take(4).Select(t => t.Name)) + (shout.Count > 4 ? "; and more." : ".");
            Notify("Busy Bunny reminder", text);
            if (_w.IsActive) _w.Say(text, false);
        }

        /// <summary>Moves a reminder to its next time by RemindRepeat, or clears it when there is no repeat.</summary>
        public static void Advance(TaskItem t, DateTime now)
        {
            if (t.RemindAt is not DateTime at) return;
            var next = Next(t.RemindRepeat, at, now);
            t.RemindAt = next;
            if (next == null) t.RemindRepeat = "";
        }

        /// <summary>
        /// The first time after now that the rule gives, stepping from at and keeping its time of day.
        /// Rules: every day, daily, every weekday, every weekend, every week, every month, every year,
        /// every 3 days, every other week, every Monday, every Tuesday and Thursday. Null when there is
        /// no rule or it is not understood.
        /// </summary>
        public static DateTime? Next(string rule, DateTime at, DateTime now)
        {
            var step = Step(rule);
            if (step == null) return null;
            var d = at;
            for (int i = 0; i < 5000 && d <= now; i++)
            {
                var n = step(d);
                if (n <= d) return null;
                d = n;
            }
            return d > now ? d : null;
        }

        private static Func<DateTime, DateTime>? Step(string rule)
        {
            string r = rule.Trim().ToLowerInvariant();
            if (r.Length == 0) return null;
            var words = Regex.Matches(r, "[a-z]+").Select(m => m.Value).ToList();
            if (words.Contains("weekday") || words.Contains("weekdays")) return d => NextDay(d, x => x.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday));
            if (words.Contains("weekend") || words.Contains("weekends")) return d => NextDay(d, x => x.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday);
            var days = Enum.GetValues<DayOfWeek>().Where(dw =>
            {
                string full = dw.ToString().ToLowerInvariant();
                return words.Any(w => w.Length >= 3 && (full.StartsWith(w) || w == full + "s"));
            }).ToHashSet();
            if (days.Count > 0) return d => NextDay(d, x => days.Contains(x.DayOfWeek));
            int n = 1;
            var m = Regex.Match(r, @"\b(\d+)\b");
            if (m.Success) n = Math.Max(1, int.Parse(m.Groups[1].Value));
            else if (words.Contains("other")) n = 2;
            if (words.Any(w => w is "day" or "days" or "daily" or "everyday")) return d => d.AddDays(n);
            if (words.Any(w => w is "week" or "weeks" or "weekly")) return d => d.AddDays(7 * n);
            if (words.Any(w => w is "month" or "months" or "monthly")) return d => d.AddMonths(n);
            if (words.Any(w => w is "year" or "years" or "yearly" or "annually")) return d => d.AddYears(n);
            return null;
        }

        private static DateTime NextDay(DateTime d, Func<DateTime, bool> ok)
        {
            var x = d.AddDays(1);
            for (int i = 0; i < 7 && !ok(x); i++) x = x.AddDays(1);
            return x;
        }

        public void SelfTest(Store store)
        {
            var mon = new DateTime(2026, 10, 12, 9, 0, 0); // a Monday
            void Want(string rule, DateTime at, DateTime now, DateTime? want)
            {
                var got = Next(rule, at, now);
                if (got != want) throw new Exception("reminder repeat '" + rule + "' gave " + got + ", wanted " + want);
            }
            Want("every day", mon, mon, mon.AddDays(1));
            Want("daily", mon, mon.AddDays(3).AddHours(1), mon.AddDays(4));
            Want("every Monday", mon, mon, mon.AddDays(7));
            Want("every Tuesday and Thursday", mon, mon, mon.AddDays(1));
            Want("every weekday", mon.AddDays(4), mon.AddDays(4), mon.AddDays(7));
            Want("every other week", mon, mon, mon.AddDays(14));
            Want("every 3 days", mon, mon, mon.AddDays(3));
            Want("every month", mon, mon, mon.AddMonths(1));
            Want("", mon, mon, null);
            Want("whenever", mon, mon, null);
            var t = new TaskItem { RemindAt = mon, RemindRepeat = "every day" };
            Advance(t, mon.AddMinutes(1));
            if (t.RemindAt != mon.AddDays(1)) throw new Exception("repeating reminder did not move");
            t = new TaskItem { RemindAt = mon };
            Advance(t, mon.AddMinutes(1));
            if (t.RemindAt != null) throw new Exception("one-off reminder was not cleared");
        }

        /// <summary>A simple Settings page.</summary>
        public sealed class Page : ISettingsPage
        {
            private readonly Action _apply;
            public Page(string name, FrameworkElement content, Action apply) { Name = name; Content = content; _apply = apply; }
            public string Name { get; }
            public FrameworkElement Content { get; }
            public void Apply() => _apply();
        }
    }
}
