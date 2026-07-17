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

public partial class VisualCalibrationPage : UserControl
{
    private const double PulsesPerVisionUnit = VisionCalibrationService.PulsesPerVisionUnit;
    private const double DefaultPositionTolerancePulses = 10d;
    private static readonly string DefaultCalibrationDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "标定文件");
    private static readonly string DefaultCalibrationFilePath = Path.Combine(
        DefaultCalibrationDirectory,
        "第一套XY标定.xml");
    private static readonly string DefaultSecondCalibrationFilePath = Path.Combine(
        DefaultCalibrationDirectory,
        "第二套XY标定.xml");
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private readonly VisionCalibrationProfileStore _profileStore = new();
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
    private bool _nozzle1ClickVerified;
    private bool _nozzle2ClickVerified;
    private bool _suppressClickMoveModeEvent;
    private MotionControlPage? _motionController;
    private CalibrationCenterPosition? _recordedCenter;
    private CalibrationCenterPosition? _nozzleTeachCameraPosition;
    private VisionTargetTool? _nozzleTeachTool;
    private CancellationTokenSource? _calibrationCancellation;
    private CancellationTokenSource? _clickMoveCancellation;
    private VisualCalibrationSettings _uiSettings = VisionCalibrationService.Shared.Settings;
    private bool _settingsLoaded;

    private VisionCalibrationAxisPair ActiveAxisPair => _visionCalibration.ActiveAxisPair;

    private VisionCalibrationAxisSet ActiveAxisSet => _visionCalibration.ActiveAxisSet;

    public VisualCalibrationPage()
    {
        InitializeComponent();
        _settingsSaveTimer.Tick += SettingsSaveTimer_Tick;
        Unloaded += VisualCalibrationPage_Unloaded;
        EnsureDefaultCalibrationDirectory();
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

    private void AxisSet_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_settingsLoaded)
        {
            return;
        }

        if (_calibrationRunning || _clickMoveRunning || _centerSyncRunning)
        {
            AxisSetComboBox.SelectedItem = AxisSetComboBox.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => string.Equals(
                    item.Tag as string,
                    VisionCalibrationService.ToSettingsValue(ActiveAxisSet),
                    StringComparison.Ordinal));
            return;
        }

        SaveCalibrationSettingsNoThrow();
        _visionCalibration.ActiveAxisSet = VisionCalibrationService.ParseAxisSet(
            (AxisSetComboBox.SelectedItem as ComboBoxItem)?.Tag as string);
        _recordedCenter = null;
        _nozzleTeachCameraPosition = null;
        _nozzleTeachTool = null;
        _nozzle1ClickVerified = false;
        _nozzle2ClickVerified = false;
        LoadCalibrationSettings();
        _visionCalibration.Save();
        UpdateVisionOffsetPreview();
        UpdateCommandState();
    }

    private void SelectAxisSetComboBox()
    {
        if (AxisSetComboBox is null)
        {
            return;
        }

        var value = VisionCalibrationService.ToSettingsValue(ActiveAxisSet);
        AxisSetComboBox.SelectedItem = AxisSetComboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag as string, value, StringComparison.Ordinal))
            ?? AxisSetComboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private string GetDefaultCalibrationFilePath()
    {
        return ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? DefaultSecondCalibrationFilePath
            : DefaultCalibrationFilePath;
    }

    private void ResetActiveNozzleCalibration()
    {
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

    private bool ActiveNozzle1Calibrated => ActiveAxisSet == VisionCalibrationAxisSet.Second
        ? _uiSettings.SecondNozzleOffsetCalibrated
        : _uiSettings.NozzleOffsetCalibrated;

    private bool ActiveNozzle2Calibrated => ActiveAxisSet == VisionCalibrationAxisSet.Second
        ? _uiSettings.SecondNozzle2OffsetCalibrated
        : _uiSettings.Nozzle2OffsetCalibrated;

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
        get => ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondCalibrationProfilePath
            : _uiSettings.CalibrationProfilePath;
        set
        {
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
        get => ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondCalibrationFilePath
            : _uiSettings.CalibrationFilePath;
        set
        {
            if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
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
        get => ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondClickTargetTool
            : _uiSettings.ClickTargetTool;
        set
        {
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
        if (EnableClickMoveCheckBox.IsChecked == true)
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

    private async void RecordCameraToolPoint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            _nozzleTeachCameraPosition = motionController.CaptureCalibrationCenter(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo);
            _nozzleTeachTool = VisionTargetTool.Nozzle1;
            ResetActiveNozzleCalibration();
            _nozzle1ClickVerified = false;
            _nozzle2ClickVerified = false;
            SaveCalibrationSettingsNoThrow();
            UpdateCalibrationProfilePathDisplay();
            UpdateNozzleTeachUi();
            SetNozzleCalibrationStatus(
                "十字已记录，请手动让吸嘴1对准同一标记。",
                WorkflowStatus.Running);

            if (EnableClickMoveCheckBox.IsChecked == true)
            {
                SetClickMoveCheckedNoEvent(false);
                _ = await ConfigureClickMoveModeAsync(false);
            }
        }
        catch (Exception exception)
        {
            _nozzleTeachCameraPosition = null;
            _nozzleTeachTool = null;
            SetNozzleCalibrationStatus($"记录十字位置失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
            UpdateCommandState();
        }
    }

    private void RecordNozzleToolPoint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var cameraPosition = _nozzleTeachCameraPosition
                ?? throw new InvalidOperationException("请先记录十字对准位置。");
            var nozzleTool = _nozzleTeachTool
                ?? throw new InvalidOperationException("请选择需要标定的吸嘴。");
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            var nozzlePosition = motionController.CaptureCalibrationCenter(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo);
            var offsetX = nozzlePosition.ActualX - cameraPosition.ActualX;
            var offsetY = nozzlePosition.ActualY - cameraPosition.ActualY;
            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            {
                throw new InvalidOperationException("计算得到的吸嘴偏移无效。");
            }

            SetActiveNozzleOffset(nozzleTool, offsetX, offsetY);
            if (nozzleTool != VisionTargetTool.Nozzle2)
            {
                _nozzleTeachTool = VisionTargetTool.Nozzle2;
                SaveCalibrationSettingsNoThrow();
                UpdateNozzleTeachUi();
                SetNozzleCalibrationStatus(
                    "吸嘴1已记录，请直接让吸嘴2对准同一标记，不用再对十字。",
                    WorkflowStatus.Running);
                return;
            }

            _nozzleTeachCameraPosition = null;
            _nozzleTeachTool = null;
            SelectClickTargetTool(VisionTargetTool.Nozzle1);
            SaveCalibrationSettingsNoThrow();
            UpdateNozzleTeachUi();
            SetNozzleCalibrationStatus(
                "双吸嘴完成，请分别点击验证吸嘴1、吸嘴2，确认后保存配置。",
                WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            SetNozzleCalibrationStatus($"记录吸嘴位置失败：{exception.Message}", WorkflowStatus.Error);
        }
        finally
        {
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
                completionMessage + "；轴1/2已回到中心，请继续第4步双吸嘴对位。",
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
            _nozzleTeachCameraPosition = null;
            _nozzleTeachTool = null;
            ResetActiveNozzleCalibration();
            _nozzle1ClickVerified = false;
            _nozzle2ClickVerified = false;
            _visionCalibration.Save();
            UpdateCalibrationProfilePathDisplay();
            UpdateNozzleTeachUi();
            UpdateNozzleCalibrationDisplay();

            SelectClickTargetTool(VisionTargetTool.Camera);
            SetClickMoveCheckedNoEvent(true);
            if (!await ConfigureClickMoveModeAsync(true))
            {
                SetClickMoveCheckedNoEvent(false);
            }
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
            SetWorkflowStatus("双吸嘴配置已加载，可在第5步选择吸嘴点击测试。", WorkflowStatus.Success);
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
            var path = SaveCurrentCalibrationProfile();
            success = true;
            feedbackMessage = $"双吸嘴标定配置已保存：{path}";
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
            case "RecordCamera":
                RecordCameraToolPoint_Click(this, new RoutedEventArgs());
                break;
            case "RecordNozzle":
                RecordNozzleToolPoint_Click(this, new RoutedEventArgs());
                break;
            case "ReturnCameraCenter":
                ReturnCameraToCenter_Click(this, new RoutedEventArgs());
                break;
            case "StopClickMove":
                StopClickMove_Click(this, new RoutedEventArgs());
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
                $"请先完成第4步{GetToolDisplayName(targetTool)}对位。",
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
                var targetTool = GetSelectedTargetTool();
                if (!_visionCalibration.IsToolCalibrated(targetTool))
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
            var velocity = ParsePositiveDouble(VelocityTextBox.Text, "点击移动速度");
            var current = motionController.CaptureCalibrationCenter(
                ActiveAxisPair.XHardwareAxisNo,
                ActiveAxisPair.YHardwareAxisNo);
            var deltaX = (e.CenterTransformedX - e.TransformedX) * PulsesPerVisionUnit;
            var deltaY = (e.CenterTransformedY - e.TransformedY) * PulsesPerVisionUnit;
            var targetTool = GetSelectedTargetTool();
            var target = _visionCalibration.CalculateTarget(
                current.ActualX + deltaX,
                current.ActualY + deltaY,
                targetTool);
            var targetX = target.X;
            var targetY = target.Y;
            var targetName = GetToolDisplayName(targetTool);

            if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
            {
                throw new InvalidOperationException("标定转换后的轴目标无效。");
            }

            _clickMoveCancellation = new CancellationTokenSource();
            _clickMoveRunning = true;
            UpdateCommandState();
            SetClickMoveStatus(
                $"点击({e.PixelX}, {e.PixelY})；正在把{targetName}中心移动到 " +
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
                VisionTargetTool.Camera => "相机中心已对准目标，请点击“记录十字”。",
                _ when finishNozzleVerification =>
                    "双吸嘴验证完成，正在退出点击移动。",
                _ when _nozzle1ClickVerified => "请继续验证吸嘴2。",
                _ => "请继续验证吸嘴1。"
            };
            if (targetTool == VisionTargetTool.Camera)
            {
                SetNozzleCalibrationStatus(
                    "相机中心已对准目标，请记录十字位置，再手动移动吸嘴1对准同一目标。",
                    WorkflowStatus.Running);
            }

            SetClickMoveStatus(
                $"{targetName}验证完成：X={actual.ActualX:0.###}、Y={actual.ActualY:0.###}；" +
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
        var stepX = ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondStepXPulses
            : _uiSettings.StepXPulses;
        var stepY = ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondStepYPulses
            : _uiSettings.StepYPulses;
        var velocity = ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondVelocityPulsesPerSecond
            : _uiSettings.VelocityPulsesPerSecond;
        var settleMilliseconds = ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondSettleMilliseconds
            : _uiSettings.SettleMilliseconds;
        var movePriority = ActiveAxisSet == VisionCalibrationAxisSet.Second
            ? _uiSettings.SecondMovePriority
            : _uiSettings.MovePriority;

        StepXPulsesTextBox.Text = FormatPositiveSetting(stepX, 100_000);
        StepYPulsesTextBox.Text = FormatPositiveSetting(stepY, 100_000);
        VelocityTextBox.Text = FormatPositiveSetting(velocity, 100_000);
        SettleMillisecondsTextBox.Text = Math.Max(0, settleMilliseconds)
            .ToString(CultureInfo.CurrentCulture);

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
        if (NozzleTeachStepText is null || RecordNozzleToolPointButton is null)
        {
            return;
        }

        if (_nozzleTeachCameraPosition is null || _nozzleTeachTool is null)
        {
            NozzleTeachStepText.Text = "等待记录十字";
            RecordNozzleToolPointButton.Content = "2 记录吸嘴1";
            return;
        }

        if (_nozzleTeachTool == VisionTargetTool.Nozzle1)
        {
            NozzleTeachStepText.Text = "当前：吸嘴1";
            RecordNozzleToolPointButton.Content = "2 记录吸嘴1";
            return;
        }

        NozzleTeachStepText.Text = "当前：吸嘴2";
        RecordNozzleToolPointButton.Content = "3 记录吸嘴2";
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

    private string SaveCurrentCalibrationProfile()
    {
        if (!ActiveNozzle1Calibrated || !ActiveNozzle2Calibrated)
        {
            throw new InvalidOperationException("请先按顺序完成吸嘴1和吸嘴2对位。");
        }

        if (!_nozzle1ClickVerified || !_nozzle2ClickVerified)
        {
            throw new InvalidOperationException("请先分别完成吸嘴1和吸嘴2的点击移动验证。");
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
            Nozzle1Calibrated = true,
            Nozzle1OffsetXPulses = ActiveNozzle1OffsetX,
            Nozzle1OffsetYPulses = ActiveNozzle1OffsetY,
            Nozzle2Calibrated = true,
            Nozzle2OffsetXPulses = ActiveNozzle2OffsetX,
            Nozzle2OffsetYPulses = ActiveNozzle2OffsetY
        };
        _profileStore.Save(profilePath, profile);
        ActiveCalibrationProfilePath = profilePath;
        _visionCalibration.Save();
        UpdateCalibrationProfilePathDisplay();
        return profilePath;
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
        _nozzleTeachCameraPosition = null;
        _nozzleTeachTool = null;
        _nozzle1ClickVerified = false;
        _nozzle2ClickVerified = false;

        StepXPulsesTextBox.Text = FormatPositiveSetting(profile.StepXPulses, 100_000);
        StepYPulsesTextBox.Text = FormatPositiveSetting(profile.StepYPulses, 100_000);
        VelocityTextBox.Text = FormatPositiveSetting(profile.VelocityPulsesPerSecond, 100_000);
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
                if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
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
                if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
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
                if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
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
                if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
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
            if (ActiveAxisSet == VisionCalibrationAxisSet.Second)
            {
                _uiSettings.SecondMovePriority = movePriority;
            }
            else
            {
                _uiSettings.MovePriority = movePriority;
            }

            ActiveClickTargetTool = VisionCalibrationService.ToSettingsValue(GetSelectedTargetTool());
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
            RecordCameraToolPointButton is null ||
            ImportCalibrationFileButton is null ||
            LoadCalibrationProfileButton is null ||
            SaveCalibrationProfileButton is null ||
            AxisSetComboBox is null ||
            ClickTargetToolComboBox is null)
        {
            return;
        }

        var calibrationPathValid = TryGetCalibrationFilePath(
            CalibrationFilePathTextBox.Text,
            out var calibrationFilePath);
        var clickTargetReady = _visionCalibration.IsToolCalibrated(GetSelectedTargetTool());
        var profileReadyToSave =
            ActiveNozzle1Calibrated &&
            ActiveNozzle2Calibrated &&
            _nozzle1ClickVerified &&
            _nozzle2ClickVerified &&
            calibrationPathValid &&
            File.Exists(calibrationFilePath);

        RecordCenterButton.IsEnabled =
            !_calibrationRunning &&
            !_livePreviewStarting &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null;
        StartLivePreviewButton.IsEnabled =
            !_calibrationRunning &&
            !_livePreviewStarting &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _hostReady;
        StartCalibrationButton.IsEnabled =
            !_calibrationRunning &&
            !_livePreviewStarting &&
            !_calibrationFileImporting &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null &&
            _hostReady &&
            calibrationPathValid;
        StopCalibrationButton.IsEnabled = _calibrationRunning;
        AxisSetComboBox.IsEnabled =
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            !_calibrationFileImporting;
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
            !_calibrationFileImporting &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        ChooseCalibrationFileButton.IsEnabled =
            !_calibrationRunning &&
            !_calibrationFileImporting &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        ImportCalibrationFileButton.IsEnabled =
            !_calibrationRunning &&
            !_calibrationFileImporting &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        LoadCalibrationProfileButton.IsEnabled =
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
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
            _nozzleTeachCameraPosition is null;
        RecordCameraToolPointButton.IsEnabled =
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            _motionController is not null;
        RecordNozzleToolPointButton.IsEnabled =
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null &&
            _nozzleTeachCameraPosition is not null;
        ClickTargetToolComboBox.IsEnabled =
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            EnableClickMoveCheckBox.IsChecked != true;
        EnableClickMoveCheckBox.IsEnabled =
            !_calibrationRunning &&
            !_clickMoveRunning &&
            !_clickMoveConfigurationRunning &&
            _motionController is not null &&
            _nozzleTeachCameraPosition is null &&
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
                        CalibrationFilePathTextBox.IsEnabled,
                        ChooseCalibrationFileButton.IsEnabled,
                        ImportCalibrationFileButton.IsEnabled,
                        LoadCalibrationProfileButton.IsEnabled,
                        SaveCalibrationProfileButton.IsEnabled,
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
                    RecordNozzleToolPointButton.Content?.ToString() ?? "2 记录吸嘴1",
                    NozzleCalibrationStatusText.Text,
                    clickTarget,
                    EnableClickMoveCheckBox.IsChecked == true,
                    ClickMoveStatusText.Text,
                    StartLivePreviewButton.IsEnabled,
                    RecordCenterButton.IsEnabled,
                    StartCalibrationButton.IsEnabled,
                    _calibrationRunning,
                    StepXPulsesTextBox.IsEnabled,
                    RecordCameraToolPointButton.IsEnabled,
                    RecordNozzleToolPointButton.IsEnabled,
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
                    WorkflowStatusText.Text);
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
