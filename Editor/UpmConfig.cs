using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

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

        /// <summary>Keeps that token for that registry: its section written, or its token replaced.</summary>
        public static void SetToken(string registry, string token)
        {
            registry = registry.TrimEnd('/');
            string text = File.Exists(FilePath) ? File.ReadAllText(FilePath) : string.Empty;
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
            string directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(FilePath, text, new UTF8Encoding(false));
        }

        /// <summary>The section of that registry: its header and its lines, up to the next section.</summary>
        private static Match Section(string text, string registry)
        {
            string url = Regex.Escape(registry.TrimEnd('/'));

            return Regex.Match(text, "^\\[npmAuth\\.\"" + url + "/?\"\\][^\\[]*", RegexOptions.Multiline);
        }
    }
}
