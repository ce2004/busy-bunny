using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>
    /// A small local error log, errors.txt in the data folder, kept under about 256 KB. Never put task
    /// names, notes or anything she typed in it: Help, Report a problem copies its last lines to share.
    /// </summary>
    public static class ErrorLog
    {
        /// <summary>Off during the self-test, so a test never writes to her log.</summary>
        public static bool Enabled = true;
        public static string FilePath => Path.Combine(Store.Folder, "errors.txt");
        private const long Cap = 256 * 1024;
        private static readonly object Gate = new();

        public static void Write(string text)
        {
            if (!Enabled) return;
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Store.Folder);
                    var f = new FileInfo(FilePath);
                    if (f.Exists && f.Length > Cap)
                    {
                        var lines = File.ReadAllLines(FilePath);
                        File.WriteAllLines(FilePath, lines.Skip(lines.Length / 2));
                    }
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " version " + Updater.Current + ": " + text.TrimEnd() + Environment.NewLine);
                }
            }
            catch { }
        }

        public static void Write(Exception e) => Write("Error: " + e);

        /// <summary>The last lines of the log, oldest first.</summary>
        public static List<string> Last(int count)
        {
            try
            {
                lock (Gate)
                    return File.Exists(FilePath) ? File.ReadAllLines(FilePath).TakeLast(count).ToList() : new List<string>();
            }
            catch { return new List<string>(); }
        }
    }

    /// <summary>
    /// Keeping her tasks safe (sweep 2.2, 2.3, 2.6, 9.2, 9.13): a failed save is said, retried every few
    /// seconds and tried again on closing and at Windows sign-out; what happened at start (a damaged file
    /// replaced by a backup, an update rolled back) is said once; the first start on a new PC explains
    /// that tasks stay on each PC; the daily backup can also go to a folder she picks; Help, Report a
    /// problem copies the details to share.
    /// </summary>
    public sealed class DataSafety : IFeature
    {
        private MainWindow? _w;
        private DispatcherTimer? _retry;
        private bool _toldFailing, _sessionEnding;
        private DateTime _settingsToldAt = DateTime.MinValue;

        public void Attach(MainWindow w)
        {
            _w = w;
            var store = w.Store;
            Store.ExtraBackupFolder = w.AppSettings.BackupCopyFolder.Length > 0 ? w.AppSettings.BackupCopyFolder : null;
            _retry = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _retry.Tick += (_, _) =>
            {
                if (!store.Dirty) { _retry.Stop(); return; }
                store.TrySave();
            };
            store.SaveFailed += e =>
            {
                if (!_toldFailing)
                {
                    _toldFailing = true;
                    w.Say("I could not save your tasks just now" + Reason(e) + ". Your change is still here, and I will keep trying every few seconds.", true);
                }
                _retry.Start();
            };
            store.SaveRecovered += () =>
            {
                _retry.Stop();
                if (_toldFailing) w.Say("Your tasks are saved again.");
                _toldFailing = false;
            };
            Settings.SaveFailed += _ =>
            {
                if (DateTime.Now - _settingsToldAt < TimeSpan.FromMinutes(2)) return;
                _settingsToldAt = DateTime.Now;
                w.Say("I could not save your settings just now. I will try again the next time you change one.");
            };

            if (Application.Current != null) Application.Current.SessionEnding += (_, _) => { _sessionEnding = true; store.TrySave(); };
            w.Closing += OnClosing;
            w.Started += SayWhatHappened;

            w.AddMenuItem("Help", "Report a _problem (copies the details)", ReportProblem);
            w.AddMenuItem("Help", "Where my tasks are sa_ved", () => w.Say(WhereWords(w.AppSettings), true));
        }

        /// <summary>A few plain words for why saving failed.</summary>
        private static string Reason(Exception e)
        {
            int code = e.HResult & 0xFFFF;
            if (code is 112 or 39) return ", because the disk is full";
            if (code is 32 or 33) return ", because another program has the file open";
            if (e is UnauthorizedAccessException) return ", because Windows would not let me write to the file";
            return "";
        }

        private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            if (_w == null || e.Cancel) return;
            var store = _w.Store;
            if (store.TrySave()) return;
            bool reallyClosing = Reminders.Quitting || !_w.AppSettings.KeepInTray;
            if (!reallyClosing || _sessionEnding) return;
            var answer = MessageBox.Show(_w,
                "Busy Bunny could not save your latest changes" + (store.LastSaveError is string why ? ": " + why : ".") +
                "\n\nIf it closes now, those changes are lost. Close anyway?\n\nChoose No to keep Busy Bunny open while it keeps trying. File, Export a backup can also save a copy somewhere else.",
                "Busy Bunny", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                Reminders.Quitting = false;
                _retry?.Start();
            }
        }

        /// <summary>Once, after the splash: anything that went wrong at start, and the first-run line about other PCs.</summary>
        private void SayWhatHappened()
        {
            if (_w == null) return;
            var notes = new List<string>();
            if (App.RolledBackFrom is string bad) notes.Add("The update to version " + bad + " did not start properly, so I put version " + Updater.Current + " back. Your tasks are fine.");
            if (_w.Store.LoadNote is string n) notes.Add(n);
            if (Settings.LoadNote is string sn) notes.Add(sn);
            if (_w.Store.FirstRun && !_w.AppSettings.PcNoteSaid)
            {
                _w.AppSettings.PcNoteSaid = true;
                _w.AppSettings.Save();
                notes.Add("One thing to know: your tasks are kept on this PC only, so they will not show up on your other computer by themselves yet. To move them, use File, Export a backup here, then File, Restore from a backup on the other one.");
            }
            _w.Store.LoadNote = null;
            Settings.LoadNote = null;
            if (notes.Count > 0) _w.Say(string.Join(" ", notes));
        }

        /// <summary>Plain words for where her data and backups are.</summary>
        public static string WhereWords(Settings settings)
        {
            string s = "Your tasks are saved on this PC in " + Store.FilePath + ". Each day a backup goes in " + Store.BackupFolder + ", and the last 30 days are kept.";
            if (settings.BackupCopyFolder.Length > 0) s += " A copy of each daily backup also goes in " + Store.ExtraFolderIn(settings.BackupCopyFolder) + ".";
            else s += " To keep a copy somewhere else too, like your Google Drive folder, use Options, Settings, Backups.";
            return s;
        }

        // ---- Help, Report a problem (sweep 9.13) ----

        /// <summary>The details to share: version, Windows, architecture, counts and the last 50 log lines. No task names or notes.</summary>
        public static string ProblemReport(Store store)
        {
            var lines = new List<string>
            {
                "Busy Bunny problem report, " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                "Busy Bunny version " + Updater.Current + ", " + Updater.Arch + " build",
                "Windows: " + RuntimeInformation.OSDescription + " (" + Environment.OSVersion.Version + "), " + RuntimeInformation.OSArchitecture + " PC, running as " + RuntimeInformation.ProcessArchitecture,
                ".NET: " + RuntimeInformation.FrameworkDescription,
                "Tasks: " + store.Data.Tasks.Count + " in " + store.Data.Lists.Count + " lists, " + store.UndoCount + " undo steps",
                "Saving: " + (store.Dirty ? "failing right now: " + store.LastSaveError : "fine"),
                "",
                "Last lines of the error log:",
            };
            var log = ErrorLog.Last(50);
            if (log.Count == 0) lines.Add("(empty)");
            lines.AddRange(log);
            return string.Join(Environment.NewLine, lines);
        }

        private void ReportProblem()
        {
            if (_w == null) return;
            string text = ProblemReport(_w.Store);
            for (int i = 0; i < 5; i++)
            {
                try
                {
                    Clipboard.SetText(text);
                    _w.Say("I copied the details for a problem report: the version, Windows, and the last lines of the error log, with no task names or notes. Paste them wherever you are reporting the problem.", true);
                    return;
                }
                catch { System.Threading.Thread.Sleep(100); }
            }
            _w.Say("I could not reach the clipboard just now. Try Report a problem again in a moment.", true);
        }

        // ---- Settings, Backups (sweep 2.2) ----

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings)
        {
            yield return new BackupsPage(store, settings, _w);
        }

        private sealed class BackupsPage : ISettingsPage
        {
            private readonly Store _store;
            private readonly Settings _settings;
            private readonly MainWindow? _w;
            private readonly CheckBox _on;
            private readonly TextBox _folder;
            private readonly Button _browse;

            public BackupsPage(Store store, Settings settings, MainWindow? w)
            {
                _store = store;
                _settings = settings;
                _w = w;
                var panel = new StackPanel();
                var where = new TextBox
                {
                    Text = "Your tasks are saved on this PC in " + Store.FilePath + ". Each day a backup goes in " + Store.BackupFolder +
                        ", and the last 30 days are kept. These stay on this PC, so if it is lost, so are they. A copy in a folder that syncs, like Google Drive, keeps them safe.",
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    BorderThickness = new Thickness(0),
                };
                panel.Children.Add(Layout.Row("Where your tasks are _kept", where, 460));
                _on = new CheckBox { Content = "Also _copy the daily backup to a folder I pick", IsChecked = settings.BackupCopyFolder.Length > 0, Margin = new Thickness(0, 6, 0, 4) };
                panel.Children.Add(_on);
                _folder = new TextBox { Text = settings.BackupCopyFolder };
                panel.Children.Add(Layout.Row("Backup copy fol_der", _folder, 460));
                _browse = Layout.Button("Bro_wse...");
                AutomationProperties.SetName(_browse, "Browse for the backup copy folder");
                _browse.Click += (_, _) => Browse();
                panel.Children.Add(_browse);
                panel.Children.Add(new TextBlock
                {
                    Text = "The copies go in a Busy Bunny backups folder inside the one you pick, the last 30 days of them.",
                    TextWrapping = TextWrapping.Wrap,
                    MaxWidth = 460,
                    Margin = new Thickness(0, 8, 0, 0),
                });
                Content = panel;
            }

            public string Name => "Backups";
            public FrameworkElement Content { get; }

            private void Browse()
            {
                var owner = Window.GetWindow(_browse);
                var d = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a folder for the backup copy" };
                if (_folder.Text.Trim() is string now && now.Length > 0 && Directory.Exists(now)) d.InitialDirectory = now;
                if ((owner != null ? d.ShowDialog(owner) : d.ShowDialog()) == true)
                {
                    _folder.Text = d.FolderName;
                    _on.IsChecked = true;
                    Speech.Say(_folder, "Backup copy folder: " + d.FolderName + ".");
                }
                _browse.Focus();
            }

            public string? Validate()
            {
                if (_on.IsChecked != true) return null;
                string f = _folder.Text.Trim();
                if (f.Length == 0) return "Pick a folder for the backup copy with Browse, or uncheck Also copy the daily backup.";
                if (!Directory.Exists(f)) return "I cannot find the backup copy folder " + f + ". Check its name, or pick it with Browse.";
                return null;
            }

            public void Apply()
            {
                string f = _on.IsChecked == true ? _folder.Text.Trim() : "";
                bool changed = f != _settings.BackupCopyFolder;
                _settings.BackupCopyFolder = f;
                Store.ExtraBackupFolder = f.Length > 0 ? f : null;
                if (!changed || f.Length == 0 || _w == null) return;
                // A first copy now, so she knows it works.
                if (_store.CopyBackupTo(f, null, out string error))
                    Speech.Say(_w, "Today's backup is copied to " + Store.ExtraFolderIn(f) + ", and a new copy goes there each day.");
                else
                    Speech.Say(_w, "I could not copy the backup to that folder: " + error + ". I will keep trying each day.");
            }
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("data safety: " + what); }
            string dir = Path.Combine(Path.GetTempPath(), "busybunny-safety-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                string file = Path.Combine(dir, "tasks.json"), backups = Path.Combine(dir, "Backups");

                // A damaged tasks.json loads the newest good backup and says so; the damaged file is kept.
                var a = Store.Load(file, backups);
                Need(a.FirstRun, "an empty folder is a first run");
                a.Data.Tasks.Clear();
                a.Data.Tasks.Add(new TaskItem { Name = "keep me", List = "Homework" });
                Need(a.TrySave(), "first save");
                Need(Directory.GetFiles(backups).Length == 1, "the daily backup was made");
                File.WriteAllText(file, "{ \"Tasks\": [ { \"Name\": \"half");
                foreach (var extra in new[] { file + ".new", file + ".bak" }) if (File.Exists(extra)) File.Delete(extra);
                var b = Store.Load(file, backups);
                Need(b.Data.Tasks.Any(t => t.Name == "keep me"), "a damaged file did not load the backup");
                Need(b.LoadNote != null && b.LoadNote.Contains("damaged"), "a damaged file was not announced");
                Need(!b.FirstRun && !b.Data.Tasks.Any(t => t.Name == "refill prescription"), "a damaged file brought in the sample tasks");
                Need(Directory.GetFiles(dir).Count(x => Path.GetFileName(x).StartsWith("tasks.json.damaged-")) == 1, "the damaged file was not kept");

                // Damaged with no backup at all: empty lists, never the sample tasks over her file.
                string lone = Path.Combine(dir, "lone.json");
                File.WriteAllText(lone, "");
                var c = Store.Load(lone);
                Need(c.Data.Tasks.Count == 0 && c.Data.Lists.Count > 0 && c.LoadNote != null, "a damaged file with no backup got sample tasks");

                // A value from a newer version, and nulls, do not make the file unreadable.
                Need(b.TrySave(), "save after rescue");
                string text = File.ReadAllText(file).Replace("\"Status\":\"ThisWeek\"", "\"Status\":\"Snoozed\"").Replace("\"Notes\":\"\"", "\"Notes\":null");
                File.WriteAllText(file, text);
                var d = Store.Load(file, backups);
                Need(d.LoadNote == null && d.Data.Tasks.Count == b.Data.Tasks.Count, "an unknown status made the file unreadable");
                Need(d.Data.Tasks.All(t => t.Notes != null), "a null was not filled in");
                File.WriteAllText(lone, "{\"Lists\":null,\"Tasks\":[{\"Name\":\"x\",\"Tag\":null,\"Subtasks\":null}]}");
                var e = Store.Load(lone);
                Need(e.Data.Lists.Count > 0 && e.Data.Tasks.Count == 1 && e.Data.Tasks[0].Tag == "", "nulls in the file broke loading");

                // A file another program has open at start is waited for, not treated as damaged.
                Need(d.TrySave(), "save before the lock test");
                var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None);
                var release = new System.Threading.Thread(() => { System.Threading.Thread.Sleep(1200); held.Dispose(); });
                release.Start();
                var f = Store.Load(file, backups);
                release.Join();
                Need(f.LoadNote == null && f.Data.Tasks.Any(t => t.Name == "keep me"), "a locked file was treated as damaged");

                // A failed save is caught, flagged, raised and retried; the file on disk stays whole.
                int failed = 0, recovered = 0;
                f.SaveFailed += _ => failed++;
                f.SaveRecovered += () => recovered++;
                f.Data.Tasks.Add(new TaskItem { Name = "added while locked", List = "Homework" });
                using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None))
                {
                    f.Save(); // never throws
                    Need(f.Dirty && failed == 1 && f.LastSaveError != null, "a failed save was not flagged");
                }
                Need(Store.ReadFile(file)?.Tasks.Any(t => t.Name == "keep me") == true, "a failed save damaged the file");
                Need(f.TrySave() && !f.Dirty && recovered == 1, "the retry did not save");
                Need(Store.ReadFile(file)?.Tasks.Any(t => t.Name == "added while locked") == true, "the retried save lost the change");

                // A missing tasks.json with its new copy left over loads that copy.
                File.Copy(file, file + ".new", true);
                File.Delete(file);
                var g = Store.Load(file, backups);
                Need(g.Data.Tasks.Any(t => t.Name == "added while locked") && g.LoadNote != null, "a left-over new copy was not used");
                Need(g.TrySave() && File.Exists(file), "saving after a missing file");

                // A new copy left newer than tasks.json (a swap failed, then it closed) is the one loaded.
                g.Data.Tasks.Add(new TaskItem { Name = "only in the new copy", List = "Homework" });
                g.WriteCopy(file + ".new");
                File.SetLastWriteTimeUtc(file + ".new", File.GetLastWriteTimeUtc(file).AddMinutes(1));
                var h = Store.Load(file, backups);
                Need(h.Data.Tasks.Any(t => t.Name == "only in the new copy") && h.LoadNote != null, "a newer new copy was not loaded");
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            // Smarter Bunny's summary never counts a private list (sweep 2.1), nor an archived one (R3 19).
            var now = DateTime.Now;
            string Filter(Store s) => s.SummaryFor(now, t => !s.ListOf(t).PrivateFromBunny);
            string before = Filter(store);
            store.Data.Lists.Add(new TaskList { Name = "ZZSECRET list", PrivateFromBunny = true });
            store.Data.Lists.Add(new TaskList { Name = "Old list", Archived = true });
            store.Data.Tasks.Add(new TaskItem { Name = "ZZSECRET meeting", List = "ZZSECRET list", IsMeeting = true, Type = TaskType.Meeting, Due = now.AddMinutes(30), DueHasTime = true, Status = Status.Today });
            store.Data.Tasks.Add(new TaskItem { Name = "ZZSECRET test", List = "ZZSECRET list", Type = TaskType.Test, Due = now.Date.AddDays(-2), Status = Status.Today, Size = Size.Large });
            store.Data.Tasks.Add(new TaskItem { Name = "ZZSECRET done", List = "ZZSECRET list", Status = Status.Done, DoneAt = now });
            store.Data.Tasks.Add(new TaskItem { Name = "archived overdue", List = "Old list", Due = now.Date.AddDays(-3), Status = Status.Today, IsMeeting = true });
            string after = Filter(store);
            Need(!after.Contains("ZZSECRET") && after == before, "a private list changed Bunny's summary: " + after + " (was " + before + ")");
            Need(store.Summary(now) != after, "the private tasks did not count in her own summary");
            store.Data.Tasks.RemoveAll(t => t.List == "ZZSECRET list");
            Need(store.Summary(now) == before, "an archived list counted in the summary: " + store.Summary(now) + " (was " + before + ")");

            // Undo keeps a list private that is private now (R3 5), and does not bring back a reminder that already went off.
            var r = new TaskItem { Name = "pills", List = "Homework", RemindAt = now.AddMinutes(-1), RemindRepeat = "every day" };
            store.Data.Tasks.Add(r);
            store.Remember("test step");
            store.Data.Lists[0].PrivateFromBunny = true;
            r.RemindAt = now.AddDays(1).AddMinutes(-1);
            Need(store.Undo() == "test step", "undo");
            Need(store.Data.Lists[0].PrivateFromBunny, "undo made a private list visible to Bunny again");
            Need(store.Data.Tasks.First(t => t.Id == r.Id).RemindAt > now, "undo brought back a reminder that already went off");

            // The problem report carries no task names.
            Need(!ProblemReport(store).Contains("pills") && ProblemReport(store).Contains(Updater.Current.ToString()), "problem report");
        }
    }
}
