using System;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Glitchr.Store
{
    /// <summary>
    /// Buying from a hub that sells (its manifest's "checkout"): a device code asked of the forge,
    /// its page opened in the browser - the user signs in, buys, links this editor -, the forge
    /// asked every few seconds until it hands over the user's licence key, which is kept as the
    /// registry's token in the user's .upmconfig.toml. The Package Manager reads that file when
    /// Unity starts: the window offers to restart the project.
    /// A forge that takes one-click purchases also gives, once linked, the user's account token -
    /// even to a user without a licence (then there is no key): kept by the system's secret store
    /// for that forge (<see cref="AccountTokens"/>), it is what <see cref="HubPurchases"/> buys with.
    /// </summary>
    public static class HubCheckout
    {
        public sealed class Pending
        {
            public string UserCode;
            public string Link;
            public HubItem Item;
        }

        /// <summary>What linking the editor gave.</summary>
        public sealed class Linked
        {
            /// <summary>The user's licence key, kept in .upmconfig.toml; null when they hold no licence there.</summary>
            public string Key;
            /// <summary>An account token was given and kept: one-click purchases from this forge.</summary>
            public bool Account;
        }

        /// <summary>The user's code and page, opened in the browser (or by <paramref name="open"/>); then waits until the editor is linked (null: given up or expired).</summary>
        public static async Task<Linked> Buy(HubItem item, Action<Pending> started, Func<bool> cancelled, Action<string> open = null)
        {
            HubOrigin origin = item.Origin;
            string certificate = origin.Source.CertificateSha256;
            HubHttp.Answer answer = await HubHttp.PostJson(origin.DeviceUrl, new System.Collections.Generic.Dictionary<string, object> { ["item"] = item.Name }, certificate);
            if (!answer.Ok) throw new Exception("The hub refused the purchase: " + (answer.Error ?? ("HTTP " + answer.Status)));
            object code = answer.Json;
            string device = Json.String(Json.Get(code, "device_code"));
            string link = Json.String(Json.Get(code, "verification_uri_complete")) ?? Json.String(Json.Get(code, "verification_uri"));
            double interval = Json.Get(code, "interval") is double i ? i : 5;
            double expires = Json.Get(code, "expires_in") is double e ? e : 600;
            started?.Invoke(new Pending { UserCode = Json.String(Json.Get(code, "user_code")), Link = link, Item = item });
            (open ?? Application.OpenURL)(link);

            DateTime end = DateTime.UtcNow.AddSeconds(expires);
            while (DateTime.UtcNow < end && !(cancelled?.Invoke() ?? false))
            {
                await Delay(interval);
                HubHttp.Answer poll = await HubHttp.PostJson(origin.TokenUrl, new System.Collections.Generic.Dictionary<string, object> { ["device_code"] = device }, certificate);
                if (poll.Ok)
                {
                    return Grant(poll.Json, origin);
                }
                string error = Json.String(Json.Get(poll.Json, "error"));
                if (error == "slow_down") interval += 2;
                else if (error != "authorization_pending") return null;
            }

            return null;
        }

        /// <summary>
        /// The forge's answer once the editor is linked: the licence key (when the user holds a
        /// licence) kept for its registry in .upmconfig.toml, the account token (when the forge
        /// gives one) kept for the forge by the system's secret store.
        /// </summary>
        public static Linked Grant(object granted, HubOrigin origin)
        {
            string key = Json.String(Json.Get(granted, "key"));
            string account = Json.String(Json.Get(granted, "account_token"));
            if (string.IsNullOrEmpty(key) && string.IsNullOrEmpty(account)) throw new Exception("The hub linked this editor but gave neither a key nor an account.");
            var linked = new Linked();
            if (!string.IsNullOrEmpty(key))
            {
                string registry = Json.String(Json.Get(granted, "registry")) ?? origin.Registry;
                UpmConfig.SetToken(registry, key);
                linked.Key = key;
            }
            string home = HubPurchases.Authority(origin.TokenUrl);
            if (!string.IsNullOrEmpty(account) && home != null)
            {
                AccountTokens.Keep(home, account);
                linked.Account = true;
            }

            return linked;
        }

        /// <summary>Waits on the editor's loop (a Task.Delay continuation may not come back to it).</summary>
        internal static Task Delay(double seconds)
        {
            var completion = new TaskCompletionSource<bool>();
            double until = EditorApplication.timeSinceStartup + seconds;
            void Check()
            {
                if (EditorApplication.timeSinceStartup < until) return;
                EditorApplication.update -= Check;
                completion.TrySetResult(true);
            }
            EditorApplication.update += Check;

            return completion.Task;
        }

        /// <summary>Unity again, on this project: the Package Manager then reads the new key.</summary>
        public static void RestartProject()
        {
            EditorApplication.OpenProject(System.IO.Directory.GetCurrentDirectory());
        }
    }
}
