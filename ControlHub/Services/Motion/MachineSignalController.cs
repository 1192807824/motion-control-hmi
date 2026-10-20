namespace ControlHub.Services.Motion;

public enum MachineOperatingState
{
    Standby,
    Running,
    Paused,
    Resetting,
    Stopping
}

/// <summary>高电平有效：Y29红、Y30绿、Y31黄、Y00蜂鸣器。仅管理这四个输出。</summary>
public sealed class MachineSignalController
{
    public const int BuzzerBit = 0;
    public const int RedBit = 29;
    public const int GreenBit = 30;
    public const int YellowBit = 31;

    private readonly HashSet<string> _latchedAlarms = [];
    private HashSet<string> _activeFaults = [];
    private readonly Dictionary<int, bool> _writtenOutputs = [];
    private MachineOperatingState _operatingState;
    private bool _shutdown;

    public void SetOperatingState(MachineOperatingState state)
    {
        // 明确开始新流程/继续运行时清除上一轮的软件故障；实际设备故障仍优先显示红灯。
        if (state == MachineOperatingState.Running && _operatingState != state)
            ResetLatchedAlarms();
        _operatingState = state;
    }

    public void ReportAlarm(string code) => _latchedAlarms.Add("alarm:" + code);

    public void UpdateActiveFaults(IEnumerable<string> faults)
    {
        var next = faults.Select(fault => "active:" + fault).ToHashSet(StringComparer.Ordinal);
        _activeFaults = next;
    }

    public void SilenceBuzzer()
    {
        // 操作员也可能从数字输出页手动打开蜂鸣，消音时必须实际写0。
        _writtenOutputs.Remove(BuzzerBit);
    }

    public void ResetLatchedAlarms()
    {
        _latchedAlarms.Clear();
    }

    public void Shutdown(IMotionCard card)
    {
        _writtenOutputs.Clear();
        try
        {
            WriteOutputs(card, new Dictionary<int, bool>
            {
                [BuzzerBit] = false, [RedBit] = false, [GreenBit] = false, [YellowBit] = false
            });
            const uint signalMask = (1u << BuzzerBit) | (1u << RedBit) | (1u << GreenBit) | (1u << YellowBit);
            if (card.IsOpen && (card.ReadDigitalOutputs(0) & signalMask) != 0)
                throw new InvalidOperationException("退出前蜂鸣器/三色灯关闭回读不符。");
            _shutdown = true;
        }
        catch
        {
            // 退出被阻止后仍须继续显示真正的故障，不能提前永久屏蔽报警。
            _writtenOutputs.Clear();
            throw;
        }
    }

    public IReadOnlyDictionary<int, bool> DesiredOutputs
    {
        get
        {
            var fault = _latchedAlarms.Count > 0 || _activeFaults.Count > 0;
            return new Dictionary<int, bool>
            {
                // 现场暂时禁用蜂鸣器；任何运行、故障、急停状态都不打开Y00。
                [BuzzerBit] = false,
                [RedBit] = !_shutdown && fault,
                [GreenBit] = !_shutdown && !fault && _operatingState == MachineOperatingState.Running,
                [YellowBit] = !_shutdown && !fault && _operatingState != MachineOperatingState.Running
            };
        }
    }

    public void Apply(IMotionCard card)
    {
        WriteOutputs(card, DesiredOutputs);
    }

    private void WriteOutputs(IMotionCard card, IReadOnlyDictionary<int, bool> desired)
    {
        if (!card.IsOpen)
        {
            _writtenOutputs.Clear();
            return;
        }

        List<Exception> failures = [];
        // 先熄灭旧灯/消音，再点亮目标灯，避免正常切换时两种颜色重叠。
        foreach (var (bit, high) in desired.OrderBy(item => item.Value))
        {
            if (_writtenOutputs.TryGetValue(bit, out var previous) && previous == high) continue;
            try
            {
                if (bit >= card.DigitalOutputCount)
                    throw new InvalidOperationException($"控制卡未提供指示输出 Y{bit:00}。");
                card.WriteDigitalOutput(bit, high);
                _writtenOutputs[bit] = high;
            }
            catch (Exception exception)
            {
                _writtenOutputs.Remove(bit);
                failures.Add(new InvalidOperationException($"Y{bit:00}写入{(high ? 1 : 0)}失败。", exception));
            }
        }
        if (failures.Count > 0) throw new AggregateException("三色灯/蜂鸣器输出未全部写入成功。", failures);
    }
}
