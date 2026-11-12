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
  and dark skins; the logo is an HTTPS image of 1 MB at most. The window draws
  the accent and the logo; `banner` is read for the clients with room for one. Without a theme
  (the older `color` and `logo` keys count as one), the window keeps its
  plain look.

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
It shows under Sources as a preset, which the user can hide (and show again).
Nothing of the package runs: the window reads its JSON.

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
