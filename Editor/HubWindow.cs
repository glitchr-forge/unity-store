using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Glitchr.Store
{
    /// <summary>
    /// Window > Forge Store: the sources' packages (search, versions and changelogs), installed,
    /// updated and removed in the open project in one click; the hubs' games (played in the browser
    /// when free, downloaded once bought); the sources, added on the fly - a hub manifest, a UPM
    /// registry, a Git URL. "Buy" appears only for a source whose hub sells (its manifest's
    /// checkout); the key it brings is kept in the user's .upmconfig.toml, never in the project.
    /// </summary>
    public sealed class HubWindow : EditorWindow
    {
        private enum Tab { Packages, Games, Sources }

        private Tab tab;
        private string search = string.Empty;
        private Vector2 scroll;
        private HubCatalogResult catalog;
        private Dictionary<string, string> installed = new Dictionary<string, string>();
        private readonly HashSet<string> open = new HashSet<string>();
        private bool busy;
        private string status;
        private HubCheckout.Pending pending;
        private bool cancelPurchase;

        private string newType = HubSource.Manifest;
        private string newUrl = string.Empty;
        private string newName = string.Empty;
        private string newScopes = string.Empty;
        private string newCertificate = string.Empty;

        [MenuItem("Window/Forge Store")]
        public static void Open() => GetWindow<HubWindow>("Forge Store").Show();

        private void OnEnable()
        {
            minSize = new Vector2(420, 320);
            Refresh();
        }

        private async void Refresh()
        {
            if (busy) return;
            busy = true;
            status = "Reading the sources…";
            Repaint();
            try
            {
                catalog = await HubCatalogReader.Read(HubSources.All());
                installed = await HubInstaller.Installed();
                titleContent = new GUIContent(catalog.Brand ?? "Forge Store");
                status = catalog.Errors.Count > 0 ? string.Join("\n", catalog.Errors) : null;
            }
            catch (Exception e)
            {
                status = e.Message;
            }
            busy = false;
            Repaint();
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                tab = (Tab)GUILayout.Toolbar((int)tab, new[] { "Packages", "Games", "Sources" }, EditorStyles.toolbarButton, GUILayout.Width(240));
                GUILayout.FlexibleSpace();
                if (tab != Tab.Sources) search = GUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60))) Refresh();
                }
            }

            if (pending != null)
            {
                EditorGUILayout.HelpBox("Buying " + pending.Item.Label + ": confirm the code " + pending.UserCode + " in your browser, buy, then link this editor. This window waits for the key.", MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Open the page again")) Application.OpenURL(pending.Link);
                    if (GUILayout.Button("Cancel")) cancelPurchase = true;
                }
            }
            if (!string.IsNullOrEmpty(status)) EditorGUILayout.HelpBox(status, MessageType.None);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            switch (tab)
            {
                case Tab.Packages: DrawPackages(); break;
                case Tab.Games: DrawGames(); break;
                case Tab.Sources: DrawSources(); break;
            }
            EditorGUILayout.EndScrollView();
        }

        private bool Matches(params string[] texts)
        {
            return string.IsNullOrEmpty(search) || texts.Any(t => !string.IsNullOrEmpty(t) && t.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        private void DrawPackages()
        {
            if (catalog == null) return;
            foreach (HubItem item in catalog.Items.Where(i => Matches(i.Name, i.DisplayName, i.Description)))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    installed.TryGetValue(item.Name, out string current);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Label(item.Label, EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                        GUILayout.Label(current != null ? "installed " + current : item.Version ?? string.Empty, EditorStyles.miniLabel);
                    }
                    GUILayout.Label(item.Name + (item.Origin?.HubName != null ? "  ·  " + item.Origin.HubName : string.Empty), EditorStyles.miniLabel);
                    if (!string.IsNullOrEmpty(item.Description)) GUILayout.Label(item.Description, EditorStyles.wordWrappedLabel);

                    using (new EditorGUILayout.HorizontalScope())
                    using (new EditorGUI.DisabledScope(busy || pending != null))
                    {
                        if (item.Paid && !item.Installable)
                        {
                            // Only a hub that sells offers to buy; another one says what it is.
                            if (item.Origin != null && item.Origin.Sells)
                            {
                                if (GUILayout.Button("Buy" + (item.Price != null ? " · " + item.Price : string.Empty), GUILayout.Width(150))) Buy(item);
                            }
                            else
                            {
                                GUILayout.Label("Licensed", EditorStyles.miniLabel);
                            }
                        }
                        else if (current == null)
                        {
                            if (GUILayout.Button("Install", GUILayout.Width(90))) Run("Installing " + item.Label, () => HubInstaller.Install(item));
                        }
                        else
                        {
                            if (item.Version != null && current != item.Version && GitUrlOrNewer(item, current) && GUILayout.Button("Update to " + item.Version, GUILayout.Width(130)))
                            {
                                Run("Updating " + item.Label, () => HubInstaller.Install(item));
                            }
                            if (GUILayout.Button("Remove", GUILayout.Width(80))) Run("Removing " + item.Label, () => HubInstaller.Remove(item.Name));
                        }
                        GUILayout.FlexibleSpace();
                        if (!string.IsNullOrEmpty(item.Page) && GUILayout.Button("Page", GUILayout.Width(60))) Application.OpenURL(item.Page);
                    }

                    if (item.Versions.Count > 0)
                    {
                        bool unfolded = EditorGUILayout.Foldout(open.Contains(item.Name), "Versions and changes", true);
                        if (unfolded) open.Add(item.Name); else open.Remove(item.Name);
                        if (unfolded)
                        {
                            foreach (HubVersion version in item.Versions)
                            {
                                GUILayout.Label(version.Version + (version.Date != null ? "  ·  " + version.Date.Substring(0, Math.Min(10, version.Date.Length)) : string.Empty), EditorStyles.miniBoldLabel);
                                if (!string.IsNullOrEmpty(version.Changelog)) GUILayout.Label(version.Changelog, EditorStyles.wordWrappedMiniLabel);
                            }
                        }
                    }
                }
            }
            if (catalog.Items.Count == 0 && !busy) GUILayout.Label("No package: add a source.", EditorStyles.centeredGreyMiniLabel);
        }

        private static bool GitUrlOrNewer(HubItem item, string current)
        {
            if (!string.IsNullOrEmpty(item.GitUrl)) return false;
            try { return new Version(Numeric(item.Version)) > new Version(Numeric(current)); }
            catch { return item.Version != current; }
        }

        private static string Numeric(string version)
        {
            string head = new string(version.TakeWhile(c => char.IsDigit(c) || c == '.').ToArray()).Trim('.');

            return head.Contains('.') ? head : head + ".0";
        }

        private void DrawGames()
        {
            if (catalog == null) return;
            foreach (HubGame game in catalog.Games.Where(g => Matches(g.Name, g.Tagline)))
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(game.Name, EditorStyles.boldLabel);
                    if (!string.IsNullOrEmpty(game.Tagline)) GUILayout.Label(game.Tagline, EditorStyles.wordWrappedLabel);
                    if (game.Platforms.Count > 0) GUILayout.Label(string.Join(" · ", game.Platforms), EditorStyles.miniLabel);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        // A free game plays in the browser and downloads; a paid one downloads once bought.
                        if (!string.IsNullOrEmpty(game.Play) && GUILayout.Button(game.Preview ? "Play the preview" : "Play in the browser", GUILayout.Width(150))) Application.OpenURL(game.Play);
                        if (!string.IsNullOrEmpty(game.Page) && GUILayout.Button(game.Paid ? "Buy and download" + (game.Price != null ? " · " + game.Price : string.Empty) : "Download", GUILayout.Width(190))) Application.OpenURL(game.Page);
                        GUILayout.FlexibleSpace();
                    }
                }
            }
            if (catalog.Games.Count == 0 && !busy) GUILayout.Label("No game in these sources.", EditorStyles.centeredGreyMiniLabel);
        }

        private void DrawSources()
        {
            foreach (HubSource source in HubSources.All())
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(source.Type, EditorStyles.miniLabel, GUILayout.Width(60));
                    GUILayout.Label(source.Label + (source.Scopes.Count > 0 ? "  (" + string.Join(", ", source.Scopes) + ")" : string.Empty), EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                    {
                        HubSources.Remove(source);
                        Refresh();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("Add a source", EditorStyles.boldLabel);
            int type = Array.IndexOf(new[] { HubSource.Manifest, HubSource.Upm, HubSource.Git }, newType);
            newType = new[] { HubSource.Manifest, HubSource.Upm, HubSource.Git }[EditorGUILayout.Popup("Kind", Math.Max(0, type), new[] { "Hub (manifest URL)", "UPM registry", "Git package" })];
            newUrl = EditorGUILayout.TextField(newType == HubSource.Manifest ? "Manifest URL" : newType == HubSource.Upm ? "Registry URL" : "Git URL", newUrl);
            newName = EditorGUILayout.TextField("Name", newName);
            if (newType == HubSource.Upm) newScopes = EditorGUILayout.TextField("Scopes", newScopes);
            if (newType != HubSource.Git) newCertificate = EditorGUILayout.TextField(new GUIContent("Certificate SHA-256", "Development only: a self-signed certificate's fingerprint."), newCertificate);
            using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(newUrl)))
            {
                if (GUILayout.Button("Add"))
                {
                    HubSources.Add(new HubSource { Type = newType, Url = newUrl.Trim(), Name = newName.Trim(), Scopes = HubSources.ParseScopes(newScopes), CertificateSha256 = string.IsNullOrWhiteSpace(newCertificate) ? null : newCertificate.Trim() });
                    newUrl = newName = newScopes = newCertificate = string.Empty;
                    Refresh();
                }
            }
        }

        private async void Run(string what, Func<System.Threading.Tasks.Task> action)
        {
            busy = true;
            status = what + "…";
            Repaint();
            try
            {
                await action();
                installed = await HubInstaller.Installed();
                status = what + ": done.";
            }
            catch (Exception e)
            {
                status = what + ": " + e.Message;
            }
            busy = false;
            Repaint();
        }

        private async void Buy(HubItem item)
        {
            cancelPurchase = false;
            try
            {
                string key = await HubCheckout.Buy(item, started => { pending = started; Repaint(); }, () => cancelPurchase);
                pending = null;
                if (key == null)
                {
                    status = "The purchase was not completed.";
                }
                else if (EditorUtility.DisplayDialog("Key received", "Your licence key is kept for " + item.Origin.Registry + " in " + UpmConfig.FilePath + ". Unity's Package Manager reads it when Unity starts: restart this project now, then install " + item.Label + " from this window.", "Restart now", "Later"))
                {
                    HubCheckout.RestartProject();
                }
                else
                {
                    status = "Key kept. Restart Unity, then install " + item.Label + ".";
                }
            }
            catch (Exception e)
            {
                pending = null;
                status = e.Message;
            }
            Repaint();
        }
    }
}
