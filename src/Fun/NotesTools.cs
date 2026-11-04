using System;
using System.Collections.Generic;
using System.Linq;

namespace BusyBunny
{
    /// <summary>
    /// Notes to self drawer: time capsule, question of the week, proud moments log, letter to future you, win of the day
    /// and where I left off. Her own words stay on this PC and are never sent to Claude.
    /// </summary>
    public sealed class NotesTools : IFeature
    {
        public sealed class Capsule { public string Semester { get; set; } = ""; public List<string> Goals { get; set; } = new(); public string Note { get; set; } = ""; public bool Opened { get; set; } }
        public sealed class Capsules { public List<Capsule> Items { get; set; } = new(); }
        public sealed class Answer { public string Question { get; set; } = ""; public string Text { get; set; } = ""; }
        public sealed class Answers { public Dictionary<string, Answer> Weeks { get; set; } = new(); }
        public sealed class Moment { public DateTime When { get; set; } public string Text { get; set; } = ""; }
        public sealed class Moments { public List<Moment> Items { get; set; } = new(); }
        public sealed class Letter { public string Month { get; set; } = ""; public string Text { get; set; } = ""; public bool Opened { get; set; } }
        public sealed class Letters { public List<Letter> Items { get; set; } = new(); }
        public sealed class Marks { public string WinAsked { get; set; } = ""; public string CapsuleSaid { get; set; } = ""; public string LetterSaid { get; set; } = ""; }

        public const string CapsuleId = "notes.capsule", QuestionId = "notes.question", ProudId = "notes.proud", LetterId = "notes.letter",
            WinId = "notes.win", LeftId = "notes.leftoff";
        private const string MarksId = "notes.marks";

        private MainWindow _w = null!;
        private Store S => _w.Store;

        public void Attach(MainWindow w)
        {
            _w = w;
            void T(string id, string name, string what, Action run) => Tools.Register(new BunnyTool(id, name, Drawers.Notes, what, run) { Since = "0.14.0" });
            T(CapsuleId, "Time capsule", "Goals and a sealed note for the semester, opened when it ends.", TimeCapsule);
            T(QuestionId, "Question of the week", "One question to think about each week; your answer is kept.", Question);
            T(ProudId, "Proud moments log", "Save moments you are proud of, and hear them again.", Proud);
            T(LetterId, "Letter to future you", "Write a letter each month; it opens next month.", LetterTool);
            T(WinId, "Win of the day", "Type one win from today; it goes in your proud moments.", WinOfTheDay);
            T(LeftId, "Where I left off", "A note on where you stopped a task, read back when you open it again.", LeftOff);

            // Her note stays when the task is finished (set back, it is there again); it is only not read for a finished task.
            w.DetailNotes.Add(t => LeftOffNote(S, t) is string n ? "Where you left off: " + n + "." : null);

            // The evening question waits for a moment it may be spoken, and is not used up while hushed.
            Ticker.Every(WinId, now => WinDue(S, w.AppSettings, now), AskWin);
            PlanKit.Morning(CapsuleId, (s, now) =>
            {
                var m = s.World.GetKept<Marks>(MarksId);
                string sem = Semester(now);
                if (m.CapsuleSaid == sem) return null;
                var c = s.World.Get<Capsules>(CapsuleId);
                bool ready = c.Items.Any(x => x.Semester != sem && !x.Opened);
                bool fresh = FocusLogic.SemesterStart(now) > now.Date.AddDays(-14) && !c.Items.Any(x => x.Semester == sem);
                if (!ready && !fresh) return null;
                m.CapsuleSaid = sem;
                s.World.SetKept(MarksId, m);
                return ready ? "Your time capsule from last semester is ready to open, in Notes to self." : "A new semester: the time capsule in Notes to self is waiting for your goals.";
            });
            PlanKit.Morning(LetterId, (s, now) =>
            {
                var m = s.World.GetKept<Marks>(MarksId);
                string month = now.ToString("yyyy-MM");
                if (m.LetterSaid == month || !s.World.Get<Letters>(LetterId).Items.Any(l => string.CompareOrdinal(l.Month, month) < 0 && !l.Opened)) return null;
                m.LetterSaid = month;
                s.World.SetKept(MarksId, m);
                return "A letter from last month's you is waiting in Notes to self.";
            });
            PlanKit.Morning(QuestionId, (s, now) => now.DayOfWeek == DayOfWeek.Monday ? "This week's question: " + QuestionFor(now) : null);
        }

        // ---- For other tools ----

        /// <summary>Her where I left off note for an open task, or null (tool off, no note, or the task is finished).</summary>
        public static string? LeftOffNote(Store s, TaskItem t) =>
            Tools.IsOn(LeftId) && !PlanKit.Finished(t) && s.World.Get<PlanDay.TaskTexts>(LeftId).Of.TryGetValue(t.Id, out var n) ? n : null;

        /// <summary>Whether to ask for the win of the day now: from 7 pm, not asked yet today, and an unprompted line may be spoken (not in quiet hours, class, a sprint or Calm mode).</summary>
        public static bool WinDue(Store s, Settings? cfg, DateTime now) =>
            now.Hour >= 19 && Tools.IsOn(WinId) && !Calm.FunPaused && s.World.GetKept<Marks>(MarksId).WinAsked != s.Key(now) && Calm.CanSpeakUnprompted(s, cfg, now);

        public static string Semester(DateTime now) => FocusLogic.SemesterStart(now).ToString("yyyy-MM");

        /// <summary>This semester's time capsule goals (readable all semester; only the note is sealed).</summary>
        public static List<string> CapsuleGoals(Store s) => s.World.Get<Capsules>(CapsuleId).Items.FirstOrDefault(c => c.Semester == Semester(DateTime.Now))?.Goals ?? new();

        /// <summary>Her proud moments, oldest first (future-you messages can read from these).</summary>
        public static List<string> ProudMoments(Store s) => s.World.Get<Moments>(ProudId).Items.Select(m => m.Text).ToList();

        /// <summary>Adds a proud moment (no Remember).</summary>
        public static void AddProud(Store s, string text, DateTime when)
        {
            var m = s.World.Get<Moments>(ProudId);
            m.Items.Add(new Moment { When = when, Text = text });
            s.World.Set(ProudId, m);
        }

        public static string QuestionFor(DateTime now)
        {
            var all = Content.Lines("notes.question");
            if (all.Count == 0) return "";
            int week = (int)(Engine.WeekStart(now) - new DateTime(2026, 1, 5)).TotalDays / 7;
            return all[((week % all.Count) + all.Count) % all.Count];
        }

        /// <summary>A capsule is sealed while its semester is still going.</summary>
        public static bool Sealed(Capsule c, DateTime now) => c.Semester == Semester(now);

        // ---- The tools ----

        private void TimeCapsule()
        {
            var now = DateTime.Now;
            var all = S.World.Get<Capsules>(CapsuleId);
            var old = all.Items.Where(c => !Sealed(c, now) && !c.Opened).OrderBy(c => c.Semester).FirstOrDefault();
            var mine = all.Items.FirstOrDefault(c => Sealed(c, now));
            var options = new List<string>();
            if (old != null) options.Add("Open the time capsule from the semester that started " + DateTime.Parse(old.Semester + "-01").ToString("MMMM yyyy"));
            options.Add(mine == null ? "Write this semester's capsule" : "Change this semester's goals");
            if (all.Items.Any(c => c.Opened)) options.Add("Read capsules already opened");
            int i = PlanKit.Pick(_w, "Time capsule", "_What now?", options, 0, mine != null ? "This semester's note is sealed until the semester ends." : null);
            if (i < 0) return;
            if (options[i].StartsWith("Open"))
            {
                PlanKit.Change<Capsules>(S, CapsuleId, "opening the time capsule", x => x.Items.First(c => c.Semester == old!.Semester).Opened = true);
                SoundBank.Play("SurpriseReward");
                PlanKit.Show(_w, "Time capsule", CapsuleLines(old!));
                return;
            }
            if (options[i].StartsWith("Read"))
            {
                PlanKit.Show(_w, "Opened time capsules", all.Items.Where(c => c.Opened).OrderBy(c => c.Semester).SelectMany(CapsuleLines).ToList());
                return;
            }
            var goals = PlanKit.AskText(_w, "Time capsule", "Your _goals for this semester, one per line", string.Join("\n", mine?.Goals ?? new()), "You can check in on these each month, in the Plan drawer.", lines: true);
            if (goals == null) return;
            string? note = mine?.Note;
            if (mine == null)
            {
                note = PlanKit.AskText(_w, "Time capsule", "A _note to open when the semester ends", "", "Once saved it is sealed: you hear it again when the semester is over.", lines: true, allowEmpty: true);
                if (note == null) return;
            }
            var list = goals.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            string sem = Semester(now);
            PlanKit.Change<Capsules>(S, CapsuleId, "the time capsule", x =>
            {
                x.Items.RemoveAll(c => c.Semester == sem);
                x.Items.Add(new Capsule { Semester = sem, Goals = list, Note = note ?? "" });
            });
            _w.Say(mine == null ? "Sealed. " + PlanKit.Plural(list.Count, "goal", "goals") + " and a note, opened when the semester ends." : "Goals updated. The note stays sealed.", true);
        }

        private static List<string> CapsuleLines(Capsule c)
        {
            var lines = new List<string> { "From the start of the semester of " + DateTime.Parse(c.Semester + "-01").ToString("MMMM yyyy") + ":" };
            lines.AddRange(c.Goals.Select(g => "Goal: " + g));
            if (c.Note.Length > 0) lines.Add("Your note: " + c.Note);
            return lines;
        }

        private void Question()
        {
            var now = DateTime.Now;
            string week = S.Key(Engine.WeekStart(now)), q = QuestionFor(now);
            var a = S.World.Get<Answers>(QuestionId);
            var past = a.Weeks.Where(kv => kv.Key != week && kv.Value.Text.Length > 0).OrderBy(kv => kv.Key).ToList();
            if (past.Count > 0)
            {
                int c = PlanKit.Pick(_w, "Question of the week", "_What now?", new[] { "Answer this week's question", "Hear past answers" }, 0, q);
                if (c < 0) return;
                if (c == 1) { PlanKit.Show(_w, "Past answers", past.SelectMany(kv => new[] { "Week of " + kv.Key + ": " + kv.Value.Question, "You said: " + kv.Value.Text }).ToList()); return; }
            }
            var text = PlanKit.AskText(_w, "Question of the week", "_Your answer", a.Weeks.TryGetValue(week, out var have) ? have.Text : "", q, lines: true, allowEmpty: true);
            if (text == null) return;
            PlanKit.Change<Answers>(S, QuestionId, "answering the question of the week", x => x.Weeks[week] = new Answer { Question = q, Text = text });
            _w.Say(text.Length > 0 ? "Answer kept." : "No answer this week, and that is fine too.", true);
        }

        private void Proud()
        {
            var items = S.World.Get<Moments>(ProudId).Items;
            var options = new List<string> { "Add a proud moment" };
            if (items.Count > 0) { options.Add("Hear them all"); options.Add("Remove one"); }
            int c = PlanKit.Pick(_w, "Proud moments log", "_What now?", options, items.Count > 0 ? 1 : 0, PlanKit.Plural(items.Count, "moment", "moments") + " saved.");
            if (c < 0) return;
            if (options[c] == "Hear them all") { PlanKit.Show(_w, "Proud moments", items.Select(m => m.When.ToString("MMMM d") + ": " + m.Text).ToList()); return; }
            if (options[c] == "Remove one")
            {
                int r = PlanKit.Pick(_w, "Proud moments log", "Moment to _remove", items.Select(m => m.When.ToString("MMMM d") + ": " + m.Text).ToList(), items.Count - 1);
                if (r < 0) return;
                PlanKit.Change<Moments>(S, ProudId, "removing a proud moment", x => x.Items.RemoveAt(r));
                _w.Say("Removed. Control Z brings it back.", true);
                return;
            }
            var text = PlanKit.AskText(_w, "Proud moments log", "What are you _proud of?", "", "Big or small. How it felt is worth writing down too.", lines: true);
            if (text == null) return;
            PlanKit.Change<Moments>(S, ProudId, "a proud moment", _ => { });
            AddProud(S, text, DateTime.Now);
            S.World.Save();
            _w.Say("Saved. " + PlanKit.Plural(items.Count + 1, "proud moment", "proud moments") + " in the log.", true);
        }

        private void LetterTool()
        {
            var now = DateTime.Now;
            string month = now.ToString("yyyy-MM");
            var all = S.World.Get<Letters>(LetterId);
            var waiting = all.Items.Where(l => string.CompareOrdinal(l.Month, month) < 0 && !l.Opened).OrderBy(l => l.Month).FirstOrDefault();
            var mine = all.Items.FirstOrDefault(l => l.Month == month);
            var options = new List<string>();
            if (waiting != null) options.Add("Open the letter from " + DateTime.Parse(waiting.Month + "-01").ToString("MMMM"));
            options.Add(mine == null ? "Write this month's letter" : "Change this month's letter");
            if (all.Items.Any(l => l.Opened)) options.Add("Read letters already opened");
            int c = PlanKit.Pick(_w, "Letter to future you", "_What now?", options);
            if (c < 0) return;
            if (options[c].StartsWith("Open"))
            {
                PlanKit.Change<Letters>(S, LetterId, "opening a letter", x => x.Items.First(l => l.Month == waiting!.Month).Opened = true);
                PlanKit.Show(_w, "A letter from " + DateTime.Parse(waiting!.Month + "-01").ToString("MMMM"), new[] { waiting.Text });
                return;
            }
            if (options[c].StartsWith("Read"))
            {
                PlanKit.Show(_w, "Opened letters", all.Items.Where(l => l.Opened).OrderBy(l => l.Month).Select(l => DateTime.Parse(l.Month + "-01").ToString("MMMM yyyy") + ": " + l.Text).ToList());
                return;
            }
            var text = PlanKit.AskText(_w, "Letter to future you", "Dear future _me", mine?.Text ?? "", "It opens next month. You can change it until then.", lines: true);
            if (text == null) return;
            PlanKit.Change<Letters>(S, LetterId, "a letter to future you", x => { x.Items.RemoveAll(l => l.Month == month); x.Items.Add(new Letter { Month = month, Text = text }); });
            _w.Say("Letter kept. It opens next month.", true);
        }

        private void WinOfTheDay()
        {
            var text = PlanKit.AskText(_w, "Win of the day", "Today's _win", "", "One thing that went well today. It goes in your proud moments.");
            if (text == null) return;
            PlanKit.Change<Moments>(S, ProudId, "the win of the day", _ => { });
            AddProud(S, "Win of the day: " + text, DateTime.Now);
            var m = S.World.GetKept<Marks>(MarksId); m.WinAsked = S.Key(DateTime.Now); S.World.SetKept(MarksId, m);
            S.World.Save();
            _w.Say("Win kept: " + text + ".", true);
        }

        private void AskWin(DateTime now)
        {
            var m = S.World.GetKept<Marks>(MarksId);
            m.WinAsked = S.Key(now);
            S.World.SetKept(MarksId, m);
            S.World.Save();
            bool logged = S.World.Get<Moments>(ProudId).Items.Any(x => x.When.Date == now.Date && x.Text.StartsWith("Win of the day"));
            if (!logged) Calm.SayUnprompted(_w, Content.Pick(S, "notes.winask"));
        }

        private void LeftOff()
        {
            var t = PlanKit.PickTask(_w, "Where I left off", "_Task", PlanKit.OpenTasks(S, DateTime.Now));
            if (t == null) return;
            var have = S.World.Get<PlanDay.TaskTexts>(LeftId).Of.TryGetValue(t.Id, out var n) ? n : "";
            var text = PlanKit.AskText(_w, "Where I left off", "Where you _stopped, and what is next", have, "You hear it with the task next time you open it. Leave it empty to clear it.", allowEmpty: true);
            if (text == null) return;
            PlanKit.Change<PlanDay.TaskTexts>(S, LeftId, "a where I left off note", x => { if (text.Length == 0) x.Of.Remove(t.Id); else x.Of[t.Id] = text; });
            _w.Say(text.Length == 0 ? "Note cleared." : "Noted for " + t.Name + ". It is read with the task.", true);
        }

        // ---- Self-test ----

        public void SelfTest(Store s)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("notes: " + what); }
            var now = DateTime.Now;
            Need(Sealed(new Capsule { Semester = Semester(now) }, now) && !Sealed(new Capsule { Semester = Semester(now.AddMonths(-7)) }, now), "time capsule sealing");
            Need(Semester(new DateTime(2026, 9, 1)) == "2026-08" && Semester(new DateTime(2027, 3, 1)) == "2027-01", "semester keys");
            Need(QuestionFor(new DateTime(2026, 11, 2)) == QuestionFor(new DateTime(2026, 11, 8)) && QuestionFor(new DateTime(2026, 11, 2)) != QuestionFor(new DateTime(2026, 11, 9)), "the question changes weekly");
            // Saving and undo of a proud moment.
            int had = ProudMoments(s).Count;
            s.Remember("test proud");
            AddProud(s, "test moment", now);
            Need(ProudMoments(s).Count == had + 1, "proud moment not kept");
            s.Undo();
            Need(ProudMoments(s).Count == had, "undo kept the proud moment");
            // Where I left off: kept when the task is finished, read again once it is set back.
            var t = new TaskItem { Name = "left off test", List = s.Data.Lists[0].Name, Status = Status.Done };
            s.World.Set(LeftId, new PlanDay.TaskTexts { Of = { [t.Id] = "page 4" } });
            if (Tools.IsOn(LeftId))
            {
                Need(LeftOffNote(s, t) == null, "a left off note read for a finished task");
                t.Status = Status.Today;
                Need(LeftOffNote(s, t) == "page 4", "the left off note was lost when the task was finished");
            }
            s.World.Set(LeftId, new PlanDay.TaskTexts());

            // Win of the day waits while hushed (Calm mode, quiet hours) instead of being used up.
            var evening = now.Date.AddHours(20);
            s.World.SetKept(MarksId, new Marks());
            Need(!WinDue(s, new Settings { CalmMode = true }, evening), "the win question came in Calm mode");
            Need(!WinDue(s, new Settings { QuietStart = 19, QuietEnd = 8 }, evening), "the win question came in quiet hours");
            Need(!WinDue(s, null, now.Date.AddHours(12)), "the win question came at noon");

            foreach (var topic in new[] { "notes.question", "notes.winask" }) Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            foreach (var id in new[] { CapsuleId, QuestionId, ProudId, LetterId, WinId, LeftId }) Need(Tools.Find(id) != null, id + " is not in the toolbox");
        }
    }
}
