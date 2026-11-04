using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// The Skills drawer (sweep 12.7, items 1 to 5 and 8 to 10): optional skill ideas, not treatment. Both things are true,
    /// Bunny never pulls away (a warm welcome back, and a rule the self-test checks on every content line), a feelings check
    /// before a task (off until she turns it on), five short skills, how today went (what went right first), done is done
    /// (an optional lock), leave it imperfect challenges and the urge timer. Nothing here pays for checking again or gives
    /// endless reassurance: wins are capped each day and repeat asks get a gentle nudge to act.
    /// </summary>
    public sealed class SkillsTools : IFeature
    {
        private const string About = "skills.about", Both = "skills.both", Never = "skills.never", Feel = "skills.feel", Today = "skills.today",
            Done = "skills.doneisdone", Imperfect = "skills.imperfect", Urge = "skills.urge";
        private sealed class FeelState { public int Level { get; set; } = 7; public DateTime Last { get; set; } }
        private sealed class Seen { public DateTime Last { get; set; } }
        private sealed class Lock { public bool On { get; set; } }
        public sealed class Moments { public string Week { get; set; } = ""; public int Count { get; set; } }

        /// <summary>The five skills: name, tool id, the one plain line.</summary>
        public static readonly (string Name, string Id, string Line)[] Skills =
        {
            ("Stop and step back", "skills.stop", "Stop. Take one step back from the moment and one slow breath. Notice what is going on, then choose what to do next."),
            ("Check the facts", "skills.facts", "Ask what actually happened, what you are telling yourself about it, and whether the feeling fits the facts or a guess."),
            ("Opposite action", "skills.opposite", "When a feeling pushes you toward something that will not help, gently do the opposite: hiding becomes reaching out, avoiding becomes one small step."),
            ("Wise mind", "skills.wise", "Let your feeling side and your thinking side both have a say, then ask what the part of you that knows both would do."),
            ("Five-senses self-soothe", "skills.soothe", "Comfort each sense with something warm and kind: something to touch, hear, smell, taste and picture."),
        };
        private static readonly string[] Feelings = { "Calm", "Okay", "Anxious", "Sad", "Frustrated", "Overwhelmed", "Ashamed", "Lonely", "Restless" };

        private MainWindow _w = null!;
        private Store S => _w.Store;
        private readonly Dictionary<Guid, DateTime> _finished = new();
        private bool _urgeAsked;

        public void Attach(MainWindow w)
        {
            _w = w;
            const string since = "0.14.0";
            Tools.Register(new BunnyTool(About, "About the Skills drawer", Drawers.Skills, "These are optional ideas, not treatment. Your therapist can say which ones fit you.", ShowAbout) { Since = since });
            Tools.Register(new BunnyTool(Both, "Both things are true", Drawers.Skills, "Tell Bunny about a rough moment; it names what was hard and what went right.", BothThings) { Since = since });
            Tools.Register(new BunnyTool(Today, "How today went", Drawers.Skills, "What went right today first, then what is still open.", HowToday) { Since = since });
            foreach (var (name, id, line) in Skills)
                Tools.Register(new BunnyTool(id, name, Drawers.Skills, line, id == "skills.soothe" ? SelfSoothe : () => _w.Say(name + ". " + line, true)) { Since = since });
            Tools.Register(new BunnyTool(Feel, "Feelings check", Drawers.Skills, "How you feel and how strong. Above the level you set, Bunny offers a skill before a task.", () => FeelCheck(false), DefaultOn: false) { Since = since });
            Tools.Register(new BunnyTool(Done, "Done is done", Drawers.Skills, "Enter turns on or off a lock: a task you finished stays finished for the rest of the day. Control Z still fixes a real mistake.", ToggleLock)
                { Since = since, State = () => LockOn(S) ? "on" : "off" });
            Tools.Register(new BunnyTool(Imperfect, "Leave it imperfect", Drawers.Skills, "Small challenges, like sending an email without rereading it a third time. A few points each.", LeaveImperfect) { Since = since });
            Tools.Register(new BunnyTool(Urge, "Urge timer", Drawers.Skills, "Want to redo or recheck something? Wait ten minutes first. If the urge passes, you earn points.", UrgeTimer) { Since = since });
            Tools.Register(new BunnyTool(Never, "Bunny never pulls away", Drawers.Skills, "Bunny is never disappointed in you, and is glad when you come back after days away.",
                () => _w.Say("Bunny is never disappointed in you and never goes cold. However long you are away, it is glad you are back, and there is nothing to make up for.", true)) { Since = since });

            GameEvents.TaskDone += t => _finished[t.Id] = DateTime.Now;
            GameEvents.TaskUndone += t =>
            {
                if (!Tools.IsOn(Done) || !LockOn(S) || !_finished.TryGetValue(t.Id, out var when) || !Holds(true, when, DateTime.Now)) return;
                // Put it back once every handler has run, unless another change came in between.
                int steps = S.UndoCount;
                string name = t.Name;
                _w.Dispatcher.BeginInvoke(() =>
                {
                    if (S.UndoCount != steps || S.Undo() == null) return;
                    _w.Refresh();
                    _w.Say("Done is done: " + name + " stays finished for today. If it was a real mistake, Done is done in the Skills drawer turns the lock off.", true);
                }, System.Windows.Threading.DispatcherPriority.Background);
            };
            w.Started += () =>
            {
                foreach (var t in S.Data.Tasks) if (t.DoneAt is DateTime d && d.Date == DateTime.Today) _finished[t.Id] = d;
            };
            GameEvents.DayStarted += WelcomeBack;
        }

        // ---- Shared with other tools ----

        /// <summary>What went right comes first, then what was hard (sweep 12.7 item 5). Either part may be empty.</summary>
        public static string RightFirst(IEnumerable<string> right, IEnumerable<string> hard)
        {
            var r = right.Where(x => x.Length > 0).ToList();
            var h = hard.Where(x => x.Length > 0).ToList();
            string good = r.Count == 0 ? "" : "What went right: " + Join(r) + ".";
            string rest = h.Count == 0 ? "" : (good.Length > 0 ? " Still open: " : "Still open: ") + Join(h) + ".";
            return (good + rest).Trim();
        }

        private static string Join(List<string> parts) => parts.Count <= 1 ? string.Join("", parts) : string.Join(", ", parts.Take(parts.Count - 1)) + " and " + parts[^1];

        /// <summary>The good things of a day, worked out from what she did: tasks finished, things she logged, sprint minutes.</summary>
        public static List<string> WentRight(Store s, DateTime now)
        {
            var list = new List<string>();
            int done = s.Data.Tasks.Count(t => t.DoneAt?.Date == now.Date && t.Status is Status.Done or Status.DoneNotSubmitted);
            int things = s.Data.Log.Count(e => e.Kind == "DidAThing" && e.When.Date == now.Date);
            int minutes = s.Data.Log.Where(e => e.Kind == "Sprint" && e.When.Date == now.Date).Sum(e => e.Minutes);
            if (done > 0) list.Add("you finished " + FunKit.Plural(done, "task", "tasks"));
            if (things > 0) list.Add("you did " + FunKit.Plural(things, "thing", "things") + " that were never on a list");
            if (minutes > 0) list.Add("you put in " + FocusLogic.Duration(minutes) + " of focused work");
            return list;
        }

        /// <summary>
        /// The feelings check before a task, when she turned it on: at most once an hour. False when she chose a skill
        /// instead, so the tool that asked should not start the task.
        /// </summary>
        public static bool BeforeTask(MainWindow w)
        {
            if (!Tools.IsOn(Feel) || Features.Get<SkillsTools>() is not SkillsTools me) return true;
            var st = w.Store.World.GetKept<FeelState>(Feel);
            if (DateTime.Now - st.Last < TimeSpan.FromHours(1)) return true;
            return me.FeelCheck(true);
        }

        // ---- The tools ----

        private void ShowAbout() => FunKit.Read(_w, "About the Skills drawer", new[]
        {
            "The Skills drawer holds short skill ideas, drawn from general ideas used in DBT and in exposure-style practice.",
            "They are optional ideas, not treatment, and Busy Bunny is not a therapist. Your therapist, if you have one, can say which ones fit you.",
            "Every one has its own switch in Settings, Fun and tools, and the feelings check and the done is done lock start off.",
            "Nothing here pays for checking things again, and none of it is about being perfect.",
        });

        private void BothThings()
        {
            string? moment = FunKit.Ask(_w, "Both things are true", "_What happened?", "", "A few words is enough, like I snapped at my friend. It is not saved anywhere and never sent to Claude.");
            if (moment == null) return;
            var now = DateTime.Now;
            var m = S.World.GetKept<Moments>(Both);
            string week = S.Key(Engine.WeekStart(now));
            if (m.Week != week) { m.Week = week; m.Count = 0; }
            m.Count++;
            S.World.SetKept(Both, m);
            S.World.Save();
            _w.Say(BothAnd(S, moment, now), true);
        }

        /// <summary>A both-and line: her words, said kindly, and what went right today (or a kind fallback).</summary>
        public static string BothAnd(Store s, string moment, DateTime now)
        {
            string m = moment.Trim().TrimEnd('.', '!', '?');
            var right = WentRight(s, now);
            string good = right.Count > 0 ? "And today " + Join(right) + "." : Content.Pick(s, "skills.both");
            return "Both things are true. " + m + ", and that was hard. " + good + " One moment does not erase the day.";
        }

        private void HowToday()
        {
            var now = DateTime.Now;
            var right = WentRight(S, now);
            if (right.Count == 0) right.Add("you are here and looking, which is a start");
            int open = S.TodayAll(now).Count(FunKit.Open), late = S.Overdue(now).Count;
            var hard = new List<string>();
            if (open > 0) hard.Add(FunKit.Plural(open, "task", "tasks") + " on Today");
            if (late > 0) hard.Add(late + " overdue, waiting for whenever you are ready");
            _w.Say(RightFirst(right, hard), true);
        }

        private void SelfSoothe()
        {
            var items = Content.Lines("skills.soothe").OrderBy(_ => FunKit.Rng.Next()).Take(6).ToList();
            var lines = new List<string> { "Five-senses self-soothe: comfort each sense with something warm and kind. A few ideas:" };
            lines.AddRange(items);
            lines.Add("Pick one or two. There is no wrong way to do this.");
            FunKit.Read(_w, "Five-senses self-soothe", lines);
        }

        private bool FeelCheck(bool beforeTask)
        {
            var st = S.World.GetKept<FeelState>(Feel);
            var back = _w.KeepFocus();
            var win = Layout.Dialog(_w, "Feelings check");
            var feel = new ComboBox { ItemsSource = Feelings, SelectedIndex = 1 };
            var strength = new ComboBox { ItemsSource = Enumerable.Range(1, 10).Select(i => i.ToString()).ToList(), SelectedIndex = 2 };
            var level = new ComboBox { ItemsSource = Enumerable.Range(1, 10).Select(i => i.ToString()).ToList(), SelectedIndex = Math.Clamp(st.Level, 1, 10) - 1 };
            bool ok = false;
            var okb = Layout.Button("_OK", isDefault: true);
            okb.Click += (_, _) => { ok = true; win.Close(); };
            var skip = Layout.Button(beforeTask ? "_Skip, go to the task" : "Cancel", isCancel: true);
            skip.Click += (_, _) => win.Close();
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(Layout.Row("_How do you feel?", feel));
            panel.Children.Add(Layout.Row("How _strong, 1 to 10?", strength));
            panel.Children.Add(Layout.Row("_Offer a skill from this strength up", level));
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(okb);
            row.Children.Add(skip);
            panel.Children.Add(row);
            win.Content = panel;
            win.Loaded += (_, _) => feel.Focus();
            win.ShowDialog();
            back();
            st.Last = DateTime.Now;
            if (ok) st.Level = level.SelectedIndex + 1;
            S.World.SetKept(Feel, st);
            S.World.Save();
            if (!ok) return true;
            int strong = strength.SelectedIndex + 1;
            if (!OfferSkill(strong, st.Level)) { _w.Say("Thanks for checking in. " + (beforeTask ? "On to the task." : "That is good to know."), true); return true; }
            var options = Skills.Select(x => x.Name).Append(beforeTask ? "Go to the task anyway" : "No skill right now").ToList();
            int i = FunKit.Pick(_w, "A skill first?", Feelings[Math.Max(0, feel.SelectedIndex)] + " at " + strong + " is a lot. Want to try a skill before anything else?", options);
            if (i < 0 || i >= Skills.Length) { if (beforeTask) _w.Say("Okay, on to the task. Be kind to yourself.", true); return true; }
            Tools.Find(Skills[i].Id)?.Run();
            return false;
        }

        public static bool OfferSkill(int strength, int level) => strength >= Math.Clamp(level, 1, 10);

        // ---- Done is done ----

        private static bool LockOn(Store s) => s.World.GetKept<Lock>(Done).On;

        /// <summary>Whether the lock keeps a task finished: on, and the task was finished today.</summary>
        public static bool Holds(bool on, DateTime finished, DateTime now) => on && finished.Date == now.Date;

        private void ToggleLock()
        {
            bool on = !LockOn(S);
            S.World.SetKept(Done, new Lock { On = on });
            S.World.Save();
            _w.Say(on ? "Done is done is on now. A task you finish stays finished for the rest of the day, so there is nothing to go back and redo. Control Z right after still fixes a real mistake."
                : "Done is done is off now. Finished tasks can be reopened again.", true);
        }

        // ---- Leave it imperfect and the urge timer ----

        private void LeaveImperfect()
        {
            var all = Content.Lines("skills.imperfect");
            var three = all.OrderBy(_ => FunKit.Rng.Next()).Take(3).ToList();
            int i = FunKit.Pick(_w, "Leave it imperfect", "Pick one small challenge. Doing it badly on purpose is the point.", three);
            if (i < 0) return;
            int did = FunKit.Pick(_w, "Leave it imperfect", three[i], new[] { "I did it, and left it imperfect", "Not now" });
            if (did != 0) { _w.Say("Okay. It will be here when you want it.", true); return; }
            FunKit.Win(_w, Imperfect, 3, "Left it imperfect", 3, "You left it imperfect, and the world kept turning.");
        }

        private void UrgeTimer()
        {
            if (_urgeAsked)
            {
                _urgeAsked = false;
                int i = FunKit.Pick(_w, "Urge timer", "Ten minutes went by. Did the urge to redo or recheck pass?", new[] { "It passed, or it is quieter", "I still want to" });
                if (i < 0) { _urgeAsked = true; return; }
                if (i == 0) { FunKit.Win(_w, Urge, 3, "Let an urge pass", 3, Content.Pick(S, "skills.urge")); return; }
                _w.Say("That is okay. Go ahead and do it once, then let it be. Waiting at all was practice.", true);
                return;
            }
            if (FunTimer.Handle(_w, Urge)) return;
            FunTimer.Start(_w, Urge, "Urge timer", 10, null, false, (_, _) =>
            {
                _urgeAsked = true;
                _w.Say("Ten minutes are up. Open the Urge timer again to say whether the urge passed. Either way is okay.");
            });
            _w.Say("Urge timer: ten minutes. Notice the urge to redo or recheck, and let it sit without acting on it. I will tell you when the time is up.", true);
        }

        // ---- Bunny never pulls away ----

        private void WelcomeBack(DateTime day)
        {
            var seen = S.World.GetKept<Seen>(Never);
            var last = seen.Last;
            seen.Last = day.Date;
            S.World.SetKept(Never, seen);
            S.World.Save();
            if (!AwayLong(last, day) || !Tools.IsOn(Never) || Calm.FunPaused) return;
            Calm.SayUnprompted(_w, Content.Pick(S, "skills.welcome"));
        }

        /// <summary>Three days or more since she last opened Busy Bunny (never on the very first day).</summary>
        public static bool AwayLong(DateTime last, DateTime day) => last != default && (day.Date - last.Date).TotalDays >= 3;

        /// <summary>Words that would make Bunny sound cold, disappointed or leaving (sweep 12.7 item 2).</summary>
        public static readonly string[] Cold = { "disappointed in you", "disappointing", "where were you", "where have you been", "finally back", "finally showed",
            "i give up on you", "gave up on you", "leave you", "leaving you", "abandon", "you failed", "you should have", "shame on you", "what is wrong with you" };

        /// <summary>A content line that would sound cold, or null when every line of these topics is warm.</summary>
        public static string? ColdLine(IEnumerable<string> topics)
        {
            foreach (var topic in topics)
                foreach (var line in Content.Lines(topic))
                    if (Cold.Any(c => line.Contains(c, StringComparison.OrdinalIgnoreCase))) return topic + ": " + line;
            return null;
        }

        /// <summary>Every content topic built into Busy Bunny.</summary>
        public static List<string> AllTopics() => typeof(App).Assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("content.") && n.EndsWith(".txt")).Select(n => n["content.".Length..^".txt".Length]).ToList();

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("skills: " + what); }
            var now = new DateTime(2026, 11, 4, 15, 0, 0);
            Need(RightFirst(new[] { "you finished 2 tasks" }, new[] { "1 overdue" }) == "What went right: you finished 2 tasks. Still open: 1 overdue.", "right first order: " + RightFirst(new[] { "you finished 2 tasks" }, new[] { "1 overdue" }));
            Need(RightFirst(Array.Empty<string>(), new[] { "3 on Today" }) == "Still open: 3 on Today.", "nothing right yet");
            var t = new TaskItem { Name = "x", List = store.Data.Lists[0].Name, Status = Status.Done, DoneAt = now };
            store.Data.Tasks.Add(t);
            Need(WentRight(store, now).Any(l => l.Contains("1 task")), "went right missed a finished task");
            string both = BothAnd(store, "I ruined the whole day.", now);
            Need(both.StartsWith("Both things are true.") && both.IndexOf("hard") < both.IndexOf("finished"), "both-and line: " + both);
            Need(Holds(true, now.AddHours(-3), now) && !Holds(true, now.AddDays(-1), now) && !Holds(false, now, now), "done is done lock rule");
            Need(OfferSkill(8, 7) && OfferSkill(7, 7) && !OfferSkill(6, 7), "feelings check level");
            Need(AwayLong(now.AddDays(-3), now) && !AwayLong(now.AddDays(-2), now) && !AwayLong(default, now), "welcome back after days away");
            Need(ColdLine(AllTopics()) is not string cold || Fail(cold), "a cold line");
            foreach (var topic in new[] { "skills.both", "skills.welcome", "skills.soothe", "skills.imperfect", "skills.urge" })
                Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            foreach (var topic in AllTopics())
                foreach (var l in Content.Lines(topic)) Need(!l.Contains((char)35) && !l.Contains((char)42), topic + " has a pound sign or asterisk");
            Need(Tools.Find(Feel)?.DefaultOn == false, "the feelings check must start off");
            foreach (var id in new[] { About, Both, Never, Feel, Today, Done, Imperfect, Urge }.Concat(Skills.Select(x => x.Id)))
                Need(Tools.Find(id) != null, id + " is not in the toolbox");
            store.Data.Tasks.Remove(t);
        }

        private static bool Fail(string why) => throw new Exception("skills: Bunny sounds cold in " + why);
    }
}
