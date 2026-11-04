using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace BusyBunny
{
    /// <summary>A school system Busy Bunny can read from with a token. Read only: nothing here submits, posts or changes anything.</summary>
    public interface ISchoolSource
    {
        /// <summary>Canvas or Moodle, for spoken lines.</summary>
        string System { get; }
        Task<SchoolSite> SiteAsync(CancellationToken stop);
        Task<List<SchoolCourse>> CoursesAsync(CancellationToken stop);
        Task<List<SchoolWork>> WorkAsync(List<SchoolCourse> courses, CancellationToken stop);
        Task<List<SchoolGrade>> GradesAsync(List<SchoolCourse> courses, List<SchoolWork> work, CancellationToken stop);
        Task<List<SchoolNews>> NewsAsync(List<SchoolCourse> courses, CancellationToken stop);
    }

    public static class SchoolJson
    {
        public static string Str(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Number => v.GetRawText(), JsonValueKind.True => "true", JsonValueKind.False => "false", _ => "" } : "";
        public static double? Num(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v)
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble() : v.ValueKind == JsonValueKind.String && double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null : null;
        public static bool Bool(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) && n != 0));
        public static JsonElement Obj(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) ? v : default;
        public static IEnumerable<JsonElement> Arr(JsonElement e, string k) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
        public static IEnumerable<JsonElement> Arr(JsonElement e) => e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : Enumerable.Empty<JsonElement>();
        /// <summary>An ISO time like 2026-11-10T04:59:59Z in her own time zone.</summary>
        public static DateTime? Iso(JsonElement e, string k) =>
            DateTimeOffset.TryParse(Str(e, k), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.LocalDateTime : null;
        /// <summary>A Unix time in seconds in her own time zone; 0 means none.</summary>
        public static DateTime? Unix(JsonElement e, string k) => Num(e, k) is double s && s > 0 ? DateTimeOffset.FromUnixTimeSeconds((long)s).LocalDateTime : null;

        public static JsonElement Parse(string body, string system)
        {
            try { using var d = JsonDocument.Parse(body); return d.RootElement.Clone(); }
            catch (JsonException) { throw new SchoolProblem(system + " sent something Busy Bunny could not read. Check the school address."); }
        }
    }

    /// <summary>Canvas REST API v1 with the token as Authorization Bearer. GET only.</summary>
    public sealed class CanvasSource : ISchoolSource
    {
        private readonly SchoolHttp _http;
        private readonly string _token;
        public string System => "Canvas";

        public CanvasSource(string address, string token) { _http = new SchoolHttp(address); _token = token; }

        private string Url(string path) => SchoolAddress.Join(_http.Base, "api/v1/" + path);

        private static SchoolProblem Problem(SchoolReply r)
        {
            if (r.Status == 401) return new SchoolProblem("Canvas did not accept the token. Make a new one in Canvas, Account, Settings, New access token, and paste it in.", 401);
            // Canvas sends X-Rate-Limit-Remaining with every answer; only this body (or nothing left) means slow down.
            bool throttled = r.Body.Contains("Rate Limit Exceeded", StringComparison.OrdinalIgnoreCase)
                || (double.TryParse(r.RateLeft, NumberStyles.Float, CultureInfo.InvariantCulture, out var left) && left <= 0);
            if (r.Status == 403 && throttled) return new SchoolProblem("Canvas asked Busy Bunny to slow down. Try again in a minute.", 403, throttled: true);
            if (r.Status == 403) return new SchoolProblem("Canvas said Busy Bunny may not see that. Your school may have turned off access tokens; if so, the school connection cannot work there.", 403, blocked: true);
            if (r.Status == 404) return new SchoolProblem("Canvas could not find that. Check the school address, like https://yourschool.instructure.com.", 404);
            if (r.Status == 429) return new SchoolProblem("Canvas asked Busy Bunny to slow down. Try again in a minute.", 429, throttled: true);
            if (r.Status >= 500) return new SchoolProblem("Canvas is having trouble right now. Try again later.", r.Status);
            return new SchoolProblem("Canvas answered with an error, code " + r.Status + ".", r.Status);
        }

        private async Task<JsonElement> Get(string path, CancellationToken stop)
        {
            var r = await _http.Send(HttpMethod.Get, Url(path), null, _token, stop).ConfigureAwait(false);
            if (r.Status != 200) throw Problem(r);
            return SchoolJson.Parse(r.Body, System);
        }

        /// <summary>Every page of a list, following the Link header's next (on the school's host only), at most 20 pages.</summary>
        private async Task<List<JsonElement>> GetAll(string path, CancellationToken stop)
        {
            var all = new List<JsonElement>();
            string? url = Url(path);
            for (int page = 0; page < 20 && url != null; page++)
            {
                var r = await _http.Send(HttpMethod.Get, url, null, _token, stop).ConfigureAwait(false);
                if (r.Status != 200) throw Problem(r);
                all.AddRange(SchoolJson.Arr(SchoolJson.Parse(r.Body, System)));
                url = r.Next;
            }
            return all;
        }

        public async Task<SchoolSite> SiteAsync(CancellationToken stop)
        {
            var me = await Get("users/self", stop).ConfigureAwait(false);
            return new SchoolSite { Name = "Canvas at " + _http.Host, User = SchoolJson.Str(me, "short_name") is { Length: > 0 } sn ? sn : SchoolJson.Str(me, "name") };
        }

        public async Task<List<SchoolCourse>> CoursesAsync(CancellationToken stop)
        {
            var list = await GetAll("courses?enrollment_state=active&include[]=total_scores&per_page=100", stop).ConfigureAwait(false);
            var courses = new List<SchoolCourse>();
            foreach (var c in list)
            {
                string id = SchoolJson.Str(c, "id"), name = SchoolText.Name(SchoolJson.Str(c, "name"));
                if (id.Length == 0 || name.Length == 0 || SchoolJson.Bool(c, "access_restricted_by_date")) continue;
                var en = SchoolJson.Arr(c, "enrollments").FirstOrDefault(e => SchoolJson.Str(e, "type").StartsWith("student", StringComparison.OrdinalIgnoreCase));
                string grade = "";
                if (en.ValueKind == JsonValueKind.Object)
                {
                    string letter = SchoolJson.Str(en, "computed_current_grade");
                    double? score = SchoolJson.Num(en, "computed_current_score");
                    grade = string.Join(", ", new[] { score is double s ? SchoolText.Num(s) + " percent" : "", letter }.Where(x => x.Length > 0));
                }
                courses.Add(new SchoolCourse { Id = id, Name = name, Code = SchoolJson.Str(c, "course_code"), Grade = grade });
            }
            return courses;
        }

        public async Task<List<SchoolWork>> WorkAsync(List<SchoolCourse> courses, CancellationToken stop)
        {
            var work = new List<SchoolWork>();
            foreach (var c in courses)
            {
                List<JsonElement> items;
                // A class that hides its assignments is skipped, not the whole check.
                try { items = await GetAll("courses/" + Uri.EscapeDataString(c.Id) + "/assignments?include[]=submission&order_by=due_at&per_page=100", stop).ConfigureAwait(false); }
                catch (SchoolProblem p) when (p.Status is 403 or 404 && !p.Throttled) { continue; }
                foreach (var a in items)
                {
                    string name = SchoolText.Name(SchoolJson.Str(a, "name"));
                    if (name.Length == 0 || (a.TryGetProperty("published", out var pub) && pub.ValueKind == JsonValueKind.False)) continue;
                    var sub = SchoolJson.Obj(a, "submission");
                    string state = SchoolJson.Str(sub, "workflow_state");
                    bool submitted = SchoolJson.Str(sub, "submitted_at").Length > 0 || state is "submitted" or "pending_review" || (state == "graded" && SchoolJson.Str(sub, "submitted_at").Length > 0);
                    double? points = SchoolJson.Num(a, "points_possible"), score = SchoolJson.Num(sub, "score");
                    bool quiz = SchoolJson.Bool(a, "is_quiz_assignment") || SchoolJson.Arr(a, "submission_types").Any(t => t.ValueKind == JsonValueKind.String && t.GetString() == "online_quiz");
                    var rubric = SchoolJson.Arr(a, "rubric").Select(r =>
                    {
                        string d = SchoolText.Plain(SchoolJson.Str(r, "description"), 200).Replace('\n', ' '), l = SchoolText.Plain(SchoolJson.Str(r, "long_description"), 300).Replace('\n', ' ');
                        return d + (SchoolJson.Num(r, "points") is double rp ? ", " + SchoolText.Num(rp) + " points" : "") + (l.Length > 0 ? ": " + l : "");
                    }).Where(x => x.Trim().Length > 0).ToList();
                    work.Add(new SchoolWork
                    {
                        Key = "canvas:" + SchoolJson.Str(a, "id"), CourseId = c.Id, Course = c.Name, CourseCode = c.Code, Name = name,
                        Due = SchoolJson.Iso(a, "due_at"), IsTest = SchoolText.IsTest(name) || (quiz && SchoolText.IsTest(name + " quiz")),
                        Submitted = submitted, Missing = SchoolJson.Bool(sub, "missing"), Graded = score != null && state == "graded",
                        Points = points, Score = score is double sc ? SchoolText.Num(sc) + (points is double pp ? " of " + SchoolText.Num(pp) : "") : "",
                        GradedAt = SchoolJson.Iso(sub, "graded_at"), Details = SchoolText.Plain(SchoolJson.Str(a, "description")), Rubric = string.Join("\n", rubric),
                    });
                }
            }
            return work;
        }

        public Task<List<SchoolGrade>> GradesAsync(List<SchoolCourse> courses, List<SchoolWork> work, CancellationToken stop) =>
            Task.FromResult(courses.Select(c => new SchoolGrade
            {
                Course = c.Name, Overall = c.Grade,
                Recent = work.Where(w => w.CourseId == c.Id && w.Score.Length > 0).OrderByDescending(w => w.GradedAt ?? w.Due ?? DateTime.MinValue).Take(3).Select(w => w.Name + ", " + w.Score).ToList(),
            }).ToList());

        public async Task<List<SchoolNews>> NewsAsync(List<SchoolCourse> courses, CancellationToken stop)
        {
            if (courses.Count == 0) return new();
            string codes = string.Join("&", courses.Select(c => "context_codes[]=course_" + Uri.EscapeDataString(c.Id)));
            string from = DateTime.Today.AddDays(-21).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), to = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var items = await GetAll("announcements?" + codes + "&start_date=" + from + "&end_date=" + to + "&per_page=50", stop).ConfigureAwait(false);
            return items.Select(a =>
            {
                string ctx = SchoolJson.Str(a, "context_code");
                var c = courses.FirstOrDefault(x => "course_" + x.Id == ctx);
                return new SchoolNews { Course = c?.Name ?? "", Title = SchoolText.Name(SchoolJson.Str(a, "title")), Text = SchoolText.Plain(SchoolJson.Str(a, "message"), 1200), Posted = SchoolJson.Iso(a, "posted_at") };
            }).OrderByDescending(n => n.Posted ?? DateTime.MinValue).ToList();
        }
    }

    /// <summary>Moodle's web service (webservice/rest/server.php, JSON) with her token. Only functions that read.</summary>
    public sealed class MoodleSource : ISchoolSource
    {
        private readonly SchoolHttp _http;
        private readonly string _token;
        private HashSet<string>? _functions;
        private int _userId;
        public string System => "Moodle";

        public MoodleSource(string address, string token) { _http = new SchoolHttp(address); _token = token; }

        /// <summary>Calls one web service function. Moodle reports errors as JSON with an exception field even with HTTP 200.</summary>
        private async Task<JsonElement> Call(string fn, IEnumerable<KeyValuePair<string, string>>? args, CancellationToken stop)
        {
            var form = new List<KeyValuePair<string, string>> { new("wstoken", _token), new("wsfunction", fn), new("moodlewsrestformat", "json") };
            if (args != null) form.AddRange(args);
            var r = await _http.Send(HttpMethod.Post, SchoolAddress.Join(_http.Base, "webservice/rest/server.php"), new FormUrlEncodedContent(form), null, stop).ConfigureAwait(false);
            if (r.Status == 403) throw new SchoolProblem("Moodle said no. Your school may have turned off Moodle's web service; if so, the school connection cannot work there.", 403, blocked: true);
            if (r.Status == 404) throw new SchoolProblem("Busy Bunny could not find Moodle at that address. Check the school address, like " + SchoolAddress.MoodleDefault + ".", 404);
            if (r.Status >= 500) throw new SchoolProblem("Moodle is having trouble right now. Try again later.", r.Status);
            if (r.Status != 200) throw new SchoolProblem("Moodle answered with an error, code " + r.Status + ".", r.Status);
            var root = SchoolJson.Parse(r.Body, System);
            if (root.ValueKind == JsonValueKind.Object && (root.TryGetProperty("exception", out _) || root.TryGetProperty("errorcode", out _))) throw Error(root);
            return root;
        }

        public static SchoolProblem Error(JsonElement e)
        {
            string code = SchoolJson.Str(e, "errorcode");
            return code switch
            {
                "invalidtoken" => new SchoolProblem("Moodle did not accept the token. Get a new one from Moodle, Preferences, Security keys, or sign in once to get one.", 401),
                "accessexception" or "webservicenotenabled" or "servicenotavailable" or "noservice" or "enablewsdescription" => new SchoolProblem("Your school's Moodle does not allow this. If it has turned off tokens, the school connection cannot work there.", 403, blocked: true),
                "nopermissions" or "requireloginerror" => new SchoolProblem("Moodle said you may not see that part.", 403),
                _ => new SchoolProblem("Moodle said: " + SchoolText.Plain(SchoolJson.Str(e, "message"), 160).Replace('\n', ' '), 0),
            };
        }

        private bool Has(string fn) => _functions == null || _functions.Count == 0 || _functions.Contains(fn);

        public async Task<SchoolSite> SiteAsync(CancellationToken stop)
        {
            var info = await Call("core_webservice_get_site_info", null, stop).ConfigureAwait(false);
            _userId = (int)(SchoolJson.Num(info, "userid") ?? 0);
            _functions = SchoolJson.Arr(info, "functions").Select(f => SchoolJson.Str(f, "name")).Where(n => n.Length > 0).ToHashSet();
            return new SchoolSite { Name = SchoolText.Name(SchoolJson.Str(info, "sitename")) is { Length: > 0 } n ? n : "Moodle at " + _http.Host, User = SchoolJson.Str(info, "firstname") is { Length: > 0 } f ? f : SchoolJson.Str(info, "fullname") };
        }

        private async Task Ready(CancellationToken stop) { if (_functions == null) await SiteAsync(stop).ConfigureAwait(false); }

        public async Task<List<SchoolCourse>> CoursesAsync(CancellationToken stop)
        {
            await Ready(stop).ConfigureAwait(false);
            var list = await Call("core_enrol_get_users_courses", new[] { new KeyValuePair<string, string>("userid", _userId.ToString(CultureInfo.InvariantCulture)) }, stop).ConfigureAwait(false);
            var now = DateTimeOffset.Now.ToUnixTimeSeconds();
            return SchoolJson.Arr(list).Where(c => !(SchoolJson.Num(c, "enddate") is double end && end > 0 && end < now - 14 * 86400) && !SchoolJson.Bool(c, "hidden"))
                .Select(c => new SchoolCourse { Id = SchoolJson.Str(c, "id"), Name = SchoolText.Name(SchoolJson.Str(c, "fullname")), Code = SchoolJson.Str(c, "shortname") })
                .Where(c => c.Id.Length > 0 && c.Name.Length > 0).ToList();
        }

        private static IEnumerable<KeyValuePair<string, string>> Ids(string name, IEnumerable<SchoolCourse> courses) =>
            courses.Select((c, i) => new KeyValuePair<string, string>(name + "[" + i + "]", c.Id));

        public async Task<List<SchoolWork>> WorkAsync(List<SchoolCourse> courses, CancellationToken stop)
        {
            await Ready(stop).ConfigureAwait(false);
            var work = new List<SchoolWork>();
            var byId = courses.ToDictionary(c => c.Id);
            if (courses.Count > 0 && Has("mod_assign_get_assignments"))
            {
                try
                {
                    var r = await Call("mod_assign_get_assignments", Ids("courseids", courses), stop).ConfigureAwait(false);
                    foreach (var c in SchoolJson.Arr(r, "courses"))
                    {
                        byId.TryGetValue(SchoolJson.Str(c, "id"), out var course);
                        foreach (var a in SchoolJson.Arr(c, "assignments"))
                        {
                            string name = SchoolText.Name(SchoolJson.Str(a, "name"));
                            if (name.Length == 0) continue;
                            double? max = SchoolJson.Num(a, "grade");
                            work.Add(new SchoolWork
                            {
                                Key = "moodle:assign:" + SchoolJson.Str(a, "id"), AssignId = (int)(SchoolJson.Num(a, "id") ?? 0), CourseId = course?.Id ?? SchoolJson.Str(c, "id"),
                                Course = course?.Name ?? SchoolText.Name(SchoolJson.Str(c, "fullname")), CourseCode = course?.Code ?? SchoolJson.Str(c, "shortname"),
                                Name = name, Due = SchoolJson.Unix(a, "duedate"), IsTest = SchoolText.IsTest(name), Points = max is double m && m > 0 ? m : null,
                                Details = SchoolText.Plain(SchoolJson.Str(a, "intro")),
                            });
                        }
                    }
                }
                catch (SchoolProblem p) when (!p.Blocked && p.Status != 401) { }
            }
            // Quizzes and other things with a deadline come from the timeline (action events), each only once.
            if (Has("core_calendar_get_action_events_by_timesort"))
            {
                try
                {
                    long from = DateTimeOffset.Now.AddDays(-30).ToUnixTimeSeconds();
                    var r = await Call("core_calendar_get_action_events_by_timesort", new KeyValuePair<string, string>[] { new("timesortfrom", from.ToString(CultureInfo.InvariantCulture)), new("limitnum", "50") }, stop).ConfigureAwait(false);
                    foreach (var e in SchoolJson.Arr(r, "events"))
                    {
                        string module = SchoolJson.Str(e, "modulename"), instance = SchoolJson.Str(e, "instance");
                        if (module == "assign" && work.Any(w => w.Key == "moodle:assign:" + instance)) continue;
                        if (SchoolText.CleanDeadline(SchoolJson.Str(e, "name")) is not string name) continue;
                        var course = SchoolJson.Obj(e, "course");
                        string key = "moodle:" + (module.Length > 0 ? module : "event") + ":" + (instance.Length > 0 ? instance : SchoolJson.Str(e, "id"));
                        if (work.Any(w => w.Key == key)) continue;
                        work.Add(new SchoolWork
                        {
                            Key = key, CourseId = SchoolJson.Str(course, "id"), Course = SchoolText.Name(SchoolJson.Str(course, "fullname")), CourseCode = SchoolJson.Str(course, "shortname"),
                            Name = name, Due = SchoolJson.Unix(e, "timesort"), IsTest = module == "quiz" ? SchoolText.IsTest(name + " quiz") : SchoolText.IsTest(name),
                            // Never turned in from the timeline: actionable false mostly means a quiz that has not opened yet.
                            Details = SchoolText.Plain(SchoolJson.Str(e, "description"), 1500),
                        });
                    }
                }
                catch (SchoolProblem p) when (!p.Blocked && p.Status != 401) { }
            }
            // Whether each assignment near now was submitted, where the school allows asking.
            if (Has("mod_assign_get_submission_status"))
            {
                var now = DateTime.Now;
                foreach (var w in work.Where(w => w.AssignId > 0 && w.Due is DateTime d && d > now.AddDays(-30) && d < now.AddDays(60)).Take(30).ToList())
                {
                    try
                    {
                        var s = await Call("mod_assign_get_submission_status", new[] { new KeyValuePair<string, string>("assignid", w.AssignId.ToString(CultureInfo.InvariantCulture)) }, stop).ConfigureAwait(false);
                        var last = SchoolJson.Obj(s, "lastattempt");
                        string status = SchoolJson.Str(SchoolJson.Obj(last, "submission"), "status");
                        if (status.Length == 0) status = SchoolJson.Str(SchoolJson.Obj(last, "teamsubmission"), "status");
                        w.Submitted = status == "submitted";
                        w.Graded = SchoolJson.Str(last, "gradingstatus") == "graded";
                        w.Missing = !w.Submitted && w.Due < now;
                        string shown = SchoolText.Plain(SchoolJson.Str(SchoolJson.Obj(s, "feedback"), "gradefordisplay"), 80).Replace('\n', ' ');
                        if (shown.Length > 0) w.Score = shown.Replace("/", "of").Replace("  ", " ");
                    }
                    catch (SchoolProblem p) when (!p.Blocked && p.Status != 401) { break; }
                }
            }
            return work;
        }

        public async Task<List<SchoolGrade>> GradesAsync(List<SchoolCourse> courses, List<SchoolWork> work, CancellationToken stop)
        {
            await Ready(stop).ConfigureAwait(false);
            var grades = new List<SchoolGrade>();
            foreach (var c in courses)
            {
                var g = new SchoolGrade { Course = c.Name };
                if (Has("gradereport_user_get_grade_items"))
                {
                    try
                    {
                        var r = await Call("gradereport_user_get_grade_items", new KeyValuePair<string, string>[] { new("courseid", c.Id), new("userid", _userId.ToString(CultureInfo.InvariantCulture)) }, stop).ConfigureAwait(false);
                        var items = SchoolJson.Arr(r, "usergrades").SelectMany(u => SchoolJson.Arr(u, "gradeitems")).ToList();
                        static bool Real(string s) => s.Length > 0 && s != "-" && s != "&nbsp;";
                        var total = items.FirstOrDefault(i => SchoolJson.Str(i, "itemtype") == "course");
                        if (total.ValueKind == JsonValueKind.Object)
                        {
                            string pct = SchoolText.Plain(SchoolJson.Str(total, "percentageformatted"), 30), shown = SchoolText.Plain(SchoolJson.Str(total, "gradeformatted"), 30);
                            g.Overall = string.Join(", ", new[] { Real(pct) ? pct.Replace(" %", " percent").Replace("%", " percent") : "", Real(shown) && shown != pct ? shown : "" }.Where(x => x.Length > 0));
                        }
                        g.Recent = items.Where(i => SchoolJson.Str(i, "itemtype") != "course" && SchoolJson.Num(i, "graderaw") != null)
                            .OrderByDescending(i => SchoolJson.Num(i, "gradedategraded") ?? 0).Take(3)
                            .Select(i => SchoolText.Name(SchoolJson.Str(i, "itemname")) + ", " + SchoolText.Num(SchoolJson.Num(i, "graderaw")!.Value) + (SchoolJson.Num(i, "grademax") is double m ? " of " + SchoolText.Num(m) : "")).ToList();
                    }
                    catch (SchoolProblem p) when (!p.Blocked && p.Status != 401) { }
                }
                if (g.Recent.Count == 0) g.Recent = work.Where(w => w.CourseId == c.Id && w.Score.Length > 0).Take(3).Select(w => w.Name + ", " + w.Score).ToList();
                grades.Add(g);
            }
            return grades;
        }

        public async Task<List<SchoolNews>> NewsAsync(List<SchoolCourse> courses, CancellationToken stop)
        {
            await Ready(stop).ConfigureAwait(false);
            var news = new List<SchoolNews>();
            if (courses.Count == 0 || !Has("mod_forum_get_forums_by_courses")) return news;
            JsonElement forums;
            try { forums = await Call("mod_forum_get_forums_by_courses", Ids("courseids", courses), stop).ConfigureAwait(false); }
            catch (SchoolProblem p) when (!p.Blocked && p.Status != 401) { return news; }
            foreach (var f in SchoolJson.Arr(forums).Where(f => SchoolJson.Str(f, "type") == "news").Take(12))
            {
                var course = courses.FirstOrDefault(c => c.Id == SchoolJson.Str(f, "course"));
                JsonElement r;
                try
                {
                    r = Has("mod_forum_get_forum_discussions")
                        ? await Call("mod_forum_get_forum_discussions", new KeyValuePair<string, string>[] { new("forumid", SchoolJson.Str(f, "id")), new("page", "0"), new("perpage", "5") }, stop).ConfigureAwait(false)
                        : await Call("mod_forum_get_forum_discussions_paginated", new KeyValuePair<string, string>[] { new("forumid", SchoolJson.Str(f, "id")), new("sortby", "timemodified"), new("sortdirection", "DESC"), new("page", "0"), new("perpage", "5") }, stop).ConfigureAwait(false);
                }
                catch (SchoolProblem p) when (!p.Blocked && p.Status != 401) { continue; }
                foreach (var d in SchoolJson.Arr(r, "discussions"))
                    news.Add(new SchoolNews
                    {
                        Course = course?.Name ?? "", Title = SchoolText.Name(SchoolJson.Str(d, "name") is { Length: > 0 } n ? n : SchoolJson.Str(d, "subject")),
                        Text = SchoolText.Plain(SchoolJson.Str(d, "message"), 1200), Posted = SchoolJson.Unix(d, "created") ?? SchoolJson.Unix(d, "timemodified"),
                    });
            }
            return news.OrderByDescending(n => n.Posted ?? DateTime.MinValue).ToList();
        }

        /// <summary>
        /// Signs in once with her Moodle username and password to get a token for the Moodle app's web service
        /// (login/token.php, service moodle_mobile_app). The password is sent only to her school over HTTPS and never kept.
        /// </summary>
        public static async Task<string> SignInAsync(string address, string user, string password, CancellationToken stop)
        {
            var http = new SchoolHttp(address);
            var form = new FormUrlEncodedContent(new KeyValuePair<string, string>[] { new("username", user), new("password", password), new("service", "moodle_mobile_app") });
            var r = await http.Send(HttpMethod.Post, SchoolAddress.Join(http.Base, "login/token.php"), form, null, stop).ConfigureAwait(false);
            if (r.Status == 404) throw new SchoolProblem("Busy Bunny could not find Moodle's sign-in at that address. Check the school address.", 404);
            if (r.Status != 200) throw new SchoolProblem("Moodle answered with an error, code " + r.Status + ".", r.Status);
            var root = SchoolJson.Parse(r.Body, "Moodle");
            string token = SchoolJson.Str(root, "token");
            if (token.Length > 0) return token;
            string code = SchoolJson.Str(root, "errorcode");
            if (code == "invalidlogin") throw new SchoolProblem("Moodle did not accept that username and password. If you sign in to Moodle through a school page, use the security key from Preferences, Security keys instead.", 401);
            throw new SchoolProblem("Your school does not allow signing in for the Moodle app this way. Try Preferences, Security keys instead.", 403, blocked: true);
        }
    }
}
