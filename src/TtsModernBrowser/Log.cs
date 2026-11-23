using System;
using BepInEx.Logging;
using UnityEngine;

namespace TtsModernBrowser
{
    /// <summary>
    /// Routes plugin messages to BepInEx's manual log source (which lands in
    /// BepInEx\LogOutput.log) and mirrors WebViewToolkit's Unity log lines there too,
    /// since BepInEx does not write Unity logs to disk by default.
    /// </summary>
    internal static class Log
    {
        private static ManualLogSource _source;
        private static bool _mirroring;

        internal static void Attach(ManualLogSource source)
        {
            _source = source;
            if (_mirroring) return;

            _mirroring = true;
            Application.logMessageReceived += MirrorUnityLog;
        }

        internal static void Info(string message)
        {
            if (_source != null) _source.LogInfo(message);
            else Debug.Log(message);
        }

        internal static void Warn(string message)
        {
            if (_source != null) _source.LogWarning(message);
            else Debug.LogWarning(message);
        }

        internal static void Error(string message)
        {
            if (_source != null) _source.LogError(message);
            else Debug.LogError(message);
        }

        private static void MirrorUnityLog(string condition, string stackTrace, LogType type)
        {
            if (_source == null || condition == null) return;
            if (condition.IndexOf("[WebView", StringComparison.Ordinal) < 0) return;

            if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
                _source.LogError(condition);
            else if (type == LogType.Warning)
                _source.LogWarning(condition);
            else
                _source.LogInfo(condition);
        }
    }
}
