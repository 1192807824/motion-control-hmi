using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using CalculatorModuleCs;
using GlobalVariableModuleCs;
using IMVSBlobFindModuCs;
using IMVSCalibTransformModuCs;
using IMVSCircleFindModuCs;
using IMVSCircleFitModuCs;
using IMVSLineFindModuCs;
using IMVSNPointCalibModuCs;
using IMVSRotateCalculateModuCs;
using ShellModuleCs;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.Interface;

namespace VisionMasterHost;

public partial class MainWindow : Window
{
    private const string FixedSolutionFileName = "新纳方案.sol";
    private const string FallbackSolutionFileName = "标定方案.sol";
    private const string DefaultInspectionProcedureName = "找芯片流程";
    private const string DefaultNozzlePointProcedureName = "粗定位示教流程";
    private const string DefaultCalibrationProcedureName = "标定流程";
    private const string DefaultLowerCameraCalibrationProcedureName = "下相机标定流程";
    private const string CalibrationImageSourceName = "图像源1";
    private const string NPointCalibrationModuleName = "N点标定1";
    private const string CalibrationTransformModuleName = "标定转换1";
    private const string InspectionBlobModuleName = "Blob分析1";
    private const string InspectionScriptModuleName = "脚本1";
    private const string Nozzle1CircleModuleName = "圆查找1";
    private const string Nozzle2CircleModuleName = "圆查找2";
    private const string DefaultRotationPointProcedureName = "获取三点流程";
    private const string DefaultRotationCenterProcedureName = "计算旋转中心";
    private const string RotationCenterCircleModuleName = "圆拟合1";
    private const string DefaultLowerCameraCorrectionProcedureName = "下相机纠偏";
    private const string LowerCameraCorrectionImageSourceName = "图像源1";
    private const string LowerCameraCorrectionRotationModuleName = "旋转计算2";
    private const string LowerCameraCorrectionTransformModuleName = "标定转换3";
    private const string LowerCameraCorrectionCenterTransformModuleName = "标定转换4";
    private const uint LowerCameraCorrectionResultModuleId = 85;
    private const string GlobalVariableModuleName = "全局变量1";

    private readonly VisionCalibrationSettings _settings = VisionCalibrationSettings.Load();
    private readonly bool _embedded;
    private readonly string? _commandPipeName;
    private readonly string? _eventPipeName;
    private readonly CancellationTokenSource _commandPipeCancellation = new();
    private readonly object _commandPipeSync = new();
    private VmProcedure? _previewProcedure;
    private VmProcedure? _inspectionProcedure;
    private VmProcedure? _nozzlePointProcedure;
    private VmProcedure? _calibrationProcedure;
    private IMVSCalibTransformModuTool? _standaloneTransformModule;
    private IMVSBlobFindModuTool? _standaloneBlobModule;
    private VmModule? _displayedModule;
    private VmModule? _crosshairModule;
    private EventHandler? _crosshairModuleResultHandler;
    private NamedPipeServerStream? _activeCommandPipe;
    private Task? _commandPipeTask;
    private VisionCalibrationSession? _calibrationSession;
    private string _loadedSolutionPath = "";
    private bool _solutionLoaded;
    private bool _closed;
    private bool _sdkAvailable = true;
    private bool _busy;
    private bool _initializingFixedSolution;
    private bool _calibrationViewActive;
    private int _calibrationRenderGeneration;
    private bool _clickMoveEnabled;
    private bool _clickTransformBusy;
    private bool _applyingCalibrationSidebarState;
    private bool _clickCenterPixelReady;
    private float _clickCenterPixelX;
    private float _clickCenterPixelY;
    private int _clickImagePixelWidth;
    private int _clickImagePixelHeight;
    private int _clickCenterInitializationQueued;
    private int _liveRenderGeneration;
    private bool _livePreviewRenderReady;
    private TaskCompletionSource<int>? _livePreviewFirstFrameSource;
    private TaskCompletionSource<bool> _fixedSolutionLoadSource =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _fixedSolutionLoadError = "固定视觉方案尚未开始加载。";
    private string _clickCalibrationPath = "";
    private string? _fullscreenRenderTarget;
    private bool _showingCalibrationRender;
    private bool _showingSplitRender;
    private bool _nozzleTeachingResultsDisplayed;
    private bool _manualNozzleCircleMode;
    private readonly List<Point> _manualNozzle1CirclePoints = [];
    private readonly List<Point> _manualNozzle2CirclePoints = [];
    private ManualNozzleCircle? _manualNozzle1Circle;
    private ManualNozzleCircle? _manualNozzle2Circle;
    private Point? _automaticNozzle1Center;
    private Point? _automaticNozzle2Center;
    private string _activeCalibrationProcedureName;
    private readonly string? _configuredSolutionPath;
    private readonly string _inspectionProcedureName;
    private readonly string _nozzlePointProcedureName;
    private readonly string _calibrationProcedureName;
    private readonly string _lowerCameraCalibrationProcedureName;
    private readonly string _rotationPointProcedureName;
    private readonly string _rotationCenterProcedureName;
    private readonly string _lowerCameraCorrectionProcedureName;

    public MainWindow(
        bool embedded,
        string? commandPipeName = null,
        string? eventPipeName = null,
        string? solutionPath = null,
        string? inspectionProcedureName = null,
        string? nozzlePointProcedureName = null,
        string? calibrationProcedureName = null,
        string? lowerCameraCalibrationProcedureName = null,
        string? rotationPointProcedureName = null,
        string? rotationCenterProcedureName = null,
        string? lowerCameraCorrectionProcedureName = null)
    {
        InitializeComponent();
        _embedded = embedded;
        _commandPipeName = string.IsNullOrWhiteSpace(commandPipeName) ? null : commandPipeName;
        _eventPipeName = string.IsNullOrWhiteSpace(eventPipeName) ? null : eventPipeName;
        _configuredSolutionPath = string.IsNullOrWhiteSpace(solutionPath)
            ? null
            : Path.GetFullPath(solutionPath);
        _inspectionProcedureName = ResolveProcedureName(
            inspectionProcedureName,
            DefaultInspectionProcedureName);
        _nozzlePointProcedureName = ResolveProcedureName(
            nozzlePointProcedureName,
            DefaultNozzlePointProcedureName);
        _calibrationProcedureName = ResolveProcedureName(
            calibrationProcedureName,
            DefaultCalibrationProcedureName);
        _lowerCameraCalibrationProcedureName = ResolveProcedureName(
            lowerCameraCalibrationProcedureName,
            DefaultLowerCameraCalibrationProcedureName);
        _rotationPointProcedureName = ResolveProcedureName(
            rotationPointProcedureName,
            DefaultRotationPointProcedureName);
        _rotationCenterProcedureName = ResolveProcedureName(
            rotationCenterProcedureName,
            DefaultRotationCenterProcedureName);
        _lowerCameraCorrectionProcedureName = ResolveProcedureName(
            lowerCameraCorrectionProcedureName,
            DefaultLowerCameraCorrectionProcedureName);
        _activeCalibrationProcedureName = _calibrationProcedureName;
        if (embedded)
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
        }
        else
        {
            CalibrationToolbarPanel.Visibility = Visibility.Collapsed;
        }
    }

    public void ReportSdkInitializationFailure(Exception exception)
    {
        _sdkAvailable = false;
        var vmException = FindVmException(exception);
        var message = vmException?.errorCode == unchecked((int)0xE0000700)
            ? "未检测到 VisionMaster 加密狗或授权状态异常（0xE0000700）。"
            : FormatException(exception);
        SdkErrorTextBlock.Text = message;
        SdkErrorPanel.Visibility = Visibility.Visible;
        VisionRenderControl.IsEnabled = false;
        CalibrationRenderControl.IsEnabled = false;
        CenterCrosshair.Visibility = Visibility.Collapsed;
        ImagePlaceholder.Visibility = Visibility.Collapsed;
        CalibrationImagePlaceholder.Visibility = Visibility.Collapsed;
        UpdateCommandState();
        SetStatus(message, StatusKind.Error);
        _fixedSolutionLoadError = message;
        _fixedSolutionLoadSource.TrySetResult(false);
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        StartCommandPipeServer();
        if (!_sdkAvailable)
        {
            return;
        }

        try
        {
            VisionRenderControl.SetRenderToolbarVisible(false);
            CalibrationRenderControl.SetRenderToolbarVisible(false);
            VisionRenderControl.ChangeImageComboBoxVisibility(false);
            CalibrationRenderControl.ChangeImageComboBoxVisibility(false);
        }
        catch (Exception exception)
        {
            ReportSdkInitializationFailure(exception);
            return;
        }

        LoadFixedSolution();
    }

    private void StartCommandPipeServer()
    {
        if (_commandPipeTask is not null || string.IsNullOrWhiteSpace(_commandPipeName))
        {
            return;
        }

        _commandPipeTask = RunCommandPipeServerAsync(_commandPipeCancellation.Token);
    }

    private async Task RunCommandPipeServerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _commandPipeName!,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                lock (_commandPipeSync)
                {
                    _activeCommandPipe = pipe;
                }

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                using (var reader = new StreamReader(
                           pipe,
                           new UTF8Encoding(false),
                           false,
                           1024,
                           leaveOpen: true))
                using (var writer = new StreamWriter(
                           pipe,
                           new UTF8Encoding(false),
                           1024,
                           leaveOpen: true)
                       {
                           AutoFlush = true
                       })
                {
                    var command = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (command is null)
                    {
                        continue;
                    }

                    var responseTask = await Dispatcher.InvokeAsync(
                        () => ExecuteCalibrationCommandSafelyAsync(command));
                    var response = await responseTask.ConfigureAwait(false);
                    await writer.WriteLineAsync(response).ConfigureAwait(false);
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
                // The client may disconnect between a command and its reply; accept the next command.
            }
            finally
            {
                lock (_commandPipeSync)
                {
                    if (ReferenceEquals(_activeCommandPipe, pipe))
                    {
                        _activeCommandPipe = null;
                    }
                }

                pipe?.Dispose();
            }
        }
    }

    private async Task<string> ExecuteCalibrationCommandSafelyAsync(string command)
    {
        try
        {
            var commandName = command.Split('\t')[0];
            var result = commandName switch
            {
                "ACTIVATE_CALIBRATION_VIEW" => await ActivateCalibrationViewAsync(),
                "SET_CALIBRATION_PROCEDURE" => await SetCalibrationProcedureAsync(command.Split('\t')),
                "ACTIVATE_INSPECTION_VIEW" => await ActivateInspectionViewAsync(),
                "DEACTIVATE_CALIBRATION_VIEW" => await DeactivateCalibrationViewAsync(),
                "START_LIVE_PREVIEW" => await StartLivePreviewFromCommandAsync(),
                "SET_CLICK_MODE" => await SetClickMoveModeAsync(command.Split('\t')),
                "PREPARE" => await PrepareNinePointCalibrationAsync(command.Split('\t')),
                "CAPTURE" => await CaptureNinePointCalibrationAsync(command.Split('\t')),
                "COMPLETE" => await CompleteNinePointCalibrationAsync(),
                "ABORT" => await AbortNinePointCalibrationAsync(),
                _ => ExecuteCalibrationCommand(command)
            };
            return EncodePipeResponse(success: true, result);
        }
        catch (Exception exception)
        {
            return EncodePipeResponse(success: false, FormatException(exception));
        }
    }

    private string ExecuteCalibrationCommand(string command)
    {
        var parts = command.Split('\t');
        return parts[0] switch
        {
            "SET_CENTER" => SetCalibrationCenter(parts),
            "PREPARE" => throw new InvalidOperationException("PREPARE must be executed asynchronously."),
            "CAPTURE" => throw new InvalidOperationException("CAPTURE must be executed asynchronously."),
            "COMPLETE" => throw new InvalidOperationException("COMPLETE must be executed asynchronously."),
            "ABORT" => throw new InvalidOperationException("ABORT must be executed asynchronously."),
            "SET_CLICK_MODE" => throw new InvalidOperationException("SET_CLICK_MODE must be executed asynchronously."),
            "IMPORT_CALIBRATION_FILE" => ImportCalibrationFile(parts),
            "SET_CALIBRATION_TOOLBAR_STATE" => SetCalibrationToolbarState(parts),
            "SET_CALIBRATION_SAVE_FEEDBACK" => SetCalibrationSaveFeedback(parts),
            "SET_CALIBRATION_SIDEBAR_STATE" => SetCalibrationSidebarState(parts),
            "START_LIVE_PREVIEW" => throw new InvalidOperationException("START_LIVE_PREVIEW must be executed asynchronously."),
            "TRANSFORM_PIXEL" => TransformPixel(parts),
            "RUN_RECTANGLE_BLOB" => RunRectangleBlobInspection(parts),
            "RUN_NOZZLE_POINTS" => RunNozzlePointInspection(parts),
            "RUN_ROTATION_CENTER_CAPTURE" => RunRotationCenterCapture(parts),
            "CALCULATE_ROTATION_CENTER" => CalculateRotationCenter(parts),
            "RUN_LOWER_CAMERA_CORRECTION" => RunLowerCameraCorrection(parts),
            _ => throw new InvalidOperationException($"不支持的视觉标定命令：{parts[0]}")
        };
    }

    private static string ResolveProcedureName(string? configuredName, string defaultName)
    {
        return string.IsNullOrWhiteSpace(configuredName) ? defaultName : configuredName!.Trim();
    }

    private async Task<string> ActivateCalibrationViewAsync()
    {
        ApplyCalibrationShellLayout();
        if (_calibrationViewActive && _calibrationProcedure is not null)
        {
            return "标定界面已经开启。";
        }

        await WaitForFixedSolutionAsync();

        await ActivateCalibrationProcedureAsync(_activeCalibrationProcedureName);
        return "视觉标定界面已开启。";
    }

    private async Task<string> SetCalibrationProcedureAsync(IReadOnlyList<string> parts)
    {
        if (parts.Count != 2)
        {
            throw new InvalidDataException("切换标定流程的参数不正确。");
        }

        var procedureName = parts[1] switch
        {
            "Lower" => _lowerCameraCalibrationProcedureName,
            "Standard" => _calibrationProcedureName,
            _ => throw new InvalidDataException("标定流程只能选择 Standard 或 Lower。")
        };
        await ActivateCalibrationProcedureAsync(procedureName);
        return "标定模式已切换。";
    }

    private async Task ActivateCalibrationProcedureAsync(string procedureName)
    {
        if (_calibrationSession is not null || _busy)
        {
            throw new InvalidOperationException("九点标定正在执行，不能切换视觉流程。");
        }

        await WaitForFixedSolutionAsync();

        var procedure = GetRequiredProcedure(procedureName);
        StopAllContinuousExecutionNoThrow();
        _activeCalibrationProcedureName = procedureName;
        _calibrationProcedure = procedure;
        _previewProcedure = procedure;
        _calibrationViewActive = true;
        CalibrationProcedureNameText.Text = procedureName;
        CalibrationProcedureComboBox.SelectedItem = procedureName;
        PreviewProcedureComboBox.SelectedItem = procedureName;
        PopulateImageSteps(procedureName, procedure);
        ApplyCalibrationShellLayout();
        ApplyCalibrationRenderLayout();
        await RefreshRenderLayoutAsync();
        ClearCalibrationRenderer();
        BindCalibrationModule(ResolveNPointCalibrationModule(procedureName));
        await RefreshRenderLayoutAsync();
        UpdateCommandState();
        SetStatus("视觉标定已就绪，点击一键九点标定后开始取像。", StatusKind.Success);
    }

    private async Task<string> DeactivateCalibrationViewAsync()
    {
        ApplySplitRenderLayout();
        await RefreshRenderLayoutAsync();
        _clickMoveEnabled = false;
        _clickCenterPixelReady = false;
        DetachCrosshairModule();
        StopAllContinuousExecutionNoThrow();
        _calibrationSession = null;
        _calibrationViewActive = false;
        ClearCalibrationRenderer();
        UpdateCommandState();
        SetStatus("已离开视觉标定：固定方案保持加载，实时画面已停止。", StatusKind.Ready);
        return "标定界面已关闭。";
    }

    private async Task<string> ActivateInspectionViewAsync()
    {
        StopAllContinuousExecutionNoThrow();
        await WaitForFixedSolutionAsync();

        _inspectionProcedure ??= GetRequiredProcedure(_inspectionProcedureName);
        _calibrationViewActive = false;
        _clickMoveEnabled = false;
        _clickCenterPixelReady = false;
        ApplyInspectionShellLayout();
        await RefreshRenderLayoutAsync();
        SetStatus("主页视觉待命：显示当前流程的图像源1", StatusKind.Ready);
        return "主页视觉图像显示已开启。";
    }

    /// <summary>
    /// 导入已有九点标定 XML。标定转换模块若未放进当前流程，则创建一个独立工具，
    /// 让主页坐标换算不再依赖“标定流程”中必须存在标定转换模块。
    /// </summary>
    private string ImportCalibrationFile(IReadOnlyList<string> parts)
    {
        if (parts.Count != 2)
        {
            throw new InvalidDataException("导入标定文件命令参数不正确。");
        }

        var fullPath = DecodeAndValidateCalibrationFilePath(parts[1]);
        var transformModule = GetCalibrationTransformModule();
        transformModule.ModuParams.LoadCalibPath = fullPath;
        SetStatus($"已导入标定文件：{Path.GetFileName(fullPath)}", StatusKind.Success);
        return $"标定文件已导入并应用：{fullPath}";
    }

    /// <summary>
    /// 在已加载的固定方案内，单次执行“找芯片流程”，再按 VisionMaster 结果表原顺序
    /// 读取“Blob分析1”的全部结果。所有相机流程均互斥、单次执行。
    /// </summary>
    private string RunRectangleBlobInspection(IReadOnlyList<string> parts)
    {
        return RunTwoPointInspection(parts, _inspectionProcedureName, ref _inspectionProcedure);
    }

    /// <summary>
    /// 单次执行“粗定位示教流程”，采集并显示两个吸嘴画面。
    /// 自动圆仅作为视觉参考，最终示教坐标由操作员在两张图上的手动画圆产生。
    /// 流程不驱动运动轴或吸嘴动作。
    /// </summary>
    private string RunNozzlePointInspection(IReadOnlyList<string> parts)
    {
        return RunTwoPointInspection(parts, _nozzlePointProcedureName, ref _nozzlePointProcedure);
    }

    private string RunRotationCenterCapture(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            throw new InvalidDataException("获取旋转中心采集点命令参数不正确。");
        }

        EnsureRotationCenterCommandReady();
        var procedure = GetRequiredProcedure(_rotationPointProcedureName);
        var blobModule = ResolveNamedBlobFindModule(
            _rotationPointProcedureName,
            InspectionBlobModuleName);
        StopAllContinuousExecutionNoThrow();
        PrepareLiveRendererForCameraAcquisition();
        procedure.Run(true);
        EnsureProcedureRunSucceeded(procedure, _rotationPointProcedureName);

        var result = blobModule.ModuResult;
        if (result is null || result.ModuStatus != 1)
        {
            throw new InvalidOperationException(
                $"{_rotationPointProcedureName}.{InspectionBlobModuleName}返回NG，请检查吸嘴图像和Blob分析参数。");
        }

        var centroids = result.CentroidPoint;
        if (result.BlobNum < 1 || centroids is null || centroids.Count < 1)
        {
            throw new InvalidOperationException(
                $"{_rotationPointProcedureName}.{InspectionBlobModuleName}未返回Blob质心。");
        }

        // 与 VisionMaster 当前结果表一致，读取第 0 行“质心X / 质心Y”。
        var centroid = centroids[0];
        if (float.IsNaN(centroid.X) || float.IsInfinity(centroid.X) ||
            float.IsNaN(centroid.Y) || float.IsInfinity(centroid.Y))
        {
            throw new InvalidOperationException("Blob分析返回的质心X/Y无效。");
        }

        BindInspectionResultModule(blobModule);
        RefreshInspectionDisplayNoThrow();
        SetStatus(
            $"获取旋转中心Blob质心完成：X={centroid.X:0.###}，Y={centroid.Y:0.###}",
            StatusKind.Success);
        return string.Join(
            "\t",
            centroid.X.ToString("R", CultureInfo.InvariantCulture),
            centroid.Y.ToString("R", CultureInfo.InvariantCulture));
    }

    private string CalculateRotationCenter(IReadOnlyList<string> parts)
    {
        if (parts.Count != 7)
        {
            throw new InvalidDataException("计算旋转中心命令必须包含三个点的六个坐标。");
        }

        EnsureRotationCenterCommandReady();
        var values = new float[6];
        for (var index = 0; index < values.Length; index++)
        {
            var value = ParseFiniteDouble(parts[index + 1], $"旋转中心参数{index + 1}");
            if (value < float.MinValue || value > float.MaxValue)
            {
                throw new InvalidDataException($"旋转中心参数{index + 1}超出float范围。");
            }

            values[index] = (float)value;
        }

        StopAllContinuousExecutionNoThrow();
        var globalVariables = ResolveGlobalVariableModule();
        globalVariables.SetVarFloat("X1", [values[0]]);
        globalVariables.SetVarFloat("Y1", [values[1]]);
        globalVariables.SetVarFloat("X2", [values[2]]);
        globalVariables.SetVarFloat("Y2", [values[3]]);
        globalVariables.SetVarFloat("X3", [values[4]]);
        globalVariables.SetVarFloat("Y3", [values[5]]);

        var procedure = GetRequiredProcedure(_rotationCenterProcedureName);
        var circleModule = ResolveNamedModule<IMVSCircleFitModuTool>(
            _rotationCenterProcedureName,
            RotationCenterCircleModuleName);
        procedure.Run(true);
        EnsureProcedureRunSucceeded(procedure, _rotationCenterProcedureName);

        var result = circleModule.ModuResult;
        if (result is null || result.ModuStatus != 1 || result.FitStatus != 1)
        {
            throw new InvalidOperationException(
                $"{_rotationCenterProcedureName}.{RotationCenterCircleModuleName}返回NG，请检查三个采集点。");
        }

        var center = result.OutputCircle?.CenterPoint
            ?? throw new InvalidOperationException(
                $"{_rotationCenterProcedureName}.{RotationCenterCircleModuleName}未返回圆心。");
        if (float.IsNaN(center.X) || float.IsInfinity(center.X) ||
            float.IsNaN(center.Y) || float.IsInfinity(center.Y))
        {
            throw new InvalidOperationException("圆拟合返回的圆心X/Y无效。");
        }

        BindInspectionResultModule(circleModule);
        RefreshInspectionDisplayNoThrow();
        SetStatus(
            $"旋转中心计算完成：X={center.X:0.###}，Y={center.Y:0.###}",
            StatusKind.Success);
        return string.Join(
            "\t",
            center.X.ToString("R", CultureInfo.InvariantCulture),
            center.Y.ToString("R", CultureInfo.InvariantCulture));
    }

    private void EnsureRotationCenterCommandReady()
    {
        if (_busy || _calibrationSession is not null)
        {
            throw new InvalidOperationException("九点标定正在执行，不能计算旋转中心。");
        }

        if (!_solutionLoaded)
        {
            LoadFixedSolution();
        }

        if (!_solutionLoaded)
        {
            throw new InvalidOperationException(
                $"固定方案尚未加载完成，请确认桌面存在“{FixedSolutionFileName}”或“{FallbackSolutionFileName}”。");
        }
    }

    private string RunLowerCameraCorrection(IReadOnlyList<string> parts)
    {
        if (parts.Count != 4)
        {
            throw new InvalidDataException(
                "下相机纠偏命令必须包含旋转圆心和当前吸嘴标定文件。");
        }

        EnsureRotationCenterCommandReady();
        var centerX = ParseFiniteDouble(parts[1], "旋转圆心X");
        var centerY = ParseFiniteDouble(parts[2], "旋转圆心Y");
        if (centerX < float.MinValue || centerX > float.MaxValue ||
            centerY < float.MinValue || centerY > float.MaxValue)
        {
            throw new InvalidDataException("下相机旋转圆心超出float范围。");
        }

        var rotationCenterX = (float)centerX;
        var rotationCenterY = (float)centerY;
        var calibrationPath = DecodeAndValidateCalibrationFilePath(parts[3]);
        var procedure = GetRequiredProcedure(_lowerCameraCorrectionProcedureName);
        var imageSourceModule = ResolveNamedModule<VmModule>(
            _lowerCameraCorrectionProcedureName,
            LowerCameraCorrectionImageSourceName);
        var rotationModule = ResolveNamedModule<IMVSRotateCalculateModuTool>(
            _lowerCameraCorrectionProcedureName,
            LowerCameraCorrectionRotationModuleName);
        var transformModule = ResolveNamedModule<IMVSCalibTransformModuTool>(
            _lowerCameraCorrectionProcedureName,
            LowerCameraCorrectionTransformModuleName);
        var centerTransformModule = ResolveNamedModule<IMVSCalibTransformModuTool>(
            _lowerCameraCorrectionProcedureName,
            LowerCameraCorrectionCenterTransformModuleName);
        var resultModule = ResolveModuleById<CalculatorModuleTool>(
            procedure,
            _lowerCameraCorrectionProcedureName,
            LowerCameraCorrectionResultModuleId);

        StopAllContinuousExecutionNoThrow();
        // 每次纠偏都把当前吸嘴对应的已标定圆心直接写入“旋转计算2”。
        rotationModule.ModuParams.RotateCenter =
        [
            new VM.PlatformSDKCS.PointF { X = rotationCenterX, Y = rotationCenterY }
        ];

        // 新纠偏支路的两个标定转换必须同时使用当前吸嘴自己的九点标定文件。
        transformModule.ModuParams.LoadCalibPath = calibrationPath;
        centerTransformModule.ModuParams.LoadCalibPath = calibrationPath;

        PrepareLiveRendererForCameraAcquisition();
        try
        {
            procedure.Run(true);
        }
        finally
        {
            // 显示与流程判定解耦：即使流程返回 NG 或 Run 抛异常，也先绑定
            // “旋转计算2”显示底图及全部上游渲染叠加；该节点没有图像时再回退原图。
            ShowLowerCameraCorrectionResultNoThrow(rotationModule, imageSourceModule);
        }

        EnsureProcedureRunSucceeded(procedure, _lowerCameraCorrectionProcedureName);

        var result = resultModule.ModuResult;
        if (result is null || result.ModuStatus != 1)
        {
            throw new InvalidOperationException(
                $"{_lowerCameraCorrectionProcedureName}的85号计算模块返回NG。");
        }

        // 85号计算模块已经汇总标定转换3/4和旋转计算2，应用程序只读取最终 X/Y/R。
        var correctionX = ReadSingleCalculatorFloat(result, "X", LowerCameraCorrectionResultModuleId);
        var correctionY = ReadSingleCalculatorFloat(result, "Y", LowerCameraCorrectionResultModuleId);
        var correctionR = ReadSingleCalculatorFloat(result, "R", LowerCameraCorrectionResultModuleId);

        SetStatus(
            $"下相机纠偏完成：X偏差={correctionX:0.00000}，Y偏差={correctionY:0.00000}，角度={correctionR:0.00000}°",
            StatusKind.Success);
        return string.Join(
            "\t",
            correctionX.ToString("R", CultureInfo.InvariantCulture),
            correctionY.ToString("R", CultureInfo.InvariantCulture),
            correctionR.ToString("R", CultureInfo.InvariantCulture));
    }

    private void ShowLowerCameraCorrectionResultNoThrow(
        VmModule renderedResultModule,
        VmModule imageSourceModule)
    {
        try
        {
            BindInspectionResultModule(renderedResultModule);
        }
        catch
        {
            TryBindInspectionResultModuleNoThrow(imageSourceModule);
            return;
        }

        RefreshInspectionDisplayNoThrow();
        QueueLowerCameraCorrectionResultRefreshNoThrow(
            imageSourceModule,
            DispatcherPriority.Render,
            fallbackToImageSource: false);
        QueueLowerCameraCorrectionResultRefreshNoThrow(
            imageSourceModule,
            DispatcherPriority.Background,
            fallbackToImageSource: true);
    }

    private static TModule ResolveModuleById<TModule>(
        VmProcedure procedure,
        string procedureName,
        uint moduleId)
        where TModule : VmModule
    {
        if (procedure.GetModuleByID(moduleId) is TModule module)
        {
            return module;
        }

        throw new InvalidOperationException(
            $"固定方案的“{procedureName}”中未找到类型正确的{moduleId}号模块。");
    }

    private static float ReadSingleCalculatorFloat(
        CalculatorResult result,
        string outputName,
        uint moduleId)
    {
        FloatDataArray output;
        try
        {
            output = result.GetOutputFloat(outputName);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{moduleId}号计算模块未发布浮点结果“{outputName}”。",
                exception);
        }

        var values = output.pFloatVal;
        if (output.nValueNum < 1 || values is null || values.Length < 1)
        {
            throw new InvalidOperationException(
                $"{moduleId}号计算模块的“{outputName}”没有返回结果。");
        }

        var value = values[0];
        if (float.IsNaN(value) || float.IsInfinity(value))
        {
            throw new InvalidOperationException(
                $"{moduleId}号计算模块的“{outputName}”结果无效。");
        }

        return value;
    }

    private static void EnsureProcedureRunSucceeded(VmProcedure procedure, string procedureName)
    {
        if (procedure.GetIsExecuteNormal() == 1)
        {
            return;
        }

        var errors = procedure.GetModuErrorInfoList();
        var details = errors is null
            ? ""
            : string.Join(
                "；",
                errors.Select(error =>
                    $"{error.strDisplayName}(0x{unchecked((uint)error.nErrorCode):X8})"));
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(details)
                ? $"固定方案中的{procedureName}执行异常。"
                : $"固定方案中的{procedureName}执行异常：{details}");
    }

    private static GlobalVariableModuleTool ResolveGlobalVariableModule()
    {
        try
        {
            if (VmSolution.Instance[GlobalVariableModuleName] is GlobalVariableModuleTool module)
            {
                return module;
            }
        }
        catch
        {
        }

        foreach (var candidate in VmSolution.Instance.Modules)
        {
            if (candidate is GlobalVariableModuleTool module &&
                (string.Equals(module.Name, GlobalVariableModuleName, StringComparison.Ordinal) ||
                 string.Equals(module.StrModuleName, GlobalVariableModuleName, StringComparison.Ordinal) ||
                 string.Equals(module.FullName, GlobalVariableModuleName, StringComparison.Ordinal)))
            {
                return module;
            }
        }

        throw new InvalidOperationException($"固定方案中未找到“{GlobalVariableModuleName}”。");
    }

    private static TModule ResolveNamedModule<TModule>(string procedureName, string displayModuleName)
        where TModule : VmModule
    {
        try
        {
            if (VmSolution.Instance[$"{procedureName}.{displayModuleName}"] is TModule directModule)
            {
                return directModule;
            }
        }
        catch
        {
        }

        var procedure = VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException($"固定方案中未找到“{procedureName}”。");
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            if (!string.Equals(displayName, displayModuleName, StringComparison.Ordinal))
            {
                continue;
            }

            var moduleName = info.strModuleName?.Trim() ?? "";
            foreach (var candidate in new[]
                     {
                         $"{procedureName}.{displayName}",
                         $"{procedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is TModule module)
                    {
                        return module;
                    }
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException(
            $"固定方案的“{procedureName}”中未找到“{displayModuleName}”。");
    }

    private string RunTwoPointInspection(
        IReadOnlyList<string> parts,
        string procedureName,
        ref VmProcedure? cachedProcedure)
    {
        if (parts.Count != 1)
        {
            throw new InvalidDataException($"{procedureName}命令参数不正确。");
        }

        if (_busy || _calibrationSession is not null)
        {
            throw new InvalidOperationException("视觉标定正在执行，暂不允许拍照检测。");
        }

        if (!_solutionLoaded)
        {
            // 进程窗口先于方案加载完成时，主页可能已经发来第一次检测命令。
            // 在 UI 线程内同步确保固定方案已就绪，避免出现启动时序导致的方案切换或误报。
            LoadFixedSolution();
        }

        if (!_solutionLoaded)
        {
            throw new InvalidOperationException(
                $"固定方案尚未加载完成，请确认桌面存在“{FixedSolutionFileName}”或“{FallbackSolutionFileName}”。");
        }

        cachedProcedure ??= GetRequiredProcedure(procedureName);
        var procedure = cachedProcedure;
        var isNozzlePointProcedure = string.Equals(
            procedureName,
            _nozzlePointProcedureName,
            StringComparison.Ordinal);
        ShellModuleTool? scriptModule = null;
        IMVSCircleFindModuTool? nozzle1CircleModule = null;
        IMVSCircleFindModuTool? nozzle2CircleModule = null;
        VmModule? displayModule = null;
        if (isNozzlePointProcedure)
        {
            nozzle1CircleModule = ResolveNamedModule<IMVSCircleFindModuTool>(
                procedureName,
                Nozzle1CircleModuleName);
            nozzle2CircleModule = ResolveNamedModule<IMVSCircleFindModuTool>(
                procedureName,
                Nozzle2CircleModuleName);
        }
        else
        {
            scriptModule = ResolveNamedModule<ShellModuleTool>(
                procedureName,
                InspectionScriptModuleName);
            displayModule = ResolveNamedModule<VmModule>(procedureName, CalibrationImageSourceName);
        }
        // 同一相机不能被两个流程同时占用。找点前明确停止方案内的连续执行。
        StopAllContinuousExecutionNoThrow();

        if (isNozzlePointProcedure)
        {
            ApplyNozzleTeachingRenderLayout();
            RefreshRenderLayout();
        }

        // 厂商渲染控件在相机被其他海康程序占用时会针对每次空结果弹出
        // “无图片数据”。拍照前先解绑，只有流程成功后才把有效结果交给控件。
        if (isNozzlePointProcedure)
        {
            PrepareNozzleTeachingRenderersForCameraAcquisition();
        }
        else
        {
            PrepareLiveRendererForCameraAcquisition();
        }

        InspectionImageFile? inspectionImage = null;
        try
        {
            procedure.Run(true);
            if (procedure.GetIsExecuteNormal() != 1)
            {
                var errors = procedure.GetModuErrorInfoList();
                var details = errors is null
                    ? ""
                    : string.Join(
                        "；",
                        errors.Select(error =>
                            $"{error.strDisplayName}(0x{unchecked((uint)error.nErrorCode):X8})"));
                throw new InvalidOperationException(
                    string.IsNullOrWhiteSpace(details)
                        ? $"固定方案中的{procedureName}执行异常。"
                        : $"固定方案中的{procedureName}执行异常：{details}");
            }
            if (isNozzlePointProcedure)
            {
                // 现场安装方向中圆查找2对应吸嘴1、圆查找1对应吸嘴2。
                // 两个结果模块分别绑定左右渲染控件，显示各自的原图和圆查找叠加图形。
                BindNozzleTeachingResultModules(nozzle2CircleModule!, nozzle1CircleModule!);
                RefreshNozzleTeachingDisplaysNoThrow();
            }
            else
            {
                BindInspectionResultModule(displayModule!);
            }

            List<RectangleBlobCandidate> candidates;
            if (isNozzlePointProcedure)
            {
                // 现场安装方向中圆查找2对应吸嘴1、圆查找1对应吸嘴2，
                // 按吸嘴顺序返回，避免上层显示和保存时再次交换。
                // 手动画圆是粗定位示教的最终结果。自动圆只作为画面参考；即使自动圆
                // 返回NG，也要把两张相机画面交给操作员，不能阻断手动画圆。
                var automaticNozzle1 = ReadCircleCenterOrPlaceholder(
                    nozzle2CircleModule!,
                    procedureName,
                    Nozzle2CircleModuleName);
                var automaticNozzle2 = ReadCircleCenterOrPlaceholder(
                    nozzle1CircleModule!,
                    procedureName,
                    Nozzle1CircleModuleName);
                _automaticNozzle1Center = TryGetAutomaticCircleCenter(automaticNozzle1);
                _automaticNozzle2Center = TryGetAutomaticCircleCenter(automaticNozzle2);
                candidates =
                [
                    automaticNozzle1,
                    automaticNozzle2
                ];
            }
            else
            {
                var scriptResult = scriptModule!.ModuResult;
                candidates = ReadAllScriptResults(scriptResult);
                if (scriptResult.ModuStatus != 1 && candidates.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"{procedureName}.{InspectionScriptModuleName}返回NG，请检查脚本和上游模块参数。");
                }

                // 生产找芯片时，脚本的“未找到目标”可能会以NG且0个结果返回。
                // 这是正常的缺料判定，交给主页执行震动、重拍和排空收尾，不能在视觉进程内报错。
                // 与 VisionMaster 的“当前结果”表严格一致：按脚本1原顺序返回全部X/Y/R，不截断、不筛选也不重排。
            }

            var imageWarning = "";
            try
            {
                if (isNozzlePointProcedure)
                {
                    VisionRenderControl.UpdateVMResultShow();
                    ImagePlaceholder.Visibility = Visibility.Collapsed;
                    inspectionImage = SaveInspectionImage();
                }
                else
                {
                    // 生产主页直接承载VisionMaster画面，不再保存随后会被删除的临时图片；
                    // 但吸嘴坐标换算必须使用本次图像的真实中心，因此仍需刷新一次并返回宽高。
                    VisionRenderControl.UpdateVMResultShow();
                    ImagePlaceholder.Visibility = Visibility.Collapsed;
                    var image = VisionRenderControl.ImageSource;
                    if (image is null || image.Width <= 0 || image.Height <= 0)
                    {
                        throw new InvalidOperationException("本次检测图像没有有效尺寸。");
                    }

                    inspectionImage = new InspectionImageFile(
                        string.Empty,
                        image.Width,
                        image.Height);
                }
            }
            catch (Exception exception)
            {
                // X/Y 是本次生产步骤的必要结果；检测图保存失败时仍然把坐标返回主页。
                imageWarning = $"；检测图未返回：{FormatException(exception)}";
            }

            SetStatus(
                $"{procedureName}执行完成：共返回 {candidates.Count} 个结果{imageWarning}",
                StatusKind.Success);
            var responseParts = new List<string>
            {
                candidates.Count.ToString(CultureInfo.InvariantCulture)
            };
            foreach (var candidate in candidates)
            {
                responseParts.Add(candidate.PixelX.ToString("R", CultureInfo.InvariantCulture));
                responseParts.Add(candidate.PixelY.ToString("R", CultureInfo.InvariantCulture));
                if (!isNozzlePointProcedure)
                {
                    responseParts.Add(candidate.RotationDegrees.ToString("R", CultureInfo.InvariantCulture));
                }
                responseParts.Add(candidate.Left.ToString(CultureInfo.InvariantCulture));
                responseParts.Add(candidate.Top.ToString(CultureInfo.InvariantCulture));
                responseParts.Add(candidate.Width.ToString(CultureInfo.InvariantCulture));
                responseParts.Add(candidate.Height.ToString(CultureInfo.InvariantCulture));
            }

            responseParts.Add((inspectionImage?.PixelWidth ?? 0).ToString(CultureInfo.InvariantCulture));
            responseParts.Add((inspectionImage?.PixelHeight ?? 0).ToString(CultureInfo.InvariantCulture));
            responseParts.Add(inspectionImage?.FilePath ?? "");
            return string.Join("\t", responseParts);
        }
        catch
        {
            if (inspectionImage is not null)
            {
                try
                {
                    File.Delete(inspectionImage.FilePath);
                }
                catch
                {
                }
            }

            throw;
        }
        finally
        {
            if (!_closed)
            {
                // 找点流程保持单次执行；停止全部连续流程即可释放相机，同时保留结果图。
                StopAllContinuousExecutionNoThrow();
            }

            UpdateCommandState();
        }
    }

    private string SetCalibrationToolbarState(IReadOnlyList<string> parts)
    {
        if (parts.Count != 8)
        {
            throw new InvalidDataException("标定文件菜单状态参数不正确。");
        }

        string path;
        try
        {
            path = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("标定文件菜单路径格式不正确。", exception);
        }

        CalibrationToolbarPathTextBox.Text = string.IsNullOrWhiteSpace(path)
            ? "未设置"
            : Path.GetFileName(path.Trim());
        CalibrationToolbarPathTextBox.ToolTip = string.IsNullOrWhiteSpace(path)
            ? "当前应用的九点标定文件尚未设置"
            : $"当前应用文件：{path}";
        CalibrationToolbarPathTextBox.IsEnabled = parts[2] == "1";
        ChooseCalibrationToolbarButton.IsEnabled = parts[3] == "1";
        ImportCalibrationToolbarButton.IsEnabled = parts[4] == "1";
        LoadCalibrationProfileToolbarButton.IsEnabled = parts[5] == "1";
        SaveCalibrationProfileToolbarButton.IsEnabled = parts[6] == "1";
        var simplifiedMode = parts[7] == "1";
        LoadCalibrationProfileToolbarButton.Visibility = simplifiedMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        SaveCalibrationProfileToolbarButton.Visibility = simplifiedMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (!SaveCalibrationProfileToolbarButton.IsEnabled)
        {
            ResetCalibrationSaveFeedback();
        }
        return "标定文件菜单状态已更新。";
    }

    private string SetCalibrationSaveFeedback(IReadOnlyList<string> parts)
    {
        if (parts.Count != 3 || (parts[1] != "0" && parts[1] != "1"))
        {
            throw new InvalidDataException("保存配置反馈参数不正确。");
        }

        string message;
        try
        {
            message = Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("保存配置反馈文本格式不正确。", exception);
        }

        var success = parts[1] == "1";
        SaveCalibrationProfileToolbarButton.Content = success ? "保存成功" : "保存失败";
        SaveCalibrationProfileToolbarButton.ToolTip = message;
        SaveCalibrationProfileToolbarButton.Background = new SolidColorBrush(
            success ? Color.FromRgb(11, 73, 55) : Color.FromRgb(91, 24, 33));
        SaveCalibrationProfileToolbarButton.BorderBrush = new SolidColorBrush(
            success ? Color.FromRgb(0, 199, 120) : Color.FromRgb(217, 13, 22));
        SaveCalibrationProfileToolbarButton.Foreground = new SolidColorBrush(
            success ? Color.FromRgb(30, 234, 134) : Color.FromRgb(255, 128, 137));
        SetStatus(message, success ? StatusKind.Success : StatusKind.Error);
        return "保存配置反馈已显示。";
    }

    private void ResetCalibrationSaveFeedback()
    {
        SaveCalibrationProfileToolbarButton.Content = "保存配置";
        SaveCalibrationProfileToolbarButton.ToolTip = "保存当前九点标定和双吸嘴粗定位示教配置";
        SaveCalibrationProfileToolbarButton.Background = new SolidColorBrush(Color.FromRgb(11, 43, 36));
        SaveCalibrationProfileToolbarButton.BorderBrush = new SolidColorBrush(Color.FromRgb(0, 169, 101));
        SaveCalibrationProfileToolbarButton.Foreground = new SolidColorBrush(Color.FromRgb(30, 234, 134));
    }

    private string SetCalibrationSidebarState(IReadOnlyList<string> parts)
    {
        if (parts.Count != 39)
        {
            throw new InvalidDataException("标定侧栏状态参数不正确。");
        }

        static string Decode(string encoded)
        {
            try
            {
                return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException exception)
            {
                throw new InvalidDataException("标定侧栏文本格式不正确。", exception);
            }
        }

        static void SetText(TextBox textBox, string value)
        {
            // 侧栏输入会通过进程间消息同步到主程序，主程序随后回写整套状态。
            // 用户正在编辑时不能用回写值覆盖文本，否则每次按键后光标和内容都会被重置。
            if (textBox.IsKeyboardFocusWithin)
            {
                return;
            }

            if (!string.Equals(textBox.Text, value, StringComparison.Ordinal))
            {
                textBox.Text = value;
            }
        }

        static void SelectByTag(ComboBox comboBox, string tag)
        {
            var item = comboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(candidate => string.Equals(candidate.Tag as string, tag, StringComparison.Ordinal));
            if (item is not null && !ReferenceEquals(comboBox.SelectedItem, item))
            {
                comboBox.SelectedItem = item;
            }
        }

        static Brush ParseBrush(string value, Brush fallback)
        {
            try
            {
                return (Brush?)new BrushConverter().ConvertFromString(value) ?? fallback;
            }
            catch
            {
                return fallback;
            }
        }

        _applyingCalibrationSidebarState = true;
        try
        {
            SidebarCenterXPulseText.Text = Decode(parts[1]);
            SidebarCenterYPulseText.Text = Decode(parts[2]);
            SidebarCenterVmText.Text = Decode(parts[3]);
            SetText(SidebarStepXTextBox, Decode(parts[4]));
            SetText(SidebarStepYTextBox, Decode(parts[5]));
            SelectByTag(SidebarMovePriorityComboBox, Decode(parts[6]));
            SetText(SidebarVelocityTextBox, Decode(parts[7]));
            SetText(SidebarSettleTextBox, Decode(parts[8]));
            SidebarNozzleTeachStepText.Text = Decode(parts[9]);
            SidebarRecordNozzleButton.Content = Decode(parts[10]);
            SidebarNozzleStatusText.Text = Decode(parts[11]);
            SelectByTag(SidebarClickTargetComboBox, Decode(parts[12]));
            SidebarEnableClickMoveCheckBox.IsChecked = parts[13] == "1";
            SidebarClickMoveStatusText.Text = Decode(parts[14]);
            SidebarStartLivePreviewButton.IsEnabled = parts[15] == "1";
            SidebarRecordCenterButton.IsEnabled = parts[16] == "1";
            SidebarStartCalibrationButton.IsEnabled = parts[17] == "1" || parts[18] == "1";
            var calibrationRunning = parts[18] == "1";
            SidebarStartCalibrationButton.Tag = calibrationRunning ? "StopCalibration" : "StartCalibration";
            SidebarStartCalibrationButton.Content = calibrationRunning ? "停止九点标定" : "一键九点标定";
            SidebarStartCalibrationButton.Background = new SolidColorBrush(
                calibrationRunning ? Color.FromRgb(117, 18, 28) : Color.FromRgb(0, 169, 101));
            SidebarStartCalibrationButton.BorderBrush = new SolidColorBrush(
                calibrationRunning ? Color.FromRgb(217, 13, 22) : Color.FromRgb(0, 199, 120));
            var parameterInputsEnabled = parts[19] == "1";
            SidebarStepXTextBox.IsEnabled = parameterInputsEnabled;
            SidebarStepYTextBox.IsEnabled = parameterInputsEnabled;
            SidebarMovePriorityComboBox.IsEnabled = parameterInputsEnabled;
            SidebarVelocityTextBox.IsEnabled = parameterInputsEnabled;
            SidebarSettleTextBox.IsEnabled = parameterInputsEnabled;
            SidebarRecordCameraButton.IsEnabled = parts[20] == "1";
            SidebarRecordNozzleButton.IsEnabled = parts[21] == "1";
            SidebarAssignPoint1Nozzle2Button.IsEnabled = parts[21] == "1";
            SidebarClickTargetComboBox.IsEnabled = parts[22] == "1";
            SidebarEnableClickMoveCheckBox.IsEnabled = parts[23] == "1";
            SidebarReturnCameraCenterButton.IsEnabled = parts[24] == "1";
            SidebarStopClickMoveButton.IsEnabled = parts[25] == "1";
            SidebarCenterVmText.Foreground = ParseBrush(Decode(parts[26]), Brushes.LimeGreen);
            SidebarNozzleStatusText.Foreground = ParseBrush(Decode(parts[27]), Brushes.LightSteelBlue);
            SidebarClickMoveStatusText.Foreground = ParseBrush(Decode(parts[28]), Brushes.LightSteelBlue);
            SidebarStartCalibrationButton.ToolTip = Decode(parts[29]);
            SidebarRecordNozzleDotButton.IsEnabled = parts[30] == "1";
            var simplifiedMode = parts[31] == "1";
            var lowerCameraNozzleName = Decode(parts[32]);
            SidebarCenterTitleText.Text = simplifiedMode ? "记录中心" : "移动中心";
            SidebarRecordCenterButton.Content = simplifiedMode ? "记录当前中心" : "移动中心";
            var rotationCenterRunning = parts[34] == "1";
            SidebarNozzleTeachSection.Visibility = simplifiedMode
                ? Visibility.Collapsed
                : Visibility.Visible;
            SidebarRotationCenterSection.Visibility = simplifiedMode
                ? Visibility.Visible
                : Visibility.Collapsed;
            SidebarRotationCenterButton.IsEnabled = parts[33] == "1" || rotationCenterRunning;
            SidebarRotationCenterButton.Tag = rotationCenterRunning
                ? "StopRotationCenter"
                : "CalculateRotationCenter";
            SidebarRotationCenterButton.Content = rotationCenterRunning
                ? "停止旋转中心计算"
                : $"计算并写入参数设置 · {lowerCameraNozzleName}";
            SidebarRotationCenterStatusText.Text = Decode(parts[35]);
            SidebarRotationCenterStatusText.Foreground = rotationCenterRunning
                ? new SolidColorBrush(Color.FromRgb(255, 183, 77))
                : new SolidColorBrush(Color.FromRgb(175, 192, 205));
            SidebarLowerCameraCorrectionSection.Visibility = simplifiedMode
                ? Visibility.Visible
                : Visibility.Collapsed;
            SidebarClickMoveSection.Visibility = simplifiedMode
                ? Visibility.Collapsed
                : Visibility.Visible;
            var lowerCameraCorrectionTestRunning = parts[37] == "1";
            SidebarLowerCameraCorrectionTestButton.IsEnabled =
                parts[36] == "1" || lowerCameraCorrectionTestRunning;
            SidebarLowerCameraCorrectionTestButton.Tag = lowerCameraCorrectionTestRunning
                ? "StopLowerCameraCorrectionTest"
                : "RunLowerCameraCorrectionTest";
            SidebarLowerCameraCorrectionTestButton.Content = lowerCameraCorrectionTestRunning
                ? "停止纠偏测试"
                : $"纠偏测试 · {lowerCameraNozzleName}";
            SidebarLowerCameraCorrectionTestStatusText.Text = Decode(parts[38]);
            SidebarLowerCameraCorrectionTestStatusText.Foreground = lowerCameraCorrectionTestRunning
                ? new SolidColorBrush(Color.FromRgb(255, 183, 77))
                : new SolidColorBrush(Color.FromRgb(175, 192, 205));
            SidebarClickTargetComboBox.Visibility = Visibility.Visible;
            SidebarClickMoveStepText.Text = "4";
            SidebarClickMoveTitleText.Text = "点击移动";
            SidebarEnableClickMoveCheckBox.Content = "点击图像移动";
            if (simplifiedMode)
            {
                SidebarStartCalibrationButton.Content = calibrationRunning
                    ? $"停止{lowerCameraNozzleName}标定"
                    : $"一键九点标定 · {lowerCameraNozzleName}";
            }
        }
        finally
        {
            _applyingCalibrationSidebarState = false;
        }

        return "标定侧栏状态已更新。";
    }

    /// <summary>
    /// 与参考程序一致，直接把指定结果模块绑定到 VmRenderControl。
    /// </summary>
    private void BindInspectionResultModule(VmModule resultModule)
    {
        DetachCrosshairModule();
        _displayedModule = null;
        ImagePlaceholder.Visibility = Visibility.Visible;
        CenterCrosshair.Visibility = Visibility.Collapsed;
        VisionRenderControl.ModuleSource = resultModule;
    }

    private void PrepareLiveRendererForCameraAcquisition()
    {
        ClearNozzleTeachingResultRenderersNoThrow();
        DetachCrosshairModule();
        _displayedModule = null;
        _livePreviewRenderReady = false;
        Interlocked.Increment(ref _liveRenderGeneration);
        CenterCrosshair.Visibility = Visibility.Collapsed;
        ImagePlaceholder.Visibility = Visibility.Visible;
        try
        {
            VisionRenderControl.ModuleSource = null;
            VisionRenderControl.ClearDisplayView();
        }
        catch
        {
            // 清理显示失败不影响相机流程；关键是不能再主动刷新空结果。
        }
    }

    private void PrepareNozzleTeachingRenderersForCameraAcquisition()
    {
        _automaticNozzle1Center = null;
        _automaticNozzle2Center = null;
        PrepareLiveRendererForCameraAcquisition();
        _calibrationRenderGeneration++;
        CalibrationImagePlaceholder.Visibility = Visibility.Visible;
        try
        {
            CalibrationRenderControl.ModuleSource = null;
            CalibrationRenderControl.ClearDisplayView();
        }
        catch
        {
            // 清理第二张旧结果失败不影响本次流程；成功执行后会重新绑定圆查找结果。
        }
    }

    private void BindNozzleTeachingResultModules(
        IMVSCircleFindModuTool nozzle1ResultModule,
        IMVSCircleFindModuTool nozzle2ResultModule)
    {
        DetachCrosshairModule();
        _displayedModule = null;
        CenterCrosshair.Visibility = Visibility.Collapsed;
        ImagePlaceholder.Visibility = Visibility.Visible;
        CalibrationImagePlaceholder.Visibility = Visibility.Visible;
        // 先标记为已占用，任一控件绑定异常时，后续流程仍会完整解绑两路结果模块。
        _nozzleTeachingResultsDisplayed = true;
        VisionRenderControl.ModuleSource = nozzle1ResultModule;
        CalibrationRenderControl.ModuleSource = nozzle2ResultModule;
        BeginManualNozzleCircleSelection(restoreRender: false);
    }

    private void ClearNozzleTeachingResultRenderersNoThrow()
    {
        ResetManualNozzleCircleSelection(disableControls: true);
        if (!_nozzleTeachingResultsDisplayed)
        {
            return;
        }

        _nozzleTeachingResultsDisplayed = false;
        DetachCrosshairModule();
        _displayedModule = null;
        _livePreviewRenderReady = false;
        Interlocked.Increment(ref _liveRenderGeneration);
        _calibrationRenderGeneration++;
        CenterCrosshair.Visibility = Visibility.Collapsed;
        ImagePlaceholder.Visibility = Visibility.Visible;
        CalibrationImagePlaceholder.Visibility = Visibility.Visible;
        try
        {
            VisionRenderControl.ModuleSource = null;
            VisionRenderControl.ClearDisplayView();
        }
        catch
        {
        }

        try
        {
            CalibrationRenderControl.ModuleSource = null;
            CalibrationRenderControl.ClearDisplayView();
        }
        catch
        {
        }
    }

    private void RefreshNozzleTeachingDisplaysNoThrow()
    {
        try
        {
            VisionRenderControl.UpdateVMResultShow();
            ImagePlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 第一张图显示不参与视觉结果判定。
        }

        try
        {
            CalibrationRenderControl.UpdateVMResultShow();
            CalibrationImagePlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 第二张图显示不参与视觉结果判定。
        }
    }

    private void ManualNozzleCircleButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_nozzleTeachingResultsDisplayed)
        {
            SetStatus("请先执行粗定位示教，采集两个吸嘴画面。", StatusKind.Error);
            return;
        }

        BeginManualNozzleCircleSelection(restoreRender: true);
    }

    private void ClearManualNozzleCirclesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_nozzleTeachingResultsDisplayed)
        {
            return;
        }

        BeginManualNozzleCircleSelection(restoreRender: true);
    }

    private void BeginManualNozzleCircleSelection(bool restoreRender)
    {
        _manualNozzle1CirclePoints.Clear();
        _manualNozzle2CirclePoints.Clear();
        _manualNozzle1Circle = null;
        _manualNozzle2Circle = null;
        _manualNozzleCircleMode = true;
        SidebarManualCircleButton.IsEnabled = true;
        SidebarManualCircleButton.Content = "重新画圆";
        SidebarClearManualCircleButton.IsEnabled = true;
        SidebarManualCircleStatusText.Text = "吸嘴1：0/2　吸嘴2：0/2\n每张图先点圆心、再点圆边";
        if (restoreRender)
        {
            RestoreNozzleTeachingResultImagesNoThrow();
        }

        SetStatus("手动画圆已开启：每张图第一下点圆心，第二下点圆边。", StatusKind.Ready);
    }

    private void ResetManualNozzleCircleSelection(bool disableControls)
    {
        _manualNozzleCircleMode = false;
        _manualNozzle1CirclePoints.Clear();
        _manualNozzle2CirclePoints.Clear();
        _manualNozzle1Circle = null;
        _manualNozzle2Circle = null;
        if (SidebarManualCircleButton is null || SidebarClearManualCircleButton is null ||
            SidebarManualCircleStatusText is null)
        {
            return;
        }

        SidebarManualCircleButton.Content = "手动画圆";
        SidebarManualCircleButton.IsEnabled = !disableControls && _nozzleTeachingResultsDisplayed;
        SidebarClearManualCircleButton.IsEnabled = !disableControls && _nozzleTeachingResultsDisplayed;
        SidebarManualCircleStatusText.Text = "采集画面后，每张图先点圆心、再点圆边";
    }

    private void RestoreNozzleTeachingResultImagesNoThrow()
    {
        try
        {
            VisionRenderControl.UpdateVMResultShow();
        }
        catch
        {
        }

        try
        {
            CalibrationRenderControl.UpdateVMResultShow();
        }
        catch
        {
        }
    }

    private async Task HandleManualNozzleCircleClickAsync(int nozzleNumber, int pixelX, int pixelY)
    {
        if (!_manualNozzleCircleMode || !_nozzleTeachingResultsDisplayed || _closed)
        {
            return;
        }

        var points = nozzleNumber == 1
            ? _manualNozzle1CirclePoints
            : _manualNozzle2CirclePoints;
        var existingCircle = nozzleNumber == 1
            ? _manualNozzle1Circle
            : _manualNozzle2Circle;
        if (existingCircle is not null || points.Count >= 2)
        {
            return;
        }

        var point = new Point(pixelX, pixelY);
        points.Add(point);
        DrawManualCirclePoint(nozzleNumber, point, points.Count);

        if (points.Count == 2)
        {
            var deltaX = points[1].X - points[0].X;
            var deltaY = points[1].Y - points[0].Y;
            var circle = new ManualNozzleCircle(
                points[0],
                Math.Sqrt(deltaX * deltaX + deltaY * deltaY));
            if (!TryValidateManualNozzleCircle(nozzleNumber, circle, out var validationError))
            {
                points.RemoveAt(1);
                RestoreAndRedrawManualNozzleCircles();
                SidebarManualCircleStatusText.Text =
                    $"吸嘴{nozzleNumber}：{validationError}\n请保留圆心，重新点击圆边。";
                SetStatus($"吸嘴{nozzleNumber}画圆未通过检查：{validationError}", StatusKind.Error);
                return;
            }

            if (nozzleNumber == 1)
            {
                _manualNozzle1Circle = circle;
            }
            else
            {
                _manualNozzle2Circle = circle;
            }

            DrawManualCircle(nozzleNumber, circle);
        }

        UpdateManualNozzleCircleStatus();
        if (_manualNozzle1Circle is null || _manualNozzle2Circle is null)
        {
            return;
        }

        _manualNozzleCircleMode = false;
        SidebarManualCircleButton.Content = "重新画圆";
        SidebarManualCircleStatusText.Text =
            $"完成：吸嘴1 ({_manualNozzle1Circle.Center.X:0.##}, {_manualNozzle1Circle.Center.Y:0.##})　" +
            $"吸嘴2 ({_manualNozzle2Circle.Center.X:0.##}, {_manualNozzle2Circle.Center.Y:0.##})";
        SetStatus("两个手动画圆均已完成，可以保存双吸嘴结果。", StatusKind.Success);
        await SendHostEventAsync(string.Join(
            "\t",
            "MANUAL_NOZZLE_CIRCLES",
            _manualNozzle1Circle.Center.X.ToString("R", CultureInfo.InvariantCulture),
            _manualNozzle1Circle.Center.Y.ToString("R", CultureInfo.InvariantCulture),
            _manualNozzle1Circle.Radius.ToString("R", CultureInfo.InvariantCulture),
            _manualNozzle2Circle.Center.X.ToString("R", CultureInfo.InvariantCulture),
            _manualNozzle2Circle.Center.Y.ToString("R", CultureInfo.InvariantCulture),
            _manualNozzle2Circle.Radius.ToString("R", CultureInfo.InvariantCulture)));
    }

    private void UpdateManualNozzleCircleStatus()
    {
        var nozzle1Status = _manualNozzle1Circle is null
            ? $"{_manualNozzle1CirclePoints.Count}/2"
            : "完成";
        var nozzle2Status = _manualNozzle2Circle is null
            ? $"{_manualNozzle2CirclePoints.Count}/2"
            : "完成";
        SidebarManualCircleStatusText.Text =
            $"吸嘴1：{nozzle1Status}　吸嘴2：{nozzle2Status}\n第一下点圆心，第二下点圆边";
    }

    private void RestoreAndRedrawManualNozzleCircles()
    {
        RestoreNozzleTeachingResultImagesNoThrow();
        for (var index = 0; index < _manualNozzle1CirclePoints.Count; index++)
        {
            DrawManualCirclePoint(1, _manualNozzle1CirclePoints[index], index + 1);
        }

        for (var index = 0; index < _manualNozzle2CirclePoints.Count; index++)
        {
            DrawManualCirclePoint(2, _manualNozzle2CirclePoints[index], index + 1);
        }

        if (_manualNozzle1Circle is not null)
        {
            DrawManualCircle(1, _manualNozzle1Circle);
        }

        if (_manualNozzle2Circle is not null)
        {
            DrawManualCircle(2, _manualNozzle2Circle);
        }
    }

    private void DrawManualCirclePoint(int nozzleNumber, Point point, int pointNumber)
    {
        var renderControl = nozzleNumber == 1 ? VisionRenderControl : CalibrationRenderControl;
        renderControl.DrawShape(new VMControls.WPF.PointEx(
            (float)point.X,
            (float)point.Y,
            1d,
            "#FFD54F",
            5d,
            $"手动画圆点{pointNumber}"));
    }

    private void DrawManualCircle(int nozzleNumber, ManualNozzleCircle circle)
    {
        var renderControl = nozzleNumber == 1 ? VisionRenderControl : CalibrationRenderControl;
        const string color = "#00E676";
        renderControl.DrawShape(new VMControls.WPF.CircleEx(
            circle.Center,
            circle.Radius,
            1d,
            color,
            "#00000000",
            3d,
            $"吸嘴{nozzleNumber}手动画圆"));

        var crosshairHalfLength = Math.Max(8d, Math.Min(24d, circle.Radius * 0.2d));
        renderControl.DrawShape(new VMControls.WPF.LineEx(
            new Point(circle.Center.X - crosshairHalfLength, circle.Center.Y),
            new Point(circle.Center.X + crosshairHalfLength, circle.Center.Y),
            1d,
            color,
            3d,
            false,
            "手动画圆圆心"));
        renderControl.DrawShape(new VMControls.WPF.LineEx(
            new Point(circle.Center.X, circle.Center.Y - crosshairHalfLength),
            new Point(circle.Center.X, circle.Center.Y + crosshairHalfLength),
            1d,
            color,
            3d,
            false,
            "手动画圆圆心"));
    }

    private bool TryValidateManualNozzleCircle(
        int nozzleNumber,
        ManualNozzleCircle circle,
        out string error)
    {
        if (double.IsNaN(circle.Radius) || double.IsInfinity(circle.Radius) || circle.Radius < 5d)
        {
            error = "圆半径太小";
            return false;
        }

        var renderControl = nozzleNumber == 1 ? VisionRenderControl : CalibrationRenderControl;
        var image = renderControl.ImageSource;
        if (image is null || image.Width <= 0 || image.Height <= 0)
        {
            error = "无法读取当前画面尺寸";
            return false;
        }

        const double edgeTolerance = 2d;
        if (circle.Center.X - circle.Radius < -edgeTolerance ||
            circle.Center.Y - circle.Radius < -edgeTolerance ||
            circle.Center.X + circle.Radius > image.Width - 1d + edgeTolerance ||
            circle.Center.Y + circle.Radius > image.Height - 1d + edgeTolerance)
        {
            error = "画出的圆超出当前画面";
            return false;
        }

        var automaticCenter = nozzleNumber == 1
            ? _automaticNozzle1Center
            : _automaticNozzle2Center;
        if (automaticCenter is { } reference)
        {
            var automaticDeltaX = circle.Center.X - reference.X;
            var automaticDeltaY = circle.Center.Y - reference.Y;
            var centerDistance = Math.Sqrt(
                automaticDeltaX * automaticDeltaX + automaticDeltaY * automaticDeltaY);
            var maximumAllowedDistance = Math.Max(100d, circle.Radius * 2d);
            if (centerDistance > maximumAllowedDistance)
            {
                error =
                    $"手动圆心与吸嘴{nozzleNumber}原位置相差{centerDistance:0.#}像素，已禁止保存";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static Point? TryGetAutomaticCircleCenter(RectangleBlobCandidate candidate)
    {
        return candidate.PixelX > 0f && candidate.PixelY > 0f
            ? new Point(candidate.PixelX, candidate.PixelY)
            : null;
    }

    private void RefreshInspectionDisplayNoThrow()
    {
        try
        {
            VisionRenderControl.UpdateVMResultShow();
            ImagePlaceholder.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // 图像显示不参与生产判定；视觉流程结果仍按各结果模块正常读取。
        }
    }

    private void TryBindInspectionResultModuleNoThrow(VmModule resultModule)
    {
        try
        {
            BindInspectionResultModule(resultModule);
            RefreshInspectionDisplayNoThrow();
        }
        catch
        {
            // 显示异常不能覆盖流程自身的成功/失败原因。
        }
    }

    private void QueueLowerCameraCorrectionResultRefreshNoThrow(
        VmModule imageSourceModule,
        DispatcherPriority priority,
        bool fallbackToImageSource)
    {
        var generation = _liveRenderGeneration;
        try
        {
            _ = Dispatcher.InvokeAsync(
                () =>
                {
                    if (generation != _liveRenderGeneration)
                    {
                        return;
                    }

                    RefreshInspectionDisplayNoThrow();
                    if (!fallbackToImageSource || HasCurrentVisionRenderImage())
                    {
                        return;
                    }

                    // Render 后仍没有图像，说明“旋转计算2”没有发布可渲染底图。
                    // 此时绑定本次图像源，至少保证流程 NG 时原始相机图仍然可见。
                    TryBindInspectionResultModuleNoThrow(imageSourceModule);
                },
                priority);
        }
        catch
        {
            // 窗口关闭时不再安排补刷；流程状态不受显示队列影响。
        }
    }

    /// <summary>
    /// 将当前结果对应的原始相机图保存到共享临时目录，并读取 BMP 像素尺寸。
    /// 文件由主程序成功加载到内存后删除。
    /// </summary>
    private InspectionImageFile SaveInspectionImage()
    {
        // SDK 的 SaveOriginalImage 在当前渲染图为空时不会抛异常，
        // 而是直接弹出“无图片数据”。先检查可避免厂商弹窗。
        if (!HasCurrentVisionRenderImage())
        {
            throw new InvalidOperationException("当前没有可保存的检测图像。");
        }

        var directory = Path.Combine(Path.GetTempPath(), "ControlHubVision");
        Directory.CreateDirectory(directory);
        var imagePath = Path.Combine(directory, $"blob-{Guid.NewGuid():N}.bmp");
        try
        {
            VisionRenderControl.SaveOriginalImage(imagePath);
            using var stream = new FileStream(
                imagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 26 || reader.ReadUInt16() != 0x4D42)
            {
                throw new InvalidDataException("VisionMaster 保存的Blob检测图不是有效 BMP 图像。");
            }

            stream.Position = 18;
            var pixelWidth = Math.Abs((long)reader.ReadInt32());
            var pixelHeight = Math.Abs((long)reader.ReadInt32());
            if (pixelWidth <= 0 || pixelHeight <= 0 ||
                pixelWidth > int.MaxValue || pixelHeight > int.MaxValue)
            {
                throw new InvalidDataException("VisionMaster 保存的Blob检测图尺寸无效。");
            }

            return new InspectionImageFile(imagePath, (int)pixelWidth, (int)pixelHeight);
        }
        catch
        {
            try
            {
                File.Delete(imagePath);
            }
            catch
            {
            }

            throw;
        }
    }

    /// <summary>
    /// 不依赖 VisionMaster 流程中的 Blob 节点，直接创建 SDK Blob 工具分析本次相机图。
    /// 自动阈值分别尝试“暗目标/亮背景”和“亮目标/暗背景”，选择矩形质量更好的一组。
    /// </summary>
    private List<RectangleBlobCandidate> RunStandaloneBlobAnalysis(InspectionImageFile image)
    {
        _standaloneBlobModule ??= new IMVSBlobFindModuTool();
        using var bitmap = new System.Drawing.Bitmap(image.FilePath);
        var inputImage = new ImageBaseData(bitmap);
        try
        {
            var parameters = _standaloneBlobModule.ModuParams
                ?? throw new InvalidOperationException("无法初始化海康代码Blob分析参数。");
            var pixelCount = (long)image.PixelWidth * image.PixelHeight;
            var minimumArea = (int)Math.Min(5000L, Math.Max(20L, pixelCount / 100000L));
            var maximumArea = (int)Math.Min(
                int.MaxValue,
                Math.Max(minimumArea + 1L, pixelCount / 2L));
            var candidateSets = new List<List<RectangleBlobCandidate>>();
            Exception? lastRunException = null;

            foreach (var polarity in new[]
                     {
                         BlobFindParam.PolarityEnum.DarkOnBright,
                         BlobFindParam.PolarityEnum.BrightOnDark
                     })
            {
                try
                {
                    parameters.InputImage = inputImage;
                    parameters.ThresholdType = BlobFindParam.ThresholdTypeEnum.AutoThreshold;
                    parameters.Polarity = polarity;
                    parameters.FindNum = 100;
                    parameters.SelectByArea = true;
                    parameters.MinArea = minimumArea;
                    parameters.MaxArea = maximumArea;
                    parameters.SelectByRectangularity = false;
                    parameters.SortFeature = BlobFindParam.SortFeatureEnum.SortFeatureRect;
                    parameters.SortMode = BlobFindParam.SortModeEnum.SortModeDecend;
                    parameters.Connectivity = BlobFindParam.ConnectivityEnum.Connected_8;
                    parameters.BlobNumLimitEnable = false;
                    parameters.OKWhenNumIsZero = false;
                    parameters.BolbOutLineEnable = true;
                    _standaloneBlobModule.Run();

                    var result = _standaloneBlobModule.ModuResult;
                    if (result.ModuStatus != 1)
                    {
                        continue;
                    }

                    candidateSets.Add(ReadBlobCandidates(
                        result,
                        image.PixelWidth,
                        image.PixelHeight,
                        excludeFullFrameBlob: true));
                }
                catch (Exception exception)
                {
                    lastRunException = exception;
                }
            }

            if (candidateSets.Count == 0)
            {
                throw new InvalidOperationException(
                    "海康代码Blob分析执行失败，请检查当前相机图是否有效。",
                    lastRunException);
            }

            return candidateSets
                .OrderByDescending(ScoreBlobCandidateSet)
                .ThenByDescending(set => set.Count)
                .First();
        }
        finally
        {
            inputImage.Dispose();
        }
    }

    private static double ScoreBlobCandidateSet(IReadOnlyCollection<RectangleBlobCandidate> candidates)
    {
        return candidates
            .OrderByDescending(candidate => candidate.Rectangularity)
            .ThenByDescending(candidate => candidate.Area)
            .Take(2)
            .Sum(candidate =>
                Math.Max(0d, Math.Min(1d, candidate.Rectangularity)) * 1000d +
                Math.Log10(Math.Max(1d, candidate.Area)));
    }

    private static List<RectangleBlobCandidate> ReadBlobCandidates(
        BlobFindResult result,
        int imageWidth,
        int imageHeight,
        bool excludeFullFrameBlob)
    {
        if (result.ModuStatus != 1)
        {
            throw new InvalidOperationException("Blob分析返回NG，请检查相机图和阈值参数。");
        }

        var candidates = new List<RectangleBlobCandidate>();
        var points = result.CentroidPoint;
        if (points is null)
        {
            return candidates;
        }

        var candidateCount = Math.Min(result.BlobNum, points.Count);
        for (var index = 0; index < candidateCount; index++)
        {
            var point = points[index];
            if (float.IsNaN(point.X) || float.IsInfinity(point.X) ||
                float.IsNaN(point.Y) || float.IsInfinity(point.Y))
            {
                continue;
            }

            var rectangularity = result.Rectangularity is not null && index < result.Rectangularity.Count
                ? result.Rectangularity[index]
                : 0f;
            var area = result.Area is not null && index < result.Area.Count
                ? result.Area[index]
                : 0f;
            if (float.IsNaN(rectangularity) || float.IsInfinity(rectangularity))
            {
                rectangularity = 0f;
            }

            if (float.IsNaN(area) || float.IsInfinity(area))
            {
                area = 0f;
            }

            var blobRect = result.BlobRect is not null && index < result.BlobRect.Count
                ? result.BlobRect[index]
                : null;
            var left = blobRect?.RectPoint.X ?? (int)Math.Round(point.X);
            var top = blobRect?.RectPoint.Y ?? (int)Math.Round(point.Y);
            var width = Math.Max(1, blobRect?.RectWidth ?? 1);
            var height = Math.Max(1, blobRect?.RectHeight ?? 1);
            if (excludeFullFrameBlob &&
                left <= 1 && top <= 1 &&
                left + width >= imageWidth - 1 &&
                top + height >= imageHeight - 1)
            {
                continue;
            }

            candidates.Add(new RectangleBlobCandidate(
                point.X,
                point.Y,
                rectangularity,
                area,
                left,
                top,
                width,
                height));
        }

        return candidates;
    }

    private static List<RectangleBlobCandidate> ReadAllScriptResults(ShellResult result)
    {
        Exception? outputException = null;
        try
        {
            var outputX = result.GetOutputFloat("X");
            var outputY = result.GetOutputFloat("Y");
            var outputR = result.GetOutputFloat("R");
            var resultCount = GetMatchingScriptResultCount(outputX, outputY, outputR);
            if (resultCount > 0)
            {
                return Enumerable.Range(0, resultCount)
                    .Select(index => ReadScriptResultRow(outputX, outputY, outputR, index))
                    .ToList();
            }
        }
        catch (Exception exception)
        {
            outputException = exception;
        }

        // 部分脚本把多行结果直接写入模块数据显示，而不是声明X/Y/R三个浮点数组输出。
        // 同时兼容截图所示的“X:...,Y:...,R:...”格式，仍按文本出现顺序返回全部结果。
#pragma warning disable CS0618 // VisionMaster 4.4仅通过此兼容属性公开脚本模块的自定义显示行。
        var displayedResults = ReadScriptResultShowRows(result.ResultShow);
#pragma warning restore CS0618
        if (displayedResults.Count > 0 || outputException is null || result.ModuStatus != 1)
        {
            return displayedResults;
        }

        throw new InvalidOperationException(
            "找芯片流程.脚本1无法读取X/Y/R输出。请检查脚本输出名或模块数据格式。",
            outputException);
    }

    private static List<RectangleBlobCandidate> ReadScriptResultShowRows(string? resultShow)
    {
        var candidates = new List<RectangleBlobCandidate>();
        if (string.IsNullOrWhiteSpace(resultShow))
        {
            return candidates;
        }

        const string numberPattern = @"[-+]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][-+]?\d+)?";
        var matches = Regex.Matches(
            resultShow,
            $@"X\s*[:：]\s*(?<x>{numberPattern})\s*[,，]\s*Y\s*[:：]\s*(?<y>{numberPattern})\s*[,，]\s*R\s*[:：]\s*(?<r>{numberPattern})",
            RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        foreach (Match match in matches)
        {
            if (!float.TryParse(match.Groups["x"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixelX) ||
                !float.TryParse(match.Groups["y"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pixelY) ||
                !float.TryParse(match.Groups["r"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var rotationDegrees) ||
                float.IsNaN(pixelX) || float.IsInfinity(pixelX) || pixelX < 0 ||
                float.IsNaN(pixelY) || float.IsInfinity(pixelY) || pixelY < 0 ||
                float.IsNaN(rotationDegrees) || float.IsInfinity(rotationDegrees))
            {
                throw new InvalidOperationException("找芯片流程.脚本1的模块数据包含无效X/Y/R。");
            }

            candidates.Add(new RectangleBlobCandidate(
                pixelX,
                pixelY,
                0f,
                0f,
                (int)Math.Round(pixelX),
                (int)Math.Round(pixelY),
                1,
                1,
                rotationDegrees));
        }

        return candidates;
    }

    private static int GetMatchingScriptResultCount(
        FloatDataArray outputX,
        FloatDataArray outputY,
        FloatDataArray outputR)
    {
        var xCount = GetScriptOutputCount(outputX, "X");
        var yCount = GetScriptOutputCount(outputY, "Y");
        var rCount = GetScriptOutputCount(outputR, "R");
        if (xCount != yCount || xCount != rCount)
        {
            throw new InvalidOperationException(
                $"找芯片流程.脚本1返回的X/Y/R数量不一致：X={xCount}，Y={yCount}，R={rCount}。");
        }

        return xCount;
    }

    private static int GetScriptOutputCount(FloatDataArray output, string outputName)
    {
        var arrayLength = output.pFloatVal?.Length ?? 0;
        if (output.nValueNum < 0 || output.nValueNum > arrayLength)
        {
            throw new InvalidOperationException(
                $"找芯片流程.脚本1的{outputName}输出数量无效：声明{output.nValueNum}，实际{arrayLength}。");
        }

        return output.nValueNum;
    }

    private static RectangleBlobCandidate ReadScriptResultRow(
        FloatDataArray outputX,
        FloatDataArray outputY,
        FloatDataArray outputR,
        int index)
    {
        var pixelX = outputX.pFloatVal[index];
        var pixelY = outputY.pFloatVal[index];
        var rotationDegrees = outputR.pFloatVal[index];
        if (float.IsNaN(pixelX) || float.IsInfinity(pixelX) || pixelX < 0 ||
            float.IsNaN(pixelY) || float.IsInfinity(pixelY) || pixelY < 0 ||
            float.IsNaN(rotationDegrees) || float.IsInfinity(rotationDegrees))
        {
            throw new InvalidOperationException(
                $"找芯片流程.脚本1第 {index + 1} 行的X/Y/R无效。");
        }

        return new RectangleBlobCandidate(
            pixelX,
            pixelY,
            0f,
            0f,
            (int)Math.Round(pixelX),
            (int)Math.Round(pixelY),
            1,
            1,
            rotationDegrees);
    }

    private static RectangleBlobCandidate ReadCircleCenter(
        IMVSCircleFindModuTool module,
        string procedureName,
        string moduleName)
    {
        var result = module.ModuResult;
        if (result is null || result.ModuStatus != 1)
        {
            throw new InvalidOperationException(
                $"{procedureName}.{moduleName}返回NG，请检查吸嘴图像和圆查找参数。");
        }

        var center = result.OutputCircle?.CenterPoint
            ?? throw new InvalidOperationException(
                $"{procedureName}.{moduleName}未返回圆心。");
        if (float.IsNaN(center.X) || float.IsInfinity(center.X) || center.X < 0 ||
            float.IsNaN(center.Y) || float.IsInfinity(center.Y) || center.Y < 0)
        {
            throw new InvalidOperationException(
                $"{procedureName}.{moduleName}返回的中心X/Y无效。");
        }

        return new RectangleBlobCandidate(
            center.X,
            center.Y,
            0f,
            0f,
            (int)Math.Round(center.X),
            (int)Math.Round(center.Y),
            1,
            1);
    }

    private static RectangleBlobCandidate ReadCircleCenterOrPlaceholder(
        IMVSCircleFindModuTool module,
        string procedureName,
        string moduleName)
    {
        try
        {
            return ReadCircleCenter(module, procedureName, moduleName);
        }
        catch
        {
            return new RectangleBlobCandidate(0f, 0f, 0f, 0f, 0, 0, 1, 1);
        }
    }

    private string TransformPixel(IReadOnlyList<string> parts)
    {
        if (parts.Count != 4)
        {
            throw new InvalidDataException("像素坐标转换参数不正确。");
        }

        var pixelX = ParseFiniteDouble(parts[1], "像素X");
        var pixelY = ParseFiniteDouble(parts[2], "像素Y");
        if (pixelX < 0 || pixelY < 0)
        {
            throw new InvalidDataException("像素坐标不能小于0。");
        }

        var fullPath = DecodeAndValidateCalibrationFilePath(parts[3]);

        if (_busy || _calibrationSession is not null)
        {
            throw new InvalidOperationException("视觉标定正在执行，暂不允许像素坐标转换。");
        }

        if (!_solutionLoaded)
        {
            throw new InvalidOperationException("固定视觉方案尚未就绪。");
        }

        var (centerPixelX, centerPixelY) = GetClickCenterPixel();
        try
        {
            if (pixelX >= _clickImagePixelWidth || pixelY >= _clickImagePixelHeight)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(pixelX),
                    $"像素坐标超出当前图像范围：宽{_clickImagePixelWidth}，高{_clickImagePixelHeight}。");
            }

            var transformModule = GetCalibrationTransformModule();
            transformModule.ModuParams.LoadCalibPath = fullPath;
            transformModule.ModuParams.InputPoint =
            [
                new VM.PlatformSDKCS.PointF { X = (float)pixelX, Y = (float)pixelY },
                new VM.PlatformSDKCS.PointF { X = centerPixelX, Y = centerPixelY }
            ];
            transformModule.Run();
            var result = transformModule.ModuResult;
            if (result.ModuStatus != 1 || result.TransPoint is null || result.TransPoint.Count < 2)
            {
                throw new InvalidOperationException("标定转换模块未返回像素点和图像中心的机械坐标。");
            }

            var transformedPoint = result.TransPoint[0];
            var transformedCenter = result.TransPoint[1];
            SetStatus(
                $"像素({pixelX}, {pixelY})已按{Path.GetFileName(fullPath)}完成标定转换。",
                StatusKind.Success);
            return string.Join(
                "\t",
                transformedPoint.X.ToString("R", CultureInfo.InvariantCulture),
                transformedPoint.Y.ToString("R", CultureInfo.InvariantCulture),
                centerPixelX.ToString("R", CultureInfo.InvariantCulture),
                centerPixelY.ToString("R", CultureInfo.InvariantCulture),
                transformedCenter.X.ToString("R", CultureInfo.InvariantCulture),
                transformedCenter.Y.ToString("R", CultureInfo.InvariantCulture));
        }
        finally
        {
            UpdateCommandState();
        }
    }

    private Task<string> SetClickMoveModeAsync(IReadOnlyList<string> parts)
    {
        if (parts.Count != 3 || (parts[1] != "0" && parts[1] != "1"))
        {
            throw new InvalidDataException("点击移动配置参数不正确。");
        }

        var enabled = parts[1] == "1";
        var calibrationPath = DecodeCalibrationFilePath(parts[2]);

        if (!enabled)
        {
            _clickMoveEnabled = false;
            _clickCalibrationPath = calibrationPath;
            VisionRenderControl.SetRenderToolbarVisible(false);
            CenterCrosshair.Visibility = Visibility.Collapsed;
            DetachCrosshairModule();
            UpdateCommandState();
            SetStatus("点击视觉移动已关闭。", StatusKind.Ready);
            return Task.FromResult("点击视觉移动已关闭。");
        }

        if (_eventPipeName is null)
        {
            throw new InvalidOperationException("当前视觉窗口没有运动回传通道。");
        }

        if (!_solutionLoaded)
        {
            throw new InvalidOperationException("固定视觉方案尚未就绪。");
        }

        var fullPath = ValidateCalibrationFilePath(calibrationPath);
        var transformModule = GetCalibrationTransformModule();
        transformModule.ModuParams.LoadCalibPath = fullPath;
        ApplyLiveRenderLayout();
        if (!TryStartLivePreview(out var previewError))
        {
            throw new InvalidOperationException($"实时画面启动失败：{previewError}");
        }

        _clickCalibrationPath = fullPath;
        _clickCenterPixelReady = false;
        VisionRenderControl.SetRenderToolbarVisible(false);
        _clickMoveEnabled = true;
        CenterCrosshair.Visibility = Visibility.Collapsed;
        AttachCrosshairModule();
        UpdateCommandState();
        SetStatus($"点击移动已启用：{Path.GetFileName(fullPath)}", StatusKind.Success);
        return Task.FromResult($"已引用标定文件：{fullPath}。点击图像后将把该点移到绿色十字中心。");
    }

    private string SetCalibrationCenter(IReadOnlyList<string> parts)
    {
        if (parts.Count != 3)
        {
            throw new InvalidDataException("写入标定中心的参数数量不正确。");
        }

        if (!_solutionLoaded || _calibrationProcedure is null)
        {
            throw new InvalidOperationException("固定视觉方案尚未加载完成，无法写入标定流程。");
        }

        var procedureName = _activeCalibrationProcedureName;

        StopAllContinuousExecutionNoThrow();
        ApplyCalibrationRenderLayout();
        ClearCalibrationRenderer();
        BindCalibrationModule(ResolveNPointCalibrationModule(procedureName));
        RefreshRenderLayout();

        var centerX = ParseFiniteDouble(parts[1], "基准点X");
        var centerY = ParseFiniteDouble(parts[2], "基准点Y");
        try
        {
            var nPointModule = ResolveNPointCalibrationModule(procedureName);
            var parameters = nPointModule.ModuParams;
            parameters.BasePointX = centerX;
            parameters.BasePointY = centerY;
            if (Math.Abs(parameters.BasePointX - centerX) > 0.000001 ||
                Math.Abs(parameters.BasePointY - centerY) > 0.000001)
            {
                throw new InvalidOperationException("N点标定模块未接受新的基准点参数。");
            }

            SetStatus(
                $"已写入并读回 {procedureName}.N点标定1：基准点 X={parameters.BasePointX:0.####}，" +
                $"Y={parameters.BasePointY:0.####}",
                StatusKind.Success);
            return
                $"已写入并读回 {procedureName}.N点标定1：基准点 X={parameters.BasePointX:0.####}，" +
                $"Y={parameters.BasePointY:0.####}。" +
                "独立打开的 VisionMaster 编辑器不会同步刷新宿主进程内存参数。";
        }
        finally
        {
            UpdateCommandState();
        }
    }

    private async Task<string> PrepareNinePointCalibrationAsync(IReadOnlyList<string> parts)
    {
        if (parts.Count != 8)
        {
            throw new InvalidDataException("准备九点标定的参数数量不正确。");
        }

        if (!_solutionLoaded)
        {
            throw new InvalidOperationException("固定视觉方案尚未加载完成，无法开始九点标定。");
        }

        var centerX = ParseFiniteDouble(parts[1], "基准点X");
        var centerY = ParseFiniteDouble(parts[2], "基准点Y");
        var offsetX = ParsePositiveDouble(parts[3], "间距X");
        var offsetY = ParsePositiveDouble(parts[4], "间距Y");
        var xFirst = parts[5] switch
        {
            "X" => true,
            "Y" => false,
            _ => throw new InvalidDataException("移动优先参数只能是 X 或 Y。")
        };
        var lowerCamera = parts[6] switch
        {
            "Lower" => true,
            "Keep" => false,
            _ => throw new InvalidDataException("相机位置参数只能是 Keep 或 Lower。")
        };
        var expectedProcedureName = lowerCamera
            ? _lowerCameraCalibrationProcedureName
            : _calibrationProcedureName;
        if (!string.Equals(_activeCalibrationProcedureName, expectedProcedureName, StringComparison.Ordinal))
        {
            await ActivateCalibrationProcedureAsync(expectedProcedureName);
        }
        var procedureName = _activeCalibrationProcedureName;
        string calibrationPath;
        try
        {
            calibrationPath = Path.GetFullPath(
                Encoding.UTF8.GetString(Convert.FromBase64String(parts[7])));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("标定文件路径格式不正确。", exception);
        }

        if (!string.Equals(Path.GetExtension(calibrationPath), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("标定文件必须使用 .xml 扩展名。");
        }

        var calibrationDirectory = Path.GetDirectoryName(calibrationPath)
            ?? throw new InvalidDataException("无法确定标定文件的保存目录。");

        StopAllContinuousExecutionNoThrow();
        ApplyCalibrationRenderLayout();
        await RefreshRenderLayoutAsync();

        var nPointModule = ResolveNPointCalibrationModule(procedureName);
        try
        {
            var parameters = nPointModule.ModuParams;
            parameters.CalibPointGet = NPointCalibParam.CalibPointGetEnum.TriggerAcquisition;
            if (lowerCamera)
            {
                parameters.CameraMode = NPointCalibParam.CameraModeEnum.CameraStaticDown;
            }
            parameters.CalibPointTotalNum = 9;
            parameters.RotPointTotalNum = 0;
            parameters.TeachEnable = false;
            parameters.BasePointX = centerX;
            parameters.BasePointY = centerY;
            parameters.MoveAlignX = offsetX;
            parameters.MoveAlignY = offsetY;
            parameters.MoveFirstType = xFirst
                ? NPointCalibParam.MoveFirstTypeEnum.XFirst
                : NPointCalibParam.MoveFirstTypeEnum.YFirst;
            parameters.ChangeDirectionMoveTime = 3;
            parameters.UseRelativeCoordinates = false;

            Directory.CreateDirectory(calibrationDirectory);
            var backupPath = CalibrationBackupService.BackupBeforeOverwrite(calibrationPath);
            parameters.CalibPathName = calibrationPath;
            parameters.RefreshFileEnable = true;
            parameters.DoClearPoint();

            _calibrationSession = new VisionCalibrationSession(nPointModule, calibrationPath, backupPath);
            ClearCalibrationRenderer();
            BindCalibrationModule(nPointModule);
            SetBusy(true);
            SetStatus(
                $"九点标定已准备：基准({centerX:0.####}, {centerY:0.####})，" +
                $"偏移({offsetX:0.####}, {offsetY:0.####})，{(xFirst ? "X" : "Y")}优先，" +
                $"{(lowerCamera ? "下相机" : "原方案相机")}模式",
                StatusKind.Busy);
            return $"已准备九点标定：{calibrationPath}";
        }
        catch
        {
            _calibrationSession = null;
            ClearCalibrationRenderer();
            SetBusy(false);
            throw;
        }
    }

    private async Task<string> CaptureNinePointCalibrationAsync(IReadOnlyList<string> parts)
    {
        if (parts.Count != 2 ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pointNumber) ||
            pointNumber is < 1 or > 9)
        {
            throw new InvalidDataException("采集点号必须在 1 到 9 之间。");
        }

        var session = _calibrationSession
            ?? throw new InvalidOperationException("尚未准备九点标定参数。");
        if (pointNumber != session.NextPointNumber)
        {
            throw new InvalidOperationException(
                $"采集顺序错误：当前应采集第 {session.NextPointNumber} 点，不是第 {pointNumber} 点。");
        }

        if (_calibrationProcedure is null)
        {
            throw new InvalidOperationException("当前 VisionMaster 流程已失效。");
        }

        var stopwatch = Stopwatch.StartNew();
        RunCalibrationProcedureOnce();
        stopwatch.Stop();

        var result = session.Module.ModuResult;
        if (result.ModuStatus != 1)
        {
            throw new InvalidOperationException(
                $"第 {pointNumber} 点 N点标定模块返回 NG，请检查相机取像和圆查找结果。");
        }

        session.NextPointNumber++;
        var displayWarning = "";
        try
        {
            await RefreshCalibrationResultDisplayAsync();
        }
        catch (Exception exception)
        {
            // N点结果已经成功写入后，显示控件刷新失败不能取消整轮标定或清空有效点。
            displayWarning = $"；标定结果画面刷新失败：{FormatException(exception)}";
        }

        SetStatus(
            $"第 {pointNumber}/9 点已采集，流程用时 {stopwatch.Elapsed.TotalMilliseconds:0.0} ms" +
            displayWarning,
            StatusKind.Busy);
        return $"第 {pointNumber}/9 点 VisionMaster 流程执行完成。";
    }

    private Task<string> CompleteNinePointCalibrationAsync()
    {
        var session = _calibrationSession
            ?? throw new InvalidOperationException("尚未准备九点标定参数。");
        if (session.NextPointNumber != 10)
        {
            throw new InvalidOperationException(
                $"九点数据尚未采集完成，当前已完成 {session.NextPointNumber - 1}/9 点。");
        }

        var result = session.Module.ModuResult;
        if (result.ModuStatus != 1 || result.CalibStatus != 1)
        {
            throw new InvalidOperationException("九点已采集，但 VisionMaster 未生成有效标定结果。");
        }

        if (result.CalibErrStatus != 0)
        {
            throw new InvalidOperationException("标定矩阵已生成，但标定误差评估未通过。");
        }

        var pixelPrecision = result.PixelPrecision;
        session.Module.ModuParams.DoSaveFile(session.CalibrationPath);

        // DoSaveFile 是本轮标定的提交边界。文件一旦保存成功，就先结束会话，
        // 避免客户端等待显示刷新超时后发送的迟到 ABORT 把成功结果清空。
        _calibrationSession = null;
        StopAllContinuousExecutionNoThrow();
        var displayWarning = "";
        QueueCalibrationResultRefreshNoThrow(DispatcherPriority.ContextIdle);
        try
        {
            ApplyCalibrationRenderLayout();
            RefreshRenderLayout();
            // 保持 PREPARE 时建立的同一模块绑定，先同步刷新一次，再异步补刷最终结果；
            // 不得清屏或重绑。COMPLETE 不等待渲染队列，保存成功后可立即返回。
            RefreshCalibrationResultDisplay();
        }
        catch (Exception exception)
        {
            // 标定文件已经成功保存，最终画面刷新失败不能把标定结果改判为失败。
            displayWarning = $"；最终标定画面刷新失败：{FormatException(exception)}";
        }

        var message =
            $"九点标定成功，像素精度 {pixelPrecision:0.######}，" +
            $"标定文件：{session.CalibrationPath}" +
            (string.IsNullOrWhiteSpace(session.BackupPath)
                ? ""
                : $"；旧文件已备份：{session.BackupPath}") +
            displayWarning;
        FinishCalibrationUiNoThrow(message, StatusKind.Success);
        return Task.FromResult(message);
    }

    private Task<string> AbortNinePointCalibrationAsync()
    {
        var session = _calibrationSession;
        if (session is null)
        {
            // COMPLETE 可能已经保存成功，只是客户端在回包前超时并补发了 ABORT。
            // 此时必须幂等返回，不能清空最终画面，也不能覆盖成功状态。
            return Task.FromResult("当前没有正在进行的九点标定，已保留现有结果画面。");
        }

        if (session.NextPointNumber == 10)
        {
            _calibrationSession = null;
            StopAllContinuousExecutionNoThrow();
            QueueCalibrationResultRefreshNoThrow(DispatcherPriority.ContextIdle);
            // 九个点均已被 N 点模块接受后，即使最终校验、保存、回中心或通信失败，
            // 也保留最后一帧和模块内采集点，供现场确认真正的失败原因。
            try
            {
                ApplyCalibrationRenderLayout();
                RefreshRenderLayout();
                RefreshCalibrationResultDisplay();
            }
            catch
            {
                // 保留控件中已经存在的最后一帧；显示失败不能再次触发清屏。
            }

            const string completedPointsMessage =
                "九个标定点均已采集，完成处理未成功；最后画面和标定点已保留，未执行清空。";
            FinishCalibrationUiNoThrow(completedPointsMessage, StatusKind.Error);
            return Task.FromResult(completedPointsMessage);
        }

        var clearWarning = "";
        _calibrationRenderGeneration++;
        try
        {
            session.Module.ModuParams.DoClearPoint();
        }
        catch (Exception exception)
        {
            clearWarning = $"；VisionMaster 清点失败：{FormatException(exception)}";
        }
        finally
        {
            // 即使厂商 SDK 清点抛异常，也必须释放会话和忙状态，避免宿主永久锁死。
            _calibrationSession = null;
            StopAllContinuousExecutionNoThrow();
            try
            {
                ClearCalibrationRenderer();
                ApplyCalibrationRenderLayout();
                RefreshRenderLayout();
            }
            catch
            {
                // 流程状态清理优先于显示控件收尾。
            }
        }

        var message = string.IsNullOrWhiteSpace(clearWarning)
            ? "九点标定已取消，本次未完成的标定点已清空。"
            : "九点标定已取消，本次未完成的标定点未能全部清空" + clearWarning;
        FinishCalibrationUiNoThrow(
            message,
            string.IsNullOrWhiteSpace(clearWarning) ? StatusKind.Ready : StatusKind.Error);
        return Task.FromResult(message);
    }

    private static double ParseFiniteDouble(string value, string name)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result) ||
            double.IsNaN(result) ||
            double.IsInfinity(result))
        {
            throw new InvalidDataException($"{name}必须是有效数值。");
        }

        return result;
    }

    private static double ParsePositiveDouble(string value, string name)
    {
        var result = ParseFiniteDouble(value, name);
        if (result <= 0)
        {
            throw new InvalidDataException($"{name}必须大于 0。");
        }

        return result;
    }

    private static string DecodeAndValidateCalibrationFilePath(string encodedPath)
    {
        return ValidateCalibrationFilePath(DecodeCalibrationFilePath(encodedPath));
    }

    private static string DecodeCalibrationFilePath(string encodedPath)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(encodedPath));
        }
        catch (FormatException exception)
        {
            throw new InvalidDataException("标定文件路径格式不正确。", exception);
        }
    }

    private static string ValidateCalibrationFilePath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("标定文件必须使用 .xml 扩展名。");
        }

        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("标定文件不存在。", fullPath);
        }

        return fullPath;
    }

    private static string EncodePipeResponse(bool success, string message)
    {
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(message ?? ""));
        return $"{(success ? "OK" : "ERR")}\t{encoded}";
    }

    private static IMVSNPointCalibModuTool ResolveNPointCalibrationModule(string procedureName)
    {
        var procedure = VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (!string.Equals(displayName, NPointCalibrationModuleName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         $"{procedureName}.{displayName}",
                         $"{procedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is IMVSNPointCalibModuTool module)
                    {
                        return module;
                    }
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException(
            $"流程“{procedureName}”中未找到“{NPointCalibrationModuleName}”。");
    }

    private static IMVSCalibTransformModuTool ResolveCalibrationTransformModule(string procedureName)
    {
        var procedure = VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (!string.Equals(displayName, CalibrationTransformModuleName, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         $"{procedureName}.{displayName}",
                         $"{procedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is IMVSCalibTransformModuTool module)
                    {
                        return module;
                    }
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException(
            $"流程“{procedureName}”中未找到“{CalibrationTransformModuleName}”。");
    }

    /// <summary>
    /// 优先复用流程里已有的标定转换模块；没有时使用独立模块读取导入的 XML。
    /// 独立模块只负责坐标换算，不参与或修改用户的 VisionMaster 流程。
    /// </summary>
    private IMVSCalibTransformModuTool GetCalibrationTransformModule()
    {
        if (_solutionLoaded && _calibrationProcedure is not null)
        {
            try
            {
                return ResolveCalibrationTransformModule(_activeCalibrationProcedureName);
            }
            catch (InvalidOperationException)
            {
                // 当前流程只有N点标定也可以，下面创建独立转换工具直接读取XML。
            }
        }

        return _standaloneTransformModule ??= new IMVSCalibTransformModuTool();
    }

    /// <summary>
    /// 在当前实时流程中定位海康 Blob分析模块。显示名和模块类型名都参与匹配，
    /// 以兼容用户给模块改名以及不同 VisionMaster 方案的命名方式。
    /// </summary>
    private static IMVSBlobFindModuTool? TryResolveBlobFindModule(string procedureName)
    {
        var procedure = VmSolution.Instance[procedureName] as VmProcedure;
        if (procedure is null)
        {
            return null;
        }
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (!string.Equals(moduleName, "IMVSBlobFindModu", StringComparison.Ordinal) &&
                !displayName.Contains("Blob分析"))
            {
                continue;
            }

            foreach (var candidate in new[]
                     {
                         $"{procedureName}.{displayName}",
                         $"{procedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is IMVSBlobFindModuTool module)
                    {
                        return module;
                    }
                }
                catch
                {
                    // 部分 SDK 模块类型名不是可查询路径，继续尝试下一个候选名。
                }
            }
        }

        return null;
    }

    /// <summary>
    /// 按显示名精确定位流程内的 Blob 模块；找不到时直接报错，不回退到代码 Blob。
    /// </summary>
    private static IMVSBlobFindModuTool ResolveNamedBlobFindModule(
        string procedureName,
        string blobModuleName)
    {
        try
        {
            if (VmSolution.Instance[$"{procedureName}.{blobModuleName}"] is IMVSBlobFindModuTool directModule)
            {
                return directModule;
            }
        }
        catch
        {
            // 某些方案需要先从模块列表取得实际查询键，下面进行精确显示名回退。
        }

        var procedure = VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException($"固定方案中未找到“{procedureName}”。");
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            if (!string.Equals(displayName, blobModuleName, StringComparison.Ordinal))
            {
                continue;
            }

            var moduleName = info.strModuleName?.Trim() ?? "";
            foreach (var candidate in new[]
                     {
                         $"{procedureName}.{displayName}",
                         $"{procedureName}.{moduleName}",
                         displayName,
                         moduleName
                     }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal))
            {
                try
                {
                    if (VmSolution.Instance[candidate] is IMVSBlobFindModuTool module)
                    {
                        return module;
                    }
                }
                catch
                {
                }
            }
        }

        throw new InvalidOperationException(
            $"固定方案的“{procedureName}”中未找到“{blobModuleName}”。");
    }

    private async void VisionRenderControl_OnMouseLeftButtonDownPixelChanged(int pixelX, int pixelY)
    {
        if (_manualNozzleCircleMode && _nozzleTeachingResultsDisplayed)
        {
            try
            {
                await HandleManualNozzleCircleClickAsync(1, pixelX, pixelY);
            }
            catch (Exception exception)
            {
                SetStatus($"吸嘴1手动画圆失败：{FormatException(exception)}", StatusKind.Error);
            }

            return;
        }

        await HandleVisionImageClickAsync(pixelX, pixelY, null, null);
    }

    private async void CalibrationRenderControl_OnMouseLeftButtonDownPixelChanged(int pixelX, int pixelY)
    {
        if (_manualNozzleCircleMode && _nozzleTeachingResultsDisplayed)
        {
            try
            {
                await HandleManualNozzleCircleClickAsync(2, pixelX, pixelY);
            }
            catch (Exception exception)
            {
                SetStatus($"吸嘴2手动画圆失败：{FormatException(exception)}", StatusKind.Error);
            }
        }
    }

    private async Task HandleVisionImageClickAsync(
        int pixelX,
        int pixelY,
        int? imageWidth,
        int? imageHeight)
    {
        if (!_clickMoveEnabled || _clickTransformBusy || _closed)
        {
            return;
        }

        _clickTransformBusy = true;
        try
        {
            if (imageWidth is > 0 && imageHeight is > 0)
            {
                _clickImagePixelWidth = imageWidth.Value;
                _clickImagePixelHeight = imageHeight.Value;
                _clickCenterPixelX = (float)((imageWidth.Value - 1) / 2d);
                _clickCenterPixelY = (float)((imageHeight.Value - 1) / 2d);
                _clickCenterPixelReady = true;
            }

            if (_busy || _calibrationSession is not null)
            {
                throw new InvalidOperationException("视觉标定正在执行，暂不允许点击移动。");
            }

            if (!_solutionLoaded)
            {
                throw new InvalidOperationException("固定视觉方案尚未就绪。");
            }

            if (string.IsNullOrWhiteSpace(_clickCalibrationPath) || !File.Exists(_clickCalibrationPath))
            {
                throw new FileNotFoundException("当前引用的标定文件不存在。", _clickCalibrationPath);
            }

            var previewWasRunning = _previewProcedure?.ContinuousRunEnable == true;
            if (previewWasRunning)
            {
                _previewProcedure!.ContinuousRunEnable = false;
            }

            VM.PlatformSDKCS.PointF transformedPoint;
            VM.PlatformSDKCS.PointF transformedCenter;
            float centerPixelX;
            float centerPixelY;
            try
            {
                (centerPixelX, centerPixelY) = GetClickCenterPixel();
                var transformModule = GetCalibrationTransformModule();
                transformModule.ModuParams.LoadCalibPath = _clickCalibrationPath;
                transformModule.ModuParams.InputPoint =
                [
                    new VM.PlatformSDKCS.PointF
                    {
                        X = pixelX,
                        Y = pixelY
                    },
                    new VM.PlatformSDKCS.PointF
                    {
                        X = centerPixelX,
                        Y = centerPixelY
                    }
                ];
                transformModule.Run();
                var result = transformModule.ModuResult;
                if (result.ModuStatus != 1 || result.TransPoint is null || result.TransPoint.Count < 2)
                {
                    throw new InvalidOperationException("标定转换模块未返回点击点和中心点的机械坐标。");
                }

                transformedPoint = result.TransPoint[0];
                transformedCenter = result.TransPoint[1];
            }
            finally
            {
                if (previewWasRunning && _previewProcedure is not null)
                {
                    _previewProcedure.ContinuousRunEnable = true;
                }

                UpdateCommandState();
            }

            SetStatus(
                $"点击({pixelX}, {pixelY}) → 十字中心({centerPixelX:0.##}, {centerPixelY:0.##})",
                StatusKind.Success);
            await SendHostEventAsync(string.Join(
                "\t",
                "CLICK_TARGET",
                pixelX.ToString(CultureInfo.InvariantCulture),
                pixelY.ToString(CultureInfo.InvariantCulture),
                transformedPoint.X.ToString("R", CultureInfo.InvariantCulture),
                transformedPoint.Y.ToString("R", CultureInfo.InvariantCulture),
                centerPixelX.ToString("R", CultureInfo.InvariantCulture),
                centerPixelY.ToString("R", CultureInfo.InvariantCulture),
                transformedCenter.X.ToString("R", CultureInfo.InvariantCulture),
                transformedCenter.Y.ToString("R", CultureInfo.InvariantCulture)));
        }
        catch (Exception exception)
        {
            var message = FormatException(exception);
            SetStatus($"点击坐标转换失败：{message}", StatusKind.Error);
            try
            {
                await SendHostEventAsync(
                    $"CLICK_ERROR\t{Convert.ToBase64String(Encoding.UTF8.GetBytes(message))}");
            }
            catch
            {
            }
        }
        finally
        {
            _clickTransformBusy = false;
        }
    }

    private (float X, float Y) GetClickCenterPixel()
    {
        if (_clickCenterPixelReady)
        {
            return (_clickCenterPixelX, _clickCenterPixelY);
        }

        try
        {
            // 只需要宽高即可计算中心，不再每帧调用 SaveOriginalImage。
            // 原实现在渲染尚未出图时会触发 SDK 的“无图片数据”弹窗，
            // 捕获失败后又会在下一帧重试，因而造成无限弹窗。
            var image = VisionRenderControl.ImageSource;
            if (image is null || image.Width <= 0 || image.Height <= 0)
            {
                throw new InvalidOperationException("当前渲染画面尚未出图。");
            }

            var pixelWidth = image.Width;
            var pixelHeight = image.Height;

            _clickCenterPixelX = (float)((pixelWidth - 1) / 2d);
            _clickCenterPixelY = (float)((pixelHeight - 1) / 2d);
            _clickImagePixelWidth = pixelWidth;
            _clickImagePixelHeight = pixelHeight;
            _clickCenterPixelReady = true;
            return (_clickCenterPixelX, _clickCenterPixelY);
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("无法读取当前图像中心，请确认实时画面已经正常出图。", exception);
        }
    }

    private bool HasCurrentVisionRenderImage()
    {
        try
        {
            var image = VisionRenderControl.ImageSource;
            return image is not null && image.Width > 0 && image.Height > 0;
        }
        catch
        {
            return false;
        }
    }

    private void DrawImageCenterCrosshair()
    {
        if (!_clickCenterPixelReady || _closed)
        {
            return;
        }

        const string crosshairColor = "#00E676";
        const double crosshairThickness = 1d;
        var horizontal = new VMControls.WPF.LineEx(
            new Point(0, _clickCenterPixelY),
            new Point(_clickImagePixelWidth - 1, _clickCenterPixelY),
            1d,
            crosshairColor,
            crosshairThickness,
            false,
            "图像中心");
        var vertical = new VMControls.WPF.LineEx(
            new Point(_clickCenterPixelX, 0),
            new Point(_clickCenterPixelX, _clickImagePixelHeight - 1),
            1d,
            crosshairColor,
            crosshairThickness,
            false,
            "图像中心");
        VisionRenderControl.DrawShape(horizontal);
        VisionRenderControl.DrawShape(vertical);
    }

    private void QueueImageCenterCrosshair()
    {
        if (!_clickCenterPixelReady || _closed)
        {
            return;
        }

        const string crosshairColor = "#00E676";
        const double crosshairThickness = 1d;
        var horizontal = new VMControls.WPF.LineEx(
            new Point(0, _clickCenterPixelY),
            new Point(_clickImagePixelWidth - 1, _clickCenterPixelY),
            1d,
            crosshairColor,
            crosshairThickness,
            false,
            "图像中心");
        var vertical = new VMControls.WPF.LineEx(
            new Point(_clickCenterPixelX, 0),
            new Point(_clickCenterPixelX, _clickImagePixelHeight - 1),
            1d,
            crosshairColor,
            crosshairThickness,
            false,
            "图像中心");
        VisionRenderControl.AddShape(horizontal);
        VisionRenderControl.AddShape(vertical);
    }

    private void AttachCrosshairModule()
    {
        DetachCrosshairModule();
        if (_displayedModule is null)
        {
            return;
        }

        _crosshairModule = _displayedModule;
        var module = _crosshairModule;
        var renderGeneration = Volatile.Read(ref _liveRenderGeneration);
        _crosshairModuleResultHandler = (_, _) =>
            CrosshairModule_ModuleResultCallBackArrived(module, renderGeneration);
        try
        {
            module.EnableResultCallback();
            module.ModuleResultCallBackArrived += _crosshairModuleResultHandler;
        }
        catch
        {
            _crosshairModule = null;
            _crosshairModuleResultHandler = null;
        }
    }

    private void DetachCrosshairModule()
    {
        if (_crosshairModule is null)
        {
            return;
        }

        try
        {
            if (_crosshairModuleResultHandler is not null)
            {
                _crosshairModule.ModuleResultCallBackArrived -= _crosshairModuleResultHandler;
            }
        }
        catch
        {
        }

        _crosshairModule = null;
        _crosshairModuleResultHandler = null;
    }

    private void CrosshairModule_ModuleResultCallBackArrived(VmModule module, int renderGeneration)
    {
        if (!ReferenceEquals(module, _crosshairModule) ||
            renderGeneration != Volatile.Read(ref _liveRenderGeneration))
        {
            return;
        }

        _livePreviewFirstFrameSource?.TrySetResult(renderGeneration);
        _ = Dispatcher.BeginInvoke(
            () =>
            {
                if (renderGeneration == Volatile.Read(ref _liveRenderGeneration))
                {
                    ImagePlaceholder.Visibility = Visibility.Collapsed;
                }
            },
            DispatcherPriority.Render);
        if (!_livePreviewRenderReady)
        {
            return;
        }

        if (_clickCenterPixelReady)
        {
            QueueImageCenterCrosshair();
        }
        else
        {
            QueueClickCenterInitialization();
        }
    }

    private void QueueClickCenterInitialization()
    {
        if (_closed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _clickCenterInitializationQueued, 1) != 0)
        {
            return;
        }

        _ = Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (_clickCenterPixelReady || _closed)
                {
                    return;
                }

                _ = GetClickCenterPixel();
                DrawImageCenterCrosshair();
            }
            catch
            {
                // The next realtime frame callback retries initialization.
            }
            finally
            {
                Interlocked.Exchange(ref _clickCenterInitializationQueued, 0);
            }
        }, DispatcherPriority.Render);
    }

    private async Task SendHostEventAsync(string message)
    {
        if (string.IsNullOrWhiteSpace(_eventPipeName))
        {
            return;
        }

        using var pipe = new NamedPipeClientStream(
            ".",
            _eventPipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2_000).ConfigureAwait(false);
        using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            1024,
            leaveOpen: true)
        {
            AutoFlush = true
        };
        await writer.WriteLineAsync(message).ConfigureAwait(false);
    }

    private void RenderToolButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag })
        {
            return;
        }

        var parts = tag.Split(':');
        if (parts.Length != 2)
        {
            return;
        }

        var target = parts[0];

        var renderControl = string.Equals(target, "Live", StringComparison.Ordinal)
            ? VisionRenderControl
            : CalibrationRenderControl;

        switch (parts[1])
        {
            case "ZoomIn":
                renderControl.EnlargeView();
                break;
            case "ZoomOut":
                renderControl.ShrinkView();
                break;
            case "Actual":
                renderControl.InitViewSize();
                break;
            case "Fullscreen":
                ToggleRenderFullscreen(target);
                break;
        }
    }

    private void ToggleRenderFullscreen(string target)
    {
        if (string.Equals(_fullscreenRenderTarget, target, StringComparison.Ordinal))
        {
            if (_showingSplitRender)
            {
                ApplySplitRenderLayout();
            }
            else if (_showingCalibrationRender)
            {
                ApplyCalibrationRenderLayout();
            }
            else
            {
                ApplyLiveRenderLayout();
            }

            RefreshRenderLayout();
            return;
        }

        _fullscreenRenderTarget = target;
        WorkspaceSidebarColumn.Width = new GridLength(0);
        WorkspaceGapColumn.Width = new GridLength(0);
        CalibrationSidebar.Visibility = Visibility.Collapsed;

        var showLive = string.Equals(target, "Live", StringComparison.Ordinal);
        LiveRenderPanel.Visibility = showLive ? Visibility.Visible : Visibility.Collapsed;
        CalibrationRenderPanel.Visibility = showLive ? Visibility.Collapsed : Visibility.Visible;
        var visiblePanel = showLive ? LiveRenderPanel : CalibrationRenderPanel;
        Grid.SetColumn(visiblePanel, 0);
        Grid.SetColumnSpan(visiblePanel, 3);
        LiveFullscreenButton.Content = showLive ? "\uE73F" : "\uE740";
        CalibrationFullscreenButton.Content = showLive ? "\uE740" : "\uE73F";
        RefreshRenderLayout();
    }

    private void ApplySplitRenderLayout()
    {
        _showingSplitRender = true;
        _showingCalibrationRender = false;
        _fullscreenRenderTarget = null;
        WorkspaceSidebarColumn.Width = new GridLength(320);
        WorkspaceGapColumn.Width = new GridLength(6);
        CalibrationSidebar.Visibility = Visibility.Visible;
        LiveRenderPanel.Visibility = Visibility.Visible;
        CalibrationRenderPanel.Visibility = Visibility.Visible;
        Grid.SetColumn(LiveRenderPanel, 0);
        Grid.SetColumnSpan(LiveRenderPanel, 1);
        Grid.SetColumn(CalibrationRenderPanel, 2);
        Grid.SetColumnSpan(CalibrationRenderPanel, 1);
        LiveFullscreenButton.Content = "\uE740";
        CalibrationFullscreenButton.Content = "\uE740";
    }

    private void ApplyCalibrationShellLayout()
    {
        MinWidth = 1024;
        MinHeight = 640;
        CommandBarRow.Height = new GridLength(52);
        StatusBarRow.Height = new GridLength(34);
        CommandBar.Visibility = Visibility.Visible;
        StatusBar.Visibility = Visibility.Visible;
        WorkspaceGrid.Margin = new Thickness(0, 0, 8, 8);
        LiveRenderHeaderRow.Height = new GridLength(40);
        LiveRenderHeader.Visibility = Visibility.Visible;
        ImagePlaceholderText.Text = "等待实时图像";
    }

    private void ApplyInspectionShellLayout()
    {
        MinWidth = 1;
        MinHeight = 1;
        CommandBarRow.Height = new GridLength(0);
        StatusBarRow.Height = new GridLength(0);
        CommandBar.Visibility = Visibility.Collapsed;
        StatusBar.Visibility = Visibility.Collapsed;
        WorkspaceGrid.Margin = new Thickness(0);
        WorkspaceSidebarColumn.Width = new GridLength(0);
        WorkspaceGapColumn.Width = new GridLength(0);
        CalibrationSidebar.Visibility = Visibility.Collapsed;
        LiveRenderHeaderRow.Height = new GridLength(0);
        LiveRenderHeader.Visibility = Visibility.Collapsed;
        ImagePlaceholderText.Text = "等待相机图像";
        LiveRenderPanel.Visibility = Visibility.Visible;
        CalibrationRenderPanel.Visibility = Visibility.Collapsed;
        Grid.SetColumn(LiveRenderPanel, 0);
        Grid.SetColumnSpan(LiveRenderPanel, 3);
    }

    private void ApplyLiveRenderLayout()
    {
        ClearNozzleTeachingResultRenderersNoThrow();
        SetRenderPanelLabels("实时画面", "等待实时图像", "标定结果", "等待九点标定");
        ApplySingleRenderLayout(showLive: true);
    }

    private void ApplyCalibrationRenderLayout()
    {
        ClearNozzleTeachingResultRenderersNoThrow();
        SetRenderPanelLabels("实时画面", "等待实时图像", "标定结果", "等待九点标定");
        ApplySingleRenderLayout(showLive: false);
    }

    private void ApplyNozzleTeachingRenderLayout()
    {
        ApplySplitRenderLayout();
        SetRenderPanelLabels(
            "吸嘴1粗定位 · 圆查找2",
            "等待吸嘴1粗定位结果",
            "吸嘴2粗定位 · 圆查找1",
            "等待吸嘴2粗定位结果");
    }

    private void SetRenderPanelLabels(
        string liveTitle,
        string livePlaceholder,
        string calibrationTitle,
        string calibrationPlaceholder)
    {
        LiveRenderTitleText.Text = liveTitle;
        ImagePlaceholderText.Text = livePlaceholder;
        CalibrationRenderTitleText.Text = calibrationTitle;
        CalibrationImagePlaceholderText.Text = calibrationPlaceholder;
    }

    private void ApplySingleRenderLayout(bool showLive)
    {
        _showingSplitRender = false;
        _showingCalibrationRender = !showLive;
        _fullscreenRenderTarget = null;
        WorkspaceSidebarColumn.Width = new GridLength(320);
        WorkspaceGapColumn.Width = new GridLength(6);
        CalibrationSidebar.Visibility = Visibility.Visible;
        LiveRenderPanel.Visibility = showLive ? Visibility.Visible : Visibility.Collapsed;
        CalibrationRenderPanel.Visibility = showLive ? Visibility.Collapsed : Visibility.Visible;
        var visiblePanel = showLive ? LiveRenderPanel : CalibrationRenderPanel;
        Grid.SetColumn(visiblePanel, 0);
        Grid.SetColumnSpan(visiblePanel, 3);
        LiveFullscreenButton.Content = "\uE740";
        CalibrationFullscreenButton.Content = "\uE740";
    }

    private void RefreshRenderLayout()
    {
        RenderGrid.UpdateLayout();
        LiveRenderPanel.UpdateLayout();
        CalibrationRenderPanel.UpdateLayout();
        VisionRenderControl.UpdateLayout();
        CalibrationRenderControl.UpdateLayout();
    }

    private async Task RefreshRenderLayoutAsync()
    {
        RefreshRenderLayout();
        await Dispatcher.InvokeAsync(RefreshRenderLayout, DispatcherPriority.Loaded);
    }

    private async void CalibrationToolbarAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action } || string.IsNullOrWhiteSpace(action))
        {
            return;
        }

        try
        {
            if (string.Equals(action, "SaveProfile", StringComparison.Ordinal))
            {
                SaveCalibrationProfileToolbarButton.Content = "保存中...";
                SaveCalibrationProfileToolbarButton.IsEnabled = false;
                SetStatus("正在保存双吸嘴标定配置...", StatusKind.Busy);
            }

            await SendHostEventAsync($"CALIBRATION_TOOLBAR_ACTION\t{action}");
        }
        catch (Exception exception)
        {
            if (string.Equals(action, "SaveProfile", StringComparison.Ordinal))
            {
                ResetCalibrationSaveFeedback();
            }

            SetStatus($"标定文件菜单操作失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private async void CalibrationSidebarButton_Click(object sender, RoutedEventArgs e)
    {
        if (_applyingCalibrationSidebarState || sender is not Button { Tag: string action })
        {
            return;
        }

        await SendCalibrationSidebarActionNoThrowAsync(action, string.Empty);
    }

    private async void CalibrationSidebarInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_applyingCalibrationSidebarState || sender is not TextBox { Tag: string action } textBox)
        {
            return;
        }

        await SendCalibrationSidebarActionNoThrowAsync(action, textBox.Text);
    }

    private async void CalibrationSidebarSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_applyingCalibrationSidebarState ||
            sender is not ComboBox { Tag: string action } comboBox ||
            comboBox.SelectedItem is not ComboBoxItem selectedItem)
        {
            return;
        }

        await SendCalibrationSidebarActionNoThrowAsync(
            action,
            selectedItem.Tag as string ?? selectedItem.Content?.ToString() ?? string.Empty);
    }

    private async void CalibrationSidebarClickMove_Changed(object sender, RoutedEventArgs e)
    {
        if (_applyingCalibrationSidebarState || sender is not CheckBox checkBox)
        {
            return;
        }

        await SendCalibrationSidebarActionNoThrowAsync(
            "EnableClickMove",
            checkBox.IsChecked == true ? "1" : "0");
    }

    private async Task SendCalibrationSidebarActionNoThrowAsync(string action, string value)
    {
        try
        {
            var encodedValue = Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
            await SendHostEventAsync($"CALIBRATION_SIDEBAR_ACTION\t{action}\t{encodedValue}");
        }
        catch (Exception exception)
        {
            SetStatus($"标定侧栏操作失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void LoadFixedSolution()
    {
        if (_fixedSolutionLoadSource.Task.IsCompleted)
        {
            _fixedSolutionLoadSource =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        _initializingFixedSolution = true;
        SetBusy(true);
        var solutionPath = GetFixedSolutionPath();
        var previousSolutionClosed = false;
        try
        {
            if (!File.Exists(solutionPath))
            {
                _fixedSolutionLoadError =
                    $"未找到固定视觉方案。请将“{FixedSolutionFileName}”或“{FallbackSolutionFileName}”放到当前用户桌面。";
                SetStatus(_fixedSolutionLoadError, StatusKind.Error);
                return;
            }

            SetStatus("正在初始化视觉组件…", StatusKind.Busy);
            CloseCurrentSolution();
            previousSolutionClosed = true;
            _loadedSolutionPath = Path.GetFullPath(solutionPath);
            VmSolution.Load(_loadedSolutionPath, "");
            _solutionLoaded = true;

            var procedureNames = GetProcedureNames();
            _calibrationProcedure = GetRequiredProcedure(_calibrationProcedureName);
            _previewProcedure = _calibrationProcedure;
            _inspectionProcedure = GetRequiredProcedure(_inspectionProcedureName);
            _nozzlePointProcedure = null;
            _calibrationViewActive = false;

            // 方案可能保存了连续运行状态；先缓存本方案流程，再统一停止，
            // 确保不会遗漏当前标定和实时画面流程。
            StopAllContinuousExecutionNoThrow();

            // 保留隐藏控件仅供现有渲染/标定逻辑读取；用户不能再切换方案或流程。
            PreviewProcedureComboBox.ItemsSource = procedureNames;
            CalibrationProcedureComboBox.ItemsSource = procedureNames;
            PreviewProcedureComboBox.SelectedItem = _calibrationProcedureName;
            CalibrationProcedureComboBox.SelectedItem = _calibrationProcedureName;

            _settings.SolutionPath = _loadedSolutionPath;
            _settings.PreviewProcedureName = _calibrationProcedureName;
            _settings.CalibrationProcedureName = _calibrationProcedureName;
            SolutionPathTextBox.Text = _loadedSolutionPath;
            PopulateImageSteps(_calibrationProcedureName, _previewProcedure);
            SaveSettingsNoThrow();
            _fixedSolutionLoadError = "";

            UpdateCommandState();
            SetStatus("视觉组件已就绪", StatusKind.Success);
        }
        catch (Exception exception)
        {
            if (previousSolutionClosed)
            {
                CloseCurrentSolutionNoThrow();
            }

            _fixedSolutionLoadError = $"固定视觉方案加载失败：{FormatException(exception)}";
            SetStatus(_fixedSolutionLoadError, StatusKind.Error);
        }
        finally
        {
            _initializingFixedSolution = false;
            SetBusy(false);
            _fixedSolutionLoadSource.TrySetResult(_solutionLoaded);
        }
    }

    private async Task WaitForFixedSolutionAsync()
    {
        var loadSource = _fixedSolutionLoadSource;
        if (!loadSource.Task.IsCompleted || _initializingFixedSolution)
        {
            await loadSource.Task;
        }

        if (!_solutionLoaded)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(_fixedSolutionLoadError)
                    ? $"固定视觉方案加载失败，请确认桌面存在“{FixedSolutionFileName}”或“{FallbackSolutionFileName}”。"
                    : _fixedSolutionLoadError);
        }
    }

    private string GetFixedSolutionPath()
    {
        if (!string.IsNullOrWhiteSpace(_configuredSolutionPath))
        {
            return _configuredSolutionPath!;
        }

        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        var preferredPath = Path.Combine(desktop, FixedSolutionFileName);
        if (File.Exists(preferredPath))
        {
            return preferredPath;
        }

        var fallbackPath = Path.Combine(desktop, FallbackSolutionFileName);
        return File.Exists(fallbackPath) ? fallbackPath : preferredPath;
    }

    private static VmProcedure GetRequiredProcedure(string procedureName)
    {
        return VmSolution.Instance[procedureName] as VmProcedure
            ?? throw new InvalidOperationException(
                $"固定方案中未找到必需流程“{procedureName}”。");
    }

    private void PreviewProcedureComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingFixedSolution ||
            !_solutionLoaded ||
            PreviewProcedureComboBox.SelectedItem is not string procedureName)
        {
            return;
        }

        try
        {
            StopPreviewProcedureNoThrow();
            _previewProcedure = VmSolution.Instance[procedureName] as VmProcedure
                ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
            _settings.PreviewProcedureName = procedureName;
            PopulateImageSteps(procedureName, _previewProcedure);
            SaveSettingsNoThrow();
            if (TryStartLivePreview(out var previewError))
            {
                SetStatus($"{procedureName} 已单次采集到画面1", StatusKind.Success);
            }
            else
            {
                SetStatus($"流程已加载，但画面1采集失败：{previewError}", StatusKind.Error);
            }
        }
        catch (Exception exception)
        {
            _previewProcedure = null;
            ImageStepComboBox.ItemsSource = null;
            ClearRenderer();
            UpdateCommandState();
            SetStatus($"实时流程加载失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void CalibrationProcedureComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingFixedSolution ||
            !_solutionLoaded ||
            CalibrationProcedureComboBox.SelectedItem is not string procedureName)
        {
            return;
        }

        try
        {
            _calibrationProcedure = VmSolution.Instance[procedureName] as VmProcedure
                ?? throw new InvalidOperationException($"方案中未找到流程“{procedureName}”。");
            _settings.CalibrationProcedureName = procedureName;
            var nPointModule = ResolveNPointCalibrationModule(procedureName);
            ClearCalibrationRenderer();
            BindCalibrationModule(nPointModule);
            SaveSettingsNoThrow();
            UpdateCommandState();
            SetStatus($"标定流程已选择：{procedureName}", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            _calibrationProcedure = null;
            ClearCalibrationRenderer();
            UpdateCommandState();
            SetStatus($"标定流程加载失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void PopulateImageSteps(string procedureName, VmProcedure procedure)
    {
        var options = GetImageStepOptions(procedureName, procedure);
        ImageStepComboBox.ItemsSource = options;
        ImageStepComboBox.SelectedItem = options.FirstOrDefault(option =>
            string.Equals(option.DisplayName, CalibrationImageSourceName, StringComparison.Ordinal))
            ?? throw new InvalidOperationException(
                $"流程“{procedureName}”中未找到“{CalibrationImageSourceName}”。");
    }

    private static IReadOnlyList<VisionModuleOption> GetImageStepOptions(
        string procedureName,
        VmProcedure procedure)
    {
        var options = new List<VisionModuleOption>();
        var moduleList = procedure.GetProcedureModuleList();
        for (var index = 0; index < moduleList.nNum; index++)
        {
            var info = moduleList.astModuleInfo[index];
            var displayName = info.strDisplayName?.Trim() ?? "";
            var moduleName = info.strModuleName?.Trim() ?? "";
            if (string.IsNullOrWhiteSpace(displayName))
            {
                continue;
            }

            var option = ResolveModuleOption(procedureName, displayName, moduleName);
            if (option is not null
                && options.All(existing =>
                    !string.Equals(existing.ModuleKey, option.ModuleKey, StringComparison.Ordinal)))
            {
                options.Add(option);
            }
        }

        if (options.Count == 0)
        {
            options.Add(new VisionModuleOption(
                $"{procedureName}（流程图像）",
                procedureName,
                procedure,
                isImageSource: false));
        }

        return options;
    }

    private static VisionModuleOption? ResolveModuleOption(
        string procedureName,
        string displayName,
        string moduleName)
    {
        var candidates = new[]
        {
            $"{procedureName}.{displayName}",
            $"{procedureName}.{moduleName}",
            moduleName,
            displayName
        };

        foreach (var candidate in candidates
                     .Where(value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.Ordinal))
        {
            try
            {
                if (VmSolution.Instance[candidate] is IVmModule module)
                {
                    var isImageSource =
                        displayName.Contains("图像源") ||
                        displayName.Contains("图像采集") ||
                        moduleName.IndexOf("ImageSource", StringComparison.OrdinalIgnoreCase) >= 0;
                    return new VisionModuleOption(displayName, candidate, module, isImageSource);
                }
            }
            catch
            {
                // Some SDK module names are type names rather than lookup paths.
            }
        }

        return null;
    }

    private void ImageStepComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializingFixedSolution ||
            !_solutionLoaded ||
            ImageStepComboBox.SelectedItem is not VisionModuleOption option)
        {
            return;
        }

        try
        {
            BindImageStep(option, persistSelection: true);
            SetStatus($"当前图像：{PreviewProcedureComboBox.SelectedItem} / {option.DisplayName}", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            ClearRenderer();
            SetStatus($"图像步骤绑定失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void RunOnce_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProcedureReady())
        {
            return;
        }

        try
        {
            var stopwatch = Stopwatch.StartNew();
            CapturePreviewFrame();
            stopwatch.Stop();
            SetStatus(
                $"画面1采集完成，用时 {stopwatch.Elapsed.TotalMilliseconds:0.0} ms",
                StatusKind.Success);
        }
        catch (Exception exception)
        {
            SetStatus($"画面1采集失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private void ContinuousRun_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureProcedureReady())
        {
            return;
        }

        try
        {
            if (TryStartLivePreview(out var previewError))
            {
                SetStatus("画面1采集完成", StatusKind.Success);
            }
            else
            {
                SetStatus($"画面1采集失败：{previewError}", StatusKind.Error);
            }
        }
        catch (Exception exception)
        {
            SetStatus($"画面1采集失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private async Task<string> StartLivePreviewFromCommandAsync()
    {
        if (!EnsureProcedureReady())
        {
            throw new InvalidOperationException("固定视觉方案或实时相机流程尚未就绪。");
        }

        // 先启动连续取像并通过模块回调等待首帧，避免用不同图像模块的私有结果字段
        // 猜测是否出图。收到首帧后再绑定渲染控件。
        var imageOption = ImageStepComboBox.SelectedItem as VisionModuleOption
            ?? throw new InvalidOperationException("当前实时画面模块尚未选择。");
        PrepareImageStepForFirstFrame(imageOption);
        ImagePlaceholderText.Text = "正在连接相机，等待首帧…";
        ImagePlaceholder.Visibility = Visibility.Visible;
        var firstFrameSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        _livePreviewFirstFrameSource = firstFrameSource;
        if (!TryStartLivePreview(out var previewError, bindSelectedImageStep: false))
        {
            _livePreviewFirstFrameSource = null;
            throw new InvalidOperationException($"实时画面启动失败：{previewError}");
        }

        var expectedGeneration = _liveRenderGeneration;
        var firstFrameWait = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                var remaining = TimeSpan.FromSeconds(10) - firstFrameWait.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    ImagePlaceholderText.Text = "等待首帧超时，请检查相机连接";
                    throw new TimeoutException("实时相机在 10 秒内未返回图像数据。");
                }

                var completedTask = await Task.WhenAny(firstFrameSource.Task, Task.Delay(remaining));
                if (!ReferenceEquals(completedTask, firstFrameSource.Task))
                {
                    ImagePlaceholderText.Text = "等待首帧超时，请检查相机连接";
                    throw new TimeoutException("实时相机在 10 秒内未返回图像数据。");
                }

                var receivedGeneration = await firstFrameSource.Task;
                if (receivedGeneration == expectedGeneration)
                {
                    break;
                }

                firstFrameSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
                _livePreviewFirstFrameSource = firstFrameSource;
            }
        }
        finally
        {
            if (ReferenceEquals(_livePreviewFirstFrameSource, firstFrameSource))
            {
                _livePreviewFirstFrameSource = null;
            }
        }

        VisionRenderControl.ModuleSource = imageOption.Module;
        ApplyLiveRenderLayout();
        RefreshRenderLayout();
        VisionRenderControl.UpdateVMResultShow();
        _livePreviewRenderReady = true;
        ImagePlaceholder.Visibility = Visibility.Collapsed;
        QueueClickCenterInitialization();
        SetStatus("实时画面已就绪。", StatusKind.Success);
        return "实时画面已就绪。";
    }

    private void StopRun_Click(object sender, RoutedEventArgs e)
    {
        if (_previewProcedure is null)
        {
            return;
        }

        try
        {
            StopAllContinuousExecutionNoThrow();
            UpdateCommandState();
            SetStatus("相机流程已停止", StatusKind.Ready);
        }
        catch (Exception exception)
        {
            SetStatus($"停止流程失败：{FormatException(exception)}", StatusKind.Error);
        }
    }

    private static IReadOnlyList<string> GetProcedureNames()
    {
        var procedureList = VmSolution.Instance.GetAllProcedureList();
        var names = new List<string>(checked((int)procedureList.nNum));
        for (var index = 0; index < procedureList.nNum; index++)
        {
            var name = procedureList.astProcessInfo[index].strProcessName?.Trim();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name!);
            }
        }

        return names;
    }

    private bool EnsureProcedureReady()
    {
        if (_solutionLoaded && _previewProcedure is not null)
        {
            return true;
        }

        SetStatus("固定视觉方案或实时相机流程尚未就绪。", StatusKind.Error);
        return false;
    }

    private void CloseCurrentSolution()
    {
        DetachCrosshairModule();
        _clickMoveEnabled = false;
        StopAllContinuousExecutionNoThrow();
        ClearRenderer();

        if (_solutionLoaded)
        {
            VmSolution.Instance?.CloseSolution();
        }

        _previewProcedure = null;
        _inspectionProcedure = null;
        _nozzlePointProcedure = null;
        _calibrationProcedure = null;
        _calibrationViewActive = false;
        _loadedSolutionPath = "";
        _solutionLoaded = false;
        PreviewProcedureComboBox.ItemsSource = null;
        CalibrationProcedureComboBox.ItemsSource = null;
        ImageStepComboBox.ItemsSource = null;
        UpdateCommandState();
    }

    private void CloseCurrentSolutionNoThrow()
    {
        DetachCrosshairModule();
        _clickMoveEnabled = false;
        StopAllContinuousExecutionNoThrow();
        ClearRenderer();
        try
        {
            VmSolution.Instance?.CloseSolution();
        }
        catch
        {
        }

        _previewProcedure = null;
        _inspectionProcedure = null;
        _nozzlePointProcedure = null;
        _calibrationProcedure = null;
        _calibrationViewActive = false;
        _loadedSolutionPath = "";
        _solutionLoaded = false;
        PreviewProcedureComboBox.ItemsSource = null;
        CalibrationProcedureComboBox.ItemsSource = null;
        ImageStepComboBox.ItemsSource = null;
        UpdateCommandState();
    }

    private void ClearRenderer()
    {
        _displayedModule = null;
        try
        {
            VisionRenderControl.ModuleSource = null;
            VisionRenderControl.ClearDisplayView();
        }
        catch
        {
        }

        ClearCalibrationRenderer();

        if (_sdkAvailable)
        {
            CenterCrosshair.Visibility = Visibility.Collapsed;
            ImagePlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void ClearCalibrationRenderer()
    {
        ClearNozzleTeachingResultRenderersNoThrow();
        _calibrationRenderGeneration++;
        try
        {
            CalibrationRenderControl.ModuleSource = null;
            CalibrationRenderControl.ClearDisplayView();
        }
        catch
        {
        }

        if (_sdkAvailable)
        {
            CalibrationImagePlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void BindCalibrationModule(IMVSNPointCalibModuTool module)
    {
        _calibrationRenderGeneration++;
        CalibrationImagePlaceholder.Visibility = Visibility.Visible;
        // N 点模块本身没有图像输出字段；VmRenderControl 会沿模块输入关系取上游
        // 图像源作为底图，并把当前标定点、进度和状态作为渲染图形叠加显示。
        CalibrationRenderControl.ModuleSource = module;
    }

    private async Task RefreshCalibrationResultDisplayAsync()
    {
        // PREPARE 已经绑定 N 点模块。九次采集期间必须保持这一绑定，不能清屏或
        // 重新赋值 ModuleSource，否则底图回调与标定图形回调会竞态，造成叠加时有时无。
        // 先保证最终补刷已经入队；同步刷新暂时失败也不能短路后续重试。
        QueueCalibrationResultRefreshNoThrow(DispatcherPriority.ContextIdle);
        Exception? lastDisplayException = null;
        var displayed = false;
        try
        {
            RefreshCalibrationResultDisplay();
            displayed = true;
        }
        catch (Exception exception)
        {
            lastDisplayException = exception;
        }

        // 让本次流程排入队列的底图/渲染回调先提交，再在同一绑定上补刷一次叠加。
        // 这里只等待 Render，不再等待可能长期无法到达的 ContextIdle。
        try
        {
            await Dispatcher.InvokeAsync(
                () =>
                {
                    try
                    {
                        RefreshCalibrationResultDisplay();
                        displayed = true;
                    }
                    catch (Exception exception)
                    {
                        lastDisplayException = exception;
                    }
                },
                DispatcherPriority.Render);
        }
        catch (Exception exception)
        {
            lastDisplayException = exception;
        }

        if (!displayed && lastDisplayException is not null)
        {
            throw new InvalidOperationException(
                "标定流程执行成功，但当前渲染结果尚未发布；已安排延迟补刷。",
                lastDisplayException);
        }
    }

    private void RefreshCalibrationResultDisplay()
    {
        CalibrationRenderControl.UpdateVMResultShow();
        CalibrationImagePlaceholder.Visibility = Visibility.Collapsed;
    }

    private void QueueCalibrationResultRefreshNoThrow(DispatcherPriority priority)
    {
        var generation = _calibrationRenderGeneration;
        try
        {
            _ = Dispatcher.InvokeAsync(
                () =>
                {
                    if (generation != _calibrationRenderGeneration)
                    {
                        return;
                    }

                    try
                    {
                        RefreshCalibrationResultDisplay();
                    }
                    catch
                    {
                        // 标定数据已经保存/保留；延迟补刷失败不改变流程结果。
                    }
                },
                priority);
        }
        catch
        {
            // Dispatcher 正在关闭时不再安排显示刷新，标定数据状态不受影响。
        }
    }

    private void StopPreviewProcedureNoThrow()
    {
        if (_previewProcedure is null)
        {
            return;
        }

        try
        {
            _previewProcedure.ContinuousRunEnable = false;
        }
        catch
        {
        }
    }

    private void RestoreRealtimePreviewNoThrow()
    {
        if (_previewProcedure is null)
        {
            return;
        }

        try
        {
            var options = GetImageStepOptions(_activeCalibrationProcedureName, _previewProcedure);
            ImageStepComboBox.ItemsSource = options;
            var imageOption = options.FirstOrDefault(option =>
                string.Equals(option.DisplayName, CalibrationImageSourceName, StringComparison.Ordinal));
            if (imageOption is not null)
            {
                BindImageStep(imageOption, persistSelection: false);
            }

        }
        catch
        {
            // Blob 结果已经取得；恢复实时画面失败不能改变本次检测结果。
        }
    }

    private void RunCalibrationProcedureOnce()
    {
        if (_calibrationProcedure is null)
        {
            throw new InvalidOperationException("当前 VisionMaster 标定流程已失效。");
        }

        // The camera belongs exclusively to the calibration flow until all nine points finish.
        StopAllContinuousExecutionNoThrow();
        _calibrationProcedure.Run(true);
    }

    private void CapturePreviewFrame()
    {
        if (_previewProcedure is null)
        {
            throw new InvalidOperationException("实时相机流程尚未加载。");
        }

        StopAllContinuousExecutionNoThrow();
        _previewProcedure.Run(true);
        VisionRenderControl.UpdateVMResultShow();
    }

    private bool TryStartLivePreview(
        out string errorMessage,
        bool bindSelectedImageStep = true)
    {
        if (!_solutionLoaded || _previewProcedure is null)
        {
            errorMessage = "视觉方案或流程尚未加载";
            return false;
        }

        if (_calibrationSession is not null)
        {
            errorMessage = "九点标定正在执行";
            return false;
        }

        var stage = "绑定实时画面";
        try
        {
            if (bindSelectedImageStep &&
                ImageStepComboBox.SelectedItem is VisionModuleOption option)
            {
                BindImageStep(option, persistSelection: false);
            }

            stage = "启动实时相机流程";
            StopAllContinuousExecutionNoThrow();
            _previewProcedure.ContinuousRunEnable = true;
            UpdateCommandState();
            errorMessage = "";
            return true;
        }
        catch (Exception exception)
        {
            UpdateCommandState();
            errorMessage = $"{stage}失败：{FormatException(exception)}";
            return false;
        }
    }

    private void BindImageStep(VisionModuleOption option, bool persistSelection)
    {
        PrepareImageStepForFirstFrame(option);
        VisionRenderControl.ModuleSource = option.Module;
        _livePreviewRenderReady = true;

        if (!persistSelection)
        {
            return;
        }

        _settings.ImageModuleKey = option.ModuleKey;
        SaveSettingsNoThrow();
    }

    private void PrepareImageStepForFirstFrame(VisionModuleOption option)
    {
        ClearNozzleTeachingResultRenderersNoThrow();
        DetachCrosshairModule();
        _livePreviewRenderReady = false;
        _clickCenterPixelReady = false;
        _clickImagePixelWidth = 0;
        _clickImagePixelHeight = 0;
        _displayedModule = option.Module as VmModule;
        Interlocked.Increment(ref _liveRenderGeneration);
        ImagePlaceholder.Visibility = Visibility.Visible;
        try
        {
            VisionRenderControl.ModuleSource = null;
            VisionRenderControl.ClearDisplayView();
        }
        catch
        {
            // 清空旧帧只影响显示；相机首帧仍通过模块结果回调判定。
        }

        CenterCrosshair.Visibility = Visibility.Collapsed;
        AttachCrosshairModule();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateCommandState();
    }

    private void FinishCalibrationUiNoThrow(string message, StatusKind statusKind)
    {
        // 标定保存或中止的协议结果不能再被纯 UI 收尾异常改写。
        _busy = false;
        try
        {
            UpdateCommandState();
        }
        catch
        {
        }

        try
        {
            SetStatus(message, statusKind);
        }
        catch
        {
        }
    }

    private void UpdateCommandState()
    {
        if (!_sdkAvailable)
        {
            CommandBar.IsEnabled = false;
            return;
        }

        CommandBar.IsEnabled = true;
        var previewReady = _solutionLoaded && _previewProcedure is not null;
        var calibrationReady = _solutionLoaded && _calibrationProcedure is not null;
        var continuousRunning = previewReady && _previewProcedure!.ContinuousRunEnable;
        ChooseSolutionButton.IsEnabled = false;
        PreviewProcedureComboBox.IsEnabled = false;
        CalibrationProcedureComboBox.IsEnabled = false;
        ImageStepComboBox.IsEnabled = !_busy && previewReady && !continuousRunning && !_clickMoveEnabled;
        RunOnceButton.IsEnabled = !_busy && previewReady && !continuousRunning;
        ContinuousRunButton.IsEnabled = !_busy && previewReady && !continuousRunning;
        StopRunButton.IsEnabled = !_busy && continuousRunning;
        if (!calibrationReady && _solutionLoaded)
        {
            CalibrationProcedureComboBox.ToolTip = "固定方案中的标定流程未就绪";
        }
    }

    private void StopAllContinuousExecutionNoThrow()
    {
        try
        {
            if (VmSolution.Instance is not null)
            {
                VmSolution.Instance.ContinuousRunEnable = false;
            }
        }
        catch
        {
        }

        foreach (var procedure in new[] { _previewProcedure, _inspectionProcedure, _nozzlePointProcedure, _calibrationProcedure })
        {
            if (procedure is null)
            {
                continue;
            }

            try
            {
                procedure.ContinuousRunEnable = false;
            }
            catch
            {
            }
        }
    }

    private void SaveSettingsNoThrow()
    {
        try
        {
            _settings.Save();
        }
        catch
        {
            // The vision workflow remains usable even if local preferences cannot be persisted.
        }
    }

    private void SetStatus(string message, StatusKind kind)
    {
        StatusTextBlock.Text = message;
        StatusIndicator.Fill = new SolidColorBrush(kind switch
        {
            StatusKind.Success => Color.FromRgb(57, 197, 107),
            StatusKind.Error => Color.FromRgb(217, 13, 22),
            StatusKind.Busy => Color.FromRgb(13, 110, 232),
            _ => Color.FromRgb(224, 162, 26)
        });
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

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        DetachCrosshairModule();
        _commandPipeCancellation.Cancel();
        lock (_commandPipeSync)
        {
            try
            {
                _activeCommandPipe?.Dispose();
            }
            catch
            {
            }

            _activeCommandPipe = null;
        }

        StopAllContinuousExecutionNoThrow();
        try
        {
            _standaloneTransformModule?.Dispose();
            _standaloneTransformModule = null;
        }
        catch
        {
        }

        try
        {
            _standaloneBlobModule?.Dispose();
            _standaloneBlobModule = null;
        }
        catch
        {
        }

        try
        {
            VisionRenderControl.Dispose();
        }
        catch
        {
        }

        try
        {
            CalibrationRenderControl.Dispose();
        }
        catch
        {
        }

        try
        {
            VmSolution.Instance?.CloseSolution();
        }
        catch
        {
        }

        try
        {
            VmSolution.Instance?.Dispose();
        }
        catch
        {
        }
    }

    private sealed class VisionModuleOption
    {
        public VisionModuleOption(
            string displayName,
            string moduleKey,
            IVmModule module,
            bool isImageSource)
        {
            DisplayName = displayName;
            ModuleKey = moduleKey;
            Module = module;
            IsImageSource = isImageSource;
        }

        public string DisplayName { get; }

        public string ModuleKey { get; }

        public IVmModule Module { get; }

        public bool IsImageSource { get; }
    }

    private sealed class VisionCalibrationSession
    {
        public VisionCalibrationSession(
            IMVSNPointCalibModuTool module,
            string calibrationPath,
            string? backupPath)
        {
            Module = module;
            CalibrationPath = calibrationPath;
            BackupPath = backupPath;
        }

        public IMVSNPointCalibModuTool Module { get; }

        public string CalibrationPath { get; }

        public string? BackupPath { get; }

        public int NextPointNumber { get; set; } = 1;
    }

    private sealed class RectangleBlobCandidate
    {
        public RectangleBlobCandidate(
            float pixelX,
            float pixelY,
            float rectangularity,
            float area,
            int left,
            int top,
            int width,
            int height,
            float rotationDegrees = 0f)
        {
            PixelX = pixelX;
            PixelY = pixelY;
            Rectangularity = rectangularity;
            Area = area;
            Left = left;
            Top = top;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
            RotationDegrees = rotationDegrees;
        }

        public float PixelX { get; }

        public float PixelY { get; }

        public float RotationDegrees { get; }

        public float Rectangularity { get; }

        public float Area { get; }

        public int Left { get; }

        public int Top { get; }

        public int Width { get; }

        public int Height { get; }
    }

    private sealed class InspectionImageFile
    {
        public InspectionImageFile(string filePath, int pixelWidth, int pixelHeight)
        {
            FilePath = filePath;
            PixelWidth = pixelWidth;
            PixelHeight = pixelHeight;
        }

        public string FilePath { get; }

        public int PixelWidth { get; }

        public int PixelHeight { get; }
    }

    private sealed class ManualNozzleCircle
    {
        public ManualNozzleCircle(Point center, double radius)
        {
            Center = center;
            Radius = radius;
        }

        public Point Center { get; }

        public double Radius { get; }
    }

    private enum StatusKind
    {
        Ready,
        Busy,
        Success,
        Error
    }
}
