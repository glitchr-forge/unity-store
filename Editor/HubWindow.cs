using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Glitchr.Store
{
    /// <summary>
    /// Window > Forge Store: the sources' packages (search, versions and changelogs), installed,
    /// updated and removed in the open project in one click; the hubs' games (played in the browser
    /// when free, downloaded once bought); the sources, added on the fly - a hub manifest, a UPM
    /// registry, a Git URL, a GitHub owner with a repository pattern - or preset by a package of the
    /// project. "Buy" appears only for a source whose hub sells (its manifest's checkout); the key it
    /// brings is kept in the user's .upmconfig.toml, never in the project.
    /// </summary>
    public sealed class HubWindow : EditorWindow
    {
        private enum Tab { Packages, Games, Sources }

        /// <summary>The package's former identifiers: a project that still has one is offered to remove it.</summary>
        public static readonly string[] FormerNames = { "io.glitchr.hub", "com.glitchr.hub" };

        private static readonly string[] Kinds = { HubSource.Manifest, HubSource.Upm, HubSource.Git, HubSource.GitHub };
        private static readonly string[] KindLabels = { "Hub (manifest URL)", "UPM registry", "Git package", "GitHub owner and pattern" };

        private Tab tab;
        private string search = string.Empty;
        private Vector2 scroll;
        private HubCatalogResult catalog;
        private Dictionary<string, string> installed = new Dictionary<string, string>();
        private readonly HashSet<string> open = new HashSet<string>();
        private readonly Dictionary<string, string> chosenRef = new Dictionary<string, string>();
        private readonly Dictionary<string, List<string>> branches = new Dictionary<string, List<string>>();
        private bool busy;
        private string status;
        private HubCheckout.Pending pending;
        private bool cancelPurchase;

        private string newType = HubSource.Manifest;
        private string newUrl = string.Empty;
        private string newName = string.Empty;
        private string newScopes = string.Empty;
        private string newCertificate = string.Empty;
        private string newOwner = string.Empty;
        private string newPattern = "unity-*";
        private string pastedToken = string.Empty;

        private string logoUrl;
        private Texture2D logo;

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
                titleContent = new GUIContent(catalog.Theme?.Name ?? "Forge Store");
                status = catalog.Errors.Count > 0 ? string.Join("\n", catalog.Errors) : null;
                LoadLogo(catalog.Theme?.Logo);
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
            DrawHeader();
            DrawFormerNames();

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

        /// <summary>The theme of the first hub that has one (a preset's first): its logo, name and accent. None: nothing drawn.</summary>
        private void DrawHeader()
        {
            HubTheme theme = catalog?.Theme;
            if (theme == null) return;
            Rect bar = GUILayoutUtility.GetRect(0, 34, GUILayout.ExpandWidth(true));
            Color accent = Accent(theme);
            EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 3, bar.width, 3), accent);
            float x = bar.x + 8;
            if (logo != null)
            {
                GUI.DrawTexture(new Rect(x, bar.y + 4, 24, 24), logo, ScaleMode.ScaleToFit);
                x += 30;
            }
            GUI.Label(new Rect(x, bar.y + 7, bar.width - x, 20), theme.Name ?? "Forge Store", EditorStyles.boldLabel);
        }

        private static Color Accent(HubTheme theme)
        {
            string text = EditorGUIUtility.isProSkin ? theme?.AccentDark ?? theme?.Accent : theme?.Accent;

            return text != null && ColorUtility.TryParseHtmlString(text, out Color colour) ? colour : new Color(0.5f, 0.5f, 0.5f);
        }

        /// <summary>A logo from the theme, 1 MB at most; anything that is not an image is dropped.</summary>
        private void LoadLogo(string url)
        {
            if (url == logoUrl) return;
            logoUrl = url;
            logo = null;
            if (string.IsNullOrEmpty(url) || !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;
            var request = UnityWebRequestTexture.GetTexture(url, true);
            request.timeout = 20;
            request.SendWebRequest().completed += _ =>
            {
                if (request.result == UnityWebRequest.Result.Success && request.downloadedBytes <= 1024 * 1024 && url == logoUrl)
                {
                    logo = DownloadHandlerTexture.GetContent(request);
                    Repaint();
                }
                request.Dispose();
            };
        }

        /// <summary>The project still has the package under a former name: offered to remove it (Unity has no alias).</summary>
        private void DrawFormerNames()
        {
            foreach (string former in FormerNames.Where(installed.ContainsKey))
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label(former + " is Forge Store's former name: this window replaces it.", EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUI.DisabledScope(busy))
                    {
                        if (GUILayout.Button("Remove " + former, GUILayout.Width(170))) Run("Removing " + former, () => HubInstaller.Remove(former));
                    }
                }
            }
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
                Rect card = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                if (item.Origin?.Theme != null) EditorGUI.DrawRect(new Rect(card.x, card.y, 3, card.height), Accent(item.Origin.Theme));
                installed.TryGetValue(item.Name, out string current);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(item.Label, EditorStyles.boldLabel);
                    if (item.GitHub != null && item.GitHub.Private) GUILayout.Label("private", EditorStyles.miniLabel);
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
                    else if (item.GitHub != null)
                    {
                        DrawGitHubActions(item, current);
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

                if (item.Versions.Count > 0 && item.GitHub == null)
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
                EditorGUILayout.EndVertical();
            }
            if (catalog.Items.Count == 0 && !busy) GUILayout.Label("No package: add a source.", EditorStyles.centeredGreyMiniLabel);
        }

        /// <summary>
        /// A GitHub package: a tag or branch picked (the latest tag by default; branches asked of
        /// GitHub only when wanted), installed by its Git URL; an update offered when a newer tag exists.
        /// </summary>
        private void DrawGitHubActions(HubItem item, string current)
        {
            GitHubPackage package = item.GitHub;
            string key = package.Owner + "/" + package.Repository;
            var refs = new List<string>(package.Tags);
            string defaultLabel = "default branch" + (string.IsNullOrEmpty(package.DefaultBranch) ? string.Empty : " (" + package.DefaultBranch + ")");
            if (branches.TryGetValue(key, out List<string> known)) refs.AddRange(known.Where(b => !refs.Contains(b)));
            var labels = new List<string>(refs) { defaultLabel };
            if (!branches.ContainsKey(key)) labels.Add("Other branches…");
            chosenRef.TryGetValue(key, out string chosen);
            int index = chosen == null ? (package.LatestTag != null ? refs.IndexOf(package.LatestTag) : refs.Count) : chosen == string.Empty ? refs.Count : refs.IndexOf(chosen);
            int picked = EditorGUILayout.Popup(Math.Max(0, index), labels.ToArray(), GUILayout.Width(150));
            if (picked == refs.Count + 1 && !branches.ContainsKey(key))
            {
                LoadBranches(package);
            }
            else if (picked != index)
            {
                chosenRef[key] = picked >= refs.Count ? string.Empty : refs[picked];
            }
            chosenRef.TryGetValue(key, out chosen);
            string reference = chosen == null ? package.LatestTag : chosen.Length == 0 ? null : chosen;
            // Unity needs a reference for a Git URL to be fixed: the default branch is "#" + its name.
            string target = reference ?? package.DefaultBranch;

            if (current == null)
            {
                if (GUILayout.Button("Install", GUILayout.Width(90))) Run("Installing " + item.Label, () => HubInstaller.Install(item, target));
            }
            else
            {
                if (chosen == null && GitHubCatalog.Updatable(current, package.LatestVersion))
                {
                    if (GUILayout.Button("Update to " + (package.LatestTag ?? package.LatestVersion), GUILayout.Width(150))) Run("Updating " + item.Label, () => HubInstaller.Install(item, package.LatestTag ?? package.DefaultBranch));
                }
                else if (chosen != null && GUILayout.Button("Switch to " + (reference ?? package.DefaultBranch), GUILayout.Width(150)))
                {
                    Run("Moving " + item.Label, () => HubInstaller.Install(item, target));
                }
                if (GUILayout.Button("Remove", GUILayout.Width(80))) Run("Removing " + item.Label, () => HubInstaller.Remove(item.Name));
            }
        }

        private async void LoadBranches(GitHubPackage package)
        {
            string key = package.Owner + "/" + package.Repository;
            branches[key] = new List<string>();
            try
            {
                GitHubApi api = HubCatalogReader.GitHubClient();
                branches[key] = await GitHubCatalog.Branches(api, package.Owner, package.Repository);
                api.SaveCache();
            }
            catch (Exception e)
            {
                status = key + ": " + e.Message;
            }
            Repaint();
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
                    GUILayout.Label(source.Type == HubSource.Package ? "preset" : source.Type, EditorStyles.miniLabel, GUILayout.Width(60));
                    string detail = source.Type == HubSource.Package ? "  (from " + source.Url + ")" : source.Scopes.Count > 0 ? "  (" + string.Join(", ", source.Scopes) + ")" : string.Empty;
                    GUILayout.Label(source.Label + detail, EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button(source.Type == HubSource.Package ? "Hide" : "Remove", GUILayout.Width(70)))
                    {
                        HubSources.Remove(source);
                        Refresh();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            foreach (string hidden in HubSources.Hidden())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Hidden preset of " + hidden, EditorStyles.miniLabel);
                    if (GUILayout.Button("Show", GUILayout.Width(70)))
                    {
                        HubSources.SetHidden(hidden, false);
                        Refresh();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("Add a source", EditorStyles.boldLabel);
            int type = Array.IndexOf(Kinds, newType);
            newType = Kinds[EditorGUILayout.Popup("Kind", Math.Max(0, type), KindLabels)];
            if (newType == HubSource.GitHub)
            {
                newOwner = EditorGUILayout.TextField(new GUIContent("Owner", "A GitHub organisation or user."), newOwner);
                newPattern = EditorGUILayout.TextField(new GUIContent("Repositories", "A name pattern: * for any run of characters, several separated by commas."), newPattern);
            }
            else
            {
                newUrl = EditorGUILayout.TextField(newType == HubSource.Manifest ? "Manifest URL" : newType == HubSource.Upm ? "Registry URL" : "Git URL", newUrl);
            }
            newName = EditorGUILayout.TextField("Name", newName);
            if (newType == HubSource.Upm) newScopes = EditorGUILayout.TextField("Scopes", newScopes);
            if (newType == HubSource.Manifest || newType == HubSource.Upm) newCertificate = EditorGUILayout.TextField(new GUIContent("Certificate SHA-256", "Development only: a self-signed certificate's fingerprint."), newCertificate);
            bool ready = newType == HubSource.GitHub ? GitHubCatalog.ValidOwner(newOwner.Trim()) : !string.IsNullOrWhiteSpace(newUrl);
            using (new EditorGUI.DisabledScope(!ready))
            {
                if (GUILayout.Button("Add"))
                {
                    HubSources.Add(newType == HubSource.GitHub
                        ? new HubSource { Type = HubSource.GitHub, Owner = newOwner.Trim(), Pattern = newPattern.Trim(), Name = newName.Trim() }
                        : new HubSource { Type = newType, Url = newUrl.Trim(), Name = newName.Trim(), Scopes = HubSources.ParseScopes(newScopes), CertificateSha256 = string.IsNullOrWhiteSpace(newCertificate) ? null : newCertificate.Trim() });
                    newUrl = newName = newScopes = newCertificate = newOwner = string.Empty;
                    newPattern = "unity-*";
                    Refresh();
                }
            }

            DrawGitHubAccount();
        }

        /// <summary>The GitHub token for private repositories: kept by the system's secret store, or git's own.</summary>
        private void DrawGitHubAccount()
        {
            EditorGUILayout.Space();
            GUILayout.Label("GitHub", EditorStyles.boldLabel);
            GUILayout.Label("Public repositories need nothing. For private ones, a token that can read them (a fine-grained token with Contents: read, or a classic one with repo), kept in " + GitHubToken.StoreName + ", never in the project. Unity's git installs them with your own git credentials.", EditorStyles.wordWrappedMiniLabel);
            if (GitHubToken.HasStored)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("A token is kept.", EditorStyles.miniLabel);
                    if (GUILayout.Button("Forget it", GUILayout.Width(80)))
                    {
                        GitHubToken.Forget();
                        Refresh();
                    }
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    pastedToken = EditorGUILayout.PasswordField("Token", pastedToken);
                    using (new EditorGUI.DisabledScope(!GitHubToken.LooksLikeToken(pastedToken.Trim())))
                    {
                        if (GUILayout.Button("Keep", GUILayout.Width(60)))
                        {
                            try { status = "Token kept in " + GitHubToken.Store(pastedToken) + "."; }
                            catch (Exception e) { status = e.Message; }
                            pastedToken = string.Empty;
                            GUI.FocusControl(null);
                            Refresh();
                        }
                    }
                }
            }
            bool useGit = EditorGUILayout.ToggleLeft("Else use git's credential helper for github.com", GitHubToken.UseGitCredentials);
            if (useGit != GitHubToken.UseGitCredentials) GitHubToken.UseGitCredentials = useGit;
            int? remaining = GitHubRateLimit.Shared.Remaining;
            if (remaining != null) GUILayout.Label("GitHub requests left this hour: " + remaining, EditorStyles.miniLabel);
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
