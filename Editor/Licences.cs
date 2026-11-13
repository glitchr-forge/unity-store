using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Glitchr.Store
{
    /// <summary>
    /// A licence token as its store signs it - a Forge's (format 2: bound to its registry and
    /// package, one machine, 30 days) or an offline licence a static hub's author signed (omnihub
    /// licence sign: no machine runs on any machine, no valid_until runs without an end):
    /// <c>base64url(payload JSON) "." base64url(Ed25519 signature of the payload JSON)</c>. Read here
    /// without Forge Seal, which only checks the signature (its Ed25519), so any Forge Seal version
    /// will do.
    /// </summary>
    public sealed class LicenceToken
    {
        public string Token;
        public byte[] PayloadBytes;
        public byte[] Signature;
        public object Payload;

        public int Version;
        public string Registry;
        public string Package;
        public string Software;
        public string Licensee;
        public string Email;
        public string Machine;
        public long IssuedAt;
        public long ValidUntil;
        public long UpdatesUntil;

        /// <summary>The licence opened with the store's previous key, within its bound (after a rotation).</summary>
        public bool ByPreviousKey;

        /// <summary>A format 2 token that names no machine: any machine.</summary>
        public bool AnyMachine => Version >= 2 && string.IsNullOrEmpty(Machine);

        /// <summary>Whether it has an end: a format 2 token without valid_until has none.</summary>
        public bool Ends => Version < 2 || ValidUntil != 0;

        /// <summary>Who holds it: an offline licence's licensee, else the seat's e-mail.</summary>
        public string Holder => !string.IsNullOrEmpty(Licensee) ? Licensee : Email;

        /// <summary>The token read (not checked), or null when it is no token.</summary>
        public static LicenceToken Parse(string token)
        {
            string[] parts = (token ?? string.Empty).Trim().Split('.');
            if (parts.Length != 2 || parts[0].Length == 0 || parts[1].Length == 0) return null;
            byte[] json, signature;
            try
            {
                json = Base64Url(parts[0]);
                signature = Base64Url(parts[1]);
            }
            catch (FormatException)
            {
                return null;
            }
            object payload;
            try
            {
                payload = Json.Parse(Encoding.UTF8.GetString(json));
            }
            catch (FormatException)
            {
                return null;
            }
            if (Json.Object(payload) == null || signature.Length != 64) return null;

            return new LicenceToken
            {
                Token = token.Trim(),
                PayloadBytes = json,
                Signature = signature,
                Payload = payload,
                Version = Json.Get(payload, "v") is double v ? (int)v : 0,
                Registry = Json.String(Json.Get(payload, "registry")),
                Package = Json.String(Json.Get(payload, "package")),
                Software = Json.String(Json.Get(payload, "software")),
                Licensee = Json.String(Json.Get(payload, "licensee")),
                Email = Json.String(Json.Get(payload, "email")),
                Machine = Json.String(Json.Get(payload, "machine")),
                IssuedAt = Number(payload, "issued_at"),
                ValidUntil = Number(payload, "valid_until"),
                UpdatesUntil = Number(payload, "updates_until"),
            };
        }

        /// <summary>Whether the token's signature is that key's (Ed25519 through <paramref name="verify"/>).</summary>
        public bool SignedBy(string key, Func<byte[], byte[], byte[], bool> verify)
        {
            byte[] bytes = KeyPins.Decode(key);
            if (bytes == null || verify == null) return false;
            try { return verify(Signature, PayloadBytes, bytes); }
            catch (Exception) { return false; }
        }

        /// <summary>"no end", or the day it runs out.</summary>
        public string Validity => Ends ? "until " + Day(ValidUntil) : "no end";

        public static string Day(long unix) => DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        private static long Number(object payload, string name) => Json.Get(payload, name) is double d ? (long)d : 0;

        private static byte[] Base64Url(string text)
        {
            string base64 = text.Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
                case 1: throw new FormatException("Not base64url.");
            }

            return Convert.FromBase64String(base64);
        }
    }

    /// <summary>
    /// A licence checked as Forge Store checks it, before keeping it or showing it: the store's
    /// pinned key (or its bounded previous key), then the binding of plan item 17.2 - format 2, the
    /// registry the package is installed from, the package's name -, this machine when it names
    /// one, its end when it has one. Every refusal says why in plain words.
    /// </summary>
    public static class LicenceCheck
    {
        public static LicenceToken Verify(KeyPins pins, string store, string token, string registry, string package, Func<byte[], byte[], byte[], bool> verify, string machineIdentifier, DateTimeOffset now, out string reason)
        {
            KeyPins.Pin pin = pins?.Read(store);
            if (pin == null || KeyPins.Decode(pin.Key) == null)
            {
                reason = "no licensing key is pinned for this store yet: read its hub first (Refresh).";

                return null;
            }
            if (pin.Pending != null)
            {
                reason = "the store announces a new licensing key: accept it or keep the pinned one first.";

                return null;
            }
            LicenceToken read = LicenceToken.Parse(token);
            if (read == null)
            {
                reason = "this is not a licence token (two base64url parts separated by a dot, the first a JSON payload).";

                return null;
            }
            if (verify == null)
            {
                reason = "Forge Seal (io.glitchr.licensing) is not in the project to check the licence's signature; a licensed package usually brings it.";

                return null;
            }
            if (!read.SignedBy(pin.Key, verify))
            {
                if (pin.Previous != null && read.SignedBy(pin.Previous, verify))
                {
                    if (!pin.PreviousOpens(now))
                    {
                        pins.DropExpired(store);
                        reason = "the licence was signed with the store's previous key, which no longer opens licences since " + LicenceToken.Day(pin.PreviousUntil) + ": ask the store for one signed with its current key.";

                        return null;
                    }
                    read.ByPreviousKey = true;
                }
                else
                {
                    reason = "the licence's signature does not verify with the store's key: it is another store's, or it was changed.";

                    return null;
                }
            }
            if (read.Version < 2)
            {
                reason = "this licence is of format 1: it names no store and cannot be bound to this one. Activate it again online (the store then signs one bound to its registry), or ask the seller for a new one.";

                return null;
            }
            if (!SameRegistry(read.Registry, registry))
            {
                reason = "the licence is another store's: it names " + (read.Registry ?? "no registry") + ", and " + package + " comes from " + (registry ?? "no registry") + ".";

                return null;
            }
            if ((read.Package ?? string.Empty) != (package ?? string.Empty))
            {
                reason = "the licence is for " + (string.IsNullOrEmpty(read.Package) ? "a game or an application, not a package" : read.Package) + ", not " + package + ".";

                return null;
            }
            if (!read.AnyMachine && read.Machine != MachineHash(machineIdentifier))
            {
                reason = "the licence is another machine's.";

                return null;
            }
            if (read.Ends && now.ToUnixTimeSeconds() >= read.ValidUntil)
            {
                reason = "the licence ran out on " + LicenceToken.Day(read.ValidUntil) + ": ask the store for a new one.";

                return null;
            }
            reason = null;

            return read;
        }

        /// <summary>The machine as a licence names it: SHA-256 (hex) of its trimmed identifier, as Forge Seal and the Forge compute it.</summary>
        public static string MachineHash(string identifier)
        {
            using (SHA256 sha = SHA256.Create())
            {
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes((identifier ?? string.Empty).Trim())).Select(b => b.ToString("x2")));
            }
        }

        /// <summary>Two registry addresses of the same store: a final slash, the scheme's and the host's case and a default port aside (as Forge Seal's LicenseToken.SameRegistry).</summary>
        public static bool SameRegistry(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return false;
            a = a.Trim();
            b = b.Trim();
            if (Uri.TryCreate(a, UriKind.Absolute, out Uri x) && Uri.TryCreate(b, UriKind.Absolute, out Uri y))
            {
                return string.Equals(x.Scheme, y.Scheme, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(x.Host, y.Host, StringComparison.OrdinalIgnoreCase)
                    && x.Port == y.Port
                    && x.AbsolutePath.TrimEnd('/') == y.AbsolutePath.TrimEnd('/')
                    && x.Query == y.Query;
            }

            return a.TrimEnd('/') == b.TrimEnd('/');
        }

        /// <summary>
        /// The registry a package of the project is installed from: the lock's <c>url</c> for a
        /// registry package, else the scoped registry of the manifest whose scope matches its name
        /// most closely (a scope matches the name itself and the names under it). Null for Unity's
        /// own registry, a Git package, a folder.
        /// </summary>
        public static string InstalledRegistry(string manifestJson, string lockJson, string package)
        {
            object locked = string.IsNullOrEmpty(lockJson) ? null : Json.Get(Json.Parse(lockJson), "dependencies", package);
            string source = Json.String(Json.Get(locked, "source"));
            string url = Json.String(Json.Get(locked, "url"));
            if (source != null && source != InstalledPackage.Registry) return null;
            if (!string.IsNullOrEmpty(url)) return url.Trim().TrimEnd('/');

            object manifest = string.IsNullOrEmpty(manifestJson) ? null : Json.Parse(manifestJson);
            string requested = Json.String(Json.Get(manifest, "dependencies", package));
            if (requested != null && InstalledPackages.SourceOf(requested) != InstalledPackage.Registry) return null;
            string best = null;
            int length = -1;
            foreach (object registry in Json.Array(Json.Get(manifest, "scopedRegistries")) ?? new List<object>())
            {
                foreach (string scope in (Json.Array(Json.Get(registry, "scopes")) ?? new List<object>()).OfType<string>())
                {
                    if (scope.Length > length && (package == scope || package.StartsWith(scope + ".", StringComparison.Ordinal)))
                    {
                        best = Json.String(Json.Get(registry, "url"));
                        length = scope.Length;
                    }
                }
            }

            return best?.Trim().TrimEnd('/');
        }
    }

    /// <summary>
    /// The licences the user keeps, per user, in <c>Forge Store/licences.json</c> under the local
    /// application data - the folder of keys.json, never the project: a licence is its buyer's (it
    /// names them), opens the package in every project of theirs, and must not travel with a
    /// repository to teammates or to the public. One per registry and package. Forge Seal's
    /// PackageLicence reads the same file, so a licensed package checks itself.
    /// <code>
    /// {"version": 1, "licences": [{"registry": "https://owner.github.io/hub", "package": "com.example.tool",
    ///   "store": "manifest:https://owner.github.io/hub/-/hub.json", "token": "...", "addedAt": "2026-11-13T09:00:00Z"}]}
    /// </code>
    /// Read again before each change, unknown fields kept, written through a renamed temporary
    /// file; a file that is not JSON is never written over.
    /// </summary>
    public sealed class LicenceStore
    {
        public sealed class Kept
        {
            public string Registry;
            public string Package;
            public string Store;
            public string Token;
            public string AddedAt;
        }

        private readonly SharedFile file;

        public LicenceStore(string path)
        {
            file = new SharedFile(path ?? throw new ArgumentNullException(nameof(path)));
        }

        public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge Store", "licences.json");

        public string Path => file.Path;

        /// <summary>The shared file under the licences; tests replace its reading.</summary>
        public SharedFile File => file;

        public List<Kept> All() => Entries(file.Read(false)).Select(Of).Where(k => k.Package != null && k.Token != null).ToList();

        public Kept Get(string registry, string package) => All().FirstOrDefault(k => k.Package == package && LicenceCheck.SameRegistry(k.Registry, registry));

        /// <summary>Kept (in place of the one of the same registry and package).</summary>
        public void Put(string registry, string package, string store, string token, DateTime? now = null)
        {
            Change(root =>
            {
                var entries = RawEntries(root);
                var entry = entries.OfType<Dictionary<string, object>>().FirstOrDefault(e => Matches(e, registry, package));
                if (entry == null)
                {
                    entry = new Dictionary<string, object>();
                    entries.Add(entry);
                }
                entry["registry"] = registry.Trim().TrimEnd('/');
                entry["package"] = package;
                entry["store"] = store;
                entry["token"] = token.Trim();
                entry["addedAt"] = (now ?? DateTime.UtcNow).ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

                return true;
            });
        }

        /// <summary>The licence of that registry and package dropped; true when there was one.</summary>
        public bool Remove(string registry, string package)
        {
            bool removed = false;
            Change(root =>
            {
                removed = RawEntries(root).RemoveAll(e => e is Dictionary<string, object> d && Matches(d, registry, package)) > 0;

                return removed;
            });

            return removed;
        }

        private void Change(Func<Dictionary<string, object>, bool> apply)
        {
            Dictionary<string, object> root = file.Read(true);
            if (file.Damaged) throw new FormatException(file.Path + " is not a licences file: left as it is (move it away to keep licences again).");
            try
            {
                if (!root.ContainsKey("version")) root["version"] = 1;
                if (apply(root)) file.Write(root);
            }
            catch (Exception)
            {
                file.Forget();
                throw;
            }
        }

        private static bool Matches(Dictionary<string, object> entry, string registry, string package)
        {
            return Json.String(Json.Get(entry, "package")) == package && LicenceCheck.SameRegistry(Json.String(Json.Get(entry, "registry")), registry);
        }

        private static List<object> RawEntries(Dictionary<string, object> root)
        {
            if (!(root.TryGetValue("licences", out object value) && value is List<object> list))
            {
                list = new List<object>();
                root["licences"] = list;
            }

            return list;
        }

        private static IEnumerable<Dictionary<string, object>> Entries(Dictionary<string, object> root) => (Json.Array(Json.Get(root, "licences")) ?? new List<object>()).OfType<Dictionary<string, object>>();

        private static Kept Of(Dictionary<string, object> entry)
        {
            return new Kept
            {
                Registry = Json.String(Json.Get(entry, "registry")),
                Package = Json.String(Json.Get(entry, "package")),
                Store = Json.String(Json.Get(entry, "store")),
                Token = Json.String(Json.Get(entry, "token")),
                AddedAt = Json.String(Json.Get(entry, "addedAt")),
            };
        }
    }
}
