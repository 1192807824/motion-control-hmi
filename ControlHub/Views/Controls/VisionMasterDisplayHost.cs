using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ControlHub.Views.Controls;

/// <summary>
/// Lightweight native child-window surface used to display the existing VisionMaster window
/// outside of the page that owns the VisionMaster process.
/// </summary>
public sealed class VisionMasterDisplayHost : HwndHost
{
    private const int WmSize = 0x0005;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsExControlParent = 0x00010000;

    private IntPtr _hostWindow;

    public event EventHandler? HostWindowChanged;

    public event EventHandler? HostSizeChanged;

    public IntPtr HostWindow => _hostWindow;

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        _hostWindow = CreateWindowEx(
            WsExControlParent,
            "static",
            "",
            WsChild | WsVisible | WsClipChildren | WsClipSiblings,
            0,
            0,
            1,
            1,
            hwndParent.Handle,
            IntPtr.Zero,
            IntPtr.Zero,
            IntPtr.Zero);

        if (_hostWindow == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 VisionMaster 显示承载窗口。");
        }

        HostWindowChanged?.Invoke(this, EventArgs.Empty);
        return new HandleRef(this, _hostWindow);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        if (hwnd.Handle != IntPtr.Zero)
        {
            _ = DestroyWindow(hwnd.Handle);
        }

        _hostWindow = IntPtr.Zero;
        HostWindowChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        HostSizeChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override IntPtr WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmSize)
        {
            HostSizeChanged?.Invoke(this, EventArgs.Empty);
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(
        int exStyle,
        string className,
        string windowName,
        int style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parent,
        IntPtr menu,
        IntPtr instance,
        IntPtr param);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyWindow(IntPtr window);
}
