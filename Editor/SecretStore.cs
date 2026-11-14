using System;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;

namespace Glitchr.Store
{
    /// <summary>
    /// One secret kept by the system's secret store: the macOS keychain (a generic password of a
    /// service and an account), Windows' DPAPI (encrypted for this user, kept in EditorPrefs), the
    /// Secret Service on Linux (secret-tool). The GitHub token and each forge's account token have
    /// a slot of their own. Never in a project, never logged; the secret goes to the tools on their
    /// standard input, never on a command line.
    /// </summary>
    public sealed class SecretStore
    {
        private readonly string service;
        private readonly string linuxService;
        private readonly string account;
        private readonly string dpapiKey;

        /// <param name="service">The keychain item's service (and the Secret Service's label).</param>
        /// <param name="linuxService">The Secret Service's "service" attribute.</param>
        /// <param name="account">The item's account: what the secret is for.</param>
        /// <param name="dpapiKey">The EditorPrefs key of the DPAPI-encrypted secret on Windows.</param>
        public SecretStore(string service, string linuxService, string account, string dpapiKey)
        {
            this.service = service;
            this.linuxService = linuxService;
            this.account = account;
            this.dpapiKey = dpapiKey;
        }

        /// <summary>Where a secret is kept on this system.</summary>
        public static string Name
        {
            get
            {
                if (Application.platform == RuntimePlatform.OSXEditor) return "the macOS keychain";
                if (Application.platform == RuntimePlatform.WindowsEditor) return "Windows' DPAPI, for this user";
                if (Application.platform == RuntimePlatform.LinuxEditor) return "the Secret Service (secret-tool)";

                return "this session only";
            }
        }

        /// <summary>
        /// Keeps the secret. Null when it is kept by the system; otherwise where it is kept instead
        /// ("this session only..."): the caller keeps it in memory.
        /// </summary>
        public string Write(string secret)
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor:
                    // security -i reads its command on stdin: the secret never shows in a process list.
                    Run("/usr/bin/security", "-i", "add-generic-password -U -s \"" + service + "\" -a \"" + account + "\" -w \"" + secret + "\"\n", out _);
                    return null;
                case RuntimePlatform.WindowsEditor:
                    string blob = Run("powershell", "-NoProfile -NonInteractive -Command \"$t = [Console]::In.ReadToEnd().Trim(); ConvertTo-SecureString $t -AsPlainText -Force | ConvertFrom-SecureString\"", secret, out _);
                    EditorPrefs.SetString(dpapiKey, (blob ?? string.Empty).Trim());
                    return null;
                case RuntimePlatform.LinuxEditor:
                    if (Run("secret-tool", "store --label=\"" + service + "\" service " + linuxService + " account " + account, secret, out int code) == null || code != 0)
                    {
                        return "this session only (secret-tool is missing)";
                    }
                    return null;
                default:
                    return "this session only";
            }
        }

        /// <summary>The kept secret, trimmed, or null.</summary>
        public string Read()
        {
            try
            {
                string text = null;
                switch (Application.platform)
                {
                    case RuntimePlatform.OSXEditor:
                        text = Run("/usr/bin/security", "find-generic-password -s \"" + service + "\" -a \"" + account + "\" -w", null, out int mac);
                        if (mac != 0) text = null;
                        break;
                    case RuntimePlatform.WindowsEditor:
                        string blob = EditorPrefs.GetString(dpapiKey, string.Empty);
                        if (blob.Length == 0) return null;
                        text = Run("powershell", "-NoProfile -NonInteractive -Command \"$s = ConvertTo-SecureString ([Console]::In.ReadToEnd().Trim()); [Runtime.InteropServices.Marshal]::PtrToStringBSTR([Runtime.InteropServices.Marshal]::SecureStringToBSTR($s))\"", blob, out _);
                        break;
                    case RuntimePlatform.LinuxEditor:
                        text = Run("secret-tool", "lookup service " + linuxService + " account " + account, null, out int linux);
                        if (linux != 0) text = null;
                        break;
                }

                return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Forgets the kept secret (nothing kept is not an error).</summary>
        public void Delete()
        {
            switch (Application.platform)
            {
                case RuntimePlatform.OSXEditor: Run("/usr/bin/security", "delete-generic-password -s \"" + service + "\" -a \"" + account + "\"", null, out _); break;
                case RuntimePlatform.WindowsEditor: EditorPrefs.DeleteKey(dpapiKey); break;
                case RuntimePlatform.LinuxEditor: Run("secret-tool", "clear service " + linuxService + " account " + account, null, out _); break;
            }
        }

        /// <summary>A tool run with its input on stdin; its output, or null when it cannot start. Nothing is logged.</summary>
        public static string Run(string file, string arguments, string input, out int code, bool quietGit = false)
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
