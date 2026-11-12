# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package
adheres to [Semantic Versioning](https://semver.org/).

## [1.3.0] - 2026-11-12

### Added

- The Installed tab: the project's packages, read from `Packages/manifest.json`
  and `packages-lock.json` with no network call. The ones a source offers or
  Forge Store installed come first, with their version, source, and for a Git
  package its tag, branch or commit; their updates are made one by one or all
  at once (one `Client.AddAndRemove`: one resolve, one recompilation); Remove.
  A package whose source was removed, or no longer lists it, is flagged, not
  hidden: which source installed what is kept in the project's
  `UserSettings/ForgeStore/installed.json`. The project's other packages
  follow, with Unity's recommended version (never across a major version);
  dependencies and built-in modules on request.
- The theme's banner, drawn in the window's header (the theme of a preset's
  hub first): cropped to the window's width at 6:1, the name on a strip of the
  skin's own colour, the accent under it. PNG or JPEG only, told by their
  bytes, 2 MB and 4096 pixels a side at most (the logo 1 MB and 1024 pixels);
  fetched in the background, kept in the user's `Forge Store/images` (32 MB
  in all, the oldest first out), asked again after a day; a failure draws the
  plain header.
- A store's licensing key, pinned on first use: the hub's
  `licensing.publicKey` is kept in the user's `Forge Store/keys.json`. A later,
  different key is held as pending and shown as a warning: until the user
  accepts it (or keeps the pinned one), the store's licences are not trusted
  and its packages are not bought from the window. A rotation the pinned key
  signed (`previousKey`, `rotation`) passes without a prompt; its signature is
  checked by Forge Seal (`io.glitchr.licensing`) when the project has it, and
  without it every change waits for the user. `StoreTrust.VerifyLicence`
  checks a licence token against a store's pinned key through Forge Seal.

## [1.2.0] - 2026-11-12

### Added

- A GitHub source: an organisation or a user and a repository pattern
  (`unity-*`, several separated by commas). Every matching repository whose
  `package.json` names a package is listed - public ones without an account,
  private ones with the user's GitHub token - with its tags; it is installed
  by its Git URL at the tag or branch picked (the latest tag by default), and
  a newer tag offers an update. GitHub's REST API is asked with ETags, its
  rate limit respected (nothing is sent until it resets; the window shows what
  it knows), its answers kept in the user's folder.
- The GitHub token: pasted once and kept by the system (macOS keychain,
  Windows' DPAPI, the Secret Service on Linux), or read from git's credential
  helper. Sent to `api.github.com` only, never in the project, never logged.
- Presets: a package of the project whose `package.json` has a `"hub"` section
  brings its sources (a company's base package), hidden in one click. A hub's
  sources may be of the types `github` and `manifest` too.
- A hub's theme, data only: `"theme"` with `accent`, `accentDark`, `logo`
  and `banner` (or the older `color` and `logo`) gives the window its
  header (logo and name) and its accent; `banner` is read, not drawn yet
  (drawn since 1.3.0).
- A notice for a project that still has the former name (`io.glitchr.hub`,
  `com.glitchr.hub`), with a button that removes it.

### Changed

- Agnostic: no source is built in. Glitch Art's hub was the default; it is now
  `io.glitchr.studio`'s preset, like any company's.
- A hub's source that fails (a host that does not answer, a private owner
  without a token) no longer hides the hub's other sources.

## [1.1.0] - 2026-11-09

### Added

- A hub can live in a package: the `"hub"` section of its `package.json`
  (`io.glitchr.studio` is Glitch Art's), read when the package is added
  as a Git source, or as a hub by its repository's URL.
- A Git package on GitHub is described by its `package.json`, read at the
  URL's tag and `?path=` folder: its package name, version and
  description.

### Fixed

- The package compiles: two calls still named the `Glitchr.Hub`
  namespace after the rename to `Glitchr.Store`.
- A Git package shows as installed, and Remove removes it: it was known
  by its repository's name (`unity-toolkit`), not its package's
  (`io.glitchr.toolkit`).

## [1.0.0] - 2026-10-27

### Added

- Window > Forge Store: the sources' packages - search, versions and
  changelogs -, installed, updated and removed in the open project in one
  click; the hubs' games, played in the browser when free, downloaded once
  bought; the sources, added on the fly: a hub manifest, a UPM registry with
  its scopes, a Git package.
- Buying from a hub whose manifest declares a checkout: a device code, the
  hub's page in the browser, the key kept as the registry's token in the
  user's `.upmconfig.toml`, then a restart for the Package Manager to read it.
