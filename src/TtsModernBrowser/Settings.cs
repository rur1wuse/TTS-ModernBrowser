using System;
using System.IO;
using BepInEx.Configuration;

namespace TtsModernBrowser
{
    internal static class Settings
    {
        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> MaxBrowsers;
        internal static ConfigEntry<int> DefaultWidth;
        internal static ConfigEntry<string> CaptureMode;
        internal static ConfigEntry<bool> FlipTexture;
        internal static ConfigEntry<bool> SuppressHotkeys;
        internal static ConfigEntry<string> UserDataFolder;

        internal static void Bind(ConfigFile config)
        {
            Enabled = config.Bind("General", "Enabled", true,
                "Enable the world-space modern browser overlay.");
            MaxBrowsers = config.Bind("General", "MaxBrowsers", 4,
                "Maximum number of concurrent WebView2 instances.");
            UserDataFolder = config.Bind("General", "UserDataFolder", "",
                "WebView2 user data folder. Empty = <plugin dir>\\WebView2Data.");
            DefaultWidth = config.Bind("Rendering", "DefaultWidth", 1280,
                "WebView2 render width in pixels; height follows the tablet aspect ratio.");
            CaptureMode = config.Bind("Rendering", "CaptureMode", "Auto",
                "Frame source: Auto (graphics capture, fall back to window capture), " +
                "GraphicsCapture, or WindowCapture.");
            FlipTexture = config.Bind("Rendering", "FlipTexture", false,
                "Flip the WebView texture vertically if the page renders upside down.");
            SuppressHotkeys = config.Bind("Input", "SuppressHotkeys", true,
                "Block TTS hotkeys while keyboard focus is inside the browser.");
        }

        /// <summary>Frames come from PrintWindow instead of Windows Graphics Capture.</summary>
        internal static bool WindowCaptureForced =>
            string.Equals(CaptureMode.Value, "WindowCapture", StringComparison.OrdinalIgnoreCase);

        /// <summary>Auto may switch to window capture when graphics capture yields black frames.</summary>
        internal static bool CaptureFallbackAllowed =>
            !string.Equals(CaptureMode.Value, "GraphicsCapture", StringComparison.OrdinalIgnoreCase);

        internal static string ResolveUserDataFolder(string pluginDir)
        {
            var configured = UserDataFolder.Value;
            var path = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(pluginDir, "WebView2Data")
                : configured;

            Directory.CreateDirectory(path);
            return path;
        }
    }
}
