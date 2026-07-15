using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace ControlHub.Views.Controls;

public sealed class VisionMasterProcessHost : HwndHost
{
    private const int GwlStyle = -16;
    private const int SwShow = 5;
    private const int WmClose = 0x0010;
    private const int WmSize = 0x0005;
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

    private readonly object _syncRoot = new();
    private readonly object _eventPipeSync = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _commandGate = new(1, 1);
    private readonly string _pipeName = $"ControlHub.VisionCalibration.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly string _eventPipeName = $"ControlHub.VisionCalibration.Events.{Environment.ProcessId}.{Guid.NewGuid():N}";
    private readonly CancellationTokenSource _eventPipeCancellation = new();
    private CancellationTokenSource? _startCancellation;
    private Process? _process;
    private NamedPipeServerStream? _activeEventPipe;
    private Task? _eventPipeTask;
    private IntPtr _hostWindow;
    private IntPtr _visionWindow;
    private bool _disposed;

    public event EventHandler? Started;

    public event EventHandler<VisionMasterHostFailedEventArgs>? Failed;

    public event EventHandler? Exited;

    public event EventHandler<VisionClickTargetEventArgs>? ClickTargetReceived;

    public event EventHandler<VisionClickTargetFailedEventArgs>? ClickTargetFailed;

    public async Task StartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await StartCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        if (_disposed)
        {
            return;
        }

        StartEventPipeServer();

        if (_hostWindow == IntPtr.Zero)
        {
            await Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (_hostWindow == IntPtr.Zero)
        {
            RaiseFailed("嵌入窗口尚未创建。 ");
            return;
        }

        lock (_syncRoot)
        {
            if (_process is { HasExited: false })
            {
                return;
            }

            _startCancellation?.Dispose();
            _startCancellation = new CancellationTokenSource();
        }

        var cancellationToken = _startCancellation.Token;

        var executablePath = ResolveHostExecutablePath();
        if (!File.Exists(executablePath))
        {
            StopProcess();
            RaiseFailed($"未找到 VisionMasterHost：{executablePath}");
            return;
        }

        Process process;
        try
        {
            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = $"--embedded --parent-pid {Environment.ProcessId} --pipe-name {_pipeName} --event-pipe-name {_eventPipeName}",
                    WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                    UseShellExecute = false,
                    CreateNoWindow = false
                },
                EnableRaisingEvents = true
            };
            process.Exited += VisionProcess_Exited;
            if (!process.Start())
            {
                process.Dispose();
                StopProcess();
                RaiseFailed("VisionMasterHost 进程未能启动。 ");
                return;
            }

            lock (_syncRoot)
            {
                _process = process;
            }
        }
        catch (Exception exception)
        {
            StopProcess();
            if (!_disposed)
            {
                RaiseFailed(exception.Message);
            }
            return;
        }

        try
        {
            var windowHandle = await WaitForMainWindowAsync(process, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            if (windowHandle == IntPtr.Zero)
            {
                throw new TimeoutException("等待 VisionMaster 标定窗口超时。 ");
            }

            await Dispatcher.InvokeAsync(() => AttachVisionWindow(windowHandle));
            Started?.Invoke(this, EventArgs.Empty);
        }
        catch (OperationCanceledException)
        {
            // A restart or application shutdown intentionally cancels startup.
        }
        catch (Exception exception)
        {
            StopProcess();
            if (!_disposed)
            {
                RaiseFailed(exception.Message);
            }
        }
    }

    public async Task RestartAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (_disposed)
            {
                return;
            }

            StopProcess();
            await StartCoreAsync();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public Task<string> PrepareNinePointCalibrationAsync(
        double centerX,
        double centerY,
        double offsetX,
        double offsetY,
        bool xFirst,
        string calibrationFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationFilePath);
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(calibrationFilePath));
        return SendCalibrationCommandAsync(
            string.Join(
                "\t",
                "PREPARE",
                centerX.ToString("R", CultureInfo.InvariantCulture),
                centerY.ToString("R", CultureInfo.InvariantCulture),
                offsetX.ToString("R", CultureInfo.InvariantCulture),
                offsetY.ToString("R", CultureInfo.InvariantCulture),
                xFirst ? "X" : "Y",
                encodedPath),
            cancellationToken);
    }

    public Task<string> SetCalibrationCenterAsync(
        double centerX,
        double centerY,
        CancellationToken cancellationToken)
    {
        return SendCalibrationCommandAsync(
            string.Join(
                "\t",
                "SET_CENTER",
                centerX.ToString("R", CultureInfo.InvariantCulture),
                centerY.ToString("R", CultureInfo.InvariantCulture)),
            cancellationToken);
    }

    public Task<string> CaptureCalibrationPointAsync(
        int pointNumber,
        CancellationToken cancellationToken)
    {
        return SendCalibrationCommandAsync(
            $"CAPTURE\t{pointNumber.ToString(CultureInfo.InvariantCulture)}",
            cancellationToken);
    }

    public Task<string> CompleteNinePointCalibrationAsync(CancellationToken cancellationToken)
    {
        return SendCalibrationCommandAsync("COMPLETE", cancellationToken);
    }

    public Task<string> SetClickMoveModeAsync(
        bool enabled,
        string calibrationFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationFilePath);
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(calibrationFilePath));
        return SendCalibrationCommandAsync(
            $"SET_CLICK_MODE\t{(enabled ? "1" : "0")}\t{encodedPath}",
            cancellationToken);
    }

    public Task<string> ImportCalibrationFileAsync(
        string calibrationFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationFilePath);
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(calibrationFilePath));
        return SendCalibrationCommandAsync(
            $"IMPORT_CALIBRATION_FILE\t{encodedPath}",
            cancellationToken);
    }

    public async Task<VisionPixelTransformResult> TransformPixelAsync(
        double pixelX,
        double pixelY,
        string calibrationFilePath,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY) || pixelX < 0 || pixelY < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelX), "像素坐标不能小于0。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationFilePath);
        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(calibrationFilePath));
        var response = await SendCalibrationCommandAsync(
            string.Join(
                "\t",
                "TRANSFORM_PIXEL",
                pixelX.ToString("R", CultureInfo.InvariantCulture),
                pixelY.ToString("R", CultureInfo.InvariantCulture),
                encodedPath),
            cancellationToken);
        var parts = response.Split('\t');
        if (parts.Length != 6 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var transformedX) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var transformedY) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerPixelX) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerPixelY) ||
            !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerTransformedX) ||
            !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerTransformedY) ||
            !double.IsFinite(transformedX) ||
            !double.IsFinite(transformedY) ||
            !double.IsFinite(centerPixelX) ||
            !double.IsFinite(centerPixelY) ||
            !double.IsFinite(centerTransformedX) ||
            !double.IsFinite(centerTransformedY))
        {
            throw new InvalidDataException("VisionMaster 返回的像素标定转换结果无效。");
        }

        return new VisionPixelTransformResult(
            pixelX,
            pixelY,
            transformedX,
            transformedY,
            centerPixelX,
            centerPixelY,
            centerTransformedX,
            centerTransformedY);
    }

    /// <summary>
    /// 加载“找芯片”方案并单次执行其中的“流程1 → Blob分析1”，
    /// 返回 VisionMaster 结果表前两行的矩形与像素质心。
    /// </summary>
    public async Task<VisionRectangleBlobResult> RunRectangleBlobInspectionAsync(
        string solutionPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(solutionPath);
        var fullPath = Path.GetFullPath(solutionPath);
        if (!string.Equals(Path.GetExtension(fullPath), ".sol", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("找芯片方案必须使用 .sol 扩展名。");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("找芯片方案文件不存在。", fullPath);
        }

        var encodedPath = Convert.ToBase64String(Encoding.UTF8.GetBytes(fullPath));
        var response = await SendCalibrationCommandAsync(
            $"RUN_RECTANGLE_BLOB\t{encodedPath}",
            cancellationToken);
        var parts = response.Split('\t');
        if (parts.Length != 15 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var firstX) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var firstY) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var firstLeft) ||
            !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var firstTop) ||
            !double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var firstWidth) ||
            !double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var firstHeight) ||
            !double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var secondX) ||
            !double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var secondY) ||
            !double.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var secondLeft) ||
            !double.TryParse(parts[9], NumberStyles.Float, CultureInfo.InvariantCulture, out var secondTop) ||
            !double.TryParse(parts[10], NumberStyles.Float, CultureInfo.InvariantCulture, out var secondWidth) ||
            !double.TryParse(parts[11], NumberStyles.Float, CultureInfo.InvariantCulture, out var secondHeight) ||
            !int.TryParse(parts[12], NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageWidth) ||
            !int.TryParse(parts[13], NumberStyles.Integer, CultureInfo.InvariantCulture, out var imageHeight) ||
            !double.IsFinite(firstX) ||
            !double.IsFinite(firstY) ||
            !double.IsFinite(firstLeft) ||
            !double.IsFinite(firstTop) ||
            !double.IsFinite(firstWidth) ||
            !double.IsFinite(firstHeight) ||
            !double.IsFinite(secondX) ||
            !double.IsFinite(secondY) ||
            !double.IsFinite(secondLeft) ||
            !double.IsFinite(secondTop) ||
            !double.IsFinite(secondWidth) ||
            !double.IsFinite(secondHeight) ||
            firstX < 0 || firstY < 0 || secondX < 0 || secondY < 0 ||
            firstWidth <= 0 || firstHeight <= 0 || secondWidth <= 0 || secondHeight <= 0 ||
            !((imageWidth > 0 && imageHeight > 0 && !string.IsNullOrWhiteSpace(parts[14])) ||
              (imageWidth == 0 && imageHeight == 0 && string.IsNullOrWhiteSpace(parts[14]))))
        {
            throw new InvalidDataException("VisionMaster 返回的Blob检测图或矩形结果无效。");
        }

        return new VisionRectangleBlobResult(
            new VisionBlobRectangle(firstX, firstY, firstLeft, firstTop, firstWidth, firstHeight),
            new VisionBlobRectangle(secondX, secondY, secondLeft, secondTop, secondWidth, secondHeight),
            parts[14],
            imageWidth,
            imageHeight);
    }

    public async Task AbortNinePointCalibrationAsync()
    {
        try
        {
            _ = await SendCalibrationCommandAsync("ABORT", CancellationToken.None);
        }
        catch
        {
            // The visual process may already have exited; motion cancellation remains authoritative.
        }
    }

    private async Task<string> SendCalibrationCommandAsync(
        string command,
        CancellationToken cancellationToken)
    {
        await _commandGate.WaitAsync(cancellationToken);
        try
        {
            lock (_syncRoot)
            {
                if (_process is not { HasExited: false })
                {
                    throw new InvalidOperationException("视觉组件尚未启动。");
                }
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await using var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeout.Token);

            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 1024, leaveOpen: true)
            {
                AutoFlush = true
            };
            await writer.WriteLineAsync(command.AsMemory(), timeout.Token);
            var response = await reader.ReadLineAsync(timeout.Token);
            if (string.IsNullOrWhiteSpace(response))
            {
                throw new IOException("视觉组件未返回标定命令结果。");
            }

            var separator = response.IndexOf('\t');
            var status = separator < 0 ? response : response[..separator];
            var encodedMessage = separator < 0 ? "" : response[(separator + 1)..];
            string message;
            try
            {
                message = string.IsNullOrEmpty(encodedMessage)
                    ? ""
                    : Encoding.UTF8.GetString(Convert.FromBase64String(encodedMessage));
            }
            catch (FormatException)
            {
                message = encodedMessage;
            }

            if (string.Equals(status, "OK", StringComparison.Ordinal))
            {
                return message;
            }

            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(message) ? "VisionMaster 标定命令执行失败。" : message);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待 VisionMaster 标定命令超时。");
        }
        finally
        {
            _commandGate.Release();
        }
    }

    public void Shutdown()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _eventPipeCancellation.Cancel();
        lock (_eventPipeSync)
        {
            try
            {
                _activeEventPipe?.Dispose();
            }
            catch
            {
            }

            _activeEventPipe = null;
        }

        StopProcess();
    }

    private void StartEventPipeServer()
    {
        if (_eventPipeTask is not null)
        {
            return;
        }

        _eventPipeTask = RunEventPipeServerAsync(_eventPipeCancellation.Token);
    }

    private async Task RunEventPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _eventPipeName,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                lock (_eventPipeSync)
                {
                    _activeEventPipe = pipe;
                }

                await pipe.WaitForConnectionAsync(cancellationToken);
                using var reader = new StreamReader(
                    pipe,
                    new UTF8Encoding(false),
                    false,
                    1024,
                    leaveOpen: true);
                var message = await reader.ReadLineAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(message))
                {
                    DispatchHostEvent(message);
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
                // The helper may close while reconnecting; accept the next event connection.
            }
            finally
            {
                lock (_eventPipeSync)
                {
                    if (ReferenceEquals(_activeEventPipe, pipe))
                    {
                        _activeEventPipe = null;
                    }
                }

                pipe?.Dispose();
            }
        }
    }

    private void DispatchHostEvent(string message)
    {
        var parts = message.Split('\t');
        if (parts.Length == 9 &&
            string.Equals(parts[0], "CLICK_TARGET", StringComparison.Ordinal) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixelX) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pixelY) &&
            double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var worldX) &&
            double.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var worldY) &&
            double.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerPixelX) &&
            double.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerPixelY) &&
            double.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerWorldX) &&
            double.TryParse(parts[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerWorldY) &&
            double.IsFinite(worldX) &&
            double.IsFinite(worldY) &&
            double.IsFinite(centerPixelX) &&
            double.IsFinite(centerPixelY) &&
            double.IsFinite(centerWorldX) &&
            double.IsFinite(centerWorldY))
        {
            _ = Dispatcher.BeginInvoke(
                () => ClickTargetReceived?.Invoke(
                    this,
                    new VisionClickTargetEventArgs(
                        pixelX,
                        pixelY,
                        worldX,
                        worldY,
                        centerPixelX,
                        centerPixelY,
                        centerWorldX,
                        centerWorldY)));
            return;
        }

        if (parts.Length == 2 && string.Equals(parts[0], "CLICK_ERROR", StringComparison.Ordinal))
        {
            string errorMessage;
            try
            {
                errorMessage = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            }
            catch (FormatException)
            {
                errorMessage = parts[1];
            }

            _ = Dispatcher.BeginInvoke(
                () => ClickTargetFailed?.Invoke(
                    this,
                    new VisionClickTargetFailedEventArgs(errorMessage)));
        }
    }

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
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建 VisionMaster 承载窗口。 ");
        }

        return new HandleRef(this, _hostWindow);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        Shutdown();
        if (hwnd.Handle != IntPtr.Zero)
        {
            _ = DestroyWindow(hwnd.Handle);
        }

        _hostWindow = IntPtr.Zero;
    }

    protected override void OnWindowPositionChanged(Rect rcBoundingBox)
    {
        base.OnWindowPositionChanged(rcBoundingBox);
        ResizeVisionWindow();
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
            ResizeVisionWindow();
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private static async Task<IntPtr> WaitForMainWindowAsync(
        Process process,
        CancellationToken cancellationToken)
    {
        return await Task.Run(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"VisionMasterHost 已提前退出（代码 {process.ExitCode}）。");
                }

                process.Refresh();
                var windowHandle = process.MainWindowHandle;
                if (windowHandle == IntPtr.Zero)
                {
                    windowHandle = FindVisibleTopLevelWindow(process.Id);
                }

                if (windowHandle != IntPtr.Zero)
                {
                    return windowHandle;
                }

                Thread.Sleep(50);
            }

            return IntPtr.Zero;
        }, cancellationToken);
    }

    private static IntPtr FindVisibleTopLevelWindow(int processId)
    {
        var result = IntPtr.Zero;
        _ = EnumWindows(
            (windowHandle, parameter) =>
            {
                _ = GetWindowThreadProcessId(windowHandle, out var windowProcessId);
                if (windowProcessId != (uint)processId || !IsWindowVisible(windowHandle))
                {
                    return true;
                }

                result = windowHandle;
                return false;
            },
            IntPtr.Zero);
        return result;
    }

    private void AttachVisionWindow(IntPtr windowHandle)
    {
        if (_hostWindow == IntPtr.Zero || windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("VisionMaster 嵌入窗口句柄无效。 ");
        }

        if (!IsWindow(windowHandle))
        {
            throw new Win32Exception("VisionMaster 窗口句柄已失效。 ");
        }

        Marshal.SetLastPInvokeError(0);
        var style = GetWindowLong(windowHandle, GwlStyle);
        var getStyleError = Marshal.GetLastPInvokeError();
        if (style == 0 && getStyleError != 0)
        {
            throw new Win32Exception(getStyleError, "无法读取 VisionMaster 窗口样式。 ");
        }

        Marshal.SetLastPInvokeError(0);
        var previousParent = SetParent(windowHandle, _hostWindow);
        var setParentError = Marshal.GetLastPInvokeError();
        if (previousParent == IntPtr.Zero && setParentError != 0)
        {
            throw new Win32Exception(setParentError, "无法嵌入 VisionMaster 窗口。 ");
        }

        style &= ~(WsPopup | WsCaption | WsThickFrame);
        style |= WsChild | WsVisible | WsClipChildren | WsClipSiblings;

        Marshal.SetLastPInvokeError(0);
        var previousStyle = SetWindowLong(windowHandle, GwlStyle, style);
        var setStyleError = Marshal.GetLastPInvokeError();
        if (previousStyle == 0 && setStyleError != 0)
        {
            _ = SetParent(windowHandle, previousParent);
            throw new Win32Exception(setStyleError, "无法设置 VisionMaster 嵌入窗口样式。 ");
        }

        if (GetParent(windowHandle) != _hostWindow)
        {
            _ = SetParent(windowHandle, previousParent);
            throw new Win32Exception("VisionMaster 窗口父子关系验证失败。 ");
        }

        _visionWindow = windowHandle;
        _ = ShowWindow(windowHandle, SwShow);
        ResizeVisionWindow(throwOnFailure: true);
    }

    private void ResizeVisionWindow(bool throwOnFailure = false)
    {
        if (_hostWindow == IntPtr.Zero || _visionWindow == IntPtr.Zero)
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException("VisionMaster 承载窗口尚未准备完成。 ");
            }

            return;
        }

        if (!GetClientRect(_hostWindow, out var bounds))
        {
            if (throwOnFailure)
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法读取 VisionMaster 承载区域。 ");
            }

            return;
        }

        var width = Math.Max(1, bounds.Right - bounds.Left);
        var height = Math.Max(1, bounds.Bottom - bounds.Top);
        var resized = SetWindowPos(
            _visionWindow,
            IntPtr.Zero,
            0,
            0,
            width,
            height,
            SwpNoActivate | SwpFrameChanged | SwpShowWindow);
        if (!resized && throwOnFailure)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "无法调整 VisionMaster 嵌入窗口大小。 ");
        }
    }

    private void StopProcess()
    {
        Process? process;
        IntPtr visionWindow;
        lock (_syncRoot)
        {
            _startCancellation?.Cancel();
            _startCancellation?.Dispose();
            _startCancellation = null;
            process = _process;
            _process = null;
            visionWindow = _visionWindow;
            _visionWindow = IntPtr.Zero;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -= VisionProcess_Exited;
            if (!process.HasExited)
            {
                var closeRequested = visionWindow != IntPtr.Zero
                    && IsWindow(visionWindow)
                    && PostMessage(visionWindow, WmClose, IntPtr.Zero, IntPtr.Zero);
                if (!closeRequested)
                {
                    closeRequested = process.CloseMainWindow();
                }

                if (!closeRequested || !process.WaitForExit(8_000))
                {
                    process.Kill(entireProcessTree: true);
                    _ = process.WaitForExit(2_000);
                }
            }
        }
        catch
        {
            // The owned helper process may already be terminating.
        }
        finally
        {
            process.Dispose();
        }
    }

    private void VisionProcess_Exited(object? sender, EventArgs e)
    {
        if (sender is not Process process)
        {
            return;
        }

        var shouldNotify = false;
        lock (_syncRoot)
        {
            if (!ReferenceEquals(_process, process))
            {
                return;
            }

            _process = null;
            _visionWindow = IntPtr.Zero;
            shouldNotify = !_disposed;
        }

        process.Exited -= VisionProcess_Exited;
        process.Dispose();

        if (shouldNotify)
        {
            _ = Dispatcher.BeginInvoke(() => Exited?.Invoke(this, EventArgs.Empty));
        }
    }

    private void RaiseFailed(string message)
    {
        Failed?.Invoke(this, new VisionMasterHostFailedEventArgs(message));
    }

    private static string ResolveHostExecutablePath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("VISIONMASTER_HOST_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        return Path.Combine(AppContext.BaseDirectory, "VisionMasterHost", "VisionMasterHost.exe");
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

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

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

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}

public sealed class VisionMasterHostFailedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed class VisionClickTargetEventArgs(
    int pixelX,
    int pixelY,
    double transformedX,
    double transformedY,
    double centerPixelX,
    double centerPixelY,
    double centerTransformedX,
    double centerTransformedY) : EventArgs
{
    public int PixelX { get; } = pixelX;

    public int PixelY { get; } = pixelY;

    public double TransformedX { get; } = transformedX;

    public double TransformedY { get; } = transformedY;

    public double CenterPixelX { get; } = centerPixelX;

    public double CenterPixelY { get; } = centerPixelY;

    public double CenterTransformedX { get; } = centerTransformedX;

    public double CenterTransformedY { get; } = centerTransformedY;
}

public sealed class VisionClickTargetFailedEventArgs(string message) : EventArgs
{
    public string Message { get; } = message;
}

public sealed record VisionPixelTransformResult(
    double PixelX,
    double PixelY,
    double TransformedX,
    double TransformedY,
    double CenterPixelX,
    double CenterPixelY,
    double CenterTransformedX,
    double CenterTransformedY);

public sealed record VisionBlobRectangle(
    double X,
    double Y,
    double Left,
    double Top,
    double Width,
    double Height);

public sealed record VisionRectangleBlobResult(
    VisionBlobRectangle Rectangle1,
    VisionBlobRectangle Rectangle2,
    string ImagePath,
    int ImageWidth,
    int ImageHeight);
