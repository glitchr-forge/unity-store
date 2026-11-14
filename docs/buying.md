# Buying

A paid package of a hub that sells shows **Buy** and its lowest price. The
editor has no session on the hub's site, so the purchase follows OAuth's
device flow (RFC 8628):

1. The window asks the hub for a code (`checkout.device`). The hub answers a
   device code, kept by the window, a user code such as `ABCD-EFGH`, and a
   page.
2. The page opens in the browser. Signed in, the user sees the code, buys the
   package's licence through the site's shop, then presses "Link this
   editor".
3. Meanwhile the window asks the hub (`checkout.token`) every few seconds.
   Once linked, it receives the user's licence key - once - and the registry
   it opens.
4. The key is written as that registry's token in the user's
   `~/.upmconfig.toml` (`%USERPROFILE%\.upmconfig.toml` on Windows, or the
   file `UPM_USER_CONFIG_FILE` names):

   ```toml
   [npmAuth."https://forge.example.com/upm"]
   token = "glk_..."
   alwaysAuth = true
   ```

5. Unity's Package Manager reads that file when Unity starts: the window
   offers to restart the project; then **Install**.

The key opens every licence its seat's e-mail holds on that hub, as it does
for Composer. Nothing of it is written in the project.

A user who holds no licence there yet receives no key: only the account
token below. The window says so, or goes on to the confirmation when the
hub takes one-click purchases.

## Buying in one click

A Forge that takes one-click purchases lists three more addresses in its
manifest's `checkout`: `quote`, `purchases` and `methods`, on the same host
as `token`. Linking the editor (the device flow above, once) then also gives
the user's **account token**: it is kept by the system's secret store for
that Forge (the macOS keychain, Windows' DPAPI for this user, the Secret
Service on Linux), never in the project, and sent to that Forge's addresses
only, as `Authorization: Bearer`. A Forge that answers 401 to it no longer
knows the editor: the token is forgotten and Buy links the editor again.

1. **Buy** opens the confirmation. The window asks the Forge
   `GET <quote>?offer=<id>` (the offer's `id` in the catalogue; a package with
   several offers has an Offer menu): the price this user is charged, in
   minor units, taxes and promotions included - it may differ from the
   catalogue's price, which is before tax -, its currency, the store's name
   and the saved payment method it would be charged to. Nothing is charged.
2. **Pay** sends `POST <purchases>` `{"offer", "price", "currency",
   "method"}` exactly as shown, with an `Idempotency-Key` made for this
   confirmation. A retry (a timeout, a second click) sends the same key: the
   Forge answers the same purchase and charges nothing again. Changing the
   offer or the method, or a new price, makes a new confirmation and a new
   key.
3. What the Forge answers:
   - **paid**: the licence is issued and its key handed over (once); it is
     kept in `~/.upmconfig.toml` as above and the window offers to restart
     the project.
   - **price changed** (409 `price_changed`): the amount is no longer the
     one confirmed - nothing is charged. The window shows the new price
     beside the old one and asks again.
   - **requires action** (3-D Secure): the bank's page opens in the browser;
     the window asks `GET <purchases>/<id>` every three seconds, ten minutes
     at most, until the purchase is paid or refused. Closing the window
     stops asking, not the payment.
   - **refused** (402): the bank's reason; nothing is charged. Another
     method can be chosen.
   - **payment method required**: none is saved yet.
   - **purchase in progress**: another purchase of the user is running; the
     same confirmation is sent again a few times, three seconds apart.
   - an unknown offer, payments unavailable, a reused key: said in a
     sentence, nothing charged.

### The statements: terms of sale and withdrawal

A Forge 1.1 asks its buyer, before paying, for statements the law requires
for digital content supplied at once: the terms of sale accepted, immediate
supply asked for and the right of withdrawal waived (Code de la
consommation L221-28 13°). Its quote gives them (`consent`), worded by the
Forge in the editor's language (Preferences > Languages, else the system's;
sent as `?locale=`, e.g. `fr`, `zh_CN`).

- The confirmation shows each statement **as the Forge words it** - the
  window adds no wording of its own -, each with its own box, **none ticked**;
  a statement with a link (the terms of sale) has a **Read** button that opens
  it in the browser (https only). A text that does not match the hash the
  Forge gives beside it is not shown, and the purchase cannot go on.
- **Pay** stays disabled until every box is ticked. The purchase sends
  `consent` `{"version", "locale", "<statement id>": true...}`: the version
  and language shown, unchanged.
- **409 `consent_changed`**: the Forge's texts changed since they were shown
  (reworded, another language). Nothing is charged; the new texts are shown,
  every box unticked, and Pay makes a new confirmation (a new key).
- **422 `consent_required`**: a statement was not ticked - the boxes are shown
  again. From a Forge whose quote gave no statements, the window says so: the
  Forge asks statements its confirmation did not give, nothing was charged,
  buy on its page.
- A Forge whose quote has no `consent` (1.0), or an empty list, is bought
  from as before: nothing is sent.
- Nothing is remembered: every purchase asks again.

Payment methods are listed in the confirmation (**Payment methods**):
**Add a payment method** asks the Forge for its payment provider's page
(`POST <methods>`) and opens it in the browser - the card is entered there,
never in Unity, and the Forge keeps no card number -; **Read again** lists
them once it is saved; **Remove** deletes one (`DELETE <methods>/<id>`).
Pages the Forge gives to open are opened only when they are `https`
addresses without a user name (`http` on this machine only, for a
development Forge).
