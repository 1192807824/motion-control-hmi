using System.Windows.Threading;

namespace ControlHub.Views.Pages;

public partial class MotionControlPage
{
    // 模块1001的第一组输入X06：红/0为门全关，绿/1为开门。
    private const int DoorInputPort = 0;
    private const int DoorInputBit = 6;
    private readonly object _doorInterlockSync = new();
    private volatile bool _doorInterlockInputActive;
    private bool _doorInterlockTripLatched;

    public bool DoorInterlockEnabled => _motionOptions.DoorInterlockEnabled;

    public void SaveDoorInterlockEnabled(bool enabled)
    {
        lock (_doorInterlockSync)
        {
            var previous = _motionOptions.DoorInterlockEnabled;
            _motionOptions.DoorInterlockEnabled = enabled;
            try { _motionOptionsStore.Save(_motionOptions); }
            catch
            {
                _motionOptions.DoorInterlockEnabled = previous;
                throw;
            }

            if (!enabled)
            {
                _doorInterlockInputActive = false;
                _doorInterlockTripLatched = false;
                ClearDoorInterlockActiveAlarms();
            }
            // 启用时即检查当前电平，不依赖下一次开门沿。
            RefreshDoorInterlock();
        }
        StartExternalEmergencyStopMonitor();
        RefreshMachineSignals(retryImmediately: true);
    }

    private void RefreshDoorInterlock()
    {
        lock (_doorInterlockSync)
        {
            if (!DoorInterlockEnabled || _motionOptions.SimulationMode || _closed || !_motionCard.IsOpen)
                return;

            bool active;
            Exception? readError = null;
            try
            {
                var inputs = _motionCard.ReadDigitalInputs(DoorInputPort);
                active = (inputs & (1u << DoorInputBit)) != 0;
            }
            catch (Exception exception)
            {
                // 启用的门禁输入读不到时也停止，不能继续相信旧的关门状态。
                active = true;
                readError = exception;
            }

            var wasActive = _doorInterlockInputActive;
            _doorInterlockInputActive = active;
            if (active && !_doorInterlockTripLatched)
            {
                _doorInterlockTripLatched = true;
                IssueSafetyInputEmergencyStop(
                    readError is null ? "门禁触发（门已打开）" : "门禁输入读取失败",
                    readError is null ? "DOOR-OPEN" : "DOOR-INPUT-READ",
                    readError is null ? "模块1001 X06=1（绿色），门已打开" : "无法可靠读取模块1001 X06门禁输入",
                    readError);
            }
            else if (!active)
            {
                _doorInterlockTripLatched = false;
                if (wasActive)
                    _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
                    {
                        if (!_closed && !_doorInterlockInputActive)
                        {
                            ClearDoorInterlockActiveAlarms();
                            RefreshMachineSignals(retryImmediately: true);
                        }
                    }));
            }
        }
    }

    private void ClearDoorInterlockActiveAlarms()
    {
        _activeAlarmKeys.Remove("door-interlock:DOOR-OPEN");
        _activeAlarmKeys.Remove("door-interlock:DOOR-INPUT-READ");
    }

    private void ThrowIfDoorInterlockActive()
    {
        RefreshDoorInterlock();
        if (_doorInterlockInputActive)
            throw new InvalidOperationException("门禁已启用：门未关闭或门禁输入读取失败，禁止启动运动。请关好门并排除故障后手动重新启动。");
    }
}
