using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace Glitchr.Store
{
    /// <summary>
    /// The one-click purchase's confirmation (<see cref="HubPurchases"/>). One click on Buy opens
    /// it, one click on Pay confirms: the price the forge charges this user (VAT included), its
    /// currency, the store's name and the saved payment method. Each confirmation has its own key,
    /// sent again on its retries, so nothing is charged twice. A price the forge no longer charges
    /// (409 price_changed) is shown and asked again; a bank that asks its buyer (3-D Secure) opens
    /// in the browser, and the purchase is asked about until it is settled. Payment methods are
    /// saved on the provider's own page (the browser), listed and removed here.
    /// </summary>
    public sealed class PurchaseWindow : EditorWindow
    {
        private HubItem item;
        private HubPurchases purchases;
        private Action<HubItem, PurchaseOutcome> paid;

        private int offerIndex;
        private PurchaseQuote quote;
        private List<PaymentMethod> methods = new List<PaymentMethod>();
        private string method;
        private PurchaseConfirmation confirmation;
        private string note;
        private MessageType noteType = MessageType.Info;
        private string busy;
        private string bankPage;
        private bool showMethods;
        private bool closed;
        private bool waiting;
        private int waitRound;

        /// <summary>The confirmation for that item; <paramref name="paid"/> is told once it is paid.</summary>
        public static void Open(HubItem item, HubPurchases purchases, Action<HubItem, PurchaseOutcome> paid)
        {
            var window = CreateInstance<PurchaseWindow>();
            window.item = item;
            window.purchases = purchases;
            window.paid = paid;
            window.titleContent = new GUIContent("Buy " + item.Label);
            window.minSize = new Vector2(440, 320);
            window.ShowUtility();
            window.Load(null);
        }

        private void OnDestroy()
        {
            closed = true;
            waitRound++;
        }

        private List<PaymentMethod> Usable => methods.Where(m => !m.Expired).ToList();

        private async void Load(string message, MessageType type = MessageType.Info)
        {
            confirmation = null;
            busy = "Asking " + (item.Origin?.HubName ?? "the forge") + " for your price…";
            Repaint();
            try
            {
                long offer = item.Offers[Mathf.Clamp(offerIndex, 0, item.Offers.Count - 1)].Id;
                quote = await purchases.Quote(offer);
                methods = await purchases.Methods();
                if (!Usable.Any(m => m.Id == method)) method = quote.Method?.Id ?? Usable.FirstOrDefault()?.Id;
                note = message;
                noteType = type;
            }
            catch (Exception e)
            {
                quote = null;
                note = e.Message;
                noteType = MessageType.Error;
            }
            busy = null;
            if (!closed) Repaint();
        }

        private void OnGUI()
        {
            if (item == null || purchases == null)
            {
                // A domain reload forgets the purchase in hand: nothing was confirmed meanwhile.
                Close();
                return;
            }
            GUILayout.Label("Buy " + item.Label, EditorStyles.boldLabel);
            if (busy != null)
            {
                EditorGUILayout.HelpBox(busy, MessageType.Info);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (bankPage != null && GUILayout.Button("Open the bank's page again")) Application.OpenURL(bankPage);
                    if (waiting && GUILayout.Button("Close")) Close();
                }
                return;
            }
            if (!string.IsNullOrEmpty(note)) EditorGUILayout.HelpBox(note, noteType);
            if (quote == null)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Try again")) Load(null);
                    if (GUILayout.Button("Close")) Close();
                }
                return;
            }

            if (item.Offers.Count > 1)
            {
                int chosen = EditorGUILayout.Popup("Offer", offerIndex, item.Offers.Select(o => o.Title ?? "Offer " + o.Id).ToArray());
                if (chosen != offerIndex)
                {
                    offerIndex = chosen;
                    Load(null);
                    return;
                }
            }
            EditorGUILayout.LabelField("Store", quote.Store ?? item.Origin?.HubName ?? string.Empty);
            EditorGUILayout.LabelField("Item", quote.Title ?? item.Label);
            EditorGUILayout.LabelField("Price", quote.Amount, EditorStyles.boldLabel);
            EditorGUILayout.LabelField(" ", "what you are charged, taxes included", EditorStyles.miniLabel);
            List<PaymentMethod> usable = Usable;
            if (usable.Count > 1)
            {
                int current = Mathf.Max(0, usable.FindIndex(m => m.Id == method));
                int next = EditorGUILayout.Popup("Paid with", current, usable.Select(m => m.Label ?? m.Id).ToArray());
                if (usable[next].Id != method)
                {
                    method = usable[next].Id;
                    confirmation = null;
                }
            }
            else
            {
                EditorGUILayout.LabelField("Paid with", usable.Count == 1 ? usable[0].Label ?? usable[0].Id : "no saved payment method");
            }

            showMethods = EditorGUILayout.Foldout(showMethods || usable.Count == 0, "Payment methods", true);
            if (showMethods || usable.Count == 0) DrawMethods();

            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Cancel", GUILayout.Width(90))) Close();
                using (new EditorGUI.DisabledScope(method == null))
                {
                    if (GUILayout.Button("Pay " + quote.Amount, GUILayout.Width(170))) Send(0);
                }
            }
        }

        private void DrawMethods()
        {
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Saved on the payment provider's own page; the forge keeps no card number.", EditorStyles.wordWrappedMiniLabel);
                foreach (PaymentMethod m in methods.ToList())
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(m.Shown);
                        if (GUILayout.Button("Remove", GUILayout.Width(70))) Remove(m);
                    }
                }
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Add a payment method…")) AddMethod();
                    if (GUILayout.Button("Read again", GUILayout.Width(90))) Load(null);
                }
            }
        }

        private async void AddMethod()
        {
            try
            {
                Application.OpenURL(await purchases.AddMethod());
                note = "Save the payment method in your browser, then come back here: Read again.";
                noteType = MessageType.Info;
            }
            catch (Exception e)
            {
                note = e.Message;
                noteType = MessageType.Error;
            }
            if (!closed) Repaint();
        }

        private async void Remove(PaymentMethod m)
        {
            if (!EditorUtility.DisplayDialog("Remove a payment method", "Remove " + (m.Label ?? m.Id) + " from your account on " + (quote?.Store ?? item.Origin?.HubName) + "?", "Remove", "Keep")) return;
            try
            {
                await purchases.RemoveMethod(m.Id);
                Load(null);
            }
            catch (Exception e)
            {
                note = e.Message;
                noteType = MessageType.Error;
                Repaint();
            }
        }

        private async void Send(int retries)
        {
            confirmation ??= PurchaseConfirmation.Of(quote, method);
            busy = "Paying " + quote.Amount + "…";
            Repaint();
            PurchaseOutcome outcome;
            try
            {
                outcome = await purchases.Buy(confirmation);
            }
            catch (Exception e)
            {
                // The same confirmation (its key) goes again on Pay: nothing is charged twice.
                busy = null;
                note = e.Message + (e is AccountRefusedException ? string.Empty : " Pay again sends the same confirmation: nothing is charged twice.");
                noteType = MessageType.Error;
                if (e is AccountRefusedException) quote = null;
                if (!closed) Repaint();
                return;
            }
            if (outcome.What == PurchaseOutcome.Kind.InProgress && retries < 5)
            {
                await HubCheckout.Delay(3);
                if (!closed) Send(retries + 1);
                return;
            }
            Settle(outcome);
        }

        private void Settle(PurchaseOutcome outcome)
        {
            busy = null;
            bankPage = null;
            waiting = false;
            switch (outcome.What)
            {
                case PurchaseOutcome.Kind.Paid:
                    Close();
                    paid?.Invoke(item, outcome);
                    return;
                case PurchaseOutcome.Kind.RequiresAction:
                    bankPage = outcome.Url;
                    Application.OpenURL(outcome.Url);
                    Wait(outcome.Id, true);
                    return;
                case PurchaseOutcome.Kind.Pending:
                    Wait(outcome.Id, false);
                    return;
                case PurchaseOutcome.Kind.Refused:
                    confirmation = null;
                    note = "Your payment was refused, nothing was charged" + (string.IsNullOrEmpty(outcome.Reason) ? "." : ": " + outcome.Reason) + " Choose another payment method, or try again.";
                    noteType = MessageType.Warning;
                    break;
                case PurchaseOutcome.Kind.PriceChanged:
                    string before = quote.Amount;
                    if (outcome.Offer != 0) quote.Offer = outcome.Offer;
                    quote.Price = outcome.Price;
                    quote.Currency = outcome.Currency;
                    confirmation = null;
                    note = "The price is now " + quote.Amount + " (it was " + before + "). Nothing was charged: confirm again to pay " + quote.Amount + ".";
                    noteType = MessageType.Warning;
                    break;
                case PurchaseOutcome.Kind.MethodRequired:
                    showMethods = true;
                    Load("Save a payment method first (Payment methods, below).", MessageType.Warning);
                    return;
                case PurchaseOutcome.Kind.InProgress:
                    note = "Another purchase of yours is still running on the forge. Pay again in a moment: this confirmation is kept.";
                    noteType = MessageType.Warning;
                    break;
            }
            if (!closed) Repaint();
        }

        /// <summary>
        /// The bank's check (its page in the browser) or the provider's answer: the purchase asked
        /// about every few seconds, ten minutes at most. Closing stops asking, not the purchase.
        /// </summary>
        private async void Wait(string id, bool bank)
        {
            int round = ++waitRound;
            waiting = true;
            busy = (bank ? "Your bank asks you to confirm the payment, in your browser." : "The payment provider has not answered yet.")
                + "\nWaiting for " + (item.Origin?.HubName ?? "the forge") + "… Closing this window stops waiting, not the payment: once paid, the licence is in your account.";
            Repaint();
            DateTime end = DateTime.UtcNow.AddMinutes(10);
            while (!closed && round == waitRound && DateTime.UtcNow < end)
            {
                await HubCheckout.Delay(3);
                if (closed || round != waitRound) return;
                PurchaseOutcome outcome;
                try { outcome = await purchases.Status(id); }
                catch (Exception e)
                {
                    note = e.Message;
                    noteType = MessageType.Error;
                    continue;
                }
                if (outcome.What == PurchaseOutcome.Kind.RequiresAction || outcome.What == PurchaseOutcome.Kind.Pending) continue;
                Settle(outcome);
                return;
            }
            if (closed || round != waitRound) return;
            busy = null;
            bankPage = null;
            waiting = false;
            quote = null;
            note = "No answer from the forge after ten minutes. If you paid, the licence is in your account on " + (item.Origin?.HubName ?? "the forge") + ".";
            noteType = MessageType.Warning;
            Repaint();
        }
    }
}
