using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Glitchr.Store
{
    /// <summary>
    /// A per-user JSON file another app writes too (Forge Hub shares keys.json): read as a tree,
    /// read again before every decision and every write, only what the caller owns changed, unknown
    /// fields kept, written to a temporary file of the same folder then renamed over it. A file
    /// caught while another writer rewrites it in place (a reader in between sees it half-written)
    /// is read again a few times before it counts as damaged.
    /// </summary>
    public sealed class SharedFile
    {
        /// <summary>Readings of a file that does not parse before it counts as damaged, and the pause between two.</summary>
        public const int Attempts = 4;
        public const int PauseMilliseconds = 40;

        public string Path { get; }

        /// <summary>How the file's text is read (tests replace it to play a writer caught mid-write); null: no file.</summary>
        public Func<string, string> ReadText = path => File.Exists(path) ? File.ReadAllText(path) : null;

        /// <summary>How the reader waits between two attempts (tests skip the wait).</summary>
        public Action<int> Pause = Thread.Sleep;

        private Dictionary<string, object> cached;
        private bool cachedDamaged;
        private DateTime stampTime;
        private long stampLength = -1;

        public SharedFile(string path)
        {
            Path = path;
        }

        /// <summary>Whether the last reading found a file that is not a JSON object (then read as empty).</summary>
        public bool Damaged => cachedDamaged;

        /// <summary>
        /// The file as a tree. <paramref name="fresh"/>: read from the disk whatever happened; else
        /// the last reading is reused while the file's time and length are unchanged (a window
        /// drawing many times a second reads it once per change).
        /// </summary>
        public Dictionary<string, object> Read(bool fresh)
        {
            if (!fresh && cached != null && Stamp(out DateTime seen, out long size) && seen == stampTime && size == stampLength) return cached;
            for (int attempt = 1; ; attempt++)
            {
                Stamp(out DateTime time, out long length);
                string text;
                try
                {
                    text = ReadText(Path);
                }
                catch (FileNotFoundException)
                {
                    text = null;
                }
                catch (DirectoryNotFoundException)
                {
                    text = null;
                }
                catch (IOException) when (attempt < Attempts)
                {
                    // Held by the other app's rename on Windows: in a moment.
                    Pause(PauseMilliseconds);
                    continue;
                }
                Dictionary<string, object> tree = null;
                bool damaged = false;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    try
                    {
                        tree = Json.Object(Json.Parse(text));
                    }
                    catch (FormatException)
                    {
                        tree = null;
                    }
                    if (tree == null)
                    {
                        if (attempt < Attempts)
                        {
                            Pause(PauseMilliseconds);
                            continue;
                        }
                        damaged = true;
                    }
                }
                cached = tree ?? new Dictionary<string, object>();
                cachedDamaged = damaged;
                stampTime = time;
                stampLength = length;

                return cached;
            }
        }

        /// <summary>Writes the tree whole: a temporary file in the same folder, renamed over the file.</summary>
        public void Write(Dictionary<string, object> tree)
        {
            string folder = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path));
            Directory.CreateDirectory(folder);
            string temporary = System.IO.Path.Combine(folder, "." + System.IO.Path.GetFileName(Path) + "." + ProfileStore.NewId() + ".tmp");
            try
            {
                File.WriteAllText(temporary, Json.Write(tree, true) + "\n", new UTF8Encoding(false));
                if (File.Exists(Path)) File.Replace(temporary, Path, null);
                else File.Move(temporary, Path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            cached = tree;
            cachedDamaged = false;
            Stamp(out stampTime, out stampLength);
        }

        /// <summary>The next reading goes to the disk (a change that could not be written is not kept).</summary>
        public void Forget()
        {
            cached = null;
            stampLength = -1;
        }

        /// <summary>The file kept aside as <c>&lt;name&gt;.damaged</c> (the last damaged copy wins).</summary>
        public void KeepAside()
        {
            try { File.Copy(Path, Path + ".damaged", true); }
            catch (Exception) { /* nothing more to save */ }
        }

        /// <summary>The members of an object sorted by name (ordinal, as Forge Hub sorts them), values untouched.</summary>
        public static Dictionary<string, object> Sorted(Dictionary<string, object> tree)
        {
            var sorted = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> pair in tree.OrderBy(p => p.Key, StringComparer.Ordinal)) sorted[pair.Key] = pair.Value;

            return sorted;
        }

        private bool Stamp(out DateTime time, out long length)
        {
            try
            {
                var info = new FileInfo(Path);
                if (!info.Exists)
                {
                    time = DateTime.MinValue;
                    length = -2;

                    return true;
                }
                time = info.LastWriteTimeUtc;
                length = info.Length;

                return true;
            }
            catch (Exception)
            {
                time = DateTime.MinValue;
                length = -1;

                return false;
            }
        }
    }
}
