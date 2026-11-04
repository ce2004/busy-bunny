using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Shared windows for the Smarter Bunny extras (Stage 4): a form, a checklist she approves before anything is saved,
    /// a one-question pick list and a big text box. All standard controls with names; Escape closes; the caller keeps
    /// focus with w.KeepFocus().
    /// </summary>
    public static class SmartUi
    {
        public static TextBox Big(string name, double width = 640, double height = 200, string text = "")
        {
            var b = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = width, Height = height };
            AutomationProperties.SetName(b, name);
            return b;
        }

        public static TextBox ReadOnly(string name, string text, double width = 640, double height = 220)
        {
            var b = Big(name, width, height, text);
            b.IsReadOnly = true;
            b.IsReadOnlyCaretVisible = true;
            return b;
        }

        public static TextBlock Intro(string text, double width = 640) =>
            new() { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = width, Margin = new Thickness(0, 0, 0, 6) };

        /// <summary>A window with what build adds, then OK and Cancel (and any extra buttons). True when OK was pressed and validate found nothing.</summary>
        public static bool Form(Window owner, string title, string okText, Action<StackPanel> build, Func<string?> validate, params (string Text, Action<Window> Act)[] extras)
        {
            var w = Layout.Dialog(owner, title);
            var panel = new StackPanel { Margin = new Thickness(14) };
            build(panel);
            var buttons = new WrapPanel();
            var ok = Layout.Button(okText, isDefault: true);
            buttons.Children.Add(ok);
            foreach (var (text, act) in extras)
            {
                var b = Layout.Button(text);
                b.Click += (_, _) => act(w);
                buttons.Children.Add(b);
            }
            buttons.Children.Add(Layout.Button("Cancel", isCancel: true));
            panel.Children.Add(buttons);
            w.Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = SystemParameters.WorkArea.Height - 80, Focusable = false };
            ok.Click += (_, _) =>
            {
                string? problem = validate();
                // An empty problem keeps the window open quietly (it did something and wants her to stay).
                if (problem != null) { if (problem.Length > 0) Speech.Say(w, problem, true); return; }
                w.DialogResult = true;
            };
            w.Loaded += (_, _) => FirstControl(panel)?.Focus();
            return w.ShowDialog() == true;
        }

        private static Control? FirstControl(Panel p)
        {
            foreach (var child in p.Children)
            {
                if (child is Control c && c is not Label && c.Focusable && c.IsEnabled) return c;
                if (child is Panel inner && FirstControl(inner) is Control found) return found;
            }
            return null;
        }

        /// <summary>The result of a checklist: Cancel, Skip this group, or the lines left checked.</summary>
        public sealed class Picked
        {
            public bool Cancelled, Skipped;
            public List<int> Chosen = new();
        }

        /// <summary>
        /// A line per item with a check box: Space checks or unchecks, the arrows move. okText saves the checked ones;
        /// with canSkip there is also a Skip this group button. Nothing is saved here: the caller saves after.
        /// </summary>
        public static Picked Checklist(Window owner, string title, string intro, IReadOnlyList<(string Text, bool On)> items, string okText, bool canSkip = false)
        {
            var result = new Picked();
            var on = items.Select(i => i.On).ToArray();
            var list = new ListBox { Width = 720, Height = Math.Min(360, 40 + 34 * Math.Max(1, items.Count)) };
            VirtualizingPanel.SetIsVirtualizing(list, false);
            AutomationProperties.SetName(list, title + ". Space checks or unchecks a line");
            var boxes = new List<CheckBox>();
            for (int i = 0; i < items.Count; i++)
            {
                int at = i;
                string text = items[i].Text;
                var cb = new CheckBox { Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 660 }, IsChecked = items[i].On };
                AutomationProperties.SetName(cb, text);
                cb.Checked += (_, _) => on[at] = true;
                cb.Unchecked += (_, _) => on[at] = false;
                boxes.Add(cb);
                list.Items.Add(new ListBoxItem { Content = cb, Focusable = false, IsTabStop = false });
            }
            var w = Layout.Dialog(owner, title);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Intro(intro + " " + Count(items.Count, "line") + ".", 720));
            panel.Children.Add(list);
            var buttons = new WrapPanel();
            var ok = Layout.Button(okText, isDefault: true);
            ok.Click += (_, _) =>
            {
                if (!on.Any(x => x) && !canSkip) { Speech.Say(w, "Nothing is checked.", true); return; }
                w.DialogResult = true;
            };
            buttons.Children.Add(ok);
            var all = Layout.Button("Check a_ll");
            all.Click += (_, _) => { foreach (var b in boxes) b.IsChecked = true; Speech.Say(w, "All checked.", true); };
            var none = Layout.Button("Check _none");
            none.Click += (_, _) => { foreach (var b in boxes) b.IsChecked = false; Speech.Say(w, "None checked.", true); };
            buttons.Children.Add(all);
            buttons.Children.Add(none);
            if (canSkip)
            {
                var skip = Layout.Button("_Skip this group");
                skip.Click += (_, _) => { result.Skipped = true; w.DialogResult = true; };
                buttons.Children.Add(skip);
            }
            buttons.Children.Add(Layout.Button("Cancel", isCancel: true));
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) => { if (boxes.Count > 0) boxes[0].Focus(); else ok.Focus(); };
            if (w.ShowDialog() != true) { result.Cancelled = true; return result; }
            if (!result.Skipped) result.Chosen = Enumerable.Range(0, items.Count).Where(i => on[i]).ToList();
            return result;
        }

        /// <summary>
        /// A small window while Bunny works on something she is waiting for: it says what is happening, says "still
        /// reading" every 30 seconds, and its Stop button (or Escape) cancels the work. It closes by itself when the work
        /// ends. Returns the work's result, or throws what the work threw (OperationCanceledException when stopped).
        /// </summary>
        public static async Task<T> Waiting<T>(Window owner, string title, string line, string still, Func<CancellationToken, Task<T>> work)
        {
            using var cts = new CancellationTokenSource();
            var w = Layout.Dialog(owner, title);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Intro(line, 420));
            // A Cancel button: pressing it or Escape closes the window, and closing stops the work.
            var stop = Layout.Button("_Stop", isCancel: true);
            panel.Children.Add(stop);
            w.Content = panel;
            bool done = false;
            var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            timer.Tick += (_, _) => Speech.Say(w, still);
            w.Closing += (_, _) => { if (!done) { try { cts.Cancel(); } catch (ObjectDisposedException) { } Speech.Say(owner, "Stopping Bunny.", true); } };
            w.Loaded += (_, _) => { stop.Focus(); Speech.Say(w, line, true); timer.Start(); };
            var task = work(cts.Token);
            _ = task.ContinueWith(_ => { done = true; if (w.IsVisible) w.Close(); else w.Loaded += (_, _) => w.Close(); }, TaskScheduler.FromCurrentSynchronizationContext());
            if (!task.IsCompleted) w.ShowDialog();
            timer.Stop();
            try { return await task; }
            catch (Exception e) when (cts.IsCancellationRequested && e is not StoppedByHand) { throw new StoppedByHand(); }
        }

        /// <summary>She pressed Stop (or Escape) while Bunny was working.</summary>
        public sealed class StoppedByHand : Exception { public StoppedByHand() : base("Okay, Bunny stopped.") { } }

        /// <summary>One question with a pick list of answers. The index picked, or -1 when she closed it.</summary>
        public static int Choose(Window owner, string title, string question, IReadOnlyList<string> choices)
        {
            var w = Layout.Dialog(owner, title);
            var panel = new StackPanel { Margin = new Thickness(14) };
            panel.Children.Add(Intro(question, 460));
            var list = new ListBox { Height = Math.Min(260, 30 * choices.Count + 10) };
            foreach (var c in choices)
            {
                var item = new ListBoxItem { Content = c };
                AutomationProperties.SetName(item, c);
                list.Items.Add(item);
            }
            list.SelectedIndex = 0;
            panel.Children.Add(Layout.Row(question.Length < 60 ? question : "Your answer", list, 460));
            var buttons = new WrapPanel();
            var ok = Layout.Button("OK", isDefault: true);
            ok.Click += (_, _) => w.DialogResult = true;
            buttons.Children.Add(ok);
            buttons.Children.Add(Layout.Button("Not now", isCancel: true));
            panel.Children.Add(buttons);
            w.Content = panel;
            w.Loaded += (_, _) => TextViewer.Select(list, 0);
            return w.ShowDialog() == true ? list.SelectedIndex : -1;
        }

        public static string Count(int n, string one, string? many = null) => n + " " + (n == 1 ? one : many ?? one + "s");

        /// <summary>Money as she hears it, like $1.25.</summary>
        public static string Money(double dollars) => "$" + Math.Max(0, dollars).ToString("0.00", CultureInfo.InvariantCulture);
    }

    /// <summary>Text helpers for the extras: what goes to Claude (visible tasks only) and the words she hears.</summary>
    public static class SmartText
    {
        /// <summary>How Bunny should sound, from her settings: personality and roast dial.</summary>
        public static string Voice(Settings? cfg)
        {
            cfg ??= Engines.Settings ?? new Settings();
            var who = SmartBunny.Personalities.FirstOrDefault(p => p.Name.Equals(cfg.BunnyPersonality, StringComparison.OrdinalIgnoreCase));
            if (who.Name == null) who = SmartBunny.Personalities[0];
            string roast = Tone.RoastNames[Math.Clamp(cfg.RoastLevel, 0, Tone.RoastNames.Length - 1)];
            return "Sound like Bunny, " + who.Name + ": " + who.How + ". Teasing level: " + roast + ".";
        }

        /// <summary>The names of lists Bunny may hear about: not private, not inside a private list, not archived.</summary>
        public static HashSet<string> VisibleLists(Store s) =>
            s.Data.Lists.Where(l => !SmartBunny.ListPrivate(s, l) && !l.Archived).Select(l => l.Name).ToHashSet();

        /// <summary>Fills {key} placeholders in a content line.</summary>
        public static string Fill(string line, params (string Key, string Value)[] values)
        {
            foreach (var (k, v) in values) line = line.Replace("{" + k + "}", v);
            return line;
        }

        /// <summary>The line with its first letter in capitals.</summary>
        public static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

        /// <summary>The JSON object in an answer from Claude, even with words around it. Null when there is none.</summary>
        public static JsonElement? Json(string answer)
        {
            int a = answer.IndexOf('{'), b = answer.LastIndexOf('}');
            if (a < 0 || b <= a) return null;
            try { return JsonDocument.Parse(answer[a..(b + 1)]).RootElement.Clone(); }
            catch (JsonException) { return null; }
        }

        public static string Str(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? Plain(v.GetString() ?? "") : "";

        public static IEnumerable<JsonElement> Array(JsonElement e, string key) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();

        /// <summary>Text from Claude made safe to show: no Markdown, no pound signs or asterisks, one line, not too long.</summary>
        public static string Plain(string s, int max = 200)
        {
            s = SmartBunny.Clean(s).Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= max ? s : s[..max].TrimEnd() + "...";
        }
    }
}
