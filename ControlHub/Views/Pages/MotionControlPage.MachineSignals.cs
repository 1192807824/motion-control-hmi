using System.Windows.Threading;
using ControlHub.Services.Motion;

namespace ControlHub.Views.Pages;

public partial class MotionControlPage
{
    private readonly MachineSignalController _machineSignals = new();
    private DateTime _signalRetryAfterUtc;
    private bool _signalWriteFailureReported;
    private bool _alarmResetInProgress;
    private bool _signalRefreshQueued;

    public void SetMachineOperatingState(MachineOperatingState state)
    {
        _machineSignals.SetOperatingState(state);
        QueueMachineSignalRefresh();
    }

    public void ReportMachineSignalAlarm(string code)
    {
        if (!Dispatcher.CheckAccess())
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(() => ReportMachineSignalAlarm(code)));
            return;
        }
        if (_closed) return;
        _machineSignals.ReportAlarm(code);
        if (_alarmResetInProgress) _machineSignals.SilenceBuzzer();
        QueueMachineSignalRefresh();
    }

    private void QueueMachineSignalRefresh()
    {
        // 记录故障时可能尚未执行停止命令，指示IO稍后刷新，不能抢占安全停止。
        // 主页的停止状态也可能来自急停前通知，同样不能在通知里同步写灯。
        if (_signalRefreshQueued) return;
        _signalRefreshQueued = true;
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _signalRefreshQueued = false;
            RefreshMachineSignals(retryImmediately: true);
        }));
    }

    private IEnumerable<string> GetActiveMachineSignalFaults()
    {
        if (_motionSafetyLock) yield return "motion-safety-lock";
        if (_externalEmergencyStopInputActive) yield return "external-emergency-stop";
        foreach (var key in _activeAlarmKeys)
        {
            if (key.StartsWith("axis-fault:", StringComparison.Ordinal) ||
                key.StartsWith("axis-emg:", StringComparison.Ordinal) ||
                key.StartsWith("axis-stop:", StringComparison.Ordinal) ||
                key.StartsWith("bus:", StringComparison.Ordinal) ||
                key.StartsWith("poll:", StringComparison.Ordinal) ||
                key is "poll-general" or "io-read" or "external-emergency-stop-read")
                yield return key;
        }
    }

    private void RefreshMachineSignals(bool retryImmediately = false)
    {
        if (_closed) return;
        _machineSignals.UpdateActiveFaults(GetActiveMachineSignalFaults());
        if (!retryImmediately && DateTime.UtcNow < _signalRetryAfterUtc) return;
        try
        {
            // 指示输出独立于工艺阀：急停/安全锁下仍允许红灯报警及蜂鸣消音。
            _machineSignals.Apply(_motionCard);
            _signalRetryAfterUtc = default;
            _signalWriteFailureReported = false;
        }
        catch (Exception exception)
        {
            _signalRetryAfterUtc = DateTime.UtcNow.AddSeconds(1);
            if (!_signalWriteFailureReported)
            {
                _signalWriteFailureReported = true;
                RecordAlarm("MACHINE-SIGNAL-IO", FormatException(exception), signal: false);
            }
        }
    }

    private void SilenceMachineBuzzer()
    {
        _machineSignals.UpdateActiveFaults(GetActiveMachineSignalFaults());
        _machineSignals.SilenceBuzzer();
        RefreshMachineSignals(retryImmediately: true);
    }

    private bool TryShutdownMachineSignals(out string failureMessage)
    {
        failureMessage = "";
        try
        {
            _machineSignals.Shutdown(_motionCard);
            return true;
        }
        catch (Exception exception)
        {
            failureMessage = $"退出前未能确认蜂鸣器和三色灯关闭，控制卡保持连接，请检查IO后重试。\n\n{FormatException(exception)}";
            RecordAlarm("SHUTDOWN-SIGNAL-FAILED", failureMessage);
            return false;
        }
    }
}
