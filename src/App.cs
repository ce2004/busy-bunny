using System;
using System.IO;
using System.Linq;
using System.Windows;

namespace BusyBunny
{
    public sealed class App : Application
    {
        [STAThread]
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "--selftest") return SelfTest();
            if (args.Length > 1 && args[0] == "--write-icon") { Logo.WriteIcon(args[1]); return 0; }
            if (args.Length > 2 && args[0] == "--write-music") { MusicLoop.WriteWav(args[1], double.Parse(args[2], System.Globalization.CultureInfo.InvariantCulture)); return 0; }
            if (args.Length > 2 && args[0] == "--write-sound") { File.WriteAllBytes(args[2], Sounds.WavOf(args[1]) ?? Array.Empty<byte>()); return 0; }
            bool updated = args.Length > 1 && args[0] == "--after-update" && int.TryParse(args[1], out int pid);
            int back = Array.IndexOf(args, "--rolled-back");
            if (back >= 0) RolledBackFrom = back + 1 < args.Length ? args[back + 1] : "the new version";
            if (updated) Updater.FinishUpdate(int.Parse(args[1])); // waits for the old copy to close first
            else Updater.CleanLeftovers();

            // One copy at a time: two would each save over the other's changes. Opening it again
            // brings the open one to the front instead.
            using var only = new System.Threading.Mutex(false, @"Local\BusyBunny.Running");
            using var show = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, @"Local\BusyBunny.Show");
            bool mine;
            try { mine = only.WaitOne(updated ? 15000 : 0); }
            catch (System.Threading.AbandonedMutexException) { mine = true; }
            if (!mine) { show.Set(); return 0; }

            // A new version that crashed on its last start is swapped back for the old one (sweep 9.10).
            if (Updater.RollBackIfBroken()) { only.ReleaseMutex(); return 0; }

            // An update downloaded earlier and not yet installed (she chose Later, or Busy Bunny closed
            // first): install it now, before anything opens, and start the new copy.
            if (!updated && Updater.ApplyReadyQuietly(restart: true)) { only.ReleaseMutex(); return 0; }
            // Installed while closing last time: show what is new this time.
            Version? closedFrom = updated ? null : Updater.TakeUpdatedMarker();

            SpaceAsTyping();
            var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
            Store? store = null;
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log(e.Exception);
                // Only say the tasks are safe once they really are saved.
                bool saved = store == null || store.TrySave();
                MessageBox.Show("Something went wrong in Busy Bunny: " + e.Exception.Message + "\n\n" +
                    (saved ? "Your tasks are saved." : "Your latest change could not be saved yet. Busy Bunny will keep trying, so leave it open for now.") +
                    " Help, Report a problem copies the details for whoever helps you.", "Busy Bunny", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            // Errors on other threads and in background tasks are written down too (R1 19); a last save is tried.
            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                if (e.ExceptionObject is Exception x) Log(x); else ErrorLog.Write("Unhandled error: " + e.ExceptionObject);
                if (e.IsTerminating) { try { store?.TrySave(); } catch { } }
            };
            System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                Log(e.Exception);
                e.SetObserved();
            };
            var settings = Settings.Load();
            UiSounds.Hook(app, settings);
            settings.Apply();
            Mixer.Start();
            Sounds.Warm();
            try { store = Store.Load(); }
            catch (StoreLockedException e)
            {
                // Locked is not damaged: nothing is replaced, nothing starts over. She tries again in a moment.
                Log(e);
                Updater.UndoTrialStart();
                MessageBox.Show("Busy Bunny could not open your tasks, because another program is using the file right now (maybe a backup, sync or virus scan). Nothing was changed.\n\nWait a minute, then start Busy Bunny again.",
                    "Busy Bunny", MessageBoxButton.OK, MessageBoxImage.Warning);
                only.ReleaseMutex();
                return 1;
            }
            Store.ExtraBackupFolder = settings.BackupCopyFolder.Length > 0 ? settings.BackupCopyFolder : null;
            var w = new MainWindow(store, settings) { Icon = Logo.Image() };
            app.MainWindow = w;
            new System.Threading.Thread(() =>
            {
                while (true)
                {
                    show.WaitOne();
                    app.Dispatcher.BeginInvoke(() =>
                    {
                        if (!w.IsVisible) w.Show();
                        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
                        w.Activate();
                    });
                }
            }) { IsBackground = true }.Start();
            Splash.ShowThen(w, () =>
            {
                // After an update, the changelog opens by itself so the new things are easy to find.
                // Only what is new since the copy that did the update (older copies do not say which they were).
                Version? from = updated && args.Length > 2 ? Updater.NormalizeTag(args[2]) : closedFrom;
                if ((updated || closedFrom != null) && RolledBackFrom == null) w.Dispatcher.BeginInvoke(() => w.ShowChanges(from), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                w.StartAutoUpdates();
                w.OnStarted();
                // Running fine for a minute: an update that just came in has proved itself, and the old copy can go.
                WindowShown = true;
                var ok = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
                ok.Tick += (_, _) => { ok.Stop(); Updater.MarkStartedOk(); };
                ok.Start();
            });
            // Windows signing out or shutting down: save first (sweep 2.6).
            app.SessionEnding += (_, _) => store?.TrySave();
            app.Exit += (_, _) =>
            {
                // It opened and closed normally, so this version works.
                if (WindowShown) Updater.MarkStartedOk();
                // Never closes without trying to save.
                store?.TrySave();
                // Closing with an update ready (she chose Later): install it now, quietly, for next time.
                Updater.ApplyReadyQuietly(restart: false);
            };
            int code = app.Run();
            only.ReleaseMutex();
            return code;
        }

        /// <summary>
        /// WPF text boxes type a space themselves when the key goes down, so Windows never makes the
        /// typed-character message NVDA listens for: NVDA said each letter but never the word, with
        /// speak typed words on. So Space in a text box posts that message itself, and the space goes
        /// in when it arrives, like any letter. NVDA then hears it as it does in Notepad.
        /// </summary>
        public static void SpaceAsTyping()
        {
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TextBox), System.Windows.Input.Keyboard.PreviewKeyDownEvent,
                new System.Windows.Input.KeyEventHandler((s, e) =>
                {
                    if (e.Key != System.Windows.Input.Key.Space || s is not System.Windows.Controls.TextBox tb || tb.IsReadOnly) return;
                    var mods = System.Windows.Input.Keyboard.Modifiers;
                    if (mods != System.Windows.Input.ModifierKeys.None && mods != System.Windows.Input.ModifierKeys.Shift) return;
                    if (System.Windows.Input.InputMethod.Current?.ImeState == System.Windows.Input.InputMethodState.On) return;
                    if (PresentationSource.FromVisual(tb) is not System.Windows.Interop.HwndSource src) return;
                    e.Handled = true;
                    PostMessage(src.Handle, 0x0102, (IntPtr)' ', (IntPtr)1); // WM_CHAR
                }));
            EventManager.RegisterClassHandler(typeof(System.Windows.Controls.TextBox), System.Windows.Input.TextCompositionManager.PreviewTextInputEvent,
                new System.Windows.Input.TextCompositionEventHandler((s, e) =>
                {
                    if (e.Text != " " || s is not System.Windows.Controls.TextBox tb || tb.IsReadOnly) return;
                    e.Handled = true;
                    if (tb.MaxLength > 0 && tb.Text.Length - tb.SelectionLength >= tb.MaxLength) return;
                    int at = tb.SelectionStart;
                    tb.SelectedText = " ";
                    tb.Select(at + 1, 0);
                }));
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public static string LogPath => ErrorLog.FilePath;

        /// <summary>Set when this copy was started after a broken update was rolled back: the version that failed.</summary>
        public static string? RolledBackFrom;
        /// <summary>The main window has opened (for the update trial).</summary>
        public static bool WindowShown;

        private static void Log(Exception e) => ErrorLog.Write(e);

        /// <summary>Deletes a self-test data file and everything saving leaves next to it (the new, spare and damaged copies).</summary>
        internal static void DeleteTestFiles(string file)
        {
            try
            {
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(file)!))
                    if (Path.GetFileName(f).StartsWith(Path.GetFileName(file), StringComparison.OrdinalIgnoreCase)) File.Delete(f);
            }
            catch { }
        }

        public static string Resource(string name)
        {
            using var s = typeof(App).Assembly.GetManifestResourceStream(name);
            if (s == null) return "";
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        /// <summary>
        /// Checks the parts that can break without anyone noticing: every sound can be made from the
        /// built-in recordings, dates are understood, the urgent rules and the summary hold, saving and
        /// loading keep every task, and the main window builds. Writes the result to
        /// TEMP\busybunny-selftest.txt and exits with 0 when everything passed.
        /// </summary>
        private static int SelfTest()
        {
            string result, musicLevel = "";
            // Nothing the self-test does goes into her error log.
            ErrorLog.Enabled = false;
            try
            {
                foreach (string s in Sounds.All.Skip(1))
                    if (Sounds.Length(s) < 0.2) throw new Exception("sound did not build: " + s);
                foreach (Sounds.Event e in Enum.GetValues<Sounds.Event>())
                    if (!Sounds.All.Contains(Sounds.Default(e))) throw new Exception("no default sound for " + e);

                var now = new DateTime(2026, 10, 12, 9, 0, 0); // a Monday
                void Date(string text, DateTime? want, bool time)
                {
                    var (got, hasTime) = Dates.Parse(text, now);
                    if (got != want || (want != null && hasTime != time)) throw new Exception("date '" + text + "' gave " + got + ", wanted " + want);
                }
                Date("tomorrow", now.Date.AddDays(1), false);
                Date("friday", new DateTime(2026, 10, 16), false);
                Date("Friday at 3", new DateTime(2026, 10, 16, 15, 0, 0), true);
                Date("10/20", new DateTime(2026, 10, 20), false);
                Date("today at 9:30 am", new DateTime(2026, 10, 12, 9, 30, 0), true);
                Date("next monday", new DateTime(2026, 10, 19), false);
                Date("blorp", null, false);
                Date("3pm", new DateTime(2026, 10, 12, 15, 0, 0), true);
                Date("at 3", new DateTime(2026, 10, 12, 15, 0, 0), true);
                Date("tomorrow 10:15 am", new DateTime(2026, 10, 13, 10, 15, 0), true);
                Date("wed", new DateTime(2026, 10, 14), false);
                Date("oct 20", new DateTime(2026, 10, 20), false);
                // Edit task writes dates this way; a past one must keep its year.
                Date("10/5/2026 at 3:00 pm", new DateTime(2026, 10, 5, 15, 0, 0), true);
                Date("9/1/2026", new DateTime(2026, 9, 1), false);
                Language.SelfTest();
                var (peak, rms) = MusicLoop.Measure(40);
                if (peak < 0.02 || rms < 0.002) throw new Exception("the background music is silent: peak " + peak + ", average " + rms);
                if (peak > 0.99) throw new Exception("the background music clips: peak " + peak);
                musicLevel = " music peak " + peak.ToString("0.00") + " average " + rms.ToString("0.000");
                foreach (string inst in Sounds.InstrumentNames)
                    if (Sounds.Length(Sounds.ClimbPrefix + inst) < 5) throw new Exception("update climb did not build on " + inst);

                // Updates: three versions newer, a draft and a pre-release left out, the newest one still without its files.
                string releases = "[" +
                    "{\"tag_name\":\"v9.0.0\",\"body\":\"building\",\"assets\":[]}," +
                    "{\"tag_name\":\"v8.0.0\",\"draft\":true,\"body\":\"draft\",\"assets\":[]}," +
                    "{\"tag_name\":\"v7.0.0-beta\",\"prerelease\":true,\"body\":\"beta\",\"assets\":[]}," +
                    "{\"tag_name\":\"v0.5.0\",\"body\":\"Five\",\"assets\":[{\"name\":\"BusyBunny-x64.exe\",\"browser_download_url\":\"https://x/5\",\"digest\":\"sha256:ab\"}]}," +
                    "{\"tag_name\":\"v0.4.0\",\"body\":\"Four\",\"assets\":[{\"name\":\"BusyBunny-x64.exe\",\"browser_download_url\":\"https://x/4\",\"digest\":\"sha256:cd\"}]}," +
                    "{\"tag_name\":\"v0.3.0\",\"body\":\"Three\",\"assets\":[]}," +
                    "{\"tag_name\":\"v0.2.0\",\"body\":\"Two\",\"assets\":[]}]";
                var pick = Updater.Pick(Updater.ParseReleases(releases, "BusyBunny-x64.exe"), new Version(0, 2, 0));
                if (pick == null || pick.Version != new Version(0, 5, 0) || pick.Missed.Count != 3 || pick.Missed[0].Notes != "Five" || pick.Missed[2].Version != new Version(0, 3, 0) || pick.Sha256 != "ab")
                    throw new Exception("update check picked the wrong versions");
                bool saidNotYet = false;
                try { Updater.Pick(Updater.ParseReleases(releases, "BusyBunny-x64.exe"), new Version(0, 5, 0)); }
                catch (InvalidOperationException) { saidNotYet = true; }
                if (!saidNotYet) throw new Exception("a version still being built was not reported as not ready");
                if (Updater.Pick(Updater.ParseReleases(releases, "BusyBunny-x64.exe"), new Version(9, 0, 0)) != null)
                    throw new Exception("update check offered an update to the newest copy");
                // Sweep 9.10: a download with no checksum is refused before anything is fetched.
                string noDigest = "[{\"tag_name\":\"v99.0.0\",\"body\":\"x\",\"assets\":[{\"name\":\"BusyBunny-x64.exe\",\"browser_download_url\":\"https://x/99\"}]}]";
                var unsigned = Updater.Pick(Updater.ParseReleases(noDigest, "BusyBunny-x64.exe"), new Version(0, 1, 0));
                if (unsigned == null || unsigned.Sha256 != null) throw new Exception("a release with no checksum was read wrong");
                bool refused = false;
                try { Updater.PrepareAsync(unsigned).GetAwaiter().GetResult(); }
                catch (InvalidOperationException) { refused = true; }
                if (!refused) throw new Exception("the updater did not refuse a download with no checksum");
                // The speech queue: her key's answer always goes at once; lines nobody asked for wait 2 seconds.
                var t0 = new DateTime(2026, 10, 12, 9, 0, 0);
                if (!Speech.WouldSayNow(false, t0, t0.AddMilliseconds(500), t0.AddMilliseconds(600))) throw new Exception("speech held back the answer to a key press");
                if (Speech.WouldSayNow(false, t0, t0.AddSeconds(-5), t0.AddMilliseconds(600))) throw new Exception("speech did not space out lines nobody asked for");
                if (!Speech.WouldSayNow(false, t0, t0.AddSeconds(-5), t0.AddSeconds(2))) throw new Exception("speech held a line past 2 seconds");
                if (!Speech.WouldSayNow(true, t0, t0.AddSeconds(-5), t0.AddMilliseconds(100))) throw new Exception("speech held back an interrupting line");

                string file = Path.Combine(Path.GetTempPath(), "busybunny-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var store = Store.Load(file);
                    int count = store.Data.Tasks.Count;
                    var (urgent, _) = store.Today(DateTime.Now);
                    if (urgent.Count == 0) throw new Exception("the sample has nothing urgent");
                    if (!store.DueSoon(urgent[0], DateTime.Now) && urgent.Any(t => store.DueSoon(t, DateTime.Now))) throw new Exception("due within 24 hours is not first");
                    if (store.Summary(DateTime.Now).Length == 0) throw new Exception("no summary");
                    if (store.Data.Lists[0].TabName != "Homework (Coffee)") throw new Exception("tab name was " + store.Data.Lists[0].TabName);
                    if (Scales.Rank(store.Data.Lists[0].Scale) != "1 Triple shot, 2 Double shot, 3 Single shot, 4 Decaf.") throw new Exception("scale rank reads wrong");
                    store.Remember("test");
                    store.Data.Tasks[0].Status = Status.Done;
                    store.Save();
                    var again = Store.Load(file);
                    if (again.Data.Tasks.Count != count || again.Data.Tasks[0].Status != Status.Done) throw new Exception("saving lost something");
                    if (store.Undo() == null || store.Data.Tasks[0].Status == Status.Done) throw new Exception("undo did not work");

                    var app = new App();
                    var w = new MainWindow(store, new Settings { CheckForUpdatesAtStart = false });
                    w.Refresh();
                    w.Close();

                    foreach (var f in Features.All)
                    {
                        string other = Path.Combine(Path.GetTempPath(), "busybunny-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                        try { f.SelfTest(Store.Load(other)); }
                        catch (Exception e) { throw new Exception(f.GetType().Name + ": " + e.Message, e); }
                        finally { DeleteTestFiles(other); }
                    }
                }
                finally { DeleteTestFiles(file); }
                result = "ok, version " + Updater.Current + ", " + (Sounds.All.Count - 1) + " sounds," + musicLevel;
            }
            catch (Exception e) { result = "FAIL: " + e; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "busybunny-selftest.txt"), result);
            return result.StartsWith("ok") ? 0 : 1;
        }
    }
}
