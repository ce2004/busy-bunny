using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;

namespace BusyBunny
{
    /// <summary>
    /// The burrow text adventure (Part 10 item 6) and village mystery week (12.1 item 6). The adventure is a short
    /// puzzle of rooms in the tunnels, new each Saturday: she types commands, replies stay in a list, progress is saved
    /// after every command, and hints come from tasks finished that week. The mystery comes once a month: clues from
    /// finished tasks, then from Friday she names the culprit, and right or wrong it ends kindly.
    /// </summary>
    public sealed class FolkGames : IFeature
    {
        private static MainWindow _w = null!;
        private static Store S => _w.Store;

        public void Attach(MainWindow w)
        {
            _w = w;
            Folk.Tool("folk.adventure", "The burrow text adventure", Drawers.Games, "A short typed puzzle in the tunnels, new each Saturday; finished tasks earn hints.", () => Adventure(null), "this week's typed puzzle in the tunnels");
            Folk.Tool("folk.mystery", "Village mystery", Drawers.Games, "Once a month a gentle whodunit: tasks earn clues, then you name who did it.", () => Mystery(null));
            Folk.Visit("folk.mystery", (_, now) => InMysteryWeek(now) && Folk.First(S, "mystery.visit." + Folk.MonthKey(now))
                ? new[] { "It is mystery week in the village: " + MysteryNow(S, now).Get("title") + " Village mystery, in the Games drawer." } : Array.Empty<string>());
            GameEvents.TaskDone += _ =>
            {
                var now = DateTime.Now;
                if (Calm.FunPaused || !Tools.IsOn("folk.mystery") || !InMysteryWeek(now) || Guessed(S, now) != null) return;
                int c = Clues(S, now);
                if (c > 1 && Folk.First(S, "mystery.clue." + Folk.MonthKey(now) + "." + c)) Folk.AfterTask(_w, Folk.Pick(S, "folk.clue.earned") + " That is clue " + c + " of 5.");
            };
        }

        // ---- The adventure: puzzles from the content library ----

        public sealed class Room { public string Id = "", Name = "", Touch = "", Sound = "", Looks = "", Locked = ""; public Dictionary<string, string> Exits = new(); public List<string> Items = new(); }
        public sealed class Thing { public string Id = "", Name = "", Touch = "", Sound = "", Looks = "", Use = "", UseLine = "", Opens = ""; }
        public sealed class Puzzle
        {
            public string Title = "", Intro = "", Start = "", Goal = "", Win = "", WinLine = "";
            public Dictionary<string, Room> Rooms = new();
            public Dictionary<string, Thing> Things = new();
            public List<string> Hints = new();
        }

        private static List<Puzzle>? _puzzles;
        private static string None(string v) => v.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : v;
        private static List<string> CommaList(string v) => None(v).Split(',').Select(x => x.Trim().ToLowerInvariant()).Where(x => x.Length > 0).ToList();

        public static List<Puzzle> Puzzles()
        {
            if (_puzzles != null) return _puzzles;
            var list = new List<Puzzle>();
            Puzzle? p = null;
            foreach (var e in Folk.Items("folk.adventure"))
            {
                if (e.ContainsKey("puzzle"))
                {
                    p = new Puzzle { Title = e.Get("puzzle"), Intro = e.Get("intro"), Start = e.Get("start"), Goal = e.Get("goal"), Win = e.Get("win"), WinLine = e.Get("winline") };
                    list.Add(p);
                }
                else if (p == null) continue;
                else if (e.ContainsKey("room"))
                {
                    var r = new Room { Id = e.Get("room"), Name = e.Get("name"), Touch = e.Get("touch"), Sound = e.Get("sound"), Looks = e.Get("looks"), Locked = None(e.Get("locked")).ToLowerInvariant(), Items = CommaList(e.Get("items")) };
                    foreach (var exit in CommaList(e.Get("exits")))
                    {
                        var parts = exit.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 2) r.Exits[parts[0]] = parts[1].Trim();
                    }
                    p.Rooms[r.Id] = r;
                }
                else if (e.ContainsKey("item"))
                    p.Things[e.Get("item")] = new Thing { Id = e.Get("item"), Name = e.Get("name"), Touch = e.Get("touch"), Sound = e.Get("sound"), Looks = e.Get("looks"), Use = None(e.Get("use")), UseLine = None(e.Get("useline")), Opens = None(e.Get("opens")).ToLowerInvariant() };
                else if (e.ContainsKey("hint")) p.Hints.Add(e.Get("hint"));
            }
            return _puzzles = list;
        }

        /// <summary>Where she is in a puzzle. Kept after every command, never rewound by Control Z.</summary>
        public sealed class AdvState
        {
            public string Key { get; set; } = "";
            public int Index { get; set; }
            public string Room { get; set; } = "";
            public List<string> Held { get; set; } = new();
            public Dictionary<string, string> Placed { get; set; } = new();
            public List<string> Opened { get; set; } = new();
            public int Hints { get; set; }
            public bool Solved { get; set; }
            public bool Paid { get; set; }
            public List<string> Log { get; set; } = new();
        }

        /// <summary>The Saturday this week's puzzle came out, and which puzzle it is.</summary>
        public static (string Key, int Index, DateTime Saturday) ThisWeek(Store s, DateTime now)
        {
            var sat = now.Date.AddDays(-(((int)now.DayOfWeek + 1) % 7));
            int n = Math.Max(0, (int)((sat - Engine.WeekStart(Folk.Start(s))).TotalDays / 7) + 1);
            return (sat.ToString("yyyy-MM-dd"), Folk.Mod(n, Math.Max(1, Puzzles().Count)), sat);
        }

        public static AdvState Load(Store s, DateTime now)
        {
            var g = s.World.GetKept<AdvState>("folk.adventure");
            var (key, index, _) = ThisWeek(s, now);
            if (g.Key == key && g.Room.Length > 0) return g;
            return Fresh(key, index);
        }

        public static AdvState Fresh(string key, int index)
        {
            var p = Puzzles()[index];
            var g = new AdvState { Key = key, Index = index, Room = p.Start };
            foreach (var r in p.Rooms.Values) foreach (var i in r.Items) g.Placed[i] = r.Id;
            return g;
        }

        public static void Save(Store s, AdvState g) { if (g.Log.Count > 200) g.Log.RemoveRange(0, g.Log.Count - 200); s.World.SetKept("folk.adventure", g); s.World.Save(); }

        /// <summary>Hints she has earned: one to start, and one more for every task finished since the puzzle came out, up to five.</summary>
        public static int HintsEarned(Store s, DateTime now) => Math.Min(5, 1 + Folk.FinishedSince(s, ThisWeek(s, now).Saturday));

        private static readonly Dictionary<string, string> Dirs = new()
        {
            ["n"] = "north", ["s"] = "south", ["e"] = "east", ["w"] = "west", ["u"] = "up", ["d"] = "down",
            ["north"] = "north", ["south"] = "south", ["east"] = "east", ["west"] = "west", ["up"] = "up", ["down"] = "down",
        };

        public const string HelpText = "Commands: look, or l, to hear where you are. go north, or just north or n, and the same for south, east, west, up and down. take, then a thing. drop, then a thing. use, then a thing. examine, then a thing. inventory, or i. hint, for a clue; finished tasks earn more. restart, to begin this puzzle again. help, for this list.";

        private static string Look(Store s, Puzzle p, AdvState g)
        {
            var r = p.Rooms[g.Room];
            var here = g.Placed.Where(kv => kv.Value == r.Id).Select(kv => p.Things.TryGetValue(kv.Key, out var t) ? t.Name + " (" + kv.Key + ")" : kv.Key).ToList();
            return Folk.Fill(s, r.Name + ". " + Folk.End(r.Touch) + " " + Folk.End(r.Sound) + " " + Folk.End(r.Looks)
                + (here.Count > 0 ? " Here: " + string.Join(", ", here) + "." : "") + " Ways out: " + string.Join(", ", r.Exits.Keys) + ".");
        }

        private static string? Match(Puzzle p, IEnumerable<string> ids, string word) =>
            ids.FirstOrDefault(i => i == word) ?? ids.FirstOrDefault(i => p.Things.TryGetValue(i, out var t) && t.Name.Split(' ').Any(x => x.Trim(',', '.').Equals(word, StringComparison.OrdinalIgnoreCase)));

        /// <summary>Does one command and returns the reply. Changes the game; the caller saves it.</summary>
        public static string Do(Store s, AdvState g, string command, int hintsEarned, bool sounds = true)
        {
            var p = Puzzles()[g.Index];
            string c = command.Trim().ToLowerInvariant();
            foreach (var filler in new[] { " the ", " a ", " an " }) c = (" " + c + " ").Replace(filler, " ").Trim();
            var words = c.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count == 0) return "Type a command, like look, or help for the list.";
            string verb = words[0], rest = string.Join(" ", words.Skip(1));
            if (verb == "pick" && words.Count > 1 && words[1] == "up") { verb = "take"; rest = string.Join(" ", words.Skip(2)); }
            if (verb is "get" or "grab") verb = "take";
            if (verb is "x" or "inspect" or "feel" || verb == "look" && rest.Length > 0 && !Dirs.ContainsKey(rest)) verb = "examine";
            if (verb == "go" && Dirs.ContainsKey(rest)) { verb = rest; rest = ""; }
            if (verb is "help" or "?") return HelpText;
            if (verb is "look" or "l") return Look(s, p, g);
            if (verb is "inventory" or "i" or "inv")
                return g.Held.Count == 0 ? "Your paws are empty." : "You are carrying " + string.Join(", ", g.Held.Select(h => p.Things.TryGetValue(h, out var t) ? t.Name + " (" + h + ")" : h)) + ".";
            if (verb is "hint" or "hints" or "clue")
            {
                if (p.Hints.Count == 0) return "There are no hints for this one.";
                if (g.Hints >= Math.Min(hintsEarned, p.Hints.Count))
                    return g.Hints >= p.Hints.Count ? "That is every hint. The last one was: " + p.Hints[^1] : "Finish another task to earn the next hint. The last one was: " + p.Hints[Math.Max(0, g.Hints - 1)];
                g.Hints++;
                return "Hint " + g.Hints + ": " + Folk.Fill(s, p.Hints[g.Hints - 1]);
            }
            if (verb == "restart") { var f = Fresh(g.Key, g.Index); g.Room = f.Room; g.Held = f.Held; g.Placed = f.Placed; g.Opened = f.Opened; g.Solved = false; return "Back to the start. " + Folk.Fill(s, p.Intro) + " " + Look(s, p, g); }
            if (Dirs.TryGetValue(verb, out var dir))
            {
                var r = p.Rooms[g.Room];
                if (!r.Exits.TryGetValue(dir, out var to) || !p.Rooms.ContainsKey(to)) return "You cannot go " + dir + " from here. Ways out: " + string.Join(", ", r.Exits.Keys) + ".";
                if (r.Locked == dir && !g.Opened.Contains(r.Id + " " + dir)) return "The way " + dir + " is blocked. Something might open it.";
                g.Room = to;
                if (sounds) SoundBank.Play("Bunny, hop");
                return Look(s, p, g);
            }
            if (rest.Length == 0) return "I did not understand that. Type help for the commands.";
            if (verb == "take")
            {
                var here = g.Placed.Where(kv => kv.Value == g.Room).Select(kv => kv.Key).ToList();
                if (Match(p, here, rest) is not string id) return Match(p, g.Held, rest) != null ? "You already have that." : "There is no " + rest + " here.";
                g.Placed.Remove(id);
                g.Held.Add(id);
                return "Taken: " + (p.Things.TryGetValue(id, out var t) ? t.Name + ". " + Folk.End(t.Touch) : id + ".");
            }
            if (verb == "drop")
            {
                if (Match(p, g.Held, rest) is not string id) return "You are not carrying that.";
                g.Held.Remove(id);
                g.Placed[id] = g.Room;
                return "Dropped.";
            }
            if (verb == "examine")
            {
                var near = g.Held.Concat(g.Placed.Where(kv => kv.Value == g.Room).Select(kv => kv.Key));
                if (Match(p, near, rest) is not string id || !p.Things.TryGetValue(id, out var t)) return "You do not find any " + rest + " nearby.";
                return Folk.Fill(s, t.Name + ". " + Folk.End(t.Touch) + " " + Folk.End(t.Sound) + " " + Folk.End(t.Looks));
            }
            if (verb == "use")
            {
                if (Match(p, g.Held, rest) is not string id)
                    return Match(p, g.Placed.Where(kv => kv.Value == g.Room).Select(kv => kv.Key), rest) != null ? "Pick it up first: take " + rest + "." : "You are not carrying that.";
                var t = p.Things[id];
                if (g.Solved) return "The puzzle is already solved. A new one comes out on Saturday.";
                if (id == p.Goal && g.Room == p.Win)
                {
                    g.Solved = true;
                    if (sounds) SoundBank.Play("Singing bells, sparkle");
                    return "Solved! " + Folk.Fill(s, p.WinLine);
                }
                if (t.Use == g.Room && t.Use.Length > 0)
                {
                    if (t.Opens.Length > 0 && !g.Opened.Contains(g.Room + " " + t.Opens)) g.Opened.Add(g.Room + " " + t.Opens);
                    return t.UseLine.Length > 0 ? Folk.Fill(s, t.UseLine) : "Something clicks. The way " + t.Opens + " is open now.";
                }
                return "Nothing happens here. Maybe it is meant for somewhere else.";
            }
            return "I did not understand that. Type help for the commands.";
        }

        private static void Adventure(Window? owner)
        {
            if (Puzzles().Count == 0) return;
            var now = DateTime.Now;
            var g = Load(S, now);
            var p = Puzzles()[g.Index];
            if (g.Log.Count == 0) { g.Log.Add(Folk.Fill(S, p.Title + ". " + p.Intro) + " " + Look(S, p, g) + " Type help for the commands."); Save(S, g); }

            var o = owner ?? _w;
            var back = o == _w ? _w.KeepFocus() : () => { };
            var w = Layout.Dialog(o, p.Title);
            var replies = new ListBox { Width = 680, Height = 300 };
            VirtualizingPanel.SetIsVirtualizing(replies, false);
            ScrollViewer.SetHorizontalScrollBarVisibility(replies, ScrollBarVisibility.Disabled);
            AutomationProperties.SetName(replies, "Replies");
            void Add(string line)
            {
                var item = new ListBoxItem { Content = new TextBlock { Text = line, TextWrapping = TextWrapping.Wrap, MaxWidth = 640 } };
                AutomationProperties.SetName(item, line);
                replies.Items.Add(item);
                replies.SelectedIndex = replies.Items.Count - 1;
                replies.ScrollIntoView(item);
            }
            foreach (var l in g.Log) Add(l);
            var box = new TextBox();
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
                e.Handled = true;
                string cmd = box.Text.Trim();
                if (cmd.Length == 0) return;
                box.Clear();
                var game = Load(S, DateTime.Now);
                bool wasSolved = game.Solved;
                string reply = Do(S, game, cmd, HintsEarned(S, DateTime.Now));
                if (game.Solved && !wasSolved && !game.Paid)
                {
                    game.Paid = true;
                    Ledger.Pay(_w, 10, "Solved the burrow adventure: " + Puzzles()[game.Index].Title, "folk.adventure", null, true, PayVoice.Silent);
                    reply += " Plus 10 points. " + PlayTime.SpendLine(S.Data) + ".";
                }
                game.Log.Add("You typed: " + cmd);
                game.Log.Add(reply);
                Save(S, game);
                Add("You typed: " + cmd);
                Add(reply);
                Speech.Say(w, reply, true);
            };
            var close = Layout.Button("_Close", isCancel: true);
            close.Click += (_, _) => w.Close();
            var panel = new StackPanel { Margin = new Thickness(12) };
            panel.Children.Add(new Label { Content = "_Replies", Target = replies, Padding = new Thickness(0, 0, 0, 2) });
            panel.Children.Add(replies);
            panel.Children.Add(Layout.Row("_Command, then Enter", box, 680));
            panel.Children.Add(new TextBlock { Text = "Type help for the commands. Your progress is saved after every command. Finished tasks this week earn hints.", TextWrapping = TextWrapping.Wrap, MaxWidth = 680 });
            panel.Children.Add(close);
            w.Content = panel;
            w.Loaded += (_, _) =>
            {
                box.Focus();
                w.Dispatcher.BeginInvoke(() => Speech.Say(w, g.Log[^1], false), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            };
            w.ShowDialog();
            back();
        }

        // ---- Village mystery week ----

        /// <summary>The Monday on or after the 8th: mystery week, once a month.</summary>
        public static DateTime MysteryMonday(DateTime now)
        {
            var eighth = new DateTime(now.Year, now.Month, 8);
            return eighth.AddDays(((int)DayOfWeek.Monday - (int)eighth.DayOfWeek + 7) % 7);
        }

        public static bool InMysteryWeek(DateTime now) => now.Date >= MysteryMonday(now) && now.Date < MysteryMonday(now).AddDays(7);
        public static Dictionary<string, string> MysteryNow(Store s, DateTime now) => Folk.Item("folk.mysteries", Folk.Months(s, now));

        /// <summary>One clue to start, one more for each task finished during mystery week, up to five.</summary>
        public static int Clues(Store s, DateTime now)
        {
            var mon = MysteryMonday(now);
            if (now.Date < mon) return 0;
            var end = now < mon.AddDays(7) ? now : mon.AddDays(7);
            return Math.Min(5, 1 + Folk.FinishedBetween(s, mon, end.AddSeconds(1)));
        }

        public static bool CanGuess(Store s, DateTime now) => now.Date >= MysteryMonday(now).AddDays(4) || Clues(s, now) >= 5;

        public sealed class Guess { public string Name { get; set; } = ""; public bool Right { get; set; } }
        public static Guess? Guessed(Store s, DateTime now) => s.World.Get<Dictionary<string, Guess>>("folk.mystery").TryGetValue(Folk.MonthKey(now), out var g) ? g : null;

        /// <summary>Names the culprit: 5 points if right, 3 for playing detective anyway. One Control Z step. Returns the ending.</summary>
        public static string Name(Store s, DateTime now, string who, MainWindow? w)
        {
            var m = MysteryNow(s, now);
            bool right = who.Equals(Folk.Fill(s, m.Get("culprit")), StringComparison.OrdinalIgnoreCase);
            int pts = right ? 5 : 3;
            if (w != null) Ledger.Pay(w, pts, (right ? "Solved the village mystery" : "Played detective in the village mystery"), "folk.mystery", null, true, PayVoice.Silent);
            else Ledger.Pay(s, pts, "Village mystery", "folk.mystery", null, true);
            var all = s.World.Get<Dictionary<string, Guess>>("folk.mystery");
            all[Folk.MonthKey(now)] = new Guess { Name = who, Right = right };
            s.World.Set("folk.mystery", all);
            s.World.Save();
            s.Save();
            return Folk.Fill(s, right ? m.Get("right") : m.Get("kind")) + " Plus " + pts + " points.";
        }

        private static void Mystery(Window? owner)
        {
            Folk.List(owner, "Village mystery", () =>
            {
                var now = DateTime.Now;
                var m = MysteryNow(S, now);
                var mon = MysteryMonday(now);
                var rows = new List<Folk.Row>();
                if (now.Date < mon)
                {
                    rows.Add(new Folk.Row("Mystery week starts " + Folk.Day(mon) + ". Something will go missing, gently. Finish tasks that week to earn clues."));
                    return rows;
                }
                rows.Add(new Folk.Row(Folk.Fill(S, m.Get("title") + " " + m.Get("setup")) + " Suspects: " + Folk.Fill(S, m.Get("suspects")) + "."));
                if (Guessed(S, now) is Guess g)
                    rows.Add(new Folk.Row("Solved: you named " + g.Name + (g.Right ? ", and you were right." : ". The real answer came out kindly.") + " Enter to hear the ending again.",
                        win => Folk.Read(win, Folk.Fill(S, m.Get("title")), Folk.Fill(S, g.Right ? m.Get("right") : m.Get("kind")))));
                else if (CanGuess(S, now))
                    rows.Add(new Folk.Row("Name who did it. Enter to choose.", win =>
                    {
                        var suspects = Folk.Fill(S, m.Get("suspects")).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                        int pick = Folk.Choose(win, "Who did it?", "Who do you think did it? Right or wrong, it ends kindly.", suspects);
                        if (pick < 0) return;
                        string ending = Name(S, DateTime.Now, suspects[pick], _w);
                        Folk.Read(win, Folk.Fill(S, m.Get("title")), ending);
                    }));
                else rows.Add(new Folk.Row("You can name who did it from Friday, " + mon.AddDays(4).ToString("MMMM d") + ", or as soon as all five clues are in."));
                int clues = Clues(S, now);
                for (int i = clues; i >= 1; i--) rows.Add(new Folk.Row("Clue " + i + ": " + Folk.Fill(S, m.Get("clue" + i))));
                if (clues < 5 && InMysteryWeek(now)) rows.Add(new Folk.Row("Each task you finish this week turns up another clue."));
                return rows;
            }, "Newest clue first. Enter on the naming line chooses who did it.");
        }

        // ---- Self-test ----

        /// <summary>Every puzzle can be solved: a search over going, taking and using.</summary>
        public static int Solve(Store s, int index)
        {
            var p = Puzzles()[index];
            var start = Fresh("test", index);
            var seen = new HashSet<string>();
            var queue = new Queue<(AdvState G, int Steps)>();
            queue.Enqueue((start, 0));
            string Key(AdvState g) => g.Room + "|" + string.Join(",", g.Held.OrderBy(x => x)) + "|" + string.Join(",", g.Placed.OrderBy(x => x.Key).Select(x => x.Key + x.Value)) + "|" + string.Join(",", g.Opened.OrderBy(x => x));
            AdvState Copy(AdvState g) => JsonSerializer.Deserialize<AdvState>(JsonSerializer.Serialize(g))!;
            while (queue.Count > 0 && seen.Count < 20000)
            {
                var (g, steps) = queue.Dequeue();
                if (!seen.Add(Key(g))) continue;
                var cmds = p.Rooms[g.Room].Exits.Keys.Select(d => "go " + d)
                    .Concat(g.Placed.Where(kv => kv.Value == g.Room).Select(kv => "take " + kv.Key))
                    .Concat(g.Held.Select(h => "use " + h)).Concat(g.Held.Select(h => "drop " + h));
                foreach (var cmd in cmds)
                {
                    var next = Copy(g);
                    Do(s, next, cmd, 5, false);
                    if (next.Solved) return steps + 1;
                    queue.Enqueue((next, steps + 1));
                }
            }
            return -1;
        }

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("Folk, games: " + what); }
            var ps = Puzzles();
            Need(ps.Count >= 4, "fewer than 4 puzzles");
            for (int i = 0; i < ps.Count; i++)
            {
                var p = ps[i];
                Need(p.Rooms.Count is >= 5 and <= 10 && p.Rooms.ContainsKey(p.Start) && p.Rooms.ContainsKey(p.Win) && p.Things.ContainsKey(p.Goal) && p.Hints.Count == 5, p.Title + " is not complete");
                foreach (var r in p.Rooms.Values) foreach (var to in r.Exits.Values) Need(p.Rooms.ContainsKey(to), p.Title + ": an exit to nowhere from " + r.Id);
                Need(Solve(store, i) > 0, p.Title + " cannot be solved");
            }
            // Commands: help lists them, a wrong word is answered kindly, and hints wait for finished tasks.
            var g = Fresh("t", 0);
            Need(Do(store, g, "help", 1, false).Contains("inventory") && Do(store, g, "dance wildly", 1, false).Contains("help"), "help and unknown commands");
            Need(Do(store, g, "hint", 1, false).StartsWith("Hint 1") && Do(store, g, "hint", 1, false).Contains("Finish another task"), "hints wait for tasks");
            Need(Do(store, g, "hint", 2, false).StartsWith("Hint 2"), "a finished task earns a hint");
            Need(Do(store, g, "look", 1, false).Contains("Ways out") && Do(store, g, "i", 1, false).Contains("empty"), "look and inventory");
            Need(!Do(store, g, "look", 1, false).Contains('{'), "a room still has a placeholder");
            // Saved after every command: a reload has the same room.
            var now = new DateTime(2026, 11, 7, 10, 0, 0);
            Folk.SetStart(store, now.AddDays(-20));
            var live = Load(store, now);
            var dir = Puzzles()[live.Index].Rooms[live.Room].Exits.Keys.First(d => Puzzles()[live.Index].Rooms[live.Room].Locked != d);
            Do(store, live, "go " + dir, 1, false);
            Save(store, live);
            Need(Load(store, now).Room == live.Room && Load(store, now.AddDays(7)).Room == Puzzles()[ThisWeek(store, now.AddDays(7)).Index].Start, "progress saved, and a new puzzle next Saturday");
            Need(ThisWeek(store, new DateTime(2026, 11, 7)).Key == "2026-11-07" && ThisWeek(store, new DateTime(2026, 11, 13)).Key == "2026-11-07", "the puzzle changes on Saturday");

            // Mystery week: the Monday on or after the 8th, clues from tasks, a kind ending either way, paid once and undone together.
            Need(Folk.Items("folk.mysteries").Count >= 12 && Folk.Items("folk.mysteries").All(m => m.Get("suspects").Split(',').Select(x => x.Trim()).Contains(m.Get("culprit"))), "every culprit is a suspect");
            Need(Content.Lines("folk.clue.earned").Count >= 20, "folk.clue.earned has fewer than 20 lines");
            Need(MysteryMonday(new DateTime(2026, 11, 20)) == new DateTime(2026, 11, 9) && MysteryMonday(new DateTime(2026, 6, 1)) == new DateTime(2026, 6, 8), "mystery Mondays");
            var mon = new DateTime(2026, 11, 9, 9, 0, 0);
            var d = store.Data;
            var keep = d.Log.ToList();
            d.Log.Clear();
            Need(Clues(store, mon) == 1 && !CanGuess(store, mon), "one free clue on Monday");
            for (int i = 0; i < 6; i++) d.Log.Add(new LogEntry { Kind = "Done", When = mon.AddHours(i), Points = 1, TaskId = Guid.NewGuid() });
            Need(Clues(store, mon.AddHours(8)) == 5 && CanGuess(store, mon.AddHours(8)), "clues from tasks, then naming");
            int before = Engine.Total(d);
            var m = MysteryNow(store, mon);
            string wrong = m.Get("suspects").Split(',').Select(x => x.Trim()).First(x => x != m.Get("culprit"));
            string ending = Name(store, mon.AddHours(8), Folk.Fill(store, wrong), null);
            Need(ending.Length > 20 && !ending.Contains('{') && Engine.Total(d) == before + 3 && Guessed(store, mon)?.Right == false, "a wrong guess ends kindly and still pays a little");
            store.Undo();
            d = store.Data;
            Need(Engine.Total(d) == before && Guessed(store, mon) == null, "undo of naming the culprit");
            d.Log.Clear();
            d.Log.AddRange(keep);
        }
    }
}
