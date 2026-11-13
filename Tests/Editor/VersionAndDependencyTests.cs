using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Glitchr.Store.Tests
{
    /// <summary>
    /// The Update button's menu (every known version, newest first, exactly the one chosen) and a
    /// Git package's Git dependencies, added with it because Unity does not resolve them.
    /// </summary>
    public class VersionAndDependencyTests
    {
        private static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

        private static HubItem GitHubItem(string name, string repository, params string[] tags)
        {
            var package = new GitHubPackage { Owner = "example-org", Repository = repository, Name = name, DefaultBranch = "main", Tags = GitHubCatalog.SortTags(tags) };

            return new HubItem { Name = name, GitUrl = package.GitUrl, GitHub = package, Version = package.LatestVersion, Origin = new HubOrigin { HubName = "Example" } };
        }

        [Test]
        public void AGitHubPackagesMenuListsItsTagsNewestFirstEachInstalledByItsTag()
        {
            HubItem item = GitHubItem("com.example.tool", "unity-tool", "v1.2.0", "v2.0.0", "nightly", "v1.10.0", "1.10.0", "v2.1.0-beta.1");

            List<VersionChoice> choices = VersionChoices.For(item, "1.10.0", "v1.10.0");

            CollectionAssert.AreEqual(new[] { "2.1.0-beta.1", "2.0.0", "1.10.0", "1.2.0" }, choices.Select(c => c.Version).ToArray(), "older ones included to go back; a version once; a tag without a version left out");
            Assert.AreEqual("https://github.com/example-org/unity-tool.git#v1.2.0", choices.Last().Identifier, "exactly that tag");
            Assert.IsTrue(choices.Single(c => c.Version == "1.10.0").Installed);
            Assert.IsTrue(choices.Single(c => c.Version == "2.0.0").CrossesMajor);
            Assert.IsFalse(choices.Single(c => c.Version == "1.2.0").CrossesMajor);
            Assert.AreEqual("v1.2.0", choices.Last().Label);
        }

        [Test]
        public void ARegistrysMenuListsItsVersionsAsNameAtVersion()
        {
            var item = new HubItem { Name = "com.example.kit", Version = "3.1.0", Origin = new HubOrigin { Registry = "https://upm.example.com" } };
            item.Versions.AddRange(new[] { "3.0.0", "2.9.1", "3.1.0", "3.0.0-pre.1" }.Select(v => new HubVersion { Version = v }));

            List<VersionChoice> choices = VersionChoices.For(item, "3.0.0");

            CollectionAssert.AreEqual(new[] { "3.1.0", "3.0.0", "3.0.0-pre.1", "2.9.1" }, choices.Select(c => c.Version).ToArray());
            CollectionAssert.AreEqual(new[] { "com.example.kit@3.1.0", "com.example.kit@3.0.0", "com.example.kit@3.0.0-pre.1", "com.example.kit@2.9.1" }, choices.Select(c => c.Identifier).ToArray());
            PackageUpdate update = VersionChoices.Update(item.Name, choices.Last(), item);
            Assert.AreEqual("com.example.kit@2.9.1", update.Identifier);
            Assert.AreSame(item, update.Item, "its registry is written into the manifest when missing");

            Assert.IsEmpty(VersionChoices.For(new HubItem { Name = "com.example.paid", Version = "1.0.0", Paid = true, Installable = false }), "a licensed package not bought: nothing to choose");
            var git = new HubItem { Name = "com.example.git", GitUrl = "https://example.com/x.git#v1.0.0", Version = "1.0.0" };
            Assert.AreEqual("https://example.com/x.git#v1.0.0", VersionChoices.For(git).Single().Identifier, "a plain Git URL names one version");
        }

        [Test]
        public void AMajorMoveAsksFirstOnlyForAPackageThatIsNotFromTheUsersSources()
        {
            List<VersionChoice> unity = VersionChoices.ForRegistry("com.unity.timeline", new[] { "1.8.13", "2.0.0", "1.7.6", "file:../x" }, "1.8.13");
            CollectionAssert.AreEqual(new[] { "2.0.0", "1.8.13", "1.7.6" }, unity.Select(c => c.Version).ToArray());
            Assert.IsTrue(VersionChoices.NeedsConfirmation(unity[0], false), "Unity's registry, another major: never silent");
            Assert.IsFalse(VersionChoices.NeedsConfirmation(unity[2], false), "the same major, older: no question");
            Assert.IsFalse(VersionChoices.NeedsConfirmation(unity[0], true), "the user's own source: chosen on purpose");
        }

        [Test]
        public void AGitPackagesGitDependenciesTheSourcesOfferAreAddedOnceToAFewLevels()
        {
            HubItem core = GitHubItem("com.example.core", "unity-core", "v1.0.0", "v1.4.0", "v2.0.0");
            HubItem ui = GitHubItem("com.example.ui", "unity-ui", "v0.3.0");
            HubItem deep = GitHubItem("com.example.deep", "unity-deep", "v1.0.0");
            HubItem deeper = GitHubItem("com.example.deeper", "unity-deeper", "v1.0.0");
            var registry = new HubItem { Name = "com.example.registry", Version = "1.0.0", Origin = new HubOrigin { Registry = "https://upm.example.com" } };
            var reads = new List<string>();
            var dependencies = new Dictionary<string, Dictionary<string, string>>
            {
                ["unity-tool#v1.0.0"] = new Dictionary<string, string> { ["com.example.core"] = "1.2.0", ["com.example.ui"] = "0.3.0", ["com.unity.ugui"] = "2.0.0", ["com.example.registry"] = "1.0.0", ["com.example.present"] = "1.0.0" },
                ["unity-core#v1.4.0"] = new Dictionary<string, string> { ["com.example.tool"] = "1.0.0", ["com.example.deep"] = "1.0.0" },
                ["unity-ui#v0.3.0"] = new Dictionary<string, string> { ["com.example.core"] = "1.0.0" },
                ["unity-deep#v1.0.0"] = new Dictionary<string, string> { ["com.example.deeper"] = "1.0.0" },
            };
            GitDependencies.Reader read = (url, reference) =>
            {
                string key = GitHubRepository.Of(url).Name + "#" + reference;
                reads.Add(key);

                return Task.FromResult(dependencies.TryGetValue(key, out var found) ? found : new Dictionary<string, string>());
            };
            var offered = new[] { core, ui, deep, deeper, registry };
            var projectHas = new HashSet<string> { "com.unity.ugui", "com.example.present" };

            GitDependencyPlan plan = Sync(GitDependencies.Plan(new[] { ("com.example.tool", "https://github.com/example-org/unity-tool.git#v1.0.0", "v1.0.0") }, offered, projectHas, read, maxDepth: 2));

            CollectionAssert.AreEqual(new[] { "com.example.core", "com.example.ui", "com.example.deep" }, plan.Added.Select(a => a.Name).ToArray(), "core once (the cycle back to the tool cut), deeper beyond the bound");
            Assert.AreEqual("https://github.com/example-org/unity-core.git#v1.4.0", plan.Added[0].Identifier, "1.2.0 wanted: the newest 1.x above it, not 2.0.0");
            Assert.AreEqual("com.example.tool", plan.Added[0].RequiredBy);
            Assert.AreEqual("com.example.core", plan.Added[2].RequiredBy);
            Assert.AreEqual(new[] { registry }, plan.Registries.ToArray(), "a registry's dependency: its registry declared, Unity resolves it");
            CollectionAssert.DoesNotContain(plan.Added.Select(a => a.Name).ToList(), "com.example.present", "the project has it");
            CollectionAssert.DoesNotContain(reads, "unity-deeper#v1.0.0");
            StringAssert.Contains("com.example.core v1.4.0 (for com.example.tool)", plan.Summary);
            StringAssert.StartsWith("Also added", plan.Summary);

            GitDependencyPlan none = Sync(GitDependencies.Plan(new[] { ("com.example.ui", "https://github.com/example-org/unity-ui.git#v0.3.0", "v0.3.0") }, offered, new HashSet<string> { "com.example.core" }, read));
            Assert.IsEmpty(none.Added, "already in the project in some form: nothing added");
            Assert.IsNull(none.Summary);
        }

        [Test]
        public void AFailingReadIsNotedAndTheInstallGoesOn()
        {
            GitDependencies.Reader read = (url, reference) => throw new InvalidOperationException("offline");
            GitDependencyPlan plan = Sync(GitDependencies.Plan(new[] { ("com.example.tool", "https://github.com/example-org/unity-tool.git", (string)null) }, new HubItem[0], new HashSet<string>(), read));
            Assert.IsEmpty(plan.Added);
            StringAssert.Contains("offline", plan.Notes.Single());
        }

        [Test]
        public void ADependencysTagIsItsVersionsElseTheNewestOfItsMajor()
        {
            var tags = new[] { "v1.0.0", "v1.3.0", "v1.2.0", "v2.0.0", "docs" };
            Assert.AreEqual("v1.2.0", GitDependencies.Tag(tags, "1.2.0"));
            Assert.AreEqual("v1.3.0", GitDependencies.Tag(tags, "1.1.0"));
            Assert.AreEqual("v2.0.0", GitDependencies.Tag(tags, "3.0.0"), "nothing fits: the newest");
            Assert.IsNull(GitDependencies.Tag(new[] { "docs" }, "1.0.0"));
        }

        [Test]
        public void TheDependenciesAreReadFromWhatTheCatalogueHasElseFromGitHubAtTheTag()
        {
            var github = new FakeWeb();
            string path = GitHubApi.Root + "/repos/example-org/unity-tool/contents/Packages/tool/package.json?ref=v1.0.0";
            github.Routes[path] = request =>
            {
                if (request.Headers.TryGetValue("If-None-Match", out string seen) && seen == "\"e1\"") return new WebResponse { Status = 304 };
                var answer = new WebResponse { Status = 200, Text = "{\"name\":\"com.example.tool\",\"dependencies\":{\"com.example.core\":\"1.0.0\",\"bad\":1}}" };
                answer.Headers["ETag"] = "\"e1\"";

                return answer;
            };
            var api = new GitHubApi(github, new GitHubCache(), limit: new GitHubRateLimit());
            HubItem known = GitHubItem("com.example.core", "unity-core", "v1.0.0");
            known.Dependencies = new Dictionary<string, string> { ["com.example.base"] = "1.0.0" };
            GitDependencies.Reader read = GitDependencies.GitHubReader(api, new[] { known });

            CollectionAssert.AreEqual(new[] { "com.example.base" }, Sync(read("https://github.com/example-org/unity-core.git", null)).Keys.ToArray(), "the default branch's, already read");
            var tool = Sync(read("https://github.com/example-org/unity-tool.git?path=Packages/tool#v1.0.0", null));
            CollectionAssert.AreEqual(new[] { "com.example.core" }, tool.Keys.ToArray(), "a version that is no string is left out");
            Sync(read("https://github.com/example-org/unity-tool.git?path=Packages/tool", "v1.0.0"));
            Assert.AreEqual("\"e1\"", github.Requests.Last().Headers["If-None-Match"], "asked again with its ETag");
            Assert.AreEqual(GitHubApi.RawType, github.Requests.Last().Headers["Accept"]);
            Assert.IsNull(Sync(read("https://gitlab.example.com/a/b.git#v1.0.0", null)), "not GitHub: nothing known");
            Assert.IsNull(Sync(read("https://github.com/example-org/unity-missing.git", "v9.0.0")), "no package.json there");
            Assert.AreEqual(3, github.Requests.Count);
        }

        [Test]
        public void TheProjectHasAPackageInAnyForm()
        {
            string project = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "forge-store-has-" + Guid.NewGuid().ToString("N"));
            string packages = System.IO.Path.Combine(project, "Packages");
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(packages, "com.example.embedded"));
            System.IO.Directory.CreateDirectory(System.IO.Path.Combine(packages, "folder-name"));
            try
            {
                System.IO.File.WriteAllText(System.IO.Path.Combine(packages, "manifest.json"), "{\"dependencies\":{\"com.example.asked\":\"https://github.com/o/r.git\"}}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(packages, "packages-lock.json"), "{\"dependencies\":{\"com.example.indirect\":{\"version\":\"1.0.0\"}}}");
                System.IO.File.WriteAllText(System.IO.Path.Combine(packages, "folder-name", "package.json"), "{\"name\":\"com.example.renamed\"}");

                HashSet<string> has = HubInstaller.ProjectHas(project);

                CollectionAssert.IsSupersetOf(has, new[] { "com.example.asked", "com.example.indirect", "com.example.embedded", "com.example.renamed" });
                Assert.IsTrue(HubInstaller.IsGitIdentifier("https://github.com/o/r.git#v1"));
                Assert.IsFalse(HubInstaller.IsGitIdentifier("com.example.github@1.0.0"), "a name with .git in it is no Git URL");
            }
            finally
            {
                System.IO.Directory.Delete(project, true);
            }
        }
    }
}
