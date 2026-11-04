using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>
    /// Body double mode (sweep 3.4 and 8.5, Get going): Bunny starts a sprint with her, says one line, then stays quiet
    /// the whole time (it counts as a sprint for the calm rules and Bunny's check-ins). At the end it asks one question,
    /// how it went, and answers in a line. The minutes count like any sprint, for time per class and real times.
    /// With a Claude key the lines are written fresh from the task's name (never a private task); otherwise the library.
    /// </summary>
    public sealed class SmartBody : IFeature
    {
        private const string Id = "bodydouble";
        public static readonly int[] MinuteChoices = { 10, 15, 25, 45, 60 };
        private static SmartBody? _me;
        private MainWindow _w = null!;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        private DateTime _endUtc, _lastUtc;
        private double _awake;
        private int _minutes;
        private TaskItem? _task;
        private bool _asking;

        /// <summary>A body double sprint is running.</summary>
        public static bool Running => _me != null && (_me._timer.IsEnabled || _me._asking);

        public static readonly string[] Answers = { "I finished it", "I made progress", "I got stuck", "I got distracted, and that is okay" };

        public void Attach(MainWindow w)
        {
            _me = this;
            _w = w;
            Tools.Register(new BunnyTool(Id, "Body double", Drawers.GetGoing, "Bunny works alongside you for a sprint, stays quiet, and checks in at the end. Again tells you the time left.", StartOrCheck) { Since = "0.14.0" });
            Calm.OtherSprints.Add(() => Running);
            _timer.Tick += (_, _) => Tick();
            w.Closed += (_, _) => _timer.Stop();
        }

        private void StartOrCheck()
        {
            if (_timer.IsEnabled)
            {
                var left = _endUtc - DateTime.UtcNow;
                string time = left.TotalSeconds < 60 ? "less than a minute" : SmartUi.Count((int)Math.Ceiling(left.TotalMinutes), "minute");
                var r = MessageBox.Show(_w, time + " left with Bunny" + (_task != null ? " on " + _task.Name : "") + ". Stop now?", "Body double", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
                if (r != MessageBoxResult.Yes || !_timer.IsEnabled) return;
                _timer.Stop();
                _w.Say("Stopped. Bunny packs up its books. No harm done.", true);
                return;
            }
            if (FocusTools.SprintRunning) { _w.Say("A sprint is already running. Bunny will join the next one.", true); return; }
            var now = DateTime.Now;
            var s = _w.Store;
            var (urgent, rest) = s.Today(now);
            var pool = urgent.Concat(rest).Concat(s.Data.Tasks.Where(t => t.Status == Status.ThisWeek)).Where(t => t.Status != Status.Done && !s.ListOf(t).Archived).Distinct().Take(40).ToList();
            var chosen = _w.ChosenTask;
            if (chosen != null && chosen.Status != Status.Done && !pool.Contains(chosen)) pool.Insert(0, chosen);
            var minutes = new ComboBox { ItemsSource = MinuteChoices.Select(m => m + " minutes").ToList(), SelectedIndex = 2 };
            var which = new ComboBox { ItemsSource = new[] { "No particular task" }.Concat(pool.Select(t => s.Line(t, now, true))).ToList() };
            which.SelectedIndex = chosen != null && pool.IndexOf(chosen) >= 0 ? pool.IndexOf(chosen) + 1 : 0;
            var back = _w.KeepFocus();
            bool ok = SmartUi.Form(_w, "Body double", "_Start", p =>
            {
                p.Children.Add(SmartUi.Intro("Bunny works alongside you, quietly. It says hello, then nothing at all until the time is up.", 520));
                p.Children.Add(Layout.Row("_Minutes", minutes, 200));
                p.Children.Add(Layout.Row("_Task (optional)", which, 520));
            }, () => null);
            back();
            if (!ok) return;
            _minutes = MinuteChoices[Math.Max(0, minutes.SelectedIndex)];
            _task = which.SelectedIndex > 0 ? pool[which.SelectedIndex - 1] : null;
            _endUtc = DateTime.UtcNow.AddMinutes(_minutes);
            _lastUtc = DateTime.UtcNow;
            _awake = 0;
            _timer.Start();
            GameEvents.RaiseSprintStarted(_minutes, _task);
            if (_task != null) GameEvents.RaiseTaskStarted(_task);
            Hello();
        }

        /// <summary>What Claude may hear about the task: its name only when it is on a list Bunny may see.</summary>
        public static string About(Store s, TaskItem? t) =>
            t != null && SmartBunny.MayShare(s, t) && !s.ListOf(t).Archived ? " She is working on: " + t.Name + "." : " She did not say what she is working on.";

        private async void Hello()
        {
            var s = _w.Store;
            string start = "Body double for " + _minutes + " minutes" + (_task != null ? " on " + _task.Name : "") + ". ";
            string line = await BunnyWrite.Short(s, Id, "Write one short line, under 25 words, as a study buddy sitting down to work quietly next to a student for " + _minutes + " minutes. " + SmartText.Voice(_w.AppSettings) + About(s, _task) + " End by saying you will be quiet now.", "smart.body.start");
            _w.Say(start + line + " Run Body double again for the time left.", true);
        }

        private void Tick()
        {
            var now = DateTime.UtcNow;
            double gap = (now - _lastUtc).TotalSeconds;
            _lastUtc = now;
            if (gap > 0) _awake += Math.Min(gap, 5);
            if (now >= _endUtc) Finish();
        }

        /// <summary>The minutes that count: the awake time, or all of them when the laptop never slept.</summary>
        public static int Counted(int planned, double awakeSeconds)
        {
            int awake = Math.Clamp((int)Math.Round(awakeSeconds / 60), 0, planned);
            return awake < planned - 1 ? awake : planned;
        }

        public static void Log(Store s, int minutes, TaskItem? t, DateTime now)
        {
            s.Remember("finishing a body double sprint");
            s.Data.Log.Add(new LogEntry
            {
                When = now, Kind = "Sprint", Minutes = minutes, Text = "Body double sprint, " + minutes + " minutes" + (t != null ? " on " + t.Name : ""),
                Tag = t?.Tag ?? "", List = t?.List ?? "", TaskId = t?.Id,
            });
        }

        private void Finish()
        {
            _timer.Stop();
            int minutes = Counted(_minutes, _awake);
            if (minutes == 0) { _w.Say("The body double sprint ended while the laptop was asleep, so there was nothing to count.", true); return; }
            Log(_w.Store, minutes, _task, DateTime.Now);
            _w.SaveAndRefresh();
            Sounds.Play(Sounds.Event.SprintFinished);
            GameEvents.RaiseSprintDone(minutes);
            _asking = true;
            Ask(minutes);
        }

        /// <summary>The one question at the end. It waits until Busy Bunny is in front with no window open, so it never lands on top of something else.</summary>
        private void Ask(int minutes)
        {
            if (!_w.IsActive || _w.OwnedWindows.Count > 0)
            {
                var wait = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
                wait.Tick += (_, _) => { if (_w.IsActive && _w.OwnedWindows.Count == 0) { wait.Stop(); Ask(minutes); } };
                wait.Start();
                return;
            }
            var t = _task;
            string question = Content.Pick(_w.Store, "smart.body.end");
            var back = _w.KeepFocus();
            int pick;
            try { pick = SmartUi.Choose(_w, "Body double, time is up", minutes + " minutes done. " + question, Answers); }
            finally { _asking = false; back(); }
            if (pick < 0) { _w.Say("Okay. Bunny is proud of you either way.", true); return; }
            if (pick == 0 && t != null && t.Status != Status.Done && _w.Store.Data.Tasks.Contains(t)) _w.SetTaskStatus(t, Status.Done);
            Reply(pick);
        }

        private async void Reply(int pick)
        {
            string line = await BunnyWrite.Short(_w.Store, Id, "A student just finished a " + _minutes + " minute body double sprint with you and said: " + Answers[pick] + ". Answer in one kind line, under 25 words, " +
                (pick == 2 ? "with one tiny next step. " : "") + SmartText.Voice(_w.AppSettings) + About(_w.Store, _task), "smart.body.after");
            _w.Say(line);
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Body double: " + what); }
            Need(Counted(25, 25 * 60) == 25 && Counted(25, 24.4 * 60) == 25 && Counted(25, 10 * 60) == 10 && Counted(25, 0) == 0, "counted minutes");
            var t = store.Data.Tasks[0];
            int before = store.Data.Log.Count;
            Log(store, 25, t, DateTime.Now);
            Need(store.Data.Log.Count == before + 1 && store.Data.Log[^1].Kind == "Sprint" && store.Data.Log[^1].TaskId == t.Id, "the sprint was not logged");
            store.Undo();
            Need(store.Data.Log.Count == before, "undo did not take the sprint back");
            var secret = new TaskList { Name = "ZZPRIVATE", PrivateFromBunny = true };
            store.Data.Lists.Add(secret);
            Need(!About(store, new TaskItem { Name = "ZZSECRET", List = secret.Name }).Contains("ZZ") && About(store, t).Contains(t.Name), "a private task reached Claude");
            foreach (var topic in new[] { "smart.body.start", "smart.body.end", "smart.body.after" }) Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            Need(!Running && Calm.OtherSprints.Count > 0, "body double is not known to the calm rules");
        }
    }
}
