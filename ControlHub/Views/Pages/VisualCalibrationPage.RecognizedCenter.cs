using System.IO;
using System.Windows;
using ControlHub.Services.Motion;
using ControlHub.Services.Vision;

namespace ControlHub.Views.Pages;

public partial class VisualCalibrationPage
{
    private bool CanMoveRecognizedCenter() =>
        ActiveCalibrationMode == VisualCalibrationMode.First && !_shutdown && _hostReady &&
        _motionController is not null && !_closingVisionProcesses && !_calibrationRunning && !_centerSyncRunning &&
        !_clickMoveRunning && !_clickMoveConfigurationRunning && !_calibrationProcedureSwitchRunning &&
        !_livePreviewStarting && !_calibrationFileImporting && !_nozzlePointFinding && !_nozzlePointSaving &&
        !_rotationCenterRunning && !_lowerCameraCorrectionTestRunning && _manualJogAxisNo is null &&
        EnableClickMoveCheckBox.IsChecked != true && GetSelectedTargetTool() != VisionTargetTool.Camera &&
        _visionCalibration.IsToolCalibrated(GetSelectedTargetTool()) &&
        TryGetCalibrationFilePath(CalibrationFilePathTextBox.Text, out var path) && File.Exists(path);

    private async void RecognizedCenterMove_Click(object sender, RoutedEventArgs e)
    {
        if (!CanMoveRecognizedCenter())
        {
            SetClickMoveStatus("请在第一套视觉选择已标定吸嘴，关闭点击图像移动，并等待当前操作完成。", WorkflowStatus.Error);
            return;
        }
        _clickMoveRunning = true;
        _clickMoveCancellation = new CancellationTokenSource();
        var token = _clickMoveCancellation.Token;
        UpdateCommandState();
        try
        {
            var controller = _motionController!;
            var nozzle = GetSelectedTargetTool();
            var calibration = _visionCalibration.GetSnapshot();
            var path = GetCalibrationFilePath(CalibrationFilePathTextBox.Text);
            var velocity = ParsePositiveDouble(VelocityTextBox.Text, "识别移动速度");
            CalibrationCenterPosition? actual = null;
            SetClickMoveStatus($"正在执行{RecognizedCenterMove.ProcedureName}，识别108匹配框中心…", WorkflowStatus.Running);
            await RecognizedCenterMove.ExecuteAsync(calibration, nozzle,
                () => controller.CaptureCalibrationCenter(1, 2),
                cancellation => VisionHost.RunRecognizedCenterAsync(path, cancellation),
                async (target, cancellation) =>
                {
                    var current = controller.CaptureCalibrationCenter(1, 2);
                    SetClickMoveStatus($"{GetToolDisplayName(nozzle)}对准识别中心：X={target.X:0.###}，Y={target.Y:0.###} pulse…", WorkflowStatus.Running);
                    actual = await controller.MoveCalibrationAxesToAsync(1, 2, target.X, target.Y, velocity,
                        DefaultPositionTolerancePulses,
                        CalculateDirectMoveTimeout(target.X - current.ActualX, target.Y - current.ActualY, velocity), cancellation);
                }, token);
            SetClickMoveStatus($"{GetToolDisplayName(nozzle)}已对准匹配框中心：X={actual!.ActualX:0.###}，Y={actual.ActualY:0.###} pulse。", WorkflowStatus.Success);
        }
        catch (OperationCanceledException)
        {
            SetClickMoveStatus("识别中心点移动已取消；如已启动XY，已请求停止。", WorkflowStatus.Error);
        }
        catch (Exception exception)
        {
            SetClickMoveStatus($"识别中心点移动失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _clickMoveCancellation?.Dispose();
            _clickMoveCancellation = null;
            _clickMoveRunning = false;
            UpdateCommandState();
        }
    }
}
