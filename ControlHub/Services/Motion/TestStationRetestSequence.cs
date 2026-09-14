namespace ControlHub.Services.Motion;

public static class TestStationRetestSequence
{
    public static async Task RunAsync(
        int axisNo, double waitPosition, double pressPosition, int dwellMilliseconds,
        Func<int, double, CancellationToken, Task> moveAndWait,
        Func<int, CancellationToken, Task> delay,
        Action<string> report,
        CancellationToken cancellationToken)
    {
        if (axisNo is < 13 or > 15 || !double.IsFinite(waitPosition) || !double.IsFinite(pressPosition) ||
            dwellMilliseconds is < 0 or > 60000)
            throw new ArgumentException("测试站复测的轴号、位置或下压等待时间无效。");
        cancellationToken.ThrowIfCancellationRequested();
        report("返回等待位");
        await moveAndWait(axisNo, waitPosition, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        report("重新下压");
        await moveAndWait(axisNo, pressPosition, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        report($"下压到位，等待 {dwellMilliseconds} ms");
        await delay(dwellMilliseconds, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
    }
}
