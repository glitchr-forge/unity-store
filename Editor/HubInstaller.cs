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

                return;
            }
            var manifest = new ProjectManifest();
            var scopes = new List<string>(item.Origin.Scopes);
            if (!scopes.Any(s => item.Name.StartsWith(s, StringComparison.Ordinal)))
            {
                // The package's own scope, when the source names none that covers it.
                scopes.Add(string.Join(".", item.Name.Split('.').Take(2)));
            }
            if (manifest.AddScopedRegistry(item.Origin.RegistryName ?? item.Origin.HubName, item.Origin.Registry, scopes))
            {
                manifest.Save();
            }
            await Add(item.Name + "@" + (version ?? item.Version));
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
