using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Glitchr.Store
{
    /// <summary>A GitHub repository named by a Git URL: owner, name, the ?path= folder and the #reference.</summary>
    public sealed class GitHubRepository
    {
        public string Owner;
        public string Name;
        public string Folder;
        public string Reference;

        /// <summary>The repository of a github.com Git URL (https, ssh, git+https), or null for any other address.</summary>
        public static GitHubRepository Of(string gitUrl)
        {
            if (string.IsNullOrEmpty(gitUrl)) return null;
            string rest = gitUrl.Trim(), reference = null, folder = null;
            int hash = rest.IndexOf('#');
            if (hash >= 0) { reference = rest.Substring(hash + 1); rest = rest.Substring(0, hash); }
            int path = rest.IndexOf("?path=", StringComparison.Ordinal);
            if (path >= 0) { folder = rest.Substring(path + 6).Trim('/'); rest = rest.Substring(0, path); }
            string repo = null;
            foreach (string prefix in new[] { "https://github.com/", "git@github.com:", "ssh://git@github.com/", "git+https://github.com/" })
            {
                if (rest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) repo = rest.Substring(prefix.Length);
            }
            if (repo == null) return null;
            repo = repo.TrimEnd('/');
            if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) repo = repo.Substring(0, repo.Length - 4);
            string[] parts = repo.Split('/');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;

            return new GitHubRepository { Owner = parts[0], Name = parts[1], Folder = string.IsNullOrEmpty(folder) ? null : folder, Reference = string.IsNullOrEmpty(reference) ? null : reference };
        }

        /// <summary>The API path of its package.json at a reference (the URL's own when null, else the default branch).</summary>
        public string PackageJsonPath(string reference = null)
        {
            reference ??= Reference;

            return "/repos/" + Owner + "/" + Name + "/contents/" + (Folder == null ? string.Empty : Folder + "/") + "package.json"
                + (string.IsNullOrEmpty(reference) ? string.Empty : "?ref=" + Uri.EscapeDataString(reference));
        }
    }

    /// <summary>A Git package added with the one the user installs, because that one needs it.</summary>
    public sealed class GitDependency
    {
        public string Name;
        public string Version;
        public string Reference;
        public string Identifier;
        public string RequiredBy;
        public HubItem Item;

        public string Label => (Item?.Label ?? Name) + " " + (Reference ?? Version ?? "default branch");
    }

    /// <summary>What a Git install brings with it: the Git packages to add, the registries to declare, what could not be read.</summary>
    public sealed class GitDependencyPlan
    {
        public List<GitDependency> Added = new List<GitDependency>();
        /// <summary>Registry packages a source offers that a Git package needs: their registry goes into the manifest, Unity resolves them.</summary>
        public List<HubItem> Registries = new List<HubItem>();
        public List<string> Notes = new List<string>();

        /// <summary>The short line the window shows, or null when nothing is added.</summary>
        public string Summary => Added.Count == 0 ? null
            : "Also added, as Unity does not resolve a Git package's Git dependencies: " + string.Join(", ", Added.Select(a => a.Label + " (for " + a.RequiredBy + ")")) + ".";
    }

    /// <summary>
    /// Unity's Package Manager does not resolve a Git package's dependencies on other Git packages:
    /// a package.json can only name versions, which Unity looks for in its registries. So, when a
    /// Git package is installed, its dependencies are read (the package.json already fetched, else
    /// GitHub's contents API at the tag installed), and each one a source of the user offers as a
    /// Git package, and that the project has in no form, is added in the same AddAndRemove - then
    /// its own, a few levels down, each package once.
    /// </summary>
    public static class GitDependencies
    {
        public const int MaxDepth = 3;

        /// <summary>Reads a Git package's dependencies at a reference (null: unknown, nothing added for it).</summary>
        public delegate Task<Dictionary<string, string>> Reader(string gitUrl, string reference);

        /// <summary>A package.json's "dependencies": name, version.</summary>
        public static Dictionary<string, string> Of(object packageJson)
        {
            var map = Json.Object(Json.Get(packageJson, "dependencies"));
            if (map == null) return new Dictionary<string, string>();

            return map.Where(p => p.Value is string).ToDictionary(p => p.Key, p => (string)p.Value);
        }

        /// <summary>
        /// The Git packages to add with <paramref name="roots"/> (name, Git URL, reference): the
        /// dependencies the sources' <paramref name="offered"/> Git items cover and
        /// <paramref name="projectHas"/> lacks, to <paramref name="maxDepth"/> levels, cycles cut.
        /// </summary>
        public static async Task<GitDependencyPlan> Plan(IEnumerable<(string Name, string GitUrl, string Reference)> roots, IEnumerable<HubItem> offered, ICollection<string> projectHas, Reader read, int maxDepth = MaxDepth)
        {
            var plan = new GitDependencyPlan();
            var items = (offered ?? Enumerable.Empty<HubItem>()).ToList();
            var rootList = roots.ToList();
            var known = new HashSet<string>(projectHas ?? Array.Empty<string>(), StringComparer.Ordinal);
            foreach (var root in rootList) known.Add(root.Name);
            var queue = new Queue<(string Name, string Url, string Reference, int Depth)>(rootList.Select(r => (r.Name, r.GitUrl, r.Reference, 0)));
            var registries = new HashSet<string>();
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                if (next.Depth >= maxDepth) continue;
                Dictionary<string, string> dependencies;
                try { dependencies = await read(next.Url, next.Reference); }
                catch (Exception e)
                {
                    plan.Notes.Add(next.Name + "'s dependencies could not be read: " + e.Message);
                    continue;
                }
                if (dependencies == null) continue;
                foreach (KeyValuePair<string, string> dependency in dependencies.OrderBy(d => d.Key, StringComparer.Ordinal))
                {
                    if (known.Contains(dependency.Key)) continue;
                    HubItem git = items.FirstOrDefault(i => i.Name == dependency.Key && !string.IsNullOrEmpty(i.GitUrl) && !(i.Paid && !i.Installable));
                    if (git == null)
                    {
                        HubItem registry = items.FirstOrDefault(i => i.Name == dependency.Key && string.IsNullOrEmpty(i.GitUrl) && i.Origin?.Registry != null);
                        if (registry != null && registries.Add(registry.Name)) plan.Registries.Add(registry);

                        continue; // a registry's: Unity resolves it (its registry declared); else Unity's own error says what is missing
                    }
                    known.Add(dependency.Key);
                    string reference = git.GitHub != null ? Tag(git.GitHub.Tags, dependency.Value) ?? git.GitHub.DefaultBranch : null;
                    var added = new GitDependency
                    {
                        Name = dependency.Key,
                        Version = git.GitHub != null && reference != null ? GitHubCatalog.TagVersion(reference) ?? git.Version : git.Version,
                        Reference = reference,
                        Identifier = HubInstaller.GitIdentifier(git, reference),
                        RequiredBy = next.Name,
                        Item = git,
                    };
                    plan.Added.Add(added);
                    queue.Enqueue((added.Name, git.GitUrl, reference ?? GitHubRepository.Of(git.GitUrl)?.Reference, next.Depth + 1));
                }
            }

            return plan;
        }

        /// <summary>
        /// The tag a dependency on <paramref name="wanted"/> gets: that version's tag, else the newest
        /// of the same major version above it, else the newest tag; null without a versioned tag.
        /// </summary>
        public static string Tag(IEnumerable<string> tags, string wanted)
        {
            var versioned = GitHubCatalog.SortTags(tags ?? Enumerable.Empty<string>()).Where(t => GitHubCatalog.TagVersion(t) != null).ToList();
            if (versioned.Count == 0) return null;
            string exact = versioned.FirstOrDefault(t => GitHubCatalog.TagVersion(t) == wanted);
            if (exact != null) return exact;
            string major = VersionChoices.Major(wanted);
            string compatible = versioned.FirstOrDefault(t => VersionChoices.Major(GitHubCatalog.TagVersion(t)) == major && GitHubCatalog.CompareVersions(GitHubCatalog.TagVersion(t), wanted) > 0);

            return compatible ?? versioned[0];
        }

        /// <summary>
        /// The reader the installer uses: a package.json the catalogue already read (same repository,
        /// same reference), else GitHub's contents API with the user's token and ETag cache. Not a
        /// GitHub address: null (nothing known).
        /// </summary>
        public static Reader GitHubReader(GitHubApi api, IEnumerable<HubItem> known)
        {
            var items = (known ?? Enumerable.Empty<HubItem>()).ToList();

            return async (url, reference) =>
            {
                GitHubRepository repo = GitHubRepository.Of(url);
                reference ??= repo?.Reference;
                foreach (HubItem item in items.Where(i => i.Dependencies != null && HubCatalogReader.SameRepository(i.GitUrl, url)))
                {
                    string at = item.GitHub != null ? null : item.DependenciesReference;
                    bool defaultBranch = reference == null || item.GitHub != null && reference == item.GitHub.DefaultBranch;
                    if (at == reference || at == null && defaultBranch) return item.Dependencies;
                }
                if (repo == null) return null;
                GitHubApi.Answer answer = await api.Get(repo.PackageJsonPath(reference), GitHubApi.RawType);
                if (answer.Status == 404) return null;
                if (!answer.Ok) throw new GitHubException(answer.Status, repo.PackageJsonPath(reference));

                return Of(answer.Json);
            };
        }
    }
}
