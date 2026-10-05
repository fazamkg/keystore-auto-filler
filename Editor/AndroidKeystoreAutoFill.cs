using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

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
            string keystorePath = hasKeystoreField ? Path.Combine(Dir, entry.keystore) : null;
            bool keystoreFound = hasKeystoreField && File.Exists(keystorePath);

            PlayerSettings.Android.useCustomKeystore = true;

            if (keystoreFound)
            {
                PlayerSettings.Android.keystoreName = keystorePath;
                PlayerSettings.Android.keyaliasName = entry.alias;
            }
            else
            {
                // Empty keystore field or file not in config folder: keep project's existing keystore/alias, only fill passwords
                string existing = PlayerSettings.Android.keystoreName;
                if (string.IsNullOrEmpty(existing))
                {
                    if (!silent)
                        Debug.LogWarning(
                            $"[Keystore] No keystore in config for '{entry.name}' and project has none set. " +
                            "Only passwords will be applied.");
                }
                else if (!silent)
                {
                    string reason = hasKeystoreField
                        ? $"keystore file missing ({keystorePath})"
                        : "keystore field empty";
                    Debug.Log($"[Keystore] {reason}; keeping project keystore/alias and filling passwords for '{entry.name}'");
                }
            }

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
    } 
}