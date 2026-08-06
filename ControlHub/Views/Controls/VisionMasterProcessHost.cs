using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using ControlHub.Services.Persistence;

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
    private const int MaximumInspectionBlobResultCount = 10;

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
    private IntPtr _activeDisplayWindow;
    private bool _disposed;
    private string? _solutionPath;
    private VisionProcedureNames _procedureNames = new();

    public event EventHandler? Started;

    public event EventHandler<VisionMasterHostFailedEventArgs>? Failed;

    public event EventHandler? Exited;

    public event EventHandler<VisionClickTargetEventArgs>? ClickTargetReceived;

    public event EventHandler<VisionClickTargetFailedEventArgs>? ClickTargetFailed;

    public event EventHandler<CalibrationToolbarActionEventArgs>? CalibrationToolbarActionRequested;

    public event EventHandler<CalibrationSidebarActionEventArgs>? CalibrationSidebarActionRequested;

    public string? SolutionPath => _solutionPath;

    public VisionProcedureNames ProcedureNames => ProductRecipeStore.Clone(_procedureNames);

    public void SetSolutionPath(string? solutionPath)
    {
        _solutionPath = string.IsNullOrWhiteSpace(solutionPath)
            ? null
            : Path.GetFullPath(solutionPath);
    }

    public void SetProcedureNames(VisionProcedureNames procedureNames)
    {
        ArgumentNullException.ThrowIfNull(procedureNames);
        procedureNames.Validate();
        _procedureNames = ProductRecipeStore.Clone(procedureNames);
    }

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

        if (GetActiveDisplayWindow() == IntPtr.Zero)
        {
            await Dispatcher.InvokeAsync(
                () => { },
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        if (GetActiveDisplayWindow() == IntPtr.Zero)
        {
            RaiseFailed("嵌入窗口尚未创建。 ");
            return;
        }

        Process? exitedProcess;
        lock (_syncRoot)
        {
            if (IsProcessRunning(_process))
            {
                return;
            }

            // 已退出的旧进程仍可能触发 Exited 事件；先脱离并清理它，
            // 再创建新的监控对象，避免启动等待线程读取到已释放的 Process。
            exitedProcess = _process;
            _process = null;
            _visionWindow = IntPtr.Zero;
            _startCancellation?.Dispose();
            _startCancellation = new CancellationTokenSource();
        }

        DisposeProcessNoThrow(exitedProcess);

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
            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = Path.GetDirectoryName(executablePath)!,
                UseShellExecute = false,
                CreateNoWindow = false
            };
            startInfo.ArgumentList.Add("--embedded");
            startInfo.ArgumentList.Add("--parent-pid");
            startInfo.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
            startInfo.ArgumentList.Add("--pipe-name");
            startInfo.ArgumentList.Add(_pipeName);
            startInfo.ArgumentList.Add("--event-pipe-name");
            startInfo.ArgumentList.Add(_eventPipeName);
            if (!string.IsNullOrWhiteSpace(_solutionPath))
            {
                startInfo.ArgumentList.Add("--solution-path");
                startInfo.ArgumentList.Add(_solutionPath);
            }
            AddProcedureNameArgument(startInfo, "--inspection-procedure", _procedureNames.Inspection);
            AddProcedureNameArgument(startInfo, "--nozzle-teaching-procedure", _procedureNames.NozzleTeaching);
            AddProcedureNameArgument(startInfo, "--calibration-procedure", _procedureNames.Calibration);
            AddProcedureNameArgument(startInfo, "--lower-calibration-procedure", _procedureNames.LowerCameraCalibration);
            AddProcedureNameArgument(startInfo, "--rotation-point-procedure", _procedureNames.RotationPoint);
            AddProcedureNameArgument(startInfo, "--rotation-center-procedure", _procedureNames.RotationCenter);
            AddProcedureNameArgument(startInfo, "--lower-correction-procedure", _procedureNames.LowerCameraCorrection);

            process = new Process
            {
                StartInfo = startInfo,
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

    private static void AddProcedureNameArgument(
        ProcessStartInfo startInfo,
        string optionName,
        string procedureName)
    {
        startInfo.ArgumentList.Add(optionName);
        startInfo.ArgumentList.Add(procedureName);
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
        bool lowerCamera,
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
                lowerCamera ? "Lower" : "Keep",
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

    public Task<string> ActivateCalibrationViewAsync(CancellationToken cancellationToken)
    {
        return SendCalibrationCommandAsync("ACTIVATE_CALIBRATION_VIEW", cancellationToken);
    }

    public Task<string> SetCalibrationProcedureAsync(
        bool lowerCamera,
        CancellationToken cancellationToken)
    {
        return SendCalibrationCommandAsync(
            $"SET_CALIBRATION_PROCEDURE\t{(lowerCamera ? "Lower" : "Standard")}",
            cancellationToken);
    }

    public Task<string> ActivateInspectionViewAsync(CancellationToken cancellationToken)
    {
        return SendCalibrationCommandAsync("ACTIVATE_INSPECTION_VIEW", cancellationToken);
    }

    public Task<string> DeactivateCalibrationViewAsync(CancellationToken cancellationToken)
    {
        return SendCalibrationCommandAsync("DEACTIVATE_CALIBRATION_VIEW", cancellationToken);
    }

    public Task<string> StartLivePreviewAsync(CancellationToken cancellationToken)
    {
        return SendCalibrationCommandAsync("START_LIVE_PREVIEW", cancellationToken);
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

    public Task<string> SetCalibrationToolbarStateAsync(
        string calibrationFilePath,
        bool pathEnabled,
        bool chooseEnabled,
        bool importEnabled,
        bool loadProfileEnabled,
        bool saveProfileEnabled,
        bool simplifiedMode,
        CancellationToken cancellationToken)
    {
        var encodedPath = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(calibrationFilePath?.Trim() ?? string.Empty));
        return SendCalibrationCommandAsync(
            string.Join(
                "\t",
                "SET_CALIBRATION_TOOLBAR_STATE",
                encodedPath,
                pathEnabled ? "1" : "0",
                chooseEnabled ? "1" : "0",
                importEnabled ? "1" : "0",
                loadProfileEnabled ? "1" : "0",
                saveProfileEnabled ? "1" : "0",
                simplifiedMode ? "1" : "0"),
            cancellationToken);
    }

    public Task<string> ShowCalibrationSaveFeedbackAsync(
        bool success,
        string message,
        CancellationToken cancellationToken)
    {
        var encodedMessage = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(message?.Trim() ?? string.Empty));
        return SendCalibrationCommandAsync(
            $"SET_CALIBRATION_SAVE_FEEDBACK\t{(success ? "1" : "0")}\t{encodedMessage}",
            cancellationToken);
    }

    public Task<string> SetCalibrationSidebarStateAsync(
        CalibrationSidebarState state,
        CancellationToken cancellationToken)
    {
        static string Encode(string? value)
        {
            return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
        }

        return SendCalibrationCommandAsync(
            string.Join(
                "\t",
                "SET_CALIBRATION_SIDEBAR_STATE",
                Encode(state.CenterXPulse),
                Encode(state.CenterYPulse),
                Encode(state.CenterVm),
                Encode(state.StepX),
                Encode(state.StepY),
                Encode(state.MovePriority),
                Encode(state.Velocity),
                Encode(state.SettleMilliseconds),
                Encode(state.NozzleTeachStep),
                Encode(state.RecordNozzleContent),
                Encode(state.NozzleStatus),
                Encode(state.ClickTarget),
                state.ClickMoveChecked ? "1" : "0",
                Encode(state.ClickMoveStatus),
                state.StartLivePreviewEnabled ? "1" : "0",
                state.RecordCenterEnabled ? "1" : "0",
                state.StartCalibrationEnabled ? "1" : "0",
                state.CalibrationRunning ? "1" : "0",
                state.ParameterInputsEnabled ? "1" : "0",
                state.RecordCameraEnabled ? "1" : "0",
                state.RecordNozzleEnabled ? "1" : "0",
                state.ClickTargetEnabled ? "1" : "0",
                state.EnableClickMoveEnabled ? "1" : "0",
                state.ReturnCameraCenterEnabled ? "1" : "0",
                state.StopClickMoveEnabled ? "1" : "0",
                Encode(state.CenterVmColor),
                Encode(state.NozzleStatusColor),
                Encode(state.ClickMoveStatusColor),
                Encode(state.WorkflowStatus),
                state.RecordNozzleDotPositionEnabled ? "1" : "0",
                state.SimplifiedMode ? "1" : "0",
                Encode(state.LowerCameraNozzleName),
                state.RotationCenterEnabled ? "1" : "0",
                state.RotationCenterRunning ? "1" : "0",
                Encode(state.RotationCenterStatus),
                state.LowerCameraCorrectionTestEnabled ? "1" : "0",
                state.LowerCameraCorrectionTestRunning ? "1" : "0",
                Encode(state.LowerCameraCorrectionTestStatus)),
            cancellationToken);
    }

    public async Task<VisionRotationPoint> CaptureRotationCenterPointAsync(
        CancellationToken cancellationToken)
    {
        var response = await SendCalibrationCommandAsync(
            "RUN_ROTATION_CENTER_CAPTURE",
            cancellationToken);
        var parts = response.Split('\t');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.IsFinite(x) ||
            !double.IsFinite(y))
        {
            throw new InvalidDataException("VisionMaster 返回的矩形中心点无效。");
        }

        return new VisionRotationPoint(x, y);
    }

    public async Task<VisionRotationCenterResult> CalculateRotationCenterAsync(
        IReadOnlyList<VisionRotationPoint> points,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (points.Count != 3 || points.Any(point => !double.IsFinite(point.X) || !double.IsFinite(point.Y)))
        {
            throw new ArgumentException("计算旋转中心必须提供三个有效中心点。", nameof(points));
        }

        var commandParts = new List<string> { "CALCULATE_ROTATION_CENTER" };
        foreach (var point in points)
        {
            commandParts.Add(point.X.ToString("R", CultureInfo.InvariantCulture));
            commandParts.Add(point.Y.ToString("R", CultureInfo.InvariantCulture));
        }

        var response = await SendCalibrationCommandAsync(
            string.Join("\t", commandParts),
            cancellationToken);
        var parts = response.Split('\t');
        if (parts.Length != 2 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerX) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var centerY) ||
            !double.IsFinite(centerX) ||
            !double.IsFinite(centerY))
        {
            throw new InvalidDataException("VisionMaster 返回的旋转中心无效。");
        }

        return new VisionRotationCenterResult(centerX, centerY);
    }

    public async Task<VisionLowerCameraCorrectionResult> RunLowerCameraCorrectionAsync(
        double circleCenterX,
        double circleCenterY,
        string calibrationFilePath,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(circleCenterX) || !double.IsFinite(circleCenterY))
        {
            throw new ArgumentOutOfRangeException(nameof(circleCenterX), "下相机旋转圆心必须是有效数字。");
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationFilePath);
        var response = await SendCalibrationCommandAsync(
            string.Join(
                "\t",
                "RUN_LOWER_CAMERA_CORRECTION",
                circleCenterX.ToString("R", CultureInfo.InvariantCulture),
                circleCenterY.ToString("R", CultureInfo.InvariantCulture),
                Convert.ToBase64String(Encoding.UTF8.GetBytes(calibrationFilePath))),
            cancellationToken);
        var parts = response.Split('\t');
        if (parts.Length != 3 ||
            !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var angle) ||
            !double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(angle))
        {
            throw new InvalidDataException("VisionMaster 返回的下相机纠偏偏差或夹角无效。");
        }

        return new VisionLowerCameraCorrectionResult(x, y, angle);
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
    /// 在按需加载的固定视觉方案中，单次执行
    /// “找芯片流程 → Blob分析1”，返回结果表全部矩形与像素质心。
    /// </summary>
    public Task<VisionRectangleBlobResult> RunRectangleBlobInspectionAsync(
        CancellationToken cancellationToken)
    {
        return RunTwoPointBlobInspectionAsync("RUN_RECTANGLE_BLOB", cancellationToken);
    }

    /// <summary>
    /// 手动触发固定方案中的“粗定位示教流程”，返回“Blob分析1”结果表前两行的质心 X/Y。
    /// </summary>
    public Task<VisionRectangleBlobResult> RunNozzlePointInspectionAsync(
        CancellationToken cancellationToken)
    {
        return RunTwoPointBlobInspectionAsync("RUN_NOZZLE_POINTS", cancellationToken);
    }

    private async Task<VisionRectangleBlobResult> RunTwoPointBlobInspectionAsync(
        string command,
        CancellationToken cancellationToken)
    {
        var response = await SendCalibrationCommandAsync(
            command,
            cancellationToken);
        var parts = response.Split('\t');
        if (parts.Length < 4 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var resultCount) ||
            resultCount < 0 ||
            resultCount > MaximumInspectionBlobResultCount ||
            parts.Length != 1 + resultCount * 6 + 3)
        {
            throw new InvalidDataException("VisionMaster 返回的Blob检测图或矩形结果无效。");
        }

        var rectangles = new List<VisionBlobRectangle>(resultCount);
        for (var resultIndex = 0; resultIndex < resultCount; resultIndex++)
        {
            var offset = 1 + resultIndex * 6;
            if (!double.TryParse(parts[offset], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
                !double.TryParse(parts[offset + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
                !double.TryParse(parts[offset + 2], NumberStyles.Float, CultureInfo.InvariantCulture, out var left) ||
                !double.TryParse(parts[offset + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out var top) ||
                !double.TryParse(parts[offset + 4], NumberStyles.Float, CultureInfo.InvariantCulture, out var width) ||
                !double.TryParse(parts[offset + 5], NumberStyles.Float, CultureInfo.InvariantCulture, out var height) ||
                !double.IsFinite(x) ||
                !double.IsFinite(y) ||
                !double.IsFinite(left) ||
                !double.IsFinite(top) ||
                !double.IsFinite(width) ||
                !double.IsFinite(height) ||
                x < 0 ||
                y < 0 ||
                width <= 0 ||
                height <= 0)
            {
                throw new InvalidDataException(
                    $"VisionMaster 返回的第{resultIndex + 1}个Blob矩形结果无效。");
            }

            rectangles.Add(new VisionBlobRectangle(x, y, left, top, width, height));
        }

        var imageOffset = 1 + resultCount * 6;
        if (!int.TryParse(
                parts[imageOffset],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var imageWidth) ||
            !int.TryParse(
                parts[imageOffset + 1],
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var imageHeight) ||
            !((imageWidth > 0 &&
               imageHeight > 0 &&
               !string.IsNullOrWhiteSpace(parts[imageOffset + 2])) ||
              (imageWidth == 0 &&
               imageHeight == 0 &&
               string.IsNullOrWhiteSpace(parts[imageOffset + 2]))))
        {
            throw new InvalidDataException("VisionMaster 返回的Blob检测图信息无效。");
        }

        return new VisionRectangleBlobResult(
            rectangles,
            parts[imageOffset + 2],
            imageWidth,
            imageHeight);
    }

    public void AttachDisplayHost(IntPtr displayHostWindow)
    {
        if (_disposed || displayHostWindow == IntPtr.Zero)
        {
            return;
        }

        _activeDisplayWindow = displayHostWindow;
        if (_visionWindow == IntPtr.Zero)
        {
            return;
        }

        ReparentVisionWindow(displayHostWindow);
        ResizeVisionWindow();
    }

    public void UseDefaultDisplayHost()
    {
        if (_disposed)
        {
            return;
        }

        _activeDisplayWindow = IntPtr.Zero;
        if (_hostWindow != IntPtr.Zero && _visionWindow != IntPtr.Zero)
        {
            ReparentVisionWindow(_hostWindow);
            ResizeVisionWindow();
        }
    }

    public void RefreshDisplayHost()
    {
        ResizeVisionWindow();
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
                if (!IsProcessRunning(_process))
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
        if (parts.Length == 3 &&
            string.Equals(parts[0], "CALIBRATION_SIDEBAR_ACTION", StringComparison.Ordinal))
        {
            string value;
            try
            {
                value = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
            }
            catch (FormatException)
            {
                value = string.Empty;
            }

            _ = Dispatcher.BeginInvoke(
                () => CalibrationSidebarActionRequested?.Invoke(
                    this,
                    new CalibrationSidebarActionEventArgs(parts[1], value)));
            return;
        }

        if (parts.Length == 2 &&
            string.Equals(parts[0], "CALIBRATION_TOOLBAR_ACTION", StringComparison.Ordinal) &&
            Enum.TryParse<CalibrationToolbarAction>(parts[1], ignoreCase: false, out var toolbarAction))
        {
            _ = Dispatcher.BeginInvoke(
                () => CalibrationToolbarActionRequested?.Invoke(
                    this,
                    new CalibrationToolbarActionEventArgs(toolbarAction)));
            return;
        }

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
                try
                {
                    process.Refresh();
                    if (process.HasExited)
                    {
                        throw new InvalidOperationException(
                            $"VisionMasterHost 已提前退出（代码 {process.ExitCode}）。");
                    }

                    var windowHandle = process.MainWindowHandle;
                    if (windowHandle == IntPtr.Zero)
                    {
                        windowHandle = FindVisibleTopLevelWindow(process.Id);
                    }

                    if (windowHandle != IntPtr.Zero)
                    {
                        return windowHandle;
                    }
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (ObjectDisposedException exception)
                {
                    throw new InvalidOperationException(
                        "VisionMasterHost 启动期间进程句柄已失效。",
                        exception);
                }
                catch (InvalidOperationException) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
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
        if (GetActiveDisplayWindow() == IntPtr.Zero || windowHandle == IntPtr.Zero)
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

        var displayHostWindow = GetActiveDisplayWindow();
        Marshal.SetLastPInvokeError(0);
        var previousParent = SetParent(windowHandle, displayHostWindow);
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

        if (GetParent(windowHandle) != displayHostWindow)
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
        var displayHostWindow = GetActiveDisplayWindow();
        if (displayHostWindow == IntPtr.Zero || _visionWindow == IntPtr.Zero)
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException("VisionMaster 承载窗口尚未准备完成。 ");
            }

            return;
        }

        if (!GetClientRect(displayHostWindow, out var bounds))
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

    private IntPtr GetActiveDisplayWindow()
    {
        return _activeDisplayWindow != IntPtr.Zero ? _activeDisplayWindow : _hostWindow;
    }

    private void ReparentVisionWindow(IntPtr displayHostWindow)
    {
        if (_visionWindow == IntPtr.Zero || displayHostWindow == IntPtr.Zero)
        {
            return;
        }

        if (!IsWindow(_visionWindow))
        {
            throw new Win32Exception("VisionMaster 窗口句柄已经失效。");
        }

        Marshal.SetLastPInvokeError(0);
        var previousParent = SetParent(_visionWindow, displayHostWindow);
        var setParentError = Marshal.GetLastPInvokeError();
        if (previousParent == IntPtr.Zero && setParentError != 0)
        {
            throw new Win32Exception(setParentError, "无法切换 VisionMaster 显示承载窗口。");
        }

        if (GetParent(_visionWindow) != displayHostWindow)
        {
            _ = SetParent(_visionWindow, previousParent);
            throw new Win32Exception("VisionMaster 显示承载窗口验证失败。");
        }

        _ = ShowWindow(_visionWindow, SwShow);
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

            _visionWindow = IntPtr.Zero;
            shouldNotify = !_disposed;
        }

        if (shouldNotify)
        {
            _ = Dispatcher.BeginInvoke(() => Exited?.Invoke(this, EventArgs.Empty));
        }
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
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private void DisposeProcessNoThrow(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            process.Exited -= VisionProcess_Exited;
        }
        catch
        {
        }

        try
        {
            process.Dispose();
        }
        catch
        {
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

public enum CalibrationToolbarAction
{
    SaveLocation,
    Import,
    LoadProfile,
    SaveProfile
}

public sealed class CalibrationToolbarActionEventArgs(CalibrationToolbarAction action) : EventArgs
{
    public CalibrationToolbarAction Action { get; } = action;
}

public sealed class CalibrationSidebarActionEventArgs(string action, string value) : EventArgs
{
    public string Action { get; } = action;

    public string Value { get; } = value;
}

public sealed record CalibrationSidebarState(
    string CenterXPulse,
    string CenterYPulse,
    string CenterVm,
    string StepX,
    string StepY,
    string MovePriority,
    string Velocity,
    string SettleMilliseconds,
    string NozzleTeachStep,
    string RecordNozzleContent,
    string NozzleStatus,
    string ClickTarget,
    bool ClickMoveChecked,
    string ClickMoveStatus,
    bool StartLivePreviewEnabled,
    bool RecordCenterEnabled,
    bool StartCalibrationEnabled,
    bool CalibrationRunning,
    bool ParameterInputsEnabled,
    bool RecordCameraEnabled,
    bool RecordNozzleEnabled,
    bool ClickTargetEnabled,
    bool EnableClickMoveEnabled,
    bool ReturnCameraCenterEnabled,
    bool StopClickMoveEnabled,
    string CenterVmColor,
    string NozzleStatusColor,
    string ClickMoveStatusColor,
    string WorkflowStatus,
    bool RecordNozzleDotPositionEnabled,
    bool SimplifiedMode,
    string LowerCameraNozzleName,
    bool RotationCenterEnabled,
    bool RotationCenterRunning,
    string RotationCenterStatus,
    bool LowerCameraCorrectionTestEnabled,
    bool LowerCameraCorrectionTestRunning,
    string LowerCameraCorrectionTestStatus);

public sealed record VisionRotationPoint(double X, double Y);

public sealed record VisionRotationCenterResult(double CenterX, double CenterY);

public sealed record VisionLowerCameraCorrectionResult(
    double CorrectionX,
    double CorrectionY,
    double MeasuredAngle);

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
    IReadOnlyList<VisionBlobRectangle> Rectangles,
    string ImagePath,
    int ImageWidth,
    int ImageHeight)
{
    public VisionBlobRectangle Rectangle1 =>
        Rectangles.Count >= 1
            ? Rectangles[0]
            : throw new InvalidOperationException("Blob结果中没有第1个矩形。");

    public VisionBlobRectangle Rectangle2 =>
        Rectangles.Count >= 2
            ? Rectangles[1]
            : throw new InvalidOperationException("Blob结果中没有第2个矩形。");
}
