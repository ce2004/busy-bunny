using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace BusyBunny
{
    /// <summary>
    /// Fresh writing from Bunny (sweep 3.4 and 8.5): with the key set, the cheer after a finished task, a week in review
    /// and Semester Wrapped are written by Bunny through BunnyWrite (its daily limits, Calm mode and the spending cap
    /// apply), from visible tasks only. With no key, offline or over a limit, the content library writes them instead.
    /// Fresh cheers are written a few at a time ahead of need, so a finish never waits for Claude; one replaces the
    /// library cheer, and never outranks a level up, a record or points.
    /// </summary>
    public sealed class SmartFresh : IFeature
    {
        private const string CheersId = "freshcheers", WeekId = "weekinwords", WrappedId = "wrappedbybunny", CacheKey = "smart.cheers";
        private MainWindow _w = null!;
        private bool _filling, _writing;

        public sealed class Cheers { public List<string> Lines { get; set; } = new(); }

        public void Attach(MainWindow w)
        {
            _w = w;
            BunnyTool? cheers = null;
            cheers = Tools.Register(new BunnyTool(CheersId, "Fresh cheers from Bunny", Drawers.Wins, "With a Claude key, Bunny writes new cheers for finished tasks. Enter turns it on or off.", () =>
            {
                bool on = !Tools.IsOn(w.AppSettings, cheers!);
                Tools.SetOn(w.AppSettings, cheers!, on);
                w.AppSettings.Save();
                w.Say(on ? "Fresh cheers from Bunny are on now." + (SmartBunny.Ready ? "" : " They start once Bunny has a Claude key.") : "Fresh cheers from Bunny are off now. The usual cheers stay.", true);
            }) { Since = "0.14.0", State = () => Tools.IsOn(CheersId) ? "on" : "off" });
            Tools.Register(new BunnyTool(WeekId, "Your week, by Bunny", Drawers.Wins, "Bunny looks back at your week in a few sentences, then the numbers.", () => Show(week: true)) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(WrappedId, "Semester Wrapped, by Bunny", Drawers.Wins, "Your semester so far, written up by Bunny, then the numbers.", () => Show(week: false)) { Since = "0.14.0" });
            GameEvents.TaskDone += Done;
            GameEvents.DayStarted += _ => Fill();
        }

        // ---- Fresh cheers ----

        private bool CheersAllowed => Tools.IsOn(CheersId) && !Calm.FunPaused && SmartBunny.Ready;

        /// <summary>Takes the next fresh cheer, or null when none is waiting. Kept bookkeeping: undo never brings one back.</summary>
        public static string? Take(Store s)
        {
            var c = s.World.GetKept<Cheers>(CacheKey);
            if (c.Lines.Count == 0) return null;
            string line = c.Lines[0];
            c.Lines.RemoveAt(0);
            s.World.SetKept(CacheKey, c);
            s.World.Save();
            return line;
        }

        /// <summary>Keeps the lines of Bunny's answer that can be cheers: one per line, plain, not too long, no repeats.</summary>
        public static int Keep(Store s, string answer)
        {
            var c = s.World.GetKept<Cheers>(CacheKey);
            var library = Content.Lines("smart.cheer").ToHashSet();
            var fresh = answer.Replace("\r", "").Split('\n').Select(l => SmartText.Plain(l.Trim().TrimStart('-', '.', ' ', '0', '1', '2', '3', '4', '5', '6', '7', '8', '9').Trim(), 220))
                .Where(l => l.Length >= 8 && !library.Contains(l) && !c.Lines.Contains(l)).Take(5).ToList();
            c.Lines.AddRange(fresh);
            if (c.Lines.Count > 8) c.Lines.RemoveRange(0, c.Lines.Count - 8);
            s.World.SetKept(CacheKey, c);
            s.World.Save();
            return fresh.Count;
        }

        /// <summary>The priority a fresh cheer holds: it is heard over Chatter lines, but a badge, points reason, goal, record or level up wins.</summary>
        public const int CheerPriority = Game.Badge;

        /// <summary>Whether a fresh cheer is waiting, without using it.</summary>
        public static bool HasCheer(Store s) => s.World.GetKept<Cheers>(CacheKey).Lines.Count > 0;

        // Kept to tasks and the line after the finish: no Claude call here. Writing more cheers waits until the app is idle.
        private void Done(TaskItem t)
        {
            // Not while Bunny's own tool is finishing a task: its answer comes first.
            if (!CheersAllowed || Speech.Muted) return;
            if (HasCheer(_w.Store))
            {
                // A place in the line after this finish. The paid cheer is used only when it is the one heard; otherwise it waits for the next finish.
                var late = Game.QueueLate(_w, CheerPriority);
                _w.Dispatcher.BeginInvoke(() =>
                {
                    if (late.Decided && late.Chosen && !Calm.FunPaused && Take(_w.Store) is string line) { Game.Note(_w.Store, line); _w.Say(line); }
                    Fill();
                }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
            else _w.Dispatcher.BeginInvoke(() => Fill(), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>Writes a few cheers ahead, when fewer than two are waiting. Never more than BunnyWrite allows.</summary>
        private async void Fill()
        {
            var s = _w.Store;
            if (_filling || !CheersAllowed || s.World.GetKept<Cheers>(CacheKey).Lines.Count >= 2 || !BunnyWrite.MayAsk(s, CheersId, DateTime.Now)) return;
            _filling = true;
            try
            {
                var now = DateTime.Now;
                var today = BunnyWrite.Visible(s).Where(x => x.DoneAt?.Date == now.Date).Select(x => x.Name).Take(5).ToList();
                string prompt = "Write 4 different cheers for a college student finishing tasks in her to-do app, one per line, each under 20 words, no numbering. " +
                    SmartText.Voice(_w.AppSettings) + SmartMemory.ForWriting(s) +
                    (today.Count > 0 ? " Today she finished: " + string.Join(", ", today) + "." : "") + " Vary them: some about momentum, some silly, some calm.";
                string answer = await BunnyWrite.Short(s, CheersId, prompt, "smart.cheer");
                Keep(s, answer);
            }
            catch (Exception e) { ErrorLog.Write("Fresh cheers failed: " + e.GetType().Name); }
            finally { _filling = false; }
        }

        // ---- The week and the semester, in words ----

        /// <summary>The week's numbers, from every list (these stay on this computer).</summary>
        public static List<string> WeekLines(Store s, DateTime now)
        {
            var from = Engine.WeekStart(now);
            var done = s.Data.Tasks.Where(t => t.DoneAt >= from && t.DoneAt <= now).ToList();
            var lines = new List<string> { "Finished this week: " + SmartUi.Count(done.Count, "task") + "." };
            var tags = done.Where(t => t.Tag.Length > 0).GroupBy(t => t.Tag, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(4).ToList();
            if (tags.Count > 0) lines.Add("Most of it went to " + string.Join(", ", tags.Select(g => g.Key + " " + g.Count())) + ".");
            int minutes = s.Data.Log.Where(e => e.Kind == "Sprint" && e.When >= from && e.When <= now).Sum(e => e.Minutes);
            if (minutes > 0) lines.Add("Sprint time: " + minutes / 60 + " hours " + minutes % 60 + " minutes.");
            var next = s.Data.Tasks.Where(t => t.Status != Status.Done && t.Due >= from.AddDays(7) && t.Due < from.AddDays(14) && !s.ListOf(t).Archived).OrderBy(t => t.Due).ToList();
            lines.Add("Due next week: " + SmartUi.Count(next.Count, "task") + (next.Count > 0 ? ", first " + next[0].Name + " " + Words.Day(next[0].Due!.Value, now) + "." : "."));
            return lines;
        }

        /// <summary>What Claude gets for the week: visible tasks only.</summary>
        public static string WeekPrompt(Store s, Settings? cfg, DateTime now)
        {
            var from = Engine.WeekStart(now);
            var seen = BunnyWrite.Visible(s);
            var done = seen.Where(t => t.DoneAt >= from && t.DoneAt <= now).Select(t => t.Name + (t.Tag.Length > 0 ? " (" + t.Tag + ")" : "")).Take(20).ToList();
            var next = seen.Where(t => t.Status != Status.Done && t.Due >= from.AddDays(7) && t.Due < from.AddDays(14)).OrderBy(t => t.Due).Take(10).Select(t => t.Name + " due " + t.Due!.Value.ToString("dddd")).ToList();
            var overdue = seen.Count(t => Store.IsOverdue(t, now));
            return "Write a short, warm look back at a college student's week, 3 to 5 sentences, then one small idea for next week. Never shame her for what is not done. " + SmartText.Voice(cfg) + SmartMemory.ForWriting(s) +
                " Finished this week: " + (done.Count > 0 ? string.Join("; ", done) : "nothing logged yet") + ". Due next week: " + (next.Count > 0 ? string.Join("; ", next) : "nothing yet") + ". Overdue: " + overdue + ".";
        }

        /// <summary>What Claude gets for the semester: counts from visible lists only.</summary>
        public static string WrappedPrompt(Store s, Settings? cfg, DateTime now)
        {
            var (name, from, to) = GameFeature.Semester(now);
            var visible = SmartText.VisibleLists(s);
            var done = s.Data.Log.Where(e => e.Kind == "Done" && e.When.Date >= from && e.When.Date <= to && visible.Contains(e.List)).ToList();
            var tags = done.Where(e => e.Tag.Length > 0).GroupBy(e => e.Tag, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).Take(5).Select(g => g.Key + " " + g.Count());
            int minutes = s.Data.Log.Where(e => e.Kind == "Sprint" && e.When.Date >= from && e.When.Date <= to && visible.Contains(e.List)).Sum(e => e.Minutes);
            var bosses = s.Data.Log.Where(e => e.Kind == "Boss" && e.When.Date >= from && visible.Contains(e.List)).Count();
            return "Write a fun Semester Wrapped recap for a college student, like a year-in-review, 4 to 6 sentences, celebrating what she did. " + SmartText.Voice(cfg) + SmartMemory.ForWriting(s) +
                " Semester: " + name + ". Tasks finished: " + done.Count + ". By class: " + string.Join(", ", tags) + ". Focused sprint time: " + minutes / 60 + " hours. Bosses beaten: " + bosses + ".";
        }

        private async void Show(bool week)
        {
            if (_writing) { _w.Say("Bunny is still writing the last one.", true); return; }
            var s = _w.Store;
            var now = DateTime.Now;
            string title = week ? "Your week, by Bunny" : "Semester Wrapped, by Bunny";
            string id = week ? WeekId : WrappedId;
            _writing = true;
            string words;
            try
            {
                if (SmartBunny.Ready && !Calm.FunPaused && BunnyWrite.MayAsk(s, id, now)) _w.Say("Bunny is writing it.", true);
                words = await BunnyWrite.Short(s, id, week ? WeekPrompt(s, _w.AppSettings, now) : WrappedPrompt(s, _w.AppSettings, now), week ? "smart.week" : "smart.wrapped");
            }
            finally { _writing = false; }
            var lines = new List<string> { words };
            lines.AddRange(week ? WeekLines(s, now) : GameFeature.Wrapped(s, now));
            var back = _w.KeepFocus();
            TextViewer.Show(_w, title, lines);
            back();
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Fresh writing: " + what); }
            foreach (var topic in new[] { "smart.cheer", "smart.week", "smart.wrapped" })
                Need(Content.Lines(topic).Count >= 20, topic + " has fewer than 20 lines");
            Need(Take(store) == null, "a cheer was waiting in a new store");
            Need(Keep(store, "1. You did the thing!\n- Momentum looks good on you.\n\nok\n**Bold** move, finishing that.") == 3, "keeping Bunny's lines");
            Need(Take(store) == "You did the thing!" && Take(store) == "Momentum looks good on you.", "cheers come out in order");
            Need(Take(store) == "Bold move, finishing that.", "marks were not cleaned");
            Need(Take(store) == null, "a cheer was used twice");
            // Private tasks never reach the prompts.
            var secret = new TaskList { Name = "ZZPRIVATE", PrivateFromBunny = true };
            store.Data.Lists.Add(secret);
            var now = DateTime.Now;
            store.Data.Tasks.Add(new TaskItem { Name = "ZZSECRET done", List = secret.Name, Tag = "ZZTAG", Status = Status.Done, DoneAt = now });
            store.Data.Tasks.Add(new TaskItem { Name = "ZZSECRET next", List = secret.Name, Due = now.AddDays(7) });
            store.Data.Log.Add(new LogEntry { Kind = "Done", When = now, List = secret.Name, Tag = "ZZTAG", Text = "ZZSECRET" });
            foreach (var text in new[] { WeekPrompt(store, new Settings(), now), WrappedPrompt(store, new Settings(), now) })
                Need(!text.Contains("ZZ"), "a private task reached Claude: " + text);
            Need(WeekLines(store, now).Count >= 2, "the week's numbers");
            // Only her humor lines go into routine writing, never the rest of About me.
            store.World.Set(SmartMemory.AboutId, new SmartMemory.AboutMe { Lines = new List<string> { "Never mention ZZDAD", "Describe ZZTOUCH things by touch", "Dry humor please" } });
            foreach (var text in new[] { WeekPrompt(store, new Settings(), now), WrappedPrompt(store, new Settings(), now) })
                Need(!text.Contains("ZZ") && text.Contains("Dry humor"), "About me lines in fresh writing: " + text);
            Need(!HasCheer(store) && Keep(store, "A brand new cheer for you.") == 1 && HasCheer(store), "a waiting cheer");
            Need(CheerPriority > Game.Other && CheerPriority < Game.Chatter, "a fresh cheer must rank behind points and goals and ahead of Chatter lines");
            Need(Tools.Find(CheersId)?.Spoken.EndsWith(", now on") == true, "fresh cheers do not say whether they are on");
        }
    }
}
