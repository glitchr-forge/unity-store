using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Glitchr.Store.Tests
{
    /// <summary>
    /// A store's licensing key pinned on first use, a later change held until the user decides, a
    /// signed rotation passing alone - and the theme's images kept in a bounded cache, images only.
    /// </summary>
    public class TrustTests
    {
        // Throwaway Ed25519 keys made for these tests (their private halves were never kept): the
        // rotation is OLD's signature of "forge-key-rotation:v1:" + NEW.
        private const string Old = "Y/T6mi9iPOI6Zz76FZg2g9W9fQWvQvFKE55o6s75pY4=";
        private const string New = "5Wr6MvD/0ktioPo2QsSyiHKTCPeraqZf94+MwEYZ6cc=";
        private const string Rotation = "Tb6sYPiqPaiKVkvqPJWI4L6D1/ZqD0OqtbHuRnUaoUULuJ2BAoOZYsxfJZIf7CSlthewgR6svrxxdbVXMEtxBA==";
        private const string Other = "AQIDBAUGBwgJCgsMDQ4PEBESExQVFhcYGRobHB0eHyA=";
        private const string Store = "manifest:https://hub.example.com/hub.json";

        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "forge-store-trust-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(directory, true);

        private static HubLicensing Announce(string key, string previous = null, string rotation = null)
        {
            return new HubLicensing { Store = Store, StoreName = "Example", PublicKey = key, PreviousKey = previous, Rotation = rotation };
        }

        [Test]
        public void AHubsLicensingBlockIsReadAsData()
        {
            object hub = Json.Parse("{\"name\":\"Example\",\"licensing\":{\"publicKey\":\" " + New + " \",\"previousKey\":\"" + Old + "\",\"rotation\":\"" + Rotation + "\"}}");
            HubLicensing licensing = HubLicensing.Of(hub, Store, "Example");
            Assert.AreEqual(New, licensing.PublicKey);
            Assert.AreEqual(Old, licensing.PreviousKey);
            Assert.AreEqual(Rotation, licensing.Rotation);
            Assert.IsNull(HubLicensing.Of(Json.Parse("{\"name\":\"Free\"}"), Store, "Free"), "a hub that sells nothing has no key");

            Assert.AreEqual(Store, HubCatalogReader.StoreId(HubSource.Manifest, "https://Hub.Example.com/hub.json/"));
            Assert.AreEqual("git:https://github.com/example-org/unity-base", HubCatalogReader.StoreId(HubSource.Git, "https://github.com/Example-Org/unity-base.git?path=/Pkg#v2.0.0"), "a new tag keeps the store's pin");
            Assert.AreEqual("package:com.example.base", HubCatalogReader.StoreId(HubSource.Package, "com.example.base"));
        }

        [Test]
        public void TheFirstKeyIsPinnedAndALaterChangeWaitsForTheUser()
        {
            string path = Path.Combine(directory, "keys.json");
            var pins = new KeyPins(path, null);
            Assert.IsNull(pins.TrustedKey(Store));
            Assert.AreEqual(KeyCheck.Invalid, pins.Observe(Announce("not a key")));
            Assert.AreEqual(KeyCheck.Invalid, pins.Observe(Announce(Convert.ToBase64String(new byte[16]))), "16 bytes is no Ed25519 key");
            Assert.AreEqual(KeyCheck.Pinned, pins.Observe(Announce(Old)));
            Assert.AreEqual(Old, pins.TrustedKey(Store));
            Assert.AreEqual(KeyCheck.Same, pins.Observe(Announce(Old)));
            pins.Save();

            pins = new KeyPins(path, null);
            Assert.AreEqual(Old, pins.TrustedKey(Store), "kept in the user's file");
            Assert.AreEqual(KeyCheck.Changed, pins.Observe(Announce(Other)));
            Assert.IsNull(pins.TrustedKey(Store), "no licence of the store is trusted while the change waits");
            StringAssert.Contains("no signed rotation", pins.Get(Store).Reason);
            Assert.AreEqual(KeyCheck.Changed, pins.Observe(Announce(Other)), "asked again until decided");
            pins.Save();
            Assert.AreEqual(Other, new KeyPins(path, null).Get(Store).Pending, "the pending change survives a restart");

            pins.Accept(Store);
            Assert.AreEqual(Other, pins.TrustedKey(Store), "accepted: the new key is pinned");
            Assert.IsNull(pins.Get(Store).Pending);

            Assert.AreEqual(KeyCheck.Changed, pins.Observe(Announce(Old)));
            pins.Refuse(Store);
            Assert.AreEqual(Other, pins.TrustedKey(Store), "refused: the pinned key stays the trusted one");
            Assert.AreEqual(KeyCheck.Refused, pins.Observe(Announce(Old)), "a refused key does not ask again");
            Assert.AreEqual(Other, pins.TrustedKey(Store));

            Assert.AreEqual(KeyCheck.Changed, pins.Observe(Announce(New)));
            Assert.AreEqual(KeyCheck.Same, pins.Observe(Announce(Other)), "the hub serves the pinned key again: the change is withdrawn");
            Assert.AreEqual(Other, pins.TrustedKey(Store));

            pins.Forget(Store);
            Assert.AreEqual(KeyCheck.Pinned, pins.Observe(Announce(New)), "forgotten: pinned again on first use");
            Assert.AreEqual(KeyPins.Fingerprint(New), KeyPins.Fingerprint(" " + New));
            Assert.AreNotEqual(KeyPins.Fingerprint(New), KeyPins.Fingerprint(Old));
        }

        [Test]
        public void ARotationThePinnedKeySignedPassesWithoutAPrompt()
        {
            var calls = new List<string>();
            Func<byte[], byte[], byte[], bool> verify = (signature, message, key) =>
            {
                calls.Add(Convert.ToBase64String(key));

                return Convert.ToBase64String(signature) == Rotation && System.Text.Encoding.UTF8.GetString(message) == HubLicensing.RotationPrefix + New;
            };
            var pins = new KeyPins(Path.Combine(directory, "keys.json"), verify);
            pins.Observe(Announce(Old));

            Assert.AreEqual(KeyCheck.Changed, pins.Observe(Announce(New, Other, Rotation)), "a rotation from another key than the pinned one");
            Assert.AreEqual(KeyCheck.Changed, pins.Observe(Announce(New, Old, Convert.ToBase64String(new byte[64]))), "a signature that does not verify");
            StringAssert.Contains("does not verify", pins.Get(Store).Reason);
            Assert.AreEqual(KeyCheck.Rotated, pins.Observe(Announce(New, Old, Rotation)));
            Assert.AreEqual(New, pins.TrustedKey(Store));
            Assert.IsNull(pins.Get(Store).Pending, "the change that waited is settled by the rotation");
            CollectionAssert.AreEqual(new[] { Old, Old }, calls, "checked with the pinned key, never with the announced one");

            var without = new KeyPins(null, null);
            without.Observe(Announce(Old));
            Assert.AreEqual(KeyCheck.Changed, without.Observe(Announce(New, Old, Rotation)), "without Forge Seal to check it, the user decides");
            StringAssert.Contains("io.glitchr.licensing", without.Get(Store).Reason);
        }

        [Test]
        public void ForgeSealChecksARealRotationWhenTheProjectHasIt()
        {
            if (!ForgeSeal.Available) Assert.Ignore("io.glitchr.licensing is not in this project.");
            var pins = new KeyPins(null, ForgeSeal.Verifier);
            Assert.IsTrue(pins.CanVerifyRotations);
            pins.Observe(Announce(Old));
            Assert.AreEqual(KeyCheck.Changed, pins.Observe(Announce(New, Old, Convert.ToBase64String(new byte[64]))));
            Assert.AreEqual(KeyCheck.Rotated, pins.Observe(Announce(New, Old, Rotation)), "OpenSSL's Ed25519 signature, verified by Forge Seal");
            Assert.AreEqual(New, pins.TrustedKey(Store));
            Assert.IsNull(ForgeSeal.VerifyToken("not.a.token", New));
        }

        [Test]
        public void ADamagedFileIsKeptAside()
        {
            string path = Path.Combine(directory, "keys.json");
            File.WriteAllText(path, "{ damaged");
            var pins = new KeyPins(path, null);
            Assert.IsNull(pins.Get(Store));
            Assert.AreEqual("{ damaged", File.ReadAllText(path + ".damaged"));
        }

        // A 1x1 PNG, and the head of a 640x480 JPEG (SOI, APP0, SOF0).
        private static readonly byte[] Png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        private static readonly byte[] Jpeg =
        {
            0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00,
            0xFF, 0xC0, 0x00, 0x11, 0x08, 0x01, 0xE0, 0x02, 0x80, 0x03, 0x01, 0x22, 0x00, 0x02, 0x11, 0x01, 0x03, 0x11, 0x01,
        };

        [Test]
        public void OnlyPngAndJpegImagesWithinTheirBoundsAreKept()
        {
            Assert.AreEqual("png", ImageCache.Kind(Png));
            Assert.AreEqual((1, 1), ImageCache.Size(Png));
            Assert.AreEqual("jpg", ImageCache.Kind(Jpeg));
            Assert.AreEqual((640, 480), ImageCache.Size(Jpeg));
            byte[] svg = System.Text.Encoding.UTF8.GetBytes("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");
            Assert.IsNull(ImageCache.Kind(svg), "an SVG is code as much as an image");
            Assert.IsNull(ImageCache.Kind(System.Text.Encoding.UTF8.GetBytes("<!doctype html><html>")));

            Assert.IsNull(ImageCache.Problem(Png, 1024, 16));
            StringAssert.Contains("not a PNG or JPEG", ImageCache.Problem(svg, 1024, 16));
            StringAssert.Contains("larger than", ImageCache.Problem(Png, 10, 16), "too many bytes");
            StringAssert.Contains("pixels", ImageCache.Problem(Jpeg, 1024, 512), "too many pixels, told before decoding");
            var huge = (byte[])Png.Clone();
            huge[16] = 0x7F; // a width of two billion pixels in the header
            StringAssert.Contains("pixels", ImageCache.Problem(huge, 1024, 4096));
        }

        [Test]
        public void TheCacheReturnsWhatItKeptAndStaysWithinItsBound()
        {
            string folder = Path.Combine(directory, "images");
            var cache = new ImageCache(folder, Png.Length * 2 + 1);
            Assert.IsNull(cache.Get("https://example.com/banner.png", 1024, 4096, out _));
            Assert.IsFalse(cache.Put("https://example.com/page.html", System.Text.Encoding.UTF8.GetBytes("<html>"), 1024, 4096), "not an image: not kept");
            Assert.IsTrue(cache.Put("https://example.com/banner.png", Png, 1024, 4096));
            CollectionAssert.AreEqual(Png, cache.Get("https://example.com/banner.png", 1024, 4096, out DateTime kept));
            Assert.That(DateTime.UtcNow - kept, Is.LessThan(TimeSpan.FromMinutes(1)));
            Assert.IsNull(cache.Get("https://example.com/banner.png", 10, 4096, out _), "a kept image beyond the caller's bound is not returned");

            File.SetLastWriteTimeUtc(Directory.GetFiles(folder).Single(), DateTime.UtcNow.AddHours(-2));
            Assert.IsTrue(cache.Put("https://example.com/logo.png", Png, 1024, 4096));
            File.SetLastWriteTimeUtc(Directory.GetFiles(folder).OrderBy(File.GetLastWriteTimeUtc).Last(), DateTime.UtcNow.AddHours(-1));
            Assert.IsTrue(cache.Put("https://example.com/third.png", Png, 1024, 4096));
            Assert.AreEqual(2, Directory.GetFiles(folder, "*.img").Length, "the folder kept within its bound");
            Assert.IsNull(cache.Get("https://example.com/banner.png", 1024, 4096, out _), "the oldest went first");
            Assert.IsNotNull(cache.Get("https://example.com/third.png", 1024, 4096, out _), "never the one just kept");
            Assert.IsEmpty(Directory.GetFiles(folder, "*.part"));
        }
    }
}
