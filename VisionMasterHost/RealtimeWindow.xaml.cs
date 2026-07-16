using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.Interface;

namespace VisionMasterHost;

public partial class RealtimeWindow : Window
{
    private const string SolutionFileName = "实时画面.sol";
    private const string ProcedureName = "流程1";
    private const string ImageSourceName = "图像源1";
    private const int ContinuousExecutionInProgressErrorCode = unchecked((int)0xE0000311);
    private const int GwlStyle = -16;
    private const int SwpNoActivate = 0x0010;
    private const int SwpFrameChanged = 0x0020;
    private const int WsChild = 0x40000000;
    private const int WsClipSiblings = 0x04000000;
    private const int WsClipChildren = 0x02000000;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsPopup = unchecked((int)0x80000000);

    private readonly string? _eventPipeName;
    private readonly string? _commandPipeName;
    private readonly IntPtr _parentWindow;
    private readonly CancellationTokenSource _commandCancellation = new();
    private Task? _commandTask;
    private Task? _windowReadySignalTask;
    private VmProcedure? _procedure;
    private bool _closed;
    private bool _embeddingFailed;
    private int _startupSignalSent;

    public RealtimeWindow(
        string? eventPipeName,
        string? commandPipeName,
        IntPtr parentWindow)
    {
        InitializeComponent();
        _eventPipeName = string.IsNullOrWhiteSpace(eventPipeName) ? null : eventPipeName;
        _commandPipeName = string.IsNullOrWhiteSpace(commandPipeName) ? null : commandPipeName;
        _parentWindow = parentWindow;
    }

    public void ReportSdkInitializationFailure(Exception exception)
    {
        var message = FormatException(exception);
        ShowError(message);
        SignalStartupError(message);
    }

    public bool PrepareForDisplay()
    {
        try
        {
            var windowHandle = new WindowInteropHelper(this).EnsureHandle();
            EmbedIntoParent(windowHandle);
            _windowReadySignalTask = SendEventAsync(
                $"LIVE_WINDOW\t{windowHandle.ToInt64().ToString(CultureInfo.InvariantCulture)}");
            return true;
        }
        catch (Exception exception)
        {
            _embeddingFailed = true;
            var message = $"实时画面窗口嵌入失败：{exception.Message}";
            ShowError(message);
            Interlocked.Exchange(ref _startupSignalSent, 1);
            _windowReadySignalTask = SendEventAsync($"LIVE_WINDOW_ERROR\t{Encode(message)}");
            _windowReadySignalTask.GetAwaiter().GetResult();
            return false;
        }
    }

    private async void RealtimeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartCommandServer();
        try
        {
            if (_windowReadySignalTask is not null)
            {
                await _windowReadySignalTask;
            }

            if (_closed || _embeddingFailed)
            {
                return;
            }

            RealtimeRenderControl.SetRenderToolbarVisible(false);
            RealtimeRenderControl.ChangeImageComboBoxVisibility(false);

            var solutionPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                SolutionFileName);
            if (!File.Exists(solutionPath))
            {
                throw new FileNotFoundException($"桌面未找到“{SolutionFileName}”。", solutionPath);
            }

            VmSolution.Load(solutionPath, "");
            _procedure = VmSolution.Instance[ProcedureName] as VmProcedure
                ?? throw new InvalidOperationException($"“{SolutionFileName}”中未找到“{ProcedureName}”。");
            var imageSource = ResolveImageSource(_procedure);
            RealtimeRenderControl.ModuleSource = imageSource;
            try
            {
                RealtimeRenderControl.UpdateVMResultShow();
            }
            catch
            {
                // 连续取流的第一帧尚未到达。
            }

            StartContinuousPreview();
            _ = WaitForFirstFrameAsync(_commandCancellation.Token);
        }
        catch (Exception exception)
        {
            var message = FormatException(exception);
            ShowError(message);
            SignalStartupError(message);
        }
    }

    private void EmbedIntoParent(IntPtr windowHandle)
    {
        if (_parentWindow == IntPtr.Zero || !IsWindow(_parentWindow))
        {
            throw new InvalidOperationException("实时画面承载窗口句柄无效。");
        }

        if (windowHandle == IntPtr.Zero || !IsWindow(windowHandle))
        {
            throw new InvalidOperationException("实时画面窗口句柄无效。");
        }

        var style = GetWindowLong(windowHandle, GwlStyle);
        if (style == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取实时画面窗口样式。");
        }

        _ = SetParent(windowHandle, _parentWindow);
        style &= ~(WsPopup | WsCaption | WsThickFrame);
        style |= WsChild | WsClipChildren | WsClipSiblings;
        if (SetWindowLong(windowHandle, GwlStyle, style) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置实时画面窗口样式。");
        }

        if (GetParent(windowHandle) != _parentWindow)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法建立实时画面窗口的父子关系。");
        }

        if (!GetClientRect(_parentWindow, out var bounds))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法读取实时画面承载区域。");
        }

        if (!SetWindowPos(
                windowHandle,
                IntPtr.Zero,
                0,
                0,
                Math.Max(1, bounds.Right - bounds.Left),
                Math.Max(1, bounds.Bottom - bounds.Top),
                SwpNoActivate | SwpFrameChanged))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "无法设置实时画面窗口尺寸。");
        }
    }

    private async Task WaitForFirstFrameAsync(CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            var frameReady = await Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    _ = ReadCurrentImageSize();
                    RealtimeRenderControl.UpdateVMResultShow();
                    return true;
                }
                catch
                {
                    return false;
                }
            });
            if (frameReady)
            {
                SignalStartupReady();
                return;
            }

            try
            {
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        if (!cancellationToken.IsCancellationRequested)
        {
            var message = "“实时画面.sol / 流程1 / 图像源1”已启动，但15秒内没有取得有效图像，请检查相机连接和图像源配置。";
            await Dispatcher.InvokeAsync(() => ShowError(message));
            SignalStartupError(message);
        }
    }

    private void SignalStartupReady()
    {
        if (Interlocked.Exchange(ref _startupSignalSent, 1) == 0)
        {
            _ = SendEventAsync("LIVE_READY");
        }
    }

    private void SignalStartupError(string message)
    {
        if (Interlocked.Exchange(ref _startupSignalSent, 1) == 0)
        {
            _ = SendEventAsync($"LIVE_ERROR\t{Encode(message)}");
        }
    }

    private static IVmModule ResolveImageSource(VmProcedure procedure)
    {
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            if (!string.Equals(displayName, ImageSourceName, StringComparison.Ordinal))
            {
                continue;
            }

            var moduleName = info.strModuleName?.Trim() ?? "";
            foreach (var candidate in new[]
                     {
                         $"{ProcedureName}.{displayName}",
                         $"{ProcedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is IVmModule module)
                    {
                        return module;
                    }
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException($"“{ProcedureName}”中未找到“{ImageSourceName}”。");
    }

    private void StartContinuousPreview()
    {
        if (_procedure is null || _procedure.ContinuousRunEnable)
        {
            return;
        }

        try
        {
            _procedure.ContinuousRunEnable = true;
        }
        catch (Exception exception) when (HasVmErrorCode(exception, ContinuousExecutionInProgressErrorCode))
        {
            // VisionMaster ErrorCodeDefine.h: IMVS_EC_MODULE_CONTINUE_EXECUTE（正在连续执行）。
        }
    }

    private async void RealtimeRenderControl_OnMouseLeftButtonDownPixelChanged(int pixelX, int pixelY)
    {
        try
        {
            var (width, height) = ReadCurrentImageSize();
            await SendEventAsync(string.Join(
                "\t",
                "IMAGE_CLICK",
                pixelX.ToString(CultureInfo.InvariantCulture),
                pixelY.ToString(CultureInfo.InvariantCulture),
                width.ToString(CultureInfo.InvariantCulture),
                height.ToString(CultureInfo.InvariantCulture)));
        }
        catch (Exception exception)
        {
            await SendEventAsync($"IMAGE_CLICK_ERROR\t{Encode(FormatException(exception))}");
        }
    }

    private (int Width, int Height) ReadCurrentImageSize()
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"vm-live-{Guid.NewGuid():N}.bmp");
        try
        {
            RealtimeRenderControl.SaveOriginalImage(temporaryPath);
            using var image = Image.FromFile(temporaryPath);
            if (image.Width <= 0 || image.Height <= 0)
            {
                throw new InvalidOperationException("实时画面尺寸无效。");
            }

            return (image.Width, image.Height);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private void StartCommandServer()
    {
        if (_commandTask is not null || _commandPipeName is null)
        {
            return;
        }

        _commandTask = RunCommandServerAsync(_commandCancellation.Token);
    }

    private async Task RunCommandServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    _commandPipeName!,
                    PipeDirection.In,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using var reader = new StreamReader(pipe, new UTF8Encoding(false));
                var command = await reader.ReadLineAsync().ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(command))
                {
                    await Dispatcher.InvokeAsync(() => ExecuteRenderCommand(command));
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

    private void ExecuteRenderCommand(string command)
    {
        switch (command)
        {
            case "ZOOM_IN":
                RealtimeRenderControl.EnlargeView();
                break;
            case "ZOOM_OUT":
                RealtimeRenderControl.ShrinkView();
                break;
            case "ACTUAL":
                RealtimeRenderControl.InitViewSize();
                break;
        }
    }

    private async Task SendEventAsync(string message)
    {
        if (_eventPipeName is null || _closed)
        {
            return;
        }

        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                _eventPipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(3_000).ConfigureAwait(false);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            await writer.WriteLineAsync(message).ConfigureAwait(false);
        }
        catch
        {
            // 父宿主可能正在关闭。
        }
    }

    private void ShowError(string message)
    {
        ErrorTextBlock.Text = message;
        ErrorPanel.Visibility = Visibility.Visible;
    }

    private void RealtimeWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _commandCancellation.Cancel();
        try
        {
            if (_procedure is not null)
            {
                _procedure.ContinuousRunEnable = false;
            }
        }
        catch
        {
        }

        try
        {
            RealtimeRenderControl.Dispose();
        }
        catch
        {
        }

        try
        {
            VmSolution.Instance?.CloseSolution();
            VmSolution.Instance?.Dispose();
        }
        catch
        {
        }
    }

    private static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? ""));

    private static bool HasVmErrorCode(Exception exception, int errorCode)
    {
        var vmException = FindVmException(exception) ?? VmSolution.GetVmException(exception);
        return vmException?.errorCode == errorCode;
    }

    private static string FormatException(Exception exception)
    {
        var vmException = FindVmException(exception) ?? VmSolution.GetVmException(exception);
        return vmException is null
            ? exception.Message
            : $"{vmException.Message}（错误码 0x{vmException.errorCode:X8}）";
    }

    private static VmException? FindVmException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is VmException vmException)
            {
                return vmException;
            }
        }

        return null;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr newParent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr window, int index, int newStyle);

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

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
