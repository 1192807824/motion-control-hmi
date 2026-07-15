using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Services.Vision;

namespace ControlHub.Views.Pages;

public partial class HomePage : UserControl
{
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private MotionControlPage? _motionController;
    private VisualCalibrationPage? _visualCalibrationController;
    private bool _coordinateTransformRunning;

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
        UpdateCoordinateTransformCommandState();
    }

    public void AttachVisionCalibrationController(VisualCalibrationPage visualCalibrationController)
    {
        _visualCalibrationController = visualCalibrationController
            ?? throw new ArgumentNullException(nameof(visualCalibrationController));
        UpdateCoordinateTransformCommandState();
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
    /// 从视觉标定页的共享配置中加载第一套 XY 标定文件和两个吸嘴偏移。
    /// </summary>
    private VisionCalibrationSnapshot GetFirstSetCalibrationSnapshot()
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

        if (!snapshot.CalibrationProfileExists)
        {
            throw new FileNotFoundException(
                "第一套 XY 配置尚未保存，请先在视觉标定页完成双吸嘴验证并保存配置。",
                snapshot.CalibrationProfilePath);
        }

        return snapshot;
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
        var current = motionController.CaptureCalibrationCenter(
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
        if (_coordinateTransformRunning)
        {
            return;
        }

        try
        {
            var pixelX = ParsePixelCoordinate(PixelXTextBox.Text, "像素 X");
            var pixelY = ParsePixelCoordinate(PixelYTextBox.Text, "像素 Y");

            _coordinateTransformRunning = true;
            UpdateCoordinateTransformCommandState();
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
            UpdateCoordinateTransformCommandState();
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

    private void UpdateCoordinateTransformCommandState()
    {
        if (ConvertBothNozzlesButton is null)
        {
            return;
        }

        var canStart =
            !_coordinateTransformRunning &&
            _motionController is not null &&
            _visualCalibrationController is not null;
        ConvertBothNozzlesButton.IsEnabled = canStart;
        PixelXTextBox.IsEnabled = !_coordinateTransformRunning;
        PixelYTextBox.IsEnabled = !_coordinateTransformRunning;
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
