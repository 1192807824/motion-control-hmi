namespace ProductionFlowDemo;

public enum TestStationKind
{
    T1,
    T2,
    T3,
}

public enum TestState
{
    Pending,
    Passed,
    Failed,
    Skipped,
}

public enum BinCode
{
    Unassigned,
    Ok,
    T1Ng,
    T2Ng,
    T3Ng,
}

public sealed class PartState
{
    public PartState(string partId)
    {
        PartId = partId;
    }

    public string PartId { get; }

    public TestState T1 { get; private set; } = TestState.Pending;

    public TestState T2 { get; private set; } = TestState.Pending;

    public TestState T3 { get; private set; } = TestState.Pending;

    public BinCode FinalBin { get; private set; } = BinCode.Unassigned;

    public bool CanRun(TestStationKind station)
    {
        return station switch
        {
            TestStationKind.T1 => T1 == TestState.Pending,
            TestStationKind.T2 => T1 == TestState.Passed && T2 == TestState.Pending,
            TestStationKind.T3 =>
                T1 == TestState.Passed &&
                T2 == TestState.Passed &&
                T3 == TestState.Pending,
            _ => false,
        };
    }

    public bool IsSkipped(TestStationKind station)
    {
        return station switch
        {
            TestStationKind.T1 => T1 == TestState.Skipped,
            TestStationKind.T2 => T2 == TestState.Skipped,
            TestStationKind.T3 => T3 == TestState.Skipped,
            _ => false,
        };
    }

    public void ApplyResult(TestStationKind station, bool passed)
    {
        if (!CanRun(station))
        {
            throw new InvalidOperationException(
                $"产品 {PartId} 当前状态不允许执行 {station}。"
            );
        }

        switch (station)
        {
            case TestStationKind.T1:
                T1 = passed ? TestState.Passed : TestState.Failed;
                if (!passed)
                {
                    T2 = TestState.Skipped;
                    T3 = TestState.Skipped;
                    FinalBin = BinCode.T1Ng;
                }

                break;

            case TestStationKind.T2:
                T2 = passed ? TestState.Passed : TestState.Failed;
                if (!passed)
                {
                    T3 = TestState.Skipped;
                    FinalBin = BinCode.T2Ng;
                }

                break;

            case TestStationKind.T3:
                T3 = passed ? TestState.Passed : TestState.Failed;
                FinalBin = passed ? BinCode.Ok : BinCode.T3Ng;
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(station), station, null);
        }
    }
}

public sealed class RotaryFlowState
{
    public const int SlotCount = 16;
    public const int LoadSlotA = 0;
    public const int LoadSlotB = 1;
    public const int T1Slot = 2;
    public const int T2Slot = 3;
    public const int T3Slot = 4;
    public const int UnloadSlotA = 6;
    public const int UnloadSlotB = 7;
    public const int TransferPitch = 2;

    private PartState?[] _slots = new PartState?[SlotCount];

    public int BeatNumber { get; private set; }

    public bool IsTransferBeat => BeatNumber % TransferPitch == 0;

    public PartState? GetAt(int slot)
    {
        return _slots[slot];
    }

    public void AdvanceOneSlot()
    {
        var next = new PartState?[SlotCount];
        for (var slot = 0; slot < SlotCount; slot++)
        {
            var part = _slots[slot];
            if (part is null)
            {
                continue;
            }

            var target = (slot + 1) % SlotCount;
            if (next[target] is not null)
            {
                throw new InvalidOperationException($"转盘位置 {target} 出现产品碰撞。");
            }

            next[target] = part;
        }

        _slots = next;
        BeatNumber++;
    }

    public void PlaceLoadedPair(LoadedPair pair)
    {
        if (_slots[LoadSlotA] is not null || _slots[LoadSlotB] is not null)
        {
            throw new InvalidOperationException("位置 0、1 尚未清空，禁止第一套 XY 上料。");
        }

        _slots[LoadSlotA] = pair.First;
        _slots[LoadSlotB] = pair.Second;
    }

    public IReadOnlyList<PartState> GetUnloadPair()
    {
        return new[] {_slots[UnloadSlotA], _slots[UnloadSlotB]}
            .Where(static part => part is not null)
            .Cast<PartState>()
            .ToArray();
    }

    public void RemoveUnloadPair()
    {
        _slots[UnloadSlotA] = null;
        _slots[UnloadSlotB] = null;
    }

    public string DescribeOccupiedSlots()
    {
        var occupied = _slots
            .Select((part, slot) => part is null ? null : $"{slot}:{part.PartId}")
            .Where(static value => value is not null);
        return string.Join(", ", occupied!);
    }
}

public sealed record LoadedPair(PartState First, PartState Second);

public interface IIndexTable
{
    Task IndexOnePitchAsync(CancellationToken cancellationToken);
}

public interface IPressAxes
{
    Task DownTogetherAsync(IReadOnlyList<int> hardwareAxes, CancellationToken cancellationToken);

    Task UpTogetherAsync(IReadOnlyList<int> hardwareAxes, CancellationToken cancellationToken);
}

public interface ITestStation
{
    TestStationKind Kind { get; }

    Task<bool> StartAndWaitResultAsync(PartState part, CancellationToken cancellationToken);
}

public interface ILoader
{
    Task<LoadedPair> LoadTwoAsync(CancellationToken cancellationToken);
}

public interface IUnloader
{
    Task UnloadByBinAsync(IReadOnlyList<PartState> parts, CancellationToken cancellationToken);
}

public interface IProductionInterlocks
{
    Task WaitIndexSafeAsync(CancellationToken cancellationToken);

    Task WaitTableInPositionAsync(CancellationToken cancellationToken);

    Task WaitTransferSafeAsync(CancellationToken cancellationToken);
}

public interface IFlowPersistence
{
    Task SaveIndexAsync(RotaryFlowState state, CancellationToken cancellationToken);

    Task SavePartAsync(PartState part, CancellationToken cancellationToken);

    Task WriteEventAsync(string message, CancellationToken cancellationToken);
}

public sealed class ProductionOrchestrator
{
    private static readonly int[] PressAxisNumbers = [13, 14, 15];

    private readonly IIndexTable _table;
    private readonly IPressAxes _pressAxes;
    private readonly IReadOnlyDictionary<TestStationKind, ITestStation> _testStations;
    private readonly ILoader _loader;
    private readonly IUnloader _unloader;
    private readonly IProductionInterlocks _interlocks;
    private readonly IFlowPersistence _persistence;

    public ProductionOrchestrator(
        RotaryFlowState state,
        IIndexTable table,
        IPressAxes pressAxes,
        IEnumerable<ITestStation> testStations,
        ILoader loader,
        IUnloader unloader,
        IProductionInterlocks interlocks,
        IFlowPersistence persistence)
    {
        State = state;
        _table = table;
        _pressAxes = pressAxes;
        _testStations = testStations.ToDictionary(static station => station.Kind);
        _loader = loader;
        _unloader = unloader;
        _interlocks = interlocks;
        _persistence = persistence;

        foreach (var kind in Enum.GetValues<TestStationKind>())
        {
            if (!_testStations.ContainsKey(kind))
            {
                throw new ArgumentException($"缺少测试站 {kind}。", nameof(testStations));
            }
        }
    }

    public RotaryFlowState State { get; }

    public async Task PrimeAsync(CancellationToken cancellationToken)
    {
        await _interlocks.WaitTransferSafeAsync(cancellationToken);
        var pair = await _loader.LoadTwoAsync(cancellationToken);
        State.PlaceLoadedPair(pair);
        await _persistence.SaveIndexAsync(State, cancellationToken);
    }

    public async Task RunOneBeatAsync(CancellationToken cancellationToken)
    {
        await _interlocks.WaitIndexSafeAsync(cancellationToken);
        await _table.IndexOnePitchAsync(cancellationToken);
        State.AdvanceOneSlot();
        await _interlocks.WaitTableInPositionAsync(cancellationToken);
        await _persistence.SaveIndexAsync(State, cancellationToken);

        var axesAreDown = false;
        try
        {
            await _pressAxes.DownTogetherAsync(PressAxisNumbers, cancellationToken);
            axesAreDown = true;

            await Task.WhenAll(
                RunStationAsync(TestStationKind.T1, RotaryFlowState.T1Slot, cancellationToken),
                RunStationAsync(TestStationKind.T2, RotaryFlowState.T2Slot, cancellationToken),
                RunStationAsync(TestStationKind.T3, RotaryFlowState.T3Slot, cancellationToken)
            );
        }
        finally
        {
            if (axesAreDown)
            {
                // 实机应使用独立的安全回升策略，并同时受急停硬回路约束。
                await _pressAxes.UpTogetherAsync(PressAxisNumbers, CancellationToken.None);
            }
        }

        if (State.IsTransferBeat)
        {
            await RunLoadThenUnloadAsync(cancellationToken);
        }
    }

    private async Task RunStationAsync(
        TestStationKind stationKind,
        int rotarySlot,
        CancellationToken cancellationToken)
    {
        var part = State.GetAt(rotarySlot);
        if (part is null)
        {
            await _persistence.WriteEventAsync($"{stationKind}：空工位。", cancellationToken);
            return;
        }

        if (part.IsSkipped(stationKind))
        {
            await _persistence.WriteEventAsync(
                $"{stationKind}：{part.PartId} 已有最终 BIN {part.FinalBin}，跳过 START。",
                cancellationToken
            );
            return;
        }

        if (!part.CanRun(stationKind))
        {
            throw new InvalidOperationException(
                $"{stationKind} 上的产品 {part.PartId} 前序状态不完整，停止自动循环。"
            );
        }

        var passed = await _testStations[stationKind]
            .StartAndWaitResultAsync(part, cancellationToken);
        part.ApplyResult(stationKind, passed);
        await _persistence.SavePartAsync(part, cancellationToken);
    }

    private async Task RunLoadThenUnloadAsync(CancellationToken cancellationToken)
    {
        await _interlocks.WaitTransferSafeAsync(cancellationToken);

        // 按当前要求先执行第一套 XY 上料，再执行第二套 XY 分 BIN 下料。
        var loadedPair = await _loader.LoadTwoAsync(cancellationToken);
        State.PlaceLoadedPair(loadedPair);
        await _persistence.SaveIndexAsync(State, cancellationToken);

        var unloadPair = State.GetUnloadPair();
        if (unloadPair.Count == 0)
        {
            return;
        }

        if (unloadPair.Any(static part => part.FinalBin == BinCode.Unassigned))
        {
            throw new InvalidOperationException("下料位存在尚未确定 BIN 的产品，禁止下料。");
        }

        await _unloader.UnloadByBinAsync(unloadPair, cancellationToken);
        State.RemoveUnloadPair();
        await _persistence.SaveIndexAsync(State, cancellationToken);
    }
}
