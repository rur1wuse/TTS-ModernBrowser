using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using WebViewToolkit.Native;

namespace TtsModernBrowser
{
    /// <summary>
    /// Desktop mouse/keyboard forwarding into the focused overlay, plus the global
    /// "pointer is over a page" flags that InputGuard uses to mute TTS input.
    /// </summary>
    internal sealed class BrowserInput : MonoBehaviour
    {
        private const int OverlayLayerMask = 1 << 2; // layer 2 = Ignore Raycast, used only by our BoxCollider
        private const float RayLength = 100f;

        private static readonly bool[] ButtonsDown = new bool[3];
        private static readonly bool[] ClickViaPagePending = new bool[3];
        private static readonly TabletBrowser[] ButtonTargets = new TabletBrowser[3];
        private static readonly Vector2[] ButtonPoints = new Vector2[3];
        private static readonly HashSet<MouseEventType> RejectedTypes = new HashSet<MouseEventType>();

        private static TabletBrowser _hovered;
        private static TabletBrowser _focused;
        private static Vector2 _lastPointer = new Vector2(-1f, -1f);
        private static Vector2 _lastGoodMouse;
        private static int _lastHoverFrame = -1000;
        private static bool _degradedLogged;
        private static bool _pointerSent;
        private static bool _lastOverPage;
        private static bool _lastUiHover;
        private static bool _lastUiTyping;
        private static bool _nativeMouseRejected;
        private static int _entryLogs;
        private static int _dispatchLogs;
        private static int _clickLogs;
        private static float _lastLeftDownTime = -10f;
        private static TabletBrowser _lastLeftDownTarget;

        internal static bool PointerOverPage { get; private set; }
        internal static bool KeyboardFocus { get; private set; }
        internal static bool UiHover { get; private set; }
        internal static bool UiTyping { get; private set; }

        private void Update()
        {
            UiHover = DetectUiHover();
            UiTyping = DetectUiTyping();

            if (_focused != null && _focused.IsDisposed)
            {
                _focused = null;
                KeyboardFocus = false;
            }

            TabletBrowser target = null;
            float u = 0f, v = 0f;

            var previousHover = _hovered;
            var previousPoint = _lastPointer;

            // Pick unconditionally: UICamera.hoveredObject is non-null on almost every frame in
            // TTS (including while clicking the table), so gating on it suppressed all input.
            Pick(out target, out u, out v);

            if (target != null) _lastHoverFrame = Time.frameCount;

            if (target != _hovered)
            {
                if (_hovered != null && !_hovered.IsDisposed && _pointerSent)
                    Send(_hovered, MouseEventType.Leave, MouseButton.None, _lastPointer.x, _lastPointer.y);

                _pointerSent = false;
                _hovered = target;
            }

            PointerOverPage = target != null;

            if (PointerOverPage != _lastOverPage || UiHover != _lastUiHover || UiTyping != _lastUiTyping)
            {
                _lastOverPage = PointerOverPage;
                _lastUiHover = UiHover;
                _lastUiTyping = UiTyping;
                Log.Info($"[MB] input: overPage={PointerOverPage} uiHover={UiHover} uiTyping={UiTyping} " +
                         $"keyboardFocus={KeyboardFocus}");
            }

            if (target != null)
            {
                var pointer = new Vector2(u, v);
                if (!_pointerSent || pointer != _lastPointer)
                {
                    if (!_pointerSent && _entryLogs++ < 5)
                        Log.Info($"[MB] pointer entered page at ({u:F3}, {v:F3})");

                    Send(target, MouseEventType.Move, MouseButton.None, u, v);
                    _lastPointer = pointer;
                    _pointerSent = true;
                }

                // Keep the release point in step with the drag while it stays on this page.
                for (var i = 0; i < 3; i++)
                    if (ButtonsDown[i] && ButtonTargets[i] == target) ButtonPoints[i] = pointer;
            }

            HandleButtons(target, u, v, previousHover, previousPoint);

            // Keys go to the page only while it owns the keyboard focus and no TTS input
            // field is active - the same condition InputGuard uses to mute TTS hotkeys.
            if (KeyboardFocus && _focused != null && !_focused.IsDisposed && !UiTyping)
                ForwardKeyboard(_focused);

            if (target != null)
            {
                var scroll = Input.mouseScrollDelta.y;
                if (Mathf.Abs(scroll) > 0.001f)
                    Send(target, MouseEventType.Wheel, MouseButton.None, u, v, scroll);

                // Fullscreen toggle that does not depend on the page owning keyboard focus or
                // on the site's own button wiring.
                if (Input.GetKeyDown(KeyCode.F11))
                {
                    target.WebView.ExecuteScript("window.__mbFullscreenToggle && window.__mbFullscreenToggle()");
                    Log.Info("[MB] F11: toggled page fullscreen");
                }
            }
        }

        // ------------------------------------------------------------------
        // Picking
        // ------------------------------------------------------------------

        private static void Pick(out TabletBrowser browser, out float u, out float v)
        {
            browser = null;
            u = 0f;
            v = 0f;

            var camera = ResolveCamera();
            if (camera == null) return;

            // TTS warps/hides the hardware cursor around clicks; on some frames Unity then
            // reports the position as the origin, which would make the ray miss the tablet
            // exactly when the button goes down. Reuse the last usable position instead.
            var mousePosition = SanitizedMousePosition();
            var ray = camera.ScreenPointToRay(mousePosition);

            var hits = Physics.RaycastAll(ray, RayLength, OverlayLayerMask);
            if (hits.Length > 0)
            {
                var nearest = hits[0];
                for (var i = 1; i < hits.Length; i++)
                    if (hits[i].distance < nearest.distance) nearest = hits[i];

                var found = BrowserManager.FindByCollider(nearest.collider);
                if (found != null && TryToUv(found, nearest.point, out u, out v))
                {
                    browser = found;
                    return;
                }
            }

            // Fallback: intersect the pointer ray with each screen plane analytically. This
            // keeps input working even when the physics collider is missing, on an unexpected
            // layer, or rejected by the raycast layer mask.
            PickAnalytic(ray, out browser, out u, out v);
        }

        private static Vector2 SanitizedMousePosition()
        {
            var position = (Vector2)Input.mousePosition;
            var width = Screen.width;
            var height = Screen.height;

            var outOfBounds = position.x < 0f || position.y < 0f ||
                              position.x > width + 1f || position.y > height + 1f;
            var degenerate = position.sqrMagnitude < 1f;

            if ((degenerate || outOfBounds) && _lastGoodMouse.sqrMagnitude >= 1f)
            {
                if (!_degradedLogged)
                {
                    _degradedLogged = true;
                    Log.Info($"[MB] cursor position reported as {position}; reusing {_lastGoodMouse} for picking.");
                }

                return _lastGoodMouse;
            }

            _lastGoodMouse = position;
            return position;
        }

        private static void PickAnalytic(Ray ray, out TabletBrowser browser, out float u, out float v)
        {
            browser = null;
            u = 0f;
            v = 0f;

            var bestDistance = float.MaxValue;
            var active = BrowserManager.Active;

            for (var i = 0; i < active.Count; i++)
            {
                var candidate = active[i];
                if (candidate == null || candidate.IsDisposed) continue;

                var hitTransform = candidate.HitTransform;
                if (hitTransform == null) continue;

                var plane = new Plane(hitTransform.forward, hitTransform.position);
                if (!plane.Raycast(ray, out var enter) || enter <= 0f || enter >= bestDistance) continue;

                if (!TryToUv(candidate, ray.GetPoint(enter), out var candidateU, out var candidateV)) continue;

                bestDistance = enter;
                browser = candidate;
                u = candidateU;
                v = candidateV;
            }
        }

        private static bool TryToUv(TabletBrowser browser, Vector3 worldPoint, out float u, out float v)
        {
            u = 0f;
            v = 0f;

            var hitTransform = browser.HitTransform;
            if (hitTransform == null) return false;

            // Preferred path: the screen mesh's own UVs, so the pointer lands exactly where the
            // shader samples the texture regardless of how the asset is laid out.
            var screenTransform = browser.ScreenTransform;
            if (browser.Uv.Valid && screenTransform != null)
            {
                var screenLocal = screenTransform.InverseTransformPoint(worldPoint);
                if (browser.Uv.TryMap(screenLocal, out var meshU, out var meshV))
                {
                    u = Mathf.Clamp01(meshU);
                    v = Settings.FlipTexture.Value ? Mathf.Clamp01(meshV) : 1f - Mathf.Clamp01(meshV);
                    return true;
                }
            }

            var size = browser.HitSize;
            if (size.x <= 0f || size.y <= 0f) return false;

            var local = hitTransform.InverseTransformPoint(worldPoint);
            var rawU = local.x / size.x + 0.5f;
            var rawV = local.y / size.y + 0.5f;
            if (rawU < 0f || rawU > 1f || rawV < 0f || rawV > 1f) return false;

            u = rawU;
            v = Settings.FlipTexture.Value ? rawV : 1f - rawV;
            return true;
        }

        private static Camera ResolveCamera()
        {
            var camera = Camera.main;
            if (camera != null) return camera;

            var cameras = Camera.allCameras;
            for (var i = 0; i < cameras.Length; i++)
                if (cameras[i] != null && cameras[i].enabled) return cameras[i];

            return null;
        }

        // ------------------------------------------------------------------
        // Mouse buttons
        // ------------------------------------------------------------------

        private static void HandleButtons(TabletBrowser target, float u, float v,
            TabletBrowser previousTarget, Vector2 previousPoint)
        {
            for (var i = 0; i < 3; i++)
            {
                var button = i == 0 ? MouseButton.Left : i == 1 ? MouseButton.Right : MouseButton.Middle;

                if (Input.GetMouseButtonDown(i))
                {
                    // The press frame itself can lose the hit (TTS warps the cursor, or the
                    // press makes an NGUI element appear), so a press within a few frames of
                    // hovering the page still counts as a press on the page.
                    var pressTarget = target;
                    var pressU = u;
                    var pressV = v;

                    if (pressTarget == null && previousTarget != null &&
                        Time.frameCount - _lastHoverFrame <= 3)
                    {
                        pressTarget = previousTarget;
                        pressU = previousPoint.x;
                        pressV = previousPoint.y;
                    }

                    ButtonsDown[i] = true;
                    ButtonTargets[i] = pressTarget;
                    ButtonPoints[i] = new Vector2(pressU, pressV);

                    // Double-clicking the page toggles fullscreen. Doing it here (instead of
                    // relying on the site's button or on a hotkey TTS may swallow) makes the
                    // gesture work on any player and any page.
                    var isDoubleClick = false;
                    if (i == 0 && pressTarget != null)
                    {
                        var now = Time.unscaledTime;
                        isDoubleClick = _lastLeftDownTarget == pressTarget && now - _lastLeftDownTime <= 0.35f;
                        _lastLeftDownTime = isDoubleClick ? -10f : now;
                        _lastLeftDownTarget = pressTarget;
                    }

                    if (isDoubleClick)
                    {
                        ClickViaPagePending[i] = false;
                        pressTarget.WebView.ExecuteScript("window.__mbFullscreenToggle && window.__mbFullscreenToggle()");
                        Log.Info("[MB] double-click on page: toggled fullscreen");
                        continue;
                    }

                    // WebView2 refuses synthesised mouse input on some machines (any rejected
                    // event in this session is the signal). Once that happens, clicks are
                    // delivered from inside the page instead of through the native layer.
                    var inPage = pressTarget != null && _nativeMouseRejected;
                    var delivered = false;

                    if (inPage)
                    {
                        ClickViaPagePending[i] = true;
                    }
                    else if (pressTarget != null)
                    {
                        delivered = Send(pressTarget, MouseEventType.Down, button, pressU, pressV);
                        ClickViaPagePending[i] = !delivered;
                    }

                    if (_dispatchLogs++ < 40)
                        Log.Info($"[MB] {button} down at ({pressU:F3}, {pressV:F3}); " +
                                 $"target={(pressTarget != null ? "page" : "none")}; " +
                                 $"native={(inPage ? "skipped" : delivered ? "ok" : "rejected")}; " +
                                 $"mouse={Input.mousePosition} uiHover={UiHover}");

                    if (i == 0)
                    {
                        _focused = pressTarget;
                        KeyboardFocus = pressTarget != null;
                    }
                }

                if (Input.GetMouseButtonUp(i))
                {
                    var captured = ButtonTargets[i];
                    if (captured != null && !captured.IsDisposed)
                    {
                        var point = ButtonPoints[i];
                        if (ClickViaPagePending[i]) ClickViaPage(captured, button, point.x, point.y);
                        else Send(captured, MouseEventType.Up, button, point.x, point.y);
                    }

                    ButtonsDown[i] = false;
                    ClickViaPagePending[i] = false;
                    ButtonTargets[i] = null;
                }
            }
        }

        /// <summary>Forwards a mouse event; returns false when the native layer rejected it.</summary>
        private static bool Send(TabletBrowser browser, MouseEventType type, MouseButton button,
            float u, float v, float wheel = 0f)
        {
            if (browser == null || browser.IsDisposed) return false;
            if (browser.WebView.SendMouseEvent(type, button, u, v, wheel)) return true;

            _nativeMouseRejected = true;

            if (RejectedTypes.Add(type))
                Log.Warn($"[MB] SendMouseEvent({type}) was rejected by the native layer; " +
                         "falling back to in-page input for clicks and scrolling.");

            return false;
        }

        /// <summary>
        /// Fallback for machines where WebView2 refuses synthesised mouse input: click the
        /// element under the pointer from inside the page instead.
        /// </summary>
        private static void ClickViaPage(TabletBrowser browser, MouseButton button, float u, float v)
        {
            // elementFromPoint works in CSS pixels; with Windows display scaling the CSS
            // viewport is smaller than the render size, and using the render size put clicks
            // outside the document (bottom of the player silently did nothing).
            var size = browser.PageSize;
            if (size.x < 1f || size.y < 1f) return;

            var x = Mathf.RoundToInt(Mathf.Clamp01(u) * (size.x - 1f));
            var y = Mathf.RoundToInt(Mathf.Clamp01(v) * (size.y - 1f));
            var domButton = button == MouseButton.Right ? 2 : button == MouseButton.Middle ? 1 : 0;

            var script =
                "(function(){var x=" + x + ",y=" + y + ";" +
                "var e=document.elementFromPoint(x,y);" +
                "if(!e){window.chrome.webview.postMessage('MB_CLICK:none:'+x+','+y);return;}" +
                "var o={bubbles:true,cancelable:true,clientX:x,clientY:y,button:" + domButton + "};" +
                "try{e.dispatchEvent(new MouseEvent('mousedown',o));e.dispatchEvent(new MouseEvent('mouseup',o));" +
                (domButton == 0 ? "e.click();" : "") +
                "window.chrome.webview.postMessage('MB_CLICK:'+x+','+y+':'+e.tagName+'.'+e.className);" +
                "}catch(err){}})()";

            var dispatched = browser.WebView.ExecuteScript(script);

            if (_clickLogs++ < 40)
                Log.Info($"[MB] dispatched an in-page click at ({x},{y}) of {size.x:F0}x{size.y:F0} (script={dispatched})");
        }

        // ------------------------------------------------------------------
        // Keyboard
        // ------------------------------------------------------------------

        private static bool CtrlHeld => Input.GetKey(KeyCode.LeftControl) || Input.GetKey(KeyCode.RightControl);

        private static void ForwardKeyboard(TabletBrowser target)
        {
            foreach (var c in Input.inputString)
            {
                switch (c)
                {
                    case '\b': SendKey(target, 0x08); break;
                    case '\n':
                    case '\r': SendKey(target, 0x0D); break;
                    case '\t': SendKey(target, 0x09); break;
                    default: InsertText(target, c.ToString()); break;
                }
            }

            if (CtrlHeld)
            {
                if (Input.GetKeyDown(KeyCode.A)) target.WebView.ExecuteScript("document.execCommand('selectAll')");
                if (Input.GetKeyDown(KeyCode.C)) target.WebView.ExecuteScript("document.execCommand('copy')");
                if (Input.GetKeyDown(KeyCode.X)) target.WebView.ExecuteScript("document.execCommand('cut')");
                if (Input.GetKeyDown(KeyCode.Z)) target.WebView.ExecuteScript("document.execCommand('undo')");
                if (Input.GetKeyDown(KeyCode.Y)) target.WebView.ExecuteScript("document.execCommand('redo')");
                if (Input.GetKeyDown(KeyCode.V)) InsertText(target, GUIUtility.systemCopyBuffer);
                return; // Ctrl combos never double as text
            }

            if (Input.GetKeyDown(KeyCode.Escape)) SendKey(target, 0x1B);
            if (Input.GetKeyDown(KeyCode.Delete)) SendKey(target, 0x2E);
            if (Input.GetKeyDown(KeyCode.UpArrow)) SendKey(target, 0x26);
            if (Input.GetKeyDown(KeyCode.DownArrow)) SendKey(target, 0x28);
            if (Input.GetKeyDown(KeyCode.LeftArrow)) SendKey(target, 0x25);
            if (Input.GetKeyDown(KeyCode.RightArrow)) SendKey(target, 0x27);
            if (Input.GetKeyDown(KeyCode.Home)) SendKey(target, 0x24);
            if (Input.GetKeyDown(KeyCode.End)) SendKey(target, 0x23);
            if (Input.GetKeyDown(KeyCode.PageUp)) SendKey(target, 0x21);
            if (Input.GetKeyDown(KeyCode.PageDown)) SendKey(target, 0x22);
            if (Input.GetKeyDown(KeyCode.F5)) SendKey(target, 0x74);
        }

        private static void SendKey(TabletBrowser target, uint virtualKey)
        {
            target.WebView.SendKeyEvent(virtualKey, 0, true);
            target.WebView.SendKeyEvent(virtualKey, 0, false);
        }

        private static void InsertText(TabletBrowser target, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            target.WebView.ExecuteScript("window.__mbInsert && window.__mbInsert(" + JsLiteral.Quote(text) + ");");
        }

        // ------------------------------------------------------------------
        // TTS UI detection (NGUI Custom UI + uGUI fallback)
        // ------------------------------------------------------------------

        private static bool DetectUiHover()
        {
            // Only NGUI counts. TTS ships full-screen uGUI graphics for some screens, and
            // EventSystem.IsPointerOverGameObject() then reports "over UI" everywhere, which
            // would swallow every pointer event before it reaches the page.
            try
            {
                return UICamera.hoveredObject != null;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static bool DetectUiTyping()
        {
            try
            {
                var selected = UICamera.selectedObject;
                if (selected != null && selected.GetComponent<UIInput>() != null) return true;
            }
            catch (Exception) { }

            var eventSystem = EventSystem.current;
            if (eventSystem == null) return false;

            var current = eventSystem.currentSelectedGameObject;
            return current != null && current.GetComponent<InputField>() != null;
        }
    }
}
