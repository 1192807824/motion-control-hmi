using System.IO;
using System.Windows;
using System.Windows.Controls;
using ControlHub.Services.Motion;
using ControlHub.Services.Vision;

namespace ControlHub.Views.Pages;

public partial class VisualCalibrationPage
{
    private NozzleAlignmentCorrection? _nozzleAlignmentCorrection;
    private bool _nozzleAlignmentOperation;

    private bool CanCorrectNozzleAlignment() => CanMoveRecognizedCenter() &&
        _nozzleAlignmentCorrection?.Matches(_visionCalibration.GetSnapshot(), GetSelectedTargetTool()) == true;

    private void UpdateNozzleAlignmentState()
    {
        // Menu re-entry synchronizes the current procedure. Disable controls while it runs,
        // but retain the completed alignment; real mode changes explicitly clear it.
        if (!_nozzleAlignmentOperation && (!CanMoveRecognizedCenter(ignoreProcedureSynchronization: true) ||
            _nozzleAlignmentCorrection?.Matches(_visionCalibration.GetSnapshot(), GetSelectedTargetTool()) != true))
            _nozzleAlignmentCorrection = null;
        NozzleAlignmentPanel.Visibility = ActiveCalibrationMode == VisualCalibrationMode.First
            ? Visibility.Visible : Visibility.Collapsed;
        NozzleAlignmentPanel.IsEnabled = CanCorrectNozzleAlignment();
        SaveNozzleAlignmentButton.Content = GetSelectedTargetTool() == VisionTargetTool.Nozzle2
            ? "保存微调结果 → Z2" : "保存微调结果 → Z1";
    }

    private async void NozzleAlignmentJog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string direction }) await JogNozzleAlignmentAsync(direction);
    }

    private async Task JogNozzleAlignmentAsync(string direction)
    {
        if (!CanCorrectNozzleAlignment()) return;
        _nozzleAlignmentOperation = true;
        _clickMoveRunning = true;
        _clickMoveCancellation = new CancellationTokenSource();
        var token = _clickMoveCancellation.Token;
        UpdateCommandState();
        try
        {
            var session = _nozzleAlignmentCorrection!;
            var distance = ParsePositiveDouble(NozzleAlignmentDistanceTextBox.Text, "微调距离");
            var velocity = ParsePositiveDouble(VelocityTextBox.Text, "微调速度");
            var (dx, dy) = direction switch
            {
                "AlignUp" => (0d, distance),
                "AlignDown" => (0d, -distance),
                "AlignLeft" => (distance, 0d),
                "AlignRight" => (-distance, 0d),
                _ => throw new InvalidOperationException("微调方向无效。")
            };
            var controller = _motionController!;
            var current = controller.CaptureCalibrationCenter(1, 2);
            SetClickMoveStatus($"{GetToolDisplayName(session.Tool)}正在微调XY…", WorkflowStatus.Running);
            var actual = await controller.MoveCalibrationAxesToAsync(1, 2,
                current.ActualX + dx, current.ActualY + dy, velocity,
                Math.Min(DefaultPositionTolerancePulses, distance / 10),
                CalculateDirectMoveTimeout(dx, dy, velocity), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_nozzleAlignmentCorrection, session)) return;
            SetClickMoveStatus($"微调到位：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse。对准后保存微调结果。", WorkflowStatus.Ready);
        }
        catch (Exception exception)
        {
            _nozzleAlignmentCorrection = null;
            SetClickMoveStatus(exception is OperationCanceledException
                ? "微调已停止，请重新识别中心点后再调整。"
                : $"微调失败：{exception.Message} 请重新识别中心点。", WorkflowStatus.Error);
        }
        finally
        {
            _clickMoveCancellation?.Dispose();
            _clickMoveCancellation = null;
            _clickMoveRunning = false;
            _nozzleAlignmentOperation = false;
            UpdateCommandState();
        }
    }

    private void SaveNozzleAlignment_Click(object sender, RoutedEventArgs e)
    {
        if (!CanCorrectNozzleAlignment()) return;
        var session = _nozzleAlignmentCorrection!;
        var previousX = session.Tool == VisionTargetTool.Nozzle1 ? ActiveNozzle1OffsetX : ActiveNozzle2OffsetX;
        var previousY = session.Tool == VisionTargetTool.Nozzle1 ? ActiveNozzle1OffsetY : ActiveNozzle2OffsetY;
        try
        {
            var actual = _motionController!.CaptureCalibrationCenter(1, 2);
            var offset = session.CalculateOffset(actual);
            SetActiveNozzleOffset(session.Tool, offset.X, offset.Y);
            SaveCurrentCalibrationProfile();
            RecordNozzleVerification(session.Tool);
            UpdateNozzleCalibrationDisplay();
            SetClickMoveStatus($"Z{(session.Tool == VisionTargetTool.Nozzle1 ? 1 : 2)} 微调结果已保存：偏移X={offset.X:0.###}，Y={offset.Y:0.###} pulse。下次识别移动使用新结果。", WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            SetActiveNozzleOffset(session.Tool, previousX, previousY);
            SetClickMoveStatus($"微调保存失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            // Consume the session even on a failed save; never apply a stale correction again.
            _nozzleAlignmentCorrection = null;
            UpdateCommandState();
        }
    }

    private void NozzleAlignmentDistance_Changed(object sender, TextChangedEventArgs e) => ScheduleCalibrationSidebarSync();

    private bool CanMoveRecognizedCenter(bool ignoreProcedureSynchronization = false) =>
        ActiveCalibrationMode == VisualCalibrationMode.First && !_shutdown && _hostReady &&
        _motionController is not null && !_closingVisionProcesses && !_calibrationRunning && !_centerSyncRunning &&
        !_clickMoveRunning && !_clickMoveConfigurationRunning &&
        (ignoreProcedureSynchronization || !_calibrationProcedureSwitchRunning) &&
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
        _nozzleAlignmentCorrection = null;
        _nozzleAlignmentOperation = true;
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
            SetClickMoveStatus($"正在执行{RecognizedCenterMove.ProcedureName}，读取110脚本1的XY坐标…", WorkflowStatus.Running);
            var target = await RecognizedCenterMove.ExecuteAsync(calibration, nozzle,
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
            token.ThrowIfCancellationRequested();
            _nozzleAlignmentCorrection = new NozzleAlignmentCorrection(calibration, target, actual!);
            SetClickMoveStatus($"{GetToolDisplayName(nozzle)}已到识别目标位置，可用方向按钮微调，对准后保存对应Z结果。", WorkflowStatus.Success);
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
            _nozzleAlignmentOperation = false;
            UpdateCommandState();
        }
    }
}
