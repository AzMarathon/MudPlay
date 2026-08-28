using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;

namespace MudPlay.Services;

// Windows reports a window's Position / FrameSize including the invisible DWM resize
// border (~7px per side on Win10/11), so two windows snapped "frame-flush" show a
// visible gap of roughly twice that border (report paradigm-20260827-062950). The
// DWMWA_EXTENDED_FRAME_BOUNDS attribute returns the VISIBLE rectangle in physical
// pixels; comparing it to the reported Position yields the per-window inset the snap
// geometry needs so it aligns visible edges — not frame edges — flush.
//
// No-op off Windows and on any failure (returns false), so callers fall back to the
// frame rect unchanged: Linux/macOS FrameSize is already the visible frame, so only
// Windows needs this. Sanity-bounded so a stale handle or an odd DWM result can never
// snap to a wrong edge — it just falls back.
internal static class NativeWindowFrame
{
    // On success, `inset` is the visible rect's top-left offset from the window's
    // reported Position and `visibleSize` is the visible rect's size, both physical
    // pixels. The inset is position-independent (a border width), so a caller can
    // apply it to a hypothetical position, not just the window's current one.
    public static bool TryGetVisibleFrame(Window window, out PixelPoint inset, out PixelSize visibleSize)
    {
        inset = default;
        visibleSize = default;
        if (!OperatingSystem.IsWindows()) return false;

        nint hwnd = window.TryGetPlatformHandle()?.Handle ?? nint.Zero;
        if (hwnd == nint.Zero) return false;

        try
        {
            if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                    out RECT visible, Marshal.SizeOf<RECT>()) != 0)
                return false;

            PixelPoint pos = window.Position;
            int left = visible.Left - pos.X;
            int top = visible.Top - pos.Y;
            int width = visible.Right - visible.Left;
            int height = visible.Bottom - visible.Top;

            // A real inset is a small non-negative border and the visible rect is
            // positive; anything outside that is a bad read (none/stale handle, a
            // maximized quirk) → fall back to the frame rect rather than snap wrong.
            if (left < 0 || top < 0 || left > 64 || top > 64 || width <= 0 || height <= 0)
                return false;

            inset = new PixelPoint(left, top);
            visibleSize = new PixelSize(width, height);
            return true;
        }
        catch (DllNotFoundException) { return false; }
        catch (EntryPointNotFoundException) { return false; }
    }

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // Classic DllImport (not the source-generated LibraryImport) so the project
    // doesn't need AllowUnsafeBlocks for one Windows-only helper. Never invoked off
    // Windows — the caller guards on OperatingSystem.IsWindows() first.
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out RECT value, int size);
}
