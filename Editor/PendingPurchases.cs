using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Glitchr.Store
{
    /// <summary>A purchase whose outcome this editor has not seen to the end: never a licence key nor an account token.</summary>
    public sealed class PendingPurchase
    {
        /// <summary>The forge, as <see cref="HubPurchases.Authority"/> writes it.</summary>
        public string Forge;
        public string Hub;
        /// <summary>The item's UPM name.</summary>
        public string Item;
        /// <summary>The confirmation's idempotency key: what finds the purchase again.</summary>
        public string Key;
        public long Offer;
        public long Price;
        public string Currency;
        public string Id;
        /// <summary>UNIX time it was confirmed.</summary>
        public long Since;

        public Dictionary<string, object> ToJson()
        {
            var json = new Dictionary<string, object> { ["forge"] = Forge, ["hub"] = Hub, ["item"] = Item, ["key"] = Key, ["offer"] = Offer, ["price"] = Price, ["currency"] = Currency, ["since"] = Since };
            if (Id != null) json["id"] = Id;
            return json;
        }

        public static PendingPurchase Of(object json)
        {
            string forge = Json.String(Json.Get(json, "forge")), key = Json.String(Json.Get(json, "key"));
            if (forge == null || key == null) return null;
            return new PendingPurchase
            {
                Forge = forge, Key = key,
                Hub = Json.String(Json.Get(json, "hub")),
                Item = Json.String(Json.Get(json, "item")),
                Offer = Json.Get(json, "offer") is double o ? (long)o : 0,
                Price = Json.Get(json, "price") is double p ? (long)p : 0,
                Currency = Json.String(Json.Get(json, "currency")),
                Id = Json.String(Json.Get(json, "id")),
                Since = Json.Get(json, "since") is double s ? (long)s : 0,
            };
        }
    }

    /// <summary>
    /// Purchases kept on disk so that a lost answer survives a restart of Unity: Forge Store/
    /// purchases.json in the user's local application data, beside keys.json and licences.json
    /// (the same format as Forge Hub's pending-purchases.json, in a file of Forge Store's own).
    /// Kept just before a purchase is sent; forgotten once its key is written, once the forge says
    /// nothing was charged, or once it answers the purchase is not this account's (or its licence
    /// can make no key any more).
    /// </summary>
    public sealed class PendingPurchases
    {
        private readonly SharedFile file;

        public PendingPurchases(string path)
        {
            file = new SharedFile(path ?? throw new ArgumentNullException(nameof(path)));
        }

        public static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge Store", "purchases.json");

        private static PendingPurchases user;

        /// <summary>The user's file (tests make their own).</summary>
        public static PendingPurchases User => user ??= new PendingPurchases(DefaultPath);

        public string Path => file.Path;

        public List<PendingPurchase> All() => (Json.Array(Json.Get(file.Read(true), "purchases")) ?? new List<object>()).Select(PendingPurchase.Of).Where(p => p != null).ToList();

        public void Keep(PendingPurchase purchase)
        {
            List<PendingPurchase> all = All();
            all.RemoveAll(p => p.Forge == purchase.Forge && p.Key == purchase.Key);
            all.Add(purchase);
            Save(all);
        }

        public void Found(string forge, string key, string id)
        {
            List<PendingPurchase> all = All();
            foreach (PendingPurchase p in all.Where(p => p.Forge == forge && p.Key == key)) p.Id = id;
            Save(all);
        }

        public void Forget(string forge, string key)
        {
            List<PendingPurchase> all = All();
            if (all.RemoveAll(p => p.Forge == forge && p.Key == key) > 0) Save(all);
        }

        public void ForgetId(string id)
        {
            List<PendingPurchase> all = All();
            if (all.RemoveAll(p => p.Id == id) > 0) Save(all);
        }

        private void Save(List<PendingPurchase> all)
        {
            file.Write(new Dictionary<string, object> { ["purchases"] = all.Select(p => (object)p.ToJson()).ToList() });
        }

        /// <summary>What a purchase's answer (or its failure) means for its entry.</summary>
        public void Settle(string forge, string key, PurchaseOutcome outcome, Exception error)
        {
            if (error != null)
            {
                if (error is PurchaseGoneException) Forget(forge, key);
                return; // an answer that did not come: kept for later
            }
            switch (outcome.What)
            {
                case PurchaseOutcome.Kind.Paid when outcome.KeyLost:
                case PurchaseOutcome.Kind.RequiresAction:
                case PurchaseOutcome.Kind.Pending:
                    Found(forge, key, outcome.Id);
                    break;
                case PurchaseOutcome.Kind.InProgress:
                    break;
                default:
                    // The key written (or none to recover), nothing charged.
                    Forget(forge, key);
                    break;
            }
        }

        /// <summary>The same for a purchase asked by its id (its status, its key again).</summary>
        public void SettleById(string id, PurchaseOutcome outcome, Exception error)
        {
            if (error is PurchaseGoneException || error == null && (outcome.What == PurchaseOutcome.Kind.Paid && !outcome.KeyLost || outcome.What == PurchaseOutcome.Kind.Refused)) ForgetId(id);
        }
    }

    /// <summary>The forge knows no such purchase of this account, or its licence can make no key any more: nothing to find again.</summary>
    public sealed class PurchaseGoneException : Exception
    {
        public PurchaseGoneException(string message) : base(message) { }
    }
}
