using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Services.Motion;
using ControlHub.Services.Vision;

namespace ControlHub.Views.Pages;

public partial class HomePage : UserControl
{
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private MotionControlPage? _motionController;

    public HomePage()
    {
        InitializeComponent();
        _visionCalibration.Changed += VisionCalibration_Changed;
        RefreshVisionCalibrationStatus();
    }

    public void AttachMotionController(MotionControlPage motionController)
    {
        _motionController = motionController ?? throw new ArgumentNullException(nameof(motionController));
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
}
