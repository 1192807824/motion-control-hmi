namespace ProductionFlowDemo;

public sealed class DemoIndexTable : IIndexTable
{
    public async Task IndexOnePitchAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("轴0：转一格并等待到位");
        await Task.Delay(20, cancellationToken);
    }
}

public sealed class DemoPressAxes : IPressAxes
{
    public async Task DownTogetherAsync(
        IReadOnlyList<int> hardwareAxes,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"轴 {string.Join('/', hardwareAxes)}：同步下压");
        await Task.Delay(15, cancellationToken);
    }

    public async Task UpTogetherAsync(
        IReadOnlyList<int> hardwareAxes,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"轴 {string.Join('/', hardwareAxes)}：同步回升");
        await Task.Delay(15, cancellationToken);
    }
}

public sealed class DemoTestStation(TestStationKind kind) : ITestStation
{
    public TestStationKind Kind { get; } = kind;

    public async Task<bool> StartAndWaitResultAsync(
        PartState part,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"{Kind}：向仪表发送 START，产品 {part.PartId}");
        await Task.Delay(35, cancellationToken);

        return (Kind, part.PartId) switch
        {
            (TestStationKind.T1, "P002") => false,
            (TestStationKind.T2, "P003") => false,
            (TestStationKind.T3, "P004") => false,
            _ => true,
        };
    }
}

public sealed class DemoLoader : ILoader
{
    private int _nextPartNumber = 1;

    public async Task<LoadedPair> LoadTwoAsync(CancellationToken cancellationToken)
    {
        var first = new PartState($"P{_nextPartNumber:000}");
        var second = new PartState($"P{_nextPartNumber + 1:000}");
        _nextPartNumber += 2;

        Console.WriteLine($"第一套XY：上料 {first.PartId} / {second.PartId} 到位置0 / 1");
        await Task.Delay(30, cancellationToken);
        return new LoadedPair(first, second);
    }
}

public sealed class DemoUnloader : IUnloader
{
    public async Task UnloadByBinAsync(
        IReadOnlyList<PartState> parts,
        CancellationToken cancellationToken)
    {
        foreach (var part in parts)
        {
            Console.WriteLine($"第二套XY：{part.PartId} -> {part.FinalBin}");
        }

        await Task.Delay(30, cancellationToken);
    }
}

public sealed class DemoInterlocks : IProductionInterlocks
{
    public Task WaitIndexSafeAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("互锁：三根测试轴在上位，两个XY已退出");
        return Task.CompletedTask;
    }

    public Task WaitTableInPositionAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public Task WaitTransferSafeAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}

public sealed class DemoPersistence : IFlowPersistence
{
    public Task SaveIndexAsync(
        RotaryFlowState state,
        CancellationToken cancellationToken)
    {
        Console.WriteLine($"状态：第 {state.BeatNumber} 拍 [{state.DescribeOccupiedSlots()}]");
        return Task.CompletedTask;
    }

    public Task SavePartAsync(PartState part, CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"结果：{part.PartId} T1={part.T1}, T2={part.T2}, T3={part.T3}, BIN={part.FinalBin}"
        );
        return Task.CompletedTask;
    }

    public Task WriteEventAsync(string message, CancellationToken cancellationToken)
    {
        Console.WriteLine(message);
        return Task.CompletedTask;
    }
}
