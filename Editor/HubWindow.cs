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
    /// updated and removed in the open project in one click; the project's installed packages, read
    /// from its manifest and lock (updated all at once, removed); the hubs' games (played in the browser
    /// when free, downloaded once bought); the sources, added on the fly - a hub manifest, a UPM
    /// registry, a Git URL, a GitHub owner with a repository pattern - or preset by a package of the
    /// project. "Buy" appears only for a source whose hub sells (its manifest's checkout); the key it
    /// brings is kept in the user's .upmconfig.toml, never in the project.
    /// </summary>
    public sealed class HubWindow : EditorWindow
    {
        private enum Tab { Packages, Installed, Games, Sources }

        /// <summary>A theme's banner: drawn at this width-to-height ratio, cropped to it.</summary>
        public const float BannerAspect = 6f;
        public const long BannerMaxBytes = 2 * 1024 * 1024;
        public const int BannerMaxPixels = 4096;
        public const long LogoMaxBytes = 1024 * 1024;
        public const int LogoMaxPixels = 1024;

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

        private ThemeImage logo;
        private ThemeImage banner;

        private List<InstalledPackage> projectPackages = new List<InstalledPackage>();
        private InstallRecord record = new InstallRecord(null);
        private Dictionary<string, string> latestKnown = new Dictionary<string, string>();
        private string projectError;
        private bool showIndirect;

        [MenuItem("Window/Forge Store")]
        public static void Open() => GetWindow<HubWindow>("Forge Store").Show();

        private void OnEnable()
        {
            minSize = new Vector2(420, 320);
            logo ??= new ThemeImage(LogoMaxBytes, LogoMaxPixels);
            banner ??= new ThemeImage(BannerMaxBytes, BannerMaxPixels);
            ReadProject();
            Refresh();
        }

        private void OnDisable()
        {
            logo?.Clear();
            banner?.Clear();
        }

        /// <summary>
        /// The project's packages from Packages/manifest.json and packages-lock.json, with the
        /// versions and names Unity already knows - local files only, no network call.
        /// </summary>
        private void ReadProject()
        {
            try
            {
                string folder = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Packages");
                string lockPath = System.IO.Path.Combine(folder, "packages-lock.json");
                var registered = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                var versions = registered.GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().version);
                projectPackages = InstalledPackages.Read(System.IO.File.ReadAllText(System.IO.Path.Combine(folder, "manifest.json")), System.IO.File.Exists(lockPath) ? System.IO.File.ReadAllText(lockPath) : null, versions);
                var names = registered.GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().displayName);
                foreach (InstalledPackage package in projectPackages) package.DisplayName = names.TryGetValue(package.Name, out string display) ? display : null;
                // Unity's recommended version when it has one, else the newest it knows for this editor.
                latestKnown = registered.Where(p => p.versions != null).GroupBy(p => p.name).ToDictionary(g => g.Key, g => string.IsNullOrEmpty(g.First().versions.recommended) ? g.First().versions.latestCompatible : g.First().versions.recommended);
                record = new InstallRecord(InstallRecord.ProjectPath);
                projectError = null;
            }
            catch (Exception e)
            {
                projectError = "The project's packages: " + e.Message;
            }
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
                ReadProject();
                titleContent = new GUIContent(catalog.Theme?.Name ?? "Forge Store");
                status = catalog.Errors.Count > 0 ? string.Join("\n", catalog.Errors) : null;
                logo.Load(catalog.Theme?.Logo, Repaint);
                banner.Load(catalog.Theme?.Banner, Repaint);
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
                tab = (Tab)GUILayout.Toolbar((int)tab, new[] { "Packages", "Installed", "Games", "Sources" }, EditorStyles.toolbarButton, GUILayout.Width(300));
                GUILayout.FlexibleSpace();
                if (tab != Tab.Sources) search = GUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(60))) Refresh();
                }
            }
            DrawHeader();
            DrawFormerNames();
            DrawKeyChanges();

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
                case Tab.Installed: DrawInstalled(); break;
                case Tab.Games: DrawGames(); break;
                case Tab.Sources: DrawSources(); break;
            }
            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// The theme of the hub that has one (a preset's first): its banner, cropped to the window's
        /// width at a fixed ratio, its logo, name and accent. No theme: nothing drawn. No banner, or
        /// one that failed: the plain bar with the logo and the name.
        /// </summary>
        private void DrawHeader()
        {
            HubTheme theme = catalog?.Theme;
            if (theme == null) return;
            Color accent = Accent(theme);
            Texture2D image = banner?.Texture;
            Texture2D mark = logo?.Texture;
            if (image != null)
            {
                float width = Mathf.Max(1, position.width);
                float height = Mathf.Clamp(Mathf.Round(width / BannerAspect), 48, 140);
                Rect area = GUILayoutUtility.GetRect(width, height, GUILayout.ExpandWidth(true));
                GUI.DrawTexture(area, image, ScaleMode.ScaleAndCrop);
                // A strip of the skin's own colour under the name, so it reads on any picture.
                var strip = new Rect(area.x, area.yMax - 27, area.width, 24);
                EditorGUI.DrawRect(strip, EditorGUIUtility.isProSkin ? new Color(0.13f, 0.13f, 0.13f, 0.72f) : new Color(0.93f, 0.93f, 0.93f, 0.78f));
                float x = strip.x + 8;
                if (mark != null)
                {
                    GUI.DrawTexture(new Rect(x, strip.y + 2, 20, 20), mark, ScaleMode.ScaleToFit);
                    x += 26;
                }
                GUI.Label(new Rect(x, strip.y + 3, strip.width - x, 18), theme.Name ?? "Forge Store", EditorStyles.boldLabel);
                EditorGUI.DrawRect(new Rect(area.x, area.yMax - 3, area.width, 3), accent);

                return;
            }
            Rect bar = GUILayoutUtility.GetRect(0, 34, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(new Rect(bar.x, bar.yMax - 3, bar.width, 3), accent);
            float left = bar.x + 8;
            if (mark != null)
            {
                GUI.DrawTexture(new Rect(left, bar.y + 4, 24, 24), mark, ScaleMode.ScaleToFit);
                left += 30;
            }
            GUI.Label(new Rect(left, bar.y + 7, bar.width - left, 20), theme.Name ?? "Forge Store", EditorStyles.boldLabel);
        }

        private static Color Accent(HubTheme theme)
        {
            string text = EditorGUIUtility.isProSkin ? theme?.AccentDark ?? theme?.Accent : theme?.Accent;

            return text != null && ColorUtility.TryParseHtmlString(text, out Color colour) ? colour : new Color(0.5f, 0.5f, 0.5f);
        }

        /// <summary>
        /// A store whose hub now announces another licensing key than the pinned one, without a
        /// rotation the pinned key signed: the user accepts the new key or keeps the pinned one;
        /// until then the store's licences are not trusted and it sells nothing here.
        /// </summary>
        private void DrawKeyChanges()
        {
            if (catalog == null) return;
            foreach (HubLicensing licensing in catalog.Licensing.Where(l => l.Check == KeyCheck.Changed))
            {
                KeyPins.Pin pin = StoreTrust.Pins.Get(licensing.Store);
                if (pin?.Pending == null) continue;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.HelpBox(
                        (licensing.StoreName ?? licensing.Store) + " announces a new licensing key. Pinned: " + KeyPins.Fingerprint(pin.Key) + "; announced: " + KeyPins.Fingerprint(pin.Pending)
                        + ". It did not pass by itself: " + pin.Reason + ". Until you decide, this store's licences are not trusted. Accept it only if the store told you it changed its key.",
                        MessageType.Warning);
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button("Keep the pinned key", GUILayout.Width(150)))
                        {
                            StoreTrust.Pins.Refuse(licensing.Store);
                            StoreTrust.Pins.Save();
                            licensing.Check = KeyCheck.Refused;
                        }
                        if (GUILayout.Button("Accept the new key", GUILayout.Width(150)))
                        {
                            StoreTrust.Pins.Accept(licensing.Store);
                            StoreTrust.Pins.Save();
                            licensing.Check = KeyCheck.Same;
                        }
                    }
                }
            }
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
                        if (item.Origin != null && item.Origin.Sells && StoreTrust.Pins.Get(item.Origin.Store)?.Pending != null)
                        {
                            GUILayout.Label("Its store's key changed: decide above first", EditorStyles.miniLabel);
                        }
                        else if (item.Origin != null && item.Origin.Sells)
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

        /// <summary>
        /// The project's packages, read from its manifest and lock: first the ones a source of
        /// Forge Store offers or Forge Store installed (their updates, all at once; a package whose
        /// source is gone is flagged, not hidden), then the project's other packages. Updates and
        /// removals go through Unity's Package Manager, which resolves every dependency.
        /// </summary>
        private void DrawInstalled()
        {
            if (projectError != null) EditorGUILayout.HelpBox(projectError, MessageType.Warning);
            List<HubItem> items = catalog?.Items ?? new List<HubItem>();
            var sourceKeys = new HashSet<string>(HubSources.All().Select(s => s.Key));
            var ours = new List<(InstalledPackage Package, HubItem Item, InstallRecord.Entry Entry)>();
            var others = new List<InstalledPackage>();
            foreach (InstalledPackage package in projectPackages)
            {
                HubItem item = InstalledPackages.Offer(package, items);
                InstallRecord.Entry entry = record.Get(package.Name);
                if (item != null || entry != null) ours.Add((package, item, entry)); else others.Add(package);
            }

            var updates = ours.Select(o => InstalledPackages.UpdateFrom(o.Package, o.Item)).Where(u => u != null).ToList();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("From your sources (" + ours.Count + ")", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(busy || updates.Count == 0))
                {
                    if (GUILayout.Button(updates.Count == 0 ? "Up to date" : "Update all (" + updates.Count + ")", GUILayout.Width(130)))
                    {
                        Run("Updating " + updates.Count + " package" + (updates.Count > 1 ? "s" : string.Empty), () => HubInstaller.Apply(updates));
                    }
                }
            }
            foreach (var (package, item, entry) in ours.Where(o => Matches(o.Package.Name, o.Package.DisplayName)))
            {
                string from = item?.Origin?.HubName ?? entry?.Label;
                string flag = null;
                if (item == null && catalog != null && !busy)
                {
                    flag = sourceKeys.Contains(entry.Source)
                        ? "Its source, " + entry.Label + ", no longer lists it (or did not answer this time)."
                        : "Its source, " + entry.Label + ", is no longer among your sources.";
                }
                DrawInstalledRow(package, from, InstalledPackages.UpdateFrom(package, item), flag, item?.Origin?.Theme);
            }
            if (ours.Count == 0) GUILayout.Label("No package of the project comes from your sources.", EditorStyles.centeredGreyMiniLabel);

            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("The project's other packages", EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                showIndirect = GUILayout.Toggle(showIndirect, "Dependencies and built-in modules", GUILayout.Width(230));
            }
            foreach (InstalledPackage package in others.Where(p => (showIndirect || p.Direct && !p.Name.StartsWith("com.unity.modules.", StringComparison.Ordinal)) && Matches(p.Name, p.DisplayName)))
            {
                latestKnown.TryGetValue(package.Name, out string latest);
                DrawInstalledRow(package, package.Url, InstalledPackages.UpdateTo(package, latest), null, null);
            }
        }

        private void DrawInstalledRow(InstalledPackage package, string from, PackageUpdate update, string flag, HubTheme theme)
        {
            Rect card = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            if (theme != null) EditorGUI.DrawRect(new Rect(card.x, card.y, 3, card.height), Accent(theme));
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label(package.Label, EditorStyles.boldLabel);
                GUILayout.FlexibleSpace();
                GUILayout.Label(package.Version ?? package.Requested ?? string.Empty, EditorStyles.miniLabel);
            }
            var detail = new List<string> { package.Name, package.Source ?? "not resolved yet" };
            if (!string.IsNullOrEmpty(from)) detail.Add(from);
            if (package.GitDescription != null) detail.Add(package.GitDescription);
            if (!package.Direct && package.Dependents.Count > 0) detail.Add("dependency of " + string.Join(", ", package.Dependents));
            GUILayout.Label(string.Join("  ·  ", detail), EditorStyles.wordWrappedMiniLabel);
            if (flag != null) EditorGUILayout.HelpBox(flag, MessageType.Warning);
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(busy))
            {
                if (update != null && GUILayout.Button("Update to " + update.ToVersion, GUILayout.Width(150))) Run("Updating " + package.Label, () => HubInstaller.Apply(new[] { update }));
                if (package.Direct && GUILayout.Button("Remove", GUILayout.Width(80))) Run("Removing " + package.Label, () => HubInstaller.Remove(package.Name));
                GUILayout.FlexibleSpace();
            }
            EditorGUILayout.EndVertical();
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
            DrawPinnedKeys();
        }

        /// <summary>The stores' licensing keys, pinned on first use: each one's mark, a pending change, and a way to forget it.</summary>
        private void DrawPinnedKeys()
        {
            var pins = StoreTrust.Pins.All.ToList();
            if (pins.Count == 0) return;
            EditorGUILayout.Space();
            GUILayout.Label("Licensing keys", EditorStyles.boldLabel);
            GUILayout.Label("A store's key is pinned the first time its hub is read; licences are checked against it, not against what the hub serves later. "
                + (ForgeSeal.Available ? "Forge Seal checks a key the pinned one signed." : "Forge Seal (io.glitchr.licensing) is not in this project: every change of key waits for you."), EditorStyles.wordWrappedMiniLabel);
            foreach (var pair in pins)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    GUILayout.Label((pair.Value.Name ?? pair.Key) + "  ·  " + KeyPins.Fingerprint(pair.Value.Key) + (pair.Value.Pending != null ? "  ·  new key waiting: " + KeyPins.Fingerprint(pair.Value.Pending) : string.Empty), EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button(new GUIContent("Forget", "Its next key is pinned again on first use."), GUILayout.Width(70)))
                    {
                        StoreTrust.Pins.Forget(pair.Key);
                        StoreTrust.Pins.Save();
                        GUIUtility.ExitGUI();
                    }
                }
            }
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
                ReadProject();
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
