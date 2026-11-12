using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Glitchr.Store
{
    /// <summary>
    /// The images a hub's theme names (its banner, its logo), kept in the user's folder (Forge
    /// Store/images in the local application data): images only - PNG or JPEG, told by their first
    /// bytes, not by what the server says -, of a bounded size in bytes and in pixels, the whole
    /// folder bounded too (the oldest go first). Data only: nothing else from a registry is kept.
    /// </summary>
    public sealed class ImageCache
    {
        public const long MaxFolderBytes = 32L * 1024 * 1024;
        /// <summary>How long an image is used before it is asked again (in the background; the kept one shows meanwhile).</summary>
        public static readonly TimeSpan Fresh = TimeSpan.FromDays(1);

        private readonly string folder;
        private readonly long maxFolderBytes;

        public ImageCache(string folder, long maxFolderBytes = MaxFolderBytes)
        {
            this.folder = folder;
            this.maxFolderBytes = maxFolderBytes;
        }

        public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Forge Store", "images");

        /// <summary>"png", "jpg", or null for anything else.</summary>
        public static string Kind(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8) return null;
            if (bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47 && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A) return "png";
            if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF) return "jpg";

            return null;
        }

        /// <summary>An image's width and height read from its header (PNG's IHDR, JPEG's SOF), before it is decoded; null when unreadable.</summary>
        public static (int Width, int Height)? Size(byte[] bytes)
        {
            string kind = Kind(bytes);
            if (kind == "png")
            {
                if (bytes.Length < 24 || bytes[12] != (byte)'I' || bytes[13] != (byte)'H' || bytes[14] != (byte)'D' || bytes[15] != (byte)'R') return null;

                return (BigEndian(bytes, 16, 4), BigEndian(bytes, 20, 4));
            }
            if (kind == "jpg")
            {
                int i = 2;
                while (i + 4 <= bytes.Length)
                {
                    if (bytes[i] != 0xFF) return null;
                    byte marker = bytes[i + 1];
                    if (marker == 0xFF) { i++; continue; } // fill byte
                    if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                    if (marker == 0xD9 || marker == 0xDA) return null; // end, or the scan before any frame
                    int length = BigEndian(bytes, i + 2, 2);
                    if (length < 2) return null;
                    bool frame = marker >= 0xC0 && marker <= 0xCF && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
                    if (frame)
                    {
                        if (i + 9 > bytes.Length) return null;

                        return (BigEndian(bytes, i + 7, 2), BigEndian(bytes, i + 5, 2));
                    }
                    i += 2 + length;
                }
            }

            return null;
        }

        /// <summary>Null when these bytes may be kept and drawn; else why not.</summary>
        public static string Problem(byte[] bytes, long maxBytes, int maxPixels)
        {
            if (bytes == null || bytes.Length == 0) return "empty";
            if (bytes.Length > maxBytes) return "larger than " + maxBytes / 1024 + " KB";
            if (Kind(bytes) == null) return "not a PNG or JPEG image";
            var size = Size(bytes);
            if (size == null) return "an unreadable image header";
            if (size.Value.Width <= 0 || size.Value.Height <= 0 || size.Value.Width > maxPixels || size.Value.Height > maxPixels) return "larger than " + maxPixels + " pixels a side";

            return null;
        }

        private string FileOf(string url)
        {
            using (var sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(url));

                return Path.Combine(folder, string.Concat(hash.Take(16).Select(b => b.ToString("x2"))) + ".img");
            }
        }

        /// <summary>The kept image of that URL, and when it was kept; null when there is none, or none that is still an image within these bounds.</summary>
        public byte[] Get(string url, long maxBytes, int maxPixels, out DateTime keptAt)
        {
            keptAt = DateTime.MinValue;
            if (folder == null || string.IsNullOrEmpty(url)) return null;
            string file = FileOf(url);
            try
            {
                var info = new FileInfo(file);
                if (!info.Exists || info.Length > maxBytes) return null;
                byte[] bytes = File.ReadAllBytes(file);
                if (Problem(bytes, maxBytes, maxPixels) != null) return null;
                keptAt = info.LastWriteTimeUtc;

                return bytes;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Keeps the image when it is one within these bounds; true when kept. The folder is then trimmed to its bound, the oldest first.</summary>
        public bool Put(string url, byte[] bytes, long maxBytes, int maxPixels)
        {
            if (folder == null || string.IsNullOrEmpty(url) || Problem(bytes, maxBytes, maxPixels) != null) return false;
            try
            {
                Directory.CreateDirectory(folder);
                string file = FileOf(url), part = file + ".part";
                File.WriteAllBytes(part, bytes);
                if (File.Exists(file)) File.Delete(file);
                File.Move(part, file);
                Trim(file);

                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>The folder within its bound: the least recently kept images removed first, never the one just kept.</summary>
        private void Trim(string keep)
        {
            var files = new DirectoryInfo(folder).GetFiles("*.img").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            long total = 0;
            foreach (FileInfo file in files)
            {
                total += file.Length;
                if (total > maxFolderBytes && !string.Equals(file.FullName, Path.GetFullPath(keep), StringComparison.Ordinal)) file.Delete();
            }
        }

        private static int BigEndian(byte[] bytes, int offset, int count)
        {
            if (offset + count > bytes.Length) return 0;
            long value = 0;
            for (int i = 0; i < count; i++) value = (value << 8) | bytes[offset + i];

            return value > int.MaxValue ? int.MaxValue : (int)value;
        }
    }
}
