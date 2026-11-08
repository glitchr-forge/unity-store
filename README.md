# Forge Store

A window of the Unity editor - **Window > Forge Store** - that browses
package hubs and installs their packages in the open project in one click:

- **Packages.** Every source's packages, with search, versions and
  changelogs. Install, update or remove: the window writes the source's scoped
  registry into `Packages/manifest.json` when the project lacks it, then asks
  Unity's Package Manager to add or remove the package.
- **Games.** A hub's games: a free one is played in the browser (and
  downloaded from its page); a paid one is downloaded once bought, played in
  the browser only when its studio opened a playable preview.
- **Sources.** Added on the fly and kept per user, not per project:
  - a **hub manifest** (`/upm/-/hub.json` on a forge running
    omnibase/forge, or `hub.json` of a static hub on GitHub Pages);
  - a **UPM registry** and its scopes (OpenUPM, a company's Verdaccio...);
  - a **Git package** URL.

  Glitch Art's hub is there from the start.
- **Buy.** Only for a source whose manifest declares a checkout (a static
  hub never does). The window opens the hub's page in the browser with a
  short code; the user signs in, buys, and links the editor. The window then
  receives the licence key and keeps it as the registry's token in the user's
  `~/.upmconfig.toml` - never in the project. Unity's Package Manager reads
  that file when Unity starts: the window offers to restart the project.

Requires Unity 2021.3 or later. Editor only: nothing of it goes into a build.

## Installation

From Glitch Art's registry, in `Packages/manifest.json`:

```json
"scopedRegistries": [
  {"name": "Glitch Art", "url": "https://glitchr.io/upm", "scopes": ["io.glitchr"]}
],
"dependencies": {
  "io.glitchr.store": "1.0.0"
}
```

Or from Git: `"io.glitchr.store": "https://github.com/glitchr-forge/unity-store.git#1.0.0"`.

See [docs/sources.md](docs/sources.md) for the hub manifest and the kinds of
source, [docs/buying.md](docs/buying.md) for the device flow.

## Tests

Editor tests (Window > General > Test Runner > EditMode), with the package in
the project's `testables`: the JSON the window reads, the manifest it writes,
the key it keeps.

## Licence

MIT - see [LICENSE.md](LICENSE.md).
