using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;

namespace Glitchr.Store
{
    /// <summary>
    /// A place packages come from: a hub manifest (a forge's /upm/-/hub.json, a static hub's
    /// hub.json), a UPM registry with its scopes, a Git package, or a GitHub owner's repositories
    /// matching a pattern. Kept per user (EditorPrefs), not per project. Nothing is built in: a
    /// package installed in the project may preset its own hub (the "hub" section of its
    /// package.json), which the user can hide.
    /// </summary>
    [Serializable]
    public sealed class HubSource
    {
        public const string Manifest = "manifest";
        public const string Upm = "upm";
        public const string Git = "git";
        public const string GitHub = "github";
        /// <summary>A preset: the "hub" section of a package installed in the project (Url is its name).</summary>
        public const string Package = "package";
        /// <summary>The active profile of the user's profiles.json (Url is its id).</summary>
        public const string Profile = "profile";

        public string Type = Manifest;
        public string Name;
        public string Url;
        public List<string> Scopes = new List<string>();

        /// <summary>A GitHub source: the organisation or user, and the repositories' name pattern ("unity-*").</summary>
        public string Owner;
        public string Pattern;

        /// <summary>Development only: the SHA-256 of a self-signed certificate this source may present.</summary>
        public string CertificateSha256;

        public string Label
        {
            get
            {
                if (!string.IsNullOrEmpty(Name)) return Name;
                if (Type == GitHub) return "GitHub " + Owner + (string.IsNullOrEmpty(Pattern) ? string.Empty : "/" + Pattern);

                return Url;
            }
        }

        /// <summary>What tells two sources apart.</summary>
        public string Key => Type == GitHub ? "github:" + (Owner ?? string.Empty).ToLowerInvariant() + "/" + (Pattern ?? string.Empty).ToLowerInvariant() : Type + ":" + (Url ?? string.Empty).TrimEnd('/').ToLowerInvariant();
    }

    public static class HubSources
    {
        private const string Key = "Glitchr.Store.Sources";
        private const string HiddenKey = "Glitchr.Store.HiddenPresets";

        [Serializable]
        private sealed class Saved
        {
            public List<HubSource> Sources = new List<HubSource>();
        }

        /// <summary>The user's profiles file (injectable: tests use their own).</summary>
        public static Func<ProfileStore> Profiles = () => new ProfileStore();

        /// <summary>The active profile, then the user's own sources, then the presets of the project's packages that are not hidden.</summary>
        public static List<HubSource> All()
        {
            ForgeProfile active = null;
            try { active = Profiles().ActiveProfile; }
            catch (Exception) { /* a damaged profiles file: no profile, the window says so */ }

            return Compose(active, User(), HubPresets.Sources(), Hidden());
        }

        /// <summary>
        /// What the catalogue reads, in order: the active profile (none when null), the sources the
        /// user added by hand (kept whatever the profile), the project's presets that are not hidden.
        /// </summary>
        public static List<HubSource> Compose(ForgeProfile active, IEnumerable<HubSource> user, IEnumerable<HubSource> presets, ICollection<string> hidden)
        {
            var all = new List<HubSource>();
            if (active != null) all.Add(active.Source);
            var keys = new HashSet<string>(all.Select(s => s.Key));
            all.AddRange(user.Where(s => s.Type != HubSource.Profile && keys.Add(s.Key)));
            all.AddRange(presets.Where(p => !hidden.Contains(p.Url) && keys.Add(p.Key)));

            return all;
        }

        /// <summary>The sources the user added.</summary>
        public static List<HubSource> User()
        {
            string json = EditorPrefs.GetString(Key, string.Empty);
            var saved = string.IsNullOrEmpty(json) ? null : UnityEngine.JsonUtility.FromJson<Saved>(json);

            return saved?.Sources ?? new List<HubSource>();
        }

        public static void Save(List<HubSource> sources)
        {
            EditorPrefs.SetString(Key, UnityEngine.JsonUtility.ToJson(new Saved { Sources = sources.Where(s => s.Type != HubSource.Package && s.Type != HubSource.Profile).ToList() }));
        }

        public static void Add(HubSource source)
        {
            var sources = User();
            sources.RemoveAll(s => s.Key == source.Key);
            sources.Add(source);
            Save(sources);
        }

        public static void Remove(HubSource source)
        {
            if (source.Type == HubSource.Package)
            {
                SetHidden(source.Url, true);

                return;
            }
            var sources = User();
            sources.RemoveAll(s => s.Key == source.Key);
            Save(sources);
        }

        /// <summary>The packages whose preset the user hid.</summary>
        public static HashSet<string> Hidden()
        {
            return new HashSet<string>(EditorPrefs.GetString(HiddenKey, string.Empty).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries));
        }

        public static void SetHidden(string package, bool hidden)
        {
            var set = Hidden();
            if (hidden) set.Add(package); else set.Remove(package);
            EditorPrefs.SetString(HiddenKey, string.Join("\n", set));
        }

        /// <summary>Parses "a, b c" into scopes.</summary>
        public static List<string> ParseScopes(string text)
        {
            return (text ?? string.Empty).Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        }
    }

    /// <summary>
    /// Presets: a package installed in the project whose package.json has a "hub" section (a
    /// company's base package, as io.glitchr.studio is Glitch Art's) brings that hub - its sources
    /// and its theme - as a source, read from the disk. Data only: nothing of a package runs.
    /// </summary>
    public static class HubPresets
    {
        public static List<HubSource> Sources()
        {
            var sources = new List<HubSource>();
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                string path = Path.Combine(package.resolvedPath ?? string.Empty, "package.json");
                if (!File.Exists(path)) continue;
                object hub;
                try { hub = HubOf(File.ReadAllText(path)); }
                catch (Exception) { continue; }
                if (hub == null) continue;
                sources.Add(new HubSource { Type = HubSource.Package, Name = Json.String(Json.Get(hub, "name")) ?? package.displayName, Url = package.name });
            }

            return sources;
        }

        /// <summary>A package.json's "hub" section, or null when it has none.</summary>
        public static object HubOf(string packageJson)
        {
            object hub = Json.Get(Json.Parse(packageJson), "hub");

            return Json.Object(hub) != null ? hub : null;
        }

        /// <summary>The "hub" section of an installed package, read again from the disk.</summary>
        public static object Read(string package)
        {
            var info = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().FirstOrDefault(p => p.name == package);
            string path = info == null ? null : Path.Combine(info.resolvedPath, "package.json");
            if (path == null || !File.Exists(path)) throw new Exception(package + " is no longer in the project.");

            return HubOf(File.ReadAllText(path)) ?? throw new Exception(package + " has no \"hub\" section.");
        }
    }
}
