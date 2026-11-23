using System;
using System.Collections.Generic;
using UnityEngine;
using WebViewToolkit;
using WebViewToolkit.Native;

namespace TtsModernBrowser
{
    /// <summary>
    /// Scans the scene for Tablets whose URL carries the mod marker and keeps one
    /// WebView2 overlay per marked tablet. Unmarked tablets keep vanilla CEF behaviour.
    /// </summary>
    internal sealed class BrowserManager : MonoBehaviour
    {
        internal const string UrlMarker = "about:blank#mb=";
        internal const string NotesMarker = "MB1:";
        private const float ScanInterval = 0.5f;

        private static readonly List<TabletBrowser> Browsers = new List<TabletBrowser>();
        private static readonly HashSet<int> CapacityLogged = new HashSet<int>();
        private static readonly HashSet<int> FailureLogged = new HashSet<int>();
        private static readonly HashSet<int> MarkerLogged = new HashSet<int>();
        private static readonly HashSet<int> DecodeLogged = new HashSet<int>();
        private static readonly HashSet<int> FailedTablets = new HashSet<int>();

        private float _nextScan;
        private WebViewManager _webViewManager;
        private bool _webViewInitFailed;

        internal static IReadOnlyList<TabletBrowser> Active => Browsers;

        internal static TabletBrowser FindByCollider(Collider collider)
        {
            if (collider == null) return null;
            for (var i = 0; i < Browsers.Count; i++)
            {
                var browser = Browsers[i];
                if (!browser.IsDisposed && browser.Collider == collider) return browser;
            }
            return null;
        }

        private void Update()
        {
            if (_webViewManager != null)
            {
                _webViewManager.Tick();

                // Cheap: re-applies the WebView texture if the game re-assigned the
                // tablet material or the native side swapped textures after a resize.
                for (var i = 0; i < Browsers.Count; i++) Browsers[i].Refresh();
            }

            if (Time.unscaledTime < _nextScan) return;
            _nextScan = Time.unscaledTime + ScanInterval;
            Scan();
        }

        private void Scan()
        {
            var tablets = UnityEngine.Object.FindObjectsByType<TabletObject>(FindObjectsSortMode.None);
            foreach (var tablet in tablets)
            {
                if (tablet == null) continue;

                var marker = ResolveMarker(tablet);
                var existing = Find(tablet);

                if (marker != null)
                {
                    if (existing != null)
                    {
                        existing.MissedScans = 0;
                        existing.SetTargetUrl(marker);
                        existing.Refresh();
                        continue;
                    }

                    if (MarkerLogged.Add(tablet.GetInstanceID()))
                        Log.Info($"[MB] Marker on tablet {tablet.GetInstanceID()}: {marker}");

                    if (FailedTablets.Contains(tablet.GetInstanceID())) continue;

                    if (!EnsureWebViewManager()) continue;

                    var max = Mathf.Max(1, Settings.MaxBrowsers.Value);
                    if (Browsers.Count >= max)
                    {
                        if (CapacityLogged.Add(tablet.GetInstanceID()))
                            Log.Warn($"[MB] MaxBrowsers ({max}) reached; tablet {tablet.GetInstanceID()} stays on vanilla CEF.");
                        continue;
                    }

                    TryCreateBrowser(tablet, marker);
                }
                else if (existing != null)
                {
                    existing.MissedScans++;

                    if (existing.MissedScans == 1)
                        Log.Info($"[MB] Marker missing on tablet {tablet.GetInstanceID()} " +
                                 $"(grace {TabletBrowser.MissesBeforeDispose} scans): {DescribeMarker(tablet)}");

                    if (existing.MissedScans < TabletBrowser.MissesBeforeDispose) continue;

                    Log.Info($"[MB] Marker cleared on tablet {tablet.GetInstanceID()}; removing overlay. " +
                             DescribeMarker(tablet));
                    TearDown(existing);
                }
            }

            PruneDetached();
        }

        /// <summary>Raw marker evidence for the log when a tablet stops looking marked.</summary>
        private static string DescribeMarker(TabletObject tablet)
        {
            string url;
            string notes;

            try { url = tablet.browser != null ? tablet.browser.Url ?? "<null>" : "<no browser>"; }
            catch (Exception e) { url = "<error: " + e.Message + ">"; }

            try { notes = tablet.GetComponent<NetPhysObject>()?.GMNotes ?? "<null>"; }
            catch (Exception e) { notes = "<error: " + e.Message + ">"; }

            return $"Browser.url='{url}' GMNotes='{notes}'";
        }

        private void TryCreateBrowser(TabletObject tablet, string url)
        {
            TabletBrowser browser;
            string error;

            switch (TabletBrowser.TryCreate(tablet, url, Bootstrap.UserDataFolder, out browser, out error))
            {
                case TabletBrowser.CreateStatus.Created:
                    Browsers.Add(browser);
                    break;

                case TabletBrowser.CreateStatus.Retry:
                    if (FailureLogged.Add(tablet.GetInstanceID()))
                        Log.Info($"[MB] Overlay pending on tablet {tablet.GetInstanceID()}: {error}");
                    break;

                case TabletBrowser.CreateStatus.Failed:
                    FailedTablets.Add(tablet.GetInstanceID());
                    if (FailureLogged.Add(tablet.GetInstanceID()))
                        Log.Error($"[MB] Cannot create browser for tablet {tablet.GetInstanceID()}: {error}. " +
                                  "Not retrying for this tablet.");
                    break;
            }
        }

        private static void ForgetTablet(int instanceId)
        {
            CapacityLogged.Remove(instanceId);
            FailureLogged.Remove(instanceId);
            MarkerLogged.Remove(instanceId);
            DecodeLogged.Remove(instanceId);
            FailedTablets.Remove(instanceId);
        }

        private static void TearDown(TabletBrowser browser)
        {
            var tablet = browser.Tablet;
            if (tablet != null) ForgetTablet(tablet.GetInstanceID());

            browser.Dispose();
            Browsers.Remove(browser);
        }

        private void PruneDetached()
        {
            for (var i = Browsers.Count - 1; i >= 0; i--)
            {
                var browser = Browsers[i];
                if (browser.IsAlive) continue;

                var tablet = browser.Tablet;
                if (tablet != null) ForgetTablet(tablet.GetInstanceID());

                browser.Dispose();
                Browsers.RemoveAt(i);
            }
        }

        private static TabletBrowser Find(TabletObject tablet)
        {
            for (var i = 0; i < Browsers.Count; i++)
            {
                var browser = Browsers[i];
                if (!browser.IsDisposed && browser.Tablet == tablet) return browser;
            }
            return null;
        }

        private bool EnsureWebViewManager()
        {
            if (_webViewManager != null) return true;
            if (_webViewInitFailed) return false;

            try
            {
                var manager = WebViewManager.Instance;
                if (manager != null && manager.IsInitialized)
                {
                    _webViewManager = manager;
                    Log.Info("[MB] WebView2 backend attached.");
                    return true;
                }

                _webViewInitFailed = true;
                var nativeState = WebViewNative.WebViewToolkit_IsInitialized();
                Log.Error($"[MB] WebView2 backend failed to initialize (graphics API {SystemInfo.graphicsDeviceType}, native={nativeState}); overlays disabled.");
                return false;
            }
            catch (Exception e)
            {
                _webViewInitFailed = true;
                Log.Error($"[MB] WebView2 backend unavailable: {e}");
                return false;
            }
        }

        /// <summary>
        /// Reads the marker from Browser.url, falling back to GMNotes (network-synced even
        /// when the CEF URL property does not push to other clients).
        /// </summary>
        private static string ResolveMarker(TabletObject tablet)
        {
            var zfBrowser = tablet.browser;
            if (zfBrowser != null)
            {
                string url = null;
                try { url = zfBrowser.Url; }
                catch (Exception e) { Log.Warn($"[MB] Reading Browser.Url failed: {e.Message}"); }

                if (!string.IsNullOrEmpty(url) && url.StartsWith(UrlMarker, StringComparison.OrdinalIgnoreCase))
                {
                    var encoded = url.Substring(UrlMarker.Length);
                    try { return Uri.UnescapeDataString(encoded); }
                    catch (Exception)
                    {
                        if (DecodeLogged.Add(tablet.GetInstanceID()))
                            Log.Error($"[MB] Undecodable URL marker on tablet {tablet.GetInstanceID()}: {url}");
                        return null;
                    }
                }
            }

            var notes = tablet.GetComponent<NetPhysObject>()?.GMNotes;
            if (!string.IsNullOrEmpty(notes) && notes.StartsWith(NotesMarker, StringComparison.Ordinal))
            {
                var url = notes.Substring(NotesMarker.Length).Trim();
                return url.Length > 0 ? url : null;
            }

            return null;
        }

        private void OnDestroy()
        {
            for (var i = Browsers.Count - 1; i >= 0; i--) Browsers[i].Dispose();
            Browsers.Clear();

            if (_webViewManager != null)
            {
                try { _webViewManager.Shutdown(); }
                catch (Exception e) { Log.Warn($"[MB] WebView shutdown failed: {e.Message}"); }
                _webViewManager = null;
            }
        }
    }
}
