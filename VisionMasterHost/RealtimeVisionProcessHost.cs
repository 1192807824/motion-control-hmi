using System.ComponentModel;
using System.Diagnostics;
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
    private TaskCompletionSource<string>? _readySignal;
    private IntPtr _hostWindow;
    private IntPtr _childWindow;
    private bool _disposed;

    public bool IsRunning => IsProcessRunning(_process) && _childWindow != IntPtr.Zero;

    public event EventHandler<RealtimeImageClickedEventArgs>? ImageClicked;

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
            _readySignal = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                var windowHandle = await WaitForMainWindowAsync(process, cancellationToken);
                if (windowHandle == IntPtr.Zero)
                {
                    throw new TimeoutException("等待实时画面窗口超时。");
                }

                AttachChildWindow(windowHandle);
                var timeoutTask = Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
                var completed = await Task.WhenAny(_readySignal.Task, timeoutTask);
                if (completed != _readySignal.Task)
                {
                    throw new TimeoutException("等待“实时画面.sol”出图超时。");
                }

                var error = await _readySignal.Task;
                if (!string.IsNullOrWhiteSpace(error))
                {
                    throw new InvalidOperationException(error);
                }
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
        if (parts.Length == 1 && parts[0] == "LIVE_READY")
        {
            _readySignal?.TrySetResult("");
            return;
        }

        if (parts.Length == 2 && parts[0] == "LIVE_ERROR")
        {
            var error = Decode(parts[1]);
            _readySignal?.TrySetResult(error);
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
        _ = SetParent(windowHandle, _hostWindow);
        style &= ~(WsPopup | WsCaption | WsThickFrame);
        style |= WsChild | WsVisible | WsClipChildren | WsClipSiblings;
        _ = SetWindowLong(windowHandle, GwlStyle, style);
        if (GetParent(windowHandle) != _hostWindow)
        {
            throw new Win32Exception("实时画面窗口嵌入失败。");
        }

        _childWindow = windowHandle;
        _ = ShowWindow(windowHandle, SwShow);
        ResizeChildWindow();
    }

    private void ResizeChildWindow()
    {
        if (_hostWindow == IntPtr.Zero || _childWindow == IntPtr.Zero ||
            !GetClientRect(_hostWindow, out var bounds))
        {
            return;
        }

        _ = SetWindowPos(
            _childWindow,
            IntPtr.Zero,
            0,
            0,
            Math.Max(1, bounds.Right - bounds.Left),
            Math.Max(1, bounds.Bottom - bounds.Top),
            SwpNoActivate | SwpFrameChanged | SwpShowWindow);
    }

    private void StopCore()
    {
        _startCancellation?.Cancel();
        _startCancellation?.Dispose();
        _startCancellation = null;
        _readySignal?.TrySetCanceled();
        _readySignal = null;

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
        if (_disposed)
        {
            return;
        }

        _childWindow = IntPtr.Zero;
        _readySignal?.TrySetResult("实时画面进程已退出。");
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                process.Refresh();
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"实时画面进程已提前退出（代码 {process.ExitCode}）。");
                }

                if (process.MainWindowHandle != IntPtr.Zero)
                {
                    return process.MainWindowHandle;
                }

                Thread.Sleep(50);
            }

            return IntPtr.Zero;
        }, cancellationToken);
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
