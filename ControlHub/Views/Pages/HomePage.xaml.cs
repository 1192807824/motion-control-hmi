using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using ControlHub.Services.Vision;
using ControlHub.Views.Controls;
using Microsoft.Win32;
using ShapeLine = System.Windows.Shapes.Line;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace ControlHub.Views.Pages;

public partial class HomePage : UserControl
{
    private const string ChipInspectionProcedureName = "流程1";
    private const string ChipInspectionBlobModuleName = "Blob分析1";
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private MotionControlPage? _motionController;
    private VisualCalibrationPage? _visualCalibrationController;
    private bool _coordinateTransformRunning;
    private bool _startSequenceRunning;
    private bool _assignedNozzleMoveRunning;
    private VisionMotionTarget? _blob1Nozzle1Target;
    private VisionMotionTarget? _blob2Nozzle2Target;
    private int _nextAssignedNozzleMoveStep;

    public HomePage()
    {
        InitializeComponent();
        _visionCalibration.Changed += VisionCalibration_Changed;
        RefreshVisionCalibrationStatus();
        RefreshFirstSetCalibrationDetails();
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
    /// 主页开始按钮只执行两个顺序步骤：
    /// 1. 第一套 XY 回到九点标定中心；2. 运行“找芯片”方案中的“流程1”。
    /// 流程完成后读取“Blob分析1”结果表的前两行，并在主页显示两组像素质心 X、Y。
    /// 本阶段仍不执行取料、吸嘴或后续摆盘动作。
    /// </summary>
    private async void StartProduction_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_startSequenceRunning || _coordinateTransformRunning || _assignedNozzleMoveRunning)
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

            // 方案和标定文件只在开始动作被明确触发后检查；路径失效时让用户重新选择一次。
            // 选择成功会保存，后续点击“开始”不再重复弹窗。
            var calibrationFile = GetOrSelectFirstSetCalibrationFile();
            var inspectionSolution = GetOrSelectChipInspectionSolution();

            _startSequenceRunning = true;
            UpdateHomeCommandState();
            ClearBlobInspectionResult();
            ClearAssignedNozzleTargets();
            SetStartProductionStatus("步骤1/2：正在读取第一套 XY 标定中心…", Color.FromRgb(242, 181, 68));

            var center = ReadFirstSetCalibrationCenter(calibrationFile.FilePath);
            if (calibrationFile.WasSelected)
            {
                _visionCalibration.Settings.CalibrationFilePath = calibrationFile.FilePath;
                _visionCalibration.Save();
            }

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
                $"步骤1/2：第一套 XY 正在回初始中心 X={center.X:0.###}，Y={center.Y:0.###}…",
                Color.FromRgb(242, 181, 68));
            var actual = await motionController.MoveCalibrationAxesToAsync(
                VisionCalibrationService.FirstSetXHardwareAxisNo,
                VisionCalibrationService.FirstSetYHardwareAxisNo,
                center.X,
                center.Y,
                velocity,
                positionTolerance: 10d,
                moveTimeoutMilliseconds: timeoutMilliseconds,
                cancellationToken: CancellationToken.None);

            // 必须等轴1、轴2均确认到位后，才允许加载并单次执行找芯片流程。
            // 流程名和模块名都采用截图中的固定名称，避免误跑标定流程或其他实时流程。
            SetStartProductionStatus(
                $"步骤2/2：XY已到初始位置({actual.ActualX:0.###}, {actual.ActualY:0.###})，" +
                $"正在运行{ChipInspectionProcedureName} → {ChipInspectionBlobModuleName}…",
                Color.FromRgb(242, 181, 68));
            var blobResult = await visualCalibrationController.RunRectangleBlobInspectionAsync(
                inspectionSolution.FilePath,
                CancellationToken.None);
            SetBlobInspectionResult(blobResult);

            // 只有方案确实包含流程1/Blob分析1并成功执行后，才记住首次选择的路径。
            if (inspectionSolution.WasSelected)
            {
                _visionCalibration.Settings.ChipInspectionSolutionPath = inspectionSolution.FilePath;
                _visionCalibration.Save();
            }

            try
            {
                // 两个目标必须在本次拍照位置立即换算并缓存。后续吸嘴1移动后，
                // 不能再用已经变化的当前轴位置去计算吸嘴2，否则第二个绝对目标会产生偏差。
                var assignedTargets = CalculateAssignedNozzleTargets(
                    blobResult,
                    calibrationFile.FilePath,
                    actual.ActualX,
                    actual.ActualY);
                SetAssignedNozzleTargets(assignedTargets);
                SetStartProductionStatus(
                    "完成：Blob已显示并完成分配；请点击“吸嘴1 → 物体1”",
                    Color.FromRgb(73, 209, 125));
            }
            catch (Exception exception)
            {
                ClearAssignedNozzleTargets();
                SetCoordinateTransformStatus($"吸嘴分配失败：{exception.Message}", false);
                SetStartProductionStatus(
                    $"Blob已显示；吸嘴目标换算失败：{exception.Message}",
                    Color.FromRgb(242, 181, 68));
            }
        }
        catch (OperationCanceledException exception)
        {
            SetStartProductionStatus(
                exception.Message,
                Color.FromRgb(242, 181, 68));
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
            UpdateHomeCommandState();
        }
    }

    /// <summary>
    /// 从第一套九点标定 XML 的 WorldPointLst 读取机械网格中心，并换算为控制卡脉冲。
    /// 直接读取标定文件可保证“回初始位置”不依赖当前加载的是标定方案还是找芯片方案。
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

    private (string FilePath, bool WasSelected) GetOrSelectChipInspectionSolution()
    {
        var configuredPath = _visionCalibration.Settings.ChipInspectionSolutionPath?.Trim() ?? "";
        if (IsExistingFileWithExtension(configuredPath, ".sol"))
        {
            return (Path.GetFullPath(configuredPath), false);
        }

        return (
            SelectFile(
                "选择“找芯片”VisionMaster方案（需包含 流程1 / Blob分析1）",
                "VisionMaster 方案 (*.sol)|*.sol|所有文件 (*.*)|*.*",
                configuredPath,
                "已取消开始：未选择“找芯片”方案。"),
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
        Nozzle1MechanicalCoordinateText.Text = FormatMechanicalCoordinate(targets.Nozzle1);
        Nozzle2MechanicalCoordinateText.Text = FormatMechanicalCoordinate(targets.Nozzle2);
        SetCoordinateTransformStatus(
            "已分配：Blob结果1 → 吸嘴1，Blob结果2 → 吸嘴2；等待第一次点击。",
            true);
        UpdateAssignedNozzleButtonText();
        UpdateHomeCommandState();
    }

    private void ClearAssignedNozzleTargets()
    {
        _blob1Nozzle1Target = null;
        _blob2Nozzle2Target = null;
        _nextAssignedNozzleMoveStep = 0;
        Nozzle1MechanicalCoordinateText.Text = "X = —　Y = —";
        Nozzle2MechanicalCoordinateText.Text = "X = —　Y = —";
        UpdateAssignedNozzleButtonText();
        UpdateHomeCommandState();
    }

    /// <summary>
    /// 同一个按钮分两次执行：第一次让吸嘴1对位Blob结果1，第二次让吸嘴2对位Blob结果2。
    /// 此处只移动第一套XY，不控制Z轴、真空或取料动作。
    /// </summary>
    private async void MoveAssignedNozzle_Click(object sender, RoutedEventArgs e)
    {
        if (_assignedNozzleMoveRunning || _coordinateTransformRunning || _startSequenceRunning)
        {
            return;
        }

        var step = _nextAssignedNozzleMoveStep;
        var target = step switch
        {
            1 => _blob1Nozzle1Target,
            2 => _blob2Nozzle2Target,
            _ => null
        };
        if (target is null)
        {
            SetCoordinateTransformStatus("请先点击“开始运行”完成Blob识别和吸嘴分配。", false);
            return;
        }

        var nozzleName = step == 1 ? "吸嘴1" : "吸嘴2";
        var objectName = step == 1 ? "物体1" : "物体2";
        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var velocity = _visionCalibration.Settings.VelocityPulsesPerSecond;
            if (!double.IsFinite(velocity) || velocity <= 0)
            {
                throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
            }

            _assignedNozzleMoveRunning = true;
            UpdateHomeCommandState();
            MoveAssignedNozzleTitleText.Text = $"{nozzleName}移动中";
            MoveAssignedNozzleHintText.Text = $"正在对位{objectName}…";
            SetCoordinateTransformStatus(
                $"正在移动{nozzleName}到{objectName}：X={target.Value.X:0.###}，Y={target.Value.Y:0.###}",
                true);

            var current = motionController.CaptureCalibrationFeedback(
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
                cancellationToken: CancellationToken.None);

            if (step == 1)
            {
                _nextAssignedNozzleMoveStep = 2;
                SetCoordinateTransformStatus(
                    $"吸嘴1已到物体1({actual.ActualX:0.###}, {actual.ActualY:0.###})；请再点一次移动吸嘴2。",
                    true);
            }
            else
            {
                _nextAssignedNozzleMoveStep = 3;
                SetCoordinateTransformStatus(
                    $"吸嘴2已到物体2({actual.ActualX:0.###}, {actual.ActualY:0.###})；两次顺序对位完成。",
                    true);
            }
        }
        catch (Exception exception)
        {
            SetCoordinateTransformStatus($"{nozzleName}对位失败：{exception.Message}", false);
        }
        finally
        {
            _assignedNozzleMoveRunning = false;
            UpdateAssignedNozzleButtonText();
            UpdateHomeCommandState();
        }
    }

    private void VisionCalibration_Changed(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(RefreshVisionCalibrationStatus);
            return;
        }

        RefreshVisionCalibrationStatus();
        RefreshFirstSetCalibrationDetails();
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

    private async void ConvertBothNozzles_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_coordinateTransformRunning || _startSequenceRunning)
        {
            return;
        }

        try
        {
            var pixelX = ParsePixelCoordinate(PixelXTextBox.Text, "像素 X");
            var pixelY = ParsePixelCoordinate(PixelYTextBox.Text, "像素 Y");

            _coordinateTransformRunning = true;
            // 手工换算会覆盖右侧坐标显示，因此同时取消上一轮Blob的顺序移动目标。
            ClearAssignedNozzleTargets();
            UpdateHomeCommandState();
            SetCoordinateTransformStatus($"正在换算像素({pixelX}, {pixelY})…", true);

            var targets = await ConvertPixelToMechanicalTargetsAsync(pixelX, pixelY);
            Nozzle1MechanicalCoordinateText.Text = FormatMechanicalCoordinate(targets.Nozzle1);
            Nozzle2MechanicalCoordinateText.Text = FormatMechanicalCoordinate(targets.Nozzle2);
            SetCoordinateTransformStatus("换算完成；仅显示坐标，未下发运动命令。", true);
        }
        catch (Exception exception)
        {
            Nozzle1MechanicalCoordinateText.Text = "X = —　Y = —";
            Nozzle2MechanicalCoordinateText.Text = "X = —　Y = —";
            SetCoordinateTransformStatus($"换算失败：{exception.Message}", false);
        }
        finally
        {
            _coordinateTransformRunning = false;
            UpdateHomeCommandState();
        }
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

    private void RefreshFirstSetCalibrationDetails()
    {
        if (FirstSetCalibrationDetailsText is null)
        {
            return;
        }

        var snapshot = _visionCalibration.GetSnapshot();
        var profileName = snapshot.CalibrationProfileExists
            ? Path.GetFileName(snapshot.CalibrationProfilePath)
            : "配置未保存";
        var nozzle1 = snapshot.Nozzle1Calibrated ? "嘴1√" : "嘴1×";
        var nozzle2 = snapshot.Nozzle2Calibrated ? "嘴2√" : "嘴2×";
        FirstSetCalibrationDetailsText.Text = $"轴1=X / 轴2=Y　{profileName}　{nozzle1}　{nozzle2}";
        FirstSetCalibrationDetailsText.ToolTip =
            $"标定文件：{snapshot.CalibrationFilePath}\n" +
            $"XY配置：{snapshot.CalibrationProfilePath}\n" +
            $"吸嘴1偏移：X={snapshot.Nozzle1OffsetX:0.###}，Y={snapshot.Nozzle1OffsetY:0.###} pulse\n" +
            $"吸嘴2偏移：X={snapshot.Nozzle2OffsetX:0.###}，Y={snapshot.Nozzle2OffsetY:0.###} pulse";
    }

    private void UpdateHomeCommandState()
    {
        if (ConvertBothNozzlesButton is null ||
            StartProductionButton is null ||
            MoveAssignedNozzleButton is null)
        {
            return;
        }

        var controllersReady =
            _motionController is not null &&
            _visualCalibrationController is not null;
        var commandsIdle =
            !_coordinateTransformRunning &&
            !_startSequenceRunning &&
            !_assignedNozzleMoveRunning;
        ConvertBothNozzlesButton.IsEnabled = controllersReady && commandsIdle;
        StartProductionButton.IsEnabled = controllersReady && commandsIdle;
        MoveAssignedNozzleButton.IsEnabled =
            controllersReady &&
            commandsIdle &&
            ((_nextAssignedNozzleMoveStep == 1 && _blob1Nozzle1Target is not null) ||
             (_nextAssignedNozzleMoveStep == 2 && _blob2Nozzle2Target is not null));
        PixelXTextBox.IsEnabled = commandsIdle;
        PixelYTextBox.IsEnabled = commandsIdle;
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
                MoveAssignedNozzleHintText.Text = "第一次点击：移动到Blob结果1";
                break;
            case 2:
                MoveAssignedNozzleTitleText.Text = "吸嘴2 → 物体2";
                MoveAssignedNozzleHintText.Text = "第二次点击：移动到Blob结果2";
                break;
            case 3:
                MoveAssignedNozzleTitleText.Text = "顺序对位完成";
                MoveAssignedNozzleHintText.Text = "重新开始识别后可再次执行";
                break;
            default:
                MoveAssignedNozzleTitleText.Text = "吸嘴顺序对位";
                MoveAssignedNozzleHintText.Text = "等待Blob识别与目标分配";
                break;
        }
    }

    private void SetStartProductionStatus(string message, Color color)
    {
        StartProductionHintText.Text = message;
        StartProductionHintText.ToolTip = message;
        StartProductionHintText.Foreground = new SolidColorBrush(color);
    }

    private void ClearBlobInspectionResult()
    {
        BlobRectangle1CenterText.Text = "矩形1质心(px)：X = —　Y = —";
        BlobRectangle2CenterText.Text = "矩形2质心(px)：X = —　Y = —";
        BlobInspectionImage.Source = null;
        BlobInspectionOverlayCanvas.Children.Clear();
        BlobInspectionImageViewbox.Visibility = Visibility.Collapsed;
        BlobInspectionImagePlaceholder.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text = "等待拍照";
        BlobInspectionImageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(98, 181, 255));
    }

    private void SetBlobInspectionResult(VisionRectangleBlobResult result)
    {
        BlobRectangle1CenterText.Text =
            $"矩形1质心(px)：X = {result.Rectangle1.X:0.###}　Y = {result.Rectangle1.Y:0.###}";
        BlobRectangle2CenterText.Text =
            $"矩形2质心(px)：X = {result.Rectangle2.X:0.###}　Y = {result.Rectangle2.Y:0.###}";
        if (result.ImageWidth <= 0 ||
            result.ImageHeight <= 0 ||
            string.IsNullOrWhiteSpace(result.ImagePath))
        {
            BlobInspectionImageStatusText.Text = "XY已显示 · 本次未返回检测图";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(242, 181, 68));
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

    private void SetCoordinateTransformStatus(string message, bool success)
    {
        CoordinateTransformStatusText.Text = message;
        CoordinateTransformStatusText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(73, 209, 125)
            : Color.FromRgb(242, 181, 68));
    }

    private static string FormatMechanicalCoordinate(VisionMotionTarget target)
    {
        return $"X = {target.X:0.###}　Y = {target.Y:0.###}";
    }

    private static double ParseFiniteCoordinate(string? value, string fieldName)
    {
        if (!TryParseCoordinate(value, out var parsed))
        {
            throw new ArgumentException($"{fieldName}必须是有效数字。");
        }

        return parsed;
    }

    private static double ParsePixelCoordinate(string? value, string fieldName)
    {
        var parsed = ParseFiniteCoordinate(value, fieldName);
        if (parsed < 0)
        {
            throw new ArgumentException($"{fieldName}必须大于等于0。");
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
}
