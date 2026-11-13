using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Glitchr.Store
{
    /// <summary>A repository of a GitHub owner that is a UPM package: its package.json has a name.</summary>
    public sealed class GitHubPackage
    {
        public string Owner;
        public string Repository;
        public bool Private;
        public string DefaultBranch;
        public string Page;
        public string Name;
        public string DisplayName;
        public string Description;
        /// <summary>The version of the package.json on the default branch.</summary>
        public string Version;
        /// <summary>The package.json's dependencies on the default branch.</summary>
        public Dictionary<string, string> Dependencies;
        /// <summary>The repository's tags, newest version first.</summary>
        public List<string> Tags = new List<string>();

        public string LatestTag => Tags.FirstOrDefault(t => GitHubCatalog.TagVersion(t) != null);

        /// <summary>The version a project gets from the latest tag, else the default branch's.</summary>
        public string LatestVersion => LatestTag != null ? GitHubCatalog.TagVersion(LatestTag) : Version;

        public string GitUrl => GitHubCatalog.GitUrl(Owner, Repository);
    }

    /// <summary>
    /// A GitHub organisation's or user's repositories whose names match a pattern ("unity-*"), read
    /// through the REST API: the public ones without a token, the private ones the token can read.
    /// A repository is listed when it is a UPM package, its package.json at the root naming it.
    /// </summary>
    public static class GitHubCatalog
    {
        private static readonly Regex OwnerName = new Regex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$");

        public static bool ValidOwner(string owner) => !string.IsNullOrEmpty(owner) && OwnerName.IsMatch(owner);

        /// <summary>
        /// Whether a repository's name matches the pattern: globs ("*" any run, "?" one character),
        /// several separated by commas or spaces, case-insensitive as GitHub's names are. An empty
        /// pattern matches every repository.
        /// </summary>
        public static bool Matches(string pattern, string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string[] globs = (pattern ?? string.Empty).Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (globs.Length == 0) return true;

            return globs.Any(glob => Regex.IsMatch(name, "^" + Regex.Escape(glob).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        public static string GitUrl(string owner, string repository, string reference = null)
        {
            return "https://github.com/" + owner + "/" + repository + ".git" + (string.IsNullOrEmpty(reference) ? string.Empty : "#" + reference);
        }

        /// <summary>The version a tag names ("v1.2.0", "1.2.0", "release-1.2.0" give 1.2.0), or null.</summary>
        public static string TagVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag)) return null;
            Match match = Regex.Match(tag, "(\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?)$");

            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>Semantic versions compared: numbers, then a pre-release before its release.</summary>
        public static int CompareVersions(string a, string b)
        {
            if (a == b) return 0;
            if (a == null) return -1;
            if (b == null) return 1;
            string[] pa = a.Split(new[] { '-' }, 2), pb = b.Split(new[] { '-' }, 2);
            int[] na = Numbers(pa[0]), nb = Numbers(pb[0]);
            for (int i = 0; i < Math.Max(na.Length, nb.Length); i++)
            {
                int x = i < na.Length ? na[i] : 0, y = i < nb.Length ? nb[i] : 0;
                if (x != y) return x.CompareTo(y);
            }
            if (pa.Length != pb.Length) return pa.Length < pb.Length ? 1 : -1; // 1.0.0 > 1.0.0-beta
            if (pa.Length == 1) return 0;
            string[] ia = pa[1].Split('.'), ib = pb[1].Split('.');
            for (int i = 0; i < Math.Min(ia.Length, ib.Length); i++)
            {
                bool da = int.TryParse(ia[i], out int xa), db = int.TryParse(ib[i], out int xb);
                int c = da && db ? xa.CompareTo(xb) : da ? -1 : db ? 1 : string.CompareOrdinal(ia[i], ib[i]);
                if (c != 0) return c;
            }

            return ia.Length.CompareTo(ib.Length);
        }

        /// <summary>Whether a project at <paramref name="installed"/> may move to <paramref name="latest"/>.</summary>
        public static bool Updatable(string installed, string latest)
        {
            return !string.IsNullOrEmpty(installed) && !string.IsNullOrEmpty(latest) && CompareVersions(latest, installed) > 0;
        }

        private static int[] Numbers(string text)
        {
            return text.Split('.').Select(p => int.TryParse(p, out int n) ? n : 0).ToArray();
        }

        /// <summary>
        /// The owner's repositories this request may see: an organisation's (type=all: the private
        /// ones a member's token reads), a user's own when the token is theirs, else a user's public.
        /// </summary>
        public static async Task<List<object>> Repositories(GitHubApi api, string owner)
        {
            if (!ValidOwner(owner)) throw new ArgumentException("Not a GitHub owner: " + owner);
            GitHubApi.Answer account = await api.Get("/users/" + owner);
            if (account.Status == 404) throw new Exception("GitHub has no organisation or user named " + owner + ".");
            if (!account.Ok) throw new GitHubException(account.Status, "/users/" + owner);
            if (Json.String(Json.Get(account.Json, "type")) == "Organization")
            {
                return await api.GetAll("/orgs/" + owner + "/repos?type=all&per_page=100");
            }
            if (await api.Authenticated())
            {
                GitHubApi.Answer me = await api.Get("/user");
                if (me.Ok && string.Equals(Json.String(Json.Get(me.Json, "login")), owner, StringComparison.OrdinalIgnoreCase))
                {
                    return await api.GetAll("/user/repos?affiliation=owner&visibility=all&per_page=100");
                }
            }

            return await api.GetAll("/users/" + owner + "/repos?type=owner&per_page=100");
        }

        /// <summary>
        /// The owner's repositories matching the pattern that are UPM packages, with their tags.
        /// A repository that cannot be read is noted in <paramref name="errors"/> and skipped.
        /// </summary>
        public static async Task<List<GitHubPackage>> Packages(GitHubApi api, string owner, string pattern, List<string> errors = null)
        {
            var packages = new List<GitHubPackage>();
            foreach (object repo in await Repositories(api, owner))
            {
                string name = Json.String(Json.Get(repo, "name"));
                if (!Matches(pattern, name)) continue;
                string login = Json.String(Json.Get(repo, "owner", "login")) ?? owner;
                if (!string.Equals(login, owner, StringComparison.OrdinalIgnoreCase)) continue; // a collaborator's repository
                try
                {
                    GitHubApi.Answer manifest = await api.Get("/repos/" + login + "/" + name + "/contents/package.json", GitHubApi.RawType);
                    if (manifest.Status == 404) continue; // not a package: no package.json at its root
                    if (!manifest.Ok) throw new GitHubException(manifest.Status, "/repos/" + login + "/" + name + "/contents/package.json");
                    object json;
                    try { json = manifest.Json; }
                    catch (FormatException) { continue; }
                    string package = Json.String(Json.Get(json, "name"));
                    if (string.IsNullOrEmpty(package)) continue;

                    var found = new GitHubPackage
                    {
                        Owner = login,
                        Repository = name,
                        Private = Json.Get(repo, "private") is bool p && p,
                        DefaultBranch = Json.String(Json.Get(repo, "default_branch")),
                        Page = Json.String(Json.Get(repo, "html_url")),
                        Name = package,
                        DisplayName = Json.String(Json.Get(json, "displayName")),
                        Description = Json.String(Json.Get(json, "description")) ?? Json.String(Json.Get(repo, "description")),
                        Version = Json.String(Json.Get(json, "version")),
                        Dependencies = GitDependencies.Of(json),
                    };
                    GitHubApi.Answer tags = await api.Get("/repos/" + login + "/" + name + "/tags?per_page=100");
                    if (tags.Ok) found.Tags = SortTags((Json.Array(tags.Json) ?? new List<object>()).Select(t => Json.String(Json.Get(t, "name"))));
                    packages.Add(found);
                }
                catch (GitHubRateLimitException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    errors?.Add(login + "/" + name + ": " + e.Message);
                }
            }

            return packages.OrderBy(p => p.DisplayName ?? p.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>A repository's branches, for the version picker (asked only when it opens).</summary>
        public static async Task<List<string>> Branches(GitHubApi api, string owner, string repository)
        {
            return (await api.GetAll("/repos/" + owner + "/" + repository + "/branches?per_page=100", 3))
                .Select(b => Json.String(Json.Get(b, "name"))).Where(b => !string.IsNullOrEmpty(b)).ToList();
        }

        /// <summary>Tags that name a version first, newest first; the others after, as GitHub gave them.</summary>
        public static List<string> SortTags(IEnumerable<string> tags)
        {
            var list = tags.Where(t => !string.IsNullOrEmpty(t)).ToList();
            var versioned = list.Where(t => TagVersion(t) != null).OrderByDescending(t => TagVersion(t), Comparer<string>.Create(CompareVersions)).ToList();

            return versioned.Concat(list.Where(t => TagVersion(t) == null)).ToList();
        }
    }
}
