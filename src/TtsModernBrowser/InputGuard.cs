using System;
using HarmonyLib;
using UnityEngine;

namespace TtsModernBrowser
{
    /// <summary>
    /// Keeps TTS from grabbing the tablet behind the browser page and from firing its
    /// hotkeys while the page owns the keyboard.
    ///
    /// Deliberately does NOT patch Pointer.Update: that method also writes
    /// Cursor.visible and drives the game's own drawn pointer, so blocking it makes the
    /// pointer freeze or disappear while it is over the page. Only the grab action is
    /// suppressed, so pointer rendering keeps running.
    /// </summary>
    internal static class InputGuard
    {
        private static Harmony _harmony;

        internal static void Apply()
        {
            _harmony = new Harmony(Plugin.Guid);
            Patch("Pointer", "Grab", nameof(BlockGrab));
            Patch("Pointer", "StartInteract", nameof(BlockGrab));
            Patch("UserDefinedHotkeyManager", "DoHotkey", nameof(BlockHotkey));
        }

        // --------------------------------------------------------------
        // Preconditions
        //
        // Harmony runs the original method when a prefix returns true and skips it when the
        // prefix returns false, so these return the *allow* condition.
        // --------------------------------------------------------------

        private static bool BlockGrab()
        {
            return !(BrowserInput.PointerOverPage && !BrowserInput.UiTyping);
        }

        private static bool BlockHotkey()
        {
            return !(Settings.SuppressHotkeys.Value && BrowserInput.KeyboardFocus && !BrowserInput.UiTyping);
        }

        // --------------------------------------------------------------
        // Harmony plumbing
        // --------------------------------------------------------------

        private static void Patch(string typeName, string methodName, string prefixName)
        {
            var type = AccessTools.TypeByName(typeName);
            if (type == null)
            {
                Log.Error($"[MB] Patch target type '{typeName}' not found; TTS input will not be suppressed there.");
                return;
            }

            var target = AccessTools.Method(type, methodName);
            if (target == null)
            {
                Log.Error($"[MB] Patch target '{typeName}.{methodName}' not found.");
                return;
            }

            var prefix = AccessTools.Method(typeof(InputGuard), prefixName);
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Log.Info($"[MB] Patched {typeName}.{methodName}");
        }
    }
}
