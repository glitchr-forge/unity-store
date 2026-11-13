using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Glitchr.Store
{
    /// <summary>
    /// A package of the open project, as Packages/manifest.json asks for it and packages-lock.json
    /// says Unity resolved it: its version, where it comes from (a registry, Git, a local or
    /// embedded folder, Unity's built-in modules), for a Git package its reference and commit.
    /// Read from the files only: no network call.
    /// </summary>
    public sealed class InstalledPackage
    {
        public const string Registry = "registry";
        public const string Git = "git";
        public const string Local = "local";
        public const string Embedded = "embedded";
        public const string Builtin = "builtin";

        public string Name;
        /// <summary>What the manifest asks for: a version, a Git URL, a file: path. Null for an indirect dependency.</summary>
        public string Requested;
        /// <summary>The package's version: the lock's for a registry package, the package.json's (when known) for the others.</summary>
        public string Version;
        /// <summary>The lock's "source": registry, git, local, embedded, builtin (null when the lock does not list it yet).</summary>
        public string Source;
        /// <summary>A registry package: the registry's URL. A Git package: its URL without the reference.</summary>
        public string Url;
        /// <summary>A Git package: the reference after "#" (a tag, a branch or a commit), null for the default branch.</summary>
        public string GitReference;
        /// <summary>A Git package: the commit Unity fetched.</summary>
        public string GitCommit;
        /// <summary>0 for a package the manifest names; more for a dependency of one.</summary>
        public int Depth;
        public List<string> Dependents = new List<string>();
        public string DisplayName;

        public bool Direct => Requested != null;
        public string Label => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;

        /// <summary>What kind of reference a Git package is pinned to: "commit", "tag" (a version), "branch", or the default branch.</summary>
        public string GitReferenceKind
        {
            get
            {
                if (Source != Git) return null;
                if (string.IsNullOrEmpty(GitReference)) return "default branch";
                if (Regex.IsMatch(GitReference, "^[0-9a-fA-F]{7,40}$") && GitReference.Any(char.IsDigit) && GitReference.Any(char.IsLetter)) return "commit";

                return GitHubCatalog.TagVersion(GitReference) != null ? "tag" : "branch";
            }
        }

        /// <summary>"tag v1.2.0 · commit 6939976" for a Git package; null for the others.</summary>
        public string GitDescription
        {
            get
            {
                if (Source != Git) return null;
                string kind = GitReferenceKind;
                string head = string.IsNullOrEmpty(GitReference) ? kind : kind + " " + GitReference;
                if (kind == "commit" || string.IsNullOrEmpty(GitCommit)) return head;

                return head + " · commit " + GitCommit.Substring(0, Math.Min(7, GitCommit.Length));
            }
        }
    }

    /// <summary>An update the Installed tab can make: what Unity's Package Manager is given, and the version it leads to.</summary>
    public sealed class PackageUpdate
    {
        public string Name;
        public string Identifier;
        public string ToVersion;
        /// <summary>A registry package of a source: its registry, written into the manifest when missing.</summary>
        public HubItem Item;
    }

    public static class InstalledPackages
    {
        /// <summary>
        /// The project's packages: every package the lock lists (the manifest's own, then their
        /// dependencies), and a manifest's dependency the lock does not have yet. Versions Unity
        /// knows better (a Git package's package.json) come from <paramref name="resolved"/>.
        /// </summary>
        public static List<InstalledPackage> Read(string manifestJson, string lockJson, IDictionary<string, string> resolved = null)
        {
            var requested = Json.Object(Json.Get(Json.Parse(manifestJson), "dependencies")) ?? new Dictionary<string, object>();
            var locked = string.IsNullOrEmpty(lockJson) ? new Dictionary<string, object>() : Json.Object(Json.Get(Json.Parse(lockJson), "dependencies")) ?? new Dictionary<string, object>();
            var packages = new Dictionary<string, InstalledPackage>();

            foreach (KeyValuePair<string, object> pair in locked)
            {
                string version = Json.String(Json.Get(pair.Value, "version"));
                var package = new InstalledPackage
                {
                    Name = pair.Key,
                    Source = Json.String(Json.Get(pair.Value, "source")),
                    Depth = Json.Get(pair.Value, "depth") is double d ? (int)d : 0,
                    GitCommit = Json.String(Json.Get(pair.Value, "hash")),
                    Requested = requested.TryGetValue(pair.Key, out object r) ? r as string : null,
                };
                Describe(package, version ?? package.Requested, Json.String(Json.Get(pair.Value, "url")));
                packages[pair.Key] = package;
            }
            foreach (KeyValuePair<string, object> pair in requested)
            {
                if (packages.ContainsKey(pair.Key) || !(pair.Value is string asked)) continue;
                var package = new InstalledPackage { Name = pair.Key, Requested = asked, Source = SourceOf(asked) };
                Describe(package, asked, null);
                packages[pair.Key] = package;
            }
            foreach (KeyValuePair<string, object> pair in locked)
            {
                foreach (string dependency in (Json.Object(Json.Get(pair.Value, "dependencies")) ?? new Dictionary<string, object>()).Keys)
                {
                    if (packages.TryGetValue(dependency, out InstalledPackage target)) target.Dependents.Add(pair.Key);
                }
            }
            if (resolved != null)
            {
                foreach (InstalledPackage package in packages.Values)
                {
                    if (resolved.TryGetValue(package.Name, out string version) && !string.IsNullOrEmpty(version) && !version.Contains("://") && !version.StartsWith("file:", StringComparison.Ordinal)) package.Version = version;
                }
            }

            return packages.Values.OrderBy(p => p.Direct ? 0 : 1).ThenBy(p => p.Name, StringComparer.Ordinal).ToList();
        }

        private static void Describe(InstalledPackage package, string version, string url)
        {
            package.Source ??= SourceOf(version);
            if (package.Source == InstalledPackage.Git)
            {
                string text = version ?? string.Empty;
                int hash = text.IndexOf('#');
                package.Url = hash >= 0 ? text.Substring(0, hash) : text;
                package.GitReference = hash >= 0 && hash < text.Length - 1 ? text.Substring(hash + 1) : null;
            }
            else if (package.Source == InstalledPackage.Registry || package.Source == InstalledPackage.Builtin)
            {
                package.Version = version;
                package.Url = url?.TrimEnd('/');
            }
            else
            {
                package.Url = version;
            }
        }

        /// <summary>What a manifest's value is, before the lock says: a Git URL, a folder, a registry version.</summary>
        public static string SourceOf(string requested)
        {
            if (string.IsNullOrEmpty(requested)) return null;
            if (requested.StartsWith("file:", StringComparison.Ordinal)) return InstalledPackage.Local;
            if (requested.StartsWith("git", StringComparison.OrdinalIgnoreCase) || requested.StartsWith("ssh:", StringComparison.OrdinalIgnoreCase)
                || requested.Contains(".git") || requested.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || requested.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                return InstalledPackage.Git;
            }

            return InstalledPackage.Registry;
        }

        /// <summary>
        /// The source's item that offers this installed package: the same name, from the same kind
        /// of place - a Git item for a Git package (of the same repository when both are known), a
        /// registry's item for a registry package (of the same registry when the lock names it).
        /// </summary>
        public static HubItem Offer(InstalledPackage package, IEnumerable<HubItem> items)
        {
            HubItem fallback = null;
            foreach (HubItem item in items.Where(i => i.Name == package.Name))
            {
                if (package.Source == InstalledPackage.Git && !string.IsNullOrEmpty(item.GitUrl))
                {
                    if (HubCatalogReader.SameRepository(item.GitUrl, package.Url)) return item;
                }
                else if (package.Source == InstalledPackage.Registry && string.IsNullOrEmpty(item.GitUrl) && item.Origin?.Registry != null)
                {
                    if (string.IsNullOrEmpty(package.Url) || string.Equals(item.Origin.Registry.TrimEnd('/'), package.Url, StringComparison.OrdinalIgnoreCase)) return item;
                }
                else if (package.Source == null || package.Source == InstalledPackage.Git && string.IsNullOrEmpty(package.Url))
                {
                    fallback ??= item;
                }
            }

            return fallback;
        }

        /// <summary>
        /// The update a source offers for an installed package, or null: only for a package the
        /// manifest names, never for a folder or a built-in module. A GitHub package moves to its
        /// latest tag, a Git package to its source's URL when that one is newer, a registry package
        /// to the source's latest version.
        /// </summary>
        public static PackageUpdate UpdateFrom(InstalledPackage package, HubItem item)
        {
            if (item == null || !package.Direct) return null;
            if (package.Source == InstalledPackage.Git)
            {
                if (item.GitHub != null)
                {
                    string tag = item.GitHub.LatestTag;
                    if (tag == null || tag == package.GitReference || !GitHubCatalog.Updatable(package.Version, item.GitHub.LatestVersion)) return null;

                    return new PackageUpdate { Name = package.Name, Identifier = HubInstaller.GitIdentifier(item, tag), ToVersion = tag };
                }
                if (string.IsNullOrEmpty(item.GitUrl) || !GitHubCatalog.Updatable(package.Version, item.Version)) return null;
                string reference = item.GitUrl.Contains("#") ? item.GitUrl.Substring(item.GitUrl.IndexOf('#') + 1) : null;
                if (reference != null && reference == package.GitReference) return null;

                return new PackageUpdate { Name = package.Name, Identifier = item.GitUrl, ToVersion = reference ?? item.Version };
            }
            if (package.Source != InstalledPackage.Registry || !string.IsNullOrEmpty(item.GitUrl)) return null;
            if (item.Paid && !item.Installable) return null;
            if (!GitHubCatalog.Updatable(package.Version, item.Version)) return null;

            return new PackageUpdate { Name = package.Name, Identifier = package.Name + "@" + item.Version, ToVersion = item.Version, Item = item };
        }

        /// <summary>
        /// A registry package's update to the version Unity knows (from its own cache), or null.
        /// Never across a major version: that one is chosen in Unity's own window, with its notes.
        /// </summary>
        public static PackageUpdate UpdateTo(InstalledPackage package, string latestCompatible)
        {
            if (!package.Direct || package.Source != InstalledPackage.Registry || !GitHubCatalog.Updatable(package.Version, latestCompatible)) return null;
            if (Major(package.Version) != Major(latestCompatible)) return null;

            return new PackageUpdate { Name = package.Name, Identifier = package.Name + "@" + latestCompatible, ToVersion = latestCompatible };
        }

        /// <summary>
        /// What the Installed tab says of a package Forge Store installed that no source offers
        /// now: a profile's that is not active ("from profile Name"), the active profile's or a
        /// source's that no longer lists it, a source the user removed. The flag is a warning only
        /// when something is wrong; another profile's package is information.
        /// </summary>
        public static (string Text, bool Warning) SourceNote(InstallRecord.Entry entry, ICollection<string> sourceKeys, IEnumerable<ForgeProfile> profiles, string activeProfile)
        {
            if (entry == null) return (null, false);
            var all = (profiles ?? Enumerable.Empty<ForgeProfile>()).ToList();
            string prefix = HubSource.Profile + ":";
            if (entry.Source != null && entry.Source.StartsWith(prefix, StringComparison.Ordinal))
            {
                string id = entry.Source.Substring(prefix.Length);
                ForgeProfile profile = all.FirstOrDefault(p => p.Id == id);
                if (id == activeProfile) return ("Its profile, " + (profile?.Name ?? entry.Label) + ", no longer lists it (or did not answer this time).", true);
                if (profile != null) return ("From profile " + profile.Name + ", not the active one.", false);

                return ("From another profile" + (string.IsNullOrEmpty(entry.Label) ? string.Empty : " (" + entry.Label + ", since removed)") + ".", false);
            }
            if (sourceKeys != null && sourceKeys.Contains(entry.Source)) return ("Its source, " + entry.Label + ", no longer lists it (or did not answer this time).", true);
            // A source of 1.3.0 that a profile now carries: that profile's, whether active or not.
            ForgeProfile carrier = all.FirstOrDefault(p => p.Id != activeProfile && Carries(p, entry.Source));
            if (carrier != null) return ("From profile " + carrier.Name + ", not the active one.", false);

            return ("Its source, " + entry.Label + ", is no longer among your sources.", true);
        }

        /// <summary>Whether a profile is, or lists, the source of that key (its own URL as a manifest, or one of its hub's sources).</summary>
        public static bool Carries(ForgeProfile profile, string sourceKey)
        {
            if (profile == null || string.IsNullOrEmpty(sourceKey)) return false;
            if (profile.Url != null && new HubSource { Type = HubSource.Manifest, Url = profile.Url }.Key == sourceKey) return true;
            foreach (object entry in Json.Array(Json.Get(profile.Hub, "sources")) ?? new List<object>())
            {
                string type = Json.String(Json.Get(entry, "type")) ?? HubSource.Upm;
                var source = new HubSource { Type = type, Url = Json.String(Json.Get(entry, "url")), Owner = Json.String(Json.Get(entry, "owner")), Pattern = Json.String(Json.Get(entry, "pattern")) };
                if (source.Key == sourceKey) return true;
            }

            return false;
        }

        private static string Major(string version) => (version ?? string.Empty).Split('.', '-')[0];
    }

    /// <summary>
    /// Which packages of a project Forge Store installed, and from which source: kept in the
    /// project's UserSettings folder (Unity's per-user project settings, left out of version
    /// control by Unity's own ignore file), so a package whose source is gone is still known as one
    /// of Forge Store's - flagged, not hidden.
    /// </summary>
    public sealed class InstallRecord
    {
        public sealed class Entry
        {
            public string Source;
            public string Label;
        }

        private readonly string path;
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>();

        public InstallRecord(string path)
        {
            this.path = path;
            if (path == null || !System.IO.File.Exists(path)) return;
            try
            {
                foreach (KeyValuePair<string, object> pair in Json.Object(Json.Parse(System.IO.File.ReadAllText(path))) ?? new Dictionary<string, object>())
                {
                    entries[pair.Key] = new Entry { Source = Json.String(Json.Get(pair.Value, "source")), Label = Json.String(Json.Get(pair.Value, "label")) };
                }
            }
            catch (Exception)
            {
                entries.Clear(); // a damaged record is an empty one
            }
        }

        /// <summary>The open project's record: UserSettings/ForgeStore/installed.json.</summary>
        public static string ProjectPath => System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "UserSettings", "ForgeStore", "installed.json");

        public Entry Get(string package) => entries.TryGetValue(package, out Entry entry) ? entry : null;

        public IEnumerable<string> Packages => entries.Keys;

        public void Set(string package, HubSource source)
        {
            if (source == null) return;
            entries[package] = new Entry { Source = source.Key, Label = source.Label };
        }

        public bool Remove(string package) => entries.Remove(package);

        public void Save()
        {
            if (path == null) return;
            var root = new Dictionary<string, object>();
            foreach (KeyValuePair<string, Entry> pair in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                root[pair.Key] = new Dictionary<string, object> { ["source"] = pair.Value.Source, ["label"] = pair.Value.Label };
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, Json.Write(root, true) + "\n");
        }
    }
}
