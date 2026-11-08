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
    /// </summary>
    public static class HubCheckout
    {
        public sealed class Pending
        {
            public string UserCode;
            public string Link;
            public HubItem Item;
        }

        /// <summary>The user's code and page, opened in the browser (or by <paramref name="open"/>); then waits for the key (null: given up or expired).</summary>
        public static async Task<string> Buy(HubItem item, Action<Pending> started, Func<bool> cancelled, Action<string> open = null)
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
                    object granted = poll.Json;
                    string key = Json.String(Json.Get(granted, "key"));
                    string registry = Json.String(Json.Get(granted, "registry")) ?? origin.Registry;
                    UpmConfig.SetToken(registry, key);

                    return key;
                }
                string error = Json.String(Json.Get(poll.Json, "error"));
                if (error == "slow_down") interval += 2;
                else if (error != "authorization_pending") return null;
            }

            return null;
        }

        /// <summary>Waits on the editor's loop (a Task.Delay continuation may not come back to it).</summary>
        private static Task Delay(double seconds)
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
