# Changelog

All notable changes to this package are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the package
adheres to [Semantic Versioning](https://semver.org/).

## [1.7.0] - 2026-11-14

### Added

- **Buying in one click** from a Forge that takes it (its manifest's
  `checkout.quote`, `purchases` and `methods`, on the host of
  `checkout.token`; `HubOrigin.OneClick`). Once the editor is linked, Buy
  opens a confirmation (`PurchaseWindow`): the price the Forge charges this
  user (`GET quote?offer=<id>`, minor units, taxes included), its currency,
  the store's name and the saved payment method; an Offer menu when the
  package has several offers. **Pay** sends the purchase as shown with an
  `Idempotency-Key` per confirmation, sent again on its retries, so nothing
  is charged twice. A 409 `price_changed` shows the new price beside the old
  one and asks again (a new key); `requires_action` (3-D Secure) opens the
  bank's page in the browser and asks `GET purchases/<id>` every 3 seconds,
  10 minutes at most; 402 shows the bank's reason; `payment_method_required`,
  `purchase_in_progress` (sent again up to five times), `unknown_offer`,
  `payments_unavailable`, `idempotency_key_reused` are said in a sentence.
  Once paid, the licence key handed over (once) is kept for its registry in
  `~/.upmconfig.toml` as the device flow keeps it. `HubPurchases`,
  `PurchaseQuote`, `PurchaseConfirmation`, `PurchaseOutcome`.
- **Payment methods** in the confirmation: listed (`GET methods`), added on
  the payment provider's own page opened in the browser (`POST methods`),
  removed (`DELETE methods/<id>`). Only `https` pages without a user name are
  opened (`http` on this machine only, for a development Forge).
- **The account token** the device flow now gives (`account_token`, even to a
  user without a licence) is kept by the system's secret store per Forge
  (`AccountTokens`: the macOS keychain, Windows' DPAPI, the Secret Service),
  never in the project, sent as Bearer to that Forge's own addresses only; a
  401 forgets it and Buy links the editor again.
- `HubItem.Offers` (`HubOffer`: id, title, price, currency, seats, months),
  read from a Forge's catalogue; `HubAmount.Format` (no minor unit for JPY,
  KRW and the other zero-decimal currencies), now also used for the Buy
  button's price.

### Changed

- The device flow no longer requires a licence key in the Forge's answer: a
  user without a licence gets the account token only (`HubCheckout.Linked`,
  `HubCheckout.Grant`); 1.6.0 wrote an empty token into `.upmconfig.toml`.
  With a hub that takes one-click purchases, Buy only links the editor and the
  confirmation follows in the window.
- The GitHub token and the account tokens share one secret store
  (`SecretStore`); the GitHub token's keychain item is unchanged.

### Tests

- Eight new EditMode tests against a Forge answering as omnibase/forge's
  HubPurchaseController does (the price changed then paid once, the retry with
  the same key, 3-D Secure then pending then paid, the key handed over once,
  refusals, payment methods, an account token the Forge no longer knows, the
  device flow without licence). 84/84 in Unity 6000.3.28f1 batch mode with
  Forge Seal, 70 + 3 skipped without. The real UnityWebRequest path probed
  against a local server: Bearer and Idempotency-Key sent, a 409 body read,
  DELETE, a redirect not followed.

## [1.6.0] - 2026-11-13

### Added

- What a static hub sells: its hub.json's `licensed`
  (`[{"package": <UPM name>, "get": <https address, optional>}]`, written by
  omnisense/omnihub with `licensing`) is read with the hub's key
  (`HubLicensing.Licensed`, `LicensedPackage`). On a store that publishes it,
  **Enter a licence** shows only on the packages it lists, in the Packages
  and Installed tabs alike, and those with a `get` link get a **Get** button
  that opens it in the browser (Application.OpenURL, never a shell) - an
  absolute https address with a host and no user name or password in it;
  any other link is not opened and the window says why. The packages it
  does not list are free: no licence field. An empty list: none. A store
  that publishes a key but no `licensed` keeps 1.5.0's behaviour (the field
  on every package of its registries); without a key the list is ignored.
  `LicenceOffer.For(item, licensing)`, `LicenceOffer.GetRefusal`,
  `LicenceOffer.OpenGet`, `LicenceOffer.RegistryWarning`.
- **This machine's licence id**, a foldout under Enter a licence (opened by
  Get too): the SHA-256 hex of Unity's `SystemInfo.deviceUniqueIdentifier`,
  exactly what the licence check compares with a licence's `machine` (and
  what omnihub's `licence sign --machine` writes), with **Copy**, for a
  seller who binds a licence to one machine (`--machine-hash`). The hash
  only, never the raw identifier; shown on demand. `StoreTrust.MachineLicenceId`,
  `LicenceCheck.MachineLicenceId(identifier)`.

### Changed

- `licensing.registry` is read without its final slash. When the registry a
  package's licence must name (the one the project installed it from, else
  the one it would come from) differs from it - a package another upm source
  of the hub serves, say -, the licence field warns that such a licence
  would be refused there. The verification itself is unchanged, and a
  Forge's licences are untouched. The same choices as Forge Hub 1.4.0 (empty
  `licensed`, entries that are not objects with a `package` ignored, Get
  links, the machine id).

## [1.5.0] - 2026-11-13

### Added

- Licences bound to their store (plan item 17.2):
  `StoreTrust.VerifyLicence(store, token, registry, package, out reason)`
  opens a licence only when its signature verifies with the store's pinned
  key, it is of format 2, its `registry` is the registry the project
  installed the package from (the lock's `url`, else the scoped registry
  whose scope matches it most closely; a final slash and the host's case
  aside), its `package` is the package's name, its `machine` (when it names
  one) is this machine's and its `valid_until` (when it has one) is ahead.
  A format 1 token is refused with an invitation to activate again online;
  a token of another store says "the licence is another store's".
- The store's previous key after a rotation: a hub's
  `licensing.previousKeyValidUntil` keeps the former key in the pin
  (`previous`, `previousUntil` = the earlier of that date and 30 days after
  the rotation was seen), so licences signed before the rotation keep
  opening until then; never without that date, dropped once past. Also on
  Accept of a change whose hub names the pinned key as `previousKey`.
  `licensing.registry` is read too. Sources > Licensing keys shows a kept
  previous key and its end.
- A static hub's offline licences (plan item 14): on a package of a store
  that publishes a key and sells nothing in the window, **Enter a licence**
  in the Packages and Installed tabs. The pasted token is checked as above
  and kept per user in `Forge Store/licences.json` (local application data,
  beside keys.json, never in the project), shown with its licensee and
  validity, removable; a wrong or foreign token is refused with the reason.
  A format 2 token without `machine` opens on any machine, without
  `valid_until` has no end. Forge Seal 1.1.0's `PackageLicence` reads the
  same file, so the package checks itself. See docs/licences.md.

### Changed

- `keys.json` is shared with Forge Hub 1.2.0 (profiles-questions 15): read
  again before every decision (a key announced, Accept, Keep, Forget, a
  licence's check) and each change written at once - through a temporary
  file renamed over it, only the store decided on and only the fields
  Forge Store owns changed, other stores and unknown fields kept, stores
  sorted by name. A file that does not parse is read again a few times
  (40 ms apart) before it counts as damaged. A decision taken in Forge Hub
  is seen at once and a stale copy never undoes it. `KeyPins.Save()` has
  nothing left to write; `KeyPins.Get` returns a copy of the pin.
- The signature of a licence is checked with Forge Seal's Ed25519 only (any
  Forge Seal version); Forge Store reads the payload itself.

### Deprecated

- `StoreTrust.VerifyLicence(store, token, out reason)`: binds the licence to
  nothing; use the overload with the registry and the package.

## [1.4.0] - 2026-11-13

### Added

- Profiles: the Profiles tab lists the profiles of the per-user
  `profiles.json` that Forge Hub (the desktop app) shares - a profile is one
  hub document (name, theme, sources, licensing), shared as an https address
  or a file. Add one by its address (https; http only from this machine; no
  user name, password or token in the address; redirects followed on the same
  host only, another host refused with its address named; 1 MB at most) or
  from a file (an exported `<name>.forge-profile.json`, a `hub.json`, or a
  `package.json` with a `"hub"` section); rename, remove, read again (on
  demand, once per editor start, a failed attempt counting; a failure keeps
  the profile as last read), export (the bare hub document, never written
  over an existing file in a folder), and pick the active one, or none. The
  file sits in `~/Library/Application Support/Forge` on macOS (where Rust's
  `dirs` puts it; Mono's `LocalApplicationData` is `~/.local/share` there),
  `%LOCALAPPDATA%\Forge` on Windows, `$XDG_DATA_HOME/Forge` or
  `~/.local/share/Forge` on Linux, or wherever `FORGE_PROFILES_FILE` says. It
  is read as a JSON tree and written whole through a temporary file renamed
  over it, read again just before each write, unknown fields kept; a file
  that is not JSON is never written over. Anything that looks like a secret
  (members named token, password, secret, apiKey, licenseKey, privateKey...,
  unless they hold an address, and any GitHub token) is taken out of a hub
  document on add, refresh and export, as Forge Hub does. The same address
  added twice updates the profile already there; the user's name for a
  profile survives a refresh. A URL profile is the same store as that hub
  added as a source, so its licensing key stays pinned as in 1.3.0.
- The window's header shows the active profile: its name, and its theme
  before a preset's. The project's presets are shown in the Profiles tab,
  read only, "from this project" (hideable as before); switching profile
  never changes the project.
- The Installed tab says "From profile <name>" (or "from another profile")
  for a package another profile gave, instead of flagging its source as gone
  - also for a package installed in 1.3.0 from a source a profile now carries.
- A version picker: next to Update (in the catalogue and the Installed tab),
  a menu lists every known version of the package, newest first, older ones
  included to go back - a GitHub package's tags, a registry's or a hub's
  versions, Unity's compatible versions for the project's other packages.
  Choosing one installs exactly that version (a Git package by its URL at
  that tag, a registry's as `name@version`). The plain click still takes the
  newest; a move across a major version of a package that is not from your
  sources asks first.
- Git dependencies: Unity's Package Manager does not resolve a Git package's
  dependencies on other Git packages. Installing or updating a Git package now
  reads its `package.json` dependencies (the one the catalogue already read,
  else GitHub's contents API at the tag installed, with the same token and
  ETag cache), and each one a source of yours offers as a Git package and the
  project lacks in every form (manifest, lock, registered, a folder of
  `Packages/`) is added in the same `AddAndRemove` - its tag is the version
  asked for, else the newest of that major version. Three levels at most,
  each package once (cycles cut). A dependency a registry of your sources
  offers gets its registry written into the manifest, and Unity resolves it.
  The window says in one line what it added.
- Samples and embedding, in the Installed tab: an installed package's samples
  (its `package.json`'s `samples`) are listed and imported into
  `Assets/Samples/<package>/<version>/<sample>` by Unity's own Sample API, as
  Unity's window does (a previous import is replaced only once you agree);
  **Embed** copies a registry's or a Git package from Unity's cache into
  `Packages/<name>` (`Client.Embed`), so it becomes editable.

### Changed

- The sources you added by hand (the Sources tab, kept in EditorPrefs) stay
  beside profiles and are read with any profile and with none; they are not
  turned into a local profile. The project's presets moved from the Sources
  tab to the Profiles tab.
- `HubInstaller.Install` and `HubInstaller.Apply` return the line telling
  what was added with the package (or null).

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
