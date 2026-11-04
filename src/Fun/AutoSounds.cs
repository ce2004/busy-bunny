using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>
    /// Automatic sounds, each with its own switch: confetti checkpoints at 25, 50, 75 and 100 percent of the day's effort,
    /// class tones that climb through the week, a submission sound, a finish line sound for the last task of the day, and
    /// soft ticks in the last minute of a sprint. Built-in recordings only, through the sound bank, a moment after the
    /// app's own sound. Calm mode silences them (the sound bank sees to it).
    /// </summary>
    public sealed class AutoSounds : IFeature
    {
        public sealed class DayMark { public string Day { get; set; } = ""; public int Reached { get; set; } public bool Finished { get; set; } }
        public sealed class ClassWeek { public string Week { get; set; } = ""; public Dictionary<string, HashSet<Guid>> Tags { get; set; } = new(); }

        public const string ConfettiId = "auto.confetti", ClassId = "auto.classtones", SubmitId = "auto.submit", FinishId = "auto.finishline", TickId = "auto.sprintticks";
        public static readonly int[] Checkpoints = { 25, 50, 75, 100 };
        private static readonly string[] ConfettiSounds = { "Pizzicato strings, single", "Pizzicato strings, two up", "Pizzicato strings, rising", "Pizzicato strings, sparkle" };
        private static readonly string[] ClassInstruments = { "Piano", "Flute", "Singing bells", "Xylophone", "Pizzicato strings", "Organ", "Cello", "Strings" };
        private static readonly string[] Climb = { "chord low", "single", "chord high", "four up", "sparkle" };

        private MainWindow _w = null!;
        private Store S => _w.Store;
        private DispatcherTimer? _ticks;
        private DateTime _sprintEnd;

        public void Attach(MainWindow w)
        {
            _w = w;
            for (int i = 0; i < Checkpoints.Length; i++) SoundBank.Register(ConfettiId + Checkpoints[i], ConfettiSounds[i]);
            SoundBank.Register(SubmitId, "Bunny, binky");
            SoundBank.Register(FinishId, "Flute, sparkle");
            SoundBank.Register(TickId, "Soft piano, single");
            void T(string id, string name, string drawer, string what, string more) =>
                Tools.Register(new BunnyTool(id, name, drawer, what, () => { _w.Say(name + " is on. " + more, true); }) { Since = "0.14.0" });
            T(ConfettiId, "Confetti checkpoints", Drawers.Wins, "Automatic: a small sound at 25, 50, 75 and 100 percent of the day's effort.", "");
            T(ClassId, "Class tones that climb", Drawers.Wins, "Automatic: each class tag has its own tone, a step higher each time you finish a task in it this week.", "Each class starts low on Monday.");
            T(SubmitId, "Submission sound", Drawers.Wins, "Automatic: moving a task from Done, not submitted to Done plays its own sound.", "Submit something and listen.");
            T(FinishId, "Finish line sound", Drawers.Wins, "Automatic: the last task on Today plays its own sound.", "Clear Today to hear it.");
            T(TickId, "Sprint countdown sounds", Drawers.GetGoing, "Automatic: soft ticks in the last minute of a sprint.", "Four soft ticks, 15 seconds apart, as a sprint ends.");
            // The confetti tool says where today stands.
            Tools.Register(Tools.Find(ConfettiId)! with { Run = () => _w.Say("Confetti checkpoints is on. You are at " + Percent(S, DateTime.Now) + " percent of today's effort.", true) });

            GameEvents.TaskDone += OnDone;
            GameEvents.TaskUndone += t => UnClass(S, t, DateTime.Now);
            GameEvents.StatusChanged += (t, old) =>
            {
                if (old == Status.DoneNotSubmitted && t.Status == Status.Done && Tools.IsOn(SubmitId)) PlanKit.PlayLater(SubmitId);
            };
            GameEvents.SprintStarted += (min, _) => StartTicks(min);
        }

        // ---- Rules ----

        public static int Percent(Store s, DateTime now)
        {
            var (_, _, pd, pt) = s.Progress(now);
            return pt == 0 ? 0 : pd * 100 / pt;
        }

        /// <summary>The highest checkpoint newly passed, or 0.</summary>
        public static int Crossed(int reachedBefore, int percent) => Checkpoints.Where(c => c > reachedBefore && percent >= c).DefaultIfEmpty(0).Max();

        /// <summary>Counts the task for its class this week (once). The new count, or 0 when it is not a class or already counted.</summary>
        public static int CountClass(Store s, TaskItem t, DateTime now)
        {
            if (t.Tag.Length == 0 || !IsClass(s, t.Tag)) return 0;
            var c = s.World.Get<ClassWeek>(ClassId);
            string week = s.Key(Engine.WeekStart(now));
            if (c.Week != week) c = new ClassWeek { Week = week };
            string key = t.Tag.ToLowerInvariant();
            if (!c.Tags.TryGetValue(key, out var set)) c.Tags[key] = set = new();
            if (!set.Add(t.Id)) return 0;
            s.World.Set(ClassId, c);
            s.World.Save();
            return set.Count;
        }

        public static void UnClass(Store s, TaskItem t, DateTime now)
        {
            var c = s.World.Get<ClassWeek>(ClassId);
            if (c.Week != s.Key(Engine.WeekStart(now)) || !c.Tags.TryGetValue(t.Tag.ToLowerInvariant(), out var set) || !set.Remove(t.Id)) return;
            s.World.Set(ClassId, c);
            s.World.Save();
        }

        public static bool IsClass(Store s, string tag) =>
            s.Data.Classes.Any(c => c.Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)) || s.Data.Tags.Any(x => x.Name.Equals(tag, StringComparison.OrdinalIgnoreCase) && x.Description.Length > 0);

        public static string ClassTone(string tag, int count)
        {
            int h = tag.ToLowerInvariant().Aggregate(7, (a, ch) => a * 31 + ch) & 0x7fffffff;
            return ClassInstruments[h % ClassInstruments.Length] + ", " + Climb[Math.Clamp(count - 1, 0, Climb.Length - 1)];
        }

        // ---- Handlers ----

        private void OnDone(TaskItem t)
        {
            var s = S;
            var now = DateTime.Now;
            int count = CountClass(s, t, now);
            if (Calm.FunPaused) return;
            var m = s.World.GetKept<DayMark>(ConfettiId);
            if (m.Day != s.Key(now)) m = new DayMark { Day = s.Key(now) };
            var (done, total, _, _) = s.Progress(now);
            int pct = Percent(s, now), crossed = Crossed(m.Reached, pct);
            bool finish = total > 0 && done >= total && !m.Finished;
            if (crossed > 0) m.Reached = crossed;
            if (finish) m.Finished = true;
            s.World.SetKept(ConfettiId, m);
            s.World.Save();
            int delay = 700;
            if (finish && Tools.IsOn(FinishId)) { PlanKit.PlayLater(FinishId, delay); delay += 900; }
            else if (crossed > 0 && Tools.IsOn(ConfettiId)) { PlanKit.PlayLater(ConfettiId + crossed, delay); delay += 700; }
            if (count > 0 && Tools.IsOn(ClassId)) PlanKit.PlayLater(ClassTone(t.Tag, count), delay);
        }

        private static bool AnySprint() => FocusTools.SprintRunning || Calm.OtherSprints.Any(f => f()) || Calm.FocusTimers.Any(f => f());

        private void StartTicks(int minutes)
        {
            _ticks?.Stop();
            if (!Tools.IsOn(TickId)) return;
            _sprintEnd = DateTime.UtcNow.AddMinutes(minutes);
            _ticks = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
            _ticks.Tick += (_, _) =>
            {
                double left = (_sprintEnd - DateTime.UtcNow).TotalSeconds;
                // Any kind of sprint counts: focus sprints, and the timers of I am stuck and Smarter body.
                if (left <= 5 || !AnySprint()) { _ticks?.Stop(); return; }
                if (left <= 62 && Tools.IsOn(TickId)) SoundBank.Play(TickId);
            };
            _ticks.Start();
        }

        // ---- Self-test ----

        public void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("auto sounds: " + what); }
            var now = DateTime.Now;
            Need(Crossed(0, 30) == 25 && Crossed(25, 30) == 0 && Crossed(25, 80) == 75 && Crossed(75, 100) == 100 && Crossed(0, 10) == 0, "confetti checkpoints");
            Need(ClassTone("PSY", 1) != ClassTone("PSY", 3) && ClassTone("PSY", 9) == ClassTone("PSY", 5) && ClassTone("PSY", 1) == ClassTone("psy", 1), "class tones do not climb");
            foreach (var id in new[] { SubmitId, FinishId, TickId, ConfettiId + 25, ConfettiId + 100 }) Need(SoundBank.Has(id), id + " has no sound");
            foreach (var n in ClassInstruments.SelectMany(i => Climb.Select(c => i + ", " + c)).Concat(ConfettiSounds).Concat(new[] { "Bunny, binky", "Flute, sparkle", "Soft piano, single" }))
                Need(Sounds.All.Contains(n), "missing built-in sound " + n);
            // Class counts: once per task, back on undo.
            s.Data.Tags.Add(new TagInfo { Name = "TESTCLASS", Description = "A test class" });
            var t = new TaskItem { Name = "class test", Tag = "TESTCLASS", List = s.Data.Lists[0].Name };
            Need(CountClass(s, t, now) == 1 && CountClass(s, t, now) == 0, "a class task counted twice");
            UnClass(s, t, now);
            Need(CountClass(s, t, now) == 1, "undo did not take the class step back");
            Need(CountClass(s, new TaskItem { Name = "x", Tag = "NOTACLASS" }, now) == 0, "a plain tag counted as a class");
            s.Data.Tags.RemoveAll(x => x.Name == "TESTCLASS");
            foreach (var id in new[] { ConfettiId, ClassId, SubmitId, FinishId, TickId }) Need(Tools.Find(id) != null, id + " is not in the toolbox");
        }
    }
}
