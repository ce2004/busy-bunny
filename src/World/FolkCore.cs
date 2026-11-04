using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// Shared helpers for the people and stories of Bunny's world (Stage 7, sweep 8.7 and 8.8): the village's clock,
    /// progress worked out from the points log (so Control Z takes it back by itself), text from the content library,
    /// and the few small windows every feature here uses (read a text, a list, a pick, a name).
    /// </summary>
    public static class Folk
    {
        public const string Since = "0.14.0";
        public static readonly Random Rng = new();
        public static MainWindow? W => Engines.Window;

        // ---- Registering ----

        /// <summary>A tool in the toolbox, and a line on the Bunny tab when tab (its few words there) is given.</summary>
        public static BunnyTool Tool(string id, string name, string drawer, string what, Action run, string? tab = null)
        {
            var t = Tools.Register(new BunnyTool(id, name, drawer, what, run) { Since = Since });
            if (tab != null) BunnyTab.Register(name, tab, run, id);
            return t;
        }

        /// <summary>
        /// A part of Visit your bunny that runs only while its tool is on and the visit still has room for an extra line
        /// (GameFeature.MaxVisitExtras). Asking parts (ask true) run first, and only one of them may ask (TryAsk).
        /// A part never opens a window: an ask is a line telling her which tool to answer it in.
        /// </summary>
        public static void Visit(string toolId, Func<MainWindow, DateTime, IEnumerable<string>> part, bool ask = false)
        {
            Func<MainWindow, DateTime, IEnumerable<string>> hook = (w, now) => Tools.IsOn(toolId) && GameFeature.VisitExtrasLeft > 0 ? part(w, now).ToList() : Array.Empty<string>();
            if (ask) GameFeature.VisitHooks.Insert(0, hook); else GameFeature.VisitHooks.Add(hook);
        }

        /// <summary>
        /// A reminder for Visit your bunny (something waiting for her, not news): a few words, folded with the others into
        /// one "Waiting for you" line after how Bunny is, and said at most once a day. Null or empty when nothing waits.
        /// </summary>
        public static void Reminder(string toolId, string key, Func<Store, DateTime, string?> what) =>
            GameFeature.VisitHooks.Add((w, now) =>
            {
                if (Tools.IsOn(toolId) && what(w.Store, now) is string t && t.Trim().Length > 0) Remind(w.Store, key, now, t.Trim());
                return Array.Empty<string>();
            });

        /// <summary>Adds a reminder to this visit's "Waiting for you" line, unless it was already said today.</summary>
        public static void Remind(Store s, string key, DateTime now, string what)
        {
            var said = s.World.GetKept<Dictionary<string, string>>("folk.reminded");
            if (said.TryGetValue(key, out var day) && day == s.Key(now)) return;
            said[key] = s.Key(now);
            s.World.SetKept("folk.reminded", said);
            s.World.Save();
            GameFeature.VisitReminders.Add(what);
        }

        /// <summary>True for the first ask of this visit; every later part must not ask (sweep 12.2: one thing at a time).</summary>
        public static bool TryAsk()
        {
            if (GameFeature.AskedThisVisit) return false;
            GameFeature.AskedThisVisit = true;
            return true;
        }

        /// <summary>A line offered after a finished task (one is spoken per finish; the rest go to What happened today).</summary>
        public static void AfterTask(MainWindow w, string text) { if (text.Trim().Length > 0) Calm.AfterTaskDone(w, Game.Chatter, text.Trim()); }

        /// <summary>True the first time a key is seen, so a milestone is said once however often a task is toggled.</summary>
        public static bool First(Store s, string key)
        {
            var set = s.World.GetKept<HashSet<string>>("folk.said");
            if (!set.Add(key)) return false;
            s.World.SetKept("folk.said", set);
            s.World.Save();
            return true;
        }

        public static bool Seen(Store s, string key) => s.World.GetKept<HashSet<string>>("folk.said").Contains(key);

        // ---- Progress, from the points log ----

        /// <summary>Every finished task, once, with when it was first finished. Un-finishing removes its entry, so this follows undo.</summary>
        public static List<(Guid Id, DateTime When)> Finished(Store s) => s.Data.Log
            .Where(e => e.Kind == "Done" && e.TaskId != null)
            .GroupBy(e => e.TaskId!.Value)
            .Select(g => (g.Key, g.Min(e => e.When)))
            .OrderBy(x => x.Item2).ToList();

        public static int FinishedBetween(Store s, DateTime from, DateTime to) => Finished(s).Count(x => x.When >= from && x.When < to);
        public static int FinishedSince(Store s, DateTime from) => Finished(s).Count(x => x.When >= from);

        private sealed class StartBox { public DateTime When { get; set; } }

        /// <summary>
        /// The day she came to the village: the same first day the seasons calendar uses for the village anniversary
        /// (SeasonCalendar.FirstDay), so there is only ever one anniversary.
        /// </summary>
        public static DateTime Start(Store s)
        {
            var test = s.World.GetKept<StartBox>("folk.start.test");
            return test.When != default ? test.When : SeasonCalendar.FirstDay(s);
        }

        /// <summary>Only for self-tests on their throwaway store: pretends she came on this day.</summary>
        public static void SetStart(Store s, DateTime when) { s.World.SetKept("folk.start.test", new StartBox { When = when.Date }); s.World.Save(); }

        /// <summary>Whole weeks (Monday to Monday) since she came to the village.</summary>
        public static int Weeks(Store s, DateTime now) => Math.Max(0, (int)((Engine.WeekStart(now) - Engine.WeekStart(Start(s))).TotalDays / 7));
        public static int Months(Store s, DateTime now) { var st = Start(s); return Math.Max(0, (now.Year - st.Year) * 12 + now.Month - st.Month); }
        public static string WeekKey(DateTime now) => Engine.WeekStart(now).ToString("yyyy-MM-dd");
        public static string MonthKey(DateTime now) => now.ToString("yyyy-MM");
        /// <summary>Weeks since this semester began (0 in its first week).</summary>
        public static int SemesterWeek(DateTime now) => Math.Max(0, (int)((Engine.WeekStart(now) - Engine.WeekStart(GameFeature.Semester(now).From)).TotalDays / 7));
        /// <summary>A number that goes up by one each semester: spring, summer, fall, then the next spring.</summary>
        public static int SemesterIndex(DateTime now) => now.Year * 3 + (now.Month <= 5 ? 0 : now.Month <= 7 ? 1 : 2);
        public static string Day(DateTime d) => d.ToString("dddd, MMMM d");

        /// <summary>A festival day: the app's fixed holidays, Thanksgiving, the Fourth of July and the first day of each season.</summary>
        public static string? Festival(DateTime d)
        {
            if (RealWorld.Holiday(d) is string h) return h;
            if (d.Month == 7 && d.Day == 4) return "the Fourth of July";
            if (d.Month == 11 && d.DayOfWeek == DayOfWeek.Thursday && d.Day > 21) return "Thanksgiving";
            return (d.Month, d.Day) switch
            {
                (3, 20) => "the first day of spring",
                (6, 21) => "the first day of summer",
                (9, 22) => "the first day of fall",
                (12, 21) => "the first day of winter",
                _ => null,
            };
        }

        /// <summary>A festival today or in the two days before it, so a visit still catches it.</summary>
        public static string? FestivalNear(DateTime now)
        {
            for (int i = 0; i <= 2; i++) if (Festival(now.Date.AddDays(-i)) is string f) return f;
            return null;
        }

        public static (DateTime Day, string What) NextFestival(DateTime now)
        {
            for (int i = 1; i < 400; i++) if (Festival(now.Date.AddDays(i)) is string f) return (now.Date.AddDays(i), f);
            return (now.Date.AddDays(1), "a festival");
        }

        public static bool RoughToday(Store s, DateTime now) => s.Data.PeekDay(now).Mode == DayMode.RoughDay;
        public static bool RestToday(Store s, DateTime now) => s.Data.PeekDay(now).Mode != DayMode.Normal;

        // ---- Text ----

        public sealed class Names { public string Goldie { get; set; } = "Goldie"; public string Partner { get; set; } = ""; }
        public static Names NamesOf(Store s) => s.World.Get<Names>("folk.names");
        public static string Goldie(Store s) => NamesOf(s).Goldie is { Length: > 0 } g ? g : "Goldie";

        /// <summary>Fills {name}, {goldie}, {partner} and any other placeholders given.</summary>
        public static string Fill(Store s, string text, params (string Key, string Value)[] more)
        {
            var n = NamesOf(s);
            text = text.Replace("{name}", s.Data.Game.BunnyName).Replace("{goldie}", Goldie(s))
                .Replace("{partner}", n.Partner.Length > 0 ? n.Partner : "Bunny's new friend");
            foreach (var (k, v) in more) text = text.Replace("{" + k + "}", v);
            text = Regex.Replace(text, @"\b1 (task|piece|thing|year|day|clue)s\b", "1 $1");
            // A line that starts with a filled-in phrase ("a year ago today, ...") still starts with a capital.
            return text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;
        }

        /// <summary>A line from the library, not one of the last 10 used, filled in.</summary>
        public static string Pick(Store s, string topic, params (string Key, string Value)[] more) => Fill(s, Content.Pick(s, topic), more);

        /// <summary>The entry at a place in the list (wrapping round), for things that must stay the same, like this week's letter.</summary>
        public static string Nth(Store s, string topic, int i, params (string Key, string Value)[] more)
        {
            var all = Content.Lines(topic);
            return all.Count == 0 ? "" : Fill(s, all[Mod(i, all.Count)], more);
        }

        public static int Mod(int a, int n) => n <= 0 ? 0 : ((a % n) + n) % n;

        /// <summary>A hash that is the same every run (string.GetHashCode is not).</summary>
        public static int Hash(string text)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in text) { h ^= c; h *= 16777619; }
                return (int)(h & 0x7fffffff);
            }
        }

        private static readonly Dictionary<string, List<Dictionary<string, string>>> _items = new();
        public static List<Dictionary<string, string>> Items(string topic) => _items.TryGetValue(topic, out var have) ? have : _items[topic] = Content.Items(topic);
        public static Dictionary<string, string> Item(string topic, int i) { var all = Items(topic); return all.Count == 0 ? new() : all[Mod(i, all.Count)]; }
        public static string Get(this Dictionary<string, string> item, string key) => item.TryGetValue(key, out var v) ? v : "";

        /// <summary>A described thing, touch and sound first, then looks.</summary>
        public static string Describe(Dictionary<string, string> item)
        {
            var parts = new[] { "touch", "smell", "sound", "looks" }.Select(k => item.Get(k)).Where(v => v.Length > 0).Select(End);
            return string.Join(" ", parts);
        }

        /// <summary>Ends a sentence with a full stop if it has none.</summary>
        public static string End(string s) { s = s.Trim(); return s.Length == 0 || ".!?".Contains(s[^1]) ? s : s + "."; }

        /// <summary>An entry of a BLOCKS file: header lines (key: value), then a body with its paragraphs kept.</summary>
        public sealed record Block(Dictionary<string, string> Head, string Body)
        {
            public string Get(string key) => Head.TryGetValue(key, out var v) ? v : "";
        }

        private static readonly Dictionary<string, List<Block>> _blocks = new();
        private static readonly Regex HeadLine = new(@"^([a-z0-9]+):\s*(.*)$");

        /// <summary>The entries of a BLOCKS file: separated by a line of three equals signs; blank lines inside are paragraphs.</summary>
        public static List<Block> Blocks(string topic)
        {
            if (_blocks.TryGetValue(topic, out var have)) return have;
            var list = new List<Block>();
            string text = App.Resource("content." + topic + ".txt").Replace("\r", "");
            foreach (var part in Regex.Split(text, @"^[ \t]*===[ \t]*$", RegexOptions.Multiline))
            {
                var lines = part.Split('\n').Select(l => l.Trim()).ToList();
                while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
                var head = new Dictionary<string, string>();
                // Header lines only while the block starts with them, before its first blank line.
                while (lines.Count > 0 && HeadLine.Match(lines[0]) is { Success: true } m) { head[m.Groups[1].Value] = m.Groups[2].Value.Trim(); lines.RemoveAt(0); }
                string body = Regex.Replace(string.Join("\n", lines).Trim(), @"\n{3,}", "\n\n");
                if (body.Length > 0 || head.Count > 0) list.Add(new Block(head, body));
            }
            return _blocks[topic] = list;
        }

        public static Block? NthBlock(string topic, int i) { var all = Blocks(topic); return all.Count == 0 ? null : all[Mod(i, all.Count)]; }

        /// <summary>
        /// Fresh writing from Smart Bunny when it can (the key is set, Calm mode is off, under the daily limit), told who
        /// Bunny is now (sweep 12.8 D), or the fallback. The request must be built from visible tasks only.
        /// </summary>
        public static async Task<string> Write(Store s, string featureId, string request, Func<string> fallback)
        {
            string got = "";
            try
            {
                if (SmartBunny.Ready && !Calm.FunPaused && BunnyWrite.MayAsk(s, featureId, DateTime.Now))
                    got = await BunnyWrite.Short(s, featureId, FolkBunny.Persona(s, DateTime.Now) + " " + request, "folk.none");
            }
            catch (Exception e) { ErrorLog.Write("Folk writing for " + featureId + " failed: " + e.GetType().Name); }
            got = (got ?? "").Replace(((char)35).ToString(), "").Replace(((char)42).ToString(), "").Trim();
            return got.Length > 0 ? got : fallback();
        }

        /// <summary>Names of tasks Bunny may see (not private, not archived) finished since a time, for prompts and the radio.</summary>
        public static List<string> VisibleWins(Store s, DateTime from)
        {
            var visible = BunnyWrite.Visible(s).ToDictionary(t => t.Id);
            return Finished(s).Where(x => x.When >= from && visible.ContainsKey(x.Id)).Select(x => visible[x.Id].Name).ToList();
        }

        // ---- Small windows ----

        private static Window Owner(Window? owner) => owner ?? W ?? throw new InvalidOperationException("No window yet.");
        private static Action Keep(Window owner) => owner == W ? W!.KeepFocus() : () => { };

        /// <summary>A text to read: a read-only box, so NVDA can move by line and paragraph. Extra buttons can act on it.</summary>
        public static void Read(Window? owner, string title, string text, params (string Label, Action<Window> Act)[] buttons)
        {
            var o = Owner(owner);
            var back = Keep(o);
            var w = Layout.Dialog(o, title);
            var box = new TextBox
            {
                IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 640, Height = 340,
                Text = text.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            };
            AutomationProperties.SetName(box, title);
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, act) in buttons)
            {
                var b = Layout.Button(label);
                b.Click += (_, _) => act(w);
                row.Children.Add(b);
            }
            var close = Layout.Button("_Close", isCancel: true);
            close.Click += (_, _) => w.Close();
            row.Children.Add(close);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(box);
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) => { box.CaretIndex = 0; box.Focus(); };
            w.ShowDialog();
            back();
        }

        /// <summary>A line of a list window: its text, and what Enter does (null: nothing, it is just to read).</summary>
        public sealed record Row(string Text, Action<Window>? Enter = null);

        /// <summary>
        /// A list she arrows through that stays open: Enter does the line's action, then the list is rebuilt near where
        /// she was. Buttons act and rebuild too. Escape closes; focus goes back where she was.
        /// </summary>
        public static void List(Window? owner, string title, Func<List<Row>> rows, string hint, params (string Label, Action<Window> Act)[] buttons)
        {
            var o = Owner(owner);
            var back = Keep(o);
            var w = Layout.Dialog(o, title);
            var list = new ListBox { Width = 680, Height = 340 };
            VirtualizingPanel.SetIsVirtualizing(list, false);
            ScrollViewer.SetHorizontalScrollBarVisibility(list, ScrollBarVisibility.Disabled);
            AutomationProperties.SetHelpText(list, hint);
            List<Row> shown = new();
            void Fill(int select)
            {
                shown = rows();
                list.Items.Clear();
                foreach (var r in shown)
                {
                    var item = new ListBoxItem { Content = new TextBlock { Text = r.Text, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 } };
                    AutomationProperties.SetName(item, r.Text);
                    list.Items.Add(item);
                }
                AutomationProperties.SetName(list, title + ", " + shown.Count + (shown.Count == 1 ? " line" : " lines"));
                if (w.IsLoaded) TextViewer.Select(list, Math.Clamp(select, 0, Math.Max(0, shown.Count - 1)));
                else if (shown.Count > 0) list.SelectedIndex = Math.Clamp(select, 0, shown.Count - 1);
            }
            void Act(Action<Window> a)
            {
                int at = Math.Max(0, list.SelectedIndex);
                try { a(w); }
                catch (Exception e) { ErrorLog.Write("Folk list " + title + " failed: " + e); Speech.Say(w, "That did not work just now.", true); }
                if (w.IsVisible) Fill(at);
            }
            list.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
                e.Handled = true;
                int i = list.SelectedIndex;
                if (i >= 0 && i < shown.Count && shown[i].Enter is Action<Window> a) Act(a);
            };
            list.MouseDoubleClick += (_, _) => { int i = list.SelectedIndex; if (i >= 0 && i < shown.Count && shown[i].Enter is Action<Window> a) Act(a); };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var (label, act) in buttons)
            {
                var b = Layout.Button(label);
                b.Click += (_, _) => { Act(act); TextViewer.Select(list, Math.Max(0, list.SelectedIndex)); };
                row.Children.Add(b);
            }
            var close = Layout.Button("_Close", isCancel: true);
            close.Click += (_, _) => w.Close();
            row.Children.Add(close);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new Label { Content = "_" + title, Target = list, Padding = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(list);
            panel.Children.Add(new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, MaxWidth = 680, Margin = new Thickness(0, 6, 0, 0) });
            panel.Children.Add(row);
            w.Content = panel;
            Fill(0);
            w.Loaded += (_, _) => TextViewer.Select(list, Math.Max(0, list.SelectedIndex));
            w.ShowDialog();
            back();
        }

        /// <summary>A pick: the index of the line she chose with Enter, or -1 when she pressed Escape.</summary>
        public static int Choose(Window? owner, string title, string prompt, IList<string> options, int select = 0)
        {
            var o = Owner(owner);
            var back = Keep(o);
            var w = Layout.Dialog(o, title);
            var list = TextViewer.LinesList(options, prompt, Math.Min(320, 40 + 34 * options.Count));
            int result = -1;
            void Pick() { if (list.SelectedIndex >= 0) { result = list.SelectedIndex; w.Close(); } }
            list.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.None) { e.Handled = true; Pick(); } };
            list.MouseDoubleClick += (_, _) => Pick();
            var ok = Layout.Button("_Choose", isDefault: true);
            ok.Click += (_, _) => Pick();
            var cancel = Layout.Button("Cancel", isCancel: true);
            cancel.Click += (_, _) => w.Close();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, MaxWidth = 680, Margin = new Thickness(0, 0, 0, 6) });
            panel.Children.Add(list);
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) => TextViewer.Select(list, Math.Clamp(select, 0, Math.Max(0, options.Count - 1)));
            w.ShowDialog();
            back();
            return result;
        }

        /// <summary>A name or a few words she types: the text, or null when she cancelled or left it empty.</summary>
        public static string? Ask(Window? owner, string title, string label, string initial)
        {
            var o = Owner(owner);
            var back = Keep(o);
            var w = Layout.Dialog(o, title);
            var box = new TextBox { Text = initial };
            string? result = null;
            var ok = Layout.Button("OK", isDefault: true);
            ok.Click += (_, _) => { result = box.Text.Trim(); w.Close(); };
            var cancel = Layout.Button("Cancel", isCancel: true);
            cancel.Click += (_, _) => w.Close();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(ok);
            row.Children.Add(cancel);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Layout.Row(label, box, 360));
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
            w.ShowDialog();
            back();
            return string.IsNullOrWhiteSpace(result) ? null : Clean(result);
        }

        /// <summary>Takes out the two characters never used in Busy Bunny's text.</summary>
        public static string Clean(string s) => s.Replace(((char)35).ToString(), "").Replace(((char)42).ToString(), "").Trim();

        /// <summary>Says a line in a window she is in (the main window when none).</summary>
        public static void Say(Window? owner, string text)
        {
            if (owner != null && owner != W) Speech.Say(owner, text, true);
            else W?.Say(text, true);
        }

        public static string Plural(int n, string one, string many) => n + " " + (n == 1 ? one : many);

        /// <summary>Checks every folk content file: no pound sign or asterisk, and enough entries.</summary>
        public static void CheckContent(Action<bool, string> need)
        {
            var names = typeof(App).Assembly.GetManifestResourceNames().Where(n => n.StartsWith("content.folk.")).ToList();
            need(names.Count >= 60, "only " + names.Count + " folk content files");
            foreach (var n in names)
            {
                string text = App.Resource(n);
                need(!text.Contains((char)35) && !text.Contains((char)42), n + " has a pound sign or an asterisk");
                need(!text.Contains("Inez") && !text.Contains("Morgan"), n + " names a real person");
            }
        }
    }
}
