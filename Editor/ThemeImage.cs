using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace Glitchr.Store
{
    /// <summary>
    /// A theme's images as textures: read from the user's cache first, fetched over HTTPS in the
    /// background (never on the editor's thread, a request stopped as soon as it outgrows its
    /// bound), kept only when they are PNG or JPEG images within their bounds. A failure draws
    /// nothing and says nothing: the header falls back to its plain look.
    /// </summary>
    public sealed class ThemeImage
    {
        public readonly long MaxBytes;
        public readonly int MaxPixels;

        private readonly ImageCache cache;
        private string url;
        private Action changed;

        public Texture2D Texture { get; private set; }

        public ThemeImage(long maxBytes, int maxPixels, ImageCache cache = null)
        {
            MaxBytes = maxBytes;
            MaxPixels = maxPixels;
            this.cache = cache ?? new ImageCache(ImageCache.DefaultFolder);
        }

        /// <summary>Shows the image of that URL (null: none); <paramref name="repaint"/> is called when it arrives.</summary>
        public async void Load(string address, Action repaint)
        {
            if (address == url) return;
            url = address;
            changed = repaint;
            Clear();
            if (string.IsNullOrEmpty(address) || !address.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return;

            DateTime keptAt = DateTime.MinValue;
            byte[] kept = null;
            try { kept = await Task.Run(() => cache.Get(address, MaxBytes, MaxPixels, out keptAt)); }
            catch (Exception) { /* no cache: fetched below */ }
            if (address != url) return;
            if (kept != null) Show(kept);
            if (kept != null && DateTime.UtcNow - keptAt < ImageCache.Fresh) return;

            byte[] fetched = await Fetch(address, MaxBytes);
            if (fetched == null || address != url || ImageCache.Problem(fetched, MaxBytes, MaxPixels) != null) return;
            try { await Task.Run(() => cache.Put(address, fetched, MaxBytes, MaxPixels)); }
            catch (Exception) { /* shown, not kept */ }
            if (address == url) Show(fetched);
        }

        private void Show(byte[] bytes)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave, wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            if (!texture.LoadImage(bytes, true) || texture.width > MaxPixels || texture.height > MaxPixels)
            {
                UnityEngine.Object.DestroyImmediate(texture);

                return;
            }
            Clear();
            Texture = texture;
            changed?.Invoke();
        }

        public void Clear()
        {
            if (Texture != null) UnityEngine.Object.DestroyImmediate(Texture);
            Texture = null;
        }

        /// <summary>The bytes at that URL, or null: a failure, a timeout, or more than <paramref name="maxBytes"/> (the request is aborted).</summary>
        public static Task<byte[]> Fetch(string address, long maxBytes)
        {
            var completion = new TaskCompletionSource<byte[]>();
            UnityWebRequest request;
            try
            {
                request = new UnityWebRequest(address, UnityWebRequest.kHttpVerbGET, new DownloadHandlerBuffer(), null) { timeout = 20, redirectLimit = 3 };
                request.SendWebRequest();
            }
            catch (Exception)
            {
                completion.SetResult(null);

                return completion.Task;
            }
            void Check()
            {
                if (!request.isDone && request.downloadedBytes <= (ulong)maxBytes) return;
                EditorApplication.update -= Check;
                byte[] bytes = null;
                if (request.isDone && request.result == UnityWebRequest.Result.Success && request.downloadedBytes <= (ulong)maxBytes) bytes = request.downloadHandler.data;
                else if (!request.isDone) request.Abort();
                request.Dispose();
                completion.TrySetResult(bytes);
            }
            EditorApplication.update += Check;

            return completion.Task;
        }
    }
}
