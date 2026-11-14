using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Glitchr.Store
{
    /// <summary>
    /// The user's Package Manager configuration (~/.upmconfig.toml, %USERPROFILE%\.upmconfig.toml on
    /// Windows, or the file UPM_USER_CONFIG_FILE names): where a registry's token - a licence key -
    /// is kept, never in a project. The Package Manager reads it when Unity starts.
    /// </summary>
    public static class UpmConfig
    {
        public static string FilePath
        {
            get
            {
                string configured = Environment.GetEnvironmentVariable("UPM_USER_CONFIG_FILE");
                if (!string.IsNullOrEmpty(configured)) return configured;
                string home = Environment.GetEnvironmentVariable("USERPROFILE");
                if (string.IsNullOrEmpty(home)) home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

                return Path.Combine(home, ".upmconfig.toml");
            }
        }

        /// <summary>The token kept for that registry, or null.</summary>
        public static string Token(string registry)
        {
            string text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
            Match section = Section(text, registry);
            if (!section.Success) return null;
            Match token = Regex.Match(section.Value, "^\\s*token\\s*=\\s*\"([^\"]*)\"", RegexOptions.Multiline);

            return token.Success ? token.Groups[1].Value : null;
        }

        /// <summary>
        /// Keeps that token for that registry: its section written, or its token replaced. One
        /// writer at a time, the editor and Forge Hub alike: an exclusive lock on the file beside
        /// it (&lt;file&gt;.forge-lock, the lock Forge Hub's set_token takes) is held while the file is
        /// read, changed and replaced - written to a temporary file in the same folder, then moved
        /// over it -, so neither loses the other's key and no reader sees a half-written file.
        /// </summary>
        public static void SetToken(string registry, string token)
        {
            registry = registry.TrimEnd('/');
            string path = FilePath;
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            using (ForgeLock.Exclusive(path))
            {
                string text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
                string block = "[npmAuth.\"" + registry + "\"]\ntoken = \"" + token + "\"\nalwaysAuth = true\n";
                Match section = Section(text, registry);
                if (section.Success)
                {
                    text = text.Substring(0, section.Index) + block + text.Substring(section.Index + section.Length);
                }
                else
                {
                    if (text.Length > 0 && !text.EndsWith("\n")) text += "\n";
                    if (text.Length > 0) text += "\n";
                    text += block;
                }
                string temporary = path + "." + System.Diagnostics.Process.GetCurrentProcess().Id + ".tmp";
                File.WriteAllText(temporary, text, new UTF8Encoding(false));
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                }
                finally
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
            }
        }

        /// <summary>The section of that registry: its header and its lines, up to the next section.</summary>
        private static Match Section(string text, string registry)
        {
            string url = Regex.Escape(registry.TrimEnd('/'));

            return Regex.Match(text, "^\\[npmAuth\\.\"" + url + "/?\"\\][^\\[]*", RegexOptions.Multiline);
        }
    }

    /// <summary>
    /// The lock Forge Hub shares on a file both apps write (&lt;file&gt;.forge-lock): a POSIX record
    /// lock on macOS and Linux (Mono's FileStream.Lock is fcntl's, as Forge Hub's lock), LockFile on
    /// Windows. A record lock belongs to the process: the editor's threads take turns on a monitor
    /// first. Waited for 20 seconds at most.
    /// </summary>
    public static class ForgeLock
    {
        private static readonly object InProcess = new object();

        public static string LockPath(string path) => path + ".forge-lock";

        public static IDisposable Exclusive(string path, int timeoutMilliseconds = 20000)
        {
            Monitor.Enter(InProcess);
            FileStream file = null;
            try
            {
                file = new FileStream(LockPath(path), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (true)
                {
                    try
                    {
                        file.Lock(0, 1);
                        return new Held(file);
                    }
                    catch (IOException) when (waited.ElapsedMilliseconds < timeoutMilliseconds)
                    {
                        Thread.Sleep(25);
                    }
                }
            }
            catch
            {
                file?.Dispose();
                Monitor.Exit(InProcess);
                throw;
            }
        }

        private sealed class Held : IDisposable
        {
            private FileStream file;

            public Held(FileStream file) { this.file = file; }

            public void Dispose()
            {
                if (file == null) return;
                try { file.Unlock(0, 1); }
                catch (IOException) { }
                file.Dispose();
                file = null;
                Monitor.Exit(InProcess);
            }
        }
    }
}
