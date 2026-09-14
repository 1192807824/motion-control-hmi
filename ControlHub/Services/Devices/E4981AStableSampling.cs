using System.Diagnostics;

namespace ControlHub.Services.Devices;

public static class E4981AStableSampling
{
    private const int SamplingIntervalMilliseconds = 50;

    public static void Validate(TcpConnectionSettings settings)
    {
        if (settings.StabilitySampleCount is < 2 or > 20)
            throw new InvalidOperationException("连续稳定次数必须在2～20之间。");
        if (settings.StabilityMaximumTestCount < settings.StabilitySampleCount || settings.StabilityMaximumTestCount > 1000)
            throw new InvalidOperationException("最大测试次数必须不小于连续稳定次数，且不超过1000次。");
        if (!double.IsFinite(settings.StabilityCapacitancePercent) || settings.StabilityCapacitancePercent <= 0 || settings.StabilityCapacitancePercent > 100)
            throw new InvalidOperationException("电容稳定波动必须大于0且不超过100%。");
        if (!double.IsFinite(settings.StabilityDissipationTolerance) || settings.StabilityDissipationTolerance <= 0)
            throw new InvalidOperationException("损耗D稳定波动必须为大于0的有限数值。");
    }

    public static async Task<E4981AMeasurementResult> RunAsync(
        TcpConnectionSettings settings,
        Func<CancellationToken, Task<E4981AMeasurementResult>> measure,
        CancellationToken cancellationToken,
        Action<string>? progress = null)
    {
        Validate(settings);
        cancellationToken.ThrowIfCancellationRequested();
        var watch = Stopwatch.StartNew();
        var window = new Queue<E4981AMeasurementResult>();
        E4981AMeasurementResult last = new(-1, double.NaN, double.NaN, null, "");
        var count = 0;
        for (var attempt = 1; attempt <= settings.StabilityMaximumTestCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // 每次等待完整回复再发下一条指令；单条通讯仍受连接设置中的命令超时保护。
            var sample = await measure(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            last = sample;
            count = attempt;
            if (!sample.IsSuccessful || !double.IsFinite(sample.DissipationFactor))
                window.Clear();
            else
            {
                var judged = E4981AProtocol.ApplyLossLimit(sample, settings);
                window.Enqueue(judged);
                if (window.Count > settings.StabilitySampleCount) window.Dequeue();
                if (window.Count == settings.StabilitySampleCount && IsStable(window, settings))
                {
                    progress?.Invoke($"稳定采样完成：共{count}/{settings.StabilityMaximumTestCount}次，连续{window.Count}次稳定，耗时{watch.Elapsed.TotalMilliseconds:0} ms");
                    return judged with { SampleCount = count, SamplingElapsedMilliseconds = watch.Elapsed.TotalMilliseconds };
                }
            }
            progress?.Invoke($"稳定采样第{count}/{settings.StabilityMaximumTestCount}次：{sample.StatusDescription}，C={sample.CapacitanceNf:G9} nF，D={sample.DissipationFactor:G9}");
            if (attempt < settings.StabilityMaximumTestCount)
                await Task.Delay(SamplingIntervalMilliseconds, cancellationToken);
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke($"稳定采样已达最大测试次数：{count}次读数未满足稳定条件，将按测试失败处理");
        return last with { StabilityAttemptsExhausted = true, LossFailureReason = null, SampleCount = count, SamplingElapsedMilliseconds = watch.Elapsed.TotalMilliseconds };
    }

    private static bool IsStable(IEnumerable<E4981AMeasurementResult> samples, TcpConnectionSettings settings)
    {
        var readings = samples.ToArray();
        // 整个窗口的极差必须合格，防止逐次缓慢漂移被当作稳定；跨分档/损耗边界也须重新稳定。
        var minC = readings.Min(x => x.CapacitanceFarads);
        var maxC = readings.Max(x => x.CapacitanceFarads);
        var scale = Math.Max(Math.Abs(minC), Math.Abs(maxC));
        return maxC - minC <= scale * (settings.StabilityCapacitancePercent / 100)
            && readings.Max(x => x.DissipationFactor) - readings.Min(x => x.DissipationFactor) <= settings.StabilityDissipationTolerance
            && readings.All(x => x.Bin == readings[0].Bin && x.LossRejected == readings[0].LossRejected);
    }
}
