using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;

namespace BusyBunny
{
    public sealed class Settings
    {
        public bool SoundsOn { get; set; } = true;
        public int SoundVolume { get; set; } = 80;
        public int SoundKey { get; set; } = 10;
        public Dictionary<string, string> SoundChoices { get; set; } = new();
        /// <summary>The sound pack last picked in the Sounds window (Classic, Bunny pack and so on).</summary>
        public string SoundPack { get; set; } = "Classic";
        /// <summary>The seasonal pack hint already spoken, like Halloween 2026.</summary>
        public string PackHinted { get; set; } = "";
        public bool ShortMode { get; set; }
        /// <summary>What each Board group shows (Morgan's issue 10), by status name: 0 things to do, 1 things due, 2 both. Both when missing.</summary>
        public Dictionary<string, int> BoardShow { get; set; } = new();
        public bool LowEnergy { get; set; }
        public bool ShuffleMode { get; set; }
        public bool CheckForUpdatesAtStart { get; set; } = true;
        public int UrgentWarnAbove { get; set; } = 5;
        /// <summary>The quiet background music while Busy Bunny is in front.</summary>
        public bool MusicOn { get; set; } = true;
        /// <summary>Its volume in percent, 10 by default.</summary>
        public int MusicVolume { get; set; } = 10;
        public bool SetupGuideShown { get; set; }
        public bool MorningCheckIn { get; set; } = true;
        public string EndOfDay { get; set; } = "week";
        public string LastResetDay { get; set; } = "";
        public string LastRhythmDay { get; set; } = "";
        public bool KeepInTray { get; set; } = true;
        public bool TrayHintShown { get; set; }
        public bool HotkeyQuickAdd { get; set; } = true;
        public bool HotkeyRead { get; set; } = true;
        public int FollowUpDays { get; set; } = 3;
        /// <summary>The last day each nudge was given, so each comes once a day.</summary>
        public Dictionary<string, string> NudgeDates { get; set; } = new();
        /// <summary>The roast dial (spec 12.2): 0 off, 1 gentle, 2 teasing, 3 spicy, 4 savage.</summary>
        public int RoastLevel { get; set; } = 2;
        /// <summary>Emoji at big moments only (spec 12.3).</summary>
        public bool EmojisOn { get; set; } = true;
        public bool FinalsMode { get; set; }
        /// <summary>The season the finals offer was last made for, like 2026-12, so it is made once.</summary>
        public string FinalsOffered { get; set; } = "";
        /// <summary>Phrases and facts already used, by group, so they do not repeat until most have been.</summary>
        public Dictionary<string, List<int>> PhraseUsed { get; set; } = new();
        /// <summary>Surprise unlocks: Set:Drama, Emoji:Animals and so on.</summary>
        public List<string> Unlocked { get; set; } = new();

        // ---- Smarter Bunny ----
        /// <summary>The Claude API key, encrypted for this Windows user (DPAPI), or empty.</summary>
        public string BunnyKey { get; set; } = "";
        /// <summary>Hours between Bunny's check-ins, or 0 for never.</summary>
        public int BunnyCheckHours { get; set; } = 3;
        /// <summary>Bunny checks in only from this hour (24-hour clock) until BunnyCheckUntil (sweep 2.5).</summary>
        public int BunnyCheckFrom { get; set; } = 9;
        public int BunnyCheckUntil { get; set; } = 21;
        /// <summary>When Bunny last checked in.</summary>
        public DateTime BunnyLastCheck { get; set; }
        /// <summary>Ask before Bunny deletes a task.</summary>
        public bool BunnyAskBeforeDelete { get; set; } = true;
        /// <summary>Let Bunny read dates the form does not understand.</summary>
        public bool BunnyReadsDates { get; set; } = true;
        /// <summary>What happens when Bunny answers: 0 reads the answer out loud, 1 just says Bunny answered, 2 says nothing (Morgan's issue 15).</summary>
        public int BunnyVoice { get; set; }
        /// <summary>Bunny's personality, one of SmartBunny.Personalities.</summary>
        public string BunnyPersonality { get; set; } = "";
        /// <summary>A short briefing from Bunny the first time Busy Bunny opens each day.</summary>
        public bool BunnyBriefing { get; set; } = true;
        public string BunnyBriefedDay { get; set; } = "";

        // ---- Adventures (Morgan's issue 17) ----
        /// <summary>Vex, the rival bunny, trash-talks after tasks.</summary>
        public bool RivalOn { get; set; } = true;
        /// <summary>The dramatic narrator announces big finishes.</summary>
        public bool NarratorOn { get; set; } = true;

        // ---- Data safety (sweep 2.2 and 2.3) ----
        /// <summary>A folder she picked (like her Google Drive folder) that also gets the daily backup, or empty.</summary>
        public string BackupCopyFolder { get; set; } = "";
        /// <summary>The first-run line about tasks staying on each PC has been said.</summary>
        public bool PcNoteSaid { get; set; }

        // ---- Calm rules and the toolbox (sweep 8.1, Part 4) ----
        /// <summary>Calm mode: every fun feature pauses at once, keeping all progress.</summary>
        public bool CalmMode { get; set; }
        /// <summary>Lines nobody asked for, from every feature together, at most this many a day.</summary>
        public int UnpromptedCap { get; set; } = 3;
        /// <summary>Quiet hours, from this hour (24-hour clock) until QuietEnd.</summary>
        public int QuietStart { get; set; } = 22;
        public int QuietEnd { get; set; } = 8;
        /// <summary>Toolbox tools turned off in Settings, Fun and tools, by id.</summary>
        public List<string> ToolsOff { get; set; } = new();
        /// <summary>Tools turned on that start off, by id.</summary>
        public List<string> ToolsOn { get; set; } = new();
        /// <summary>Her favorite tools, by id, in the order she marked them.</summary>
        public List<string> ToolFavorites { get; set; } = new();
        /// <summary>When each tool was last used, by id.</summary>
        public Dictionary<string, DateTime> ToolUsed { get; set; } = new();
        /// <summary>The town she typed for the weather, and where it is (looked up once). Never found automatically.</summary>
        public string WeatherTown { get; set; } = "";
        /// <summary>The daily limit on Claude writing fun lines (BunnyWrite and the narrator): all features together, and each one. Only answers that came back count.</summary>
        public int BunnyWriteDaily { get; set; } = 40;
        public int BunnyWritePerFeature { get; set; } = 10;
        public double WeatherLat { get; set; }
        public double WeatherLon { get; set; }

        public static string FilePath => Path.Combine(Store.Folder, "settings.json");

        /// <summary>Something to tell her after starting, like settings that came back from their spare copy. Null when all was well.</summary>
        public static string? LoadNote { get; set; }
        /// <summary>Raised when settings could not be saved, with the reason. DataSafety says it.</summary>
        public static event Action<string>? SaveFailed;

        private static readonly JsonSerializerOptions SaveJson = new() { WriteIndented = true };
        /// <summary>Set when settings.json could not be read at start (in use, or damaged with no rescue copy): it is then never replaced this time.</summary>
        public static bool KeepFile { get; private set; }

        /// <summary>
        /// Reads settings.json. A file another program holds open is waited for; a damaged one falls back
        /// to settings.json.bak (the copy before the last save) and LoadNote says so, instead of quietly
        /// starting over and losing her key, sounds and choices.
        /// </summary>
        public static Settings Load()
        {
            foreach (string f in new[] { FilePath, FilePath + ".new", FilePath + ".bak" })
            {
                string? text = null;
                // Up to 8 seconds for the real file, like the tasks file: a sync or virus scan lets go soon.
                for (int i = 0; i < (f == FilePath ? 40 : 10) && text == null; i++)
                {
                    try { if (!File.Exists(f)) break; text = File.ReadAllText(f); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { System.Threading.Thread.Sleep(200); }
                }
                if (text == null)
                {
                    if (f == FilePath && File.Exists(FilePath))
                    {
                        // Locked, not damaged: her real file must never be replaced by defaults or an older copy.
                        KeepFile = true;
                        LoadNote = "Another program was using your settings file, so for now I am using a spare copy or the usual settings. Changes to settings will not be kept until Busy Bunny starts again.";
                        ErrorLog.Write("Settings file was locked at start; it will not be overwritten this time.");
                    }
                    continue;
                }
                try
                {
                    if (JsonSerializer.Deserialize<Settings>(text) is Settings s)
                    {
                        if (f != FilePath)
                        {
                            LoadNote = "Your settings file was damaged, so I used its spare copy. Check Options, Settings if something looks different.";
                            ErrorLog.Write("Settings file was damaged or missing; loaded " + Path.GetFileName(f) + ".");
                        }
                        s.Fix();
                        return s;
                    }
                }
                catch (Exception e) when (e is JsonException or NotSupportedException or InvalidOperationException) { }
                if (f == FilePath)
                {
                    try { File.Copy(f, f + ".damaged", true); }
                    catch { KeepFile = true; } // no rescue copy yet: do not replace it either
                }
            }
            if (File.Exists(FilePath) && !KeepFile)
            {
                LoadNote = "Your settings file was damaged, so your settings are back to how they start. The damaged one is kept as settings.json.damaged.";
                ErrorLog.Write("Settings file was damaged and had no spare copy.");
            }
            return new();
        }

        /// <summary>Fills in anything a damaged or hand-edited file left as null.</summary>
        private void Fix()
        {
            SoundChoices ??= new(); BoardShow ??= new(); NudgeDates ??= new(); PhraseUsed ??= new(); Unlocked ??= new();
            SoundPack ??= "Classic"; PackHinted ??= ""; EndOfDay ??= "week"; LastResetDay ??= ""; LastRhythmDay ??= "";
            FinalsOffered ??= ""; BunnyKey ??= ""; BunnyPersonality ??= ""; BunnyBriefedDay ??= ""; BackupCopyFolder ??= "";
            ToolsOff ??= new(); ToolsOn ??= new(); ToolFavorites ??= new(); ToolUsed ??= new(); WeatherTown ??= "";
        }

        /// <summary>
        /// Saves by writing settings.json.new straight to the disk and swapping it in, keeping the one
        /// before as settings.json.bak, so a crash or power cut never leaves a broken file. A failure is
        /// logged and announced (SaveFailed), never hidden.
        /// </summary>
        public void Save()
        {
            string file = FilePath, fresh = file + ".new", bak = file + ".bak";
            if (KeepFile) return;
            try
            {
                Directory.CreateDirectory(Store.Folder);
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(this, SaveJson);
                using (var fs = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 14, FileOptions.WriteThrough))
                {
                    fs.Write(bytes, 0, bytes.Length);
                    fs.Flush(true);
                }
                if (!File.Exists(file)) { File.Move(fresh, file); return; }
                for (int attempt = 1; ; attempt++)
                {
                    try { File.Replace(fresh, file, bak, ignoreMetadataErrors: true); return; }
                    catch (IOException) when (attempt < 3 && File.Exists(file)) { System.Threading.Thread.Sleep(100); }
                    catch
                    {
                        if (!File.Exists(file)) { try { if (File.Exists(fresh)) File.Move(fresh, file); } catch { } }
                        throw;
                    }
                }
            }
            catch (Exception e)
            {
                ErrorLog.Write("Saving settings failed: " + e.GetType().Name + ": " + e.Message);
                try { SaveFailed?.Invoke(e.Message); } catch { }
            }
        }

        /// <summary>Puts these settings into the sound player.</summary>
        public void Apply()
        {
            Sounds.Enabled = SoundsOn;
            Sounds.Volume = Math.Clamp(SoundVolume, 10, 100);
            Sounds.Key = Math.Clamp(SoundKey, 0, 11);
            // Choices saved before Busy Bunny had its own instruments move to the nearest one.
            foreach (var k in SoundChoices.Keys.ToList()) SoundChoices[k] = Sounds.Migrate(SoundChoices[k]);
            Sounds.Choice = e => SoundChoices.TryGetValue(e.ToString(), out var c) ? c : Sounds.DefaultName;
            UiSounds.UpdateMusic();
        }
    }

    /// <summary>
    /// Says something through the screen reader without moving focus (spec 3.3), using a UI
    /// Automation notification, which NVDA reads politely after what it is already saying.
    ///
    /// No floods (sweep 9.5): everything said during one action (a key press, a timer) is joined into
    /// one notification once the action is finished, a few milliseconds later. The answer to a key she
    /// pressed always goes at once. Lines nobody asked for (a feature speaking up on its own) wait until
    /// at least 2 seconds after the last thing said, and an interrupting line drops what was still waiting.
    /// </summary>
    public static class Speech
    {
        /// <summary>While Smarter Bunny changes things, the usual spoken lines (Added, Done...) wait: Bunny says it all in its answer.</summary>
        public static bool Muted;

        /// <summary>The least time between two lines nobody asked for.</summary>
        public static readonly TimeSpan Gap = TimeSpan.FromSeconds(2);

        private static readonly List<(UIElement From, string Text)> _burst = new(), _waiting = new();
        private static bool _burstInterrupt, _flushQueued, _hooked;
        private static DateTime _lastSpoken = DateTime.MinValue, _lastKey = DateTime.MinValue;
        private static System.Windows.Threading.DispatcherTimer? _timer;

        public static void Say(UIElement from, string text, bool interrupt = false)
        {
            if (Muted || string.IsNullOrWhiteSpace(text)) return;
            var disp = from.Dispatcher;
            if (!disp.CheckAccess()) { disp.BeginInvoke(() => Say(from, text, interrupt)); return; }
            Hook();
            // An interrupting line goes first, and whatever was waiting is said right after it, never dropped.
            if (interrupt) _burstInterrupt = true;
            if (!_burst.Any(b => b.Text == text)) _burst.Add((from, text));
            if (!_flushQueued)
            {
                _flushQueued = true;
                disp.BeginInvoke(Flush, System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        /// <summary>Notes each key press, so its answer is never held back.</summary>
        private static void Hook()
        {
            if (_hooked) return;
            _hooked = true;
            try
            {
                // On the preview, with handled events too: the main window and the task form handle their
                // shortcut keys there, so KeyDown never comes for Control D, Delete, F2 and the rest.
                EventManager.RegisterClassHandler(typeof(Window), System.Windows.Input.Keyboard.PreviewKeyDownEvent,
                    new System.Windows.Input.KeyEventHandler((_, _) => _lastKey = DateTime.Now), true);
            }
            catch { }
        }

        private static void Flush()
        {
            _flushQueued = false;
            if (_burst.Count == 0) return;
            var now = DateTime.Now;
            bool answer = WouldSayNow(_burstInterrupt, _lastSpoken, _lastKey, now);
            if (answer)
            {
                // Her answer first, then anything that was waiting.
                var all = _burst.Concat(_waiting).ToList();
                bool interrupt = _burstInterrupt;
                _burst.Clear(); _waiting.Clear(); _burstInterrupt = false;
                _timer?.Stop();
                Raise(all, interrupt);
                return;
            }
            foreach (var b in _burst) if (!_waiting.Any(x => x.Text == b.Text)) _waiting.Add(b);
            _burst.Clear();
            var disp = _waiting[^1].From.Dispatcher;
            if (_timer == null)
            {
                _timer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background, disp);
                _timer.Tick += (_, _) =>
                {
                    _timer!.Stop();
                    if (_waiting.Count == 0) return;
                    if (DateTime.Now - _lastSpoken < Gap) { _timer.Interval = _lastSpoken + Gap - DateTime.Now; _timer.Start(); return; }
                    var all = _waiting.ToList();
                    _waiting.Clear();
                    Raise(all, false);
                };
            }
            _timer.Stop();
            var wait = _lastSpoken + Gap - now;
            _timer.Interval = wait > TimeSpan.Zero ? wait : TimeSpan.FromMilliseconds(1);
            _timer.Start();
        }

        private static void Raise(List<(UIElement From, string Text)> lines, bool interrupt)
        {
            if (lines.Count == 0) return;
            _lastSpoken = DateTime.Now;
            string text = string.Join(" ", lines.Select(l => l.Text.Trim()));
            try
            {
                // Said from the window in front: NVDA can drop notifications from a window behind a dialog,
                // and a dialog that has just closed can no longer speak.
                UIElement from = lines[^1].From;
                var own = Window.GetWindow(from);
                bool alive = PresentationSource.FromVisual(from) != null;
                if (!alive || own == null || !own.IsActive)
                {
                    var active = Application.Current?.Windows.OfType<Window>().FirstOrDefault(x => x.IsActive);
                    if (active != null) from = active;
                    else if (!alive && Application.Current?.MainWindow is Window main) from = main;
                }
                // Panels have no automation peer, so their lines would be lost: then the window says it.
                var peer = UIElementAutomationPeer.FromElement(from) ?? UIElementAutomationPeer.CreatePeerForElement(from);
                if (peer == null && Window.GetWindow(from) is Window w) peer = UIElementAutomationPeer.FromElement(w) ?? UIElementAutomationPeer.CreatePeerForElement(w);
                peer?.RaiseNotificationEvent(AutomationNotificationKind.Other,
                    interrupt ? AutomationNotificationProcessing.ImportantMostRecent : AutomationNotificationProcessing.All,
                    text, "BusyBunny");
            }
            catch { }
        }

        /// <summary>For the self-test: what the queue would do with a line now, without a screen reader.</summary>
        internal static bool WouldSayNow(bool interrupt, DateTime lastSpoken, DateTime lastKey, DateTime now) =>
            interrupt || lastKey > lastSpoken || now - lastSpoken >= Gap;
    }

    public static class Layout
    {
        /// <summary>A label and its control: the label's access key moves to the control, and the control is named after it.</summary>
        public static FrameworkElement Row(string label, Control c, double width = 320)
        {
            c.Width = width;
            AutomationProperties.SetName(c, label.Replace("_", ""));
            var l = new Label { Content = label, Target = c, Padding = new Thickness(0, 6, 0, 2) };
            var p = new StackPanel { Margin = new Thickness(0, 0, 0, 6) };
            p.Children.Add(l);
            p.Children.Add(c);
            return p;
        }

        public static Button Button(string text, bool isDefault = false, bool isCancel = false) =>
            new() { Content = text, IsDefault = isDefault, IsCancel = isCancel, MinWidth = 90, Margin = new Thickness(0, 8, 8, 0), Padding = new Thickness(10, 3, 10, 3) };

        public static Window Dialog(Window owner, string title)
        {
            var w = new Window
            {
                Title = title,
                SizeToContent = SizeToContent.WidthAndHeight,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterScreen,
                ShowInTaskbar = false,
                FontSize = 15,
            };
            // A window that has closed (Busy Bunny closing for an update) cannot own one: then it stands alone.
            if (PresentationSource.FromVisual(owner) != null)
            {
                w.Owner = owner;
                w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }
            return w;
        }
    }
}
