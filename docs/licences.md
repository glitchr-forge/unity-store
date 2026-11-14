# Licences

A store that sells licensed packages signs each licence with its Ed25519 key.
Forge Store pins that key per user (see [sources.md](sources.md#licensing-a-stores-key)),
checks a licence against the pinned key - never against what the hub serves
today -, and requires the licence to name the store and the package it opens.
Two kinds of store sign licences:

- **A Forge** (omnibase/forge, self-hosted or hosted): the full tier. It sells
  through its checkout (see [buying.md](buying.md)), counts seats and machines,
  revokes. Its tokens are bound to one machine and run 30 days; the software
  activates again online.
- **A static hub** (a template on GitHub Pages, no server): the offline tier.
  Its author signs licences on their own machine (`omnihub licence sign`) and
  sells them through a link of their own (a shop, a payment link). The buyer
  pastes the licence into Forge Store. What can be checked: the store, the
  package, an end, and one machine when the seller asked for it. What cannot:
  seats and revocation.

Light protection, not DRM: a licence checked offline can be copied.

## Bound to the store and the package (format 2)

A licence token is `base64url(payload JSON) "." base64url(Ed25519 signature)`.
Its payload of format 2 names, signed:

- `registry`: the address of the store's Unity registry - a Forge's
  `https://<site>/upm` (its manifest's `licensing.registry`), a static hub's
  registry as its manifest lists it (`sources[0].url`, what Forge Store writes
  as the project's scoped registry);
- `package`: the package's UPM name (null for a game or an application).

Forge Store opens a licence for a package only when, all of them:

1. a key is pinned for its store and no change of key waits for the user;
2. the signature verifies with the pinned key - or with the store's previous
   key, within its bound (below);
3. `v` is 2 or more. A format 1 token names no store: it is refused, with an
   invitation to activate again online (a Forge then signs a format 2 token)
   or to ask the seller for a new one;
4. `registry` is the registry the project installed the package from (the
   lock's `url`, else the scoped registry whose scope matches the package most
   closely), a final slash, the scheme's and the host's case aside - else
   "the licence is another store's": a key signed by store B never opens a
   package of the same name installed from store A;
5. `package` is the package's name;
6. `machine`, when the token names one, is this machine's (SHA-256 of
   Unity's `SystemInfo.deviceUniqueIdentifier`, as Forge Seal computes it);
   absent, any machine;
7. now is before `valid_until`, when the token has one; absent, no end.

`StoreTrust.VerifyLicence(store, token, registry, package, out reason)` is
that check, for whoever needs it in the editor. The signature itself is
Forge Seal's Ed25519 (`io.glitchr.licensing`, any version): without Forge
Seal in the project, no licence is opened ("Forge Seal is not in the
project"). A licensed package usually depends on it anyway.

## After a rotation: the previous key, bounded

When a store rotates its key, the licences its previous key signed are still
running (a Forge's run up to 30 days). The hub then announces, beside the
signed rotation, until when they may:

```json
"licensing": {"publicKey": "<new>", "previousKey": "<old>", "rotation": "<old's signature of the new>",
              "previousKeyValidUntil": 1795564800}
```

On a rotation that passes (or when the user accepts a change whose hub names
the pinned key as `previousKey`), the pin keeps the former key with an end:
`previousUntil = min(previousKeyValidUntil, the moment the change was seen +
30 days)` - a hub.json that serves a far date cannot extend a leaked key.
Without a `previousKeyValidUntil`, the former key is not kept. A licence the
pinned key does not verify is tried with the former key while it opens, and
refused once past ("signed with the store's previous key, which no longer
opens licences since ..."); the former key is then dropped from the pin.

For a static hub this means: an offline licence without an end, signed with
the old key, stops opening at most 30 days after the rotation is seen - its
seller signs it again with the new key and the buyer pastes the new one.

## A static hub's licences, pasted

### Which packages take a licence: `licensed`

A static hub has no catalogue, so its hub.json says what it sells (omnihub
writes it from `hub.yaml`, only beside `licensing`):

```json
"licensing": {"publicKey": "<base64>", "registry": "https://owner.github.io/hub"},
"licensed": [{"package": "com.example.tool", "get": "https://shop.example.org/buy/tool"},
             {"package": "com.example.kit"}]
```

- On a store that publishes `licensed`, **Enter a licence** shows on the
  packages it lists only (Packages and Installed tabs alike); the others are
  free and show no licence field. An empty list: the store sells nothing.
- A listed package with a `get` link gets a **Get** button: the link opens in
  the browser, and the licence field and this machine's licence id unfold
  for the seller's form. The link is used for nothing else, and only when it
  is an absolute `https://` address with a host and without a user name or
  password before it;
  any other (`http:`, `javascript:`, `file:`, a relative path...) is not
  opened and the window says why.
- A store that publishes a key but no `licensed` (an older hub) keeps
  Forge Store 1.5.0's behaviour: the field on every package of its registry.
- Entries of `licensed` that are not objects with a `package` (a plain
  string, say) are ignored; a `licensed` that is not a list counts as absent.
  Without a key, `licensed` is ignored: no field anywhere.
- `licensing.registry` (omnihub writes the hub's `sources[0].url`) is read
  without its final slash. A licence stays bound to the registry the project
  installed the package from (else the one it would come from); when that is
  not the registry the store's licences name - a package another upm source
  of the hub serves, say -, the field warns that a licence of this store would
  be refused there. Nothing in the verification changes.
- A Forge is not concerned: it sells through its checkout, its activated
  licences arrive by themselves.

### This machine's licence id

For a licence bound to one machine, the seller needs the buyer's machine: under
Enter a licence, **This machine's licence id** (a foldout, shown on demand)
gives it with **Copy** - 64 lowercase hex digits, the SHA-256 of Unity's
`SystemInfo.deviceUniqueIdentifier` trimmed, in UTF-8: exactly what the
check above compares with a licence's `machine`, what Forge Seal's
`PackageLicence` compares, and what `omnihub licence sign --machine-hash`
takes as given (`--machine <identifier>` hashes the raw identifier the same
way; on macOS Unity reports it in uppercase). It is not a secret - it opens
nothing by itself - and the raw identifier is never shown. Without it, the
seller signs a licence that opens on any machine. `StoreTrust.MachineLicenceId`.

### Pasting it

The buyer pastes the token the seller sent (one line), **Check and keep**
checks it as above - against the store's pinned key, the registry the
package comes from (or would come from) and the package's name - and keeps
it. A kept licence shows its licensee and its validity ("no end", or "until"
a day; any machine or this machine) and can be removed. A licence that no
longer opens (another machine, past its end, the store's key changed) says
why. A wrong or foreign token is refused with the reason, and nothing is kept.

The payload of an offline licence (`omnihub licence sign`):

```json
{"v": 2, "registry": "https://owner.github.io/hub", "package": "com.example.tool",
 "licensee": "Ada <ada@example.org>", "issued_at": 1792972800}
```

with `valid_until` and `updates_until` optional, and `machine` (SHA-256 hex
of the buyer's machine identifier) only when the seller binds it to one.

### Where licences are kept, and why there

`Forge Store/licences.json` under the user's local application data, beside
`keys.json` (`~/.local/share` on macOS and Linux unless `XDG_DATA_HOME` is
set, `%LOCALAPPDATA%` on Windows - where Unity's Mono puts it):

```json
{"version": 1, "licences": [
  {"registry": "https://owner.github.io/hub", "package": "com.example.tool",
   "store": "manifest:https://owner.github.io/hub/-/hub.json", "token": "...", "addedAt": "2026-11-13T09:00:00Z"}
]}
```

Never in the project: a licence names its buyer and is theirs, it opens the
package in every project of that user, and a project's repository travels -
to teammates, who need licences of their own, and sometimes to the public.
One licence per registry and package; read again before each change, unknown
fields kept, written through a temporary file renamed over it, never written
over when it is not JSON.

The package checks itself with Forge Seal 1.1.0 or later, which reads that
file: `PackageLicence.Check(registry, package, publicKey, out refusal)`.

## keys.json, shared with Forge Hub

Forge Hub (the desktop app) pins keys in the very same file, so one approval
per store holds for both apps. Forge Store reads it again before every
decision (a key announced, Accept, Keep, Forget, a licence's check), writes
each change at once, changes only the store it decides on and only the fields
it owns (`key`, `name`, `pending`, `reason`, `refused`, `previous`,
`previousUntil`), keeps every other store and field, writes the stores sorted
by name with a two-space indent through a temporary file renamed over the
file, and reads a file that does not parse a few times (40 ms apart) before
calling it damaged - then keeps it aside as `keys.json.damaged` and pins its
stores again on their next reading.

```json
{"manifest:https://hub.example.com/hub.json": {"key": "<base64>", "name": "Example",
  "previous": "<base64>", "previousUntil": 1795564800}}
```

## Buying

A Forge that takes one-click purchases (its manifest's `checkout.quote`,
`purchases` and `methods`) is bought from in the window, once the editor is
linked to it: **Buy** opens a confirmation with the price the Forge charges
this user (taxes included), its currency, the store's name and the saved
payment method, and **Pay** confirms it; a stray click never charges. Any
other hub that sells keeps the device-flow page in the browser, where the
shop's own checkout confirms the payment. See [buying.md](buying.md).
