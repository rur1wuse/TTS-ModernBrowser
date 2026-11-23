using System;
using System.IO;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TtsModernBrowser
{
    /// <summary>
    /// Prepares native loading for WebViewToolkit.
    ///
    /// WebViewToolkit.dll must be mapped by Unity's Mono DllImport path, not by
    /// LoadLibraryW: Unity's loader is what calls its UnityPluginLoad export, and
    /// only then does the plugin receive the D3D11 device it renders with. Loading
    /// it by hand yields "Failed to initialize WebView manager" (ErrorNotInitialized).
    ///
    /// So this only puts the plugin folder on the native search path and maps
    /// WebView2Loader.dll (WebViewToolkit's import), then probes one export so the
    /// failure, if any, is logged at startup instead of at first use.
    /// </summary>
    internal static class NativeLoader
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryW(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetDllDirectoryW(string lpPathName);

        [DllImport("WebViewToolkit", CallingConvention = CallingConvention.Cdecl)]
        private static extern int WebViewToolkit_IsInitialized();

        internal static bool Load(string pluginDir)
        {
            if (string.IsNullOrEmpty(pluginDir) || !Directory.Exists(pluginDir))
            {
                Log.Error($"[MB] Plugin directory not found: {pluginDir}");
                return false;
            }

            var toolkitPath = Path.Combine(pluginDir, "WebViewToolkit.dll");
            var loaderPath = Path.Combine(pluginDir, "WebView2Loader.dll");

            foreach (var path in new[] { toolkitPath, loaderPath })
            {
                if (!File.Exists(path))
                {
                    Log.Error($"[MB] Missing native library: {path}");
                    return false;
                }
            }

            // Native search path for WebViewToolkit -> WebView2Loader.dll.
            SetDllDirectoryW(pluginDir);

            if (LoadLibraryW(loaderPath) == IntPtr.Zero)
            {
                Log.Error($"[MB] LoadLibrary failed for {loaderPath} (Win32 {Marshal.GetLastWin32Error()})");
                return false;
            }

            Log.Info($"[MB] WebView2Loader mapped from {pluginDir}");

            try
            {
                var initialized = WebViewToolkit_IsInitialized();
                Log.Info($"[MB] WebViewToolkit resolved through Mono (initialized={initialized})");
                return true;
            }
            catch (Exception e)
            {
                Log.Error($"[MB] WebViewToolkit could not be loaded: {e.GetType().Name}: {e.Message}");
                return false;
            }
            finally
            {
                // Do not leave the plugin folder on the process-wide DLL search path: any
                // later LoadLibrary in the game would otherwise resolve our names first.
                SetDllDirectoryW(null);
            }
        }
    }
}
