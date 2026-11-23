using System;
using System.IO;
using BepInEx.Configuration;
using UnityEngine;

namespace TtsModernBrowser
{
    /// <summary>
    /// Single initialization path shared by the BepInEx plugin and the Doorstop loader.
    /// </summary>
    public static class Bootstrap
    {
        private static bool _started;

        internal static string UserDataFolder { get; private set; }

        public static void Start(string pluginDllPath)
        {
            if (_started) return;
            _started = true;

            var pluginDir = Path.GetDirectoryName(pluginDllPath) ?? string.Empty;
            Log.Info($"[MB] Bootstrapping from {pluginDir}");

            if (!EnsureSettings(pluginDir)) return;

            if (!NativeLoader.Load(pluginDir))
            {
                Log.Error("[MB] Browser feature disabled: WebView2 native libraries unavailable.");
                return;
            }

            ConfigureWebView2Arguments();
            try
            {
                UserDataFolder = Settings.ResolveUserDataFolder(pluginDir);
                Log.Info($"[MB] WebView2 user data folder: {UserDataFolder}");
            }
            catch (Exception e)
            {
                Log.Error($"[MB] UserDataFolder unusable, falling back to WebView2 default: {e.Message}");
                UserDataFolder = null;
            }

            var go = new GameObject("TTSModernBrowser");
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<BrowserManager>();
            go.AddComponent<BrowserInput>();

            try
            {
                InputGuard.Apply();
                Log.Info("[MB] TTS Modern Browser ready");
            }
            catch (Exception e)
            {
                Log.Error($"[MB] InputGuard failed, TTS input will not be suppressed: {e}");
            }
        }

        /// <summary>
        /// BepInEx binds the config in Plugin.Awake; the Doorstop fallback reaches Start()
        /// directly, so bind there too. Every Settings entry is read through a ConfigEntry
        /// that would otherwise be null on that path.
        /// </summary>
        private static bool EnsureSettings(string pluginDir)
        {
            if (Settings.MaxBrowsers == null)
            {
                try
                {
                    var configDir = Path.GetFullPath(Path.Combine(pluginDir, "..", "..", "config"));
                    Directory.CreateDirectory(configDir);
                    Settings.Bind(new ConfigFile(Path.Combine(configDir, "com.tts.modernbrowser.cfg"), true));
                    Log.Info($"[MB] Bound standalone config in {configDir}");
                }
                catch (Exception e)
                {
                    Log.Error($"[MB] Cannot create config, browser feature disabled: {e.Message}");
                    return false;
                }
            }

            return Settings.Enabled.Value;
        }

        /// <summary>
        /// WebView2 appends these switches to the Chromium command line, and the variable
        /// must be set before the WebView2 environment exists. Chromium otherwise throttles
        /// - and in some cases stops - painting a window it considers occluded; Windows
        /// Graphics Capture then has an empty surface, so the page loads but the tablet
        /// texture stays black.
        /// </summary>
        private static void ConfigureWebView2Arguments()
        {
            const string variable = "WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS";
            const string arguments =
                "--disable-features=CalculateNativeWinOcclusion" +
                " --disable-backgrounding-occluded-windows" +
                " --disable-renderer-backgrounding" +
                " --disable-background-timer-throttling";

            try
            {
                var existing = Environment.GetEnvironmentVariable(variable);
                var combined = string.IsNullOrEmpty(existing) ? arguments : existing + " " + arguments;

                Environment.SetEnvironmentVariable(variable, combined);
                Log.Info($"[MB] WebView2 browser arguments: {combined}");
            }
            catch (Exception e)
            {
                Log.Warn($"[MB] Could not set {variable}: {e.Message}");
            }
        }
    }
}
