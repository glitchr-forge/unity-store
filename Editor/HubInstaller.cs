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

        /// <summary>
        /// Adds or moves the package: a registry's at that version (the latest when null), a Git
        /// package at that tag or branch (for a GitHub source, its latest tag when null). Private
        /// repositories are fetched by Unity's git with the user's own git credentials: no token is
        /// ever written into the project's manifest.
        /// </summary>
        public static async Task Install(HubItem item, string version = null)
        {
            if (!string.IsNullOrEmpty(item.GitUrl))
            {
                await Add(GitIdentifier(item, version));
                Record(item);

                return;
            }
            var manifest = new ProjectManifest();
            if (EnsureRegistry(manifest, item)) manifest.Save();
            await Add(item.Name + "@" + (version ?? item.Version));
            Record(item);
        }

        /// <summary>
        /// Several updates at once, through one AddAndRemove: one resolve and one recompilation
        /// instead of one per package. Unity's Package Manager resolves every dependency, as for a
        /// single install. The registries the updates need are written into the manifest first.
        /// </summary>
        public static async Task Apply(IList<PackageUpdate> updates, IList<string> removals = null)
        {
            if ((updates == null || updates.Count == 0) && (removals == null || removals.Count == 0)) return;
            updates ??= new List<PackageUpdate>();
            var registryItems = updates.Where(u => u.Item != null && string.IsNullOrEmpty(u.Item.GitUrl) && u.Item.Origin?.Registry != null).ToList();
            if (registryItems.Count > 0)
            {
                var manifest = new ProjectManifest();
                bool changed = false;
                foreach (PackageUpdate update in registryItems) changed |= EnsureRegistry(manifest, update.Item);
                if (changed) manifest.Save();
            }
            AddAndRemoveRequest request = await Wait(Client.AddAndRemove(updates.Select(u => u.Identifier).ToArray(), removals?.ToArray()));
            if (request.Status != StatusCode.Success) throw new Exception(request.Error?.message ?? "The Package Manager refused the changes.");
            if (removals != null && removals.Count > 0)
            {
                var record = new InstallRecord(InstallRecord.ProjectPath);
                foreach (string name in removals) record.Remove(name);
                record.Save();
            }
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
