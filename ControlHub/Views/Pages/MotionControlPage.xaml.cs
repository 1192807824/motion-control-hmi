using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ControlHub.Models;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;

namespace ControlHub.Views.Pages;

public partial class MotionControlPage : UserControl
{
    private const ushort RingRedundancyDisconnectedWarning = 0x0228;
    private readonly IMotionCard _motionCard;
    private readonly MotionCardOptions _motionOptions;
    private readonly MotionCardOptionsStore _motionOptionsStore = new();
    private readonly AxisSettingsStore _axisSettingsStore = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly HashSet<string> _activeAlarmKeys = [];
    private readonly Dictionary<int, DateTime> _homeDeadlines = [];
    private readonly Dictionary<int, DateTime> _homeIssuedAtUtc = [];
    private readonly HashSet<int> _homeObservedMovingAxisNos = [];
    private readonly string? _configurationError;
    private CancellationTokenSource? _homeSequenceCancellation;
    private CancellationTokenSource? _positionMoveCancellation;
    private CancellationTokenSource? _axisSelectionFeedbackCancellation;
    private CancellationTokenSource? _calibrationMotionCancellation;
    private Stopwatch? _commandStopwatch;
    private int? _activeJogAxisNo;
    private FrameworkElement? _activeJogInputOwner;
    private int? _activePositionAxisNo;
    private double? _activePositionTarget;
    private DateTime? _activePositionIssuedAtUtc;
    private DateTime? _activePositionDeadlineUtc;
    private double _activePositionTolerance;
    private int _activePositionTimeoutMilliseconds;
    private bool _activePositionObservedMoving;
    private int? _operatorStopRequestedAxisNo;
    private bool _operatorImmediateStopRequested;
    private int? _failedPositionAxisNo;
    private readonly HashSet<int> _pendingStopAxisNos = [];
    private readonly Dictionary<int, DateTime> _stopConfirmationDeadlines = [];
    private readonly HashSet<int> _emergencyStopPendingAxisNos = [];
    private ushort? _connectedCardNo;
    private IoPointKind _ioMode = IoPointKind.DigitalInput;
    private MotionWorkbenchMode _workbenchMode = MotionWorkbenchMode.ContinuousJog;
    private CommandStage _commandStage = CommandStage.Ready;
    private bool _initialized;
    private bool _polling;
    private bool _closed;
    private bool _motionSafetyLock;
    private bool _loadingAxisSettings;
    private bool _homeConfigurationSaveHealthy = true;
    private bool _calibrationOperationActive;
    private string? _motionSafetyLockReason;
    private Window? _ownerWindow;

    public MotionControlPage()
    {
        InitializeComponent();

        try
        {
            _motionOptions = _motionOptionsStore.Load();
        }
        catch (Exception exception)
        {
            _motionOptions = new MotionCardOptions();
            _configurationError = $"运动配置读取失败：{exception.Message}";
        }

        Tuning.LoadFrom(_motionOptions.MoveProfile);
        HomeTuning.LoadFrom(_motionOptions.HomeProfile, _motionOptions.HomeTimeoutSeconds, sequenceOrder: 0);
        RelativeModeRadio.IsChecked = !Tuning.AbsolutePositionMode;
        AbsoluteModeRadio.IsChecked = Tuning.AbsolutePositionMode;

        _motionCard = MotionCardFactory.Create(_motionOptions);
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_motionOptions.PollIntervalMilliseconds)
        };
        _pollTimer.Tick += (_, _) => PollMotionState();
        Loaded += MotionControlPage_Loaded;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    public MotionTuningSettings Tuning { get; } = new();

    public MotionHomeTuningSettings HomeTuning { get; } = new();

    private ObservableCollection<AxisStatus>? Axes => ViewModel?.Axes;

    private AxisStatus? SelectedAxis
    {
        get => ViewModel?.SelectedAxis;
        set
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.SelectedAxis = value;
            }
        }
    }

    public bool TryShutdown(out string failureMessage)
    {
        failureMessage = "";
        if (_closed)
        {
            return true;
        }

        _homeSequenceCancellation?.Cancel();
        _positionMoveCancellation?.Cancel();
        _axisSelectionFeedbackCancellation?.Cancel();
        _calibrationMotionCancellation?.Cancel();

        if (_motionCard.IsOpen)
        {
            try
            {
                _motionCard.EmergencyStop();
            }
            catch (Exception exception)
            {
                ActivateMotionSafetyLock(
                    "程序退出时全轴急停命令下发失败",
                    exception,
                    "SHUTDOWN-EMERGENCY-STOP-FAILED");
                failureMessage =
                    "程序退出前未能下发全轴急停，控制卡将保持打开，窗口不会关闭。\n\n" +
                    $"原因：{FormatException(exception)}\n\n" +
                    "请先使用硬件急停并确认机构完全停止，再排查控制卡通讯；确认安全后重新尝试关闭。";
                return false;
            }

            if (!TryConfirmAllHardwareAxesStopped(out var confirmationFailure))
            {
                ActivateMotionSafetyLock(
                    "程序退出时未能确认全部硬件轴停止",
                    null,
                    "SHUTDOWN-STOP-CONFIRM-FAILED");
                failureMessage =
                    "已发送全轴急停，但程序无法确认所有硬件轴都已停止。控制卡将保持打开，窗口不会关闭。\n\n" +
                    $"原因：{confirmationFailure}\n\n" +
                    "请先使用硬件急停并目视确认机构完全停止，再恢复控制卡通讯并重新尝试关闭。";
                return false;
            }
        }

        try
        {
            if (!TryCommitPersistentInputBindings())
            {
                throw new InvalidDataException("存在无法识别的输入，请修正红框字段后再关闭程序。");
            }

            CommitAndSaveAxisSettingsOrThrow();
            SaveMotionConfigurationOrThrow();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            var details = FormatException(exception);
            RecordAlarm("SETTINGS-SAVE-BEFORE-CLOSE", details);
            failureMessage =
                "输入参数保存失败，程序暂不关闭，以免下次启动丢失本次设置。\n\n" +
                $"原因：{details}\n\n" +
                "请修正无效参数或检查程序目录写入权限后，再重新关闭。";
            return false;
        }

        _closed = true;
        _pollTimer.Stop();
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated -= OwnerWindow_Deactivated;
            _ownerWindow = null;
        }

        if (_activePositionAxisNo is { } closingPositionAxisNo)
        {
            ClearPositionTracking(closingPositionAxisNo);
        }

        _pendingStopAxisNos.Clear();
        _stopConfirmationDeadlines.Clear();
        _emergencyStopPendingAxisNos.Clear();
        ClearAllHomeTracking();
        _positionMoveCancellation?.Cancel();

        try
        {
            _motionCard.Close();
        }
        catch
        {
            // The process is closing; native resources cannot be recovered here.
        }

        return true;
    }

    public void Shutdown()
    {
        _ = TryShutdown(out _);
    }

    public async Task RunNinePointCalibrationAsync(
        NinePointMotionRequest request,
        Func<NinePointMotionPosition, CancellationToken, Task> captureAsync,
        IProgress<NinePointMotionProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(captureAsync);
        ValidateNinePointMotionRequest(request);

        if (_closed)
        {
            throw new InvalidOperationException("运动控制已经关闭，不能执行标定。 ");
        }

        if (_motionSafetyLock)
        {
            throw new InvalidOperationException($"运动安全锁已激活：{_motionSafetyLockReason ?? "停止安全链异常"}。 ");
        }

        if (!_motionCard.IsOpen)
        {
            throw new InvalidOperationException("运动控制卡尚未连接。 ");
        }

        if (IsAnyMotionWorkflowActive())
        {
            throw new InvalidOperationException("当前存在运动、回零或停止流程，请等待完成后再标定。 ");
        }

        var xAxis = GetCalibrationAxis(request.XHardwareAxisNo, "X");
        var yAxis = GetCalibrationAxis(request.YHardwareAxisNo, "Y");
        var initialX = ReadReadyCalibrationAxis(xAxis, "X");
        var initialY = ReadReadyCalibrationAxis(yAxis, "Y");
        var offsets = new (double X, double Y)[]
        {
            (-request.StepX, -request.StepY),
            (0, -request.StepY),
            (request.StepX, -request.StepY),
            (request.StepX, 0),
            (request.StepX, request.StepY),
            (0, request.StepY),
            (-request.StepX, request.StepY),
            (-request.StepX, 0),
            (0, 0)
        };

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _calibrationMotionCancellation = linkedCancellation;
        _calibrationOperationActive = true;
        var completed = false;

        try
        {
            for (var index = 0; index < offsets.Length; index++)
            {
                linkedCancellation.Token.ThrowIfCancellationRequested();
                var pointNumber = index + 1;
                var targetX = initialX.FeedbackPosition + offsets[index].X;
                var targetY = initialY.FeedbackPosition + offsets[index].Y;
                progress?.Report(new NinePointMotionProgress(
                    index,
                    offsets.Length,
                    $"正在移动到第 {pointNumber}/9 点：({targetX:0.###}, {targetY:0.###})"));

                await MoveCalibrationAxesAsync(
                    xAxis,
                    yAxis,
                    targetX,
                    targetY,
                    request,
                    linkedCancellation.Token);

                if (request.SettleMilliseconds > 0)
                {
                    progress?.Report(new NinePointMotionProgress(
                        index,
                        offsets.Length,
                        $"第 {pointNumber}/9 点已到位，等待机构稳定"));
                    await Task.Delay(request.SettleMilliseconds, linkedCancellation.Token);
                }

                var (actualX, actualY) = ReadSettledCalibrationPosition(
                    xAxis,
                    yAxis,
                    targetX,
                    targetY,
                    request.PositionTolerance);
                var position = new NinePointMotionPosition(
                    pointNumber,
                    targetX,
                    targetY,
                    actualX,
                    actualY);
                await captureAsync(position, linkedCancellation.Token);
                progress?.Report(new NinePointMotionProgress(
                    pointNumber,
                    offsets.Length,
                    $"第 {pointNumber}/9 点采集完成"));
            }

            completed = true;
        }
        finally
        {
            if (!completed && _motionCard.IsOpen)
            {
                StopCalibrationAxesNoThrow(xAxis, yAxis);
            }

            _calibrationOperationActive = false;
            _calibrationMotionCancellation = null;
            UpdateHomeEditorState();
        }
    }

    private static void ValidateNinePointMotionRequest(NinePointMotionRequest request)
    {
        if (request.XHardwareAxisNo < 0 || request.YHardwareAxisNo < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "X、Y 轴号必须有效。 ");
        }

        if (request.XHardwareAxisNo == request.YHardwareAxisNo)
        {
            throw new ArgumentException("X 轴和 Y 轴不能选择同一根轴。", nameof(request));
        }

        if (!double.IsFinite(request.StepX) || Math.Abs(request.StepX) <= double.Epsilon ||
            !double.IsFinite(request.StepY) || Math.Abs(request.StepY) <= double.Epsilon)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "X、Y 每步距离必须是非零有效数值。 ");
        }

        if (!double.IsFinite(request.Velocity) || request.Velocity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "标定速度必须大于 0。 ");
        }

        if (!double.IsFinite(request.PositionTolerance) || request.PositionTolerance <= 0 ||
            request.MoveTimeoutMilliseconds < 100 || request.SettleMilliseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "标定到位容差、超时或稳定等待参数无效。 ");
        }
    }

    private AxisStatus GetCalibrationAxis(int hardwareAxisNo, string coordinateName)
    {
        if (hardwareAxisNo >= _motionCard.AxisCount)
        {
            throw new InvalidOperationException(
                $"{coordinateName} 轴选择了轴 {hardwareAxisNo + 1}，但当前控制卡只有 {_motionCard.AxisCount} 根轴。 ");
        }

        return Axes?.FirstOrDefault(axis => axis.HardwareAxisNo == hardwareAxisNo && axis.IsAvailable)
            ?? throw new InvalidOperationException($"{coordinateName} 轴（轴 {hardwareAxisNo + 1}）当前不可用。 ");
    }

    private MotionAxisSnapshot ReadReadyCalibrationAxis(AxisStatus axis, string coordinateName)
    {
        var snapshot = _motionCard.ReadAxis(axis.HardwareAxisNo);
        ApplySnapshot(axis, snapshot);
        EnsureCalibrationAxisSafe(snapshot, coordinateName);
        if (!snapshot.Homed)
        {
            throw new InvalidOperationException(
                $"{coordinateName} 轴（轴 {axis.HardwareAxisNo + 1}）尚未回零，禁止开始九点标定。 ");
        }

        if (snapshot.IsMoving)
        {
            throw new InvalidOperationException(
                $"{coordinateName} 轴（轴 {axis.HardwareAxisNo + 1}）仍在运动。 ");
        }

        return snapshot;
    }

    private async Task MoveCalibrationAxesAsync(
        AxisStatus xAxis,
        AxisStatus yAxis,
        double targetX,
        double targetY,
        NinePointMotionRequest request,
        CancellationToken cancellationToken)
    {
        var beforeX = _motionCard.ReadAxis(xAxis.HardwareAxisNo);
        var beforeY = _motionCard.ReadAxis(yAxis.HardwareAxisNo);
        EnsureCalibrationAxisSafe(beforeX, "X");
        EnsureCalibrationAxisSafe(beforeY, "Y");

        if (Math.Abs(beforeX.FeedbackPosition - targetX) > request.PositionTolerance)
        {
            _motionCard.MoveAbsolute(xAxis.HardwareAxisNo, targetX, request.Velocity);
        }

        if (Math.Abs(beforeY.FeedbackPosition - targetY) > request.PositionTolerance)
        {
            _motionCard.MoveAbsolute(yAxis.HardwareAxisNo, targetY, request.Velocity);
        }

        xAxis.Target = targetX;
        yAxis.Target = targetY;
        var deadline = DateTime.UtcNow.AddMilliseconds(request.MoveTimeoutMilliseconds);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var currentX = _motionCard.ReadAxis(xAxis.HardwareAxisNo);
            var currentY = _motionCard.ReadAxis(yAxis.HardwareAxisNo);
            ApplySnapshot(xAxis, currentX);
            ApplySnapshot(yAxis, currentY);
            EnsureCalibrationAxisSafe(currentX, "X");
            EnsureCalibrationAxisSafe(currentY, "Y");

            var xInPosition = Math.Abs(currentX.FeedbackPosition - targetX) <= request.PositionTolerance;
            var yInPosition = Math.Abs(currentY.FeedbackPosition - targetY) <= request.PositionTolerance;
            if (!currentX.IsMoving && !currentY.IsMoving && xInPosition && yInPosition)
            {
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"九点标定运动超时。X={currentX.FeedbackPosition:0.###}/{targetX:0.###}，" +
                    $"Y={currentY.FeedbackPosition:0.###}/{targetY:0.###}。 ");
            }

            await Task.Delay(50, cancellationToken);
        }
    }

    private (double X, double Y) ReadSettledCalibrationPosition(
        AxisStatus xAxis,
        AxisStatus yAxis,
        double targetX,
        double targetY,
        double tolerance)
    {
        var settledX = _motionCard.ReadAxis(xAxis.HardwareAxisNo);
        var settledY = _motionCard.ReadAxis(yAxis.HardwareAxisNo);
        ApplySnapshot(xAxis, settledX);
        ApplySnapshot(yAxis, settledY);
        EnsureCalibrationAxisSafe(settledX, "X");
        EnsureCalibrationAxisSafe(settledY, "Y");

        if (settledX.IsMoving || settledY.IsMoving ||
            Math.Abs(settledX.FeedbackPosition - targetX) > tolerance ||
            Math.Abs(settledY.FeedbackPosition - targetY) > tolerance)
        {
            throw new InvalidOperationException(
                $"机构稳定等待后偏离目标。X={settledX.FeedbackPosition:0.###}/{targetX:0.###}，" +
                $"Y={settledY.FeedbackPosition:0.###}/{targetY:0.###}。 ");
        }

        return (settledX.FeedbackPosition, settledY.FeedbackPosition);
    }

    private static void EnsureCalibrationAxisSafe(MotionAxisSnapshot snapshot, string coordinateName)
    {
        if (!snapshot.ServoEnabled)
        {
            throw new InvalidOperationException($"{coordinateName} 轴未使能。 ");
        }

        if (snapshot.Alarm || snapshot.EmergencyInput || snapshot.PositiveLimit || snapshot.NegativeLimit)
        {
            throw new InvalidOperationException(
                $"{coordinateName} 轴存在报警、急停或限位信号：{snapshot.StateText}。 ");
        }
    }

    private void StopCalibrationAxesNoThrow(AxisStatus xAxis, AxisStatus yAxis)
    {
        foreach (var axis in new[] { xAxis, yAxis }.DistinctBy(item => item.HardwareAxisNo))
        {
            try
            {
                _motionCard.Stop(axis.HardwareAxisNo);
            }
            catch (Exception decelerationStopException)
            {
                try
                {
                    _motionCard.Stop(axis.HardwareAxisNo, emergency: true);
                }
                catch (Exception emergencyStopException)
                {
                    ActivateMotionSafetyLock(
                        $"九点标定异常后轴 {axis.HardwareAxisNo + 1} 停止失败",
                        new AggregateException(decelerationStopException, emergencyStopException),
                        $"CALIBRATION-AXIS-{axis.HardwareAxisNo:00}-STOP-FAILED");
                }
            }
        }
    }

    private void MotionControlPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (DesignerProperties.GetIsInDesignMode(this))
        {
            return;
        }

        if (_initialized || _closed)
        {
            return;
        }

        _initialized = true;
        _ownerWindow = Window.GetWindow(this);
        if (_ownerWindow is not null)
        {
            _ownerWindow.Deactivated -= OwnerWindow_Deactivated;
            _ownerWindow.Deactivated += OwnerWindow_Deactivated;
        }

        if (_configurationError is not null)
        {
            HandleInitializationFailure(_configurationError, "运动控制：配置错误");
            return;
        }

        try
        {
            var connection = _motionCard.Open();
            _connectedCardNo = connection.IsSimulation ? null : connection.CardNo;
            if (ViewModel is { } viewModel)
            {
                var detectedCardList = string.Join(
                    " / ",
                    connection.DetectedCards.Select(card =>
                        connection.IsSimulation
                            ? $"仿真 {card.CardNo}"
                            : $"{card.CardNo}(0x{card.CardType:X})"));
                viewModel.MotionDetectedCardsText =
                    $"检测卡：{detectedCardList}｜当前：{connection.CardNo}";
            }

            SetConnectionText(connection.IsSimulation
                ? $"运动控制：仿真模式（{connection.AxisCount} 轴）"
                : $"运动控制：卡 {connection.CardNo} 已连接（检测 {connection.AxisCount} 轴）");
            if (!connection.IsSimulation && connection.DetectedCardCount > 1 && _motionOptions.CardNo is null)
            {
                RecordAlarm(
                    "MOTION-MULTI-CARD",
                    $"检测到多张运动卡 [{string.Join(", ", connection.DetectedCards.Select(card => card.CardNo))}]，当前按默认选择卡 {connection.CardNo}。请在 motion-settings.json 中明确设置 CardNo。");
            }

            if (ViewModel?.AxisSettingsLoadWarning is { } axisSettingsWarning &&
                !string.IsNullOrWhiteSpace(axisSettingsWarning))
            {
                RecordAlarmOnce(
                    "axis-settings-load",
                    "AXIS-SETTINGS-LOAD",
                    axisSettingsWarning);
            }

            var visibleAxisCount = Math.Min(connection.AxisCount, _motionOptions.AxisCount);
            ViewModel?.PopulateMotionAxes(visibleAxisCount);
            UpdateAxisAvailability(visibleAxisCount);
            if (SelectedAxis is { } initialAxis)
            {
                LoadTuningForAxis(initialAxis);
            }
            ConfigureIoPoints(IoPointKind.DigitalInput);
            SetWorkbenchMode(MotionWorkbenchMode.ContinuousJog);
            SetCommandStage(CommandStage.Ready, "准备");
            PollMotionState();
            _pollTimer.Start();
        }
        catch (Exception exception)
        {
            HandleInitializationFailure(
                FormatInitializationException(exception),
                "运动控制：连接失败");
        }
    }

    private void HandleInitializationFailure(string details, string connectionText)
    {
        ViewModel?.ClearMotionData();
        SetConnectionText(connectionText);
        RecordAlarm("MOTION-CARD-INIT", details);

        var troubleshooting =
            details.Contains("LTDMC.dll", StringComparison.OrdinalIgnoreCase) ||
            details.Contains("位数", StringComparison.Ordinal)
                ? "请检查 32 位雷赛运行库及 LTDMC.dll 是否与 win-x86 程序匹配，并确认 DLL 位于程序同目录。"
                : "请先关闭其他可能占用控制卡的软件，再根据上方 SDK 操作名和返回码检查板卡、驱动及总线状态。程序启动阶段不会写入轴参数。";
        var message =
            "未能读取运动控制卡，轴列表无法加载。\n\n" +
            $"原因：{details}\n\n" +
            troubleshooting;
        var owner = Window.GetWindow(this);
        if (owner is null)
        {
            MessageBox.Show(
                message,
                "运动控制卡初始化失败",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        MessageBox.Show(
            owner,
            message,
            "运动控制卡初始化失败",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private async void AxisSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var previousAxis = e.RemovedItems.OfType<AxisStatus>().FirstOrDefault();
        var nextAxis = e.AddedItems.OfType<AxisStatus>().FirstOrDefault();
        CancellationTokenSource? feedbackCancellation = null;

        if (previousAxis is not null && nextAxis is not null &&
            previousAxis.HardwareAxisNo != nextAxis.HardwareAxisNo)
        {
            _axisSelectionFeedbackCancellation?.Cancel();
            feedbackCancellation = new CancellationTokenSource();
            _axisSelectionFeedbackCancellation = feedbackCancellation;
            AxisSwitchOverlay.Visibility = Visibility.Visible;
        }

        if (previousAxis is not null)
        {
            try
            {
                SaveMotionConfigurationOrThrow(previousAxis);
                SaveAxisSettingsOrThrow();
            }
            catch (Exception exception) when (
                exception is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                RecordAlarm($"AXIS-{previousAxis.HardwareAxisNo:00}-PROFILE", $"轴参数未保存：{exception.Message}");
            }
        }

        if (_activeJogAxisNo is { } activeAxisNo && SelectedAxis?.HardwareAxisNo != activeAxisNo)
        {
            StopActiveJog("切换轴");
        }

        if (SelectedAxis is { } selectedAxis)
        {
            LoadTuningForAxis(selectedAxis);
        }

        if (_activeJogAxisNo is null && _activePositionAxisNo is null && _pendingStopAxisNos.Count == 0 && _homeDeadlines.Count == 0)
        {
            SetCommandStage(
                _motionSafetyLock ? CommandStage.Failed : CommandStage.Ready,
                _motionSafetyLock
                    ? "停止安全链异常，运动锁定（需重启）"
                    : SelectedAxis is null
                        ? "请选择轴"
                        : "准备");
        }

        UpdateHomeEditorState();

        if (feedbackCancellation is null)
        {
            return;
        }

        try
        {
            // Keep the feedback visible long enough to be perceived even when the
            // selected axis configuration loads immediately.
            await Task.Delay(450, feedbackCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // A newer axis selection owns the progress indicator now.
        }
        finally
        {
            if (ReferenceEquals(_axisSelectionFeedbackCancellation, feedbackCancellation))
            {
                AxisSwitchOverlay.Visibility = Visibility.Collapsed;
                _axisSelectionFeedbackCancellation = null;
            }

            feedbackCancellation.Dispose();
        }
    }

    private void AxisName_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox { DataContext: AxisStatus axis } editor)
        {
            return;
        }

        SelectedAxis = axis;
        editor.Tag = axis.Name;
        editor.IsReadOnly = false;
        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    private void AxisName_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.IsReadOnly)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            editor.Text = editor.Tag as string ?? editor.Text;
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void AxisName_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (ContainsChineseCharacters(e.Text))
        {
            e.Handled = true;
        }
    }

    private void AxisName_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (!e.SourceDataObject.GetDataPresent(DataFormats.UnicodeText, true))
        {
            e.CancelCommand();
            return;
        }

        var pastedText = e.SourceDataObject.GetData(DataFormats.UnicodeText) as string;
        if (ContainsChineseCharacters(pastedText))
        {
            e.CancelCommand();
        }
    }

    private void AxisName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: AxisStatus axis } editor || editor.IsReadOnly)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(editor.Text))
        {
            editor.Text = editor.Tag as string ?? $"轴 {axis.HardwareAxisNo}";
        }

        editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        editor.IsReadOnly = true;
        editor.Tag = null;
        SaveAxisSettings();
    }

    private void IoName_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBox { DataContext: IoPoint point } editor)
        {
            return;
        }

        editor.Tag = point.Name;
        editor.IsReadOnly = false;
        editor.Focus();
        editor.SelectAll();
        e.Handled = true;
    }

    private void IoName_KeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox editor || editor.IsReadOnly)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            editor.Text = editor.Tag as string ?? editor.Text;
            editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void IoName_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox { DataContext: IoPoint point } editor || editor.IsReadOnly)
        {
            return;
        }

        var previousDisplayName = editor.Tag as string ?? point.Name;
        var normalizedName = editor.Text.Trim();
        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            normalizedName = point.DefaultName;
        }

        editor.Text = normalizedName;
        editor.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        editor.IsReadOnly = true;
        editor.Tag = null;

        var key = GetIoNameKey(point.Kind, point.Channel);
        var hadPreviousValue = _motionOptions.IoPointNames.TryGetValue(key, out var previousStoredName);
        if (string.Equals(normalizedName, point.DefaultName, StringComparison.Ordinal))
        {
            _motionOptions.IoPointNames.Remove(key);
        }
        else
        {
            _motionOptions.IoPointNames[key] = normalizedName;
        }

        try
        {
            _motionOptionsStore.Save(_motionOptions);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            if (hadPreviousValue && previousStoredName is not null)
            {
                _motionOptions.IoPointNames[key] = previousStoredName;
            }
            else
            {
                _motionOptions.IoPointNames.Remove(key);
            }

            point.Name = previousDisplayName;
            editor.Text = previousDisplayName;
            RecordAlarm("IO-NAME-SAVE", $"I/O 名称保存失败：{exception.Message}");
        }
    }

    private static bool ContainsChineseCharacters(string? text)
    {
        return !string.IsNullOrEmpty(text) && text.Any(character =>
            character is >= '\u3400' and <= '\u4DBF' or
                         >= '\u4E00' and <= '\u9FFF' or
                         >= '\uF900' and <= '\uFAFF');
    }

    private void ModeTab_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string modeText } ||
            !Enum.TryParse<MotionWorkbenchMode>(modeText, out var mode))
        {
            return;
        }

        if (_activeJogAxisNo is not null && mode != MotionWorkbenchMode.ContinuousJog)
        {
            StopActiveJog("切换模式");
        }

        SetWorkbenchMode(mode);
    }

    private void RelativeMode_Checked(object sender, RoutedEventArgs e)
    {
        Tuning.AbsolutePositionMode = false;
        SetPositionModeUi();
        if (_initialized && !_loadingAxisSettings)
        {
            SaveMotionConfiguration(writeAlarm: true);
        }
    }

    private void AbsoluteMode_Checked(object sender, RoutedEventArgs e)
    {
        Tuning.AbsolutePositionMode = true;
        SetPositionModeUi();
        if (_initialized && !_loadingAxisSettings)
        {
            SaveMotionConfiguration(writeAlarm: true);
        }
    }

    private async void AbsoluteMove_Click(object sender, RoutedEventArgs e)
    {
        await ExecutePositionMoveAsync(1);
    }

    private void LoadTuningForAxis(AxisStatus axis)
    {
        _loadingAxisSettings = true;
        try
        {
            Tuning.LoadFrom(_motionOptions.GetMoveProfile(axis.HardwareAxisNo));
            HomeTuning.LoadFrom(
                _motionOptions.GetHomeProfile(axis.HardwareAxisNo),
                _motionOptions.HomeTimeoutSeconds,
                GetHomeSequenceOrder(axis.HardwareAxisNo));
            if (RelativeModeRadio is not null && AbsoluteModeRadio is not null)
            {
                RelativeModeRadio.IsChecked = !Tuning.AbsolutePositionMode;
                AbsoluteModeRadio.IsChecked = Tuning.AbsolutePositionMode;
            }

        }
        finally
        {
            _loadingAxisSettings = false;
        }

        SetPositionModeUi();
        UpdateHomeEditorState();
    }

    private int GetHomeSequenceOrder(int hardwareAxisNo)
    {
        var index = Array.IndexOf(_motionOptions.HomeSequence, hardwareAxisNo);
        return index < 0 ? 0 : index + 1;
    }

    private void SetPositionModeUi()
    {
        if (MoveValueLabel is null ||
            RelativeNegativeButton is null ||
            RelativePositiveButton is null ||
            AbsoluteTargetButton is null)
        {
            return;
        }

        MoveValueLabel.Text = Tuning.AbsolutePositionMode ? "目标位置" : "移动距离";
        RelativeNegativeButton.Visibility = Tuning.AbsolutePositionMode ? Visibility.Collapsed : Visibility.Visible;
        RelativePositiveButton.Visibility = Tuning.AbsolutePositionMode ? Visibility.Collapsed : Visibility.Visible;
        AbsoluteTargetButton.Visibility = Tuning.AbsolutePositionMode ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SetWorkbenchMode(MotionWorkbenchMode mode)
    {
        _workbenchMode = mode;
        if (FixedModePanel is null)
        {
            return;
        }

        FixedModePanel.Visibility = mode == MotionWorkbenchMode.FixedPosition ? Visibility.Visible : Visibility.Collapsed;
        JogModePanel.Visibility = mode == MotionWorkbenchMode.ContinuousJog ? Visibility.Visible : Visibility.Collapsed;
        HomeModePanel.Visibility = mode == MotionWorkbenchMode.Home ? Visibility.Visible : Visibility.Collapsed;
        SetModeTabState(FixedModeTab, mode == MotionWorkbenchMode.FixedPosition);
        SetModeTabState(JogModeTab, mode == MotionWorkbenchMode.ContinuousJog);
        SetModeTabState(HomeModeTab, mode == MotionWorkbenchMode.Home);
        if (_activeJogAxisNo is null && _activePositionAxisNo is null && _pendingStopAxisNos.Count == 0 && _homeDeadlines.Count == 0)
        {
            SetCommandStage(
                _motionSafetyLock ? CommandStage.Failed : CommandStage.Ready,
                _motionSafetyLock
                    ? "停止安全链异常，运动锁定（需重启）"
                    : "准备");
        }
    }

    private static void SetModeTabState(Button button, bool selected)
    {
        button.Background = selected
            ? new SolidColorBrush(Color.FromRgb(0x0D, 0x6E, 0xE8))
            : new SolidColorBrush(Color.FromRgb(0x23, 0x3D, 0x55));
        button.Foreground = Brushes.White;
        button.BorderBrush = selected
            ? new SolidColorBrush(Color.FromRgb(0x2A, 0x8B, 0xFF))
            : new SolidColorBrush(Color.FromRgb(0x3D, 0x59, 0x70));
    }

    private async void FixedMove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string directionText } ||
            !int.TryParse(directionText, out var direction) ||
            direction is not (-1 or 1))
        {
            return;
        }

        await ExecutePositionMoveAsync(direction);
    }

    private async Task ExecutePositionMoveAsync(int direction)
    {
        if (SelectedAxis is not { } axis || !EnsureConnected())
        {
            return;
        }

        if (axis.IsMoving ||
            _activeJogAxisNo is not null ||
            _activePositionAxisNo is not null ||
            _pendingStopAxisNos.Count > 0 ||
            _homeDeadlines.Count > 0 ||
            _homeSequenceCancellation is not null)
        {
            RecordAlarm($"AXIS-{axis.HardwareAxisNo:00}-BUSY", "已有运动或减速停止尚未确认完成，不能重复下发定距命令。");
            return;
        }

        CancellationTokenSource? cancellation = null;
        var waitForCompletion = Tuning.WaitForCompletion;
        var commandIssued = false;
        try
        {
            EnsureServoEnabled(axis);
            ValidateSelectedAxisMotion(axis);
            ApplyTuningSettings();

            var beforeMove = _motionCard.ReadAxis(axis.HardwareAxisNo);
            ApplySnapshot(axis, beforeMove);
            if (beforeMove.IsMoving)
            {
                throw new MotionCardException($"{axis.Name} 正在运动，拒绝重复下发定位命令。", "轴忙检查");
            }

            var expectedTarget = Tuning.AbsolutePositionMode
                ? axis.JogDistance
                : beforeMove.CommandPosition + direction * axis.JogDistance;

            if (Tuning.AbsolutePositionMode)
            {
                _motionCard.MoveAbsolute(
                    axis.HardwareAxisNo,
                    expectedTarget,
                    axis.JogSpeed);
            }
            else
            {
                _motionCard.MoveRelative(
                    axis.HardwareAxisNo,
                    direction * axis.JogDistance,
                    axis.JogSpeed);
            }

            commandIssued = true;
            _activePositionAxisNo = axis.HardwareAxisNo;
            _activePositionTarget = expectedTarget;
            _activePositionIssuedAtUtc = DateTime.UtcNow;
            _activePositionDeadlineUtc = _activePositionIssuedAtUtc.Value.AddMilliseconds(Tuning.CompletionTimeoutMilliseconds);
            _activePositionTolerance = Tuning.CompletionTolerance;
            _activePositionTimeoutMilliseconds = Tuning.CompletionTimeoutMilliseconds;
            _activePositionObservedMoving = false;
            _operatorStopRequestedAxisNo = null;
            _operatorImmediateStopRequested = false;
            _failedPositionAxisNo = null;
            axis.Target = expectedTarget;
            axis.IsMoving = true;
            axis.State = Tuning.AbsolutePositionMode
                ? $"绝对定位命令已发送：{expectedTarget:0.###} {axis.Unit}"
                : $"相对定距命令已发送：{direction * axis.JogDistance:0.###} {axis.Unit}";
            _commandStopwatch = Stopwatch.StartNew();
            SetCommandStage(CommandStage.Issued, "命令已下发");

            if (!waitForCompletion)
            {
                SetCommandStage(CommandStage.Running, "运行中（后台跟踪）");
                return;
            }

            cancellation = new CancellationTokenSource();
            _positionMoveCancellation = cancellation;
            await WaitForPositionMoveAsync(axis, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            if (_activePositionAxisNo == axis.HardwareAxisNo)
            {
                var safetyFailure = _motionSafetyLock || _commandStage == CommandStage.Failed;
                axis.State = safetyFailure
                    ? "运动等待因安全异常取消，停止确认与运动锁保持"
                    : "运动等待已取消";
                ClearPositionTracking(axis.HardwareAxisNo);
                SetCommandStage(
                    safetyFailure ? CommandStage.Failed : CommandStage.Stopped,
                    safetyFailure ? "安全异常，运动锁定" : "等待已取消");
            }
        }
        catch (Exception exception)
        {
            RecordAlarm($"AXIS-{axis.HardwareAxisNo:00}-MOVE", FormatException(exception));
            if (commandIssued)
            {
                _failedPositionAxisNo = axis.HardwareAxisNo;
                axis.State = "定位异常，正在安全停止";
                SetCommandStage(CommandStage.Failed, "执行失败，停止确认中");
                RequestStopAfterPositionFailure(axis);
            }
            else
            {
                axis.State = "定距运动失败";
                SetCommandStage(CommandStage.Failed, "执行失败");
                ClearPositionTracking(axis.HardwareAxisNo);
            }
        }
        finally
        {
            if (cancellation is not null && ReferenceEquals(_positionMoveCancellation, cancellation))
            {
                _positionMoveCancellation.Dispose();
                _positionMoveCancellation = null;
            }

            SaveMotionConfiguration(writeAlarm: true);
            PollMotionState();
        }
    }

    private async Task WaitForPositionMoveAsync(AxisStatus axis, CancellationToken cancellationToken)
    {
        var deadline = _activePositionDeadlineUtc
            ?? throw new InvalidOperationException("定位命令没有有效的超时截止时间。");
        await Task.Delay(Math.Min(_motionOptions.PollIntervalMilliseconds, 100), cancellationToken);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _motionCard.ReadAxis(axis.HardwareAxisNo);
            ApplySnapshot(axis, snapshot);
            ProcessSnapshotAlarms(axis, snapshot);
            if (snapshot.Alarm || snapshot.EmergencyInput)
            {
                throw new MotionCardException(
                    $"{axis.Name} 运动时发生报警，错误码 0x{snapshot.AxisErrorCode:X4}，停止原因 {snapshot.StopReason}。");
            }

            if (snapshot.IsMoving)
            {
                _activePositionObservedMoving = true;
            }

            if (TryCompleteTrackedPosition(axis, snapshot))
            {
                return;
            }

            SetCommandStage(CommandStage.Running, snapshot.IsMoving ? "运行中" : "等待编码器到位");
            await Task.Delay(_motionOptions.PollIntervalMilliseconds, cancellationToken);
        }

        throw new TimeoutException(
            $"{axis.Name} 在 {_activePositionTimeoutMilliseconds} ms 内未完成运动，将发送减速停止。");
    }

    private bool TryCompleteTrackedPosition(AxisStatus axis, MotionAxisSnapshot snapshot)
    {
        if (_activePositionAxisNo != axis.HardwareAxisNo)
        {
            return false;
        }

        if (snapshot.IsMoving)
        {
            _activePositionObservedMoving = true;
            return false;
        }

        if (!_activePositionObservedMoving &&
            _activePositionIssuedAtUtc is { } issuedAt &&
            DateTime.UtcNow < issuedAt.AddMilliseconds(Math.Max(100, _motionOptions.PollIntervalMilliseconds * 2)))
        {
            return false;
        }

        if (_failedPositionAxisNo == axis.HardwareAxisNo)
        {
            axis.State = "定位失败，已确认停止";
            ClearPositionTracking(axis.HardwareAxisNo);
            SetCommandStage(CommandStage.Failed, "执行失败，轴已停止");
            return true;
        }

        if (_operatorStopRequestedAxisNo == axis.HardwareAxisNo)
        {
            var stopKind = _operatorImmediateStopRequested ? "立即停止" : "减速停止";
            var stopFailed = _motionSafetyLock || _commandStage == CommandStage.Failed;
            axis.State = stopFailed
                ? $"手动{stopKind}发生异常，轴已确认停止"
                : $"已手动{stopKind}";
            ClearPositionTracking(axis.HardwareAxisNo);
            SetCommandStage(
                stopFailed ? CommandStage.Failed : CommandStage.Stopped,
                stopFailed ? $"{stopKind}异常，轴已确认停止" : $"已{stopKind}");
            return true;
        }

        if (snapshot.StopReason != 0)
        {
            throw new MotionCardException(
                $"{axis.Name} 未正常到位，停止原因 {snapshot.StopReason}。",
                "运动完成检查");
        }

        if (_activePositionTarget is not { } expectedTarget)
        {
            throw new InvalidOperationException("定位目标跟踪信息缺失，不能确认运动完成。");
        }

        var commandError = Math.Abs(snapshot.CommandPosition - expectedTarget);
        if (commandError > _activePositionTolerance)
        {
            throw new MotionCardException(
                $"{axis.Name} 已停止但未到指令目标：目标 {expectedTarget:0.###}，指令位置 {snapshot.CommandPosition:0.###}，允许误差 {_activePositionTolerance:0.###} {axis.Unit}。",
                "运动完成检查");
        }

        var targetError = Math.Abs(snapshot.FeedbackPosition - expectedTarget);
        if (targetError > _activePositionTolerance)
        {
            if (_activePositionDeadlineUtc is { } activeDeadline && DateTime.UtcNow >= activeDeadline)
            {
                throw new TimeoutException(
                    $"{axis.Name} 已停止，但编码器在 {_activePositionTimeoutMilliseconds} ms 内未进入目标 ±{_activePositionTolerance:0.###} {axis.Unit} 到位范围。当前反馈误差 {targetError:0.###} {axis.Unit}。");
            }

            return false;
        }

        axis.State = "运动完成并已到位";
        ClearPositionTracking(axis.HardwareAxisNo);
        SetCommandStage(CommandStage.Stopped, "运动完成");
        return true;
    }

    private void ClearPositionTracking(int hardwareAxisNo)
    {
        if (_activePositionAxisNo != hardwareAxisNo)
        {
            return;
        }

        _activePositionAxisNo = null;
        _activePositionTarget = null;
        _activePositionIssuedAtUtc = null;
        _activePositionDeadlineUtc = null;
        _activePositionTolerance = 0;
        _activePositionTimeoutMilliseconds = 0;
        _activePositionObservedMoving = false;
        if (_failedPositionAxisNo == hardwareAxisNo)
        {
            _failedPositionAxisNo = null;
        }
        if (_operatorStopRequestedAxisNo == hardwareAxisNo)
        {
            _operatorStopRequestedAxisNo = null;
            _operatorImmediateStopRequested = false;
        }
    }

    private void RequestStopAfterPositionFailure(AxisStatus axis)
    {
        IssueAxisStopWithEscalation(
            axis,
            axis.HardwareAxisNo,
            immediate: false,
            "MOVE",
            "定位异常，正在安全停止");
    }

    private bool IssueAxisStopWithEscalation(
        AxisStatus? axis,
        int hardwareAxisNo,
        bool immediate,
        string code,
        string reason)
    {
        if (_pendingStopAxisNos.Contains(hardwareAxisNo) &&
            (!immediate || _emergencyStopPendingAxisNos.Contains(hardwareAxisNo)))
        {
            return true;
        }

        try
        {
            if (!_motionCard.IsOpen)
            {
                throw new MotionCardException("运动控制卡已断开，无法发送单轴停止命令。");
            }

            _motionCard.Stop(hardwareAxisNo, emergency: immediate);
            ArmStopConfirmation(hardwareAxisNo, emergencyStopIssued: false);
            ClearHomeTracking(hardwareAxisNo);
            if (axis is not null)
            {
                axis.State = $"{reason}，等待停止确认";
            }

            return true;
        }
        catch (Exception stopException)
        {
            RecordAlarm($"AXIS-{hardwareAxisNo:00}-{code}-STOP", FormatException(stopException));
        }

        try
        {
            _motionCard.EmergencyStop();
            _homeSequenceCancellation?.Cancel();
            ClearAllHomeTracking();
            ArmAllHardwareAxisStopConfirmations(emergencyStopIssued: true);
            foreach (var item in (Axes ?? []).Where(item => item.IsAvailable))
            {
                item.State = "单轴停止失败，已升级全轴急停，等待停止确认";
            }

            SetCommandStage(CommandStage.Failed, "已升级全轴急停，等待确认");
            RecordAlarm(
                $"AXIS-{hardwareAxisNo:00}-{code}-ESCALATED",
                "单轴停止失败，已升级为全轴急停。");
            return true;
        }
        catch (Exception emergencyException)
        {
            ActivateMotionSafetyLock(
                "单轴停止失败，且升级全轴急停也下发失败",
                emergencyException,
                "EMERGENCY-STOP-FAILED");
            return false;
        }
    }

    private void ArmStopConfirmation(int hardwareAxisNo, bool emergencyStopIssued)
    {
        _pendingStopAxisNos.Add(hardwareAxisNo);
        _stopConfirmationDeadlines[hardwareAxisNo] = DateTime.UtcNow.AddMilliseconds(
            _motionOptions.StopConfirmationTimeoutMilliseconds);
        if (emergencyStopIssued)
        {
            _emergencyStopPendingAxisNos.Add(hardwareAxisNo);
        }
        else
        {
            _emergencyStopPendingAxisNos.Remove(hardwareAxisNo);
        }
    }

    private int[] GetAllHardwareAxisNumbers()
    {
        return _motionCard.IsOpen && _motionCard.AxisCount > 0
            ? Enumerable.Range(0, _motionCard.AxisCount).ToArray()
            : [];
    }

    private void ArmAllHardwareAxisStopConfirmations(bool emergencyStopIssued)
    {
        foreach (var hardwareAxisNo in GetAllHardwareAxisNumbers())
        {
            ArmStopConfirmation(hardwareAxisNo, emergencyStopIssued);
        }
    }

    private bool TryIssueGlobalEmergencyStop(string reason, string alarmCode)
    {
        if (!_motionCard.IsOpen)
        {
            RecordAlarm(alarmCode, "运动控制卡未连接，无法下发全轴急停命令。");
            return false;
        }

        try
        {
            _motionCard.EmergencyStop();
            return true;
        }
        catch (Exception exception)
        {
            ActivateMotionSafetyLock(reason, exception, alarmCode);
            return false;
        }
    }

    private void ActivateMotionSafetyLock(
        string reason,
        Exception? exception,
        string alarmCode,
        bool armStopConfirmations = true)
    {
        _motionSafetyLock = true;
        _motionSafetyLockReason = exception is null
            ? reason
            : $"{reason}：{FormatException(exception)}";
        _homeSequenceCancellation?.Cancel();
        _positionMoveCancellation?.Cancel();
        if (_activePositionAxisNo is { } activePositionAxisNo)
        {
            _failedPositionAxisNo = activePositionAxisNo;
        }

        if (armStopConfirmations)
        {
            ArmAllHardwareAxisStopConfirmations(emergencyStopIssued: false);
        }
        if (ViewModel is { } viewModel)
        {
            viewModel.MotionControlsEnabled = false;
        }

        foreach (var axis in (Axes ?? []).Where(axis => axis.IsAvailable))
        {
            axis.StatusReadHealthy = false;
            axis.State = "停止安全链异常，运动已锁定（需重启程序）";
        }

        SetConnectionText("运动控制：停止安全链异常，运动锁定");
        SetCommandStage(CommandStage.Failed, "停止安全链异常，运动锁定（需重启）");
        RecordAlarmOnce(
            $"motion-safety-lock:{alarmCode}",
            alarmCode,
            $"{_motionSafetyLockReason}。软件运动命令已锁定；请使用硬件急停，确认机构停止并排除故障后重启程序。");
    }

    private bool TryConfirmAllHardwareAxesStopped(out string failure)
    {
        var pendingAxisNos = GetAllHardwareAxisNumbers().ToHashSet();
        var lastReadErrors = new Dictionary<int, string>();
        var deadline = DateTime.UtcNow.AddMilliseconds(
            _motionOptions.StopConfirmationTimeoutMilliseconds);

        while (pendingAxisNos.Count > 0)
        {
            foreach (var hardwareAxisNo in pendingAxisNos.ToArray())
            {
                try
                {
                    var snapshot = _motionCard.ReadAxis(hardwareAxisNo);
                    lastReadErrors.Remove(hardwareAxisNo);
                    if (!snapshot.IsMoving)
                    {
                        pendingAxisNos.Remove(hardwareAxisNo);
                    }
                }
                catch (Exception exception)
                {
                    lastReadErrors[hardwareAxisNo] = FormatException(exception);
                }
            }

            if (pendingAxisNos.Count == 0)
            {
                failure = "";
                return true;
            }

            if (DateTime.UtcNow >= deadline)
            {
                break;
            }

            Thread.Sleep(Math.Clamp(_motionOptions.PollIntervalMilliseconds / 2, 25, 100));
        }

        var axisText = string.Join(", ", pendingAxisNos.Select(axisNo => axisNo.ToString()));
        var readErrorText = lastReadErrors.Count == 0
            ? ""
            : $"；状态读取错误：{string.Join(" | ", lastReadErrors.Select(item => $"轴 {item.Key}: {item.Value}"))}";
        failure = $"在 {_motionOptions.StopConfirmationTimeoutMilliseconds} ms 内未确认硬件轴 [{axisText}] 停止{readErrorText}";
        return false;
    }

    private void JogHold_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        StartContinuousJogFromInput(GetDirection(sender), sender as FrameworkElement);
    }

    private void JogHold_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        StopActiveJogFromInput(sender, "松开按钮");
    }

    private void JogHold_MouseLeave(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            StopActiveJogFromInput(sender, "指针离开按钮");
        }
    }

    private void JogHold_LostMouseCapture(object sender, MouseEventArgs e)
    {
        StopActiveJogFromInput(sender, "按钮已释放");
    }

    private void JogHold_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        StopActiveJogFromInput(sender, "JOG 按钮失去键盘焦点");
    }

    private void OwnerWindow_Deactivated(object? sender, EventArgs e)
    {
        StopActiveJog("窗口失去焦点");
    }

    private void JogHold_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!e.IsRepeat && e.Key is Key.Space or Key.Enter)
        {
            StartContinuousJogFromInput(GetDirection(sender), sender as FrameworkElement);
            e.Handled = true;
        }
    }

    private void JogHold_PreviewKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Enter)
        {
            StopActiveJogFromInput(sender, "按键已释放");
            e.Handled = true;
        }
    }

    private static int GetDirection(object sender)
    {
        return sender is FrameworkElement { Tag: string directionText } && int.TryParse(directionText, out var direction)
            ? Math.Sign(direction)
            : 0;
    }

    private void StartContinuousJog(int direction)
    {
        StartContinuousJogFromInput(direction, inputOwner: null);
    }

    private void StartContinuousJogFromInput(int direction, FrameworkElement? inputOwner)
    {
        if (direction == 0 ||
            _activeJogAxisNo is not null ||
            _activePositionAxisNo is not null ||
            _pendingStopAxisNos.Count > 0 ||
            _homeDeadlines.Count > 0 ||
            _homeSequenceCancellation is not null ||
            SelectedAxis is not { } axis)
        {
            return;
        }

        if (axis.IsMoving)
        {
            RecordAlarm($"AXIS-{axis.HardwareAxisNo:00}-BUSY", $"{axis.Name} 正在运动，不能启动连续 JOG。");
            return;
        }

        if (!ExecuteMotion(
                axis,
                direction > 0 ? "JOG+" : "JOG-",
                () =>
                {
                    EnsureServoEnabled(axis);
                    ValidateSelectedAxisMotion(axis);
                    ApplyTuningSettings();
                    _motionCard.Jog(axis.HardwareAxisNo, direction * axis.JogSpeed);
                }))
        {
            SetCommandStage(CommandStage.Failed, "JOG 启动失败");
            return;
        }

        _activeJogAxisNo = axis.HardwareAxisNo;
        _activeJogInputOwner = inputOwner;
        _commandStopwatch = Stopwatch.StartNew();
        axis.IsMoving = true;
        axis.State = direction > 0 ? "连续 JOG 正向运行" : "连续 JOG 负向运行";
        SetCommandStage(CommandStage.Running, direction > 0 ? "正向 JOG 运行中" : "负向 JOG 运行中");
    }

    private void StopActiveJogFromInput(object sender, string reason)
    {
        if (!ReferenceEquals(sender, _activeJogInputOwner))
        {
            return;
        }

        StopActiveJog(reason);
    }

    private void StopActiveJog(string reason)
    {
        if (_activeJogAxisNo is not { } hardwareAxisNo ||
            _pendingStopAxisNos.Contains(hardwareAxisNo))
        {
            return;
        }

        SetCommandStage(CommandStage.Releasing, "松开停止");
        var axis = Axes?.FirstOrDefault(item => item.HardwareAxisNo == hardwareAxisNo);
        if (IssueAxisStopWithEscalation(
                axis,
                hardwareAxisNo,
                immediate: false,
                "JOG",
                $"{reason}，减速停止中"))
        {
            PollMotionState();
        }
    }

    private void ResetProfile_Click(object sender, RoutedEventArgs e)
    {
        Tuning.ResetProfile();
        ApplyTuningSettings();
        SaveMotionConfiguration(writeAlarm: true);
    }

    private void MotionProfile_LostFocus(object sender, RoutedEventArgs e)
    {
        SaveMotionConfiguration(writeAlarm: true);
    }

    private void MotionProfile_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loadingAxisSettings)
        {
            SaveMotionConfiguration(writeAlarm: true);
        }
    }

    private void HomeProfile_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingAxisSettings)
        {
            return;
        }

        if (!TryCommitHomeEditorBindings())
        {
            _homeConfigurationSaveHealthy = false;
            UpdateHomeActionState();
            RecordAlarmOnce(
                "home-profile-binding-error",
                "HOME-PROFILE-INPUT",
                "回零参数包含无法识别的输入，请修正红框字段后再回零。");
            return;
        }

        _activeAlarmKeys.Remove("home-profile-binding-error");
        SaveMotionConfiguration(writeAlarm: true);
    }

    private void LocalStop_Click(object sender, RoutedEventArgs e)
    {
        StopCurrentMotion(immediate: false);
    }

    private void ImmediateStop_Click(object sender, RoutedEventArgs e)
    {
        StopCurrentMotion(immediate: true);
    }

    private void StopCurrentMotion(bool immediate)
    {
        if (_activeJogAxisNo is { } activeJogAxisNo)
        {
            if (!immediate)
            {
                StopActiveJog("手动停止");
                return;
            }

            var joggingAxis = Axes?.FirstOrDefault(item => item.HardwareAxisNo == activeJogAxisNo);
            if (joggingAxis is not null)
            {
                StopAxis(joggingAxis, immediate: true);
            }

            return;
        }

        if (_activePositionAxisNo is { } activePositionAxisNo)
        {
            var movingAxis = Axes?.FirstOrDefault(item => item.HardwareAxisNo == activePositionAxisNo);
            if (movingAxis is not null)
            {
                StopAxis(movingAxis, immediate);
            }

            return;
        }

        if (_homeDeadlines.Count > 0)
        {
            var homeAxisNo = _homeDeadlines.Keys.First();
            var homingAxis = Axes?.FirstOrDefault(item => item.HardwareAxisNo == homeAxisNo);
            if (homingAxis is not null && StopAxis(homingAxis, immediate))
            {
                _homeSequenceCancellation?.Cancel();
            }

            return;
        }

        if (SelectedAxis is { } axis)
        {
            StopAxis(axis, immediate);
        }
    }

    private void ValidateSelectedAxisMotion(AxisStatus axis)
    {
        if (!double.IsFinite(axis.JogSpeed) || axis.JogSpeed <= 0)
        {
            throw new InvalidDataException("运行速度必须是大于 0 的有限数值。");
        }

        if (_workbenchMode == MotionWorkbenchMode.FixedPosition && !double.IsFinite(axis.JogDistance))
        {
            throw new InvalidDataException(Tuning.AbsolutePositionMode
                ? "绝对目标位置必须是有限数值。"
                : "相对移动距离必须是有限数值。");
        }

        if (_workbenchMode == MotionWorkbenchMode.FixedPosition &&
            !Tuning.AbsolutePositionMode &&
            axis.JogDistance <= 0)
        {
            throw new InvalidDataException("相对移动距离必须是大于 0 的有限数值。");
        }

        if (Tuning.StartVelocity > axis.JogSpeed)
        {
            throw new InvalidDataException("启动速度不能大于运行速度。");
        }

        if (Tuning.StopVelocity > axis.JogSpeed)
        {
            throw new InvalidDataException("停止速度不能大于运行速度。");
        }
    }

    private void ApplyTuningSettings(AxisStatus? axisOverride = null)
    {
        var axis = axisOverride ?? SelectedAxis;
        var profile = axis is not null
            ? _motionOptions.GetOrCreateMoveProfile(axis.HardwareAxisNo)
            : _motionOptions.MoveProfile;
        Tuning.ApplyTo(profile);
    }

    private int ApplyHomeTuningSettings(AxisStatus axis)
    {
        var profile = HomeTuning.CreateProfile();
        if (HomeTuning.SequenceOrder > _motionOptions.AxisCount)
        {
            throw new InvalidDataException($"回零顺序号不能大于配置轴数 {_motionOptions.AxisCount}。");
        }

        var sequence = _motionOptions.HomeSequence
            .Where(hardwareAxisNo => hardwareAxisNo != axis.HardwareAxisNo)
            .ToList();
        var normalizedOrder = 0;
        if (profile.Enabled && HomeTuning.SequenceOrder > 0)
        {
            var insertionIndex = Math.Min(HomeTuning.SequenceOrder - 1, sequence.Count);
            sequence.Insert(insertionIndex, axis.HardwareAxisNo);
            normalizedOrder = insertionIndex + 1;
        }

        _motionOptions.AxisHomeProfiles[axis.HardwareAxisNo] = profile;
        _motionOptions.HomeTimeoutSeconds = HomeTuning.TimeoutSeconds;
        _motionOptions.HomeSequence = sequence.ToArray();
        UpdateAxisHomeConfiguration(axis, profile);
        return normalizedOrder;
    }

    private void SaveMotionConfiguration(bool writeAlarm)
    {
        try
        {
            SaveMotionConfigurationOrThrow();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            _homeConfigurationSaveHealthy = false;
            UpdateHomeActionState();
            if (writeAlarm)
            {
                RecordAlarm("MOTION-CONFIG-SAVE", $"运动参数保存失败：{exception.Message}");
            }
        }
    }

    private void SaveMotionConfigurationOrThrow(AxisStatus? axisOverride = null)
    {
        var axis = axisOverride ?? SelectedAxis;
        var axisKey = axis?.HardwareAxisNo ?? 0;
        MotionMoveProfile? previousMoveProfileValue = null;
        var hadMoveProfile = axis is not null &&
                             _motionOptions.AxisMoveProfiles.TryGetValue(axisKey, out previousMoveProfileValue);
        var previousMoveProfile = previousMoveProfileValue?.Clone();
        MotionHomeProfile? previousHomeProfile = null;
        var hadHomeProfile = axis is not null &&
                             _motionOptions.AxisHomeProfiles.TryGetValue(axisKey, out previousHomeProfile);
        var previousTimeoutSeconds = _motionOptions.HomeTimeoutSeconds;
        var previousHomeSequence = _motionOptions.HomeSequence.ToArray();
        try
        {
            ApplyTuningSettings(axis);
            var normalizedSequenceOrder = axis is null ? 0 : ApplyHomeTuningSettings(axis);
            _motionOptionsStore.Save(_motionOptions);

            _loadingAxisSettings = true;
            try
            {
                HomeTuning.SequenceOrder = normalizedSequenceOrder;
            }
            finally
            {
                _loadingAxisSettings = false;
            }

            _homeConfigurationSaveHealthy = true;
            _activeAlarmKeys.Remove("home-profile-binding-error");
            UpdateHomeActionState();
        }
        catch
        {
            if (axis is not null)
            {
                if (hadMoveProfile && previousMoveProfile is not null)
                {
                    _motionOptions.AxisMoveProfiles[axisKey] = previousMoveProfile;
                }
                else
                {
                    _motionOptions.AxisMoveProfiles.Remove(axisKey);
                }

                if (hadHomeProfile && previousHomeProfile is not null)
                {
                    _motionOptions.AxisHomeProfiles[axisKey] = previousHomeProfile;
                }
                else
                {
                    _motionOptions.AxisHomeProfiles.Remove(axisKey);
                }

                UpdateAxisHomeConfiguration(axis, _motionOptions.GetHomeProfile(axis.HardwareAxisNo));
            }

            _motionOptions.HomeTimeoutSeconds = previousTimeoutSeconds;
            _motionOptions.HomeSequence = previousHomeSequence;
            _homeConfigurationSaveHealthy = false;
            UpdateHomeActionState();
            throw;
        }
    }

    private bool TryCommitHomeEditorBindings()
    {
        if (HomeEditorPanel is null)
        {
            return true;
        }

        var valid = true;
        foreach (var textBox in FindVisualChildren<TextBox>(HomeEditorPanel))
        {
            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            valid &= !Validation.GetHasError(textBox);
        }

        return valid;
    }

    private bool TryCommitPersistentInputBindings()
    {
        var valid = true;
        foreach (var textBox in FindVisualChildren<TextBox>(this))
        {
            if (IoItemsControl is not null && IsVisualDescendantOf(textBox, IoItemsControl))
            {
                continue;
            }

            textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            valid &= !Validation.GetHasError(textBox);
        }

        return valid;
    }

    private static bool IsVisualDescendantOf(DependencyObject element, DependencyObject ancestor)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private bool TrySaveHomeSettingsBeforeMotion()
    {
        if (!TryCommitHomeEditorBindings())
        {
            _homeConfigurationSaveHealthy = false;
            UpdateHomeActionState();
            RecordAlarm("HOME-PROFILE-INPUT", "回零参数包含无法识别的输入，命令未下发。");
            return false;
        }

        try
        {
            SaveMotionConfigurationOrThrow();
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RecordAlarm("HOME-PROFILE-SAVE", $"回零参数未校验并保存成功，命令未下发：{exception.Message}");
            return false;
        }
    }

    private void SetCommandStage(CommandStage stage, string text)
    {
        _commandStage = stage;
        UpdateHomeEditorState();
        if (CommandStateText is null)
        {
            return;
        }

        CommandStateText.Text = text;
        CommandStateText.Foreground = stage switch
        {
            CommandStage.Stopped => new SolidColorBrush(Color.FromRgb(0x39, 0xC5, 0x6B)),
            CommandStage.Failed => new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
            _ => new SolidColorBrush(Color.FromRgb(0x62, 0xB5, 0xFF))
        };
        if (stage is CommandStage.Stopped or CommandStage.Failed)
        {
            _commandStopwatch?.Stop();
        }

        UpdateCommandRuntime();
    }

    private void UpdateCommandRuntime()
    {
        if (CommandElapsedText is null)
        {
            return;
        }

        var elapsed = _commandStopwatch?.Elapsed ?? TimeSpan.Zero;
        CommandElapsedText.Text = elapsed.ToString(@"hh\:mm\:ss\.fff");
    }

    private void JogNegative_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(GetAxisFromSender(sender), -1);
    }

    private void JogPositive_Click(object sender, RoutedEventArgs e)
    {
        MoveSelected(GetAxisFromSender(sender), 1);
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        var axis = GetAxisFromSender(sender);
        if (axis is null)
        {
            return;
        }

        if (!TrySaveHomeSettingsBeforeMotion())
        {
            return;
        }

        if (!axis.HomeConfigured)
        {
            RecordAlarm($"AXIS-{axis.HardwareAxisNo:00}-HOME-CONFIG", $"{axis.Name} 未配置回零参数，命令未下发。");
            return;
        }

        if (_activeJogAxisNo is not null ||
            _activePositionAxisNo is not null ||
            _pendingStopAxisNos.Count > 0 ||
            _homeDeadlines.Count > 0 ||
            _homeSequenceCancellation is not null ||
            axis.IsMoving)
        {
            RecordAlarm($"AXIS-{axis.HardwareAxisNo:00}-HOME-BUSY", "已有运动或停止确认尚未结束，不能启动回原点。");
            return;
        }

        ExecuteMotion(
            axis,
            "HOME",
            () =>
            {
                EnsureServoEnabled(axis);
                _motionCard.Home(axis.HardwareAxisNo);
                axis.Homed = false;
                axis.IsMoving = true;
                axis.State = "回零命令已发送";
                StartHomeTracking(
                    axis.HardwareAxisNo,
                    DateTime.UtcNow.AddSeconds(_motionOptions.HomeTimeoutSeconds));
                _commandStopwatch = Stopwatch.StartNew();
                SetCommandStage(CommandStage.Issued, "回零命令已下发");
            });
    }

    private async void HomeAll_Click(object sender, RoutedEventArgs e)
    {
        if (_homeSequenceCancellation is not null)
        {
            return;
        }

        if (_activeJogAxisNo is not null ||
            _activePositionAxisNo is not null ||
            _pendingStopAxisNos.Count > 0 ||
            _homeDeadlines.Count > 0)
        {
            RecordAlarm("HOME-ALL-BUSY", "已有运动或停止确认尚未结束，不能启动顺序回原点。");
            return;
        }

        if (!EnsureConnected())
        {
            return;
        }

        if (!TrySaveHomeSettingsBeforeMotion())
        {
            return;
        }

        AxisStatus[] sequence;
        try
        {
            sequence = PreflightHomeSequence();
        }
        catch (Exception exception)
        {
            RecordAlarm("HOME-ALL-PREFLIGHT", FormatException(exception));
            return;
        }

        _homeSequenceCancellation = new CancellationTokenSource();
        HomeAllButton.SetCurrentValue(IsEnabledProperty, false);
        _commandStopwatch = Stopwatch.StartNew();
        try
        {
            foreach (var axis in sequence)
            {
                _homeSequenceCancellation.Token.ThrowIfCancellationRequested();
                var hardwareAxisNo = axis.HardwareAxisNo;
                _motionCard.Home(hardwareAxisNo);
                axis.Homed = false;
                axis.IsMoving = true;
                axis.State = "顺序回零中";
                var deadline = DateTime.UtcNow.AddSeconds(_motionOptions.HomeTimeoutSeconds);
                StartHomeTracking(hardwareAxisNo, deadline);
                SetCommandStage(CommandStage.Running, $"{axis.Name} 回零中");
                await WaitForHomeAsync(axis, deadline, _homeSequenceCancellation.Token);
            }

            SetCommandStage(CommandStage.Stopped, "顺序回零完成");
        }
        catch (OperationCanceledException)
        {
            foreach (var axis in Axes ?? [])
            {
                if (axis.IsMoving)
                {
                    axis.State = "回零序列已取消";
                }
            }
        }
        catch (Exception exception)
        {
            try
            {
                _motionCard.EmergencyStop();
                ClearAllHomeTracking();
                ArmAllHardwareAxisStopConfirmations(emergencyStopIssued: true);
                foreach (var axis in (Axes ?? []).Where(axis => axis.IsAvailable))
                {
                    axis.State = "顺序回零异常，已发送全轴急停，等待停止确认";
                }
            }
            catch (Exception emergencyException)
            {
                ActivateMotionSafetyLock(
                    "顺序回零异常，且全轴急停下发失败",
                    emergencyException,
                    "HOME-ALL-EMERGENCY-STOP");
            }

            RecordAlarm("HOME-ALL", FormatException(exception));
            SetCommandStage(CommandStage.Failed, "顺序回零失败");
        }
        finally
        {
            _homeSequenceCancellation.Dispose();
            _homeSequenceCancellation = null;
            HomeAllButton.SetCurrentValue(IsEnabledProperty, CanRunHomeSequence());
            PollMotionState();
        }
    }

    private void ServoOn_Click(object sender, RoutedEventArgs e)
    {
        SetServo(GetAxisFromSender(sender), true);
    }

    private void ServoOff_Click(object sender, RoutedEventArgs e)
    {
        SetServo(GetAxisFromSender(sender), false);
    }

    private void ServoAllOn_Click(object sender, RoutedEventArgs e)
    {
        SetAllServos(true);
    }

    private void ServoAllOff_Click(object sender, RoutedEventArgs e)
    {
        SetAllServos(false);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var axis = GetAxisFromSender(sender);
        if (axis is not null)
        {
            StopAxis(axis);
        }
    }

    private void StopAll_Click(object sender, RoutedEventArgs e)
    {
        if (!TryIssueGlobalEmergencyStop("操作员请求全轴急停", "EMERGENCY-STOP-FAILED"))
        {
            return;
        }

        _homeSequenceCancellation?.Cancel();
        _activeJogAxisNo = null;
        _activeJogInputOwner = null;
        _activePositionAxisNo = null;
        _activePositionTarget = null;
        _activePositionIssuedAtUtc = null;
        _activePositionDeadlineUtc = null;
        _activePositionTolerance = 0;
        _activePositionTimeoutMilliseconds = 0;
        _activePositionObservedMoving = false;
        _operatorStopRequestedAxisNo = null;
        _operatorImmediateStopRequested = false;
        _failedPositionAxisNo = null;
        _pendingStopAxisNos.Clear();
        _stopConfirmationDeadlines.Clear();
        _emergencyStopPendingAxisNos.Clear();
        ClearAllHomeTracking();
        _positionMoveCancellation?.Cancel();

        ArmAllHardwareAxisStopConfirmations(emergencyStopIssued: true);
        foreach (var axis in (Axes ?? []).Where(axis => axis.IsAvailable))
        {
            axis.State = "已发送全轴急停，等待停止确认";
        }

        SetCommandStage(
            _motionSafetyLock ? CommandStage.Failed : CommandStage.Releasing,
            _motionSafetyLock
                ? "全轴急停重试已下发，安全锁保持至重启"
                : "全轴急停，等待确认");
        PollMotionState();
    }

    private void ClearAlarm_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnyMotionWorkflowActive())
        {
            RecordAlarm(
                "CLEAR-ALARM-BUSY",
                "运动、回零或停止确认期间不能清除报警和停止原因。请先确认所有轴已停止。");
            return;
        }

        if (!ExecuteMotion(
                null,
                "CLEAR-ALARM",
                () => _motionCard.ClearAlarms(
                    (Axes ?? []).Where(axis => axis.IsAvailable).Select(axis => axis.HardwareAxisNo))))
        {
            return;
        }

        ViewModel?.AlarmRecords.Clear();
        // Keep active keys while a physical alarm is still present. Clearing them
        // here makes the following poll insert every active alarm straight back
        // into the history, so the operator sees no visible effect.
        PollMotionState();
    }

    private void IoMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string modeText } ||
            !Enum.TryParse<IoPointKind>(modeText, out var mode))
        {
            return;
        }

        ConfigureIoPoints(mode);
        PollIoState();
    }

    private void DigitalOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IoPoint { Kind: IoPointKind.DigitalOutput } point })
        {
            return;
        }

        if (ExecuteMotion(null, $"DO-{point.Channel}", () => _motionCard.WriteDigitalOutput(point.Channel, !point.IsOn)))
        {
            PollIoState();
        }
    }

    private void AnalogOutput_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: IoPoint { Kind: IoPointKind.AnalogOutput } point })
        {
            return;
        }

        if (ExecuteMotion(null, $"AO-{point.Channel}", () => _motionCard.WriteAnalogOutput(point.Channel, point.Value)))
        {
            PollIoState();
        }
    }

    private AxisStatus[] PreflightHomeSequence()
    {
        var hardwareSequence = _motionOptions.GetHomeSequence();
        if (hardwareSequence.Count == 0)
        {
            throw new InvalidDataException("回零序列为空。");
        }

        var axisByHardwareNo = (Axes ?? []).ToDictionary(axis => axis.HardwareAxisNo);
        var sequence = hardwareSequence.Select(hardwareAxisNo =>
        {
            if (!axisByHardwareNo.TryGetValue(hardwareAxisNo, out var axis) || !axis.IsAvailable)
            {
                throw new MotionCardException($"回零序列中的硬件轴 {hardwareAxisNo} 不可用。");
            }

            _motionOptions.GetHomeProfile(hardwareAxisNo).Validate(requireEnabled: true);
            return axis;
        }).ToArray();

        var busError = _motionCard.ReadBusErrorCode();
        if (busError != 0 && busError != RingRedundancyDisconnectedWarning)
        {
            throw new MotionCardException($"EtherCAT 总线错误 0x{busError:X4}。");
        }

        foreach (var axis in (Axes ?? []).Where(axis => axis.IsAvailable))
        {
            var snapshot = _motionCard.ReadAxis(axis.HardwareAxisNo);
            ApplySnapshot(axis, snapshot);
            ProcessSnapshotAlarms(axis, snapshot);
            if (snapshot.IsMoving || snapshot.Alarm || snapshot.EmergencyInput)
            {
                throw new MotionCardException($"{axis.Name} 正在运动或存在报警/急停输入，不能启动顺序回零。");
            }
        }

        if (sequence.Any(axis => !axis.ServoOn))
        {
            throw new MotionCardException("顺序回零前必须先使能序列中的全部轴。");
        }

        return sequence;
    }

    private void StartHomeTracking(int hardwareAxisNo, DateTime deadline)
    {
        _homeDeadlines[hardwareAxisNo] = deadline;
        _homeIssuedAtUtc[hardwareAxisNo] = DateTime.UtcNow;
        _homeObservedMovingAxisNos.Remove(hardwareAxisNo);
    }

    private void ClearHomeTracking(int hardwareAxisNo)
    {
        _homeDeadlines.Remove(hardwareAxisNo);
        _homeIssuedAtUtc.Remove(hardwareAxisNo);
        _homeObservedMovingAxisNos.Remove(hardwareAxisNo);
    }

    private void ClearAllHomeTracking()
    {
        _homeDeadlines.Clear();
        _homeIssuedAtUtc.Clear();
        _homeObservedMovingAxisNos.Clear();
    }

    private async Task WaitForHomeAsync(AxisStatus axis, DateTime deadline, CancellationToken cancellationToken)
    {
        var issuedAt = DateTime.UtcNow;
        var observedMoving = false;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _motionCard.ReadAxis(axis.HardwareAxisNo);
            ApplySnapshot(axis, snapshot);
            ProcessSnapshotAlarms(axis, snapshot);
            if (snapshot.Alarm || snapshot.EmergencyInput)
            {
                throw new MotionCardException(
                    $"{axis.Name}（硬件轴 {axis.HardwareAxisNo}）回零时发生报警 0x{snapshot.AxisErrorCode:X4}。");
            }

            if (snapshot.IsMoving)
            {
                observedMoving = true;
            }

            if (!snapshot.IsMoving && snapshot.Homed)
            {
                return;
            }

            if (!snapshot.IsMoving && !snapshot.Homed &&
                (snapshot.StopReason != 0 ||
                 observedMoving ||
                 DateTime.UtcNow >= issuedAt.AddMilliseconds(Math.Max(1000, _motionOptions.PollIntervalMilliseconds * 3))))
            {
                throw new MotionCardException(
                    $"{axis.Name} 回零未完成即停止，停止原因 {snapshot.StopReason}。",
                    "回零完成检查");
            }

            await Task.Delay(_motionOptions.PollIntervalMilliseconds, cancellationToken);
        }

        throw new TimeoutException(
            $"{axis.Name}（硬件轴 {axis.HardwareAxisNo}）在 {_motionOptions.HomeTimeoutSeconds} 秒内未完成回零。");
    }

    private void PollMotionState()
    {
        if (_polling || !_motionCard.IsOpen || _closed)
        {
            return;
        }

        _polling = true;
        try
        {
            EnforceSafetyDeadlines(DateTime.UtcNow);
            var busError = _motionCard.ReadBusErrorCode();
            if (busError != 0 && busError != RingRedundancyDisconnectedWarning)
            {
                SetConnectionText($"运动控制：EtherCAT 总线错误 0x{busError:X4}");
                RecordAlarmOnce(
                    $"bus:{busError}",
                    $"BUS-{busError:X4}",
                    $"EtherCAT 总线错误 0x{busError:X4}。请检查 ENI 配置、从站状态和网线连接。");
                foreach (var axis in Axes ?? [])
                {
                    axis.State = $"总线错误 0x{busError:X4}";
                    axis.StatusReadHealthy = false;
                    axis.Alarm = true;
                }

                HandleGlobalMonitoringFailure($"EtherCAT 总线错误 0x{busError:X4}");
                return;
            }

            RemoveAlarmKeys("bus:");
            if (busError == RingRedundancyDisconnectedWarning)
            {
                SetConnectionText($"运动控制：环网冗余断开警告 0x{busError:X4}（运动未锁定）");
                RecordAlarmOnce(
                    "bus-warning:0228",
                    "BUS-WARN-0228",
                    "EtherCAT 环网冗余连接断开；程序继续读取轴状态并允许运动，请确认主链路正常。");
            }
            else
            {
                _activeAlarmKeys.Remove("bus-warning:0228");
                SetConnectionText(_motionSafetyLock
                    ? "运动控制：停止安全链异常，运动锁定（需重启）"
                    : _motionOptions.SimulationMode
                        ? $"运动控制：仿真模式（{_motionCard.AxisCount} 轴）"
                        : _connectedCardNo is { } cardNo
                            ? $"运动控制：EtherCAT 正常（卡 {cardNo}，{_motionCard.AxisCount} 轴）"
                            : $"运动控制：EtherCAT 正常（{_motionCard.AxisCount} 轴）");
            }

            foreach (var axis in Axes ?? [])
            {
                if (!axis.IsAvailable)
                {
                    continue;
                }

                try
                {
                    var snapshot = _motionCard.ReadAxis(axis.HardwareAxisNo);
                    ApplySnapshot(axis, snapshot);
                    ProcessSnapshotAlarms(axis, snapshot);
                    _activeAlarmKeys.Remove($"poll:{axis.HardwareAxisNo}");
                }
                catch (Exception exception)
                {
                    axis.State = "状态读取失败";
                    axis.StatusReadHealthy = false;
                    RecordAlarmOnce(
                        $"poll:{axis.HardwareAxisNo}",
                        $"AXIS-{axis.HardwareAxisNo:00}-READ",
                        FormatException(exception));
                    HandleAxisMonitoringFailure(axis, "轴状态读取失败");
                }
            }

            PollHiddenStopConfirmations();
            PollIoState();
            UpdateCommandRuntime();
        }
        catch (Exception exception)
        {
            SetConnectionText("运动控制：通讯异常");
            RecordAlarmOnce("poll-general", "MOTION-POLL", FormatException(exception));
            foreach (var axis in (Axes ?? []).Where(axis => axis.IsAvailable))
            {
                axis.StatusReadHealthy = false;
                axis.State = "运动监控通讯异常";
            }

            HandleGlobalMonitoringFailure("运动监控通讯异常");
        }
        finally
        {
            EnforceSafetyDeadlines(DateTime.UtcNow);
            HomeAllButton?.SetCurrentValue(IsEnabledProperty, CanRunHomeSequence());
            UpdateHomeEditorState();
            _polling = false;
        }
    }

    private void HandleAxisMonitoringFailure(AxisStatus axis, string reason)
    {
        if (!IsAxisMotionWorkflowActive(axis.HardwareAxisNo) && !axis.IsMoving)
        {
            return;
        }

        if (_activePositionAxisNo == axis.HardwareAxisNo)
        {
            _failedPositionAxisNo = axis.HardwareAxisNo;
        }

        SetCommandStage(CommandStage.Failed, "运动监控中断，正在安全停止");
        IssueAxisStopWithEscalation(
            axis,
            axis.HardwareAxisNo,
            immediate: true,
            "MONITORING",
            reason);
        _homeSequenceCancellation?.Cancel();
    }

    private void HandleGlobalMonitoringFailure(string reason)
    {
        if (!IsAnyMotionWorkflowActive() || _emergencyStopPendingAxisNos.Count > 0)
        {
            return;
        }

        try
        {
            _motionCard.EmergencyStop();
            _homeSequenceCancellation?.Cancel();
            ClearAllHomeTracking();
            ArmAllHardwareAxisStopConfirmations(emergencyStopIssued: true);
            foreach (var axis in (Axes ?? []).Where(axis => axis.IsAvailable))
            {
                axis.State = $"{reason}，已发送全轴急停，等待停止确认";
            }

            SetCommandStage(CommandStage.Failed, "运动监控中断，已发送全轴急停");
            RecordAlarmOnce(
                "monitoring-emergency-stop",
                "MONITORING-EMERGENCY-STOP",
                $"{reason}；因无法可靠监控运动，已发送全轴急停。");
        }
        catch (Exception exception)
        {
            ActivateMotionSafetyLock(
                $"{reason}，且全轴急停下发失败",
                exception,
                "EMERGENCY-STOP-FAILED");
        }
    }

    private void EnforceSafetyDeadlines(DateTime now)
    {
        EnforceStopConfirmationDeadlines(now);

        if (_activePositionAxisNo is { } positionAxisNo &&
            _activePositionDeadlineUtc is { } positionDeadline &&
            now >= positionDeadline &&
            _failedPositionAxisNo is null)
        {
            var axis = Axes?.FirstOrDefault(item => item.HardwareAxisNo == positionAxisNo);
            _failedPositionAxisNo = positionAxisNo;
            if (axis is not null)
            {
                axis.State = "定位运动超时，正在安全停止";
            }

            RecordAlarmOnce(
                $"position-timeout:{positionAxisNo}",
                $"AXIS-{positionAxisNo:00}-MOVE-TIMEOUT",
                $"定位运动在 {_activePositionTimeoutMilliseconds} ms 内未完成，正在执行安全停止。");
            SetCommandStage(CommandStage.Failed, "运动超时，停止确认中");
            IssueAxisStopWithEscalation(
                axis,
                positionAxisNo,
                immediate: false,
                "MOVE-TIMEOUT",
                "定位运动超时");
        }

        foreach (var item in _homeDeadlines.Where(item => now >= item.Value).ToArray())
        {
            var axis = Axes?.FirstOrDefault(candidate => candidate.HardwareAxisNo == item.Key);
            RecordAlarmOnce(
                $"home-timeout:{item.Key}",
                $"AXIS-{item.Key + 1:00}-HOME-TIMEOUT",
                $"回零在 {_motionOptions.HomeTimeoutSeconds} 秒内未完成，正在执行立即停止。");
            SetCommandStage(CommandStage.Failed, "回零超时，停止确认中");
            IssueAxisStopWithEscalation(
                axis,
                item.Key,
                immediate: true,
                "HOME-TIMEOUT",
                "回零超时");
            _homeSequenceCancellation?.Cancel();
        }
    }

    private void EnforceStopConfirmationDeadlines(DateTime now)
    {
        var expiredAxisNos = _stopConfirmationDeadlines
            .Where(item => now >= item.Value && _pendingStopAxisNos.Contains(item.Key))
            .Select(item => item.Key)
            .ToArray();
        if (expiredAxisNos.Length == 0)
        {
            return;
        }

        var ordinaryStopTimeouts = expiredAxisNos
            .Where(axisNo => !_emergencyStopPendingAxisNos.Contains(axisNo))
            .ToArray();
        if (ordinaryStopTimeouts.Length > 0)
        {
            foreach (var axisNo in ordinaryStopTimeouts)
            {
                RecordAlarmOnce(
                    $"stop-confirm-timeout:{axisNo}",
                    $"AXIS-{axisNo:00}-STOP-CONFIRM-TIMEOUT",
                    $"单轴停止命令在 {_motionOptions.StopConfirmationTimeoutMilliseconds} ms 内未确认停止，正在升级全轴急停。");
            }

            try
            {
                _motionCard.EmergencyStop();
                _homeSequenceCancellation?.Cancel();
                ClearAllHomeTracking();
                var availableAxes = (Axes ?? []).Where(axis => axis.IsAvailable).ToArray();
                ArmAllHardwareAxisStopConfirmations(emergencyStopIssued: true);
                foreach (var axis in availableAxes)
                {
                    axis.State = "停止确认超时，已升级全轴急停，等待确认";
                }

                SetCommandStage(CommandStage.Failed, "停止确认超时，已升级全轴急停");
                return;
            }
            catch (Exception exception)
            {
                ActivateMotionSafetyLock(
                    "停止确认超时，且升级全轴急停下发失败",
                    exception,
                    "EMERGENCY-STOP-FAILED");
                return;
            }
        }

        foreach (var axisNo in expiredAxisNos)
        {
            _stopConfirmationDeadlines.Remove(axisNo);
            var axis = Axes?.FirstOrDefault(item => item.HardwareAxisNo == axisNo);
            if (axis is not null)
            {
                axis.State = "全轴急停后仍未确认停止，运动已锁定";
            }

            RecordAlarmOnce(
                $"emergency-stop-confirm-timeout:{axisNo}",
                $"AXIS-{axisNo:00}-EMERGENCY-CONFIRM-TIMEOUT",
                $"全轴急停后 {_motionOptions.StopConfirmationTimeoutMilliseconds} ms 内仍未确认该轴停止，已保持软件运动锁定。");
        }

        ActivateMotionSafetyLock(
            "全轴急停已下发，但仍有硬件轴未在期限内确认停止",
            null,
            "EMERGENCY-STOP-CONFIRM-TIMEOUT",
            armStopConfirmations: false);
    }

    private void PollIoState()
    {
        if (!_motionCard.IsOpen || ViewModel is not { } viewModel)
        {
            return;
        }

        try
        {
            switch (_ioMode)
            {
                case IoPointKind.DigitalInput:
                {
                    var inputs = _motionCard.ReadDigitalInputs(_motionOptions.DigitalInputPort);
                    foreach (var point in viewModel.IoPoints)
                    {
                        point.IsOn = (inputs & (1u << point.BitNo)) != 0;
                    }

                    break;
                }
                case IoPointKind.DigitalOutput:
                {
                    var outputs = _motionCard.ReadDigitalOutputs(_motionOptions.DigitalOutputPort);
                    foreach (var point in viewModel.IoPoints)
                    {
                        point.IsOn = (outputs & (1u << point.BitNo)) != 0;
                    }

                    break;
                }
                case IoPointKind.AnalogInput:
                    foreach (var point in viewModel.IoPoints)
                    {
                        point.Value = _motionCard.ReadAnalogInput(point.Channel);
                    }

                    break;
                case IoPointKind.AnalogOutput:
                    foreach (var point in viewModel.IoPoints)
                    {
                        if (Keyboard.FocusedElement is TextBox { DataContext: IoPoint focusedPoint } &&
                            ReferenceEquals(point, focusedPoint))
                        {
                            continue;
                        }

                        point.Value = _motionCard.ReadAnalogOutput(point.Channel);
                    }

                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }

            _activeAlarmKeys.Remove("io-read");
        }
        catch (Exception exception)
        {
            RecordAlarmOnce("io-read", "IO-READ", FormatException(exception));
        }
    }

    private void ConfigureIoPoints(IoPointKind mode)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        _ioMode = mode;
        var count = mode switch
        {
            IoPointKind.DigitalInput => _motionCard.DigitalInputCount,
            IoPointKind.DigitalOutput => _motionCard.DigitalOutputCount,
            IoPointKind.AnalogInput => _motionCard.AnalogInputCount,
            IoPointKind.AnalogOutput => _motionCard.AnalogOutputCount,
            _ => 0
        };

        // Digital port reads return a 32-bit image. Do not truncate the 32
        // hardware points to the old 20-point dashboard limit.
        if (mode is IoPointKind.DigitalInput or IoPointKind.DigitalOutput)
        {
            count = Math.Min(count, 32);
        }
        viewModel.IoPoints.Clear();
        for (var channel = 0; channel < count; channel++)
        {
            var defaultName = mode switch
            {
                IoPointKind.DigitalInput => $"X{channel:00}",
                IoPointKind.DigitalOutput => $"Y{channel:00}",
                IoPointKind.AnalogInput => $"AI{channel}",
                IoPointKind.AnalogOutput => $"AO{channel}",
                _ => channel.ToString()
            };
            var nameKey = GetIoNameKey(mode, channel);
            var displayName = _motionOptions.IoPointNames.GetValueOrDefault(nameKey);
            viewModel.IoPoints.Add(new IoPoint
            {
                Channel = channel,
                Kind = mode,
                DefaultName = defaultName,
                Name = string.IsNullOrWhiteSpace(displayName) ? defaultName : displayName
            });
        }

        SetIoModeButtonState(DigitalInputTab, mode == IoPointKind.DigitalInput);
        SetIoModeButtonState(DigitalOutputTab, mode == IoPointKind.DigitalOutput);
        SetIoModeButtonState(AnalogInputTab, mode == IoPointKind.AnalogInput);
        SetIoModeButtonState(AnalogOutputTab, mode == IoPointKind.AnalogOutput);
    }

    private static string GetIoNameKey(IoPointKind kind, int channel)
    {
        return $"{kind}:{channel}";
    }

    private static void SetIoModeButtonState(Button button, bool selected)
    {
        button.Background = selected
            ? new SolidColorBrush(Color.FromRgb(0x0D, 0x6E, 0xE8))
            : new SolidColorBrush(Color.FromRgb(0x29, 0x4A, 0x64));
        button.Foreground = Brushes.White;
        button.FontWeight = FontWeights.Bold;
    }

    private void PollHiddenStopConfirmations()
    {
        var visibleAxisNos = (Axes ?? [])
            .Where(axis => axis.IsAvailable)
            .Select(axis => axis.HardwareAxisNo)
            .ToHashSet();
        foreach (var hardwareAxisNo in _pendingStopAxisNos
                     .Where(axisNo => !visibleAxisNos.Contains(axisNo))
                     .ToArray())
        {
            try
            {
                var snapshot = _motionCard.ReadAxis(hardwareAxisNo);
                _activeAlarmKeys.Remove($"hidden-stop-poll:{hardwareAxisNo}");
                if (!snapshot.IsMoving)
                {
                    CompleteStopConfirmation(hardwareAxisNo, null);
                }
            }
            catch (Exception exception)
            {
                RecordAlarmOnce(
                    $"hidden-stop-poll:{hardwareAxisNo}",
                    $"AXIS-{hardwareAxisNo:00}-STOP-READ",
                    $"未显示硬件轴 {hardwareAxisNo} 的停止状态读取失败：{FormatException(exception)}");
            }
        }
    }

    private void CompleteStopConfirmation(int hardwareAxisNo, AxisStatus? axis)
    {
        var stopWasFailure = _motionSafetyLock ||
                             _failedPositionAxisNo == hardwareAxisNo ||
                             _commandStage == CommandStage.Failed;
        _pendingStopAxisNos.Remove(hardwareAxisNo);
        _stopConfirmationDeadlines.Remove(hardwareAxisNo);
        _emergencyStopPendingAxisNos.Remove(hardwareAxisNo);
        ClearHomeTracking(hardwareAxisNo);
        if (_activeJogAxisNo == hardwareAxisNo)
        {
            _activeJogAxisNo = null;
            _activeJogInputOwner = null;
        }

        if (axis is not null)
        {
            axis.State = _motionSafetyLock
                ? "已确认停止，运动安全锁保持（需重启程序）"
                : _failedPositionAxisNo == hardwareAxisNo
                    ? "定位失败，已确认停止"
                    : "已确认停止";
        }

        if (_pendingStopAxisNos.Count == 0)
        {
            SetCommandStage(
                stopWasFailure ? CommandStage.Failed : CommandStage.Stopped,
                _motionSafetyLock
                    ? "全部硬件轴已确认停止，运动安全锁保持至重启"
                    : stopWasFailure
                        ? "异常停止已全部确认"
                        : "已确认全部停止");
        }
        else
        {
            SetCommandStage(
                stopWasFailure ? CommandStage.Failed : CommandStage.Releasing,
                $"停止确认中（剩余 {_pendingStopAxisNos.Count} 轴）");
        }
    }

    private void ApplySnapshot(AxisStatus axis, MotionAxisSnapshot snapshot)
    {
        axis.Position = snapshot.FeedbackPosition;
        axis.Target = snapshot.TargetPosition;
        axis.Speed = snapshot.Speed;
        axis.ServoOn = snapshot.ServoEnabled;
        axis.Homed = snapshot.Homed;
        axis.Alarm = snapshot.Alarm || snapshot.EmergencyInput;
        axis.PositiveLimit = snapshot.PositiveLimit;
        axis.NegativeLimit = snapshot.NegativeLimit;
        axis.IsMoving = snapshot.IsMoving;
        axis.State = _motionSafetyLock
            ? "停止安全链异常，运动已锁定（需重启程序）"
            : snapshot.StateText;
        axis.StatusReadHealthy = !_motionSafetyLock;

        if (_pendingStopAxisNos.Contains(axis.HardwareAxisNo) && !snapshot.IsMoving)
        {
            CompleteStopConfirmation(axis.HardwareAxisNo, axis);
        }
        else if (_activeJogAxisNo == axis.HardwareAxisNo && !snapshot.IsMoving)
        {
            _activeJogAxisNo = null;
            _activeJogInputOwner = null;
            axis.State = "JOG 已停止";
            SetCommandStage(CommandStage.Stopped, "JOG 已停止");
        }

        if (_activePositionAxisNo == axis.HardwareAxisNo && _positionMoveCancellation is null)
        {
            if (snapshot.IsMoving)
            {
                _activePositionObservedMoving = true;
            }
            else
            {
                try
                {
                    if (!TryCompleteTrackedPosition(axis, snapshot))
                    {
                        SetCommandStage(CommandStage.Running, "等待编码器到位");
                    }
                }
                catch (Exception exception)
                {
                    axis.State = "定位停止但未正常到位";
                    SetCommandStage(CommandStage.Failed, "未正常到位");
                    RecordAlarm($"AXIS-{axis.HardwareAxisNo:00}-MOVE-CHECK", FormatException(exception));
                    ClearPositionTracking(axis.HardwareAxisNo);
                }
            }
        }

        if (snapshot.Homed &&
            !snapshot.IsMoving &&
            !snapshot.Alarm &&
            !snapshot.EmergencyInput)
        {
            if (_homeDeadlines.ContainsKey(axis.HardwareAxisNo))
            {
                ClearHomeTracking(axis.HardwareAxisNo);
                axis.State = "回零完成";
                if (_homeSequenceCancellation is null)
                {
                    SetCommandStage(CommandStage.Stopped, "回零完成");
                }
            }
        }
        else if (_homeSequenceCancellation is null &&
                 _homeDeadlines.ContainsKey(axis.HardwareAxisNo))
        {
            if (snapshot.IsMoving)
            {
                _homeObservedMovingAxisNos.Add(axis.HardwareAxisNo);
            }

            if (snapshot.Alarm || snapshot.EmergencyInput)
            {
                RecordAlarmOnce(
                    $"home-failed:{axis.HardwareAxisNo}",
                    $"AXIS-{axis.HardwareAxisNo:00}-HOME-FAILED",
                    $"{axis.Name} 回零时发生报警或急停输入，错误码 0x{snapshot.AxisErrorCode:X4}。");
                SetCommandStage(CommandStage.Failed, "回零报警，正在安全停止");
                IssueAxisStopWithEscalation(
                    axis,
                    axis.HardwareAxisNo,
                    immediate: true,
                    "HOME-FAILED",
                    "回零报警，已补发立即停止");
            }
            else if (!snapshot.IsMoving &&
                     (_homeObservedMovingAxisNos.Contains(axis.HardwareAxisNo) ||
                      snapshot.StopReason != 0 ||
                      (_homeIssuedAtUtc.TryGetValue(axis.HardwareAxisNo, out var issuedAt) &&
                       DateTime.UtcNow >= issuedAt.AddMilliseconds(
                           Math.Max(1000, _motionOptions.PollIntervalMilliseconds * 3)))))
            {
                SetCommandStage(CommandStage.Failed, "回零未完成");
                RecordAlarmOnce(
                    $"home-failed:{axis.HardwareAxisNo}",
                    $"AXIS-{axis.HardwareAxisNo:00}-HOME-FAILED",
                    $"{axis.Name} 回零未完成即停止，停止原因 {snapshot.StopReason}。");
                IssueAxisStopWithEscalation(
                    axis,
                    axis.HardwareAxisNo,
                    immediate: true,
                    "HOME-EARLY-STOP",
                    "回零未完成即停止，已补发立即停止");
            }
        }
    }

    private void ProcessSnapshotAlarms(AxisStatus axis, MotionAxisSnapshot snapshot)
    {
        var faultKey = $"axis-fault:{axis.HardwareAxisNo}";
        if (snapshot.Alarm)
        {
            RecordAlarmOnce(
                faultKey,
                $"AXIS-{axis.HardwareAxisNo:00}-{snapshot.AxisErrorCode:X4}",
                $"{axis.Name}（硬件轴 {axis.HardwareAxisNo}）报警，状态机 {snapshot.StateMachine}，错误码 0x{snapshot.AxisErrorCode:X4}。");
        }
        else
        {
            _activeAlarmKeys.Remove(faultKey);
        }

        var emergencyKey = $"axis-emg:{axis.HardwareAxisNo}";
        if (snapshot.EmergencyInput)
        {
            RecordAlarmOnce(
                emergencyKey,
                $"AXIS-{axis.HardwareAxisNo:00}-EMG",
                $"{axis.Name}（硬件轴 {axis.HardwareAxisNo}）急停输入有效。");
        }
        else
        {
            _activeAlarmKeys.Remove(emergencyKey);
        }

        var stopKey = $"axis-stop:{axis.HardwareAxisNo}:{snapshot.StopReason}";
        if (snapshot.StopReason != 0)
        {
            RecordAlarmOnce(
                stopKey,
                $"AXIS-{axis.HardwareAxisNo:00}-STOP-{snapshot.StopReason}",
                $"{axis.Name}（硬件轴 {axis.HardwareAxisNo}）异常停止，停止原因 {snapshot.StopReason}。");
        }
        else
        {
            RemoveAlarmKeys($"axis-stop:{axis.HardwareAxisNo}:");
        }
    }

    private void MoveSelected(AxisStatus? axis, int direction)
    {
        if (axis is null)
        {
            return;
        }

        ExecuteMotion(
            axis,
            "MOVE",
            () =>
            {
                EnsureServoEnabled(axis);
                if (!double.IsFinite(axis.JogDistance) || axis.JogDistance <= 0)
                {
                    throw new InvalidDataException("点动距离必须是大于 0 的有限数值。");
                }

                if (!double.IsFinite(axis.JogSpeed) || axis.JogSpeed <= 0)
                {
                    throw new InvalidDataException("点动速度必须是大于 0 的有限数值。");
                }

                _motionCard.MoveRelative(
                    axis.HardwareAxisNo,
                    direction * axis.JogDistance,
                    axis.JogSpeed);
                axis.State = direction > 0 ? "正向寸动命令已发送" : "负向寸动命令已发送";
            });
    }

    private void SetAllServos(bool enabled)
    {
        if (enabled && IsAnyMotionWorkflowActive())
        {
            RecordAlarm(
                "SERVO-ALL-ON-BUSY",
                "仍有轴在运动、回零或等待停止确认，禁止全轴使能。请先确认所有轴已停止。");
            return;
        }

        if (enabled && (Axes?.Any(axis => axis.IsAvailable && !axis.StatusReadHealthy) ?? true))
        {
            RecordAlarm(
                "SERVO-ALL-ON-STATUS-INVALID",
                "存在轴状态读取异常，禁止使能。请先恢复运动卡通讯并确认实时状态。");
            return;
        }

        if (!enabled && IsAnyMotionWorkflowActive())
        {
            RecordAlarm(
                "SERVO-ALL-OFF-BUSY",
                "仍有轴在运动、回零或等待停止确认。请先使用减速停止或全轴急停，并确认停止后再解除使能。");
            return;
        }

        if (!ExecuteMotion(null, enabled ? "SERVO-ALL-ON" : "SERVO-ALL-OFF", () => _motionCard.SetAllServos(enabled)))
        {
            return;
        }

        foreach (var axis in Axes ?? [])
        {
            axis.State = enabled ? "全轴使能命令已完成" : "全轴已停止并解除使能";
        }

        if (!enabled)
        {
            _homeSequenceCancellation?.Cancel();
            _activeJogAxisNo = null;
            _activeJogInputOwner = null;
            _activePositionAxisNo = null;
            _activePositionTarget = null;
            _activePositionIssuedAtUtc = null;
            _activePositionDeadlineUtc = null;
            _activePositionTolerance = 0;
            _activePositionTimeoutMilliseconds = 0;
            _activePositionObservedMoving = false;
            _operatorStopRequestedAxisNo = null;
            _operatorImmediateStopRequested = false;
            _failedPositionAxisNo = null;
            _pendingStopAxisNos.Clear();
            _stopConfirmationDeadlines.Clear();
            _emergencyStopPendingAxisNos.Clear();
            ClearAllHomeTracking();
            _positionMoveCancellation?.Cancel();
            SetCommandStage(CommandStage.Stopped, "全轴已解除使能");
        }

        PollMotionState();
    }

    private void SetServo(AxisStatus? axis, bool enabled)
    {
        if (axis is null || axis.ServoOn == enabled)
        {
            return;
        }

        if (enabled && !axis.StatusReadHealthy)
        {
            RecordAlarm(
                $"AXIS-{axis.HardwareAxisNo:00}-SERVO-ON-STATUS-INVALID",
                "轴状态读取异常，禁止使能。请先恢复通讯并确认实时状态。");
            return;
        }

        if (enabled &&
            (_homeSequenceCancellation is not null ||
             IsAxisMotionWorkflowActive(axis.HardwareAxisNo)))
        {
            RecordAlarm(
                $"AXIS-{axis.HardwareAxisNo:00}-SERVO-ON-BUSY",
                "该轴正在运动、回零或等待停止确认，禁止使能。请先确认轴已停止。");
            return;
        }

        if (!enabled &&
            (_homeSequenceCancellation is not null ||
             IsAxisMotionWorkflowActive(axis.HardwareAxisNo)))
        {
            RecordAlarm(
                $"AXIS-{axis.HardwareAxisNo:00}-SERVO-OFF-BUSY",
                "该轴正在运动、回零或等待停止确认。请先停止并确认后再解除使能。");
            return;
        }

        if (ExecuteMotion(
                axis,
                enabled ? "SERVO-ON" : "SERVO-OFF",
                () => _motionCard.ServoOn(axis.HardwareAxisNo, enabled)))
        {
            axis.State = enabled ? "伺服已使能" : "伺服已解除";
            PollMotionState();
        }
    }

    private bool StopAxis(AxisStatus axis, bool immediate = false)
    {
        if (!immediate && _activeJogAxisNo == axis.HardwareAxisNo)
        {
            StopActiveJog("手动停止");
            return _pendingStopAxisNos.Contains(axis.HardwareAxisNo);
        }

        if (_activePositionAxisNo == axis.HardwareAxisNo)
        {
            _operatorStopRequestedAxisNo = axis.HardwareAxisNo;
            _operatorImmediateStopRequested = immediate;
        }

        SetCommandStage(
            CommandStage.Releasing,
            immediate ? "单轴立即停止中" : "减速停止中");
        if (IssueAxisStopWithEscalation(
                axis,
                axis.HardwareAxisNo,
                immediate,
                immediate ? "MANUAL-IMMEDIATE" : "MANUAL",
                immediate ? "已发送单轴立即停止" : "已发送减速停止"))
        {
            PollMotionState();
            return true;
        }

        return false;
    }

    private bool ExecuteMotion(AxisStatus? axis, string code, Action action)
    {
        if (!EnsureConnected())
        {
            return false;
        }

        try
        {
            action();
            return true;
        }
        catch (Exception exception)
        {
            if (axis is not null)
            {
                axis.State = "命令执行失败";
            }

            RecordAlarm(
                axis is null ? code : $"AXIS-{axis.HardwareAxisNo:00}-{code}",
                FormatException(exception));
            return false;
        }
    }

    private bool EnsureConnected()
    {
        if (_calibrationOperationActive)
        {
            RecordAlarmOnce(
                "motion-command-blocked-by-calibration",
                "CALIBRATION-IN-PROGRESS",
                "九点标定正在运行，其他运动命令已被暂时阻止。 ");
            return false;
        }

        if (_motionSafetyLock)
        {
            RecordAlarmOnce(
                "motion-command-blocked-by-safety-lock",
                "MOTION-SAFETY-LOCK",
                $"运动命令已被安全锁阻止：{_motionSafetyLockReason ?? "停止安全链异常"}。请确认机构安全并重启程序。");
            return false;
        }

        if (_motionCard.IsOpen)
        {
            return true;
        }

        return false;
    }

    private static void EnsureServoEnabled(AxisStatus axis)
    {
        if (!axis.StatusReadHealthy)
        {
            throw new MotionCardException($"{axis.Name} 状态读取异常，禁止下发运动命令。");
        }

        if (!axis.ServoOn)
        {
            throw new MotionCardException($"{axis.Name} 尚未使能，命令未执行。");
        }

        if (axis.Alarm)
        {
            throw new MotionCardException($"{axis.Name} 存在报警或急停信号，命令未执行。");
        }
    }

    private AxisStatus? GetAxisFromSender(object sender)
    {
        if (sender is FrameworkElement { DataContext: AxisStatus axisFromRow })
        {
            SelectedAxis = axisFromRow;
            return axisFromRow;
        }

        return SelectedAxis;
    }

    private void UpdateAxisAvailability(int detectedAxisCount)
    {
        foreach (var axis in Axes ?? [])
        {
            var homeProfile = _motionOptions.GetHomeProfile(axis.HardwareAxisNo);
            axis.IsAvailable = axis.HardwareAxisNo >= 0 && axis.HardwareAxisNo < detectedAxisCount;
            axis.StatusReadHealthy = false;
            UpdateAxisHomeConfiguration(axis, homeProfile);
            axis.State = axis.IsAvailable ? "等待首次状态读取" : "控制卡未配置该轴";
        }

        HomeAllButton?.SetCurrentValue(IsEnabledProperty, CanRunHomeSequence());
        UpdateHomeEditorState();
    }

    private static void UpdateAxisHomeConfiguration(AxisStatus axis, MotionHomeProfile profile)
    {
        axis.HomeConfigured = profile.Enabled;
        axis.HomeConfigurationSummary = profile.Enabled
            ? $"模式 {profile.Mode}｜低速 {profile.LowVelocity:0.###} units（脉冲）/s｜高速 {profile.HighVelocity:0.###} units（脉冲）/s｜偏移 {profile.OffsetPosition:0.###} {axis.Unit}"
            : "当前轴未启用回零";
    }

    private void UpdateHomeEditorState()
    {
        if (HomeEditorPanel is null)
        {
            return;
        }

        HomeEditorPanel.IsEnabled =
            ViewModel?.MotionControlsEnabled == true &&
            !_motionSafetyLock &&
            !IsAnyMotionWorkflowActive() &&
            SelectedAxis is { IsAvailable: true, StatusReadHealthy: true, IsMoving: false };
        UpdateHomeActionState();
    }

    private void UpdateHomeActionState()
    {
        var workflowIdle = !IsAnyMotionWorkflowActive();
        HomeCurrentButton?.SetCurrentValue(
            IsEnabledProperty,
            _homeConfigurationSaveHealthy &&
            workflowIdle &&
            !_motionSafetyLock &&
            SelectedAxis?.CanHome == true);
        HomeAllButton?.SetCurrentValue(
            IsEnabledProperty,
            _homeConfigurationSaveHealthy && CanRunHomeSequence());
    }

    private bool CanRunHomeSequence()
    {
        var axisByHardwareNo = (Axes ?? []).ToDictionary(axis => axis.HardwareAxisNo);
        var sequence = _motionOptions.GetHomeSequence();
        return ViewModel?.MotionControlsEnabled == true &&
               !_motionSafetyLock &&
               !IsAnyMotionWorkflowActive() &&
               sequence.Count > 0 &&
               sequence.All(hardwareAxisNo =>
                   axisByHardwareNo.TryGetValue(hardwareAxisNo, out var axis) &&
                   axis.IsAvailable &&
                   axis.StatusReadHealthy &&
                   axis.HomeConfigured &&
                   axis.ServoOn &&
                   !axis.Alarm &&
                   !axis.IsMoving);
    }

    private bool IsAnyMotionWorkflowActive()
    {
        return _calibrationOperationActive ||
               _activeJogAxisNo is not null ||
               _activePositionAxisNo is not null ||
               _pendingStopAxisNos.Count > 0 ||
               _homeDeadlines.Count > 0 ||
               _homeSequenceCancellation is not null ||
               (Axes?.Any(axis => axis.IsAvailable && axis.IsMoving) ?? false);
    }

    private bool IsAxisMotionWorkflowActive(int hardwareAxisNo)
    {
        return _activeJogAxisNo == hardwareAxisNo ||
               _activePositionAxisNo == hardwareAxisNo ||
               _pendingStopAxisNos.Contains(hardwareAxisNo) ||
               _homeDeadlines.ContainsKey(hardwareAxisNo) ||
               Axes?.FirstOrDefault(axis => axis.HardwareAxisNo == hardwareAxisNo)?.IsMoving == true;
    }

    private void SetConnectionText(string text)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.MotionConnectionText = text;
        }
    }

    private void RecordAlarmOnce(string key, string code, string message)
    {
        if (_activeAlarmKeys.Add(key))
        {
            RecordAlarm(code, message);
        }
    }

    private void RecordAlarm(string code, string message)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        viewModel.AlarmRecords.Insert(0, new AlarmInfo
        {
            Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            Code = code,
            Message = message,
            Level = "报警",
            Status = "未确认"
        });

        while (viewModel.AlarmRecords.Count > 200)
        {
            viewModel.AlarmRecords.RemoveAt(viewModel.AlarmRecords.Count - 1);
        }
    }

    private void RemoveAlarmKeys(string prefix)
    {
        _activeAlarmKeys.RemoveWhere(key => key.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static string FormatException(Exception exception)
    {
        return exception switch
        {
            MotionCardException motionException => motionException.Message,
            TimeoutException timeoutException => timeoutException.Message,
            InvalidDataException dataException => dataException.Message,
            ArgumentException argumentException => argumentException.Message,
            InvalidOperationException operationException => operationException.Message,
            _ => $"{exception.GetType().Name}: {exception.Message}"
        };
    }

    private static string FormatInitializationException(Exception exception)
    {
        var message = FormatException(exception);
        if (exception is not MotionCardException motionException)
        {
            return message;
        }

        var diagnostics = new List<string>();
        if (!string.IsNullOrWhiteSpace(motionException.Operation))
        {
            diagnostics.Add($"SDK 操作：{motionException.Operation}");
        }

        if (motionException.NativeErrorCode is { } nativeErrorCode)
        {
            diagnostics.Add($"返回码：{nativeErrorCode}");
        }

        if (motionException.BusErrorCode is { } busErrorCode)
        {
            diagnostics.Add($"总线错误码：0x{busErrorCode:X4}");
        }

        return diagnostics.Count == 0
            ? message
            : $"{message}（{string.Join("；", diagnostics)}）";
    }

    private void AxisDataGrid_Loaded(object sender, RoutedEventArgs e)
    {
        ApplyAxisNumberSort();
        ResizeAxisRows();
    }

    private void AxisDataGrid_LayoutChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeAxisRows();
    }

    private void AxisDataGrid_CellEditEnding(object sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction == DataGridEditAction.Cancel || e.Column.Header?.ToString() != "轴名称")
        {
            return;
        }

        Dispatcher.BeginInvoke(SaveAxisSettings, DispatcherPriority.Background);
    }

    private void AxisSettingsTextBox_LostFocus(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(SaveAxisSettings, DispatcherPriority.Background);
    }

    private void AxisDataGrid_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!AxisDataGrid.IsKeyboardFocusWithin)
        {
            ClearAxisSelection();
        }
    }

    private void AlarmDataGrid_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!AlarmDataGrid.IsKeyboardFocusWithin)
        {
            ClearAlarmSelection();
        }
    }

    private void MotionControlPage_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var clickedElement = e.OriginalSource as DependencyObject;
        if (!IsDataGridElement(clickedElement, AxisDataGrid))
        {
            ClearAxisSelection();
        }

        if (!IsDataGridElement(clickedElement, AlarmDataGrid))
        {
            ClearAlarmSelection();
        }
    }

    private void MotionControlPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    private void ClearAxisSelection()
    {
        AxisDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        AxisDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
        AxisDataGrid.SelectedItem = null;
        AxisDataGrid.CurrentCell = new DataGridCellInfo();
        SelectedAxis = null;
    }

    private void ClearAlarmSelection()
    {
        AlarmDataGrid.SelectedItem = null;
        AlarmDataGrid.CurrentCell = new DataGridCellInfo();
    }

    private static bool IsDataGridElement(DependencyObject? element, DataGrid dataGrid)
    {
        while (element is not null)
        {
            if (ReferenceEquals(element, dataGrid))
            {
                return true;
            }

            element = VisualTreeHelper.GetParent(element);
        }

        return false;
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindVisualChildren<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private void ResizeAxisRows()
    {
        if (Axes is not { Count: > 0 } axes || AxisDataGrid.ActualHeight <= 0)
        {
            return;
        }

        const double headerAndBorders = 38;
        var rowHeight = Math.Floor((AxisDataGrid.ActualHeight - headerAndBorders) / axes.Count);
        AxisDataGrid.RowHeight = Math.Max(34, rowHeight);
    }

    private void ApplyAxisNumberSort()
    {
        var view = CollectionViewSource.GetDefaultView(AxisDataGrid.ItemsSource);
        if (view is null)
        {
            return;
        }

        view.SortDescriptions.Clear();
        view.SortDescriptions.Add(new SortDescription(nameof(AxisStatus.AxisNo), ListSortDirection.Ascending));
    }

    private void CommitAndSaveAxisSettingsOrThrow()
    {
        AxisDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        AxisDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SaveAxisSettingsOrThrow();
    }

    private void SaveAxisSettings()
    {
        if (Axes is { Count: > 0 } axes)
        {
            try
            {
                SaveAxisSettingsOrThrow();
            }
            catch (Exception exception)
            {
                RecordAlarm("AXIS-SETTINGS-SAVE", $"轴名称/速度/距离保存失败：{exception.Message}");
            }
        }
    }

    private void SaveAxisSettingsOrThrow()
    {
        if (Axes is { Count: > 0 } axes)
        {
            _axisSettingsStore.Save(axes);
        }
    }
}

internal enum MotionWorkbenchMode
{
    FixedPosition,
    ContinuousJog,
    Home
}

internal enum CommandStage
{
    Ready,
    Issued,
    Running,
    Releasing,
    Stopped,
    Failed
}
