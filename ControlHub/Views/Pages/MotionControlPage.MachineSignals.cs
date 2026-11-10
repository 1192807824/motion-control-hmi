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
    private int _machineSignalRevision;

    public void SetMachineOperatingState(MachineOperatingState state)
    {
        if (state == MachineOperatingState.Running) Interlocked.Increment(ref _machineSignalRevision);
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
        // 急停可能改变板卡输出状态，必须重新下发Y00=1，不能只相信旧缓存。
        if (_alarmResetInProgress || code == "EMERGENCY-STOP") _machineSignals.SilenceBuzzer();
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

    private void RefreshMachineSignals(bool retryImmediately = false)
    {
        if (_closed) return;
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
                RecordAlarm("MACHINE-SIGNAL-IO", FormatException(exception));
            }
        }
    }

    private void SilenceMachineBuzzer()
    {
        _machineSignals.SilenceBuzzer();
        RefreshMachineSignals(retryImmediately: true);
    }

    private void ShutdownMachineSignalsNoThrow()
    {
        try
        {
            _machineSignals.Shutdown(_motionCard);
        }
        catch (Exception exception)
        {
            RecordAlarm("SHUTDOWN-SIGNAL-IO",
                $"退出时关闭蜂鸣器/三色灯的写入或回读异常，运动轴已确认停止，继续退出：{FormatException(exception)}");
        }
    }
}
