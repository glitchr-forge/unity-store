using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Glitchr.Store
{
    /// <summary>
    /// What a hub declares for its licences, data only: the Ed25519 public key that signs them
    /// ("licensing": {"publicKey"}), and, when the store changed its key, the previous key with its
    /// signature of the new one ("previousKey", "rotation").
    /// </summary>
    public sealed class HubLicensing
    {
        /// <summary>The prefix of the text a store's previous key signs to hand over to its new key: this, then the new key in base64.</summary>
        public const string RotationPrefix = "forge-key-rotation:v1:";

        /// <summary>The store, as the window knows it: its source's key (a manifest URL, a preset's package, a Git repository).</summary>
        public string Store;
        public string StoreName;
        public string PublicKey;
        public string PreviousKey;
        public string Rotation;
        /// <summary>What the reading made of it against the store's pin.</summary>
        public KeyCheck Check;

        public static HubLicensing Of(object hub, string store, string storeName)
        {
            object licensing = Json.Object(Json.Get(hub, "licensing"));
            string key = Json.String(Json.Get(licensing, "publicKey"));
            if (string.IsNullOrEmpty(key)) return null;

            return new HubLicensing
            {
                Store = store,
                StoreName = storeName,
                PublicKey = key.Trim(),
                PreviousKey = Json.String(Json.Get(licensing, "previousKey"))?.Trim(),
                Rotation = Json.String(Json.Get(licensing, "rotation"))?.Trim(),
            };
        }

        /// <summary>The text the previous key signs for a rotation to <paramref name="newKey"/>.</summary>
        public static byte[] RotationMessage(string newKey) => Encoding.UTF8.GetBytes(RotationPrefix + newKey);
    }

    /// <summary>What a store's announced key does to its pin.</summary>
    public enum KeyCheck
    {
        /// <summary>Not a 32-byte Ed25519 key in base64: ignored, nothing pinned.</summary>
        Invalid,
        /// <summary>The store's first key: pinned (trust on first use).</summary>
        Pinned,
        /// <summary>The pinned key, again.</summary>
        Same,
        /// <summary>A new key the pinned one signed: pinned instead, without a prompt.</summary>
        Rotated,
        /// <summary>A different key without a valid rotation: the user must accept it; until then the store's licences are not trusted.</summary>
        Changed,
        /// <summary>A different key the user already refused: the pinned key is kept, without a new prompt.</summary>
        Refused,
    }

    /// <summary>
    /// The public keys of the stores, pinned on first use, per user (Forge Store/keys.json in the
    /// local application data, never in the project). A licence is checked against the pinned key,
    /// not against whatever a hub.json serves today: a later, different key is held as pending until
    /// the user accepts it, and the store's licences are not trusted in the meantime. A rotation the
    /// pinned key signed passes without a prompt - its signature is checked by io.glitchr.licensing
    /// (Forge Seal) when the project has it; without it, every change waits for the user.
    /// </summary>
    public sealed class KeyPins
    {
        public const int KeyLength = 32;

        public sealed class Pin
        {
            public string Key;
            public string Name;
            /// <summary>A different key announced since, waiting for the user.</summary>
            public string Pending;
            /// <summary>Keys the user refused: no prompt for them again.</summary>
            public List<string> Refused = new List<string>();
            /// <summary>Why the pending key could not pass alone (no rotation, a bad signature, no Forge Seal to check it).</summary>
            public string Reason;
        }

        private readonly string path;
        private readonly Func<byte[], byte[], byte[], bool> verify;
        private readonly Dictionary<string, Pin> pins = new Dictionary<string, Pin>();
        private bool changed;

        /// <param name="verify">Ed25519's check (signature, message, public key), or null when no package provides it.</param>
        public KeyPins(string path, Func<byte[], byte[], byte[], bool> verify)
        {
            this.path = path;
            this.verify = verify;
            if (path == null || !System.IO.File.Exists(path)) return;
            try
            {
                foreach (KeyValuePair<string, object> pair in Json.Object(Json.Parse(System.IO.File.ReadAllText(path))) ?? new Dictionary<string, object>())
                {
                    pins[pair.Key] = new Pin
                    {
                        Key = Json.String(Json.Get(pair.Value, "key")),
                        Name = Json.String(Json.Get(pair.Value, "name")),
                        Pending = Json.String(Json.Get(pair.Value, "pending")),
                        Reason = Json.String(Json.Get(pair.Value, "reason")),
                        Refused = (Json.Array(Json.Get(pair.Value, "refused")) ?? new List<object>()).OfType<string>().ToList(),
                    };
                }
            }
            catch (Exception)
            {
                // A damaged file is kept aside (keys.json.damaged) before anything replaces it;
                // its stores are pinned again on their next reading.
                pins.Clear();
                try { System.IO.File.Copy(path, path + ".damaged", true); }
                catch (Exception) { /* nothing more to save */ }
            }
        }

        /// <summary>The user's file: Forge Store/keys.json in the local application data.</summary>
        public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge Store", "keys.json");

        public bool CanVerifyRotations => verify != null;

        public Pin Get(string store) => pins.TryGetValue(store ?? string.Empty, out Pin pin) ? pin : null;

        public IEnumerable<KeyValuePair<string, Pin>> All => pins;

        /// <summary>The key the store's licences are checked against: the pinned one, or null while a change waits for the user.</summary>
        public string TrustedKey(string store)
        {
            Pin pin = Get(store);

            return pin == null || pin.Pending != null ? null : pin.Key;
        }

        /// <summary>A 32-byte key in base64, or null.</summary>
        public static byte[] Decode(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) return null;
            try
            {
                byte[] bytes = Convert.FromBase64String(key.Trim());

                return bytes.Length == KeyLength ? bytes : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>A short mark of a key, to compare two by eye: the first bytes of its SHA-256, in hex pairs.</summary>
        public static string Fingerprint(string key)
        {
            byte[] bytes = Decode(key);
            if (bytes == null) return "?";
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);

                return string.Join(":", hash.Take(8).Select(b => b.ToString("x2")));
            }
        }

        /// <summary>What the store's hub announces, checked against its pin.</summary>
        public KeyCheck Observe(HubLicensing announced)
        {
            if (announced == null || string.IsNullOrEmpty(announced.Store) || Decode(announced.PublicKey) == null) return KeyCheck.Invalid;
            string key = Normal(announced.PublicKey);
            Pin pin = Get(announced.Store);
            if (pin == null || Decode(pin.Key) == null)
            {
                pins[announced.Store] = new Pin { Key = key, Name = announced.StoreName };
                changed = true;

                return KeyCheck.Pinned;
            }
            if (!string.IsNullOrEmpty(announced.StoreName) && pin.Name != announced.StoreName)
            {
                pin.Name = announced.StoreName;
                changed = true;
            }
            if (key == pin.Key)
            {
                if (pin.Pending != null)
                {
                    // The hub serves the pinned key again: the change is withdrawn.
                    pin.Pending = pin.Reason = null;
                    changed = true;
                }

                return KeyCheck.Same;
            }
            if (pin.Refused.Contains(key)) return KeyCheck.Refused;

            string reason = RotationProblem(pin.Key, announced);
            if (reason == null)
            {
                pin.Key = key;
                pin.Pending = pin.Reason = null;
                changed = true;

                return KeyCheck.Rotated;
            }
            if (pin.Pending != key || pin.Reason != reason)
            {
                pin.Pending = key;
                pin.Reason = reason;
                changed = true;
            }

            return KeyCheck.Changed;
        }

        /// <summary>Null when the announced key is a rotation the pinned key signed; else why it is not.</summary>
        private string RotationProblem(string pinned, HubLicensing announced)
        {
            if (string.IsNullOrEmpty(announced.Rotation) || string.IsNullOrEmpty(announced.PreviousKey)) return "the store gives no signed rotation";
            if (Normal(announced.PreviousKey) != pinned) return "the rotation starts from another key than the pinned one";
            if (verify == null) return "Forge Seal (io.glitchr.licensing) is not in the project to check the rotation's signature";
            byte[] signature;
            try { signature = Convert.FromBase64String(announced.Rotation); }
            catch (FormatException) { return "the rotation's signature is not base64"; }
            bool valid;
            try { valid = verify(signature, HubLicensing.RotationMessage(Normal(announced.PublicKey)), Decode(pinned)); }
            catch (Exception) { valid = false; }

            return valid ? null : "the rotation's signature does not verify with the pinned key";
        }

        /// <summary>The user accepts the pending key: pinned, and the store's licences trusted again.</summary>
        public void Accept(string store)
        {
            Pin pin = Get(store);
            if (pin?.Pending == null) return;
            pin.Key = pin.Pending;
            pin.Pending = pin.Reason = null;
            changed = true;
        }

        /// <summary>The user refuses the pending key: the pinned key stays the trusted one, and that key does not ask again.</summary>
        public void Refuse(string store)
        {
            Pin pin = Get(store);
            if (pin?.Pending == null) return;
            if (!pin.Refused.Contains(pin.Pending)) pin.Refused.Add(pin.Pending);
            pin.Pending = pin.Reason = null;
            changed = true;
        }

        /// <summary>The store's pin dropped: its next key is pinned again on first use.</summary>
        public void Forget(string store)
        {
            if (pins.Remove(store ?? string.Empty)) changed = true;
        }

        public void Save()
        {
            if (path == null || !changed) return;
            var root = new Dictionary<string, object>();
            foreach (KeyValuePair<string, Pin> pair in pins.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                var entry = new Dictionary<string, object> { ["key"] = pair.Value.Key, ["name"] = pair.Value.Name };
                if (pair.Value.Pending != null) entry["pending"] = pair.Value.Pending;
                if (pair.Value.Reason != null) entry["reason"] = pair.Value.Reason;
                if (pair.Value.Refused.Count > 0) entry["refused"] = pair.Value.Refused.Cast<object>().ToList();
                root[pair.Key] = entry;
            }
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            System.IO.File.WriteAllText(path, Json.Write(root, true) + "\n", new UTF8Encoding(false));
            changed = false;
        }

        private static string Normal(string key) => Convert.ToBase64String(Decode(key));
    }

    /// <summary>
    /// io.glitchr.licensing (Forge Seal), when the project has it: found by name at run time, so
    /// Forge Store neither depends on it nor breaks without it. Its Ed25519 check verifies a key
    /// rotation; its token check verifies a licence against a store's pinned key.
    /// </summary>
    public static class ForgeSeal
    {
        private static bool searched;
        private static MethodInfo ed25519Verify;
        private static MethodInfo tokenVerify;

        private static void Find()
        {
            if (searched) return;
            searched = true;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.GetName().Name != "Glitchr.Licensing") continue;
                ed25519Verify = assembly.GetType("Glitchr.Licensing.Ed25519", false)?.GetMethod("Verify", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(byte[]), typeof(byte[]), typeof(byte[]) }, null);
                tokenVerify = assembly.GetType("Glitchr.Licensing.LicenseToken", false)?.GetMethod("Verify", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(string), typeof(string) }, null);
            }
        }

        /// <summary>Whether Forge Seal is in the project (and has the checks this window calls).</summary>
        public static bool Available
        {
            get
            {
                Find();

                return ed25519Verify != null && ed25519Verify.ReturnType == typeof(bool);
            }
        }

        /// <summary>Ed25519's check (signature, message, public key), or null without Forge Seal.</summary>
        public static Func<byte[], byte[], byte[], bool> Verifier => Available ? (s, m, k) => (bool)ed25519Verify.Invoke(null, new object[] { s, m, k }) : (Func<byte[], byte[], byte[], bool>)null;

        /// <summary>A signed licence token's payload (Forge Seal's LicensePayload) when it verifies with that key; else null.</summary>
        public static object VerifyToken(string token, string publicKey)
        {
            Find();
            if (tokenVerify == null || string.IsNullOrEmpty(publicKey)) return null;
            try { return tokenVerify.Invoke(null, new object[] { token, publicKey }); }
            catch (TargetInvocationException) { return null; }
        }
    }

    /// <summary>
    /// The stores' trust, for whoever checks a licence in the editor: the key pinned for a store
    /// (null while a change waits for the user), and a licence token checked against it.
    /// </summary>
    public static class StoreTrust
    {
        private static KeyPins pins;

        /// <summary>The user's pins, read once per domain.</summary>
        public static KeyPins Pins => pins ??= new KeyPins(KeyPins.DefaultPath, ForgeSeal.Verifier);

        /// <summary>Read the file again (after another editor changed it).</summary>
        public static void Reload() => pins = null;

        public static string TrustedKey(string store) => Pins.TrustedKey(store);

        /// <summary>
        /// A licence token of that store, checked with its pinned key by Forge Seal: its payload,
        /// or null - with the reason - when the store's key waits for the user, when Forge Seal is
        /// not in the project, or when the signature does not verify.
        /// </summary>
        public static object VerifyLicence(string store, string token, out string reason)
        {
            string key = TrustedKey(store);
            if (key == null)
            {
                reason = Pins.Get(store) == null ? "no key is pinned for this store" : "the store's key changed: accept it in Forge Store first";

                return null;
            }
            if (!ForgeSeal.Available)
            {
                reason = "Forge Seal (io.glitchr.licensing) is not in the project";

                return null;
            }
            object payload = ForgeSeal.VerifyToken(token, key);
            reason = payload == null ? "the licence's signature does not verify with the store's key" : null;

            return payload;
        }
    }
}
