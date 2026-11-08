using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Glitchr.Store
{
    /// <summary>
    /// The open project's Packages/manifest.json, as the hub changes it: a scoped registry for a
    /// source (its scopes merged into an existing entry of the same URL), a dependency set or
    /// removed. The Package Manager resolves the file again when it changes.
    /// </summary>
    public sealed class ProjectManifest
    {
        public string Path { get; }
        private readonly Dictionary<string, object> root;

        public ProjectManifest(string path = null)
        {
            Path = path ?? System.IO.Path.Combine(Directory.GetCurrentDirectory(), "Packages", "manifest.json");
            root = Json.Object(Json.Parse(File.ReadAllText(Path))) ?? throw new FormatException(Path + " is no JSON object.");
        }

        public Dictionary<string, object> Dependencies
        {
            get
            {
                if (!(root.TryGetValue("dependencies", out object value) && value is Dictionary<string, object> map))
                {
                    map = new Dictionary<string, object>();
                    root["dependencies"] = map;
                }

                return map;
            }
        }

        public string Version(string name) => Dependencies.TryGetValue(name, out object v) ? v as string : null;

        public void SetDependency(string name, string version) => Dependencies[name] = version;

        public bool RemoveDependency(string name) => Dependencies.Remove(name);

        /// <summary>The registry, with these scopes: added, or its scopes completed. True when the file changes.</summary>
        public bool AddScopedRegistry(string name, string url, IEnumerable<string> scopes)
        {
            url = url.TrimEnd('/');
            if (!(root.TryGetValue("scopedRegistries", out object value) && value is List<object> registries))
            {
                registries = new List<object>();
                root["scopedRegistries"] = registries;
            }
            var wanted = scopes.Where(s => !string.IsNullOrEmpty(s)).ToList();
            foreach (var registry in registries.OfType<Dictionary<string, object>>())
            {
                if (!string.Equals((Json.String(Json.Get(registry, "url")) ?? string.Empty).TrimEnd('/'), url, StringComparison.OrdinalIgnoreCase)) continue;
                var current = Json.Array(Json.Get(registry, "scopes")) ?? new List<object>();
                bool changed = false;
                foreach (string scope in wanted)
                {
                    if (current.OfType<string>().Contains(scope)) continue;
                    current.Add(scope);
                    changed = true;
                }
                registry["scopes"] = current;

                return changed;
            }
            registries.Add(new Dictionary<string, object> { ["name"] = name, ["url"] = url, ["scopes"] = wanted.Cast<object>().ToList() });

            return true;
        }

        public void Save() => File.WriteAllText(Path, Json.Write(root, true) + "\n");
    }
}
