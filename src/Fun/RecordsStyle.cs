using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>
    /// Celebration style (sweep 8.4, Settings): quiet, loud or roast. Every Stage 6 celebration asks this class, and
    /// any other feature may too: Celebration.Style(store), Celebration.Line(store, news), Celebration.Sound(store, id).
    /// Quiet says only the news, with no sound. Loud adds a cheer and a sound. Roast adds a teasing line and a sound.
    /// Kept in the world's bookkeeping, so Control Z never changes it.
    /// </summary>
    public static class Celebration
    {
        public const string Quiet = "quiet", Loud = "loud", Roast = "roast";
        public static readonly string[] Styles = { Quiet, Loud, Roast };
        private const string Id = "records.style";
        public sealed class Pref { public string Style { get; set; } = Loud; }

        public static string Style(Store s) => s.World.GetKept<Pref>(Id).Style is string st && Styles.Contains(st) ? st : Loud;
        public static void Set(Store s, string style)
        {
            s.World.SetKept(Id, new Pref { Style = Styles.Contains(style) ? style : Loud });
            s.World.Save();
        }

        /// <summary>The cheer after some news: nothing when quiet, a kind line when loud (kept in the compliment jar), a tease for roast.</summary>
        public static string Cheer(Store s)
        {
            switch (Style(s))
            {
                case Quiet: return "";
                case Roast: return Content.Pick(s, "records.cheer.roast");
                default:
                    string line = Content.Pick(s, "records.cheer.loud");
                    RecordsStyle.KeepCompliment(s, line);
                    return line;
            }
        }

        /// <summary>The news, then the cheer the style asks for.</summary>
        public static string Line(Store s, string news) => (news + " " + Cheer(s)).Trim();

        /// <summary>
        /// Plays a celebration sound unless the style is quiet or Calm mode is on (the sound bank lets event names like
        /// SurpriseReward through in Calm mode, and celebrations use them). Tools she opens may call SoundBank.Play directly.
        /// </summary>
        public static void Sound(Store s, string id) { if (Plays(s)) SoundBank.Play(id); }

        /// <summary>Whether Sound would play now (for the self-test).</summary>
        public static bool Plays(Store s) => Style(s) != Quiet && !Calm.FunPaused;

        public static string Describe(string style) => style switch
        {
            Quiet => "Quiet: just the news, no sounds",
            Roast => "Roast: the news with a little teasing",
            _ => "Loud: the news, a cheer and a sound",
        };
    }

    /// <summary>
    /// The style switch and its Settings page, Rate a line (thumbs up or down on the last fun line, which the content
    /// library then prefers or leaves out), and the compliment jar (kind lines kept to hear again, one on a rough day).
    /// </summary>
    public sealed class RecordsStyle : IFeature
    {
        private const string StyleId = "records.style", RateId = "records.rate", JarId = "records.compliments";
        private MainWindow _w = null!;

        public sealed class Ratings { public Dictionary<string, int> Of { get; set; } = new(); }
        public sealed class Jar { public List<string> Lines { get; set; } = new(); public string RoughDay { get; set; } = ""; }

        public void Attach(MainWindow w)
        {
            _w = w;
            var s = w.Store;
            Tools.Register(new BunnyTool(StyleId, "Celebration style", Drawers.Wins, "Enter switches between quiet, loud and roast celebrations.", () =>
            {
                string next = Celebration.Styles[(Array.IndexOf(Celebration.Styles, Celebration.Style(s)) + 1) % Celebration.Styles.Length];
                Celebration.Set(s, next);
                w.Say("Celebration style is " + next + " now. " + Celebration.Describe(next) + ".", true);
            }) { Since = "0.14.0", State = () => Celebration.Style(s) });
            Tools.Register(new BunnyTool(RateId, "Rate a line", Drawers.Wins, "Thumbs up or down on the last fun line, so you hear more of the ones you like, or keep a funny one as an inside joke.", Rate) { Since = "0.14.0" });
            Tools.Register(new BunnyTool(JarId, "Compliment jar", Drawers.Wins, "Hear a kind line about your work, or put one in.", ShowJar) { Since = "0.14.0" });
            BunnyTab.Register("Compliment jar", "a kind line about your work", ShowJar, JarId);

            Content.Rating = (store, topic, line) => Tools.IsOn(RateId) ? RatingOf(store, topic, line) : 0;
            Speech.Said += NoteHeard;
            // A rough day: one kind line from the jar, once that day. Kept in What happened today, and said when the calm rules allow.
            Ticker.Every(JarId + ".rough", _ => true, now =>
            {
                if (!Tools.IsOn(JarId) || Calm.FunPaused) return;
                if (!s.Data.Days.TryGetValue(s.Key(now), out var day) || day == null || day.Mode != DayMode.RoughDay) return;
                var jar = s.World.GetKept<Jar>(JarId + ".rough");
                if (jar.RoughDay == s.Key(now)) return;
                jar.RoughDay = s.Key(now);
                s.World.SetKept(JarId + ".rough", jar);
                s.World.Save();
                Calm.SayUnprompted(w, "From the compliment jar, for a rough day: " + OneCompliment(s));
            });
        }

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) { yield return new StylePage(store); }

        private sealed class StylePage : ISettingsPage
        {
            private readonly Store _s;
            private readonly ComboBox _pick = new();
            public StylePage(Store s)
            {
                _s = s;
                foreach (var st in Celebration.Styles) _pick.Items.Add(Celebration.Describe(st));
                _pick.SelectedIndex = Array.IndexOf(Celebration.Styles, Celebration.Style(s));
                var panel = new StackPanel { Margin = new Thickness(8) };
                panel.Children.Add(new TextBlock { Text = "How Busy Bunny celebrates your wins: marbles, records, games and more.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 6) });
                panel.Children.Add(Layout.Row("Celebration _style", _pick, 360));
                Content = panel;
            }
            public string Name => "Celebrations";
            public FrameworkElement Content { get; }
            public void Apply() { if (_pick.SelectedIndex >= 0) Celebration.Set(_s, Celebration.Styles[_pick.SelectedIndex]); }
        }

        // ---- Rate a line ----

        private static Ratings? _cache;
        private static Store? _cacheFor;
        private static Ratings Load(Store s)
        {
            if (_cache == null || !ReferenceEquals(_cacheFor, s)) { _cache = s.World.GetKept<Ratings>(RateId); _cacheFor = s; }
            return _cache;
        }

        public static int RatingOf(Store s, string topic, string line) => Load(s).Of.TryGetValue(topic + "|" + line, out int r) ? r : 0;

        /// <summary>Stores her rating for a line: 1 up, minus 1 down, 0 clears it. Kept across Control Z: it is her taste.</summary>
        public static void SetRating(Store s, string topic, string line, int rating)
        {
            var r = Load(s);
            string key = topic + "|" + line;
            if (rating == 0) r.Of.Remove(key); else r.Of[key] = Math.Sign(rating);
            s.World.SetKept(RateId, r);
            s.World.Save();
        }

        /// <summary>The last library line she actually heard: topic, line as typed, and the words as said. Null before the first.</summary>
        public static (string Topic, string Line, string Said)? Heard { get; private set; }

        /// <summary>
        /// Called with every line handed to the screen reader: the newest library pick found in it becomes the line to
        /// rate. Picks never spoken (a cheer that lost to a level up, a marble line only kept) never count. Placeholders
        /// filled after the pick ({n}, {task}) match any words.
        /// </summary>
        public static void NoteHeard(string spoken)
        {
            if (string.IsNullOrWhiteSpace(spoken)) return;
            for (int k = Content.Recent.Count - 1; k >= 0; k--)
            {
                var (topic, line, given) = Content.Recent[k];
                if (given.Trim().Length < 4) continue;
                var parts = Regex.Split(given.Trim(), @"\{\w+\}").Select(Regex.Escape);
                var m = Regex.Match(spoken, string.Join(".+?", parts));
                if (!m.Success) continue;
                Heard = (topic, line, m.Value);
                return;
            }
        }

        private void Rate()
        {
            if (Heard is not (string topic, string line, string said)) { _w.Say("There is no fun line to rate yet. Finish something first.", true); return; }
            int now = RatingOf(_w.Store, topic, line);
            var choices = new[] { "Thumbs up: more like this", "Thumbs down: not this one again", "No rating", "That was funny, keep it as an inside joke" };
            int i = GamesFunKit.Pick(_w, "Rate a line", "_Your rating", choices, now > 0 ? 0 : now < 0 ? 1 : 2, "The last fun line you heard was: " + said);
            if (i < 0) return;
            _w.Say(ApplyRating(_w.Store, topic, line, said, i), true);
        }

        /// <summary>
        /// Her choice in Rate a line: 0 thumbs up, 1 thumbs down, 2 no rating, 3 an inside joke, which keeps the words
        /// she heard in Bunny's jokes and also counts as a thumbs up. Returns what to say.
        /// </summary>
        public static string ApplyRating(Store s, string topic, string line, string said, int choice)
        {
            SetRating(s, topic, line, choice is 0 or 3 ? 1 : choice == 1 ? -1 : 0);
            if (choice == 3)
                return FolkBunny.AddJoke(s, said) ? "Ha, an inside joke now. Bunny will bring it up again some day, and you will hear lines like that a bit more."
                    : "Bunny already keeps that one as an inside joke. You will hear lines like that a bit more.";
            return choice == 0 ? "Thumbs up. You will hear lines like that a bit more." : choice == 1 ? "Thumbs down. That line is out of the rotation." : "Rating cleared.";
        }

        // ---- Compliment jar ----

        /// <summary>Keeps a kind line in the jar (once each, the newest 200).</summary>
        public static void KeepCompliment(Store s, string line)
        {
            line = line.Trim();
            if (line.Length == 0) return;
            var jar = s.World.Get<Jar>(JarId);
            if (jar.Lines.Contains(line)) return;
            jar.Lines.Add(line);
            if (jar.Lines.Count > 200) jar.Lines.RemoveAt(0);
            s.World.Set(JarId, jar);
            s.World.Save();
        }

        /// <summary>One line from the jar, or from the library while the jar is still empty.</summary>
        public static string OneCompliment(Store s)
        {
            var jar = s.World.Get<Jar>(JarId);
            return jar.Lines.Count == 0 ? Content.Pick(s, "records.compliments") : jar.Lines[GamesFunKit.Rng.Next(jar.Lines.Count)];
        }

        private void ShowJar()
        {
            var s = _w.Store;
            int n = s.World.Get<Jar>(JarId).Lines.Count;
            var choices = new[] { "Read me one", "Put a kind line in the jar", "Hear every line in the jar" };
            int i = GamesFunKit.Pick(_w, "Compliment jar", "_What now", choices, 0, "The compliment jar has " + GamesFunKit.Plural(n, "kind line") + " in it.");
            if (i == 0) _w.Say(OneCompliment(s), true);
            else if (i == 1)
            {
                var line = GamesFunKit.Ask(_w, "Compliment jar", "_Kind line", intro: "Something kind someone said, or that you want to remember.");
                if (line == null) return;
                KeepCompliment(s, line);
                _w.Say("In the jar. It will come back on a day you need it.", true);
            }
            else if (i == 2)
            {
                var all = s.World.Get<Jar>(JarId).Lines;
                GamesFunKit.Read(_w, "Compliment jar", all.Count == 0 ? new List<string> { "The jar is empty for now. Kind lines go in as you celebrate wins." } : all.AsEnumerable().Reverse().ToList());
            }
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("celebrations: " + what); }
            foreach (var t in new[] { "records.cheer.loud", "records.cheer.roast", "records.compliments" })
                Need(Content.Lines(t).Count >= 20, t + " has fewer than 20 lines");
            Celebration.Set(store, Celebration.Quiet);
            Need(Celebration.Line(store, "News.") == "News.", "quiet added a cheer");
            store.Remember("before roast");
            Celebration.Set(store, Celebration.Roast);
            store.Undo();
            Need(Celebration.Style(store) == Celebration.Roast, "Control Z changed the celebration style");
            Celebration.Set(store, Celebration.Loud);
            string loud = Celebration.Line(store, "News.");
            Need(loud.Length > 6 && store.World.Get<Jar>(JarId).Lines.Count == 1, "a loud cheer did not go in the compliment jar");
            // Ratings: a turned-down line never comes up while others remain.
            var lines = Content.Lines("records.cheer.roast");
            for (int k = 1; k < lines.Count; k += 2) SetRating(store, "records.cheer.roast", lines[k], -1);
            var rng = new Random(3);
            for (int k = 0; k < 6; k++)
            {
                string p = Content.Pick(store, "records.cheer.roast", rng);
                int at = lines.FindIndex(l => l.Replace("{name}", store.Data.Game.BunnyName) == p);
                Need(at % 2 == 0, "a thumbs down line came up: " + p);
            }
            // Rate a line rates what she heard, not the last pick: a line picked but never said does not count.
            string spoken = Content.Pick(store, "records.cheer.roast", rng);
            Content.Pick(store, "records.marbles.drop", rng);
            NoteHeard("Level up! " + spoken + " 12 points to spend.");
            Need(Heard?.Topic == "records.cheer.roast" && Heard?.Said == spoken, "Rate a line did not pick the line she heard");
            var ceremony = Content.Lines("records.ceremony").FirstOrDefault(l => l.Contains("{task}"));
            if (ceremony != null)
            {
                Content.Recent.Add(("records.ceremony", ceremony, ceremony));
                NoteHeard("Task of the week: Essay. " + ceremony.Replace("{task}", "Essay") + " 5 points.");
                Need(Heard?.Topic == "records.ceremony" && Heard?.Said.Contains("Essay") == true, "Rate a line missed a line with its task filled in");
            }
            // Calm mode and quiet style keep celebration sounds silent.
            Celebration.Set(store, Celebration.Quiet);
            Need(!Celebration.Plays(store), "quiet style still plays celebration sounds");
            Celebration.Set(store, Celebration.Loud);
            Need(Celebration.Plays(store) == !Calm.FunPaused, "Calm mode does not silence celebration sounds");
            for (int k = 1; k < lines.Count; k += 2) SetRating(store, "records.cheer.roast", lines[k], 0);
            Need(RatingOf(store, "records.cheer.roast", lines[1]) == 0, "clearing a rating");
            // An inside joke goes in Bunny's jokes and counts as a thumbs up.
            string joke = "Selftest joke line " + Guid.NewGuid().ToString("N");
            ApplyRating(store, "records.cheer.roast", lines[1], joke, 3);
            Need(RatingOf(store, "records.cheer.roast", lines[1]) == 1, "an inside joke did not count as a thumbs up");
            Need(FolkBunny.JokesOf(store).Contains(joke), "an inside joke did not go in Bunny's jokes");
            FolkBunny.DropJoke(store, joke);
            SetRating(store, "records.cheer.roast", lines[1], 0);
            _cache = null; _cacheFor = null;
        }
    }
}
