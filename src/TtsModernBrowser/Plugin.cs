using System;
using BepInEx;
using BepInEx.Logging;

namespace TtsModernBrowser
{
    [BepInPlugin(Guid, "TTS Modern Browser", Version)]
    public sealed class Plugin : BaseUnityPlugin
    {
        internal const string Guid = "com.tts.modernbrowser";
        internal const string Version = "0.1.0";

        private ManualLogSource _log;

        private void Awake()
        {
            _log = Logger;
            Log.Attach(Logger);
            try
            {
                Settings.Bind(Config);
                if (!Settings.Enabled.Value)
                {
                    _log.LogInfo("Disabled by config.");
                    return;
                }

                Bootstrap.Start(typeof(Plugin).Assembly.Location);
            }
            catch (Exception e)
            {
                _log.LogError($"Startup failed: {e}");
            }
        }
    }
}
