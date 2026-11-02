using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using AText = Anthropic.Models.Messages.TextBlock;
using TextBlock = System.Windows.Controls.TextBlock;

namespace BusyBunny
{
    /// <summary>
    /// Smarter Bunny: Talk to Bunny on the main screen, powered by Claude (Sonnet) with her own API key.
    /// Bunny can find, add, change, finish and delete tasks, manage lists and categories, change
    /// settings, set the kind of day, ask her things in a small window, keep notes about what helps
    /// her, open tabs, and check on her now and then. It only works with what Busy Bunny already has:
    /// for anything new, it points her to Help, Request a feature. Every change can be undone with
    /// Control Z, one step at a time.
    /// </summary>
    public sealed class SmartBunny : IFeature
    {
        public const string ModelId = "claude-sonnet-5-5";
        public const string PageName = "Smarter Bunny";
        private static SmartBunny? _me;

        private MainWindow _w = null!;
        private Settings Cfg => _w.AppSettings;
        private Store S => _w.Store;
        private readonly TextBox _ask = new() { Margin = new Thickness(0, 0, 0, 4) };
        private readonly TextBox _answers = new()
        {
            IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            Height = 80, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        private readonly List<MessageParam> _history = new();
        private DateTime _historyDay;
        private bool _busy;

        // ---- The key, encrypted for this Windows user ----

        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Busy Bunny, Smarter Bunny");
        public static string Protect(string key) =>
            key.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(key), Entropy, DataProtectionScope.CurrentUser));
        public static string Unprotect(string saved)
        {
            if (saved.Length == 0) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved), Entropy, DataProtectionScope.CurrentUser)); }
            catch { return ""; }
        }
        private string SavedKey() => Unprotect(Cfg.BunnyKey);

        /// <summary>Whether Bunny has a key and can talk.</summary>
        public static bool Ready => _me != null && _me.SavedKey().Length > 0;
        public static bool CanReadDates => Ready && _me!.Cfg.BunnyReadsDates;

        // ---- On the main screen ----

        public void Attach(MainWindow w)
        {
            _me = this;
            _w = w;
            AutomationProperties.SetName(_ask, "Talk to Bunny");
            AutomationProperties.SetHelpText(_ask, "Ask Bunny anything, or tell it what to do, like add PSY reading due Friday, or what should I do first. Enter sends.");
            AutomationProperties.SetName(_answers, "Bunny's answers");
            AutomationProperties.SetHelpText(_answers, "What you and Bunny said, newest at the bottom. Bunny also says its answers out loud.");
            _ask.PreviewKeyDown += (_, e) =>
            {
                if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
                e.Handled = true;
                Send(_ask.Text);
            };
            var panel = new StackPanel();
            panel.Children.Add(new Label { Content = "Talk to _Bunny", Target = _ask, Padding = new Thickness(0, 2, 0, 2) });
            panel.Children.Add(_ask);
            panel.Children.Add(new Label { Content = "Bunny's answer_s", Target = _answers, Padding = new Thickness(0, 2, 0, 2) });
            panel.Children.Add(_answers);
            w.AddBottom(panel);

            w.AddMenuItem("File", "_Talk to Bunny", FocusAsk, "Ctrl+Shift+A");
            w.AddMenuItem("File", "Bunny, check o_n me now", () => CheckIn(byHand: true));
            w.AddMenuItem("File", "Start a ne_w talk with Bunny", NewTalk);
            w.AddMenuItem("Options", "Smarter _Bunny...", () => w.OpenSettingsAt(PageName));
            w.AddKey(ModifierKeys.Control | ModifierKeys.Shift, Key.A, FocusAsk, "Control Shift A: Talk to Bunny. Type, then Enter. Bunny can add and change tasks, settings and more");
            w.MinuteTick += () => { if (ShouldCheckIn(DateTime.Now)) CheckIn(byHand: false); };
            w.NewDay += _ => _history.Clear();
        }

        private void FocusAsk()
        {
            _ask.Focus();
            if (!Ready) _w.Say("Talk to Bunny. Bunny needs a Claude API key first: Options, Smarter Bunny.", false);
        }

        private void NewTalk()
        {
            _history.Clear();
            _answers.Clear();
            _w.Say("New talk with Bunny. It still remembers its notes about you.", true);
            _ask.Focus();
        }

        private void Log(string line)
        {
            string text = _answers.Text + (_answers.Text.Length > 0 ? Environment.NewLine : "") + line;
            if (text.Length > 6000) text = text[^6000..];
            _answers.Text = text;
            _answers.CaretIndex = Math.Max(0, text.Length - line.Length);
            _answers.ScrollToEnd();
        }

        private void Send(string typed)
        {
            string text = typed.Trim();
            if (text.Length == 0) return;
            if (!Ready)
            {
                _w.Say("Bunny needs a Claude API key before it can talk. Opening Settings, Smarter Bunny.", true);
                _w.OpenSettingsAt(PageName);
                return;
            }
            if (_busy) { _w.Say("Bunny is still thinking about the last one.", true); return; }
            _ask.Clear();
            Log("You: " + text);
            _ = Run(text, focusBack: _ask);
        }

        // ---- Check-ins ----

        private bool ShouldCheckIn(DateTime now)
        {
            if (!Ready || _busy || Cfg.BunnyCheckHours <= 0) return false;
            if (now.Hour < 9 || now.Hour >= 21) return false;
            if (Cfg.BunnyLastCheck == default) { Cfg.BunnyLastCheck = now; Cfg.Save(); return false; }
            if ((now - Cfg.BunnyLastCheck).TotalHours < Cfg.BunnyCheckHours) return false;
            if (!_w.IsVisible || !_w.IsActive || _w.OwnedWindows.Count > 0) return false;
            if (S.Data.Days.TryGetValue(S.Key(now), out var d) && d.Mode == DayMode.Vacation) return false;
            return true;
        }

        private void CheckIn(bool byHand)
        {
            if (!Ready) { _w.Say("Bunny needs a Claude API key first. Opening Settings, Smarter Bunny.", true); _w.OpenSettingsAt(PageName); return; }
            if (_busy) { _w.Say("Bunny is busy with something already.", true); return; }
            Cfg.BunnyLastCheck = DateTime.Now;
            Cfg.Save();
            Log(byHand ? "You: check on me." : "Bunny checks in.");
            _ = Run(CheckInPrompt, focusBack: null);
        }

        private const string CheckInPrompt =
            "(Check-in time. She did not type this: Busy Bunny asks you to check on her now.) Look at her day first, with find_tasks for overdue and today. " +
            "Then use ask_user once to check on her: how she is doing, her energy, or something specific like an overdue task, with a few short choices and room to type. " +
            "Answer what she says. If something would clearly help, like moving a task, splitting a big one into subtasks, or low-energy mode, offer it, and only do it if she agrees in an ask_user answer. " +
            "If she closes the window without answering, leave her be and say so in a few words. Keep it short and kind.";

        // ---- Talking to Claude ----

        private sealed class Declined : Exception { }

        private async Task Run(string userText, UIElement? focusBack)
        {
            _busy = true;
            int before = _history.Count;
            Speech.Say(_w, "Bunny is thinking.");
            string reply;
            try
            {
                if (_historyDay != DateTime.Today || _history.Count > 80) { _history.Clear(); _historyDay = DateTime.Today; before = 0; }
                _history.Add(new UserMessageParam { Content = Context(DateTime.Now) + "\n\n" + userText });
                reply = Clean(await Loop());
            }
            catch (Exception e)
            {
                // Back to how the talk was, so the next question starts clean.
                if (_history.Count > before) _history.RemoveRange(before, _history.Count - before);
                reply = Problem(e);
            }
            finally
            {
                _busy = false;
                Speech.Muted = false;
            }
            Log("Bunny: " + reply);
            if (focusBack != null && _w.IsActive && _w.OwnedWindows.Count == 0) focusBack.Focus();
            Speech.Say(_w, reply, true);
        }

        private static string Problem(Exception e) => e switch
        {
            Declined => "Bunny can't help with that one. Try asking another way.",
            AnthropicUnauthorizedException or AnthropicForbiddenException => "Claude did not accept the API key. Check it in Options, Smarter Bunny.",
            AnthropicRateLimitException => "Claude is busy or the account has hit its limit. Try again in a minute, or check your credit at console.anthropic.com.",
            Anthropic5xxException => "Claude is having a moment. Try again in a minute.",
            AnthropicIOException or System.Net.Http.HttpRequestException or TaskCanceledException => "Bunny could not reach Claude. Check the internet connection and try again.",
            AnthropicBadRequestException b when b.Message.Contains("credit", StringComparison.OrdinalIgnoreCase) => "The Claude account is out of credit. Add some at console.anthropic.com, then try again.",
            _ => "Something went wrong talking to Claude: " + Short(e.Message, 200),
        };

        private static string Short(string s, int n) => s.Length <= n ? s : s[..n] + "...";

        private AnthropicClient Client()
        {
            var client = new AnthropicClient { ApiKey = SavedKey(), Timeout = TimeSpan.FromMinutes(3) };
            // For testing only: a stand-in server instead of Claude.
            if (Environment.GetEnvironmentVariable("BUSYBUNNY_CLAUDE_URL") is string url && url.Length > 0) client = new AnthropicClient { ApiKey = SavedKey(), Timeout = TimeSpan.FromMinutes(3), BaseUrl = url };
            return client;
        }

        private async Task<string> Loop()
        {
            var client = Client();
            var tools = ToolList();
            for (int round = 0; round < 15; round++)
            {
                var response = await client.Messages.Create(new MessageCreateParams
                {
                    Model = ModelId,
                    MaxTokens = 16000,
                    System = new List<TextBlockParam> { new() { Text = SystemPrompt, CacheControl = new CacheControlEphemeral() } },
                    Tools = tools,
                    Messages = _history.ToList(),
                    OutputConfig = new OutputConfig { Effort = Effort.Medium },
                    CacheControl = new CacheControlEphemeral(),
                });
                if (response.StopReason == "refusal") throw new Declined();

                var assistant = new List<ContentBlockParam>();
                var results = new List<ContentBlockParam>();
                var said = new StringBuilder();
                var calls = new List<ToolUseBlock>();
                foreach (ContentBlock block in response.Content)
                {
                    if (block.TryPickText(out AText? text))
                    {
                        assistant.Add(new TextBlockParam { Text = text.Text });
                        said.Append(text.Text);
                    }
                    else if (block.TryPickThinking(out ThinkingBlock? thinking))
                        assistant.Add(new ThinkingBlockParam { Thinking = thinking.Thinking, Signature = thinking.Signature });
                    else if (block.TryPickRedactedThinking(out RedactedThinkingBlock? redacted))
                        assistant.Add(new RedactedThinkingBlockParam { Data = redacted.Data });
                    else if (block.TryPickToolUse(out ToolUseBlock? use))
                    {
                        assistant.Add(new ToolUseBlockParam { ID = use.ID, Name = use.Name, Input = use.Input });
                        calls.Add(use);
                    }
                }
                if (assistant.Count == 0) assistant.Add(new TextBlockParam { Text = "Okay." });
                _history.Add(new AssistantMessageParam { Content = assistant });
                if (calls.Count == 0) return said.Length > 0 ? said.ToString() : "Done.";

                foreach (var use in calls)
                {
                    string result;
                    bool failed = false;
                    Speech.Muted = true;
                    try { result = await RunTool(use.Name, use.Input); }
                    catch (Exception e) { result = "That did not work: " + e.Message; failed = true; }
                    finally { Speech.Muted = false; }
                    results.Add(new ToolResultBlockParam { ToolUseID = use.ID, Content = result, IsError = failed });
                }
                _history.Add(new UserMessageParam { Content = results });
            }
            return "Bunny got a bit lost there. Try asking again, one thing at a time.";
        }

        /// <summary>Plain spoken text: no Markdown, and never a pound sign or asterisk (spec).</summary>
        public static string Clean(string s)
        {
            s = s.Replace("*", "").Replace("#", "").Replace("`", "");
            s = Regex.Replace(s, @"^\s*[-•]\s+", "", RegexOptions.Multiline);
            s = Regex.Replace(s, @"\n{3,}", "\n\n");
            return s.Trim();
        }

        /// <summary>A date phrase the form could not read, worked out by Claude. Null when it is not a date.</summary>
        public static async Task<(DateTime When, bool HasTime)?> ReadDateAsync(string text, DateTime now, Status week)
        {
            if (_me == null || !Ready) return null;
            var response = await _me.Client().Messages.Create(new MessageCreateParams
            {
                Model = ModelId,
                MaxTokens = 4000,
                OutputConfig = new OutputConfig { Effort = Effort.Low },
                System = "You turn a student's words for a date into a date. Reply with only YYYY-MM-DD, or YYYY-MM-DD HH:mm when a time is given, or the word none when the words are not a date. Weeks start on Monday.",
                Messages = [new UserMessageParam { Content = "Now: " + now.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ". The task is set to " + Words.Of(week) +
                    (week == Status.NextWeek ? ", so a day name on its own means that day next week." : week == Status.ThisWeek ? ", so a day name on its own means that day this week." : ".") + " Words: " + text }],
            });
            string reply = string.Concat(response.Content.Select(b => b.Value).OfType<AText>().Select(t => t.Text)).Trim();
            return ParseWhen(reply) is (DateTime d, bool t) ? (d, t) : null;
        }

        /// <summary>YYYY-MM-DD or YYYY-MM-DD HH:mm, or else plain words as the form reads them.</summary>
        public static (DateTime When, bool HasTime)? ParseWhen(string s, DateTime? now = null)
        {
            s = s.Trim();
            if (DateTime.TryParseExact(s, new[] { "yyyy-MM-dd HH:mm", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-ddTHH:mm:ss" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return (dt, true);
            if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return (d, false);
            var (when, hasTime) = Language.ParseDate(s, now ?? DateTime.Now);
            return when is DateTime w ? (w, hasTime) : null;
        }

        // ---- What Bunny knows ----

        private const string SystemPrompt =
@"You are Bunny, the helper inside Busy Bunny, a Windows to-do app made for Morgan, a blind psychology student who uses the NVDA screen reader and braille. Conner built the app for her. You talk to her through the Talk to Bunny box on the main screen, and your answers are read aloud by her screen reader.

How you talk:
Write plain spoken sentences, short and warm, like a funny, kind friend who is good at planning. Two to five sentences is usually right. Never use Markdown, headings, bullets, tables, emoji, the pound sign or the asterisk. Say dates as words, like Friday, November 6, never as numbers with slashes. Do not read out task ids. Match the roast dial in the context line: 0 is plain and gentle, 1 gentle, 2 lightly teasing, 3 spicy, 4 savage but never mean about her worth, her disability or her health.

What you can do:
You can look at and change her tasks, lists, categories, settings and day with your tools, and you should just do what she asks instead of telling her how. Look things up with find_tasks or get_overview before changing them; never guess an id. After changing things, say briefly what you did, and that Control Z undoes it when that helps. If her request is unclear in a way that matters, like which of two tasks, ask in your reply or with ask_user. Use ask_user when a small window with choices is easier than typing, and for check-ins. You can keep short notes about her with remember, like goals, what helps her focus, or class names; keep them useful and kind, and never store passwords or health details she did not ask you to keep.

Being helpful beyond the list:
Help her plan: suggest what to do first, break big assignments into subtasks, spread work across the week with do dates, notice overdue and piled-up tasks, and offer low-energy mode or a rough day when she sounds worn out. Celebrate wins. Be honest when a plan does not fit the hours she has. If she seems in real distress or talks about harming herself, be caring, encourage her to reach out to someone she trusts, and mention that in the US she can call or text 988.

Limits:
You can only use features Busy Bunny already has. You cannot add new features, buttons, sounds or screens, change how the app works, or reach other apps, email or the internet. When she asks for something the app cannot do, say so plainly, and tell her she can ask for it with Help, Request a feature, which opens a new issue on GitHub; offer to open it with open_feature_request. Never pretend to have done something you did not do. You cannot see or change the API key.

Busy Bunny in brief:
Tabs: Today (To do today, Overdue, Due today, Get ahead), Priority, Focus (Overdue and Focus now), Upcoming, Board, Search, and one tab per list, each with its categories. Statuses: Today, This week, Next week, Done not submitted, Waiting on someone, Done. A do date is the day she plans to do a task; on that day it shows on Today. Each list has its own priority scale, highest first, like Coffee: Triple shot, Double shot, Single shot, Decaf. Task types: general, assignment, test, meeting, event, personal goal. Sizes: small, medium, large, multi-day. Energy: easy or brain heavy. Keys: Control N quick add, F2 edit, Control D done, Control 1 to 6 set status, Delete, Control Z undo, Control R read my day, Control F search, Control Shift A talk to Bunny, Control Comma settings, Shift with arrows to select several tasks. The game gives karma points, levels, streaks and badges for finishing tasks. Fun menu has the kind of day, rough day, and everything is too much. View menu has low-energy mode and shuffle mode.";

        private string Context(DateTime now)
        {
            var sb = new StringBuilder();
            sb.Append("(Context from Busy Bunny, not typed by her: now is ").Append(now.ToString("dddd, MMMM d, yyyy, h:mm tt", CultureInfo.InvariantCulture))
              .Append(". ").Append(S.Summary(now)).Append(" Roast dial: ").Append(Cfg.RoastLevel).Append('.');
            if (_w.TabNames.Count > 0 && _w.OpenListName is string open) sb.Append(" She is on the ").Append(open).Append(" tab.");
            if (_w.ChosenTask is TaskItem t) sb.Append(" Her cursor is on the task [").Append(Short(t)).Append("] ").Append(S.Line(t, now)).Append('.');
            if (S.Data.BunnyNotes.Count > 0) sb.Append(" Your notes about her: ").Append(string.Join(" | ", S.Data.BunnyNotes)).Append('.');
            sb.Append(')');
            return sb.ToString();
        }

        private static string Short(TaskItem t) => t.Id.ToString("N")[..8];

        private string Describe(TaskItem t, DateTime now)
        {
            var sb = new StringBuilder();
            sb.Append('[').Append(Short(t)).Append("] ").Append(S.Line(t, now)).Append(". List ").Append(t.List);
            if (t.Section.Length > 0) sb.Append(", category ").Append(t.Section);
            sb.Append(". Type ").Append(t.Type).Append(", size ").Append(t.Size);
            if (t.Due is DateTime d) sb.Append(", due ").Append(d.ToString(t.DueHasTime ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (t.DoDate is DateTime p) sb.Append(", do date ").Append(p.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            if (t.Energy != Energy.Unset) sb.Append(", energy ").Append(t.Energy);
            if (t.Repeat.Length > 0) sb.Append(", repeats ").Append(t.Repeat);
            if (t.QuickWin) sb.Append(", quick win");
            if (t.GradeWeight > 0) sb.Append(", ").Append(t.GradeWeight).Append(" percent of the grade");
            if (t.Subtasks.Count > 0) sb.Append(". Subtasks: ").Append(string.Join("; ", t.Subtasks.Select(s => s.Name + (s.Done ? " (done)" : ""))));
            if (t.Notes.Length > 0) sb.Append(". Notes: ").Append(Short(t.Notes.Replace("\n", " "), 300));
            if (t.Comments.Count > 0) sb.Append(". ").Append(t.Comments.Count).Append(" comments, last: ").Append(Short(t.Comments[^1].Text, 150));
            if (t.Status == Status.Done && t.DoneAt is DateTime done) sb.Append(". Finished ").Append(done.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
            return sb.Append('.').ToString();
        }

        private string Overview(DateTime now)
        {
            var sb = new StringBuilder();
            sb.AppendLine("Now: " + now.ToString("dddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + ". This week ends Sunday " + Store.WeekEnd(now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".");
            sb.AppendLine("Summary: " + S.Summary(now));
            sb.AppendLine("Tabs: " + string.Join(", ", _w.TabNames) + ".");
            sb.AppendLine("Lists, in tab order:");
            foreach (var l in S.Data.Lists.Where(l => !l.Archived))
            {
                int open = S.Data.Tasks.Count(t => t.List == l.Name && t.Status != Status.Done);
                sb.Append("  ").Append(l.Name);
                if (l.Parent.Length > 0) sb.Append(" (inside ").Append(l.Parent).Append(')');
                sb.Append(": ").Append(open).Append(" open. Priority scale ").Append(l.Scale.Length == 0 ? "none" : l.ScaleName + ": " + string.Join(", ", l.Scale.Select((s, i) => (i + 1) + " " + s)));
                sb.Append(". Categories: ").Append(l.Sections.Count == 0 ? "none" : string.Join(", ", l.Sections));
                sb.Append(". New tasks start as ").Append(Words.Of(l.DefaultStatus)).Append(l.InToday ? "." : ". Kept out of Today.").AppendLine();
            }
            var archived = S.Data.Lists.Where(l => l.Archived).Select(l => l.Name).ToList();
            if (archived.Count > 0) sb.AppendLine("Archived lists: " + string.Join(", ", archived) + ".");
            if (S.Data.Tags.Count > 0) sb.AppendLine("Tags: " + string.Join(", ", S.Data.Tags.Select(t => t.Name + (t.Description.Length > 0 ? " (" + t.Description + ")" : ""))) + ".");
            if (S.Data.Classes.Count > 0)
                sb.AppendLine("Class times: " + string.Join("; ", S.Data.Classes.Select(c => c.Tag + " " + string.Join(" ", c.Days) + " " + c.Start.ToString(@"hh\:mm") + " to " + c.End.ToString(@"hh\:mm") + (c.Where.Length > 0 ? " at " + c.Where : ""))) + ".");
            var day = S.Data.Days.TryGetValue(S.Key(now), out var d) ? d : new DayInfo();
            sb.AppendLine("Today: " + (day.Mode == DayMode.Normal ? "a normal day" : day.Mode == DayMode.Vacation ? "vacation" : "a rough day") + ", " + S.HoursToday(now) + " hours available, kind of day " + S.DayType(now) +
                (day.Energy.Length > 0 ? ", energy " + day.Energy : "") + ". Usual hours a day: " + S.Data.HoursAvailable + ".");
            int points = Engine.Total(S.Data);
            sb.AppendLine("Game: " + points + " karma points, level " + Engine.LevelFor(points) + ", " + Engine.LevelName(Engine.LevelFor(points)) + ".");
            sb.AppendLine("Settings: " + string.Join(", ", SettingValues().Select(kv => kv.Key + " " + kv.Value)) + ".");
            sb.AppendLine(S.Data.BunnyNotes.Count == 0 ? "Your notes about her: none yet." : "Your notes about her: " + string.Join(" | ", S.Data.BunnyNotes.Select((n, i) => (i + 1) + ". " + n)));
            return sb.ToString();
        }

        private Dictionary<string, object> SettingValues() => new()
        {
            ["sounds_on"] = Cfg.SoundsOn, ["sound_volume"] = Cfg.SoundVolume, ["music_on"] = Cfg.MusicOn, ["music_volume"] = Cfg.MusicVolume,
            ["short_mode"] = Cfg.ShortMode, ["low_energy_mode"] = Cfg.LowEnergy, ["shuffle_mode"] = Cfg.ShuffleMode, ["roast_level"] = Cfg.RoastLevel,
            ["emojis_on"] = Cfg.EmojisOn, ["finals_mode"] = Cfg.FinalsMode, ["morning_check_in"] = Cfg.MorningCheckIn, ["warn_when_urgent_more_than"] = Cfg.UrgentWarnAbove,
            ["hours_available"] = S.Data.HoursAvailable, ["keep_in_tray"] = Cfg.KeepInTray, ["follow_up_days"] = Cfg.FollowUpDays,
            ["check_for_updates"] = Cfg.CheckForUpdatesAtStart, ["bunny_check_in_hours"] = Cfg.BunnyCheckHours, ["bunny_asks_before_deleting"] = Cfg.BunnyAskBeforeDelete,
        };

        // ---- Tools ----

        private static object Str(string description) => new { type = "string", description };
        private static object Int(string description) => new { type = "integer", description };
        private static object Bool(string description) => new { type = "boolean", description };
        private static object Strs(string description) => new { type = "array", items = new { type = "string" }, description };
        private static object OneOf(string description, params string[] values) => new { type = "string", @enum = values, description };

        private static readonly string[] StatusNames = { "today", "this_week", "next_week", "done_not_submitted", "waiting", "done" };

        private static Tool T(string name, string description, object properties, params string[] required)
        {
            var props = JsonSerializer.SerializeToElement(properties).EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            return new Tool { Name = name, Description = description, InputSchema = new() { Properties = props, Required = [.. required] } };
        }

        private static object TaskFields(bool adding) => new
        {
            name = Str(adding ? "The task's name, without date words." : "A new name."),
            list = Str("The list's name, exactly as get_overview gives it." + (adding ? " Leave out for the list she is on, or Homework." : "")),
            category = Str("A category of that list, or an empty string for none. It must already exist; manage_list adds categories."),
            tag = Str("A short tag, usually a class like PSY, or an empty string for none."),
            status = OneOf("The status.", StatusNames),
            priority = Int("1 is the top level of the list's priority scale, 2 the next, and so on. 0 for none."),
            due = Str("When it is due: YYYY-MM-DD, or YYYY-MM-DD HH:mm with a time. none clears it."),
            do_date = Str("The day she plans to do it: YYYY-MM-DD. On that day it shows on Today. none clears it."),
            size = OneOf("How big it is.", "small", "medium", "large", "multi_day"),
            type = OneOf("The kind of task.", "general", "assignment", "test", "meeting", "event", "personal_goal"),
            energy = OneOf("The energy it needs.", "unset", "easy", "brain_heavy"),
            repeat = Str("How it repeats, in plain words like every Monday, every 2 weeks on Tuesday, every day. none stops it repeating."),
            notes = Str("Notes. This replaces the notes it had."),
            quick_win = Bool("A quick win: two minutes or less."),
            grade_weight = Int("How many percent of the grade it counts, 0 to 100."),
        };

        private static List<ToolUnion> ToolList()
        {
            var add = JsonSerializer.SerializeToElement(TaskFields(true)).EnumerateObject().ToDictionary(p => p.Name, p => (object)p.Value.Clone());
            add["subtasks"] = Strs("Steps to check off, in order.");
            return new List<ToolUnion>
            {
                T("get_overview", "Her lists with their priority scales and categories, tags, class times, today's kind of day and hours, game level, every setting and your notes about her. Use it before changing lists or settings.", new { }),
                T("find_tasks", "Finds tasks and gives each with its id, line, list, category, dates and details.",
                    new
                    {
                        which = OneOf("Which tasks. open is every task not done.", "open", "today", "overdue", "this_week", "next_week", "waiting", "done_recently", "all"),
                        text = Str("Words to look for in the name, tag, notes or subtasks."),
                        list = Str("Only this list."),
                        limit = Int("At most this many, 60 if left out."),
                    }),
                T("add_task", "Adds a task. Busy Bunny plays its sound and it can be undone with Control Z.", add, "name"),
                T("update_tasks", "Changes one or more tasks: give only the fields to change. For finishing tasks use set_status.",
                    new { ids = Strs("The task ids from find_tasks."), changes = new { type = "object", properties = TaskFields(false), description = "The fields to change." } }, "ids", "changes"),
                T("set_status", "Sets the status of one or more tasks, as Control 1 to 6 does: Done earns points and makes the next copy of a repeating task.",
                    new { ids = Strs("The task ids."), status = OneOf("The new status.", StatusNames) }, "ids", "status"),
                T("delete_tasks", "Deletes tasks. She may be asked to confirm first. Prefer done over delete for finished work.",
                    new { ids = Strs("The task ids.") }, "ids"),
                T("subtasks", "Adds, checks off, unchecks or removes subtasks of one task, by subtask name.",
                    new { id = Str("The task id."), add = Strs("New subtasks to add."), check = Strs("Subtasks to check off."), uncheck = Strs("Subtasks to uncheck."), remove = Strs("Subtasks to remove.") }, "id"),
                T("add_comment", "Adds a dated comment to a task, like progress made.", new { id = Str("The task id."), text = Str("The comment.") }, "id", "text"),
                T("manage_list", "Changes a list, or makes a new one: its name, priority scale, categories, where new tasks start, whether it shows in Today, or archiving it.",
                    new
                    {
                        list = Str("The list's name."),
                        create = Bool("True to make a new list with this name."),
                        new_name = Str("A new name for it."),
                        scale = Str("A priority scale from the library (Coffee, Space, Burner, Importance, Attendance, Todoist, Bunny, Heat, Rock, Speed, Volume, Road trip), Custom with custom_levels, or none."),
                        custom_levels = Strs("For a custom scale: the levels, highest first."),
                        custom_name = Str("For a custom scale: its name."),
                        default_status = OneOf("The status its new tasks start with.", StatusNames.Take(5).ToArray()),
                        show_in_today = Bool("Whether its tasks show on Today and Focus."),
                        archived = Bool("True archives it: no tab, its tasks show nowhere. False brings it back."),
                        add_categories = Strs("Categories to add."),
                        remove_categories = Strs("Categories to remove; their tasks keep their list and lose the category."),
                    }, "list"),
                T("change_settings", "Changes her settings. Give only the ones to change. get_overview shows the current values.",
                    new
                    {
                        sounds_on = Bool("Sounds on or off."), sound_volume = Int("Sound volume, 10 to 100."),
                        music_on = Bool("Quiet background music while Busy Bunny is in front."), music_volume = Int("Music volume, 1 to 50."),
                        short_mode = Bool("Shorter task lines."), low_energy_mode = Bool("Only small or easy tasks on Today and Focus."),
                        shuffle_mode = Bool("A new order each day for the not-urgent tasks."), roast_level = Int("How much Busy Bunny teases her: 0 off, 1 gentle, 2 teasing, 3 spicy, 4 savage."),
                        emojis_on = Bool("Emoji at big moments."), finals_mode = Bool("Finals week mode."), morning_check_in = Bool("The morning check-in window."),
                        warn_when_urgent_more_than = Int("Warn when tasks to do today go over this many, 0 for never."),
                        hours_available = Int("Hours she has for work on a typical day, 1 to 14."), keep_in_tray = Bool("Keep running in the tray when closed."),
                        follow_up_days = Int("Days before nudging about a task waiting on someone."), check_for_updates = Bool("Check for updates automatically."),
                        bunny_check_in_hours = Int("Hours between your check-ins, 0 for never."), bunny_asks_before_deleting = Bool("Ask her before you delete tasks."),
                    }),
                T("set_day", "Sets up a day: normal, vacation (everything off Today) or a rough day (only what is due soon or on Today shows), its hours, or its kind of day.",
                    new
                    {
                        date = Str("YYYY-MM-DD, today if left out."),
                        mode = OneOf("The kind of day.", "normal", "vacation", "rough_day"),
                        hours = Int("Hours she has that day, 0 for her usual."),
                        kind_of_day = OneOf("The Bunny set for the day, or auto to let Busy Bunny pick.", "auto", "Fox at the door", "Hop to it", "Nibble on it", "Burrow nap"),
                    }),
                T("ask_user", "Shows her a small window with your question, a button for each choice, and a box to type her own answer. Returns what she picked or typed, or that she closed it. Use it for check-ins and for choices.",
                    new { question = Str("The question, short and plain."), choices = Strs("Two to five short answers to pick from."), allow_typing = Bool("Let her type her own answer. True if left out.") }, "question"),
                T("remember", "Keeps a short note about her for later talks, like a goal or what helps her.", new { note = Str("The note, one sentence.") }, "note"),
                T("forget", "Removes your notes about her: one by its number from get_overview, or all.", new { number = Int("The note's number."), all = Bool("True removes every note.") }),
                T("open_tab", "Opens a tab on the main screen, like Today, Focus, Overdue is on Today, Board, Upcoming, or a list's tab, and puts her in its tasks.", new { name = Str("The tab's name.") }, "name"),
                T("open_feature_request", "Opens a new feature request on GitHub in her browser, for something Busy Bunny cannot do yet. Only when she agrees.", new { idea = Str("A short title for the idea.") }, "idea"),
            };
        }

        // ---- Running a tool ----

        private static string? Get(IReadOnlyDictionary<string, JsonElement> a, string k) =>
            a.TryGetValue(k, out var v) && v.ValueKind != JsonValueKind.Null ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;
        private static int? GetInt(IReadOnlyDictionary<string, JsonElement> a, string k) =>
            a.TryGetValue(k, out var v) && (v.ValueKind == JsonValueKind.Number ? v.TryGetInt32(out int n) : int.TryParse(v.ToString(), out n)) ? n : null;
        private static bool? GetBool(IReadOnlyDictionary<string, JsonElement> a, string k) =>
            a.TryGetValue(k, out var v) ? v.ValueKind == JsonValueKind.True ? true : v.ValueKind == JsonValueKind.False ? false : bool.TryParse(v.ToString(), out bool b) ? b : null : null;
        private static List<string> GetList(IReadOnlyDictionary<string, JsonElement> a, string k)
        {
            if (!a.TryGetValue(k, out var v)) return new();
            if (v.ValueKind == JsonValueKind.Array) return v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() ?? "" : x.ToString()).Where(x => x.Trim().Length > 0).ToList();
            if (v.ValueKind == JsonValueKind.String && v.GetString() is string s && s.Trim().Length > 0) return new() { s };
            return new();
        }
        private static Dictionary<string, JsonElement> Obj(IReadOnlyDictionary<string, JsonElement> a, string k)
        {
            if (!a.TryGetValue(k, out var v)) return new();
            if (v.ValueKind == JsonValueKind.String && v.GetString() is string s && s.TrimStart().StartsWith("{"))
                try { v = JsonDocument.Parse(s).RootElement; } catch { return new(); }
            return v.ValueKind == JsonValueKind.Object ? v.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone()) : new();
        }

        private static Status StatusOf(string s) => s.Trim().ToLowerInvariant().Replace(' ', '_').Replace(",", "") switch
        {
            "today" => Status.Today,
            "this_week" => Status.ThisWeek,
            "next_week" => Status.NextWeek,
            "done_not_submitted" => Status.DoneNotSubmitted,
            "waiting" or "waiting_on_someone" => Status.Waiting,
            "done" => Status.Done,
            _ => throw new Exception("Unknown status " + s + ". Use " + string.Join(", ", StatusNames) + "."),
        };

        /// <summary>The tasks for ids from find_tasks (the first 8 letters of each id), or exact names.</summary>
        public static List<TaskItem> Find(Store store, IEnumerable<string> ids)
        {
            var found = new List<TaskItem>();
            foreach (var raw in ids)
            {
                string id = raw.Trim().Trim('[', ']').Replace("-", "").ToLowerInvariant();
                var hits = id.Length >= 4 ? store.Data.Tasks.Where(t => t.Id.ToString("N").StartsWith(id)).ToList() : new();
                if (hits.Count == 0) hits = store.Data.Tasks.Where(t => t.Name.Equals(raw.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
                if (hits.Count == 0) throw new Exception("No task with the id " + raw + ". Use find_tasks to get ids.");
                if (hits.Count > 1) throw new Exception(raw + " matches " + hits.Count + " tasks. Use the ids from find_tasks.");
                if (!found.Contains(hits[0])) found.Add(hits[0]);
            }
            if (found.Count == 0) throw new Exception("No task ids given.");
            return found;
        }

        private async Task<string> RunTool(string name, IReadOnlyDictionary<string, JsonElement> a)
        {
            var now = DateTime.Now;
            switch (name)
            {
                case "get_overview":
                    return Overview(now);

                case "find_tasks":
                {
                    string which = Get(a, "which") ?? "open";
                    string text = (Get(a, "text") ?? "").Trim();
                    string list = (Get(a, "list") ?? "").Trim();
                    int limit = Math.Clamp(GetInt(a, "limit") ?? 60, 1, 200);
                    IEnumerable<TaskItem> q = which switch
                    {
                        "today" => S.TodayAll(now),
                        "overdue" => S.Overdue(now),
                        "this_week" => S.Data.Tasks.Where(t => Store.PlanGroup(t, now) == Status.ThisWeek || Store.DueGroup(t, now) == Status.ThisWeek),
                        "next_week" => S.Data.Tasks.Where(t => Store.PlanGroup(t, now) == Status.NextWeek || Store.DueGroup(t, now) == Status.NextWeek),
                        "waiting" => S.Data.Tasks.Where(t => t.Status == Status.Waiting),
                        "done_recently" => S.Data.Tasks.Where(t => t.Status == Status.Done && t.DoneAt > now.AddDays(-14)).OrderByDescending(t => t.DoneAt),
                        "all" => S.Data.Tasks,
                        _ => S.Data.Tasks.Where(t => t.Status != Status.Done).OrderBy(t => t.Status).ThenBy(t => t.Due ?? DateTime.MaxValue),
                    };
                    if (list.Length > 0) q = q.Where(t => t.List.Equals(list, StringComparison.OrdinalIgnoreCase));
                    if (text.Length > 0)
                    {
                        var words = text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        q = q.Where(t =>
                        {
                            string hay = (t.Name + " " + t.Tag + " " + t.Notes + " " + t.Section + " " + string.Join(" ", t.Subtasks.Select(s => s.Name))).ToLowerInvariant();
                            return words.All(hay.Contains);
                        });
                    }
                    var all = q.ToList();
                    if (all.Count == 0) return "No tasks found.";
                    var lines = all.Take(limit).Select(t => Describe(t, now)).ToList();
                    if (all.Count > limit) lines.Add("And " + (all.Count - limit) + " more.");
                    return all.Count + (all.Count == 1 ? " task:" : " tasks:") + "\n" + string.Join("\n", lines);
                }

                case "add_task":
                {
                    string listName = Get(a, "list") ?? _w.OpenListName ?? S.ActiveLists.FirstOrDefault()?.Name ?? "Homework";
                    var list = ListNamed(listName);
                    var t = new TaskItem { List = list.Name, Status = list.DefaultStatus, StatusSince = now };
                    ApplyFields(S, t, a, now);
                    foreach (var step in GetList(a, "subtasks")) t.Subtasks.Add(new Subtask { Name = step.Trim() });
                    if (t.Status == Status.Today && t.DoDate > now.Date) t.Status = t.DoDate <= Store.WeekEnd(now) ? Status.ThisWeek : Status.NextWeek;
                    _w.AddTask(t, announce: false);
                    return "Added: " + Describe(t, now);
                }

                case "update_tasks":
                {
                    var tasks = Find(S, GetList(a, "ids"));
                    var changes = Obj(a, "changes");
                    if (changes.Count == 0) throw new Exception("No changes given.");
                    Status? status = changes.ContainsKey("status") ? StatusOf(Get(changes, "status") ?? "") : null;
                    changes.Remove("status");
                    if (changes.Count > 0)
                    {
                        S.Remember("Bunny changing " + Names(tasks));
                        foreach (var t in tasks) ApplyFields(S, t, changes, now);
                        _w.SaveAndRefresh();
                    }
                    if (status is Status s) foreach (var t in tasks.Where(t => t.Status != s)) _w.SetTaskStatus(t, s);
                    return "Changed:\n" + string.Join("\n", tasks.Select(t => Describe(t, now)));
                }

                case "set_status":
                {
                    var tasks = Find(S, GetList(a, "ids"));
                    var s = StatusOf(Get(a, "status") ?? "");
                    foreach (var t in tasks.Where(t => t.Status != s)) _w.SetTaskStatus(t, s);
                    var (done, total, _, _) = S.Progress(now);
                    return Names(tasks) + " now " + Words.Of(s) + ". " + done + " of " + total + " done today." +
                        (s == Status.Done && tasks.Any(t => t.Repeat.Length > 0) ? " The next copy of the repeating ones was added." : "");
                }

                case "delete_tasks":
                {
                    var tasks = Find(S, GetList(a, "ids"));
                    if (Cfg.BunnyAskBeforeDelete)
                    {
                        string what = tasks.Count == 1 ? tasks[0].Name : tasks.Count + " tasks: " + Names(tasks);
                        if (MessageBox.Show(_w, "Bunny wants to delete " + what + ". Delete " + (tasks.Count == 1 ? "it" : "them") + "?", "Bunny asks", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes)
                            return "She said no, so nothing was deleted.";
                    }
                    S.Remember("Bunny deleting " + Names(tasks));
                    foreach (var t in tasks) S.Data.Tasks.Remove(t);
                    _w.SaveAndRefresh();
                    return "Deleted " + Names(tasks) + ". Control Z brings them back.";
                }

                case "subtasks":
                {
                    var t = Find(S, new[] { Get(a, "id") ?? "" })[0];
                    S.Remember("Bunny changing the subtasks of " + t.Name);
                    Subtask Sub(string n) => t.Subtasks.FirstOrDefault(s => s.Name.Equals(n.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? t.Subtasks.FirstOrDefault(s => s.Name.Contains(n.Trim(), StringComparison.OrdinalIgnoreCase))
                        ?? throw new Exception(t.Name + " has no subtask called " + n + ". It has: " + string.Join("; ", t.Subtasks.Select(s => s.Name)));
                    foreach (var n in GetList(a, "add")) t.Subtasks.Add(new Subtask { Name = n.Trim() });
                    foreach (var n in GetList(a, "check")) Sub(n).Done = true;
                    foreach (var n in GetList(a, "uncheck")) Sub(n).Done = false;
                    foreach (var n in GetList(a, "remove")) t.Subtasks.Remove(Sub(n));
                    t.TouchedAt = now;
                    _w.SaveAndRefresh();
                    return Describe(t, now) + (t.Subtasks.Count > 0 && t.Subtasks.All(s => s.Done) && t.Status != Status.Done ? " Every step is done; the task itself is still open." : "");
                }

                case "add_comment":
                {
                    var t = Find(S, new[] { Get(a, "id") ?? "" })[0];
                    S.Remember("Bunny commenting on " + t.Name);
                    t.Comments.Add(new Comment { When = now, Text = (Get(a, "text") ?? "").Trim() });
                    t.TouchedAt = now;
                    _w.SaveAndRefresh();
                    return "Comment added to " + t.Name + ".";
                }

                case "manage_list":
                    return ManageList(a);

                case "change_settings":
                    return ChangeSettings(a);

                case "set_day":
                {
                    var date = Get(a, "date") is string ds && ParseWhen(ds, now) is (DateTime dd, _) ? dd.Date : now.Date;
                    S.Remember("Bunny setting up " + Words.Day(date, now));
                    var day = S.Data.Day(date);
                    if (Get(a, "mode") is string mode)
                        day.Mode = mode switch { "vacation" => DayMode.Vacation, "rough_day" => DayMode.RoughDay, _ => DayMode.Normal };
                    if (GetInt(a, "hours") is int h) day.Hours = Math.Clamp(h, 0, 16);
                    if (Get(a, "kind_of_day") is string kind) day.DayType = kind == "auto" ? "" : kind;
                    _w.SaveAndRefresh();
                    return Words.Day(date, now) + " is " + (day.Mode == DayMode.Normal ? "a normal day" : day.Mode == DayMode.Vacation ? "a vacation day" : "a rough day") +
                        (day.Hours > 0 ? ", " + day.Hours + " hours" : "") + ". Kind of day: " + S.DayType(date) + ".";
                }

                case "ask_user":
                    return Ask(Get(a, "question") ?? "", GetList(a, "choices"), GetBool(a, "allow_typing") ?? true);

                case "remember":
                {
                    string note = (Get(a, "note") ?? "").Trim();
                    if (note.Length == 0) throw new Exception("No note given.");
                    S.Remember("Bunny keeping a note");
                    S.Data.BunnyNotes.Add(Short(note, 300));
                    while (S.Data.BunnyNotes.Count > 40) S.Data.BunnyNotes.RemoveAt(0);
                    _w.SaveAndRefresh();
                    return "Noted. " + S.Data.BunnyNotes.Count + " notes.";
                }

                case "forget":
                {
                    S.Remember("Bunny forgetting a note");
                    if (GetBool(a, "all") == true) S.Data.BunnyNotes.Clear();
                    else if (GetInt(a, "number") is int n && n >= 1 && n <= S.Data.BunnyNotes.Count) S.Data.BunnyNotes.RemoveAt(n - 1);
                    else throw new Exception("Give a note number from 1 to " + S.Data.BunnyNotes.Count + ", or all.");
                    _w.SaveAndRefresh();
                    return S.Data.BunnyNotes.Count + " notes left.";
                }

                case "open_tab":
                {
                    string tab = (Get(a, "name") ?? "").Trim();
                    if (tab.Equals("overdue", StringComparison.OrdinalIgnoreCase)) tab = "Today";
                    return _w.OpenTab(tab) ? "Opened the " + tab + " tab." : "There is no tab called " + tab + ". Tabs: " + string.Join(", ", _w.TabNames) + ".";
                }

                case "open_feature_request":
                {
                    Speech.Muted = false;
                    _w.RequestFeature(Get(a, "idea") ?? "");
                    await Task.Delay(300);
                    return "Opened a new feature request on GitHub in her browser.";
                }
            }
            throw new Exception("No tool called " + name + ".");
        }

        private static string Names(List<TaskItem> tasks) => tasks.Count <= 4 ? string.Join(", ", tasks.Select(t => t.Name)) : string.Join(", ", tasks.Take(3).Select(t => t.Name)) + " and " + (tasks.Count - 3) + " more";

        private TaskList ListNamed(string name) => ListNamed(S, name);
        private static TaskList ListNamed(Store store, string name) =>
            store.Data.Lists.FirstOrDefault(l => l.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? throw new Exception("No list called " + name + ". Lists: " + string.Join(", ", store.Data.Lists.Where(l => !l.Archived).Select(l => l.Name)) + ".");

        /// <summary>Puts the fields Claude gave onto a task. Throws, saying what is wrong, before changing anything it cannot.</summary>
        public static void ApplyFields(Store store, TaskItem t, IReadOnlyDictionary<string, JsonElement> a, DateTime now)
        {
            if (Get(a, "list") is string ln && ln.Trim().Length > 0)
            {
                var l = ListNamed(store, ln);
                if (l.Name != t.List) { t.List = l.Name; if (!l.Sections.Contains(t.Section)) t.Section = ""; }
            }
            var list = store.ListOf(t);
            if (Get(a, "name") is string n && n.Trim().Length > 0) t.Name = n.Trim();
            if (Get(a, "category") is string c)
            {
                c = c.Trim();
                if (c.Length == 0 || c.Equals("none", StringComparison.OrdinalIgnoreCase)) t.Section = "";
                else t.Section = list.Sections.FirstOrDefault(s => s.Equals(c, StringComparison.OrdinalIgnoreCase))
                    ?? throw new Exception(list.Name + " has no category called " + c + ". Its categories: " + (list.Sections.Count == 0 ? "none" : string.Join(", ", list.Sections)) + ". manage_list can add it.");
            }
            if (Get(a, "tag") is string tag)
            {
                tag = tag.Trim();
                var known = store.Data.Tags.FirstOrDefault(x => x.Name.Equals(tag, StringComparison.OrdinalIgnoreCase));
                if (tag.Length > 0 && known == null) { tag = tag.ToUpperInvariant(); store.Data.Tags.Add(new TagInfo { Name = tag }); }
                else if (known != null) tag = known.Name;
                t.Tag = tag;
            }
            if (Get(a, "status") is string st)
            {
                var s = StatusOf(st);
                if (s != t.Status) { t.Status = s; t.StatusSince = now; t.DoneAt = s == Status.Done ? now : null; }
            }
            if (GetInt(a, "priority") is int p) t.Priority = Math.Clamp(p, 0, list.Scale.Length);
            if (Get(a, "due") is string due)
            {
                if (due.Trim().Length == 0 || due.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) { t.Due = null; t.DueHasTime = false; }
                else if (ParseWhen(due, now) is (DateTime d, bool time)) { t.Due = d; t.DueHasTime = time; }
                else throw new Exception("Could not read the due date " + due + ". Use YYYY-MM-DD.");
            }
            if (Get(a, "do_date") is string doDate)
            {
                if (doDate.Trim().Length == 0 || doDate.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) t.DoDate = null;
                else if (ParseWhen(doDate, now) is (DateTime d, _)) t.DoDate = d.Date;
                else throw new Exception("Could not read the do date " + doDate + ". Use YYYY-MM-DD.");
            }
            if (Get(a, "size") is string size)
                t.Size = size.ToLowerInvariant() switch { "small" => Size.Small, "large" => Size.Large, "multi_day" or "multi-day" or "multiday" => Size.MultiDay, _ => Size.Medium };
            if (Get(a, "type") is string type)
                t.Type = type.ToLowerInvariant() switch { "assignment" => TaskType.Assignment, "test" => TaskType.Test, "meeting" => TaskType.Meeting, "event" => TaskType.Event, "personal_goal" => TaskType.PersonalGoal, _ => TaskType.General };
            if (Get(a, "energy") is string energy)
                t.Energy = energy.ToLowerInvariant() switch { "easy" => Energy.Easy, "brain_heavy" or "brain-heavy" => Energy.BrainHeavy, _ => Energy.Unset };
            if (Get(a, "repeat") is string repeat)
            {
                if (repeat.Trim().Length == 0 || repeat.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) t.Repeat = "";
                else t.Repeat = RepeatRule.Parse(repeat)?.Describe() ?? throw new Exception("Could not read the repeat " + repeat + ". Try every Monday or every 2 weeks.");
            }
            if (Get(a, "notes") is string notes) t.Notes = notes.Trim();
            if (GetBool(a, "quick_win") is bool qw) t.QuickWin = qw;
            if (GetInt(a, "grade_weight") is int gw) t.GradeWeight = Math.Clamp(gw, 0, 100);
            t.IsMeeting = t.Type == TaskType.Meeting || t.List == "Meetings";
            t.TouchedAt = now;
        }

        private string ManageList(IReadOnlyDictionary<string, JsonElement> a)
        {
            string name = (Get(a, "list") ?? "").Trim();
            if (name.Length == 0) throw new Exception("No list name given.");
            S.Remember("Bunny changing the list " + name);
            var l = S.Data.Lists.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (l == null)
            {
                if (GetBool(a, "create") != true) throw new Exception("No list called " + name + ". Set create to true to make it. Lists: " + string.Join(", ", S.Data.Lists.Select(x => x.Name)) + ".");
                l = new TaskList { Name = name };
                S.Data.Lists.Add(l);
            }
            if (Get(a, "new_name") is string nn && nn.Trim().Length > 0 && nn.Trim() != l.Name)
            {
                string fresh = nn.Trim();
                if (S.Data.Lists.Any(x => x != l && x.Name.Equals(fresh, StringComparison.OrdinalIgnoreCase))) throw new Exception("There is already a list called " + fresh + ".");
                foreach (var t in S.Data.Tasks.Where(t => t.List == l.Name)) t.List = fresh;
                foreach (var sub in S.Data.Lists.Where(x => x.Parent == l.Name)) sub.Parent = fresh;
                l.Name = fresh;
            }
            if (Get(a, "scale") is string scale && scale.Trim().Length > 0)
            {
                scale = scale.Trim();
                if (scale.Equals("none", StringComparison.OrdinalIgnoreCase)) { l.Scale = Array.Empty<string>(); l.ScaleName = ""; }
                else if (scale.Equals("custom", StringComparison.OrdinalIgnoreCase))
                {
                    var levels = GetList(a, "custom_levels").Select(x => x.Trim()).ToArray();
                    if (levels.Length < 2) throw new Exception("A custom scale needs at least two levels in custom_levels.");
                    l.Scale = levels;
                    l.ScaleName = (Get(a, "custom_name") ?? "Custom").Trim();
                }
                else
                {
                    var lib = Scales.Library.FirstOrDefault(s => s.Name.Equals(scale, StringComparison.OrdinalIgnoreCase));
                    if (lib.Levels == null) throw new Exception("No scale called " + scale + ". Scales: " + string.Join(", ", Scales.Library.Select(s => s.Name)) + ", Custom, or none.");
                    l.Scale = lib.Levels;
                    l.ScaleName = lib.Name;
                }
                foreach (var t in S.Data.Tasks.Where(t => t.List == l.Name && t.Priority > l.Scale.Length)) t.Priority = l.Scale.Length;
            }
            if (Get(a, "default_status") is string ds) l.DefaultStatus = StatusOf(ds) is var s && s != Status.Done ? s : Status.ThisWeek;
            if (GetBool(a, "show_in_today") is bool inToday) l.InToday = inToday;
            if (GetBool(a, "archived") is bool arch) l.Archived = arch;
            foreach (var c in GetList(a, "add_categories"))
                if (!l.Sections.Any(s => s.Equals(c.Trim(), StringComparison.OrdinalIgnoreCase))) l.Sections.Add(c.Trim());
            foreach (var c in GetList(a, "remove_categories"))
            {
                var sec = l.Sections.FirstOrDefault(s => s.Equals(c.Trim(), StringComparison.OrdinalIgnoreCase));
                if (sec == null) continue;
                l.Sections.Remove(sec);
                foreach (var t in S.Data.Tasks.Where(t => t.List == l.Name && t.Section == sec)) t.Section = "";
            }
            _w.SaveAndRefresh();
            return "List " + l.Name + ": scale " + (l.Scale.Length == 0 ? "none" : l.ScaleName + " (" + string.Join(", ", l.Scale) + ")") +
                ", categories " + (l.Sections.Count == 0 ? "none" : string.Join(", ", l.Sections)) + ", new tasks start as " + Words.Of(l.DefaultStatus) +
                (l.InToday ? "" : ", kept out of Today") + (l.Archived ? ", archived" : "") + ".";
        }

        private string ChangeSettings(IReadOnlyDictionary<string, JsonElement> a)
        {
            var c = Cfg;
            var done = new List<string>();
            var focus = Features.Get<FocusTools>();
            foreach (var key in a.Keys)
            {
                bool? b = GetBool(a, key);
                int? n = GetInt(a, key);
                switch (key)
                {
                    case "sounds_on" when b != null: c.SoundsOn = b.Value; break;
                    case "sound_volume" when n != null: c.SoundVolume = Math.Clamp(n.Value, 10, 100); break;
                    case "music_on" when b != null: c.MusicOn = b.Value; break;
                    case "music_volume" when n != null: c.MusicVolume = Math.Clamp(n.Value, 1, 50); break;
                    case "short_mode" when b != null: c.ShortMode = b.Value; break;
                    case "low_energy_mode" when b != null: if (focus != null) focus.SetLow(b.Value, false); else c.LowEnergy = b.Value; break;
                    case "shuffle_mode" when b != null: if (focus != null) focus.SetShuffle(b.Value); else c.ShuffleMode = b.Value; break;
                    case "roast_level" when n != null: c.RoastLevel = Math.Clamp(n.Value, 0, 4); break;
                    case "emojis_on" when b != null: c.EmojisOn = b.Value; break;
                    case "finals_mode" when b != null: c.FinalsMode = b.Value; break;
                    case "morning_check_in" when b != null: c.MorningCheckIn = b.Value; break;
                    case "warn_when_urgent_more_than" when n != null: c.UrgentWarnAbove = Math.Clamp(n.Value, 0, 15); break;
                    case "hours_available" when n != null: S.Remember("Bunny changing your usual hours"); S.Data.HoursAvailable = Math.Clamp(n.Value, 1, 14); break;
                    case "keep_in_tray" when b != null: c.KeepInTray = b.Value; break;
                    case "follow_up_days" when n != null: c.FollowUpDays = Math.Clamp(n.Value, 1, 30); break;
                    case "check_for_updates" when b != null: c.CheckForUpdatesAtStart = b.Value; break;
                    case "bunny_check_in_hours" when n != null: c.BunnyCheckHours = Math.Clamp(n.Value, 0, 24); break;
                    case "bunny_asks_before_deleting" when b != null: c.BunnyAskBeforeDelete = b.Value; break;
                    default: throw new Exception("Cannot change " + key + ". Settings that can change: " + string.Join(", ", SettingValues().Keys) + ".");
                }
                done.Add(key + " " + SettingValues()[key]);
            }
            if (done.Count == 0) throw new Exception("No settings given.");
            _w.SettingsChanged();
            return "Changed: " + string.Join(", ", done) + ".";
        }

        /// <summary>Bunny asks her something in a small window: the question, a button per choice, and a box for her own answer.</summary>
        private string Ask(string question, List<string> choices, bool typing)
        {
            if (question.Trim().Length == 0) throw new Exception("No question given.");
            bool wasMuted = Speech.Muted;
            Speech.Muted = false;
            try
            {
                question = Clean(question);
                var w = Layout.Dialog(_w, "Bunny asks");
                string? answer = null;
                var text = new TextBox { Text = question, IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Width = 420, Margin = new Thickness(0, 0, 0, 8) };
                AutomationProperties.SetName(text, "Bunny asks");
                var panel = new StackPanel { Margin = new Thickness(14) };
                panel.Children.Add(text);
                var buttons = new WrapPanel { Width = 420 };
                Button? first = null;
                foreach (var choice in choices.Take(6))
                {
                    var b = Layout.Button(Clean(choice).Replace("_", "__"));
                    b.Click += (_, _) => { answer = choice; w.DialogResult = true; };
                    buttons.Children.Add(b);
                    first ??= b;
                }
                if (buttons.Children.Count > 0) panel.Children.Add(buttons);
                var box = new TextBox();
                if (typing)
                {
                    panel.Children.Add(Layout.Row(choices.Count > 0 ? "Or _type your own answer" : "_Your answer", box, 420));
                    box.PreviewKeyDown += (_, e) => { if (e.Key == Key.Enter && box.Text.Trim().Length > 0) { e.Handled = true; answer = box.Text.Trim(); w.DialogResult = true; } };
                }
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (typing)
                {
                    var send = Layout.Button("_Send");
                    send.Click += (_, _) => { if (box.Text.Trim().Length == 0) { Speech.Say(w, "Type an answer first, or pick one of the buttons.", true); box.Focus(); return; } answer = box.Text.Trim(); w.DialogResult = true; };
                    row.Children.Add(send);
                }
                row.Children.Add(Layout.Button("Not now", isCancel: true));
                panel.Children.Add(row);
                w.Content = panel;
                w.Loaded += (_, _) =>
                {
                    text.Focus();
                    text.CaretIndex = 0;
                    Speech.Say(w, question + (choices.Count > 0 ? " Tab to the answers." : ""), true);
                };
                w.ShowDialog();
                return answer == null ? "She closed the window without answering." : "She answered: " + answer;
            }
            finally { Speech.Muted = wasMuted; }
        }

        // ---- Settings ----

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) => new[] { new Page(store, settings) };

        private sealed class Page : ISettingsPage
        {
            private readonly Store _store;
            private readonly Settings _cfg;
            private readonly PasswordBox _key = new();
            private readonly ComboBox _check = new();
            private readonly CheckBox _askDelete, _dates;
            private readonly StackPanel _panel = new();
            private bool _clearNotes;
            private static readonly int[] Hours = { 0, 1, 2, 3, 4, 6, 8 };

            public string Name => PageName;
            public FrameworkElement Content => _panel;

            public Page(Store store, Settings cfg)
            {
                _store = store;
                _cfg = cfg;
                var about = new TextBox
                {
                    IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, BorderThickness = new Thickness(0), Width = 440,
                    Text = "Smarter Bunny lets you talk to Bunny in the Talk to Bunny box on the main screen. Bunny can add and change tasks, plan your week, check on you, and change settings for you. " +
                           "It uses Claude, by Anthropic, with your own Claude API key. The API is pay as you go and separate from a Claude plan: get a key at console.anthropic.com, under API keys. " +
                           "The key is kept encrypted on this computer, for your Windows account only. Your tasks are sent to Claude when you talk to Bunny or it checks on you.",
                };
                AutomationProperties.SetName(about, "About Smarter Bunny");
                _panel.Children.Add(about);
                string saved = Unprotect(cfg.BunnyKey);
                _key.Password = saved;
                AutomationProperties.SetHelpText(_key, saved.Length > 0 ? "A key is saved. Paste a new one to replace it." : "Paste your Claude API key here. It starts with sk-ant.");
                _panel.Children.Add(Layout.Row("Claude _API key", _key, 440));
                var buttons = new WrapPanel { Width = 440 };
                var test = Layout.Button("_Test the key");
                test.Click += async (_, _) => await Test(test);
                var get = Layout.Button("_Get a key");
                get.Click += (_, _) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://console.anthropic.com/settings/keys") { UseShellExecute = true })?.Dispose(); }
                    catch { }
                    Speech.Say(_panel, "Opening the Claude Console in your browser. Make a key, copy it, and paste it in the API key box.", true);
                };
                var forget = Layout.Button("_Forget the key");
                forget.Click += (_, _) => { _key.Clear(); Speech.Say(_panel, "Key cleared. Press OK to save that.", true); _key.Focus(); };
                foreach (var b in new[] { test, get, forget }) buttons.Children.Add(b);
                _panel.Children.Add(buttons);

                _check.ItemsSource = Hours.Select(h => h == 0 ? "Never" : h == 1 ? "Every hour" : "Every " + h + " hours").ToList();
                _check.SelectedIndex = Math.Max(0, Array.IndexOf(Hours, cfg.BunnyCheckHours) is int i && i >= 0 ? i : 3);
                AutomationProperties.SetHelpText(_check, "Bunny checks on you in a small window, between 9 in the morning and 9 at night, when Busy Bunny is in front.");
                _panel.Children.Add(Layout.Row("Bunny _checks on me", _check));
                _askDelete = new CheckBox { Content = "Ask me before Bunny _deletes a task", IsChecked = cfg.BunnyAskBeforeDelete, Margin = new Thickness(0, 6, 0, 4) };
                _dates = new CheckBox { Content = "Bunny reads dates the task form does not under_stand", IsChecked = cfg.BunnyReadsDates, Margin = new Thickness(0, 4, 0, 4) };
                _panel.Children.Add(_askDelete);
                _panel.Children.Add(_dates);

                var notes = new TextBox
                {
                    IsReadOnly = true, IsReadOnlyCaretVisible = true, TextWrapping = TextWrapping.Wrap, Width = 440, MaxHeight = 120, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Text = store.Data.BunnyNotes.Count == 0 ? "Nothing yet." : string.Join(Environment.NewLine, store.Data.BunnyNotes.Select((n, i) => (i + 1) + ". " + n)),
                };
                _panel.Children.Add(Layout.Row("What Bunny _remembers about you", notes, 440));
                var clear = Layout.Button("C_lear what Bunny remembers");
                clear.HorizontalAlignment = HorizontalAlignment.Left;
                clear.Click += (_, _) => { _clearNotes = true; notes.Text = "Nothing, once you press OK."; Speech.Say(_panel, "Bunny will forget its notes when you press OK.", true); };
                _panel.Children.Add(clear);
            }

            private async Task Test(Button test)
            {
                string key = _key.Password.Trim();
                if (key.Length == 0) { Speech.Say(_panel, "Paste a key in the API key box first.", true); _key.Focus(); return; }
                test.IsEnabled = false;
                Speech.Say(_panel, "Asking Claude.", true);
                try
                {
                    var client = new AnthropicClient { ApiKey = key, Timeout = TimeSpan.FromSeconds(60) };
                    var r = await client.Messages.Create(new MessageCreateParams
                    {
                        Model = ModelId,
                        MaxTokens = 2000,
                        OutputConfig = new OutputConfig { Effort = Effort.Low },
                        Messages = [new UserMessageParam { Content = "You are Bunny, a cheerful helper in a to-do app. Say hello in one short sentence, no emoji." }],
                    });
                    string hi = Clean(string.Concat(r.Content.Select(b => b.Value).OfType<AText>().Select(t => t.Text)));
                    Speech.Say(_panel, "The key works. Bunny says: " + hi + " Press OK to save it.", true);
                }
                catch (Exception e) { Speech.Say(_panel, "That did not work. " + Problem(e), true); }
                finally { test.IsEnabled = true; test.Focus(); }
            }

            public string? Validate()
            {
                string key = _key.Password.Trim();
                if (key.Length > 0 && !key.StartsWith("sk-ant-")) { _key.Focus(); return "That does not look like a Claude API key. They start with sk-ant."; }
                return null;
            }

            public void Apply()
            {
                string key = _key.Password.Trim();
                if (key != Unprotect(_cfg.BunnyKey))
                {
                    _cfg.BunnyKey = Protect(key);
                    _cfg.BunnyLastCheck = DateTime.Now; // the first check-in comes later, not the moment it is set up
                }
                _cfg.BunnyCheckHours = Hours[Math.Max(0, _check.SelectedIndex)];
                _cfg.BunnyAskBeforeDelete = _askDelete.IsChecked == true;
                _cfg.BunnyReadsDates = _dates.IsChecked == true;
                if (_clearNotes) _store.Data.BunnyNotes.Clear();
                _cfg.Save();
            }
        }

        // ---- Self-test ----

        public void SelfTest(Store store)
        {
            void Check(bool ok, string what) { if (!ok) throw new Exception("Smarter Bunny: " + what); }
            Check(ToolList().Count == 16, "tool list");
            Check(Clean("**Hi** there # friend\n- one\n- two") == "Hi there  friend\none\ntwo", "clean gave " + Clean("**Hi** there # friend\n- one\n- two"));
            Check(ParseWhen("2026-11-03 14:30") == (new DateTime(2026, 11, 3, 14, 30, 0), true), "time");
            Check(ParseWhen("2026-11-03") == (new DateTime(2026, 11, 3), false), "date");
            Check(ParseWhen("none") == null, "none");
            Check(Unprotect(Protect("sk-ant-test")) == "sk-ant-test", "key round trip");

            var now = new DateTime(2026, 11, 2, 10, 0, 0);
            var st = new Store { PathOverride = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "busybunny-bunny-test.json") };
            st.Data.Lists = Sample.Lists();
            st.Data.Lists[0].Sections.Add("PSY 101");
            var t = new TaskItem { Name = "x", List = "Homework" };
            st.Data.Tasks.Add(t);
            JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();
            var fields = J("{\"name\":\"chapter 5 reading\",\"category\":\"psy 101\",\"tag\":\"psy\",\"priority\":9,\"due\":\"2026-11-06\",\"do_date\":\"2026-11-04\",\"size\":\"large\",\"type\":\"assignment\",\"repeat\":\"every friday\"}")
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
            ApplyFields(st, t, fields, now);
            Check(t.Name == "chapter 5 reading" && t.Section == "PSY 101" && t.Tag == "PSY" && t.Priority == 4 && t.Due == new DateTime(2026, 11, 6) && t.DoDate == new DateTime(2026, 11, 4)
                && t.Size == Size.Large && t.Type == TaskType.Assignment && t.Repeat == "every Friday", "fields gave " + st.Line(t, now));
            bool threw = false;
            try { ApplyFields(st, t, new Dictionary<string, JsonElement> { ["category"] = J("\"Nope\"") }, now); } catch { threw = true; }
            Check(threw && t.Section == "PSY 101", "an unknown category must be refused");
            Check(Find(st, new[] { t.Id.ToString("N")[..8] }).Single() == t, "find by short id");
            Check(StatusOf("this week") == Status.ThisWeek && StatusOf("done_not_submitted") == Status.DoneNotSubmitted, "status words");
        }
    }
}
