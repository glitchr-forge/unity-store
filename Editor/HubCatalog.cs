using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;

namespace Glitchr.Store
{
    /// <summary>A package a source offers, and how to install it.</summary>
    public sealed class HubItem
    {
        public string Name;
        public string DisplayName;
        public string Description;
        public string Version;
        public List<HubVersion> Versions = new List<HubVersion>();
        public bool Paid;
        /// <summary>The registry serves it to this user: free, or covered by their key.</summary>
        public bool Installable = true;
        public string Page;
        public string Price;
        public string Image;

        public HubOrigin Origin;
        /// <summary>A Git package: its URL, in place of a registry.</summary>
        public string GitUrl;
        /// <summary>A package of a GitHub source: its repository, visibility and tags.</summary>
        public GitHubPackage GitHub;
        /// <summary>A Git package: its package.json's dependencies, when already read (at <see cref="DependenciesReference"/>; null for the default branch).</summary>
        public Dictionary<string, string> Dependencies;
        public string DependenciesReference;

        public string Label => string.IsNullOrEmpty(DisplayName) ? Name : DisplayName;
    }

    public sealed class HubVersion
    {
        public string Version;
        public string Date;
        public string Changelog;
    }

    /// <summary>A game of a hub: played in the browser when free (or previewed), else downloaded once bought.</summary>
    public sealed class HubGame
    {
        public string Name;
        public string Tagline;
        public string Play;
        public bool Preview;
        public bool Paid;
        public string Page;
        public string Price;
        public List<string> Platforms = new List<string>();
    }

    /// <summary>Where an item comes from: its registry, the brand of its hub, and whether it sells.</summary>
    public sealed class HubOrigin
    {
        public HubSource Source;
        public string HubName;
        public string Color;
        public string Registry;
        public string RegistryName;
        public List<string> Scopes = new List<string>();
        public string DeviceUrl;
        public string TokenUrl;
        public HubTheme Theme;
        /// <summary>The store whose key signs this item's licences (see <see cref="KeyPins"/>).</summary>
        public string Store;
        public bool Sells => !string.IsNullOrEmpty(DeviceUrl) && !string.IsNullOrEmpty(TokenUrl);
    }

    /// <summary>What every source offers, read the way it is published: a forge's catalogue, or a static registry's search and documents.</summary>
    public sealed class HubCatalogResult
    {
        public List<HubItem> Items = new List<HubItem>();
        public List<HubGame> Games = new List<HubGame>();
        public List<string> Errors = new List<string>();
        public string Brand;
        public string Color;
        /// <summary>The window's header: the active profile's theme, else the first a preset's hub declared, else the first any hub declared.</summary>
        public HubTheme Theme;
        public bool ThemeFromPreset;
        public bool ThemeFromProfile;
        /// <summary>The active profile's name, shown in the header even when its hub has no theme.</summary>
        public string Profile;
        /// <summary>The licensing keys the hubs announced, each checked against its pin.</summary>
        public List<HubLicensing> Licensing = new List<HubLicensing>();
    }

    /// <summary>
    /// A hub's look, data only: its name, logo, banner and accent colours (Unity's light and dark
    /// skins), from its "theme" block - or its older "color" and "logo" keys. No theme: the plain
    /// Forge Store look.
    /// </summary>
    public sealed class HubTheme
    {
        public string Name;
        public string Logo;
        public string Banner;
        public string Accent;
        public string AccentDark;

        public static HubTheme Of(object hub)
        {
            object theme = Json.Object(Json.Get(hub, "theme"));
            var read = new HubTheme
            {
                Name = Json.String(Json.Get(hub, "name")),
                Logo = Json.String(Json.Get(theme, "logo")) ?? Json.String(Json.Get(hub, "logo")),
                Banner = Json.String(Json.Get(theme, "banner")),
                Accent = Colour(Json.String(Json.Get(theme, "accent")) ?? Json.String(Json.Get(hub, "color"))),
            };
            read.AccentDark = Colour(Json.String(Json.Get(theme, "accentDark"))) ?? read.Accent;

            return read.Accent == null && read.Logo == null && read.Banner == null ? null : read;
        }

        /// <summary>A "#rgb" or "#rrggbb" colour, else null: nothing else is read from a registry.</summary>
        public static string Colour(string text)
        {
            return text != null && System.Text.RegularExpressions.Regex.IsMatch(text, "^#(?:[0-9A-Fa-f]{3}|[0-9A-Fa-f]{6})$") ? text : null;
        }
    }

    public static class HubCatalogReader
    {
        /// <summary>The GitHub client of a reading: its cache in the user's folder, the user's token.</summary>
        public static Func<GitHubApi> GitHubClient = () => new GitHubApi(UnityWebTransport.Instance, new GitHubCache(GitHubCache.DefaultPath), GitHubToken.Current);

        /// <summary>The user's pinned keys, which a reading checks the hubs' announced keys against.</summary>
        public static Func<KeyPins> Pins = () => StoreTrust.Pins;

        /// <summary>
        /// What names a store for its pinned key: its manifest's URL, a preset's package, a Git
        /// package's repository (whatever its tag, so a new release keeps the pin).
        /// </summary>
        public static string StoreId(string type, string url)
        {
            string text = (url ?? string.Empty).Trim();
            if (type == HubSource.Git) text = text.Split('#')[0].Split('?')[0];
            text = text.TrimEnd('/');
            if (type == HubSource.Git && text.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) text = text.Substring(0, text.Length - 4);

            return type + ":" + text.ToLowerInvariant();
        }

        public static async Task<HubCatalogResult> Read(IEnumerable<HubSource> sources)
        {
            var result = new HubCatalogResult();
            GitHubApi github = null;
            GitHubApi GitHub() => github ??= GitHubClient();
            foreach (HubSource source in sources)
            {
                try
                {
                    switch (source.Type)
                    {
                        case HubSource.Manifest: await ReadManifest(source, result, GitHub); break;
                        case HubSource.Package: await ReadHub(HubPresets.Read(source.Url), source, result, GitHub, StoreId(HubSource.Package, source.Url)); break;
                        case HubSource.Profile:
                            ForgeProfile profile = HubSources.Profiles().Get(source.Url) ?? throw new Exception("this profile is no longer in your profiles.");
                            result.Profile ??= profile.Name;
                            await ReadHub(profile.Hub, source, result, GitHub, profile.StoreId);
                            break;
                        case HubSource.GitHub:
                            await ReadGitHub(GitHub(), source.Owner, source.Pattern, new HubOrigin { Source = source, HubName = source.Label }, result);
                            break;
                        case HubSource.Upm:
                            var origin = new HubOrigin { Source = source, HubName = source.Label, Registry = source.Url.TrimEnd('/'), RegistryName = source.Label, Scopes = source.Scopes };
                            await ReadRegistry(origin, null, result);
                            break;
                        case HubSource.Git:
                            object hub = await ReadGit(source.Url, source.Name, new HubOrigin { Source = source, HubName = source.Label }, result);
                            if (hub != null) await ReadHub(hub, source, result, GitHub, StoreId(HubSource.Git, source.Url));
                            break;
                    }
                }
                catch (Exception e)
                {
                    result.Errors.Add(source.Label + ": " + e.Message);
                }
            }
            if (github != null)
            {
                try { github.SaveCache(); }
                catch (Exception e) { result.Errors.Add("GitHub cache: " + e.Message); }
            }
            if (result.Licensing.Count > 0)
            {
                try
                {
                    KeyPins pins = Pins();
                    foreach (HubLicensing licensing in result.Licensing) licensing.Check = pins.Observe(licensing);
                    pins.Save();
                }
                catch (Exception e)
                {
                    result.Errors.Add("Pinned keys: " + e.Message);
                }
            }

            return result;
        }

        private static async Task ReadManifest(HubSource source, HubCatalogResult result, Func<GitHubApi> github, string url = null, int depth = 0)
        {
            url ??= source.Url;
            HubHttp.Answer answer = await HubHttp.Get(PackageJsonUrl(url) ?? url, null, source.CertificateSha256);
            if (!answer.Ok) throw new Exception(answer.Error ?? ("HTTP " + answer.Status));
            await ReadHub(HubOf(answer.Json), source, result, github, StoreId(HubSource.Manifest, url), depth);
        }

        /// <summary>A GitHub owner's repositories matching the pattern that are UPM packages, each installable from Git.</summary>
        private static async Task ReadGitHub(GitHubApi api, string owner, string pattern, HubOrigin origin, HubCatalogResult result)
        {
            foreach (GitHubPackage package in await GitHubCatalog.Packages(api, owner, pattern, result.Errors))
            {
                // Already listed by a Git source of the same repository: that entry gains the tags.
                HubItem known = result.Items.FirstOrDefault(i => i.Name == package.Name && i.GitHub == null && SameRepository(i.GitUrl, package.GitUrl));
                if (known != null)
                {
                    known.GitHub = package;
                    known.Page ??= package.Page;
                    continue;
                }
                result.Items.Add(new HubItem
                {
                    Name = package.Name,
                    DisplayName = package.DisplayName ?? package.Repository,
                    Description = package.Description,
                    Version = package.LatestVersion,
                    Page = package.Page,
                    GitUrl = package.GitUrl,
                    GitHub = package,
                    Dependencies = package.Dependencies,
                    Origin = origin,
                    Versions = package.Tags.Select(t => new HubVersion { Version = t }).ToList(),
                });
            }
        }

        /// <summary>
        /// A hub's own document: a hub.json, or the "hub" section of a package's package.json (a
        /// package that lists the packages it goes with, as io.glitchr.studio does). Unity ignores
        /// the section; the window reads it.
        /// </summary>
        public static object HubOf(object json)
        {
            object hub = Json.Get(json, "hub");

            return Json.Object(hub) != null ? hub : json;
        }

        /// <summary>
        /// Where a Git package's package.json is served, for a GitHub repository: at the URL's
        /// #reference (the default branch without one) and in its ?path= folder. Null for any other
        /// address.
        /// </summary>
        public static string PackageJsonUrl(string gitUrl)
        {
            GitHubRepository repo = GitHubRepository.Of(gitUrl);
            if (repo == null) return null;

            return "https://raw.githubusercontent.com/" + repo.Owner + "/" + repo.Name + "/" + (string.IsNullOrEmpty(repo.Reference) ? "HEAD" : repo.Reference) + "/"
                + (string.IsNullOrEmpty(repo.Folder) ? string.Empty : repo.Folder + "/") + "package.json";
        }

        /// <summary>
        /// A Git package, described by its package.json when GitHub serves it: its package name (so
        /// the window knows it installed), version and description. Returns the package's "hub"
        /// section, if it has one.
        /// </summary>
        private static async Task<object> ReadGit(string url, string label, HubOrigin origin, HubCatalogResult result)
        {
            var item = new HubItem { Name = GitName(url), DisplayName = label, Description = url, GitUrl = url, Origin = origin };
            result.Items.Add(item);
            string raw = PackageJsonUrl(url);
            if (raw == null) return null;
            HubHttp.Answer answer = await HubHttp.Get(raw);
            object package;
            try { package = answer.Ok ? answer.Json : null; }
            catch (FormatException) { package = null; }
            if (Json.Object(package) == null) return null; // private, or not a package: as it was given

            item.Name = Json.String(Json.Get(package, "name")) ?? item.Name;
            item.DisplayName = string.IsNullOrEmpty(label) ? Json.String(Json.Get(package, "displayName")) ?? url : label;
            item.Description = Json.String(Json.Get(package, "description")) ?? url;
            item.Version = Json.String(Json.Get(package, "version"));
            item.Dependencies = GitDependencies.Of(package);
            item.DependenciesReference = GitHubRepository.Of(url)?.Reference;
            object hub = Json.Get(package, "hub");

            return Json.Object(hub) != null ? hub : null;
        }

        private static async Task ReadHub(object manifest, HubSource source, HubCatalogResult result, Func<GitHubApi> github, string store, int depth = 0)
        {
            string name = Json.String(Json.Get(manifest, "name")) ?? source.Label;
            HubTheme theme = HubTheme.Of(manifest);
            result.Brand ??= name;
            result.Color ??= Json.String(Json.Get(manifest, "color"));
            bool preset = source.Type == HubSource.Package, profile = source.Type == HubSource.Profile;
            if (theme != null && !result.ThemeFromProfile && (result.Theme == null || profile || preset && !result.ThemeFromPreset))
            {
                result.Theme = theme;
                result.ThemeFromPreset = preset;
                result.ThemeFromProfile = profile;
            }
            HubLicensing licensing = HubLicensing.Of(manifest, store, name);
            if (licensing != null && result.Licensing.All(l => l.Store != store)) result.Licensing.Add(licensing);
            string device = Json.String(Json.Get(manifest, "checkout", "device"));
            string token = Json.String(Json.Get(manifest, "checkout", "token"));
            string games = Json.String(Json.Get(manifest, "games"));
            var catalogs = new HashSet<string>();

            foreach (object entry in Json.Array(Json.Get(manifest, "sources")) ?? new List<object>())
            {
                // Each entry on its own: a hub that does not answer, or a private owner without a
                // token, does not hide the hub's other sources.
                string label = Json.String(Json.Get(entry, "name")) ?? name;
                try
                {
                    await ReadEntry(entry, manifest, name, label, theme, device, token, games, catalogs, source, result, github, store, depth);
                }
                catch (Exception e)
                {
                    result.Errors.Add(label + ": " + e.Message);
                }
            }
            if (!string.IsNullOrEmpty(games) && !catalogs.Contains(games))
            {
                HubHttp.Answer g = await HubHttp.Get(games, null, source.CertificateSha256);
                if (g.Ok) ReadGames(g.Json, result);
            }
        }

        private static async Task ReadEntry(object entry, object manifest, string name, string label, HubTheme theme, string device, string token, string games, HashSet<string> catalogs, HubSource source, HubCatalogResult result, Func<GitHubApi> github, string store, int depth)
        {
            string type = Json.String(Json.Get(entry, "type")) ?? HubSource.Upm;
            string url = Json.String(Json.Get(entry, "url"));
            if (type == HubSource.GitHub)
            {
                await ReadGitHub(github(), Json.String(Json.Get(entry, "owner")), Json.String(Json.Get(entry, "pattern")), new HubOrigin { Source = source, HubName = name, Theme = theme }, result);

                return;
            }
            if (string.IsNullOrEmpty(url)) return;
            if (type == HubSource.Manifest)
            {
                if (depth < 2) await ReadManifest(source, result, github, url, depth + 1); // a hub naming a hub, not forever

                return;
            }
            if (type == HubSource.Git)
            {
                await ReadGit(url, label, new HubOrigin { Source = source, HubName = name, Theme = theme }, result);

                return;
            }
            var origin = new HubOrigin
            {
                Source = source, HubName = name, Color = Json.String(Json.Get(manifest, "color")),
                Registry = url.TrimEnd('/'), RegistryName = label,
                Scopes = (Json.Array(Json.Get(entry, "scopes")) ?? new List<object>()).OfType<string>().ToList(),
                DeviceUrl = device, TokenUrl = token, Theme = theme, Store = store,
            };
            string catalog = Json.String(Json.Get(entry, "catalog"));
            if (!string.IsNullOrEmpty(catalog)) catalogs.Add(catalog);
            await ReadRegistry(origin, catalog, result, games == catalog);
        }

        /// <summary>A registry's packages: from its forge catalogue when it has one, else from its search and documents.</summary>
        private static async Task ReadRegistry(HubOrigin origin, string catalog, HubCatalogResult result, bool withGames = false)
        {
            string key = UpmConfig.Token(origin.Registry);
            string certificate = origin.Source.CertificateSha256;
            if (!string.IsNullOrEmpty(catalog))
            {
                HubHttp.Answer answer = await HubHttp.Get(catalog, key, certificate);
                if (answer.Status == 401 && !string.IsNullOrEmpty(key)) answer = await HubHttp.Get(catalog, null, certificate);
                if (!answer.Ok) throw new Exception(answer.Error ?? ("HTTP " + answer.Status));
                foreach (object p in Json.Array(Json.Get(answer.Json, "packages")) ?? new List<object>())
                {
                    var item = new HubItem
                    {
                        Name = Json.String(Json.Get(p, "name")),
                        DisplayName = Json.String(Json.Get(p, "displayName")),
                        Description = Json.String(Json.Get(p, "description")),
                        Version = Json.String(Json.Get(p, "version")),
                        Paid = Json.String(Json.Get(p, "pricing")) == "licensed",
                        Installable = Json.Get(p, "installable") is bool b ? b : true,
                        Page = Json.String(Json.Get(p, "page")),
                        Image = Json.String(Json.Get(p, "image")),
                        Price = Price(Json.Array(Json.Get(p, "offers"))),
                        Origin = origin,
                    };
                    foreach (object v in Json.Array(Json.Get(p, "versions")) ?? new List<object>())
                    {
                        item.Versions.Add(new HubVersion { Version = Json.String(Json.Get(v, "version")), Date = Json.String(Json.Get(v, "date")), Changelog = Json.String(Json.Get(v, "changelog")) });
                    }
                    result.Items.Add(item);
                }
                if (withGames) ReadGames(answer.Json, result);

                return;
            }

            // A plain registry (a static hub, OpenUPM...): its search, then each package's document.
            var names = new HashSet<string>();
            foreach (string scope in origin.Scopes.DefaultIfEmpty(string.Empty))
            {
                HubHttp.Answer search = await HubHttp.Get(origin.Registry + "/-/v1/search?text=" + Uri.EscapeDataString(scope) + "&from=0&size=250", key, certificate);
                if (!search.Ok) throw new Exception("search: " + (search.Error ?? ("HTTP " + search.Status)));
                foreach (object o in Json.Array(Json.Get(search.Json, "objects")) ?? new List<object>())
                {
                    string name = Json.String(Json.Get(o, "package", "name"));
                    if (!string.IsNullOrEmpty(name) && (scope.Length == 0 || name.StartsWith(scope, StringComparison.Ordinal))) names.Add(name);
                }
            }
            foreach (string name in names.OrderBy(n => n))
            {
                HubHttp.Answer document = await HubHttp.Get(origin.Registry + "/" + name, key, certificate);
                if (!document.Ok) continue;
                object doc = document.Json;
                string latest = Json.String(Json.Get(doc, "dist-tags", "latest"));
                object head = Json.Get(doc, "versions", latest ?? string.Empty);
                var item = new HubItem
                {
                    Name = name,
                    DisplayName = Json.String(Json.Get(head, "displayName")),
                    Description = Json.String(Json.Get(head, "description")) ?? Json.String(Json.Get(doc, "description")),
                    Version = latest,
                    Origin = origin,
                };
                var versions = Json.Object(Json.Get(doc, "versions")) ?? new Dictionary<string, object>();
                foreach (string v in versions.Keys.Reverse())
                {
                    item.Versions.Add(new HubVersion { Version = v, Date = Json.String(Json.Get(doc, "time", v)) });
                }
                result.Items.Add(item);
            }
        }

        private static void ReadGames(object catalog, HubCatalogResult result)
        {
            foreach (object g in Json.Array(Json.Get(catalog, "games")) ?? new List<object>())
            {
                result.Games.Add(new HubGame
                {
                    Name = Json.String(Json.Get(g, "name")),
                    Tagline = Json.String(Json.Get(g, "tagline")),
                    Play = Json.String(Json.Get(g, "play")),
                    Preview = Json.Get(g, "preview") is bool p && p,
                    Paid = Json.String(Json.Get(g, "pricing")) == "licensed",
                    Page = Json.String(Json.Get(g, "page")),
                    Price = Price(Json.Array(Json.Get(g, "offers"))),
                    Platforms = (Json.Array(Json.Get(g, "platforms")) ?? new List<object>()).OfType<string>().ToList(),
                });
            }
        }

        /// <summary>The lowest offer, as "12.00 EUR".</summary>
        private static string Price(List<object> offers)
        {
            if (offers == null || offers.Count == 0) return null;
            object cheapest = offers.OrderBy(o => Json.Get(o, "price") is double d ? d : double.MaxValue).First();
            double cents = Json.Get(cheapest, "price") is double c ? c : 0;

            return (cents / 100).ToString("0.00", CultureInfo.InvariantCulture) + " " + (Json.String(Json.Get(cheapest, "currency")) ?? string.Empty);
        }

        /// <summary>Two Git URLs of the same repository, whatever their reference or folder.</summary>
        public static bool SameRepository(string a, string b)
        {
            string Base(string url)
            {
                string u = (url ?? string.Empty).Split('#')[0].Split('?')[0].TrimEnd('/');
                return (u.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? u.Substring(0, u.Length - 4) : u).ToLowerInvariant();
            }

            return !string.IsNullOrEmpty(a) && Base(a) == Base(b);
        }

        private static string GitName(string url)
        {
            string last = url.TrimEnd('/').Split('/').Last();
            int hash = last.IndexOf('#');
            if (hash >= 0) last = last.Substring(0, hash);

            return last.EndsWith(".git") ? last.Substring(0, last.Length - 4) : last;
        }
    }
}
