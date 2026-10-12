using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace BusyBunny
{
    /// <summary>
    /// Updates from the latest GitHub release, the same way Kova does: downloads this PC's exe
    /// (ARM64 or x64), checks its SHA-256 against the one GitHub publishes, swaps it in place (a
    /// running exe can be renamed, not overwritten) and restarts. Nothing changes until the new
    /// copy has fully arrived and checked out.
    /// </summary>
    public static class Updater
    {
        public const string Repo = "ce2004/busy-bunny";
        public const string Website = "https://github.com/" + Repo;

        public sealed record Release(Version Version, string Notes, string Url, string? Sha256);

        public static Version Current
        {
            get
            {
                var v = typeof(Updater).Assembly.GetName().Version ?? new Version(0, 0, 0);
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        public static string Arch => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "ARM64" : "x64";
        private static string AssetName => "BusyBunny-" + (RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x64") + ".exe";

        private static HttpClient Http()
        {
            var h = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            h.DefaultRequestHeaders.UserAgent.ParseAdd("BusyBunny/" + Current);
            return h;
        }

        private static readonly TimeSpan ApiLimit = TimeSpan.FromSeconds(30);
        private static readonly TimeSpan StallLimit = TimeSpan.FromSeconds(60);
        private const string WebPageInstead = "The internet connection answered with a web page instead of GitHub. Maybe a Wi-Fi sign-in page?";

        /// <summary>A release tag as a three-part version: v1.0.0, 1.0.0-beta and 1.0 all work. Null when it is not a version.</summary>
        public static Version? NormalizeTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return null;
            string t = tag.Trim().TrimStart('v', 'V');
            int cut = t.IndexOfAny(new[] { '-', '+', ' ' });
            if (cut >= 0) t = t[..cut];
            if (Version.TryParse(t, out var v)) return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return int.TryParse(t, out int major) && major >= 0 ? new Version(major, 0, 0) : null;
        }

        internal sealed record Parsed(Version Version, string Notes, string? Url, string? Sha256);

        internal static Parsed ParseRelease(string text, string asset)
        {
            if (text.TrimStart().StartsWith("<", StringComparison.Ordinal)) throw new InvalidOperationException(WebPageInstead);
            JsonDocument doc;
            try { doc = JsonDocument.Parse(text); }
            catch (JsonException) { throw new InvalidOperationException(WebPageInstead); }
            using (doc)
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("tag_name", out var tagEl) || tagEl.ValueKind != JsonValueKind.String ||
                    NormalizeTag(tagEl.GetString()) is not Version version)
                    throw new InvalidOperationException("GitHub's answer about the newest Busy Bunny could not be understood. Try again later.");
                string notes = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() ?? "" : "";
                if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
                {
                    foreach (var a in assets.EnumerateArray())
                    {
                        if (a.ValueKind != JsonValueKind.Object || !a.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || n.GetString() != asset) continue;
                        if (!a.TryGetProperty("browser_download_url", out var u) || u.ValueKind != JsonValueKind.String) continue;
                        string? sha = a.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is string ds && ds.StartsWith("sha256:") ? ds[7..] : null;
                        return new Parsed(version, notes, u.GetString(), sha);
                    }
                }
                return new Parsed(version, notes, null, null);
            }
        }

        private static async Task<string> ReleaseJsonAsync()
        {
            using var h = Http();
            using var limit = new System.Threading.CancellationTokenSource(ApiLimit);
            try
            {
                using var resp = await h.GetAsync("https://api.github.com/repos/" + Repo + "/releases/latest", limit.Token);
                string text = await resp.Content.ReadAsStringAsync(limit.Token);
                if (resp.Content.Headers.ContentType?.MediaType == "text/html" || text.TrimStart().StartsWith("<")) throw new InvalidOperationException(WebPageInstead);
                if ((int)resp.StatusCode is 403 or 429) throw new InvalidOperationException("GitHub is limiting update checks right now. Try again in an hour.");
                if ((int)resp.StatusCode == 404) throw new InvalidOperationException("There is no Busy Bunny release on GitHub yet.");
                if (!resp.IsSuccessStatusCode)
                    throw new InvalidOperationException("GitHub answered " + (int)resp.StatusCode + " " + resp.ReasonPhrase + ". Try again later.");
                return text;
            }
            catch (OperationCanceledException) when (limit.IsCancellationRequested)
            {
                throw new InvalidOperationException("GitHub did not answer. Check the internet connection and try again.");
            }
            catch (HttpRequestException e)
            {
                throw new InvalidOperationException("Could not reach GitHub: " + e.Message, e);
            }
        }

        /// <summary>The latest release, or null when this copy is already up to date.</summary>
        public static async Task<Release?> CheckAsync()
        {
            var p = ParseRelease(await ReleaseJsonAsync(), AssetName);
            if (p.Version <= Current) return null;
            if (p.Url == null) throw new InvalidOperationException("Version " + p.Version + " has no " + AssetName + " yet. Try again in a few minutes.");
            return new Release(p.Version, p.Notes, p.Url, p.Sha256);
        }

        /// <summary>Downloads, checks and swaps in the new exe, then starts it. The caller saves and closes.</summary>
        public static async Task InstallAsync(Release r)
        {
            string exe = Environment.ProcessPath!;
            string fresh = exe + ".new", old = exe + ".old";
            if (r.Sha256 == null) throw new InvalidOperationException("GitHub gave no checksum for the download, so it was not installed. Try again later.");
            try
            {
                await DownloadAsync(r.Url, fresh, r.Sha256);
                if (!TryDelete(old)) old = exe + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                ClearReadOnly(exe);
                // A virus scan often opens a brand new file for a moment: that clears by itself, so try a few times.
                for (int attempt = 1; ; attempt++)
                {
                    if (!File.Exists(exe) && File.Exists(fresh)) { File.Move(fresh, exe); break; }
                    try { File.Replace(fresh, exe, old, ignoreMetadataErrors: true); break; }
                    catch (IOException) when (attempt < 5) { await Task.Delay(500); }
                }
            }
            catch (Exception e)
            {
                try { if (!File.Exists(exe) && File.Exists(old)) File.Move(old, exe); } catch { }
                try { ClearReadOnly(fresh); File.Delete(fresh); } catch { }
                if (e is UnauthorizedAccessException)
                    throw new InvalidOperationException("Nothing was changed. Busy Bunny could not replace itself in " + Path.GetDirectoryName(exe) +
                        ". If it is in Program Files, move it to your Documents folder and try again.", e);
                throw;
            }
            try
            {
                Process.Start(new ProcessStartInfo(exe, "--after-update " + Environment.ProcessId) { UseShellExecute = false });
            }
            catch (Exception e)
            {
                throw new InvalidOperationException("Updated to version " + r.Version + ", but it could not restart by itself. Please start Busy Bunny again.", e);
            }
        }

        private static async Task DownloadAsync(string url, string fresh, string sha256)
        {
            using var h = Http();
            using var stall = new System.Threading.CancellationTokenSource(StallLimit);
            try
            {
                using var resp = await h.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token);
                if (resp.Content.Headers.ContentType?.MediaType == "text/html") throw new InvalidOperationException(WebPageInstead + " Nothing was changed.");
                resp.EnsureSuccessStatusCode();
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                ClearReadOnly(fresh);
                await using (var src = await resp.Content.ReadAsStreamAsync(stall.Token))
                await using (var dst = new FileStream(fresh, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                {
                    byte[] buf = new byte[1 << 16];
                    int n;
                    stall.CancelAfter(StallLimit);
                    while ((n = await src.ReadAsync(buf, stall.Token)) > 0)
                    {
                        stall.CancelAfter(StallLimit); // something arrived: the minute starts again
                        hash.AppendData(buf, 0, n);
                        await dst.WriteAsync(buf.AsMemory(0, n), stall.Token);
                    }
                }
                if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("The download was damaged. Nothing was changed.");
            }
            catch (OperationCanceledException) when (stall.IsCancellationRequested)
            {
                throw new IOException("The download stopped: nothing arrived for a minute. Nothing was changed. Try again when the connection is better.");
            }
            catch (HttpRequestException e)
            {
                throw new IOException("The download failed: " + e.Message + " Nothing was changed.", e);
            }
        }

        /// <summary>At startup after an update: wait for the old copy to close, then delete what it left behind.</summary>
        public static void FinishUpdate(int oldPid)
        {
            try { using var p = Process.GetProcessById(oldPid); p.WaitForExit(15000); } catch { }
            new System.Threading.Thread(() =>
            {
                for (int i = 0; i < 20 && !CleanLeftovers(); i++) System.Threading.Thread.Sleep(250);
            }) { IsBackground = true }.Start();
        }

        public static bool CleanLeftovers()
        {
            bool all = true;
            try
            {
                string exe = Environment.ProcessPath!;
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(exe)!, Path.GetFileName(exe) + ".old"))
                    all &= TryDelete(f);
                foreach (string f in Directory.GetFiles(Path.GetDirectoryName(exe)!))
                    if (Path.GetFileName(f).StartsWith(Path.GetFileName(exe) + ".old-", StringComparison.OrdinalIgnoreCase)) all &= TryDelete(f);
            }
            catch { }
            return all;
        }

        private static void ClearReadOnly(string path)
        {
            try
            {
                if (!File.Exists(path)) return;
                var a = File.GetAttributes(path);
                if ((a & FileAttributes.ReadOnly) != 0) File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
            }
            catch { }
        }

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path)) return true;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
                return true;
            }
            catch { return false; }
        }
    }
}
