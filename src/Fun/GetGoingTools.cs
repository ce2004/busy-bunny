using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>
    /// The Get going drawer (sweep 8.4 and 13.3 item 17): morning dice roll, focus music, power hour, momentum mode, claim a
    /// task, ten-minute tidy, hard thing first, two-task warmup, batch mode, micro-goals, polish timer, dance break, and
    /// sprint length by energy (automatic). Doubled points are paid as a bonus on the task, so un-finishing takes them back.
    /// </summary>
    public sealed class GetGoingTools : IFeature
    {
        private const string Dice = "go.dice", Sound = "go.sound", Power = "go.power", Momentum = "go.momentum", Claim = "go.claim", Tidy = "go.tidy",
            Hard = "go.hard", Warmup = "go.warmup", Batch = "go.batch", Micro = "go.micro", Polish = "go.polish", Dance = "go.dance", SprintLen = "go.sprintlen";

        public sealed class DiceRoll { public string Day { get; set; } = ""; public string List { get; set; } = ""; }
        public sealed class PowerHour { public int Hour { get; set; } = -1; public string Started { get; set; } = ""; public string Ended { get; set; } = ""; }
        public sealed class Music { public bool On { get; set; } = true; public int Volume { get; set; } = 10; }
        public sealed class Claimed { public Guid? Id { get; set; } }
        public sealed class DayTask { public string Day { get; set; } = ""; public Guid? Task { get; set; } }
        public sealed class WarmupPlan { public string Day { get; set; } = ""; public Guid? Big { get; set; } public List<Guid> Warm { get; set; } = new(); }
        public sealed class MicroGoal { public string Goal { get; set; } = ""; public bool Waiting { get; set; } }
        public sealed class Polished { public string Day { get; set; } = ""; public HashSet<Guid> Tasks { get; set; } = new(); }
        public sealed class Best { public int Count { get; set; } }
        private sealed class Offered { public string Day { get; set; } = ""; }

        private MainWindow _w = null!;
        private Store S => _w.Store;
        private readonly List<(Guid Id, DateTime When)> _run = new();
        private readonly DispatcherTimer _musicClock = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private readonly DispatcherTimer _beat = new() { Interval = TimeSpan.FromMilliseconds(330) };
        private bool _musicMine;
        private int _beatStep;
        private static bool _classHooked;

        public void Attach(MainWindow w)
        {
            _w = w;
            FunTimer.Hook();
            const string since = "0.14.0";
            Tools.Register(new BunnyTool(Dice, "Morning dice roll", Drawers.GetGoing, "Each morning the dice pick one list; its tasks earn double today.", DiceTool) { Since = since });
            Tools.Register(new BunnyTool(Sound, "Focus music", Drawers.GetGoing, "Busy Bunny's soft music on real instruments plays all through a sprint, with its own volume. No rain or campfire recordings yet.", MusicTool)
                { Since = since, State = () => S.World.GetKept<Music>(Sound).On ? "on" : "off" });
            Tools.Register(new BunnyTool(Power, "Power hour", Drawers.GetGoing, "Pick one hour a day when points double, with a soft sound at the start and end.", PowerTool) { Since = since });
            Tools.Register(new BunnyTool(Momentum, "Momentum mode", Drawers.GetGoing, "Automatic: after three tasks in a row, offers one more quick win, once a day.",
                () => _w.Say("Momentum mode is on. After three tasks in a row, I offer exactly one more quick win, once a day. Settings, Fun and tools turns it off.", true)) { Since = since });
            Tools.Register(new BunnyTool(Claim, "Claim a task", Drawers.GetGoing, "Say I am doing this now: it stays at the top of Today until it is done or unclaimed.", ClaimTool) { Since = since });
            Tools.Register(new BunnyTool(Tidy, "Ten-minute tidy", Drawers.GetGoing, "Ten minutes for clearing small tasks; counts how many you cleared.", TidyTool) { Since = since });
            Tools.Register(new BunnyTool(Hard, "Hard thing first", Drawers.GetGoing, "Offers your hardest task first, for bonus points; more in the morning.", HardTool) { Since = since });
            Tools.Register(new BunnyTool(Warmup, "Two-task warmup", Drawers.GetGoing, "Two tiny tasks to warm up before a big one.", WarmupTool) { Since = since });
            Tools.Register(new BunnyTool(Batch, "Batch mode", Drawers.GetGoing, "Groups similar small tasks, like emails, into one sprint.", BatchTool) { Since = since });
            Tools.Register(new BunnyTool(Micro, "Micro-goal for a sprint", Drawers.GetGoing, "Set one small goal for your next sprint, like read three pages.", MicroTool) { Since = since });
            Tools.Register(new BunnyTool(Polish, "Polish timer", Drawers.GetGoing, "For a task that is basically done: a short timer that caps the polishing.", PolishTool) { Since = since });
            Tools.Register(new BunnyTool(Dance, "Dance break", Drawers.GetGoing, "Two minutes of dancing to a beat on real instruments, then back to it.", DanceTool) { Since = since });
            Tools.Register(new BunnyTool(SprintLen, "Sprint length by energy", Drawers.GetGoing, "Automatic: sprints start at 10 minutes on low energy and 25 on high.",
                () => _w.Say("Sprint length by energy is on. With low energy a sprint starts at 10 minutes, with high energy at 25, otherwise 15. Today: " + FunKit.SprintLength(FunKit.Energy(S, _w.AppSettings, DateTime.Now)) + " minutes.", true)) { Since = since });

            SoundBank.Register("go.power.start", "Singing bells, rising");
            SoundBank.Register("go.power.end", "Singing bells, falling");

            GameEvents.DayStarted += day => { if (Tools.IsOn(Dice) && !Calm.FunPaused && RollIfNew(S, DateTime.Now, FunKit.Rng) is string list) Calm.SayUnprompted(_w, DiceLine(list)); };
            GameEvents.TaskDone += OnDone;
            GameEvents.TaskUndone += OnUndone;
            GameEvents.SprintDone += _ => MicroAfterSprint();
            Ticker.Every(Power, _ => true, PowerTick);

            // Claim a task: first among the tasks that are not urgent, in the summary line, and on the task's own field.
            w.Store.SummaryParts.Add((now, shortMode) => ClaimedTask(S) is TaskItem t && S.Counts(t) && Tools.IsOn(Claim) ? (shortMode ? "now: " : "doing now: ") + t.Name : null);
            w.DetailNotes.Add(t => S.World.Get<Claimed>(Claim).Id == t.Id && Tools.IsOn(Claim) ? "You claimed this one: doing it now." : null);
            w.Started += () =>
            {
                // Last of the reorder rules, so shuffle mode never moves a claimed task down.
                w.Store.ReorderRules.Remove(PinClaimed);
                w.Store.ReorderRules.Add(PinClaimed);
            };
            w.Store.ReorderRules.Add(PinClaimed);

            _musicClock.Tick += (_, _) => MusicTick();
            _musicClock.Start();
            _beat.Tick += (_, _) => Beat();
            w.Closed += (_, _) => { _musicClock.Stop(); _beat.Stop(); };

            if (!_classHooked)
            {
                _classHooked = true;
                // The sprint window (FocusTools) starts at 15 minutes; on a low or high energy day it starts at 10 or 25.
                EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
                {
                    if (s is not Window win || win.Title != "Start a sprint" || !Tools.IsOn(SprintLen) || Engines.Store is not Store st) return;
                    int len = FunKit.SprintLength(FunKit.Energy(st, Engines.Settings, DateTime.Now));
                    var minutes = Descendants(win).OfType<ComboBox>().FirstOrDefault(c => c.Items.Count > 0 && c.Items[0] is string first && first.EndsWith(" minutes"));
                    if (minutes == null) return;
                    int at = minutes.Items.IndexOf(len + " minutes");
                    if (at >= 0) minutes.SelectedIndex = at;
                }));
            }
        }

        private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
        {
            foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
            {
                yield return child;
                foreach (var d in Descendants(child)) yield return d;
            }
        }

        // ---- Finishing tasks: doubled points, momentum, claims, hard thing first, warmups, tidy counts ----

        private void OnDone(TaskItem t)
        {
            var now = DateTime.Now;
            if (FunTimer.Current is FunTimer timer && timer.Id is Tidy or Batch) timer.Count++;
            if (S.World.Get<Claimed>(Claim).Id == t.Id) S.World.Set(Claim, new Claimed());

            // Doubled points: the dice roll's list, and power hour. Paid on the task, so un-finishing takes them back.
            bool dice = Tools.IsOn(Dice) && DiceApplies(S.World.GetKept<DiceRoll>(Dice), t, now);
            bool power = Tools.IsOn(Power) && Ledger.Active(S, now).Any(m => m.Id == Power);
            int bonus = DoubleBonus(Engine.PointsFor(t, S.ListOf(t), half: t.Status == Status.DoneNotSubmitted), dice, power);
            if (bonus > 0)
                Ledger.Pay(_w, bonus, dice && power ? "Power hour and the dice roll both double this one" : dice ? "The dice roll doubles " + t.List + " today" : "Power hour doubles it", dice ? Dice : Power, t.Id);

            var hard = S.World.Get<DayTask>(Hard);
            if (Tools.IsOn(Hard) && hard.Day == S.Key(now) && hard.Task == t.Id)
            {
                int pts = HardBonus(now);
                if (Ledger.Pay(S, pts, "Hard thing first", Hard, t.Id) is Outcome o) Game.Queue(_w, o);
                S.World.Set(Hard, new DayTask());
                Calm.AfterTaskDone(_w, Game.Other, "Hard thing first, and done. " + pts + " bonus points. Everything else is downhill from here.");
            }

            var plan = S.World.Get<WarmupPlan>(Warmup);
            if (Tools.IsOn(Warmup) && plan.Day == S.Key(now) && plan.Warm.Contains(t.Id) && plan.Warm.All(id => FunKit.Find(S, id) is not TaskItem x || !FunKit.Open(x)))
            {
                S.World.Set(Warmup, new WarmupPlan());
                if (FunKit.Find(S, plan.Big) is TaskItem big && FunKit.Open(big))
                    Calm.AfterTaskDone(_w, Game.Other, "Warmed up. Now the big one: " + big.Name + ".");
            }
            S.World.Save();

            if (!Tools.IsOn(Momentum) || Calm.FunPaused) return;
            if (Step(_run, t.Id, now) < 3) return;
            var offered = S.World.GetKept<Offered>(Momentum);
            if (offered.Day == S.Key(now)) return;
            var win = S.Data.Tasks.FirstOrDefault(x => x.Id != t.Id && FocusLogic.IsQuickWin(x) && FunKit.Open(x) && S.CanShowToday(x, now) && !S.IsArchived(x));
            if (win == null) return;
            S.World.SetKept(Momentum, new Offered { Day = S.Key(now) });
            S.World.Save();
            Calm.AfterTaskDone(_w, Game.Other, Content.Pick(S, "go.momentum").Replace("{task}", win.Name));
        }

        private void OnUndone(TaskItem t)
        {
            if (FunTimer.Current is FunTimer timer && timer.Id is Tidy or Batch && timer.Count > 0) timer.Count--;
            _run.RemoveAll(r => r.Id == t.Id);
        }

        /// <summary>Tasks finished within 30 minutes of each other: how many in a row now, this one included.</summary>
        public static int Step(List<(Guid Id, DateTime When)> run, Guid id, DateTime now)
        {
            if (run.Count > 0 && now - run[^1].When > TimeSpan.FromMinutes(30)) run.Clear();
            run.RemoveAll(r => r.Id == id);
            run.Add((id, now));
            return run.Count;
        }

        /// <summary>The extra points for doubling: base times the factor minus base, the factor capped at 4.</summary>
        public static int DoubleBonus(int basePoints, bool dice, bool power)
        {
            double f = Math.Min(Ledger.MaxBoost, (dice ? 2 : 1) * (power ? 2 : 1));
            return (int)Math.Round(basePoints * (f - 1));
        }

        public static int HardBonus(DateTime now) => now.Hour < 12 ? 10 : 5;

        // ---- Morning dice roll ----

        public static bool DiceApplies(DiceRoll roll, TaskItem t, DateTime now) => roll.Day == now.ToString("yyyy-MM-dd") && roll.List.Length > 0 && roll.List == t.List;

        /// <summary>Rolls today's list once a day; returns it when it was rolled just now, null when today already had a roll.</summary>
        public static string? RollIfNew(Store s, DateTime now, Random rng)
        {
            var roll = s.World.GetKept<DiceRoll>(Dice);
            if (roll.Day == s.Key(now)) return null;
            var lists = FunKit.TodayLists(s);
            var withWork = lists.Where(l => s.Data.Tasks.Any(t => t.List == l.Name && FunKit.Open(t))).ToList();
            var pool = withWork.Count > 0 ? withWork : lists;
            if (pool.Count == 0) return null;
            roll = new DiceRoll { Day = s.Key(now), List = pool[rng.Next(pool.Count)].Name };
            s.World.SetKept(Dice, roll);
            s.World.Save();
            return roll.List;
        }

        private string DiceLine(string list) => Content.Pick(S, "go.dice").Replace("{list}", list);

        private void DiceTool()
        {
            var now = DateTime.Now;
            if (RollIfNew(S, now, FunKit.Rng) is string fresh) { _w.Say(DiceLine(fresh), true); return; }
            var roll = S.World.GetKept<DiceRoll>(Dice);
            _w.Say(roll.List.Length > 0 ? "Today's dice roll: " + roll.List + ". Its tasks earn double until midnight. A new roll comes tomorrow morning." : "There is no list to roll for yet.", true);
        }

        // ---- Focus music ----

        private void MusicTool()
        {
            var m = S.World.GetKept<Music>(Sound);
            var options = new List<string> { m.On ? "Turn focus music off" : "Turn focus music on" };
            int[] levels = { 5, 10, 15, 20, 30 };
            options.AddRange(levels.Select(v => "Volume " + v + " percent" + (v == m.Volume ? ", now" : "")));
            int i = FunKit.Pick(_w, "Focus music", "Focus music is " + (m.On ? "on" : "off") + ", at " + m.Volume + " percent. It is Busy Bunny's soft music on real instruments, played all through a sprint, even while you work in another program.", options);
            if (i < 0) return;
            if (i == 0) m.On = !m.On; else { m.Volume = levels[i - 1]; m.On = true; }
            S.World.SetKept(Sound, m);
            S.World.Save();
            _w.Say("Focus music is " + (m.On ? "on now, at " + m.Volume + " percent. It plays while a sprint runs." : "off now.") + (_w.AppSettings.SoundsOn ? "" : " Sounds are off in Settings, so it stays quiet until they are on."), true);
        }

        private void MusicTick()
        {
            bool sprint = FocusTools.SprintRunning || FunTimer.Current?.Sprint == true;
            if (!sprint && !_musicMine) return;
            var m = S.World.GetKept<Music>(Sound);
            bool want = sprint && m.On && Tools.IsOn(Sound) && !Calm.FunPaused && _w.AppSettings.SoundsOn;
            if (want)
            {
                Mixer.Music.Level = Math.Clamp(m.Volume, 1, 50) / 100f;
                if (!Mixer.Music.Wanted) { Mixer.Music.Wanted = true; Mixer.Wake(); }
                _musicMine = true;
            }
            else if (_musicMine)
            {
                _musicMine = false;
                UiSounds.UpdateMusic();
            }
        }

        // ---- Power hour ----

        private void PowerTool()
        {
            var now = DateTime.Now;
            var p = S.World.GetKept<PowerHour>(Power);
            if (Ledger.Active(S, now).FirstOrDefault(m => m.Id == Power) is Multiplier run)
            {
                _w.Say("Power hour is on: " + FunKit.Plural((int)Math.Ceiling((run.Until - now).TotalMinutes), "minute", "minutes") + " left, and points double.", true);
                return;
            }
            var hours = Enumerable.Range(7, 16).ToList();
            var options = new List<string> { "No power hour" };
            options.AddRange(hours.Select(h => HourWords(h) + " to " + HourWords(h + 1)));
            int at = p.Hour < 0 ? 0 : Math.Max(0, hours.IndexOf(p.Hour) + 1);
            int i = FunKit.Pick(_w, "Power hour", "Pick one hour a day when points double. " + (p.Hour < 0 ? "None is set." : "Now " + HourWords(p.Hour) + "."), options, at);
            if (i < 0) return;
            p.Hour = i == 0 ? -1 : hours[i - 1];
            S.World.SetKept(Power, p);
            S.World.Save();
            if (p.Hour < 0) { _w.Say("No power hour now.", true); return; }
            _w.Say("Power hour is " + HourWords(p.Hour) + " to " + HourWords(p.Hour + 1) + " every day. Points double then, with a soft sound at the start and the end.", true);
            PowerTick(now);
        }

        private static string HourWords(int h) => (h % 12 == 0 ? 12 : h % 12) + (h < 12 || h == 24 ? " am" : " pm");

        private void PowerTick(DateTime now)
        {
            var p = S.World.GetKept<PowerHour>(Power);
            if (p.Hour < 0 || !Tools.IsOn(Power)) return;
            string today = S.Key(now);
            if (PowerStarts(p, now))
            {
                p.Started = today;
                S.World.SetKept(Power, p);
                Ledger.AddMultiplier(S, Power, 2, now.Date.AddHours(p.Hour + 1), "Power hour");
                if (!Calm.FunPaused) SoundBank.Play("go.power.start");
                Calm.SayUnprompted(_w, Content.Pick(S, "go.power") + " Points double until " + HourWords(p.Hour + 1) + ".");
            }
            else if (p.Started == today && p.Ended != today && now >= now.Date.AddHours(p.Hour + 1))
            {
                p.Ended = today;
                S.World.SetKept(Power, p);
                S.World.Save();
                if (!Calm.FunPaused) SoundBank.Play("go.power.end");
                Game.Note(S, "Power hour is over for today.");
            }
        }

        /// <summary>Whether power hour should start now: inside her hour and not started yet today.</summary>
        public static bool PowerStarts(PowerHour p, DateTime now) => p.Hour >= 0 && now.Hour == p.Hour && p.Started != now.ToString("yyyy-MM-dd");

        // ---- Claim a task ----

        public static TaskItem? ClaimedTask(Store s) => FunKit.Find(s, s.World.Get<Claimed>(Claim).Id) is TaskItem t && FunKit.Open(t) ? t : null;

        /// <summary>The claimed task first. Store.ReorderRules orders the tasks after the urgent ones; an urgent claim is already at the top.</summary>
        private List<TaskItem> PinClaimed(List<TaskItem> list, DateTime now)
        {
            if (!Tools.IsOn(Claim) || ClaimedTask(S) is not TaskItem t || !list.Contains(t)) return list;
            return list.Where(x => x == t).Concat(list.Where(x => x != t)).ToList();
        }

        private void ClaimTool()
        {
            var now = DateTime.Now;
            if (ClaimedTask(S) is TaskItem cur)
            {
                int i = FunKit.Pick(_w, "Claim a task", "You claimed " + cur.Name + ".", new[] { "Keep it claimed", "Unclaim it", "Claim a different task" });
                if (i <= 0) return;
                if (i == 1)
                {
                    World.Change<Claimed>(S, Claim, "unclaiming " + cur.Name, c => c.Id = null);
                    _w.Refresh();
                    _w.Say(cur.Name + " is not claimed any more.", true);
                    return;
                }
            }
            var pool = FunKit.Candidates(S, now);
            if (_w.ChosenTask is TaskItem c0 && FunKit.Open(c0) && !pool.Contains(c0)) pool.Insert(0, c0);
            var t = FunKit.PickTask(_w, "Claim a task", "Which task are you doing now?", pool, _w.ChosenTask);
            if (t == null) { if (pool.Count == 0) _w.Say("There is no open task to claim.", true); return; }
            if (!SkillsTools.BeforeTask(_w)) return;
            World.Change<Claimed>(S, Claim, "claiming " + t.Name, c => c.Id = t.Id);
            _w.Refresh();
            bool onToday = S.TodayAll(now).Contains(t);
            _w.SelectTask(t);
            _w.Say("Claimed: " + t.Name + ". " + (onToday ? "It stays at the top of what you are doing until it is done or unclaimed." : "It is not planned for today, but the summary line says you are on it.") + " Control Z undoes it.", true);
        }

        // ---- Ten-minute tidy and batch mode ----

        private void TidyTool()
        {
            if (FunTimer.Handle(_w, Tidy)) return;
            var now = DateTime.Now;
            int small = S.TodayAll(now).Count(t => FunKit.Open(t) && (t.Size == Size.Small || FocusLogic.IsQuickWin(t)));
            FunTimer.Start(_w, Tidy, "Ten-minute tidy", 10, null, true, (t, _) =>
            {
                var best = S.World.GetKept<Best>(Tidy);
                bool record = t.Count > best.Count && best.Count > 0;
                if (t.Count > best.Count) { S.World.SetKept(Tidy, new Best { Count = t.Count }); S.World.Save(); }
                string line = t.Count == 0 ? "Tidy time is up. Nothing got cleared, and that is fine: some things take longer than they look."
                    : "Tidy time is up. You cleared " + FunKit.Plural(t.Count, "small thing", "small things") + "." + (record ? " That is your best tidy yet." : "") + " " + Content.Pick(S, "go.tidy");
                _w.Say(line);
            });
            _w.Say("Ten-minute tidy: clear as many small tasks as you can. " + (small > 0 ? FunKit.Plural(small, "small task is", "small tasks are") + " on Today." : "Quick wins, Control Shift Q, has tiny ones.") + " I will count them.", true);
        }

        /// <summary>Similar small open tasks, grouped by class tag, task type or a word like email: groups of two or more.</summary>
        public static List<(string Label, List<TaskItem> Tasks)> Groups(Store s, DateTime now)
        {
            var small = s.Data.Tasks.Where(t => FunKit.Open(t) && (t.Size == Size.Small || FocusLogic.IsQuickWin(t)) && s.CanShowToday(t, now) && !s.IsArchived(t)).ToList();
            var groups = new List<(string, List<TaskItem>)>();
            string[] words = { "email", "call", "read", "form", "pay", "buy", "text", "print", "submit", "schedule", "clean", "reply" };
            foreach (var w in words)
            {
                var g = small.Where(t => t.Name.Contains(w, StringComparison.OrdinalIgnoreCase)).ToList();
                if (g.Count >= 2) groups.Add((char.ToUpper(w[0]) + w[1..] + " tasks", g));
            }
            foreach (var g in small.Where(t => t.Tag.Length > 0).GroupBy(t => t.Tag).Where(g => g.Count() >= 2)) groups.Add((g.Key + " tasks", g.ToList()));
            foreach (var g in small.Where(t => t.Type != TaskType.General).GroupBy(t => t.Type).Where(g => g.Count() >= 2)) groups.Add((g.Key + " tasks", g.ToList()));
            return groups.GroupBy(g => g.Item1).Select(g => g.First()).Take(12).ToList();
        }

        private void BatchTool()
        {
            if (FunTimer.Handle(_w, Batch)) return;
            var now = DateTime.Now;
            var groups = Groups(S, now);
            if (groups.Count == 0) { _w.Say("No batch yet: batch mode needs two or more small tasks that go together, like two emails or two tasks with the same class tag.", true); return; }
            int i = FunKit.Pick(_w, "Batch mode", "Which batch? It becomes one sprint.", groups.Select(g => g.Label + ", " + g.Tasks.Count + ": " + string.Join(", ", g.Tasks.Take(4).Select(t => t.Name))).ToList());
            if (i < 0) return;
            var (label, tasks) = groups[i];
            int minutes = Math.Clamp(5 * tasks.Count, 10, 25);
            FunTimer.Start(_w, Batch, "Batch sprint, " + label, minutes, null, true, (t, counted) =>
                _w.Say("Batch sprint over. You cleared " + t.Count + " of " + tasks.Count + " " + label.ToLowerInvariant() + ", in " + FunKit.Plural(counted, "minute", "minutes") + ". Batching counts."));
            _w.SelectTask(tasks[0]);
            _w.Say("Batch sprint: " + minutes + " minutes for " + tasks.Count + " " + label.ToLowerInvariant() + ". Start with " + tasks[0].Name + ".", true);
        }

        // ---- Hard thing first and two-task warmup ----

        /// <summary>How hard a task looks: its size, brain-heavy energy and top priority.</summary>
        public static int Hardness(TaskItem t) =>
            (t.Size switch { Size.Small => 1, Size.Medium => 2, Size.Large => 3, _ => 4 }) + (t.Energy == Energy.BrainHeavy ? 2 : 0) + (t.Priority == 1 ? 1 : 0);

        private void HardTool()
        {
            var now = DateTime.Now;
            var pool = FunKit.Candidates(S, now).Where(t => t.Status != Status.DoneNotSubmitted).OrderByDescending(Hardness).Take(3).ToList();
            if (pool.Count == 0) { _w.Say("Nothing hard on the list right now. Lucky you.", true); return; }
            int pts = HardBonus(now);
            var t = FunKit.PickTask(_w, "Hard thing first", "Your hardest task first, for " + pts + " bonus points" + (now.Hour < 12 ? " this morning." : " today. Mornings pay 10.") + " The first one is the hardest.", pool);
            if (t == null || !SkillsTools.BeforeTask(_w)) return;
            World.Change<DayTask>(S, Hard, "choosing " + t.Name + " as the hard thing first", d => { d.Day = S.Key(now); d.Task = t.Id; });
            _w.SelectTask(t);
            _w.Say("Hard thing first: " + t.Name + ". Finish it today for " + pts + " bonus points. The rest will feel easy after this.", true);
        }

        private void WarmupTool()
        {
            var now = DateTime.Now;
            var pool = FunKit.Candidates(S, now);
            var bigs = pool.Where(t => t.Size is Size.Large or Size.MultiDay || t.Energy == Energy.BrainHeavy).ToList();
            if (_w.ChosenTask is TaskItem c && FunKit.Open(c) && !bigs.Contains(c)) bigs.Insert(0, c);
            if (bigs.Count == 0) { _w.Say("No big task to warm up for. Everything open looks small already.", true); return; }
            var big = FunKit.PickTask(_w, "Two-task warmup", "Which big task are you warming up for?", bigs, _w.ChosenTask);
            if (big == null) return;
            var warm = S.Data.Tasks.Where(t => t != big && FunKit.Open(t) && FocusLogic.IsQuickWin(t) && S.CanShowToday(t, now) && !S.IsArchived(t))
                .Concat(pool.Where(t => t != big && t.Size == Size.Small)).Distinct().Take(2).ToList();
            if (warm.Count < 2) { _w.Say("There are not two tiny tasks to warm up with. Mark a couple as quick wins with Task, Quick win, and try again.", true); return; }
            World.Change<WarmupPlan>(S, Warmup, "planning a warmup", p => { p.Day = S.Key(now); p.Big = big.Id; p.Warm = warm.Select(x => x.Id).ToList(); });
            _w.SelectTask(warm[0]);
            _w.Say("Warmup: first " + warm[0].Name + ", then " + warm[1].Name + ". Then " + big.Name + ". I will say when you are warmed up.", true);
        }

        // ---- Micro-goals ----

        private void MicroTool()
        {
            var m = S.World.GetKept<MicroGoal>(Micro);
            if (m.Waiting && m.Goal.Length > 0)
            {
                int i = FunKit.Pick(_w, "Micro-goal", "Your goal for that sprint was: " + m.Goal + ". Did you reach it?", new[] { "Yes, I reached it", "Not quite", "Set a new goal" });
                if (i < 0) return;
                S.World.SetKept(Micro, new MicroGoal());
                S.World.Save();
                if (i == 0) { FunKit.Win(_w, Micro, 3, "Micro-goal reached", 4, "Goal reached: " + m.Goal + "."); return; }
                if (i == 1) { _w.Say("That is okay. Part of a goal is still a start, and the sprint counted.", true); return; }
            }
            string? goal = FunKit.Ask(_w, "Micro-goal", "_Small goal for the next sprint", m.Waiting ? "" : m.Goal, "Something small you can finish in one sprint, like read three pages.");
            if (goal == null) return;
            S.World.SetKept(Micro, new MicroGoal { Goal = goal });
            S.World.Save();
            bool running = FocusTools.SprintRunning || FunTimer.Current?.Sprint == true;
            _w.Say("Micro-goal set: " + goal + ". " + (running ? "It counts for the sprint running now." : "Start a sprint with Control Shift T.") + " Afterwards, open this again to say whether you reached it.", true);
        }

        private void MicroAfterSprint()
        {
            var m = S.World.GetKept<MicroGoal>(Micro);
            if (m.Goal.Length == 0 || m.Waiting || !Tools.IsOn(Micro)) return;
            m.Waiting = true;
            S.World.SetKept(Micro, m);
            S.World.Save();
            _w.Say("Your micro-goal was: " + m.Goal + ". Micro-goal for a sprint, in the toolbox, marks it reached.");
        }

        // ---- Polish timer ----

        private void PolishTool()
        {
            if (FunTimer.Handle(_w, Polish)) return;
            var now = DateTime.Now;
            var pool = S.Data.Tasks.Where(t => t.Status == Status.DoneNotSubmitted && !S.IsArchived(t)).Concat(FunKit.Candidates(S, now)).Distinct().ToList();
            if (_w.ChosenTask is TaskItem c && c.Status != Status.Done && !pool.Contains(c)) pool.Insert(0, c);
            var t = FunKit.PickTask(_w, "Polish timer", "Which task is basically done?", pool, _w.ChosenTask);
            if (t == null) { if (pool.Count == 0) _w.Say("There is nothing open to polish.", true); return; }
            var done = S.World.GetKept<Polished>(Polish);
            if (done.Day != S.Key(now)) done = new Polished { Day = S.Key(now) };
            // One polish a task a day, so the timer never turns into checking it again and again.
            if (done.Tasks.Contains(t.Id)) { _w.Say("You already polished " + t.Name + " today. It is ready. Done is done.", true); return; }
            int[] lengths = { 5, 10, 15 };
            int i = FunKit.Pick(_w, "Polish timer", "How long may you polish " + t.Name + "? When the time is up, it is done.", lengths.Select(m => m + " minutes").ToList(), 1);
            if (i < 0) return;
            done.Tasks.Add(t.Id);
            S.World.SetKept(Polish, done);
            S.World.Save();
            FunTimer.Start(_w, Polish, "Polish timer", lengths[i], t, true, (timer, _) => _w.Say(Content.Pick(S, "go.polish").Replace("{task}", timer.Task?.Name ?? "it")));
            _w.Say("Polish timer: " + lengths[i] + " minutes for " + t.Name + ". When it ends, so does the polishing.", true);
        }

        // ---- Dance break ----

        private void DanceTool()
        {
            if (FunTimer.Handle(_w, Dance)) return;
            GameEvents.RaiseBreakStarted();
            _beatStep = 0;
            FunTimer.Start(_w, Dance, "Dance break", 2, null, false, (_, _) =>
            {
                _beat.Stop();
                GameEvents.RaiseBreakEnded();
                _w.Say(Content.Pick(S, "go.dance"));
            });
            if (Sounds.Enabled) _beat.Start();
            _w.Say("Dance break: two minutes. Move however feels good, sitting or standing. Open it again to stop early.", true);
        }

        // A light groove on recorded instruments: low pizzicato on the beat, xylophone between, a cello on the one.
        private void Beat()
        {
            if (FunTimer.Current?.Id != Dance || !Sounds.Enabled) { _beat.Stop(); return; }
            int s = _beatStep++ % 16;
            if (s % 4 == 0) Sounds.PlayScaleNote(s == 0 ? "Cello" : "Pizzicato strings", s == 8 ? 4 : 0, 0.55);
            else if (s % 4 == 2) Sounds.PlayScaleNote("Pizzicato strings", s == 6 || s == 14 ? 4 : 2, 0.45);
            else if (s == 3 || s == 11) Sounds.PlayScaleNote("Xylophone", s == 3 ? 4 : 7, 0.3);
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("get going: " + what); }
            var now = new DateTime(2026, 11, 4, 9, 30, 0);
            Need(DoubleBonus(10, true, false) == 10 && DoubleBonus(10, true, true) == 30 && DoubleBonus(10, false, false) == 0, "doubled points");
            var list = store.Data.Lists[0].Name;
            var t = new TaskItem { Name = "dice test", List = list };
            Need(DiceApplies(new DiceRoll { Day = "2026-11-04", List = list }, t, now) && !DiceApplies(new DiceRoll { Day = "2026-11-03", List = list }, t, now), "dice roll day");
            string? rolled = RollIfNew(store, now, new Random(3));
            Need(rolled != null && RollIfNew(store, now, new Random(4)) == null && store.World.GetKept<DiceRoll>(Dice).List == rolled, "one dice roll a day");
            Need(RollIfNew(store, now.AddDays(1), new Random(3)) != null, "a new roll the next day");
            var p = new PowerHour { Hour = 9 };
            Need(PowerStarts(p, now) && !PowerStarts(p, now.AddHours(1)), "power hour start");
            p.Started = "2026-11-04";
            Need(!PowerStarts(p, now), "power hour started twice in a day");
            var run = new List<(Guid, DateTime)>();
            var a = Guid.NewGuid();
            Step(run, a, now); Step(run, Guid.NewGuid(), now.AddMinutes(10));
            Need(Step(run, Guid.NewGuid(), now.AddMinutes(20)) == 3, "three in a row");
            Need(Step(run, Guid.NewGuid(), now.AddMinutes(60)) == 1, "a long gap starts a new run");
            Need(Step(run, a, now.AddMinutes(61)) == 2 && Step(run, a, now.AddMinutes(62)) == 2, "the same task twice counted twice");
            Need(HardBonus(now) == 10 && HardBonus(now.AddHours(5)) == 5, "hard thing first bonus");
            Need(Hardness(new TaskItem { Size = Size.Large, Energy = Energy.BrainHeavy }) > Hardness(new TaskItem { Size = Size.Medium }), "hardness");

            // Claiming is one Control Z step, and the claimed task goes first.
            store.Data.Tasks.Add(t);
            World.Change<Claimed>(store, Claim, "claiming", c => c.Id = t.Id);
            Need(ClaimedTask(store) == t, "claim");
            store.Undo();
            Need(ClaimedTask(store) == null, "Control Z did not undo a claim");
            var b1 = new TaskItem { Name = "email Sam", Size = Size.Small, List = list, Status = Status.ThisWeek };
            var b2 = new TaskItem { Name = "email the TA", Size = Size.Small, List = list, Status = Status.ThisWeek };
            store.Data.Tasks.AddRange(new[] { b1, b2 });
            Need(Groups(store, DateTime.Now).Any(g => g.Label == "Email tasks" && g.Tasks.Count == 2), "batch groups");
            store.Data.Tasks.RemoveAll(x => x == b1 || x == b2 || x.Name == "dice test");
            foreach (var topic in new[] { "go.dice", "go.momentum", "go.power", "go.tidy", "go.polish", "go.dance" })
                Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            Need(Content.Lines("go.dice").All(l => l.Contains("{list}")) && Content.Lines("go.momentum").All(l => l.Contains("{task}")), "a line without its blank");
            foreach (var id in new[] { Dice, Sound, Power, Momentum, Claim, Tidy, Hard, Warmup, Batch, Micro, Polish, Dance, SprintLen })
                Need(Tools.Find(id) != null, id + " is not in the toolbox");
        }
    }
}
