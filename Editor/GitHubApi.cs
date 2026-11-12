using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Glitchr.Store
{
    /// <summary>GitHub's rate limit is spent: nothing is asked of it before <see cref="Until"/>.</summary>
    public sealed class GitHubRateLimitException : Exception
    {
        public DateTime Until { get; }

        public GitHubRateLimitException(DateTime until, bool authenticated)
            : base("GitHub's rate limit is reached until " + until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture)
                + (authenticated ? "." : " (60 requests an hour without a token; add a GitHub token in Sources for 5,000)."))
        {
            Until = until;
        }
    }

    /// <summary>
    /// What GitHub said of its rate limit (X-RateLimit-Remaining and -Reset, Retry-After), shared by
    /// every request of the editor: once it is spent, nothing is sent before it resets.
    /// </summary>
    public sealed class GitHubRateLimit
    {
        public static readonly GitHubRateLimit Shared = new GitHubRateLimit();

        public int? Remaining { get; private set; }
        public DateTime BlockedUntil { get; private set; } = DateTime.MinValue;

        public bool Blocked(DateTime now) => now < BlockedUntil;

        public void Read(WebResponse response, DateTime now)
        {
            if (int.TryParse(response.Header("X-RateLimit-Remaining"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int remaining)) Remaining = remaining;
            DateTime? reset = long.TryParse(response.Header("X-RateLimit-Reset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long epoch)
                ? DateTimeOffset.FromUnixTimeSeconds(epoch).UtcDateTime
                : (DateTime?)null;
            bool refused = response.Status == 403 || response.Status == 429;
            if (refused && int.TryParse(response.Header("Retry-After"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds))
            {
                BlockedUntil = now.AddSeconds(seconds); // the secondary limit
            }
            else if (Remaining == 0 && (refused || response.Ok))
            {
                BlockedUntil = reset ?? now.AddMinutes(1);
            }
        }
    }

    /// <summary>
    /// The answers GitHub gave, by request, with their ETag: asked again with If-None-Match, a
    /// "304 Not Modified" reuses them (and costs no request of the rate limit when signed in).
    /// Kept in the user's folder, never in a project; bodies over 512 KB are not kept.
    /// </summary>
    public sealed class GitHubCache
    {
        public const int MaxBody = 512 * 1024;
        public const int MaxEntries = 1000;

        public sealed class Entry
        {
            public string ETag;
            public string Body;
            public string Next;
            public double Time;
        }

        private readonly string path;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();
        private bool changed;

        public GitHubCache(string path = null)
        {
            this.path = path;
            if (path == null || !File.Exists(path)) return;
            try
            {
                foreach (KeyValuePair<string, object> pair in Json.Object(Json.Parse(File.ReadAllText(path))) ?? new Dictionary<string, object>())
                {
                    entries[pair.Key] = new Entry
                    {
                        ETag = Json.String(Json.Get(pair.Value, "etag")),
                        Body = Json.String(Json.Get(pair.Value, "body")),
                        Next = Json.String(Json.Get(pair.Value, "next")),
                        Time = Json.Get(pair.Value, "time") is double t ? t : 0,
                    };
                }
            }
            catch (Exception)
            {
                entries.Clear(); // a damaged cache is a cache to rebuild
            }
        }

        /// <summary>The user's cache file: Forge Store/github.json in the local application data.</summary>
        public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge Store", "github.json");

        public int Count => entries.Count;

        public Entry Get(string key) => entries.TryGetValue(key, out Entry entry) ? entry : null;

        public void Put(string key, Entry entry)
        {
            if (entry.Body != null && entry.Body.Length > MaxBody) return;
            entry.Time = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            entries[key] = entry;
            changed = true;
            foreach (string old in entries.OrderBy(e => e.Value.Time).Take(Math.Max(0, entries.Count - MaxEntries)).Select(e => e.Key).ToList()) entries.Remove(old);
        }

        public void Save()
        {
            if (path == null || !changed) return;
            var root = new Dictionary<string, object>();
            foreach (KeyValuePair<string, Entry> pair in entries)
            {
                root[pair.Key] = new Dictionary<string, object> { ["etag"] = pair.Value.ETag, ["body"] = pair.Value.Body, ["next"] = pair.Value.Next, ["time"] = pair.Value.Time };
            }
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            File.WriteAllText(path, Json.Write(root), new UTF8Encoding(false));
            changed = false;
        }
    }

    /// <summary>
    /// GitHub's REST API (api.github.com), read only: conditional requests (ETag), the rate limit
    /// respected, pages followed by their Link header. The token, when there is one, is sent to
    /// api.github.com only, and never written anywhere.
    /// </summary>
    public sealed class GitHubApi
    {
        public const string Root = "https://api.github.com";
        public const string JsonType = "application/vnd.github+json";
        public const string RawType = "application/vnd.github.raw+json";

        private readonly IWebTransport transport;
        private readonly GitHubCache cache;
        private readonly GitHubRateLimit limit;
        private readonly Func<Task<string>> tokenProvider;
        private readonly Func<DateTime> now;
        private Task<string> token;

        public GitHubApi(IWebTransport transport, GitHubCache cache, Func<Task<string>> tokenProvider = null, GitHubRateLimit limit = null, Func<DateTime> now = null)
        {
            this.transport = transport;
            this.cache = cache ?? new GitHubCache();
            this.tokenProvider = tokenProvider ?? (() => Task.FromResult<string>(null));
            this.limit = limit ?? GitHubRateLimit.Shared;
            this.now = now ?? (() => DateTime.UtcNow);
        }

        public sealed class Answer
        {
            public long Status;
            public string Text;
            public string Next;
            /// <summary>From the cache, GitHub not asked (its rate limit spent).</summary>
            public bool Stale;
            public bool Ok => Status >= 200 && Status < 300;
            public object Json => string.IsNullOrEmpty(Text) ? null : global::Glitchr.Store.Json.Parse(Text);
        }

        /// <summary>Keeps the answers' ETags for the next reading.</summary>
        public void SaveCache() => cache.Save();

        /// <summary>Whether a token is used: the provider is asked once.</summary>
        public async Task<bool> Authenticated() => !string.IsNullOrEmpty(await Token());

        private Task<string> Token() => token ??= tokenProvider();

        /// <summary>A GET of the API: "/users/x" or a full https://api.github.com/... address (a Link).</summary>
        public async Task<Answer> Get(string path, string accept = JsonType)
        {
            string url = path.StartsWith("https://", StringComparison.Ordinal) ? path : Root + path;
            if (!url.StartsWith(Root + "/", StringComparison.Ordinal)) throw new ArgumentException("Not GitHub's API: " + url);
            string bearer = await Token();
            // Answers are kept per account: a private listing never answers for another token, or none.
            string key = Fingerprint(bearer) + " " + accept + " " + url;
            GitHubCache.Entry cached = cache.Get(key);
            if (limit.Blocked(now()))
            {
                if (cached != null) return new Answer { Status = 200, Text = cached.Body, Next = cached.Next, Stale = true };
                throw new GitHubRateLimitException(limit.BlockedUntil, !string.IsNullOrEmpty(bearer));
            }

            var request = new WebRequest { Url = url };
            request.Headers["Accept"] = accept;
            request.Headers["X-GitHub-Api-Version"] = "2022-11-28";
            if (!string.IsNullOrEmpty(bearer)) request.Headers["Authorization"] = "Bearer " + bearer;
            if (!string.IsNullOrEmpty(cached?.ETag)) request.Headers["If-None-Match"] = cached.ETag;
            WebResponse response = await transport.Send(request);
            if (response.Error != null && response.Status == 0) throw new Exception("GitHub: " + response.Error);
            limit.Read(response, now());

            if (response.Status == 304 && cached != null) return new Answer { Status = 200, Text = cached.Body, Next = cached.Next };
            if (response.Ok)
            {
                string next = NextLink(response.Header("Link"));
                cache.Put(key, new GitHubCache.Entry { ETag = response.Header("ETag"), Body = response.Text, Next = next });

                return new Answer { Status = response.Status, Text = response.Text, Next = next };
            }
            if (limit.Blocked(now()))
            {
                if (cached != null) return new Answer { Status = 200, Text = cached.Body, Next = cached.Next, Stale = true };
                throw new GitHubRateLimitException(limit.BlockedUntil, !string.IsNullOrEmpty(bearer));
            }

            return new Answer { Status = response.Status, Text = response.Text };
        }

        /// <summary>Every page of a list, followed by its Link header (at most <paramref name="pages"/>).</summary>
        public async Task<List<object>> GetAll(string path, int pages = 10)
        {
            var all = new List<object>();
            string next = path;
            for (int page = 0; page < pages && next != null; page++)
            {
                Answer answer = await Get(next);
                if (!answer.Ok) throw new GitHubException(answer.Status, next);
                all.AddRange(global::Glitchr.Store.Json.Array(answer.Json) ?? new List<object>());
                next = answer.Next;
            }

            return all;
        }

        /// <summary>The rel="next" address of a Link header, or null.</summary>
        public static string NextLink(string link)
        {
            if (string.IsNullOrEmpty(link)) return null;
            Match match = Regex.Match(link, "<([^>]+)>\\s*;\\s*rel=\"next\"");

            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>A short, one-way mark of the token (never the token): the cache's account.</summary>
        private static string Fingerprint(string token)
        {
            if (string.IsNullOrEmpty(token)) return "public";
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(token)), 0, 6).Replace("-", string.Empty).ToLowerInvariant();
            }
        }
    }

    /// <summary>GitHub refused a request (404 for a repository the token cannot read, 401 for a bad token).</summary>
    public sealed class GitHubException : Exception
    {
        public long Status { get; }

        public GitHubException(long status, string path) : base("GitHub answered " + status + " for " + Strip(path) + (status == 401 ? " (the token is refused)" : string.Empty))
        {
            Status = status;
        }

        private static string Strip(string path) => path.StartsWith(GitHubApi.Root, StringComparison.Ordinal) ? path.Substring(GitHubApi.Root.Length) : path;
    }
}
