using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace VisionMasterHost;

public sealed class RealtimeVisionProcessHost : HwndHost
{
    private const int GwlStyle = -16;
    private const int WmClose = 0x0010;
    private const int WmSize = 0x0005;
    private const int SwShow = 5;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;
    private const int SwpShowWindow = 0x0040;
    private const int WsChild = 0x40000000;
    private const int WsVisible = 0x10000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsPopup = unchecked((int)0x80000000);
    private const int WsExControlParent = 0x00010000;

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly string _eventPipeName = $"VisionMaster.Live.Events.{Process.GetCurrentProcess().Id}.{Guid.NewGuid():N}";
    private readonly string _commandPipeName = $"VisionMaster.Live.Commands.{Process.GetCurrentProcess().Id}.{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _eventCancellation = new();
    private Process? _process;
    private CancellationTokenSource? _startCancellation;
    private Task? _eventTask;
    private TaskCompletionSource<IntPtr>? _windowSignal;
    private IntPtr _hostWindow;
    private IntPtr _childWindow;
    private bool _disposed;

    public bool IsRunning => IsProcessRunning(_process) && _childWindow != IntPtr.Zero;

    public event EventHandler<RealtimeImageClickedEventArgs>? ImageClicked;

    public event EventHandler? Ready;

    public event EventHandler<RealtimePreviewFailedEventArgs>? Failed;

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed || IsProcessRunning(_process))
            {
                return;
            }

            if (_hostWindow == IntPtr.Zero)
            {
                await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Loaded);
            }

            if (_hostWindow == IntPtr.Zero)
            {
                throw new InvalidOperationException("实时画面承载窗口尚未创建。");
            }

            StartEventServer();
            _windowSignal = new TaskCompletionSource<IntPtr>(TaskCreationOptions.RunContinuationsAsynchronously);
            _startCancellation?.Dispose();
            _startCancellation = new CancellationTokenSource();
            var cancellationToken = _startCancellation.Token;

            var executablePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("无法确定视觉宿主程序路径。");
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments =
                        $"--live-only --parent-pid {Process.GetCurrentProcess().Id} " +
                        $"--live-parent-hwnd {_hostWindow.ToInt64().ToString(CultureInfo.InvariantCulture)} " +
                        $"--live-event-pipe-name {_eventPipeName} --live-command-pipe-name {_commandPipeName}",
                    WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                    UseShellExecute = false,
                    CreateNoWindow = false
                },
                EnableRaisingEvents = true
            };
            process.Exited += ChildProcess_Exited;
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("实时画面进程未能启动。");
            }

            _process = process;
            try
            {
                var windowHandle = await WaitForEmbeddedWindowAsync(_windowSignal.Task, cancellationToken);
                if (windowHandle == IntPtr.Zero)
                {
                    throw new TimeoutException("等待实时画面窗口超时。");
                }

                _ = GetWindowThreadProcessId(windowHandle, out var windowProcessId);
                if (windowProcessId != (uint)process.Id)
                {
                    throw new InvalidOperationException("实时画面窗口不属于已启动的实时画面进程。");
                }

                AttachChildWindow(windowHandle);
                _windowSignal = null;
            }
            catch
            {
                StopCore();
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task StopAsync()
    {
        try
        {
            _startCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        await _lifecycleGate.WaitAsync();
        try
        {
            StopCore();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task ZoomInAsync() => SendCommandAsync("ZOOM_IN");

    public Task ZoomOutAsync() => SendCommandAsync("ZOOM_OUT");

    public Task ActualSizeAsync() => SendCommandAsync("ACTUAL");

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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建实时画面承载窗口。");
        }

        return new HandleRef(this, _hostWindow);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        _disposed = true;
        _eventCancellation.Cancel();
        StopCore();
        if (hwnd.Handle != IntPtr.Zero)
        {
            _ = DestroyWindow(hwnd.Handle);
        }

        _hostWindow = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeChildWindow();
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
            ResizeChildWindow();
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void StartEventServer()
    {
        if (_eventTask is null)
        {
            _eventTask = RunEventServerAsync(_eventCancellation.Token);
        }
    }

    private async Task RunEventServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _eventPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                var message = await reader.ReadLineAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    DispatchEvent(message);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (IOException) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private void DispatchEvent(string message)
    {
        var parts = message.Split('\t');
        if (parts.Length == 2
            && parts[0] == "LIVE_WINDOW"
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var handleValue)
            && handleValue > 0)
        {
            _windowSignal?.TrySetResult(new IntPtr(handleValue));
            return;
        }

        if (parts.Length == 1 && parts[0] == "LIVE_READY")
        {
            _ = Dispatcher.BeginInvoke(() => Ready?.Invoke(this, EventArgs.Empty));
            return;
        }

        if (parts.Length == 2 && parts[0] == "LIVE_ERROR")
        {
            var error = Decode(parts[1]);
            _ = Dispatcher.BeginInvoke(() => Failed?.Invoke(this, new RealtimePreviewFailedEventArgs(error)));
            return;
        }

        if (parts.Length == 2 && parts[0] == "LIVE_WINDOW_ERROR")
        {
            var error = Decode(parts[1]);
            _windowSignal?.TrySetException(new InvalidOperationException(error));
            _ = Dispatcher.BeginInvoke(() => Failed?.Invoke(this, new RealtimePreviewFailedEventArgs(error)));
            return;
        }

        if (parts.Length == 5 &&
            parts[0] == "IMAGE_CLICK" &&
            int.TryParse(parts[1], out var pixelX) &&
            int.TryParse(parts[2], out var pixelY) &&
            int.TryParse(parts[3], out var imageWidth) &&
            int.TryParse(parts[4], out var imageHeight) &&
            imageWidth > 0 && imageHeight > 0)
        {
            _ = Dispatcher.BeginInvoke(() => ImageClicked?.Invoke(
                this,
                new RealtimeImageClickedEventArgs(pixelX, pixelY, imageWidth, imageHeight)));
            return;
        }

        if (parts.Length == 2 && parts[0] == "IMAGE_CLICK_ERROR")
        {
            var error = Decode(parts[1]);
            _ = Dispatcher.BeginInvoke(() => Failed?.Invoke(this, new RealtimePreviewFailedEventArgs(error)));
        }
    }

    private async Task SendCommandAsync(string command)
    {
        if (!IsRunning)
        {
            return;
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            _commandPipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2_000).ConfigureAwait(false);
        using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
        await writer.WriteLineAsync(command).ConfigureAwait(false);
    }

    private void AttachChildWindow(IntPtr windowHandle)
    {
        if (_hostWindow == IntPtr.Zero || !IsWindow(windowHandle))
        {
            throw new InvalidOperationException("实时画面窗口句柄无效。");
        }

        var style = GetWindowLong(windowHandle, GwlStyle);
        if (style == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取实时画面窗口样式。");
        }

        var previousParent = GetParent(windowHandle);
        _ = SetParent(windowHandle, _hostWindow);
        style &= ~(WsPopup | WsCaption | WsThickFrame);
        style |= WsChild | WsVisible | WsClipChildren | WsClipSiblings;
        if (SetWindowLong(windowHandle, GwlStyle, style) == 0)
        {
            _ = SetParent(windowHandle, previousParent);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置实时画面嵌入窗口样式。");
        }

        if (GetParent(windowHandle) != _hostWindow)
        {
            _ = SetParent(windowHandle, previousParent);
            throw new Win32Exception("实时画面窗口嵌入失败。");
        }

        _childWindow = windowHandle;
        _ = ShowWindow(windowHandle, SwShow);
        ResizeChildWindow(throwOnFailure: true);
    }

    private void ResizeChildWindow(bool throwOnFailure = false)
    {
        if (_hostWindow == IntPtr.Zero || _childWindow == IntPtr.Zero)
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException("实时画面承载窗口尚未准备完成。");
            }

            return;
        }

        if (!GetClientRect(_hostWindow, out var bounds))
        {
            if (throwOnFailure)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取实时画面承载区域。");
            }

            return;
        }

        var resized = SetWindowPos(
            _childWindow,
            IntPtr.Zero,
            0,
            0,
            Math.Max(1, bounds.Right - bounds.Left),
            Math.Max(1, bounds.Bottom - bounds.Top),
            SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        if (!resized && throwOnFailure)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法调整实时画面嵌入窗口大小。");
        }
    }

    private void StopCore()
    {
        _startCancellation?.Cancel();
        _startCancellation?.Dispose();
        _startCancellation = null;
        _windowSignal?.TrySetCanceled();
        _windowSignal = null;
        var process = _process;
        _process = null;
        var childWindow = _childWindow;
        _childWindow = IntPtr.Zero;
        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -= ChildProcess_Exited;
            if (!process.HasExited)
            {
                if (childWindow != IntPtr.Zero && IsWindow(childWindow))
                {
                    _ = PostMessage(childWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
                }

                if (!process.WaitForExit(2_000))
                {
                    process.Kill();
                    process.WaitForExit(2_000);
                }
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void ChildProcess_Exited(object? sender, EventArgs e)
    {
        if (_disposed || sender is not Process process || !ReferenceEquals(process, _process))
        {
            return;
        }

        var message = "实时画面进程已退出。";
        try
        {
            message = $"实时画面进程已退出（代码 {process.ExitCode}）。";
        }
        catch
        {
        }

        _process = null;
        _childWindow = IntPtr.Zero;
        _windowSignal?.TrySetException(new InvalidOperationException(message));
        _windowSignal = null;
        process.Exited -= ChildProcess_Exited;
        process.Dispose();
        _ = Dispatcher.BeginInvoke(
            () => Failed?.Invoke(this, new RealtimePreviewFailedEventArgs(message)));
    }

    private static async Task<IntPtr> WaitForEmbeddedWindowAsync(
        Task<IntPtr> windowTask,
        CancellationToken cancellationToken)
    {
        var timeoutTask = Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        var completedTask = await Task.WhenAny(windowTask, timeoutTask);
        if (completedTask == windowTask)
        {
            return await windowTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return IntPtr.Zero;
    }

    private static bool IsProcessRunning(Process? process)
    {
        if (process is null)
        {
            return false;
        }

        try
        {
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string Decode(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return value;
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
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
        IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int newStyle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        int flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr window, out NativeRect rectangle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(
        IntPtr window,
        int message,
        IntPtr wParam,
        IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

public sealed class RealtimeImageClickedEventArgs : EventArgs
{
    public RealtimeImageClickedEventArgs(int pixelX, int pixelY, int imageWidth, int imageHeight)
    {
        PixelX = pixelX;
        PixelY = pixelY;
        ImageWidth = imageWidth;
        ImageHeight = imageHeight;
    }

    public int PixelX { get; }
    public int PixelY { get; }
    public int ImageWidth { get; }
    public int ImageHeight { get; }
}

public sealed class RealtimePreviewFailedEventArgs : EventArgs
{
    public RealtimePreviewFailedEventArgs(string message)
    {
        Message = message;
    }

    public string Message { get; }
}
