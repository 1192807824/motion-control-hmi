using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private readonly IMotionCard _motionCard;
    private readonly MotionCardOptions _motionOptions;
    private readonly AxisSettingsStore _axisSettingsStore = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly HashSet<string> _activeAlarmKeys = [];
    private readonly Dictionary<int, DateTime> _homeDeadlines = [];
    private readonly string? _configurationError;
    private CancellationTokenSource? _homeSequenceCancellation;
    private IoPointKind _ioMode = IoPointKind.DigitalInput;
    private bool _initialized;
    private bool _polling;
    private bool _closed;

    public MotionControlPage()
    {
        InitializeComponent();

        try
        {
            _motionOptions = new MotionCardOptionsStore().Load();
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            _motionOptions = new MotionCardOptions();
            _configurationError = $"运动配置读取失败：{exception.Message}";
        }

        _motionCard = MotionCardFactory.Create(_motionOptions);
        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(_motionOptions.PollIntervalMilliseconds)
        };
        _pollTimer.Tick += (_, _) => PollMotionState();
        Loaded += MotionControlPage_Loaded;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

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

    public void Shutdown()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _pollTimer.Stop();
        _homeSequenceCancellation?.Cancel();
        CommitAndSaveAxisSettings();

        if (_motionCard.IsOpen)
        {
            try
            {
                _motionCard.EmergencyStop();
            }
            catch
            {
                // Closing must continue even if the card is already unavailable.
            }
        }

        try
        {
            _motionCard.Close();
        }
        catch
        {
            // The process is closing; native resources cannot be recovered here.
        }
    }

    private void MotionControlPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_initialized || _closed)
        {
            return;
        }

        _initialized = true;
        if (_configurationError is not null)
        {
            ViewModel?.ClearMotionData();
            SetConnectionText("运动控制：配置错误");
            return;
        }

        try
        {
            var connection = _motionCard.Open();
            SetConnectionText(connection.IsSimulation
                ? $"运动控制：仿真模式（{connection.AxisCount} 轴）"
                : $"运动控制：卡 {connection.CardNo} 已连接（检测 {connection.AxisCount} 轴）");
            var visibleAxisCount = Math.Min(connection.AxisCount, _motionOptions.AxisCount);
            ViewModel?.PopulateMotionAxes(visibleAxisCount);
            UpdateAxisAvailability(visibleAxisCount);
            ConfigureIoPoints(IoPointKind.DigitalInput);
            PollMotionState();
            _pollTimer.Start();
        }
        catch (Exception)
        {
            ViewModel?.ClearMotionData();
            SetConnectionText("运动控制：未连接");
        }
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

        ExecuteMotion(
            axis,
            "HOME",
            () =>
            {
                EnsureServoEnabled(axis);
                _motionCard.Home(axis.HardwareAxisNo);
                axis.Homed = false;
                axis.State = "回零命令已发送";
                _homeDeadlines[axis.HardwareAxisNo] = DateTime.UtcNow.AddSeconds(_motionOptions.HomeTimeoutSeconds);
            });
    }

    private async void HomeAll_Click(object sender, RoutedEventArgs e)
    {
        if (_homeSequenceCancellation is not null)
        {
            return;
        }

        if (!EnsureConnected())
        {
            return;
        }

        _homeSequenceCancellation = new CancellationTokenSource();
        HomeAllButton.SetCurrentValue(IsEnabledProperty, false);
        try
        {
            foreach (var hardwareAxisNo in _motionOptions.GetHomeSequence())
            {
                _homeSequenceCancellation.Token.ThrowIfCancellationRequested();
                var axis = Axes?.FirstOrDefault(item => item.HardwareAxisNo == hardwareAxisNo);
                if (axis is null || !axis.IsAvailable)
                {
                    throw new MotionCardException($"回零序列中的硬件轴 {hardwareAxisNo} 不可用。");
                }

                if (!axis.ServoOn)
                {
                    _motionCard.ServoOn(hardwareAxisNo, true);
                }

                _motionCard.Home(hardwareAxisNo);
                axis.Homed = false;
                axis.State = "顺序回零中";
                await WaitForHomeAsync(axis, _homeSequenceCancellation.Token);
            }
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
            }
            catch
            {
                // Preserve the homing error that caused the safety stop.
            }

            RecordAlarm("HOME-ALL", FormatException(exception));
        }
        finally
        {
            _homeSequenceCancellation.Dispose();
            _homeSequenceCancellation = null;
            HomeAllButton.SetCurrentValue(IsEnabledProperty, ViewModel?.MotionControlsEnabled == true);
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
        _homeSequenceCancellation?.Cancel();
        _homeDeadlines.Clear();
        if (!ExecuteMotion(null, "EMERGENCY-STOP", _motionCard.EmergencyStop))
        {
            return;
        }

        foreach (var axis in Axes ?? [])
        {
            axis.State = "已发送全轴急停";
        }

        PollMotionState();
    }

    private void ClearAlarm_Click(object sender, RoutedEventArgs e)
    {
        if (!ExecuteMotion(
                null,
                "CLEAR-ALARM",
                () => _motionCard.ClearAlarms(
                    (Axes ?? []).Where(axis => axis.IsAvailable).Select(axis => axis.HardwareAxisNo))))
        {
            return;
        }

        ViewModel?.AlarmRecords.Clear();
        _activeAlarmKeys.Clear();
        _homeDeadlines.Clear();
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

    private async Task WaitForHomeAsync(AxisStatus axis, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow.AddSeconds(_motionOptions.HomeTimeoutSeconds);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = _motionCard.ReadAxis(axis.HardwareAxisNo);
            ApplySnapshot(axis, snapshot);
            if (snapshot.Alarm)
            {
                throw new MotionCardException(
                    $"{axis.Name}（硬件轴 {axis.HardwareAxisNo}）回零时发生报警 0x{snapshot.AxisErrorCode:X4}。");
            }

            if (!snapshot.IsMoving && snapshot.Homed)
            {
                return;
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
            var busError = _motionCard.ReadBusErrorCode();
            if (busError != 0)
            {
                SetConnectionText($"运动控制：EtherCAT 总线错误 0x{busError:X4}");
                RecordAlarmOnce(
                    $"bus:{busError}",
                    $"BUS-{busError:X4}",
                    $"EtherCAT 总线错误 0x{busError:X4}。请检查 ENI 配置、从站状态和网线连接。");
                foreach (var axis in Axes ?? [])
                {
                    axis.State = $"总线错误 0x{busError:X4}";
                    axis.Alarm = true;
                }

                return;
            }

            RemoveAlarmKeys("bus:");
            SetConnectionText(_motionOptions.SimulationMode
                ? $"运动控制：仿真模式（{_motionCard.AxisCount} 轴）"
                : $"运动控制：EtherCAT 正常（卡轴数 {_motionCard.AxisCount}）");

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
                    RecordAlarmOnce(
                        $"poll:{axis.HardwareAxisNo}",
                        $"AXIS-{axis.AxisNo:00}-READ",
                        FormatException(exception));
                }
            }

            PollIoState();
        }
        catch (Exception exception)
        {
            SetConnectionText("运动控制：通讯异常");
            RecordAlarmOnce("poll-general", "MOTION-POLL", FormatException(exception));
        }
        finally
        {
            _polling = false;
        }
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

        count = Math.Min(count, 20);
        viewModel.IoPoints.Clear();
        for (var channel = 0; channel < count; channel++)
        {
            viewModel.IoPoints.Add(new IoPoint
            {
                Channel = channel,
                Kind = mode,
                Name = mode switch
                {
                    IoPointKind.DigitalInput => $"X{channel:00}",
                    IoPointKind.DigitalOutput => $"Y{channel:00}",
                    IoPointKind.AnalogInput => $"AI{channel}",
                    IoPointKind.AnalogOutput => $"AO{channel}",
                    _ => channel.ToString()
                }
            });
        }

        SetIoModeButtonState(DigitalInputTab, mode == IoPointKind.DigitalInput);
        SetIoModeButtonState(DigitalOutputTab, mode == IoPointKind.DigitalOutput);
        SetIoModeButtonState(AnalogInputTab, mode == IoPointKind.AnalogInput);
        SetIoModeButtonState(AnalogOutputTab, mode == IoPointKind.AnalogOutput);
    }

    private static void SetIoModeButtonState(Button button, bool selected)
    {
        button.Background = selected ? new SolidColorBrush(Color.FromRgb(0x1E, 0x5E, 0xA8)) : new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF6));
        button.Foreground = selected ? Brushes.White : Brushes.Black;
        button.FontWeight = FontWeights.Bold;
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
        axis.State = snapshot.StateText;

        if (snapshot.Homed)
        {
            _homeDeadlines.Remove(axis.HardwareAxisNo);
        }
        else if (_homeDeadlines.TryGetValue(axis.HardwareAxisNo, out var deadline) && DateTime.UtcNow >= deadline)
        {
            _homeDeadlines.Remove(axis.HardwareAxisNo);
            try
            {
                _motionCard.Stop(axis.HardwareAxisNo, emergency: true);
            }
            catch (Exception exception)
            {
                RecordAlarm($"AXIS-{axis.AxisNo:00}-HOME-STOP", FormatException(exception));
            }

            RecordAlarmOnce(
                $"home-timeout:{axis.HardwareAxisNo}",
                $"AXIS-{axis.AxisNo:00}-HOME-TIMEOUT",
                $"{axis.Name}（硬件轴 {axis.HardwareAxisNo}）在 {_motionOptions.HomeTimeoutSeconds} 秒内未完成回零，已发送立即停止。");
        }
    }

    private void ProcessSnapshotAlarms(AxisStatus axis, MotionAxisSnapshot snapshot)
    {
        var faultKey = $"axis-fault:{axis.HardwareAxisNo}";
        if (snapshot.Alarm)
        {
            RecordAlarmOnce(
                faultKey,
                $"AXIS-{axis.AxisNo:00}-{snapshot.AxisErrorCode:X4}",
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
                $"AXIS-{axis.AxisNo:00}-EMG",
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
                $"AXIS-{axis.AxisNo:00}-STOP-{snapshot.StopReason}",
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
        if (!ExecuteMotion(null, enabled ? "SERVO-ALL-ON" : "SERVO-ALL-OFF", () => _motionCard.SetAllServos(enabled)))
        {
            return;
        }

        foreach (var axis in Axes ?? [])
        {
            axis.State = enabled ? "全轴使能命令已完成" : "全轴已停止并解除使能";
        }

        PollMotionState();
    }

    private void SetServo(AxisStatus? axis, bool enabled)
    {
        if (axis is null || axis.ServoOn == enabled)
        {
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

    private void StopAxis(AxisStatus axis)
    {
        _homeDeadlines.Remove(axis.HardwareAxisNo);
        if (ExecuteMotion(axis, "STOP", () => _motionCard.Stop(axis.HardwareAxisNo)))
        {
            axis.State = "已发送减速停止";
            PollMotionState();
        }
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
                axis is null ? code : $"AXIS-{axis.AxisNo:00}-{code}",
                FormatException(exception));
            return false;
        }
    }

    private bool EnsureConnected()
    {
        if (_motionCard.IsOpen)
        {
            return true;
        }

        return false;
    }

    private static void EnsureServoEnabled(AxisStatus axis)
    {
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
            axis.IsAvailable = axis.HardwareAxisNo >= 0 && axis.HardwareAxisNo < detectedAxisCount;
            axis.State = axis.IsAvailable ? "等待首次状态读取" : "控制卡未配置该轴";
        }
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

    private void CommitAndSaveAxisSettings()
    {
        AxisDataGrid.CommitEdit(DataGridEditingUnit.Cell, true);
        AxisDataGrid.CommitEdit(DataGridEditingUnit.Row, true);
        SaveAxisSettings();
    }

    private void SaveAxisSettings()
    {
        if (Axes is { Count: > 0 } axes)
        {
            _axisSettingsStore.Save(axes);
        }
    }
}
