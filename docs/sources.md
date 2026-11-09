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
- `checkout`, when present, lets the window offer to buy (see
  [buying.md](buying.md)).

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

## A development site

A site with a self-signed certificate is refused by the editor's requests.
For development only, give the source its certificate's SHA-256 fingerprint
(`openssl x509 -in cert.pem -noout -fingerprint -sha256`): that certificate
is then accepted for that source. Unity's Package Manager needs it too: a
`caFile` in its global configuration (Unity manual, "Configure your network
for Package Manager").
