using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Threading;

namespace BusyBunny
{
    /// <summary>
    /// Games after a sprint (sweep 8.4): the trivia break (one multiple-choice question, psychology facts favored,
    /// 5 points for a right answer, Escape skips) and the reward spin (each finished sprint earns a spin for a small
    /// random reward). Points go through the ledger as their own Control Z step.
    /// </summary>
    public sealed class GamesBreak : IFeature
    {
        public const string TriviaId = "games.trivia", SpinId = "games.spin";
        public const int TriviaPrize = 5, PaidQuestionsADay = 3, MaxSpins = 3;
        private MainWindow _w = null!;
        private Store S => _w.Store;

        public sealed class Quiz { public List<int> Asked { get; set; } = new(); public string Day { get; set; } = ""; public int PaidToday { get; set; } public bool AfterSprint { get; set; } }
        public sealed class Spins { public int Ready { get; set; } }
        public sealed record Question(string Text, string Right, List<string> Choices, string Why);

        public void Attach(MainWindow w)
        {
            _w = w;
            Tools.Register(new BunnyTool(TriviaId, "Trivia break", Drawers.Games, "One multiple-choice question, mostly psychology. A right answer pays " + TriviaPrize + " points.", () => Ask(fromSprint: false)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(SpinId, "Reward spin", Drawers.Games, "Each finished sprint earns a spin for a small surprise reward.", Spin) { Since = "0.14.0" });
            BunnyTab.Register("Reward spin", "spins you have earned with sprints", Spin, SpinId);
            GameEvents.SprintDone += minutes =>
            {
                if (minutes <= 0) return;
                if (Tools.IsOn(SpinId)) AddSpin(S);
                if (!Tools.IsOn(TriviaId) || Calm.FunPaused)
                {
                    if (Tools.IsOn(SpinId)) Game.Note(S, "You earned a reward spin. Games, Reward spin.");
                    return;
                }
                var q = S.World.GetKept<Quiz>(TriviaId);
                q.AfterSprint = true;
                S.World.SetKept(TriviaId, q);
                S.World.Save();
                // After the sprint's own line and window, when Busy Bunny is idle.
                w.Dispatcher.BeginInvoke(() => { if (w.IsActive) Ask(fromSprint: true); }, DispatcherPriority.ApplicationIdle);
            };
        }

        // ---- Trivia ----

        public static List<Question> All()
        {
            var list = new List<Question>();
            foreach (var i in Content.Items("games.trivia"))
            {
                if (!i.TryGetValue("q", out var q) || !i.TryGetValue("a", out var a)) continue;
                var wrong = new[] { "b", "c", "d" }.Where(i.ContainsKey).Select(k => i[k]).ToList();
                list.Add(new Question(q, a, wrong.Prepend(a).ToList(), i.GetValueOrDefault("why") ?? ""));
            }
            return list;
        }

        /// <summary>The next question: never one of the last 40 asked (kept across restarts, never undone).</summary>
        public static (int Index, Question Q) Next(Store s, Random rng)
        {
            var all = All();
            var q = s.World.GetKept<Quiz>(TriviaId);
            var skip = q.Asked.TakeLast(Math.Min(40, all.Count - 1)).ToHashSet();
            var choices = Enumerable.Range(0, all.Count).Where(i => !skip.Contains(i)).ToList();
            int pick = choices[rng.Next(choices.Count)];
            q.Asked.Add(pick);
            if (q.Asked.Count > 60) q.Asked.RemoveRange(0, q.Asked.Count - 60);
            s.World.SetKept(TriviaId, q);
            s.World.Save();
            return (pick, all[pick]);
        }

        /// <summary>Whether a right answer pays now: always for the question after a sprint, otherwise 3 a day.</summary>
        public static bool WillPay(Store s, DateTime now)
        {
            var q = s.World.GetKept<Quiz>(TriviaId);
            return q.AfterSprint || q.Day != s.Key(now) || q.PaidToday < PaidQuestionsADay;
        }

        /// <summary>Pays a right answer as its own Control Z step, and counts it. Returns what it paid.</summary>
        public static int PayRight(Store s, DateTime now)
        {
            var q = s.World.GetKept<Quiz>(TriviaId);
            bool pays = WillPay(s, now);
            if (q.Day != s.Key(now)) { q.Day = s.Key(now); q.PaidToday = 0; }
            if (pays && !q.AfterSprint) q.PaidToday++;
            q.AfterSprint = false;
            s.World.SetKept(TriviaId, q);
            s.World.Save();
            if (!pays) return 0;
            Ledger.Pay(s, TriviaPrize, "Trivia break: a right answer", TriviaId, remember: true);
            s.Save();
            return TriviaPrize;
        }

        private void Ask(bool fromSprint)
        {
            var back = fromSprint ? _w.KeepFocus() : null;
            try
            {
                var (_, q) = Next(S, FunKit.Rng);
                var choices = q.Choices.OrderBy(_ => FunKit.Rng.Next()).ToList();
                string intro = (fromSprint ? "Trivia break, for finishing that sprint. Escape skips. " : "") + q.Text;
                int i = FunKit.Pick(_w, "Trivia break", "_Your answer", choices, 0, intro);
                if (i < 0)
                {
                    var k = S.World.GetKept<Quiz>(TriviaId);
                    k.AfterSprint = false;
                    S.World.SetKept(TriviaId, k);
                    S.World.Save();
                    _w.Say("Skipped. The answer was " + q.Right + ".", true);
                    return;
                }
                if (choices[i] == q.Right)
                {
                    int paid = PayRight(S, DateTime.Now);
                    Celebration.Sound(S, "SurpriseReward");
                    _w.SaveAndRefresh();
                    _w.Say("Right! " + q.Why + (paid > 0 ? " " + paid + " points. " + PlayTime.SpendLine(S.Data) + "." : " That one was for fun: the three paid questions for today are used up."), true);
                }
                else
                {
                    var k = S.World.GetKept<Quiz>(TriviaId);
                    k.AfterSprint = false;
                    S.World.SetKept(TriviaId, k);
                    S.World.Save();
                    _w.Say("Not quite. It was " + q.Right + ". " + q.Why, true);
                }
            }
            finally { back?.Invoke(); }
        }

        // ---- Reward spin ----

        public static void AddSpin(Store s)
        {
            var sp = s.World.Get<Spins>(SpinId);
            sp.Ready = Math.Min(MaxSpins, sp.Ready + 1);
            FunKit.Put(s, SpinId, sp);
        }

        /// <summary>Spends a spin and pays its reward, all one Control Z step. Returns the line, or null with no spin ready.</summary>
        public static string? SpinOnce(Store s, Random rng)
        {
            var sp = s.World.Get<Spins>(SpinId);
            if (sp.Ready <= 0) return null;
            var prizes = Content.Items("games.spin").Where(i => i.ContainsKey("text")).ToList();
            var prize = prizes[rng.Next(prizes.Count)];
            int points = int.TryParse(prize.GetValueOrDefault("points"), out int p) ? Math.Clamp(p, 1, 10) : 3;
            s.Remember("a reward spin");
            sp.Ready--;
            s.World.Set(SpinId, sp);
            s.World.Save();
            Ledger.Pay(s, points, "Reward spin", SpinId);
            s.Save();
            return prize["text"] + " " + points + " points. " + PlayTime.SpendLine(s.Data) + ".";
        }

        private void Spin()
        {
            var sp = S.World.Get<Spins>(SpinId);
            if (sp.Ready <= 0) { _w.Say("No spins ready. Finish a sprint to earn one; you can save up to " + MaxSpins + ".", true); return; }
            int i = FunKit.Pick(_w, "Reward spin", "_Spin", new[] { "Spin the wheel", "Save it for later" }, 0, "You have " + FunKit.Plural(sp.Ready, "spin") + " ready.");
            if (i != 0) return;
            Celebration.Sound(S, "Xylophone, sparkle");
            string? line = SpinOnce(S, FunKit.Rng);
            _w.SaveAndRefresh();
            if (line != null) _w.Say("The wheel clicks around and stops. " + line, true);
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("trivia and spins: " + what); }
            var all = All();
            Need(all.Count >= 60, "only " + all.Count + " trivia questions");
            Need(all.All(q => q.Choices.Count >= 3 && q.Choices.Distinct().Count() == q.Choices.Count && q.Why.Length > 0), "a trivia question has too few or repeated choices, or no explanation");
            Need(Content.Items("games.spin").Count >= 20, "fewer than 20 spin prizes");
            var rng = new Random(5);
            var seen = new List<int>();
            for (int k = 0; k < 50; k++) { var (i, _) = Next(store, rng); Need(!seen.TakeLast(40).Contains(i), "a trivia question came back too soon"); seen.Add(i); }
            // Three paid questions a day, plus the one after each sprint; Control Z takes a payment back.
            var now = DateTime.Now;
            int before = Engine.Total(store.Data);
            for (int k = 0; k < 4; k++) PayRight(store, now);
            Need(Engine.Total(store.Data) == before + PaidQuestionsADay * TriviaPrize, "more than three paid questions a day");
            var q = store.World.GetKept<Quiz>(TriviaId); q.AfterSprint = true; store.World.SetKept(TriviaId, q);
            Need(PayRight(store, now) == TriviaPrize, "the question after a sprint did not pay");
            store.Undo();
            Need(Engine.Total(store.Data) == before + PaidQuestionsADay * TriviaPrize, "Control Z did not take the trivia points back");
            // Spins: at most 3 saved, one spent per spin, undone together with its points.
            for (int k = 0; k < 5; k++) AddSpin(store);
            Need(store.World.Get<Spins>(SpinId).Ready == MaxSpins, "more than 3 spins saved");
            before = Engine.Total(store.Data);
            Need(SpinOnce(store, rng) != null && store.World.Get<Spins>(SpinId).Ready == MaxSpins - 1 && Engine.Total(store.Data) > before, "a spin");
            store.Undo();
            Need(store.World.Get<Spins>(SpinId).Ready == MaxSpins && Engine.Total(store.Data) == before, "Control Z did not give the spin back with its points");
        }
    }
}
