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
    /// ("licensing": {"publicKey"}), the registry its licences are bound to ("registry"), and, when
    /// the store changed its key, the previous key with its signature of the new one ("previousKey",
    /// "rotation") and the end of the previous key's licences ("previousKeyValidUntil", UNIX time).
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
        /// <summary>UNIX seconds past which no licence the previous key signed may open; 0: not announced (the previous key is then not kept).</summary>
        public long PreviousKeyValidUntil;
        /// <summary>The registry the store's licences name (a Forge's https://&lt;site&gt;/upm, a static hub's sources[0].url), when the hub says.</summary>
        public string Registry;
        /// <summary>A static hub's packages sold with a licence (its top-level "licensed"), each with its "get" link; null when the hub has no such list.</summary>
        public List<LicensedPackage> Licensed;
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
                PreviousKeyValidUntil = Json.Get(licensing, "previousKeyValidUntil") is double until && until > 0 ? (long)until : 0,
                Registry = Json.String(Json.Get(licensing, "registry"))?.Trim().TrimEnd('/'),
                Licensed = LicensedPackage.ListOf(hub),
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
    /// local application data, never in the project) - the very file Forge Hub, the desktop app,
    /// reads and writes, so one approval per store holds for both apps. A licence is checked
    /// against the pinned key, not against whatever a hub.json serves today: a later, different key
    /// is held as pending until the user accepts it, and the store's licences are not trusted in the
    /// meantime. A rotation the pinned key signed passes without a prompt - its signature is checked
    /// by io.glitchr.licensing (Forge Seal) when the project has it; without it, every change waits
    /// for the user. After a rotation the former key keeps opening the licences it signed for a
    /// bounded time (<see cref="Pin.Previous"/>).
    /// <para>
    /// The file is read again before every decision (Observe, Accept, Refuse, Forget, a licence's
    /// check) and each change is written at once - the file read again just before, only this
    /// store's known fields changed, other stores and unknown fields kept, through a temporary file
    /// renamed over it - so a decision taken in Forge Hub is seen at once and never undone.
    /// </para>
    /// </summary>
    public sealed class KeyPins
    {
        public const int KeyLength = 32;

        /// <summary>
        /// The longest a previous key keeps opening licences once its rotation is seen: a Forge's
        /// tokens run 30 days at most, and a hub.json that announces a far previousKeyValidUntil
        /// cannot extend a leaked key beyond it.
        /// </summary>
        public static readonly TimeSpan PreviousKeyCap = TimeSpan.FromDays(30);

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
            /// <summary>
            /// The key pinned before the last rotation (or accepted change) whose hub announced it as
            /// previousKey with a previousKeyValidUntil: the licences it signed still open until
            /// <see cref="PreviousUntil"/>. Never kept without that date.
            /// </summary>
            public string Previous;
            /// <summary>UNIX seconds: min(the hub's previousKeyValidUntil, when the change was seen + <see cref="PreviousKeyCap"/>).</summary>
            public long PreviousUntil;

            /// <summary>Whether <see cref="Previous"/> still opens licences at <paramref name="now"/>.</summary>
            public bool PreviousOpens(DateTimeOffset now) => Decode(Previous) != null && PreviousUntil > now.ToUnixTimeSeconds();

            public static Pin Of(object entry)
            {
                return new Pin
                {
                    Key = Json.String(Json.Get(entry, "key")),
                    Name = Json.String(Json.Get(entry, "name")),
                    Pending = Json.String(Json.Get(entry, "pending")),
                    Reason = Json.String(Json.Get(entry, "reason")),
                    Refused = (Json.Array(Json.Get(entry, "refused")) ?? new List<object>()).OfType<string>().ToList(),
                    Previous = Json.String(Json.Get(entry, "previous")),
                    PreviousUntil = Json.Get(entry, "previousUntil") is double until ? (long)until : 0,
                };
            }

            /// <summary>Only what a pin owns written back into its entry; the entry's other fields are kept.</summary>
            public void WriteInto(Dictionary<string, object> entry)
            {
                entry["key"] = Key;
                entry["name"] = Name;
                Optional(entry, "pending", Pending);
                Optional(entry, "reason", Reason);
                Optional(entry, "refused", Refused.Count > 0 ? Refused.Cast<object>().ToList() : null);
                Optional(entry, "previous", Previous);
                Optional(entry, "previousUntil", Previous != null ? PreviousUntil : (object)null);
            }

            private static void Optional(Dictionary<string, object> entry, string name, object value)
            {
                if (value == null) entry.Remove(name);
                else entry[name] = value;
            }
        }

        private readonly SharedFile file;
        private readonly Dictionary<string, object> memory = new Dictionary<string, object>();
        private readonly Func<byte[], byte[], byte[], bool> verify;
        private bool keptAside;

        /// <summary>The time a rotation is seen and a previous key checked at (tests set it).</summary>
        public Func<DateTimeOffset> Clock = () => DateTimeOffset.UtcNow;

        /// <param name="path">The file; null keeps the pins in memory only.</param>
        /// <param name="verify">Ed25519's check (signature, message, public key), or null when no package provides it.</param>
        public KeyPins(string path, Func<byte[], byte[], byte[], bool> verify)
        {
            this.verify = verify;
            if (path == null) return;
            file = new SharedFile(path);
            Tree(false);
        }

        /// <summary>The user's file: Forge Store/keys.json in the local application data (Unity's: ~/.local/share on macOS and Linux unless XDG_DATA_HOME, %LOCALAPPDATA% on Windows).</summary>
        public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge Store", "keys.json");

        /// <summary>The shared file under the pins (null in memory); tests replace its reading.</summary>
        public SharedFile File => file;

        public bool CanVerifyRotations => verify != null;

        /// <summary>The store's pin as the file holds it now (a copy: change it through Observe, Accept, Refuse, Forget).</summary>
        public Pin Get(string store) => Json.Object(Json.Get(Tree(false), store ?? string.Empty)) is Dictionary<string, object> entry ? Pin.Of(entry) : null;

        /// <summary>The store's pin read from the disk this instant, for a decision.</summary>
        public Pin Read(string store) => Json.Object(Json.Get(Tree(true), store ?? string.Empty)) is Dictionary<string, object> entry ? Pin.Of(entry) : null;

        public IEnumerable<KeyValuePair<string, Pin>> All => Tree(false).Where(p => Json.Object(p.Value) != null).OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, Pin>(p.Key, Pin.Of(p.Value))).ToList();

        /// <summary>The key the store's licences are checked against: the pinned one, or null while a change waits for the user.</summary>
        public string TrustedKey(string store)
        {
            Pin pin = Read(store);

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
            KeyCheck check = KeyCheck.Invalid;
            Change(root => ObserveIn(root, announced, Clock(), out check));

            return check;
        }

        /// <summary>Every announced key checked in one change of the file; each one's <see cref="HubLicensing.Check"/> set.</summary>
        public void ObserveAll(IEnumerable<HubLicensing> announced)
        {
            var list = announced.ToList();
            if (list.Count == 0) return;
            DateTimeOffset now = Clock();
            Change(root =>
            {
                bool changed = false;
                foreach (HubLicensing licensing in list)
                {
                    changed |= ObserveIn(root, licensing, now, out KeyCheck check);
                    licensing.Check = check;
                }

                return changed;
            });
        }

        /// <summary>True when the pin changed.</summary>
        private bool ObserveIn(Dictionary<string, object> root, HubLicensing announced, DateTimeOffset now, out KeyCheck check)
        {
            check = KeyCheck.Invalid;
            if (announced == null || string.IsNullOrEmpty(announced.Store) || Decode(announced.PublicKey) == null) return false;
            string key = Normal(announced.PublicKey);
            var entry = Json.Object(Json.Get(root, announced.Store));
            Pin pin = entry == null ? null : Pin.Of(entry);
            if (pin == null || Decode(pin.Key) == null)
            {
                // The first key (or one in place of a damaged pin): pinned as it is.
                entry ??= new Dictionary<string, object>();
                new Pin { Key = key, Name = announced.StoreName }.WriteInto(entry);
                root[announced.Store] = entry;
                check = KeyCheck.Pinned;

                return true;
            }
            bool changed = false;
            if (!string.IsNullOrEmpty(announced.StoreName) && pin.Name != announced.StoreName)
            {
                pin.Name = announced.StoreName;
                changed = true;
            }
            changed |= DropExpiredPrevious(pin, now);
            if (key == pin.Key)
            {
                if (pin.Pending != null)
                {
                    // The hub serves the pinned key again: the change is withdrawn.
                    pin.Pending = pin.Reason = null;
                    changed = true;
                }
                check = KeyCheck.Same;
            }
            else if (pin.Refused.Contains(key))
            {
                check = KeyCheck.Refused;
            }
            else
            {
                string reason = RotationProblem(pin.Key, announced);
                if (reason == null)
                {
                    string former = pin.Key;
                    pin.Key = key;
                    pin.Pending = pin.Reason = null;
                    SetPrevious(pin, former, announced.PreviousKeyValidUntil, now);
                    changed = true;
                    check = KeyCheck.Rotated;
                }
                else
                {
                    if (pin.Pending != key || pin.Reason != reason)
                    {
                        pin.Pending = key;
                        pin.Reason = reason;
                        changed = true;
                    }
                    check = KeyCheck.Changed;
                }
            }
            if (changed) pin.WriteInto(entry);

            return changed;
        }

        /// <summary>
        /// The former key kept, bounded: until min(<paramref name="announcedUntil"/>, now + the cap).
        /// Without an announced end (0), or one already past, no former key is kept.
        /// </summary>
        private static void SetPrevious(Pin pin, string former, long announcedUntil, DateTimeOffset now)
        {
            long until = Math.Min(announcedUntil, now.Add(PreviousKeyCap).ToUnixTimeSeconds());
            if (announcedUntil <= 0 || Decode(former) == null || until <= now.ToUnixTimeSeconds())
            {
                pin.Previous = null;
                pin.PreviousUntil = 0;

                return;
            }
            pin.Previous = Normal(former);
            pin.PreviousUntil = until;
        }

        /// <summary>A former key past its end (or kept without one) dropped; true when there was one.</summary>
        private static bool DropExpiredPrevious(Pin pin, DateTimeOffset now)
        {
            if (pin.Previous == null && pin.PreviousUntil == 0 || pin.PreviousOpens(now)) return false;
            pin.Previous = null;
            pin.PreviousUntil = 0;

            return true;
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

        /// <summary>
        /// The user accepts the pending key: pinned, and the store's licences trusted again. When
        /// the hub that announced it (<paramref name="announced"/>) names the key pinned until now
        /// as its previousKey with a previousKeyValidUntil, that key keeps opening its licences until
        /// then (bounded as after a rotation).
        /// </summary>
        public void Accept(string store, HubLicensing announced = null)
        {
            DateTimeOffset now = Clock();
            Change(root => Edit(root, store, pin =>
            {
                if (pin.Pending == null) return false;
                string former = pin.Key;
                pin.Key = pin.Pending;
                pin.Pending = pin.Reason = null;
                bool handedOver = announced != null && Decode(announced.PublicKey) != null && Normal(announced.PublicKey) == pin.Key
                    && Decode(announced.PreviousKey) != null && Decode(former) != null && Normal(announced.PreviousKey) == Normal(former);
                SetPrevious(pin, former, handedOver ? announced.PreviousKeyValidUntil : 0, now);

                return true;
            }));
        }

        /// <summary>The user refuses the pending key: the pinned key stays the trusted one, and that key does not ask again.</summary>
        public void Refuse(string store)
        {
            Change(root => Edit(root, store, pin =>
            {
                if (pin.Pending == null) return false;
                if (!pin.Refused.Contains(pin.Pending)) pin.Refused.Add(pin.Pending);
                pin.Pending = pin.Reason = null;

                return true;
            }));
        }

        /// <summary>The store's pin dropped: its next key is pinned again on first use.</summary>
        public void Forget(string store)
        {
            Change(root => root.Remove(store ?? string.Empty));
        }

        /// <summary>The store's former key dropped once past its end.</summary>
        public void DropExpired(string store)
        {
            DateTimeOffset now = Clock();
            Change(root => Edit(root, store, pin => DropExpiredPrevious(pin, now)));
        }

        /// <summary>Nothing left to write: since 1.5.0 each change is written at once. Kept for callers of 1.4.0.</summary>
        public void Save()
        {
        }

        /// <summary>The file read again, changed, written whole (sorted by store) when the change says so - or over a damaged file, kept aside first.</summary>
        private void Change(Func<Dictionary<string, object>, bool> apply)
        {
            Dictionary<string, object> root = Tree(true);
            try
            {
                bool changed = apply(root);
                if (file != null && (changed || file.Damaged)) file.Write(SharedFile.Sorted(root));
            }
            catch (Exception)
            {
                file?.Forget();
                throw;
            }
        }

        private Dictionary<string, object> Tree(bool fresh)
        {
            if (file == null) return memory;
            Dictionary<string, object> tree;
            try
            {
                tree = file.Read(fresh);
            }
            catch (System.IO.IOException) when (!fresh)
            {
                // Held by another app longer than the attempts last: shown as unknown this time; a
                // decision reads it again and says why it cannot.
                return new Dictionary<string, object>();
            }
            if (file.Damaged && !keptAside)
            {
                // A damaged file is kept aside (keys.json.damaged) before anything replaces it;
                // its stores are pinned again on their next reading.
                file.KeepAside();
                keptAside = true;
            }
            else if (!file.Damaged)
            {
                keptAside = false;
            }

            return tree;
        }

        /// <summary>A store's pin changed by <paramref name="apply"/> (which says whether it did), written back into its entry.</summary>
        private static bool Edit(Dictionary<string, object> root, string store, Func<Pin, bool> apply)
        {
            if (!(Json.Get(root, store ?? string.Empty) is Dictionary<string, object> entry)) return false;
            Pin pin = Pin.Of(entry);
            if (!apply(pin)) return false;
            pin.WriteInto(entry);

            return true;
        }

        private static string Normal(string key)
        {
            byte[] bytes = Decode(key);

            return bytes == null ? null : Convert.ToBase64String(bytes);
        }
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
    /// (null while a change waits for the user), a licence token checked against it - bound to the
    /// registry and the package it opens -, and the licences the user keeps.
    /// </summary>
    public static class StoreTrust
    {
        private static KeyPins pins;
        private static LicenceStore licences;

        /// <summary>The user's pins (the file is read again before each decision).</summary>
        public static KeyPins Pins => pins ??= new KeyPins(KeyPins.DefaultPath, ForgeSeal.Verifier);

        /// <summary>The licences the user keeps for packages of stores that sign them offline.</summary>
        public static LicenceStore Licences => licences ??= new LicenceStore(LicenceStore.DefaultPath);

        /// <summary>The pins and licences opened again (Forge Seal added to or removed from the project, say).</summary>
        public static void Reload()
        {
            pins = null;
            licences = null;
        }

        public static string TrustedKey(string store) => Pins.TrustedKey(store);

        /// <summary>This machine, as a licence names it (hashed): Unity's device identifier, as Forge Seal's LicenseClient uses.</summary>
        public static string MachineIdentifier => UnityEngine.SystemInfo.deviceUniqueIdentifier;

        /// <summary>"This machine's licence id" (64 lowercase hex): the hash <see cref="VerifyLicence(string, string, string, string, out string)"/> compares with a licence's machine, for a seller who binds one to this machine.</summary>
        public static string MachineLicenceId => LicenceCheck.MachineLicenceId(MachineIdentifier);

        /// <summary>
        /// A licence token of that store for <paramref name="package"/> installed from
        /// <paramref name="registry"/>, checked with the store's pinned key (or, within its bound,
        /// the key it had before a rotation) by Forge Seal: genuine, of format 2, naming that
        /// registry and that package, this machine's when it names one, not run out. The token
        /// read, or null with the reason in plain words.
        /// </summary>
        public static LicenceToken VerifyLicence(string store, string token, string registry, string package, out string reason)
        {
            try
            {
                return LicenceCheck.Verify(Pins, store, token, registry, package, ForgeSeal.Verifier, MachineIdentifier, DateTimeOffset.UtcNow, out reason);
            }
            catch (Exception e) when (e is System.IO.IOException || e is UnauthorizedAccessException)
            {
                reason = "the pinned keys cannot be read now (" + e.Message + ").";

                return null;
            }
        }

        /// <summary>
        /// The signature only, with the store's pinned key (or its bounded previous key): binds
        /// nothing - a token of another registry or package passes. Seal's LicensePayload, or null
        /// with the reason.
        /// </summary>
        [Obsolete("Binds the licence to nothing: use VerifyLicence(store, token, registry, package, out reason).")]
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
            KeyPins.Pin pin = Pins.Read(store);
            if (payload == null && pin != null && pin.PreviousOpens(DateTimeOffset.UtcNow)) payload = ForgeSeal.VerifyToken(token, pin.Previous);
            reason = payload == null ? "the licence's signature does not verify with the store's key" : null;

            return payload;
        }
    }
}
