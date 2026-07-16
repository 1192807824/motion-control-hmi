using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using IMVSBlobFindModuCs;
using IMVSCalibTransformModuCs;
using IMVSNPointCalibModuCs;
using VM.Core;
using VM.PlatformSDKCS;
using VMControls.Interface;

namespace VisionMasterHost;

public partial class MainWindow : Window
{
    private const string FixedSolutionFileName = "新纳方案.sol";
    private const string FallbackSolutionFileName = "标定方案.sol";
    private const string InspectionProcedureName = "找芯片流程";
    private const string CalibrationProcedureName = "标定流程";
    private const string InspectionBlobModuleName = "Blob分析1";

    private readonly VisionCalibrationSettings _settings = VisionCalibrationSettings.Load();
    private readonly bool _embedded;
    private readonly string? _commandPipeName;
    private readonly string? _eventPipeName;
    private readonly CancellationTokenSource _commandPipeCancellation = new();
    private readonly object _commandPipeSync = new();
    private VmProcedure? _previewProcedure;
    private VmProcedure? _inspectionProcedure;
    private VmProcedure? _calibrationProcedure;
    private IMVSCalibTransformModuTool? _standaloneTransformModule;
    private IMVSBlobFindModuTool? _standaloneBlobModule;
    private VmModule? _displayedModule;
    private VmModule? _crosshairModule;
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
    private bool _clickMoveEnabled;
    private bool _clickTransformBusy;
    private bool _applyingCalibrationSidebarState;
    private bool _clickCenterPixelReady;
    private float _clickCenterPixelX;
    private float _clickCenterPixelY;
    private int _clickImagePixelWidth;
    private int _clickImagePixelHeight;
    private int _clickCenterInitializationQueued;
    private string _clickCalibrationPath = "";
    private string? _fullscreenRenderTarget;
    private bool _showingCalibrationRender;

    public MainWindow(
        bool embedded,
        string? commandPipeName = null,
        string? eventPipeName = null)
    {
        InitializeComponent();
        _embedded = embedded;
        _commandPipeName = string.IsNullOrWhiteSpace(commandPipeName) ? null : commandPipeName;
        _eventPipeName = string.IsNullOrWhiteSpace(eventPipeName) ? null : eventPipeName;
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

        SetStatus("视觉方案按需加载：主页找芯片或进入视觉标定时才打开", StatusKind.Ready);
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
                "DEACTIVATE_CALIBRATION_VIEW" => await DeactivateCalibrationViewAsync(),
                "PREPARE" => await PrepareNinePointCalibrationAsync(command.Split('\t')),
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
            "CAPTURE" => CaptureNinePointCalibration(parts),
            "COMPLETE" => throw new InvalidOperationException("COMPLETE must be executed asynchronously."),
            "ABORT" => throw new InvalidOperationException("ABORT must be executed asynchronously."),
            "SET_CLICK_MODE" => SetClickMoveMode(parts),
            "IMPORT_CALIBRATION_FILE" => ImportCalibrationFile(parts),
            "SET_CALIBRATION_TOOLBAR_STATE" => SetCalibrationToolbarState(parts),
            "SET_CALIBRATION_SIDEBAR_STATE" => SetCalibrationSidebarState(parts),
            "TRANSFORM_PIXEL" => TransformPixel(parts),
            "RUN_RECTANGLE_BLOB" => RunRectangleBlobInspection(parts),
            _ => throw new InvalidOperationException($"不支持的视觉标定命令：{parts[0]}")
        };
    }

    private async Task<string> ActivateCalibrationViewAsync()
    {
        if (_calibrationViewActive && _calibrationProcedure is not null)
        {
            return "标定界面已经开启。";
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

        _calibrationProcedure = GetRequiredProcedure(CalibrationProcedureName);
        _calibrationViewActive = true;

        CalibrationProcedureComboBox.SelectedItem = CalibrationProcedureName;
        ApplyLiveRenderLayout();
        await RefreshRenderLayoutAsync();
        ClearCalibrationRenderer();
        BindCalibrationModule(ResolveNPointCalibrationModule(CalibrationProcedureName));
        if (!TryStartLivePreview(out var previewError))
        {
            throw new InvalidOperationException($"实时画面启动失败：{previewError}");
        }
        await RefreshRenderLayoutAsync();

        UpdateCommandState();
        SetStatus("标定界面已开启：使用标定流程图像源，标定流程已就绪。", StatusKind.Success);
        return "标定界面已开启。";
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
        CloseCurrentSolutionNoThrow();
        UpdateCommandState();
        SetStatus("已离开视觉标定：实时画面和新纳方案均已关闭", StatusKind.Ready);
        return "标定界面已关闭。";
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
    /// 读取“Blob分析1”的前两行。所有相机流程均互斥、单次执行。
    /// </summary>
    private string RunRectangleBlobInspection(IReadOnlyList<string> parts)
    {
        if (parts.Count != 1)
        {
            throw new InvalidDataException("找芯片流程命令参数不正确。");
        }

        if (_busy || _calibrationSession is not null)
        {
            throw new InvalidOperationException("视觉标定正在执行，暂不允许拍照检测。");
        }

        if (!_solutionLoaded || _inspectionProcedure is null)
        {
            // 进程窗口先于方案加载完成时，主页可能已经发来第一次检测命令。
            // 在 UI 线程内同步确保固定方案已就绪，避免出现启动时序导致的方案切换或误报。
            LoadFixedSolution();
        }

        if (!_solutionLoaded || _inspectionProcedure is null)
        {
            throw new InvalidOperationException(
                $"固定方案尚未加载完成，请确认桌面存在“{FixedSolutionFileName}”或“{FallbackSolutionFileName}”。");
        }

        var procedure = _inspectionProcedure;
        var blobModule = ResolveNamedBlobFindModule(InspectionProcedureName, InspectionBlobModuleName);
        // 同一相机不能被两个流程同时占用。找芯片前明确停止方案内的连续执行。
        StopAllContinuousExecutionNoThrow();

        var imageStepBound = TryBindInspectionImageStep(InspectionProcedureName, procedure);

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
                        ? $"固定方案中的{InspectionProcedureName}执行异常。"
                        : $"固定方案中的{InspectionProcedureName}执行异常：{details}");
            }

            var blobResult = blobModule.ModuResult;
            if (blobResult.ModuStatus != 1)
            {
                throw new InvalidOperationException(
                    $"{InspectionProcedureName}.{InspectionBlobModuleName}返回NG，请检查相机图和模块参数。");
            }

            var resultCount = Math.Min(blobResult.BlobNum, blobResult.CentroidPoint?.Count ?? 0);
            if (resultCount < 2)
            {
                throw new InvalidOperationException(
                    $"{InspectionProcedureName}.{InspectionBlobModuleName}只返回 {resultCount} 个结果，至少需要两个。");
            }

            // 与 VisionMaster 的“当前结果”表严格一致：直接读取第0、1行，不筛选也不重排。
            var first = ReadBlobResultRow(blobResult, 0);
            var second = ReadBlobResultRow(blobResult, 1);

            var imageWarning = "";
            if (imageStepBound)
            {
                try
                {
                    VisionRenderControl.UpdateVMResultShow();
                    inspectionImage = SaveInspectionImage();
                }
                catch (Exception exception)
                {
                    // X/Y 是本次生产步骤的必要结果；检测图保存失败时仍然把坐标返回主页。
                    imageWarning = $"；检测图未返回：{FormatException(exception)}";
                }
            }

            SetStatus(
                $"{InspectionProcedureName}执行完成：结果1({first.PixelX:0.###}, {first.PixelY:0.###})，" +
                $"结果2({second.PixelX:0.###}, {second.PixelY:0.###}){imageWarning}",
                StatusKind.Success);
            return string.Join(
                "\t",
                first.PixelX.ToString("R", CultureInfo.InvariantCulture),
                first.PixelY.ToString("R", CultureInfo.InvariantCulture),
                first.Left.ToString(CultureInfo.InvariantCulture),
                first.Top.ToString(CultureInfo.InvariantCulture),
                first.Width.ToString(CultureInfo.InvariantCulture),
                first.Height.ToString(CultureInfo.InvariantCulture),
                second.PixelX.ToString("R", CultureInfo.InvariantCulture),
                second.PixelY.ToString("R", CultureInfo.InvariantCulture),
                second.Left.ToString(CultureInfo.InvariantCulture),
                second.Top.ToString(CultureInfo.InvariantCulture),
                second.Width.ToString(CultureInfo.InvariantCulture),
                second.Height.ToString(CultureInfo.InvariantCulture),
                (inspectionImage?.PixelWidth ?? 0).ToString(CultureInfo.InvariantCulture),
                (inspectionImage?.PixelHeight ?? 0).ToString(CultureInfo.InvariantCulture),
                inspectionImage?.FilePath ?? "");
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
                // 找芯片流程始终单次执行；结束后只恢复画面1绑定，不启动连续取流。
                procedure.ContinuousRunEnable = false;
                RestoreRealtimePreviewNoThrow();
            }

            UpdateCommandState();
        }
    }

    private string SetCalibrationToolbarState(IReadOnlyList<string> parts)
    {
        if (parts.Count != 6)
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

        CalibrationToolbarPathTextBox.Text = path;
        CalibrationToolbarPathTextBox.ToolTip = string.IsNullOrWhiteSpace(path)
            ? "当前九点标定文件"
            : path;
        CalibrationToolbarPathTextBox.IsEnabled = parts[2] == "1";
        ChooseCalibrationToolbarButton.IsEnabled = parts[3] == "1";
        ImportCalibrationToolbarButton.IsEnabled = parts[4] == "1";
        LoadCalibrationProfileToolbarButton.IsEnabled = parts[5] == "1";
        return "标定文件菜单状态已更新。";
    }

    private string SetCalibrationSidebarState(IReadOnlyList<string> parts)
    {
        if (parts.Count != 29)
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
            SidebarRecordCenterButton.IsEnabled = parts[15] == "1";
            SidebarStartCalibrationButton.IsEnabled = parts[16] == "1" || parts[17] == "1";
            var calibrationRunning = parts[17] == "1";
            SidebarStartCalibrationButton.Tag = calibrationRunning ? "StopCalibration" : "StartCalibration";
            SidebarStartCalibrationButton.Content = calibrationRunning ? "停止九点标定" : "一键九点标定";
            SidebarStartCalibrationButton.Background = new SolidColorBrush(
                calibrationRunning ? Color.FromRgb(117, 18, 28) : Color.FromRgb(0, 169, 101));
            SidebarStartCalibrationButton.BorderBrush = new SolidColorBrush(
                calibrationRunning ? Color.FromRgb(217, 13, 22) : Color.FromRgb(0, 199, 120));
            var parameterInputsEnabled = parts[18] == "1";
            SidebarStepXTextBox.IsEnabled = parameterInputsEnabled;
            SidebarStepYTextBox.IsEnabled = parameterInputsEnabled;
            SidebarMovePriorityComboBox.IsEnabled = parameterInputsEnabled;
            SidebarVelocityTextBox.IsEnabled = parameterInputsEnabled;
            SidebarSettleTextBox.IsEnabled = parameterInputsEnabled;
            SidebarRecordCameraButton.IsEnabled = parts[19] == "1";
            SidebarRecordNozzleButton.IsEnabled = parts[20] == "1";
            SidebarClickTargetComboBox.IsEnabled = parts[21] == "1";
            SidebarEnableClickMoveCheckBox.IsEnabled = parts[22] == "1";
            SidebarReturnCameraCenterButton.IsEnabled = parts[23] == "1";
            SidebarStopClickMoveButton.IsEnabled = parts[24] == "1";
            SidebarCenterVmText.Foreground = ParseBrush(Decode(parts[25]), Brushes.LimeGreen);
            SidebarNozzleStatusText.Foreground = ParseBrush(Decode(parts[26]), Brushes.LightSteelBlue);
            SidebarClickMoveStatusText.Foreground = ParseBrush(Decode(parts[27]), Brushes.LightSteelBlue);
            SidebarStartCalibrationButton.ToolTip = Decode(parts[28]);
        }
        finally
        {
            _applyingCalibrationSidebarState = false;
        }

        return "标定侧栏状态已更新。";
    }

    /// <summary>
    /// 将找芯片流程的图像源绑定到渲染控件，以便把本次执行图显示到主页预留区域。
    /// 绑定本身不会启动连续运行，也不会触发相机提前采图。
    /// </summary>
    private bool TryBindInspectionImageStep(string procedureName, VmProcedure procedure)
    {
        var options = GetImageStepOptions(procedureName, procedure);
        ImageStepComboBox.ItemsSource = options;
        var imageOption = options.FirstOrDefault(option =>
            string.Equals(option.DisplayName, "图像源1", StringComparison.Ordinal));
        if (imageOption is null)
        {
            ClearRenderer();
            return false;
        }

        BindImageStep(imageOption, persistSelection: false);
        return true;
    }

    /// <summary>
    /// 将当前结果对应的原始相机图保存到共享临时目录，并读取 BMP 像素尺寸。
    /// 文件由主程序成功加载到内存后删除。
    /// </summary>
    private InspectionImageFile SaveInspectionImage()
    {
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

    private static RectangleBlobCandidate ReadBlobResultRow(BlobFindResult result, int index)
    {
        var points = result.CentroidPoint;
        var resultCount = Math.Min(result.BlobNum, points?.Count ?? 0);
        if (points is null || index < 0 || index >= resultCount)
        {
            throw new InvalidOperationException($"Blob分析1未返回第 {index + 1} 行结果。");
        }

        var point = points[index];
        if (float.IsNaN(point.X) || float.IsInfinity(point.X) || point.X < 0 ||
            float.IsNaN(point.Y) || float.IsInfinity(point.Y) || point.Y < 0)
        {
            throw new InvalidOperationException($"Blob分析1第 {index + 1} 行的质心 X/Y 无效。");
        }

        var rectangularity = result.Rectangularity is not null && index < result.Rectangularity.Count
            ? result.Rectangularity[index]
            : 0f;
        var area = result.Area is not null && index < result.Area.Count
            ? result.Area[index]
            : 0f;
        var blobRect = result.BlobRect is not null && index < result.BlobRect.Count
            ? result.BlobRect[index]
            : null;
        return new RectangleBlobCandidate(
            point.X,
            point.Y,
            float.IsNaN(rectangularity) || float.IsInfinity(rectangularity) ? 0f : rectangularity,
            float.IsNaN(area) || float.IsInfinity(area) ? 0f : area,
            blobRect?.RectPoint.X ?? (int)Math.Round(point.X),
            blobRect?.RectPoint.Y ?? (int)Math.Round(point.Y),
            Math.Max(1, blobRect?.RectWidth ?? 1),
            Math.Max(1, blobRect?.RectHeight ?? 1));
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

    private string SetClickMoveMode(IReadOnlyList<string> parts)
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
            DetachCrosshairModule();
            _clickCenterPixelReady = false;
            _clickCalibrationPath = calibrationPath;
            VisionRenderControl.SetRenderToolbarVisible(false);
            CenterCrosshair.Visibility = Visibility.Visible;
            UpdateCommandState();
            SetStatus("点击视觉移动已关闭。", StatusKind.Ready);
            return "点击视觉移动已关闭。";
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
        QueueClickCenterInitialization();
        UpdateCommandState();
        SetStatus($"点击移动已启用：{Path.GetFileName(fullPath)}", StatusKind.Success);
        return $"已引用标定文件：{fullPath}。点击图像后将把该点移到绿色十字中心。";
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

        const string procedureName = CalibrationProcedureName;

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
        if (parts.Count != 7)
        {
            throw new InvalidDataException("准备九点标定的参数数量不正确。");
        }

        if (!_solutionLoaded || _calibrationProcedure is null)
        {
            throw new InvalidOperationException("固定视觉方案尚未加载完成，无法开始九点标定。");
        }

        const string procedureName = CalibrationProcedureName;

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
        string calibrationPath;
        try
        {
            calibrationPath = Path.GetFullPath(
                Encoding.UTF8.GetString(Convert.FromBase64String(parts[6])));
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
            parameters.CalibPathName = calibrationPath;
            parameters.RefreshFileEnable = true;
            parameters.DoClearPoint();

            _calibrationSession = new VisionCalibrationSession(nPointModule, calibrationPath);
            ClearCalibrationRenderer();
            BindCalibrationModule(nPointModule);
            SetBusy(true);
            SetStatus(
                $"九点标定已准备：基准({centerX:0.####}, {centerY:0.####})，" +
                $"偏移({offsetX:0.####}, {offsetY:0.####})，{(xFirst ? "X" : "Y")}优先",
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

    private string CaptureNinePointCalibration(IReadOnlyList<string> parts)
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
        CalibrationRenderControl.UpdateVMResultShow();

        var result = session.Module.ModuResult;
        if (result.ModuStatus != 1)
        {
            throw new InvalidOperationException(
                $"第 {pointNumber} 点 N点标定模块返回 NG，请检查相机取像和圆查找结果。");
        }

        session.NextPointNumber++;
        SetStatus(
            $"第 {pointNumber}/9 点已采集，流程用时 {stopwatch.Elapsed.TotalMilliseconds:0.0} ms",
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

        session.Module.ModuParams.DoSaveFile(session.CalibrationPath);
        StopAllContinuousExecutionNoThrow();
        ClearCalibrationRenderer();
        _calibrationSession = null;
        ApplyLiveRenderLayout();
        var previewRestored = TryStartLivePreview(out var previewError);
        RefreshRenderLayout();
        var message =
            $"九点标定成功，像素精度 {result.PixelPrecision:0.######}，" +
            $"标定文件：{session.CalibrationPath}" +
            (previewRestored ? "；实时画面已恢复" : $"；实时画面恢复失败：{previewError}");
        SetBusy(false);
        SetStatus(message, StatusKind.Success);
        return Task.FromResult(message);
    }

    private Task<string> AbortNinePointCalibrationAsync()
    {
        if (_calibrationSession is { } session)
        {
            session.Module.ModuParams.DoClearPoint();
            _calibrationSession = null;
        }

        ClearCalibrationRenderer();
        StopAllContinuousExecutionNoThrow();
        ApplyLiveRenderLayout();
        _ = TryStartLivePreview(out _);
        RefreshRenderLayout();
        var message = "九点标定已取消，本次未完成的标定点已清空。";
        SetBusy(false);
        SetStatus(message, StatusKind.Ready);
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
            if (!string.Equals(moduleName, "IMVSNPointCalibModu", StringComparison.Ordinal) &&
                !displayName.Contains("N点标定"))
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

        throw new InvalidOperationException("当前流程中未找到“N点标定”模块。");
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
            if (!string.Equals(moduleName, "IMVSCalibTransformModu", StringComparison.Ordinal) &&
                !displayName.Contains("标定转换"))
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

        throw new InvalidOperationException("当前标定流程中未找到“标定转换”模块。");
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
                return ResolveCalibrationTransformModule(CalibrationProcedureName);
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
        await HandleVisionImageClickAsync(pixelX, pixelY, null, null);
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

        var temporaryImagePath = Path.Combine(
            Path.GetTempPath(),
            $"ControlHub-click-center-{Guid.NewGuid():N}.bmp");
        try
        {
            VisionRenderControl.SaveOriginalImage(temporaryImagePath);
            using var stream = new FileStream(
                temporaryImagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 26 || reader.ReadUInt16() != 0x4D42)
            {
                throw new InvalidDataException("VisionMaster 保存的原图不是有效 BMP 图像。");
            }

            stream.Position = 18;
            var pixelWidth = Math.Abs((long)reader.ReadInt32());
            var pixelHeight = Math.Abs((long)reader.ReadInt32());
            if (pixelWidth <= 0 || pixelHeight <= 0)
            {
                throw new InvalidDataException("当前图像尺寸无效。");
            }

            _clickCenterPixelX = (float)((pixelWidth - 1) / 2d);
            _clickCenterPixelY = (float)((pixelHeight - 1) / 2d);
            _clickImagePixelWidth = checked((int)pixelWidth);
            _clickImagePixelHeight = checked((int)pixelHeight);
            _clickCenterPixelReady = true;
            return (_clickCenterPixelX, _clickCenterPixelY);
        }
        catch (Exception exception) when (exception is not InvalidOperationException)
        {
            throw new InvalidOperationException("无法读取当前图像中心，请确认实时画面已经正常出图。", exception);
        }
        finally
        {
            try
            {
                File.Delete(temporaryImagePath);
            }
            catch
            {
            }
        }
    }

    private void DrawImageCenterCrosshair()
    {
        if (!_clickMoveEnabled || !_clickCenterPixelReady || _closed)
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
        if (!_clickMoveEnabled || !_clickCenterPixelReady || _closed)
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
        try
        {
            _crosshairModule.EnableResultCallback();
            _crosshairModule.ModuleResultCallBackArrived += CrosshairModule_ModuleResultCallBackArrived;
        }
        catch
        {
            _crosshairModule = null;
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
            _crosshairModule.ModuleResultCallBackArrived -= CrosshairModule_ModuleResultCallBackArrived;
        }
        catch
        {
        }

        _crosshairModule = null;
    }

    private void CrosshairModule_ModuleResultCallBackArrived(object? sender, EventArgs e)
    {
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
        if (!_clickMoveEnabled || _closed)
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
                if (!_clickMoveEnabled || _clickCenterPixelReady || _closed)
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
            if (_showingCalibrationRender)
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
        _fullscreenRenderTarget = null;
        WorkspaceSidebarColumn.Width = new GridLength(320);
        WorkspaceGapColumn.Width = new GridLength(10);
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

    private void ApplyLiveRenderLayout()
    {
        ApplySingleRenderLayout(showLive: true);
    }

    private void ApplyCalibrationRenderLayout()
    {
        ApplySingleRenderLayout(showLive: false);
    }

    private void ApplySingleRenderLayout(bool showLive)
    {
        _showingCalibrationRender = !showLive;
        _fullscreenRenderTarget = null;
        WorkspaceSidebarColumn.Width = new GridLength(320);
        WorkspaceGapColumn.Width = new GridLength(10);
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
            await SendHostEventAsync($"CALIBRATION_TOOLBAR_ACTION\t{action}");
        }
        catch (Exception exception)
        {
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
        var solutionPath = GetFixedSolutionPath();
        if (!File.Exists(solutionPath))
        {
            SetStatus(
                $"未找到固定视觉方案。请将“{FixedSolutionFileName}”或“{FallbackSolutionFileName}”放到当前用户桌面。",
                StatusKind.Error);
            return;
        }

        _initializingFixedSolution = true;
        SetBusy(true);
        SetStatus("正在加载固定视觉方案…", StatusKind.Busy);

        var previousSolutionClosed = false;
        try
        {
            CloseCurrentSolution();
            previousSolutionClosed = true;
            _loadedSolutionPath = Path.GetFullPath(solutionPath);
            VmSolution.Load(_loadedSolutionPath, "");
            _solutionLoaded = true;

            var procedureNames = GetProcedureNames();
            _calibrationProcedure = GetRequiredProcedure(CalibrationProcedureName);
            _previewProcedure = _calibrationProcedure;
            _inspectionProcedure = GetRequiredProcedure(InspectionProcedureName);
            _calibrationViewActive = false;

            // 方案可能保存了“连续运行”状态。主页阶段只保留找芯片流程，标定页打开后再取标定对象。
            StopAllContinuousExecutionNoThrow();

            // 保留隐藏控件仅供现有渲染/标定逻辑读取；用户不能再切换方案或流程。
            PreviewProcedureComboBox.ItemsSource = procedureNames;
            CalibrationProcedureComboBox.ItemsSource = procedureNames;
            PreviewProcedureComboBox.SelectedItem = CalibrationProcedureName;
            CalibrationProcedureComboBox.SelectedItem = CalibrationProcedureName;

            _settings.SolutionPath = _loadedSolutionPath;
            _settings.PreviewProcedureName = CalibrationProcedureName;
            _settings.CalibrationProcedureName = CalibrationProcedureName;
            SolutionPathTextBox.Text = _loadedSolutionPath;
            PopulateImageSteps(CalibrationProcedureName, _previewProcedure);
            ApplyLiveRenderLayout();
            if (!TryStartLivePreview(out var previewError))
            {
                throw new InvalidOperationException($"实时画面启动失败：{previewError}");
            }
            SaveSettingsNoThrow();

            UpdateCommandState();
            SetStatus(
                $"已加载固定方案：{Path.GetFileName(solutionPath)}；找芯片流程待命，标定流程尚未开启",
                StatusKind.Success);
        }
        catch (Exception exception)
        {
            if (previousSolutionClosed)
            {
                CloseCurrentSolutionNoThrow();
            }

            SetStatus($"固定视觉方案加载失败：{FormatException(exception)}", StatusKind.Error);
        }
        finally
        {
            _initializingFixedSolution = false;
            SetBusy(false);
        }
    }

    private static string GetFixedSolutionPath()
    {
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
        ImageStepComboBox.SelectedItem =
            options.FirstOrDefault(option =>
                string.Equals(option.DisplayName, "图像源1", StringComparison.Ordinal))
            ?? options.FirstOrDefault(option => option.IsImageSource)
            ?? options.FirstOrDefault(option =>
                option.DisplayName.Contains("图像采集"))
            ?? options.FirstOrDefault(option =>
                string.Equals(option.ModuleKey, _settings.ImageModuleKey, StringComparison.Ordinal))
            ?? options.FirstOrDefault(option => option.DisplayName.Contains("N点标定"))
            ?? options.FirstOrDefault(option => option.DisplayName.Contains("标定"))
            ?? options.LastOrDefault();
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
        StopPreviewProcedureNoThrow();
        ClearRenderer();

        if (_solutionLoaded)
        {
            VmSolution.Instance?.CloseSolution();
        }

        _previewProcedure = null;
        _inspectionProcedure = null;
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
        StopPreviewProcedureNoThrow();
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
        CalibrationRenderControl.ModuleSource = module;
        try
        {
            CalibrationRenderControl.UpdateVMResultShow();
        }
        catch
        {
            // The module has no render result until the first calibration capture.
        }

        CalibrationImagePlaceholder.Visibility = Visibility.Collapsed;
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
            var options = GetImageStepOptions(CalibrationProcedureName, _previewProcedure);
            ImageStepComboBox.ItemsSource = options;
            var imageOption = options.FirstOrDefault(option =>
                string.Equals(option.DisplayName, "图像源1", StringComparison.Ordinal))
                ?? options.FirstOrDefault(option => option.IsImageSource)
                ?? options.FirstOrDefault();
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
        StopPreviewProcedureNoThrow();
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

    private bool TryStartLivePreview(out string errorMessage)
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
            if (ImageStepComboBox.SelectedItem is VisionModuleOption option)
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
        DetachCrosshairModule();
        _clickCenterPixelReady = false;
        _clickImagePixelWidth = 0;
        _clickImagePixelHeight = 0;
        _displayedModule = option.Module as VmModule;
        VisionRenderControl.ModuleSource = option.Module;
        try
        {
            VisionRenderControl.UpdateVMResultShow();
        }
        catch
        {
            // A newly loaded image source may not have a render result until its first frame.
        }

        ImagePlaceholder.Visibility = Visibility.Collapsed;
        CenterCrosshair.Visibility = _clickMoveEnabled
            ? Visibility.Collapsed
            : Visibility.Visible;
        if (_clickMoveEnabled)
        {
            AttachCrosshairModule();
            QueueClickCenterInitialization();
        }
        if (!persistSelection)
        {
            return;
        }

        _settings.ImageModuleKey = option.ModuleKey;
        SaveSettingsNoThrow();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        UpdateCommandState();
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

        foreach (var procedure in new[] { _previewProcedure, _inspectionProcedure, _calibrationProcedure })
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

        StopPreviewProcedureNoThrow();
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
            string calibrationPath)
        {
            Module = module;
            CalibrationPath = calibrationPath;
        }

        public IMVSNPointCalibModuTool Module { get; }

        public string CalibrationPath { get; }

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
            int height)
        {
            PixelX = pixelX;
            PixelY = pixelY;
            Rectangularity = rectangularity;
            Area = area;
            Left = left;
            Top = top;
            Width = Math.Max(1, width);
            Height = Math.Max(1, height);
        }

        public float PixelX { get; }

        public float PixelY { get; }

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

    private enum StatusKind
    {
        Ready,
        Busy,
        Success,
        Error
    }
}
