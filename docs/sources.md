# Sources

The window reads every source the user added (Sources tab), in order, and
lists what each offers.

## A hub manifest

A JSON document that names a hub and its sources - what a forge serves at
`/upm/-/hub.json`, and what a static hub publishes as `hub.json`:

```json
{
  "version": 1,
  "name": "Glitch Art",
  "logo": "https://glitchr.io/assets/images/brand/monkey.png",
  "color": "#fc6875",
  "sources": [
    {"name": "Glitch Art", "type": "upm", "url": "https://glitchr.io/upm", "scopes": ["io.glitchr"], "catalog": "https://glitchr.io/upm/-/hub/catalog.json"},
    {"name": "A tool", "type": "git", "url": "https://github.com/someone/some-tool.git"}
  ],
  "games": "https://glitchr.io/upm/-/hub/catalog.json",
  "checkout": {"device": "https://glitchr.io/upm/-/hub/device", "token": "https://glitchr.io/upm/-/hub/token"}
}
```

- A `upm` source with a `catalog` is read from it: its packages with their
  versions and changelogs, pictures, prices, and whether the user's key
  covers them.
- A `upm` source without one is read as any registry: its search
  (`/-/v1/search?text=<scope>`), then each package's document.
- A `git` source is one package, installed by its URL. `"package"` may name it
  (`io.glitchr.toolkit`); `"install": true` marks it as one every project of
  the team gets - Forge Hub's Prepare writes those into a project at once.
- A `github` source names an `owner` (organisation or user) and a `pattern`
  for its repositories' names: see [A GitHub owner](#a-github-owner-and-a-repository-pattern).
- A `manifest` source is another hub, by the URL of its manifest.
- `checkout`, when present, lets the window offer to buy (see
  [buying.md](buying.md)).
- `theme`, optional, is the hub's look - data only, never code:

  ```json
  "theme": {"accent": "#336699", "accentDark": "#6699cc", "logo": "https://example.com/logo.png", "banner": "https://example.com/banner.png"}
  ```

  `accent` and `accentDark` are `#rgb` or `#rrggbb` colours for Unity's light
  and dark skins. The logo and the banner are HTTPS addresses of PNG or JPEG
  images - told by their first bytes, whatever the server says; an SVG, or
  anything else, is not drawn. The logo is 1 MB and 1024 pixels a side at
  most; the banner 2 MB and 4096 pixels a side, drawn across the window's
  width at a ratio of 6:1, cropped around its centre (1500 x 250 pixels fits
  it exactly), with the hub's name on a strip of the skin's colour and the
  accent under it - keep the picture's subject away from its bottom-left
  corner. The window shows the theme of a preset's hub first, else the first
  hub that has one. Images are fetched in the background, kept in the user's
  `Forge Store/images` (32 MB in all), and asked again after a day; one that
  fails is left out. Without a theme (the older `color` and `logo` keys count
  as one), the window keeps its plain look.
- `licensing`, optional, is the public key that signs the store's licences:
  see [Licensing: a store's key](#licensing-a-stores-key).

### Or in a package's `package.json`

The same document can live in a package, under a `"hub"` key - Unity's
Package Manager ignores keys it does not know. A tag of the package then
freezes the list; `io.glitchr.studio` is Glitch Art's:

```json
{
  "name": "io.glitchr.studio",
  "version": "0.2.0",
  "dependencies": {},
  "hub": {
    "version": 1,
    "name": "Glitch Art",
    "sources": [
      {"name": "Glitch Art Toolkit", "type": "git", "url": "https://github.com/glitchr-studio/unity-toolkit.git#v0.3.0"}
    ]
  }
}
```

Add it as a Git package (`https://github.com/glitchr-studio/unity-studio.git#v0.2.0`)
or as a hub, by the URL of its `package.json` or of its repository.

### A preset

A package installed in the project whose `package.json` has a `"hub"` section
is a source by itself, read from the disk: a company's base package brings its
registry, its GitHub organisation and its theme to everyone who installs it.
It shows under Profiles, "from this project", read only; the user can hide it (and show it again).
Nothing of the package runs: the window reads its JSON.

### A profile

The same document, kept per user as a profile (Profiles tab) instead of being
carried by a package: added by its https address or from a file, one profile
active at a time, in the `Forge/profiles.json` Forge Hub shares. The active
profile is read first, then the sources you added, then the project's presets;
its theme heads the window. An exported profile is the bare document,
`<name>.forge-profile.json`. No secret belongs in it: members that look like
one (a token, a password, a private key...) are taken out when a profile is
added, read again or exported.

## Licensing: a store's key

A store that issues licences declares the Ed25519 public key that signs them
(32 bytes, base64) - data only:

```json
"licensing": {"publicKey": "<base64>"}
```

The window pins it the first time it reads the hub (trust on first use), per
user, in `Forge Store/keys.json` under the local application data. Licences
are checked against the pinned key, never against what the hub serves later -
otherwise whoever can change a store's hub.json could swap the key and sign
licences of their own. A store is known by its hub's manifest URL, by its
preset package's name, or by its Git repository (whatever the tag).

When the hub announces another key:

- **A signed rotation passes alone.** The store keeps its previous key in the
  hub with its signature of the new one: the Ed25519 signature, by the
  previous key, of the UTF-8 text `forge-key-rotation:v1:` followed by the new
  key exactly as written in `publicKey`.

  ```json
  "licensing": {"publicKey": "<new, base64>", "previousKey": "<pinned, base64>", "rotation": "<signature, base64>"}
  ```

  The signature is checked by Forge Seal (`io.glitchr.licensing`) when the
  project has it. Without Forge Seal, the window cannot check it, and the
  change waits for the user like any other. A rotation passes one step only:
  a store that rotated twice since the user's last visit is confirmed by hand.
- **Any other change waits for the user.** The window shows both keys'
  fingerprints and why the change did not pass alone. Until the user accepts
  the new key, the store's licences are not trusted (`StoreTrust.TrustedKey`
  gives null) and its packages are not bought from the window. **Keep the
  pinned key** keeps the old one trusted and stops asking for that new key.
- Sources > Licensing keys lists the pinned keys; **Forget** drops one, whose
  next key is then pinned again on first use.

After a rotation the hub may add `previousKeyValidUntil` (UNIX time): the
licences the previous key signed keep opening until then, 30 days after the
rotation was seen at most. `registry` names the registry the store's licences
are bound to. Forge Hub (the desktop app) reads and writes the same
`keys.json`: one approval per store for both apps, each change read again and
written at once. See [licences.md](licences.md).

Light protection, not DRM: the pin protects the user against a swapped key,
not the store against a determined user.

## A UPM registry

Its URL and its scopes (`com.example`): any npm registry Unity's Package
Manager can read - OpenUPM, Verdaccio, a forge's `/upm`. Installing a
package writes the registry, with these scopes, into the project's
`Packages/manifest.json`.

## A Git package

Its URL, as Unity's Package Manager takes it: `https://...git`, with
`#<tag>` or `?path=<folder>` when needed.

On GitHub, the window reads the package's `package.json` at that tag and
in that folder: its name (so it shows as installed), version and
description - and its `"hub"` section, when it has one, whose sources
are listed too. A package elsewhere, or private, is listed as given.

## A GitHub owner and a repository pattern

An organisation or a user, and a pattern for the names of its repositories:
`*` for any run of characters, `?` for one, several patterns separated by
commas, case ignored. In a hub:

```json
{"name": "Our packages", "type": "github", "owner": "my-org", "pattern": "unity-*"}
```

Every matching repository with a `package.json` at its root that names a
package is listed; the others are skipped. Read through GitHub's REST API:

- `GET /users/{owner}` tells an organisation from a user; then
  `/orgs/{owner}/repos?type=all` (the private repositories a member's token
  can read), `/user/repos?affiliation=owner` when the token is the user's
  own, else `/users/{owner}/repos`; pages followed by their `Link` header;
- `GET /repos/{owner}/{repo}/contents/package.json` (raw) and
  `/repos/{owner}/{repo}/tags`; the branches only when the version picker
  asks for them.

Every request carries the last `ETag` (`If-None-Match`): a `304` reuses the
kept answer, and costs nothing of the rate limit when a token is used. When
`X-RateLimit-Remaining` reaches 0 (or GitHub answers 403/429 with
`Retry-After`), nothing more is sent until the reset; the window shows the
answers it kept. The answers live in `Forge Store/github.json` under the
user's local application data, kept per account (a one-way mark of the token,
never the token).

Public repositories need no account. Private ones need a GitHub token that can
read them (see the README); Unity's own git then installs them with the
user's git credentials. A package installed from a repository is shown as
installed; a tag newer than the installed version offers an update.

## A development site

A site with a self-signed certificate is refused by the editor's requests.
For development only, give the source its certificate's SHA-256 fingerprint
(`openssl x509 -in cert.pem -noout -fingerprint -sha256`): that certificate
is then accepted for that source. Unity's Package Manager needs it too: a
`caFile` in its global configuration (Unity manual, "Configure your network
for Package Manager").
