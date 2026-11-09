using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;

namespace Glitchr.Store.Tests
{
    /// <summary>What the window writes: a project's manifest, the user's Package Manager configuration - and the JSON it reads.</summary>
    public class HubTests
    {
        private string directory;

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "glitchr-hub-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable("UPM_USER_CONFIG_FILE", null);
            Directory.Delete(directory, true);
        }

        [Test]
        public void JsonReadsAndWritesARegistrysDocument()
        {
            object doc = Json.Parse("{\"name\":\"io.glitchr.licensing\",\"dist-tags\":{\"latest\":\"1.0.1\"},\"versions\":{\"1.0.0\":{},\"1.0.1\":{\"unity\":\"2021.3\",\"keywords\":[\"a\",\"b\"],\"n\":1.5,\"ok\":true,\"no\":null,\"text\":\"quote \\\" and \\u00e9\"}}}");
            Assert.AreEqual("1.0.1", Json.Get(doc, "dist-tags", "latest"));
            Assert.AreEqual("2021.3", Json.Get(doc, "versions", "1.0.1", "unity"));
            Assert.AreEqual(1.5, Json.Get(doc, "versions", "1.0.1", "n"));
            Assert.AreEqual(true, Json.Get(doc, "versions", "1.0.1", "ok"));
            Assert.AreEqual("quote \" and é", Json.Get(doc, "versions", "1.0.1", "text"));
            Assert.AreEqual(Json.Write(doc), Json.Write(Json.Parse(Json.Write(doc, true))), "written and read back, the same");
            Assert.Throws<FormatException>(() => Json.Parse("{\"a\":"));
        }

        [Test]
        public void APackageWithAHubSectionIsReadAsItsHub()
        {
            object package = Json.Parse("{\"name\":\"io.glitchr.studio\",\"version\":\"0.2.0\",\"dependencies\":{},\"hub\":{\"name\":\"Glitch Art\",\"sources\":[{\"type\":\"git\",\"url\":\"https://github.com/glitchr-studio/unity-toolkit.git#v0.3.0\"}]}}");
            object hub = HubCatalogReader.HubOf(package);
            Assert.AreEqual("Glitch Art", Json.Get(hub, "name"), "the section, not the package's own name");
            Assert.AreEqual(1, Json.Array(Json.Get(hub, "sources")).Count);

            object plain = Json.Parse("{\"version\":1,\"name\":\"Glitch Art\",\"sources\":[]}");
            Assert.AreSame(plain, HubCatalogReader.HubOf(plain), "a hub.json is its own hub");
        }

        [Test]
        public void AGitHubPackagesPackageJsonIsFoundAtItsReferenceAndFolder()
        {
            const string raw = "https://raw.githubusercontent.com/";
            Assert.AreEqual(raw + "glitchr-studio/unity-toolkit/v0.3.0/package.json", HubCatalogReader.PackageJsonUrl("https://github.com/glitchr-studio/unity-toolkit.git#v0.3.0"));
            Assert.AreEqual(raw + "glitchr-forge/unity-store/HEAD/package.json", HubCatalogReader.PackageJsonUrl("https://github.com/glitchr-forge/unity-store.git"));
            Assert.AreEqual(raw + "meiyasan/unity-mcp/v10.3.0-glitchr.5/MCPForUnity/package.json", HubCatalogReader.PackageJsonUrl("https://github.com/meiyasan/unity-mcp.git?path=/MCPForUnity#v10.3.0-glitchr.5"));
            Assert.AreEqual(raw + "someone/tool/main/package.json", HubCatalogReader.PackageJsonUrl("git@github.com:someone/tool.git#main"));
            Assert.IsNull(HubCatalogReader.PackageJsonUrl("https://glitchr-studio.github.io/hub.json"), "not a repository");
            Assert.IsNull(HubCatalogReader.PackageJsonUrl("https://github.com/someone/tool/blob/main/hub.json"), "a page, not a repository");
            Assert.IsNull(HubCatalogReader.PackageJsonUrl("https://gitlab.com/someone/tool.git"), "GitHub's only");
        }

        [Test]
        public void TheManifestGetsTheScopedRegistryOnceAndTheDependency()
        {
            string path = Path.Combine(directory, "manifest.json");
            File.WriteAllText(path, "{\n  \"dependencies\": {\n    \"com.unity.ugui\": \"2.0.0\"\n  }\n}\n");

            var manifest = new ProjectManifest(path);
            Assert.IsTrue(manifest.AddScopedRegistry("Glitch Art", "https://glitchr.io/upm/", new[] { "io.glitchr" }));
            manifest.SetDependency("io.glitchr.licensing", "1.0.1");
            manifest.Save();

            var again = new ProjectManifest(path);
            Assert.IsFalse(again.AddScopedRegistry("Glitch Art", "https://glitchr.io/upm", new[] { "io.glitchr" }), "already there");
            Assert.IsTrue(again.AddScopedRegistry("Other name", "https://glitchr.io/upm", new[] { "io.glitchr", "com.other" }), "its scopes completed");
            again.Save();

            object json = Json.Parse(File.ReadAllText(path));
            var registries = Json.Array(Json.Get(json, "scopedRegistries"));
            Assert.AreEqual(1, registries.Count);
            Assert.AreEqual("https://glitchr.io/upm", Json.Get(registries[0], "url"));
            CollectionAssert.AreEqual(new object[] { "io.glitchr", "com.other" }, Json.Array(Json.Get(registries[0], "scopes")));
            Assert.AreEqual("1.0.1", Json.Get(json, "dependencies", "io.glitchr.licensing"));
            Assert.AreEqual("2.0.0", Json.Get(json, "dependencies", "com.unity.ugui"), "the others kept");
        }

        [Test]
        public void TheKeyGoesToTheUsersConfigurationOnceForItsRegistry()
        {
            string path = Path.Combine(directory, ".upmconfig.toml");
            File.WriteAllText(path, "[npmAuth.\"https://other.example/npm\"]\ntoken = \"other\"\nalwaysAuth = true\n");
            Environment.SetEnvironmentVariable("UPM_USER_CONFIG_FILE", path);

            Assert.IsNull(UpmConfig.Token("https://glitchr.io/upm"));
            UpmConfig.SetToken("https://glitchr.io/upm/", "glk_FIRST");
            Assert.AreEqual("glk_FIRST", UpmConfig.Token("https://glitchr.io/upm"));
            UpmConfig.SetToken("https://glitchr.io/upm", "glk_SECOND");
            Assert.AreEqual("glk_SECOND", UpmConfig.Token("https://glitchr.io/upm"), "replaced, not added");
            Assert.AreEqual("other", UpmConfig.Token("https://other.example/npm"), "the other registry kept");
            string text = File.ReadAllText(path);
            Assert.AreEqual(1, text.Split(new[] { "[npmAuth.\"https://glitchr.io/upm\"]" }, StringSplitOptions.None).Length - 1);
        }
    }
}
