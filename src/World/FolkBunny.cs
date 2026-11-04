using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Bunny itself (sweep 8.7, 12.5, 12.6, 12.8, 13.3): moods with reasons, outfits, what Bunny is doing, naps and
    /// dreams, the journal, likes and dislikes, personality traits that shift slowly, opinions, a hobby, inside jokes,
    /// anniversaries, a poem at semester end and Bunny's playlist. Traits, likes and jokes live in the world's kept
    /// bookkeeping (Control Z never rewinds who Bunny has become); she sees and resets them in Settings.
    /// </summary>
    public sealed class FolkBunny : IFeature
    {
        private MainWindow _w = null!;
        private Store S => _w.Store;

        public void Attach(MainWindow w)
        {
            _w = w;
            Folk.Start(w.Store);
            Folk.Tool("folk.doing", "What is Bunny doing", Drawers.World, "One line on what your bunny is up to right now.", Doing);
            Folk.Tool("folk.mood", "Bunny's mood", Drawers.World, "How your bunny feels today, and why.", MoodTool);
            Folk.Tool("folk.outfits", "Bunny's outfits", Drawers.World, "Outfits your bunny has unlocked; pick one for it to wear.", Outfits);
            Folk.Tool("folk.nap", "Nap with Bunny", Drawers.Rest, "Your bunny curls up beside you, snoring softly.", Nap);
            Folk.Tool("folk.dream", "Bunny's dream", Drawers.World, "Your bunny tells you last night's dream.", Dream);
            Folk.Tool("folk.journal", "Bunny's journal", Drawers.World, "Your bunny's diary about your days, newest first.", Journal, "your bunny's diary about your days");
            Folk.Tool("folk.likes", "Bunny's likes", Drawers.World, "Your bunny's favorite things, and what it would rather avoid.", Likes);
            Folk.Tool("folk.traits", "Bunny's personality", Drawers.World, "Who your bunny is becoming: brave or careful, outdoorsy or cozy and more.", TraitsTool);
            Folk.Tool("folk.opinions", "Bunny's hot takes", Drawers.World, "Your bunny's opinions, to agree or argue with.", Opinions);
            Folk.Tool("folk.hobby", "Bunny's hobby", Drawers.World, "The hobby your bunny took up, and how it is going.", Hobby);
            Folk.Tool("folk.jokes", "Inside jokes", Drawers.World, "Lines you said were funny; your bunny brings them back now and then.", Jokes);
            Folk.Tool("folk.anniv", "Bunny remembers", Drawers.Wins, "Your firsts and milestones, and what happened on this day.", Anniversaries);
            Folk.Tool("folk.poem", "Bunny's poems", Drawers.World, "A poem your bunny writes you at the end of each semester.", Poems);
            Folk.Tool("folk.playlist", "Bunny's playlist", Drawers.Rest, "Your bunny picks music for your mood from the sounds you have.", Playlist);

            // Bunny's mood is said once, in the visit's own "how Bunny is" lines (GameFeature.Mood reads MoodNow).
            Folk.Visit("folk.traits", (_, now) => MonthLine(S, now) is string l ? new[] { l } : Array.Empty<string>());
            Folk.Visit("folk.anniv", (_, now) => Today(S, now).Select(a => Folk.Pick(S, "folk.anniv", ("when", a.When), ("what", a.What))).Take(2));
            Folk.Visit("folk.jokes", (_, now) => JokeLine(S, now) is string j ? new[] { j } : Array.Empty<string>());
            Folk.Visit("folk.poem", (_, now) => PoemReady(S, now) && Folk.First(S, "poem.ready." + Folk.SemesterIndex(now))
                ? new[] { S.Data.Game.BunnyName + " wrote you a poem for the end of the semester. It is in Bunny's poems, in the toolbox." } : Array.Empty<string>());
            Folk.Visit("folk.likes", (_, now) => LikedToday(S, now) is string liked && Folk.First(S, "liked." + S.Key(now))
                ? new[] { S.Data.Game.BunnyName + " is still glowing about today: " + liked.ToLowerInvariant() + " is one of its very favorite things. Thank you for that." } : Array.Empty<string>());
            GameEvents.ToolUsed += id => Liked(S, id, DateTime.Now);
            FolkShop.OnPlaceActivity += (place, activity) => Liked(S, place + " " + activity, DateTime.Now);
            Folk.Visit("folk.nap", (_, now) => Folk.RestToday(S, now) && Folk.First(S, "nap." + S.Key(now)) ? new[] { Folk.Pick(S, "folk.nap") } : Array.Empty<string>());

            // Small rules move the traits (sweep 12.8 A). Never in a self-test: these are only raised by real use.
            GameEvents.TaskDone += t =>
            {
                if (!Tools.IsOn("folk.traits")) return;
                Nudge(S, "curious", 0.1, DateTime.Now);
                if (t.Size is Size.Large or Size.MultiDay) Nudge(S, "brave", 0.3, DateTime.Now);
            };
            GameEvents.SprintDone += _ => { if (Tools.IsOn("folk.traits")) Nudge(S, "brave", 0.2, DateTime.Now); };
            GameEvents.ToolUsed += id => { if (Tools.IsOn("folk.traits")) ForActivity(S, id, DateTime.Now); };
            GameEvents.DayEnded += day => { if (Tools.IsOn("folk.traits")) DayDone(S, day, DateTime.Now); };
            GameEvents.WeekStarted += _ => { if (Tools.IsOn("folk.traits")) Drift(S); };
            FolkShop.OnPlaceActivity += (place, activity) => { if (Tools.IsOn("folk.traits")) ForActivity(S, place + " " + activity, DateTime.Now); };
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) { yield return new PersonalityPage(store); }

        // ---- Saying Bunny's lines (and keeping the last one, for Inside jokes) ----

        private sealed class Last { public string Text { get; set; } = ""; }

        /// <summary>Says one of Bunny's lines now and keeps it as the last line, so she can call it funny.</summary>
        public static void Tell(MainWindow w, string text)
        {
            text = text.Trim();
            if (text.Length == 0) return;
            Remember(w.Store, text);
            w.Say(text, true);
        }

        public static void Remember(Store s, string text) { s.World.SetKept("folk.last", new Last { Text = text }); s.World.Save(); }

        /// <summary>The "That was funny" button for windows that show a Bunny line.</summary>
        public static (string, Action<Window>) Funny(Store s, string text) => ("That was _funny", win =>
        {
            Folk.Say(win, AddJoke(s, text) ? "Noted. That one is an inside joke now." : "That one is already an inside joke.");
        });

        // ---- Traits (sweep 12.5, 12.8 A) ----

        public sealed class TraitBox
        {
            public Dictionary<string, double> V { get; set; } = new();
            public Dictionary<string, double> AtMonth { get; set; } = new();
            public string Month { get; set; } = "";
            public string Day { get; set; } = "";
            public Dictionary<string, double> MovedToday { get; set; } = new();
        }

        public static readonly (string Id, string High, string Low, string More, string Less)[] Axes =
        {
            ("brave", "brave", "careful", "a little braver", "a little more careful"),
            ("outdoorsy", "outdoorsy", "cozy", "a bit more outdoorsy", "a bit cozier"),
            ("chatty", "chatty", "quiet", "a little chattier", "a little quieter"),
            ("silly", "silly", "serious", "a bit sillier", "a bit more serious"),
            ("curious", "curious", "settled", "a little more curious", "a little more settled"),
        };

        public const double DailyMove = 1.0, Limit = 10;
        public static TraitBox Traits(Store s) => s.World.GetKept<TraitBox>("folk.traits");

        /// <summary>Moves a trait a little: never more than one step a day each way, and never past the ends.</summary>
        public static void Nudge(Store s, string trait, double by, DateTime now)
        {
            var b = Traits(s);
            if (b.Day != s.Key(now)) { b.Day = s.Key(now); b.MovedToday = new(); }
            double moved = b.MovedToday.GetValueOrDefault(trait);
            double allowed = by > 0 ? Math.Min(by, DailyMove - moved) : Math.Max(by, -DailyMove - moved);
            if (by > 0 ? allowed <= 0 : allowed >= 0) return;
            b.MovedToday[trait] = moved + allowed;
            b.V[trait] = Math.Clamp(b.V.GetValueOrDefault(trait) + allowed, -Limit, Limit);
            s.World.SetKept("folk.traits", b);
            s.World.Save();
        }

        private static readonly string[] OutdoorWords = { "pond", "trail", "atv", "garden", "stable", "cove", "beach", "meadow", "hill", "hills", "woods", "fish", "ride", "swim", "dive", "sled", "boat", "kite", "pool", "creek", "star", "stars", "orchard", "hike", "balloon", "waterfall" };
        private static readonly string[] CozyWords = { "nook", "fireplace", "bed", "bedroom", "read", "journal", "nap", "tea", "teahouse", "kitchen", "bake", "bakery", "cocoa", "spa", "tub", "hottub", "quilt", "library", "guest", "sleepover", "fireside" };
        /// <summary>Reading the neighbors' stories and letters settles Bunny a little (curious toward settled).</summary>
        private static readonly string[] StoryTools = { "folk.mail", "folk.penpal", "folk.mysterious", "folk.neighbors", "folk.fireside", "folk.sleepover" };

        /// <summary>The whole words of a tool id or place activity ("place.stars fish" gives place, stars, fish), so "startstreak" is not "star".</summary>
        public static HashSet<string> Words(string what) => what.ToLowerInvariant().Split(new[] { '.', ' ', ',', '_', '-' }, StringSplitOptions.RemoveEmptyEntries).ToHashSet();

        /// <summary>What she did with Bunny shapes it: trips outside make it outdoorsy, reading nook time makes it cozier.</summary>
        private sealed class LikedDay { public string Day { get; set; } = ""; public string What { get; set; } = ""; }

        /// <summary>Doing something Bunny likes (its favorite room or activity) makes it happier: kept for its mood and a thank-you in Visit your bunny.</summary>
        public static void Liked(Store s, string what, DateTime now)
        {
            if (!Tools.IsOn("folk.likes")) return;
            what = what.ToLowerInvariant();
            foreach (var (kind, item) in LikesOf(s))
            {
                if (kind is not ("room" or "activity" or "food")) continue;
                string value = item.Get("value");
                if (!value.Split(' ').Any(word => word.Length >= 4 && what.Contains(word.ToLowerInvariant()))) continue;
                s.World.SetKept("folk.likedtoday", new LikedDay { Day = s.Key(now), What = value });
                s.World.Save();
                return;
            }
        }

        public static string? LikedToday(Store s, DateTime now) => s.World.GetKept<LikedDay>("folk.likedtoday") is { } l && l.Day == s.Key(now) ? l.What : null;

        public static void ForActivity(Store s, string what, DateTime now)
        {
            what = what.ToLowerInvariant();
            var words = Words(what);
            if (OutdoorWords.Any(words.Contains)) Nudge(s, "outdoorsy", 0.3, now);
            else if (CozyWords.Any(words.Contains)) Nudge(s, "outdoorsy", -0.3, now);
            if (what.StartsWith("folk.mood") || what.StartsWith("folk.doing") || what.StartsWith("folk.opinions") || what.StartsWith("folk.dream")) Nudge(s, "chatty", 0.2, now);
            if (words.Contains("adventure") || words.Contains("mystery") || words.Contains("treasure")) Nudge(s, "curious", 0.3, now);
            if (StoryTools.Any(t => what.StartsWith(t))) Nudge(s, "curious", -0.2, now);
        }

        /// <summary>
        /// The end of a day moves Bunny back the other way too: a rough day makes it more careful, quieter and cozier; a
        /// vacation day (Bunny travels on those) more outdoorsy; a day with no chatting tools a little quieter.
        /// </summary>
        public static void DayDone(Store s, DateTime day, DateTime now)
        {
            var b = Traits(s);
            bool chatted = b.Day == s.Key(day) && b.MovedToday.GetValueOrDefault("chatty") > 0;
            var mode = s.Data.PeekDay(day).Mode;
            if (mode == DayMode.RoughDay) { Nudge(s, "outdoorsy", -0.4, now); Nudge(s, "brave", -0.3, now); Nudge(s, "chatty", -0.3, now); }
            else if (mode == DayMode.Vacation) Nudge(s, "outdoorsy", 0.4, now);
            if (!chatted && mode != DayMode.RoughDay) Nudge(s, "chatty", -0.2, now);
        }

        /// <summary>Once a week every trait drifts half a step back toward the middle, so no Bunny ends up at the same ends as every other.</summary>
        public static void Drift(Store s)
        {
            var b = Traits(s);
            foreach (var a in Axes)
            {
                double v = b.V.GetValueOrDefault(a.Id);
                b.V[a.Id] = v > 0 ? Math.Max(0, v - 0.5) : Math.Min(0, v + 0.5);
            }
            s.World.SetKept("folk.traits", b);
            s.World.Save();
        }

        public static string Word(string id, double v)
        {
            var a = Axes.First(x => x.Id == id);
            return v >= 6 ? "very " + a.High : v >= 2 ? a.High : v <= -6 ? "very " + a.Low : v <= -2 ? a.Low : "a bit " + a.High + " and a bit " + a.Low;
        }

        public static List<string> TraitLines(Store s)
        {
            var b = Traits(s);
            return Axes.Select(a => Folk.End(char.ToUpper(a.High[0]) + a.High[1..] + " or " + a.Low + ": " + Word(a.Id, b.V.GetValueOrDefault(a.Id)))).ToList();
        }

        /// <summary>Once a month, how Bunny has changed since the month before (sweep 12.5). Null when it is not time yet.</summary>
        public static string? MonthLine(Store s, DateTime now)
        {
            var b = Traits(s);
            string month = Folk.MonthKey(now);
            if (b.Month == month) return null;
            bool first = b.Month.Length == 0;
            var before = b.AtMonth;
            b.Month = month;
            b.AtMonth = new Dictionary<string, double>(b.V);
            s.World.SetKept("folk.traits", b);
            s.World.Save();
            if (first) return null;
            var biggest = Axes.Select(a => (a, d: b.V.GetValueOrDefault(a.Id) - before.GetValueOrDefault(a.Id))).OrderByDescending(x => Math.Abs(x.d)).First();
            if (Math.Abs(biggest.d) < 0.5) return s.Data.Game.BunnyName + " says: I feel about the same as last month, which I think is exactly the right amount of me.";
            return Folk.Pick(s, "folk.traits.month", ("change", biggest.d > 0 ? biggest.a.More : biggest.a.Less));
        }

        private void TraitsTool()
        {
            var lines = TraitLines(S);
            lines.Insert(0, "Who " + S.Data.Game.BunnyName + " is becoming. It changes slowly with what you do together: trips outside make it outdoorsy, reading nook time makes it cozier.");
            lines.Add("Once a month it tells you in Visit your bunny how it has changed. Options, Settings, Bunny's personality resets it.");
            TextViewer.Show(_w, S.Data.Game.BunnyName + "'s personality", lines);
        }

        // ---- Likes (sweep 12.5) ----

        public static readonly string[] LikeKinds = { "food", "color", "song", "room", "season", "neighbor", "weather", "activity" };

        /// <summary>Bunny's favorites, one per kind, and two dislikes: chosen once, then kept.</summary>
        public static Dictionary<string, Dictionary<string, string>> LikesOf(Store s)
        {
            var chosen = s.World.GetKept<Dictionary<string, int>>("folk.likes");
            var all = Folk.Items("folk.likes");
            bool changed = false;
            foreach (var kind in LikeKinds.Append("dislike").Append("dislike2"))
            {
                if (chosen.ContainsKey(kind)) continue;
                string k = kind == "dislike2" ? "dislike" : kind;
                var options = Enumerable.Range(0, all.Count).Where(i => all[i].Get("kind") == k && !chosen.ContainsValue(i)).ToList();
                if (options.Count == 0) continue;
                chosen[kind] = options[Folk.Rng.Next(options.Count)];
                changed = true;
            }
            if (changed) { s.World.SetKept("folk.likes", chosen); s.World.Save(); }
            return chosen.Where(c => c.Value >= 0 && c.Value < all.Count).ToDictionary(c => c.Key, c => all[c.Value]);
        }

        /// <summary>Whether a thing's words name one of Bunny's favorites (its food, room, color and so on).</summary>
        public static bool Likes(Store s, string text) => LikesOf(s).Where(l => !l.Key.StartsWith("dislike"))
            .Any(l => l.Value.Get("value").Length > 2 && text.Contains(l.Value.Get("value"), StringComparison.OrdinalIgnoreCase));

        private void Likes()
        {
            var likes = LikesOf(S);
            var lines = new List<string>();
            foreach (var (kind, item) in likes.OrderBy(l => l.Key.StartsWith("dislike")))
                lines.Add((kind.StartsWith("dislike") ? "Would rather avoid: " : "Favorite " + kind + ": ") + item.Get("value") + ". " + Folk.End(item.Get("line")));
            lines.Add("Getting it something it likes makes it extra happy.");
            TextViewer.Show(_w, S.Data.Game.BunnyName + "'s likes", lines);
        }

        // ---- Mood with a reason (sweep 12.6 item 1) ----

        public static (string Mood, string Reason) MoodNow(Store s, DateTime now)
        {
            var d = s.Data;
            var day = d.PeekDay(now);
            var when = new List<string>();
            if (day.Mode == DayMode.RoughDay) when.Add("rough");
            if (day.Mode == DayMode.Vacation) when.Add("vacation");
            string? festival = Folk.Festival(now);
            if (festival != null) when.Add("festival");
            if (FolkWish.GrantedLately(s, now) || LikedToday(s, now) != null) when.Add("wish");
            if (FolkVillage.NewChapterThisWeek(s, now)) when.Add("newfriend");
            if (Engine.PointsOn(d, now) >= Engine.DailyGoalFor(s, now) && Engine.PointsOn(d, now) > 0) when.Add("goal");
            if (Engine.PointsInWeek(d, now) >= Engine.WeeklyGoalFor(d)) when.Add("bigweek");
            if (Folk.FinishedBetween(s, now.Date, now.Date.AddDays(1)) > 0) when.Add("busy");
            if (now.Hour >= 21 || now.Hour < 5) when.Add("night");
            if (now.Hour >= 5 && now.Hour < 11) when.Add("morning");
            when.Add(RealWorld.Season(now).ToLowerInvariant());
            when.Add("quiet");
            var items = Folk.Items("folk.mood");
            foreach (var w in when)
            {
                var these = items.Where(i => i.Get("when") == w).ToList();
                if (these.Count == 0) continue;
                var pick = these[Folk.Mod(Folk.Hash(s.Key(now) + w), these.Count)];
                return (pick.Get("mood"), Folk.Fill(s, pick.Get("reason"), ("holiday", festival ?? "the festival")));
            }
            return ("content", "I just am.");
        }

        private void MoodTool()
        {
            var (m, r) = MoodNow(S, DateTime.Now);
            Tell(_w, S.Data.Game.BunnyName + " feels " + m + ". Why: " + Folk.End(r));
        }

        // ---- What is Bunny doing, naps, dreams ----

        public static string DoingLine(Store s, DateTime now)
        {
            if (Folk.RestToday(s, now)) return Folk.Pick(s, "folk.nap");
            string line = Folk.Pick(s, now.Hour >= 6 && now.Hour < 20 ? "folk.doing.day" : "folk.doing.night");
            string outfit = Wearing(s);
            return outfit.Length > 0 && Tools.IsOn("folk.outfits") ? line + " It is wearing " + outfit.ToLowerInvariant() + "." : line;
        }

        private void Doing() => Tell(_w, DoingLine(S, DateTime.Now));

        private void Nap()
        {
            SoundBank.Play("Soft piano, sigh");
            Tell(_w, Folk.Pick(S, "folk.nap") + " Stay as long as you like. Nothing needs you right now.");
        }

        private sealed class DreamBox { public string Day { get; set; } = ""; public string Text { get; set; } = ""; }

        private async void Dream()
        {
            var now = DateTime.Now;
            var box = S.World.GetKept<DreamBox>("folk.dream");
            if (box.Day != S.Key(now))
            {
                if (WillWrite(S, "folk.dream")) _w.Say(S.Data.Game.BunnyName + " is remembering its dream.", true);
                string text = await Folk.Write(S, "folk.dream", "Write the strange, sweet, silly dream Bunny had last night, in Bunny's own first-person voice, 2 or 3 sentences, cozy and never scary.",
                    () => Folk.Pick(S, "folk.dream"));
                box = new DreamBox { Day = S.Key(now), Text = text };
                S.World.SetKept("folk.dream", box);
                S.World.Save();
            }
            Remember(S, box.Text);
            Folk.Read(_w, S.Data.Game.BunnyName + "'s dream" + (now.Hour >= 12 ? " from last night" : ""), box.Text, Funny(S, box.Text));
        }

        // ---- Outfits (sweep 8.7) ----

        private sealed class Wear { public string Name { get; set; } = ""; }

        public static string Wearing(Store s) => s.World.Get<Wear>("folk.outfit").Name;

        public static List<Dictionary<string, string>> OutfitsOpen(Store s, DateTime now)
        {
            int level = Engine.LevelFor(Engine.Total(s.Data));
            string season = RealWorld.Season(now);
            return Folk.Items("folk.outfits").Where(o => int.TryParse(o.Get("unlock"), out int l) ? l <= level : o.Get("unlock") == season).ToList();
        }

        private void Outfits()
        {
            Folk.List(_w, S.Data.Game.BunnyName + "'s outfits", () =>
            {
                var now = DateTime.Now;
                int level = Engine.LevelFor(Engine.Total(S.Data));
                var rows = new List<Folk.Row>();
                string wearing = Wearing(S);
                rows.Add(new Folk.Row(wearing.Length > 0 ? "Wearing now: " + wearing + ". Enter takes it off." : "Wearing nothing special, just fur. " + S.Data.Game.BunnyName + " is level " + level + ": " + Engine.BunnyStage(level) + ".",
                    wearing.Length == 0 ? null : win => { World.Change<Wear>(S, "folk.outfit", "taking off Bunny's outfit", x => x.Name = ""); Folk.Say(win, "Back to plain fur."); }));
                foreach (var o in OutfitsOpen(S, now))
                {
                    string name = o.Get("name");
                    rows.Add(new Folk.Row(name + ": " + Folk.Describe(o), win =>
                    {
                        World.Change<Wear>(S, "folk.outfit", "dressing Bunny", x => x.Name = name);
                        SoundBank.Play("Bunny, binky");
                        Folk.Say(win, S.Data.Game.BunnyName + " is wearing " + name.ToLowerInvariant() + " now, and doing a little twirl.");
                    }));
                }
                var next = Folk.Items("folk.outfits").Select(o => int.TryParse(o.Get("unlock"), out int l) ? l : 0).Where(l => l > level).DefaultIfEmpty(0).Min();
                if (next > 0) rows.Add(new Folk.Row("The next outfit unlocks at level " + next + ". Seasonal outfits come with each season."));
                return rows;
            }, "Enter puts the outfit on. Escape closes.");
        }

        // ---- The journal (sweep 8.7) ----

        private void Journal() => JournalAsync();

        private async void JournalAsync()
        {
            var now = DateTime.Now;
            var written = S.World.GetKept<Dictionary<string, string>>("folk.journal");
            string today = S.Key(now);
            if (!written.ContainsKey(today) && SmartBunny.Ready && !Calm.FunPaused)
            {
                var wins = Folk.VisibleWins(S, now.Date);
                if (WillWrite(S, "folk.journal")) _w.Say(S.Data.Game.BunnyName + " is writing in its journal.", true);
                string text = await Folk.Write(S, "folk.journal", "Write Bunny's diary entry about today, 2 to 4 sentences, from Bunny's point of view, warm and a little funny, never disappointed. "
                    + (wins.Count == 0 ? "Nothing got finished today, and that is fine: write about resting." : "Today she finished " + wins.Count + " tasks, including " + string.Join(", ", wins.Take(3)) + "."), () => "");
                if (text.Length > 0) { written[today] = text; S.World.SetKept("folk.journal", written); S.World.Save(); }
            }
            Folk.List(_w, S.Data.Game.BunnyName + "'s journal", () =>
            {
                var rows = new List<Folk.Row>();
                foreach (var (day, text) in JournalEntries(S, DateTime.Now, written))
                {
                    string line = Folk.Day(day) + ". " + text;
                    rows.Add(new Folk.Row(line, win => { Remember(S, text); Folk.Read(win, "Journal, " + Folk.Day(day), text, Funny(S, text)); }));
                }
                return rows;
            }, "Newest first. Enter opens an entry to read.");
        }

        /// <summary>Entries for today and each of the last 30 days with something in them (a finished task, a rough day), newest first.</summary>
        public static List<(DateTime Day, string Text)> JournalEntries(Store s, DateTime now, Dictionary<string, string>? written = null)
        {
            written ??= s.World.GetKept<Dictionary<string, string>>("folk.journal");
            var done = Folk.Finished(s);
            var list = new List<(DateTime, string)>();
            for (int i = 0; i <= 30; i++)
            {
                var day = now.Date.AddDays(-i);
                int n = done.Count(x => x.When.Date == day);
                bool rough = s.Data.PeekDay(day).Mode == DayMode.RoughDay;
                if (i > 0 && n == 0 && !rough) continue;
                if (written.TryGetValue(s.Key(day), out var text)) { list.Add((day, text)); continue; }
                string topic = rough ? "folk.journal.rough" : n > 0 ? "folk.journal.busy" : "folk.journal.quiet";
                list.Add((day, Folk.Nth(s, topic, Folk.Hash(s.Key(day)), ("count", n.ToString()))));
            }
            return list;
        }

        // ---- Opinions (sweep 12.6 item 4) ----

        private void Opinions()
        {
            var w = Layout.Dialog(_w, S.Data.Game.BunnyName + "'s hot takes");
            var back = _w.KeepFocus();
            var box = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, Width = 560, MinHeight = 80 };
            AutomationProperties.SetName(box, "Bunny's hot take");
            string take = FavoriteClass(S, DateTime.Now) ?? Folk.Pick(S, "folk.hottakes");
            void Show(string text) { box.Text = text; Remember(S, text); Speech.Say(w, text, true); }
            box.Text = take;
            Remember(S, take);
            var agree = Layout.Button("_Agree");
            agree.Click += (_, _) => { if (Tools.IsOn("folk.traits")) Nudge(S, "silly", 0.2, DateTime.Now); Show(Folk.Pick(S, "folk.agree")); };
            var argue = Layout.Button("_Disagree");
            argue.Click += (_, _) =>
            {
                if (Tools.IsOn("folk.traits")) { Nudge(S, "brave", 0.1, DateTime.Now); Nudge(S, "silly", -0.2, DateTime.Now); }
                Show(Folk.Pick(S, "folk.argue"));
            };
            var another = Layout.Button("A_nother");
            another.Click += (_, _) => Show(Folk.Pick(S, "folk.hottakes"));
            var funny = Layout.Button("That was _funny");
            funny.Click += (_, _) => Speech.Say(w, AddJoke(S, box.Text) ? "Noted. That one is an inside joke now." : "That one is already an inside joke.", true);
            var close = Layout.Button("_Close", isCancel: true);
            close.Click += (_, _) => w.Close();
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            foreach (var b in new[] { agree, argue, another, funny, close }) row.Children.Add(b);
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(box);
            panel.Children.Add(row);
            w.Content = panel;
            w.Loaded += (_, _) => box.Focus();
            w.ShowDialog();
            back();
        }

        /// <summary>Bunny's favorite class: the tag she finished most this semester (worked out here, never sent anywhere).</summary>
        public static string? FavoriteClass(Store s, DateTime now)
        {
            var (_, from, _) = GameFeature.Semester(now);
            var top = s.Data.Log.Where(e => e.Kind == "Done" && e.Tag.Length > 0 && e.When >= from)
                .GroupBy(e => e.Tag, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (top == null || Folk.Rng.Next(3) > 0) return null;
            return "Hot take: " + top.Key + " is the best class. You keep finishing its work, so clearly it is the good one. I will hear no arguments. Well, maybe one.";
        }

        // ---- Hobby (sweep 12.6 item 5) ----

        /// <summary>Bunny's hobby now (a new one every eight weeks) and how good it has got, from the tasks finished since it started.</summary>
        public static (string Hobby, int Stage, string Line) HobbyNow(Store s, DateTime now)
        {
            var all = Folk.Items("folk.hobbies");
            if (all.Count == 0) return ("", 0, "");
            int weeks = Folk.Weeks(s, now);
            var item = all[Folk.Mod(weeks / 8, all.Count)];
            var since = Engine.WeekStart(Folk.Start(s)).AddDays(weeks / 8 * 56);
            int stage = Math.Clamp(1 + Folk.FinishedSince(s, since) / 8, 1, 5);
            string line = (weeks % 8 == 0 ? Folk.End(item.Get("start")) + " " : "") + Folk.End(item.Get("stage" + stage));
            return (item.Get("hobby"), stage, Folk.Fill(s, line));
        }

        private void Hobby()
        {
            var (hobby, stage, line) = HobbyNow(S, DateTime.Now);
            if (hobby.Length == 0) { _w.Say("No hobby yet.", true); return; }
            Tell(_w, S.Data.Game.BunnyName + "'s hobby: " + hobby + ", skill " + stage + " of 5. " + line + (stage < 5 ? " Every task you finish is practice time for it, somehow." : ""));
        }

        // ---- Inside jokes (sweep 12.6 item 3) ----

        public static List<string> JokesOf(Store s) => s.World.GetKept<List<string>>("folk.jokes");

        public static bool AddJoke(Store s, string text)
        {
            text = text.Trim();
            var list = JokesOf(s);
            if (text.Length == 0 || list.Contains(text)) return false;
            list.Add(text);
            if (list.Count > 50) list.RemoveAt(0);
            s.World.SetKept("folk.jokes", list);
            s.World.Save();
            if (Tools.IsOn("folk.traits")) Nudge(s, "silly", 0.5, DateTime.Now);
            return true;
        }

        public static void DropJoke(Store s, string text)
        {
            var list = JokesOf(s);
            if (!list.Remove(text)) return;
            s.World.SetKept("folk.jokes", list);
            s.World.Save();
        }

        /// <summary>Once a week, Bunny brings back one of her inside jokes.</summary>
        public static string? JokeLine(Store s, DateTime now)
        {
            var jokes = JokesOf(s);
            if (jokes.Count == 0 || !Folk.First(s, "joke." + Folk.WeekKey(now))) return null;
            return Folk.Pick(s, "folk.jokes.callback", ("joke", jokes[Folk.Rng.Next(jokes.Count)]));
        }

        private void Jokes()
        {
            Folk.List(_w, "Inside jokes", () =>
            {
                var rows = new List<Folk.Row>();
                // The same last line Rate a line uses (the library line she actually heard), then Bunny's last written line if that was something else.
                string heard = RecordsStyle.Heard?.Said.Trim() ?? "";
                if (heard.Length > 0 && !JokesOf(S).Contains(heard))
                    rows.Add(new Folk.Row("Call the last line funny: " + heard, win => { AddJoke(S, heard); Folk.Say(win, "Noted. That one is an inside joke now."); }));
                string last = S.World.GetKept<Last>("folk.last").Text;
                if (last.Length > 0 && last != heard && !JokesOf(S).Contains(last))
                    rows.Add(new Folk.Row("Call " + S.Data.Game.BunnyName + "'s last written line funny: " + last, win => { AddJoke(S, last); Folk.Say(win, "Noted. That one is an inside joke now."); }));
                foreach (var j in Enumerable.Reverse(JokesOf(S)))
                    rows.Add(new Folk.Row(j, win => { DropJoke(S, j); Folk.Say(win, "Okay, " + S.Data.Game.BunnyName + " will retire that one."); }));
                if (rows.Count == 0) rows.Add(new Folk.Row("No inside jokes yet. When a Bunny line makes you laugh, press That was funny, or come here and call the last line funny."));
                return rows;
            }, "Enter on a joke retires it. Enter on the first line keeps the last thing Bunny said as a joke.");
        }

        // ---- Anniversaries (sweep 12.6 item 2) ----

        /// <summary>Its anniversary is the seasons calendar's village anniversary, so it is listed but not said again here.</summary>
        public const string CameToVillage = "you came to the village";

        public static List<(DateTime When, string What)> Milestones(Store s)
        {
            var d = s.Data;
            var list = new List<(DateTime, string)> { (Folk.Start(s), CameToVillage) };
            var done = Folk.Finished(s);
            if (done.Count > 0) list.Add((done[0].When.Date, "you finished your first task here"));
            if (done.Count >= 100) list.Add((done[99].When.Date, "you finished your hundredth task"));
            if (d.Log.Where(e => e.Kind == "Boss").OrderBy(e => e.When).FirstOrDefault() is LogEntry boss) list.Add((boss.When.Date, "you beat your first boss"));
            foreach (var (id, when) in d.Game.Badges)
                if (Badges.All.FirstOrDefault(b => b.Id == id) is Badge b) list.Add((when.Date, "you earned the " + b.Name + " badge"));
            // Levels 5, 10, 15 and so on: the day the running total first reached them.
            int total = 0, next = 5;
            foreach (var e in d.Log.Where(e => Engine.KarmaKinds.Contains(e.Kind)).OrderBy(e => e.When))
            {
                total += e.Points;
                while (Engine.LevelFor(total) >= next) { list.Add((e.When.Date, "you reached level " + next)); next += 5; }
            }
            return list.OrderByDescending(x => x.Item1).ToList();
        }

        /// <summary>Milestones that have an anniversary today: a year (or more) ago, or a month ago for the firsts.</summary>
        public static List<(string When, string What)> Today(Store s, DateTime now)
        {
            var list = new List<(string, string)>();
            foreach (var (when, what) in Milestones(s).Where(m => m.What != CameToVillage))
            {
                if (when.Month == now.Month && when.Day == now.Day && when.Year < now.Year)
                {
                    int years = now.Year - when.Year;
                    list.Add((years == 1 ? "a year ago today" : years + " years ago today", what));
                }
                else if (when == now.Date.AddMonths(-1) && !what.Contains("badge")) list.Add(("a month ago today", what));
            }
            return list;
        }

        private void Anniversaries()
        {
            var now = DateTime.Now;
            var lines = Today(S, now).Select(a => "Today: " + a.When + ", " + a.What + ".").ToList();
            lines.AddRange(Milestones(S).Select(m => Folk.Day(m.When) + ", " + m.When.Year + ": " + m.What + "."));
            TextViewer.Show(_w, S.Data.Game.BunnyName + " remembers", lines);
        }

        // ---- A poem at semester end (sweep 13.3 item 14) ----

        public static bool PoemReady(Store s, DateTime now) => now.Date >= GameFeature.Semester(now).To.AddDays(-21);

        private async void Poems()
        {
            var now = DateTime.Now;
            var poems = S.World.GetKept<Dictionary<string, string>>("folk.poems");
            var (name, from, to) = GameFeature.Semester(now);
            if (PoemReady(S, now) && !poems.ContainsKey(name))
            {
                // Only tasks Bunny may see count: nothing from a private list reaches Claude, not even a number.
                int n = VisibleFinished(S, from, to.AddDays(1));
                if (WillWrite(S, "folk.poem")) _w.Say(S.Data.Game.BunnyName + " is writing you a poem.", true);
                string poem = await Folk.Write(S, "folk.poem", "Write her an original short poem for the end of her " + name + " semester, 6 to 10 lines, one poem line per text line, warm and proud, touch and sound images, no classes or grades named. She finished " + n + " tasks this semester.",
                    () => Folk.Fill(S, Folk.NthBlock("folk.poems", Folk.SemesterIndex(now))?.Body ?? ""));
                poems[name] = poem;
                S.World.SetKept("folk.poems", poems);
                S.World.Save();
            }
            Folk.List(_w, S.Data.Game.BunnyName + "'s poems", () =>
            {
                var rows = poems.Reverse().Select(p => new Folk.Row("A poem for " + p.Key, win => Folk.Read(win, "A poem for " + p.Key, p.Value, Funny(S, p.Value)))).ToList();
                if (!poems.ContainsKey(name)) rows.Insert(0, new Folk.Row(S.Data.Game.BunnyName + " is saving up words for your " + name + " poem. It arrives around " + to.AddDays(-21).ToString("MMMM d") + "."));
                return rows;
            }, "Enter opens a poem to read.");
        }

        // ---- Bunny's playlist (sweep 13.3 item 15) ----

        public static readonly string[] Moods = { "cozy", "focused", "sleepy", "bouncy", "brave", "gentle" };

        private void Playlist()
        {
            var (feel, _) = MoodNow(S, DateTime.Now);
            var options = Moods.Select(m => "Music for feeling " + m).Prepend("Let " + S.Data.Game.BunnyName + " pick for its own mood, " + feel).ToList();
            int pick = Folk.Choose(_w, S.Data.Game.BunnyName + "'s playlist", "How are you feeling? " + S.Data.Game.BunnyName + " will pick the music.", options);
            if (pick < 0) return;
            string mood = pick == 0 ? Moods[Folk.Mod(Folk.Hash(feel), Moods.Length)] : Moods[pick - 1];
            var tracks = Folk.Items("folk.playlist").Where(t => t.Get("mood") == mood && SoundBank.Has(t.Get("sound"))).ToList();
            var owned = FolkShop.OwnedInstruments(S);
            Folk.List(_w, "Playlist for feeling " + mood, () =>
            {
                var rows = tracks.Select(t => new Folk.Row(t.Get("title") + ": " + t.Get("line"), win => { SoundBank.Play(t.Get("sound")); Folk.Say(win, t.Get("title") + "."); })).ToList();
                foreach (var (iname, sound) in owned)
                    rows.Add(new Folk.Row("Your " + iname + ", which " + S.Data.Game.BunnyName + " insists on adding", win => { if (sound.Length > 0) SoundBank.Play(sound); Folk.Say(win, "Your " + iname + "."); }));
                return rows;
            }, "Enter plays a track. Escape closes.");
        }

        // ---- For Smart Bunny (sweep 12.8 D) ----

        /// <summary>Whether Folk.Write will ask Claude now (so she hears that Bunny is writing instead of a silent wait).</summary>
        public static bool WillWrite(Store s, string featureId) => SmartBunny.Ready && !Calm.FunPaused && BunnyWrite.MayAsk(s, featureId, DateTime.Now);

        /// <summary>Tasks she finished between two times that Bunny may see (never private or archived ones), for counts in a prompt.</summary>
        public static int VisibleFinished(Store s, DateTime from, DateTime to)
        {
            var visible = BunnyWrite.Visible(s).Select(t => t.Id).ToHashSet();
            return Folk.Finished(s).Count(x => x.When >= from && x.When < to && visible.Contains(x.Id));
        }

        /// <summary>Who Bunny is now, for any prompt that has Claude write as Bunny: traits, likes, mood, her notes.</summary>
        public static string Persona(Store s, DateTime now)
        {
            var b = Traits(s);
            var traits = string.Join(", ", Axes.Select(a => Word(a.Id, b.V.GetValueOrDefault(a.Id))));
            var likes = LikesOf(s);
            string fav = string.Join("; ", likes.Where(l => !l.Key.StartsWith("dislike")).Select(l => l.Key + ": " + l.Value.Get("value")));
            string dis = string.Join(", ", likes.Where(l => l.Key.StartsWith("dislike")).Select(l => l.Value.Get("value")));
            var (mood, reason) = MoodNow(s, now);
            var notes = s.Data.BunnyNotes.TakeLast(8).ToList();
            return "You are writing as Bunny, her companion bunny, named " + s.Data.Game.BunnyName + ". Bunny's personality now: " + traits + ". Favorites: " + fav + ". Dislikes: " + dis
                + ". Mood today: " + mood + ", because " + reason + (notes.Count > 0 ? " Her notes for Bunny: " + string.Join(" ", notes) : "")
                + " Bunny is always warm, never disappointed in her, never cold, never sad or scary. Touch and sound first in descriptions.";
        }

        // ---- Settings: Bunny's personality and friends ----

        private sealed class PersonalityPage : ISettingsPage
        {
            private readonly Store _store;
            private readonly StackPanel _panel = new() { Margin = new Thickness(8) };
            private readonly CheckBox _reset = new() { Content = "_Reset Bunny's personality to how it started" };
            private readonly TextBox _goldie = new(), _partner = new();

            public PersonalityPage(Store store)
            {
                _store = store;
                var traits = new TextBox { IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 110, Text = string.Join("\r\n", TraitLines(store)) };
                _panel.Children.Add(Layout.Row("Bunny's _personality now", traits, 420));
                AutomationProperties.SetName(_reset, "Reset Bunny's personality to how it started");
                _panel.Children.Add(_reset);
                var names = Folk.NamesOf(store);
                _goldie.Text = Folk.Goldie(store);
                _panel.Children.Add(Layout.Row("The golden retriever neighbor's _name", _goldie));
                _partner.Text = names.Partner;
                _partner.IsEnabled = names.Partner.Length > 0;
                _panel.Children.Add(Layout.Row("Your bunny's partner's n_ame (once they have met)", _partner));
            }

            public string Name => "Bunny's personality";
            public FrameworkElement Content => _panel;

            public void Apply()
            {
                var n = Folk.NamesOf(_store);
                string g = Folk.Clean(_goldie.Text);
                n.Goldie = g.Length > 0 ? g : "Goldie";
                if (n.Partner.Length > 0 && Folk.Clean(_partner.Text).Length > 0) n.Partner = Folk.Clean(_partner.Text);
                _store.World.Set("folk.names", n);
                if (_reset.IsChecked == true) _store.World.SetKept("folk.traits", new TraitBox { Month = Folk.MonthKey(DateTime.Now) });
                _store.World.Save();
                FolkVillage.RenameGoldie(_store);
            }
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Folk, Bunny: " + what); }
            Folk.CheckContent(Need);
            foreach (var (topic, n) in new[] { ("folk.doing.day", 20), ("folk.doing.night", 20), ("folk.nap", 20), ("folk.dream", 20), ("folk.journal.busy", 20), ("folk.journal.quiet", 20),
                ("folk.journal.rough", 20), ("folk.thanks", 20), ("folk.surprise", 20), ("folk.traits.month", 20), ("folk.hottakes", 20), ("folk.argue", 20), ("folk.agree", 20),
                ("folk.jokes.callback", 20), ("folk.anniv", 20) })
                Need(Content.Lines(topic).Count >= n, topic + " has fewer than " + n + " lines");
            Need(Folk.Items("folk.mood").Count >= 20 && Folk.Items("folk.outfits").Count >= 20 && Folk.Items("folk.likes").Count >= 20 && Folk.Items("folk.playlist").Count >= 20, "mood, outfit, likes or playlist items");
            Need(Folk.Blocks("folk.poems").Count >= 20 && Folk.Blocks("folk.poems").All(p => p.Body.Contains('\n')), "poems keep their lines");
            Need(Folk.Items("folk.playlist").All(t => SoundBank.Has(t.Get("sound"))), "a playlist sound does not exist");
            foreach (var s in new[] { "Soft piano, sigh", "Bunny, binky", "Singing bells, sparkle" }) Need(SoundBank.Has(s), "no sound " + s);

            // Traits move slowly: at most one step a day, and never past the ends.
            var now = new DateTime(2026, 11, 4, 10, 0, 0);
            store.World.SetKept("folk.traits", new TraitBox());
            for (int i = 0; i < 20; i++) Nudge(store, "brave", 0.3, now);
            Need(Math.Abs(Traits(store).V["brave"] - DailyMove) < 1e-9, "a trait moved more than a step in a day");
            for (int i = 0; i < 400; i++) Nudge(store, "brave", 1, now.AddDays(i));
            Need(Traits(store).V["brave"] <= Limit, "a trait went past its end");
            ForActivity(store, "place.pond fish", now.AddDays(500));
            Need(Traits(store).V.GetValueOrDefault("outdoorsy") > 0, "a pond trip did not make Bunny outdoorsy");
            Need(Word("brave", 7) == "very brave" && Word("outdoorsy", -3) == "cozy", "trait words");
            // Whole words only: the streak tool is not a star, the fireflies are not a fire, bread is not a bed.
            store.World.SetKept("folk.traits", new TraitBox());
            foreach (var id in new[] { "records.startstreak", "place.fireflies", "steam bread", "team snap" }) ForActivity(store, id, now.AddDays(600));
            Need(Math.Abs(Traits(store).V.GetValueOrDefault("outdoorsy")) < 1e-9, "a substring moved Bunny outdoorsy or cozy");
            // Traits move back the other way too: rough days, quiet days, reading stories, and a weekly drift to the middle.
            store.World.SetKept("folk.traits", new TraitBox { V = new() { ["brave"] = 5, ["chatty"] = 5, ["curious"] = 5, ["outdoorsy"] = -4 } });
            var roughDay = now.AddDays(700);
            store.Data.Day(roughDay).Mode = DayMode.RoughDay;
            DayDone(store, roughDay, roughDay.AddDays(1));
            store.Data.Day(roughDay).Mode = DayMode.Normal;
            Need(Traits(store).V["brave"] < 5 && Traits(store).V["chatty"] < 5, "a rough day never made Bunny more careful or quieter");
            ForActivity(store, "folk.mail", roughDay.AddDays(2));
            Need(Traits(store).V["curious"] < 5, "reading letters never settled Bunny");
            var vac = now.AddDays(710);
            store.Data.Day(vac).Mode = DayMode.Vacation;
            double before = Traits(store).V["outdoorsy"];
            DayDone(store, vac, vac.AddDays(1));
            store.Data.Day(vac).Mode = DayMode.Normal;
            Need(Traits(store).V["outdoorsy"] > before, "a vacation day made Bunny cozier, but Bunny travels then");
            double c = Traits(store).V["curious"];
            Drift(store);
            Need(Traits(store).V["curious"] < c && Traits(store).V["outdoorsy"] > before, "the weekly drift toward the middle");
            // The monthly line: nothing the first month, then the biggest change.
            store.World.SetKept("folk.traits", new TraitBox());
            Need(MonthLine(store, now) == null, "a monthly line in the very first month");
            Nudge(store, "silly", 0.9, now);
            string? month = MonthLine(store, now.AddMonths(1));
            Need(month != null && month.Contains("sillier") && MonthLine(store, now.AddMonths(1)) == null, "the monthly change line");

            // Likes: one per kind plus two dislikes, kept the same.
            var likes = LikesOf(store);
            Need(LikeKinds.All(likes.ContainsKey) && likes.ContainsKey("dislike") && likes.ContainsKey("dislike2"), "likes for every kind");
            Need(LikesOf(store)["food"].Get("value") == likes["food"].Get("value"), "likes changed between calls");
            Need(Persona(store, now).Contains(likes["food"].Get("value")), "Smart Bunny's prompt has no likes");
            string roomWord = likes["room"].Get("value").Split(' ').OrderByDescending(x => x.Length).First();
            Liked(store, "place.room." + roomWord, now);
            Need(LikedToday(store, now) != null && LikedToday(store, now.AddDays(1)) == null, "doing what Bunny likes was not noticed");
            Need(Persona(store, now).Contains("brave") || Persona(store, now).Contains("careful"), "Smart Bunny's prompt has no traits");

            // Private lists: their finished tasks never count in a number sent to Claude.
            var pl = new TaskList { Name = "Folk private test", PrivateFromBunny = true };
            store.Data.Lists.Add(pl);
            var hidden = new TaskItem { Name = "hidden", List = pl.Name, Status = Status.Done };
            var shown = new TaskItem { Name = "shown", List = store.Data.Lists[0].Name, Status = Status.Done };
            store.Data.Tasks.Add(hidden);
            store.Data.Tasks.Add(shown);
            store.Data.Log.Add(new LogEntry { Kind = "Done", When = now, Points = 1, TaskId = hidden.Id });
            store.Data.Log.Add(new LogEntry { Kind = "Done", When = now, Points = 1, TaskId = shown.Id });
            Need(VisibleFinished(store, now.Date, now.Date.AddDays(1)) == 1, "a private list's task was counted for Claude");
            store.Data.Log.RemoveAll(e => e.TaskId == hidden.Id || e.TaskId == shown.Id);
            store.Data.Tasks.Remove(hidden);
            store.Data.Tasks.Remove(shown);
            store.Data.Lists.Remove(pl);

            // Moods always have a reason; a rough day wins.
            var (m1, r1) = MoodNow(store, now);
            Need(m1.Length > 0 && r1.Length > 0, "a mood with no reason");
            store.Data.Day(now).Mode = DayMode.RoughDay;
            var roughReasons = Folk.Items("folk.mood").Where(i => i.Get("when") == "rough").Select(i => Folk.Fill(store, i.Get("reason"))).ToList();
            Need(roughReasons.Contains(MoodNow(store, now).Reason), "a rough day's mood");
            Need(JournalEntries(store, now).Any(e => e.Day == now.Date), "no journal entry for a rough day");
            store.Data.Day(now).Mode = DayMode.Normal;

            // Inside jokes: added once, retired, and brought back once a week.
            store.World.SetKept("folk.jokes", new List<string>());
            Need(AddJoke(store, "Soup is a drink.") && !AddJoke(store, "Soup is a drink."), "a joke added twice");
            Need(JokeLine(store, now) is string jl && jl.Contains("Soup is a drink.") && JokeLine(store, now) == null, "the weekly joke");
            DropJoke(store, "Soup is a drink.");
            Need(JokesOf(store).Count == 0, "retiring a joke");

            // Anniversaries: a year ago today.
            var d = store.Data;
            var keep = d.Log.ToList();
            d.Log.Clear();
            Folk.SetStart(store, now.AddYears(-1));
            d.Log.Add(new LogEntry { Kind = "Boss", When = now.AddYears(-1), Points = 0 });
            Need(Today(store, now).Any(a => a.What.Contains("first boss") && a.When == "a year ago today"), "a year ago today, the first boss");
            Need(Today(store, now).All(a => a.What != CameToVillage), "the village anniversary was celebrated twice");
            Need(HobbyNow(store, now).Stage >= 1 && HobbyNow(store, now).Line.Length > 0, "the hobby");
            d.Log.Clear();
            d.Log.AddRange(keep);
            Need(OutfitsOpen(store, new DateTime(2026, 1, 10)).Any(o => o.Get("unlock") == "Winter"), "winter outfits in winter");
            Need(PoemReady(store, new DateTime(2026, 12, 15)) && !PoemReady(store, new DateTime(2026, 10, 1)), "the poem comes at semester end");
            Need(Tools.Find("folk.journal") != null && Tools.Find("folk.playlist") != null, "tools are registered");
        }
    }
}
