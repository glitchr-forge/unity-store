using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Glitchr.Store
{
    /// <summary>A request of the one-click purchase: UnityWebRequest in the editor (<see cref="HubHttp.Requests"/>), canned answers in the tests.</summary>
    public interface IHubRequests
    {
        Task<HubHttp.Answer> Send(string method, string url, object body, string bearer, IDictionary<string, string> headers, string certificate);
    }

    /// <summary>A saved payment method, as the provider has it (never a card number).</summary>
    public sealed class PaymentMethod
    {
        public string Id;
        public string Type;
        public string Label;
        public string Brand;
        public string Last4;
        public string Expires;
        public bool Expired;

        public static PaymentMethod Of(object json)
        {
            string id = Json.String(Json.Get(json, "id"));
            if (string.IsNullOrEmpty(id)) return null;

            return new PaymentMethod
            {
                Id = id,
                Type = Json.String(Json.Get(json, "type")),
                Label = Json.String(Json.Get(json, "label")),
                Brand = Json.String(Json.Get(json, "brand")),
                Last4 = Json.String(Json.Get(json, "last4")),
                Expires = Json.String(Json.Get(json, "expires")),
                Expired = Json.Get(json, "expired") is bool e && e,
            };
        }

        public string Shown => (Label ?? Id) + (Expires != null ? "  ·  " + Expires : string.Empty) + (Expired ? "  ·  expired" : string.Empty);
    }

    /// <summary>What buying an offer charges this user now: what the confirmation shows.</summary>
    public sealed class PurchaseQuote
    {
        public long Offer;
        public string Title;
        public string Software;
        public string Package;
        /// <summary>The hub's name (its manifest's name).</summary>
        public string Store;
        /// <summary>Minor units, VAT and promotions included.</summary>
        public long Price;
        public string Currency;
        /// <summary>The saved method it would be charged to; null: none saved.</summary>
        public PaymentMethod Method;
        public string Amount => HubAmount.Format(Price, Currency);
    }

    /// <summary>What the user confirmed, sent as it is with the key of that confirmation.</summary>
    public sealed class PurchaseConfirmation
    {
        public long Offer;
        public long Price;
        public string Currency;
        /// <summary>A saved method's id; null: the first one that has not expired.</summary>
        public string Method;
        /// <summary>The confirmation's idempotency key, the same on each of its retries.</summary>
        public string Key = HubPurchases.NewKey();

        public static PurchaseConfirmation Of(PurchaseQuote quote, string method) => new PurchaseConfirmation { Offer = quote.Offer, Price = quote.Price, Currency = quote.Currency, Method = method };
    }

    /// <summary>Where a purchase stands.</summary>
    public sealed class PurchaseOutcome
    {
        public enum Kind
        {
            /// <summary>Charged, the licence issued; <see cref="KeyKept"/> when its key came with this answer.</summary>
            Paid,
            /// <summary>The bank asks its buyer (3-D Secure): open <see cref="Url"/>, then ask again.</summary>
            RequiresAction,
            /// <summary>The provider has not said yet: ask again.</summary>
            Pending,
            /// <summary>The card was refused; nothing charged.</summary>
            Refused,
            /// <summary>The amount is no longer the confirmed one: show <see cref="Price"/>, confirm again with a new key.</summary>
            PriceChanged,
            /// <summary>No saved method: save one first.</summary>
            MethodRequired,
            /// <summary>Another purchase of this user is still running: retry the same confirmation.</summary>
            InProgress,
        }

        public Kind What;
        public string Id;
        public string Status;
        public string Package;
        public string Url;
        public string Reason;
        public long Offer;
        public long Price;
        public string Currency;
        /// <summary>The licence key came with this answer and is kept for <see cref="Registry"/> in .upmconfig.toml.</summary>
        public bool KeyKept;
        public string Registry;
        /// <summary>The same confirmation was sent before: nothing charged again.</summary>
        public bool Replayed;
        public string Amount => HubAmount.Format(Price, Currency);
    }

    /// <summary>The forge refused the account token (401): it is forgotten, and the editor linked again.</summary>
    public sealed class AccountRefusedException : Exception
    {
        public AccountRefusedException() : base("The forge no longer knows this editor: link it again (Buy opens the forge's page).") { }
    }

    /// <summary>
    /// Buying in one click from a forge that takes it (its manifest's checkout.quote, purchases,
    /// methods; the forge's docs/hub.md, "Buying in one click"): the account token the device flow
    /// gave (<see cref="HubCheckout"/>) as Bearer; the user's saved payment methods (listed, added
    /// on the provider's own page, removed); the price the offer comes to for this user - what the
    /// confirmation shows, VAT included, with the store's name -; then the purchase at that price
    /// with an idempotency key, one per confirmation and sent again on its retries, so nothing is
    /// charged twice. A 409 price_changed gives the price now: shown, confirmed again with a new
    /// key. A bank that asks its buyer gives a page to open; the purchase is then asked about until
    /// it is paid or refused. Once paid, the licence key (handed over once) is kept as the
    /// registry's token in the user's .upmconfig.toml, as the device flow keeps it.
    /// </summary>
    public sealed class HubPurchases
    {
        private static readonly Regex KeyShape = new Regex("^[A-Za-z0-9_.:-]{8,64}$");
        private static readonly Regex IdShape = new Regex("^[A-Za-z0-9_-]{1,255}$");
        private static readonly Regex PurchaseShape = new Regex("^[0-9a-f]{32}$");

        private readonly HubOrigin origin;
        private readonly string token;
        private readonly IHubRequests requests;
        private readonly Action<string, string> keepKey;

        /// <param name="keepKey">Keeps a licence key for a registry (UpmConfig.SetToken by default).</param>
        public HubPurchases(HubOrigin origin, string token, IHubRequests requests = null, Action<string, string> keepKey = null)
        {
            if (origin == null || !origin.OneClick) throw new InvalidOperationException((origin?.HubName ?? "This hub") + " takes no one-click purchase.");
            this.origin = origin;
            this.token = token;
            this.requests = requests ?? HubHttp.Requests;
            this.keepKey = keepKey ?? UpmConfig.SetToken;
        }

        /// <summary>The account token kept for the item's forge; null when the editor is not linked to it yet.</summary>
        public static HubPurchases For(HubOrigin origin)
        {
            if (origin == null || !origin.OneClick) return null;
            string token = AccountTokens.Get(Authority(origin.TokenUrl));

            return token == null ? null : new HubPurchases(origin, token);
        }

        /// <summary>scheme://host[:port] of an http(s) address, lower case: what an account token is kept for.</summary>
        public static string Authority(string address)
        {
            if (!Uri.TryCreate(address ?? string.Empty, UriKind.Absolute, out Uri uri)) return null;
            if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp || !string.IsNullOrEmpty(uri.UserInfo)) return null;

            return (uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? string.Empty : ":" + uri.Port)).ToLowerInvariant();
        }

        /// <summary>A page the forge gives to open (the provider's, the bank's): https, or http on this machine only; never with a user name.</summary>
        public static string Openable(string address)
        {
            if (!Uri.TryCreate(address ?? string.Empty, UriKind.Absolute, out Uri uri)) throw new Exception("The forge gave no page to open.");
            bool local = uri.IsLoopback;
            if (!(uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp && local) || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new Exception("The forge gave a page that is not opened (" + uri.Scheme + ").");
            }

            return uri.AbsoluteUri;
        }

        /// <summary>A new confirmation key: 32 hex characters.</summary>
        public static string NewKey() => Guid.NewGuid().ToString("N");

        private Task<HubHttp.Answer> Send(string method, string url, object body = null, IDictionary<string, string> headers = null)
        {
            return requests.Send(method, url, body, token, headers, origin.Source?.CertificateSha256);
        }

        /// <summary>The forge's refusal in a sentence; a refused account token forgotten.</summary>
        private Exception Refusal(HubHttp.Answer answer)
        {
            if (answer.Status == 401)
            {
                AccountTokens.Forget(Authority(origin.TokenUrl), token);
                return new AccountRefusedException();
            }
            if (answer.Status == 0) return new Exception("The forge did not answer: " + (answer.Error ?? "no connection") + ".");
            string error = Json.String(Json.Get(Parse(answer), "error")) ?? "HTTP " + answer.Status;
            switch (error)
            {
                case "unknown_offer": return new Exception("This offer is no longer for sale.");
                case "unknown_payment_method": return new Exception("That payment method is not one of yours on this forge.");
                case "idempotency_key_reused": return new Exception("This confirmation was already sent for another offer or price: confirm again.");
                case "idempotency_key_required": return new Exception("The purchase was sent without its confirmation key.");
                case "invalid_request": return new Exception("The forge could not read the purchase.");
                case "payments_unavailable": return new Exception("The forge takes no payment right now: try again later, or buy on its page.");
                case "unknown_purchase": return new Exception("The forge knows no such purchase.");
                default: return new Exception("The forge refused: " + error + ".");
            }
        }

        private static object Parse(HubHttp.Answer answer)
        {
            try { return answer.Json; }
            catch (FormatException) { return null; }
        }

        /// <summary>The user's saved payment methods on that forge, as its provider has them now.</summary>
        public async Task<List<PaymentMethod>> Methods()
        {
            HubHttp.Answer answer = await Send("GET", origin.MethodsUrl);
            if (!answer.Ok) throw Refusal(answer);

            return (Json.Array(Json.Get(Parse(answer), "methods")) ?? new List<object>()).Select(PaymentMethod.Of).Where(m => m != null).ToList();
        }

        /// <summary>A method to save: the provider's page to open in the browser; then <see cref="Methods"/> again.</summary>
        public async Task<string> AddMethod()
        {
            HubHttp.Answer answer = await Send("POST", origin.MethodsUrl, new Dictionary<string, object>());
            if (!answer.Ok) throw Refusal(answer);

            return Openable(Json.String(Json.Get(Parse(answer), "url")));
        }

        /// <summary>Removes a saved method (changing one is adding the new one, then removing the old one).</summary>
        public async Task RemoveMethod(string id)
        {
            if (id == null || !IdShape.IsMatch(id)) throw new ArgumentException("Not a payment method's id.");
            HubHttp.Answer answer = await Send("DELETE", origin.MethodsUrl.TrimEnd('/') + "/" + id);
            if (!answer.Ok) throw Refusal(answer);
        }

        /// <summary>What the offer charges this user now, for the confirmation; nothing is charged. Refused while the store's key waits.</summary>
        public async Task<PurchaseQuote> Quote(long offer)
        {
            Gate();
            HubHttp.Answer answer = await Send("GET", origin.QuoteUrl + (origin.QuoteUrl.Contains("?") ? "&" : "?") + "offer=" + offer);
            if (!answer.Ok) throw Refusal(answer);
            object j = Parse(answer);
            if (!(Json.Get(j, "price") is double price)) throw new Exception("The forge gave no price.");
            string currency = Json.String(Json.Get(j, "currency"));
            if (currency == null || currency.Length != 3) throw new Exception("The forge gave no currency.");

            return new PurchaseQuote
            {
                Offer = Json.Get(j, "offer") is double o ? (long)o : offer,
                Title = Json.String(Json.Get(j, "title")),
                Software = Json.String(Json.Get(j, "software")),
                Package = Json.String(Json.Get(j, "package")),
                Store = Json.String(Json.Get(j, "store")),
                Price = (long)price,
                Currency = currency.ToUpperInvariant(),
                Method = Json.Get(j, "method") == null ? null : PaymentMethod.Of(Json.Get(j, "method")),
            };
        }

        /// <summary>The purchase, as its user confirmed it. Refused while the store's key waits.</summary>
        public async Task<PurchaseOutcome> Buy(PurchaseConfirmation confirmed)
        {
            Gate();
            if (confirmed?.Key == null || !KeyShape.IsMatch(confirmed.Key)) throw new ArgumentException("Not a confirmation key.");
            var body = new Dictionary<string, object> { ["offer"] = confirmed.Offer, ["price"] = confirmed.Price, ["currency"] = confirmed.Currency };
            if (!string.IsNullOrEmpty(confirmed.Method)) body["method"] = confirmed.Method;
            HubHttp.Answer answer = await Send("POST", origin.PurchasesUrl, body, new Dictionary<string, string> { ["Idempotency-Key"] = confirmed.Key });

            // 200 rather than 201: the same confirmation sent before; nothing charged again.
            return Outcome(answer, answer.Status == 200);
        }

        /// <summary>Where a purchase stands now (asked every few seconds while it waits); its first answer once paid hands the key over.</summary>
        public async Task<PurchaseOutcome> Status(string id)
        {
            if (id == null || !PurchaseShape.IsMatch(id)) throw new ArgumentException("Not a purchase's id.");
            HubHttp.Answer answer = await Send("GET", origin.PurchasesUrl.TrimEnd('/') + "/" + id);

            return Outcome(answer, false);
        }

        private void Gate()
        {
            if (!string.IsNullOrEmpty(origin.Store) && StoreTrust.Pins.Get(origin.Store)?.Pending != null)
            {
                throw new Exception((origin.HubName ?? "The store") + " announces a new licensing key: accept it or keep the pinned one first. Nothing is bought from it meanwhile.");
            }
        }

        private PurchaseOutcome Outcome(HubHttp.Answer answer, bool replayed)
        {
            object j = Parse(answer);
            string error = Json.String(Json.Get(j, "error"));
            if (answer.Status == 409 && error == "price_changed")
            {
                if (!(Json.Get(j, "price") is double now)) throw new Exception("The price changed, and the forge gave no new one.");
                return new PurchaseOutcome
                {
                    What = PurchaseOutcome.Kind.PriceChanged,
                    Offer = Json.Get(j, "offer") is double o ? (long)o : 0,
                    Price = (long)now,
                    Currency = (Json.String(Json.Get(j, "currency")) ?? string.Empty).ToUpperInvariant(),
                };
            }
            if (answer.Status == 409 && error == "payment_method_required") return new PurchaseOutcome { What = PurchaseOutcome.Kind.MethodRequired };
            if (answer.Status == 409 && error == "purchase_in_progress") return new PurchaseOutcome { What = PurchaseOutcome.Kind.InProgress };
            if (!answer.Ok && answer.Status != 402) throw Refusal(answer);

            var outcome = new PurchaseOutcome
            {
                Id = Json.String(Json.Get(j, "id")),
                Status = Json.String(Json.Get(j, "status")) ?? string.Empty,
                Package = Json.String(Json.Get(j, "package")),
                Reason = Json.String(Json.Get(j, "reason")),
                Offer = Json.Get(j, "offer") is double offer ? (long)offer : 0,
                Price = Json.Get(j, "price") is double price ? (long)price : 0,
                Currency = Json.String(Json.Get(j, "currency")),
                Replayed = replayed,
            };
            if (string.IsNullOrEmpty(outcome.Id)) throw new Exception("The forge's answer does not read as a purchase.");
            switch (outcome.Status)
            {
                case "paid":
                    outcome.What = PurchaseOutcome.Kind.Paid;
                    // The key comes once: kept for the registry it opens, as the device flow keeps it.
                    string key = Json.String(Json.Get(j, "key"));
                    string registry = Json.String(Json.Get(j, "registry")) ?? origin.Registry;
                    if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(registry))
                    {
                        keepKey(registry, key);
                        outcome.KeyKept = true;
                        outcome.Registry = registry;
                    }
                    break;
                case "requires_action":
                    outcome.What = PurchaseOutcome.Kind.RequiresAction;
                    outcome.Url = Openable(Json.String(Json.Get(j, "url")));
                    break;
                case "refused":
                    outcome.What = PurchaseOutcome.Kind.Refused;
                    break;
                case "pending":
                case "":
                    outcome.What = PurchaseOutcome.Kind.Pending;
                    break;
                default:
                    throw new Exception("The forge says the purchase is " + outcome.Status + ".");
            }

            return outcome;
        }
    }

    /// <summary>
    /// The account token each forge gave this editor (the device flow's account_token), kept by the
    /// system's secret store per forge (<see cref="HubPurchases.Authority"/>) - never in a project,
    /// never in EditorPrefs in clear, never shown, sent to that forge's addresses only.
    /// </summary>
    public static class AccountTokens
    {
        private const string Service = "Forge Store account token";
        private static readonly Regex Shape = new Regex("^[A-Za-z0-9_.\\-]{20,255}$");
        /// <summary>Tokens kept in memory only: where the system has no store, and in the tests.</summary>
        private static readonly Dictionary<string, string> Session = new Dictionary<string, string>();

        /// <summary>Tests keep their tokens in memory only, never in the system's store.</summary>
        public static bool MemoryOnly;

        private static SecretStore Slot(string authority) => new SecretStore(Service, "forge-store-account", authority, "Glitchr.Store.Account." + authority);

        public static bool LooksLikeToken(string token) => !string.IsNullOrEmpty(token) && Shape.IsMatch(token);

        public static string Get(string authority)
        {
            if (string.IsNullOrEmpty(authority)) return null;
            if (Session.TryGetValue(authority, out string kept)) return kept;
            if (MemoryOnly) return null;
            string read = Slot(authority).Read();

            return LooksLikeToken(read) ? read : null;
        }

        /// <summary>Keeps that forge's token. Returns where.</summary>
        public static string Keep(string authority, string token)
        {
            if (string.IsNullOrEmpty(authority)) throw new ArgumentException("No forge address.");
            if (!LooksLikeToken(token)) throw new ArgumentException("The forge gave no account token.");
            Session.Remove(authority);
            if (MemoryOnly)
            {
                Session[authority] = token;
                return "memory";
            }
            string elsewhere = Slot(authority).Write(token);
            if (elsewhere != null || Slot(authority).Read() != token)
            {
                Session[authority] = token;
                return elsewhere ?? "this session only";
            }

            return SecretStore.Name;
        }

        /// <summary>Forgets that forge's token (only when it is still <paramref name="token"/>, when given).</summary>
        public static void Forget(string authority, string token = null)
        {
            if (string.IsNullOrEmpty(authority)) return;
            if (token != null && Get(authority) != token) return;
            Session.Remove(authority);
            if (!MemoryOnly) Slot(authority).Delete();
        }
    }
}
