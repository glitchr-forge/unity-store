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
        private enum Tab { Packages, Installed, Games, Profiles, Sources }

        /// <summary>A URL profile is read again on demand, at most once per editor start (SessionState lasts until Unity quits).</summary>
        private const string RefreshedKey = "Glitchr.Store.ProfileRefreshed.";

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
        /// <summary>The device flow only links this editor: the forge takes one-click purchases.</summary>
        private bool linking;
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

        private List<ForgeProfile> profiles = new List<ForgeProfile>();
        private string activeProfile;
        private string profilesError;
        private string newProfileUrl = string.Empty;
        private string renaming;
        private string renameText = string.Empty;

        private string manifestText;
        private string lockText;
        private readonly Dictionary<string, string> licenceText = new Dictionary<string, string>();
        private readonly HashSet<string> licenceOpen = new HashSet<string>();
        private readonly HashSet<string> machineIdOpen = new HashSet<string>();
        private readonly Dictionary<string, string> getRefusals = new Dictionary<string, string>();
        private readonly Dictionary<string, (LicenceToken Token, string Reason)> licenceVerdicts = new Dictionary<string, (LicenceToken, string)>();

        private Dictionary<string, UnityEditor.PackageManager.PackageInfo> registeredInfo = new Dictionary<string, UnityEditor.PackageManager.PackageInfo>();
        private Dictionary<string, List<PackageSample>> samples = new Dictionary<string, List<PackageSample>>();
        private Dictionary<string, string[]> unityVersions = new Dictionary<string, string[]>();
        private readonly HashSet<string> samplesOpen = new HashSet<string>();

        [MenuItem("Window/Forge Store")]
        public static void Open() => GetWindow<HubWindow>("Forge Store").Show();

        private void OnEnable()
        {
            minSize = new Vector2(420, 320);
            logo ??= new ThemeImage(LogoMaxBytes, LogoMaxPixels);
            banner ??= new ThemeImage(BannerMaxBytes, BannerMaxPixels);
            HubInstaller.Offered = () => catalog?.Items ?? new List<HubItem>();
            ReadProfiles();
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
                manifestText = System.IO.File.ReadAllText(System.IO.Path.Combine(folder, "manifest.json"));
                lockText = System.IO.File.Exists(lockPath) ? System.IO.File.ReadAllText(lockPath) : null;
                projectPackages = InstalledPackages.Read(manifestText, lockText, versions);
                var names = registered.GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().displayName);
                foreach (InstalledPackage package in projectPackages) package.DisplayName = names.TryGetValue(package.Name, out string display) ? display : null;
                // Unity's recommended version when it has one, else the newest it knows for this editor.
                latestKnown = registered.Where(p => p.versions != null).GroupBy(p => p.name).ToDictionary(g => g.Key, g => string.IsNullOrEmpty(g.First().versions.recommended) ? g.First().versions.latestCompatible : g.First().versions.recommended);
                record = new InstallRecord(InstallRecord.ProjectPath);
                registeredInfo = registered.GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First());
                unityVersions = registered.Where(p => p.versions != null).GroupBy(p => p.name).ToDictionary(g => g.Key, g => g.First().versions.compatible ?? new string[0]);
                samples = new Dictionary<string, List<PackageSample>>();
                foreach (var info in registeredInfo.Values)
                {
                    string json = System.IO.Path.Combine(info.resolvedPath ?? string.Empty, "package.json");
                    try
                    {
                        var list = System.IO.File.Exists(json) ? PackageSamples.Read(System.IO.File.ReadAllText(json)) : null;
                        if (list != null && list.Count > 0) samples[info.name] = list;
                    }
                    catch (Exception)
                    {
                        // A package.json that does not parse has no samples to offer.
                    }
                }
                projectError = null;
            }
            catch (Exception e)
            {
                projectError = "The project's packages: " + e.Message;
            }
        }

        /// <summary>The user's profiles, read from the shared file (Forge Hub may have changed it).</summary>
        private void ReadProfiles()
        {
            try
            {
                ProfileStore store = HubSources.Profiles();
                profiles = store.Profiles();
                activeProfile = store.Active;
                profilesError = null;
            }
            catch (Exception e)
            {
                profiles = new List<ForgeProfile>();
                activeProfile = null;
                profilesError = "Your profiles file cannot be read (" + e.Message + "): no profile is used, and it is not rewritten.";
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
                ReadProfiles();
                licenceVerdicts.Clear();
                catalog = await HubCatalogReader.Read(HubSources.All());
                installed = await HubInstaller.Installed();
                ReadProject();
                titleContent = new GUIContent(catalog.Profile ?? catalog.Theme?.Name ?? "Forge Store");
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
                tab = (Tab)GUILayout.Toolbar((int)tab, new[] { "Packages", "Installed", "Games", "Profiles", "Sources" }, EditorStyles.toolbarButton, GUILayout.Width(370));
                GUILayout.FlexibleSpace();
                if (tab != Tab.Sources && tab != Tab.Profiles) search = GUILayout.TextField(search, EditorStyles.toolbarSearchField, GUILayout.MinWidth(120));
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
                EditorGUILayout.HelpBox(linking
                    ? "Linking this editor to " + pending.Item.Origin?.HubName + ": confirm the code " + pending.UserCode + " in your browser and link this editor. You then confirm the purchase of " + pending.Item.Label + " here."
                    : "Buying " + pending.Item.Label + ": confirm the code " + pending.UserCode + " in your browser, buy, then link this editor. This window waits for the key.", MessageType.Info);
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
                case Tab.Profiles: DrawProfiles(); break;
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
            string profileName = catalog?.Profile;
            if (theme == null && profileName == null) return;
            string title = profileName ?? theme?.Name ?? "Forge Store";
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
                GUI.Label(new Rect(x, strip.y + 3, strip.width - x, 18), title, EditorStyles.boldLabel);
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
            GUI.Label(new Rect(left, bar.y + 7, bar.width - left, 20), title, EditorStyles.boldLabel);
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
                            KeyDecision(() => StoreTrust.Pins.Refuse(licensing.Store));
                            licensing.Check = KeyCheck.Refused;
                        }
                        if (GUILayout.Button("Accept the new key", GUILayout.Width(150)))
                        {
                            // The announced block too: a key it names as previous keeps opening its licences, bounded.
                            KeyDecision(() => StoreTrust.Pins.Accept(licensing.Store, licensing));
                            licensing.Check = KeyCheck.Same;
                        }
                    }
                }
            }
        }

        /// <summary>A decision on a pinned key, written at once to the file Forge Hub shares; the licences checked again.</summary>
        private void KeyDecision(Action decide)
        {
            try { decide(); }
            catch (Exception e) { status = "Pinned keys: " + e.Message; }
            licenceVerdicts.Clear();
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
                        if (GUILayout.Button("Install", GUILayout.Width(90))) Run("Installing " + item.Label, progress => HubInstaller.Install(item, null, progress));
                        VersionMenu(item.Name, VersionChoices.For(item), true, item, "Version…");
                    }
                    else
                    {
                        bool newer = item.Version != null && current != item.Version && GitUrlOrNewer(item, current);
                        if (newer && GUILayout.Button("Update to " + item.Version, GUILayout.Width(130)))
                        {
                            Run("Updating " + item.Label, progress => HubInstaller.Install(item, null, progress));
                        }
                        VersionMenu(item.Name, VersionChoices.For(item, current), true, item, newer ? "▾" : "Other version…");
                        if (GUILayout.Button("Remove", GUILayout.Width(80))) Run("Removing " + item.Label, () => HubInstaller.Remove(item.Name));
                    }
                    GUILayout.FlexibleSpace();
                    if (!string.IsNullOrEmpty(item.Page) && GUILayout.Button("Page", GUILayout.Width(60))) Application.OpenURL(item.Page);
                }
                DrawLicence(item);

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
                if (GUILayout.Button("Install", GUILayout.Width(90))) Run("Installing " + item.Label, progress => HubInstaller.Install(item, target, progress));
            }
            else
            {
                bool newer = chosen == null && GitHubCatalog.Updatable(current, package.LatestVersion);
                if (newer)
                {
                    if (GUILayout.Button("Update to " + (package.LatestTag ?? package.LatestVersion), GUILayout.Width(150))) Run("Updating " + item.Label, progress => HubInstaller.Install(item, package.LatestTag ?? package.DefaultBranch, progress));
                }
                else if (chosen != null && GUILayout.Button("Switch to " + (reference ?? package.DefaultBranch), GUILayout.Width(150)))
                {
                    Run("Moving " + item.Label, progress => HubInstaller.Install(item, target, progress));
                }
                VersionMenu(item.Name, VersionChoices.For(item, current, InstalledReference(item.Name)), true, item, newer ? "▾" : "Other version…");
                if (GUILayout.Button("Remove", GUILayout.Width(80))) Run("Removing " + item.Label, () => HubInstaller.Remove(item.Name));
            }
        }

        /// <summary>The installed Git package's reference (its tag), from the project's manifest.</summary>
        private string InstalledReference(string name) => projectPackages.FirstOrDefault(p => p.Name == name)?.GitReference;

        /// <summary>
        /// The Update button's menu: every version known, newest first, older ones to go back; the
        /// installed one greyed. A move across a major version of a package that is not from the
        /// user's sources asks first.
        /// </summary>
        private void VersionMenu(string name, List<VersionChoice> choices, bool fromSources, HubItem item, string label)
        {
            if (choices.Count == 0 || choices.Count == 1 && choices[0].Installed) return;
            if (!GUILayout.Button(new GUIContent(label, "Every version known, older ones included: the one chosen is installed exactly."), GUILayout.Width(label.Length <= 2 ? 22 : 110))) return;
            var menu = new GenericMenu();
            foreach (VersionChoice choice in choices)
            {
                // A "/" would open a sub-menu.
                var text = new GUIContent(choice.Label.Replace('/', '\u2215') + (choice.CrossesMajor && !choice.Installed ? "  · major" : string.Empty));
                if (choice.Installed) menu.AddDisabledItem(text, true);
                else menu.AddItem(text, false, () => Choose(name, choice, fromSources, item));
            }
            menu.ShowAsContext();
        }

        private void Choose(string name, VersionChoice choice, bool fromSources, HubItem item)
        {
            if (VersionChoices.NeedsConfirmation(choice, fromSources)
                && !EditorUtility.DisplayDialog("Another major version", name + " " + choice.Version + " is another major version than the installed one: it may change or break what the project uses. Install it?", "Install " + choice.Version, "Cancel"))
            {
                return;
            }
            string what = "Installing " + name + " " + (choice.Reference ?? choice.Version);
            if (item != null)
            {
                // A GitHub package at the tag; a registry's at the version; a plain Git URL as it is.
                string version = item.GitHub != null ? choice.Reference : string.IsNullOrEmpty(item.GitUrl) ? choice.Version : null;
                Run(what, progress => HubInstaller.Install(item, version, progress));
            }
            else
            {
                Run(what, progress => HubInstaller.Apply(new[] { VersionChoices.Update(name, choice) }, null, progress));
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
                        Run("Updating " + updates.Count + " package" + (updates.Count > 1 ? "s" : string.Empty), progress => HubInstaller.Apply(updates, null, progress));
                    }
                }
            }
            foreach (var (package, item, entry) in ours.Where(o => Matches(o.Package.Name, o.Package.DisplayName)))
            {
                string from = item?.Origin?.HubName ?? entry?.Label;
                (string Text, bool Warning) note = (null, false);
                if (item == null && catalog != null && !busy) note = InstalledPackages.SourceNote(entry, sourceKeys, profiles, activeProfile);
                List<VersionChoice> choices = item != null && package.Direct ? VersionChoices.For(item, package.Version, package.GitReference) : new List<VersionChoice>();
                DrawInstalledRow(package, from, InstalledPackages.UpdateFrom(package, item), note.Text, note.Warning, item?.Origin?.Theme, choices, true, item);
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
                List<VersionChoice> choices = package.Direct && package.Source == InstalledPackage.Registry && unityVersions.TryGetValue(package.Name, out string[] known)
                    ? VersionChoices.ForRegistry(package.Name, known, package.Version)
                    : new List<VersionChoice>();
                DrawInstalledRow(package, package.Url, InstalledPackages.UpdateTo(package, latest), null, false, null, choices, false, null);
            }
        }

        private void DrawInstalledRow(InstalledPackage package, string from, PackageUpdate update, string flag, bool warning, HubTheme theme, List<VersionChoice> choices, bool fromSources, HubItem item)
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
            if (flag != null) EditorGUILayout.HelpBox(flag, warning ? MessageType.Warning : MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(busy))
            {
                if (update != null && GUILayout.Button("Update to " + update.ToVersion, GUILayout.Width(150))) Run("Updating " + package.Label, progress => HubInstaller.Apply(new[] { update }, null, progress));
                VersionMenu(package.Name, choices, fromSources, item, update != null ? "▾" : "Other version…");
                if (package.Direct && GUILayout.Button("Remove", GUILayout.Width(80))) Run("Removing " + package.Label, () => HubInstaller.Remove(package.Name));
                GUILayout.FlexibleSpace();
                string packagesFolder = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "Packages");
                if (PackageSamples.Embeddable(package, packagesFolder) && GUILayout.Button(new GUIContent("Embed", "Copy it into Packages/" + package.Name + " to edit it: part of the project from then on."), GUILayout.Width(70)))
                {
                    if (EditorUtility.DisplayDialog("Embed " + package.Label, package.Name + " is copied from Unity's cache into Packages/" + package.Name + ": you can edit it there, it becomes part of the project, and it no longer updates from its source. Remove that folder to go back.", "Embed", "Cancel"))
                    {
                        Run("Embedding " + package.Label, () => HubInstaller.Embed(package.Name));
                    }
                }
            }
            if (fromSources && item != null) DrawLicence(item);
            DrawSamples(package);
            EditorGUILayout.EndVertical();
        }

        /// <summary>An installed package's samples (its package.json's), each imported into Assets/Samples as Unity's window does.</summary>
        private void DrawSamples(InstalledPackage package)
        {
            if (!samples.TryGetValue(package.Name, out List<PackageSample> list) || !registeredInfo.TryGetValue(package.Name, out var info)) return;
            bool unfolded = EditorGUILayout.Foldout(samplesOpen.Contains(package.Name), "Samples (" + list.Count + ")", true);
            if (unfolded) samplesOpen.Add(package.Name); else samplesOpen.Remove(package.Name);
            if (!unfolded) return;
            foreach (PackageSample sample in list)
            {
                string folder = PackageSamples.ImportFolder(Application.dataPath, info.displayName, info.version, sample.DisplayName);
                bool imported = System.IO.Directory.Exists(folder);
                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label(sample.DisplayName + (string.IsNullOrEmpty(sample.Description) ? string.Empty : "  ·  " + sample.Description), EditorStyles.wordWrappedMiniLabel);
                    using (new EditorGUI.DisabledScope(busy))
                    {
                        if (GUILayout.Button(imported ? "Import again" : "Import", GUILayout.Width(100))) ImportSample(package.Name, info, sample, folder, imported);
                    }
                }
            }
        }

        private void ImportSample(string name, UnityEditor.PackageManager.PackageInfo info, PackageSample sample, string folder, bool imported)
        {
            if (imported && !EditorUtility.DisplayDialog("Import " + sample.DisplayName + " again", "Assets/Samples already has it: its files there are replaced (your changes to them are lost).", "Replace", "Cancel")) return;
            try
            {
                // Unity's own Sample when it knows it (Unity's window then shows it imported), else the same copy.
                var unity = UnityEditor.PackageManager.UI.Sample.FindByPackage(name, info.version).Where(s => s.displayName == sample.DisplayName).ToList();
                bool done = false;
                if (unity.Count == 1)
                {
                    var replace = UnityEditor.PackageManager.UI.Sample.ImportOptions.OverridePreviousImports;
                    done = unity[0].Import(imported ? replace : UnityEditor.PackageManager.UI.Sample.ImportOptions.None);
                    if (!done)
                    {
                        // Unity refuses while another version's import is there, as its own window asks.
                        if (!EditorUtility.DisplayDialog("Previous import", sample.DisplayName + " was imported before from another version: replace that import?", "Replace", "Cancel")) return;
                        done = unity[0].Import(replace);
                    }
                }
                if (!done)
                {
                    PackageSamples.Copy(info.resolvedPath, sample, folder, true);
                    AssetDatabase.Refresh();
                }
                status = sample.DisplayName + " imported into " + folder.Substring(System.IO.Path.GetDirectoryName(Application.dataPath).Length + 1) + ".";
            }
            catch (Exception e)
            {
                status = sample.DisplayName + ": " + e.Message;
            }
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

        /// <summary>
        /// The user's profiles (the per-user profiles.json Forge Hub shares): the active one, or
        /// none; each added by its https address or from a file, renamed, read again, exported,
        /// removed. Then the presets of the project's packages, read only. Switching never changes
        /// the project.
        /// </summary>
        private void DrawProfiles()
        {
            if (profilesError != null) EditorGUILayout.HelpBox(profilesError, MessageType.Warning);
            ProfileStore store = HubSources.Profiles();
            GUILayout.Label("A profile is a hub people share: a team's or a store's sources, look and licensing, as an address or a file. One is active at a time; switching never changes the project. Your own sources (the Sources tab) stay with every profile.", EditorStyles.wordWrappedMiniLabel);
            var names = new List<string> { "No profile (only this project's presets and your sources)" };
            names.AddRange(profiles.Select(p => p.Name.Replace('/', '\u2215')));
            int current = activeProfile == null ? 0 : profiles.FindIndex(p => p.Id == activeProfile) + 1;
            using (new EditorGUI.DisabledScope(busy || profilesError != null))
            {
                int picked = EditorGUILayout.Popup("Active profile", Math.Max(0, current), names.ToArray());
                if (picked != current) ProfileAction(() => store.SetActive(picked == 0 ? null : profiles[picked - 1].Id));
            }

            foreach (ForgeProfile profile in profiles.ToList())
            {
                Rect card = EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                if (profile.Theme != null) EditorGUI.DrawRect(new Rect(card.x, card.y, 3, card.height), Accent(profile.Theme));
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (renaming == profile.Id)
                    {
                        renameText = EditorGUILayout.TextField(renameText);
                        if (GUILayout.Button("Save", GUILayout.Width(60)) && !string.IsNullOrWhiteSpace(renameText))
                        {
                            string name = renameText;
                            renaming = null;
                            ProfileAction(() => store.Rename(profile.Id, name));
                        }
                        if (GUILayout.Button("Cancel", GUILayout.Width(60))) renaming = null;
                    }
                    else
                    {
                        GUILayout.Label(profile.Name + (profile.Id == activeProfile ? "  (active)" : string.Empty), EditorStyles.boldLabel);
                        GUILayout.FlexibleSpace();
                    }
                }
                int sources = (Json.Array(Json.Get(profile.Hub, "sources")) ?? new List<object>()).Count;
                string origin = profile.Origin == ForgeProfile.FromUrl ? profile.Url + (profile.RefreshedAt != null ? "  ·  read " + profile.RefreshedAt : string.Empty) : "from a file, added " + profile.AddedAt;
                GUILayout.Label(origin + "  ·  " + sources + " source" + (sources == 1 ? string.Empty : "s"), EditorStyles.wordWrappedMiniLabel);
                using (new EditorGUILayout.HorizontalScope())
                using (new EditorGUI.DisabledScope(busy))
                {
                    if (profile.Id != activeProfile && GUILayout.Button("Use", GUILayout.Width(60))) ProfileAction(() => store.SetActive(profile.Id));
                    if (GUILayout.Button("Rename", GUILayout.Width(70)))
                    {
                        renaming = profile.Id;
                        renameText = profile.Name;
                    }
                    if (profile.Origin == ForgeProfile.FromUrl)
                    {
                        bool asked = SessionState.GetBool(RefreshedKey + profile.Id, false);
                        using (new EditorGUI.DisabledScope(asked))
                        {
                            if (GUILayout.Button(new GUIContent("Read again", asked ? "Already read (or tried) since Unity started: once per start." : "Reads its address again (once per start); if that fails, the profile as last read stays."), GUILayout.Width(90))) RefreshProfile(profile);
                        }
                    }
                    if (GUILayout.Button(new GUIContent("Export…", "Its hub document, as a file to share: nothing secret is in it."), GUILayout.Width(70)))
                    {
                        string path = EditorUtility.SaveFilePanel("Export the profile", string.Empty, ProfileStore.ExportName(profile.Name), "json");
                        if (!string.IsNullOrEmpty(path))
                        {
                            try { status = "Exported to " + store.Export(profile.Id, path) + "."; }
                            catch (Exception e) { status = "Export: " + e.Message; }
                        }
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Remove", GUILayout.Width(70)) && EditorUtility.DisplayDialog("Remove " + profile.Name, "Forget this profile? The project keeps its packages; the ones it gave show as from another profile.", "Remove", "Cancel"))
                    {
                        ProfileAction(() => store.Remove(profile.Id));
                    }
                }
                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space();
            GUILayout.Label("Add a profile", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            using (new EditorGUI.DisabledScope(busy || profilesError != null))
            {
                newProfileUrl = EditorGUILayout.TextField(new GUIContent("Address", "A hub's https address, such as https://owner.github.io/repo/-/hub.json"), newProfileUrl);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(newProfileUrl)))
                {
                    if (GUILayout.Button("Add", GUILayout.Width(60))) AddProfile(newProfileUrl.Trim());
                }
            }
            using (new EditorGUI.DisabledScope(busy || profilesError != null))
            {
                if (GUILayout.Button("Add from a file…", GUILayout.Width(150)))
                {
                    string path = EditorUtility.OpenFilePanel("A profile (.forge-profile.json, hub.json)", string.Empty, "json");
                    if (!string.IsNullOrEmpty(path))
                    {
                        ProfileAction(() =>
                        {
                            ForgeProfile added = store.Add(ProfileImport.ReadFile(path), ForgeProfile.FromFile);
                            if (store.Active == null) store.SetActive(added.Id);
                            status = "Profile " + added.Name + " added.";
                        });
                    }
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("From this project", EditorStyles.boldLabel);
            GUILayout.Label("Hubs the project's own packages carry (the \"hub\" section of their package.json): read only, used with any profile; hide one if you do not want it.", EditorStyles.wordWrappedMiniLabel);
            var hidden = HubSources.Hidden();
            var presets = HubPresets.Sources();
            foreach (HubSource preset in presets)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    bool off = hidden.Contains(preset.Url);
                    GUILayout.Label(preset.Label + "  (from " + preset.Url + ")" + (off ? "  · hidden" : string.Empty), EditorStyles.wordWrappedLabel);
                    if (GUILayout.Button(off ? "Show" : "Hide", GUILayout.Width(70)))
                    {
                        HubSources.SetHidden(preset.Url, !off);
                        Refresh();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            if (presets.Count == 0) GUILayout.Label("No package of this project carries a hub.", EditorStyles.centeredGreyMiniLabel);
        }

        /// <summary>A change of the profiles file, then the catalogue read again.</summary>
        private void ProfileAction(Action change)
        {
            try
            {
                change();
            }
            catch (Exception e)
            {
                status = e.Message;
            }
            ReadProfiles();
            Refresh();
            GUIUtility.ExitGUI();
        }

        private async void AddProfile(string url)
        {
            busy = true;
            status = "Reading " + url + "…";
            Repaint();
            try
            {
                object hub = await ProfileImport.Fetch(ProfileRefresh.Transport(), url);
                ProfileStore store = HubSources.Profiles();
                ForgeProfile added = store.Add(hub, ForgeProfile.FromUrl, url);
                if (store.Active == null) store.SetActive(added.Id);
                SessionState.SetBool(RefreshedKey + added.Id, true);
                newProfileUrl = string.Empty;
                status = "Profile " + added.Name + " added.";
            }
            catch (Exception e)
            {
                status = url + ": " + e.Message;
            }
            busy = false;
            ReadProfiles();
            Refresh();
        }

        private async void RefreshProfile(ForgeProfile profile)
        {
            busy = true;
            status = "Reading " + profile.Url + "…";
            Repaint();
            SessionState.SetBool(RefreshedKey + profile.Id, true); // a failed attempt counts too
            try
            {
                string before = KeyOf(profile.Hub);
                await ProfileRefresh.Refresh(HubSources.Profiles(), profile);
                ForgeProfile after = HubSources.Profiles().Get(profile.Id);
                string hubName = Json.String(Json.Get(after?.Hub, "name"));
                status = profile.Name + " read again" + (hubName != null && hubName != profile.Name ? " (the hub now calls itself " + hubName + ")" : string.Empty)
                    + (before != null && before != KeyOf(after?.Hub) ? ". Its licensing key changed: the pinned one stays until you decide." : ".");
            }
            catch (Exception e)
            {
                status = profile.Name + ": not read again (" + e.Message + "); the profile as last read stays.";
            }
            busy = false;
            ReadProfiles();
            Refresh();
        }

        private static string KeyOf(object hub) => Json.String(Json.Get(hub, "licensing", "publicKey"));

        private void DrawSources()
        {
            GUILayout.Label("Your own sources, used whatever the profile. Profiles and the project's presets are in the Profiles tab.", EditorStyles.wordWrappedMiniLabel);
            foreach (HubSource source in HubSources.All().Where(s => s.Type != HubSource.Profile && s.Type != HubSource.Package))
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
                    KeyPins.Pin pin = pair.Value;
                    GUILayout.Label((pin.Name ?? pair.Key) + "  ·  " + KeyPins.Fingerprint(pin.Key)
                        + (pin.Pending != null ? "  ·  new key waiting: " + KeyPins.Fingerprint(pin.Pending) : string.Empty)
                        + (pin.PreviousOpens(DateTimeOffset.UtcNow) ? "  ·  previous key " + KeyPins.Fingerprint(pin.Previous) + " opens its licences until " + LicenceToken.Day(pin.PreviousUntil) : string.Empty), EditorStyles.wordWrappedMiniLabel);
                    if (GUILayout.Button(new GUIContent("Forget", "Its next key is pinned again on first use."), GUILayout.Width(70)))
                    {
                        KeyDecision(() => StoreTrust.Pins.Forget(pair.Key));
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        /// <summary>
        /// Where the buyer pastes a licence: a package of a static hub's own registry that its
        /// <c>licensed</c> list names (with its "Get" link), or every such package of an older hub
        /// that publishes a key but no list (see <see cref="LicenceOffer.For"/>).
        /// </summary>
        private LicenceOffer OfferOf(HubItem item) => catalog == null ? null : LicenceOffer.For(item, catalog.Licensing);

        /// <summary>The registry a licence of that item must name: the one the project installed it from, else the one it would be installed from.</summary>
        private string LicenceRegistry(HubItem item)
        {
            string installedFrom = null;
            if (manifestText != null && installed.ContainsKey(item.Name))
            {
                try { installedFrom = LicenceCheck.InstalledRegistry(manifestText, lockText, item.Name); }
                catch (FormatException) { installedFrom = null; }
            }

            return installedFrom ?? item.Origin.Registry.TrimEnd('/');
        }

        /// <summary>
        /// The licence of a package of such a store: the one kept (its licensee, its validity,
        /// Remove), or a field to paste one - checked against the store's pinned key, its registry
        /// and the package before it is kept, per user, outside the project.
        /// </summary>
        private void DrawLicence(HubItem item)
        {
            LicenceOffer offer = OfferOf(item);
            if (offer == null) return;
            string registry = LicenceRegistry(item);
            string key = registry + "|" + item.Name;
            LicenceStore.Kept kept;
            try
            {
                kept = StoreTrust.Licences.Get(registry, item.Name);
            }
            catch (Exception e)
            {
                EditorGUILayout.HelpBox("Your licences file: " + e.Message, MessageType.Warning);

                return;
            }
            if (kept != null)
            {
                if (!licenceVerdicts.TryGetValue(key, out var verdict))
                {
                    LicenceToken token = StoreTrust.VerifyLicence(item.Origin.Store, kept.Token, registry, item.Name, out string reason);
                    verdict = (token, reason);
                    licenceVerdicts[key] = verdict;
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (verdict.Token != null)
                    {
                        GUILayout.Label("Licensed to " + (verdict.Token.Holder ?? "you") + "  ·  " + verdict.Token.Validity + "  ·  " + (verdict.Token.AnyMachine ? "any machine" : "this machine")
                            + (verdict.Token.ByPreviousKey ? "  ·  signed with the store's previous key" : string.Empty), EditorStyles.wordWrappedMiniLabel);
                    }
                    else
                    {
                        EditorGUILayout.HelpBox("Your licence for " + item.Label + " does not open it here: " + verdict.Reason, MessageType.Warning);
                    }
                    if (GUILayout.Button(new GUIContent("Remove licence", "Forget the licence kept for this package (on this computer, for every project)."), GUILayout.Width(110))
                        && EditorUtility.DisplayDialog("Remove the licence", "Forget your licence for " + item.Label + "? Keep the token its seller gave you if you may need it again.", "Remove", "Cancel"))
                    {
                        try { StoreTrust.Licences.Remove(registry, item.Name); }
                        catch (Exception e) { status = e.Message; }
                        licenceVerdicts.Remove(key);
                        GUIUtility.ExitGUI();
                    }
                }

                return;
            }
            bool unfolded;
            using (new EditorGUILayout.HorizontalScope())
            {
                unfolded = EditorGUILayout.Foldout(licenceOpen.Contains(key), "Enter a licence", true);
                if (offer.Get != null && GUILayout.Button(new GUIContent("Get", "Buy a licence: opens " + offer.Get + " in your browser."), GUILayout.Width(60)))
                {
                    if (LicenceOffer.OpenGet(offer.Get, Application.OpenURL, out string refusal))
                    {
                        getRefusals.Remove(key);
                        unfolded = true; // the licence field and this machine's licence id, for the seller's form
                        status = item.Label + ": the seller's page is open in your browser. Paste the licence they send you under Enter a licence.";
                    }
                    else
                    {
                        getRefusals[key] = refusal;
                    }
                }
            }
            string mismatch = offer.RegistryWarning(registry);
            if (mismatch != null) EditorGUILayout.HelpBox(item.Label + ": " + mismatch, MessageType.Warning);
            if (getRefusals.TryGetValue(key, out string refused)) EditorGUILayout.HelpBox(item.Label + ": " + refused, MessageType.Warning);
            if (unfolded) licenceOpen.Add(key); else licenceOpen.Remove(key);
            if (!unfolded) return;
            GUILayout.Label("The licence its seller gave you (a token). It is checked against this store's pinned key, for this package of " + registry + ", and kept for you only, on this computer - never in the project.", EditorStyles.wordWrappedMiniLabel);
            DrawMachineLicenceId(key);
            using (new EditorGUILayout.HorizontalScope())
            {
                licenceText.TryGetValue(key, out string text);
                licenceText[key] = EditorGUILayout.TextField(text ?? string.Empty);
                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(licenceText[key])))
                {
                    if (GUILayout.Button("Check and keep", GUILayout.Width(110)))
                    {
                        string pasted = licenceText[key].Trim();
                        LicenceToken token = StoreTrust.VerifyLicence(item.Origin.Store, pasted, registry, item.Name, out string reason);
                        if (token == null)
                        {
                            status = item.Label + ": licence refused - " + reason;
                        }
                        else
                        {
                            try
                            {
                                StoreTrust.Licences.Put(registry, item.Name, item.Origin.Store, pasted);
                                status = "Licence kept for " + item.Label + ": licensed to " + (token.Holder ?? "you") + ", " + token.Validity + ".";
                                licenceText.Remove(key);
                                licenceOpen.Remove(key);
                            }
                            catch (Exception e)
                            {
                                status = item.Label + ": " + e.Message;
                            }
                        }
                        licenceVerdicts.Remove(key);
                        GUI.FocusControl(null);
                    }
                }
            }
        }

        /// <summary>
        /// "This machine's licence id", on demand: the hash a seller needs to bind a licence to
        /// this machine (what the licence check compares), with Copy. Not a secret, the raw
        /// identifier never shown.
        /// </summary>
        private void DrawMachineLicenceId(string key)
        {
            bool shown = EditorGUILayout.Foldout(machineIdOpen.Contains(key), "This machine's licence id", true);
            if (shown) machineIdOpen.Add(key); else machineIdOpen.Remove(key);
            if (!shown) return;
            string id;
            try { id = StoreTrust.MachineLicenceId; }
            catch (Exception e)
            {
                EditorGUILayout.HelpBox("This machine's licence id cannot be read here: " + e.Message, MessageType.Warning);

                return;
            }
            GUILayout.Label("For a licence bound to this machine, give it to the seller (their form, or a reply). It is not a secret: it opens nothing by itself. A licence sold without it opens on any machine.", EditorStyles.wordWrappedMiniLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.SelectableLabel(id, EditorStyles.textField, GUILayout.Height(EditorGUIUtility.singleLineHeight));
                if (GUILayout.Button("Copy", GUILayout.Width(60)))
                {
                    EditorGUIUtility.systemCopyBuffer = id;
                    status = "This machine's licence id is copied.";
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

        private void Run(string what, Func<System.Threading.Tasks.Task> action)
        {
            Run(what, async _ =>
            {
                await action();

                return null;
            });
        }

        /// <summary>An install or update: its progress line while it runs, then what it added with it.</summary>
        private async void Run(string what, Func<Action<string>, System.Threading.Tasks.Task<string>> action)
        {
            busy = true;
            status = what + "…";
            Repaint();
            try
            {
                string note = await action(line =>
                {
                    status = line;
                    Repaint();
                });
                installed = await HubInstaller.Installed();
                ReadProject();
                status = what + ": done." + (note != null ? "\n" + note : string.Empty);
            }
            catch (Exception e)
            {
                status = what + ": " + e.Message;
            }
            busy = false;
            Repaint();
        }

        /// <summary>
        /// Buy: a forge that takes one-click purchases, once this editor is linked to it, opens the
        /// confirmation (<see cref="PurchaseWindow"/>). Otherwise the device flow - the forge's page
        /// in the browser, where the user buys (or only links this editor, when the forge takes
        /// one-click purchases: the confirmation follows here).
        /// </summary>
        private async void Buy(HubItem item)
        {
            cancelPurchase = false;
            bool oneClick = item.Origin != null && item.Origin.OneClick && item.Offers.Count > 0;
            try
            {
                HubPurchases linkedAlready = oneClick ? HubPurchases.For(item.Origin) : null;
                if (linkedAlready != null)
                {
                    PurchaseWindow.Open(item, linkedAlready, Bought);
                    return;
                }
                HubCheckout.Linked linked = await HubCheckout.Buy(item, started => { pending = started; linking = oneClick; Repaint(); }, () => cancelPurchase);
                pending = null;
                HubPurchases now = linked != null && linked.Account && oneClick ? HubPurchases.For(item.Origin) : null;
                if (linked == null)
                {
                    status = "The purchase was not completed.";
                }
                else if (linked.Key != null)
                {
                    KeyReceived(item, item.Origin.Registry);
                }
                else if (now != null)
                {
                    status = "This editor is linked to " + item.Origin.HubName + ".";
                    PurchaseWindow.Open(item, now, Bought);
                }
                else
                {
                    status = "This editor is linked to " + item.Origin.HubName + ", and you hold no licence for " + item.Label + " there yet.";
                }
            }
            catch (Exception e)
            {
                pending = null;
                status = e.Message;
            }
            Repaint();
        }

        /// <summary>Paid in one click: its key kept (once), or the licence already in the user's account.</summary>
        private void Bought(HubItem item, PurchaseOutcome outcome)
        {
            if (outcome.KeyKept)
            {
                KeyReceived(item, outcome.Registry);
            }
            else
            {
                status = "Bought" + (outcome.Replayed ? " (already paid: nothing charged again)" : string.Empty) + ". Your licence key was handed over before; the licence is in your account on " + item.Origin?.HubName + ".";
                Repaint();
            }
        }

        private void KeyReceived(HubItem item, string registry)
        {
            if (EditorUtility.DisplayDialog("Key received", "Your licence key is kept for " + (registry ?? item.Origin.Registry) + " in " + UpmConfig.FilePath + ". Unity's Package Manager reads it when Unity starts: restart this project now, then install " + item.Label + " from this window.", "Restart now", "Later"))
            {
                HubCheckout.RestartProject();
            }
            else
            {
                status = "Key kept. Restart Unity, then install " + item.Label + ".";
            }
            Repaint();
        }
    }
}
