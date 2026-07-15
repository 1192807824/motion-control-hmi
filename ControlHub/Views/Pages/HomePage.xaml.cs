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
    private bool _startSequenceRunning;

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
    /// 主页开始按钮目前只执行步骤1：第一套 XY 回到九点标定时记录的中心位置。
    /// 完成此步后流程立即结束，不触发拍照、取料或吸嘴动作。
    /// </summary>
    private async void StartProduction_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (_startSequenceRunning || _coordinateTransformRunning)
        {
            return;
        }

        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var velocity = _visionCalibration.Settings.VelocityPulsesPerSecond;
            if (!double.IsFinite(velocity) || velocity <= 0)
            {
                throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
            }

            _startSequenceRunning = true;
            UpdateHomeCommandState();
            SetStartProductionStatus("步骤1/1：正在读取第一套 XY 标定中心…", Color.FromRgb(242, 181, 68));

            var center = await ReadFirstSetCalibrationCenterAsync(CancellationToken.None);

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
                $"步骤1/1：第一套 XY 正在回标定中心 X={center.X:0.###}，Y={center.Y:0.###}…",
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

            SetStartProductionStatus(
                $"步骤1完成：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse",
                Color.FromRgb(73, 209, 125));
        }
        catch (Exception exception)
        {
            SetStartProductionStatus(
                $"步骤1失败：{exception.Message}",
                Color.FromRgb(242, 122, 128));
        }
        finally
        {
            _startSequenceRunning = false;
            UpdateHomeCommandState();
        }
    }

    /// <summary>
    /// 从 VisionMaster 标定文件读取图像中心对应的机械绝对坐标，并换算为控制卡脉冲。
    /// </summary>
    private async Task<(double X, double Y)> ReadFirstSetCalibrationCenterAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = GetFirstSetCalibrationFileSnapshot();
        var visualCalibrationController = _visualCalibrationController
            ?? throw new InvalidOperationException("主页尚未连接视觉标定组件。");

        // TRANSFORM_PIXEL 会同时返回输入点和图像中心的转换结果。
        // 此处输入(0,0)仅用于触发读取，步骤1只使用 CenterTransformedX/Y。
        var transformed = await visualCalibrationController.TransformPixelAsync(
            0d,
            0d,
            snapshot.CalibrationFilePath,
            cancellationToken);

        // 标定文件中的中心坐标单位是 VisionMaster 单位；控制卡使用 pulse。
        // 回标定中心移动的是相机所在的 XY 基准点，因此这里不能叠加吸嘴1/2偏移。
        var centerX = transformed.CenterTransformedX * VisionCalibrationService.PulsesPerVisionUnit;
        var centerY = transformed.CenterTransformedY * VisionCalibrationService.PulsesPerVisionUnit;
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            throw new InvalidOperationException("第一套 XY 标定中心坐标无效。");
        }

        return (centerX, centerY);
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
        if (ConvertBothNozzlesButton is null || StartProductionButton is null)
        {
            return;
        }

        var controllersReady =
            _motionController is not null &&
            _visualCalibrationController is not null;
        var commandsIdle = !_coordinateTransformRunning && !_startSequenceRunning;
        ConvertBothNozzlesButton.IsEnabled = controllersReady && commandsIdle;
        StartProductionButton.IsEnabled = controllersReady && commandsIdle;
        PixelXTextBox.IsEnabled = commandsIdle;
        PixelYTextBox.IsEnabled = commandsIdle;
    }

    private void SetStartProductionStatus(string message, Color color)
    {
        StartProductionHintText.Text = message;
        StartProductionHintText.ToolTip = message;
        StartProductionHintText.Foreground = new SolidColorBrush(color);
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
