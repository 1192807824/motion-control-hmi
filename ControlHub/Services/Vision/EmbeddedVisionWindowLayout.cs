using System;
using System.Runtime.InteropServices;

namespace ControlHub.Services.Vision;

/// <summary>
/// Posts embedded-window layout requests without waiting for the vision UI thread.
/// Shared with VisionMasterHost so both processes use the same window message.
/// </summary>
internal sealed class EmbeddedVisionWindowLayout
{
    internal const int ResizeMessage = 0x8000 + 0x341;
    private IntPtr _lastWindow;
    private IntPtr _lastParent;
    private int _lastWidth;
    private int _lastHeight;

    public void Invalidate()
    {
        _lastWindow = IntPtr.Zero;
        _lastParent = IntPtr.Zero;
    }

    public bool RequestResize(IntPtr window, IntPtr parent)
    {
        if (window == IntPtr.Zero || parent == IntPtr.Zero ||
            !GetClientRect(parent, out var bounds))
        {
            return false;
        }

        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        if (_lastWindow == window && _lastParent == parent &&
            _lastWidth == width && _lastHeight == height)
        {
            return true;
        }

        // SetParent can attach the threads' input queues. SWP_ASYNCWINDOWPOS alone
        // is therefore insufficient: explicitly post, never synchronously resize here.
        if (!PostMessage(window, ResizeMessage, IntPtr.Zero, IntPtr.Zero))
        {
            Invalidate();
            return false;
        }

        _lastWindow = window;
        _lastParent = parent;
        _lastWidth = width;
        _lastHeight = height;
        return true;
    }

    // Installed only in the embedded vision window, executed by its owning thread.
    public static IntPtr HandleMessage(
        IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != ResizeMessage)
        {
            return IntPtr.Zero;
        }

        handled = true;
        var parent = GetParent(hwnd);
        if (parent != IntPtr.Zero && GetClientRect(parent, out var bounds))
        {
            // Read the CURRENT parent/size on receipt: queued requests must not
            // restore an old size after resizing or switching the display page.
            const int noZOrder = 0x0004;
            const int noActivate = 0x0010;
            _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0,
                Math.Max(1, bounds.Right - bounds.Left),
                Math.Max(1, bounds.Bottom - bounds.Top), noZOrder | noActivate);
        }

        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, int flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
