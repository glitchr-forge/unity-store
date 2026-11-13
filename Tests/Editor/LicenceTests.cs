using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;

namespace Glitchr.Store.Tests
{
    /// <summary>
    /// The licence chain on temporary files, never the user's: keys.json shared with Forge Hub
    /// (read again before each decision, the other app's fields and stores kept, renamed into
    /// place, a file caught mid-write read again), the key a store had before a rotation (kept,
    /// bounded), licences bound to their registry and package (a Forge's format 2 token, built here
    /// in its exact shape with a test key), and a static hub's offline licences (omnihub licence
    /// sign's own output) checked and kept per user. Signatures are real Ed25519: the tests'
    /// own verification, and Forge Seal's when the project has it.
    /// </summary>
    public class LicenceTests
    {
        private const string Store = "manifest:https://forge.example.com/upm/-/hub.json";
        private const string Registry = "https://forge.example.com/upm";
        private const string Package = "com.example.tool";
        private static readonly byte[] OldSeed = TestSigner.Seed("store key, before");
        private static readonly byte[] NewSeed = TestSigner.Seed("store key, after");
        private static readonly string OldKey = TestSigner.PublicKeyBase64(OldSeed);
        private static readonly string NewKey = TestSigner.PublicKeyBase64(NewSeed);
        private static readonly DateTimeOffset T0 = DateTimeOffset.FromUnixTimeSeconds(1792972800); // 2026-10-26
        private static readonly Func<byte[], byte[], byte[], bool> Ed25519 = TestSigner.Verify;

        // omnihub licence sign (PHP 8.5, composer:2 in Docker, no network), a throwaway key whose
        // private half was never kept. any: {"v":2,"registry":"https://example.github.io/hub",
        // "package":"com.example.tool","licensee":"Ada Example <ada@example.org>","issued_at":1792972800};
        // bound: the same for "ada@example.org", machine = SHA-256("machine-0001"), valid_until
        // 1795564800, updates_until 1824508800.
        private const string HubStore = "manifest:https://example.github.io/hub/-/hub.json";
        private const string HubRegistry = "https://example.github.io/hub";
        private const string HubKey = "wI9lKrPyiFJOD1O7ZjUnVYL4XlfqM5idJ9Di3xjngvA=";
        private const string AnyMachine = "eyJ2IjoyLCJyZWdpc3RyeSI6Imh0dHBzOi8vZXhhbXBsZS5naXRodWIuaW8vaHViIiwicGFja2FnZSI6ImNvbS5leGFtcGxlLnRvb2wiLCJsaWNlbnNlZSI6IkFkYSBFeGFtcGxlIDxhZGFAZXhhbXBsZS5vcmc-IiwiaXNzdWVkX2F0IjoxNzkyOTcyODAwfQ.dvqH85eBu4AAuPaAddcP5z0R6EO8dMKcmls79DJ9MOJaejFcrX2euD0nmc5PtgMfaj-lxBz6GStsLH-X3IAQAQ";
        private const string OneMachine = "eyJ2IjoyLCJyZWdpc3RyeSI6Imh0dHBzOi8vZXhhbXBsZS5naXRodWIuaW8vaHViIiwicGFja2FnZSI6ImNvbS5leGFtcGxlLnRvb2wiLCJsaWNlbnNlZSI6ImFkYUBleGFtcGxlLm9yZyIsIm1hY2hpbmUiOiIzMTdmMDZmNzdiZjcyZTAyY2I2ODJjMzY1ZmQ1ZWIwNDAwYzY0Nzk1MDY1MjhkNzQ1ZjVlYzNmMjNiMDRjYWExIiwiaXNzdWVkX2F0IjoxNzkyOTcyODAwLCJ2YWxpZF91bnRpbCI6MTc5NTU2NDgwMCwidXBkYXRlc191bnRpbCI6MTgyNDUwODgwMH0.BsY86NMALtxcbjWAPZToGg0LecvXF91KrAvjdTHYl1HfE6DQKndQWjJ7U8g6BXQxcjtAyCmrWSvDfil2tw_vCQ";

        private string directory;
        private string path;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "forge-store-licences-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            path = Path.Combine(directory, "keys.json");
        }

        [TearDown]
        public void TearDown() => Directory.Delete(directory, true);

        private static HubLicensing Announce(string key, string previous = null, string rotation = null, long until = 0, string store = Store)
        {
            return new HubLicensing { Store = store, StoreName = "Example", PublicKey = key, PreviousKey = previous, Rotation = rotation, PreviousKeyValidUntil = until };
        }

        private static string RotationBy(byte[] seed, string newKey) => Convert.ToBase64String(TestSigner.Sign(seed, HubLicensing.RotationMessage(newKey)));

        /// <summary>A token in omnibase/forge's LicenseToken::issue() shape: same fields, same order, nulls kept.</summary>
        private static string ForgeToken(byte[] seed, string registry = Registry, string package = Package, string machine = "machine-0001", int v = 2, long validUntil = 1795564800)
        {
            string bound = v >= 2 ? "\"registry\":\"" + registry + "\",\"package\":" + (package == null ? "null" : "\"" + package + "\"") + "," : string.Empty;
            string json = "{\"v\":" + v + ",\"license\":42,\"software\":\"example-tool\"," + bound + "\"email\":\"ada@example.org\",\"machine\":\""
                + LicenceCheck.MachineHash(machine) + "\",\"issued_at\":1792972800,\"valid_until\":" + validUntil + ",\"license_expires\":null,\"updates_until\":null}";

            return TestSigner.Token(seed, json);
        }

        private static LicenceToken Check(KeyPins pins, string token, out string reason, string registry = Registry, string package = Package, string machine = "machine-0001", DateTimeOffset? now = null, string store = Store, Func<byte[], byte[], byte[], bool> verify = null)
        {
            return LicenceCheck.Verify(pins, store, token, registry, package, verify ?? Ed25519, machine, now ?? T0.AddDays(1), out reason);
        }

        // 1. keys.json, shared with Forge Hub.

        [Test]
        public void ADecisionTakenInForgeHubIsSeenAtOnceAndNeverUndone()
        {
            var pins = new KeyPins(path, null) { Clock = () => T0 };
            Assert.AreEqual(KeyCheck.Pinned, pins.Observe(Announce(OldKey)));
            Assert.AreEqual(KeyCheck.Changed, pins.Observe(Announce(NewKey)));
            Assert.IsNull(pins.TrustedKey(Store));

            // Forge Hub accepts the new key meanwhile, pins a store of its own, and writes fields it owns.
            File.WriteAllText(path, "{\n  \"" + Store + "\": {\"key\": \"" + NewKey + "\", \"name\": \"Example\", \"hubNote\": \"kept\"},\n  \"git:https://github.com/example-org/unity-base\": {\"key\": \"" + OldKey + "\", \"name\": \"Base\"},\n  \"zz-later\": [1, 2]\n}\n");

            Assert.AreEqual(NewKey, pins.TrustedKey(Store), "read again for the decision: no reload needed");
            Assert.IsNull(pins.Read(Store).Pending);
            Assert.AreEqual(KeyCheck.Same, pins.Observe(Announce(NewKey)), "the stale copy does not put the pending key back");
            pins.Refuse(Store);
            Assert.AreEqual(NewKey, pins.TrustedKey(Store), "nothing to refuse any more");
            Assert.AreEqual(KeyCheck.Pinned, pins.Observe(Announce(OldKey, store: "package:com.example.base")));
            pins.Save();

            object file = Json.Parse(File.ReadAllText(path));
            Assert.AreEqual("kept", Json.Get(file, Store, "hubNote"), "the other app's field kept");
            Assert.AreEqual(OldKey, Json.Get(file, "git:https://github.com/example-org/unity-base", "key"), "the other app's store kept");
            Assert.AreEqual(2, Json.Array(Json.Get(file, "zz-later")).Count, "what neither knows kept");
            CollectionAssert.AreEqual(new[] { "git:https://github.com/example-org/unity-base", Store, "package:com.example.base", "zz-later" }, Json.Object(file).Keys.ToArray(), "stores sorted, as both apps write them");
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp"), "written through a temporary file renamed over it");
            Assert.IsFalse(File.Exists(path + ".damaged"));
        }

        [Test]
        public void AFileCaughtMidWriteIsReadAgainBeforeItCountsAsDamaged()
        {
            var pins = new KeyPins(path, null);
            pins.Observe(Announce(OldKey));
            string whole = File.ReadAllText(path);
            int reads = 0, pauses = 0;
            pins.File.ReadText = p => ++reads == 1 ? whole.Substring(0, whole.Length / 2) : File.ReadAllText(p);
            pins.File.Pause = _ => pauses++;
            Assert.AreEqual(OldKey, pins.TrustedKey(Store), "the second reading finds the whole file");
            Assert.AreEqual(1, pauses);
            Assert.IsFalse(File.Exists(path + ".damaged"));

            pins.File.ReadText = p => File.Exists(p) ? File.ReadAllText(p) : null;
            File.WriteAllText(path, "{ damaged");
            pauses = 0;
            Assert.IsNull(pins.TrustedKey(Store), "damaged after every attempt: read as empty");
            Assert.AreEqual(SharedFile.Attempts - 1, pauses);
            Assert.AreEqual("{ damaged", File.ReadAllText(path + ".damaged"), "kept aside before anything replaces it");
            Assert.AreEqual(KeyCheck.Pinned, pins.Observe(Announce(NewKey)), "pinned again on its next reading");
            Assert.AreEqual(NewKey, Json.Get(Json.Parse(File.ReadAllText(path)), Store, "key"));
        }

        [Test]
        public void TheFileForgeHubWritesIsReadAndWrittenInItsShape()
        {
            // unity-hub core/tests/fixtures/forge-store-trust.json, "keysFile" (Forge Hub 1.2.0), with a field of a later version.
            const string old = "Y/T6mi9iPOI6Zz76FZg2g9W9fQWvQvFKE55o6s75pY4=", next = "5Wr6MvD/0ktioPo2QsSyiHKTCPeraqZf94+MwEYZ6cc=", other = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
            File.WriteAllText(path, "{\n  \"manifest:https://hub.example.com/hub.json\": {\n    \"key\": \"" + other + "\",\n    \"name\": \"Example\",\n    \"pending\": \"" + next + "\",\n    \"reason\": \"the store gives no signed rotation\",\n    \"refused\": [\n      \"" + old + "\"\n    ],\n    \"seenAt\": 1792972800\n  },\n  \"package:com.example.base\": {\n    \"key\": \"" + old + "\",\n    \"name\": \"Example base\"\n  }\n}\n");
            var pins = new KeyPins(path, null);
            KeyPins.Pin pin = pins.Get("manifest:https://hub.example.com/hub.json");
            Assert.AreEqual(other, pin.Key);
            Assert.AreEqual(next, pin.Pending);
            CollectionAssert.AreEqual(new[] { old }, pin.Refused);
            Assert.AreEqual("5d:04:71:a5:b0:f8:24:da", KeyPins.Fingerprint(old), "the fixture's own fingerprints");
            Assert.AreEqual("6c:1b:a6:a8:dc:b1:60:6f", KeyPins.Fingerprint(next));
            Assert.AreEqual("ae:21:6c:2e:f5:24:7a:37", KeyPins.Fingerprint(other));
            Assert.AreEqual(old, pins.TrustedKey("package:com.example.base"));

            pins.Accept("manifest:https://hub.example.com/hub.json");
            pins.Forget("package:com.example.base");
            object file = Json.Parse(File.ReadAllText(path));
            var entry = Json.Object(Json.Get(file, "manifest:https://hub.example.com/hub.json"));
            Assert.AreEqual(next, entry["key"]);
            Assert.AreEqual("Example", entry["name"]);
            Assert.IsFalse(entry.ContainsKey("pending"));
            Assert.IsFalse(entry.ContainsKey("reason"));
            Assert.IsFalse(entry.ContainsKey("previous"), "a plain change hands nothing over");
            Assert.AreEqual(1, Json.Array(entry["refused"]).Count);
            Assert.AreEqual(1792972800d, entry["seenAt"], "a later version's field kept");
            Assert.IsNull(Json.Get(file, "package:com.example.base"));
            StringAssert.StartsWith("{\n  \"manifest:", File.ReadAllText(path), "two-space indent, as both apps write");
        }

        // 2. The previous key, after a rotation.

        [Test]
        public void LicencesSignedBeforeARotationOpenUntilThePreviousKeysEnd()
        {
            DateTimeOffset now = T0;
            var pins = new KeyPins(path, Ed25519) { Clock = () => now };
            pins.Observe(Announce(OldKey));
            string before = ForgeToken(OldSeed);
            Assert.IsNotNull(Check(pins, before, out string reason), reason);

            Assert.AreEqual(KeyCheck.Rotated, pins.Observe(Announce(NewKey, OldKey, RotationBy(OldSeed, NewKey), T0.AddDays(10).ToUnixTimeSeconds())));
            KeyPins.Pin pin = pins.Read(Store);
            Assert.AreEqual(NewKey, pin.Key);
            Assert.AreEqual(OldKey, pin.Previous);
            Assert.AreEqual(T0.AddDays(10).ToUnixTimeSeconds(), pin.PreviousUntil);

            LicenceToken read = Check(pins, before, out reason, now: T0.AddDays(9));
            Assert.IsNotNull(read, reason);
            Assert.IsTrue(read.ByPreviousKey);
            Assert.IsFalse(Check(pins, ForgeToken(NewSeed), out reason).ByPreviousKey, "the new key's own");

            now = T0.AddDays(10);
            Assert.IsNull(Check(pins, before, out reason, now: now));
            StringAssert.Contains("previous key, which no longer opens licences since 2026-11-05", reason);
            Assert.IsNull(pins.Read(Store).Previous, "dropped once past");
            Assert.IsFalse(File.ReadAllText(path).Contains("previous"));
            StringAssert.Contains("does not verify", Check(pins, before, out reason, now: now) == null ? reason : null);
        }

        [Test]
        public void ThePreviousKeyIsBoundedAndNeverKeptWithoutItsEnd()
        {
            var pins = new KeyPins(path, Ed25519) { Clock = () => T0 };
            pins.Observe(Announce(OldKey));
            pins.Observe(Announce(NewKey, OldKey, RotationBy(OldSeed, NewKey), T0.AddDays(400).ToUnixTimeSeconds()));
            Assert.AreEqual(T0.Add(KeyPins.PreviousKeyCap).ToUnixTimeSeconds(), pins.Read(Store).PreviousUntil, "a far date is capped at the rotation's sighting + 30 days");

            pins.Forget(Store);
            pins.Observe(Announce(OldKey));
            Assert.AreEqual(KeyCheck.Rotated, pins.Observe(Announce(NewKey, OldKey, RotationBy(OldSeed, NewKey))));
            Assert.IsNull(pins.Read(Store).Previous, "no previousKeyValidUntil: the previous key is not kept");
            Assert.IsNull(Check(pins, ForgeToken(OldSeed), out string reason));
            StringAssert.Contains("does not verify with the store's key", reason);

            pins.Forget(Store);
            pins.Observe(Announce(OldKey));
            pins.Observe(Announce(NewKey, OldKey, RotationBy(OldSeed, NewKey), T0.AddDays(-1).ToUnixTimeSeconds()));
            Assert.IsNull(pins.Read(Store).Previous, "an end already past keeps nothing");
        }

        [Test]
        public void AnAcceptedChangeKeepsThePreviousKeyOnlyWhenTheHubHandsItOver()
        {
            var pins = new KeyPins(path, null) { Clock = () => T0 };
            pins.Observe(Announce(OldKey));
            HubLicensing handover = Announce(NewKey, OldKey, RotationBy(OldSeed, NewKey), T0.AddDays(5).ToUnixTimeSeconds());
            Assert.AreEqual(KeyCheck.Changed, pins.Observe(handover), "without Forge Seal the rotation waits for the user");
            pins.Accept(Store, handover);
            Assert.AreEqual(NewKey, pins.TrustedKey(Store));
            Assert.AreEqual(OldKey, pins.Read(Store).Previous);
            Assert.AreEqual(T0.AddDays(5).ToUnixTimeSeconds(), pins.Read(Store).PreviousUntil);

            pins.Forget(Store);
            pins.Observe(Announce(OldKey));
            HubLicensing plain = Announce(NewKey, until: T0.AddDays(5).ToUnixTimeSeconds());
            pins.Observe(plain);
            pins.Accept(Store, plain);
            Assert.IsNull(pins.Read(Store).Previous, "a hub that does not name the pinned key as previous hands nothing over");

            pins.Forget(Store);
            pins.Observe(Announce(OldKey));
            pins.Observe(handover);
            pins.Accept(Store);
            Assert.IsNull(pins.Read(Store).Previous, "nothing announced at hand: nothing kept");
        }

        [Test]
        public void ForgeSealChecksTheRotationAndTheLicencesWhenTheProjectHasIt()
        {
            if (!ForgeSeal.Available) Assert.Ignore("io.glitchr.licensing is not in this project.");
            var pins = new KeyPins(path, ForgeSeal.Verifier) { Clock = () => T0 };
            pins.Observe(Announce(OldKey));
            Assert.AreEqual(KeyCheck.Rotated, pins.Observe(Announce(NewKey, OldKey, RotationBy(OldSeed, NewKey), T0.AddDays(10).ToUnixTimeSeconds())));
            Assert.IsTrue(Check(pins, ForgeToken(OldSeed), out string reason, verify: ForgeSeal.Verifier).ByPreviousKey, reason);
            Assert.IsNotNull(Check(pins, ForgeToken(NewSeed), out reason, verify: ForgeSeal.Verifier), reason);
            Assert.IsNull(Check(pins, ForgeToken(TestSigner.Seed("someone else")), out reason, verify: ForgeSeal.Verifier));

            pins.Forget(HubStore);
            pins.Observe(Announce(HubKey, store: HubStore));
            Assert.IsNotNull(Check(pins, AnyMachine, out reason, HubRegistry, store: HubStore, verify: ForgeSeal.Verifier), "omnihub's signature, verified by Forge Seal: " + reason);
        }

        // 3. Bound to the registry and the package.

        [Test]
        public void AForgeLicenceOpensOnlyItsOwnRegistrysPackage()
        {
            var pins = new KeyPins(path, Ed25519) { Clock = () => T0 };
            pins.Observe(Announce(NewKey));
            string token = ForgeToken(NewSeed);
            LicenceToken read = Check(pins, token, out string reason);
            Assert.IsNotNull(read, reason);
            Assert.AreEqual(2, read.Version);
            Assert.AreEqual("ada@example.org", read.Holder);
            Assert.IsFalse(read.AnyMachine);
            Assert.IsNotNull(Check(pins, token, out reason, "HTTPS://Forge.Example.COM/upm/"), "a final slash and the host's case aside");

            Assert.IsNull(Check(pins, token, out reason, "https://other.example.com/upm"));
            StringAssert.Contains("the licence is another store's: it names https://forge.example.com/upm", reason);
            Assert.IsNull(Check(pins, token, out reason, "https://forge.example.com/UPM"), "the path keeps its case");
            Assert.IsNull(Check(pins, token, out reason, package: "com.example.other"));
            StringAssert.Contains("the licence is for com.example.tool, not com.example.other", reason);
            Assert.IsNull(Check(pins, ForgeToken(NewSeed, package: null), out reason), "a game's licence opens no package");
            StringAssert.Contains("a game or an application", reason);
            Assert.IsNull(Check(pins, token, out reason, machine: "machine-0002"));
            StringAssert.Contains("another machine", reason);
            Assert.IsNull(Check(pins, token, out reason, now: DateTimeOffset.FromUnixTimeSeconds(1795564800)));
            StringAssert.Contains("ran out on 2026-11-25", reason);
        }

        [Test]
        public void AFormat1LicenceIsRefusedWithAnInvitationToActivateAgain()
        {
            var pins = new KeyPins(path, Ed25519) { Clock = () => T0 };
            pins.Observe(Announce(NewKey));
            Assert.IsNull(Check(pins, ForgeToken(NewSeed, v: 1), out string reason));
            StringAssert.Contains("format 1", reason);
            StringAssert.Contains("Activate it again online", reason);
        }

        [Test]
        public void NothingIsTrustedWithoutAPinnedKeyAFormOrForgeSeal()
        {
            var pins = new KeyPins(path, Ed25519) { Clock = () => T0 };
            string token = ForgeToken(NewSeed);
            Assert.IsNull(Check(pins, token, out string reason));
            StringAssert.Contains("no licensing key is pinned", reason);
            pins.Observe(Announce(OldKey));
            pins.Observe(Announce(NewKey));
            Assert.IsNull(Check(pins, token, out reason), "the key changed without a rotation: waiting");
            StringAssert.Contains("new licensing key", reason);
            pins.Accept(Store);
            Assert.IsNull(Check(pins, "not a token", out reason));
            StringAssert.Contains("not a licence token", reason);
            Assert.IsNull(Check(pins, "e30." + TestSigner.Base64Url(new byte[64]), out reason), "an empty payload with a signature of zeros");
            StringAssert.Contains("does not verify", reason);
            Assert.IsNull(LicenceCheck.Verify(pins, Store, token, Registry, Package, null, "machine-0001", T0, out reason), "no Forge Seal to check it");
            StringAssert.Contains("Forge Seal (io.glitchr.licensing) is not in the project", reason);
            Assert.IsNotNull(Check(pins, token, out reason), reason);
        }

        [Test]
        public void ThePackagesRegistryIsTheOneTheProjectInstalledItFrom()
        {
            const string manifest = "{\"scopedRegistries\":[{\"name\":\"A\",\"url\":\"https://a.example.com/upm/\",\"scopes\":[\"com.example\"]},{\"name\":\"B\",\"url\":\"https://b.example.com/upm\",\"scopes\":[\"com.example.tool\"]}],"
                + "\"dependencies\":{\"com.example.tool\":\"1.0.0\",\"com.example.toolbox\":\"1.0.0\",\"com.example.git\":\"https://github.com/example-org/unity-git.git#v1.0.0\"}}";
            Assert.AreEqual("https://b.example.com/upm", LicenceCheck.InstalledRegistry(manifest, null, "com.example.tool"), "the closest scope");
            Assert.AreEqual("https://a.example.com/upm", LicenceCheck.InstalledRegistry(manifest, null, "com.example.toolbox"), "a scope covers the names under it, not a longer word");
            Assert.IsNull(LicenceCheck.InstalledRegistry(manifest, null, "com.example.git"), "a Git package has no registry");
            Assert.IsNull(LicenceCheck.InstalledRegistry(manifest, null, "org.other.thing"), "Unity's own registry is no store");
            const string lockFile = "{\"dependencies\":{\"com.example.tool\":{\"version\":\"1.0.0\",\"depth\":0,\"source\":\"registry\",\"dependencies\":{},\"url\":\"https://c.example.com/upm/\"}}}";
            Assert.AreEqual("https://c.example.com/upm", LicenceCheck.InstalledRegistry(manifest, lockFile, "com.example.tool"), "the lock says where it came from");

            object hub = Json.Parse("{\"licensing\":{\"algorithm\":\"Ed25519\",\"publicKey\":\"" + NewKey + "\",\"registry\":\"https://forge.example.com/upm\",\"previousKey\":\"" + OldKey + "\",\"rotation\":\"" + RotationBy(OldSeed, NewKey) + "\",\"previousKeyValidUntil\":1795564800}}");
            HubLicensing licensing = HubLicensing.Of(hub, Store, "Example");
            Assert.AreEqual(Registry, licensing.Registry);
            Assert.AreEqual(1795564800, licensing.PreviousKeyValidUntil);
            Assert.AreEqual(0, HubLicensing.Of(Json.Parse("{\"licensing\":{\"publicKey\":\"" + NewKey + "\"}}"), Store, "Example").PreviousKeyValidUntil);
        }

        // 4. A static hub's offline licences.

        [Test]
        public void AnOfflineLicenceIsCheckedAgainstThePinnedKeyItsRegistryAndItsPackage()
        {
            var pins = new KeyPins(path, Ed25519) { Clock = () => T0 };
            pins.Observe(Announce(HubKey, store: HubStore));

            LicenceToken any = Check(pins, AnyMachine, out string reason, HubRegistry, machine: "a machine its seller never saw", now: DateTimeOffset.FromUnixTimeSeconds(4102444800), store: HubStore);
            Assert.IsNotNull(any, reason);
            Assert.AreEqual("Ada Example <ada@example.org>", any.Holder);
            Assert.IsTrue(any.AnyMachine);
            Assert.AreEqual("no end", any.Validity);

            LicenceToken one = Check(pins, OneMachine, out reason, HubRegistry + "/", store: HubStore);
            Assert.IsNotNull(one, reason);
            Assert.AreEqual("until 2026-11-25", one.Validity);
            Assert.IsNull(Check(pins, OneMachine, out reason, HubRegistry, machine: "machine-0002", store: HubStore), "a machine named and different: refused");
            StringAssert.Contains("another machine", reason);
            Assert.IsNull(Check(pins, OneMachine, out reason, HubRegistry, now: DateTimeOffset.FromUnixTimeSeconds(1795564800), store: HubStore));
            StringAssert.Contains("ran out", reason);

            Assert.IsNull(Check(pins, AnyMachine, out reason, HubRegistry, "com.example.other", store: HubStore));
            StringAssert.Contains("not com.example.other", reason);
            Assert.IsNull(Check(pins, ForgeToken(NewSeed, HubRegistry), out reason, HubRegistry, store: HubStore), "a licence another key signed, even naming this registry");
            StringAssert.Contains("does not verify with the store's key", reason);
            pins.Observe(Announce(NewKey, store: Store));
            Assert.IsNull(Check(pins, AnyMachine, out reason, Registry), "this hub's licence pasted for another store's package");
            StringAssert.Contains("does not verify", reason);
        }

        [Test]
        public void KeptLicencesLiveInTheUsersFolderOneByRegistryAndPackage()
        {
            var store = new LicenceStore(Path.Combine(directory, "licences.json"));
            Assert.IsNull(store.Get(HubRegistry, Package));
            store.Put(HubRegistry + "/", Package, HubStore, " " + AnyMachine + " ", new DateTime(2026, 11, 13, 9, 0, 0, DateTimeKind.Utc));
            LicenceStore.Kept kept = store.Get("HTTPS://example.github.io/hub", Package);
            Assert.AreEqual(AnyMachine, kept.Token, "trimmed");
            Assert.AreEqual(HubRegistry, kept.Registry);
            Assert.AreEqual(HubStore, kept.Store);
            Assert.AreEqual("2026-11-13T09:00:00Z", kept.AddedAt);

            // Another app (or a later version) adds fields; a new paste replaces the token in place.
            File.WriteAllText(store.Path, File.ReadAllText(store.Path).Replace("\"version\": 1", "\"version\": 1,\n  \"later\": true").Replace("\"addedAt\"", "\"note\": \"mine\",\n      \"addedAt\""));
            store.Put(HubRegistry, Package, HubStore, OneMachine);
            store.Put(Registry, Package, Store, ForgeToken(NewSeed));
            Assert.AreEqual(2, store.All().Count, "one per registry and package");
            Assert.AreEqual(OneMachine, store.Get(HubRegistry, Package).Token);
            object file = Json.Parse(File.ReadAllText(store.Path));
            Assert.AreEqual(true, Json.Get(file, "later"));
            Assert.AreEqual("mine", Json.Get(Json.Array(Json.Get(file, "licences"))[0], "note"));

            Assert.IsTrue(store.Remove(HubRegistry, Package));
            Assert.IsFalse(store.Remove(HubRegistry, Package));
            Assert.IsNull(store.Get(HubRegistry, Package));
            Assert.IsNotNull(store.Get(Registry, Package));
            Assert.IsEmpty(Directory.GetFiles(directory, "*.tmp"));

            File.WriteAllText(store.Path, "not json");
            store.File.Pause = _ => { };
            Assert.Throws<FormatException>(() => store.Put(HubRegistry, Package, HubStore, AnyMachine), "a damaged file is never written over");
            Assert.AreEqual("not json", File.ReadAllText(store.Path));

            string user = LicenceStore.DefaultPath;
            StringAssert.EndsWith(Path.Combine("Forge Store", "licences.json"), user);
            Assert.AreEqual(Path.GetDirectoryName(KeyPins.DefaultPath), Path.GetDirectoryName(user), "beside keys.json");
            Assert.IsFalse(Path.GetFullPath(user).StartsWith(Path.GetFullPath(Directory.GetCurrentDirectory()), StringComparison.Ordinal), "never in the project");
        }

        [Test]
        public void TheTestSignerIsRfc8032s()
        {
            byte[] seed = Hex("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60");
            CollectionAssert.AreEqual(Hex("d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"), TestSigner.PublicKey(seed));
            byte[] signature = TestSigner.Sign(seed, new byte[0]);
            CollectionAssert.AreEqual(Hex("e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"), signature);
            Assert.IsTrue(TestSigner.Verify(signature, new byte[0], TestSigner.PublicKey(seed)));
            signature[5] ^= 1;
            Assert.IsFalse(TestSigner.Verify(signature, new byte[0], TestSigner.PublicKey(seed)));
            if (ForgeSeal.Available) Assert.IsTrue(ForgeSeal.Verifier(TestSigner.Sign(seed, Encoding.UTF8.GetBytes("x")), Encoding.UTF8.GetBytes("x"), TestSigner.PublicKey(seed)), "Forge Seal agrees");
        }

        private static byte[] Hex(string hex)
        {
            var bytes = new byte[hex.Length / 2];
            for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(hex.Substring(2 * i, 2), 16);

            return bytes;
        }
    }
}
