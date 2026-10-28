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

    private bool _stoppedIndication;
    private readonly Dictionary<int, bool> _writtenOutputs = [];
    private MachineOperatingState _operatingState;
    private bool _shutdown;
    private DateTime _verifyOutputsAfterUtc;

    // 蜂鸣器按现场要求保持禁用，所有入口统一使用实际的关闭电平。
    public static bool NormalizeOutputLevel(int bit, bool requestedLevel) =>
        bit == BuzzerBit ? BuzzerOffLevel : requestedLevel;

    public void SetOperatingState(MachineOperatingState state)
    {
        // 灯只表达运行状态，不把报警列表/历史停止原因当作持续红灯条件。
        var wasStopped = _stoppedIndication;
        if (state == MachineOperatingState.Running)
            _stoppedIndication = false;
        else if ((state is MachineOperatingState.Stopping or MachineOperatingState.Standby) &&
                 (_operatingState is MachineOperatingState.Running or MachineOperatingState.Paused))
            _stoppedIndication = true;
        if (_operatingState != state || wasStopped != _stoppedIndication) _verifyOutputsAfterUtc = default;
        _operatingState = state;
    }

    public void ReportAlarm(string code)
    {
        // 仅由实际急停入口调用；普通报警记录不驱动灯。
        _stoppedIndication = true;
        _verifyOutputsAfterUtc = default;
    }

    public void SilenceBuzzer()
    {
        // 蜂鸣器低电平有效，消音时必须实际写1。
        _writtenOutputs.Remove(BuzzerBit);
    }

    public void ResetLatchedAlarms()
    {
        _stoppedIndication = false;
        _operatingState = MachineOperatingState.Standby;
        _verifyOutputsAfterUtc = default;
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
            var stopped = _stoppedIndication;
            return new Dictionary<int, bool>
            {
                // 现场暂时禁用蜂鸣器，始终保持Y00=1（关闭）。
                [BuzzerBit] = BuzzerOffLevel,
                // 三色灯低电平有效，字典值为实际输出电平（false亮、true灭）。
                [RedBit] = _shutdown || !stopped,
                [GreenBit] = _shutdown || stopped || _operatingState != MachineOperatingState.Running,
                [YellowBit] = _shutdown || stopped || _operatingState == MachineOperatingState.Running
            };
        }
    }

    public void Apply(IMotionCard card)
    {
        if (card.IsOpen && DateTime.UtcNow >= _verifyOutputsAfterUtc)
        {
            // 急停或手动IO可能改写输出，不能一直相信之前的写入缓存。
            var actual = card.ReadDigitalOutputs(0);
            foreach (var bit in new[] { BuzzerBit, RedBit, GreenBit, YellowBit })
                _writtenOutputs[bit] = (actual & (1u << bit)) != 0;
            _verifyOutputsAfterUtc = DateTime.UtcNow.AddMilliseconds(250);
        }
        if (!card.IsOpen) _verifyOutputsAfterUtc = default;
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
