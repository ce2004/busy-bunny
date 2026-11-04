using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace BusyBunny
{
    /// <summary>What Busy Bunny remembers about one thing from school: the task it became (none when she left it out), and the due date and turned-in state last seen.</summary>
    public sealed class SchoolSeen
    {
        public Guid? Task { get; set; }
        public DateTime? Due { get; set; }
        public bool Submitted { get; set; }
    }

    /// <summary>The school connection's settings and bookkeeping, kept in world.json's Kept part, so Control Z never brings back an item already seen.</summary>
    public sealed class SchoolState
    {
        /// <summary>Canvas or Moodle.</summary>
        public string System { get; set; } = "Moodle";
        public string Address { get; set; } = SchoolAddress.MoodleDefault;
        /// <summary>Her token, encrypted with DPAPI for her Windows account. Never logged, never sent anywhere but Address.</summary>
        public string Token { get; set; } = "";
        public bool Daily { get; set; } = true;
        public bool BunnyMayRead { get; set; } = true;
        public bool FollowSubmitted { get; set; }
        public string List { get; set; } = "";
        /// <summary>She approved a checklist once: from then on the daily check adds new due dates by itself.</summary>
        public bool Approved { get; set; }
        /// <summary>The day of the last automatic check, yyyy-MM-dd.</summary>
        public string LastCheck { get; set; } = "";
        public string LastResult { get; set; } = "";
        public Dictionary<string, SchoolSeen> Known { get; set; } = new();
    }

    /// <summary>
    /// School connection (Morgan's sweep 12.6, item 8): reads her classes from Canvas or Moodle with her own token and
    /// brings the due dates in as tasks. The first time she approves them in a checklist; after that the daily check
    /// (the Ticker, at most once a day, only while Busy Bunny runs) adds new ones by itself as one Control Z step and
    /// says one short line through Calm. A due date the school moves updates the task (her name and notes stay); when
    /// she asks, a task the school says she turned in is marked done. A task is never deleted when the school drops an
    /// item. Read only: nothing is ever submitted, posted or changed at the school. Smarter Bunny reads it too (SchoolBunny.cs).
    /// </summary>
    public sealed class SchoolConnection : IFeature
    {
        public const string Id = "schoolconnection", PageName = "School connection", KeptKey = "school.connection";
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("Busy Bunny, School connection");
        private MainWindow _w = null!;
        private static bool _busy;

        public void Attach(MainWindow w)
        {
            _w = w;
            Tools.Register(new BunnyTool(Id, "School connection", Drawers.Plan, "Brings due dates from Canvas or Moodle into your lists, and lets Bunny read your classes, grades and announcements.", Run) { Since = "0.15.0" });
            Ticker.Attach(w);
            Ticker.Every("school.daily", DailyDue, now => _ = Daily(now));
        }

        // ---- Her settings and token ----

        public static SchoolState State(Store s) => s.World.GetKept<SchoolState>(KeptKey) is var st && st.Known != null ? st : new SchoolState();
        public static void SaveState(Store s, SchoolState st) { s.World.SetKept(KeptKey, st); s.World.Save(); }

        public static string Protect(string token) =>
            token.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), Entropy, DataProtectionScope.CurrentUser));
        public static string Unprotect(string saved)
        {
            if (saved.Length == 0) return "";
            try { return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(saved), Entropy, DataProtectionScope.CurrentUser)); }
            catch (Exception e) when (e is CryptographicException or FormatException) { return ""; }
        }

        public static bool Ready(SchoolState st) => SchoolAddress.Parse(st.Address) != null && Unprotect(st.Token).Length > 0;
        public static string SystemName(SchoolState st) => st.System == "Canvas" ? "Canvas" : "Moodle";
        public static ISchoolSource Source(string system, string address, string token) =>
            system == "Canvas" ? new CanvasSource(address, token) : new MoodleSource(address, token);
        public static ISchoolSource Source(SchoolState st) => Source(SystemName(st), st.Address, Unprotect(st.Token));

        /// <summary>A line for her error log: what failed and a plain reason, never a token, a password or the exception's own text.</summary>
        public static string LogText(string what, Exception e, params string[] secrets)
        {
            string s = "School connection, " + what + ": " + (e is SchoolProblem p ? p.Message : e.GetType().Name);
            return Scrub(s, secrets);
        }

        public static string Scrub(string s, params string[] secrets)
        {
            foreach (string x in secrets) if (x.Length >= 4) s = s.Replace(x, "(hidden)");
            return s;
        }

        private static void Log(string what, Exception e, SchoolState st) => ErrorLog.Write(LogText(what, e, Unprotect(st.Token)));

        // ---- Turning school work into tasks ----

        public sealed class SyncPlan
        {
            public List<(SchoolWork Work, TaskItem Task, bool On)> New = new();
            /// <summary>Already on her list (same name and day): only remembered, so its date follows the school from now on.</summary>
            public List<(SchoolWork Work, TaskItem Task)> Linked = new();
            public List<(SchoolWork Work, TaskItem Task)> Moved = new();
            public List<(SchoolWork Work, TaskItem Task)> Finished = new();
            /// <summary>Seen before with nothing to change on her list; the due date and turned-in state seen are updated.</summary>
            public List<SchoolWork> Seen = new();
            public List<TagInfo> NewTags = new();
        }

        public sealed class SyncResult
        {
            public List<TaskItem> Added = new(), Moved = new(), Finished = new();
            public List<TagInfo> Tags = new();
            /// <summary>Each task marked done and the status it had, for the events a status change raises.</summary>
            public List<(TaskItem Task, Status Old)> FinishedFrom = new();
            public bool Changed => Added.Count + Moved.Count + Finished.Count > 0;
        }

        public static TaskList ListFor(Store store, SchoolState st) =>
            store.Data.Lists.FirstOrDefault(l => !l.Archived && l.Name == st.List) ?? ImportExport.DefaultList(store);

        private static readonly Regex CodeLike = new(@"\b([A-Za-z]{2,5})[\s\-_.]*(\d{2,4}[A-Za-z]?)\b", RegexOptions.Compiled);
        private static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase) { "and", "the", "for", "to", "of", "in", "an", "a", "on", "with", "section" };

        /// <summary>
        /// Her tag for a course: one whose class description matches the course name, or whose name matches the course's
        /// short code (PSY for PSY-111-01). Null when none matches.
        /// </summary>
        public static TagInfo? MatchTag(IEnumerable<TagInfo> tags, string course, string code)
        {
            string nc = SchoolText.Squash(course), nk = SchoolText.Squash(code);
            var list = tags.Where(t => t.Name.Length > 0).ToList();
            var byDesc = list.FirstOrDefault(t => SchoolText.Squash(t.Description) is var d && d.Length >= 3 && (d == nc || d == nk || (d.Length >= 6 && nc.Contains(d))));
            if (byDesc != null) return byDesc;
            var words = Regex.Split(course + " " + code, @"[^A-Za-z0-9]+").Where(x => x.Length > 0).Select(x => x.ToUpperInvariant()).ToHashSet();
            string codeFrom = CodeLike.Match(code) is { Success: true } m ? (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant()
                : CodeLike.Match(course) is { Success: true } m2 ? (m2.Groups[1].Value + m2.Groups[2].Value).ToUpperInvariant() : "";
            return list.Where(t => SchoolText.Squash(t.Name) is var n && n.Length >= 2 && (n == codeFrom || n == nk || words.Contains(n) || (nk.Length > 0 && nk.StartsWith(n) && char.IsDigit(nk[Math.Min(n.Length, nk.Length - 1)]))))
                .OrderByDescending(t => t.Name.Length).FirstOrDefault();
        }

        /// <summary>A short new tag for a course: its code like PSY111, or the first letters of its words. Never one she already has.</summary>
        public static string MakeTag(string course, string code, IEnumerable<string> taken)
        {
            string tag = "";
            foreach (string s in new[] { code, course })
                if (tag.Length == 0 && CodeLike.Match(s) is { Success: true } m) tag = (m.Groups[1].Value + m.Groups[2].Value).ToUpperInvariant();
            if (tag.Length == 0)
            {
                var words = Regex.Split(course, @"[^A-Za-z0-9]+").Where(w => w.Length > 0 && !Small.Contains(w)).ToList();
                tag = words.Count >= 2 ? new string(words.Take(5).Select(w => char.ToUpperInvariant(w[0])).ToArray()) : SchoolText.Squash(course);
            }
            if (tag.Length > 12) tag = tag[..12];
            if (tag.Length == 0) tag = "CLASS";
            var have = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
            string pick = tag;
            for (int i = 2; have.Contains(pick); i++) pick = tag + i;
            return pick;
        }

        /// <summary>What a check would do, without changing anything: new items, moved due dates, items turned in.</summary>
        public static SyncPlan Plan(Store store, SchoolState st, IEnumerable<SchoolWork> work, DateTime now, string system)
        {
            var p = new SyncPlan();
            var list = ListFor(store, st);
            var tagFor = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string TagOf(SchoolWork w)
            {
                string course = w.Course.Length > 0 ? w.Course : w.CourseCode;
                if (course.Length == 0) return "";
                if (tagFor.TryGetValue(course, out var t)) return t;
                var have = MatchTag(store.Data.Tags.Concat(p.NewTags), course, w.CourseCode);
                if (have == null)
                {
                    have = new TagInfo { Name = MakeTag(course, w.CourseCode, store.Data.Tags.Select(x => x.Name).Concat(p.NewTags.Select(x => x.Name))), Description = w.Course.Length > 0 ? w.Course : course };
                    p.NewTags.Add(have);
                }
                return tagFor[course] = have.Name;
            }
            var byId = store.Data.Tasks.GroupBy(t => t.Id).ToDictionary(g => g.Key, g => g.First());
            foreach (var w in work)
            {
                if (w.Due == null || w.Key.Length == 0 || w.Name.Length == 0) continue;
                if (st.Known.TryGetValue(w.Key, out var seen))
                {
                    // Gone from her list (she deleted it, or undid it) or left out: never added again.
                    if (seen.Task is not Guid id || !byId.TryGetValue(id, out var task) || task.Status == Status.Done) { p.Seen.Add(w); continue; }
                    bool moved = w.Due != seen.Due && task.Due != w.Due;
                    bool finished = st.FollowSubmitted && w.Submitted && !seen.Submitted;
                    if (moved) p.Moved.Add((w, task));
                    if (finished) p.Finished.Add((w, task));
                    if (!moved && !finished) p.Seen.Add(w);
                    continue;
                }
                var due = w.Due.Value;
                var t = new TaskItem
                {
                    Name = w.Name, List = list.Name, Due = w.HasTime ? due : due.Date, DueHasTime = w.HasTime,
                    Type = w.IsTest ? TaskType.Test : TaskType.Assignment, Size = w.IsTest ? Size.Large : Size.Medium,
                    Status = due.Date <= Store.WeekEnd(now) ? Status.ThisWeek : Status.NextWeek, StatusSince = now,
                    Notes = "From " + system + (w.Course.Length > 0 ? ", " + w.Course : "") + ".",
                };
                if (store.Data.Tasks.FirstOrDefault(x => x.List == t.List && x.Name.Trim().Equals(t.Name.Trim(), StringComparison.OrdinalIgnoreCase) && x.Due?.Date == t.Due?.Date) is TaskItem mine)
                {
                    p.Linked.Add((w, mine));
                    continue;
                }
                t.Tag = TagOf(w);
                p.New.Add((w, t, due >= now && !w.Submitted));
            }
            // Only tags a new task uses are offered.
            p.NewTags = p.NewTags.Where(g => p.New.Any(n => n.Task.Tag == g.Name)).ToList();
            return p;
        }

        /// <summary>
        /// Does the plan as one Control Z step: adds the new items (the chosen ones, or with chosen null the ones that start
        /// checked), moves due dates, marks turned-in tasks done. What she left out is remembered so it never comes back.
        /// </summary>
        public static SyncResult Apply(Store store, SchoolState st, SyncPlan p, IReadOnlyCollection<int>? chosen, string what, DateTime now)
        {
            var r = new SyncResult();
            var take = p.New.Select((n, i) => (n, i)).Where(x => chosen == null ? x.n.On : chosen.Contains(x.i)).Select(x => x.i).ToHashSet();
            if (take.Count + p.Moved.Count + p.Finished.Count > 0) store.Remember(what);
            for (int i = 0; i < p.New.Count; i++)
            {
                var (w, t, _) = p.New[i];
                // Added meanwhile by another check: never twice.
                if (st.Known.ContainsKey(w.Key)) continue;
                if (!take.Contains(i)) { st.Known[w.Key] = new SchoolSeen { Due = w.Due, Submitted = w.Submitted }; continue; }
                store.Data.Tasks.Add(t);
                if (t.Tag.Length > 0 && !store.Data.Tags.Any(x => x.Name.Equals(t.Tag, StringComparison.OrdinalIgnoreCase)))
                {
                    var tag = p.NewTags.FirstOrDefault(x => x.Name == t.Tag) ?? new TagInfo { Name = t.Tag };
                    store.Data.Tags.Add(tag);
                    r.Tags.Add(tag);
                }
                st.Known[w.Key] = new SchoolSeen { Task = t.Id, Due = w.Due, Submitted = w.Submitted };
                r.Added.Add(t);
            }
            foreach (var (w, t) in p.Linked) st.Known[w.Key] = new SchoolSeen { Task = t.Id, Due = w.Due, Submitted = w.Submitted };
            foreach (var (w, t) in p.Moved)
            {
                t.Due = w.HasTime ? w.Due : w.Due!.Value.Date;
                t.DueHasTime = w.HasTime;
                if (t.Status == Status.NextWeek && t.Due!.Value.Date <= Store.WeekEnd(now)) t.Status = Status.ThisWeek;
                r.Moved.Add(t);
            }
            foreach (var (w, t) in p.Finished)
            {
                if (t.Status == Status.Done) continue;
                var old = t.Status;
                t.Status = Status.Done;
                t.DoneAt = now;
                t.StatusSince = now;
                t.TouchedAt = now;
                // The same points path a status change takes: pays once, and pays the other half of Done not submitted.
                GameFeature.Finish(store, t, old, now);
                r.Finished.Add(t);
                r.FinishedFrom.Add((t, old));
            }
            foreach (var w in p.Seen.Concat(p.Moved.Select(m => m.Work)).Concat(p.Finished.Select(f => f.Work)))
                if (st.Known.TryGetValue(w.Key, out var seen))
                {
                    seen.Due = w.Due;
                    if (st.FollowSubmitted) seen.Submitted = w.Submitted;
                }
            SaveState(store, st);
            return r;
        }

        /// <summary>The events a hand status change raises, after the change is saved: bets, streak extras and the rest see it once.</summary>
        private static void RaiseFinished(SyncResult r)
        {
            foreach (var (t, old) in r.FinishedFrom)
            {
                GameEvents.RaiseStatusChanged(t, old);
                if (old is not (Status.Done or Status.DoneNotSubmitted)) GameEvents.RaiseTaskDone(t);
            }
        }

        private static string Result(SchoolState st, string line) => Scrub(line, Unprotect(st.Token));

        private static string Names(List<TaskItem> ts) => ts.Count <= 3 ? Join(ts.Select(t => t.Name).ToList()) : string.Join(", ", ts.Take(2).Select(t => t.Name)) + " and " + (ts.Count - 2) + " more";
        private static string Join(List<string> xs) => xs.Count <= 1 ? string.Join("", xs) : string.Join(", ", xs.Take(xs.Count - 1)) + " and " + xs[^1];

        /// <summary>One short line for what a check did, like "2 new due dates from Moodle."</summary>
        public static string Line(SyncResult r, string system, string? listName = null)
        {
            var parts = new List<string>();
            if (r.Added.Count > 0) parts.Add(SmartUi.Count(r.Added.Count, "new due date") + " from " + system + (listName != null ? ", on " + listName : ""));
            if (r.Moved.Count == 1) parts.Add(system + " moved " + r.Moved[0].Name + " to " + SchoolText.When(r.Moved[0].Due, r.Moved[0].DueHasTime));
            else if (r.Moved.Count > 1) parts.Add(system + " moved " + r.Moved.Count + " due dates: " + Names(r.Moved));
            if (r.Finished.Count > 0) parts.Add(Names(r.Finished) + (r.Finished.Count == 1 ? " is" : " are") + " marked done, because " + system + " says you turned " + (r.Finished.Count == 1 ? "it" : "them") + " in");
            if (r.Tags.Count > 0) parts.Add((r.Tags.Count == 1 ? "New tag " : "New tags ") + Join(r.Tags.Select(t => t.Name + (t.Description.Length > 0 ? " for " + t.Description : "")).ToList()));
            return string.Join(". ", parts.Select(SmartText.Cap)) + (parts.Count > 0 ? "." : "");
        }

        /// <summary>Her courses and everything with a due date. Throws SchoolProblem with a plain line.</summary>
        public static async Task<List<SchoolWork>> FetchWork(SchoolState st, CancellationToken stop)
        {
            if (!Ready(st)) throw new SchoolProblem("There is no school connection yet. Set it up in Options, Settings, School connection.");
            var src = Source(st);
            var courses = await src.CoursesAsync(stop).ConfigureAwait(false);
            var work = await src.WorkAsync(courses, stop).ConfigureAwait(false);
            SchoolBunny.Keep(st, courses, work);
            return work;
        }

        // ---- The daily check ----

        private bool DailyDue(DateTime now)
        {
            if (_busy || now.Hour < 7 || !Tools.IsOn(Id)) return false;
            var st = State(_w.Store);
            return st.Approved && st.Daily && st.LastCheck != _w.Store.Key(now) && Ready(st);
        }

        private async Task Daily(DateTime now)
        {
            if (_busy) return;
            _busy = true;
            var store = _w.Store;
            var st = State(store);
            try
            {
                st.LastCheck = store.Key(now);
                SaveState(store, st);
                List<SchoolWork> work;
                try { work = await FetchWork(st, CancellationToken.None); }
                catch (SchoolProblem p)
                {
                    st = State(store);
                    st.LastResult = Result(st, "The last check, " + now.ToString("dddd h:mm tt", CultureInfo.InvariantCulture) + ", did not work: " + p.Message);
                    SaveState(store, st);
                    Log("daily check", p, st);
                    return;
                }
                // Settings may have changed while it waited.
                var fresh = State(store);
                fresh.LastCheck = store.Key(now);
                string system = SystemName(fresh);
                var plan = Plan(store, fresh, work, DateTime.Now, system);
                var r = Apply(store, fresh, plan, null, "new due dates from " + system, DateTime.Now);
                fresh.LastResult = Result(fresh, "Checked " + now.ToString("dddd h:mm tt", CultureInfo.InvariantCulture) + ". " + (r.Changed ? Line(r, system) : "Nothing new."));
                SaveState(store, fresh);
                if (!r.Changed) return;
                _w.SaveAndRefresh();
                RaiseFinished(r);
                Calm.SayUnprompted(_w, Line(r, system));
            }
            catch (Exception e) { Log("daily check", e, st); }
            finally { _busy = false; }
        }

        // ---- When she asks ----

        private void Run()
        {
            if (!Ready(State(_w.Store))) { _w.OpenSettingsAt(PageName); return; }
            CheckNow();
        }

        public async void CheckNow()
        {
            if (_busy) { _w.Say("Busy Bunny is already checking your school.", true); return; }
            var store = _w.Store;
            var st = State(store);
            if (!Ready(st)) { _w.Say("There is no school connection yet. Set it up in Options, Settings, School connection.", true); return; }
            string system = SystemName(st);
            var back = _w.KeepFocus();
            // Busy from the fetch to the last save, so the daily check cannot run while the checklist is open.
            _busy = true;
            try { await CheckNowBusy(store, st, system, back); }
            finally { _busy = false; }
        }

        private async Task CheckNowBusy(Store store, SchoolState st, string system, Action back)
        {
            List<SchoolWork> work;
            try
            {
                work = await SmartUi.Waiting(_w, "Checking " + system, "Busy Bunny is checking " + system + " for due dates. Stop ends it.", "Still checking " + system + ".", stop => FetchWork(st, stop));
            }
            catch (SmartUi.StoppedByHand e) { _w.Say(e.Message + " Nothing changed.", true); back(); return; }
            catch (OperationCanceledException) { _w.Say("Stopped. Nothing changed.", true); back(); return; }
            catch (SchoolProblem p) { Log("check", p, st); _w.Say(Result(st, p.Message), true); back(); return; }
            catch (Exception e) { Log("check", e, st); _w.Say("The school check did not work just now. Nothing changed.", true); back(); return; }

            var now = DateTime.Now;
            st = State(store);
            var plan = Plan(store, st, work, now, system);
            var list = ListFor(store, st);
            IReadOnlyCollection<int>? chosen = Array.Empty<int>();
            if (plan.New.Count > 0)
            {
                string tags = plan.NewTags.Count == 0 ? "" : " New tags for your classes: " + Join(plan.NewTags.Select(t => t.Name + " for " + t.Description).ToList()) + ".";
                var picked = SmartUi.Checklist(_w, "Due dates from " + system + ", check the list",
                    "Each becomes a task on " + list.Name + ". Uncheck anything you do not want; dates that have passed and work you turned in start unchecked." +
                    (st.Approved ? "" : " After this, Busy Bunny checks once a day and adds new due dates by itself.") + tags,
                    plan.New.Select(n => (ItemText(store, n.Task, n.Work, now), n.On)).ToList(), "_Add checked", canSkip: true);
                if (picked.Cancelled) { _w.Say("Nothing was added.", true); back(); return; }
                // Skip, or nothing checked, still counts as approving: what she left out is remembered and not offered again.
                chosen = picked.Chosen;
            }
            // Read again: anything another check remembered meanwhile is skipped in Apply.
            bool approved = plan.New.Count > 0 || st.Approved;
            st = State(store);
            st.Approved = approved;
            var r = Apply(store, st, plan, chosen, "checking " + system, now);
            st.LastResult = Result(st, "Checked " + now.ToString("dddd h:mm tt", CultureInfo.InvariantCulture) + ". " + (r.Changed ? Line(r, system) : "Nothing new."));
            SaveState(store, st);
            if (r.Changed) _w.SaveAndRefresh();
            RaiseFinished(r);
            back();
            _w.Say(r.Changed ? Line(r, system, list.Name) + " Control Z undoes all of it at once." : "Nothing new from " + system + ". Your due dates are up to date.");
        }

        private static string ItemText(Store store, TaskItem t, SchoolWork w, DateTime now) =>
            store.Line(t, now) + (w.Course.Length > 0 ? ", " + w.Course : "") + (w.Submitted ? ", you turned it in" : t.Due < now ? ", that date has passed" : "");

        // ---- Settings, School connection ----

        public IEnumerable<ISettingsPage> SettingsPages(Store store, Settings settings) { yield return new Page(this, store); }

        public const string HelpText =
@"How to connect

Busy Bunny only reads: your classes, due dates, whether you turned things in, grades and announcements. It never submits, posts or changes anything at your school. Your token is saved encrypted for your Windows account, is sent only to your school's address, and is never written to any log.

Moodle (Furman's is at courses.furman.edu):
1. Sign in to Moodle in your web browser.
2. Open the user menu at the top right, with your name or picture, and choose Preferences.
3. Choose Security keys. Copy the key for Moodle mobile web service.
4. Paste it in the Access token box, then press Test the connection.
If there is no Security keys page, or no key there, press Sign in to Moodle once. You type your Moodle username and password; Busy Bunny sends them only to your school, gets a token for the Moodle app, and never keeps your password. If your school signs you in through a separate school page, this may not work.

Canvas:
1. Sign in to Canvas in your web browser.
2. Choose Account, then Settings.
3. Under Approved integrations, choose New access token. For Purpose, type Busy Bunny. You can leave the expiry date empty.
4. Choose Generate token. Canvas shows the token only once, so copy it right away.
5. Type your school's Canvas address in the School address box, like https://yourschool.instructure.com, paste the token, then press Test the connection.

If your school has turned off tokens, the school connection cannot work there, and Test the connection says so.";

        private sealed class Page : ISettingsPage
        {
            private readonly SchoolConnection _f;
            private readonly Store _store;
            private readonly StackPanel _panel = new();
            private readonly ComboBox _system = new() { ItemsSource = new[] { "Moodle", "Canvas" } };
            private readonly TextBox _address = new();
            private readonly PasswordBox _token = new();
            // A read-only box, so Tab and NVDA reach whether a token is saved and what the last check said.
            private readonly TextBox _saved = SmartUi.ReadOnly("Saved token and last check", "", 460, 60);
            private readonly CheckBox _daily, _follow, _bunny;
            private readonly ComboBox _list;
            private readonly Button _signIn, _test;
            private bool _remove, _testing;

            public string Name => PageName;
            public FrameworkElement Content => _panel;

            public Page(SchoolConnection f, Store store)
            {
                _f = f;
                _store = store;
                var st = State(store);
                _panel.Children.Add(SmartUi.Intro("Connect Busy Bunny to Canvas or Moodle with your own token. Due dates come in as tasks, you approve them the first time, and Bunny can read your classes when you ask. Read only: nothing is ever turned in or changed at your school.", 460));
                _system.SelectedItem = SystemName(st);
                _panel.Children.Add(Layout.Row("School _system", _system, 240));
                _address.Text = st.Address;
                AutomationProperties.SetHelpText(_address, "Like " + SchoolAddress.MoodleDefault + " for Moodle, or https://yourschool.instructure.com for Canvas.");
                _panel.Children.Add(Layout.Row("School _address", _address, 460));
                AutomationProperties.SetHelpText(_token, "Leave it empty to keep the saved token.");
                _panel.Children.Add(Layout.Row("Access _token", _token, 460));
                _panel.Children.Add(Layout.Row("Saved token and last check", _saved, 460));
                _hostSaved = SchoolAddress.Parse(st.Address)?.Host ?? "";
                _systemSaved = SystemName(st);
                var row = new WrapPanel { Width = 460 };
                Button Add(string text, Action act) { var b = Layout.Button(text); b.Click += (_, _) => act(); row.Children.Add(b); return b; }
                _test = Add("Test the _connection", () => _ = Test());
                _signIn = Add("Sign in to _Moodle once...", () => _ = SignIn());
                Add("_Remove the saved token", () =>
                {
                    _token.Clear();
                    _remove = true;
                    ShowSaved();
                    Speech.Say(_panel, "The saved token will be removed when you press OK.", true);
                });
                Add("Check _now", () => SettingsWindow.SaveAndThen(_f.CheckNow));
                _panel.Children.Add(row);
                _system.SelectionChanged += (_, _) =>
                {
                    bool moodle = (string)_system.SelectedItem == "Moodle";
                    _signIn.IsEnabled = moodle;
                    if (!moodle && _address.Text.Trim().TrimEnd('/') == SchoolAddress.MoodleDefault) _address.Text = "";
                    if (moodle && _address.Text.Trim().Length == 0) _address.Text = SchoolAddress.MoodleDefault;
                };
                _signIn.IsEnabled = SystemName(st) == "Moodle";
                _daily = new CheckBox { Content = "Check every _day and add new due dates by itself", IsChecked = st.Daily, Margin = new Thickness(0, 8, 0, 4) };
                _follow = new CheckBox { Content = "_Follow what the school says about submitted work: mark those tasks done", IsChecked = st.FollowSubmitted, Margin = new Thickness(0, 4, 0, 4) };
                _bunny = new CheckBox { Content = "Let _Bunny read my classes", IsChecked = st.BunnyMayRead, Margin = new Thickness(0, 4, 0, 4) };
                _panel.Children.Add(_daily);
                _panel.Children.Add(_follow);
                _panel.Children.Add(_bunny);
                var lists = store.Data.Lists.Where(l => !l.Archived).Select(l => l.Name).ToList();
                _list = new ComboBox { ItemsSource = lists, SelectedItem = ListFor(store, st).Name };
                if (_list.SelectedIndex < 0 && lists.Count > 0) _list.SelectedIndex = 0;
                _panel.Children.Add(Layout.Row("Add due dates to _list", _list, 240));
                _list.SelectionChanged += (_, _) => { ShowSaved(); if (ListPrivate()) Speech.Say(_panel, PrivateLine, true); };
                _address.TextChanged += (_, _) => ShowSaved();
                _system.SelectionChanged += (_, _) => ShowSaved();
                ShowSaved();
                _panel.Children.Add(Layout.Row("How to _get a token", SmartUi.ReadOnly("How to get a token", HelpText, 460, 220), 460));
            }

            private string _hostSaved = "", _systemSaved = "";
            private const string PrivateLine = "That list is private from Bunny, so Bunny's school tools stay off. Due dates still come in.";

            private bool ListPrivate() => _list.SelectedItem is string name && _store.Data.Lists.FirstOrDefault(l => l.Name == name) is TaskList l && SmartBunny.ListPrivate(_store, l);

            /// <summary>Whether the saved token belongs to the address and system in the boxes now. A token is only ever sent to the school it was saved for.</summary>
            private bool SameSchool() => (SchoolAddress.Parse(_address.Text)?.Host ?? "").Equals(_hostSaved, StringComparison.OrdinalIgnoreCase) && _hostSaved.Length > 0 && SystemNow() == _systemSaved;

            private void ShowSaved()
            {
                var st = State(_store);
                bool have = Unprotect(st.Token).Length > 0;
                string saved = _remove ? "The saved token will be removed when you press OK."
                    : !have ? "No token is saved yet."
                    : SameSchool() ? "A token is saved. Leave the token box empty to keep it."
                    : "The saved token is for another school address, so it will be removed when you press OK. Paste a token for this one.";
                _saved.Text = saved + (st.LastResult.Length > 0 ? " " + Result(st, st.LastResult) : "") + (ListPrivate() ? " " + PrivateLine : "");
            }

            private string TokenNow() => _token.Password.Trim().Length > 0 ? _token.Password.Trim() : _remove || !SameSchool() ? "" : Unprotect(State(_store).Token);
            private string SystemNow() => (string?)_system.SelectedItem ?? "Moodle";

            private async Task Test()
            {
                if (_testing) return;
                string token = TokenNow();
                if (token.Length == 0)
                {
                    Speech.Say(_panel, Unprotect(State(_store).Token).Length > 0 && !SameSchool()
                        ? "The saved token is for another school address, so Busy Bunny will not send it here. Paste a token for this one."
                        : "Paste your token first. How to get a token, below, says where to find it.", true);
                    return;
                }
                _testing = true;
                _test.IsEnabled = false;
                Speech.Say(_panel, "Testing the connection.", true);
                string line;
                try
                {
                    var src = Source(SystemNow(), _address.Text, token);
                    var site = await src.SiteAsync(CancellationToken.None);
                    var courses = await src.CoursesAsync(CancellationToken.None);
                    line = "It works. Connected to " + site.Name + (site.User.Length > 0 ? " as " + site.User : "") + ", with " + SmartUi.Count(courses.Count, "course") +
                        (courses.Count > 0 ? ": " + Join(courses.Take(6).Select(c => c.Name).ToList()) + (courses.Count > 6 ? " and more" : "") : "") + ". Press OK to save.";
                }
                catch (SchoolProblem p) { line = p.Message; ErrorLog.Write(LogText("test", p, token)); }
                catch (Exception e) { line = "The test did not work just now. Try again in a little while."; ErrorLog.Write(LogText("test", e, token)); }
                finally { _testing = false; _test.IsEnabled = true; }
                Speech.Say(_panel, Scrub(line, token), true);
                _test.Focus();
            }

            private async Task SignIn()
            {
                if (Window.GetWindow(_panel) is not Window owner) return;
                if (SchoolAddress.Parse(_address.Text) == null) { Speech.Say(_panel, "Type your school's Moodle address first, like " + SchoolAddress.MoodleDefault + ".", true); _address.Focus(); return; }
                var user = new TextBox();
                var pw = new PasswordBox();
                bool ok = SmartUi.Form(owner, "Sign in to Moodle once", "_Get a token", p =>
                {
                    p.Children.Add(SmartUi.Intro("Busy Bunny sends your username and password only to " + SchoolAddress.Parse(_address.Text)!.Host + ", gets a token for the Moodle app, and never keeps your password.", 420));
                    p.Children.Add(Layout.Row("Moodle _username", user, 300));
                    p.Children.Add(Layout.Row("_Password", pw, 300));
                }, () => user.Text.Trim().Length == 0 || pw.Password.Length == 0 ? "Type your username and password first." : null);
                string password = pw.Password;
                pw.Clear();
                if (!ok) { _signIn.Focus(); return; }
                Speech.Say(_panel, "Signing in to Moodle.", true);
                string line;
                try
                {
                    string token = await MoodleSource.SignInAsync(_address.Text, user.Text.Trim(), password, CancellationToken.None);
                    _token.Password = token;
                    _remove = false;
                    line = "Moodle gave Busy Bunny a token. Your password was not kept. Press Test the connection, or OK to save it.";
                }
                catch (SchoolProblem p) { line = p.Message; ErrorLog.Write(LogText("sign in", p, password)); }
                catch (Exception e) { line = "Signing in did not work just now. Try again in a little while."; ErrorLog.Write(LogText("sign in", e, password)); }
                password = "";
                Speech.Say(_panel, line, true);
                _signIn.Focus();
            }

            public string? Validate()
            {
                bool hasToken = TokenNow().Length > 0;
                if (hasToken && SchoolAddress.Parse(_address.Text) == null)
                    return "School connection: the school address should start with https, like " + SchoolAddress.MoodleDefault + ".";
                if (_token.Password.Trim().Contains(' ')) return "School connection: that token has a space in it. Copy it again, without spaces.";
                return null;
            }

            public void Apply()
            {
                var st = State(_store);
                string oldSystem = SystemName(st), oldAddress = st.Address;
                st.System = SystemNow();
                st.Address = SchoolAddress.Parse(_address.Text)?.ToString().TrimEnd('/') ?? _address.Text.Trim();
                if (_token.Password.Trim().Length > 0) st.Token = Protect(_token.Password.Trim());
                else if (_remove || !SameSchool()) st.Token = "";
                st.Daily = _daily.IsChecked == true;
                st.FollowSubmitted = _follow.IsChecked == true;
                st.BunnyMayRead = _bunny.IsChecked == true;
                if (_list.SelectedItem is string l) st.List = l;
                // Another school starts over with the first-time checklist.
                if (st.System != oldSystem || st.Address != oldAddress) st.Approved = false;
                _token.Clear();
                st.LastResult = Result(st, st.LastResult);
                SaveState(_store, st);
                SchoolBunny.Forget();
            }
        }

        // ---- Self-test ----

        /// <summary>Canned answers for the self-test: each request goes to answer, and every one is kept to check.</summary>
        internal sealed class Canned : HttpMessageHandler
        {
            public readonly List<(HttpRequestMessage Req, string Body)> Seen = new();
            private readonly Func<HttpRequestMessage, string, HttpResponseMessage> _answer;
            public Canned(Func<HttpRequestMessage, string, HttpResponseMessage> answer) { _answer = answer; }
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken stop)
            {
                string body = request.Content == null ? "" : await request.Content.ReadAsStringAsync(stop).ConfigureAwait(false);
                Seen.Add((request, body));
                return _answer(request, body);
            }
            public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK, string? next = null, string? rate = null)
            {
                var r = new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
                if (next != null) r.Headers.TryAddWithoutValidation("Link", "<" + next + ">; rel=\"next\", <" + next + ">; rel=\"last\"");
                if (rate != null) r.Headers.TryAddWithoutValidation("X-Rate-Limit-Remaining", rate);
                return r;
            }
        }

        private static T Sync<T>(Func<Task<T>> f) => Task.Run(f).GetAwaiter().GetResult();

        public void SelfTest(Store store)
        {
            void Need(bool ok, string what) { if (!ok) throw new Exception("School connection: " + what); }
            const string Secret = "ZZsecretTOKEN4417";
            var fake = new DateTime(2026, 11, 4, 9, 0, 0);
            long Unix(DateTime local) => new DateTimeOffset(local).ToUnixTimeSeconds();

            // Addresses: HTTPS only, cleaned up.
            Need(SchoolAddress.Parse("http://courses.furman.edu") == null, "an http address was taken");
            Need(SchoolAddress.Parse("courses.furman.edu/login/index.php")?.ToString() == "https://courses.furman.edu/", "address clean up gave " + SchoolAddress.Parse("courses.furman.edu/login/index.php"));
            Need(Unprotect(Protect(Secret)) == Secret && !Protect(Secret).Contains(Secret), "token round trip");

            // Names from school: plain, no marks her screen reader reads out; Moodle's timeline words cleaned up.
            Need(SchoolText.Name("Essay \u00231 <b>draft</b>") == "Essay 1 draft", "name gave " + SchoolText.Name("Essay \u00231 <b>draft</b>"));
            Need(SchoolText.CleanDeadline("Essay 1 is due") == "Essay 1" && SchoolText.CleanDeadline("Quiz 3 closes") == "Quiz 3" && SchoolText.CleanDeadline("Quiz 3 opens") == null
                && SchoolText.CleanDeadline("Lab report (Assignment due)") == "Lab report" && SchoolText.CleanDeadline("Overdue fines") == "Overdue fines", "deadline names");
            Need(SchoolText.Plain("<p>Write <b>two</b> pages &amp; cite.</p><script>bad()</script><ul><li>One</li><li>Two</li></ul>") == "Write two pages & cite.\nOne\nTwo",
                "HTML to plain text gave " + SchoolText.Plain("<p>Write <b>two</b> pages &amp; cite.</p><script>bad()</script><ul><li>One</li><li>Two</li></ul>"));
            Need(SchoolText.IsTest("Unit test") && SchoolText.IsTest("Midterm exam") && !SchoolText.IsTest("Testimony essay"), "test words");

            var oldHandler = SchoolHttp.TestHandler;
            try
            {
                // ---- Canvas: pagination, a hidden class, grades missing, HTML, rubric ----
                const string C = "https://canvas.test.edu";
                var canvas = new Canned((req, _) =>
                {
                    string u = req.RequestUri!.ToString();
                    if (u.Contains("/users/self")) return Canned.Json("{\"id\":1,\"name\":\"Morgan Test\",\"short_name\":\"Morgan\"}");
                    if (u.Contains("/courses?") && u.Contains("page=2")) return Canned.Json("[{\"id\":102,\"name\":\"Cell Biology\",\"course_code\":\"BIO 205\",\"enrollments\":[{\"type\":\"student\",\"computed_current_score\":null}]}]");
                    if (u.Contains("/courses?")) return Canned.Json("[{\"id\":101,\"name\":\"PSY-111-01 Intro to Psychology\",\"course_code\":\"PSY-111-01\",\"enrollments\":[{\"type\":\"student\",\"computed_current_score\":92.5,\"computed_current_grade\":\"A-\"}]}]",
                        next: C + "/api/v1/courses?enrollment_state=active&page=2&per_page=100");
                    if (u.Contains("/courses/101/assignments")) return Canned.Json("[{\"id\":5001,\"name\":\"Essay \u00231\",\"due_at\":\"2026-12-04T04:59:00Z\",\"points_possible\":20," +
                        "\"description\":\"<p>Write <b>two</b> pages &amp; cite.</p>\",\"submission\":{\"workflow_state\":\"unsubmitted\",\"missing\":false},\"rubric\":[{\"description\":\"Thesis\",\"points\":5,\"long_description\":\"<i>Clear</i>\"}]}," +
                        "{\"id\":5002,\"name\":\"Quiz 3\",\"due_at\":\"2026-12-10T15:00:00Z\",\"is_quiz_assignment\":true,\"points_possible\":10,\"submission\":{\"workflow_state\":\"graded\",\"submitted_at\":\"2026-11-01T09:00:00Z\",\"score\":9,\"graded_at\":\"2026-11-02T10:00:00Z\"}}," +
                        "{\"id\":5003,\"name\":\"Reading response\",\"due_at\":\"2026-10-01T04:59:00Z\",\"submission\":{\"workflow_state\":\"unsubmitted\",\"missing\":true}}]");
                    // A class that hides its work: a 403 that carries the rate header Canvas always sends, and is not throttling.
                    if (u.Contains("/courses/102/assignments")) return Canned.Json("{\"status\":\"unauthorized\",\"errors\":[{\"message\":\"user not authorized to perform that action\"}]}", HttpStatusCode.Forbidden, rate: "700.0");
                    if (u.Contains("/announcements")) return Canned.Json("[{\"title\":\"Class moved\",\"message\":\"<p>We meet in <b>Hall 2</b>.</p>\",\"posted_at\":\"2026-11-02T14:00:00Z\",\"context_code\":\"course_101\"}]");
                    return Canned.Json("{}", HttpStatusCode.NotFound);
                });
                SchoolHttp.TestHandler = canvas;
                var cs = new CanvasSource(C, Secret);
                var site = Sync(() => cs.SiteAsync(default));
                var courses = Sync(() => cs.CoursesAsync(default));
                Need(site.User == "Morgan" && courses.Count == 2, "Canvas pagination gave " + courses.Count + " courses");
                Need(courses[0].Grade == "92.5 percent, A-" && courses[1].Grade == "", "Canvas grades: " + courses[0].Grade + " / " + courses[1].Grade);
                var cwork = Sync(() => cs.WorkAsync(courses, default));
                Need(cwork.Count == 3, "Canvas work count " + cwork.Count + " (a hidden class should be skipped)");
                var essay = cwork.First(w => w.Key == "canvas:5001");
                Need(essay.Name == "Essay 1" && essay.Details == "Write two pages & cite." && essay.Rubric == "Thesis, 5 points: Clear" && essay.Points == 20 && !essay.Submitted, "Canvas essay read wrong: " + essay.Name + " / " + essay.Details + " / " + essay.Rubric);
                var quiz = cwork.First(w => w.Key == "canvas:5002");
                Need(quiz.IsTest && quiz.Submitted && quiz.Score == "9 of 10", "Canvas quiz read wrong");
                Need(cwork.First(w => w.Key == "canvas:5003").Missing, "Canvas missing flag");
                var grades = Sync(() => cs.GradesAsync(courses, cwork, default));
                Need(grades[0].Recent.Count == 1 && grades[0].Recent[0] == "Quiz 3, 9 of 10" && grades[1].Overall == "", "Canvas recent grades");
                var news = Sync(() => cs.NewsAsync(courses, default));
                Need(news.Count == 1 && news[0].Text == "We meet in Hall 2." && news[0].Course.Contains("Psychology"), "Canvas announcements");
                Need(canvas.Seen.All(s => s.Req.RequestUri!.Host == "canvas.test.edu" && s.Req.Method == HttpMethod.Get && s.Req.Headers.Authorization?.Parameter == Secret && !s.Req.RequestUri.ToString().Contains(Secret)),
                    "a Canvas request was not a GET to the school with the token as Bearer");
                // Canvas errors, said plainly.
                string Fails(Func<Task> f) { try { Task.Run(f).GetAwaiter().GetResult(); return ""; } catch (SchoolProblem p) { return p.Message; } }
                SchoolHttp.TestHandler = new Canned((_, _) => Canned.Json("{}", HttpStatusCode.Unauthorized));
                Need(Fails(() => new CanvasSource(C, Secret).SiteAsync(default)).Contains("did not accept the token"), "Canvas 401");
                SchoolHttp.TestHandler = new Canned((_, _) => Canned.Json("403 Forbidden (Rate Limit Exceeded)", HttpStatusCode.Forbidden, rate: "0.0"));
                Need(Fails(() => new CanvasSource(C, Secret).SiteAsync(default)).Contains("slow down"), "Canvas rate limit");
                SchoolHttp.TestHandler = new Canned((_, _) => Canned.Json("{\"status\":\"unauthorized\"}", HttpStatusCode.Forbidden, rate: "650.5"));
                string forbidden = Fails(() => new CanvasSource(C, Secret).SiteAsync(default));
                Need(forbidden.Length > 0 && !forbidden.Contains("slow down"), "a 403 with the usual rate header was read as slow down");
                SchoolHttp.TestHandler = new Canned((_, _) => Canned.Json("[]", next: "https://elsewhere.example.com/api/v1/courses?page=2"));
                Need(Fails(() => new CanvasSource(C, Secret).CoursesAsync(default)).Contains("only talks to your school"), "a next page on another server was followed");
                SchoolHttp.TestHandler = new Canned((_, _) => throw new HttpRequestException("failed to reach https://canvas.test.edu?token=" + Secret));
                string down = Fails(() => new CanvasSource(C, Secret).SiteAsync(default));
                Need(down.Contains("could not reach") && !down.Contains(Secret), "network error: " + down);
                Need(!LogText("check", new HttpRequestException("bad " + Secret), Secret).Contains(Secret) && !LogText("check", new SchoolProblem("x " + Secret), Secret).Contains(Secret), "the token reached a log line");

                // ---- Moodle: errors as JSON on 200, timeline events, missing grades ----
                const string M = "https://moodle.test.edu";
                long due1 = Unix(DateTime.Today.AddDays(10).AddHours(23).AddMinutes(59)), due2 = Unix(DateTime.Today.AddDays(17).AddHours(10));
                var moodle = new Canned((req, body) =>
                {
                    if (req.RequestUri!.AbsolutePath.EndsWith("/login/token.php"))
                        return Canned.Json(body.Contains("password=right") ? "{\"token\":\"" + Secret + "\",\"privatetoken\":null}" : "{\"error\":\"Invalid login\",\"errorcode\":\"invalidlogin\"}");
                    string fn = Regex.Match(body, "wsfunction=([a-z_]+)").Groups[1].Value;
                    return fn switch
                    {
                        "core_webservice_get_site_info" => Canned.Json("{\"sitename\":\"Furman Moodle\",\"userid\":7,\"firstname\":\"Morgan\",\"functions\":[]}"),
                        "core_enrol_get_users_courses" => Canned.Json("[{\"id\":3,\"shortname\":\"PSY-111-01\",\"fullname\":\"Intro to Psychology\"}]"),
                        "mod_assign_get_assignments" => Canned.Json("{\"courses\":[{\"id\":3,\"assignments\":[{\"id\":11,\"name\":\"Reflection paper\",\"duedate\":" + due1 + ",\"intro\":\"<p>One page</p>\",\"grade\":10}]}]}"),
                        "core_calendar_get_action_events_by_timesort" => Canned.Json("{\"events\":[" +
                            "{\"id\":90,\"name\":\"Unit test closes\",\"modulename\":\"quiz\",\"instance\":4,\"timesort\":" + due2 + ",\"course\":{\"id\":3,\"fullname\":\"Intro to Psychology\",\"shortname\":\"PSY-111-01\"},\"action\":{\"actionable\":true}}," +
                            "{\"id\":93,\"name\":\"Chapter quiz closes\",\"modulename\":\"quiz\",\"instance\":5,\"timesort\":" + due2 + ",\"course\":{\"id\":3,\"fullname\":\"Intro to Psychology\"},\"action\":{\"actionable\":false}}," +
                            "{\"id\":91,\"name\":\"Unit test opens\",\"modulename\":\"quiz\",\"instance\":4,\"timesort\":" + (due2 - 86400) + ",\"course\":{\"id\":3,\"fullname\":\"Intro to Psychology\"}}," +
                            "{\"id\":92,\"name\":\"Reflection paper is due\",\"modulename\":\"assign\",\"instance\":11,\"timesort\":" + due1 + ",\"course\":{\"id\":3,\"fullname\":\"Intro to Psychology\"}}]}"),
                        "mod_assign_get_submission_status" => Canned.Json("{\"lastattempt\":{\"submission\":{\"status\":\"submitted\"},\"gradingstatus\":\"notgraded\"}}"),
                        "gradereport_user_get_grade_items" => Canned.Json("{\"exception\":\"required_capability_exception\",\"errorcode\":\"nopermissions\",\"message\":\"Sorry\"}"),
                        "mod_forum_get_forums_by_courses" => Canned.Json("[{\"id\":8,\"course\":3,\"type\":\"news\"},{\"id\":9,\"course\":3,\"type\":\"general\"}]"),
                        "mod_forum_get_forum_discussions" => Canned.Json("{\"discussions\":[{\"name\":\"Welcome\",\"message\":\"<p>Hi &amp; welcome</p>\",\"created\":" + Unix(fake) + "}]}"),
                        _ => Canned.Json("{\"exception\":\"moodle_exception\",\"errorcode\":\"invalidparameter\",\"message\":\"Bad\"}"),
                    };
                });
                SchoolHttp.TestHandler = moodle;
                var ms = new MoodleSource(M, Secret);
                Need(Sync(() => ms.SiteAsync(default)).Name == "Furman Moodle", "Moodle site name");
                var mcourses = Sync(() => ms.CoursesAsync(default));
                var mwork = Sync(() => ms.WorkAsync(mcourses, default));
                Need(mwork.Count == 3, "Moodle work count " + mwork.Count);
                Need(!mwork.First(w => w.Key == "moodle:quiz:5").Submitted, "a quiz that is not open yet was read as turned in");
                var paper = mwork.First(w => w.Key == "moodle:assign:11");
                Need(paper.Submitted && paper.Details == "One page" && paper.Due == DateTimeOffset.FromUnixTimeSeconds(due1).LocalDateTime && paper.Points == 10, "Moodle assignment read wrong");
                var unit = mwork.First(w => w.Key == "moodle:quiz:4");
                Need(unit.Name == "Unit test" && unit.IsTest && !unit.Submitted, "Moodle timeline quiz read wrong: " + unit.Name);
                var mgrades = Sync(() => ms.GradesAsync(mcourses, mwork, default));
                Need(mgrades.Count == 1 && mgrades[0].Overall == "", "an exception on HTTP 200 for grades should leave the grade empty");
                var mnews = Sync(() => ms.NewsAsync(mcourses, default));
                Need(mnews.Count == 1 && mnews[0].Text == "Hi & welcome", "Moodle announcements");
                Need(moodle.Seen.All(s => s.Req.RequestUri!.Host == "moodle.test.edu" && !s.Req.RequestUri.ToString().Contains(Secret) && s.Req.Method == HttpMethod.Post), "a Moodle request had the token in its address");
                Need(Sync(() => MoodleSource.SignInAsync(M, "morgan", "right", default)) == Secret, "Moodle sign in");
                Need(Fails(() => MoodleSource.SignInAsync(M, "morgan", "wrong", default)).Contains("did not accept that username"), "Moodle bad password");
                SchoolHttp.TestHandler = new Canned((_, _) => Canned.Json("{\"exception\":\"moodle_exception\",\"errorcode\":\"invalidtoken\",\"message\":\"Invalid token - token not found\"}"));
                Need(Fails(() => new MoodleSource(M, Secret).SiteAsync(default)).Contains("did not accept the token"), "Moodle invalidtoken on HTTP 200");
                SchoolHttp.TestHandler = new Canned((_, _) => Canned.Json("<html>Moodle is down</html>"));
                Need(Fails(() => new MoodleSource(M, Secret).SiteAsync(default)).Contains("could not read"), "Moodle sent a web page");
                Need(Fails(() => new MoodleSource("http://moodle.test.edu", Secret).SiteAsync(default)).Contains("should start with https"), "an http address was used");

                // ---- Bunny's tools: short, plain, never the token ----
                SchoolHttp.TestHandler = canvas;
                var bst = new SchoolState { System = "Canvas", Address = C, Token = Protect(Secret) };
                SchoolBunny.Forget();
                Need(SchoolBunny.Defs(null, bst).Count == 5 && SchoolBunny.Defs(null, new SchoolState()).Count == 0 && SchoolBunny.Defs(null, new SchoolState { System = "Canvas", Address = C, Token = Protect(Secret), BunnyMayRead = false }).Count == 0,
                    "Bunny's school tools: five when set up, none when not set up or turned off");
                foreach (var (tool, args) in new[] { ("school_courses", "{}"), ("school_assignments", "{\"which\":\"all\"}"), ("school_assignments", "{\"which\":\"missing\"}"), ("school_assignment_details", "{\"name\":\"essay 1\"}"), ("school_grades", "{}"), ("school_announcements", "{}") })
                {
                    var a = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, System.Text.Json.JsonElement>>(args)!;
                    string said = Sync(() => SchoolBunny.Run(tool, a, bst, fake));
                    Need(said.Length > 0 && !said.Contains(Secret) && !said.Contains('<') && said.StartsWith(SchoolBunny.Quoted), tool + " gave: " + said);
                    if (tool == "school_assignment_details") Need(said.Contains("Write two pages") && said.Contains("Thesis"), "details: " + said);
                    if (tool == "school_grades") Need(said.Contains("92.5 percent") && said.Contains("no grade"), "grades: " + said);
                    if (args.Contains("missing")) Need(said.Contains("Reading response") && !said.Contains("Essay 1"), "missing: " + said);
                }
                Need(Sync(() => SchoolBunny.Run("school_courses", new Dictionary<string, System.Text.Json.JsonElement>(), new SchoolState { System = "Canvas", Address = C, Token = Protect(Secret), BunnyMayRead = false }, fake)).Contains("turned off"), "Bunny read classes she turned off");
                SchoolBunny.Forget();

                // ---- Tags from course names ----
                var tags = new List<TagInfo> { new() { Name = "PSY", Description = "Intro to Psychology" }, new() { Name = "HIST" } };
                Need(MatchTag(tags, "PSY-111-01 Intro to Psychology", "PSY-111-01")?.Name == "PSY", "tag by description");
                Need(MatchTag(tags, "World History", "HIST-120")?.Name == "HIST", "tag by code");
                Need(MatchTag(tags, "Cell Biology", "BIO 205") == null && MakeTag("Cell Biology", "BIO 205", tags.Select(t => t.Name)) == "BIO205", "new tag from a code");
                Need(MakeTag("Social Psychology Lab", "", tags.Select(t => t.Name)) == "SPL" && MakeTag("Social Psychology Lab", "", new[] { "SPL" }) == "SPL2", "new tag from words");

                // ---- Turning it into tasks ----
                store.Data.Tags.RemoveAll(t => t.Name is "PSY" or "NEU230");
                store.Data.Tags.Add(new TagInfo { Name = "PSY", Description = "Intro to Psychology" });
                var list = ListFor(store, new SchoolState());
                int before = store.Data.Tasks.Count, tagsBefore = store.Data.Tags.Count;
                var work = new List<SchoolWork>
                {
                    new() { Key = "t:1", Name = "Essay 1", Course = "PSY-111-01 Intro to Psychology", CourseCode = "PSY-111-01", Due = new DateTime(2026, 11, 6, 23, 59, 0) },
                    new() { Key = "t:2", Name = "Unit test", Course = "Neuroscience Lab", CourseCode = "NEU 230", Due = new DateTime(2026, 11, 20, 10, 0, 0), IsTest = true },
                    new() { Key = "t:3", Name = "Old reading", Course = "Neuroscience Lab", CourseCode = "NEU 230", Due = new DateTime(2026, 10, 1, 9, 0, 0) },
                    new() { Key = "t:4", Name = "No date", Course = "Neuroscience Lab" },
                };
                // Control Z takes the whole import back.
                var st1 = new SchoolState();
                var p1 = Plan(store, st1, work, fake, "Moodle");
                Need(p1.New.Count == 3 && p1.New.Count(n => n.On) == 2 && p1.NewTags.Count == 1 && p1.NewTags[0].Name == "NEU230", "plan: " + p1.New.Count + " new, " + p1.NewTags.Count + " tags");
                var r1 = Apply(store, st1, p1, null, "test import", fake);
                Need(r1.Added.Count == 2 && store.Data.Tasks.Count == before + 2 && store.Data.Tags.Count == tagsBefore + 1, "apply added " + r1.Added.Count);
                var e1 = r1.Added.First(t => t.Name == "Essay 1");
                Need(e1.Tag == "PSY" && e1.Type == TaskType.Assignment && e1.DueHasTime && e1.Due == new DateTime(2026, 11, 6, 23, 59, 0) && e1.Status == Status.ThisWeek && e1.List == list.Name, "the essay task");
                var u1 = r1.Added.First(t => t.Name == "Unit test");
                Need(u1.Type == TaskType.Test && u1.Tag == "NEU230" && u1.Status == Status.NextWeek, "the test task");
                string line = Line(r1, "Moodle");
                Need(line == "2 new due dates from Moodle. New tag NEU230 for Neuroscience Lab." && !line.Contains('\u0023') && !line.Contains('\u002A'), "line: " + line);
                store.Undo();
                Need(store.Data.Tasks.Count == before && store.Data.Tags.Count == tagsBefore, "one Control Z did not take the import back");
                Need(Plan(store, st1, work, fake, "Moodle").New.Count == 0, "an item she undid came back");

                // No duplicates on a second run; a moved date updates the task and keeps her name and notes; nothing deleted.
                var st2 = new SchoolState { FollowSubmitted = true };
                Apply(store, st2, Plan(store, st2, work, fake, "Moodle"), null, "first", fake);
                int after = store.Data.Tasks.Count;
                var again = Plan(store, st2, work, fake, "Moodle");
                Need(again.New.Count == 0 && again.Moved.Count == 0 && again.Finished.Count == 0, "a second run would add or change something");
                var mine = store.Data.Tasks.First(t => t.Id == st2.Known["t:1"].Task);
                mine.Name = "My essay";
                mine.Notes = "my notes";
                work[0].Due = new DateTime(2026, 11, 9, 12, 0, 0);
                var p3 = Plan(store, st2, work, fake, "Moodle");
                Need(p3.Moved.Count == 1 && p3.New.Count == 0, "a moved date was not found");
                var r3 = Apply(store, st2, p3, null, "moved", fake);
                Need(mine.Due == new DateTime(2026, 11, 9, 12, 0, 0) && mine.Name == "My essay" && mine.Notes == "my notes", "the moved date did not keep her edits");
                Need(Line(r3, "Moodle").StartsWith("Moodle moved My essay to Monday, November 9") && Line(r3, "Moodle").Contains("at 12 pm"), "moved line: " + Line(r3, "Moodle"));
                Need(Plan(store, st2, work, fake, "Moodle").Moved.Count == 0, "the same move was found twice");
                Apply(store, st2, Plan(store, st2, new List<SchoolWork>(), fake, "Moodle"), null, "gone", fake);
                Need(store.Data.Tasks.Count == after, "a task was deleted when the school dropped it");
                work[0].Submitted = true;
                var r4 = Apply(store, st2, Plan(store, st2, work, fake, "Moodle"), null, "turned in", fake);
                Need(r4.Finished.Count == 1 && mine.Status == Status.Done, "turned in work was not marked done");
                // Done not submitted earned half; the school says it was turned in: the other half is paid once.
                var half = new TaskItem { Name = "Poster", List = list.Name, Due = new DateTime(2026, 11, 10), Status = Status.DoneNotSubmitted, Size = Size.Medium };
                store.Data.Tasks.Add(half);
                GameFeature.Finish(store, half, Status.ThisWeek, fake);
                int Paid() => store.Data.Log.Where(e => e.Kind == "Done" && e.TaskId == half.Id).Sum(e => e.Points);
                int full = Engine.PointsFor(half, store.ListOf(half));
                Need(Paid() < full, "half points to start");
                st2.Known["t:6"] = new SchoolSeen { Task = half.Id, Due = half.Due, Submitted = false };
                var poster = new List<SchoolWork> { new() { Key = "t:6", Name = "Poster", Due = half.Due, Submitted = true } };
                var r6 = Apply(store, st2, Plan(store, st2, poster, fake, "Moodle"), null, "poster", fake);
                Need(r6.Finished.Count == 1 && half.Status == Status.Done && Paid() == full && r6.FinishedFrom[0].Old == Status.DoneNotSubmitted, "turned in work did not pay the other half: " + Paid() + " of " + full);
                Apply(store, st2, Plan(store, st2, poster, fake, "Moodle"), null, "poster again", fake);
                Need(Paid() == full, "turned in work paid twice");
                // A task she typed in herself is linked, not added twice.
                var typed = new TaskItem { Name = "lab report", List = list.Name, Due = new DateTime(2026, 11, 13) };
                store.Data.Tasks.Add(typed);
                var p5 = Plan(store, st2, new List<SchoolWork> { new() { Key = "t:5", Name = "Lab report", Course = "Neuroscience Lab", Due = new DateTime(2026, 11, 13, 23, 59, 0) } }, fake, "Moodle");
                Need(p5.New.Count == 0 && p5.Linked.Count == 1, "a task she had already was offered again");
                // Her saved state round trips, without the token in plain text.
                var keep = new SchoolState { Token = Protect(Secret), List = list.Name };
                SaveState(store, keep);
                Need(Unprotect(State(store).Token) == Secret && !System.Text.Json.JsonSerializer.Serialize(store.World.Data.Kept).Contains(Secret), "the saved token");
            }
            finally { SchoolHttp.TestHandler = oldHandler; SchoolBunny.Forget(); }
            Need(Tools.Find(Id) != null, "not in the toolbox");
            Need(SchoolBunny.Defs(null, new SchoolState()).Count == 0, "Bunny's school tools were offered with nothing set up");
        }
    }
}
