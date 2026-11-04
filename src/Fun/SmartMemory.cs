using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Xml.Linq;
using Microsoft.Win32;

namespace BusyBunny
{
    /// <summary>
    /// What Bunny knows about her, and how it changes (sweep 13.5 and 12.8 B and F).
    /// About me: a page in Settings where she imports a plain text or Word file about herself, sees line by line what
    /// will be remembered before it is saved, and can hear, change, export or delete any line. It is kept only on this
    /// computer, in the world file next to her tasks, never in the code. Claude gets only the lines a reply needs,
    /// through Talk to Bunny's about_me tool and the few lines about humor that fresh writing uses.
    /// The notes she tells Bunny (be more sarcastic, stop that joke) are Smarter Bunny's own notes; the remember tool
    /// now keeps those too, and she reads and forgets them on the Smarter Bunny page.
    /// Bunny's monthly check-in: once a month Bunny says how it changed (new notes, forgotten notes, what it learned
    /// from her tasks), and the check-in window lets her keep, undo or reset each change.
    /// </summary>
    public sealed class SmartMemory : IFeature
    {
        public const string AboutId = "smart.aboutme", GrowthId = "smart.growth", PageName = "About me";

        public sealed class AboutMe
        {
            public List<string> Lines { get; set; } = new();
            public bool BunnyMayUse { get; set; } = true;
        }

        /// <summary>How Bunny was when the month's check-in last settled: the baseline its changes are measured from.</summary>
        public sealed class Baseline
        {
            public string Month { get; set; } = "";
            public string Since { get; set; } = "";
            public List<string> Notes { get; set; } = new();
            public string Profile { get; set; } = "";
            public string AnnouncedMonth { get; set; } = "";
        }

        /// <summary>One way Bunny changed since the baseline.</summary>
        public sealed record Change(string Line, Action<Store> Undo, Action<Baseline> Keep);

        private MainWindow _w = null!;

        public void Attach(MainWindow w)
        {
            _w = w;
            Tools.Register(new BunnyTool("aboutme", "About me", Drawers.Notes, "What you told Busy Bunny about yourself: import a file, hear it, change or delete it.", () => w.OpenSettingsAt(PageName)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool("bunnycheckin", "Bunny's monthly check-in", Drawers.Notes, "How Bunny changed this month; keep, undo or reset each change.", ShowCheckIn) { Since = "0.14.0" });
            GameEvents.DayStarted += day => MonthStart(day);
        }

        // ---- About me ----

        public static AboutMe Get(Store s) => s.World.Get<AboutMe>(AboutId);

        /// <summary>Splits a file's text into the lines to remember: no blanks, no bullets or numbers, no Markdown marks.</summary>
        public static List<string> SplitLines(string text) => text.Replace("\r", "").Split('\n')
            .Select(l => Regex.Replace(l.Trim(), @"^(?:[-•]\s+|\d{1,3}[.)]\s+)", "").Replace(((char)42).ToString(), "").Replace(((char)35).ToString(), "").Trim())
            .Where(l => l.Length > 1).Select(l => l.Length > 400 ? l[..400] : l).Distinct().ToList();

        public const long MaxFileBytes = 1_000_000, MaxDocxXmlBytes = 5_000_000;
        public const int MaxImportLines = 300;

        /// <summary>A problem reading her file, in a line she can act on.</summary>
        public sealed class ReadProblem : Exception { public ReadProblem(string line) : base(line) { } }

        /// <summary>
        /// The text of a plain text file, or of a Word file's paragraphs (one line each). Opened so a file Word still has
        /// open can be read. Throws ReadProblem with a friendly line for a file that is too big, locked or not readable.
        /// </summary>
        public static string ReadFile(string path)
        {
            try
            {
                if (new FileInfo(path).Length > MaxFileBytes) throw new ReadProblem("That file is too big for About me. Pick a shorter one.");
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (!path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
                {
                    using var reader = new StreamReader(file, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    return reader.ReadToEnd();
                }
                using var zip = new ZipArchive(file, ZipArchiveMode.Read);
                var entry = zip.GetEntry("word/document.xml") ?? throw new ReadProblem("That Word file has no text in it.");
                if (entry.Length > MaxDocxXmlBytes) throw new ReadProblem("That file is too big for About me. Pick a shorter one.");
                using var stream = entry.Open();
                return DocxText(XDocument.Load(stream));
            }
            catch (ReadProblem) { throw; }
            catch (FileNotFoundException) { throw new ReadProblem("That file is not there any more."); }
            catch (UnauthorizedAccessException) { throw new ReadProblem("Windows did not let Busy Bunny open that file."); }
            catch (IOException) { throw new ReadProblem("Another program is using that file. Close it in Word first, then import again."); }
            catch (Exception e) when (e is InvalidDataException or System.Xml.XmlException) { throw new ReadProblem("That does not look like a Word file."); }
        }

        /// <summary>The lines to offer from a file: at most 300, with how many more were left out.</summary>
        public static (List<string> Lines, int LeftOut) Trimmed(List<string> lines) =>
            lines.Count <= MaxImportLines ? (lines, 0) : (lines.Take(MaxImportLines).ToList(), lines.Count - MaxImportLines);

        public static string DocxText(XDocument doc)
        {
            string Read(XNamespace w)
            {
                var sb = new StringBuilder();
                foreach (var p in doc.Descendants(w + "p"))
                {
                    // Only this paragraph's own text: a text box or nested table inside it is its own paragraph, read once on its own.
                    foreach (var node in p.Descendants().Where(n => n.Ancestors(w + "p").First() == p))
                    {
                        if (node.Name == w + "t") sb.Append(node.Value);
                        else if (node.Name == w + "tab") sb.Append(' ');
                        else if (node.Name == w + "br") sb.Append('\n');
                    }
                    sb.Append('\n');
                }
                return sb.ToString();
            }
            string text = Read("http://schemas.openxmlformats.org/wordprocessingml/2006/main");
            // Strict Open XML files use another name for the same thing.
            return text.Trim().Length > 0 ? text : Read("http://purl.oclc.org/ooxml/wordprocessingml/main");
        }

        /// <summary>Words too common to pick out a line: a topic made only of these finds nothing.</summary>
        private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
        {
            "the", "and", "you", "her", "she", "for", "with", "about", "what", "how", "like", "likes", "things", "thing", "me", "my",
            "this", "that", "they", "them", "there", "their", "your", "from", "have", "does", "when", "want", "wants", "some", "more",
            "most", "much", "very", "just", "also", "into", "onto", "over", "only", "than", "then", "will", "would", "should", "could",
            "anything", "something", "everything", "stuff", "info", "information", "know", "tell", "said", "says", "life", "self", "herself",
        };

        /// <summary>The words of a text that can pick out a line: 4 letters or more, not a stop word, cut to 6 letters so plurals still match.</summary>
        private static IEnumerable<string> Words(string text) =>
            Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+").Where(x => x.Length >= 4 && !StopWords.Contains(x)).Select(x => x.Length > 6 ? x[..6] : x).Distinct();

        /// <summary>Whether Bunny may use About me at all: the About me tool is on and she allows sharing.</summary>
        public static bool MayUse(Store s) => Tools.IsOn("aboutme") && Get(s).BunnyMayUse;

        public const int MaxLinesForClaude = 5;

        /// <summary>
        /// The About me lines about a topic, for Talk to Bunny's about_me tool: only lines with a word that starts with one
        /// of the topic's own words (common words left out), at most 5. Nothing when the tool or sharing is off.
        /// </summary>
        public static string ForClaude(Store s, string topic)
        {
            var me = Get(s);
            if (!Tools.IsOn("aboutme") || !me.BunnyMayUse) return "She has turned off sharing her About me with you.";
            if (me.Lines.Count == 0) return "She has not written an About me yet. She can add one in Options, Settings, About me.";
            var want = Words(topic).ToList();
            if (want.Count == 0) return "Ask about one specific topic, like humor or describing things.";
            var found = me.Lines.Where(l => Words(l).Any(w => want.Any(x => w.StartsWith(x, StringComparison.Ordinal)))).Take(MaxLinesForClaude).ToList();
            return found.Count == 0 ? "Nothing in her About me about " + topic + "." : "From her About me: " + string.Join(" | ", found);
        }

        /// <summary>Only words about the humor she likes: what her other lines say stays out of routine writing.</summary>
        private static readonly Regex HumorWords = new(@"\b(?:humou?r|funny|jok(?:e|es|ing)\b|laugh|sarcas|teas(?:e|es|ing)\b|puns?\b|silly|goofy|witty)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        public const int MaxCharsForWriting = 300;

        /// <summary>
        /// Her About me lines about humor, for fresh writing (cheers, the week, Wrapped): at most 300 characters. Empty when
        /// there are none, or the About me tool or sharing is off. Nothing else from About me goes into routine writing.
        /// </summary>
        public static string ForWriting(Store s)
        {
            if (!MayUse(s)) return "";
            var lines = new List<string>();
            int chars = 0;
            foreach (var l in Get(s).Lines.Where(l => HumorWords.IsMatch(l)))
            {
                if (chars + l.Length > MaxCharsForWriting) continue;
                lines.Add(l);
                chars += l.Length;
            }
            return lines.Count == 0 ? "" : " The humor she likes, in her words: " + string.Join(" | ", lines) + ".";
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) { yield return new AboutPage(this, store); }

        private sealed class AboutPage : ISettingsPage
        {
            private readonly SmartMemory _f;
            private readonly Store _store;
            private readonly List<string> _lines;
            private readonly ListBox _list = new() { Height = 200 };
            private readonly CheckBox _share;
            private readonly StackPanel _panel = new();
            private bool _changed;

            public string Name => PageName;
            public FrameworkElement Content => _panel;

            public AboutPage(SmartMemory f, Store store)
            {
                _f = f;
                _store = store;
                var me = Get(store);
                _lines = me.Lines.ToList();
                _panel.Children.Add(SmartUi.Intro("Tell Busy Bunny about yourself: your interests, the humor you like, how you like things described, what you never want. " +
                    "It stays on this computer with Bunny's notes, never in the app itself. When Bunny talks to Claude, it sends only the lines a reply needs.", 440));
                _list.KeyDown += (_, e) => { if (e.Key == Key.Delete) { e.Handled = true; DeleteLine(); } };
                AutomationProperties.SetHelpText(_list, "One line each. Delete removes the line you are on. Nothing is saved until OK.");
                _panel.Children.Add(Layout.Row("What Busy Bunny _remembers about you", _list, 440));
                Fill(0);
                var row1 = new WrapPanel { Width = 440 };
                void Add(Panel p, string text, Action act) { var b = Layout.Button(text); b.Click += (_, _) => act(); p.Children.Add(b); }
                Add(row1, "_Import an About me file...", Import);
                Add(row1, "_Add a line...", () => EditLine(-1));
                Add(row1, "_Change this line...", () => EditLine(_list.SelectedIndex));
                var row2 = new WrapPanel { Width = 440 };
                Add(row2, "_Delete this line", DeleteLine);
                Add(row2, "E_xport...", Export);
                Add(row2, "Delete _everything", DeleteAll);
                Add(row2, "Bunny's monthly chec_k-in...", () => SettingsWindow.SaveAndThen(_f.ShowCheckIn));
                _panel.Children.Add(row1);
                _panel.Children.Add(row2);
                _share = new CheckBox { Content = "Bunny may use these lines when it talks to Claude, only the ones a reply needs", IsChecked = me.BunnyMayUse, Margin = new Thickness(0, 8, 0, 4) };
                _panel.Children.Add(_share);
            }

            private Window? Owner => Window.GetWindow(_panel);

            private void Fill(int at)
            {
                _list.Items.Clear();
                if (_lines.Count == 0) _list.Items.Add(new ListBoxItem { Content = "Nothing yet. Import a file or add a line." });
                foreach (var l in _lines)
                {
                    var item = new ListBoxItem { Content = new TextBlock { Text = l, TextWrapping = TextWrapping.Wrap, MaxWidth = 400 } };
                    AutomationProperties.SetName(item, l);
                    _list.Items.Add(item);
                }
                _list.SelectedIndex = Math.Clamp(at, 0, _list.Items.Count - 1);
            }

            private void Say(string text) => Speech.Say(_panel, text, true);

            private void Import()
            {
                if (Owner is not Window owner) return;
                var d = new OpenFileDialog { Title = "Import an About me file", Filter = "Text or Word files (*.txt;*.docx)|*.txt;*.docx|All files (*.*)|*.*" };
                if (d.ShowDialog(owner) != true) return;
                List<string> all;
                try { all = SplitLines(ReadFile(d.FileName)); }
                catch (ReadProblem e) { Say(e.Message); return; }
                catch (Exception) { Say("Busy Bunny could not read that file. Try saving it as plain text, then import again."); return; }
                var (found, leftOut) = Trimmed(all.Where(l => !_lines.Contains(l)).ToList());
                if (found.Count == 0) { Say("There was nothing new in that file to remember."); return; }
                string more = leftOut > 0 ? " That file is long, so these are the first " + found.Count + "; " + SmartUi.Count(leftOut, "more line") + " were left out." : "";
                var picked = SmartUi.Checklist(owner, "What Busy Bunny will remember", "Each line is one thing Bunny will remember about you. Uncheck anything it should not keep." + more,
                    found.Select(l => (l, true)).ToList(), "_Remember checked");
                if (picked.Cancelled) { Say("Nothing imported."); _list.Focus(); return; }
                int first = _lines.Count;
                _lines.AddRange(picked.Chosen.Select(i => found[i]));
                _changed = true;
                Fill(first);
                Say("Added " + SmartUi.Count(picked.Chosen.Count, "line") + ". Press OK to save them." + (leftOut > 0 ? " " + SmartUi.Count(leftOut, "line") + " past the first " + MaxImportLines + " were left out." : ""));
                _list.Focus();
            }

            private void EditLine(int i)
            {
                if (Owner is not Window owner) return;
                bool adding = i < 0 || _lines.Count == 0;
                if (!adding && i >= _lines.Count) return;
                var box = new TextBox { Text = adding ? "" : _lines[i], TextWrapping = TextWrapping.Wrap };
                if (!SmartUi.Form(owner, adding ? "Add a line about you" : "Change this line", "OK", p => p.Children.Add(Layout.Row("_Line", box, 440)),
                    () => box.Text.Trim().Length == 0 ? "The line is empty." : null)) { _list.Focus(); return; }
                var typed = SplitLines(box.Text);
                if (typed.Count == 0) typed.Add(box.Text.Trim());
                string extra = "";
                if (adding)
                {
                    // A pasted block becomes one line each, like an import.
                    var add = typed.Where(l => !_lines.Contains(l)).ToList();
                    if (add.Count == 0) { Say("That line is there already."); _list.Focus(); return; }
                    i = _lines.Count;
                    _lines.AddRange(add);
                    if (add.Count > 1) extra = " " + SmartUi.Count(add.Count, "line") + " added, one for each line you typed.";
                }
                else
                {
                    _lines[i] = typed[0];
                    var rest = typed.Skip(1).Where(l => !_lines.Contains(l)).ToList();
                    _lines.InsertRange(i + 1, rest);
                    if (rest.Count > 0) extra = " The other " + SmartUi.Count(rest.Count, "line") + " you typed became new lines after it.";
                }
                _changed = true;
                Fill(i);
                Say((adding ? "Added." : "Changed.") + extra + " Press OK to save.");
                _list.Focus();
            }

            private void DeleteLine()
            {
                int i = _list.SelectedIndex;
                if (_lines.Count == 0 || i < 0 || i >= _lines.Count) { Say("There is no line to delete."); return; }
                _lines.RemoveAt(i);
                _changed = true;
                Fill(Math.Min(i, _lines.Count - 1));
                Say("Deleted once you press OK. " + SmartUi.Count(_lines.Count, "line") + " left.");
                _list.Focus();
            }

            private void DeleteAll()
            {
                if (_lines.Count == 0) { Say("There is nothing to delete."); return; }
                _lines.Clear();
                _changed = true;
                Fill(0);
                Say("Everything in About me goes when you press OK. Control Z on the main screen brings it back.");
                _list.Focus();
            }

            private void Export()
            {
                if (Owner is not Window owner) return;
                if (_lines.Count == 0) { Say("There is nothing to export yet."); return; }
                var d = new SaveFileDialog { Title = "Export About me", Filter = "Text files (*.txt)|*.txt", FileName = "About me.txt" };
                if (d.ShowDialog(owner) != true) return;
                try { File.WriteAllLines(d.FileName, _lines, new UTF8Encoding(false)); Say("Saved to " + Path.GetFileName(d.FileName) + "."); }
                catch (Exception e) { Say("Could not save: " + e.Message); }
            }

            public void Apply()
            {
                var me = Get(_store);
                bool share = _share.IsChecked == true;
                if (!_changed && me.BunnyMayUse == share) return;
                // Settings already remembered this step, so Control Z puts About me back with everything else.
                me.Lines = _lines.ToList();
                me.BunnyMayUse = share;
                _store.World.Set(AboutId, me);
                _store.World.Save();
            }
        }

        // ---- Bunny's monthly check-in ----

        public static string MonthKey(DateTime d) => d.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);

        public static Baseline Now(Store s, DateTime now) => new()
        {
            Month = MonthKey(now), Since = now.ToString("MMMM d"), Notes = s.Data.BunnyNotes.ToList(), Profile = s.Data.BunnyProfile,
        };

        /// <summary>How Bunny changed since the baseline, each with how to undo it and how to keep it.</summary>
        public static List<Change> Diff(Store s, Baseline b)
        {
            var list = new List<Change>();
            foreach (var n in s.Data.BunnyNotes.Where(n => !b.Notes.Contains(n)).ToList())
                list.Add(new Change("New note Bunny keeps: " + n, st => st.Data.BunnyNotes.Remove(n), bl => bl.Notes.Add(n)));
            foreach (var n in b.Notes.Where(n => !s.Data.BunnyNotes.Contains(n)).ToList())
                list.Add(new Change("A note Bunny forgot: " + n, st => st.Data.BunnyNotes.Add(n), bl => bl.Notes.Remove(n)));
            if (s.Data.BunnyProfile != b.Profile)
            {
                string old = b.Profile, now = s.Data.BunnyProfile;
                string what = now.Length == 0 ? "Bunny forgot what it learned from your tasks." : "Bunny updated what it learned from your tasks. It now starts: " + SmartText.Plain(now, 160);
                list.Add(new Change(what, st => st.Data.BunnyProfile = old, bl => bl.Profile = now));
            }
            return list;
        }

        public static Baseline GetBaseline(Store s) => s.World.GetKept<Baseline>(GrowthId);
        private static void SetBaseline(Store s, Baseline b) { s.World.SetKept(GrowthId, b); s.World.Save(); }

        /// <summary>
        /// The first day of a month: when Bunny changed, it says so once (through the calm rules). The first time ever it
        /// only notes how Bunny is now. Returns the line it offered, or null.
        /// </summary>
        public string? MonthStart(DateTime now) => MonthStart(_w, _w.Store, now);

        public static string? MonthStart(MainWindow? w, Store s, DateTime now)
        {
            var b = GetBaseline(s);
            if (b.Month.Length == 0) { SetBaseline(s, Now(s, now)); return null; }
            if (b.Month == MonthKey(now)) return null;
            var changes = Diff(s, b);
            if (changes.Count == 0) { SetBaseline(s, Now(s, now)); return null; }
            if (b.AnnouncedMonth == MonthKey(now) || !Tools.IsOn("bunnycheckin") || Calm.FunPaused) return null;
            b.AnnouncedMonth = MonthKey(now);
            SetBaseline(s, b);
            string line = SmartText.Fill(Content.Pick(s, "smart.growth"), ("count", SmartUi.Count(changes.Count, "thing")));
            if (w != null) Calm.SayUnprompted(w, line);
            return line;
        }

        private void ShowCheckIn()
        {
            var store = _w.Store;
            var back = _w.KeepFocus();
            try
            {
                var b = GetBaseline(store);
                if (b.Month.Length == 0) { SetBaseline(store, Now(store, DateTime.Now)); b = GetBaseline(store); }
                var win = Layout.Dialog(_w, "Bunny's monthly check-in");
                var panel = new StackPanel { Margin = new Thickness(14) };
                var intro = SmartUi.Intro("", 560);
                var list = new ListBox { Width = 560, Height = 220 };
                AutomationProperties.SetName(list, "How Bunny changed");
                List<Change> changes = new();
                void Refill(int at)
                {
                    b = GetBaseline(store);
                    changes = Diff(store, b);
                    intro.Text = changes.Count == 0
                        ? "Bunny has not changed since " + b.Since + ". Its notes and what it learned are all as you kept them."
                        : "Since " + b.Since + ", Bunny changed in " + SmartUi.Count(changes.Count, "way") + ". Keep the ones you like; undo the rest.";
                    list.Items.Clear();
                    foreach (var c in changes)
                    {
                        var item = new ListBoxItem { Content = new TextBlock { Text = c.Line, TextWrapping = TextWrapping.Wrap, MaxWidth = 520 } };
                        AutomationProperties.SetName(item, c.Line);
                        list.Items.Add(item);
                    }
                    if (changes.Count == 0)
                    {
                        // Everything settled: this is how Bunny is now.
                        SetBaseline(store, Now(store, DateTime.Now));
                        list.Items.Add(new ListBoxItem { Content = "Nothing to look at." });
                    }
                    TextViewer.Select(list, Math.Clamp(at, 0, list.Items.Count - 1));
                }
                void Act(bool undo)
                {
                    int i = list.SelectedIndex;
                    if (i < 0 || i >= changes.Count) { Speech.Say(win, "There is nothing to " + (undo ? "undo." : "keep."), true); return; }
                    var c = changes[i];
                    if (undo)
                    {
                        store.Remember("undoing a change to Bunny");
                        c.Undo(store);
                        _w.SaveAndRefresh();
                    }
                    else { var bl = GetBaseline(store); c.Keep(bl); SetBaseline(store, bl); }
                    Speech.Say(win, undo ? "Undone. Control Z on the main screen brings it back." : "Kept.", true);
                    Refill(i);
                }
                var buttons = new WrapPanel();
                var keep = Layout.Button("_Keep this");
                keep.Click += (_, _) => Act(false);
                var undo = Layout.Button("_Undo this");
                undo.Click += (_, _) => Act(true);
                var keepAll = Layout.Button("Keep _all");
                keepAll.Click += (_, _) => { SetBaseline(store, Now(store, DateTime.Now)); Speech.Say(win, "Kept everything. This is Bunny now.", true); Refill(0); };
                var reset = Layout.Button("_Reset to how Bunny was");
                reset.Click += (_, _) =>
                {
                    if (changes.Count == 0) { Speech.Say(win, "There is nothing to reset.", true); return; }
                    store.Remember("resetting Bunny to how it was");
                    foreach (var c in changes) c.Undo(store);
                    _w.SaveAndRefresh();
                    Speech.Say(win, "Bunny is back to how it was on " + b.Since + ". Control Z on the main screen undoes that.", true);
                    Refill(0);
                };
                foreach (var x in new[] { keep, undo, keepAll, reset }) buttons.Children.Add(x);
                var close = Layout.Button("Close", isCancel: true);
                buttons.Children.Add(close);
                panel.Children.Add(intro);
                panel.Children.Add(list);
                panel.Children.Add(buttons);
                win.Content = panel;
                win.Loaded += (_, _) => { Refill(0); Speech.Say(win, intro.Text); };
                win.ShowDialog();
            }
            finally { back(); }
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("About me and Bunny's check-in: " + what); }
            var lines = SplitLines("- I love loud thunderstorms\n\n2. Dry humor please, no puns\nDescribe things by touch first\n- I love loud thunderstorms");
            Need(lines.Count == 3 && lines[1] == "Dry humor please, no puns", "splitting lines gave " + string.Join(" / ", lines));
            XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var doc = new XDocument(new XElement(w + "document", new XElement(w + "body",
                new XElement(w + "p", new XElement(w + "r", new XElement(w + "t", "Likes "), new XElement(w + "t", "rain"))),
                new XElement(w + "p", new XElement(w + "r", new XElement(w + "t", "Never call me sweetie"))))));
            Need(SplitLines(DocxText(doc)).SequenceEqual(new[] { "Likes rain", "Never call me sweetie" }), "reading a Word file");
            store.World.Set(AboutId, new AboutMe { Lines = lines });
            Need(ForClaude(store, "humor").Contains("Dry humor") && !ForClaude(store, "humor").Contains("thunder"), "only the lines a reply needs");
            Need(ForClaude(store, "pizza").StartsWith("Nothing"), "nothing about an unknown topic");
            Need(ForWriting(store).Contains("Dry humor") && !ForWriting(store).Contains("touch"), "fresh writing got more than the humor lines");
            // Common words and short words never pull lines out; a topic word only matches the start of a line's word.
            var touchy = new List<string> { "Never mention my dad", "Therapy on Tuesdays", "You can tease me about coffee", "Never bring up my weight", "There is a dog next door",
                "My sister likes horses", "Things about school stress me", "I love the rain", "Rain sounds help me sleep", "Rainy days are cozy", "Rain boots are the best", "Rain makes me calm", "Rain on the roof is nice" };
            store.World.Set(AboutId, new AboutMe { Lines = touchy });
            foreach (var topic in new[] { "the", "about you", "you", "her", "and", "about me", "things", "me and my", "what she likes", "a" })
                Need(!ForClaude(store, topic).StartsWith("From"), "the topic " + topic + " gave lines");
            Need(!ForClaude(store, "the rapy").Contains("Therapy"), "a short word matched a longer one");
            Need(ForClaude(store, "rain").Split('|').Length == MaxLinesForClaude, "more than 5 lines in one answer");
            Need(!ForClaude(store, "raincoat").StartsWith("From"), "a topic word matched a shorter line word");
            string writing = ForWriting(store);
            Need(writing.Contains("tease me") && !writing.Contains("dad") && !writing.Contains("weight") && !writing.Contains("Therapy"), "fresh writing took private lines: " + writing);
            store.World.Set(AboutId, new AboutMe { Lines = Enumerable.Range(0, 30).Select(i => "Silly joke number " + i + " is one I really enjoy hearing").ToList() });
            Need(ForWriting(store).Length <= MaxCharsForWriting + 60, "fresh writing was too long");
            // The About me tool turned off stops all sharing, even with the Settings checkbox on.
            if (Engines.Settings is Settings cfg && Tools.Find("aboutme") is BunnyTool aboutTool)
            {
                store.World.Set(AboutId, new AboutMe { Lines = lines });
                bool wasOn = Tools.IsOn(cfg, aboutTool);
                Tools.SetOn(cfg, aboutTool, false);
                try { Need(!ForClaude(store, "humor").Contains("Dry") && ForWriting(store).Length == 0, "the About me tool turned off still shared"); }
                finally { Tools.SetOn(cfg, aboutTool, wasOn); }
            }
            // Big files are refused, and a file Word keeps open can still be read.
            string tmp = Path.Combine(Path.GetTempPath(), "busybunny-aboutme-" + Guid.NewGuid().ToString("N") + ".txt");
            try
            {
                File.WriteAllText(tmp, "Likes rain\nDry humor");
                using (new FileStream(tmp, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                    Need(SplitLines(ReadFile(tmp)).Count == 2, "a file open in another program");
                File.WriteAllText(tmp, new string('x', (int)MaxFileBytes + 10));
                bool refused = false;
                try { ReadFile(tmp); } catch (ReadProblem e) { refused = e.Message.Contains("too big"); }
                Need(refused, "a file over 1 MB was read");
            }
            finally { try { File.Delete(tmp); } catch { } }
            Need(Trimmed(Enumerable.Range(0, 350).Select(i => "line " + i).ToList()) is var (kept, leftOut) && kept.Count == MaxImportLines && leftOut == 50, "long files keep the first 300 lines");
            // Text boxes inside a paragraph are read once, and Strict Word files are read too.
            var boxed = new XDocument(new XElement(w + "document", new XElement(w + "body",
                new XElement(w + "p", new XElement(w + "r", new XElement(w + "t", "Outer")), new XElement(w + "r", new XElement(w + "txbxContent", new XElement(w + "p", new XElement(w + "r", new XElement(w + "t", "Inner")))))))));
            Need(SplitLines(DocxText(boxed)).SequenceEqual(new[] { "Outer", "Inner" }), "a text box was read twice: " + string.Join(" / ", SplitLines(DocxText(boxed))));
            XNamespace strict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
            var strictDoc = new XDocument(new XElement(strict + "document", new XElement(strict + "body", new XElement(strict + "p", new XElement(strict + "r", new XElement(strict + "t", "Strict line"))))));
            Need(SplitLines(DocxText(strictDoc)).SequenceEqual(new[] { "Strict line" }), "a Strict Word file");
            store.World.Set(AboutId, new AboutMe { Lines = lines, BunnyMayUse = false });
            Need(!ForClaude(store, "humor").Contains("Dry") && ForWriting(store).Length == 0, "sharing turned off still shared");
            store.World.Set(AboutId, new AboutMe { Lines = lines });
            // Undo puts About me back (it is a world bag).
            store.Remember("test");
            store.World.Set(AboutId, new AboutMe());
            store.Undo();
            Need(Get(store).Lines.Count == 3, "undo did not bring About me back");

            // The monthly check-in: new notes and a changed profile are found, undone and kept.
            var now = new DateTime(2026, 11, 3, 9, 0, 0);
            store.Data.BunnyNotes = new List<string> { "likes mornings" };
            store.Data.BunnyProfile = "old";
            Need(MonthStart(null, store, now) == null && GetBaseline(store).Month == "2026-11", "the first run only notes Bunny");
            store.Data.BunnyNotes.Add("be more sarcastic");
            store.Data.BunnyNotes.Remove("likes mornings");
            store.Data.BunnyProfile = "new";
            var d = Diff(store, GetBaseline(store));
            Need(d.Count == 3, "changes found: " + d.Count);
            Need(MonthStart(null, store, now.AddDays(5)) == null, "a check-in came in the same month");
            string? said = MonthStart(null, store, now.AddMonths(1));
            Need(said != null && said.Contains("3 things") && !said.Contains("{count}"), "the monthly line was " + said);
            Need(MonthStart(null, store, now.AddMonths(1).AddDays(1)) == null, "the monthly line came twice");
            d.First(c => c.Line.Contains("sarcastic")).Undo(store);
            Need(!store.Data.BunnyNotes.Contains("be more sarcastic"), "undo a new note");
            var b = GetBaseline(store);
            d.First(c => c.Line.Contains("learned")).Keep(b);
            SetBaseline(store, b);
            var left = Diff(store, GetBaseline(store));
            Need(left.Count == 1 && left[0].Line.Contains("forgot"), "keeping left " + left.Count);
            left[0].Undo(store);
            Need(Diff(store, GetBaseline(store)).Count == 0 && store.Data.BunnyNotes.Contains("likes mornings"), "a forgotten note came back");
            Need(Content.Lines("smart.growth").Count >= 20 && Content.Lines("smart.growth").All(l => l.Contains("{count}")), "smart.growth lines");
            Need(Tools.Find("aboutme") != null && Tools.Find("bunnycheckin") != null, "not in the toolbox");
        }
    }
}
