using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;

namespace Glitchr.Store.Tests
{
    /// <summary>
    /// The one-click purchase against a fake forge answering as omnibase/forge's
    /// HubPurchaseController does (its docs/hub.md, "Buying in one click"): the quote, the payment
    /// methods, the purchase with its idempotency key, 409 price_changed, 3-D Secure, 402, the
    /// device flow's account token. Account tokens stay in memory (AccountTokens.MemoryOnly), the
    /// licence key goes to a temporary .upmconfig.toml: neither the keychain nor the user's files.
    /// </summary>
    public class PurchaseTests
    {
        private const string Token = "gla_0123456789abcdef0123456789abcdef";
        private const string Forge = "https://forge.example.test";
        private const string Hub = Forge + "/upm/-/hub";

        private sealed class Seen
        {
            public string Method, Url, Bearer, Key;
            public object Body;
        }

        /// <summary>A forge as HubPurchaseController answers, in memory.</summary>
        private sealed class FakeForge : IHubRequests
        {
            public long Price = 1200;
            public List<object> Methods = new List<object>();
            public string Mode = "paid";
            public readonly Dictionary<string, string> ByKey = new Dictionary<string, string>();
            public readonly Dictionary<string, List<string>> Statuses = new Dictionary<string, List<string>>();
            public readonly HashSet<string> Handed = new HashSet<string>();
            public readonly List<Seen> Requests = new List<Seen>();
            /// <summary>The statements this forge asks (1.1): id, English, French; null: a 1.0 forge.</summary>
            public List<(string Id, string En, string Fr)> Statements;
            /// <summary>Asked at the purchase although the quote gives none.</summary>
            public bool HiddenConsent;
            /// <summary>The host rewords its texts after the quote.</summary>
            public bool Reworded;
            public readonly List<string> Locales = new List<string>();
            /// <summary>A forge 1.1 with key_handed, a purchase's key again and the account's links.</summary>
            public bool GapsClosed;
            public int Rekeyed;
            public bool SignedOut, OtherRevoked;

            public Dictionary<string, object> Consent(string locale)
            {
                bool fr = (locale ?? string.Empty).StartsWith("fr");
                var statements = new List<object>();
                foreach (var (id, en, frText) in Statements)
                {
                    string text = (fr ? frText : en) + (Reworded ? " (v2)" : string.Empty);
                    var statement = new Dictionary<string, object> { ["id"] = id, ["text"] = text, ["hash"] = Sha(text) };
                    if (id == "terms") statement["url"] = "https://shop.example.com/terms";
                    statements.Add(statement);
                }
                string version = Sha(string.Concat(statements.Select(st => Json.Get(st, "id") + ":" + Json.Get(st, "hash"))));

                return new Dictionary<string, object> { ["required"] = true, ["version"] = version, ["locale"] = fr ? "fr" : "en", ["terms_url"] = "https://shop.example.com/terms", ["statements"] = statements };
            }

            private static HubHttp.Answer Answer(long status, object body) => new HubHttp.Answer { Status = status, Text = body == null ? null : Json.Write(body) };

            private Dictionary<string, object> Purchase(string id, string status)
            {
                var body = new Dictionary<string, object> { ["id"] = id, ["status"] = status, ["offer"] = 7, ["price"] = Price, ["currency"] = "EUR", ["software"] = "example-tool", ["package"] = "com.example.tool" };
                if (status == "paid" && Handed.Add(id))
                {
                    body["key"] = "glk_newkey0123456789";
                    body["email"] = "jeanne@example.org";
                    body["registry"] = Forge + "/upm";
                }
                if (status == "paid" && GapsClosed) body["key_handed"] = true;
                if (status == "requires_action") body["url"] = "https://bank.example.test/3ds/abc";
                if (status == "refused") body["reason"] = "Your card was declined.";

                return body;
            }

            public Task<HubHttp.Answer> Send(string method, string url, object body, string bearer, IDictionary<string, string> headers, string certificate)
            {
                string key = headers != null && headers.TryGetValue("Idempotency-Key", out string k) ? k : null;
                Requests.Add(new Seen { Method = method, Url = url, Bearer = bearer, Key = key, Body = body });

                return Task.FromResult(Route(method, url, body, bearer, key));
            }

            private HubHttp.Answer Route(string method, string url, object body, string bearer, string key)
            {
                if (bearer != Token || SignedOut) return Answer(401, new Dictionary<string, object> { ["error"] = "unauthorized" });
                string path = url.Substring(Hub.Length);
                if (GapsClosed && path.StartsWith("/account-tokens"))
                {
                    if (method == "GET" && path == "/account-tokens")
                    {
                        var tokens = new List<object> { new Dictionary<string, object> { ["id"] = 12, ["prefix"] = Token.Substring(0, 12), ["created"] = "2026-11-14T09:30:00+00:00", ["last_used"] = null, ["current"] = true } };
                        if (!OtherRevoked) tokens.Add(new Dictionary<string, object> { ["id"] = 7, ["prefix"] = "gla_WXYZ-ABC", ["created"] = "2026-11-10T08:00:00+00:00", ["last_used"] = "2026-11-13T08:00:00+00:00", ["current"] = false });
                        return Answer(200, new Dictionary<string, object> { ["tokens"] = tokens });
                    }
                    if (method == "DELETE" && path == "/account-tokens/current") { SignedOut = true; return Answer(204, null); }
                    if (method == "DELETE" && path == "/account-tokens/7" && !OtherRevoked) { OtherRevoked = true; return Answer(204, null); }
                    return Answer(404, new Dictionary<string, object> { ["error"] = "unknown_account_token" });
                }
                if (GapsClosed && method == "POST" && path.StartsWith("/purchases/") && path.EndsWith("/key"))
                {
                    string id = path.Substring("/purchases/".Length, 32);
                    if (!Statuses.TryGetValue(id, out List<string> states)) return Answer(404, new Dictionary<string, object> { ["error"] = "unknown_purchase" });
                    if (states[0] != "paid") return Answer(409, new Dictionary<string, object> { ["error"] = "purchase_not_paid", ["status"] = states[0] });
                    Dictionary<string, object> again = Purchase(id, "paid");
                    again["key"] = "glk_again" + (++Rekeyed);
                    again["email"] = "jeanne@example.org";
                    again["registry"] = Forge + "/upm";
                    return Answer(200, again);
                }
                if (method == "GET" && path.StartsWith("/quote?"))
                {
                    var query = path.Substring("/quote?".Length).Split('&').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1]);
                    if (!query.TryGetValue("offer", out string offer) || offer != "7") return Answer(404, new Dictionary<string, object> { ["error"] = "unknown_offer" });
                    Locales.Add(query.TryGetValue("locale", out string l) ? l : null);
                    object usable = Methods.FirstOrDefault(m => !(Json.Get(m, "expired") is bool e && e));
                    var quote = new Dictionary<string, object> { ["offer"] = 7, ["title"] = "Example Tool, one seat", ["software"] = "example-tool", ["package"] = "com.example.tool", ["store"] = "Atelier Exemple", ["price"] = Price, ["currency"] = "EUR", ["method"] = usable };
                    if (Statements != null && !HiddenConsent) quote["consent"] = Consent(query.TryGetValue("locale", out string asked) ? asked : "en");

                    return Answer(200, quote);
                }
                if (method == "GET" && path == "/payment-methods") return Answer(200, new Dictionary<string, object> { ["gateway"] = "stripe", ["methods"] = Methods });
                if (method == "POST" && path == "/payment-methods") return Answer(201, new Dictionary<string, object> { ["id"] = "cs_1", ["url"] = "https://checkout.provider.example.test/setup/cs_1" });
                if (method == "DELETE" && path.StartsWith("/payment-methods/"))
                {
                    string id = path.Substring("/payment-methods/".Length);
                    return Methods.RemoveAll(m => (string)Json.Get(m, "id") == id) > 0 ? Answer(204, null) : Answer(404, new Dictionary<string, object> { ["error"] = "unknown_payment_method" });
                }
                if (method == "POST" && path == "/purchases")
                {
                    if (key == null) return Answer(400, new Dictionary<string, object> { ["error"] = "idempotency_key_required" });
                    if (ByKey.TryGetValue(key, out string done)) return Answer(200, Purchase(done, Statuses[done][0]));
                    if (Mode == "unavailable") return Answer(503, new Dictionary<string, object> { ["error"] = "payments_unavailable" });
                    if (Mode == "busy") return Answer(409, new Dictionary<string, object> { ["error"] = "purchase_in_progress" });
                    if (Methods.Count == 0) return Answer(409, new Dictionary<string, object> { ["error"] = "payment_method_required" });
                    if (!(Json.Get(body, "price") is long confirmed) || confirmed != Price || (string)Json.Get(body, "currency") != "EUR")
                    {
                        return Answer(409, new Dictionary<string, object> { ["error"] = "price_changed", ["offer"] = 7, ["price"] = Price, ["currency"] = "EUR" });
                    }
                    if (Statements != null)
                    {
                        object sent = Json.Get(body, "consent");
                        Dictionary<string, object> now = Consent(Json.Get(sent, "locale") as string ?? "en");
                        List<string> ids = Statements.Select(st => st.Id).ToList();
                        if (sent == null) return Answer(422, new Dictionary<string, object> { ["error"] = "consent_required", ["missing"] = ids.Cast<object>().ToList(), ["consent"] = now });
                        if ((string)Json.Get(sent, "version") != (string)now["version"]) return Answer(409, new Dictionary<string, object> { ["error"] = "consent_changed", ["consent"] = now });
                        List<object> missing = ids.Where(id => !(Json.Get(sent, id) is bool b && b)).Cast<object>().ToList();
                        if (missing.Count > 0) return Answer(422, new Dictionary<string, object> { ["error"] = "consent_required", ["missing"] = missing, ["consent"] = now });
                    }
                    string id = (ByKey.Count + 1).ToString("x32");
                    ByKey[key] = id;
                    Statuses[id] = Mode == "3ds" ? new List<string> { "requires_action", "pending", "paid" } : new List<string> { Mode == "refused" ? "refused" : "paid" };
                    Dictionary<string, object> purchase = Purchase(id, Statuses[id][0]);
                    if (Mode == "refused")
                    {
                        purchase["error"] = "payment_refused";
                        return Answer(402, purchase);
                    }

                    return Answer(201, purchase);
                }
                if (method == "GET" && path.StartsWith("/purchases/"))
                {
                    string id = path.Substring("/purchases/".Length);
                    if (!Statuses.TryGetValue(id, out List<string> list)) return Answer(404, new Dictionary<string, object> { ["error"] = "unknown_purchase" });
                    string status = list[0];
                    if (list.Count > 1) list.RemoveAt(0);

                    return Answer(200, Purchase(id, status));
                }

                return Answer(404, new Dictionary<string, object> { ["error"] = "not here" });
            }
        }

        private static string Sha(string text)
        {
            using (SHA256 sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", string.Empty).ToLowerInvariant();
        }

        private static readonly (string, string, string) Terms = ("terms", "I have read and accept the terms of sale.", "J'ai lu et j'accepte les conditions générales de vente.");
        private static readonly (string, string, string) Supply = ("immediate_supply",
            "I ask for the digital content to be supplied immediately, as soon as I pay, and I acknowledge that I thereby lose my right of withdrawal.",
            "Je demande la fourniture immédiate du contenu numérique, dès le paiement, et je reconnais perdre ainsi mon droit de rétractation.");

        private static object LastPurchaseBody(FakeForge forge) => forge.Requests.Last(r => r.Method == "POST" && r.Url.EndsWith("/purchases")).Body;

        [Test]
        public void TheStatementsAreShownAsTheForgeWordsThemAndSentBackTicked()
        {
            var forge = new FakeForge { Methods = { Card("pm_1") }, Statements = new List<(string, string, string)> { Terms, Supply } };
            HubPurchases purchases = Purchases(forge);
            PurchaseQuote quote = Sync(purchases.Quote(7, "fr-FR"));
            CollectionAssert.AreEqual(new[] { "fr_FR" }, forge.Locales, "the editor's language, as the forge takes it");
            Assert.IsTrue(quote.Consent.Required);
            Assert.AreEqual("fr", quote.Consent.Locale);
            CollectionAssert.AreEqual(new[] { "terms", "immediate_supply" }, quote.Consent.Statements.Select(st => st.Id), "in the forge's order");
            Assert.AreEqual(Supply.Item3, quote.Consent.Statements[1].Text, "the forge's text, word for word");
            Assert.AreEqual("https://shop.example.com/terms", quote.Consent.Statements[0].Url);
            Assert.IsFalse(quote.Consent.AllTicked(new[] { "terms" }), "Pay waits for every box");

            // One box ticked only: refused, nothing charged.
            PurchaseOutcome refused = Sync(purchases.Buy(PurchaseConfirmation.Of(quote, "pm_1", new[] { "terms" })));
            Assert.AreEqual(PurchaseOutcome.Kind.ConsentRequired, refused.What);
            CollectionAssert.AreEqual(new[] { "immediate_supply" }, refused.Missing);
            Assert.IsFalse(refused.Unasked);
            object sent = Json.Get(LastPurchaseBody(forge), "consent");
            Assert.AreEqual("{\"version\":\"" + quote.Consent.Version + "\",\"locale\":\"fr\",\"terms\":true}", Json.Write(sent), "true for what was ticked, nothing for the rest");

            PurchaseOutcome paid = Sync(purchases.Buy(PurchaseConfirmation.Of(quote, "pm_1", new[] { "terms", "immediate_supply" })));
            Assert.AreEqual(PurchaseOutcome.Kind.Paid, paid.What);
            Assert.AreEqual(true, Json.Get(LastPurchaseBody(forge), "consent", "immediate_supply"), "JSON true");
            Assert.AreEqual(1, forge.ByKey.Count);
        }

        [Test]
        public void TextsChangedAfterTheQuoteAreShownAgainAndConfirmedWithANewKey()
        {
            var forge = new FakeForge { Methods = { Card("pm_1") }, Statements = new List<(string, string, string)> { Supply } };
            HubPurchases purchases = Purchases(forge);
            PurchaseQuote quote = Sync(purchases.Quote(7, "en"));
            forge.Reworded = true;
            PurchaseConfirmation first = PurchaseConfirmation.Of(quote, "pm_1", new[] { "immediate_supply" });
            PurchaseOutcome changed = Sync(purchases.Buy(first));
            Assert.AreEqual(PurchaseOutcome.Kind.ConsentChanged, changed.What);
            StringAssert.EndsWith("(v2)", changed.Consent.Statements[0].Text, "the texts now");
            Assert.AreNotEqual(quote.Consent.Version, changed.Consent.Version);
            Assert.AreEqual(0, forge.ByKey.Count, "nothing charged, no purchase");

            quote.Consent = changed.Consent;
            PurchaseConfirmation again = PurchaseConfirmation.Of(quote, "pm_1", new[] { "immediate_supply" });
            Assert.AreNotEqual(first.Key, again.Key);
            Assert.AreEqual(PurchaseOutcome.Kind.Paid, Sync(purchases.Buy(again)).What);
        }

        [Test]
        public void AForgeThatAsksStatementsItsQuoteDidNotGiveIsSaidPlainly()
        {
            var forge = new FakeForge { Methods = { Card("pm_1") }, Statements = new List<(string, string, string)> { Supply }, HiddenConsent = true };
            HubPurchases purchases = Purchases(forge);
            PurchaseQuote quote = Sync(purchases.Quote(7));
            Assert.IsNull(quote.Consent, "as a 1.0 forge's quote");
            PurchaseOutcome refused = Sync(purchases.Buy(PurchaseConfirmation.Of(quote, "pm_1")));
            Assert.AreEqual(PurchaseOutcome.Kind.ConsentRequired, refused.What);
            Assert.IsTrue(refused.Unasked, "the confirmation showed none");
            Assert.IsNull(Json.Get(LastPurchaseBody(forge), "consent"), "nothing sent when nothing was shown");
        }

        [Test]
        public void AStatementThatDoesNotMatchItsHashIsNotShownAndLanguagesAreRead()
        {
            StringAssert.Contains("does not match its hash", Assert.Throws<Exception>(() => PurchaseConsent.Of(Json.Parse("{\"required\":true,\"version\":\"v\",\"locale\":\"en\",\"statements\":[{\"id\":\"terms\",\"text\":\"I accept.\",\"hash\":\"00\"}]}"))).Message);
            Assert.IsNull(PurchaseConsent.Of(null), "a 1.0 forge");
            Assert.IsNull(PurchaseConsent.Of(Json.Parse("{\"required\":false,\"version\":\"\",\"locale\":\"\",\"statements\":[]}")).Sent(new string[0]), "nothing asked: nothing sent");
            Assert.AreEqual("fr_FR", HubPurchases.Locale("fr-FR"));
            Assert.AreEqual("en", HubPurchases.Locale("en"));
            Assert.AreEqual("zh", HubPurchases.Locale("zh-Hant-TW"), "a script and a region: the language alone");
            Assert.IsNull(HubPurchases.Locale("x"));
            Assert.IsNull(HubPurchases.Locale("fr&offer=8"));
            Assert.AreEqual("fr", HubPurchases.LanguageTag(UnityEngine.SystemLanguage.French));
            Assert.IsNull(HubPurchases.LanguageTag(UnityEngine.SystemLanguage.Unknown));
            Assert.DoesNotThrow(() => HubPurchases.EditorLanguage());
        }

        [Test]
        public void ALostPaidAnswerIsFoundAgainAndItsKeyMadeAgain()
        {
            var forge = new FakeForge { Methods = { Card("pm_1") }, GapsClosed = true };
            HubPurchases purchases = Purchases(forge);
            PurchaseQuote quote = Sync(purchases.Quote(7));
            PurchaseConfirmation sent = PurchaseConfirmation.Of(quote, "pm_1");
            // The paid answer carrying the key never arrives: the forge handed it, this editor has none.
            string id = 1.ToString("x32");
            forge.ByKey[sent.Key] = id;
            forge.Statuses[id] = new List<string> { "paid" };
            forge.Handed.Add(id);

            PurchaseOutcome replay = Sync(purchases.Buy(sent));
            Assert.IsTrue(replay.What == PurchaseOutcome.Kind.Paid && replay.Replayed && !replay.KeyKept);
            Assert.IsTrue(replay.KeyHanded);
            Assert.IsTrue(replay.KeyLost, "handed by the forge, never received here");
            Assert.AreEqual(id, replay.Id, "the replay gives the purchase's id back");

            PurchaseOutcome again = Sync(purchases.KeyAgain(replay.Id));
            Assert.IsTrue(again.KeyKept && !again.KeyLost);
            CollectionAssert.AreEqual(new[] { (Forge + "/upm", "glk_again1") }, kept);
            Sync(purchases.KeyAgain(replay.Id));
            Assert.AreEqual("glk_again2", kept.Last().Key, "asked again: a newer key, kept in place of the other");

            StringAssert.Contains("no such purchase", Assert.Throws<Exception>(() => Sync(purchases.KeyAgain(new string('f', 32)))).Message);
            Assert.Throws<ArgumentException>(() => Sync(purchases.KeyAgain("../x")), "never sent");
            forge.Statuses[new string('e', 32)] = new List<string> { "pending" };
            StringAssert.Contains("not paid yet", Assert.Throws<Exception>(() => Sync(purchases.KeyAgain(new string('e', 32)))).Message);
        }

        [Test]
        public void AKeyThatCannotBeWrittenIsAskedAgainAndAForgeWithoutKeyHandedLosesNothing()
        {
            var forge = new FakeForge { Methods = { Card("pm_1") }, GapsClosed = true };
            var failing = new HubPurchases(Origin(), Token, forge, (registry, key) => throw new IOException("disk full"));
            PurchaseOutcome paid = Sync(failing.Buy(PurchaseConfirmation.Of(Sync(failing.Quote(7)), "pm_1")));
            Assert.AreEqual(PurchaseOutcome.Kind.Paid, paid.What, "paid all the same");
            Assert.IsTrue(paid.KeyLost);
            StringAssert.Contains("disk full", paid.KeyError);

            var older = new FakeForge { Methods = { Card("pm_1") } };
            HubPurchases purchases = Purchases(older);
            PurchaseConfirmation sent = PurchaseConfirmation.Of(Sync(purchases.Quote(7)), "pm_1");
            Sync(purchases.Buy(sent));
            PurchaseOutcome replay = Sync(purchases.Buy(sent));
            Assert.IsFalse(replay.KeyLost, "a forge 1.0: nothing to ask again");
        }

        [Test]
        public void SigningOutRevokesTheLinkOnTheForgeThenForgetsIt()
        {
            HubOrigin origin = Origin();
            origin.AccountTokensUrl = Hub + "/account-tokens";
            Assert.AreEqual(Hub + "/account-tokens", HubPurchases.AccountTokensUrl(origin));
            HubOrigin elsewhere = Origin();
            elsewhere.AccountTokensUrl = "https://elsewhere.example.org/x";
            Assert.IsNull(HubPurchases.AccountTokensUrl(elsewhere), "the token never goes to another host");

            var forge = new FakeForge { GapsClosed = true };
            AccountTokens.Keep(Forge, Token);
            var purchases = new HubPurchases(origin, Token, forge);
            List<LinkedToken> tokens = Sync(purchases.LinkedTokens());
            Assert.AreEqual(2, tokens.Count);
            Assert.IsTrue(tokens[0].Current && !tokens[1].Current);
            StringAssert.StartsWith("this editor", tokens[0].Shown);
            purchases.Revoke(7).GetAwaiter().GetResult();
            StringAssert.Contains("not one of your account's", Assert.Throws<Exception>(() => purchases.Revoke(7).GetAwaiter().GetResult()).Message);

            Assert.IsTrue(Sync(purchases.SignOut()), "revoked on the forge");
            Assert.IsTrue(forge.SignedOut);
            Assert.IsNull(AccountTokens.Get(Forge), "then forgotten here");

            // A forge that lists no links: forgotten here only.
            AccountTokens.Keep(Forge, Token);
            var older = new FakeForge();
            Assert.IsFalse(Sync(new HubPurchases(Origin(), Token, older).SignOut()));
            Assert.IsNull(AccountTokens.Get(Forge));
            Assert.IsEmpty(older.Requests, "nothing asked of a forge 1.0");
        }

        private string directory;
        private readonly List<(string Registry, string Key)> kept = new List<(string, string)>();

        [SetUp]
        public void SetUp()
        {
            AccountTokens.MemoryOnly = true;
            directory = Path.Combine(Path.GetTempPath(), "forge-store-purchase-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            Environment.SetEnvironmentVariable("UPM_USER_CONFIG_FILE", Path.Combine(directory, ".upmconfig.toml"));
            kept.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            AccountTokens.Forget(Forge);
            AccountTokens.MemoryOnly = false;
            Environment.SetEnvironmentVariable("UPM_USER_CONFIG_FILE", null);
            Directory.Delete(directory, true);
        }

        private static T Sync<T>(Task<T> task) => task.GetAwaiter().GetResult();

        private static HubOrigin Origin() => new HubOrigin
        {
            Source = new HubSource { Type = HubSource.Manifest, Url = Hub + ".json" },
            HubName = "Atelier Exemple",
            Registry = Forge + "/upm",
            DeviceUrl = Hub + "/device",
            TokenUrl = Hub + "/token",
            QuoteUrl = Hub + "/quote",
            PurchasesUrl = Hub + "/purchases",
            MethodsUrl = Hub + "/payment-methods",
        };

        private HubPurchases Purchases(FakeForge forge, string token = Token) => new HubPurchases(Origin(), token, forge, (registry, key) => kept.Add((registry, key)));

        private static Dictionary<string, object> Card(string id, bool expired = false) => new Dictionary<string, object> { ["id"] = id, ["type"] = "card", ["label"] = "Visa •••• 4242", ["brand"] = "visa", ["last4"] = "4242", ["expires"] = "2030-12", ["expired"] = expired };

        [Test]
        public void AmountsAddressesAndConfirmationKeys()
        {
            Assert.AreEqual("12.00 EUR", HubAmount.Format(1200, "eur"));
            Assert.AreEqual("0.05 USD", HubAmount.Format(5, "USD"));
            Assert.AreEqual("1200 JPY", HubAmount.Format(1200, "JPY"), "no minor unit");
            Assert.AreEqual("https://forge.example.test", HubPurchases.Authority("https://Forge.Example.TEST/upm/-/hub/token"));
            Assert.AreEqual("http://127.0.0.1:8443", HubPurchases.Authority("http://127.0.0.1:8443/x"));
            Assert.IsNull(HubPurchases.Authority("https://user:pw@forge.example.test/x"));
            Assert.IsNull(HubPurchases.Authority("file:///etc/passwd"));
            Assert.AreEqual("https://bank.example.test/3ds", HubPurchases.Openable("https://bank.example.test/3ds"));
            Assert.DoesNotThrow(() => HubPurchases.Openable("http://localhost:8000/x"), "a development forge on this machine");
            Assert.Throws<Exception>(() => HubPurchases.Openable("http://bank.example.test/3ds"));
            Assert.Throws<Exception>(() => HubPurchases.Openable("javascript:alert(1)"));
            string a = HubPurchases.NewKey(), b = HubPurchases.NewKey();
            Assert.IsTrue(a.Length == 32 && a.All(Uri.IsHexDigit) && a != b);
        }

        [Test]
        public void OneClickOnlyOnTheHostThatGaveTheAccountToken()
        {
            Assert.IsTrue(Origin().OneClick);
            HubOrigin elsewhere = Origin();
            elsewhere.PurchasesUrl = "https://elsewhere.example.org/upm/-/hub/purchases";
            Assert.IsFalse(elsewhere.OneClick, "the token would be sent to another host");
            HubOrigin noQuote = Origin();
            noQuote.QuoteUrl = null;
            Assert.IsFalse(noQuote.OneClick, "a forge without quote: the device flow only");
            Assert.Throws<InvalidOperationException>(() => new HubPurchases(noQuote, Token));
            Assert.IsNull(HubPurchases.For(Origin()), "not linked yet");
        }

        [Test]
        public void TheCataloguesOffersAreRead()
        {
            var offers = HubOffer.ListOf(Json.Array(Json.Parse("[{\"id\":7,\"title\":\"One seat\",\"price\":1000,\"currency\":\"EUR\",\"seats\":1,\"months\":12},{\"id\":8,\"title\":\"Team\",\"price\":50000,\"currency\":\"JPY\",\"seats\":5},{\"title\":\"no id\"}]")));
            Assert.AreEqual(2, offers.Count, "an offer without id cannot be bought");
            Assert.AreEqual(7, offers[0].Id);
            Assert.AreEqual(1000, offers[0].Price);
            Assert.AreEqual(12, offers[0].Months);
            Assert.AreEqual("One seat · 10.00 EUR", offers[0].Label);
            Assert.AreEqual("Team · 50000 JPY", offers[1].Label);
        }

        [Test]
        public void LinkingWithoutALicenceKeepsTheAccountTokenAndNoKey()
        {
            HubCheckout.Linked linked = HubCheckout.Grant(Json.Parse("{\"account_token\":\"" + Token + "\",\"email\":\"jeanne@example.org\",\"registry\":\"" + Forge + "/upm\"}"), Origin());
            Assert.IsNull(linked.Key);
            Assert.IsTrue(linked.Account);
            Assert.IsFalse(File.Exists(UpmConfig.FilePath), "no key: .upmconfig.toml untouched");
            Assert.IsNotNull(HubPurchases.For(Origin()), "linked: one-click purchases from that forge");

            HubCheckout.Linked both = HubCheckout.Grant(Json.Parse("{\"key\":\"glk_abc0123456789\",\"account_token\":\"" + Token + "\",\"registry\":\"" + Forge + "/upm\"}"), Origin());
            Assert.AreEqual("glk_abc0123456789", both.Key);
            Assert.AreEqual("glk_abc0123456789", UpmConfig.Token(Forge + "/upm"));
            Assert.Throws<Exception>(() => HubCheckout.Grant(Json.Parse("{\"email\":\"jeanne@example.org\"}"), Origin()), "neither a key nor an account");
        }

        [Test]
        public void APriceThatChangedIsShownAndConfirmedAgainThenPaidOnce()
        {
            var forge = new FakeForge { Methods = { Card("pm_old", true), Card("pm_1") } };
            HubPurchases purchases = Purchases(forge);

            PurchaseQuote quote = Sync(purchases.Quote(7));
            Assert.AreEqual(1200, quote.Price);
            Assert.AreEqual("12.00 EUR", quote.Amount);
            Assert.AreEqual("Atelier Exemple", quote.Store);
            Assert.AreEqual("pm_1", quote.Method.Id, "the first method that has not expired");
            Assert.AreEqual(Token, forge.Requests[0].Bearer, "the account token as Bearer");

            // The price moves between the confirmation and the click: nothing charged, the new one shown.
            forge.Price = 1450;
            PurchaseConfirmation first = PurchaseConfirmation.Of(quote, quote.Method.Id);
            PurchaseOutcome changed = Sync(purchases.Buy(first));
            Assert.AreEqual(PurchaseOutcome.Kind.PriceChanged, changed.What);
            Assert.AreEqual("14.50 EUR", changed.Amount);
            Assert.IsEmpty(kept, "nothing paid, no key");

            // Confirmed again at the new price, with a new key: paid, the key kept for the registry.
            var again = new PurchaseConfirmation { Offer = 7, Price = changed.Price, Currency = changed.Currency, Method = "pm_1" };
            Assert.AreNotEqual(first.Key, again.Key);
            PurchaseOutcome paid = Sync(purchases.Buy(again));
            Assert.AreEqual(PurchaseOutcome.Kind.Paid, paid.What);
            Assert.AreEqual("com.example.tool", paid.Package);
            Assert.IsTrue(paid.KeyKept);
            Assert.IsFalse(paid.Replayed);
            CollectionAssert.AreEqual(new[] { (Forge + "/upm", "glk_newkey0123456789") }, kept);

            // The same confirmation sent again (a retry): the same purchase, nothing charged, no key again.
            PurchaseOutcome replay = Sync(purchases.Buy(again));
            Assert.IsTrue(replay.What == PurchaseOutcome.Kind.Paid && replay.Replayed && !replay.KeyKept);
            List<Seen> posts = forge.Requests.Where(r => r.Method == "POST" && r.Url.EndsWith("/purchases")).ToList();
            Assert.AreEqual(3, posts.Count);
            Assert.AreEqual(1200L, Json.Get(posts[0].Body, "price"));
            Assert.AreEqual(1450L, Json.Get(posts[1].Body, "price"));
            Assert.AreEqual(again.Key, posts[1].Key);
            Assert.AreEqual(posts[1].Key, posts[2].Key, "the retry carries the same key");
            Assert.AreEqual("pm_1", Json.Get(posts[1].Body, "method"));
            Assert.AreEqual(1, forge.ByKey.Count, "one purchase");
        }

        [Test]
        public void ABankThatAsksItsBuyerGivesAPageThenThePurchaseIsAskedAbout()
        {
            var forge = new FakeForge { Methods = { Card("pm_1") }, Mode = "3ds" };
            HubPurchases purchases = Purchases(forge);
            PurchaseQuote quote = Sync(purchases.Quote(7));
            PurchaseOutcome action = Sync(purchases.Buy(PurchaseConfirmation.Of(quote, null)));
            Assert.AreEqual(PurchaseOutcome.Kind.RequiresAction, action.What);
            Assert.AreEqual("https://bank.example.test/3ds/abc", action.Url);
            Assert.IsNull(Json.Get(forge.Requests.Last().Body, "method"), "no method chosen: the forge takes the first one");
            Assert.AreEqual(PurchaseOutcome.Kind.RequiresAction, Sync(purchases.Status(action.Id)).What);
            Assert.AreEqual(PurchaseOutcome.Kind.Pending, Sync(purchases.Status(action.Id)).What);
            PurchaseOutcome paid = Sync(purchases.Status(action.Id));
            Assert.IsTrue(paid.What == PurchaseOutcome.Kind.Paid && paid.KeyKept, "the first paid answer hands the key over");
            Assert.IsFalse(Sync(purchases.Status(action.Id)).KeyKept, "once");
            Assert.Throws<ArgumentException>(() => Sync(purchases.Status("not-an-id")), "never sent");
            var unknown = Assert.Throws<Exception>(() => Sync(purchases.Status(new string('f', 32))));
            StringAssert.Contains("no such purchase", unknown.Message);
        }

        [Test]
        public void RefusalsSayWhyAndChargeNothing()
        {
            var forge = new FakeForge();
            HubPurchases purchases = Purchases(forge);

            // No saved method: the quote says so, the purchase asks for one.
            PurchaseQuote quote = Sync(purchases.Quote(7));
            Assert.IsNull(quote.Method);
            Assert.AreEqual(PurchaseOutcome.Kind.MethodRequired, Sync(purchases.Buy(PurchaseConfirmation.Of(quote, null))).What);

            // Saving one: the provider's page; removing one; an unknown one.
            Assert.AreEqual("https://checkout.provider.example.test/setup/cs_1", Sync(purchases.AddMethod()));
            forge.Methods.AddRange(new object[] { Card("pm_1"), Card("pm_2") });
            Assert.AreEqual(2, Sync(purchases.Methods()).Count);
            purchases.RemoveMethod("pm_2").GetAwaiter().GetResult();
            CollectionAssert.AreEqual(new[] { "pm_1" }, Sync(purchases.Methods()).Select(m => m.Id));
            StringAssert.Contains("not one of yours", Assert.Throws<Exception>(() => purchases.RemoveMethod("pm_2").GetAwaiter().GetResult()).Message);
            Assert.Throws<ArgumentException>(() => purchases.RemoveMethod("../x").GetAwaiter().GetResult(), "never sent");

            // The card refused (402).
            forge.Mode = "refused";
            PurchaseOutcome refused = Sync(purchases.Buy(PurchaseConfirmation.Of(quote, "pm_1")));
            Assert.AreEqual(PurchaseOutcome.Kind.Refused, refused.What);
            Assert.AreEqual("Your card was declined.", refused.Reason);

            // Another purchase running; payments down; an unknown offer; a bad confirmation key.
            forge.Mode = "busy";
            Assert.AreEqual(PurchaseOutcome.Kind.InProgress, Sync(purchases.Buy(PurchaseConfirmation.Of(quote, "pm_1"))).What);
            forge.Mode = "unavailable";
            StringAssert.Contains("takes no payment right now", Assert.Throws<Exception>(() => Sync(purchases.Buy(PurchaseConfirmation.Of(quote, "pm_1")))).Message);
            StringAssert.Contains("no longer for sale", Assert.Throws<Exception>(() => Sync(purchases.Quote(99))).Message);
            int before = forge.Requests.Count;
            Assert.Throws<ArgumentException>(() => Sync(purchases.Buy(new PurchaseConfirmation { Offer = 7, Price = 1200, Currency = "EUR", Key = "short" })));
            Assert.AreEqual(before, forge.Requests.Count, "never sent");
            Assert.IsEmpty(kept);
        }

        [Test]
        public void ATokenTheForgeNoLongerKnowsIsForgotten()
        {
            AccountTokens.Keep(Forge, "gla_unknownunknownunknownunknown00");
            HubPurchases stale = HubPurchases.For(Origin());
            Assert.IsNotNull(stale);
            var forge = new FakeForge();
            var purchases = new HubPurchases(Origin(), "gla_unknownunknownunknownunknown00", forge);
            Assert.Throws<AccountRefusedException>(() => Sync(purchases.Quote(7)));
            Assert.IsNull(AccountTokens.Get(Forge), "forgotten: Buy links the editor again");
        }
    }
}
