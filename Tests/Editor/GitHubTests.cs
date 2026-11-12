using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Glitchr.Store.Tests
{
    /// <summary>
    /// A GitHub source as the window reads it, against canned answers of GitHub's REST API: an
    /// owner's repositories matching a pattern, the ones that are UPM packages, their tags, the
    /// ETags asked again, the rate limit respected, a token sent to GitHub's API only.
    /// </summary>
    public class GitHubTests
    {
        /// <summary>GitHub's answers by path; every request kept.</summary>
        private sealed class FakeGitHub : IWebTransport
        {
            public readonly Dictionary<string, Func<WebRequest, WebResponse>> Routes = new Dictionary<string, Func<WebRequest, WebResponse>>();
            public readonly List<WebRequest> Requests = new List<WebRequest>();
            public int Remaining = 59;

            public void Json(string path, string body, string etag = null, string link = null)
            {
                Routes[path] = request =>
                {
                    if (etag != null && request.Headers.TryGetValue("If-None-Match", out string seen) && seen == etag) return Answer(304, null);
                    var answer = Answer(200, body);
                    if (etag != null) answer.Headers["ETag"] = etag;
                    if (link != null) answer.Headers["Link"] = link;

                    return answer;
                };
            }

            public WebResponse Answer(long status, string body)
            {
                var response = new WebResponse { Status = status, Text = body };
                response.Headers["X-RateLimit-Remaining"] = Remaining.ToString();
                response.Headers["X-RateLimit-Reset"] = "1893456000"; // 2030-01-01

                return response;
            }

            public Task<WebResponse> Send(WebRequest request)
            {
                Requests.Add(request);
                string path = request.Url.Substring(GitHubApi.Root.Length);

                return Task.FromResult(Routes.TryGetValue(path, out var route) ? route(request) : Answer(404, "{\"message\":\"Not Found\"}"));
            }
        }

        /// <summary>
        /// The canned answers are complete tasks, so the reading runs through at once: the tests stay
        /// synchronous, as the Test Framework of Unity 2021.3 needs.
        /// </summary>
        private static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "forge-store-github-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(directory, true);

        private static FakeGitHub AnOrganisation()
        {
            var github = new FakeGitHub();
            github.Json("/users/example-org", "{\"login\":\"example-org\",\"type\":\"Organization\"}", "\"u1\"");
            github.Json("/orgs/example-org/repos?type=all&per_page=100",
                "[{\"name\":\"unity-tool\",\"private\":false,\"default_branch\":\"main\",\"html_url\":\"https://github.com/example-org/unity-tool\",\"owner\":{\"login\":\"example-org\"}},"
                + "{\"name\":\"unity-notes\",\"private\":false,\"default_branch\":\"main\",\"owner\":{\"login\":\"example-org\"}},"
                + "{\"name\":\"website\",\"private\":false,\"default_branch\":\"main\",\"owner\":{\"login\":\"example-org\"}}]",
                "\"r1\"", "<https://api.github.com/organizations/1/repos?type=all&per_page=100&page=2>; rel=\"next\", <https://api.github.com/organizations/1/repos?type=all&per_page=100&page=2>; rel=\"last\"");
            github.Json("/organizations/1/repos?type=all&per_page=100&page=2",
                "[{\"name\":\"Unity-Secret\",\"private\":true,\"default_branch\":\"develop\",\"owner\":{\"login\":\"example-org\"}}]", "\"r2\"");
            github.Json("/repos/example-org/unity-tool/contents/package.json", "{\"name\":\"com.example.tool\",\"displayName\":\"Example Tool\",\"version\":\"1.3.0-dev\",\"description\":\"A tool.\"}", "\"p1\"");
            github.Json("/repos/example-org/unity-tool/tags?per_page=100", "[{\"name\":\"v1.10.0\"},{\"name\":\"latest-build\"},{\"name\":\"v1.2.0\"},{\"name\":\"v1.10.0-beta.2\"}]", "\"t1\"");
            github.Json("/repos/example-org/Unity-Secret/contents/package.json", "{\"name\":\"com.example.secret\",\"version\":\"0.1.0\"}");
            github.Json("/repos/example-org/Unity-Secret/tags?per_page=100", "[]");
            // unity-notes has no package.json: GitHub answers 404, it is not a package.

            return github;
        }

        [Test]
        public void AnOrganisationsMatchingRepositoriesThatArePackagesAreListedWithTheirTags()
        {
            FakeGitHub github = AnOrganisation();
            var api = new GitHubApi(github, new GitHubCache(), limit: new GitHubRateLimit());
            var errors = new List<string>();

            List<GitHubPackage> packages = Sync(GitHubCatalog.Packages(api, "example-org", "unity-*", errors));

            CollectionAssert.IsEmpty(errors);
            CollectionAssert.AreEqual(new[] { "com.example.secret", "com.example.tool" }, packages.Select(p => p.Name).ToArray(), "website does not match, unity-notes is no package; the second page is read");
            GitHubPackage tool = packages.Single(p => p.Name == "com.example.tool");
            Assert.AreEqual("Example Tool", tool.DisplayName);
            CollectionAssert.AreEqual(new[] { "v1.10.0", "v1.10.0-beta.2", "v1.2.0", "latest-build" }, tool.Tags, "versions first, newest first; 1.10 after 1.2");
            Assert.AreEqual("v1.10.0", tool.LatestTag);
            Assert.AreEqual("1.10.0", tool.LatestVersion);
            Assert.AreEqual("https://github.com/example-org/unity-tool.git", tool.GitUrl);
            GitHubPackage secret = packages.Single(p => p.Name == "com.example.secret");
            Assert.IsTrue(secret.Private);
            Assert.IsNull(secret.LatestTag);
            Assert.AreEqual("0.1.0", secret.LatestVersion, "no tag: the default branch's version");
            Assert.AreEqual(GitHubApi.RawType, github.Requests.First(r => r.Url.EndsWith("/contents/package.json", StringComparison.Ordinal)).Headers["Accept"]);
            Assert.IsTrue(github.Requests.All(r => !r.Headers.ContainsKey("Authorization")), "no token: none sent");
        }

        [Test]
        public void AskedAgainTheAnswersAreConditionalAndTheCacheKeepsThem()
        {
            FakeGitHub github = AnOrganisation();
            string file = Path.Combine(directory, "github.json");
            var first = new GitHubApi(github, new GitHubCache(file), limit: new GitHubRateLimit());
            Sync(GitHubCatalog.Packages(first, "example-org", "unity-*"));
            first.SaveCache();
            Assert.IsTrue(File.Exists(file));
            github.Requests.Clear();

            var second = new GitHubApi(github, new GitHubCache(file), limit: new GitHubRateLimit());
            List<GitHubPackage> again = Sync(GitHubCatalog.Packages(second, "example-org", "unity-*"));

            Assert.AreEqual(2, again.Count, "the same list, from 304s");
            Assert.AreEqual("\"u1\"", github.Requests.Single(r => r.Url.EndsWith("/users/example-org", StringComparison.Ordinal)).Headers["If-None-Match"]);
            Assert.AreEqual("\"p1\"", github.Requests.Single(r => r.Url.EndsWith("/unity-tool/contents/package.json", StringComparison.Ordinal)).Headers["If-None-Match"]);
        }

        [Test]
        public void ASpentRateLimitStopsTheRequestsUntilItResetsAndTheCacheAnswers()
        {
            FakeGitHub github = AnOrganisation();
            var limit = new GitHubRateLimit();
            var now = new DateTime(2029, 6, 1, 12, 0, 0, DateTimeKind.Utc);
            var cache = new GitHubCache();
            var api = new GitHubApi(github, cache, limit: limit, now: () => now);
            Sync(api.Get("/users/example-org"));

            github.Routes["/orgs/example-org/repos?type=all&per_page=100"] = _ =>
            {
                github.Remaining = 0;
                return github.Answer(403, "{\"message\":\"API rate limit exceeded\"}");
            };
            Assert.Throws<GitHubRateLimitException>(() => Sync(GitHubCatalog.Packages(api, "example-org", "unity-*")));
            Assert.IsTrue(limit.Blocked(now));
            int sent = github.Requests.Count;

            GitHubApi.Answer cached = Sync(api.Get("/users/example-org"));
            Assert.IsTrue(cached.Ok && cached.Stale, "what was read before answers from the cache");
            Assert.AreEqual(sent, github.Requests.Count, "nothing sent while the limit is spent");
            Assert.IsFalse(limit.Blocked(new DateTime(2030, 1, 1, 0, 0, 1, DateTimeKind.Utc)), "it resets at X-RateLimit-Reset");

            var secondary = new GitHubRateLimit();
            var refused = new WebResponse { Status = 429 };
            refused.Headers["Retry-After"] = "30";
            secondary.Read(refused, now);
            Assert.IsTrue(secondary.Blocked(now.AddSeconds(29)));
            Assert.IsFalse(secondary.Blocked(now.AddSeconds(31)), "Retry-After is waited, no more");
        }

        [Test]
        public void TheTokenGoesToGitHubsApiOnlyAndAUsersOwnRepositoriesAreItsPrivateOnesToo()
        {
            var github = new FakeGitHub();
            github.Json("/users/someone", "{\"login\":\"someone\",\"type\":\"User\"}");
            github.Json("/user", "{\"login\":\"Someone\"}");
            github.Json("/user/repos?affiliation=owner&visibility=all&per_page=100",
                "[{\"name\":\"unity-mine\",\"private\":true,\"owner\":{\"login\":\"someone\"}},{\"name\":\"unity-theirs\",\"private\":true,\"owner\":{\"login\":\"other\"}}]");
            github.Json("/repos/someone/unity-mine/contents/package.json", "{\"name\":\"com.someone.mine\",\"version\":\"2.0.0\"}");
            const string token = "ghp_TESTONLY0000000000000000000000000000";
            var api = new GitHubApi(github, new GitHubCache(), () => Task.FromResult(token), new GitHubRateLimit());

            List<GitHubPackage> packages = Sync(GitHubCatalog.Packages(api, "someone", "unity-*"));

            CollectionAssert.AreEqual(new[] { "com.someone.mine" }, packages.Select(p => p.Name).ToArray(), "another owner's repository is left out");
            Assert.IsTrue(github.Requests.All(r => r.Url.StartsWith(GitHubApi.Root + "/", StringComparison.Ordinal) && r.Headers["Authorization"] == "Bearer " + token));
            Assert.Throws<ArgumentException>(() => Sync(api.Get("https://example.com/steal")), "nothing but api.github.com");
            Assert.Throws<ArgumentException>(() => Sync(GitHubCatalog.Packages(api, "bad/owner", "*")));
        }

        [Test]
        public void PatternsTagsAndVersions()
        {
            Assert.IsTrue(GitHubCatalog.Matches("unity-*", "unity-store"));
            Assert.IsTrue(GitHubCatalog.Matches("unity-*", "Unity-Store"), "GitHub's names ignore case");
            Assert.IsFalse(GitHubCatalog.Matches("unity-*", "my-unity-store"));
            Assert.IsTrue(GitHubCatalog.Matches("unity-*, upm-?", "upm-a"));
            Assert.IsFalse(GitHubCatalog.Matches("unity.*", "unityXstore"), "a dot is a dot");
            Assert.IsTrue(GitHubCatalog.Matches("", "anything"));

            Assert.AreEqual("1.2.0", GitHubCatalog.TagVersion("v1.2.0"));
            Assert.AreEqual("1.2.0-rc.1", GitHubCatalog.TagVersion("release-1.2.0-rc.1"));
            Assert.IsNull(GitHubCatalog.TagVersion("nightly"));
            Assert.Greater(GitHubCatalog.CompareVersions("1.10.0", "1.9.9"), 0);
            Assert.Greater(GitHubCatalog.CompareVersions("1.0.0", "1.0.0-beta"), 0);
            Assert.Greater(GitHubCatalog.CompareVersions("1.0.0-beta.11", "1.0.0-beta.2"), 0);
            Assert.IsTrue(GitHubCatalog.Updatable("1.2.0", "1.10.0"));
            Assert.IsFalse(GitHubCatalog.Updatable("1.10.0", "1.10.0"));
            Assert.IsFalse(GitHubCatalog.Updatable(null, "1.0.0"), "not installed: nothing to update");

            Assert.AreEqual("https://api.github.com/x?page=3", GitHubApi.NextLink("<https://api.github.com/x?page=1>; rel=\"prev\", <https://api.github.com/x?page=3>; rel=\"next\""));
            Assert.IsNull(GitHubApi.NextLink("<https://api.github.com/x?page=1>; rel=\"prev\""));
        }

        [Test]
        public void AGitHubPackageIsInstalledByItsUrlAtTheChosenReference()
        {
            var item = new HubItem { Name = "com.example.tool", GitUrl = "https://github.com/example-org/unity-tool.git", GitHub = new GitHubPackage { Owner = "example-org", Repository = "unity-tool", Tags = new List<string> { "v2.0.0", "v1.0.0" } } };
            Assert.AreEqual("https://github.com/example-org/unity-tool.git#v2.0.0", HubInstaller.GitIdentifier(item), "the latest tag by default");
            Assert.AreEqual("https://github.com/example-org/unity-tool.git#develop", HubInstaller.GitIdentifier(item, "develop"));
            var plain = new HubItem { Name = "x", GitUrl = "https://example.com/x.git#v1.0.0" };
            Assert.AreEqual("https://example.com/x.git#v1.0.0", HubInstaller.GitIdentifier(plain), "a Git source keeps its own reference");
        }

        [Test]
        public void ACredentialHelpersAnswerGivesItsPasswordOnlyWhenItIsAToken()
        {
            Assert.AreEqual("ghp_TESTONLY0000000000000000000000000000", GitHubToken.ParseCredential("protocol=https\nhost=github.com\nusername=someone\npassword=ghp_TESTONLY0000000000000000000000000000\n"));
            Assert.IsNull(GitHubToken.ParseCredential("protocol=https\nhost=github.com\nusername=someone\npassword=short\n"));
            Assert.IsNull(GitHubToken.ParseCredential("protocol=https\nhost=github.com\n"));
            Assert.IsFalse(GitHubToken.LooksLikeToken("ghp_x\" -w \"injected0000000000000"), "nothing a shell or the keychain could read as more");
        }

        [Test]
        public void AHubsThemeIsDataOnly()
        {
            HubTheme theme = HubTheme.Of(Json.Parse("{\"name\":\"Example\",\"color\":\"#123456\",\"theme\":{\"accentDark\":\"#abc\",\"banner\":\"https://example.com/b.png\",\"logo\":\"https://example.com/l.png\"}}"));
            Assert.AreEqual("Example", theme.Name);
            Assert.AreEqual("#123456", theme.Accent, "the older color key");
            Assert.AreEqual("#abc", theme.AccentDark);
            Assert.AreEqual("https://example.com/l.png", theme.Logo);
            Assert.IsNull(HubTheme.Colour("red; background: url(x)"), "a colour, nothing else");
            Assert.IsNull(HubTheme.Of(Json.Parse("{\"name\":\"Plain\"}")), "no theme: the plain look");
        }

        [Test]
        public void APresetIsAPackagesHubSectionAndSourcesAreToldApartByWhatTheyRead()
        {
            Assert.IsNotNull(HubPresets.HubOf("{\"name\":\"com.example.base\",\"hub\":{\"name\":\"Example\",\"sources\":[{\"type\":\"github\",\"owner\":\"example-org\",\"pattern\":\"unity-*\"}]}}"));
            Assert.IsNull(HubPresets.HubOf("{\"name\":\"com.example.tool\",\"version\":\"1.0.0\"}"));

            var a = new HubSource { Type = HubSource.GitHub, Owner = "Example-Org", Pattern = "unity-*" };
            var b = new HubSource { Type = HubSource.GitHub, Owner = "example-org", Pattern = "unity-*", Name = "Another name" };
            Assert.AreEqual(a.Key, b.Key);
            Assert.AreNotEqual(a.Key, new HubSource { Type = HubSource.GitHub, Owner = "example-org", Pattern = "upm-*" }.Key);
            Assert.AreEqual("GitHub Example-Org/unity-*", a.Label);
        }
    }
}
