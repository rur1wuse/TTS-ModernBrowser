using System;
using System.Globalization;
using UnityEngine;
using WebViewToolkit;

namespace TtsModernBrowser
{
    /// <summary>
    /// Drives one WebView2 instance for one TTS Tablet.
    ///
    /// The overlay does not add geometry: the tablet screen's own material is pointed
    /// at the WebView texture, which keeps alignment, lighting and depth handling
    /// identical to the vanilla tablet view. The original CEF texture is put back when
    /// the tablet is unmarked. A physics-only collider on the screen supplies the
    /// pointer coordinates for input forwarding.
    /// </summary>
    internal sealed class TabletBrowser
    {
        // CEF reports about:blank#mb=... back as about:blank once the page commits, so the
        // marker legitimately blinks out. Keep the browser for ~5 s of scans before
        // treating the tablet as unmarked.
        internal const int MissesBeforeDispose = 10;

        private const string HelpersScript = @"
window.__mbInsert = function(t) {
  var el = document.activeElement;
  if (!el) return;
  if (el.isContentEditable) { document.execCommand('insertText', false, t); return; }
  if ('value' in el) {
    var s = el.selectionStart == null ? el.value.length : el.selectionStart;
    var e = el.selectionEnd == null ? s : el.selectionEnd;
    el.value = el.value.slice(0, s) + t + el.value.slice(e);
    el.selectionStart = el.selectionEnd = s + t.length;
    el.dispatchEvent(new Event('input', { bubbles: true }));
  }
};

// WebViewToolkit does not implement NewWindowRequested, so window.open and
// target=_blank would spawn a separate popup window outside the game. Keep every
// navigation inside this view instead.
(function () {
  if (window.__mbNavGuard) return;
  window.__mbNavGuard = true;

  window.open = function (url) {
    if (url) { try { location.href = url; } catch (e) { } }
    return null;
  };

  document.addEventListener('click', function (event) {
    var anchor = event.target && event.target.closest ? event.target.closest('a[href]') : null;
    if (!anchor) return;
    if (anchor.target && anchor.target !== '_self') {
      event.preventDefault();
      location.href = anchor.href;
    }
  }, true);
  document.addEventListener('submit', function (event) {
    var form = event.target;
    if (form && form.target && form.target !== '_self') form.target = '_self';
  }, true);
})();

// WebViewToolkit does not handle ContainsFullScreenElementChanged, so requestFullscreen()
// from a player does nothing visible. Emulate it from inside the page: fill the WebView
// viewport (which is the tablet screen) and answer the usual fullscreen DOM API so the
// player's own state stays consistent.
(function () {
  if (window.__mbFullscreen) return;
  window.__mbFullscreen = true;

  var pinned = null;
  var savedStyle = '';

  function post(text) {
    try { window.chrome.webview.postMessage(text); } catch (e) { }
  }

  function classOf(node) {
    if (!node || !node.className) return '';
    return typeof node.className === 'string' ? node.className : (node.className.baseVal || '');
  }

  function notifyChange() {
    document.dispatchEvent(new Event('fullscreenchange'));
    document.dispatchEvent(new Event('webkitfullscreenchange'));
  }

  function playerOf(element) {
    var node = element;
    for (var depth = 0; depth < 8 && node; depth++, node = node.parentElement) {
      if (node.querySelector && node.querySelector('video')) return node;
    }
    return element;
  }
  // Hide everything that is not on the player's own ancestor chain, so page chrome
  // (top navigation, live-room gift/anchor columns) cannot cover the video.
  var hidden = [];

  function hideSiblings(target) {
    // Start one level above the player so its own控制条 (a sibling of the <video>) survives;
    // the page chrome we want gone lives much further up.
    var node = target.parentElement || target;

    while (node && node.parentElement && parent0(node) !== null) {
      var parent = node.parentElement;

      for (var i = 0; i < parent.children.length; i++) {
        var child = parent.children[i];
        if (child === node) continue;
        if (child.tagName === 'SCRIPT' || child.tagName === 'STYLE' ||
            child.tagName === 'LINK' || child.tagName === 'HEAD' || child.tagName === 'BODY') continue;

        hidden.push([child, child.style.display]);
        child.style.setProperty('display', 'none', 'important');
      }

      node = parent;
    }

    document.documentElement.style.setProperty('overflow', 'hidden', 'important');
    document.body.style.setProperty('overflow', 'hidden', 'important');
  }

  function parent0(node) {
    return node.parentElement === document.documentElement ? null : node.parentElement;
  }

  function restoreSiblings() {
    for (var i = 0; i < hidden.length; i++) hidden[i][0].style.display = hidden[i][1];
    hidden = [];

    document.documentElement.style.removeProperty('overflow');
    document.body.style.removeProperty('overflow');
  }

  function exitFullscreen() {
    if (!pinned) return;

    restoreSiblings();
    pinned.setAttribute('style', savedStyle);
    pinned = null;

    notifyChange();
    post('MB_FS:exit');
  }

  function enterFullscreen(element) {
    var target = playerOf(element);
    if (pinned === target) { exitFullscreen(); return; }
    if (pinned) exitFullscreen();

    pinned = target;
    savedStyle = target.getAttribute('style') || '';
    target.style.cssText += ';position:fixed!important;left:0!important;top:0!important;' +
      'width:100vw!important;height:100vh!important;z-index:2147483647!important;background:#000!important;';

    hideSiblings(target);

    notifyChange();
    post('MB_FS:enter:' + target.tagName + '.' + classOf(target) + ';hidden=' + hidden.length);
  }

  // The DOM fullscreen API, answered by the emulation above.
  ['requestFullscreen', 'webkitRequestFullscreen', 'mozRequestFullScreen', 'msRequestFullscreen']
    .forEach(function (name) {
      if (!Element.prototype[name]) return;
      Element.prototype[name] = function () {
        enterFullscreen(this);
        return Promise.resolve(this);
      };
    });

  ['exitFullscreen', 'webkitExitFullscreen', 'mozCancelFullScreen', 'msExitFullscreen']
    .forEach(function (name) {
      if (!document[name]) return;
      document[name] = function () {
        exitFullscreen();
        return Promise.resolve();
      };
    });

  try {
    Object.defineProperty(document, 'fullscreenElement', { configurable: true, get: function () { return pinned; } });
    Object.defineProperty(document, 'webkitFullscreenElement', { configurable: true, get: function () { return pinned; } });
    Object.defineProperty(document, 'fullscreenEnabled', { configurable: true, get: function () { return true; } });
  } catch (e) { }

  // Fallback for players whose button only toggles a class ('ctrl-full', 'bpx-...-full').
  document.addEventListener('click', function (event) {
    var node = event.target;

    for (var depth = 0; depth < 6 && node; depth++, node = node.parentElement) {
      if (!node.getAttribute) continue;

      var label = (node.getAttribute('aria-label') || '') + ' ' +
                  (node.getAttribute('title') || '') + ' ' +
                  (node.getAttribute('data-e2e') || '') + ' ' +
                  classOf(node);

      if (!/fullscreen|full-screen|full_screen|全屏|ctrl-full|full-btn|btn-full/i.test(label)) continue;

      event.preventDefault();
      event.stopPropagation();
      enterFullscreen(node);
      return;
    }
  }, true);

  // Double-clicking the video is the other common way to enter fullscreen.
  document.addEventListener('dblclick', function (event) {
    var node = event.target;
    if (!node || node.tagName !== 'VIDEO') return;
    enterFullscreen(node);
  }, true);

  document.addEventListener('keydown', function (event) {
    if (event.key === 'Escape') exitFullscreen();
  }, true);

  // Deterministic entry point for the plugin (F11): does not depend on the site's own
  // fullscreen button wiring or on when this script was injected.
  window.__mbFullscreenToggle = function () {
    if (pinned) { exitFullscreen(); return; }

    var video = document.querySelector('video');
    enterFullscreen(video || document.body);
  };
})();

// Report the CSS viewport: Windows display scaling makes it smaller than the render size,
// and elementFromPoint() works in CSS pixels.
(function () {
  function report() {
    try {
      window.chrome.webview.postMessage('MB_VIEWPORT:' + window.innerWidth + ',' + window.innerHeight);
    } catch (e) { }
  }

  window.addEventListener('resize', report);
  report();
})();";

        private readonly TabletObject _tablet;
        private readonly WebViewInstance _webView;
        private readonly Renderer _screen;
        private Material _originalSharedMaterial;
        private Texture _originalTexture;
        private Material _material;
        private readonly GameObject _hitGo;
        private readonly BoxCollider _hitCollider;
        private readonly Vector2 _hitSize;
        private readonly ScreenUv _uv;
        private readonly int _pixelWidth;
        private readonly int _pixelHeight;

        private WindowCapture _windowCapture;
        private Vector2 _pageSize;
        private Texture _appliedTexture;
        private float _frameProbeAt;
        private bool _frameProbed;
        private string _appliedUrl;
        private bool _disposed;

        private TabletBrowser(TabletObject tablet, WebViewInstance webView, Renderer screen,
            Material originalSharedMaterial, Texture originalTexture, Material material,
            GameObject hitGo, BoxCollider hitCollider, Vector2 hitSize, ScreenUv uv,
            int pixelWidth, int pixelHeight)
        {
            _tablet = tablet;
            _webView = webView;
            _screen = screen;
            _originalSharedMaterial = originalSharedMaterial;
            _originalTexture = originalTexture;
            _material = material;
            _hitGo = hitGo;
            _hitCollider = hitCollider;
            _hitSize = hitSize;
            _uv = uv;
            _pixelWidth = pixelWidth;
            _pixelHeight = pixelHeight;
            MissedScans = 0;
        }

        /// <summary>Texture currently shown on the tablet: graphics capture or window capture.</summary>
        private Texture ActiveTexture =>
            _windowCapture != null && _windowCapture.Texture != null ? _windowCapture.Texture : _webView.Texture;

        internal TabletObject Tablet => _tablet;
        internal WebViewInstance WebView => _webView;
        internal Transform HitTransform => _hitCollider != null ? _hitCollider.transform : null;
        internal Vector2 HitSize => _hitSize;
        internal Vector2 PixelSize => new Vector2(_pixelWidth, _pixelHeight);

        /// <summary>
        /// CSS viewport of the page (window.innerWidth/innerHeight). It differs from the render
        /// size whenever Windows display scaling is not 100%, and elementFromPoint works in
        /// these units, so in-page clicks must use them.
        /// </summary>
        internal Vector2 PageSize => _pageSize.x >= 1f && _pageSize.y >= 1f ? _pageSize : PixelSize;
        internal ScreenUv Uv => _uv;
        internal Transform ScreenTransform => _screen != null ? _screen.transform : null;
        internal BoxCollider Collider => _hitCollider;
        internal int MissedScans { get; set; }

        internal bool IsDisposed => _disposed || _screen == null;
        internal bool IsAlive => !IsDisposed && _tablet != null;

        internal enum CreateStatus
        {
            Created,
            Retry,
            Failed
        }

        internal static CreateStatus TryCreate(TabletObject tablet, string url, string userDataFolder,
            out TabletBrowser browser, out string error)
        {
            browser = null;
            error = null;

            var zfBrowser = tablet != null ? tablet.browser : null;
            if (zfBrowser == null)
            {
                error = "tablet has no Browser component yet";
                return CreateStatus.Retry;
            }

            // TTS pops a "Tablet wants to open URL" confirmation on every URL change; the
            // marker is written on purpose, so auto-confirm it for managed tablets.
            try
            {
                // Static in TTS: applies to every tablet this client shows.
                if (!TabletObject.AutoconfirmURLChange)
                {
                    TabletObject.AutoconfirmURLChange = true;
                    Log.Info("[MB] tablet URL changes will not prompt for confirmation");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[MB] could not disable the URL prompt: {e.Message}");
            }

            var screen = ResolveRenderer(zfBrowser);
            if (screen == null)
            {
                error = "screen renderer not found";
                return CreateStatus.Retry;
            }

            // The mesh bounds live in the renderer's space and always describe the quad,
            // whereas world bounds collapse an axis for a rotated screen.
            var meshBounds = screen.localBounds;
            var meshSize = meshBounds.size;
            float width, height;
            bool planeIsXZ;

            if (meshSize.y >= 0.001f)
            {
                width = meshSize.x;
                height = meshSize.y;
                planeIsXZ = false;
            }
            else if (meshSize.z >= 0.001f)
            {
                width = meshSize.x;
                height = meshSize.z;
                planeIsXZ = true;
            }
            else
            {
                error = $"degenerate mesh bounds {meshSize}";
                return CreateStatus.Retry;
            }

            if (width < 0.001f || height < 0.001f)
            {
                error = $"degenerate screen size ({width:F4} x {height:F4}) from mesh {meshSize}";
                return CreateStatus.Retry;
            }

            // TTS meshes are unit-free primitives scaled by the object, so the render
            // aspect must come from the world size.
            var lossy = screen.transform.lossyScale;
            var worldWidth = width * Mathf.Abs(lossy.x);
            var worldHeight = height * (planeIsXZ ? Mathf.Abs(lossy.z) : Mathf.Abs(lossy.y));
            if (worldWidth < 0.0001f || worldHeight < 0.0001f)
            {
                error = $"degenerate world size ({worldWidth:F4} x {worldHeight:F4})";
                return CreateStatus.Retry;
            }

            var pixelWidth = Mathf.Max(320, Settings.DefaultWidth.Value);
            var pixelHeight = Mathf.Clamp(Mathf.RoundToInt(pixelWidth * worldHeight / worldWidth), 480, 1600);

            WebViewInstance webView;
            try
            {
                webView = WebViewManager.Instance.CreateWebView(pixelWidth, pixelHeight, url, false, userDataFolder);
            }
            catch (Exception e)
            {
                error = $"CreateWebView threw: {e.Message}";
                return CreateStatus.Failed;
            }

            if (webView == null || webView.Texture == null)
            {
                webView?.Dispose();
                error = "CreateWebView returned no texture";
                return CreateStatus.Failed;
            }

            var hitGo = BuildHitCollider(screen, meshBounds.center, width, height, planeIsXZ);
            var originalSharedMaterial = screen.sharedMaterial;
            var originalTexture = originalSharedMaterial != null ? originalSharedMaterial.mainTexture : null;
            var material = screen.material; // per-object instance; the shared material keeps the CEF texture

            var meshFilter = screen.GetComponent<MeshFilter>();
            var uv = ScreenUv.FromMesh(meshFilter != null ? meshFilter.sharedMesh : null);
            Log.Info($"[MB] Screen UV map: {(uv.Valid ? "fitted from mesh" : "unavailable, plane heuristic in use")}");

            var result = new TabletBrowser(tablet, webView, screen, originalSharedMaterial, originalTexture, material,
                hitGo, hitGo.GetComponent<BoxCollider>(), new Vector2(width, height), uv, pixelWidth, pixelHeight);

            result._appliedUrl = url;

            if (Settings.WindowCaptureForced) result.UseWindowCapture();
            result.ApplyTexture();
            result.HookNavigation();

            Log.Info($"[MB] Browser created: {pixelWidth}x{pixelHeight} for a {worldWidth:F3}x{worldHeight:F3} tablet screen " +
                     $"(capture={result.CaptureName}, url={url})");
            browser = result;
            return CreateStatus.Created;
        }

        /// <summary>Picks the tablet's screen quad: the renderer showing the CEF texture.</summary>
        private static Renderer ResolveRenderer(ZenFulcrum.EmbeddedBrowser.Browser zfBrowser)
        {
            Texture texture = null;
            try { texture = zfBrowser.Texture; } catch (Exception) { /* native side not ready yet */ }

            // Without the CEF texture the screen quad cannot be identified, and picking an
            // arbitrary child would retexture the tablet body for good. Report "not ready"
            // and let the next scan retry.
            if (texture == null) return zfBrowser.GetComponent<Renderer>();

            foreach (var candidate in zfBrowser.GetComponentsInChildren<Renderer>(true))
            {
                var material = candidate.sharedMaterial;
                if (material != null && material.mainTexture == texture) return candidate;
            }

            return zfBrowser.GetComponent<Renderer>();
        }
        private static GameObject BuildHitCollider(Renderer screen, Vector3 centre, float width, float height, bool planeIsXZ)
        {
            var go = new GameObject("MB_Hit");
            go.layer = 2; // Ignore Raycast: TTS pointer raycasts skip it, our overlay mask (1 << 2) hits it.

            var t = go.transform;
            t.SetParent(screen.transform, false);
            t.localPosition = centre;
            t.localRotation = planeIsXZ ? Quaternion.Euler(90f, 0f, 0f) : Quaternion.identity;
            t.localScale = Vector3.one;

            var box = go.AddComponent<BoxCollider>();
            box.size = new Vector3(width, height, 0.02f);
            return go;
        }

        private void ApplyTexture()
        {
            if (_material == null) return;

            var texture = ActiveTexture;
            if (texture == null) return;

            _material.mainTexture = texture;

            if (Settings.FlipTexture.Value && _material.HasProperty("_MainTex"))
            {
                _material.mainTextureScale = new Vector2(1f, -1f);
                _material.mainTextureOffset = new Vector2(0f, 1f);
            }

            if (texture == _appliedTexture) return;

            _appliedTexture = texture;
            Log.Info($"[MB] Screen material shows '{texture.name}' (was '{(_originalTexture != null ? _originalTexture.name : "none")}')");
        }

        private void HookNavigation()
        {
            _webView.NavigationCompleted += (url, success) =>
            {
                if (_disposed) return;

                Log.Info($"[MB] Navigation completed: {url} success={success}");
                _webView.ExecuteScript(HelpersScript);
                _webView.ExecuteScript(
                    "try{window.chrome.webview.postMessage('MB_UA:'+navigator.userAgent)}catch(e){}");
                if (success) _frameProbeAt = Time.unscaledTime + 2f;
                else Log.Warn($"[MB] Navigation failed: {url}");
            };

            // WebViewToolkit forwards window.chrome.webview.postMessage payloads here.
            // The user agent line is the quickest way to tell which engine a client runs.
            _webView.MessageReceived += message =>
            {
                if (message == null) return;

                if (message.StartsWith("MB_UA:", StringComparison.Ordinal))
                    Log.Info($"[MB] Page engine: {message.Substring("MB_UA:".Length)}");
                else if (message.StartsWith("MB_CLICK:", StringComparison.Ordinal))
                    Log.Info($"[MB] in-page click target: {message.Substring("MB_CLICK:".Length)}");
                else if (message.StartsWith("MB_FS:", StringComparison.Ordinal))
                    Log.Info($"[MB] fullscreen: {message.Substring("MB_FS:".Length)}");
                else if (message.StartsWith("MB_VIEWPORT:", StringComparison.Ordinal))
                    ApplyViewport(message.Substring("MB_VIEWPORT:".Length));
            };

            _webView.ExecuteScript(HelpersScript);
        }

        private void ApplyViewport(string payload)
        {
            var parts = payload.Split(',');
            if (parts.Length != 2) return;

            if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var width)) return;
            if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var height)) return;
            if (width < 1f || height < 1f) return;

            var size = new Vector2(width, height);
            if (size == _pageSize) return;

            _pageSize = size;
            Log.Info($"[MB] page viewport: {(int)width}x{(int)height} (render {_pixelWidth}x{_pixelHeight})");
        }

        internal void SetTargetUrl(string url)
        {
            if (_disposed || string.IsNullOrEmpty(url) || url == _appliedUrl) return;

            // Only remember the URL once the native side accepted it, otherwise a transient
            // failure would make the equality check above skip every later retry.
            if (!_webView.Navigate(url))
            {
                Log.Warn($"[MB] Navigate failed: {url}");
                return;
            }

            _appliedUrl = url;
            Log.Info($"[MB] Navigating to {url}");
        }

        /// <summary>
        /// Per-scan housekeeping. TTS re-assigns the tablet material on some state
        /// changes, and WebViewInstance swaps its texture after a native resize, so the
        /// current WebView texture is re-applied whenever it differs.
        /// </summary>
        internal void Refresh()
        {
            if (_disposed || _screen == null) return;

            var texture = ActiveTexture;
            if (texture == null) return;

            // TTS can hand the tablet a fresh material (state changes, re-enabling the
            // screen); pick that up so the browser texture keeps being the one drawn.
            var current = _screen.sharedMaterial;
            if (current != _material)
            {
                if (current != null)
                {
                    _originalSharedMaterial = current;
                    if (current.mainTexture != null && current.mainTexture != texture)
                        _originalTexture = current.mainTexture;
                }

                _material = _screen.material;
                _appliedTexture = null;
                ApplyTexture();
                return;
            }

            if (_windowCapture != null) _windowCapture.Tick();

            texture = ActiveTexture;
            if (_material != null && texture != null && _material.mainTexture != texture) ApplyTexture();

            if (!_frameProbed && _frameProbeAt > 0f && Time.unscaledTime >= _frameProbeAt)
                ProbeFrames(texture);
        }

        /// <summary>
        /// One-shot check that frames actually arrive: blits the WebView texture into a
        /// small render texture and reports whether any non-black pixel exists. A black
        /// result means Windows Graphics Capture delivered nothing (exclusive full-screen,
        /// occluded/minimized capture window, or a driver issue).
        /// </summary>
        private void ProbeFrames(Texture texture)
        {
            _frameProbed = true;

            RenderTexture target = null;
            Texture2D readback = null;
            var previous = RenderTexture.active;

            try
            {
                target = RenderTexture.GetTemporary(8, 8, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
                Graphics.Blit(texture, target);
                RenderTexture.active = target;

                readback = new Texture2D(8, 8, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0f, 0f, 8f, 8f), 0, 0);
                readback.Apply();

                var pixels = readback.GetPixels();
                var total = 0f;
                var brightest = 0f;
                foreach (var pixel in pixels)
                {
                    var luma = pixel.r + pixel.g + pixel.b;
                    total += luma;
                    if (luma > brightest) brightest = luma;
                }

                var average = total / (pixels.Length * 3f);
                if (brightest < 0.01f)
                {
                    Log.Warn($"[MB] {CaptureName} produced a black frame.");

                    // Graphics capture can fail silently on some machines; PrintWindow reads
                    // the same window through DWM and does work there.
                    if (_windowCapture == null && Settings.CaptureFallbackAllowed && UseWindowCapture())
                    {
                        _frameProbed = false;
                        _frameProbeAt = Time.unscaledTime + 2f;
                        return;
                    }
                }
                else
                {
                    Log.Info($"[MB] Frame check ({CaptureName}): average={average:F3} brightest={brightest:F3} (frames present)");
                }
            }
            catch (Exception e)
            {
                Log.Warn($"[MB] WebView frame check failed: {e.Message}");
            }
            finally
            {
                RenderTexture.active = previous;
                if (target != null) RenderTexture.ReleaseTemporary(target);
                if (readback != null) UnityEngine.Object.Destroy(readback);
            }
        }

        private string CaptureName => _windowCapture != null ? "window capture" : "graphics capture";

        /// <summary>Switches this tablet to PrintWindow frames; returns false if it cannot start.</summary>
        private bool UseWindowCapture()
        {
            if (_windowCapture != null) return _windowCapture.IsReady;

            var capture = new WindowCapture(_pixelWidth, _pixelHeight);
            if (!capture.TryInitialize())
            {
                capture.Dispose();
                Log.Error("[MB] Window capture could not initialize.");
                return false;
            }

            _windowCapture = capture;
            _appliedTexture = null;
            ApplyTexture();
            Log.Info($"[MB] Frame source switched to window capture ({_pixelWidth}x{_pixelHeight}).");
            return true;
        }
        internal void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            // Put the game's own material back on the screen and drop the clone we created,
            // so later theme/shared-material changes reach the tablet again.
            if (_screen != null)
            {
                if (_originalSharedMaterial != null) _screen.sharedMaterial = _originalSharedMaterial;
                else if (_material != null && _originalTexture != null) _material.mainTexture = _originalTexture;
            }

            if (_material != null) UnityEngine.Object.Destroy(_material);
            _material = null;

            if (_hitGo != null) UnityEngine.Object.Destroy(_hitGo);

            if (_windowCapture != null)
            {
                _windowCapture.Dispose();
                _windowCapture = null;
            }

            try { _webView?.Dispose(); }
            catch (Exception e) { Log.Warn($"[MB] WebView dispose failed: {e.Message}"); }
        }
    }
}
