using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Glitchr.Store
{
    /// <summary>
    /// The hub's requests (UnityWebRequest, awaited): JSON in and out, a Bearer token for a
    /// registry that has one, and - for a development site only - a certificate accepted by its
    /// SHA-256 fingerprint (HubSource.CertificateSha256).
    /// </summary>
    public static class HubHttp
    {
        public sealed class Answer
        {
            public long Status;
            public string Text;
            public string Error;
            public bool Ok => Status >= 200 && Status < 300;
            public object Json => string.IsNullOrEmpty(Text) ? null : Hub.Json.Parse(Text);
        }

        public static Task<Answer> Get(string url, string token = null, string certificate = null)
        {
            return Send(UnityWebRequest.Get(url), token, certificate);
        }

        public static Task<Answer> PostJson(string url, object body, string certificate = null)
        {
            var request = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(Hub.Json.Write(body))),
                downloadHandler = new DownloadHandlerBuffer(),
            };
            request.SetRequestHeader("Content-Type", "application/json");

            return Send(request, null, certificate);
        }

        private static Task<Answer> Send(UnityWebRequest request, string token, string certificate)
        {
            request.SetRequestHeader("Accept", "application/json");
            if (!string.IsNullOrEmpty(token)) request.SetRequestHeader("Authorization", "Bearer " + token);
            if (!string.IsNullOrEmpty(certificate))
            {
                request.certificateHandler = new Pinned(certificate);
                request.disposeCertificateHandlerOnDispose = true;
            }
            request.timeout = 30;
            var completion = new TaskCompletionSource<Answer>();
            request.SendWebRequest().completed += _ =>
            {
                var answer = new Answer
                {
                    Status = request.responseCode,
                    Text = request.downloadHandler?.text,
                    Error = request.result == UnityWebRequest.Result.Success ? null : request.error,
                };
                request.Dispose();
                completion.TrySetResult(answer);
            };

            return completion.Task;
        }

        /// <summary>One certificate accepted, by its fingerprint: a development site's own.</summary>
        private sealed class Pinned : CertificateHandler
        {
            private readonly string fingerprint;

            public Pinned(string fingerprint) { this.fingerprint = fingerprint.Replace(":", string.Empty).ToLowerInvariant(); }

            protected override bool ValidateCertificate(byte[] certificateData)
            {
                using (var sha = SHA256.Create())
                {
                    return BitConverter.ToString(sha.ComputeHash(certificateData)).Replace("-", string.Empty).ToLowerInvariant() == fingerprint;
                }
            }
        }
    }
}
