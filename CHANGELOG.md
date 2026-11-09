# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package
adheres to [Semantic Versioning](https://semver.org/).

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
