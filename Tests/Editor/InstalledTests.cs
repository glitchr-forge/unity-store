using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Glitchr.Store.Tests
{
    /// <summary>
    /// The Installed tab's pure parts: a project's manifest and lock read as its packages, which
    /// of them a source offers and what it updates them to, the project's record of what Forge
    /// Store installed.
    /// </summary>
    public class InstalledTests
    {
        private const string Manifest = @"{
  ""scopedRegistries"": [{""name"": ""Example"", ""url"": ""https://upm.example.com"", ""scopes"": [""com.example""]}],
  ""dependencies"": {
    ""io.glitchr.store"": ""https://github.com/glitchr-forge/unity-store.git#v1.2.0"",
    ""com.example.tool"": ""1.0.0"",
    ""com.example.branch"": ""https://github.com/example-org/unity-branch.git#develop"",
    ""com.example.head"": ""https://github.com/example-org/unity-head.git"",
    ""com.example.pinned"": ""https://github.com/example-org/unity-pinned.git#0a1b2c3d4e5f60718293a4b5c6d7e8f901234567"",
    ""com.example.local"": ""file:../../local-package"",
    ""com.unity.ugui"": ""2.0.0"",
    ""com.example.new"": ""2.0.0""
  }
}";

        private const string Lock = @"{
  ""dependencies"": {
    ""io.glitchr.store"": {""version"": ""https://github.com/glitchr-forge/unity-store.git#v1.2.0"", ""depth"": 0, ""source"": ""git"", ""dependencies"": {}, ""hash"": ""6939976aa1b2c3d4e5f60718293a4b5c6d7e8f90""},
    ""com.example.tool"": {""version"": ""1.0.0"", ""depth"": 0, ""source"": ""registry"", ""dependencies"": {""com.example.core"": ""1.1.0""}, ""url"": ""https://upm.example.com/""},
    ""com.example.core"": {""version"": ""1.1.0"", ""depth"": 1, ""source"": ""registry"", ""dependencies"": {}, ""url"": ""https://upm.example.com""},
    ""com.example.branch"": {""version"": ""https://github.com/example-org/unity-branch.git#develop"", ""depth"": 0, ""source"": ""git"", ""dependencies"": {}, ""hash"": ""1111111111111111111111111111111111111111""},
    ""com.example.head"": {""version"": ""https://github.com/example-org/unity-head.git"", ""depth"": 0, ""source"": ""git"", ""dependencies"": {}, ""hash"": ""2222222aaaaaaa""},
    ""com.example.pinned"": {""version"": ""https://github.com/example-org/unity-pinned.git#0a1b2c3d4e5f60718293a4b5c6d7e8f901234567"", ""depth"": 0, ""source"": ""git"", ""dependencies"": {}, ""hash"": ""0a1b2c3d4e5f60718293a4b5c6d7e8f901234567""},
    ""com.example.local"": {""version"": ""file:../../local-package"", ""depth"": 0, ""source"": ""local"", ""dependencies"": {}},
    ""com.example.embedded"": {""version"": ""file:com.example.embedded"", ""depth"": 0, ""source"": ""embedded"", ""dependencies"": {}},
    ""com.unity.ugui"": {""version"": ""2.0.0"", ""depth"": 0, ""source"": ""builtin"", ""dependencies"": {""com.unity.modules.ui"": ""1.0.0""}},
    ""com.unity.modules.ui"": {""version"": ""1.0.0"", ""depth"": 1, ""source"": ""builtin"", ""dependencies"": {}}
  }
}";

        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "forge-store-installed-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(directory, true);

        private static Dictionary<string, InstalledPackage> Packages(IDictionary<string, string> resolved = null)
        {
            return InstalledPackages.Read(Manifest, Lock, resolved).ToDictionary(p => p.Name);
        }

        [Test]
        public void TheManifestAndTheLockGiveEachPackagesVersionSourceAndGitReference()
        {
            var packages = Packages(new Dictionary<string, string> { ["io.glitchr.store"] = "1.2.0", ["com.example.tool"] = "https://not-a-version" });

            InstalledPackage store = packages["io.glitchr.store"];
            Assert.AreEqual(InstalledPackage.Git, store.Source);
            Assert.AreEqual("1.2.0", store.Version, "a Git package's version is its package.json's, as Unity resolved it");
            Assert.AreEqual("https://github.com/glitchr-forge/unity-store.git", store.Url);
            Assert.AreEqual("v1.2.0", store.GitReference);
            Assert.AreEqual("tag", store.GitReferenceKind);
            Assert.AreEqual("tag v1.2.0 · commit 6939976", store.GitDescription);

            Assert.AreEqual("branch develop · commit 1111111", packages["com.example.branch"].GitDescription);
            Assert.AreEqual("default branch · commit 2222222", packages["com.example.head"].GitDescription);
            Assert.AreEqual("commit", packages["com.example.pinned"].GitReferenceKind);

            InstalledPackage tool = packages["com.example.tool"];
            Assert.AreEqual(InstalledPackage.Registry, tool.Source);
            Assert.AreEqual("1.0.0", tool.Version, "a URL is never taken for a version");
            Assert.AreEqual("https://upm.example.com", tool.Url);
            Assert.IsTrue(tool.Direct);
            Assert.IsNull(tool.GitDescription);

            InstalledPackage core = packages["com.example.core"];
            Assert.IsFalse(core.Direct, "a dependency the manifest does not name");
            Assert.AreEqual(1, core.Depth);
            CollectionAssert.AreEqual(new[] { "com.example.tool" }, core.Dependents);

            Assert.AreEqual(InstalledPackage.Local, packages["com.example.local"].Source);
            Assert.AreEqual(InstalledPackage.Embedded, packages["com.example.embedded"].Source);
            Assert.IsFalse(packages["com.example.embedded"].Direct, "an embedded package is not in the manifest");
            Assert.AreEqual(InstalledPackage.Builtin, packages["com.unity.ugui"].Source);
            CollectionAssert.AreEqual(new[] { "com.unity.ugui" }, packages["com.unity.modules.ui"].Dependents);

            InstalledPackage fresh = packages["com.example.new"];
            Assert.AreEqual(InstalledPackage.Registry, fresh.Source, "in the manifest, not resolved yet");
            Assert.AreEqual("2.0.0", fresh.Version);

            var order = InstalledPackages.Read(Manifest, Lock).Select(p => p.Direct).ToList();
            Assert.AreEqual(order.OrderByDescending(d => d).ToList(), order, "the manifest's own packages first");
            Assert.AreEqual(8, InstalledPackages.Read(Manifest, null).Count, "without a lock, the manifest's");
        }

        [Test]
        public void ASourcesOfferIsMatchedByNameAndPlaceAndGivesTheUpdate()
        {
            var packages = Packages(new Dictionary<string, string> { ["io.glitchr.store"] = "1.2.0", ["com.example.branch"] = "0.4.0" });
            var registry = new HubOrigin { HubName = "Example", Registry = "https://upm.example.com", Scopes = new List<string> { "com.example" } };
            var github = new GitHubPackage { Owner = "glitchr-forge", Repository = "unity-store", Name = "io.glitchr.store", Tags = new List<string> { "v1.3.0", "v1.2.0" } };
            var items = new List<HubItem>
            {
                new HubItem { Name = "io.glitchr.store", GitUrl = github.GitUrl, GitHub = github, Origin = new HubOrigin { HubName = "Forge" } },
                new HubItem { Name = "com.example.tool", Version = "1.2.0", Origin = registry },
                new HubItem { Name = "com.example.core", Version = "1.5.0", Origin = registry },
                new HubItem { Name = "com.example.branch", GitUrl = "https://github.com/someone-else/unity-branch.git", Version = "9.0.0" },
            };

            HubItem storeOffer = InstalledPackages.Offer(packages["io.glitchr.store"], items);
            Assert.AreSame(items[0], storeOffer);
            PackageUpdate storeUpdate = InstalledPackages.UpdateFrom(packages["io.glitchr.store"], storeOffer);
            Assert.AreEqual("https://github.com/glitchr-forge/unity-store.git#v1.3.0", storeUpdate.Identifier, "a GitHub package moves to its latest tag");
            Assert.AreEqual("v1.3.0", storeUpdate.ToVersion);

            PackageUpdate toolUpdate = InstalledPackages.UpdateFrom(packages["com.example.tool"], InstalledPackages.Offer(packages["com.example.tool"], items));
            Assert.AreEqual("com.example.tool@1.2.0", toolUpdate.Identifier);
            Assert.AreSame(items[1], toolUpdate.Item, "its registry is written into the manifest when missing");

            Assert.IsNotNull(InstalledPackages.Offer(packages["com.example.core"], items), "a dependency is offered by its source");
            Assert.IsNull(InstalledPackages.UpdateFrom(packages["com.example.core"], items[2]), "but a dependency is UPM's to move, not the window's");
            Assert.IsNull(InstalledPackages.Offer(packages["com.example.branch"], items), "another repository of the same package name is not its source");

            var other = new HubItem { Name = "com.example.tool", Version = "3.0.0", Origin = new HubOrigin { Registry = "https://other.example.com" } };
            Assert.IsNull(InstalledPackages.Offer(packages["com.example.tool"], new[] { other }), "the lock names another registry");

            github.Tags = new List<string> { "v1.2.0" };
            Assert.IsNull(InstalledPackages.UpdateFrom(packages["io.glitchr.store"], storeOffer), "at the latest tag already");

            var paid = new HubItem { Name = "com.example.tool", Version = "2.0.0", Paid = true, Installable = false, Origin = registry };
            Assert.IsNull(InstalledPackages.UpdateFrom(packages["com.example.tool"], paid), "a version the user's key does not cover");

            Assert.AreEqual("com.example.tool@1.0.5", InstalledPackages.UpdateTo(packages["com.example.tool"], "1.0.5").Identifier, "Unity's own latest");
            Assert.IsNull(InstalledPackages.UpdateTo(packages["com.example.tool"], "1.0.0"));
            Assert.IsNull(InstalledPackages.UpdateTo(packages["com.example.tool"], "2.0.0"), "never across a major version");
            Assert.IsNull(InstalledPackages.UpdateTo(packages["com.unity.ugui"], "3.0.0"), "a built-in module is Unity's");
            Assert.IsNull(InstalledPackages.UpdateTo(packages["com.example.local"], "3.0.0"), "a folder has no update");
        }

        [Test]
        public void TheRecordKeepsWhichSourceInstalledAPackage()
        {
            string path = Path.Combine(directory, "UserSettings", "ForgeStore", "installed.json");
            var record = new InstallRecord(path);
            record.Set("com.example.tool", new HubSource { Type = HubSource.Manifest, Url = "https://Hub.Example.com/hub.json", Name = "Example hub" });
            record.Set("com.example.other", new HubSource { Type = HubSource.GitHub, Owner = "example-org", Pattern = "unity-*" });
            record.Set("ignored", null);
            record.Save();

            var again = new InstallRecord(path);
            Assert.AreEqual("manifest:https://hub.example.com/hub.json", again.Get("com.example.tool").Source);
            Assert.AreEqual("Example hub", again.Get("com.example.tool").Label);
            Assert.AreEqual("github:example-org/unity-*", again.Get("com.example.other").Source);
            Assert.IsNull(again.Get("ignored"));
            Assert.IsTrue(again.Remove("com.example.tool"));
            again.Save();
            CollectionAssert.AreEqual(new[] { "com.example.other" }, new InstallRecord(path).Packages.ToList());

            File.WriteAllText(path, "{ not json");
            Assert.AreEqual(0, new InstallRecord(path).Packages.Count(), "a damaged record is an empty one");
        }
    }
}
