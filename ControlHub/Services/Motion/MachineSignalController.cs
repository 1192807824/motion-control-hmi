namespace ControlHub.Services.Motion;

public enum MachineOperatingState
{
    Standby,
    Running,
    Paused,
    Resetting,
    Stopping
}

/// <summary>Y29红/Y30绿/Y31黄为低电平亮、高电平关闭；Y00蜂鸣器同为低电平有效。</summary>
public sealed class MachineSignalController
{
    public const int BuzzerBit = 0;
    public const bool BuzzerOffLevel = true;
    public const int RedBit = 29;
    public const int GreenBit = 30;
    public const int YellowBit = 31;

    private readonly HashSet<string> _latchedAlarms = [];
    private HashSet<string> _activeFaults = [];
    private readonly Dictionary<int, bool> _writtenOutputs = [];
    private MachineOperatingState _operatingState;
    private bool _shutdown;

    // 蜂鸣器按现场要求保持禁用，所有入口统一使用实际的关闭电平。
    public static bool NormalizeOutputLevel(int bit, bool requestedLevel) =>
        bit == BuzzerBit ? BuzzerOffLevel : requestedLevel;

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
        // 蜂鸣器低电平有效，消音时必须实际写1。
        _writtenOutputs.Remove(BuzzerBit);
    }

    public void ResetLatchedAlarms()
    {
        _latchedAlarms.Clear();
    }

    public void Shutdown(IMotionCard card)
    {
        // 退出阶段的指示IO异常只作诊断，不能恢复灯光/蜂鸣状态或阻止退出。
        _shutdown = true;
        _writtenOutputs.Clear();
        WriteOutputs(card, new Dictionary<int, bool>
        {
            [BuzzerBit] = BuzzerOffLevel, [RedBit] = true, [GreenBit] = true, [YellowBit] = true
        });
        const uint signalMask = (1u << BuzzerBit) | (1u << RedBit) | (1u << GreenBit) | (1u << YellowBit);
        const uint shutdownLevels = signalMask;
        if (card.IsOpen && (card.ReadDigitalOutputs(0) & signalMask) != shutdownLevels)
        {
            _writtenOutputs.Clear();
            throw new InvalidOperationException("退出前蜂鸣器/三色灯关闭回读不符。");
        }
    }

    public IReadOnlyDictionary<int, bool> DesiredOutputs
    {
        get
        {
            var fault = _latchedAlarms.Count > 0 || _activeFaults.Count > 0;
            return new Dictionary<int, bool>
            {
                // 现场暂时禁用蜂鸣器，始终保持Y00=1（关闭）。
                [BuzzerBit] = BuzzerOffLevel,
                // 三色灯低电平有效，字典值为实际输出电平（false亮、true灭）。
                [RedBit] = _shutdown || !fault,
                [GreenBit] = _shutdown || fault || _operatingState != MachineOperatingState.Running,
                [YellowBit] = _shutdown || fault || _operatingState == MachineOperatingState.Running
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
        // 先关闭蜂鸣器，再熄灭旧灯，最后点亮目标灯。
        foreach (var (bit, high) in desired.OrderBy(item => item.Key == BuzzerBit ? 0 : item.Value ? 1 : 2))
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
