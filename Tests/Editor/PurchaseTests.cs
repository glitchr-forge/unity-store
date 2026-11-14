using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
                if (bearer != Token) return Answer(401, new Dictionary<string, object> { ["error"] = "unauthorized" });
                string path = url.Substring(Hub.Length);
                if (method == "GET" && path.StartsWith("/quote?"))
                {
                    if (path != "/quote?offer=7") return Answer(404, new Dictionary<string, object> { ["error"] = "unknown_offer" });
                    object usable = Methods.FirstOrDefault(m => !(Json.Get(m, "expired") is bool e && e));

                    return Answer(200, new Dictionary<string, object> { ["offer"] = 7, ["title"] = "Example Tool, one seat", ["software"] = "example-tool", ["package"] = "com.example.tool", ["store"] = "Atelier Exemple", ["price"] = Price, ["currency"] = "EUR", ["method"] = usable });
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
