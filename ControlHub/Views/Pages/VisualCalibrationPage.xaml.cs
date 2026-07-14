using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Services.Motion;
using ControlHub.Views.Controls;
using Microsoft.Win32;

namespace ControlHub.Views.Pages;

public partial class VisualCalibrationPage : UserControl
{
    private const int FirstSetXHardwareAxisNo = 1;
    private const int FirstSetYHardwareAxisNo = 2;
    private const double PulsesPerVisionUnit = 10_000d;
    private const double DefaultPositionTolerancePulses = 10d;
    private static readonly string DefaultCalibrationFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ControlHub",
        "Calibration",
        "first-xy-calibration.xml");
    private bool _startRequested;
    private bool _shutdown;
    private bool _hostReady;
    private bool _hostCanRestart;
    private bool _calibrationRunning;
    private bool _centerSyncRunning;
    private bool _clickMoveRunning;
    private bool _clickMoveConfigurationRunning;
    private bool _suppressClickMoveModeEvent;
    private MotionControlPage? _motionController;
    private CalibrationCenterPosition? _recordedCenter;
    private CancellationTokenSource? _calibrationCancellation;
    private CancellationTokenSource? _clickMoveCancellation;

    public VisualCalibrationPage()
    {
        InitializeComponent();
        CalibrationFilePathTextBox.Text = DefaultCalibrationFilePath;
        UpdateVisionOffsetPreview();
        UpdateCommandState();
    }

    public void AttachMotionController(MotionControlPage motionController)
    {
        _motionController = motionController ?? throw new ArgumentNullException(nameof(motionController));
        UpdateCommandState();
    }

    public async Task EnsureStartedAsync()
    {
        if (_startRequested || _shutdown)
        {
            return;
        }

        _startRequested = true;
        _hostCanRestart = false;
        UpdateCommandState();
        await VisionHost.StartAsync();
    }

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        _calibrationCancellation?.Cancel();
        _clickMoveCancellation?.Cancel();
        VisionHost.Shutdown();
    }

    private async void RecordCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_centerSyncRunning)
        {
            return;
        }

        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            _recordedCenter = motionController.CaptureCalibrationCenter(
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo);
            CenterXPulseText.Text = _recordedCenter.ActualX.ToString("0.###", CultureInfo.CurrentCulture);
            CenterYPulseText.Text = _recordedCenter.ActualY.ToString("0.###", CultureInfo.CurrentCulture);
            CenterVmText.Text =
                $"基准点 X：{_recordedCenter.ActualX / PulsesPerVisionUnit:0.####}　" +
                $"Y：{_recordedCenter.ActualY / PulsesPerVisionUnit:0.####}";
            CalibrationProgressBar.Value = 0;
        }
        catch (Exception exception)
        {
            _recordedCenter = null;
            CenterXPulseText.Text = "未记录";
            CenterYPulseText.Text = "未记录";
            CenterVmText.Text = "基准点 X：--　Y：--";
            SetWorkflowStatus(exception.Message, WorkflowStatus.Error);
            UpdateCommandState();
            return;
        }

        if (!_hostReady)
        {
            SetWorkflowStatus(
                $"中心已记录：轴1 X={_recordedCenter.ActualX:0.###} pulse，" +
                $"轴2 Y={_recordedCenter.ActualY:0.###} pulse；视觉组件未就绪，启动标定时会再次写入。",
                WorkflowStatus.Ready);
            UpdateCommandState();
            return;
        }

        _centerSyncRunning = true;
        UpdateCommandState();
        SetWorkflowStatus("中心已记录，正在写入标定流程.N点标定1…", WorkflowStatus.Running);
        try
        {
            var message = await VisionHost.SetCalibrationCenterAsync(
                _recordedCenter.ActualX / PulsesPerVisionUnit,
                _recordedCenter.ActualY / PulsesPerVisionUnit,
                CancellationToken.None);
            SetWorkflowStatus(message, WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            SetWorkflowStatus(
                $"中心坐标已记录，但写入N点标定1失败：{exception.Message}；启动标定时会再次写入。",
                WorkflowStatus.Error);
        }
        finally
        {
            _centerSyncRunning = false;
            UpdateCommandState();
        }
    }

    private async void StartCalibration_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationRunning)
        {
            return;
        }

        var visionPrepared = false;
        var calibrationCompleted = false;
        try
        {
            var center = _recordedCenter
                ?? throw new InvalidOperationException("请先记录料盘中心坐标。");
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            if (!_hostReady)
            {
                throw new InvalidOperationException("VisionMaster 视觉组件尚未就绪。");
            }

            var stepX = ParsePositiveDouble(StepXPulsesTextBox.Text, "间距 X");
            var stepY = ParsePositiveDouble(StepYPulsesTextBox.Text, "间距 Y");
            var velocity = ParsePositiveDouble(VelocityTextBox.Text, "标定速度");
            var settleMilliseconds = ParseNonNegativeInt(
                SettleMillisecondsTextBox.Text,
                "到位稳定等待");
            var xFirst = (MovePriorityComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "X";
            var movePriority = xFirst
                ? NinePointMovePriority.XFirst
                : NinePointMovePriority.YFirst;
            var moveTimeoutMilliseconds = CalculateMoveTimeout(stepX, stepY, velocity);
            var calibrationFilePath = GetCalibrationFilePath(CalibrationFilePathTextBox.Text);

            _calibrationCancellation = new CancellationTokenSource();
            var cancellationToken = _calibrationCancellation.Token;
            _calibrationRunning = true;
            CalibrationProgressBar.Value = 0;
            UpdateCommandState();
            SetWorkflowStatus("正在把基准点、偏移和点序写入 VisionMaster…", WorkflowStatus.Running);

            visionPrepared = true;
            _ = await VisionHost.PrepareNinePointCalibrationAsync(
                center.ActualX / PulsesPerVisionUnit,
                center.ActualY / PulsesPerVisionUnit,
                stepX / PulsesPerVisionUnit,
                stepY / PulsesPerVisionUnit,
                xFirst,
                calibrationFilePath,
                cancellationToken);

            var request = new NinePointMotionRequest(
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo,
                center.ActualX,
                center.ActualY,
                stepX,
                stepY,
                movePriority,
                velocity,
                DefaultPositionTolerancePulses,
                moveTimeoutMilliseconds,
                settleMilliseconds);
            var progress = new Progress<NinePointMotionProgress>(progressValue =>
            {
                CalibrationProgressBar.Value = progressValue.CompletedPoints;
                SetWorkflowStatus(progressValue.Message, WorkflowStatus.Running);
            });

            await motionController.RunNinePointCalibrationAsync(
                request,
                async (position, pointCancellationToken) =>
                {
                    SetWorkflowStatus(
                        $"第 {position.Index}/9 点已到位：" +
                        $"X={position.ActualX:0.###}，Y={position.ActualY:0.###}，正在执行流程…",
                        WorkflowStatus.Running);
                    _ = await VisionHost.CaptureCalibrationPointAsync(
                        position.Index,
                        pointCancellationToken);
                },
                progress,
                cancellationToken);

            var completionMessage = await VisionHost.CompleteNinePointCalibrationAsync(cancellationToken);
            visionPrepared = false;
            calibrationCompleted = true;
            CalibrationProgressBar.Value = 9;
            SetWorkflowStatus(
                completionMessage + "；轴1/2已回到记录中心，正在启用点击移动。",
                WorkflowStatus.Success);
        }
        catch (OperationCanceledException)
        {
            SetWorkflowStatus("九点标定已停止，轴已下发减速停止命令。", WorkflowStatus.Error);
        }
        catch (Exception exception)
        {
            SetWorkflowStatus($"九点标定失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            if (visionPrepared)
            {
                await VisionHost.AbortNinePointCalibrationAsync();
            }

            _calibrationCancellation?.Dispose();
            _calibrationCancellation = null;
            _calibrationRunning = false;
            UpdateCommandState();
        }

        if (calibrationCompleted)
        {
            SetClickMoveCheckedNoEvent(true);
            _ = await ConfigureClickMoveModeAsync(true);
        }
    }

    private void StopCalibration_Click(object sender, RoutedEventArgs e)
    {
        StopCalibrationButton.IsEnabled = false;
        SetWorkflowStatus("正在停止九点标定…", WorkflowStatus.Running);
        _calibrationCancellation?.Cancel();
    }

    private void ChooseCalibrationFile_Click(object sender, RoutedEventArgs e)
    {
        var currentPath = CalibrationFilePathTextBox.Text;
        var currentDirectory = string.IsNullOrWhiteSpace(currentPath)
            ? null
            : Path.GetDirectoryName(currentPath);
        var dialog = new SaveFileDialog
        {
            Title = "选择标定文件的目录和文件名",
            Filter = "VisionMaster 标定文件 (*.xml)|*.xml|所有文件 (*.*)|*.*",
            AddExtension = true,
            DefaultExt = ".xml",
            OverwritePrompt = false,
            InitialDirectory = Directory.Exists(currentDirectory) ? currentDirectory : null
        };
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            dialog.FileName = Path.GetFileName(currentPath);
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        CalibrationFilePathTextBox.Text = dialog.FileName;
        SetWorkflowStatus("标定文件已设置。", WorkflowStatus.Ready);
        UpdateCommandState();
    }

    private void CalibrationFilePath_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateCommandState();
    }

    private async void EnableClickMove_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressClickMoveModeEvent)
        {
            return;
        }

        var enabled = EnableClickMoveCheckBox.IsChecked == true;
        await ConfigureClickMoveModeAsync(enabled);
    }

    private async Task<bool> ConfigureClickMoveModeAsync(bool enabled)
    {
        if (_clickMoveConfigurationRunning)
        {
            return false;
        }

        _clickMoveConfigurationRunning = true;
        UpdateCommandState();
        try
        {
            if (enabled)
            {
                if (!_hostReady)
                {
                    throw new InvalidOperationException("VisionMaster 视觉组件尚未就绪。");
                }

                _ = _recordedCenter
                    ?? throw new InvalidOperationException("请先记录与该标定文件对应的基准点。");
                if (_calibrationRunning)
                {
                    throw new InvalidOperationException("九点标定正在执行。");
                }

                var path = GetCalibrationFilePath(CalibrationFilePathTextBox.Text);
                if (!File.Exists(path))
                {
                    throw new FileNotFoundException("标定文件不存在，请先选择有效文件。", path);
                }

                var message = await VisionHost.SetClickMoveModeAsync(
                    true,
                    path,
                    CancellationToken.None);
                SetClickMoveStatus(message, WorkflowStatus.Success);
                return true;
            }

            _clickMoveCancellation?.Cancel();
            if (_hostReady)
            {
                var path = string.IsNullOrWhiteSpace(CalibrationFilePathTextBox.Text)
                    ? DefaultCalibrationFilePath
                    : CalibrationFilePathTextBox.Text;
                _ = await VisionHost.SetClickMoveModeAsync(false, path, CancellationToken.None);
            }

            SetClickMoveStatus("点击视觉移动已关闭。", WorkflowStatus.Ready);
            return true;
        }
        catch (Exception exception)
        {
            if (enabled)
            {
                SetClickMoveCheckedNoEvent(false);
            }

            SetClickMoveStatus($"点击移动配置失败：{exception.Message}", WorkflowStatus.Error);
            return false;
        }
        finally
        {
            _clickMoveConfigurationRunning = false;
            UpdateCommandState();
        }
    }

    private async void VisionHost_ClickTargetReceived(object? sender, VisionClickTargetEventArgs e)
    {
        if (EnableClickMoveCheckBox.IsChecked != true || _clickMoveRunning || _calibrationRunning)
        {
            if (_clickMoveRunning)
            {
                SetClickMoveStatus("上一次点击移动尚未完成，本次点击已忽略。", WorkflowStatus.Error);
            }

            return;
        }

        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            var calibrationCenter = _recordedCenter
                ?? throw new InvalidOperationException("请先记录标定基准点。");
            var velocity = ParsePositiveDouble(VelocityTextBox.Text, "点击移动速度");
            var current = motionController.CaptureCalibrationCenter(
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo);
            var baseX = calibrationCenter.ActualX / PulsesPerVisionUnit;
            var baseY = calibrationCenter.ActualY / PulsesPerVisionUnit;
            var deltaX = (e.TransformedX - baseX) * PulsesPerVisionUnit;
            var deltaY = (e.TransformedY - baseY) * PulsesPerVisionUnit;
            var targetX = current.ActualX + deltaX;
            var targetY = current.ActualY + deltaY;
            if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
            {
                throw new InvalidOperationException("标定转换后的轴目标无效。");
            }

            _clickMoveCancellation = new CancellationTokenSource();
            _clickMoveRunning = true;
            UpdateCommandState();
            SetClickMoveStatus(
                $"像素({e.PixelX}, {e.PixelY}) → 标定({e.TransformedX:0.####}, {e.TransformedY:0.####})；" +
                $"正在移动到 X={targetX:0.###}、Y={targetY:0.###} pulse…",
                WorkflowStatus.Running);

            var actual = await motionController.MoveCalibrationAxesToAsync(
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo,
                targetX,
                targetY,
                velocity,
                DefaultPositionTolerancePulses,
                CalculateDirectMoveTimeout(deltaX, deltaY, velocity),
                _clickMoveCancellation.Token);
            SetClickMoveStatus(
                $"点击移动完成：X={actual.ActualX:0.###}、Y={actual.ActualY:0.###} pulse。",
                WorkflowStatus.Success);
        }
        catch (OperationCanceledException)
        {
            SetClickMoveStatus("点击移动已停止，轴1/2已下发减速停止命令。", WorkflowStatus.Error);
        }
        catch (Exception exception)
        {
            SetClickMoveStatus($"点击移动失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _clickMoveCancellation?.Dispose();
            _clickMoveCancellation = null;
            _clickMoveRunning = false;
            UpdateCommandState();
        }
    }

    private void VisionHost_ClickTargetFailed(object? sender, VisionClickTargetFailedEventArgs e)
    {
        if (EnableClickMoveCheckBox.IsChecked == true)
        {
            SetClickMoveStatus($"标定坐标转换失败：{e.Message}", WorkflowStatus.Error);
        }
    }

    private void StopClickMove_Click(object sender, RoutedEventArgs e)
    {
        StopClickMoveButton.IsEnabled = false;
        SetClickMoveStatus("正在停止点击移动…", WorkflowStatus.Running);
        _clickMoveCancellation?.Cancel();
    }

    private void SetClickMoveCheckedNoEvent(bool value)
    {
        _suppressClickMoveModeEvent = true;
        try
        {
            EnableClickMoveCheckBox.IsChecked = value;
        }
        finally
        {
            _suppressClickMoveModeEvent = false;
        }
    }

    private async void RestartHost_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationRunning)
        {
            return;
        }

        _hostReady = false;
        _hostCanRestart = false;
        RestartHostButton.IsEnabled = false;
        HostPlaceholder.Visibility = Visibility.Visible;
        SetHostStatus("正在重启视觉组件…", HostStatus.Starting);
        UpdateCommandState();
        await VisionHost.RestartAsync();
    }

    private async void VisionHost_Started(object? sender, EventArgs e)
    {
        _hostReady = true;
        _hostCanRestart = true;
        HostPlaceholder.Visibility = Visibility.Collapsed;
        RestartHostButton.IsEnabled = true;
        SetHostStatus("视觉组件已启动", HostStatus.Ready);
        UpdateCommandState();
        if (EnableClickMoveCheckBox.IsChecked == true)
        {
            await ConfigureClickMoveModeAsync(true);
        }
    }

    private void VisionHost_Failed(object? sender, VisionMasterHostFailedEventArgs e)
    {
        _hostReady = false;
        _hostCanRestart = true;
        _calibrationCancellation?.Cancel();
        _clickMoveCancellation?.Cancel();
        SetClickMoveCheckedNoEvent(false);
        HostPlaceholder.Visibility = Visibility.Visible;
        RestartHostButton.IsEnabled = true;
        SetHostStatus($"视觉组件启动失败：{e.Message}", HostStatus.Error);
        UpdateCommandState();
    }

    private void VisionHost_Exited(object? sender, EventArgs e)
    {
        _hostReady = false;
        _hostCanRestart = true;
        _calibrationCancellation?.Cancel();
        _clickMoveCancellation?.Cancel();
        SetClickMoveCheckedNoEvent(false);
        if (_shutdown)
        {
            return;
        }

        HostPlaceholder.Visibility = Visibility.Visible;
        RestartHostButton.IsEnabled = true;
        SetHostStatus("视觉组件已退出，可点击重启。", HostStatus.Error);
        UpdateCommandState();
    }

    private void CalibrationInput_Changed(object sender, TextChangedEventArgs e)
    {
        UpdateVisionOffsetPreview();
    }

    private void UpdateVisionOffsetPreview()
    {
        if (OffsetVmText is null)
        {
            return;
        }

        if (TryParseFiniteDouble(StepXPulsesTextBox?.Text, out var stepX) && stepX > 0 &&
            TryParseFiniteDouble(StepYPulsesTextBox?.Text, out var stepY) && stepY > 0)
        {
            OffsetVmText.Text =
                $"VisionMaster 间距 X：{stepX / PulsesPerVisionUnit:0.####}　" +
                $"Y：{stepY / PulsesPerVisionUnit:0.####}";
            OffsetVmText.Foreground = new SolidColorBrush(Color.FromRgb(73, 209, 125));
            return;
        }

        OffsetVmText.Text = "X/Y 标定间距必须大于 0";
        OffsetVmText.Foreground = new SolidColorBrush(Color.FromRgb(242, 122, 128));
    }

    private void UpdateCommandState()
    {
        if (RecordCenterButton is null || CalibrationFilePathTextBox is null)
        {
            return;
        }

        var calibrationPathValid = TryGetCalibrationFilePath(
            CalibrationFilePathTextBox.Text,
            out var calibrationFilePath);

        RecordCenterButton.IsEnabled =
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null;
        StartCalibrationButton.IsEnabled =
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null &&
            _recordedCenter is not null &&
            _hostReady &&
            calibrationPathValid;
        StopCalibrationButton.IsEnabled = _calibrationRunning;
        RestartHostButton.IsEnabled =
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            _hostCanRestart;
        StepXPulsesTextBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning;
        StepYPulsesTextBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning;
        VelocityTextBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning;
        SettleMillisecondsTextBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning;
        MovePriorityComboBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning;
        CalibrationFilePathTextBox.IsEnabled =
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        ChooseCalibrationFileButton.IsEnabled =
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        EnableClickMoveCheckBox.IsEnabled =
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            _motionController is not null &&
            _recordedCenter is not null &&
            _hostReady &&
            calibrationPathValid &&
            File.Exists(calibrationFilePath);
        StopClickMoveButton.IsEnabled = _clickMoveRunning;
    }

    private void SetHostStatus(string message, HostStatus status)
    {
        HostStatusText.Text = message;
        HostStatusIndicator.Fill = new SolidColorBrush(status switch
        {
            HostStatus.Ready => Color.FromRgb(57, 197, 107),
            HostStatus.Error => Color.FromRgb(217, 13, 22),
            _ => Color.FromRgb(224, 162, 26)
        });
    }

    private void SetWorkflowStatus(string message, WorkflowStatus status)
    {
        WorkflowStatusText.Text = message;
        WorkflowStatusText.Foreground = new SolidColorBrush(status switch
        {
            WorkflowStatus.Success => Color.FromRgb(73, 209, 125),
            WorkflowStatus.Error => Color.FromRgb(242, 122, 128),
            WorkflowStatus.Running => Color.FromRgb(88, 165, 255),
            _ => Color.FromRgb(216, 228, 236)
        });
    }

    private void SetClickMoveStatus(string message, WorkflowStatus status)
    {
        ClickMoveStatusText.Text = message;
        ClickMoveStatusText.Foreground = new SolidColorBrush(status switch
        {
            WorkflowStatus.Success => Color.FromRgb(73, 209, 125),
            WorkflowStatus.Error => Color.FromRgb(242, 122, 128),
            WorkflowStatus.Running => Color.FromRgb(88, 165, 255),
            _ => Color.FromRgb(143, 178, 201)
        });
    }

    private static double ParsePositiveDouble(string? value, string name)
    {
        if (!TryParseFiniteDouble(value, out var result) || result <= 0)
        {
            throw new InvalidDataException($"{name}必须大于 0。");
        }

        return result;
    }

    private static int ParseNonNegativeInt(string? value, string name)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.CurrentCulture, out var result) || result < 0)
        {
            throw new InvalidDataException($"{name}必须是大于或等于 0 的整数。");
        }

        return result;
    }

    private static string GetCalibrationFilePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException("请设置标定文件名和保存位置。");
        }

        var fullPath = Path.GetFullPath(value.Trim());
        if (!string.Equals(Path.GetExtension(fullPath), ".xml", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("标定文件必须使用 .xml 扩展名。");
        }

        if (string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(fullPath)) ||
            string.IsNullOrWhiteSpace(Path.GetDirectoryName(fullPath)))
        {
            throw new InvalidDataException("标定文件路径无效。");
        }

        return fullPath;
    }

    private static bool TryGetCalibrationFilePath(string? value, out string fullPath)
    {
        try
        {
            fullPath = GetCalibrationFilePath(value);
            return true;
        }
        catch
        {
            fullPath = "";
            return false;
        }
    }

    private static bool TryParseFiniteDouble(string? value, out double result)
    {
        var parsed = double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out result) ||
                     double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out result);
        return parsed && double.IsFinite(result);
    }

    private static int CalculateMoveTimeout(double stepX, double stepY, double velocity)
    {
        var longestMovePulses = Math.Max(Math.Abs(stepX), Math.Abs(stepY)) * 2;
        var estimatedMilliseconds = longestMovePulses / velocity * 1000;
        return (int)Math.Clamp(estimatedMilliseconds + 15_000, 15_000, 180_000);
    }

    private static int CalculateDirectMoveTimeout(double deltaX, double deltaY, double velocity)
    {
        var longestMovePulses = Math.Max(Math.Abs(deltaX), Math.Abs(deltaY));
        var estimatedMilliseconds = longestMovePulses / velocity * 1000;
        return (int)Math.Clamp(estimatedMilliseconds + 15_000, 15_000, 180_000);
    }

    private enum HostStatus
    {
        Starting,
        Ready,
        Error
    }

    private enum WorkflowStatus
    {
        Ready,
        Running,
        Success,
        Error
    }
}
