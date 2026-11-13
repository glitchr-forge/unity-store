using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.PackageManager;
using UnityEditor.PackageManager.Requests;

namespace Glitchr.Store
{
    /// <summary>
    /// Installs, updates and removes in the open project: a registry package through its scoped
    /// registry (written in Packages/manifest.json when missing) and the Package Manager's own
    /// Client.Add/Remove, a Git package by its URL.
    /// </summary>
    public static class HubInstaller
    {
        /// <summary>The project's packages: name, version.</summary>
        public static async Task<Dictionary<string, string>> Installed()
        {
            ListRequest request = await Wait(Client.List(true, false));
            if (request.Status != StatusCode.Success) throw new Exception(request.Error?.message ?? "Package list failed.");

            return request.Result.ToDictionary(p => p.name, p => p.version);
        }

        /// <summary>The sources' packages, as the window last read them: where a Git package's dependencies are looked for.</summary>
        public static Func<IEnumerable<HubItem>> Offered = () => Enumerable.Empty<HubItem>();

        /// <summary>
        /// Adds or moves the package: a registry's at that version (the latest when null), a Git
        /// package at that tag or branch (for a GitHub source, its latest tag when null), with the
        /// Git packages it needs that the user's sources offer and the project lacks, in one
        /// AddAndRemove. Private repositories are fetched by Unity's git with the user's own git
        /// credentials: no token is ever written into the project's manifest. Returns the line
        /// telling what was added with it, or null.
        /// </summary>
        public static async Task<string> Install(HubItem item, string version = null, Action<string> progress = null)
        {
            if (!string.IsNullOrEmpty(item.GitUrl))
            {
                string identifier = GitIdentifier(item, version);
                GitDependencyPlan plan = await PlanGit(new[] { (item.Name, identifier) });
                if (plan.Summary != null) progress?.Invoke("Installing " + item.Label + " and, for it, " + string.Join(", ", plan.Added.Select(a => a.Label)) + "…");
                DeclareRegistries(plan.Registries);
                await AddAll(new[] { identifier }.Concat(plan.Added.Select(a => a.Identifier)).ToArray(), null);
                Record(item);
                foreach (GitDependency added in plan.Added) Record(added.Item);

                return Join(plan.Summary, plan.Notes);
            }
            var manifest = new ProjectManifest();
            if (EnsureRegistry(manifest, item)) manifest.Save();
            await Add(item.Name + "@" + (version ?? item.Version));
            Record(item);

            return null;
        }

        /// <summary>
        /// Several updates at once, through one AddAndRemove: one resolve and one recompilation
        /// instead of one per package. Unity's Package Manager resolves every dependency, as for a
        /// single install, except a Git package's Git dependencies, added here. The registries the
        /// updates need are written into the manifest first. Returns the line telling what was added.
        /// </summary>
        public static async Task<string> Apply(IList<PackageUpdate> updates, IList<string> removals = null, Action<string> progress = null)
        {
            if ((updates == null || updates.Count == 0) && (removals == null || removals.Count == 0)) return null;
            updates ??= new List<PackageUpdate>();
            var registryItems = updates.Where(u => u.Item != null && string.IsNullOrEmpty(u.Item.GitUrl) && u.Item.Origin?.Registry != null).Select(u => u.Item).ToList();
            var gitRoots = updates.Where(u => IsGitIdentifier(u.Identifier)).Select(u => (u.Name, u.Identifier)).ToList();
            GitDependencyPlan plan = gitRoots.Count > 0 ? await PlanGit(gitRoots, removals) : new GitDependencyPlan();
            if (plan.Summary != null) progress?.Invoke("Also adding " + string.Join(", ", plan.Added.Select(a => a.Label)) + "…");
            DeclareRegistries(registryItems.Concat(plan.Registries));
            await AddAll(updates.Select(u => u.Identifier).Concat(plan.Added.Select(a => a.Identifier)).ToArray(), removals?.ToArray());
            foreach (GitDependency added in plan.Added) Record(added.Item);
            if (removals != null && removals.Count > 0)
            {
                var record = new InstallRecord(InstallRecord.ProjectPath);
                foreach (string name in removals) record.Remove(name);
                record.Save();
            }

            return Join(plan.Summary, plan.Notes);
        }

        /// <summary>The Git dependencies of these Git identifiers (name, URL#reference) to add, read with the user's GitHub client.</summary>
        private static async Task<GitDependencyPlan> PlanGit(IEnumerable<(string Name, string Identifier)> roots, IEnumerable<string> removals = null)
        {
            var has = new HashSet<string>(ProjectHas());
            foreach (string removed in removals ?? Enumerable.Empty<string>()) has.Remove(removed);
            GitHubApi api = HubCatalogReader.GitHubClient();
            List<HubItem> offered = Offered().ToList();
            GitDependencyPlan plan = await GitDependencies.Plan(roots.Select(r => (r.Name, r.Identifier, GitHubRepository.Of(r.Identifier)?.Reference)), offered, has, GitDependencies.GitHubReader(api, offered));
            try { api.SaveCache(); }
            catch (Exception) { /* the next reading asks GitHub again */ }

            return plan;
        }

        /// <summary>
        /// Every package the project has, in any form: named by its manifest, resolved in its lock,
        /// registered with Unity, or a folder of Packages/ (an embedded package, whatever its manifest says).
        /// </summary>
        public static HashSet<string> ProjectHas(string projectFolder = null)
        {
            projectFolder ??= System.IO.Directory.GetCurrentDirectory();
            string packages = System.IO.Path.Combine(projectFolder, "Packages");
            var names = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                string manifest = System.IO.Path.Combine(packages, "manifest.json");
                string lockFile = System.IO.Path.Combine(packages, "packages-lock.json");
                if (System.IO.File.Exists(manifest)) names.UnionWith((Json.Object(Json.Get(Json.Parse(System.IO.File.ReadAllText(manifest)), "dependencies")) ?? new Dictionary<string, object>()).Keys);
                if (System.IO.File.Exists(lockFile)) names.UnionWith((Json.Object(Json.Get(Json.Parse(System.IO.File.ReadAllText(lockFile)), "dependencies")) ?? new Dictionary<string, object>()).Keys);
            }
            catch (Exception)
            {
                // A file Unity is rewriting: the registered packages and the folders still say.
            }
            if (System.IO.Directory.Exists(packages))
            {
                foreach (string folder in System.IO.Directory.GetDirectories(packages))
                {
                    string json = System.IO.Path.Combine(folder, "package.json");
                    try
                    {
                        string name = System.IO.File.Exists(json) ? Json.String(Json.Get(Json.Parse(System.IO.File.ReadAllText(json)), "name")) : null;
                        names.Add(name ?? System.IO.Path.GetFileName(folder));
                    }
                    catch (Exception)
                    {
                        names.Add(System.IO.Path.GetFileName(folder));
                    }
                }
            }
            if (projectFolder == System.IO.Directory.GetCurrentDirectory())
            {
                foreach (var info in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()) names.Add(info.name);
            }

            return names;
        }

        /// <summary>A Git URL given to the Package Manager, not name@version.</summary>
        public static bool IsGitIdentifier(string identifier) => !string.IsNullOrEmpty(identifier) && (identifier.Contains("://") || identifier.StartsWith("git@", StringComparison.Ordinal));

        private static void DeclareRegistries(IEnumerable<HubItem> items)
        {
            var list = items.Where(i => i?.Origin?.Registry != null).ToList();
            if (list.Count == 0) return;
            var manifest = new ProjectManifest();
            bool changed = false;
            foreach (HubItem item in list) changed |= EnsureRegistry(manifest, item);
            if (changed) manifest.Save();
        }

        private static async Task AddAll(string[] add, string[] remove)
        {
            AddAndRemoveRequest request = await Wait(Client.AddAndRemove(add, remove));
            if (request.Status != StatusCode.Success) throw new Exception(request.Error?.message ?? "The Package Manager refused the changes.");
        }

        private static string Join(string summary, List<string> notes)
        {
            var lines = new List<string>();
            if (summary != null) lines.Add(summary);
            lines.AddRange(notes ?? new List<string>());

            return lines.Count == 0 ? null : string.Join("\n", lines);
        }

        /// <summary>The item's registry, with a scope that covers it, in the manifest; true when the manifest changed.</summary>
        private static bool EnsureRegistry(ProjectManifest manifest, HubItem item)
        {
            var scopes = new List<string>(item.Origin.Scopes);
            if (!scopes.Any(s => item.Name.StartsWith(s, StringComparison.Ordinal)))
            {
                // The package's own scope, when the source names none that covers it.
                scopes.Add(string.Join(".", item.Name.Split('.').Take(2)));
            }

            return manifest.AddScopedRegistry(item.Origin.RegistryName ?? item.Origin.HubName, item.Origin.Registry, scopes);
        }

        /// <summary>The project's record that Forge Store installed it, and from which source; a failure to write it changes nothing else.</summary>
        private static void Record(HubItem item)
        {
            if (item == null) return;
            try
            {
                var record = new InstallRecord(InstallRecord.ProjectPath);
                record.Set(item.Name, item.Origin?.Source);
                record.Save();
            }
            catch (Exception)
            {
                // The package is installed; the Installed tab only loses its source's name.
            }
        }

        /// <summary>The Git URL Unity's Package Manager is given: the URL, then #reference when one applies.</summary>
        public static string GitIdentifier(HubItem item, string reference = null)
        {
            reference ??= item.GitHub?.LatestTag;
            if (string.IsNullOrEmpty(reference)) return item.GitUrl;
            int hash = item.GitUrl.IndexOf('#');

            return (hash >= 0 ? item.GitUrl.Substring(0, hash) : item.GitUrl) + "#" + reference;
        }

        /// <summary>
        /// The package copied from Unity's cache into Packages/&lt;name&gt; (Client.Embed): part of the
        /// project from then on, editable, no longer updated from its source.
        /// </summary>
        public static async Task Embed(string name)
        {
            EmbedRequest request = await Wait(Client.Embed(name));
            if (request.Status != StatusCode.Success) throw new Exception(request.Error?.message ?? "Embedding failed.");
        }

        public static async Task Remove(string name)
        {
            RemoveRequest request = await Wait(Client.Remove(name));
            if (request.Status != StatusCode.Success) throw new Exception(request.Error?.message ?? "Removal failed.");
            try
            {
                var record = new InstallRecord(InstallRecord.ProjectPath);
                if (record.Remove(name)) record.Save();
            }
            catch (Exception)
            {
                // Removed; a stale line in the record is dropped with the next removal.
            }
        }

        private static async Task Add(string identifier)
        {
            AddRequest request = await Wait(Client.Add(identifier));
            if (request.Status != StatusCode.Success) throw new Exception(request.Error?.message ?? "Installation failed.");
        }

        /// <summary>A Package Manager request, awaited on the editor's loop.</summary>
        public static Task<T> Wait<T>(T request) where T : Request
        {
            var completion = new TaskCompletionSource<T>();
            void Check()
            {
                if (!request.IsCompleted) return;
                EditorApplication.update -= Check;
                completion.TrySetResult(request);
            }
            EditorApplication.update += Check;

            return completion.Task;
        }
    }
}
