# Forge Store

A window of the Unity editor - **Window > Forge Store** - that browses
package hubs and installs their packages in the open project in one click:

- **Packages.** Every source's packages, with search, versions and
  changelogs. Install, update or remove: the window writes the source's scoped
  registry into `Packages/manifest.json` when the project lacks it, then asks
  Unity's Package Manager to add or remove the package.
- **Installed.** The project's packages, read from `Packages/manifest.json`
  and `packages-lock.json` - no network call. First the ones a source offers
  or Forge Store installed: their version, their source, a Git package's tag,
  branch or commit, the update a source offers, **Update all** (one
  `AddAndRemove`: one resolve, one recompilation), Remove. A package whose
  source is gone - removed from the sources, or no longer listing it - is
  flagged, not hidden; one another profile gave says "From profile <name>".
  Then the project's other packages (dependencies and built-in modules on
  request), with Unity's recommended version within the same major one. A
  package's samples are imported into `Assets/Samples` as Unity's window does,
  and **Embed** copies a package into `Packages/` to edit it. Unity's Package
  Manager does every change and resolves every dependency.
- **Versions.** Next to Update, a menu of every known version - tags,
  registry versions, a hub's versions - newest first, older ones to go back;
  the one chosen is installed exactly. A move across a major version of a
  package that is not from your sources asks first.
- **Git dependencies.** Unity does not resolve a Git package's dependencies on
  other Git packages: installing one adds, in the same change, the ones your
  sources offer and the project lacks (three levels at most), and says which.
- **Games.** A hub's games: a free one is played in the browser (and
  downloaded from its page); a paid one is downloaded once bought, played in
  the browser only when its studio opened a playable preview.
- **Profiles.** A profile is a hub people share - a team's or a store's
  sources, look and licensing - as an https address or a file. Keep several,
  switch between them (or use none); add, rename, read again, export, remove.
  They live in the per-user `Forge/profiles.json` that Forge Hub, the desktop
  app, reads and writes too (`~/Library/Application Support` on macOS,
  `%LOCALAPPDATA%` on Windows, `~/.local/share` on Linux; `FORGE_PROFILES_FILE`
  overrides it). Nothing secret is ever written there nor into an export. The
  active profile heads the window with its name and theme; switching never
  changes the project. The project's own presets are shown read only, "from
  this project".
- **Sources.** Added on the fly and kept per user, not per project, used with
  any profile. Nothing is built in: the window is open source and belongs to
  no one.
  - a **hub manifest** (`/upm/-/hub.json` on a Forge running
    omnibase/forge, or `hub.json` of a static hub on GitHub Pages);
  - a **UPM registry** and its scopes (OpenUPM, a company's Verdaccio...);
  - a **Git package** URL;
  - a **GitHub owner and a repository pattern** (`my-org`, `unity-*`): every
    matching repository that is a UPM package, public ones without an
    account, private ones with your GitHub token. Installed by its Git URL at
    the tag or branch you pick; a newer tag offers an update.

  A package of the project can preset sources: the `"hub"` section of its
  `package.json` (a company's base package lists its registry and its GitHub
  organisation there, and may give the window its theme). Presets can be
  hidden.
- **Buy.** Only for a source whose manifest declares a checkout (a static
  hub never does). The window opens the hub's page in the browser with a
  short code; the user signs in, buys, and links the editor. The window then
  receives the licence key and keeps it as the registry's token in the user's
  `~/.upmconfig.toml` - never in the project. Unity's Package Manager reads
  that file when Unity starts: the window offers to restart the project.
  A Forge that takes one-click purchases only needs the editor linked once:
  then **Buy** opens a confirmation (the price this user pays, taxes
  included, the store's name, the saved payment method) and **Pay** confirms
  it; a changed price is shown and asked again, a bank's check opens in the
  browser. Payment methods are saved on the payment provider's own page.
- **A store's look.** A hub's theme gives the window its header: a banner
  (PNG or JPEG, cropped to the window's width at 6:1), a logo, a name and an
  accent for each skin. Images are fetched in the background, kept in the
  user's folder within a size bound, and simply left out when they fail.
- **A store's licensing key, pinned.** The public key a store declares in its
  hub (`licensing.publicKey`) is pinned the first time the window reads it. A
  later, different key is shown as a warning: until the user accepts it, the
  store's licences are not trusted and it sells nothing in the window. A key
  the pinned one signed passes alone, and the licences the former key signed
  keep opening for a bounded time. The pins live in `Forge Store/keys.json`,
  which Forge Hub shares: one approval per store for both apps. See
  [docs/sources.md](docs/sources.md#licensing-a-stores-key).
- **Licences bound to their store.** A licence opens a package only when it
  names the registry the package comes from and the package itself (format
  2): a licence of store B never opens a package of the same name from store
  A. On a static hub, whose author signs licences offline and sells them
  through a link of their own, the buyer pastes the licence on the package
  (**Enter a licence**): checked, then kept for that user outside the project
  (`Forge Store/licences.json`), shown with its licensee and validity,
  removable; the package checks it with Forge Seal. A hub that lists what it
  sells (`licensed` in its hub.json) gets the field on those packages only,
  with a **Get** button to its shop (https links only); its other packages
  are free. **This machine's licence id**, shown on demand next to the
  field, is what a seller needs to bind a licence to one machine. See
  [docs/licences.md](docs/licences.md).

Requires Unity 2021.3 or later. Editor only: nothing of it goes into a build.

## Installation

From Git, in `Packages/manifest.json`:

```json
"dependencies": {
  "io.glitchr.store": "https://github.com/glitchr-forge/unity-store.git#v1.7.0"
}
```

(Package Manager > + > Add package from git URL does the same.) A registry
that serves the package - a Forge, a static hub - works as well.

### From the former name

The package was `io.glitchr.hub` (and `com.glitchr.hub` before it). Unity's
Package Manager has no alias between package names, so a project that has the
old one replaces it by a one-line change in `Packages/manifest.json`:

```diff
-  "io.glitchr.hub": "...",
+  "io.glitchr.store": "https://github.com/glitchr-forge/unity-store.git#v1.7.0",
```

When both are there, the window shows a notice with a button that removes
the old one. The saved sources are kept: they live in the user's EditorPrefs.

## Private GitHub repositories

The window lists them with a token that can read them (Sources > GitHub): a
fine-grained token with *Contents: read* on the repositories, or a classic one
with `repo`. It is kept by the system - the macOS keychain, Windows' DPAPI
for the current user, the Secret Service (`secret-tool`) on Linux - or read
from git's own credential helper when you tick it. It is sent to
`api.github.com` only, never written into the project and never logged.

Unity's Package Manager fetches a Git package with your own `git`: a private
repository installs once git can read it (a credential helper or an SSH key,
see Unity's manual, "Using private repositories with HTTPS Git URLs"). No
token is ever put in the manifest's URL.

GitHub allows 60 requests an hour without a token, 5,000 with one. The window
asks with the last ETag (`If-None-Match`), keeps the answers in the user's
folder (`Forge Store/github.json` under the local application data), and
stops asking until the limit resets, showing what it already knows.

See [docs/sources.md](docs/sources.md) for the hub manifest and the kinds of
source, [docs/buying.md](docs/buying.md) for the device flow and the one-click
purchase.

## Tests

Editor tests (Window > General > Test Runner > EditMode), with the package in
the project's `testables`: the JSON the window reads, the manifest it writes,
the key it keeps, a GitHub source against canned answers of GitHub's API
(pattern, packages, tags, ETags, rate limit, where the token goes), the
Installed tab's reading of a manifest and its lock (sources, Git references,
updates), the pinned licensing keys (first use, a change held, a signed
rotation - checked by Forge Seal when the project has it, skipped otherwise;
the file shared with Forge Hub: another app's decision seen at once, its
fields kept, a file caught mid-write), licences (the previous key's bound,
the binding to registry and package with a Forge's format 2 token and a
format 1 one, a static hub's offline licences signed by `omnihub licence
sign`, the per-user licences file, a hub's `licensed` list and its Get links,
this machine's licence id, a manifest `omnihub build` wrote) with real
Ed25519 signatures,
the images' cache (PNG and JPEG only, bounded), the profiles file (on a
temporary file, never the user's: unknown fields kept, secrets out, addresses
and redirects checked, refresh), the version menu, a Git package's Git
dependencies (against a fake GitHub), and samples (their import folder checked
against Unity's own Sample API when the test project has a package with
samples). In batch mode:

```sh
Unity -batchmode -nographics -projectPath <project> -runTests -testPlatform EditMode -testResults results.xml
```

## Licence

MIT - see [LICENSE.md](LICENSE.md).
