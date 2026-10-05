using System;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Faza
{
    [Serializable]
    class KeystoreEntry
    {
        public string name, keystore, storePass, alias, aliasPass;
    }
    [Serializable] class KeystoreConfig { public KeystoreEntry[] entries; }

    [InitializeOnLoad]
    public class AndroidKeystoreAutoFill : IPreprocessBuildWithReport
    {
        public int callbackOrder => 0;

        // Override with env var UNITY_KEYSTORE_DIR if you want a different location
        static string Dir
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("UNITY_KEYSTORE_DIR");
                return !string.IsNullOrEmpty(env)
                    ? env
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "UnityKeystores");
            }
        }

        static AndroidKeystoreAutoFill()
        {
            // Delay so we don't touch PlayerSettings mid-reload
            EditorApplication.delayCall += () => Apply(silent: true);
        }

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform == BuildTarget.Android) Apply(silent: false);
        }

        [MenuItem("Tools/Android/Apply Keystore From Config")]
        static void ApplyMenu() => Apply(silent: false);

        static void Apply(bool silent)
        {
            string configPath = Path.Combine(Dir, "keystores.json");
            if (!File.Exists(configPath))
            {
                if (!silent) Debug.LogWarning($"[Keystore] Config not found: {configPath}");
                return;
            }

            var config = JsonUtility.FromJson<KeystoreConfig>(File.ReadAllText(configPath));

            // Match on project folder name first, then Unity product name
            string folderName = new DirectoryInfo(Path.GetDirectoryName(Application.dataPath)).Name;
            KeystoreEntry entry = Find(config, folderName) ?? Find(config, PlayerSettings.productName);

            if (entry == null)
            {
                if (!silent)
                    Debug.LogWarning($"[Keystore] No entry for '{folderName}' or '{PlayerSettings.productName}' in {configPath}");
                return;
            }

            bool hasKeystoreField = !string.IsNullOrWhiteSpace(entry.keystore);
            string configKeystorePath = hasKeystoreField ? Path.Combine(Dir, entry.keystore) : null;
            bool keystoreFound = hasKeystoreField && File.Exists(configKeystorePath);

            string selectedKeystore = null;
            if (keystoreFound)
            {
                selectedKeystore = configKeystorePath;
            }
            else
            {
                // Empty keystore field or file not in config folder: keep project's existing keystore
                string existing = PlayerSettings.Android.keystoreName;
                if (string.IsNullOrEmpty(existing))
                {
                    if (!silent)
                        Debug.LogWarning(
                            $"[Keystore] No keystore in config for '{entry.name}' and project has none set. " +
                            "Only passwords will be applied.");
                }
                else
                {
                    selectedKeystore = existing;
                    if (!silent)
                    {
                        string reason = hasKeystoreField
                            ? $"keystore file missing ({configKeystorePath})"
                            : "keystore field empty";
                        Debug.Log($"[Keystore] {reason}; keeping project keystore '{existing}' and filling passwords for '{entry.name}'");
                    }
                }
            }

            string alias = entry.alias;
            if (string.IsNullOrWhiteSpace(alias))
            {
                if (!string.IsNullOrEmpty(selectedKeystore) && File.Exists(selectedKeystore))
                {
                    alias = TryGetFirstAlias(selectedKeystore, entry.storePass);
                    if (string.IsNullOrEmpty(alias))
                    {
                        if (!silent)
                            Debug.LogWarning($"[Keystore] Could not read first alias from '{selectedKeystore}'");
                    }
                    else if (!silent)
                    {
                        Debug.Log($"[Keystore] alias empty in config; using first alias '{alias}' from keystore");
                    }
                }
            }

            PlayerSettings.Android.useCustomKeystore = true;
            if (keystoreFound)
                PlayerSettings.Android.keystoreName = selectedKeystore;
            if (!string.IsNullOrWhiteSpace(alias))
                PlayerSettings.Android.keyaliasName = alias;
            PlayerSettings.Android.keystorePass = entry.storePass;
            PlayerSettings.Android.keyaliasPass = entry.aliasPass;

            if (!silent && keystoreFound) Debug.Log($"[Keystore] Applied '{entry.name}'");
        }

        static KeystoreEntry Find(KeystoreConfig c, string name)
        {
            if (c?.entries == null || string.IsNullOrEmpty(name)) return null;
            foreach (var e in c.entries)
                if (string.Equals(e.name, name, StringComparison.OrdinalIgnoreCase)) return e;
            return null;
        }

        static string TryGetFirstAlias(string keystorePath, string storePass)
        {
            string keytool = FindKeytool();
            if (keytool == null) return null;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = keytool,
                    Arguments = $"-list -keystore \"{keystorePath}\" -storepass \"{storePass ?? ""}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using (var process = Process.Start(psi))
                {
                    if (process == null) return null;
                    string stdout = process.StandardOutput.ReadToEnd();
                    process.StandardError.ReadToEnd();
                    process.WaitForExit(15000);
                    if (process.ExitCode != 0) return null;
                    return ParseFirstAlias(stdout);
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Keystore] keytool failed: {e.Message}");
                return null;
            }
        }

        static string ParseFirstAlias(string keytoolListOutput)
        {
            if (string.IsNullOrEmpty(keytoolListOutput)) return null;

            // Verbose / localized: "Alias name: myalias"
            var aliasName = Regex.Match(keytoolListOutput, @"^Alias name:\s*(.+)\s*$", RegexOptions.Multiline);
            if (aliasName.Success)
                return aliasName.Groups[1].Value.Trim();

            // Default -list: "myalias, 01-Jan-2020, PrivateKeyEntry,"
            foreach (string rawLine in keytoolListOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string line = rawLine.Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("Keystore ", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Your keystore", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("Certificate ", StringComparison.OrdinalIgnoreCase)) continue;

                int comma = line.IndexOf(',');
                if (comma <= 0) continue;
                if (line.IndexOf("PrivateKeyEntry", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("SecretKeyEntry", StringComparison.OrdinalIgnoreCase) < 0 &&
                    line.IndexOf("trustedCertEntry", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                return line.Substring(0, comma).Trim();
            }

            return null;
        }

        static string FindKeytool()
        {
            // Unity's configured JDK (Android Build Support)
            string unityJdk = TryGetUnityJdkRoot();
            if (!string.IsNullOrEmpty(unityJdk))
            {
                string candidate = KeytoolInJdk(unityJdk);
                if (candidate != null) return candidate;
            }

            string javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (!string.IsNullOrEmpty(javaHome))
            {
                string candidate = KeytoolInJdk(javaHome);
                if (candidate != null) return candidate;
            }

            // Last resort: hope keytool is on PATH
            return "keytool";
        }

        static string KeytoolInJdk(string jdkRoot)
        {
            string file = Application.platform == RuntimePlatform.WindowsEditor ? "keytool.exe" : "keytool";
            string path = Path.Combine(jdkRoot, "bin", file);
            return File.Exists(path) ? path : null;
        }

        static string TryGetUnityJdkRoot()
        {
            try
            {
                var type = Type.GetType("UnityEditor.Android.AndroidExternalToolsSettings, UnityEditor.Android.Extensions");
                var prop = type?.GetProperty("jdkRootPath");
                return prop?.GetValue(null) as string;
            }
            catch
            {
                return null;
            }
        }
    }
}
