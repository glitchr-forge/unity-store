using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Glitchr.Store
{
    /// <summary>
    /// The user's GitHub token, for the private repositories of a GitHub source. Either pasted once
    /// and kept by the system's secret store - the macOS keychain, Windows' DPAPI (encrypted for this
    /// user, in EditorPrefs), the Secret Service on Linux (secret-tool) - or read from git's own
    /// credential helper each time it is needed. Never in a project, never logged, sent to
    /// api.github.com only.
    /// </summary>
    public static class GitHubToken
    {
        private const string Service = "Forge Store GitHub token";
        private const string Account = "api.github.com";
        private const string UseGitKey = "Glitchr.Store.GitHub.UseGitCredentials";
        private const string DpapiKey = "Glitchr.Store.GitHub.TokenDpapi";
        private static readonly SecretStore Slot = new SecretStore(Service, "forge-store", Account, DpapiKey);

        private static readonly Regex Shape = new Regex("^[A-Za-z0-9_.\\-]{20,255}$");
        private static string session;
        private static string fromGit;
        private static bool gitAsked;

        /// <summary>Read the token from git's credential helper (for https://github.com) when none is kept.</summary>
        public static bool UseGitCredentials
        {
            get => EditorPrefs.GetBool(UseGitKey, false);
            set
            {
                EditorPrefs.SetBool(UseGitKey, value);
                fromGit = null;
                gitAsked = false;
            }
        }

        /// <summary>Whether a string looks like a GitHub token (ghp_, github_pat_, gho_...): what may be kept.</summary>
        public static bool LooksLikeToken(string token) => !string.IsNullOrEmpty(token) && Shape.IsMatch(token);

        /// <summary>Where a pasted token is kept on this system.</summary>
        public static string StoreName => SecretStore.Name;

        private static bool? stored;

        /// <summary>Whether a pasted token is kept: the store is asked once, then after each change.</summary>
        public static bool HasStored => !string.IsNullOrEmpty(session) || (stored ??= !string.IsNullOrEmpty(ReadStored()));

        /// <summary>The token to use: the kept one, else git's when allowed, else null.</summary>
        public static async Task<string> Current()
        {
            string kept = session ?? ReadStored();
            if (!string.IsNullOrEmpty(kept)) return kept;
            if (!UseGitCredentials) return null;
            if (!gitAsked)
            {
                gitAsked = true;
                fromGit = await Task.Run(() => FromGitCredentialHelper());
            }

            return fromGit;
        }

        /// <summary>Keeps a pasted token in the system's store. Returns where, or throws.</summary>
        public static string Store(string token)
        {
            token = token?.Trim();
            if (!LooksLikeToken(token)) throw new ArgumentException("That is not a GitHub token (ghp_..., github_pat_...).");
            session = null;
            stored = null;
            string kept = Slot.Write(token);
            if (kept != null)
            {
                session = token;
                return kept;
            }
            if (ReadStored() != token) throw new Exception("The token could not be kept in " + StoreName + ".");

            return StoreName;
        }

        /// <summary>Forgets the kept token.</summary>
        public static void Forget()
        {
            session = null;
            stored = null;
            Slot.Delete();
        }

        private static string ReadStored()
        {
            string text = Slot.Read();

            return LooksLikeToken(text) ? text : null;
        }

        /// <summary>
        /// What git's credential helper holds for https://github.com (git credential fill), without
        /// ever prompting: GIT_TERMINAL_PROMPT=0 and Git Credential Manager kept non-interactive.
        /// </summary>
        public static string FromGitCredentialHelper()
        {
            foreach (string git in new[] { "git", "/usr/bin/git", "/opt/homebrew/bin/git", "/usr/local/bin/git" })
            {
                try
                {
                    string output = Run(git, "-c credential.interactive=false credential fill", "protocol=https\nhost=github.com\n\n", out int code, true);
                    if (output == null) continue;
                    if (code != 0) return null;

                    return ParseCredential(output);
                }
                catch (Exception)
                {
                    // the next place git may be
                }
            }

            return null;
        }

        /// <summary>The password of git credential's answer (key=value lines), when it is a token.</summary>
        public static string ParseCredential(string output)
        {
            foreach (string line in (output ?? string.Empty).Split('\n'))
            {
                if (!line.StartsWith("password=", StringComparison.Ordinal)) continue;
                string password = line.Substring("password=".Length).Trim();

                return LooksLikeToken(password) ? password : null;
            }

            return null;
        }

        private static string Run(string file, string arguments, string input, out int code, bool quietGit = false) => SecretStore.Run(file, arguments, input, out code, quietGit);
    }
}
