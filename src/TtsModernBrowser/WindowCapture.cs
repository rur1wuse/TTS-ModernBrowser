using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using UnityEngine;

namespace TtsModernBrowser
{
    /// <summary>
    /// Fallback frame source: copies the WebView2 host window with PrintWindow into a
    /// managed texture. WebViewToolkit's Windows Graphics Capture path fails silently on
    /// some machines (the texture stays black with no native log), while PrintWindow with
    /// PW_RENDERFULLCONTENT does capture Chromium's DWM surface.
    ///
    /// The window is identified among this process's top-level windows by its exact client
    /// size; the game window itself never matches the requested pixel size.
    /// </summary>
    internal sealed class WindowCapture : IDisposable
    {
        private const uint PwRenderFullContent = 0x00000002;
        private const int MaxWindowSearchRetries = 40; // ~10 s at 4 searches/s
        private const float FrameInterval = 1f / 15f;

        private readonly int _width;
        private readonly int _height;

        private IntPtr _hwnd;
        private IntPtr _memoryDc;
        private IntPtr _bitmap;
        private IntPtr _previousBitmap;
        private IntPtr _bits;

        private Texture2D _texture;
        private float _nextFrame;
        private int _windowSearchAttempts;
        private int _failures;
        private bool _disposed;

        internal WindowCapture(int width, int height)
        {
            _width = width;
            _height = height;
        }

        internal Texture2D Texture => _texture;
        internal bool IsReady => _texture != null;

        internal bool TryInitialize()
        {
            if (_disposed) return false;

            _texture = new Texture2D(_width, _height, TextureFormat.BGRA32, false)
            {
                name = "MB_WindowCapture",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };

            var info = new BitmapInfo
            {
                header = new BitmapInfoHeader
                {
                    size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),
                    width = _width,
                    // Positive height: bottom-up rows, which is Unity's texture layout and
                    // also what the graphics-capture path produces after its row flip.
                    height = _height,
                    planes = 1,
                    bitCount = 32,
                    compression = 0,
                },
            };

            _memoryDc = CreateCompatibleDC(IntPtr.Zero);
            if (_memoryDc == IntPtr.Zero) return false;

            _bitmap = CreateDIBSection(_memoryDc, ref info, 0, out _bits, IntPtr.Zero, 0);
            if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero) return false;

            _previousBitmap = SelectObject(_memoryDc, _bitmap);
            return true;
        }

        /// <summary>Captures one frame if the interval elapsed. Cheap to call every frame.</summary>
        internal void Tick()
        {
            if (_disposed || _texture == null) return;
            if (Time.unscaledTime < _nextFrame) return;
            _nextFrame = Time.unscaledTime + FrameInterval;

            if (!IsWindow(_hwnd))
            {
                _hwnd = IntPtr.Zero;
                if (!TryResolveWindow()) return;
            }

            if (!PrintWindow(_hwnd, _memoryDc, PwRenderFullContent))
            {
                _failures++;
                if (_failures == 1)
                    Log.Warn("[MB] PrintWindow failed for the WebView2 window; window capture paused.");
                return;
            }

            _texture.LoadRawTextureData(_bits, _width * _height * 4);
            _texture.Apply(false, false);
        }

        private bool TryResolveWindow()
        {
            if (_windowSearchAttempts >= MaxWindowSearchRetries) return false;
            _windowSearchAttempts++;

            var processId = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var candidates = new List<IntPtr>();

            EnumWindows((handle, _) =>
            {
                if (!IsWindowVisible(handle)) return true;

                GetWindowThreadProcessId(handle, out var owner);
                if (owner != processId) return true;

                if (!GetClientRect(handle, out var client)) return true;
                if (client.Right - client.Left != _width || client.Bottom - client.Top != _height) return true;

                candidates.Add(handle);
                return true;
            }, IntPtr.Zero);

            if (candidates.Count == 0)
            {
                if (_windowSearchAttempts == MaxWindowSearchRetries)
                    Log.Warn($"[MB] No {_width}x{_height} window found for window capture; giving up.");
                return false;
            }

            _hwnd = candidates[0];
            Log.Info($"[MB] Window capture bound to 0x{_hwnd.ToInt64():X} ({_width}x{_height})");
            return true;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            if (_memoryDc != IntPtr.Zero)
            {
                if (_previousBitmap != IntPtr.Zero) SelectObject(_memoryDc, _previousBitmap);
                DeleteDC(_memoryDc);
                _memoryDc = IntPtr.Zero;
            }

            if (_bitmap != IntPtr.Zero)
            {
                DeleteObject(_bitmap);
                _bitmap = IntPtr.Zero;
            }

            if (_texture != null)
            {
                UnityEngine.Object.Destroy(_texture);
                _texture = null;
            }

            _hwnd = IntPtr.Zero;
            _bits = IntPtr.Zero;
        }

        // ------------------------------------------------------------------
        // Win32
        // ------------------------------------------------------------------

        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        private struct Rect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint size;
            public int width;
            public int height;
            public ushort planes;
            public ushort bitCount;
            public uint compression;
            public uint sizeImage;
            public int xPelsPerMeter;
            public int yPelsPerMeter;
            public uint colorsUsed;
            public uint colorsImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader header;
            public uint colors;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out Rect rect);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo info, uint usage,
            out IntPtr bits, IntPtr section, uint offset);

        [DllImport("gdi32.dll")]
        private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteDC(IntPtr hdc);
    }
}
