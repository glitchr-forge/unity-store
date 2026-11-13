using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine.Networking;

namespace Glitchr.Store
{
    /// <summary>A GET with its headers, as the GitHub client sends it.</summary>
    public sealed class WebRequest
    {
        public string Url;
        /// <summary>A 3xx is handed back as it is (the caller decides whether to follow it), not followed.</summary>
        public bool NoRedirects;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>An answer with its headers (ETag, rate limits, pagination).</summary>
    public sealed class WebResponse
    {
        public long Status;
        public string Text;
        public string Error;
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public bool Ok => Status >= 200 && Status < 300;

        public string Header(string name) => Headers.TryGetValue(name, out string value) ? value : null;
    }

    /// <summary>Sends a request: UnityWebRequest in the editor, a canned answer in the tests.</summary>
    public interface IWebTransport
    {
        Task<WebResponse> Send(WebRequest request);
    }

    /// <summary>UnityWebRequest, awaited; its callback comes back on the editor's loop.</summary>
    public sealed class UnityWebTransport : IWebTransport
    {
        public static readonly UnityWebTransport Instance = new UnityWebTransport();

        public Task<WebResponse> Send(WebRequest request)
        {
            var web = UnityWebRequest.Get(request.Url);
            foreach (KeyValuePair<string, string> header in request.Headers) web.SetRequestHeader(header.Key, header.Value);
            web.timeout = 30;
            if (request.NoRedirects) web.redirectLimit = 0;
            var completion = new TaskCompletionSource<WebResponse>();
            web.SendWebRequest().completed += _ =>
            {
                var response = new WebResponse
                {
                    Status = web.responseCode,
                    Text = web.downloadHandler?.text,
                    // A 304 or a 404 is an answer, not a failure of the connection.
                    Error = web.result == UnityWebRequest.Result.ConnectionError ? web.error : null,
                };
                Dictionary<string, string> headers = web.GetResponseHeaders();
                if (headers != null)
                {
                    foreach (KeyValuePair<string, string> header in headers) response.Headers[header.Key] = header.Value;
                }
                web.Dispose();
                completion.TrySetResult(response);
            };

            return completion.Task;
        }
    }
}
