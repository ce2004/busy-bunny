using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>One block of a weekday plan: a time of day and a tag, like morning PSY reading.</summary>
    public sealed record PlanBlock(string When, string Tag, string Label);

    /// <summary>
    /// The weekday rhythm (spec section 11). Each weekday has a few lines in the plan, like
    /// morning PSY reading. Each morning the open tasks with that tag are suggested for Today, and
    /// each block can be accepted, swapped for another task, or skipped. Never forced.
    /// </summary>
    public static class Rhythm
    {
        private static readonly string[] Times = { "early morning", "morning", "midday", "noon", "lunch", "afternoon", "evening", "night", "tonight" };

        /// <summary>Reads lines like "morning PSY reading" or "afternoon, stats problems". The tag is the known tag found in the line (the first word after the time when none matches).</summary>
        public static List<PlanBlock> Parse(string text, IEnumerable<string> knownTags)
        {
            var tags = knownTags.Where(t => t.Length > 0).OrderByDescending(t => t.Length).ToList();
            var result = new List<PlanBlock>();
            foreach (string raw in (text ?? "").Split('\n'))
            {
                string line = raw.Trim().TrimEnd(';', '.');
                if (line.Length == 0) continue;
                string when = "";
                foreach (string t in Times.OrderByDescending(x => x.Length))
                    if (line.StartsWith(t, StringComparison.OrdinalIgnoreCase) && (line.Length == t.Length || !char.IsLetterOrDigit(line[t.Length])))
                    {
                        when = t;
                        line = line[t.Length..].TrimStart(' ', ',', ':', '-');
                        break;
                    }
                if (line.Length == 0) continue;
                string tag = "";
                foreach (string t in tags)
                {
                    int i = IndexOfWord(line, t);
                    if (i >= 0) { tag = t; break; }
                }
                if (tag.Length == 0) tag = line.Split(' ', ',')[0];
                result.Add(new PlanBlock(when, tag, line));
            }
            return result;
        }

        private static int IndexOfWord(string line, string word)
        {
            int at = 0;
            while ((at = line.IndexOf(word, at, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool left = at == 0 || !char.IsLetterOrDigit(line[at - 1]);
                bool right = at + word.Length == line.Length || !char.IsLetterOrDigit(line[at + word.Length]);
                if (left && right) return at;
                at += word.Length;
            }
            return -1;
        }

        public static string PlanOf(Store store, DayOfWeek d) =>
            store.Data.WeekdayPlans.TryGetValue(d.ToString(), out var p) ? p : "";

        /// <summary>The open tasks with this tag that could go on Today, most pressing first.</summary>
        public static List<TaskItem> Candidates(Store store, string tag) => store.Data.Tasks
            .Where(t => t.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase) && t.Status is Status.ThisWeek or Status.NextWeek)
            .OrderBy(t => t.Status == Status.ThisWeek ? 0 : 1)
            .ThenBy(t => t.Due ?? DateTime.MaxValue)
            .ThenBy(store.Rank)
            .ThenBy(t => t.Priority == 0 ? 99 : t.Priority)
            .ToList();

        /// <summary>Goes through today's plan block by block. Quiet when there is no plan or nothing matches.</summary>
        public static void Suggest(MainWindow w, bool manual)
        {
            var store = w.Store;
            var now = DateTime.Now;
            string plan = PlanOf(store, now.DayOfWeek);
            var blocks = Parse(plan, store.Data.Tags.Select(t => t.Name));
            if (blocks.Count == 0)
            {
                if (manual) w.Say("There is no plan for " + now.DayOfWeek + ". Add one in Options, Settings, Weekday plans.", true);
                return;
            }
            var chosen = new List<TaskItem>();
            int shown = 0;
            foreach (var b in blocks)
            {
                var cands = Candidates(store, b.Tag).Where(t => !chosen.Contains(t)).ToList();
                if (cands.Count == 0) continue;
                shown++;
                var pick = Ask(w, now, b, cands, store);
                if (pick != null) chosen.Add(pick);
            }
            if (shown == 0)
            {
                if (manual) w.Say(now.DayOfWeek + " plan: nothing open matches its tags. Convenient, or suspicious.", true);
                return;
            }
            if (chosen.Count > 0)
            {
                store.Remember("accepting the " + now.DayOfWeek + " plan");
                foreach (var t in chosen) { t.Status = Status.Today; t.StatusSince = now; t.TouchedAt = now; }
                w.SaveAndRefresh();
                w.Say(chosen.Count + (chosen.Count == 1 ? " task" : " tasks") + " moved to Today from the " + now.DayOfWeek + " plan.");
            }
            else w.Say("Plan skipped. Today stays as it was.");
        }

        private static TaskItem? Ask(MainWindow w, DateTime now, PlanBlock b, List<TaskItem> cands, Store store)
        {
            var dlg = Layout.Dialog(w, now.DayOfWeek + " plan" + (b.When.Length > 0 ? ", " + b.When : ""));
            int index = 0;
            TaskItem? result = null;
            var about = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Text = now.DayOfWeek + " plan, " + (b.When.Length > 0 ? b.When + ": " : "") + b.Label + ". " + cands.Count + " open with the tag " + b.Tag + ".",
            };
            AutomationProperties.SetName(about, "The block");
            var pickBox = new ComboBox { ItemsSource = cands.Select(t => store.Line(t, now)).ToList(), SelectedIndex = 0 };
            var row = Layout.Row("_Suggested task", pickBox, 480);
            var accept = Layout.Button("_Accept", isDefault: true);
            var swap = Layout.Button("S_wap");
            var skip = Layout.Button("S_kip", isCancel: true);
            accept.Click += (_, _) => { result = cands[Math.Max(0, pickBox.SelectedIndex)]; dlg.DialogResult = true; };
            swap.Click += (_, _) => { index = (pickBox.SelectedIndex + 1) % cands.Count; pickBox.SelectedIndex = index; pickBox.Focus(); w.Say(store.Line(cands[index], now), true); };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal };
            buttons.Children.Add(accept);
            buttons.Children.Add(swap);
            buttons.Children.Add(skip);
            var panel = new StackPanel { Margin = new Thickness(14) };
            about.Width = 480;
            panel.Children.Add(about);
            panel.Children.Add(row);
            panel.Children.Add(buttons);
            dlg.Content = panel;
            dlg.Loaded += (_, _) => about.Focus();
            dlg.ShowDialog();
            return result;
        }
    }

    /// <summary>Options, Settings, Weekday plans: a weekday pick menu and its blocks, one per line.</summary>
    public sealed class WeekdayPage : ISettingsPage
    {
        private readonly Store _store;
        private readonly Dictionary<string, string> _plans;
        private readonly ComboBox _day = new();
        private readonly TextBox _text = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Height = 150 };
        private string _shown;

        public string Name => "Weekday plans";
        public FrameworkElement Content { get; }

        public WeekdayPage(Store store)
        {
            _store = store;
            _plans = new Dictionary<string, string>(store.Data.WeekdayPlans);
            _day.ItemsSource = Enum.GetNames<DayOfWeek>().Skip(1).Concat(new[] { "Sunday" }).ToList();
            _shown = DateTime.Now.DayOfWeek.ToString();
            _day.SelectedItem = _shown;
            _text.Text = _plans.TryGetValue(_shown, out var t) ? t : "";
            _day.SelectionChanged += (_, _) =>
            {
                Stash();
                _shown = (string)_day.SelectedItem;
                _text.Text = _plans.TryGetValue(_shown, out var p) ? p : "";
            };
            var help = new TextBox
            {
                IsReadOnly = true,
                TextWrapping = TextWrapping.Wrap,
                Width = 480,
                Text = "One block per line: a time of day, then a tag and what to do, like morning PSY reading. Each morning Busy Bunny suggests Today from that day's plan, and you accept, swap or skip each block.",
            };
            AutomationProperties.SetName(help, "How plans work");
            var p2 = new StackPanel();
            p2.Children.Add(help);
            p2.Children.Add(Layout.Row("_Weekday", _day, 200));
            p2.Children.Add(Layout.Row("_Blocks, one per line", _text, 480));
            Content = p2;
        }

        private void Stash()
        {
            string text = _text.Text.Replace("\r", "").Trim();
            if (text.Length == 0) _plans.Remove(_shown); else _plans[_shown] = text;
        }

        public void Apply()
        {
            Stash();
            _store.Data.WeekdayPlans = new Dictionary<string, string>(_plans);
        }
    }
}
