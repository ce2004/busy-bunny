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
            if (args.Length > 2 && args[0] == "--write-sound") { File.WriteAllBytes(args[2], Sounds.WavOf(args[1]) ?? Array.Empty<byte>()); return 0; }
            bool updated = args.Length > 1 && args[0] == "--after-update" && int.TryParse(args[1], out int pid);
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

            var app = new App { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.DispatcherUnhandledException += (_, e) =>
            {
                Log(e.Exception);
                MessageBox.Show("Something went wrong in Busy Bunny: " + e.Exception.Message + "\n\nYour tasks are safe. The details are in " + LogPath + ".", "Busy Bunny", MessageBoxButton.OK, MessageBoxImage.Warning);
                e.Handled = true;
            };
            var settings = Settings.Load();
            settings.Apply();
            Sounds.Warm();
            var store = Store.Load();
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
                Version? from = updated && args.Length > 2 ? Updater.NormalizeTag(args[2]) : null;
                if (updated) w.Dispatcher.BeginInvoke(() => w.ShowChanges(from), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                w.StartAutoUpdates();
                w.OnStarted();
            });
            int code = app.Run();
            only.ReleaseMutex();
            return code;
        }

        public static string LogPath => Path.Combine(Store.Folder, "error-log.txt");

        private static void Log(Exception e)
        {
            try
            {
                Directory.CreateDirectory(Store.Folder);
                File.AppendAllText(LogPath, DateTime.Now + " version " + Updater.Current + "\n" + e + "\n\n");
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
            string result;
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
                        finally { try { File.Delete(other); } catch { } }
                    }
                }
                finally { try { File.Delete(file); } catch { } }
                result = "ok, version " + Updater.Current + ", " + (Sounds.All.Count - 1) + " sounds";
            }
            catch (Exception e) { result = "FAIL: " + e; }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "busybunny-selftest.txt"), result);
            return result.StartsWith("ok") ? 0 : 1;
        }
    }
}
