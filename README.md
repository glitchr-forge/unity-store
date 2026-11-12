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
  flagged, not hidden. Then the project's other packages (dependencies and
  built-in modules on request), with Unity's recommended version within the
  same major one. Unity's Package Manager does every change and resolves
  every dependency.
- **Games.** A hub's games: a free one is played in the browser (and
  downloaded from its page); a paid one is downloaded once bought, played in
  the browser only when its studio opened a playable preview.
- **Sources.** Added on the fly and kept per user, not per project. Nothing is
  built in: the window is open source and belongs to no one.
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
- **A store's look.** A hub's theme gives the window its header: a banner
  (PNG or JPEG, cropped to the window's width at 6:1), a logo, a name and an
  accent for each skin. Images are fetched in the background, kept in the
  user's folder within a size bound, and simply left out when they fail.
- **A store's licensing key, pinned.** The public key a store declares in its
  hub (`licensing.publicKey`) is pinned the first time the window reads it. A
  later, different key is shown as a warning: until the user accepts it, the
  store's licences are not trusted and it sells nothing in the window. A key
  the pinned one signed passes alone. See [docs/sources.md](docs/sources.md#licensing-a-stores-key).

Requires Unity 2021.3 or later. Editor only: nothing of it goes into a build.

## Installation

From Git, in `Packages/manifest.json`:

```json
"dependencies": {
  "io.glitchr.store": "https://github.com/glitchr-forge/unity-store.git#v1.3.0"
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
+  "io.glitchr.store": "https://github.com/glitchr-forge/unity-store.git#v1.3.0",
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
source, [docs/buying.md](docs/buying.md) for the device flow.

## Tests

Editor tests (Window > General > Test Runner > EditMode), with the package in
the project's `testables`: the JSON the window reads, the manifest it writes,
the key it keeps, a GitHub source against canned answers of GitHub's API
(pattern, packages, tags, ETags, rate limit, where the token goes), the
Installed tab's reading of a manifest and its lock (sources, Git references,
updates), the pinned licensing keys (first use, a change held, a signed
rotation - checked by Forge Seal when the project has it, skipped otherwise)
and the images' cache (PNG and JPEG only, bounded). In batch mode:

```sh
Unity -batchmode -nographics -projectPath <project> -runTests -testPlatform EditMode -testResults results.xml
```

## Licence

MIT - see [LICENSE.md](LICENSE.md).
