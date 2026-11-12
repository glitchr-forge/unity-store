# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package
adheres to [Semantic Versioning](https://semver.org/).

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
  header (logo and name) and its accent; `banner` is read, not drawn yet.
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
