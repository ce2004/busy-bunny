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
            if (args.Length > 1 && args[0] == "--after-update" && int.TryParse(args[1], out int pid)) Updater.FinishUpdate(pid);
            else Updater.CleanLeftovers();

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
            var w = new MainWindow(store, settings);
            if (settings.CheckForUpdatesAtStart) w.Loaded += (_, _) => w.CheckForUpdates(quiet: true);
            return app.Run(w);
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

                string file = Path.Combine(Path.GetTempPath(), "busybunny-selftest-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    var store = Store.Load(file);
                    int count = store.Data.Tasks.Count;
                    var (urgent, _) = store.Today(DateTime.Now);
                    if (urgent.Count == 0) throw new Exception("the sample has nothing urgent");
                    if (!store.DueSoon(urgent[0], DateTime.Now) && urgent.Any(t => store.DueSoon(t, DateTime.Now))) throw new Exception("due within 24 hours is not first");
                    if (store.Summary(DateTime.Now).Length == 0) throw new Exception("no summary");
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
