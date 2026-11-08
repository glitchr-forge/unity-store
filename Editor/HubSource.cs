using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;

namespace Glitchr.Store
{
    /// <summary>
    /// A place packages come from: a hub manifest (a forge's /upm/-/hub.json, a static hub's
    /// hub.json), a UPM registry with its scopes, or a Git package. Kept per user (EditorPrefs),
    /// not per project; Glitch Art's hub is there from the start.
    /// </summary>
    [Serializable]
    public sealed class HubSource
    {
        public const string Manifest = "manifest";
        public const string Upm = "upm";
        public const string Git = "git";

        public string Type = Manifest;
        public string Name;
        public string Url;
        public List<string> Scopes = new List<string>();

        /// <summary>Development only: the SHA-256 of a self-signed certificate this source may present.</summary>
        public string CertificateSha256;

        public string Label => string.IsNullOrEmpty(Name) ? Url : Name;
    }

    public static class HubSources
    {
        public const string GlitchArt = "https://glitchr.io/upm/-/hub.json";
        private const string Key = "Glitchr.Store.Sources";

        [Serializable]
        private sealed class Saved
        {
            public List<HubSource> Sources = new List<HubSource>();
        }

        public static List<HubSource> All()
        {
            string json = EditorPrefs.GetString(Key, string.Empty);
            var saved = string.IsNullOrEmpty(json) ? null : UnityEngine.JsonUtility.FromJson<Saved>(json);
            if (saved == null || saved.Sources.Count == 0)
            {
                return new List<HubSource> { new HubSource { Type = HubSource.Manifest, Name = "Glitch Art", Url = GlitchArt } };
            }

            return saved.Sources;
        }

        public static void Save(List<HubSource> sources)
        {
            EditorPrefs.SetString(Key, UnityEngine.JsonUtility.ToJson(new Saved { Sources = sources }));
        }

        public static void Add(HubSource source)
        {
            var sources = All();
            sources.RemoveAll(s => string.Equals(s.Url?.TrimEnd('/'), source.Url?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase));
            sources.Add(source);
            Save(sources);
        }

        public static void Remove(HubSource source)
        {
            var sources = All();
            sources.RemoveAll(s => s.Url == source.Url && s.Type == source.Type);
            Save(sources);
        }

        /// <summary>Parses "a, b c" into scopes.</summary>
        public static List<string> ParseScopes(string text)
        {
            return (text ?? string.Empty).Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Trim()).ToList();
        }
    }
}
