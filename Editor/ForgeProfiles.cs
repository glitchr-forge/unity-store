using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

namespace Glitchr.Store
{
    /// <summary>
    /// A profile: one hub document (a preset's "hub" section, a static hub's hub.json: name, theme,
    /// sources, licensing), shared as a URL or a file, kept in the user's profiles.json beside the
    /// others. Forge Hub (the desktop app) reads and writes the same file.
    /// </summary>
    public sealed class ForgeProfile
    {
        public const string FromUrl = "url";
        public const string FromFile = "file";

        public string Id;
        public string Name;
        public string Origin;
        public string Url;
        public string AddedAt;
        public string RefreshedAt;
        /// <summary>The hub document as last read.</summary>
        public object Hub;

        public HubTheme Theme => HubTheme.Of(Hub);

        /// <summary>The store whose licensing key this profile's hub announces: a URL profile is the same store as a manifest source of that URL.</summary>
        public string StoreId => Origin == FromUrl && !string.IsNullOrEmpty(Url) ? HubCatalogReader.StoreId(HubSource.Manifest, Url) : HubSource.Profile + ":" + Id;

        /// <summary>The source the catalogue reads for this profile.</summary>
        public HubSource Source => new HubSource { Type = HubSource.Profile, Name = Name, Url = Id };
    }

    /// <summary>
    /// The per-user profiles file, <c>&lt;local app data&gt;/Forge/profiles.json</c>, shared with
    /// Forge Hub: read as a JSON tree, only what Forge Store owns changed, unknown fields kept; read
    /// again just before each write, written to a temporary file of the same folder then renamed
    /// over it. No secret is ever written there: tokens and keys stay in the system's secret store.
    /// </summary>
    public sealed class ProfileStore
    {
        /// <summary>Tests (and anyone) point both apps at another file with this variable.</summary>
        public const string EnvironmentVariable = "FORGE_PROFILES_FILE";
        public const int MaxBytes = 1024 * 1024;
        public const string ExportSuffix = ".forge-profile.json";

        public string Path { get; }

        public ProfileStore(string path = null)
        {
            Path = path ?? DefaultPath;
        }

        /// <summary>
        /// The file both apps share: FORGE_PROFILES_FILE when set, else Forge/profiles.json in the
        /// folder Rust's dirs::data_local_dir() names - ~/Library/Application Support on macOS (Mono's
        /// LocalApplicationData is ~/.local/share there, so it is not used on macOS), %LOCALAPPDATA%
        /// on Windows, $XDG_DATA_HOME or ~/.local/share on Linux.
        /// </summary>
        public static string DefaultPath
        {
            get
            {
                string set = Environment.GetEnvironmentVariable(EnvironmentVariable);
                if (!string.IsNullOrWhiteSpace(set)) return set;
                string os = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX) ? "macos"
                    : System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "windows" : "linux";

                return System.IO.Path.Combine(DataFolder(os, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetEnvironmentVariable("XDG_DATA_HOME")), "Forge", "profiles.json");
            }
        }

        /// <summary>The user's local data folder, as dirs::data_local_dir() names it.</summary>
        public static string DataFolder(string os, string home, string localAppData, string xdgDataHome)
        {
            switch (os)
            {
                case "macos": return System.IO.Path.Combine(home, "Library", "Application Support");
                case "windows": return localAppData;
                default: return !string.IsNullOrEmpty(xdgDataHome) && System.IO.Path.IsPathRooted(xdgDataHome) ? xdgDataHome : System.IO.Path.Combine(home, ".local", "share");
            }
        }

        /// <summary>The id of the active profile, or null: no profile, only the project's presets.</summary>
        public string Active
        {
            get
            {
                string id = Json.String(Json.Get(Read(), "active"));

                return id != null && Profiles().Any(p => p.Id == id) ? id : null;
            }
        }

        public ForgeProfile ActiveProfile
        {
            get
            {
                var root = Read();
                string id = Json.String(Json.Get(root, "active"));

                return id == null ? null : List(root).FirstOrDefault(p => p.Id == id);
            }
        }

        public List<ForgeProfile> Profiles() => List(Read());

        public ForgeProfile Get(string id) => Profiles().FirstOrDefault(p => p.Id == id);

        /// <summary>
        /// A hub document kept as a profile. A URL already kept is refreshed in place (one profile per
        /// address), its name and id unchanged.
        /// </summary>
        public ForgeProfile Add(object hub, string origin, string url = null, string name = null, DateTime? now = null)
        {
            if (Json.Object(hub) == null) throw new ArgumentException("A profile is a hub document: a JSON object.");
            hub = ProfileSecrets.Scrub(hub);
            string id = null;
            Change(root =>
            {
                var entries = Entries(root);
                Dictionary<string, object> same = origin == ForgeProfile.FromUrl && url != null
                    ? entries.FirstOrDefault(e => Json.String(Json.Get(e, "origin")) == ForgeProfile.FromUrl && string.Equals(Json.String(Json.Get(e, "url")), url, StringComparison.Ordinal))
                    : null;
                string stamp = Stamp(now);
                if (same != null)
                {
                    same["hub"] = hub;
                    same["refreshedAt"] = stamp;
                    id = Json.String(Json.Get(same, "id"));

                    return;
                }
                var used = new HashSet<string>(entries.Select(e => Json.String(Json.Get(e, "id"))).Where(i => i != null));
                do { id = NewId(); } while (used.Contains(id));
                var entry = new Dictionary<string, object>
                {
                    ["id"] = id,
                    ["name"] = !string.IsNullOrWhiteSpace(name) ? name.Trim() : DefaultName(hub, url),
                    ["origin"] = origin == ForgeProfile.FromUrl ? ForgeProfile.FromUrl : ForgeProfile.FromFile,
                };
                if (origin == ForgeProfile.FromUrl) entry["url"] = url;
                entry["addedAt"] = stamp;
                entry["refreshedAt"] = origin == ForgeProfile.FromUrl ? stamp : null;
                entry["hub"] = hub;
                RawEntries(root).Add(entry);
            });

            return Get(id);
        }

        public void Rename(string id, string name)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A profile needs a name.");
            if (name.Trim().Length > 120) throw new ArgumentException("A shorter name: 120 characters at most.");
            Change(root => Entry(root, id)["name"] = name.Trim());
        }

        /// <summary>The profile is forgotten; when it was the active one, no profile is active.</summary>
        public void Remove(string id)
        {
            Change(root =>
            {
                if (RawEntries(root).RemoveAll(o => o is Dictionary<string, object> e && Json.String(Json.Get(e, "id")) == id) == 0) throw new ArgumentException("No such profile.");
                if (Json.String(Json.Get(root, "active")) == id) root["active"] = null;
            });
        }

        /// <summary>Another profile active, or none (null). A project is never changed by it.</summary>
        public void SetActive(string id)
        {
            Change(root =>
            {
                if (id != null) Entry(root, id);
                root["active"] = id;
            });
        }

        /// <summary>A URL profile's hub, read again.</summary>
        public void Store(string id, object hub, DateTime? now = null)
        {
            if (Json.Object(hub) == null) throw new ArgumentException("A profile is a hub document: a JSON object.");
            hub = ProfileSecrets.Scrub(hub);
            Change(root =>
            {
                var entry = Entry(root, id);
                entry["hub"] = hub;
                entry["refreshedAt"] = Stamp(now);
            });
        }

        /// <summary>
        /// The profile's bare hub document, nothing secret in it: written as
        /// &lt;name&gt;.forge-profile.json in a folder (as "&lt;name&gt; 2..." beside a file already
        /// there, never over it), or at the path the user picked in a save panel. Returns the path.
        /// </summary>
        public string Export(string id, string target)
        {
            ForgeProfile profile = Get(id) ?? throw new ArgumentException("No such profile.");
            string path = target;
            if (Directory.Exists(target))
            {
                string stem = ExportName(profile.Name).Substring(0, ExportName(profile.Name).Length - ExportSuffix.Length);
                path = System.IO.Path.Combine(target, stem + ExportSuffix);
                for (int n = 2; File.Exists(path); n++) path = System.IO.Path.Combine(target, stem + " " + n + ExportSuffix);
            }
            File.WriteAllText(path, Json.Write(ProfileSecrets.Scrub(profile.Hub), true) + "\n", new UTF8Encoding(false));

            return path;
        }

        /// <summary>The hub's own name, else the address's host, else "Profile".</summary>
        private static string DefaultName(object hub, string url)
        {
            string name = Json.String(Json.Get(hub, "name"))?.Trim();
            if (!string.IsNullOrEmpty(name)) return name;

            return url != null && Uri.TryCreate(url, UriKind.Absolute, out Uri uri) ? uri.Authority.ToLowerInvariant() : "Profile";
        }

        /// <summary>A file name for the profile's export: its name without the characters file systems refuse.</summary>
        public static string ExportName(string name)
        {
            var clean = new string((name ?? string.Empty).Select(c => "<>:\"/\\|?*".IndexOf(c) >= 0 || char.IsControl(c) ? '-' : c).ToArray()).Trim().Trim('.');

            if (clean.Length > 100) clean = clean.Substring(0, 100);

            return (clean.Length == 0 ? "profile" : clean) + ExportSuffix;
        }

        private Dictionary<string, object> Read()
        {
            string text = File.Exists(Path) ? File.ReadAllText(Path) : null;
            if (string.IsNullOrWhiteSpace(text)) return new Dictionary<string, object> { ["version"] = 1, ["active"] = null, ["profiles"] = new List<object>() };
            // A file that is not JSON is never written over: it may be the other app's, or the user's.
            object parsed;
            try { parsed = Json.Parse(text); }
            catch (FormatException e) { throw new FormatException(Path + " is not JSON (" + e.Message + "): left as it is."); }

            return Json.Object(parsed) ?? throw new FormatException(Path + " is not a profiles file: left as it is.");
        }

        /// <summary>Read again, changed, written whole: a temporary file in the same folder, renamed over the file.</summary>
        private void Change(Action<Dictionary<string, object>> change)
        {
            Dictionary<string, object> root = Read();
            if (!root.ContainsKey("version")) root["version"] = 1;
            if (!root.ContainsKey("active")) root["active"] = null;
            RawEntries(root);
            change(root);
            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            Directory.CreateDirectory(folder);
            string temporary = System.IO.Path.Combine(folder, "." + System.IO.Path.GetFileName(Path) + "." + NewId() + ".tmp");
            File.WriteAllText(temporary, Json.Write(root, true) + "\n", new UTF8Encoding(false));
            try
            {
                if (File.Exists(Path)) File.Replace(temporary, Path, null);
                else File.Move(temporary, Path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        private static List<object> RawEntries(Dictionary<string, object> root)
        {
            if (!(root.TryGetValue("profiles", out object value) && value is List<object> list))
            {
                list = new List<object>();
                root["profiles"] = list;
            }

            return list;
        }

        private static List<Dictionary<string, object>> Entries(Dictionary<string, object> root) => RawEntries(root).OfType<Dictionary<string, object>>().ToList();

        private static Dictionary<string, object> Entry(Dictionary<string, object> root, string id)
        {
            return Entries(root).FirstOrDefault(e => Json.String(Json.Get(e, "id")) == id) ?? throw new ArgumentException("No such profile.");
        }

        private static List<ForgeProfile> List(Dictionary<string, object> root)
        {
            return RawEntries(root).OfType<Dictionary<string, object>>().Where(e => Json.String(Json.Get(e, "id")) != null).Select(e => new ForgeProfile
            {
                Id = Json.String(Json.Get(e, "id")),
                Name = Json.String(Json.Get(e, "name")) ?? Json.String(Json.Get(e, "hub", "name")) ?? "Profile",
                Origin = Json.String(Json.Get(e, "origin")) ?? ForgeProfile.FromFile,
                Url = Json.String(Json.Get(e, "url")),
                AddedAt = Json.String(Json.Get(e, "addedAt")),
                RefreshedAt = Json.String(Json.Get(e, "refreshedAt")),
                Hub = Json.Object(Json.Get(e, "hub")) ?? new Dictionary<string, object>(),
            }).ToList();
        }

        private static string Stamp(DateTime? now) => (now ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

        /// <summary>16 hex characters, random.</summary>
        public static string NewId()
        {
            var bytes = new byte[8];
            using (var random = RandomNumberGenerator.Create()) random.GetBytes(bytes);

            return BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
        }
    }

    /// <summary>A profile's address redirects to another host: told to the user, not followed.</summary>
    public sealed class ProfileRedirectException : Exception
    {
        public string Location { get; }

        public ProfileRedirectException(string from, Uri location)
            : base(from + " redirects to another host, " + location.Authority + ": not followed. Add " + location.AbsoluteUri + " itself if you trust it.")
        {
            Location = location.AbsoluteUri;
        }
    }

    /// <summary>
    /// A profile read from what people share: an exported file (&lt;name&gt;.forge-profile.json, or any
    /// hub.json, or a package.json with a "hub" section) or an https address (http only for
    /// localhost). 1 MB at most; a redirect to another host is not followed.
    /// </summary>
    public static class ProfileImport
    {
        public const int MaxRedirects = 5;

        /// <summary>The address, checked: https, or http on this machine (for tests and a local hub).</summary>
        public static Uri CheckUrl(string url)
        {
            if (!Uri.TryCreate((url ?? string.Empty).Trim(), UriKind.Absolute, out Uri uri)) throw new ArgumentException("Not an address: " + url);
            if (!string.IsNullOrEmpty(uri.UserInfo)) throw new ArgumentException("An address without a user name or password in it.");
            foreach (string pair in uri.Query.TrimStart('?').ToLowerInvariant().Split('&'))
            {
                string key = Uri.UnescapeDataString(pair.Split('=')[0]);
                if (key.Contains("token") || key == "key" || key.Contains("secret") || key.Contains("password")) throw new ArgumentException("An address without a token in it: a profile is shared, a token never is.");
            }
            if (uri.Scheme == Uri.UriSchemeHttps) return uri;
            if (uri.Scheme == Uri.UriSchemeHttp && (uri.IsLoopback || uri.Host == "localhost")) return uri;

            throw new ArgumentException("A profile is read over https only (http only from this machine): " + url);
        }

        public static object ReadFile(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("No such file.", path);
            if (info.Length > ProfileStore.MaxBytes) throw new Exception(info.Name + " is over 1 MB: not a profile.");

            return Document(Json.Parse(File.ReadAllText(path)));
        }

        /// <summary>The hub document of what was read: itself, or a package.json's "hub" section.</summary>
        public static object Document(object json)
        {
            object hub = HubCatalogReader.HubOf(json);
            if (Json.Object(hub) == null) throw new FormatException("Not a hub document: no JSON object.");
            if (Json.Get(hub, "sources") != null && Json.Array(Json.Get(hub, "sources")) == null) throw new FormatException("Not a hub document: its \"sources\" are no list.");
            if (Json.Array(Json.Get(hub, "sources")) == null && Json.String(Json.Get(hub, "name")) == null) throw new FormatException("Not a hub document: it has neither a name nor sources.");

            return hub;
        }

        /// <summary>The hub document at an address: redirects followed on the same host only, the answer bounded.</summary>
        public static async Task<object> Fetch(IWebTransport transport, string url)
        {
            Uri uri = CheckUrl(url);
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                var request = new WebRequest { Url = uri.AbsoluteUri, NoRedirects = true };
                request.Headers["Accept"] = "application/json";
                WebResponse response = await transport.Send(request);
                if (response.Error != null && response.Status == 0) throw new Exception(response.Error);
                if (response.Status >= 300 && response.Status < 400)
                {
                    string location = response.Header("Location");
                    if (string.IsNullOrEmpty(location)) throw new Exception("HTTP " + response.Status + " without a Location.");
                    var next = new Uri(uri, location);
                    if (!string.Equals(next.Authority, uri.Authority, StringComparison.OrdinalIgnoreCase)) throw new ProfileRedirectException(url, next);
                    uri = CheckUrl(next.AbsoluteUri);
                    continue;
                }
                if (!response.Ok) throw new Exception("HTTP " + response.Status);
                if (long.TryParse(response.Header("Content-Length"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long length) && length > ProfileStore.MaxBytes
                    || Encoding.UTF8.GetByteCount(response.Text ?? string.Empty) > ProfileStore.MaxBytes)
                {
                    throw new Exception("The answer is over 1 MB: not a profile.");
                }

                return Document(Json.Parse(response.Text));
            }

            throw new Exception("Too many redirects.");
        }
    }

    /// <summary>
    /// A URL profile read again: on demand only, each profile at most once per editor start (an
    /// attempt that failed counts, as in Forge Hub); a failure keeps the hub as last read.
    /// </summary>
    public static class ProfileRefresh
    {
        public static Func<IWebTransport> Transport = () => UnityWebTransport.Instance;

        /// <summary>Whether that profile was read again (or tried) since the editor started; <paramref name="asked"/> is the session's record.</summary>
        public static bool Asked(ISet<string> asked, string id) => asked != null && asked.Contains(id);

        /// <summary>Reads the profile's address again and keeps the answer; false (the cached hub kept) for a file profile or when <paramref name="quiet"/> and it fails.</summary>
        public static async Task<bool> Refresh(ProfileStore store, ForgeProfile profile, IWebTransport transport = null, bool quiet = false)
        {
            if (profile == null || profile.Origin != ForgeProfile.FromUrl || string.IsNullOrEmpty(profile.Url)) return false;
            try
            {
                object hub = await ProfileImport.Fetch(transport ?? Transport(), profile.Url);
                store.Store(profile.Id, hub);

                return true;
            }
            catch (Exception) when (quiet)
            {
                return false;
            }
        }
    }

    /// <summary>
    /// What looks like a secret, out of a hub document (on add, refresh and export, as Forge Hub
    /// does): members named as one - token, accessToken, githubToken, authToken, password, secret,
    /// clientSecret, apiKey, licenseKey, licenceKey, privateKey, authorization, case and punctuation
    /// ignored - unless their value is an http(s) address (a checkout's token endpoint is one), and
    /// any GitHub token (ghp_, gho_, ghu_, ghs_, ghr_, github_pat_) wherever it sits.
    /// </summary>
    public static class ProfileSecrets
    {
        private static readonly HashSet<string> Names = new HashSet<string> { "token", "accesstoken", "githubtoken", "authtoken", "password", "secret", "clientsecret", "apikey", "licensekey", "licencekey", "privatekey", "authorization" };
        private static readonly string[] GitHubPrefixes = { "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_" };

        /// <summary>A copy without the secrets.</summary>
        public static object Scrub(object value)
        {
            switch (value)
            {
                case Dictionary<string, object> map:
                    var clean = new Dictionary<string, object>();
                    foreach (KeyValuePair<string, object> pair in map)
                    {
                        if (SecretName(pair.Key) && !Address(pair.Value) || GitHubToken(pair.Value)) continue;
                        clean[pair.Key] = Scrub(pair.Value);
                    }

                    return clean;
                case List<object> list:
                    return list.Where(v => !GitHubToken(v)).Select(Scrub).ToList();
                default:
                    return value;
            }
        }

        public static bool SecretName(string key) => Names.Contains(new string((key ?? string.Empty).Where(c => c < 128 && char.IsLetterOrDigit(c)).ToArray()).ToLowerInvariant());

        private static bool Address(object value) => value is string s && (s.StartsWith("https://", StringComparison.Ordinal) || s.StartsWith("http://", StringComparison.Ordinal));

        private static bool GitHubToken(object value) => value is string s && GitHubPrefixes.Any(p => s.Trim().StartsWith(p, StringComparison.Ordinal));
    }
}
