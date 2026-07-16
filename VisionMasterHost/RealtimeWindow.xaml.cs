using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.Interface;

namespace VisionMasterHost;

public partial class RealtimeWindow : Window
{
    private const string SolutionFileName = "实时画面.sol";
    private const string ProcedureName = "流程1";
    private const string ImageSourceName = "图像源1";
    private const int AlreadyContinuousErrorCode = unchecked((int)0xE0000311);

    private readonly string? _eventPipeName;
    private readonly string? _commandPipeName;
    private readonly CancellationTokenSource _commandCancellation = new();
    private Task? _commandTask;
    private VmProcedure? _procedure;
    private bool _closed;

    public RealtimeWindow(string? eventPipeName, string? commandPipeName)
    {
        InitializeComponent();
        _eventPipeName = string.IsNullOrWhiteSpace(eventPipeName) ? null : eventPipeName;
        _commandPipeName = string.IsNullOrWhiteSpace(commandPipeName) ? null : commandPipeName;
    }

    public void ReportSdkInitializationFailure(Exception exception)
    {
        var message = FormatException(exception);
        ShowError(message);
        _ = SendEventAsync($"LIVE_ERROR\t{Encode(message)}");
    }

    private void RealtimeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartCommandServer();
        try
        {
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
            _ = SendEventAsync("LIVE_READY");
        }
        catch (Exception exception)
        {
            var message = FormatException(exception);
            ShowError(message);
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
        catch (Exception exception) when (HasVmErrorCode(exception, AlreadyContinuousErrorCode))
        {
            // 方案保存时已经处于连续运行，视为启动成功。
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
}
