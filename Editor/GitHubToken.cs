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
        public static string StoreName
        {
            get
            {
                if (Application.platform == RuntimePlatform.OSXEditor) return "the macOS keychain";
                if (Application.platform == RuntimePlatform.WindowsEditor) return "Windows' DPAPI, for this user";
                if (Application.platform == RuntimePlatform.LinuxEditor) return "the Secret Service (secret-tool)";

                return "this session only";
            }
        }

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
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    // security -i reads its command on stdin: the token never shows in a process list.
                    Run("/usr/bin/security", "-i", "add-generic-password -U -s \"" + Service + "\" -a \"" + Account + "\" -w \"" + token + "\"\n", out _);
                    break;
                case RuntimePlatform.WindowsEditor:
                    string blob = Run("powershell", "-NoProfile -NonInteractive -Command \"$t = [Console]::In.ReadToEnd().Trim(); ConvertTo-SecureString $t -AsPlainText -Force | ConvertFrom-SecureString\"", token, out _);
                    EditorPrefs.SetString(DpapiKey, blob.Trim());
                    break;
                case RuntimePlatform.LinuxEditor:
                    if (Run("secret-tool", "store --label=\"" + Service + "\" service forge-store account " + Account, token, out int code) == null || code != 0)
                    {
                        session = token;
                        return "this session only (secret-tool is missing)";
                    }
                    break;
                default:
                    session = token;
                    return "this session only";
            }
            if (ReadStored() != token) throw new Exception("The token could not be kept in " + StoreName + ".");

            return StoreName;
        }

        /// <summary>Forgets the kept token.</summary>
        public static void Forget()
        {
            session = null;
            stored = null;
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor: Run("/usr/bin/security", "delete-generic-password -s \"" + Service + "\" -a \"" + Account + "\"", null, out _); break;
                case RuntimePlatform.WindowsEditor: EditorPrefs.DeleteKey(DpapiKey); break;
                case RuntimePlatform.LinuxEditor: Run("secret-tool", "clear service forge-store account " + Account, null, out _); break;
            }
        }

        private static string ReadStored()
        {
            try
            {
                string text = null;
                switch (Application.platform)
                {
                    case RuntimePlatform.OSXEditor:
                        text = Run("/usr/bin/security", "find-generic-password -s \"" + Service + "\" -a \"" + Account + "\" -w", null, out int mac);
                        if (mac != 0) text = null;
                        break;
                    case RuntimePlatform.WindowsEditor:
                        string blob = EditorPrefs.GetString(DpapiKey, string.Empty);
                        if (blob.Length == 0) return null;
                        text = Run("powershell", "-NoProfile -NonInteractive -Command \"$s = ConvertTo-SecureString ([Console]::In.ReadToEnd().Trim()); [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))\"", blob, out _);
                        break;
                    case RuntimePlatform.LinuxEditor:
                        text = Run("secret-tool", "lookup service forge-store account " + Account, null, out int linux);
                        if (linux != 0) text = null;
                        break;
                }
                text = text?.Trim();

                return LooksLikeToken(text) ? text : null;
            }
            catch (Exception)
            {
                return null;
            }
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

        /// <summary>A tool run with its input on stdin; its output, or null when it cannot start. Nothing is logged.</summary>
        private static string Run(string file, string arguments, string input, out int code, bool quietGit = false)
        {
            code = -1;
            var start = new ProcessStartInfo(file, arguments)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
            };
            if (quietGit)
            {
                start.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
                start.EnvironmentVariables["GCM_INTERACTIVE"] = "never";
                start.EnvironmentVariables["GIT_ASKPASS"] = string.Empty;
                start.EnvironmentVariables["SSH_ASKPASS"] = string.Empty;
            }
            Process process;
            try { process = Process.Start(start); }
            catch (Exception) { return null; }
            if (process == null) return null;
            using (process)
            {
                if (input != null) process.StandardInput.Write(input);
                process.StandardInput.Close();
                Task<string> output = process.StandardOutput.ReadToEndAsync();
                process.StandardError.ReadToEndAsync();
                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); } catch (Exception) { }
                    return null;
                }
                code = process.ExitCode;

                return output.Result;
            }
        }
    }
}
