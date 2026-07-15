using System.Globalization;
using System.IO;
using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Services.Motion;
using ControlHub.Services.Vision;

namespace ControlHub.Views.Pages;

public partial class HomePage : UserControl
{
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private MotionControlPage? _motionController;
    private VisualCalibrationPage? _visualCalibrationController;
    private CancellationTokenSource? _nozzleTestCancellation;
    private bool _nozzleTestRunning;

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
        UpdateNozzleTestCommandState();
    }

    public void AttachVisionCalibrationController(VisualCalibrationPage visualCalibrationController)
    {
        _visualCalibrationController = visualCalibrationController
            ?? throw new ArgumentNullException(nameof(visualCalibrationController));
        UpdateNozzleTestCommandState();
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
    /// 上料流程：输入相机图像像素坐标，把第一套 XY 的吸嘴1移动到该像素对应的位置。
    /// </summary>
    public Task<CalibrationCenterPosition> ShangLiao_move_xizui1(
        double pixelX,
        double pixelY,
        double velocityPulsesPerSecond = 100_000d,
        double positionTolerancePulses = 10d,
        int timeoutMilliseconds = 30_000,
        CancellationToken cancellationToken = default)
    {
        return MoveFirstSetNozzleToPixelAsync(
            pixelX,
            pixelY,
            VisionTargetTool.Nozzle1,
            velocityPulsesPerSecond,
            positionTolerancePulses,
            timeoutMilliseconds,
            cancellationToken);
    }

    /// <summary>
    /// 上料流程：输入相机图像像素坐标，把第一套 XY 的吸嘴2移动到该像素对应的位置。
    /// </summary>
    public Task<CalibrationCenterPosition> ShangLiao_move_xizui2(
        double pixelX,
        double pixelY,
        double velocityPulsesPerSecond = 100_000d,
        double positionTolerancePulses = 10d,
        int timeoutMilliseconds = 30_000,
        CancellationToken cancellationToken = default)
    {
        return MoveFirstSetNozzleToPixelAsync(
            pixelX,
            pixelY,
            VisionTargetTool.Nozzle2,
            velocityPulsesPerSecond,
            positionTolerancePulses,
            timeoutMilliseconds,
            cancellationToken);
    }

    /// <summary>
    /// 主页运动流程可直接调用：按选定工具的标定偏移移动第一套 XY（硬件轴1、轴2）。
    /// </summary>
    public async Task<CalibrationCenterPosition> MoveToVisionTargetAsync(
        double cameraTargetX,
        double cameraTargetY,
        VisionTargetTool targetTool,
        double velocityPulsesPerSecond,
        double positionTolerancePulses = 10d,
        int timeoutMilliseconds = 30_000,
        CancellationToken cancellationToken = default)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        var target = CalculateVisionTarget(cameraTargetX, cameraTargetY, targetTool);
        return await motionController.MoveCalibrationAxesToAsync(
            VisionCalibrationService.FirstSetXHardwareAxisNo,
            VisionCalibrationService.FirstSetYHardwareAxisNo,
            target.X,
            target.Y,
            velocityPulsesPerSecond,
            positionTolerancePulses,
            timeoutMilliseconds,
            cancellationToken);
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

    private async void TestNozzle1Move_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await ExecuteNozzleTestAsync(VisionTargetTool.Nozzle1);
    }

    private async void TestNozzle2Move_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await ExecuteNozzleTestAsync(VisionTargetTool.Nozzle2);
    }

    private void StopNozzleTest_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        _nozzleTestCancellation?.Cancel();
        SetNozzleTestStatus("正在停止第一套 XY 测试移动…", false);
    }

    private async Task ExecuteNozzleTestAsync(VisionTargetTool tool)
    {
        if (_nozzleTestRunning)
        {
            return;
        }

        try
        {
            var pixelX = ParsePixelCoordinate(TestCameraTargetXTextBox.Text, "像素 X");
            var pixelY = ParsePixelCoordinate(TestCameraTargetYTextBox.Text, "像素 Y");
            var velocity = ParsePositiveTestValue(TestMoveVelocityTextBox.Text, "速度");
            var toolName = tool == VisionTargetTool.Nozzle1 ? "吸嘴1" : "吸嘴2";

            _nozzleTestCancellation = new CancellationTokenSource();
            _nozzleTestRunning = true;
            UpdateNozzleTestCommandState();
            SetNozzleTestStatus(
                $"正在转换像素({pixelX}, {pixelY})并移动{toolName}…",
                true);

            var actual = tool == VisionTargetTool.Nozzle1
                ? await ShangLiao_move_xizui1(
                    pixelX,
                    pixelY,
                    velocity,
                    cancellationToken: _nozzleTestCancellation.Token)
                : await ShangLiao_move_xizui2(
                    pixelX,
                    pixelY,
                    velocity,
                    cancellationToken: _nozzleTestCancellation.Token);
            SetNozzleTestStatus(
                $"{toolName}测试完成：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse。",
                true);
        }
        catch (OperationCanceledException)
        {
            SetNozzleTestStatus("测试移动已停止。", false);
        }
        catch (Exception exception)
        {
            SetNozzleTestStatus($"测试失败：{exception.Message}", false);
        }
        finally
        {
            _nozzleTestCancellation?.Dispose();
            _nozzleTestCancellation = null;
            _nozzleTestRunning = false;
            UpdateNozzleTestCommandState();
        }
    }

    private async Task<CalibrationCenterPosition> MoveFirstSetNozzleToPixelAsync(
        double pixelX,
        double pixelY,
        VisionTargetTool tool,
        double velocityPulsesPerSecond,
        double positionTolerancePulses,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        var snapshot = EnsureFirstSetToolReady(tool);
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
        return await MoveToVisionTargetAsync(
            cameraTargetX,
            cameraTargetY,
            tool,
            velocityPulsesPerSecond,
            positionTolerancePulses,
            timeoutMilliseconds,
            cancellationToken);
    }

    private VisionCalibrationSnapshot EnsureFirstSetToolReady(VisionTargetTool tool)
    {
        var snapshot = GetFirstSetCalibrationSnapshot();
        var calibrated = tool switch
        {
            VisionTargetTool.Nozzle1 => snapshot.Nozzle1Calibrated,
            VisionTargetTool.Nozzle2 => snapshot.Nozzle2Calibrated,
            _ => true
        };
        if (!calibrated)
        {
            var toolName = tool == VisionTargetTool.Nozzle1 ? "吸嘴1" : "吸嘴2";
            throw new InvalidOperationException($"第一套 XY 的{toolName}偏移尚未标定。");
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

    private void UpdateNozzleTestCommandState()
    {
        if (TestNozzle1MoveButton is null)
        {
            return;
        }

        var canStart =
            !_nozzleTestRunning &&
            _motionController is not null &&
            _visualCalibrationController is not null;
        TestNozzle1MoveButton.IsEnabled = canStart;
        TestNozzle2MoveButton.IsEnabled = canStart;
        StopNozzleTestButton.IsEnabled = _nozzleTestRunning;
        TestCameraTargetXTextBox.IsEnabled = !_nozzleTestRunning;
        TestCameraTargetYTextBox.IsEnabled = !_nozzleTestRunning;
        TestMoveVelocityTextBox.IsEnabled = !_nozzleTestRunning;
    }

    private void SetNozzleTestStatus(string message, bool success)
    {
        NozzleTestStatusText.Text = message;
        NozzleTestStatusText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(73, 209, 125)
            : Color.FromRgb(242, 181, 68));
    }

    private static double ParseFiniteTestValue(string? value, string fieldName)
    {
        if (!TryParseTestValue(value, out var parsed))
        {
            throw new ArgumentException($"{fieldName}必须是有效数字。");
        }

        return parsed;
    }

    private static double ParsePixelCoordinate(string? value, string fieldName)
    {
        var parsed = ParseFiniteTestValue(value, fieldName);
        if (parsed < 0)
        {
            throw new ArgumentException($"{fieldName}必须大于等于0。");
        }

        return parsed;
    }

    private static double ParsePositiveTestValue(string? value, string fieldName)
    {
        var parsed = ParseFiniteTestValue(value, fieldName);
        if (parsed <= 0)
        {
            throw new ArgumentOutOfRangeException(fieldName, $"{fieldName}必须大于0。");
        }

        return parsed;
    }

    private static bool TryParseTestValue(string? value, out double parsed)
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
