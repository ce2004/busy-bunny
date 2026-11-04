using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace BusyBunny
{
    // The school connection (Morgan's sweep 12.6, item 8): Canvas or Moodle, read with her own token. This file holds
    // what both share: the things read from school, the plain text helpers and the one HTTP door (HTTPS to her
    // school's address only).
    // SchoolApi.cs talks to Canvas and Moodle, SchoolConnection.cs turns it all into tasks, SchoolBunny.cs gives
    // Smarter Bunny its read-only tools. Nothing here ever sends, submits or changes anything at the school.

    /// <summary>A class she is in.</summary>
    public sealed class SchoolCourse
    {
        public string Id { get; set; } = "";
        public string Name { get; set; } = "";
        public string Code { get; set; } = "";
        /// <summary>Her current grade as the school shows it, like A minus or 92.5 percent, or empty when there is none yet.</summary>
        public string Grade { get; set; } = "";
    }

    /// <summary>Something with a due date: an assignment, quiz or test.</summary>
    public sealed class SchoolWork
    {
        /// <summary>Unique for the school and the item, like canvas:123, moodle:assign:5 or ical:the event's UID.</summary>
        public string Key { get; set; } = "";
        public string CourseId { get; set; } = "";
        public string Course { get; set; } = "";
        public string CourseCode { get; set; } = "";
        public string Name { get; set; } = "";
        /// <summary>When it is due, in her own time zone.</summary>
        public DateTime? Due { get; set; }
        public bool HasTime { get; set; } = true;
        public bool IsTest { get; set; }
        public bool Submitted { get; set; }
        public bool Missing { get; set; }
        public bool Graded { get; set; }
        public double? Points { get; set; }
        /// <summary>Her score as the school shows it, like 9 of 10, or empty.</summary>
        public string Score { get; set; } = "";
        public DateTime? GradedAt { get; set; }
        /// <summary>The instructions, as plain text.</summary>
        public string Details { get; set; } = "";
        public string Rubric { get; set; } = "";
        /// <summary>Moodle's assignment id, to ask for its submission later.</summary>
        public int AssignId { get; set; }
    }

    public sealed class SchoolGrade
    {
        public string Course { get; set; } = "";
        public string Overall { get; set; } = "";
        public List<string> Recent { get; set; } = new();
    }

    public sealed class SchoolNews
    {
        public string Course { get; set; } = "";
        public string Title { get; set; } = "";
        public string Text { get; set; } = "";
        public DateTime? Posted { get; set; }
    }

    public sealed class SchoolSite
    {
        public string Name { get; set; } = "";
        public string User { get; set; } = "";
    }

    /// <summary>
    /// Something went wrong, with a short plain line to say. Status is the HTTP code when there was one. Blocked means
    /// the school does not allow tokens or this kind of access.
    /// </summary>
    public sealed class SchoolProblem : Exception
    {
        public int Status { get; }
        public bool Blocked { get; }
        /// <summary>The school asked Busy Bunny to slow down (Canvas: Rate Limit Exceeded).</summary>
        public bool Throttled { get; }
        public SchoolProblem(string message, int status = 0, bool blocked = false, bool throttled = false) : base(message) { Status = status; Blocked = blocked; Throttled = throttled; }
    }

    public static class SchoolText
    {
        private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled);
        private static readonly Regex Breaks = new(@"<\s*(br|/p|/div|/li|/h[1-6]|/tr)\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Items = new(@"<\s*li\b[^>]*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex Hidden = new(@"<\s*(script|style)\b.*?<\s*/\s*\1\s*>", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
        private static readonly Regex Spaces = new(@"[ \t ]+", RegexOptions.Compiled);

        /// <summary>HTML from the school as plain text: tags gone, entities read, line breaks kept, at most max letters.</summary>
        public static string Plain(string? html, int max = 4000)
        {
            if (string.IsNullOrWhiteSpace(html)) return "";
            string s = Hidden.Replace(html, " ");
            s = Breaks.Replace(s, "\n");
            s = Items.Replace(s, "\n");
            s = Tags.Replace(s, " ");
            s = WebUtility.HtmlDecode(s).Replace("\r", "");
            s = Spaces.Replace(s, " ");
            // A tag closed right before a full stop left a space: "Hall 2 ." reads as "Hall 2."
            s = Regex.Replace(s, @" +([.,;:!?)])", "$1");
            s = string.Join("\n", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
            if (s.Length > max) s = s[..max].TrimEnd() + "...";
            return s;
        }

        /// <summary>A task name from the school: plain, one line, without the pound sign or the asterisk, which her screen reader reads out.</summary>
        public static string Name(string? s)
        {
            s = Plain(s, 200).Replace('\n', ' ');
            s = Regex.Replace(s, @"[\u0023\u002A]+\s*", "");
            return Spaces.Replace(s, " ").Trim();
        }

        private static readonly Regex TestWords = new(@"\b(quiz|quizzes|exam|exams|midterm|midterms|final exam|test|tests)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static bool IsTest(string name) => TestWords.IsMatch(name);

        /// <summary>The letters and digits only, in capitals, for matching codes like PSY-111-01 and PSY111.</summary>
        public static string Squash(string s) => new string(s.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();

        /// <summary>A when for Bunny and for spoken lines: Friday, November 6 at 11:59 pm.</summary>
        public static string When(DateTime? due, bool hasTime)
        {
            if (due is not DateTime d) return "no due date";
            string day = d.ToString("dddd, MMMM d", CultureInfo.InvariantCulture) + (d.Year != DateTime.Now.Year ? ", " + d.Year : "");
            return hasTime ? day + " at " + Words.Time(d) : day;
        }

        // Moodle's timeline names end with what happens: "Essay 1 is due", "Quiz 3 closes", "Quiz 3 opens".
        private static readonly Regex Tail = new(@"\s*(\((?:[^)]*\s)?(?:closes|close|due|deadline)\)|\b(?:is due( now)?|is closing|closes|close|due|deadline|should be completed|must be completed))\s*\.?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex NotDue = new(@"(\bopens\b|\bopen\b|\(.*opens\)|\bis due to be graded|\bstarts\b)\s*\.?\s*$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>A deadline's name as a task name: "Essay 1 is due" is Essay 1. Null for things that are not due dates, like a quiz opening.</summary>
        public static string? CleanDeadline(string summary)
        {
            string s = Name(summary);
            if (s.Length == 0 || NotDue.IsMatch(s)) return null;
            string cut = Tail.Replace(s, "").Trim().TrimEnd(':', '-', ',').Trim();
            return cut.Length > 0 ? cut : s;
        }

        /// <summary>A number of points without needless decimals.</summary>
        public static string Num(double d) => Math.Round(d, 2).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The school's address: HTTPS only, no query, no trailing slash. Everything is sent to this host and nowhere else.</summary>
    public static class SchoolAddress
    {
        public const string MoodleDefault = "https://courses.furman.edu";

        /// <summary>The address cleaned up, or null when it cannot be a school address (not HTTPS, or not a web address).</summary>
        public static Uri? Parse(string? text)
        {
            string s = (text ?? "").Trim();
            if (s.Length == 0) return null;
            if (s.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) s = "https://" + s[9..];
            if (!s.Contains("://")) s = "https://" + s;
            if (!Uri.TryCreate(s, UriKind.Absolute, out var u) || u.Scheme != Uri.UriSchemeHttps || u.Host.Length == 0 || !u.Host.Contains('.')) return null;
            string path = u.AbsolutePath.TrimEnd('/');
            foreach (string tail in new[] { "/login/index.php", "/my", "/dashboard", "/login", "/my/courses.php" })
                if (path.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) { path = path[..^tail.Length]; break; }
            return new Uri(u.Scheme + "://" + u.Authority + path.TrimEnd('/'));
        }

        public static string Join(Uri b, string path) => b.ToString().TrimEnd('/') + "/" + path.TrimStart('/');
    }

    /// <summary>One answer from the school.</summary>
    public sealed record SchoolReply(int Status, string Body, string? Next, string? RateLeft);

    /// <summary>
    /// The one door to the school. Every request must be HTTPS to the school's own host: anything else is refused before
    /// it is sent, and redirects are never followed, so a token or link can never leave for another server. Read only:
    /// Canvas is only ever asked with GET; Moodle's web service takes POST for reading too, so the token stays out of the address.
    /// </summary>
    public sealed class SchoolHttp
    {
        /// <summary>For the self-test: canned answers instead of the internet.</summary>
        internal static HttpMessageHandler? TestHandler;
        private static HttpClient? _real;
        private static HttpClient Client => TestHandler != null
            ? new HttpClient(TestHandler, false) { Timeout = TimeSpan.FromSeconds(30) }
            : _real ??= new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All, ConnectTimeout = TimeSpan.FromSeconds(15) }) { Timeout = TimeSpan.FromSeconds(40) };

        public Uri Base { get; }
        public string Host => Base.Host;

        public SchoolHttp(string address)
        {
            Base = SchoolAddress.Parse(address) ?? throw new SchoolProblem("That school address does not look right. It should start with https, like " + SchoolAddress.MoodleDefault + ".");
        }

        /// <summary>Whether a web address is on the school's own host, over HTTPS.</summary>
        public bool Ours(Uri u) => u.Scheme == Uri.UriSchemeHttps && u.Host.Equals(Base.Host, StringComparison.OrdinalIgnoreCase) && u.Port == Base.Port;

        public async Task<SchoolReply> Send(HttpMethod method, string url, HttpContent? body, string? bearer, CancellationToken stop)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || !Ours(u))
                throw new SchoolProblem("Busy Bunny only talks to your school at " + Base.Host + ", so it did not follow that.");
            using var req = new HttpRequestMessage(method, u) { Content = body };
            req.Headers.UserAgent.ParseAdd("BusyBunny/" + Updater.Current);
            req.Headers.Accept.ParseAdd("application/json");
            if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            HttpResponseMessage resp;
            try { resp = await Client.SendAsync(req, HttpCompletionOption.ResponseContentRead, stop).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!stop.IsCancellationRequested) { throw new SchoolProblem("Your school took too long to answer. Try again in a little while."); }
            catch (HttpRequestException) { throw new SchoolProblem("Busy Bunny could not reach " + Base.Host + ". Check your internet connection and the school address."); }
            using (resp)
            {
                int code = (int)resp.StatusCode;
                if (code >= 300 && code < 400)
                    throw new SchoolProblem("Your school sent Busy Bunny to a sign-in page or another address. Check the school address, or get a new token or link.", code);
                string text;
                try { text = await resp.Content.ReadAsStringAsync(stop).ConfigureAwait(false); }
                catch (Exception e) when (e is HttpRequestException or System.IO.IOException) { throw new SchoolProblem("The connection to " + Base.Host + " dropped. Try again in a little while."); }
                string? next = null;
                if (resp.Headers.TryGetValues("Link", out var links))
                    foreach (var part in string.Join(",", links).Split(','))
                        if (part.Contains("rel=\"next\"") && part.IndexOf('<') is int a and >= 0 && part.IndexOf('>') is int b && b > a) next = part[(a + 1)..b];
                string? rate = resp.Headers.TryGetValues("X-Rate-Limit-Remaining", out var r) ? r.FirstOrDefault() : null;
                return new SchoolReply(code, text, next, rate);
            }
        }
    }

}
