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
    private const int FirstSetXHardwareAxisNo = VisionCalibrationService.FirstSetXHardwareAxisNo;
    private const int FirstSetYHardwareAxisNo = VisionCalibrationService.FirstSetYHardwareAxisNo;
    private const double PulsesPerVisionUnit = VisionCalibrationService.PulsesPerVisionUnit;
    private const double DefaultPositionTolerancePulses = 10d;
    private static readonly string DefaultCalibrationDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "标定文件");
    private static readonly string DefaultCalibrationFilePath = Path.Combine(
        DefaultCalibrationDirectory,
        "第一套XY标定.xml");
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
    private bool _calibrationRunning;
    private bool _centerSyncRunning;
    private bool _clickMoveRunning;
    private bool _clickMoveConfigurationRunning;
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

    public void AttachInspectionDisplayHost(IntPtr displayHostWindow)
    {
        VisionHost.AttachDisplayHost(displayHostWindow);
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

    private async void RecordCameraToolPoint_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            _nozzleTeachCameraPosition = motionController.CaptureCalibrationCenter(
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo);
            _nozzleTeachTool = VisionTargetTool.Nozzle1;
            _uiSettings.NozzleOffsetCalibrated = false;
            _uiSettings.Nozzle2OffsetCalibrated = false;
            _uiSettings.CalibrationProfilePath = "";
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
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo);
            var offsetX = nozzlePosition.ActualX - cameraPosition.ActualX;
            var offsetY = nozzlePosition.ActualY - cameraPosition.ActualY;
            if (!double.IsFinite(offsetX) || !double.IsFinite(offsetY))
            {
                throw new InvalidOperationException("计算得到的吸嘴偏移无效。");
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
            SetClickMoveCheckedNoEvent(false);
            _uiSettings.NozzleOffsetCalibrated = false;
            _uiSettings.Nozzle2OffsetCalibrated = false;
            _uiSettings.CalibrationProfilePath = "";
            _nozzle1ClickVerified = false;
            _nozzle2ClickVerified = false;
            _visionCalibration.Save();
            UpdateCalibrationProfilePathDisplay();
            UpdateNozzleCalibrationDisplay();
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
        var currentDirectory = Path.GetDirectoryName(_uiSettings.CalibrationProfilePath);
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

    private void SaveCalibrationProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = SaveCurrentCalibrationProfile();
            SetWorkflowStatus(
                $"第一套XY标定配置已保存：{Path.GetFileName(path)}",
                WorkflowStatus.Success);
        }
        catch (Exception exception)
        {
            SetWorkflowStatus($"保存配置失败：{exception.Message}", WorkflowStatus.Error);
        }

        UpdateCommandState();
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
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo);
            var moveDeltaX = center.ActualX - current.ActualX;
            var moveDeltaY = center.ActualY - current.ActualY;

            _clickMoveCancellation = new CancellationTokenSource();
            _clickMoveRunning = true;
            UpdateCommandState();
            SetClickMoveStatus(
                $"相机正在回到标定中心：X={center.ActualX:0.###}、Y={center.ActualY:0.###} pulse…",
                WorkflowStatus.Running);

            var actual = await motionController.MoveCalibrationAxesToAsync(
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo,
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

        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("运动控制组件尚未连接。");
            var velocity = ParsePositiveDouble(VelocityTextBox.Text, "点击移动速度");
            var current = motionController.CaptureCalibrationCenter(
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo);
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
                FirstSetXHardwareAxisNo,
                FirstSetYHardwareAxisNo,
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

            var verificationMessage = _nozzle1ClickVerified && _nozzle2ClickVerified
                ? "双吸嘴验证完成，请关闭点击移动后保存配置。"
                : _nozzle1ClickVerified
                    ? "请继续验证吸嘴2。"
                    : "请继续验证吸嘴1。";
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
        StepXPulsesTextBox.Text = FormatPositiveSetting(_uiSettings.StepXPulses, 100_000);
        StepYPulsesTextBox.Text = FormatPositiveSetting(_uiSettings.StepYPulses, 100_000);
        VelocityTextBox.Text = FormatPositiveSetting(_uiSettings.VelocityPulsesPerSecond, 100_000);
        SettleMillisecondsTextBox.Text = Math.Max(0, _uiSettings.SettleMilliseconds)
            .ToString(CultureInfo.CurrentCulture);

        var priority = string.Equals(_uiSettings.MovePriority, "Y", StringComparison.OrdinalIgnoreCase)
            ? "Y"
            : "X";
        MovePriorityComboBox.SelectedItem = MovePriorityComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag as string, priority, StringComparison.Ordinal));

        CalibrationFilePathTextBox.Text =
            TryGetCalibrationFilePath(_uiSettings.CalibrationFilePath, out var savedPath)
                ? savedPath
                : DefaultCalibrationFilePath;
        var targetTool = VisionCalibrationService.ParseTargetTool(_uiSettings.ClickTargetTool);
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
        if (_uiSettings.NozzleOffsetCalibrated &&
            (!double.IsFinite(_uiSettings.NozzleOffsetXPulses) ||
             !double.IsFinite(_uiSettings.NozzleOffsetYPulses)))
        {
            _uiSettings.NozzleOffsetCalibrated = false;
        }

        if (_uiSettings.Nozzle2OffsetCalibrated &&
            (!double.IsFinite(_uiSettings.Nozzle2OffsetXPulses) ||
             !double.IsFinite(_uiSettings.Nozzle2OffsetYPulses)))
        {
            _uiSettings.Nozzle2OffsetCalibrated = false;
        }

        var nozzle1Text = _uiSettings.NozzleOffsetCalibrated
            ? $"吸嘴1：X {_uiSettings.NozzleOffsetXPulses:0.###}　Y {_uiSettings.NozzleOffsetYPulses:0.###} pulse"
            : "吸嘴1：未标定";
        var nozzle2Text = _uiSettings.Nozzle2OffsetCalibrated
            ? $"吸嘴2：X {_uiSettings.Nozzle2OffsetXPulses:0.###}　Y {_uiSettings.Nozzle2OffsetYPulses:0.###} pulse"
            : "吸嘴2：未标定";
        var status = _uiSettings.NozzleOffsetCalibrated && _uiSettings.Nozzle2OffsetCalibrated
            ? WorkflowStatus.Success
            : WorkflowStatus.Ready;
        SetNozzleCalibrationStatus($"{nozzle1Text}\n{nozzle2Text}", status);
    }

    private string SaveCurrentCalibrationProfile()
    {
        if (!_uiSettings.NozzleOffsetCalibrated || !_uiSettings.Nozzle2OffsetCalibrated)
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
            CalibrationFilePath = calibrationFilePath,
            StepXPulses = _uiSettings.StepXPulses,
            StepYPulses = _uiSettings.StepYPulses,
            VelocityPulsesPerSecond = _uiSettings.VelocityPulsesPerSecond,
            SettleMilliseconds = _uiSettings.SettleMilliseconds,
            MovePriority = _uiSettings.MovePriority,
            Nozzle1Calibrated = true,
            Nozzle1OffsetXPulses = _uiSettings.NozzleOffsetXPulses,
            Nozzle1OffsetYPulses = _uiSettings.NozzleOffsetYPulses,
            Nozzle2Calibrated = true,
            Nozzle2OffsetXPulses = _uiSettings.Nozzle2OffsetXPulses,
            Nozzle2OffsetYPulses = _uiSettings.Nozzle2OffsetYPulses
        };
        _profileStore.Save(profilePath, profile);
        _uiSettings.CalibrationProfilePath = profilePath;
        _visionCalibration.Save();
        UpdateCalibrationProfilePathDisplay();
        return profilePath;
    }

    private void ApplyCalibrationProfile(VisionCalibrationProfile profile, string profilePath)
    {
        _uiSettings.StepXPulses = profile.StepXPulses;
        _uiSettings.StepYPulses = profile.StepYPulses;
        _uiSettings.VelocityPulsesPerSecond = profile.VelocityPulsesPerSecond;
        _uiSettings.SettleMilliseconds = profile.SettleMilliseconds;
        _uiSettings.MovePriority = string.Equals(profile.MovePriority, "Y", StringComparison.OrdinalIgnoreCase)
            ? "Y"
            : "X";
        _uiSettings.CalibrationFilePath = Path.GetFullPath(profile.CalibrationFilePath);
        _uiSettings.CalibrationProfilePath = Path.GetFullPath(profilePath);
        _uiSettings.NozzleOffsetCalibrated = profile.Nozzle1Calibrated;
        _uiSettings.NozzleOffsetXPulses = profile.Nozzle1OffsetXPulses;
        _uiSettings.NozzleOffsetYPulses = profile.Nozzle1OffsetYPulses;
        _uiSettings.Nozzle2OffsetCalibrated = profile.Nozzle2Calibrated;
        _uiSettings.Nozzle2OffsetXPulses = profile.Nozzle2OffsetXPulses;
        _uiSettings.Nozzle2OffsetYPulses = profile.Nozzle2OffsetYPulses;
        _uiSettings.ClickTargetTool = "Nozzle1";
        _nozzleTeachCameraPosition = null;
        _nozzleTeachTool = null;
        _nozzle1ClickVerified = false;
        _nozzle2ClickVerified = false;

        StepXPulsesTextBox.Text = FormatPositiveSetting(_uiSettings.StepXPulses, 100_000);
        StepYPulsesTextBox.Text = FormatPositiveSetting(_uiSettings.StepYPulses, 100_000);
        VelocityTextBox.Text = FormatPositiveSetting(_uiSettings.VelocityPulsesPerSecond, 100_000);
        SettleMillisecondsTextBox.Text = _uiSettings.SettleMilliseconds.ToString(CultureInfo.CurrentCulture);
        MovePriorityComboBox.SelectedItem = MovePriorityComboBox.Items
            .OfType<ComboBoxItem>()
            .First(item => string.Equals(item.Tag as string, _uiSettings.MovePriority, StringComparison.Ordinal));
        CalibrationFilePathTextBox.Text = _uiSettings.CalibrationFilePath;
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

        var path = _uiSettings.CalibrationProfilePath?.Trim() ?? "";
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
                _uiSettings.StepXPulses = stepX;
            }

            if (TryParseFiniteDouble(StepYPulsesTextBox.Text, out var stepY) && stepY > 0)
            {
                _uiSettings.StepYPulses = stepY;
            }

            if (TryParseFiniteDouble(VelocityTextBox.Text, out var velocity) && velocity > 0)
            {
                _uiSettings.VelocityPulsesPerSecond = velocity;
            }

            if (int.TryParse(
                    SettleMillisecondsTextBox.Text,
                    NumberStyles.Integer,
                    CultureInfo.CurrentCulture,
                    out var settleMilliseconds) &&
                settleMilliseconds >= 0)
            {
                _uiSettings.SettleMilliseconds = settleMilliseconds;
            }

            _uiSettings.MovePriority =
                (MovePriorityComboBox.SelectedItem as ComboBoxItem)?.Tag as string == "Y"
                    ? "Y"
                    : "X";
            _uiSettings.ClickTargetTool =
                VisionCalibrationService.ToSettingsValue(GetSelectedTargetTool());
            if (TryGetCalibrationFilePath(CalibrationFilePathTextBox.Text, out var calibrationPath))
            {
                _uiSettings.CalibrationFilePath = calibrationPath;
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
            CalibrationFilePathTextBox is null ||
            RecordCameraToolPointButton is null ||
            ImportCalibrationFileButton is null ||
            LoadCalibrationProfileButton is null ||
            SaveCalibrationProfileButton is null ||
            ClickTargetToolComboBox is null)
        {
            return;
        }

        var calibrationPathValid = TryGetCalibrationFilePath(
            CalibrationFilePathTextBox.Text,
            out var calibrationFilePath);
        var clickTargetReady = _visionCalibration.IsToolCalibrated(GetSelectedTargetTool());
        var profileReadyToSave =
            _uiSettings.NozzleOffsetCalibrated &&
            _uiSettings.Nozzle2OffsetCalibrated &&
            _nozzle1ClickVerified &&
            _nozzle2ClickVerified &&
            calibrationPathValid &&
            File.Exists(calibrationFilePath);

        RecordCenterButton.IsEnabled =
            !_calibrationRunning &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null;
        StartCalibrationButton.IsEnabled =
            !_calibrationRunning &&
            !_calibrationFileImporting &&
            !_centerSyncRunning &&
            !_clickMoveRunning &&
            EnableClickMoveCheckBox.IsChecked != true &&
            _motionController is not null &&
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
            EnableClickMoveCheckBox.IsChecked != true &&
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
