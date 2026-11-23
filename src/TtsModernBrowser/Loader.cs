using System;
using UnityEngine;

namespace TtsModernBrowser
{
    /// <summary>
    /// UnityDoorstop entry point, used only when the BepInEx preloader cannot run.
    /// Configure doorstop_config.ini with target_assembly=TtsModernBrowser.dll and
    /// entry_point=TtsModernBrowser.Loader::Main, then delete BepInEx's winhttp.dll loader.
    /// </summary>
    public static class Loader
    {
        public static void Main()
        {
            try
            {
                Bootstrap.Start(typeof(Loader).Assembly.Location);
            }
            catch (Exception e)
            {
                Log.Error($"[MB] Doorstop bootstrap failed: {e}");
            }
        }
    }
}
