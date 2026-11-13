using System;
using System.IO;
using System.Linq;
using NUnit.Framework;

namespace Glitchr.Store.Tests
{
    /// <summary>
    /// An installed package's samples, imported where Unity's own window puts them, and which
    /// packages Embed applies to. The test project may carry com.example.samples (a local package
    /// with odd names): then the folder is checked against Unity's own Sample API.
    /// </summary>
    public class SamplesTests
    {
        private const string PackageJson = "{\"name\":\"com.example.kit\",\"displayName\":\"Example: Kit\",\"version\":\"1.2.0\",\"samples\":["
            + "{\"displayName\":\"Basic Scene\",\"description\":\"A scene.\",\"path\":\"Samples~/Basic\"},"
            + "{\"path\":\"Samples~/Unnamed/\"},{\"displayName\":\"No path\"},"
            + "{\"displayName\":\"Escape\",\"path\":\"../outside\"}]}";

        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "forge-store-samples-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown() => Directory.Delete(directory, true);

        [Test]
        public void APackagesSamplesAreReadFromItsPackageJson()
        {
            var samples = PackageSamples.Read(PackageJson);
            CollectionAssert.AreEqual(new[] { "Basic Scene", "Unnamed", "Escape" }, samples.Select(s => s.DisplayName).ToArray(), "one without a path is no sample; one without a name is named by its folder");
            Assert.AreEqual("A scene.", samples[0].Description);
            Assert.IsEmpty(PackageSamples.Read("{\"name\":\"x\"}"));
        }

        [Test]
        public void ASampleIsCopiedIntoAssetsSamplesPackageVersionSample()
        {
            string package = Path.Combine(directory, "package");
            Directory.CreateDirectory(Path.Combine(package, "Samples~", "Basic", "Scripts"));
            File.WriteAllText(Path.Combine(package, "Samples~", "Basic", "Scene.unity"), "scene");
            File.WriteAllText(Path.Combine(package, "Samples~", "Basic", "Scripts", "Thing.cs"), "class Thing {}");
            Directory.CreateDirectory(Path.Combine(directory, "outside"));
            string assets = Path.Combine(directory, "Project", "Assets");
            var samples = PackageSamples.Read(PackageJson);

            string target = PackageSamples.ImportFolder(assets, "Example: Kit", "1.2.0", samples[0].DisplayName);
            Assert.AreEqual(Path.Combine(assets, "Samples", "Example_ Kit", "1.2.0", "Basic Scene"), target);

            Assert.AreEqual(2, PackageSamples.Copy(package, samples[0], target));
            Assert.AreEqual("class Thing {}", File.ReadAllText(Path.Combine(target, "Scripts", "Thing.cs")));
            Assert.Throws<IOException>(() => PackageSamples.Copy(package, samples[0], target), "imported already: replaced only when asked");
            File.WriteAllText(Path.Combine(target, "Mine.txt"), "changed");
            Assert.AreEqual(2, PackageSamples.Copy(package, samples[0], target, true));
            Assert.IsFalse(File.Exists(Path.Combine(target, "Mine.txt")), "replaced whole");

            Assert.Throws<ArgumentException>(() => PackageSamples.Copy(package, samples[2], Path.Combine(directory, "x")), "a path that leaves the package");
            Assert.Throws<DirectoryNotFoundException>(() => PackageSamples.Copy(package, samples[1], Path.Combine(directory, "y")));
        }

        [Test]
        public void EmbedAppliesToARegistrysOrAGitPackageNotYetInPackages()
        {
            string packages = Path.Combine(directory, "Packages");
            Directory.CreateDirectory(Path.Combine(packages, "com.example.already"));
            Assert.IsTrue(PackageSamples.Embeddable(new InstalledPackage { Name = "com.example.kit", Source = InstalledPackage.Registry }, packages));
            Assert.IsTrue(PackageSamples.Embeddable(new InstalledPackage { Name = "com.example.git", Source = InstalledPackage.Git }, packages));
            Assert.IsFalse(PackageSamples.Embeddable(new InstalledPackage { Name = "com.example.already", Source = InstalledPackage.Registry }, packages), "its folder is there already");
            Assert.IsFalse(PackageSamples.Embeddable(new InstalledPackage { Name = "com.example.local", Source = InstalledPackage.Local }, packages), "the user's own folder");
            Assert.IsFalse(PackageSamples.Embeddable(new InstalledPackage { Name = "com.example.embedded", Source = InstalledPackage.Embedded }, packages));
            Assert.IsFalse(PackageSamples.Embeddable(new InstalledPackage { Name = "com.unity.modules.ui", Source = InstalledPackage.Builtin }, packages));
        }

        [Test]
        public void TheImportFolderIsTheOneUnitysOwnSampleNames()
        {
            var info = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages().FirstOrDefault(p => p.name == "com.example.samples");
            if (info == null) Assert.Ignore("The test project has no com.example.samples package.");
            var unity = UnityEditor.PackageManager.UI.Sample.FindByPackage(info.name, info.version).ToList();
            var ours = PackageSamples.Read(File.ReadAllText(Path.Combine(info.resolvedPath, "package.json")));
            Assert.AreEqual(ours.Count, unity.Count, "the same samples");
            foreach (var sample in unity)
            {
                string folder = PackageSamples.ImportFolder(UnityEngine.Application.dataPath, info.displayName, info.version, sample.displayName);
                Assert.AreEqual(Path.GetFullPath(sample.importPath), Path.GetFullPath(folder), sample.displayName);
                Assert.AreEqual(Path.GetFullPath(sample.resolvedPath), PackageSamples.SourceFolder(info.resolvedPath, ours.Single(s => s.DisplayName == sample.displayName)));
            }
        }
    }
}
