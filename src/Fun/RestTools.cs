using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// The Rest and rough days drawer (sweep 8.4 and 13.3 item 7): surprise half day, clean slate, the not today list,
    /// mood boost mini tasks, earned free time and spa day. No guilt text anywhere, every change is one Control Z step,
    /// and nothing here ever deletes a task or makes one overdue.
    /// </summary>
    public sealed class RestTools : IFeature
    {
        private const string HalfDay = "rest.halfday", Slate = "rest.slate", NotToday = "rest.nottoday", Boost = "rest.boost", FreeTime = "rest.freetime", Spa = "rest.spa";
        public sealed class HalfDayKept { public string Month { get; set; } = ""; public string OfferedDay { get; set; } = ""; }
        public sealed class HalfDayBag { public string Day { get; set; } = ""; }
        public sealed class NotTodayBag { public string Day { get; set; } = ""; public HashSet<Guid> Tasks { get; set; } = new(); }
        public sealed class FreeBag { public string Day { get; set; } = ""; public int Spent { get; set; } }
        /// <summary>Minutes of free time for each effort point finished (a small task is 1 point, medium 2, large 4).</summary>
        public const int MinutesPerPoint = 10;

        private MainWindow _w = null!;
        private Store S => _w.Store;

        public void Attach(MainWindow w)
        {
            _w = w;
            FunTimer.Hook();
            const string since = "0.14.0";
            Tools.Register(new BunnyTool(HalfDay, "Surprise half day", Drawers.Rest, "Rarely, on a light day, Bunny offers you the afternoon off. You can say no.", HalfDayTool) { Since = since });
            Tools.Register(new BunnyTool(Slate, "Clean slate", Drawers.Rest, "Moves every overdue task to the end of this week or a date you pick, in one step.", CleanSlateTool) { Since = since });
            Tools.Register(new BunnyTool(NotToday, "Not today list", Drawers.Rest, "Set tasks aside for today on purpose. They come back tomorrow.", NotTodayTool) { Since = since });
            Tools.Register(new BunnyTool(Boost, "Mood boost mini tasks", Drawers.Rest, "Tiny pleasant things to do in a low moment.", BoostTool) { Since = since });
            Tools.Register(new BunnyTool(FreeTime, "Earned free time", Drawers.Rest, "Your finished work adds up to free time off, ten minutes for each effort point.", FreeTimeTool) { Since = since });
            Tools.Register(new BunnyTool(Spa, "Spa day", Drawers.Rest, "A cozy spa scene with Bunny, best after a rough week.", SpaTool) { Since = since });

            w.Store.HideFromToday.Add((t, now) => Tools.IsOn(NotToday) && HiddenNotToday(S.World.Get<NotTodayBag>(NotToday), t, now));
            w.Store.HideFromToday.Add((t, now) => Tools.IsOn(HalfDay) && AfternoonOff(S.World.Get<HalfDayBag>(HalfDay), now) && !S.DueSoon(t, now) && t.Status != Status.DoneNotSubmitted);
            w.DetailNotes.Add(t => Tools.IsOn(NotToday) && HiddenNotToday(S.World.Get<NotTodayBag>(NotToday), t, DateTime.Now) ? "On your not today list: back tomorrow." : null);
            GameEvents.DayStarted += _ => OfferHalfDay(DateTime.Now);
        }

        // ---- Surprise half day ----

        public static bool AfternoonOff(HalfDayBag b, DateTime now) => b.Day == now.ToString("yyyy-MM-dd") && now.Hour >= 12;

        /// <summary>A light day: nothing overdue, nothing due within a day, and the day looks like Nibble on it or Burrow nap.</summary>
        public static bool Light(Store s, DateTime now) =>
            s.Overdue(now).Count == 0 && !s.TodayAll(now).Any(t => s.DueSoon(t, now)) && s.DayType(now) is "Nibble on it" or "Burrow nap";

        /// <summary>At most one offer a calendar month, in the morning of a light day, and only some light days.</summary>
        public static bool MayOffer(HalfDayKept k, Store s, DateTime now, int roll) =>
            k.Month != now.ToString("yyyy-MM") && now.Hour < 12 && roll == 0 && Light(s, now);

        private void OfferHalfDay(DateTime now)
        {
            if (!Tools.IsOn(HalfDay) || Calm.FunPaused) return;
            var k = S.World.GetKept<HalfDayKept>(HalfDay);
            if (!MayOffer(k, S, now, FunKit.Rng.Next(3))) return;
            k.Month = now.ToString("yyyy-MM");
            k.OfferedDay = S.Key(now);
            S.World.SetKept(HalfDay, k);
            S.World.Save();
            Calm.SayUnprompted(_w, "Surprise: today looks light. Want the afternoon off? Surprise half day, in the Rest and rough days drawer, says yes. Doing nothing is a no, and that is fine too.");
        }

        private void HalfDayTool()
        {
            var now = DateTime.Now;
            var bag = S.World.Get<HalfDayBag>(HalfDay);
            if (bag.Day == S.Key(now))
            {
                int i = FunKit.Pick(_w, "Surprise half day", "Your afternoon is free today. Anything not due soon stays off Today after noon.", new[] { "Keep my afternoon free", "Bring the afternoon back" });
                if (i != 1) return;
                World.Change<HalfDayBag>(S, HalfDay, "bringing the afternoon back", b => b.Day = "");
                _w.Refresh();
                _w.Say("The afternoon is back to normal. Control Z undoes that.", true);
                return;
            }
            var k = S.World.GetKept<HalfDayKept>(HalfDay);
            if (k.OfferedDay != S.Key(now))
            {
                _w.Say("No surprise today. It comes by itself, at most once a month, on a light morning. When it does, this is where you say yes.", true);
                return;
            }
            int yes = FunKit.Pick(_w, "Surprise half day", "Today looks light. Want the afternoon off? Anything not due soon leaves Today at noon.", new[] { "Yes, make the afternoon free", "No thanks" });
            if (yes != 0) { _w.Say("No problem. The offer was just an offer.", true); return; }
            World.Change<HalfDayBag>(S, HalfDay, "taking the afternoon off", b => b.Day = S.Key(now));
            _w.Refresh();
            _w.Say("Your afternoon is free. After noon, Today shows only what is due soon. Control Z undoes it.", true);
        }

        // ---- Clean slate ----

        /// <summary>The overdue tasks a clean slate moves: not meetings, not ones waiting to be submitted.</summary>
        public static List<TaskItem> SlateTasks(Store s, DateTime now) => s.Overdue(now).Where(t => !t.IsMeeting && t.Status != Status.DoneNotSubmitted).ToList();

        /// <summary>Gives every overdue task a new due date (and This week or Next week to match). Call Remember first.</summary>
        public static int CleanSlate(Store s, DateTime now, DateTime to)
        {
            var tasks = SlateTasks(s, now);
            foreach (var t in tasks)
            {
                t.Due = to.Date;
                t.DueHasTime = false;
                if (t.DoDate is DateTime d && d.Date < now.Date) t.DoDate = null;
                t.Status = to.Date <= Store.WeekEnd(now) ? Status.ThisWeek : Status.NextWeek;
                t.StatusSince = now;
                t.TouchedAt = now;
            }
            return tasks.Count;
        }

        /// <summary>Where the clean slate can move tasks: one choice a date (on a Sunday the end of the week, tomorrow and next Monday are one day), then a typed date.</summary>
        public static List<(string Text, DateTime? When)> SlateChoices(DateTime now)
        {
            var end = Store.WeekEnd(now);
            if (end <= now.Date) end = now.Date.AddDays(1);
            var dated = new List<(string Text, DateTime? When)>
            {
                ("The end of this week, " + end.ToString("dddd"), end),
                ("Tomorrow, " + now.Date.AddDays(1).ToString("dddd"), now.Date.AddDays(1)),
                ("Next Monday", Store.WeekEnd(now).AddDays(1)),
            };
            var options = dated.GroupBy(o => o.When!.Value.Date).Select(g => g.First()).ToList();
            options.Add(("A date I type", null));
            return options;
        }

        private void CleanSlateTool()
        {
            var now = DateTime.Now;
            var tasks = SlateTasks(S, now);
            if (tasks.Count == 0) { _w.Say("Nothing is overdue. The slate is already clean.", true); return; }
            var options = SlateChoices(now);
            int i = FunKit.Pick(_w, "Clean slate", FunKit.Plural(tasks.Count, "overdue task", "overdue tasks") + ". Move them all to:", options.Select(o => o.Text).ToList());
            if (i < 0) return;
            DateTime to;
            if (options[i].When is DateTime picked) to = picked;
            else
            {
                string? text = FunKit.Ask(_w, "Clean slate", "_New due date", "", "Like friday, next monday or 11/20.");
                if (text == null) return;
                if (Dates.Parse(text, now).When is not DateTime typed || typed.Date < now.Date) { _w.Say("I could not read that as a date from today on. Nothing moved.", true); return; }
                to = typed;
            }
            S.Remember("the clean slate");
            int n = CleanSlate(S, now, to);
            _w.SaveAndRefresh();
            _w.Say("Clean slate. " + FunKit.Plural(n, "task is", "tasks are") + " now due " + Words.Day(to, now) + ". Control Z puts them back.", true);
        }

        // ---- Not today list ----

        public static bool HiddenNotToday(NotTodayBag b, TaskItem t, DateTime now) => b.Day == now.ToString("yyyy-MM-dd") && b.Tasks.Contains(t.Id);

        private void NotTodayTool()
        {
            var now = DateTime.Now;
            var bag = S.World.Get<NotTodayBag>(NotToday);
            var set = bag.Day == S.Key(now) ? bag.Tasks.Select(id => FunKit.Find(S, id)).OfType<TaskItem>().ToList() : new List<TaskItem>();
            var options = new List<string> { "Set a task aside for today" };
            options.AddRange(set.Select(t => "Bring back " + t.Name));
            int i = FunKit.Pick(_w, "Not today list", set.Count == 0 ? "Nothing set aside today. Tasks you set aside come back tomorrow." : FunKit.Plural(set.Count, "task", "tasks") + " set aside until tomorrow.", options);
            if (i < 0) return;
            if (i > 0)
            {
                var back = set[i - 1];
                World.Change<NotTodayBag>(S, NotToday, "bringing back " + back.Name, b => b.Tasks.Remove(back.Id));
                _w.Refresh();
                _w.Say(back.Name + " is back on Today.", true);
                return;
            }
            var pool = S.TodayAll(now).Where(FunKit.Open).Where(t => !set.Contains(t)).ToList();
            if (_w.ChosenTask is TaskItem c && FunKit.Open(c) && !pool.Contains(c) && !set.Contains(c)) pool.Insert(0, c);
            var pick = FunKit.PickTask(_w, "Not today list", "Which task can wait until tomorrow?", pool, _w.ChosenTask);
            if (pick == null) { if (pool.Count == 0) _w.Say("Nothing on Today to set aside.", true); return; }
            World.Change<NotTodayBag>(S, NotToday, "setting " + pick.Name + " aside for today", b =>
            {
                if (b.Day != S.Key(now)) { b.Day = S.Key(now); b.Tasks.Clear(); }
                b.Tasks.Add(pick.Id);
            });
            _w.Refresh();
            bool late = Store.IsOverdue(pick, now);
            _w.Say(pick.Name + " is set aside for today and comes back tomorrow." + (late ? " It still shows under Overdue, so it never gets lost." : "") + " Control Z undoes it.", true);
        }

        // ---- Mood boost mini tasks ----

        private void BoostTool()
        {
            var ideas = Content.Lines("rest.boost").OrderBy(_ => FunKit.Rng.Next()).Take(5).ToList();
            int i = FunKit.Pick(_w, "Mood boost mini tasks", "Tiny pleasant things for a low moment. Pick one.", ideas);
            if (i < 0) return;
            int what = FunKit.Pick(_w, "Mood boost", ideas[i], new[] { "I did it", "Add it to Today", "Not now" });
            if (what == 0) { FunKit.Win(_w, Boost, 1, "A mood boost", 3, "Nice. Little things count."); return; }
            if (what != 1) return;
            var list = FunKit.TodayLists(S).FirstOrDefault()?.Name ?? _w.CurrentListName;
            _w.AddTask(new TaskItem { Name = ideas[i].TrimEnd('.'), List = list, Status = Status.Today, Size = Size.Small, Energy = Energy.Easy, QuickWin = true }, announce: false);
            _w.Say("Added to Today: " + ideas[i].TrimEnd('.') + ".", true);
        }

        // ---- Earned free time ----

        /// <summary>Minutes earned today, from effort points finished, and minutes left after what she took.</summary>
        public static (int Earned, int Left) Free(Store s, FreeBag b, DateTime now)
        {
            int earned = s.Progress(now).PointsDone * MinutesPerPoint;
            int spent = b.Day == now.ToString("yyyy-MM-dd") ? b.Spent : 0;
            return (earned, Math.Max(0, earned - spent));
        }

        private void FreeTimeTool()
        {
            if (FunTimer.Handle(_w, FreeTime)) return;
            var now = DateTime.Now;
            var (earned, left) = Free(S, S.World.Get<FreeBag>(FreeTime), now);
            if (earned == 0) { _w.Say("No free time earned yet today. Each finished task adds some: ten minutes for each effort point. Resting is allowed anyway.", true); return; }
            if (left == 0) { _w.Say("You have earned " + FocusLogic.Duration(earned) + " off today, and you have taken it all. Rest is still allowed.", true); return; }
            var takes = new[] { 15, 30, 60 }.Where(m => m < left).Append(left).Distinct().ToList();
            var options = takes.Select(m => "Take " + FocusLogic.Duration(m) + " now").Append("Just tell me").ToList();
            int i = FunKit.Pick(_w, "Earned free time", "You have earned " + FocusLogic.Duration(earned) + " off today. " + FocusLogic.Duration(left) + " left to take.", options);
            if (i < 0 || i >= takes.Count) return;
            int take = takes[i];
            World.Change<FreeBag>(S, FreeTime, "taking free time", b => { if (b.Day != S.Key(now)) { b.Day = S.Key(now); b.Spent = 0; } b.Spent += take; });
            GameEvents.RaiseBreakStarted();
            FunTimer.Start(_w, FreeTime, "Free time", take, null, false, (_, _) =>
            {
                GameEvents.RaiseBreakEnded();
                _w.Say("Your free time is up. Come back whenever you are ready.");
            }, GameEvents.RaiseBreakEnded);
            _w.Say("Enjoy " + FocusLogic.Duration(take) + ". You earned it. I will tell you when it is up.", true);
        }

        // ---- Spa day ----

        /// <summary>A rough week: a rough day, a low-energy day or a rough moment logged in Both things are true, this week.</summary>
        public static bool RoughWeek(Store s, DateTime now)
        {
            var start = Engine.WeekStart(now);
            for (var d = start; d <= now.Date; d = d.AddDays(1))
            {
                var day = s.Data.PeekDay(d);
                if (day.Mode == DayMode.RoughDay || day.Energy == "low") return true;
            }
            var m = s.World.GetKept<SkillsTools.Moments>("skills.both");
            return m.Week == s.Key(start) && m.Count > 0;
        }

        private void SpaTool()
        {
            var now = DateTime.Now;
            var scenes = Content.Items("rest.spaday");
            if (scenes.Count == 0) return;
            var scene = scenes[FunKit.Rng.Next(scenes.Count)];
            string bunny = S.Data.Game.BunnyName.Length > 0 ? S.Data.Game.BunnyName : "Bunny";
            string Fill(string key) => scene.TryGetValue(key, out var v) ? v.Replace("{name}", bunny) : "";
            var lines = new List<string> { RoughWeek(S, now) ? "This week was a lot. " + bunny + " booked you a spa day." : bunny + " says a spa day does not need a reason." };
            lines.Add(Fill("name"));
            lines.Add("Touch: " + Fill("touch"));
            lines.Add("Sound: " + Fill("sound"));
            lines.Add("Looks: " + Fill("looks"));
            lines.Add("Stay as long as you like. Nothing is waiting that cannot wait.");
            FunKit.Read(_w, "Spa day", lines.Where(l => l.Length > 0).ToList());
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("rest: " + what); }
            var sunday = new DateTime(2026, 11, 8, 10, 0, 0);
            Need(SlateChoices(sunday).Count == 2 && SlateChoices(new DateTime(2026, 11, 4, 10, 0, 0)).Count == 4, "clean slate offered the same date twice");
            var now = DateTime.Now;
            var list = store.Data.Lists[0].Name;
            var late = new TaskItem { Name = "late essay", List = list, Status = Status.Today, Due = now.Date.AddDays(-3) };
            var meet = new TaskItem { Name = "old meeting", List = list, IsMeeting = true, Due = now.Date.AddDays(-2) };
            store.Data.Tasks.AddRange(new[] { late, meet });
            int tasks = store.Data.Tasks.Count;
            store.Remember("the clean slate");
            int moved = CleanSlate(store, now, now.Date.AddDays(1));
            Need(moved >= 1 && !Store.IsOverdue(late, now) && store.Data.Tasks.Count == tasks, "clean slate did not move the overdue task, or lost one");
            Need(Store.IsOverdue(meet, now), "clean slate moved a meeting");
            store.Undo();
            var back = store.Data.Tasks.First(t => t.Id == late.Id);
            Need(Store.IsOverdue(back, now) && back.Status == Status.Today, "Control Z did not undo the clean slate");

            var bag = new NotTodayBag { Day = store.Key(now), Tasks = { back.Id } };
            Need(HiddenNotToday(bag, back, now) && !HiddenNotToday(bag, back, now.AddDays(1)), "not today must come back tomorrow");
            Need(AfternoonOff(new HalfDayBag { Day = store.Key(now) }, now.Date.AddHours(13)) && !AfternoonOff(new HalfDayBag { Day = store.Key(now) }, now.Date.AddHours(10)), "afternoon off");
            var k = new HalfDayKept { Month = now.ToString("yyyy-MM") };
            Need(!MayOffer(k, store, now.Date.AddHours(9), 0), "surprise half day twice in a month");

            var fb = new FreeBag { Day = store.Key(now), Spent = 5 };
            var (earned, left) = Free(store, fb, now);
            Need(earned == store.Progress(now).PointsDone * MinutesPerPoint && left == Math.Max(0, earned - 5), "earned free time");
            store.Data.Tasks.RemoveAll(t => t.Id == late.Id || t.Id == meet.Id);
            Need(Content.Lines("rest.boost").Count >= 20, "rest.boost has fewer than 20 lines");
            var spa = Content.Items("rest.spaday");
            Need(spa.Count >= 20 && spa.All(x => x.Keys.Take(3).SequenceEqual(new[] { "name", "touch", "sound" }) && x.ContainsKey("looks")), "spa scenes need name, touch and sound first, then looks, 20 of them");
            foreach (var id in new[] { HalfDay, Slate, NotToday, Boost, FreeTime, Spa }) Need(Tools.Find(id) != null, id + " is not in the toolbox");
        }
    }
}
