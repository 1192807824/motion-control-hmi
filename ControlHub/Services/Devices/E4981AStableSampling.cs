using System.Diagnostics;

namespace ControlHub.Services.Devices;

public static class E4981AStableSampling
{
    public static void Validate(TcpConnectionSettings settings)
    {
        if (settings.StabilityTimeoutMilliseconds is < 100 or > 60000)
            throw new InvalidOperationException("稳定采样时限必须在100～60000毫秒之间。");
        if (settings.StabilitySampleCount is < 2 or > 20)
            throw new InvalidOperationException("连续稳定次数必须在2～20之间。");
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
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(settings.StabilityTimeoutMilliseconds);
        var watch = Stopwatch.StartNew();
        var window = new Queue<E4981AMeasurementResult>();
        E4981AMeasurementResult last = new(-1, double.NaN, double.NaN, null, "");
        var count = 0;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var sample = await measure(deadline.Token);
                cancellationToken.ThrowIfCancellationRequested();
                if (watch.Elapsed.TotalMilliseconds >= settings.StabilityTimeoutMilliseconds)
                    break; // 即使设备忽略取消，迟到读数也不能判为稳定成功。
                last = sample;
                count++;
                if (!sample.IsSuccessful || !double.IsFinite(sample.DissipationFactor))
                    window.Clear();
                else
                {
                    var judged = E4981AProtocol.ApplyLossLimit(sample, settings);
                    window.Enqueue(judged);
                    if (window.Count > settings.StabilitySampleCount) window.Dequeue();
                    if (window.Count == settings.StabilitySampleCount && IsStable(window, settings))
                    {
                        progress?.Invoke($"稳定采样完成：共{count}次，连续{window.Count}次稳定，耗时{watch.Elapsed.TotalMilliseconds:0} ms");
                        return judged with { SampleCount = count, SamplingElapsedMilliseconds = watch.Elapsed.TotalMilliseconds };
                    }
                }
                progress?.Invoke($"稳定采样第{count}次：{sample.StatusDescription}，C={sample.CapacitanceNf:G9} nF，D={sample.DissipationFactor:G9}");
                await Task.Delay(50, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
        }
        cancellationToken.ThrowIfCancellationRequested();
        progress?.Invoke($"稳定采样超时：{count}次读数未满足稳定条件，将按测试失败处理");
        return last with { StabilityTimedOut = true, LossFailureReason = null, SampleCount = count, SamplingElapsedMilliseconds = watch.Elapsed.TotalMilliseconds };
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
