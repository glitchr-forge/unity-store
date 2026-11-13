using System;
using System.Collections.Generic;
using System.Linq;

namespace Glitchr.Store
{
    /// <summary>One version a package can be moved to, and what Unity's Package Manager is given for it.</summary>
    public sealed class VersionChoice
    {
        public string Version;
        /// <summary>A Git package: the tag (the URL's #reference).</summary>
        public string Reference;
        public string Identifier;
        public bool Installed;
        /// <summary>Another major version than the installed one.</summary>
        public bool CrossesMajor;

        public string Label => (Reference != null && Reference != Version ? Reference : Version) + (Installed ? "  (installed)" : string.Empty);
    }

    /// <summary>
    /// The Update button's menu: every version known of a package, newest first, older ones
    /// included to go back - a GitHub package's tags, a registry's or a hub's versions, Unity's
    /// own for the project's other packages. Choosing one installs exactly that version.
    /// </summary>
    public static class VersionChoices
    {
        /// <summary>A source's package: its tags (GitHub), else its registry's or hub's versions.</summary>
        public static List<VersionChoice> For(HubItem item, string installedVersion = null, string installedReference = null)
        {
            var choices = new List<VersionChoice>();
            if (item == null || item.Paid && !item.Installable) return choices;
            if (item.GitHub != null)
            {
                var seen = new HashSet<string>();
                foreach (string tag in GitHubCatalog.SortTags(item.GitHub.Tags))
                {
                    string version = GitHubCatalog.TagVersion(tag);
                    if (version == null || !seen.Add(version)) continue;
                    choices.Add(new VersionChoice
                    {
                        Version = version,
                        Reference = tag,
                        Identifier = HubInstaller.GitIdentifier(item, tag),
                        Installed = installedReference != null ? installedReference == tag : installedVersion == version,
                    });
                }
            }
            else if (!string.IsNullOrEmpty(item.GitUrl))
            {
                // A Git URL names one version: the one its reference points at.
                if (!string.IsNullOrEmpty(item.Version)) choices.Add(new VersionChoice { Version = item.Version, Identifier = item.GitUrl, Installed = installedVersion == item.Version });
            }
            else
            {
                var versions = item.Versions.Select(v => v.Version).Concat(new[] { item.Version });

                return ForRegistry(item.Name, versions, installedVersion);
            }
            Mark(choices, installedVersion);

            return choices;
        }

        /// <summary>A registry package: name@version for each version, newest first.</summary>
        public static List<VersionChoice> ForRegistry(string name, IEnumerable<string> versions, string installedVersion)
        {
            var choices = (versions ?? Enumerable.Empty<string>())
                .Where(v => !string.IsNullOrEmpty(v) && !v.Contains("://") && !v.StartsWith("file:", StringComparison.Ordinal))
                .Distinct()
                .OrderByDescending(v => v, Comparer<string>.Create(GitHubCatalog.CompareVersions))
                .Select(v => new VersionChoice { Version = v, Identifier = name + "@" + v, Installed = v == installedVersion })
                .ToList();
            Mark(choices, installedVersion);

            return choices;
        }

        /// <summary>
        /// Whether choosing it asks first: a move across a major version of a package that does not
        /// come from the user's sources (Unity's registry, another registry the project has) - never silent.
        /// </summary>
        public static bool NeedsConfirmation(VersionChoice choice, bool fromUserSources) => choice != null && choice.CrossesMajor && !choice.Installed && !fromUserSources;

        public static PackageUpdate Update(string name, VersionChoice choice, HubItem item = null)
        {
            return new PackageUpdate
            {
                Name = name,
                Identifier = choice.Identifier,
                ToVersion = choice.Reference ?? choice.Version,
                Item = item != null && string.IsNullOrEmpty(item.GitUrl) ? item : null,
            };
        }

        private static void Mark(List<VersionChoice> choices, string installedVersion)
        {
            if (string.IsNullOrEmpty(installedVersion)) return;
            string major = Major(installedVersion);
            foreach (VersionChoice choice in choices) choice.CrossesMajor = Major(choice.Version) != major;
        }

        public static string Major(string version) => (version ?? string.Empty).Split('.', '-')[0];
    }
}
