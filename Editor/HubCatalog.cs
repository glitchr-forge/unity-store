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
    }

    public static class HubCatalogReader
    {
        public static async Task<HubCatalogResult> Read(IEnumerable<HubSource> sources)
        {
            var result = new HubCatalogResult();
            foreach (HubSource source in sources)
            {
                try
                {
                    switch (source.Type)
                    {
                        case HubSource.Manifest: await ReadManifest(source, result); break;
                        case HubSource.Upm:
                            var origin = new HubOrigin { Source = source, HubName = source.Label, Registry = source.Url.TrimEnd('/'), RegistryName = source.Label, Scopes = source.Scopes };
                            await ReadRegistry(origin, null, result);
                            break;
                        case HubSource.Git:
                            object hub = await ReadGit(source.Url, source.Name, new HubOrigin { Source = source, HubName = source.Label }, result);
                            if (hub != null) await ReadHub(hub, source, result);
                            break;
                    }
                }
                catch (Exception e)
                {
                    result.Errors.Add(source.Label + ": " + e.Message);
                }
            }

            return result;
        }

        private static async Task ReadManifest(HubSource source, HubCatalogResult result)
        {
            HubHttp.Answer answer = await HubHttp.Get(PackageJsonUrl(source.Url) ?? source.Url, null, source.CertificateSha256);
            if (!answer.Ok) throw new Exception(answer.Error ?? ("HTTP " + answer.Status));
            await ReadHub(HubOf(answer.Json), source, result);
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
            if (repo.Split('/').Length != 2) return null;

            return "https://raw.githubusercontent.com/" + repo + "/" + (string.IsNullOrEmpty(reference) ? "HEAD" : reference) + "/"
                + (string.IsNullOrEmpty(folder) ? string.Empty : folder + "/") + "package.json";
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
            object hub = Json.Get(package, "hub");

            return Json.Object(hub) != null ? hub : null;
        }

        private static async Task ReadHub(object manifest, HubSource source, HubCatalogResult result)
        {
            string name = Json.String(Json.Get(manifest, "name")) ?? source.Label;
            result.Brand ??= name;
            result.Color ??= Json.String(Json.Get(manifest, "color"));
            string device = Json.String(Json.Get(manifest, "checkout", "device"));
            string token = Json.String(Json.Get(manifest, "checkout", "token"));
            string games = Json.String(Json.Get(manifest, "games"));
            var catalogs = new HashSet<string>();

            foreach (object entry in Json.Array(Json.Get(manifest, "sources")) ?? new List<object>())
            {
                string type = Json.String(Json.Get(entry, "type")) ?? HubSource.Upm;
                string url = Json.String(Json.Get(entry, "url"));
                if (string.IsNullOrEmpty(url)) continue;
                string label = Json.String(Json.Get(entry, "name")) ?? name;
                if (type == HubSource.Git)
                {
                    await ReadGit(url, label, new HubOrigin { Source = source, HubName = name }, result);
                    continue;
                }
                var origin = new HubOrigin
                {
                    Source = source, HubName = name, Color = Json.String(Json.Get(manifest, "color")),
                    Registry = url.TrimEnd('/'), RegistryName = label,
                    Scopes = (Json.Array(Json.Get(entry, "scopes")) ?? new List<object>()).OfType<string>().ToList(),
                    DeviceUrl = device, TokenUrl = token,
                };
                string catalog = Json.String(Json.Get(entry, "catalog"));
                if (!string.IsNullOrEmpty(catalog)) catalogs.Add(catalog);
                await ReadRegistry(origin, catalog, result, games == catalog);
            }
            if (!string.IsNullOrEmpty(games) && !catalogs.Contains(games))
            {
                HubHttp.Answer g = await HubHttp.Get(games, null, source.CertificateSha256);
                if (g.Ok) ReadGames(g.Json, result);
            }
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

        private static string GitName(string url)
        {
            string last = url.TrimEnd('/').Split('/').Last();
            int hash = last.IndexOf('#');
            if (hash >= 0) last = last.Substring(0, hash);

            return last.EndsWith(".git") ? last.Substring(0, last.Length - 4) : last;
        }
    }
}
