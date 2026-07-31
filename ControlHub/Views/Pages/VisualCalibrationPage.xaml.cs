using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.Services.Vision;
using ControlHub.Views.Controls;
using Microsoft.Win32;
using System.Windows.Threading;

namespace ControlHub.Views.Pages;

internal enum VisualCalibrationMode
{
    First,
    Second,
    LowerCamera
}

public partial class VisualCalibrationPage : UserControl
{
    private const double PulsesPerVisionUnit = VisionCalibrationService.PulsesPerVisionUnit;
    private const double DefaultPositionTolerancePulses = 10d;
    private const int LowerCameraNozzle1RotationAxisNo = 6;
    private const int LowerCameraNozzle2RotationAxisNo = 8;
    private const double RotationCenterStepPulses = 10_000d;
    private const double ArrivalPositionVelocityPulsesPerSecond = 100_000d;
    private static readonly string DefaultCalibrationDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "标定文件");
    private static readonly string DefaultCalibrationFilePath = Path.Combine(
        DefaultCalibrationDirectory,
        "第一套XY标定.xml");
    private static readonly string DefaultSecondCalibrationFilePath = Path.Combine(
        DefaultCalibrationDirectory,
        "第二套XY标定.xml");
    private static readonly string DefaultLowerCameraNozzle1CalibrationFilePath = Path.Combine(
        DefaultCalibrationDirectory,
        "下相机吸嘴1标定.xml");
    private static readonly string DefaultLowerCameraNozzle2CalibrationFilePath = Path.Combine(
        DefaultCalibrationDirectory,
        "下相机吸嘴2标定.xml");
    private static readonly string DefaultLowerCameraNozzle1TeachDataFilePath = Path.Combine(
        DefaultCalibrationDirectory,
        "下相机吸嘴1示教数据.json");
    private static readonly string DefaultLowerCameraNozzle2TeachDataFilePath = Path.Combine(
        DefaultCalibrationDirectory,
        "下相机吸嘴2示教数据.json");
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private readonly VisionCalibrationProfileStore _profileStore = new();
    private readonly LowerCameraTeachDataStore _lowerCameraTeachDataStore = new();
    private readonly DispatcherTimer _settingsSaveTimer = new()
    {
        Interval = TimeSpan.FromMilliseconds(500)
    };
    private bool _startRequested;
    private bool _shutdown;
    private bool _hostReady;
    private bool _hostCanRestart;
    private bool _calibrationViewRequested;
    private bool _calibrationRunning;
    private bool _centerSyncRunning;
    private bool _clickMoveRunning;
    private bool _clickMoveConfigurationRunning;
    private bool _livePreviewStarting;
    private bool _calibrationFileImporting;
    private bool _calibrationToolbarSyncRunning;
    private bool _calibrationToolbarSyncPending;
    private bool _calibrationSidebarSyncRunning;
    private bool _calibrationSidebarSyncPending;
    private bool _calibrationProcedureSwitchRunning;
    private bool _rotationCenterRunning;
    private bool _lowerCameraTeachRunning;
    private bool _lowerCameraCorrectionTestRunning;
    private bool _nozzle1ClickVerified;
    private bool _nozzle2ClickVerified;
    private bool _suppressClickMoveModeEvent;
    private bool _suppressLowerCameraNozzleEvent;
    private MotionControlPage? _motionController;
    private CalibrationCenterPosition? _recordedCenter;
    private CalibrationCenterPosition? _nozzleDotPosition;
    private VisionRectangleBlobResult? _pendingNozzlePointResult;
    private bool _nozzlePointFinding;
    private bool _nozzlePointSaving;
    private CancellationTokenSource? _calibrationCancellation;
    private CancellationTokenSource? _clickMoveCancellation;
    private CancellationTokenSource? _rotationCenterCancellation;
    private CancellationTokenSource? _lowerCameraTeachCancellation;
    private CancellationTokenSource? _lowerCameraCorrectionTestCancellation;
    private string _rotationCenterStatus = "九点标定完成后可计算旋转中心。";
    private string _lowerCameraTeachStatus = "旋转中心完成后可执行下相机示教。";
    private string _lowerCameraCorrectionTestStatus = "示教数据保存后可执行纠偏测试。";
    private VisualCalibrationSettings _uiSettings = VisionCalibrationService.Shared.Settings;
    private bool _settingsLoaded;

    private VisionCalibrationAxisPair ActiveAxisPair => _visionCalibration.ActiveAxisPair;

    private VisionCalibrationAxisSet ActiveAxisSet => _visionCalibration.ActiveAxisSet;

    private VisualCalibrationMode ActiveCalibrationMode
    {
        get => ParseCalibrationMode(_uiSettings.ActiveCalibrationMode, ActiveAxisSet);
        set
        {
            _uiSettings.ActiveCalibrationMode = ToSettingsValue(value);
            _visionCalibration.ActiveAxisSet = value == VisualCalibrationMode.Second
                ? VisionCalibrationAxisSet.Second
                : VisionCalibrationAxisSet.First;
        }
    }

    private bool IsLowerCameraMode => ActiveCalibrationMode == VisualCalibrationMode.LowerCamera;

    private int ActiveLowerCameraNozzle
    {
        get => _uiSettings.LowerCameraActiveNozzle == 2 ? 2 : 1;
        set => _uiSettings.LowerCameraActiveNozzle = value == 2 ? 2 : 1;
    }

    private string ActiveLowerCameraNozzleName => $"吸嘴{ActiveLowerCameraNozzle}";

    public VisualCalibrationPage()
    {
        InitializeComponent();
        _settingsSaveTimer.Tick += SettingsSaveTimer_Tick;
        Unloaded += VisualCalibrationPage_Unloaded;
        EnsureDefaultCalibrationDirectory();
        ActiveCalibrationMode = ParseCalibrationMode(_uiSettings.ActiveCalibrationMode, ActiveAxisSet);
        LoadCalibrationSettings();
        _settingsLoaded = true;
        UpdateVisionOffsetPreview();
        UpdateCommandState();
    }

    public void AttachMotionController(MotionControlPage motionController)
    {
        _motionController = motionController ?? throw new ArgumentNullException(nameof(motionController));
        UpdateCommandState();
    }

    private void ArrivalPosition_Changed(object sender, TextChangedEventArgs e)
    {
        ScheduleCalibrationSettingsSave();
        UpdateCommandState();
    }

    private async void MoveToArrivalPosition1_Click(object sender, RoutedEventArgs e)
    {
        await MoveToArrivalPositionAsync(
            1,
            ArrivalPosition1XTextBox.Text,
            ArrivalPosition1YTextBox.Text);
    }

    private async void MoveToArrivalPosition2_Click(object sender, RoutedEventArgs e)
    {
        await MoveToArrivalPositionAsync(
            2,
            ArrivalPosition2XTextBox.Text,
            ArrivalPosition2YTextBox.Text);
    }

    private async Task MoveToArrivalPositionAsync(int positionNumber, string xText, string yText)
    {
        if (_clickMoveRunning || _calibrationRunning || _centerSyncRunning ||
            _calibrationProcedureSwitchRunning || _rotationCenterRunning || _lowerCameraTeachRunning ||
            _lowerCameraCorrectionTestRunning)
        {
            return;
        }

        try
        {
            if (!IsLowerCameraMode)
            {
                throw new InvalidOperationException("到位点仅用于下相机标定。");
            }

            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            if (!TryParseFiniteDouble(xText, out var targetX) ||
                !TryParseFiniteDouble(yText, out var targetY))
            {
                throw new InvalidOperationException($"请先配置到位{positionNumber}的有效X/Y坐标。");
            }

            SaveCalibrationSettingsNoThrow();
            var current = motionController.CaptureCalibrationCenter(
                VisionCalibrationService.FirstSetXHardwareAxisNo,
                VisionCalibrationService.FirstSetYHardwareAxisNo);
            _clickMoveCancellation = new CancellationTokenSource();
            _clickMoveRunning = true;
            UpdateCommandState();
            SetWorkflowStatus(
                $"正在以速度100000移动到到位{positionNumber}：X={targetX:0.###}、Y={targetY:0.###} pulse…",
                WorkflowStatus.Running);

            var actual = await motionController.MoveCalibrationAxesToAsync(
                VisionCalibrationService.FirstSetXHardwareAxisNo,
                VisionCalibrationService.FirstSetYHardwareAxisNo,
                targetX,
                targetY,
                ArrivalPositionVelocityPulsesPerSecond,
                DefaultPositionTolerancePulses,
                CalculateDirectMoveTimeout(
                    targetX - current.ActualX,
                    targetY - current.ActualY,
                    ArrivalPositionVelocityPulsesPerSecond),
                _clickMoveCancellation.Token);
            SetWorkflowStatus(
                $"到位{positionNumber}移动完成：X={actual.ActualX:0.###}、Y={actual.ActualY:0.###} pulse。",
                WorkflowStatus.Success);
        }
        catch (OperationCanceledException)
        {
            SetWorkflowStatus($"到位{positionNumber}移动已停止。", WorkflowStatus.Error);
        }
        catch (Exception exception)
        {
            SetWorkflowStatus($"到位{positionNumber}移动失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _clickMoveCancellation?.Dispose();
            _clickMoveCancellation = null;
            _clickMoveRunning = false;
            UpdateCommandState();
        }
    }

    private void CalibrationEmergencyStop_Click(object sender, RoutedEventArgs e)
    {
        var motionController = _motionController;
        if (motionController is null)
        {
            SetWorkflowStatus("运动控制未连接，无法下发全轴急停。", WorkflowStatus.Error);
            return;
        }

        _calibrationCancellation?.Cancel();
        _clickMoveCancellation?.Cancel();
        _rotationCenterCancellation?.Cancel();
        _lowerCameraTeachCancellation?.Cancel();
        _lowerCameraCorrectionTestCancellation?.Cancel();
        var issued = motionController.EmergencyStopAllAxes("视觉标定页操作员请求全轴急停");
        SetWorkflowStatus(
            issued ? "全轴急停已下发，正在确认所有轴停止。" : "急停下发失败，请立即按硬件急停。",
            issued ? WorkflowStatus.Running : WorkflowStatus.Error);
    }

    private async void AxisSet_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        if (_calibrationRunning || _clickMoveRunning || _centerSyncRunning ||
            _calibrationProcedureSwitchRunning || _rotationCenterRunning || _lowerCameraTeachRunning ||
            _lowerCameraCorrectionTestRunning)
        {
            AxisSetComboBox.SelectedItem = AxisSetComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    ToSettingsValue(ActiveCalibrationMode),
                    StringComparison.Ordinal));
            return;
        }

        SaveCalibrationSettingsNoThrow();
        var requestedMode = ParseCalibrationMode(
            (AxisSetComboBox.SelectedItem as ComboBoxItem)?.Tag as string,
            ActiveAxisSet);
        if (requestedMode == VisualCalibrationMode.LowerCamera &&
            EnableClickMoveCheckBox.IsChecked == true)
        {
            SetClickMoveCheckedNoEvent(false);
            await ConfigureClickMoveModeAsync(false);
        }

        ActiveCalibrationMode = requestedMode;
        _recordedCenter = null;
        _nozzleDotPosition = null;
        _pendingNozzlePointResult = null;
        _nozzle1ClickVerified = false;
        _nozzle2ClickVerified = false;
        LoadCalibrationSettings();
        if (IsLowerCameraMode)
        {
            SetWorkflowStatus(
                $"下相机模式 · {ActiveLowerCameraNozzleName}：本次只标一个吸嘴，完成后再切换另一个吸嘴。",
                WorkflowStatus.Ready);
            SetClickMoveStatus($"请先完成{ActiveLowerCameraNozzleName}的下相机九点标定。", WorkflowStatus.Ready);
        }
        _visionCalibration.Save();
        UpdateVisionOffsetPreview();
        UpdateCommandState();
        await SwitchCalibrationProcedureAsync();
    }

    private async void LowerCameraNozzle_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded || _suppressLowerCameraNozzleEvent || !IsLowerCameraMode)
        {
            return;
        }

        if (_calibrationRunning || _clickMoveRunning || _centerSyncRunning ||
            _calibrationProcedureSwitchRunning || _rotationCenterRunning || _lowerCameraTeachRunning ||
            _lowerCameraCorrectionTestRunning)
        {
            SelectLowerCameraNozzleComboBox();
            return;
        }

        SaveCalibrationSettingsNoThrow();
        ActiveLowerCameraNozzle =
            (LowerCameraNozzleComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "2" ? 2 : 1;
        _recordedCenter = null;
        CalibrationProgressBar.Value = 0;
        LoadCalibrationSettings();
        _visionCalibration.Save();
        SetWorkflowStatus(
            $"已切换到下相机{ActiveLowerCameraNozzleName}标定；本次九点只保存这个吸嘴。",
            WorkflowStatus.Ready);
        SetClickMoveStatus($"请先完成{ActiveLowerCameraNozzleName}九点标定。", WorkflowStatus.Ready);
        UpdateCommandState();
        await SwitchCalibrationProcedureAsync();
    }

    private void SelectLowerCameraNozzleComboBox()
    {
        if (LowerCameraNozzleComboBox is null || LowerCameraNozzleLabel is null)
        {
            return;
        }

        var visibility = IsLowerCameraMode ? Visibility.Visible : Visibility.Collapsed;
        LowerCameraNozzleLabel.Visibility = visibility;
        LowerCameraNozzleComboBox.Visibility = visibility;
        LowerCameraArrivalPanel.Visibility = visibility;
        ClickMoveSection.Visibility = IsLowerCameraMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        _suppressLowerCameraNozzleEvent = true;
        try
        {
            var value = ActiveLowerCameraNozzle.ToString(CultureInfo.InvariantCulture);
            LowerCameraNozzleComboBox.SelectedItem = LowerCameraNozzleComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.Ordinal));
        }
        finally
        {
            _suppressLowerCameraNozzleEvent = false;
        }
    }

    private async Task SwitchCalibrationProcedureAsync()
    {
        if (!_hostReady || !_calibrationViewRequested || _shutdown || _calibrationProcedureSwitchRunning)
        {
            return;
        }

        _calibrationProcedureSwitchRunning = true;
        UpdateCommandState();
        try
        {
            var message = await VisionHost.SetCalibrationProcedureAsync(
                IsLowerCameraMode,
                CancellationToken.None);
            SetHostStatus(message, HostStatus.Ready);
        }
        catch (Exception exception)
        {
            SetWorkflowStatus($"切换视觉标定流程失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _calibrationProcedureSwitchRunning = false;
            UpdateCommandState();
        }
    }

    private void SelectAxisSetComboBox()
    {
        if (AxisSetComboBox is null)
        {
            return;
        }

        var value = ToSettingsValue(ActiveCalibrationMode);
        AxisSetComboBox.SelectedItem = AxisSetComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.Ordinal))
            ?? AxisSetComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private string GetDefaultCalibrationFilePath()
    {
        if (IsLowerCameraMode)
        {
            return GetLowerCameraCalibrationFilePath(ActiveLowerCameraNozzle);
        }

        return ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? DefaultSecondCalibrationFilePath
            : DefaultCalibrationFilePath;
    }

    private string GetLowerCameraCalibrationFilePath(int nozzleNumber)
    {
        if (nozzleNumber == 2)
        {
            return TryGetCalibrationFilePath(
                _uiSettings.LowerCameraNozzle2CalibrationFilePath,
                out var nozzle2Path)
                    ? nozzle2Path
                    : DefaultLowerCameraNozzle2CalibrationFilePath;
        }

        var configuredPath = string.IsNullOrWhiteSpace(_uiSettings.LowerCameraNozzle1CalibrationFilePath)
            ? _uiSettings.LowerCameraCalibrationFilePath
            : _uiSettings.LowerCameraNozzle1CalibrationFilePath;
        return TryGetCalibrationFilePath(configuredPath, out var nozzle1Path)
            ? nozzle1Path
            : DefaultLowerCameraNozzle1CalibrationFilePath;
    }

    private static VisualCalibrationMode ParseCalibrationMode(
        string? value,
        VisionCalibrationAxisSet fallbackAxisSet)
    {
        return value?.Trim() switch
        {
            "LowerCamera" or "Lower" => VisualCalibrationMode.LowerCamera,
            "Second" or "Axis34" or "2" => VisualCalibrationMode.Second,
            "First" or "Axis12" or "1" => VisualCalibrationMode.First,
            _ => fallbackAxisSet == VisionCalibrationAxisSet.Second
                ? VisualCalibrationMode.Second
                : VisualCalibrationMode.First
        };
    }

    private static string ToSettingsValue(VisualCalibrationMode mode)
    {
        return mode switch
        {
            VisualCalibrationMode.Second => "Second",
            VisualCalibrationMode.LowerCamera => "LowerCamera",
            _ => "First"
        };
    }

    private void ResetActiveNozzleCalibration()
    {
        if (IsLowerCameraMode)
        {
            return;
        }

        if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
        {
            _uiSettings.SecondNozzleOffsetCalibrated = false;
            _uiSettings.SecondNozzle2OffsetCalibrated = false;
            _uiSettings.SecondCalibrationProfilePath = "";
        }
        else
        {
            _uiSettings.NozzleOffsetCalibrated = false;
            _uiSettings.Nozzle2OffsetCalibrated = false;
            _uiSettings.CalibrationProfilePath = "";
        }
    }

    private void SetActiveNozzleOffset(VisionTargetTool nozzleTool, double offsetX, double offsetY)
    {
        if (IsLowerCameraMode)
        {
            throw new InvalidOperationException("下相机标定不使用吸嘴粗定位示教。");
        }

        if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
        {
            if (nozzleTool == VisionTargetTool.Nozzle2)
            {
                _uiSettings.SecondNozzle2OffsetXPulses = offsetX;
                _uiSettings.SecondNozzle2OffsetYPulses = offsetY;
                _uiSettings.SecondNozzle2OffsetCalibrated = true;
            }
            else
            {
                _uiSettings.SecondNozzleOffsetXPulses = offsetX;
                _uiSettings.SecondNozzleOffsetYPulses = offsetY;
                _uiSettings.SecondNozzleOffsetCalibrated = true;
                _uiSettings.SecondNozzle2OffsetCalibrated = false;
            }

            return;
        }

        if (nozzleTool == VisionTargetTool.Nozzle2)
        {
            _uiSettings.Nozzle2OffsetXPulses = offsetX;
            _uiSettings.Nozzle2OffsetYPulses = offsetY;
            _uiSettings.Nozzle2OffsetCalibrated = true;
        }
        else
        {
            _uiSettings.NozzleOffsetXPulses = offsetX;
            _uiSettings.NozzleOffsetYPulses = offsetY;
            _uiSettings.NozzleOffsetCalibrated = true;
            _uiSettings.Nozzle2OffsetCalibrated = false;
        }
    }

    private bool ActiveNozzle1Calibrated => !IsLowerCameraMode && ActiveAxisSet == VisionCalibrationAxisSet.Second
        ? _uiSettings.SecondNozzleOffsetCalibrated
        : !IsLowerCameraMode && _uiSettings.NozzleOffsetCalibrated;

    private bool ActiveNozzle2Calibrated => !IsLowerCameraMode && ActiveAxisSet == VisionCalibrationAxisSet.Second
        ? _uiSettings.SecondNozzle2OffsetCalibrated
        : !IsLowerCameraMode && _uiSettings.Nozzle2OffsetCalibrated;

    private double ActiveNozzle1OffsetX => ActiveAxisSet == VisionCalibrationAxisSet.Second
        ? _uiSettings.SecondNozzleOffsetXPulses
        : _uiSettings.NozzleOffsetXPulses;

    private double ActiveNozzle1OffsetY => ActiveAxisSet == VisionCalibrationAxisSet.Second
        ? _uiSettings.SecondNozzleOffsetYPulses
        : _uiSettings.NozzleOffsetYPulses;

    private double ActiveNozzle2OffsetX => ActiveAxisSet == VisionCalibrationAxisSet.Second
        ? _uiSettings.SecondNozzle2OffsetXPulses
        : _uiSettings.Nozzle2OffsetXPulses;

    private double ActiveNozzle2OffsetY => ActiveAxisSet == VisionCalibrationAxisSet.Second
        ? _uiSettings.SecondNozzle2OffsetYPulses
        : _uiSettings.Nozzle2OffsetYPulses;

    private string ActiveCalibrationProfilePath
    {
        get => IsLowerCameraMode
            ? ""
            : ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondCalibrationProfilePath
            : _uiSettings.CalibrationProfilePath;
        set
        {
            if (IsLowerCameraMode)
            {
                return;
            }

            if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
            {
                _uiSettings.SecondCalibrationProfilePath = value;
            }
            else
            {
                _uiSettings.CalibrationProfilePath = value;
            }
        }
    }

    private string ActiveCalibrationFilePath
    {
        get => IsLowerCameraMode
            ? ActiveLowerCameraNozzle == 2
                ? _uiSettings.LowerCameraNozzle2CalibrationFilePath
                : string.IsNullOrWhiteSpace(_uiSettings.LowerCameraNozzle1CalibrationFilePath)
                    ? _uiSettings.LowerCameraCalibrationFilePath
                    : _uiSettings.LowerCameraNozzle1CalibrationFilePath
            : ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondCalibrationFilePath
            : _uiSettings.CalibrationFilePath;
        set
        {
            if (IsLowerCameraMode)
            {
                if (ActiveLowerCameraNozzle == 2)
                {
                    _uiSettings.LowerCameraNozzle2CalibrationFilePath = value;
                }
                else
                {
                    _uiSettings.LowerCameraNozzle1CalibrationFilePath = value;
                    _uiSettings.LowerCameraCalibrationFilePath = value;
                }
            }
            else if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
            {
                _uiSettings.SecondCalibrationFilePath = value;
            }
            else
            {
                _uiSettings.CalibrationFilePath = value;
            }
        }
    }

    private string ActiveClickTargetTool
    {
        get => IsLowerCameraMode
            ? "Camera"
            : ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondClickTargetTool
            : _uiSettings.ClickTargetTool;
        set
        {
            if (IsLowerCameraMode)
            {
                return;
            }

            if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
            {
                _uiSettings.SecondClickTargetTool = value;
            }
            else
            {
                _uiSettings.ClickTargetTool = value;
            }
        }
    }

    public void AttachInspectionDisplayHost(IntPtr displayHostWindow)
    {
        VisionHost.AttachDisplayHost(displayHostWindow);
    }

    public async Task ActivateInspectionViewAsync(IntPtr displayHostWindow)
    {
        if (displayHostWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("主页 Blob 显示区域尚未创建。");
        }

        _calibrationViewRequested = false;
        VisionHost.AttachDisplayHost(displayHostWindow);
        await EnsureStartedAsync();
        if (!_hostReady)
        {
            throw new InvalidOperationException("VisionMaster 视觉组件尚未就绪。");
        }

        await VisionHost.ActivateInspectionViewAsync(CancellationToken.None);
        VisionHost.RefreshDisplayHost();
    }

    public void UseDefaultVisionDisplay()
    {
        VisionHost.UseDefaultDisplayHost();
    }

    public void RefreshVisionDisplay()
    {
        VisionHost.RefreshDisplayHost();
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

    public async Task ActivateCalibrationViewAsync()
    {
        _calibrationViewRequested = true;
        await EnsureStartedAsync();
        if (!_hostReady)
        {
            throw new InvalidOperationException("视觉组件尚未就绪，无法开启标定界面。");
        }

        await VisionHost.ActivateCalibrationViewAsync(CancellationToken.None);
        await SwitchCalibrationProcedureAsync();
        if (!IsLowerCameraMode && EnableClickMoveCheckBox.IsChecked == true)
        {
            await ConfigureClickMoveModeAsync(true);
        }
    }

    public async Task<bool> DeactivateCalibrationViewAsync()
    {
        if (_calibrationRunning)
        {
            SetWorkflowStatus("九点标定正在执行，请先停止标定再切换菜单。", WorkflowStatus.Error);
            return false;
        }

        _calibrationViewRequested = false;
        if (!_startRequested || !_hostReady)
        {
            return true;
        }

        try
        {
            if (EnableClickMoveCheckBox.IsChecked == true)
            {
                SetClickMoveCheckedNoEvent(false);
                await ConfigureClickMoveModeAsync(false);
            }

            await VisionHost.DeactivateCalibrationViewAsync(CancellationToken.None);
        }
        catch
        {
            // 页面切换不能因视觉进程刚好退出而中断主界面导航。
        }

        return true;
    }

    public async Task<VisionPixelTransformResult> TransformPixelAsync(
        double pixelX,
        double pixelY,
        string calibrationFilePath,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync();
        if (!_hostReady)
        {
            throw new InvalidOperationException(
                "视觉组件尚未就绪，请先进入视觉标定页确认实时相机和标定流程已启动。");
        }

        return await VisionHost.TransformPixelAsync(
            pixelX,
            pixelY,
            calibrationFilePath,
            cancellationToken);
    }

    /// <summary>
    /// 在启动时已加载的固定方案中执行“找芯片流程 → Blob分析1”，
    /// 返回前两个结果的像素质心。
    /// </summary>
    public async Task<VisionRectangleBlobResult> RunRectangleBlobInspectionAsync(
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync();
        if (!_hostReady)
        {
            throw new InvalidOperationException(
                "视觉组件尚未就绪，无法运行找芯片流程。");
        }

        return await VisionHost.RunRectangleBlobInspectionAsync(cancellationToken);
    }

    public async Task<VisionLowerCameraCorrectionResult> RunLowerCameraCorrectionAsync(
        LowerCameraTeachData teachData,
        string calibrationFilePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(teachData);
        teachData.Validate();
        ArgumentException.ThrowIfNullOrWhiteSpace(calibrationFilePath);
        await EnsureStartedAsync();
        if (!_hostReady)
        {
            throw new InvalidOperationException("视觉组件尚未就绪，无法运行下相机纠偏流程。");
        }

        return await VisionHost.RunLowerCameraCorrectionAsync(
            teachData.LineStartX,
            teachData.LineStartY,
            teachData.LineEndX,
            teachData.LineEndY,
            teachData.CircleCenterX,
            teachData.CircleCenterY,
            teachData.TransformedX,
            teachData.TransformedY,
            calibrationFilePath,
            cancellationToken);
    }

    public void Shutdown()
    {
        if (_shutdown)
        {
            return;
        }

        _shutdown = true;
        SaveCalibrationSettingsNoThrow();
        _calibrationCancellation?.Cancel();
        _clickMoveCancellation?.Cancel();
        _rotationCenterCancellation?.Cancel();
        _lowerCameraTeachCancellation?.Cancel();
        _lowerCameraCorrectionTestCancellation?.Cancel();
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
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo);
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

    private void RecordNozzleDotPosition_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            _nozzleDotPosition = motionController.CaptureCalibrationCenter(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo);
            _pendingNozzlePointResult = null;
            ResetActiveNozzleCalibration();
            _nozzle1ClickVerified = false;
            _nozzle2ClickVerified = false;
            SaveCalibrationSettingsNoThrow();
            SetNozzleCalibrationStatus(
                $"双吸嘴公共下压位置已记录：X={_nozzleDotPosition.ActualX:0.###}、" +
                $"Y={_nozzleDotPosition.ActualY:0.###} pulse。打点完成后请回拍照位。",
                WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            _nozzleDotPosition = null;
            SetNozzleCalibrationStatus($"记录双吸嘴下压XY失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            UpdateCommandState();
        }
    }

    private async void FindNozzlePoints_Click(object sender, RoutedEventArgs e)
    {
        if (_nozzlePointFinding || _nozzlePointSaving)
        {
            return;
        }

        try
        {
            var calibrationFilePath = GetCalibrationFilePath(CalibrationFilePathTextBox.Text);
            if (!File.Exists(calibrationFilePath))
            {
                throw new FileNotFoundException("九点标定文件不存在，请先完成九点标定。", calibrationFilePath);
            }

            if (!_hostReady)
            {
                throw new InvalidOperationException("视觉组件尚未就绪。");
            }

            var photoPosition = _recordedCenter
                ?? throw new InvalidOperationException("请先记录拍照位并完成九点标定。");
            _ = _nozzleDotPosition
                ?? throw new InvalidOperationException("请先在两个吸嘴同时下压的位置记录一次XY坐标。");
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            var current = motionController.CaptureCalibrationCenter(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo);
            if (Math.Abs(current.ActualX - photoPosition.ActualX) > DefaultPositionTolerancePulses ||
                Math.Abs(current.ActualY - photoPosition.ActualY) > DefaultPositionTolerancePulses)
            {
                throw new InvalidOperationException("当前不在拍照位，请先点击“回拍照位”。");
            }

            if (EnableClickMoveCheckBox.IsChecked == true)
            {
                SetClickMoveCheckedNoEvent(false);
                _ = await ConfigureClickMoveModeAsync(false);
            }

            _nozzlePointFinding = true;
            _pendingNozzlePointResult = null;
            ResetActiveNozzleCalibration();
            _nozzle1ClickVerified = false;
            _nozzle2ClickVerified = false;
            UpdateNozzleTeachUi();
            UpdateCommandState();
            SetNozzleCalibrationStatus("正在执行“粗定位示教流程”查找两个点…", WorkflowStatus.Running);

            _pendingNozzlePointResult = await VisionHost.RunNozzlePointInspectionAsync(CancellationToken.None);
            var point1 = _pendingNozzlePointResult.Rectangle1;
            var point2 = _pendingNozzlePointResult.Rectangle2;
            SetNozzleCalibrationStatus(
                $"点1：({point1.X:0.###}, {point1.Y:0.###})　点2：({point2.X:0.###}, {point2.Y:0.###})\n" +
                "请选择点1对应哪个吸嘴。",
                WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            _pendingNozzlePointResult = null;
            SetNozzleCalibrationStatus($"粗定位示教失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _nozzlePointFinding = false;
            UpdateCommandState();
        }
    }

    private async void AssignPoint1ToNozzle1_Click(object sender, RoutedEventArgs e)
    {
        await AssignNozzlePointsAsync(firstPointIsNozzle1: true);
    }

    private async void AssignPoint1ToNozzle2_Click(object sender, RoutedEventArgs e)
    {
        await AssignNozzlePointsAsync(firstPointIsNozzle1: false);
    }

    private async Task AssignNozzlePointsAsync(bool firstPointIsNozzle1)
    {
        if (_nozzlePointFinding || _nozzlePointSaving)
        {
            return;
        }

        try
        {
            var result = _pendingNozzlePointResult
                ?? throw new InvalidOperationException("请先手动点击“执行粗定位示教”。");
            var photoPosition = _recordedCenter
                ?? throw new InvalidOperationException("拍照位XY尚未记录。");
            var nozzleDotPosition = _nozzleDotPosition
                ?? throw new InvalidOperationException("双吸嘴公共下压XY尚未记录。");
            var calibrationFilePath = GetCalibrationFilePath(CalibrationFilePathTextBox.Text);
            if (!File.Exists(calibrationFilePath))
            {
                throw new FileNotFoundException("九点标定文件不存在，请先完成九点标定。", calibrationFilePath);
            }

            _nozzlePointSaving = true;
            UpdateCommandState();
            SetNozzleCalibrationStatus("正在换算两个吸嘴点并保存配置…", WorkflowStatus.Running);

            var first = await VisionHost.TransformPixelAsync(
                result.Rectangle1.X,
                result.Rectangle1.Y,
                calibrationFilePath,
                CancellationToken.None);
            var second = await VisionHost.TransformPixelAsync(
                result.Rectangle2.X,
                result.Rectangle2.Y,
                calibrationFilePath,
                CancellationToken.None);

            (double X, double Y) CalculateOffset(VisionPixelTransformResult point)
            {
                // 点在公共下压位置生成。先用标定矩阵得到该像素相对图像中心的机械位移，
                // 再叠加“公共下压位置 - 拍照位”，即可得到对应吸嘴相对相机的偏移。
                return (
                    nozzleDotPosition.ActualX - photoPosition.ActualX +
                    (point.TransformedX - point.CenterTransformedX) * PulsesPerVisionUnit,
                    nozzleDotPosition.ActualY - photoPosition.ActualY +
                    (point.TransformedY - point.CenterTransformedY) * PulsesPerVisionUnit);
            }

            var firstOffset = CalculateOffset(first);
            var secondOffset = CalculateOffset(second);
            var nozzle1Offset = firstPointIsNozzle1 ? firstOffset : secondOffset;
            var nozzle2Offset = firstPointIsNozzle1 ? secondOffset : firstOffset;
            if (!double.IsFinite(nozzle1Offset.X) ||
                !double.IsFinite(nozzle1Offset.Y) ||
                !double.IsFinite(nozzle2Offset.X) ||
                !double.IsFinite(nozzle2Offset.Y))
            {
                throw new InvalidOperationException("标定转换得到的吸嘴偏移无效。");
            }

            SetActiveNozzleOffset(VisionTargetTool.Nozzle1, nozzle1Offset.X, nozzle1Offset.Y);
            SetActiveNozzleOffset(VisionTargetTool.Nozzle2, nozzle2Offset.X, nozzle2Offset.Y);
            SelectClickTargetTool(VisionTargetTool.Nozzle1);
            var profilePath = SaveCurrentCalibrationProfile();
            _pendingNozzlePointResult = null;
            SetNozzleCalibrationStatus(
                $"点1已分配给{(firstPointIsNozzle1 ? "吸嘴1" : "吸嘴2")}，双吸嘴配置已自动保存：\n{profilePath}",
                WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            SetNozzleCalibrationStatus($"分配或保存吸嘴点失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _nozzlePointSaving = false;
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
                ?? throw new InvalidOperationException("请先记录标定中心坐标。");
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            if (IsLowerCameraMode &&
                (ActiveAxisPair.XHardwareAxisNo != VisionCalibrationService.FirstSetXHardwareAxisNo ||
                 ActiveAxisPair.YHardwareAxisNo != VisionCalibrationService.FirstSetYHardwareAxisNo))
            {
                throw new InvalidOperationException("下相机标定只允许使用第一套 XY（轴1/2）。");
            }
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
            SaveCalibrationSettingsNoThrow();

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
                IsLowerCameraMode,
                calibrationFilePath,
                cancellationToken);

            var request = new NinePointMotionRequest(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo,
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
                IsLowerCameraMode
                    ? completionMessage + $"；下相机{ActiveLowerCameraNozzleName}九点标定完成。"
                    : completionMessage + "；XY已回到拍照位。请移动到双吸嘴下压位置，同时打点并记录一次公共XY。",
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
            if (IsLowerCameraMode)
            {
                ResetActiveRotationCenter();
                _visionCalibration.Save();
                SetClickMoveCheckedNoEvent(false);
                var otherNozzle = ActiveLowerCameraNozzle == 1 ? 2 : 1;
                var otherCalibrationDone = File.Exists(GetLowerCameraCalibrationFilePath(otherNozzle));
                SetClickMoveStatus(
                    otherCalibrationDone
                        ? $"{ActiveLowerCameraNozzleName}标定完成；两个吸嘴的下相机标定文件都已具备。"
                        : $"{ActiveLowerCameraNozzleName}标定完成。请切换到吸嘴{otherNozzle}，再执行一次九点标定。",
                    WorkflowStatus.Success);
                SetRotationCenterStatus(
                    $"{ActiveLowerCameraNozzleName}九点标定已完成，可以计算旋转中心。",
                    WorkflowStatus.Ready);
                UpdateCommandState();
                return;
            }

            _nozzleDotPosition = null;
            _pendingNozzlePointResult = null;
            ResetActiveNozzleCalibration();
            _nozzle1ClickVerified = false;
            _nozzle2ClickVerified = false;
            _visionCalibration.Save();
            UpdateCalibrationProfilePathDisplay();
            UpdateNozzleTeachUi();
            UpdateNozzleCalibrationDisplay();
            SetClickMoveCheckedNoEvent(false);
            SetNozzleCalibrationStatus(
                "已回到拍照位。请移动到双吸嘴下压位置，让两个吸嘴同时打点并记录一次公共XY。",
                WorkflowStatus.Ready);
        }
    }

    private async void StartLivePreview_Click(object sender, RoutedEventArgs e)
    {
        if (_livePreviewStarting || _calibrationRunning)
        {
            return;
        }

        try
        {
            if (!_hostReady)
            {
                throw new InvalidOperationException("VisionMaster 视觉组件尚未就绪。");
            }

            _livePreviewStarting = true;
            UpdateCommandState();
            SetWorkflowStatus("正在获取实时画面…", WorkflowStatus.Running);
            var message = await VisionHost.StartLivePreviewAsync(CancellationToken.None);
            SetWorkflowStatus(string.IsNullOrWhiteSpace(message) ? "实时画面已启动。" : message, WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            SetWorkflowStatus($"获取实时画面失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _livePreviewStarting = false;
            UpdateCommandState();
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
            InitialDirectory = Directory.Exists(currentDirectory)
                ? currentDirectory
                : DefaultCalibrationDirectory
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
        SaveCalibrationSettingsNoThrow();
        UpdateCommandState();
    }

    private async void ImportCalibrationFile_Click(object sender, RoutedEventArgs e)
    {
        if (_calibrationFileImporting || _calibrationRunning)
        {
            return;
        }

        var currentPath = CalibrationFilePathTextBox.Text;
        var currentDirectory = string.IsNullOrWhiteSpace(currentPath)
            ? null
            : Path.GetDirectoryName(currentPath);
        var dialog = new OpenFileDialog
        {
            Title = "导入已有九点标定文件",
            Filter = "VisionMaster 标定文件 (*.xml)|*.xml|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(currentDirectory)
                ? currentDirectory
                : DefaultCalibrationDirectory
        };
        if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath))
        {
            dialog.FileName = Path.GetFileName(currentPath);
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        _calibrationFileImporting = true;
        UpdateCommandState();
        SetWorkflowStatus("正在导入并应用标定文件…", WorkflowStatus.Running);
        try
        {
            var fullPath = GetCalibrationFilePath(dialog.FileName);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException("选择的标定文件不存在。", fullPath);
            }

            await EnsureStartedAsync();
            if (!_hostReady)
            {
                throw new InvalidOperationException("VisionMaster 视觉组件尚未就绪。");
            }

            var message = await VisionHost.ImportCalibrationFileAsync(
                fullPath,
                CancellationToken.None);
            CalibrationFilePathTextBox.Text = fullPath;
            SaveCalibrationSettingsNoThrow();
            SetWorkflowStatus(message + "；主页开始流程将直接使用此文件。", WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            SetWorkflowStatus($"导入标定文件失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _calibrationFileImporting = false;
            UpdateCommandState();
        }
    }

    private void LoadCalibrationProfile_Click(object sender, RoutedEventArgs e)
    {
        var currentDirectory = Path.GetDirectoryName(ActiveCalibrationProfilePath);
        var dialog = new OpenFileDialog
        {
            Title = "加载第一套XY双吸嘴配置",
            Filter = "双吸嘴标定配置 (*.json)|*.json|所有文件 (*.*)|*.*",
            InitialDirectory = Directory.Exists(currentDirectory)
                ? currentDirectory
                : DefaultCalibrationDirectory
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            var profile = _profileStore.Load(dialog.FileName);
            ApplyCalibrationProfile(profile, dialog.FileName);
            SetWorkflowStatus("双吸嘴配置已加载，可在第4步选择吸嘴点击测试。", WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            SetWorkflowStatus($"加载配置失败：{exception.Message}", WorkflowStatus.Error);
        }

        UpdateCommandState();
    }

    private async void SaveCalibrationProfile_Click(object sender, RoutedEventArgs e)
    {
        var success = false;
        string feedbackMessage;
        try
        {
            var saved = SaveCurrentCalibrationProfile();
            success = true;
            feedbackMessage = $"双吸嘴标定配置已保存：{saved.FilePath}" +
                FormatBackupNotice(saved.BackupFilePath);
            SetWorkflowStatus(feedbackMessage, WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            feedbackMessage = $"保存配置失败：{exception.Message}";
            SetWorkflowStatus(feedbackMessage, WorkflowStatus.Error);
        }

        UpdateCommandState();
        if (_hostReady)
        {
            try
            {
                await VisionHost.ShowCalibrationSaveFeedbackAsync(
                    success,
                    feedbackMessage,
                    CancellationToken.None);
            }
            catch
            {
                // 配置保存结果已经显示在主界面；Host 刚好退出时无需改变保存结果。
            }
        }
    }

    private void CalibrationFilePath_Changed(object sender, TextChangedEventArgs e)
    {
        ScheduleCalibrationSettingsSave();
        if (_settingsLoaded && IsLowerCameraMode)
        {
            RefreshLowerCameraCorrectionTestStatus();
        }

        UpdateCommandState();
    }

    private void VisionHost_CalibrationToolbarActionRequested(
        object? sender,
        CalibrationToolbarActionEventArgs e)
    {
        switch (e.Action)
        {
            case CalibrationToolbarAction.SaveLocation:
                ChooseCalibrationFile_Click(this, new RoutedEventArgs());
                break;
            case CalibrationToolbarAction.Import:
                ImportCalibrationFile_Click(this, new RoutedEventArgs());
                break;
            case CalibrationToolbarAction.LoadProfile:
                LoadCalibrationProfile_Click(this, new RoutedEventArgs());
                break;
            case CalibrationToolbarAction.SaveProfile:
                SaveCalibrationProfile_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private void VisionHost_CalibrationSidebarActionRequested(
        object? sender,
        CalibrationSidebarActionEventArgs e)
    {
        switch (e.Action)
        {
            case "RecordCenter":
                RecordCenter_Click(this, new RoutedEventArgs());
                break;
            case "StartLivePreview":
                StartLivePreview_Click(this, new RoutedEventArgs());
                break;
            case "StartCalibration":
                StartCalibration_Click(this, new RoutedEventArgs());
                break;
            case "StopCalibration":
                StopCalibration_Click(this, new RoutedEventArgs());
                break;
            case "RecordNozzleDotPosition":
                RecordNozzleDotPosition_Click(this, new RoutedEventArgs());
                break;
            case "FindNozzlePoints":
                FindNozzlePoints_Click(this, new RoutedEventArgs());
                break;
            case "AssignPoint1Nozzle1":
                AssignPoint1ToNozzle1_Click(this, new RoutedEventArgs());
                break;
            case "AssignPoint1Nozzle2":
                AssignPoint1ToNozzle2_Click(this, new RoutedEventArgs());
                break;
            case "ReturnCameraCenter":
                ReturnCameraToCenter_Click(this, new RoutedEventArgs());
                break;
            case "StopClickMove":
                StopClickMove_Click(this, new RoutedEventArgs());
                break;
            case "CalculateRotationCenter":
                CalculateRotationCenter_Click(this, new RoutedEventArgs());
                break;
            case "StopRotationCenter":
                StopRotationCenter_Click(this, new RoutedEventArgs());
                break;
            case "RunLowerCameraTeach":
                RunLowerCameraTeach_Click(this, new RoutedEventArgs());
                break;
            case "StopLowerCameraTeach":
                StopLowerCameraTeach_Click(this, new RoutedEventArgs());
                break;
            case "RunLowerCameraCorrectionTest":
                RunLowerCameraCorrectionTest_Click(this, new RoutedEventArgs());
                break;
            case "StopLowerCameraCorrectionTest":
                StopLowerCameraCorrectionTest_Click(this, new RoutedEventArgs());
                break;
            case "ImportLowerCameraTeachData":
                ImportLowerCameraTeachData_Click(this, new RoutedEventArgs());
                break;
            case "StepX":
                StepXPulsesTextBox.Text = e.Value;
                break;
            case "StepY":
                StepYPulsesTextBox.Text = e.Value;
                break;
            case "Velocity":
                VelocityTextBox.Text = e.Value;
                break;
            case "Settle":
                SettleMillisecondsTextBox.Text = e.Value;
                break;
            case "MovePriority":
                MovePriorityComboBox.SelectedItem = MovePriorityComboBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, e.Value, StringComparison.Ordinal));
                break;
            case "ClickTarget":
                ClickTargetToolComboBox.SelectedItem = ClickTargetToolComboBox.Items
                    .OfType<ComboBoxItem>()
                    .FirstOrDefault(item => string.Equals(item.Tag as string, e.Value, StringComparison.Ordinal));
                break;
            case "EnableClickMove":
                var shouldEnable = e.Value == "1";
                if ((EnableClickMoveCheckBox.IsChecked == true) != shouldEnable)
                {
                    EnableClickMoveCheckBox.IsChecked = shouldEnable;
                }
                break;
        }

        ScheduleCalibrationSidebarSync();
    }

    private void CalibrationSetting_Changed(object sender, TextChangedEventArgs e)
    {
        ScheduleCalibrationSettingsSave();
    }

    private void MovePriority_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        ScheduleCalibrationSettingsSave();
    }

    private void ClickTargetTool_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        var targetTool = GetSelectedTargetTool();
        if (!_visionCalibration.IsToolCalibrated(targetTool))
        {
            SetClickMoveStatus(
                $"请先完成第3步粗定位示教，再选择{GetToolDisplayName(targetTool)}。",
                WorkflowStatus.Error);
        }

        ScheduleCalibrationSettingsSave();
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
                if (IsLowerCameraMode)
                {
                    throw new InvalidOperationException("下相机标定不使用“点哪里移动到哪里”。");
                }

                var targetTool = GetSelectedTargetTool();
                if (!IsLowerCameraMode && !_visionCalibration.IsToolCalibrated(targetTool))
                {
                    throw new InvalidOperationException(
                        $"请先完成{GetToolDisplayName(targetTool)}对位标定。");
                }

                if (!_hostReady)
                {
                    throw new InvalidOperationException("VisionMaster 视觉组件尚未就绪。");
                }

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
                    ? GetDefaultCalibrationFilePath()
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

    private async void ReturnCameraToCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_clickMoveRunning || _calibrationRunning || _centerSyncRunning)
        {
            return;
        }

        try
        {
            var center = _recordedCenter
                ?? throw new InvalidOperationException("请先在第一步记录标定中心点。");
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            var velocity = ParsePositiveDouble(VelocityTextBox.Text, "相机回中速度");
            var settleMilliseconds = ParseNonNegativeInt(
                SettleMillisecondsTextBox.Text,
                "到位稳定等待");
            var current = motionController.CaptureCalibrationCenter(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo);
            var moveDeltaX = center.ActualX - current.ActualX;
            var moveDeltaY = center.ActualY - current.ActualY;

            _clickMoveCancellation = new CancellationTokenSource();
            _clickMoveRunning = true;
            UpdateCommandState();
            SetClickMoveStatus(
                $"相机正在回到标定中心：X={center.ActualX:0.###}、Y={center.ActualY:0.###} pulse…",
                WorkflowStatus.Running);

            var actual = await motionController.MoveCalibrationAxesToAsync(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo,
                center.ActualX,
                center.ActualY,
                velocity,
                DefaultPositionTolerancePulses,
                CalculateDirectMoveTimeout(moveDeltaX, moveDeltaY, velocity),
                _clickMoveCancellation.Token);

            if (settleMilliseconds > 0)
            {
                await Task.Delay(settleMilliseconds, _clickMoveCancellation.Token);
            }

            SetClickMoveStatus(
                $"相机已回到标定中心：X={actual.ActualX:0.###}、Y={actual.ActualY:0.###} pulse。",
                WorkflowStatus.Success);
        }
        catch (OperationCanceledException)
        {
            SetClickMoveStatus("相机回中已停止。", WorkflowStatus.Error);
        }
        catch (Exception exception)
        {
            SetClickMoveStatus($"相机回中失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _clickMoveCancellation?.Dispose();
            _clickMoveCancellation = null;
            _clickMoveRunning = false;
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

        var finishNozzleVerification = false;
        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            if (IsLowerCameraMode &&
                (ActiveAxisPair.XHardwareAxisNo != VisionCalibrationService.FirstSetXHardwareAxisNo ||
                 ActiveAxisPair.YHardwareAxisNo != VisionCalibrationService.FirstSetYHardwareAxisNo))
            {
                throw new InvalidOperationException("下相机点选移动只允许驱动第一套 XY（轴1/2）。");
            }
            var velocity = ParsePositiveDouble(VelocityTextBox.Text, "点击移动速度");
            var current = motionController.CaptureCalibrationCenter(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo);
            var deltaX = (e.CenterTransformedX - e.TransformedX) * PulsesPerVisionUnit;
            var deltaY = (e.CenterTransformedY - e.TransformedY) * PulsesPerVisionUnit;
            var targetTool = GetSelectedTargetTool();
            var targetX = current.ActualX + deltaX;
            var targetY = current.ActualY + deltaY;
            if (!IsLowerCameraMode)
            {
                var target = _visionCalibration.CalculateTarget(targetX, targetY, targetTool);
                targetX = target.X;
                targetY = target.Y;
            }
            var targetName = IsLowerCameraMode
                ? $"下相机{ActiveLowerCameraNozzleName}（第一套XY）"
                : GetToolDisplayName(targetTool);

            if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
            {
                throw new InvalidOperationException("标定转换后的轴目标无效。");
            }

            _clickMoveCancellation = new CancellationTokenSource();
            _clickMoveRunning = true;
            UpdateCommandState();
            SetClickMoveStatus(
                IsLowerCameraMode
                    ? $"点击({e.PixelX}, {e.PixelY})；正在移动 XY 到 " +
                      $"X={targetX:0.###}、Y={targetY:0.###} pulse…"
                    : $"点击({e.PixelX}, {e.PixelY})；正在把{targetName}中心移动到 " +
                $"X={targetX:0.###}、Y={targetY:0.###} pulse…",
                WorkflowStatus.Running);

            var moveDeltaX = targetX - current.ActualX;
            var moveDeltaY = targetY - current.ActualY;

            var actual = await motionController.MoveCalibrationAxesToAsync(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo,
                targetX,
                targetY,
                velocity,
                DefaultPositionTolerancePulses,
                CalculateDirectMoveTimeout(moveDeltaX, moveDeltaY, velocity),
                _clickMoveCancellation.Token);
            if (targetTool == VisionTargetTool.Nozzle1)
            {
                _nozzle1ClickVerified = true;
            }
            else if (targetTool == VisionTargetTool.Nozzle2)
            {
                _nozzle2ClickVerified = true;
            }

            finishNozzleVerification =
                targetTool != VisionTargetTool.Camera &&
                _nozzle1ClickVerified &&
                _nozzle2ClickVerified;

            var verificationMessage = targetTool switch
            {
                VisionTargetTool.Camera when IsLowerCameraMode => "点哪里移动到哪里已完成。",
                VisionTargetTool.Camera => "相机中心已对准目标。",
                _ when finishNozzleVerification =>
                    "双吸嘴验证完成，正在退出点击移动。",
                _ when _nozzle1ClickVerified => "请继续验证吸嘴2。",
                _ => "请继续验证吸嘴1。"
            };
            SetClickMoveStatus(
                $"{targetName}移动完成：X={actual.ActualX:0.###}、Y={actual.ActualY:0.###}；" +
                verificationMessage,
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

        if (finishNozzleVerification)
        {
            SetClickMoveCheckedNoEvent(false);
            if (await ConfigureClickMoveModeAsync(false))
            {
                SetClickMoveStatus(
                    "双吸嘴验证完成，请点击顶部“保存配置”。",
                    WorkflowStatus.Success);
            }
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
        SetClickMoveStatus("正在停止运动…", WorkflowStatus.Running);
        _clickMoveCancellation?.Cancel();
    }

    private async void CalculateRotationCenter_Click(object sender, RoutedEventArgs e)
    {
        if (_rotationCenterRunning)
        {
            return;
        }

        var nozzleNumber = ActiveLowerCameraNozzle;
        var rotationAxisNo = nozzleNumber == 1
            ? LowerCameraNozzle1RotationAxisNo
            : LowerCameraNozzle2RotationAxisNo;
        var movedPulses = 0d;
        var rotationMotionFailed = false;
        var finalStatus = "";
        var finalStatusKind = WorkflowStatus.Ready;
        try
        {
            if (!IsLowerCameraMode)
            {
                throw new InvalidOperationException("计算旋转中心只用于下相机标定。");
            }

            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            if (!_hostReady)
            {
                throw new InvalidOperationException("VisionMaster 视觉组件尚未就绪。");
            }

            var calibrationPath = GetCalibrationFilePath(CalibrationFilePathTextBox.Text);
            if (!File.Exists(calibrationPath))
            {
                throw new FileNotFoundException("请先完成当前吸嘴的下相机九点标定。", calibrationPath);
            }

            var settleMilliseconds = ParseNonNegativeInt(
                SettleMillisecondsTextBox.Text,
                "到位稳定等待");
            _rotationCenterCancellation = new CancellationTokenSource();
            var cancellationToken = _rotationCenterCancellation.Token;
            _rotationCenterRunning = true;
            SetRotationCenterStatus(
                $"{ActiveLowerCameraNozzleName}开始采集：R轴{rotationAxisNo}，步距10000 pulse。",
                WorkflowStatus.Running);
            UpdateCommandState();

            var points = new List<VisionRotationPoint>(3);
            for (var pointIndex = 0; pointIndex < 3; pointIndex++)
            {
                SetRotationCenterStatus(
                    $"正在执行“获取三点流程”并读取第{pointIndex + 1}个矩形中心…",
                    WorkflowStatus.Running);
                var point = await VisionHost.CaptureRotationCenterPointAsync(cancellationToken);
                points.Add(point);
                SetRotationCenterStatus(
                    $"第{pointIndex + 1}点：X={point.X:0.###}，Y={point.Y:0.###}",
                    WorkflowStatus.Running);

                if (pointIndex >= 2)
                {
                    continue;
                }

                try
                {
                    SetRotationCenterStatus(
                        $"第{pointIndex + 1}点完成，R轴{rotationAxisNo}正在+10000 pulse…",
                        WorkflowStatus.Running);
                    await motionController.MoveAxisRelativeAsync(
                        rotationAxisNo,
                        RotationCenterStepPulses,
                        cancellationToken,
                        minimumCompletionTolerance: DefaultPositionTolerancePulses);
                    movedPulses += RotationCenterStepPulses;
                }
                catch
                {
                    rotationMotionFailed = true;
                    throw;
                }

                if (settleMilliseconds > 0)
                {
                    await Task.Delay(settleMilliseconds, cancellationToken);
                }
            }

            SetRotationCenterStatus(
                "三个中心点已取得，正在写入X1/Y1、X2/Y2、X3/Y3并执行“计算旋转中心”…",
                WorkflowStatus.Running);
            var result = await VisionHost.CalculateRotationCenterAsync(points, cancellationToken);
            var rotationSettingsBackupPath = _visionCalibration.BackupSettingsBeforeOverwrite();
            SaveActiveRotationCenter(result);
            _visionCalibration.Save();
            SetLowerCameraTeachStatus(
                "旋转中心已取得，正在写入标定文件并自动执行“下相机示教流程”…",
                WorkflowStatus.Running);
            var teachResult = await CaptureAndSaveLowerCameraTeachDataAsync(
                calibrationPath,
                result.CenterX,
                result.CenterY,
                cancellationToken);
            finalStatus =
                $"{ActiveLowerCameraNozzleName}旋转中心：X={result.CenterX:0.###}，Y={result.CenterY:0.###}；" +
                $"示教转换坐标：X={teachResult.Data.TransformedX:0.###}，Y={teachResult.Data.TransformedY:0.###}；" +
                $"8项数据已保存到{teachResult.FilePath}" +
                FormatBackupNotice(rotationSettingsBackupPath, "旧旋转中心配置") +
                FormatBackupNotice(teachResult.BackupFilePath, "旧示教数据");
            SetLowerCameraTeachStatus(finalStatus, WorkflowStatus.Success);
            finalStatusKind = WorkflowStatus.Success;
        }
        catch (OperationCanceledException)
        {
            finalStatus = "旋转中心计算已停止。";
            finalStatusKind = WorkflowStatus.Error;
            SetLowerCameraTeachStatus("自动下相机示教已停止。", WorkflowStatus.Error);
        }
        catch (Exception exception)
        {
            finalStatus = $"旋转中心/下相机示教流程失败：{exception.Message}";
            finalStatusKind = WorkflowStatus.Error;
            SetLowerCameraTeachStatus(
                $"自动下相机示教未完成：{exception.Message}；旋转中心若已保存，可单独重试示教。",
                WorkflowStatus.Error);
        }
        finally
        {
            if (Math.Abs(movedPulses) > 0.5 && !rotationMotionFailed && _motionController is not null)
            {
                try
                {
                    SetRotationCenterStatus(
                        $"正在将R轴{rotationAxisNo}返回开始位置（{-movedPulses:0} pulse）…",
                        WorkflowStatus.Running);
                    await _motionController.MoveAxisRelativeAsync(
                        rotationAxisNo,
                        -movedPulses,
                        CancellationToken.None,
                        minimumCompletionTolerance: DefaultPositionTolerancePulses);
                }
                catch (Exception returnException)
                {
                    finalStatus = string.IsNullOrWhiteSpace(finalStatus)
                        ? $"R轴{rotationAxisNo}回位失败：{returnException.Message}"
                        : finalStatus + $"；R轴{rotationAxisNo}回位失败：{returnException.Message}";
                    finalStatusKind = WorkflowStatus.Error;
                }
            }
            else if (rotationMotionFailed && Math.Abs(movedPulses) > 0.5)
            {
                finalStatus += $"；R轴已累计移动{movedPulses:0} pulse，因运动异常未自动回位。";
            }

            _rotationCenterCancellation?.Dispose();
            _rotationCenterCancellation = null;
            _rotationCenterRunning = false;
            SetRotationCenterStatus(
                string.IsNullOrWhiteSpace(finalStatus) ? "旋转中心流程已结束。" : finalStatus,
                finalStatusKind);
            UpdateCommandState();
        }
    }

    private void StopRotationCenter_Click(object sender, RoutedEventArgs e)
    {
        if (!_rotationCenterRunning)
        {
            return;
        }

        SetRotationCenterStatus("正在停止旋转中心计算…", WorkflowStatus.Running);
        _rotationCenterCancellation?.Cancel();
    }

    private void SaveActiveRotationCenter(VisionRotationCenterResult result)
    {
        if (ActiveLowerCameraNozzle == 2)
        {
            _uiSettings.LowerCameraNozzle2RotationCenterCalibrated = true;
            _uiSettings.LowerCameraNozzle2RotationCenterX = result.CenterX;
            _uiSettings.LowerCameraNozzle2RotationCenterY = result.CenterY;
        }
        else
        {
            _uiSettings.LowerCameraNozzle1RotationCenterCalibrated = true;
            _uiSettings.LowerCameraNozzle1RotationCenterX = result.CenterX;
            _uiSettings.LowerCameraNozzle1RotationCenterY = result.CenterY;
        }

        _lowerCameraTeachStatus =
            $"{ActiveLowerCameraNozzleName}旋转中心已更新，请执行下相机示教并保存8项数据。";
    }

    private void ResetActiveRotationCenter()
    {
        if (ActiveLowerCameraNozzle == 2)
        {
            _uiSettings.LowerCameraNozzle2RotationCenterCalibrated = false;
            _uiSettings.LowerCameraNozzle2RotationCenterX = 0;
            _uiSettings.LowerCameraNozzle2RotationCenterY = 0;
        }
        else
        {
            _uiSettings.LowerCameraNozzle1RotationCenterCalibrated = false;
            _uiSettings.LowerCameraNozzle1RotationCenterX = 0;
            _uiSettings.LowerCameraNozzle1RotationCenterY = 0;
        }

        _lowerCameraTeachStatus =
            $"请先完成{ActiveLowerCameraNozzleName}旋转中心计算。";
    }

    private void RefreshRotationCenterStatus()
    {
        if (!IsLowerCameraMode)
        {
            _rotationCenterStatus = "旋转中心计算仅用于下相机标定。";
            return;
        }

        var calibrated = ActiveLowerCameraNozzle == 2
            ? _uiSettings.LowerCameraNozzle2RotationCenterCalibrated
            : _uiSettings.LowerCameraNozzle1RotationCenterCalibrated;
        var centerX = ActiveLowerCameraNozzle == 2
            ? _uiSettings.LowerCameraNozzle2RotationCenterX
            : _uiSettings.LowerCameraNozzle1RotationCenterX;
        var centerY = ActiveLowerCameraNozzle == 2
            ? _uiSettings.LowerCameraNozzle2RotationCenterY
            : _uiSettings.LowerCameraNozzle1RotationCenterY;
        _rotationCenterStatus = calibrated
            ? $"已保存{ActiveLowerCameraNozzleName}旋转中心：X={centerX:0.###}，Y={centerY:0.###}"
            : $"完成{ActiveLowerCameraNozzleName}九点标定后可计算旋转中心。";
    }

    private void SetRotationCenterStatus(string message, WorkflowStatus status)
    {
        _rotationCenterStatus = message;
        ScheduleCalibrationSidebarSync();
    }

    private bool CanCalculateRotationCenter()
    {
        return IsLowerCameraMode &&
               !_rotationCenterRunning &&
               !_lowerCameraTeachRunning &&
               !_lowerCameraCorrectionTestRunning &&
               !_calibrationRunning &&
               !_clickMoveRunning &&
               !_clickMoveConfigurationRunning &&
               !_centerSyncRunning &&
               !_calibrationProcedureSwitchRunning &&
               _motionController is not null &&
               _hostReady &&
               TryGetCalibrationFilePath(CalibrationFilePathTextBox.Text, out var calibrationPath) &&
               File.Exists(calibrationPath);
    }

    private async void RunLowerCameraTeach_Click(object sender, RoutedEventArgs e)
    {
        if (_lowerCameraTeachRunning)
        {
            return;
        }

        try
        {
            if (!IsLowerCameraMode)
            {
                throw new InvalidOperationException("下相机示教只用于下相机标定。");
            }

            if (!_hostReady)
            {
                throw new InvalidOperationException("VisionMaster 视觉组件尚未就绪。");
            }

            var calibrationPath = GetCalibrationFilePath(CalibrationFilePathTextBox.Text);
            if (!File.Exists(calibrationPath))
            {
                throw new FileNotFoundException(
                    $"请先完成{ActiveLowerCameraNozzleName}的下相机九点标定。",
                    calibrationPath);
            }

            if (!TryGetActiveRotationCenter(out var circleCenterX, out var circleCenterY))
            {
                throw new InvalidOperationException(
                    $"请先完成{ActiveLowerCameraNozzleName}的旋转中心计算。");
            }

            _lowerCameraTeachCancellation = new CancellationTokenSource();
            _lowerCameraTeachRunning = true;
            SetLowerCameraTeachStatus(
                $"正在把{Path.GetFileName(calibrationPath)}写入标定转换1，并执行“下相机示教流程”…",
                WorkflowStatus.Running);
            UpdateCommandState();

            var captured = await CaptureAndSaveLowerCameraTeachDataAsync(
                calibrationPath,
                circleCenterX,
                circleCenterY,
                _lowerCameraTeachCancellation.Token);
            var data = captured.Data;
            var savePath = captured.FilePath;
            SetLowerCameraTeachStatus(
                $"已保存{ActiveLowerCameraNozzleName}示教数据：圆心({data.CircleCenterX:0.###}, {data.CircleCenterY:0.###})；" +
                $"直线({data.LineStartX:0.###}, {data.LineStartY:0.###})→({data.LineEndX:0.###}, {data.LineEndY:0.###})；" +
                $"转换({data.TransformedX:0.###}, {data.TransformedY:0.###})。文件：{savePath}" +
                FormatBackupNotice(captured.BackupFilePath, "旧示教数据"),
                WorkflowStatus.Success);
            RefreshLowerCameraCorrectionTestStatus();
            SetWorkflowStatus(
                $"{ActiveLowerCameraNozzleName}下相机示教数据已保存。",
                WorkflowStatus.Success);
        }
        catch (OperationCanceledException)
        {
            SetLowerCameraTeachStatus("下相机示教已停止。", WorkflowStatus.Error);
        }
        catch (Exception exception)
        {
            SetLowerCameraTeachStatus(
                $"下相机示教失败：{exception.Message}",
                WorkflowStatus.Error);
            SetWorkflowStatus(
                $"下相机示教失败：{exception.Message}",
                WorkflowStatus.Error);
        }
        finally
        {
            _lowerCameraTeachCancellation?.Dispose();
            _lowerCameraTeachCancellation = null;
            _lowerCameraTeachRunning = false;
            UpdateCommandState();
        }
    }

    private async Task<(LowerCameraTeachData Data, string FilePath, string? BackupFilePath)> CaptureAndSaveLowerCameraTeachDataAsync(
        string calibrationPath,
        double circleCenterX,
        double circleCenterY,
        CancellationToken cancellationToken)
    {
        var result = await VisionHost.RunLowerCameraTeachAsync(
            calibrationPath,
            cancellationToken);
        var data = new LowerCameraTeachData
        {
            CircleCenterX = circleCenterX,
            CircleCenterY = circleCenterY,
            LineStartX = result.LineStartX,
            LineStartY = result.LineStartY,
            LineEndX = result.LineEndX,
            LineEndY = result.LineEndY,
            TransformedX = result.TransformedX,
            TransformedY = result.TransformedY
        };
        var savePath = GetLowerCameraTeachDataFilePath(ActiveLowerCameraNozzle);
        var backupFilePath = _lowerCameraTeachDataStore.Save(savePath, data);
        return (data, savePath, backupFilePath);
    }

    private void StopLowerCameraTeach_Click(object sender, RoutedEventArgs e)
    {
        if (!_lowerCameraTeachRunning)
        {
            return;
        }

        SetLowerCameraTeachStatus("正在停止下相机示教…", WorkflowStatus.Running);
        _lowerCameraTeachCancellation?.Cancel();
    }

    private void ImportLowerCameraTeachData_Click(object sender, RoutedEventArgs e)
    {
        if (!CanImportLowerCameraTeachData())
        {
            return;
        }

        var currentFilePath = GetLowerCameraTeachDataFilePath(ActiveLowerCameraNozzle);
        var dialog = new OpenFileDialog
        {
            Title = $"导入{ActiveLowerCameraNozzleName}的8项下相机示教数据",
            Filter = "下相机示教数据 (*.txt;*.json)|*.txt;*.json|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            InitialDirectory = Directory.Exists(Path.GetDirectoryName(currentFilePath))
                ? Path.GetDirectoryName(currentFilePath)
                : DefaultCalibrationDirectory
        };
        if (File.Exists(currentFilePath))
        {
            dialog.FileName = Path.GetFileName(currentFilePath);
        }

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        try
        {
            var data = _lowerCameraTeachDataStore.Load(dialog.FileName);
            var selectedFilePath = Path.GetFullPath(dialog.FileName);
            SetLowerCameraTeachDataFilePath(ActiveLowerCameraNozzle, selectedFilePath);
            _visionCalibration.Save();
            var message =
                $"已应用{ActiveLowerCameraNozzleName}示教文件“{Path.GetFileName(selectedFilePath)}”：" +
                $"圆心({data.CircleCenterX:0.###}, {data.CircleCenterY:0.###})；" +
                $"直线({data.LineStartX:0.###}, {data.LineStartY:0.###})→({data.LineEndX:0.###}, {data.LineEndY:0.###})；" +
                $"转换({data.TransformedX:0.###}, {data.TransformedY:0.###})。现在可直接纠偏测试。";
            SetLowerCameraTeachStatus(message, WorkflowStatus.Success);
            RefreshLowerCameraCorrectionTestStatus();
            SetWorkflowStatus(message, WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            var message = $"导入{ActiveLowerCameraNozzleName}示教数据失败：{exception.Message}";
            SetLowerCameraCorrectionTestStatus(message);
            SetWorkflowStatus(message, WorkflowStatus.Error);
        }
        finally
        {
            UpdateCommandState();
        }
    }

    private async void RunLowerCameraCorrectionTest_Click(object sender, RoutedEventArgs e)
    {
        if (_lowerCameraCorrectionTestRunning)
        {
            return;
        }

        try
        {
            if (!IsLowerCameraMode)
            {
                throw new InvalidOperationException("纠偏测试只用于下相机标定。");
            }

            if (!_hostReady)
            {
                throw new InvalidOperationException("VisionMaster 视觉组件尚未就绪。");
            }

            var teachDataPath = GetLowerCameraTeachDataFilePath(ActiveLowerCameraNozzle);
            if (!File.Exists(teachDataPath))
            {
                throw new FileNotFoundException(
                    $"请先执行并保存{ActiveLowerCameraNozzleName}的下相机示教数据。",
                    teachDataPath);
            }

            var calibrationPath = GetCalibrationFilePath(CalibrationFilePathTextBox.Text);
            if (!File.Exists(calibrationPath))
            {
                throw new FileNotFoundException(
                    $"请先完成{ActiveLowerCameraNozzleName}的下相机九点标定。",
                    calibrationPath);
            }

            var teachData = _lowerCameraTeachDataStore.Load(teachDataPath);
            _lowerCameraCorrectionTestCancellation = new CancellationTokenSource();
            _lowerCameraCorrectionTestRunning = true;
            SetLowerCameraCorrectionTestStatus(
                $"正在使用标定文件“{Path.GetFileName(calibrationPath)}”，写入{ActiveLowerCameraNozzleName}的直线和圆心，并运行“下相机纠偏”…");
            SetWorkflowStatus(
                $"{ActiveLowerCameraNozzleName}下相机纠偏测试运行中…",
                WorkflowStatus.Running);
            UpdateCommandState();

            var result = await RunLowerCameraCorrectionAsync(
                teachData,
                calibrationPath,
                _lowerCameraCorrectionTestCancellation.Token);
            var message =
                $"{ActiveLowerCameraNozzleName}纠偏测试：X偏差={result.CorrectionX:0.00000}，" +
                $"Y偏差={result.CorrectionY:0.00000}，夹角={result.MeasuredAngle:0.00000}°；" +
                $"标定文件={Path.GetFileName(calibrationPath)}";
            SetLowerCameraCorrectionTestStatus(message);
            SetWorkflowStatus(message, WorkflowStatus.Success);
        }
        catch (OperationCanceledException)
        {
            SetLowerCameraCorrectionTestStatus("纠偏测试已停止。");
            SetWorkflowStatus("下相机纠偏测试已停止。", WorkflowStatus.Error);
        }
        catch (Exception exception)
        {
            SetLowerCameraCorrectionTestStatus($"纠偏测试失败：{exception.Message}");
            SetWorkflowStatus($"下相机纠偏测试失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            _lowerCameraCorrectionTestCancellation?.Dispose();
            _lowerCameraCorrectionTestCancellation = null;
            _lowerCameraCorrectionTestRunning = false;
            UpdateCommandState();
        }
    }

    private void StopLowerCameraCorrectionTest_Click(object sender, RoutedEventArgs e)
    {
        if (!_lowerCameraCorrectionTestRunning)
        {
            return;
        }

        SetLowerCameraCorrectionTestStatus("正在停止纠偏测试…");
        _lowerCameraCorrectionTestCancellation?.Cancel();
    }

    private bool TryGetActiveRotationCenter(out double centerX, out double centerY)
    {
        var calibrated = ActiveLowerCameraNozzle == 2
            ? _uiSettings.LowerCameraNozzle2RotationCenterCalibrated
            : _uiSettings.LowerCameraNozzle1RotationCenterCalibrated;
        centerX = ActiveLowerCameraNozzle == 2
            ? _uiSettings.LowerCameraNozzle2RotationCenterX
            : _uiSettings.LowerCameraNozzle1RotationCenterX;
        centerY = ActiveLowerCameraNozzle == 2
            ? _uiSettings.LowerCameraNozzle2RotationCenterY
            : _uiSettings.LowerCameraNozzle1RotationCenterY;
        return calibrated && double.IsFinite(centerX) && double.IsFinite(centerY);
    }

    private bool CanRunLowerCameraTeach()
    {
        return IsLowerCameraMode &&
               !_lowerCameraTeachRunning &&
               !_lowerCameraCorrectionTestRunning &&
               !_rotationCenterRunning &&
               !_calibrationRunning &&
               !_clickMoveRunning &&
               !_clickMoveConfigurationRunning &&
               !_centerSyncRunning &&
               !_calibrationProcedureSwitchRunning &&
               _hostReady &&
               TryGetActiveRotationCenter(out _, out _) &&
               TryGetCalibrationFilePath(CalibrationFilePathTextBox.Text, out var calibrationPath) &&
               File.Exists(calibrationPath);
    }

    private bool CanRunLowerCameraCorrectionTest()
    {
        return IsLowerCameraMode &&
               !_lowerCameraCorrectionTestRunning &&
               !_lowerCameraTeachRunning &&
               !_rotationCenterRunning &&
               !_calibrationRunning &&
               !_clickMoveRunning &&
               !_clickMoveConfigurationRunning &&
               !_centerSyncRunning &&
               !_calibrationProcedureSwitchRunning &&
               _hostReady &&
               File.Exists(GetLowerCameraTeachDataFilePath(ActiveLowerCameraNozzle)) &&
               TryGetCalibrationFilePath(CalibrationFilePathTextBox.Text, out var calibrationPath) &&
               File.Exists(calibrationPath);
    }

    private bool CanImportLowerCameraTeachData()
    {
        return IsLowerCameraMode &&
               !_lowerCameraTeachRunning &&
               !_lowerCameraCorrectionTestRunning &&
               !_rotationCenterRunning &&
               !_calibrationRunning &&
               !_clickMoveRunning &&
               !_clickMoveConfigurationRunning &&
               !_centerSyncRunning &&
               !_calibrationProcedureSwitchRunning;
    }

    private string GetLowerCameraTeachDataFilePath(int nozzleNumber)
    {
        var configuredPath = nozzleNumber == 2
            ? _uiSettings.LowerCameraNozzle2TeachDataFilePath
            : _uiSettings.LowerCameraNozzle1TeachDataFilePath;
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            try
            {
                return Path.GetFullPath(configuredPath.Trim());
            }
            catch
            {
                // Invalid legacy settings fall back to the per-nozzle default file.
            }
        }

        return nozzleNumber == 2
            ? DefaultLowerCameraNozzle2TeachDataFilePath
            : DefaultLowerCameraNozzle1TeachDataFilePath;
    }

    private void SetLowerCameraTeachDataFilePath(int nozzleNumber, string filePath)
    {
        var fullPath = Path.GetFullPath(filePath);
        if (nozzleNumber == 2)
        {
            _uiSettings.LowerCameraNozzle2TeachDataFilePath = fullPath;
        }
        else
        {
            _uiSettings.LowerCameraNozzle1TeachDataFilePath = fullPath;
        }
    }

    private void RefreshLowerCameraTeachStatus()
    {
        if (!IsLowerCameraMode)
        {
            _lowerCameraTeachStatus = "下相机示教仅用于下相机标定。";
            return;
        }

        if (!TryGetActiveRotationCenter(out _, out _))
        {
            _lowerCameraTeachStatus = $"请先完成{ActiveLowerCameraNozzleName}旋转中心计算。";
            return;
        }

        var filePath = GetLowerCameraTeachDataFilePath(ActiveLowerCameraNozzle);
        if (!File.Exists(filePath))
        {
            _lowerCameraTeachStatus = $"可执行{ActiveLowerCameraNozzleName}下相机示教并保存8项数据。";
            return;
        }

        try
        {
            var data = _lowerCameraTeachDataStore.Load(filePath);
            _lowerCameraTeachStatus =
                $"已有{ActiveLowerCameraNozzleName}示教数据：圆心({data.CircleCenterX:0.###}, {data.CircleCenterY:0.###})，" +
                $"转换({data.TransformedX:0.###}, {data.TransformedY:0.###})。再次执行将更新本吸嘴文件。";
        }
        catch (Exception exception)
        {
            _lowerCameraTeachStatus = $"已有示教文件无法读取：{exception.Message}；请重新执行。";
        }
    }

    private void SetLowerCameraTeachStatus(string message, WorkflowStatus status)
    {
        _lowerCameraTeachStatus = message;
        ScheduleCalibrationSidebarSync();
    }

    private void RefreshLowerCameraCorrectionTestStatus()
    {
        if (!IsLowerCameraMode)
        {
            _lowerCameraCorrectionTestStatus = "纠偏测试仅用于下相机标定。";
            return;
        }

        var teachDataPath = GetLowerCameraTeachDataFilePath(ActiveLowerCameraNozzle);
        if (!File.Exists(teachDataPath))
        {
            _lowerCameraCorrectionTestStatus =
                $"请先执行并保存{ActiveLowerCameraNozzleName}的下相机示教数据。";
            return;
        }

        if (!TryGetCalibrationFilePath(CalibrationFilePathTextBox.Text, out var calibrationPath) ||
            !File.Exists(calibrationPath))
        {
            _lowerCameraCorrectionTestStatus =
                $"示教数据已就绪，请选择{ActiveLowerCameraNozzleName}的下相机标定文件。";
            return;
        }

        _lowerCameraCorrectionTestStatus =
            $"已就绪：使用{ActiveLowerCameraNozzleName}示教数据和当前标定文件。";
    }

    private void SetLowerCameraCorrectionTestStatus(string message)
    {
        _lowerCameraCorrectionTestStatus = message;
        ScheduleCalibrationSidebarSync();
    }

    private static string FormatBackupNotice(string? backupFilePath, string label = "旧文件")
    {
        return string.IsNullOrWhiteSpace(backupFilePath)
            ? ""
            : $"；{label}已自动备份：{backupFilePath}";
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
        if (_hostReady && _calibrationViewRequested)
        {
            try
            {
                await ActivateCalibrationViewAsync();
            }
            catch (Exception exception)
            {
                SetHostStatus($"标定流程开启失败：{exception.Message}", HostStatus.Error);
            }
        }
    }

    private void VisionHost_Started(object? sender, EventArgs e)
    {
        _hostReady = true;
        _hostCanRestart = true;
        HostPlaceholder.Visibility = Visibility.Collapsed;
        RestartHostButton.IsEnabled = true;
        SetHostStatus("视觉组件已启动", HostStatus.Ready);
        UpdateCommandState();
    }

    private void VisionHost_Failed(object? sender, VisionMasterHostFailedEventArgs e)
    {
        _hostReady = false;
        _hostCanRestart = true;
        _calibrationCancellation?.Cancel();
        _clickMoveCancellation?.Cancel();
        _rotationCenterCancellation?.Cancel();
        _lowerCameraTeachCancellation?.Cancel();
        _lowerCameraCorrectionTestCancellation?.Cancel();
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
        _rotationCenterCancellation?.Cancel();
        _lowerCameraTeachCancellation?.Cancel();
        _lowerCameraCorrectionTestCancellation?.Cancel();
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
        ScheduleCalibrationSettingsSave();
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
                $"VisionMaster：X {stepX / PulsesPerVisionUnit:0.####}　" +
                $"Y {stepY / PulsesPerVisionUnit:0.####}";
            OffsetVmText.Foreground = new SolidColorBrush(Color.FromRgb(73, 209, 125));
            return;
        }

        OffsetVmText.Text = "X/Y 标定间距必须大于 0";
        OffsetVmText.Foreground = new SolidColorBrush(Color.FromRgb(242, 122, 128));
    }

    private void LoadCalibrationSettings()
    {
        _uiSettings = _visionCalibration.Settings;
        SelectAxisSetComboBox();
        SelectLowerCameraNozzleComboBox();
        var stepX = IsLowerCameraMode
            ? _uiSettings.LowerCameraStepXPulses
            : ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondStepXPulses
            : _uiSettings.StepXPulses;
        var stepY = IsLowerCameraMode
            ? _uiSettings.LowerCameraStepYPulses
            : ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondStepYPulses
            : _uiSettings.StepYPulses;
        var velocity = IsLowerCameraMode
            ? _uiSettings.LowerCameraVelocityPulsesPerSecond
            : ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondVelocityPulsesPerSecond
            : _uiSettings.VelocityPulsesPerSecond;
        var settleMilliseconds = IsLowerCameraMode
            ? _uiSettings.LowerCameraSettleMilliseconds
            : ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondSettleMilliseconds
            : _uiSettings.SettleMilliseconds;
        var movePriority = IsLowerCameraMode
            ? _uiSettings.LowerCameraMovePriority
            : ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondMovePriority
            : _uiSettings.MovePriority;

        StepXPulsesTextBox.Text = FormatPositiveSetting(stepX, 100_000);
        StepYPulsesTextBox.Text = FormatPositiveSetting(stepY, 100_000);
        VelocityTextBox.Text = FormatPositiveSetting(velocity, 200_000);
        SettleMillisecondsTextBox.Text = Math.Max(0, settleMilliseconds)
            .ToString(CultureInfo.CurrentCulture);
        ArrivalPosition1XTextBox.Text = FormatOptionalPosition(
            _uiSettings.LowerCameraArrivalPosition1X);
        ArrivalPosition1YTextBox.Text = FormatOptionalPosition(
            _uiSettings.LowerCameraArrivalPosition1Y);
        ArrivalPosition2XTextBox.Text = FormatOptionalPosition(
            _uiSettings.LowerCameraArrivalPosition2X);
        ArrivalPosition2YTextBox.Text = FormatOptionalPosition(
            _uiSettings.LowerCameraArrivalPosition2Y);

        var priority = string.Equals(movePriority, "Y", StringComparison.OrdinalIgnoreCase)
            ? "Y"
            : "X";
        MovePriorityComboBox.SelectedItem = MovePriorityComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag as string, priority, StringComparison.Ordinal));

        CalibrationFilePathTextBox.Text =
            TryGetCalibrationFilePath(ActiveCalibrationFilePath, out var savedPath)
                ? savedPath
                : GetDefaultCalibrationFilePath();
        var targetTool = VisionCalibrationService.ParseTargetTool(ActiveClickTargetTool);
        SelectClickTargetTool(targetTool);
        UpdateCalibrationProfilePathDisplay();
        UpdateNozzleTeachUi();
        UpdateNozzleCalibrationDisplay();
        RefreshRotationCenterStatus();
        RefreshLowerCameraTeachStatus();
        RefreshLowerCameraCorrectionTestStatus();
    }

    private void SelectClickTargetTool(VisionTargetTool targetTool)
    {
        var settingsValue = VisionCalibrationService.ToSettingsValue(targetTool);
        ClickTargetToolComboBox.SelectedItem = ClickTargetToolComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(
                item.Tag as string,
                settingsValue,
                StringComparison.Ordinal))
            ?? ClickTargetToolComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private VisionTargetTool GetSelectedTargetTool()
    {
        return VisionCalibrationService.ParseTargetTool(
            (ClickTargetToolComboBox.SelectedItem as ComboBoxItem)?.Tag as string);
    }

    private bool IsNozzleTargetSelected()
    {
        return GetSelectedTargetTool() != VisionTargetTool.Camera;
    }

    private void UpdateNozzleTeachUi()
    {
        if (NozzleTeachStepText is null)
        {
            return;
        }

        if (_nozzlePointFinding)
        {
            NozzleTeachStepText.Text = "正在查找";
            return;
        }

        if (_nozzlePointSaving)
        {
            NozzleTeachStepText.Text = "正在保存";
            return;
        }

        if (_pendingNozzlePointResult is not null)
        {
            NozzleTeachStepText.Text = "等待分配";
            return;
        }

        if (ActiveNozzle1Calibrated && ActiveNozzle2Calibrated)
        {
            NozzleTeachStepText.Text = "已保存";
            return;
        }

        NozzleTeachStepText.Text = _nozzleDotPosition is null
            ? "等待记录下压XY"
            : "下压XY已记录";
    }

    private static string GetToolDisplayName(VisionTargetTool tool)
    {
        return tool switch
        {
            VisionTargetTool.Nozzle1 => "吸嘴1",
            VisionTargetTool.Nozzle2 => "吸嘴2",
            _ => "相机中心"
        };
    }

    private void UpdateNozzleCalibrationDisplay()
    {
        if (ActiveNozzle1Calibrated &&
            (!double.IsFinite(ActiveNozzle1OffsetX) ||
             !double.IsFinite(ActiveNozzle1OffsetY)))
        {
            if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
            {
                _uiSettings.SecondNozzleOffsetCalibrated = false;
            }
            else
            {
                _uiSettings.NozzleOffsetCalibrated = false;
            }
        }

        if (ActiveNozzle2Calibrated &&
            (!double.IsFinite(ActiveNozzle2OffsetX) ||
             !double.IsFinite(ActiveNozzle2OffsetY)))
        {
            if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
            {
                _uiSettings.SecondNozzle2OffsetCalibrated = false;
            }
            else
            {
                _uiSettings.Nozzle2OffsetCalibrated = false;
            }
        }

        var nozzle1Text = ActiveNozzle1Calibrated
            ? $"吸嘴1：X {ActiveNozzle1OffsetX:0.###}　Y {ActiveNozzle1OffsetY:0.###} pulse"
            : "吸嘴1：未标定";
        var nozzle2Text = ActiveNozzle2Calibrated
            ? $"吸嘴2：X {ActiveNozzle2OffsetX:0.###}　Y {ActiveNozzle2OffsetY:0.###} pulse"
            : "吸嘴2：未标定";
        var status = ActiveNozzle1Calibrated && ActiveNozzle2Calibrated
            ? WorkflowStatus.Success
            : WorkflowStatus.Ready;
        SetNozzleCalibrationStatus($"{nozzle1Text}\n{nozzle2Text}", status);
    }

    private (string FilePath, string? BackupFilePath) SaveCurrentCalibrationProfile()
    {
        if (!ActiveNozzle1Calibrated || !ActiveNozzle2Calibrated)
        {
            throw new InvalidOperationException("请先查找两个吸嘴点并完成吸嘴分配。");
        }

        var calibrationFilePath = GetCalibrationFilePath(CalibrationFilePathTextBox.Text);
        if (!File.Exists(calibrationFilePath))
        {
            throw new FileNotFoundException("九点标定文件不存在，请先完成九点标定。", calibrationFilePath);
        }

        SaveCalibrationSettingsNoThrow();
        var profilePath = Path.Combine(
            Path.GetDirectoryName(calibrationFilePath) ?? DefaultCalibrationDirectory,
            $"{Path.GetFileNameWithoutExtension(calibrationFilePath)}.双吸嘴.json");
        var profile = new VisionCalibrationProfile
        {
            XHardwareAxisNo = ActiveAxisPair.XHardwareAxisNo,
            YHardwareAxisNo = ActiveAxisPair.YHardwareAxisNo,
            CalibrationFilePath = calibrationFilePath,
            StepXPulses = ParsePositiveDouble(StepXPulsesTextBox.Text, "间距 X"),
            StepYPulses = ParsePositiveDouble(StepYPulsesTextBox.Text, "间距 Y"),
            VelocityPulsesPerSecond = ParsePositiveDouble(VelocityTextBox.Text, "标定速度"),
            SettleMilliseconds = ParseNonNegativeInt(SettleMillisecondsTextBox.Text, "到位稳定等待"),
            MovePriority = (MovePriorityComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "Y" ? "Y" : "X",
            NozzleDotPositionRecorded = _nozzleDotPosition is not null,
            NozzleDotPositionXPulses = _nozzleDotPosition?.ActualX ?? 0d,
            NozzleDotPositionYPulses = _nozzleDotPosition?.ActualY ?? 0d,
            Nozzle1Calibrated = true,
            Nozzle1OffsetXPulses = ActiveNozzle1OffsetX,
            Nozzle1OffsetYPulses = ActiveNozzle1OffsetY,
            Nozzle2Calibrated = true,
            Nozzle2OffsetXPulses = ActiveNozzle2OffsetX,
            Nozzle2OffsetYPulses = ActiveNozzle2OffsetY
        };
        var backupFilePath = _profileStore.Save(profilePath, profile);
        ActiveCalibrationProfilePath = profilePath;
        _visionCalibration.Save();
        UpdateCalibrationProfilePathDisplay();
        return (profilePath, backupFilePath);
    }

    private void ApplyCalibrationProfile(VisionCalibrationProfile profile, string profilePath)
    {
        _visionCalibration.ActiveAxisSet = profile.XHardwareAxisNo == VisionCalibrationService.SecondSetXHardwareAxisNo &&
            profile.YHardwareAxisNo == VisionCalibrationService.SecondSetYHardwareAxisNo
                ? VisionCalibrationAxisSet.Second
                : VisionCalibrationAxisSet.First;
        SelectAxisSetComboBox();

        var movePriority = string.Equals(profile.MovePriority, "Y", StringComparison.OrdinalIgnoreCase)
            ? "Y"
            : "X";
        if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
        {
            _uiSettings.SecondStepXPulses = profile.StepXPulses;
            _uiSettings.SecondStepYPulses = profile.StepYPulses;
            _uiSettings.SecondVelocityPulsesPerSecond = profile.VelocityPulsesPerSecond;
            _uiSettings.SecondSettleMilliseconds = profile.SettleMilliseconds;
            _uiSettings.SecondMovePriority = movePriority;
            _uiSettings.SecondCalibrationFilePath = Path.GetFullPath(profile.CalibrationFilePath);
            _uiSettings.SecondCalibrationProfilePath = Path.GetFullPath(profilePath);
            _uiSettings.SecondNozzleOffsetCalibrated = profile.Nozzle1Calibrated;
            _uiSettings.SecondNozzleOffsetXPulses = profile.Nozzle1OffsetXPulses;
            _uiSettings.SecondNozzleOffsetYPulses = profile.Nozzle1OffsetYPulses;
            _uiSettings.SecondNozzle2OffsetCalibrated = profile.Nozzle2Calibrated;
            _uiSettings.SecondNozzle2OffsetXPulses = profile.Nozzle2OffsetXPulses;
            _uiSettings.SecondNozzle2OffsetYPulses = profile.Nozzle2OffsetYPulses;
            _uiSettings.SecondClickTargetTool = "Nozzle1";
        }
        else
        {
            _uiSettings.StepXPulses = profile.StepXPulses;
            _uiSettings.StepYPulses = profile.StepYPulses;
            _uiSettings.VelocityPulsesPerSecond = profile.VelocityPulsesPerSecond;
            _uiSettings.SettleMilliseconds = profile.SettleMilliseconds;
            _uiSettings.MovePriority = movePriority;
            _uiSettings.CalibrationFilePath = Path.GetFullPath(profile.CalibrationFilePath);
            _uiSettings.CalibrationProfilePath = Path.GetFullPath(profilePath);
            _uiSettings.NozzleOffsetCalibrated = profile.Nozzle1Calibrated;
            _uiSettings.NozzleOffsetXPulses = profile.Nozzle1OffsetXPulses;
            _uiSettings.NozzleOffsetYPulses = profile.Nozzle1OffsetYPulses;
            _uiSettings.Nozzle2OffsetCalibrated = profile.Nozzle2Calibrated;
            _uiSettings.Nozzle2OffsetXPulses = profile.Nozzle2OffsetXPulses;
            _uiSettings.Nozzle2OffsetYPulses = profile.Nozzle2OffsetYPulses;
            _uiSettings.ClickTargetTool = "Nozzle1";
        }
        _nozzleDotPosition = null;
        _pendingNozzlePointResult = null;
        _nozzle1ClickVerified = false;
        _nozzle2ClickVerified = false;

        StepXPulsesTextBox.Text = FormatPositiveSetting(profile.StepXPulses, 100_000);
        StepYPulsesTextBox.Text = FormatPositiveSetting(profile.StepYPulses, 100_000);
        VelocityTextBox.Text = FormatPositiveSetting(profile.VelocityPulsesPerSecond, 200_000);
        SettleMillisecondsTextBox.Text = profile.SettleMilliseconds.ToString(CultureInfo.CurrentCulture);
        MovePriorityComboBox.SelectedItem = MovePriorityComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag as string, movePriority, StringComparison.Ordinal));
        CalibrationFilePathTextBox.Text = ActiveCalibrationFilePath;
        SelectClickTargetTool(VisionTargetTool.Nozzle1);
        _visionCalibration.Save();
        UpdateVisionOffsetPreview();
        UpdateCalibrationProfilePathDisplay();
        UpdateNozzleTeachUi();
        UpdateNozzleCalibrationDisplay();
    }

    private void UpdateCalibrationProfilePathDisplay()
    {
        if (CalibrationProfilePathText is null)
        {
            return;
        }

        var path = ActiveCalibrationProfilePath?.Trim() ?? "";
        CalibrationProfilePathText.Text = string.IsNullOrWhiteSpace(path)
            ? "配置：完成双吸嘴后自动生成"
            : $"配置：{Path.GetFileName(path)}";
        CalibrationProfilePathText.ToolTip = path;
    }

    private static string FormatPositiveSetting(double value, double fallback)
    {
        return (double.IsFinite(value) && value > 0 ? value : fallback)
            .ToString("0.###", CultureInfo.CurrentCulture);
    }

    private static string FormatOptionalPosition(double? value)
    {
        return value is { } position && double.IsFinite(position)
            ? position.ToString("0.###", CultureInfo.CurrentCulture)
            : "";
    }

    private static double? ReadOptionalPosition(string? text, double? currentValue)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        return TryParseFiniteDouble(text, out var value) ? value : currentValue;
    }

    private void ScheduleCalibrationSettingsSave()
    {
        if (!_settingsLoaded || _shutdown)
        {
            return;
        }

        _settingsSaveTimer.Stop();
        _settingsSaveTimer.Start();
    }

    private void SettingsSaveTimer_Tick(object? sender, EventArgs e)
    {
        _settingsSaveTimer.Stop();
        SaveCalibrationSettingsNoThrow();
    }

    private void VisualCalibrationPage_Unloaded(object sender, RoutedEventArgs e)
    {
        _rotationCenterCancellation?.Cancel();
        _lowerCameraTeachCancellation?.Cancel();
        _lowerCameraCorrectionTestCancellation?.Cancel();
        SaveCalibrationSettingsNoThrow();
    }

    private void SaveCalibrationSettingsNoThrow()
    {
        if (!_settingsLoaded)
        {
            return;
        }

        try
        {
            if (TryParseFiniteDouble(StepXPulsesTextBox.Text, out var stepX) && stepX > 0)
            {
                if (IsLowerCameraMode)
                {
                    _uiSettings.LowerCameraStepXPulses = stepX;
                }
                else if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
                {
                    _uiSettings.SecondStepXPulses = stepX;
                }
                else
                {
                    _uiSettings.StepXPulses = stepX;
                }
            }

            if (TryParseFiniteDouble(StepYPulsesTextBox.Text, out var stepY) && stepY > 0)
            {
                if (IsLowerCameraMode)
                {
                    _uiSettings.LowerCameraStepYPulses = stepY;
                }
                else if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
                {
                    _uiSettings.SecondStepYPulses = stepY;
                }
                else
                {
                    _uiSettings.StepYPulses = stepY;
                }
            }

            if (TryParseFiniteDouble(VelocityTextBox.Text, out var velocity) && velocity > 0)
            {
                if (IsLowerCameraMode)
                {
                    _uiSettings.LowerCameraVelocityPulsesPerSecond = velocity;
                }
                else if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
                {
                    _uiSettings.SecondVelocityPulsesPerSecond = velocity;
                }
                else
                {
                    _uiSettings.VelocityPulsesPerSecond = velocity;
                }
            }

            if (int.TryParse(
                    SettleMillisecondsTextBox.Text,
                    NumberStyles.Integer,
                    CultureInfo.CurrentCulture,
                    out var settleMilliseconds) &&
                settleMilliseconds >= 0)
            {
                if (IsLowerCameraMode)
                {
                    _uiSettings.LowerCameraSettleMilliseconds = settleMilliseconds;
                }
                else if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
                {
                    _uiSettings.SecondSettleMilliseconds = settleMilliseconds;
                }
                else
                {
                    _uiSettings.SettleMilliseconds = settleMilliseconds;
                }
            }

            var movePriority = (MovePriorityComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "Y"
                ? "Y"
                : "X";
            if (IsLowerCameraMode)
            {
                _uiSettings.LowerCameraMovePriority = movePriority;
            }
            else if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
            {
                _uiSettings.SecondMovePriority = movePriority;
            }
            else
            {
                _uiSettings.MovePriority = movePriority;
            }

            ActiveClickTargetTool = VisionCalibrationService.ToSettingsValue(GetSelectedTargetTool());
            _uiSettings.LowerCameraArrivalPosition1X = ReadOptionalPosition(
                ArrivalPosition1XTextBox.Text,
                _uiSettings.LowerCameraArrivalPosition1X);
            _uiSettings.LowerCameraArrivalPosition1Y = ReadOptionalPosition(
                ArrivalPosition1YTextBox.Text,
                _uiSettings.LowerCameraArrivalPosition1Y);
            _uiSettings.LowerCameraArrivalPosition2X = ReadOptionalPosition(
                ArrivalPosition2XTextBox.Text,
                _uiSettings.LowerCameraArrivalPosition2X);
            _uiSettings.LowerCameraArrivalPosition2Y = ReadOptionalPosition(
                ArrivalPosition2YTextBox.Text,
                _uiSettings.LowerCameraArrivalPosition2Y);
            if (TryGetCalibrationFilePath(CalibrationFilePathTextBox.Text, out var calibrationPath))
            {
                ActiveCalibrationFilePath = calibrationPath;
            }

            _visionCalibration.Save();
        }
        catch
        {
            // Local preferences must never interrupt motion or vision operation.
        }
    }

    private static void EnsureDefaultCalibrationDirectory()
    {
        try
        {
            Directory.CreateDirectory(DefaultCalibrationDirectory);
        }
        catch
        {
            // The selected file path is validated again before calibration starts.
        }
    }

    private void UpdateCommandState()
    {
        if (RecordCenterButton is null ||
            StartLivePreviewButton is null ||
            CalibrationFilePathTextBox is null ||
            RecordNozzleDotPositionButton is null ||
            ReturnToPhotoPositionButton is null ||
            FindNozzlePointsButton is null ||
            AssignPoint1ToNozzle1Button is null ||
            AssignPoint1ToNozzle2Button is null ||
            ImportCalibrationFileButton is null ||
            LoadCalibrationProfileButton is null ||
            SaveCalibrationProfileButton is null ||
            AxisSetComboBox is null ||
            LowerCameraNozzleComboBox is null ||
            LowerCameraArrivalPanel is null ||
            ArrivalPosition1XTextBox is null ||
            ArrivalPosition1YTextBox is null ||
            ArrivalPosition2XTextBox is null ||
            ArrivalPosition2YTextBox is null ||
            MoveToArrivalPosition1Button is null ||
            MoveToArrivalPosition2Button is null ||
            CalibrationEmergencyStopButton is null ||
            ClickTargetToolComboBox is null)
        {
            return;
        }

        var calibrationPathValid = TryGetCalibrationFilePath(
            CalibrationFilePathTextBox.Text,
            out var calibrationFilePath);
        var clickTargetReady =
            IsLowerCameraMode || _visionCalibration.IsToolCalibrated(GetSelectedTargetTool());
        var profileReadyToSave =
            !IsLowerCameraMode &&
            ActiveNozzle1Calibrated &&
            ActiveNozzle2Calibrated &&
            calibrationPathValid &&
            File.Exists(calibrationFilePath);

        RecordCenterButton.IsEnabled =
            !_calibrationProcedureSwitchRunning &&
            !_calibrationRunning &&
            !_livePreviewStarting &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null;
        StartLivePreviewButton.IsEnabled =
            !_calibrationProcedureSwitchRunning &&
            !_calibrationRunning &&
            !_livePreviewStarting &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _hostReady;
        StartCalibrationButton.IsEnabled =
            !_calibrationProcedureSwitchRunning &&
            !_calibrationRunning &&
            !_livePreviewStarting &&
            !_calibrationFileImporting &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null &&
            _hostReady &&
            calibrationPathValid;
        StopCalibrationButton.IsEnabled = _calibrationRunning;
        AxisSetComboBox.IsEnabled =
            !_calibrationProcedureSwitchRunning &&
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_calibrationFileImporting &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning;
        LowerCameraNozzleComboBox.IsEnabled =
            IsLowerCameraMode &&
            !_calibrationProcedureSwitchRunning &&
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_calibrationFileImporting &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        var arrivalConfigurationEnabled =
            IsLowerCameraMode &&
            !_calibrationProcedureSwitchRunning &&
            !_calibrationRunning &&
            !_livePreviewStarting &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_calibrationFileImporting &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        ArrivalPosition1XTextBox.IsEnabled = arrivalConfigurationEnabled;
        ArrivalPosition1YTextBox.IsEnabled = arrivalConfigurationEnabled;
        ArrivalPosition2XTextBox.IsEnabled = arrivalConfigurationEnabled;
        ArrivalPosition2YTextBox.IsEnabled = arrivalConfigurationEnabled;
        MoveToArrivalPosition1Button.IsEnabled =
            arrivalConfigurationEnabled &&
            _motionController is not null &&
            TryParseFiniteDouble(ArrivalPosition1XTextBox.Text, out _) &&
            TryParseFiniteDouble(ArrivalPosition1YTextBox.Text, out _);
        MoveToArrivalPosition2Button.IsEnabled =
            arrivalConfigurationEnabled &&
            _motionController is not null &&
            TryParseFiniteDouble(ArrivalPosition2XTextBox.Text, out _) &&
            TryParseFiniteDouble(ArrivalPosition2YTextBox.Text, out _);
        CalibrationEmergencyStopButton.IsEnabled = _motionController is not null;
        RestartHostButton.IsEnabled =
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            _hostCanRestart;
        StepXPulsesTextBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning && !_rotationCenterRunning && !_lowerCameraTeachRunning;
        StepYPulsesTextBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning && !_rotationCenterRunning && !_lowerCameraTeachRunning;
        VelocityTextBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning && !_rotationCenterRunning && !_lowerCameraTeachRunning;
        SettleMillisecondsTextBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning && !_rotationCenterRunning && !_lowerCameraTeachRunning;
        MovePriorityComboBox.IsEnabled = !_calibrationRunning && !_clickMoveRunning && !_rotationCenterRunning && !_lowerCameraTeachRunning;
        CalibrationFilePathTextBox.IsEnabled =
            !_calibrationRunning &&
            !_calibrationFileImporting &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        ChooseCalibrationFileButton.IsEnabled =
            !_calibrationRunning &&
            !_calibrationFileImporting &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        ImportCalibrationFileButton.IsEnabled =
            !_calibrationRunning &&
            !_calibrationFileImporting &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        LoadCalibrationProfileButton.IsEnabled =
            !IsLowerCameraMode &&
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        SaveCalibrationProfileButton.Visibility = profileReadyToSave
            ? Visibility.Visible
            : Visibility.Collapsed;
        SaveCalibrationProfileButton.IsEnabled =
            profileReadyToSave &&
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            !_nozzlePointFinding &&
            !_nozzlePointSaving;
        RecordNozzleDotPositionButton.IsEnabled =
            !IsLowerCameraMode &&
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_nozzlePointFinding &&
            !_nozzlePointSaving &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null &&
            _recordedCenter is not null &&
            calibrationPathValid &&
            File.Exists(calibrationFilePath);
        ReturnToPhotoPositionButton.IsEnabled =
            !IsLowerCameraMode &&
            _recordedCenter is not null &&
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_nozzlePointFinding &&
            !_nozzlePointSaving &&
            _motionController is not null;
        FindNozzlePointsButton.IsEnabled =
            !IsLowerCameraMode &&
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_nozzlePointFinding &&
            !_nozzlePointSaving &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null &&
            _recordedCenter is not null &&
            _nozzleDotPosition is not null &&
            _hostReady &&
            calibrationPathValid &&
            File.Exists(calibrationFilePath);
        var nozzleAssignmentEnabled =
            !IsLowerCameraMode &&
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_nozzlePointFinding &&
            !_nozzlePointSaving &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _hostReady &&
            _recordedCenter is not null &&
            _nozzleDotPosition is not null &&
            _pendingNozzlePointResult is not null;
        AssignPoint1ToNozzle1Button.IsEnabled = nozzleAssignmentEnabled;
        AssignPoint1ToNozzle2Button.IsEnabled = nozzleAssignmentEnabled;
        ClickTargetToolComboBox.IsEnabled =
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        EnableClickMoveCheckBox.IsEnabled =
            !_calibrationProcedureSwitchRunning &&
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_rotationCenterRunning &&
            !_lowerCameraTeachRunning &&
            !_lowerCameraCorrectionTestRunning &&
            !_nozzlePointFinding &&
            !_nozzlePointSaving &&
            _motionController is not null &&
            _hostReady &&
            clickTargetReady &&
            calibrationPathValid &&
            File.Exists(calibrationFilePath);
        StopClickMoveButton.IsEnabled = _clickMoveRunning;
        UpdateNozzleTeachUi();
        ScheduleCalibrationToolbarSync();
        ScheduleCalibrationSidebarSync();
    }

    private void ScheduleCalibrationToolbarSync()
    {
        if (!_hostReady || _shutdown)
        {
            return;
        }

        _calibrationToolbarSyncPending = true;
        if (_calibrationToolbarSyncRunning)
        {
            return;
        }

        _calibrationToolbarSyncRunning = true;
        _ = SyncCalibrationToolbarLoopAsync();
    }

    private async Task SyncCalibrationToolbarLoopAsync()
    {
        try
        {
            while (_calibrationToolbarSyncPending && _hostReady && !_shutdown)
            {
                _calibrationToolbarSyncPending = false;
                try
                {
                    await VisionHost.SetCalibrationToolbarStateAsync(
                        CalibrationFilePathTextBox.Text,
                        IsLowerCameraMode
                            ? GetLowerCameraTeachDataFilePath(ActiveLowerCameraNozzle)
                            : "",
                        CalibrationFilePathTextBox.IsEnabled,
                        ChooseCalibrationFileButton.IsEnabled,
                        ImportCalibrationFileButton.IsEnabled,
                        LoadCalibrationProfileButton.IsEnabled,
                        SaveCalibrationProfileButton.IsEnabled,
                        IsLowerCameraMode,
                        CancellationToken.None);
                }
                catch
                {
                    if (_hostReady && !_shutdown)
                    {
                        _calibrationToolbarSyncPending = true;
                    }

                    break;
                }
            }
        }
        finally
        {
            _calibrationToolbarSyncRunning = false;
        }
    }

    private void ScheduleCalibrationSidebarSync()
    {
        if (!_hostReady || _shutdown)
        {
            return;
        }

        _calibrationSidebarSyncPending = true;
        if (_calibrationSidebarSyncRunning)
        {
            return;
        }

        _calibrationSidebarSyncRunning = true;
        _ = SyncCalibrationSidebarLoopAsync();
    }

    private async Task SyncCalibrationSidebarLoopAsync()
    {
        try
        {
            while (_calibrationSidebarSyncPending && _hostReady && !_shutdown)
            {
                _calibrationSidebarSyncPending = false;
                var movePriority =
                    (MovePriorityComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "X";
                var clickTarget =
                    (ClickTargetToolComboBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "Camera";
                var state = new CalibrationSidebarState(
                    CenterXPulseText.Text,
                    CenterYPulseText.Text,
                    CenterVmText.Text,
                    StepXPulsesTextBox.Text,
                    StepYPulsesTextBox.Text,
                    movePriority,
                    VelocityTextBox.Text,
                    SettleMillisecondsTextBox.Text,
                    NozzleTeachStepText.Text,
                    "点1 = 吸嘴1",
                    NozzleCalibrationStatusText.Text,
                    clickTarget,
                    EnableClickMoveCheckBox.IsChecked == true,
                    ClickMoveStatusText.Text,
                    StartLivePreviewButton.IsEnabled,
                    RecordCenterButton.IsEnabled,
                    StartCalibrationButton.IsEnabled,
                    _calibrationRunning,
                    StepXPulsesTextBox.IsEnabled,
                    FindNozzlePointsButton.IsEnabled,
                    AssignPoint1ToNozzle1Button.IsEnabled,
                    ClickTargetToolComboBox.IsEnabled,
                    EnableClickMoveCheckBox.IsEnabled,
                    _recordedCenter is not null &&
                    !_calibrationRunning &&
                    !_centerSyncRunning &&
                    !_clickMoveRunning &&
                    !_clickMoveConfigurationRunning &&
                    _motionController is not null,
                    StopClickMoveButton.IsEnabled,
                    GetBrushColor(CenterVmText.Foreground, "#12D879"),
                    GetBrushColor(NozzleCalibrationStatusText.Foreground, "#AFC0CD"),
                    GetBrushColor(ClickMoveStatusText.Foreground, "#AFC0CD"),
                    WorkflowStatusText.Text,
                    RecordNozzleDotPositionButton.IsEnabled,
                    IsLowerCameraMode,
                    IsLowerCameraMode ? ActiveLowerCameraNozzleName : "",
                    CanCalculateRotationCenter(),
                    _rotationCenterRunning,
                    _rotationCenterStatus,
                    CanRunLowerCameraTeach(),
                    _lowerCameraTeachRunning,
                    _lowerCameraTeachStatus,
                    CanRunLowerCameraCorrectionTest(),
                    _lowerCameraCorrectionTestRunning,
                    _lowerCameraCorrectionTestStatus,
                    CanImportLowerCameraTeachData(),
                    IsLowerCameraMode
                        ? GetLowerCameraTeachDataFilePath(ActiveLowerCameraNozzle)
                        : "");
                try
                {
                    await VisionHost.SetCalibrationSidebarStateAsync(state, CancellationToken.None);
                }
                catch
                {
                    if (_hostReady && !_shutdown)
                    {
                        _calibrationSidebarSyncPending = true;
                    }

                    break;
                }
            }
        }
        finally
        {
            _calibrationSidebarSyncRunning = false;
        }
    }

    private static string GetBrushColor(Brush brush, string fallback)
    {
        return brush is SolidColorBrush solidColorBrush
            ? solidColorBrush.Color.ToString()
            : fallback;
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
        ScheduleCalibrationSidebarSync();
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
        ScheduleCalibrationSidebarSync();
    }

    private void SetNozzleCalibrationStatus(string message, WorkflowStatus status)
    {
        NozzleCalibrationStatusText.Text = message;
        NozzleCalibrationStatusText.Foreground = new SolidColorBrush(status switch
        {
            WorkflowStatus.Success => Color.FromRgb(73, 209, 125),
            WorkflowStatus.Error => Color.FromRgb(242, 122, 128),
            WorkflowStatus.Running => Color.FromRgb(88, 165, 255),
            _ => Color.FromRgb(143, 178, 201)
        });
        ScheduleCalibrationSidebarSync();
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
