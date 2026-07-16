using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ControlHub.Services.Persistence;
using ControlHub.Services.Vision;
using ControlHub.Views.Controls;
using Microsoft.Win32;
using ShapeLine = System.Windows.Shapes.Line;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace ControlHub.Views.Pages;

public partial class HomePage : UserControl
{
    private const string ChipInspectionProcedureName = "找芯片流程";
    private const string ChipInspectionBlobModuleName = "Blob分析1";
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private readonly HomePageSettingsStore _homeSettingsStore = new();
    private HomePageSettings _homeSettings = new();
    private MotionControlPage? _motionController;
    private VisualCalibrationPage? _visualCalibrationController;
    private bool _presetPositionMoveRunning;
    private bool _startSequenceRunning;
    private bool _assignedNozzleMoveRunning;
    private bool _ddMoveRunning;
    private bool _axis13To15MoveRunning;
    private CancellationTokenSource? _productionCancellation;
    private TaskCompletionSource<bool>? _productionCompletion;
    private bool _productionStopRequested;
    private VisionMotionTarget? _blob1Nozzle1Target;
    private VisionMotionTarget? _blob2Nozzle2Target;
    private int _nextAssignedNozzleMoveStep;
    private bool _loadingPresetPositions = true;

    public HomePage()
    {
        InitializeComponent();
        LoadPresetPositions();
        _visionCalibration.Changed += VisionCalibration_Changed;
        RefreshVisionCalibrationStatus();
    }

    public void AttachMotionController(MotionControlPage motionController)
    {
        _motionController = motionController ?? throw new ArgumentNullException(nameof(motionController));
        UpdateHomeCommandState();
    }

    public void AttachVisionCalibrationController(VisualCalibrationPage visualCalibrationController)
    {
        _visualCalibrationController = visualCalibrationController
            ?? throw new ArgumentNullException(nameof(visualCalibrationController));
        UpdateHomeCommandState();
    }

    public async Task DeactivateProductionAsync()
    {
        var completion = _productionCompletion;
        if (!_startSequenceRunning || completion is null)
        {
            return;
        }

        _ = RequestProductionStop();
        await completion.Task;
    }

    public void RequestProductionStopNoWait()
    {
        _ = RequestProductionStop();
    }

    public async void RefreshVisionInspectionDisplay(VisualCalibrationPage visualCalibrationController)
    {
        if (BlobInspectionVisionDisplayHost.Visibility != Visibility.Visible ||
            BlobInspectionVisionDisplayHost.HostWindow == IntPtr.Zero)
        {
            return;
        }

        try
        {
            await visualCalibrationController.ActivateInspectionViewAsync(
                BlobInspectionVisionDisplayHost.HostWindow);
        }
        catch (Exception exception)
        {
            BlobInspectionImageStatusText.Text = $"VM显示恢复失败：{exception.Message}";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(242, 122, 128));
        }
    }

    /// <summary>
    /// 主页生产逻辑读取的视觉标定快照，包括标定文件和两个吸嘴的偏移。
    /// </summary>
    public VisionCalibrationSnapshot VisionCalibration => _visionCalibration.GetSnapshot();

    /// <summary>
    /// 将视觉计算出的相机轴坐标转换为相机/吸嘴1/吸嘴2的实际轴目标。
    /// </summary>
    public VisionMotionTarget CalculateVisionTarget(
        double cameraTargetX,
        double cameraTargetY,
        VisionTargetTool targetTool)
    {
        return _visionCalibration.CalculateTarget(cameraTargetX, cameraTargetY, targetTool);
    }

    /// <summary>
    /// 从视觉标定页的共享配置中加载第一套 XY 标定文件。
    /// </summary>
    private VisionCalibrationSnapshot GetFirstSetCalibrationFileSnapshot()
    {
        var snapshot = _visionCalibration.GetSnapshot();
        if (string.IsNullOrWhiteSpace(snapshot.CalibrationFilePath))
        {
            throw new InvalidOperationException("第一套 XY 尚未设置标定文件。");
        }

        if (!snapshot.CalibrationFileExists)
        {
            throw new FileNotFoundException(
                "第一套 XY 标定文件不存在，请先在视觉标定页完成九点标定。",
                snapshot.CalibrationFilePath);
        }

        return snapshot;
    }

    /// <summary>
    /// 加载需要同时使用标定文件和双吸嘴偏移的完整配置。
    /// </summary>
    private VisionCalibrationSnapshot GetFirstSetCalibrationSnapshot()
    {
        var snapshot = GetFirstSetCalibrationFileSnapshot();
        if (!snapshot.CalibrationProfileExists)
        {
            throw new FileNotFoundException(
                "第一套 XY 配置尚未保存，请先在视觉标定页完成双吸嘴验证并保存配置。",
                snapshot.CalibrationProfilePath);
        }

        return snapshot;
    }

    /// <summary>
    /// 每轮依次执行：回标定中心、Blob识别、双吸嘴对位、XY位置1、XY位置2，
    /// 最后让DD马达按同一相对脉冲连续转动两次，然后回中心进入下一轮。
    /// </summary>
    private async void StartProduction_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_startSequenceRunning)
        {
            _ = RequestProductionStop();
            return;
        }

        if (_presetPositionMoveRunning ||
            _assignedNozzleMoveRunning ||
            _ddMoveRunning ||
            _axis13To15MoveRunning)
        {
            return;
        }

        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var visualCalibrationController = _visualCalibrationController
                ?? throw new InvalidOperationException("主页尚未连接视觉标定组件。");
            var velocity = _visionCalibration.Settings.VelocityPulsesPerSecond;
            if (!double.IsFinite(velocity) || velocity <= 0)
            {
                throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
            }

            // 在任何轴开始运动前读取并验证完整自动流程参数，避免流程中途才发现输入缺失。
            var position1X = ParseFiniteCoordinate(PresetPosition1XTextBox.Text, "位置 1 X 轴绝对脉冲");
            var position1Y = ParseFiniteCoordinate(PresetPosition1YTextBox.Text, "位置 1 Y 轴绝对脉冲");
            var position2X = ParseFiniteCoordinate(PresetPosition2XTextBox.Text, "位置 2 X 轴绝对脉冲");
            var position2Y = ParseFiniteCoordinate(PresetPosition2YTextBox.Text, "位置 2 Y 轴绝对脉冲");
            var axis0PulseDistance = ParseFiniteCoordinate(Axis0PulseTextBox.Text, "DD马达脉冲");
            if (axis0PulseDistance == 0)
            {
                throw new InvalidOperationException("DD马达脉冲不能为 0。");
            }

            // 标定文件只在开始动作被明确触发后检查；路径失效时让用户重新选择一次。
            // 主页需要找芯片时按需加载桌面的“新纳方案.sol”，标定页离开后方案会关闭。
            var calibrationFile = GetOrSelectFirstSetCalibrationFile();
            var center = ReadFirstSetCalibrationCenter(calibrationFile.FilePath);
            if (calibrationFile.WasSelected)
            {
                _visionCalibration.Settings.CalibrationFilePath = calibrationFile.FilePath;
                _visionCalibration.Save();
            }

            _productionCancellation = new CancellationTokenSource();
            _productionCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _productionStopRequested = false;
            _startSequenceRunning = true;
            UpdateHomeCommandState();

            var cycleNumber = 0;
            while (true)
            {
                _productionCancellation.Token.ThrowIfCancellationRequested();
                cycleNumber++;
                ClearBlobInspectionResult();
                ClearAssignedNozzleTargets();

                // 当前反馈位置只用于估算本次移动所需的超时时间；读取本身不会使能或移动轴。
                var current = motionController.CaptureCalibrationFeedback(
                    VisionCalibrationService.FirstSetXHardwareAxisNo,
                    VisionCalibrationService.FirstSetYHardwareAxisNo);
                var timeoutMilliseconds = CalculateStartMoveTimeout(
                    current.ActualX,
                    current.ActualY,
                    center.X,
                    center.Y,
                    velocity);

                SetStartProductionStatus(
                    $"第{cycleNumber}轮 1/8：XY正在回初始中心({center.X:0.###}, {center.Y:0.###})…",
                    Color.FromRgb(242, 181, 68));
                var actual = await motionController.MoveCalibrationAxesToAsync(
                    VisionCalibrationService.FirstSetXHardwareAxisNo,
                    VisionCalibrationService.FirstSetYHardwareAxisNo,
                    center.X,
                    center.Y,
                    velocity,
                    positionTolerance: 10d,
                    moveTimeoutMilliseconds: timeoutMilliseconds,
                    cancellationToken: _productionCancellation.Token);

                // 必须等轴1、轴2均确认到位后，才允许单次执行固定方案中的找芯片流程。
                // 流程名和模块名都采用固定名称，避免误跑标定流程或实时流程。
                SetStartProductionStatus(
                    $"第{cycleNumber}轮 2/8：XY已到初始位置({actual.ActualX:0.###}, {actual.ActualY:0.###})，" +
                    $"正在运行{ChipInspectionProcedureName} → {ChipInspectionBlobModuleName}…",
                    Color.FromRgb(242, 181, 68));
                await PrepareBlobInspectionVisionDisplayAsync(visualCalibrationController);

                var blobResult = await visualCalibrationController.RunRectangleBlobInspectionAsync(
                    _productionCancellation.Token);
                SetBlobInspectionResult(blobResult);

                DualNozzleMechanicalTargets assignedTargets;
                try
                {
                    // 两个目标必须在本次拍照位置立即换算并缓存。后续吸嘴1移动后，
                    // 不能再用已经变化的当前轴位置去计算吸嘴2，否则第二个绝对目标会产生偏差。
                    assignedTargets = CalculateAssignedNozzleTargets(
                        blobResult,
                        calibrationFile.FilePath,
                        actual.ActualX,
                        actual.ActualY);
                }
                catch (Exception exception)
                {
                    ClearAssignedNozzleTargets();
                    SetFirstSetPositionStatus($"吸嘴分配失败：{exception.Message}", false);
                    SetStartProductionStatus(
                        $"Blob已显示；吸嘴目标换算失败：{exception.Message}",
                        Color.FromRgb(242, 181, 68));
                    return;
                }

                SetAssignedNozzleTargets(assignedTargets);
                SetStartProductionStatus(
                    $"第{cycleNumber}轮 3/8：Blob识别完成，吸嘴1正在对位物体1…",
                    Color.FromRgb(242, 181, 68));
                await MoveAssignedNozzleStepAsync(1, _productionCancellation.Token);

                SetStartProductionStatus(
                    $"第{cycleNumber}轮 4/8：吸嘴1已到位，吸嘴2正在对位物体2…",
                    Color.FromRgb(242, 181, 68));
                await MoveAssignedNozzleStepAsync(2, _productionCancellation.Token);

                SetStartProductionStatus(
                    $"第{cycleNumber}轮 5/8：XY正在移动到位置1({position1X:0.###}, {position1Y:0.###})…",
                    Color.FromRgb(242, 181, 68));
                await MovePresetPositionCoreAsync(
                    "位置 1",
                    position1X,
                    position1Y,
                    _productionCancellation.Token);

                SetStartProductionStatus(
                    $"第{cycleNumber}轮 6/8：XY正在移动到位置2({position2X:0.###}, {position2Y:0.###})…",
                    Color.FromRgb(242, 181, 68));
                await MovePresetPositionCoreAsync(
                    "位置 2",
                    position2X,
                    position2Y,
                    _productionCancellation.Token);

                SetStartProductionStatus(
                    $"第{cycleNumber}轮 7/8：DD马达第1次转动 {axis0PulseDistance:0.###} pulse…",
                    Color.FromRgb(242, 181, 68));
                await MoveAxis0RelativeCoreAsync(axis0PulseDistance, _productionCancellation.Token);

                SetStartProductionStatus(
                    $"第{cycleNumber}轮 8/8：DD马达第2次转动 {axis0PulseDistance:0.###} pulse…",
                    Color.FromRgb(242, 181, 68));
                await MoveAxis0RelativeCoreAsync(axis0PulseDistance, _productionCancellation.Token);

                SetStartProductionStatus(
                    $"第{cycleNumber}轮完成，正在回初始点开始下一轮…",
                    Color.FromRgb(73, 209, 125));
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            SetStartProductionStatus(
                "连续运行已停止。",
                Color.FromRgb(73, 209, 125));
        }
        catch (Exception exception)
        {
            SetStartProductionStatus(
                $"开始流程失败：{exception.Message}",
                Color.FromRgb(242, 122, 128));
        }
        finally
        {
            _startSequenceRunning = false;
            _productionStopRequested = false;
            _productionCancellation?.Dispose();
            _productionCancellation = null;
            var productionCompletion = _productionCompletion;
            _productionCompletion = null;
            MovePresetPosition1Button.Content = "移动";
            MovePresetPosition2Button.Content = "移动";
            Axis0MoveButton.Content = "转动";
            UpdateAssignedNozzleButtonText();
            UpdateHomeCommandState();
            productionCompletion?.TrySetResult(true);
        }
    }

    private bool RequestProductionStop()
    {
        if (!_startSequenceRunning || _productionStopRequested)
        {
            return false;
        }

        _productionStopRequested = true;
        _productionCancellation?.Cancel();
        SetStartProductionStatus("正在停止循环，请等待当前轴确认停止…", Color.FromRgb(242, 181, 68));
        UpdateHomeCommandState();
        return true;
    }

    /// <summary>
    /// 从第一套九点标定 XML 的 WorldPointLst 读取机械网格中心，并换算为控制卡脉冲。
    /// 直接读取标定文件可保证“回初始位置”不依赖当前固定方案内正在运行的流程。
    /// </summary>
    private static (double X, double Y) ReadFirstSetCalibrationCenter(string calibrationFilePath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(calibrationFilePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new InvalidDataException("第一套 XY 标定文件无法读取。", exception);
        }

        var worldPointList = document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibPointFListParam", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "ParamName", StringComparison.Ordinal))?.Value,
                    "WorldPointLst",
                    StringComparison.Ordinal));
        if (worldPointList is null)
        {
            throw new InvalidDataException("第一套 XY 标定文件中未找到 WorldPointLst。");
        }

        var calibrationType = ReadCalibrationParameter(document, "CalibType");
        var calibrationPointCountText = ReadCalibrationParameter(document, "TransNum");
        var calibrationErrorStatusText = ReadCalibrationParameter(document, "CalibErrStatus");
        if (!string.Equals(calibrationType, "NPointCalib", StringComparison.Ordinal) ||
            !int.TryParse(
                calibrationPointCountText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var calibrationPointCount) ||
            calibrationPointCount != 9 ||
            !int.TryParse(
                calibrationErrorStatusText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var calibrationErrorStatus) ||
            calibrationErrorStatus != 0)
        {
            throw new InvalidDataException("所选文件不是成功完成的九点标定文件，禁止移动到其中的坐标。");
        }

        var worldPoints = worldPointList
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "PointF", StringComparison.Ordinal))
            .Select(ReadWorldPoint)
            .ToArray();
        if (worldPoints.Length != 9)
        {
            throw new InvalidDataException("第一套 XY 标定文件的机械标定点数量不是9，禁止移动。");
        }

        // 本程序的九点采集顺序固定把第5点记录为 (0,0) 偏移，因此它就是当时记录的初始中心。
        // 使用实测中心点而不是重新求平均，避免非对称误差把绝对运动目标悄悄改掉。
        var recordedCenter = worldPoints[4];
        var centerX = recordedCenter.X *
            VisionCalibrationService.PulsesPerVisionUnit;
        var centerY = recordedCenter.Y *
            VisionCalibrationService.PulsesPerVisionUnit;
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            throw new InvalidOperationException("第一套 XY 标定中心坐标无效。");
        }

        return (centerX, centerY);
    }

    private static string? ReadCalibrationParameter(XDocument document, string parameterName)
    {
        return document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibParam", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "ParamName", StringComparison.Ordinal))?.Value,
                    parameterName,
                    StringComparison.Ordinal))?
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "ParamValue", StringComparison.Ordinal))?
            .Value;
    }

    private static (double X, double Y) ReadWorldPoint(XElement pointElement)
    {
        var xText = pointElement.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "X", StringComparison.Ordinal))?.Value;
        var yText = pointElement.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "Y", StringComparison.Ordinal))?.Value;
        if (!double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.IsFinite(x) ||
            !double.IsFinite(y))
        {
            throw new InvalidDataException("第一套 XY 标定文件包含无效的 WorldPointLst 坐标。");
        }

        return (x, y);
    }

    private (string FilePath, bool WasSelected) GetOrSelectFirstSetCalibrationFile()
    {
        var configuredPath = _visionCalibration.Settings.CalibrationFilePath?.Trim() ?? "";
        if (IsExistingFileWithExtension(configuredPath, ".xml"))
        {
            return (Path.GetFullPath(configuredPath), false);
        }

        return (
            SelectFile(
                "选择第一套 XY 标定文件",
                "VisionMaster 标定文件 (*.xml)|*.xml|所有文件 (*.*)|*.*",
                configuredPath,
                "已取消开始：未选择第一套 XY 标定文件。"),
            true);
    }

    private string SelectFile(
        string title,
        string filter,
        string configuredPath,
        string cancelledMessage)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = GetExistingDirectory(configuredPath)
        };
        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true)
        {
            throw new OperationCanceledException(cancelledMessage);
        }

        return Path.GetFullPath(dialog.FileName);
    }

    private static string GetExistingDirectory(string configuredPath)
    {
        try
        {
            var configuredDirectory = string.IsNullOrWhiteSpace(configuredPath)
                ? ""
                : Path.GetDirectoryName(Path.GetFullPath(configuredPath)) ?? "";
            if (Directory.Exists(configuredDirectory))
            {
                return configuredDirectory;
            }
        }
        catch
        {
            // 路径格式失效时回退到桌面，让用户重新选择。
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private static bool IsExistingFileWithExtension(string path, string extension)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把相机图像中的像素坐标转换成第一套 XY 两个吸嘴各自的机械目标坐标。
    /// 此方法只读取当前轴坐标，不下发任何运动命令。
    /// </summary>
    public async Task<DualNozzleMechanicalTargets> ConvertPixelToMechanicalTargetsAsync(
        double pixelX,
        double pixelY,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY) || pixelX < 0 || pixelY < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelX), "像素坐标必须是大于等于0的有效数字。");
        }

        var snapshot = EnsureFirstSetToolsReady();
        var visualCalibrationController = _visualCalibrationController
            ?? throw new InvalidOperationException("主页尚未连接视觉标定组件。");
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");

        var transformed = await visualCalibrationController.TransformPixelAsync(
            pixelX,
            pixelY,
            snapshot.CalibrationFilePath,
            cancellationToken);
        var current = motionController.CaptureCalibrationFeedback(
            VisionCalibrationService.FirstSetXHardwareAxisNo,
            VisionCalibrationService.FirstSetYHardwareAxisNo);
        var cameraTargetX = current.ActualX +
            (transformed.CenterTransformedX - transformed.TransformedX) *
            VisionCalibrationService.PulsesPerVisionUnit;
        var cameraTargetY = current.ActualY +
            (transformed.CenterTransformedY - transformed.TransformedY) *
            VisionCalibrationService.PulsesPerVisionUnit;

        return new DualNozzleMechanicalTargets(
            CalculateVisionTarget(cameraTargetX, cameraTargetY, VisionTargetTool.Nozzle1),
            CalculateVisionTarget(cameraTargetX, cameraTargetY, VisionTargetTool.Nozzle2));
    }

    /// <summary>
    /// 把 Blob结果1 固定分配给吸嘴1、Blob结果2 固定分配给吸嘴2。
    /// 计算基准使用拍照完成时的轴绝对位置，而不是后续可能已经移动过的当前位置。
    /// </summary>
    private DualNozzleMechanicalTargets CalculateAssignedNozzleTargets(
        VisionRectangleBlobResult blobResult,
        string calibrationFilePath,
        double captureX,
        double captureY)
    {
        _ = EnsureFirstSetToolsReady();
        if (blobResult.ImageWidth <= 0 || blobResult.ImageHeight <= 0)
        {
            throw new InvalidOperationException("本次Blob结果没有有效图像尺寸，无法计算吸嘴目标。");
        }

        ValidateBlobPixel(blobResult.Rectangle1, blobResult.ImageWidth, blobResult.ImageHeight, "Blob结果1");
        ValidateBlobPixel(blobResult.Rectangle2, blobResult.ImageWidth, blobResult.ImageHeight, "Blob结果2");

        var calibrationMatrix = ReadCalibrationMatrix(calibrationFilePath);
        var imageCenterX = (blobResult.ImageWidth - 1d) / 2d;
        var imageCenterY = (blobResult.ImageHeight - 1d) / 2d;
        var centerWorld = TransformCalibrationPoint(calibrationMatrix, imageCenterX, imageCenterY);

        VisionMotionTarget CalculateTarget(VisionBlobRectangle blob, VisionTargetTool tool)
        {
            var blobWorld = TransformCalibrationPoint(calibrationMatrix, blob.X, blob.Y);
            var cameraTargetX = captureX +
                (centerWorld.X - blobWorld.X) * VisionCalibrationService.PulsesPerVisionUnit;
            var cameraTargetY = captureY +
                (centerWorld.Y - blobWorld.Y) * VisionCalibrationService.PulsesPerVisionUnit;
            return CalculateVisionTarget(cameraTargetX, cameraTargetY, tool);
        }

        return new DualNozzleMechanicalTargets(
            CalculateTarget(blobResult.Rectangle1, VisionTargetTool.Nozzle1),
            CalculateTarget(blobResult.Rectangle2, VisionTargetTool.Nozzle2));
    }

    private static void ValidateBlobPixel(
        VisionBlobRectangle blob,
        int imageWidth,
        int imageHeight,
        string name)
    {
        if (!double.IsFinite(blob.X) ||
            !double.IsFinite(blob.Y) ||
            blob.X < 0 ||
            blob.Y < 0 ||
            blob.X >= imageWidth ||
            blob.Y >= imageHeight)
        {
            throw new InvalidOperationException($"{name}的像素质心超出当前图像范围。");
        }
    }

    private static double[] ReadCalibrationMatrix(string calibrationFilePath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(calibrationFilePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new InvalidDataException("第一套 XY 标定文件无法读取。", exception);
        }

        var matrixElement = document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibFloatListParam", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "ParamName", StringComparison.Ordinal))?.Value,
                    "CalibMatrix",
                    StringComparison.Ordinal));
        if (matrixElement is null)
        {
            throw new InvalidDataException("第一套 XY 标定文件中未找到 CalibMatrix。");
        }

        var values = matrixElement
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ParamValue", StringComparison.Ordinal))
            .Select(element =>
            {
                if (!double.TryParse(
                        element.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value) ||
                    !double.IsFinite(value))
                {
                    throw new InvalidDataException("第一套 XY 标定矩阵包含无效数值。");
                }

                return value;
            })
            .ToArray();
        if (values.Length != 9)
        {
            throw new InvalidDataException("第一套 XY 标定矩阵不是有效的3×3矩阵。");
        }

        return values;
    }

    private static (double X, double Y) TransformCalibrationPoint(
        IReadOnlyList<double> matrix,
        double pixelX,
        double pixelY)
    {
        var denominator = matrix[6] * pixelX + matrix[7] * pixelY + matrix[8];
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12)
        {
            throw new InvalidOperationException("第一套 XY 标定矩阵无法转换当前像素坐标。");
        }

        var worldX = (matrix[0] * pixelX + matrix[1] * pixelY + matrix[2]) / denominator;
        var worldY = (matrix[3] * pixelX + matrix[4] * pixelY + matrix[5]) / denominator;
        if (!double.IsFinite(worldX) || !double.IsFinite(worldY))
        {
            throw new InvalidOperationException("第一套 XY 标定矩阵返回了无效机械坐标。");
        }

        return (worldX, worldY);
    }

    private void SetAssignedNozzleTargets(DualNozzleMechanicalTargets targets)
    {
        _blob1Nozzle1Target = targets.Nozzle1;
        _blob2Nozzle2Target = targets.Nozzle2;
        _nextAssignedNozzleMoveStep = 1;
        SetFirstSetPositionStatus(
            "已分配：Blob结果1 → 吸嘴1，Blob结果2 → 吸嘴2；即将自动顺序对位。",
            true);
        UpdateAssignedNozzleButtonText();
        UpdateHomeCommandState();
    }

    private void ClearAssignedNozzleTargets()
    {
        _blob1Nozzle1Target = null;
        _blob2Nozzle2Target = null;
        _nextAssignedNozzleMoveStep = 0;
        UpdateAssignedNozzleButtonText();
        UpdateHomeCommandState();
    }

    /// <summary>
    /// 正常生产由“开始运行”自动连续执行两个步骤；此按钮仅用于异常后的当前步骤重试。
    /// </summary>
    private async void MoveAssignedNozzle_Click(object sender, RoutedEventArgs e)
    {
        if (_assignedNozzleMoveRunning ||
            _presetPositionMoveRunning ||
            _startSequenceRunning ||
            _ddMoveRunning ||
            _axis13To15MoveRunning)
        {
            return;
        }

        var step = _nextAssignedNozzleMoveStep;
        if (step is not (1 or 2))
        {
            SetFirstSetPositionStatus("请先点击“开始运行”完成Blob识别和吸嘴分配。", false);
            return;
        }

        try
        {
            _assignedNozzleMoveRunning = true;
            UpdateHomeCommandState();
            await MoveAssignedNozzleStepAsync(step, CancellationToken.None);
        }
        catch (Exception exception)
        {
            var nozzleName = step == 1 ? "吸嘴1" : "吸嘴2";
            SetFirstSetPositionStatus($"{nozzleName}对位失败：{exception.Message}", false);
        }
        finally
        {
            _assignedNozzleMoveRunning = false;
            UpdateAssignedNozzleButtonText();
            UpdateHomeCommandState();
        }
    }

    private async Task MoveAssignedNozzleStepAsync(int step, CancellationToken cancellationToken)
    {
        var target = step switch
        {
            1 => _blob1Nozzle1Target,
            2 => _blob2Nozzle2Target,
            _ => null
        };
        if (target is null)
        {
            throw new InvalidOperationException("当前吸嘴目标尚未由Blob结果生成。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        var velocity = _visionCalibration.Settings.VelocityPulsesPerSecond;
        if (!double.IsFinite(velocity) || velocity <= 0)
        {
            throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
        }

        var nozzleName = step == 1 ? "吸嘴1" : "吸嘴2";
        var objectName = step == 1 ? "物体1" : "物体2";
        MoveAssignedNozzleTitleText.Text = $"{nozzleName}移动中";
        MoveAssignedNozzleHintText.Text = $"正在自动对位{objectName}…";
        SetFirstSetPositionStatus(
            $"正在移动{nozzleName}到{objectName}：X={target.Value.X:0.###}，Y={target.Value.Y:0.###}",
            true);

        var current = motionController.CaptureCalibrationCenter(
            VisionCalibrationService.FirstSetXHardwareAxisNo,
            VisionCalibrationService.FirstSetYHardwareAxisNo);
        var timeoutMilliseconds = CalculateStartMoveTimeout(
            current.ActualX,
            current.ActualY,
            target.Value.X,
            target.Value.Y,
            velocity);
        var actual = await motionController.MoveCalibrationAxesToAsync(
            VisionCalibrationService.FirstSetXHardwareAxisNo,
            VisionCalibrationService.FirstSetYHardwareAxisNo,
            target.Value.X,
            target.Value.Y,
            velocity,
            positionTolerance: 10d,
            moveTimeoutMilliseconds: timeoutMilliseconds,
            cancellationToken: cancellationToken);

        if (step == 1)
        {
            _nextAssignedNozzleMoveStep = 2;
            SetFirstSetPositionStatus(
                $"吸嘴1已到物体1({actual.ActualX:0.###}, {actual.ActualY:0.###})；即将自动对位吸嘴2。",
                true);
        }
        else
        {
            _nextAssignedNozzleMoveStep = 3;
            SetFirstSetPositionStatus(
                $"吸嘴2已到物体2({actual.ActualX:0.###}, {actual.ActualY:0.###})；两次顺序对位完成。",
                true);
        }

        UpdateAssignedNozzleButtonText();
        UpdateHomeCommandState();
    }

    private async void Axis0Move_Click(object sender, RoutedEventArgs e)
    {
        if (_ddMoveRunning ||
            _presetPositionMoveRunning ||
            _startSequenceRunning ||
            _assignedNozzleMoveRunning ||
            _axis13To15MoveRunning)
        {
            return;
        }

        try
        {
            var pulseDistance = ParseFiniteCoordinate(Axis0PulseTextBox.Text, "DD马达脉冲");
            if (pulseDistance == 0)
            {
                throw new ArgumentException("DD马达脉冲不能为 0。");
            }

            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            _ddMoveRunning = true;
            UpdateHomeCommandState();
            Axis0MoveButton.Content = "转动中";
            await MoveAxis0RelativeCoreAsync(pulseDistance, CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetAxis0MoveStatus($"轴0移动失败：{exception.Message}", Color.FromRgb(242, 122, 128));
        }
        finally
        {
            _ddMoveRunning = false;
            Axis0MoveButton.Content = "转动";
            UpdateHomeCommandState();
        }
    }

    private async Task MoveAxis0RelativeCoreAsync(
        double pulseDistance,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(pulseDistance) || pulseDistance == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pulseDistance), "DD马达脉冲必须是非零有效数字。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        SetAxis0MoveStatus(
            $"轴0正在相对移动 {pulseDistance:0.###} pulse…",
            Color.FromRgb(242, 181, 68));

        var settled = await motionController.MoveAxisRelativeAsync(
            hardwareAxisNo: 0,
            pulseDistance: pulseDistance,
            cancellationToken: cancellationToken);
        SetAxis0MoveStatus(
            $"轴0完成：{pulseDistance:0.###} pulse，当前位置 {settled.FeedbackPosition:0.###}。",
            Color.FromRgb(73, 209, 125));
    }

    private void RecordAxis0Pulse_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var pulseDistance = ParseFiniteCoordinate(Axis0PulseTextBox.Text, "DD马达脉冲");
            if (pulseDistance == 0)
            {
                throw new ArgumentException("DD马达脉冲不能为 0。");
            }

            _homeSettings.Axis0RelativePulse = pulseDistance;
            _homeSettingsStore.Save(_homeSettings);
            SetAxis0MoveStatus(
                $"DD马达脉冲已记录：{pulseDistance:0.###} pulse，重启后仍保留。",
                Color.FromRgb(73, 209, 125));
        }
        catch (Exception exception)
        {
            SetAxis0MoveStatus($"DD马达脉冲记录失败：{exception.Message}", Color.FromRgb(242, 122, 128));
        }
        finally
        {
            UpdateHomeCommandState();
        }
    }

    private void Axis0PulseTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHomeCommandState();
    }

    private async void Axis13To15Move_Click(object sender, RoutedEventArgs e)
    {
        if (_axis13To15MoveRunning ||
            _presetPositionMoveRunning ||
            _startSequenceRunning ||
            _assignedNozzleMoveRunning ||
            _ddMoveRunning)
        {
            return;
        }

        try
        {
            var pulseDistance = ParseFiniteCoordinate(
                Axis13To15PulseTextBox.Text,
                "轴13 / 轴14 / 轴15同步脉冲");
            if (pulseDistance == 0)
            {
                throw new ArgumentException("轴13 / 轴14 / 轴15同步脉冲不能为 0。");
            }

            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            _axis13To15MoveRunning = true;
            UpdateHomeCommandState();
            Axis13To15MoveButton.Content = "同步转动中";
            SetAxis13To15MoveStatus(
                $"轴13 / 轴14 / 轴15正在同步相对移动 {pulseDistance:0.###} pulse…",
                Color.FromRgb(242, 181, 68));

            await motionController.MoveAxesRelativeAsync(
                new[] { 13, 14, 15 },
                pulseDistance,
                CancellationToken.None);
            SetAxis13To15MoveStatus(
                $"轴13 / 轴14 / 轴15已同步完成 {pulseDistance:0.###} pulse。",
                Color.FromRgb(73, 209, 125));
        }
        catch (Exception exception)
        {
            SetAxis13To15MoveStatus(
                $"轴13 / 轴14 / 轴15同步移动失败：{exception.Message}",
                Color.FromRgb(242, 122, 128));
        }
        finally
        {
            _axis13To15MoveRunning = false;
            Axis13To15MoveButton.Content = "同步转动";
            UpdateHomeCommandState();
        }
    }

    private void Axis13To15PulseTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHomeCommandState();
    }

    private void VisionCalibration_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshVisionCalibrationStatus);
            return;
        }

        RefreshVisionCalibrationStatus();
    }

    private void RefreshVisionCalibrationStatus()
    {
        var snapshot = _visionCalibration.GetSnapshot();
        string statusText;
        Color statusColor;

        if (!snapshot.CalibrationFileExists)
        {
            statusText = "标定文件缺失";
            statusColor = Color.FromRgb(242, 122, 128);
        }
        else if (!snapshot.CalibrationProfileExists)
        {
            statusText = "第一套XY配置未保存";
            statusColor = Color.FromRgb(242, 181, 68);
        }
        else if (snapshot.Nozzle1Calibrated && snapshot.Nozzle2Calibrated)
        {
            statusText = "双吸嘴标定就绪";
            statusColor = Color.FromRgb(57, 197, 107);
        }
        else if (snapshot.Nozzle1Calibrated || snapshot.Nozzle2Calibrated)
        {
            statusText = snapshot.Nozzle1Calibrated ? "吸嘴1标定就绪" : "吸嘴2标定就绪";
            statusColor = Color.FromRgb(242, 181, 68);
        }
        else
        {
            statusText = "相机标定就绪";
            statusColor = Color.FromRgb(61, 163, 255);
        }

        VisionCalibrationStatusText.Text = statusText;
        VisionCalibrationStatusIndicator.Fill = new SolidColorBrush(statusColor);
    }

    private async void MovePresetPosition1_Click(object sender, RoutedEventArgs e)
    {
        await MovePresetPositionAsync(
            "位置 1",
            PresetPosition1XTextBox,
            PresetPosition1YTextBox,
            MovePresetPosition1Button);
    }

    private void RecordPresetPosition1_Click(object sender, RoutedEventArgs e)
    {
        RecordPresetPosition(
            1,
            "位置 1",
            PresetPosition1XTextBox,
            PresetPosition1YTextBox);
    }

    private async void MovePresetPosition2_Click(object sender, RoutedEventArgs e)
    {
        await MovePresetPositionAsync(
            "位置 2",
            PresetPosition2XTextBox,
            PresetPosition2YTextBox,
            MovePresetPosition2Button);
    }

    private void RecordPresetPosition2_Click(object sender, RoutedEventArgs e)
    {
        RecordPresetPosition(
            2,
            "位置 2",
            PresetPosition2XTextBox,
            PresetPosition2YTextBox);
    }

    private void RecordPresetPosition(
        int positionNumber,
        string positionName,
        TextBox xInput,
        TextBox yInput)
    {
        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var current = motionController.CaptureCalibrationFeedback(
                VisionCalibrationService.FirstSetXHardwareAxisNo,
                VisionCalibrationService.FirstSetYHardwareAxisNo);

            _loadingPresetPositions = true;
            xInput.Text = current.ActualX.ToString("0.###", CultureInfo.CurrentCulture);
            yInput.Text = current.ActualY.ToString("0.###", CultureInfo.CurrentCulture);
            _loadingPresetPositions = false;

            if (positionNumber == 1)
            {
                _homeSettings.PresetPosition1X = current.ActualX;
                _homeSettings.PresetPosition1Y = current.ActualY;
            }
            else
            {
                _homeSettings.PresetPosition2X = current.ActualX;
                _homeSettings.PresetPosition2Y = current.ActualY;
            }

            _homeSettingsStore.Save(_homeSettings);
            SetFirstSetPositionStatus(
                $"{positionName}已记录并保存：X={current.ActualX:0.###}，Y={current.ActualY:0.###} pulse。",
                true);
        }
        catch (Exception exception)
        {
            _loadingPresetPositions = false;
            SetFirstSetPositionStatus($"{positionName}记录失败：{exception.Message}", false);
        }
        finally
        {
            UpdateHomeCommandState();
        }
    }

    private async Task MovePresetPositionAsync(
        string positionName,
        TextBox xInput,
        TextBox yInput,
        Button moveButton)
    {
        if (_presetPositionMoveRunning ||
            _startSequenceRunning ||
            _assignedNozzleMoveRunning ||
            _ddMoveRunning ||
            _axis13To15MoveRunning)
        {
            return;
        }

        try
        {
            var targetX = ParseFiniteCoordinate(xInput.Text, $"{positionName} X 轴绝对脉冲");
            var targetY = ParseFiniteCoordinate(yInput.Text, $"{positionName} Y 轴绝对脉冲");
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var velocity = _visionCalibration.Settings.VelocityPulsesPerSecond;
            if (!double.IsFinite(velocity) || velocity <= 0)
            {
                throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
            }

            _presetPositionMoveRunning = true;
            UpdateHomeCommandState();
            moveButton.Content = "移动中";
            await MovePresetPositionCoreAsync(
                positionName,
                targetX,
                targetY,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"{positionName}移动失败：{exception.Message}", false);
        }
        finally
        {
            _presetPositionMoveRunning = false;
            moveButton.Content = "移动";
            UpdateHomeCommandState();
        }
    }

    private async Task MovePresetPositionCoreAsync(
        string positionName,
        double targetX,
        double targetY,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
        {
            throw new ArgumentOutOfRangeException(nameof(targetX), $"{positionName}的XY目标必须是有效数字。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        var velocity = _visionCalibration.Settings.VelocityPulsesPerSecond;
        if (!double.IsFinite(velocity) || velocity <= 0)
        {
            throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
        }

        SetFirstSetPositionStatus(
            $"正在绝对移动{positionName}：X={targetX:0.###}，Y={targetY:0.###} pulse…",
            true);
        var current = motionController.CaptureCalibrationFeedback(
            VisionCalibrationService.FirstSetXHardwareAxisNo,
            VisionCalibrationService.FirstSetYHardwareAxisNo);
        var timeoutMilliseconds = CalculateStartMoveTimeout(
            current.ActualX,
            current.ActualY,
            targetX,
            targetY,
            velocity);
        var actual = await motionController.MoveCalibrationAxesToAsync(
            VisionCalibrationService.FirstSetXHardwareAxisNo,
            VisionCalibrationService.FirstSetYHardwareAxisNo,
            targetX,
            targetY,
            velocity,
            positionTolerance: 10d,
            moveTimeoutMilliseconds: timeoutMilliseconds,
            cancellationToken: cancellationToken);
        SetFirstSetPositionStatus(
            $"{positionName}已到位：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse。",
            true);
    }

    private void PresetPositionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SavePresetPositionsFromInputs();
        UpdateHomeCommandState();
    }

    private void LoadPresetPositions()
    {
        _homeSettings = _homeSettingsStore.Load();
        _loadingPresetPositions = true;
        PresetPosition1XTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition1X);
        PresetPosition1YTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition1Y);
        PresetPosition2XTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition2X);
        PresetPosition2YTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition2Y);
        Axis0PulseTextBox.Text = FormatPresetCoordinate(_homeSettings.Axis0RelativePulse);
        _loadingPresetPositions = false;
    }

    private void SavePresetPositionsFromInputs()
    {
        if (_loadingPresetPositions ||
            PresetPosition1XTextBox is null ||
            PresetPosition1YTextBox is null ||
            PresetPosition2XTextBox is null ||
            PresetPosition2YTextBox is null ||
            !TryParseOptionalCoordinate(PresetPosition1XTextBox.Text, out var position1X) ||
            !TryParseOptionalCoordinate(PresetPosition1YTextBox.Text, out var position1Y) ||
            !TryParseOptionalCoordinate(PresetPosition2XTextBox.Text, out var position2X) ||
            !TryParseOptionalCoordinate(PresetPosition2YTextBox.Text, out var position2Y))
        {
            return;
        }

        _homeSettings.PresetPosition1X = position1X;
        _homeSettings.PresetPosition1Y = position1Y;
        _homeSettings.PresetPosition2X = position2X;
        _homeSettings.PresetPosition2Y = position2Y;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"保存绝对位置失败：{exception.Message}", false);
        }
    }

    private void HomeEmergencyStop_Click(object sender, RoutedEventArgs e)
    {
        _ = RequestProductionStop();
        var motionController = _motionController;
        if (motionController is null)
        {
            HomeEmergencyStopHintText.Text = "运动控制未连接";
            return;
        }

        var issued = motionController.EmergencyStopAllAxes("主页操作员请求全轴急停");
        HomeEmergencyStopHintText.Text = issued
            ? "急停已下发，正在确认所有轴停止"
            : "急停下发失败，请立即按硬件急停";
        HomeEmergencyStopHintText.Foreground = new SolidColorBrush(
            issued ? Color.FromRgb(255, 220, 220) : Color.FromRgb(255, 188, 93));
    }

    private VisionCalibrationSnapshot EnsureFirstSetToolsReady()
    {
        var snapshot = GetFirstSetCalibrationSnapshot();
        if (!snapshot.Nozzle1Calibrated || !snapshot.Nozzle2Calibrated)
        {
            throw new InvalidOperationException("第一套 XY 的两个吸嘴偏移尚未全部标定。");
        }

        return snapshot;
    }

    private void UpdateHomeCommandState()
    {
        if (StartProductionButton is null ||
            StartProductionTitleText is null ||
            MoveAssignedNozzleButton is null ||
            Axis0PulseTextBox is null ||
            RecordAxis0PulseButton is null ||
            Axis0MoveButton is null ||
            Axis13To15PulseTextBox is null ||
            Axis13To15MoveButton is null ||
            PresetPosition1XTextBox is null ||
            PresetPosition1YTextBox is null ||
            PresetPosition2XTextBox is null ||
            PresetPosition2YTextBox is null ||
            RecordPresetPosition1Button is null ||
            RecordPresetPosition2Button is null ||
            MovePresetPosition1Button is null ||
            MovePresetPosition2Button is null ||
            HomeEmergencyStopButton is null)
        {
            return;
        }

        var visionControllersReady =
            _motionController is not null &&
            _visualCalibrationController is not null;
        var commandsIdle =
            !_presetPositionMoveRunning &&
            !_startSequenceRunning &&
            !_assignedNozzleMoveRunning &&
            !_ddMoveRunning &&
            !_axis13To15MoveRunning;
        StartProductionButton.IsEnabled =
            visionControllersReady &&
            (_startSequenceRunning ? !_productionStopRequested : commandsIdle);
        StartProductionTitleText.Text = _startSequenceRunning
            ? (_productionStopRequested ? "正在停止" : "停止循环")
            : "开始运行";
        StartProductionButton.Background = new SolidColorBrush(
            _startSequenceRunning ? Color.FromRgb(181, 22, 35) : Color.FromRgb(22, 139, 80));
        StartProductionButton.BorderBrush = new SolidColorBrush(
            _startSequenceRunning ? Color.FromRgb(255, 98, 110) : Color.FromRgb(56, 185, 121));
        MoveAssignedNozzleButton.IsEnabled =
            visionControllersReady &&
            commandsIdle &&
            ((_nextAssignedNozzleMoveStep == 1 && _blob1Nozzle1Target is not null) ||
             (_nextAssignedNozzleMoveStep == 2 && _blob2Nozzle2Target is not null));
        PresetPosition1XTextBox.IsEnabled = commandsIdle;
        PresetPosition1YTextBox.IsEnabled = commandsIdle;
        PresetPosition2XTextBox.IsEnabled = commandsIdle;
        PresetPosition2YTextBox.IsEnabled = commandsIdle;
        RecordPresetPosition1Button.IsEnabled = _motionController is not null && commandsIdle;
        RecordPresetPosition2Button.IsEnabled = _motionController is not null && commandsIdle;
        MovePresetPosition1Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            TryParseCoordinate(PresetPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(PresetPosition1YTextBox.Text, out _);
        MovePresetPosition2Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            TryParseCoordinate(PresetPosition2XTextBox.Text, out _) &&
            TryParseCoordinate(PresetPosition2YTextBox.Text, out _);
        HomeEmergencyStopButton.IsEnabled = _motionController is not null;
        Axis0PulseTextBox.IsEnabled = commandsIdle;
        RecordAxis0PulseButton.IsEnabled =
            commandsIdle &&
            TryParseCoordinate(Axis0PulseTextBox.Text, out var recordPulseDistance) &&
            recordPulseDistance != 0;
        Axis0MoveButton.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            TryParseCoordinate(Axis0PulseTextBox.Text, out var pulseDistance) &&
            pulseDistance != 0;
        Axis13To15PulseTextBox.IsEnabled = commandsIdle;
        Axis13To15MoveButton.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            TryParseCoordinate(Axis13To15PulseTextBox.Text, out var synchronizedPulseDistance) &&
            synchronizedPulseDistance != 0;
    }

    private void UpdateAssignedNozzleButtonText()
    {
        if (MoveAssignedNozzleTitleText is null || MoveAssignedNozzleHintText is null)
        {
            return;
        }

        switch (_nextAssignedNozzleMoveStep)
        {
            case 1:
                MoveAssignedNozzleTitleText.Text = "吸嘴1 → 物体1";
                MoveAssignedNozzleHintText.Text = "自动步骤1；失败时可点击重试";
                break;
            case 2:
                MoveAssignedNozzleTitleText.Text = "吸嘴2 → 物体2";
                MoveAssignedNozzleHintText.Text = "自动步骤2；失败时可点击重试";
                break;
            case 3:
                MoveAssignedNozzleTitleText.Text = "顺序对位完成";
                MoveAssignedNozzleHintText.Text = "重新开始识别后可再次执行";
                break;
            default:
                MoveAssignedNozzleTitleText.Text = "自动顺序对位";
                MoveAssignedNozzleHintText.Text = "开始运行后自动执行";
                break;
        }
    }

    private void SetStartProductionStatus(string message, Color color)
    {
        StartProductionHintText.Text = message;
        StartProductionHintText.ToolTip = message;
        StartProductionHintText.Foreground = new SolidColorBrush(color);
    }

    private void SetAxis0MoveStatus(string message, Color color)
    {
        Axis0MoveStatusText.Text = message;
        Axis0MoveStatusText.ToolTip = message;
        Axis0MoveStatusText.Foreground = new SolidColorBrush(color);
    }

    private void SetAxis13To15MoveStatus(string message, Color color)
    {
        Axis13To15MoveStatusText.Text = message;
        Axis13To15MoveStatusText.ToolTip = message;
        Axis13To15MoveStatusText.Foreground = new SolidColorBrush(color);
    }

    private async Task PrepareBlobInspectionVisionDisplayAsync(VisualCalibrationPage visualCalibrationController)
    {
        BlobInspectionImage.Source = null;
        BlobInspectionOverlayCanvas.Children.Clear();
        BlobInspectionImageViewbox.Visibility = Visibility.Collapsed;
        BlobInspectionImagePlaceholder.Visibility = Visibility.Collapsed;
        BlobInspectionVisionDisplayHost.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text = "正在运行 Blob分析1";
        BlobInspectionImageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(98, 181, 255));

        await Dispatcher.InvokeAsync(
            () => BlobInspectionVisionDisplayHost.UpdateLayout(),
            System.Windows.Threading.DispatcherPriority.Loaded);
        var displayHostWindow = BlobInspectionVisionDisplayHost.HostWindow;
        if (displayHostWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("主页 Blob 显示区域尚未创建。");
        }

        await visualCalibrationController.ActivateInspectionViewAsync(displayHostWindow);
    }

    private void BlobInspectionVisionDisplayHost_HostSizeChanged(object? sender, EventArgs e)
    {
        if (BlobInspectionVisionDisplayHost.Visibility == Visibility.Visible)
        {
            _visualCalibrationController?.RefreshVisionDisplay();
        }
    }

    private void ClearBlobInspectionResult()
    {
        BlobInspectionImage.Source = null;
        BlobInspectionOverlayCanvas.Children.Clear();
        BlobInspectionVisionDisplayHost.Visibility = Visibility.Hidden;
        BlobInspectionImageViewbox.Visibility = Visibility.Collapsed;
        BlobInspectionImagePlaceholder.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text = "等待拍照";
        BlobInspectionImageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(98, 181, 255));
    }

    private void SetBlobInspectionResult(VisionRectangleBlobResult result)
    {
        if (result.ImageWidth <= 0 ||
            result.ImageHeight <= 0 ||
            string.IsNullOrWhiteSpace(result.ImagePath))
        {
            BlobInspectionImageStatusText.Text = "XY已显示 · 本次未返回检测图";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(242, 181, 68));
            return;
        }

        if (BlobInspectionVisionDisplayHost.Visibility == Visibility.Visible)
        {
            try
            {
                File.Delete(result.ImagePath);
            }
            catch
            {
            }

            BlobInspectionImageStatusText.Text =
                $"VM组件 · Blob分析1 · {result.ImageWidth}×{result.ImageHeight}";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(73, 209, 125));
            return;
        }

        try
        {
            ShowBlobInspectionImage(result);
        }
        catch
        {
            // 图片显示不是生产结果的前置条件；Blob 的两组 X/Y 已成功取得并保留在主页。
            BlobInspectionImageStatusText.Text = "XY已显示 · 检测图加载失败";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(242, 181, 68));
        }
    }

    /// <summary>
    /// 加载 VisionMaster 本次保存的相机图，并在同一像素坐标系中叠加两个 Blob 框和质心。
    /// BitmapCacheOption.OnLoad 会把文件完整读入内存，因此加载后即可删除临时文件。
    /// </summary>
    private void ShowBlobInspectionImage(VisionRectangleBlobResult result)
    {
        if (string.IsNullOrWhiteSpace(result.ImagePath) || !File.Exists(result.ImagePath))
        {
            throw new FileNotFoundException("VisionMaster 本次Blob检测图不存在。", result.ImagePath);
        }

        BitmapImage bitmap;
        try
        {
            using var stream = new FileStream(
                result.ImagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
        }
        finally
        {
            try
            {
                File.Delete(result.ImagePath);
            }
            catch
            {
                // 图片已经完整载入内存；临时文件清理失败不影响本次结果显示。
            }
        }

        var imageWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : result.ImageWidth;
        var imageHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : result.ImageHeight;
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new InvalidDataException("VisionMaster 本次Blob检测图尺寸无效。");
        }

        BlobInspectionImageSurface.Width = imageWidth;
        BlobInspectionImageSurface.Height = imageHeight;
        BlobInspectionOverlayCanvas.Width = imageWidth;
        BlobInspectionOverlayCanvas.Height = imageHeight;
        BlobInspectionImage.Source = bitmap;
        BlobInspectionOverlayCanvas.Children.Clear();
        DrawBlobOverlay(result.Rectangle1, 1, Color.FromRgb(0, 230, 118), imageWidth, imageHeight);
        DrawBlobOverlay(result.Rectangle2, 2, Color.FromRgb(64, 196, 255), imageWidth, imageHeight);

        BlobInspectionImagePlaceholder.Visibility = Visibility.Collapsed;
        BlobInspectionImageViewbox.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text = $"已检测 · {imageWidth}×{imageHeight}";
        BlobInspectionImageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(73, 209, 125));
    }

    private void DrawBlobOverlay(
        VisionBlobRectangle blob,
        int number,
        Color color,
        double imageWidth,
        double imageHeight)
    {
        var left = Math.Clamp(blob.Left, 0d, Math.Max(0d, imageWidth - 1d));
        var top = Math.Clamp(blob.Top, 0d, Math.Max(0d, imageHeight - 1d));
        var width = Math.Clamp(blob.Width, 1d, Math.Max(1d, imageWidth - left));
        var height = Math.Clamp(blob.Height, 1d, Math.Max(1d, imageHeight - top));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var strokeThickness = Math.Max(2d, Math.Min(imageWidth, imageHeight) / 500d);

        var rectangle = new ShapeRectangle
        {
            Width = width,
            Height = height,
            Stroke = brush,
            StrokeThickness = strokeThickness
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        BlobInspectionOverlayCanvas.Children.Add(rectangle);

        var crosshairRadius = Math.Max(8d, Math.Min(imageWidth, imageHeight) / 80d);
        AddBlobCrosshairLine(
            blob.X - crosshairRadius,
            blob.Y,
            blob.X + crosshairRadius,
            blob.Y,
            brush,
            strokeThickness);
        AddBlobCrosshairLine(
            blob.X,
            blob.Y - crosshairRadius,
            blob.X,
            blob.Y + crosshairRadius,
            brush,
            strokeThickness);

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 5, 20, 30)),
            BorderBrush = brush,
            BorderThickness = new Thickness(strokeThickness),
            Padding = new Thickness(5d, 2d, 5d, 2d),
            Child = new TextBlock
            {
                Text = $"矩形{number}  X={blob.X:0.0}  Y={blob.Y:0.0}",
                Foreground = brush,
                FontSize = Math.Max(12d, Math.Min(imageWidth, imageHeight) / 55d),
                FontWeight = FontWeights.Bold
            }
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, Math.Max(0d, top - Math.Min(imageWidth, imageHeight) / 28d));
        BlobInspectionOverlayCanvas.Children.Add(label);
    }

    private void AddBlobCrosshairLine(
        double x1,
        double y1,
        double x2,
        double y2,
        Brush brush,
        double strokeThickness)
    {
        BlobInspectionOverlayCanvas.Children.Add(new ShapeLine
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = strokeThickness
        });
    }

    /// <summary>
    /// 按当前距离和配置速度估算超时，额外预留5秒用于加减速和状态刷新。
    /// </summary>
    private static int CalculateStartMoveTimeout(
        double currentX,
        double currentY,
        double targetX,
        double targetY,
        double velocity)
    {
        var longestDistance = Math.Max(
            Math.Abs(targetX - currentX),
            Math.Abs(targetY - currentY));
        var estimatedMilliseconds = longestDistance / velocity * 1000d + 5000d;
        return (int)Math.Clamp(Math.Ceiling(estimatedMilliseconds), 10_000d, 120_000d);
    }

    private void SetFirstSetPositionStatus(string message, bool success)
    {
        PresetPositionStatusText.Text = message;
        PresetPositionStatusText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(73, 209, 125)
            : Color.FromRgb(242, 181, 68));
    }

    private static double ParseFiniteCoordinate(string? value, string fieldName)
    {
        if (!TryParseCoordinate(value, out var parsed))
        {
            throw new ArgumentException($"{fieldName}必须是有效数字。");
        }

        return parsed;
    }

    private static bool TryParseCoordinate(string? value, out double parsed)
    {
        return (double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out parsed) ||
                double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed)) &&
               double.IsFinite(parsed);
    }

    private static string FormatPresetCoordinate(double? value)
    {
        return value is { } coordinate && double.IsFinite(coordinate)
            ? coordinate.ToString("0.###", CultureInfo.CurrentCulture)
            : string.Empty;
    }

    private static bool TryParseOptionalCoordinate(string? value, out double? parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = null;
            return true;
        }

        if (TryParseCoordinate(value, out var coordinate))
        {
            parsed = coordinate;
            return true;
        }

        parsed = null;
        return false;
    }
}
