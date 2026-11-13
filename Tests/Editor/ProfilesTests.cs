using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Glitchr.Store.Tests
{
    /// <summary>Canned answers by full address; every request kept.</summary>
    internal sealed class FakeWeb : IWebTransport
    {
        public readonly Dictionary<string, Func<WebRequest, WebResponse>> Routes = new Dictionary<string, Func<WebRequest, WebResponse>>();
        public readonly List<WebRequest> Requests = new List<WebRequest>();

        public void Json(string url, string body) => Routes[url] = _ => new WebResponse { Status = 200, Text = body };

        public void Redirect(string url, string location)
        {
            Routes[url] = _ =>
            {
                var response = new WebResponse { Status = 302 };
                response.Headers["Location"] = location;

                return response;
            };
        }

        public Task<WebResponse> Send(WebRequest request)
        {
            Requests.Add(request);

            return Task.FromResult(Routes.TryGetValue(request.Url, out var route) ? route(request) : new WebResponse { Status = 404, Text = "{}" });
        }
    }

    /// <summary>
    /// Profiles (PROFILES.md, shared with Forge Hub): the per-user file read as a tree and written
    /// whole, unknown fields kept; added by address or file, renamed, removed, switched, exported;
    /// secrets kept out; what the catalogue reads and what the Installed tab says of another
    /// profile's packages. The file is a temporary one: the user's own is never touched.
    /// </summary>
    public class ProfilesTests
    {
        private const string Hub = "{\"version\":1,\"name\":\"Example Studio\",\"theme\":{\"accent\":\"#ff8800\"},\"sources\":[{\"type\":\"github\",\"owner\":\"example-org\",\"pattern\":\"unity-*\"},{\"type\":\"upm\",\"url\":\"https://upm.example.com\",\"scopes\":[\"com.example\"]}],\"checkout\":{\"device\":\"https://example.com/device\",\"token\":\"https://example.com/token\"}}";

        private string directory;
        private string file;

        private static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

        [SetUp]
        public void SetUp()
        {
            directory = Path.Combine(Path.GetTempPath(), "forge-store-profiles-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            file = Path.Combine(directory, "Forge", "profiles.json");
        }

        [TearDown]
        public void TearDown()
        {
            Environment.SetEnvironmentVariable(ProfileStore.EnvironmentVariable, null);
            Directory.Delete(directory, true);
        }

        [Test]
        public void TheSharedFileIsTheOneDirsNamesOrTheEnvironmentsOne()
        {
            Assert.AreEqual(Path.Combine("/Users/me", "Library", "Application Support"), ProfileStore.DataFolder("macos", "/Users/me", "/Users/me/.local/share", null), "macOS: Rust's data_local_dir, not Mono's LocalApplicationData");
            Assert.AreEqual(@"C:\Users\me\AppData\Local", ProfileStore.DataFolder("windows", @"C:\Users\me", @"C:\Users\me\AppData\Local", null));
            Assert.AreEqual("/data", ProfileStore.DataFolder("linux", "/home/me", null, "/data"));
            Assert.AreEqual(Path.Combine("/home/me", ".local", "share"), ProfileStore.DataFolder("linux", "/home/me", null, "relative"), "a relative XDG_DATA_HOME is ignored, as dirs does");

            Environment.SetEnvironmentVariable(ProfileStore.EnvironmentVariable, file);
            Assert.AreEqual(file, ProfileStore.DefaultPath);
            Assert.AreEqual(file, new ProfileStore().Path);
        }

        [Test]
        public void AddRenameSwitchAndRemoveKeepWhatTheOtherAppWrote()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "{\"version\":1,\"active\":null,\"hubOnly\":{\"window\":[1,2]},\"profiles\":[{\"id\":\"00112233aabbccdd\",\"name\":\"Theirs\",\"origin\":\"file\",\"addedAt\":\"2026-11-13T00:00:00Z\",\"refreshedAt\":null,\"hub\":{\"name\":\"Theirs\"},\"pinnedByHub\":true}]}");
            var store = new ProfileStore(file);

            ForgeProfile added = store.Add(Json.Parse(Hub), ForgeProfile.FromUrl, "https://example-org.github.io/hub/-/hub.json", now: new DateTime(2026, 11, 13, 1, 2, 3, DateTimeKind.Utc));
            StringAssert.IsMatch("^[0-9a-f]{16}$", added.Id);
            Assert.AreEqual("Example Studio", added.Name, "the hub's own name");
            Assert.AreEqual("2026-11-13T01:02:03Z", added.AddedAt);
            Assert.AreEqual("2026-11-13T01:02:03Z", added.RefreshedAt);
            Assert.IsNull(store.Active, "adding does not switch");

            // The other app writes between two of ours: its change survives ours.
            object tree = Json.Parse(File.ReadAllText(file));
            ((Dictionary<string, object>)tree)["hubOnly"] = "changed by Forge Hub";
            File.WriteAllText(file, Json.Write(tree));

            store.Rename(added.Id, "  Our team  ");
            store.SetActive(added.Id);
            Assert.AreEqual(added.Id, store.Active);
            Assert.AreEqual("Our team", store.ActiveProfile.Name);
            Assert.AreEqual("#ff8800", store.ActiveProfile.Theme.Accent);

            // The same address again refreshes that profile: same id, the user's name kept.
            ForgeProfile again = store.Add(Json.Parse("{\"name\":\"Example Studio 2\",\"sources\":[]}"), ForgeProfile.FromUrl, "https://example-org.github.io/hub/-/hub.json");
            Assert.AreEqual(added.Id, again.Id);
            Assert.AreEqual("Our team", again.Name);
            Assert.AreEqual(2, store.Profiles().Count);

            object written = Json.Parse(File.ReadAllText(file));
            Assert.AreEqual("changed by Forge Hub", Json.Get(written, "hubOnly"), "an unknown field of the root kept");
            var theirs = Json.Array(Json.Get(written, "profiles")).Cast<Dictionary<string, object>>().Single(p => (string)p["id"] == "00112233aabbccdd");
            Assert.AreEqual(true, theirs["pinnedByHub"], "an unknown field of a profile kept");
            Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(file), "*.tmp"), "the temporary file renamed over the file");

            store.Remove(added.Id);
            Assert.IsNull(store.Active, "the active profile removed: no profile");
            Assert.AreEqual(new[] { "Theirs" }, store.Profiles().Select(p => p.Name).ToArray());
            Assert.Throws<ArgumentException>(() => store.Remove(added.Id));
            Assert.Throws<ArgumentException>(() => store.SetActive("ffffffffffffffff"));
            Assert.Throws<ArgumentException>(() => store.Rename("00112233aabbccdd", new string('x', 121)));
        }

        [Test]
        public void AFileThatIsNotJsonIsNeverWrittenOverAndAnEmptyOneIsAnEmptyList()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file));
            File.WriteAllText(file, "{\"version\":1,\"profiles\":[");
            var store = new ProfileStore(file);
            Assert.Throws<FormatException>(() => store.Profiles());
            Assert.Throws<FormatException>(() => store.Add(Json.Parse(Hub), ForgeProfile.FromFile));
            Assert.AreEqual("{\"version\":1,\"profiles\":[", File.ReadAllText(file), "left as it is");

            File.WriteAllText(file, "  \n");
            Assert.IsEmpty(store.Profiles());
            store.Add(Json.Parse(Hub), ForgeProfile.FromFile);
            Assert.AreEqual(1, store.Profiles().Count);
        }

        [Test]
        public void SecretsStayOutOfTheFileAndOfAnExport()
        {
            var store = new ProfileStore(file);
            string hub = "{\"name\":\"Leaky/Hub\",\"token\":\"abc\",\"apiKey\":\"k\",\"sources\":[{\"type\":\"git\",\"url\":\"https://github.com/o/r.git\",\"github_token\":\"x\",\"note\":\"ghp_0123456789\"}],"
                + "\"checkout\":{\"device\":\"https://example.com/device\",\"token\":\"https://example.com/token\"},\"licensing\":{\"publicKey\":\"AAAA\",\"privateKey\":\"BBBB\"},\"keep\":[\"github_pat_xyz\",\"fine\"]}";
            ForgeProfile profile = store.Add(Json.Parse(hub), ForgeProfile.FromFile);

            string text = File.ReadAllText(file);
            foreach (string secret in new[] { "\"abc\"", "\"k\"", "ghp_", "github_pat_", "BBBB", "github_token" }) StringAssert.DoesNotContain(secret, text);
            StringAssert.Contains("https://example.com/token", text, "the checkout's token endpoint is an address: kept");
            StringAssert.Contains("AAAA", text, "the public key is no secret");

            string exported = store.Export(profile.Id, directory);
            Assert.AreEqual(Path.Combine(directory, "Leaky-Hub.forge-profile.json"), exported);
            object bare = Json.Parse(File.ReadAllText(exported));
            Assert.AreEqual("Leaky/Hub", Json.Get(bare, "name"), "the bare hub document");
            Assert.IsNull(Json.Get(bare, "id"));
            Assert.AreEqual(Path.Combine(directory, "Leaky-Hub 2.forge-profile.json"), store.Export(profile.Id, directory), "never written over");

            // Imported back from the file: the same document.
            Assert.AreEqual(Json.Write(bare), Json.Write(ProfileImport.ReadFile(exported)));
        }

        [Test]
        public void AFileIsAProfileWhenItIsAHubDocumentOfAtMostOneMegabyte()
        {
            string package = Path.Combine(directory, "package.json");
            File.WriteAllText(package, "{\"name\":\"io.example.base\",\"hub\":{\"name\":\"Base\",\"sources\":[]}}");
            Assert.AreEqual("Base", Json.Get(ProfileImport.ReadFile(package), "name"), "a package.json's hub section");

            string notHub = Path.Combine(directory, "other.json");
            File.WriteAllText(notHub, "{\"dependencies\":{}}");
            Assert.Throws<FormatException>(() => ProfileImport.ReadFile(notHub));
            File.WriteAllText(notHub, "{\"name\":\"x\",\"sources\":{}}");
            Assert.Throws<FormatException>(() => ProfileImport.ReadFile(notHub), "sources that are no list");

            string big = Path.Combine(directory, "big.json");
            File.WriteAllText(big, "{\"name\":\"" + new string('a', ProfileStore.MaxBytes) + "\"}");
            Assert.That(() => ProfileImport.ReadFile(big), Throws.Exception.With.Message.Contains("1 MB"));
        }

        [Test]
        public void AnAddressIsHttpsWithoutCredentialsAndItsRedirectsStayOnItsHost()
        {
            Assert.Throws<ArgumentException>(() => ProfileImport.CheckUrl("http://example.com/hub.json"));
            Assert.DoesNotThrow(() => ProfileImport.CheckUrl("http://localhost:8080/hub.json"));
            Assert.DoesNotThrow(() => ProfileImport.CheckUrl("http://127.0.0.1/hub.json"));
            Assert.Throws<ArgumentException>(() => ProfileImport.CheckUrl("https://user:pass@example.com/hub.json"));
            Assert.Throws<ArgumentException>(() => ProfileImport.CheckUrl("https://example.com/hub.json?access_token=1"));
            Assert.Throws<ArgumentException>(() => ProfileImport.CheckUrl("ftp://example.com/hub.json"));

            var web = new FakeWeb();
            web.Redirect("https://example.com/hub", "/-/hub.json");
            web.Json("https://example.com/-/hub.json", Hub);
            web.Redirect("https://example.com/moved", "https://elsewhere.example.net/hub.json");
            web.Json("https://example.com/huge", "{\"name\":\"" + new string('a', ProfileStore.MaxBytes) + "\"}");

            Assert.AreEqual("Example Studio", Json.Get(Sync(ProfileImport.Fetch(web, "https://example.com/hub")), "name"), "a redirect on the same host is followed");
            Assert.IsTrue(web.Requests.All(r => r.NoRedirects), "the transport never follows one by itself");
            var refused = Assert.Throws<ProfileRedirectException>(() => Sync(ProfileImport.Fetch(web, "https://example.com/moved")));
            StringAssert.Contains("elsewhere.example.net", refused.Message, "the user is told where it leads");
            Assert.That(() => Sync(ProfileImport.Fetch(web, "https://example.com/huge")), Throws.Exception.With.Message.Contains("1 MB"));
            Assert.That(() => Sync(ProfileImport.Fetch(web, "https://example.com/missing")), Throws.Exception.With.Message.Contains("404"));
        }

        [Test]
        public void AFailedRefreshKeepsTheHubAsLastRead()
        {
            var store = new ProfileStore(file);
            var web = new FakeWeb();
            web.Json("https://example.com/hub.json", Hub);
            ForgeProfile profile = store.Add(Sync(ProfileImport.Fetch(web, "https://example.com/hub.json")), ForgeProfile.FromUrl, "https://example.com/hub.json");
            store.Rename(profile.Id, "Mine");

            web.Json("https://example.com/hub.json", "{\"name\":\"Renamed upstream\",\"sources\":[],\"token\":\"t\"}");
            Assert.IsTrue(Sync(ProfileRefresh.Refresh(store, store.Get(profile.Id), web)));
            Assert.AreEqual("Mine", store.Get(profile.Id).Name, "the user's name survives a refresh");
            Assert.AreEqual("Renamed upstream", Json.Get(store.Get(profile.Id).Hub, "name"));
            Assert.IsNull(Json.Get(store.Get(profile.Id).Hub, "token"), "scrubbed on refresh too");

            web.Routes.Clear();
            Assert.IsFalse(Sync(ProfileRefresh.Refresh(store, store.Get(profile.Id), web, quiet: true)));
            Assert.AreEqual("Renamed upstream", Json.Get(store.Get(profile.Id).Hub, "name"), "kept, silently");
            Assert.Throws<Exception>(() => Sync(ProfileRefresh.Refresh(store, store.Get(profile.Id), web)), "asked by the user, the failure is told");

            ForgeProfile local = store.Add(Json.Parse(Hub), ForgeProfile.FromFile);
            Assert.IsFalse(Sync(ProfileRefresh.Refresh(store, local, web)), "a file profile has no address to read again");
        }

        [Test]
        public void TheCatalogueReadsTheActiveProfileThenTheUsersSourcesThenThePresets()
        {
            var store = new ProfileStore(file);
            ForgeProfile profile = store.Add(Json.Parse(Hub), ForgeProfile.FromUrl, "https://example.com/hub.json");
            var mine = new List<HubSource> { new HubSource { Type = HubSource.Manifest, Url = "https://mine.example.com/hub.json" } };
            var presets = new List<HubSource> { new HubSource { Type = HubSource.Package, Url = "io.example.base", Name = "Base" }, new HubSource { Type = HubSource.Package, Url = "io.example.hidden" } };
            var hidden = new HashSet<string> { "io.example.hidden" };

            List<HubSource> none = HubSources.Compose(null, mine, presets, hidden);
            CollectionAssert.AreEqual(new[] { "manifest:https://mine.example.com/hub.json", "package:io.example.base" }, none.Select(s => s.Key).ToArray(), "no profile: the user's sources and the presets");

            List<HubSource> with = HubSources.Compose(profile, mine, presets, hidden);
            Assert.AreEqual(HubSource.Profile, with[0].Type);
            Assert.AreEqual(profile.Id, with[0].Url);
            Assert.AreEqual(3, with.Count, "the user's own sources stay beside the profile");
            Assert.AreEqual(HubCatalogReader.StoreId(HubSource.Manifest, "https://example.com/hub.json"), profile.StoreId, "a URL profile pins the same store key as that hub added as a source");
            Assert.AreEqual("profile:" + store.Add(Json.Parse(Hub), ForgeProfile.FromFile).Id, store.Profiles().Last().StoreId);
        }

        [Test]
        public void APackageAnotherProfileGaveIsFromThatProfileNotFromASourceGone()
        {
            var store = new ProfileStore(file);
            ForgeProfile team = store.Add(Json.Parse(Hub), ForgeProfile.FromUrl, "https://example.com/hub.json");
            ForgeProfile other = store.Add(Json.Parse("{\"name\":\"Other\",\"sources\":[]}"), ForgeProfile.FromFile);
            List<ForgeProfile> all = store.Profiles();
            var keys = new HashSet<string> { "profile:" + other.Id, "manifest:https://mine.example.com/hub.json" };

            var fromTeam = new InstallRecord.Entry { Source = "profile:" + team.Id, Label = "Example Studio" };
            Assert.AreEqual(("From profile Example Studio, not the active one.", false), InstalledPackages.SourceNote(fromTeam, keys, all, other.Id));
            Assert.AreEqual(("Its profile, Example Studio, no longer lists it (or did not answer this time).", true), InstalledPackages.SourceNote(fromTeam, keys, all, team.Id));

            var removed = new InstallRecord.Entry { Source = "profile:0000000000000000", Label = "Gone" };
            Assert.AreEqual(("From another profile (Gone, since removed).", false), InstalledPackages.SourceNote(removed, keys, all, other.Id));

            // Installed in 1.3.0 from a source the team's profile now carries.
            var fromGitHub = new InstallRecord.Entry { Source = "github:example-org/unity-*", Label = "GitHub example-org/unity-*" };
            Assert.AreEqual(("From profile Example Studio, not the active one.", false), InstalledPackages.SourceNote(fromGitHub, keys, all, other.Id));
            var fromHubUrl = new InstallRecord.Entry { Source = "manifest:https://example.com/hub.json", Label = "Example" };
            Assert.AreEqual(("From profile Example Studio, not the active one.", false), InstalledPackages.SourceNote(fromHubUrl, keys, all, null));

            var listed = new InstallRecord.Entry { Source = "manifest:https://mine.example.com/hub.json", Label = "Mine" };
            Assert.IsTrue(InstalledPackages.SourceNote(listed, keys, all, other.Id).Warning);
            var gone = new InstallRecord.Entry { Source = "manifest:https://gone.example.com/hub.json", Label = "Gone" };
            Assert.AreEqual(("Its source, Gone, is no longer among your sources.", true), InstalledPackages.SourceNote(gone, keys, all, other.Id));
        }

        [Test]
        public void TheActiveProfilesThemeAndNameHeadTheWindow()
        {
            var store = new ProfileStore(file);
            ForgeProfile profile = store.Add(Json.Parse("{\"name\":\"Team\",\"theme\":{\"accent\":\"#123456\"},\"sources\":[]}"), ForgeProfile.FromFile);
            store.Rename(profile.Id, "Our team");
            store.SetActive(profile.Id);
            Func<ProfileStore> previous = HubSources.Profiles;
            HubSources.Profiles = () => store;
            try
            {
                HubCatalogResult result = Sync(HubCatalogReader.Read(new[] { store.ActiveProfile.Source }));
                CollectionAssert.IsEmpty(result.Errors);
                Assert.AreEqual("Our team", result.Profile, "the user's name for it");
                Assert.AreEqual("#123456", result.Theme.Accent);
                Assert.IsTrue(result.ThemeFromProfile);
            }
            finally
            {
                HubSources.Profiles = previous;
            }
        }
    }
}
