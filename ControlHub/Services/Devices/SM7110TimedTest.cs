using System.Diagnostics;

namespace ControlHub.Services.Devices;

public sealed record SM7110TimedTestSettings(double MinimumResistanceOhms, double MaximumSeconds)
{
    public void Validate()
    {
        if (!double.IsFinite(MinimumResistanceOhms) || MinimumResistanceOhms <= 0)
            throw new ArgumentException("SM7110达标门限必须是大于0的有效电阻值（GΩ）。");
        if (!double.IsFinite(MaximumSeconds) || MaximumSeconds <= 0 || MaximumSeconds > 3600)
            throw new ArgumentException("请设置SM7110最长测试时间（大于0且不超过3600秒）。");
    }
}

public sealed class SM7110StopOutputException(string message, Exception inner) : InvalidOperationException(message, inner);

public static class SM7110TimedTest
{
    public static bool HasStopFailure(Exception exception) => exception is SM7110StopOutputException ||
        (exception is AggregateException aggregate && aggregate.InnerExceptions.Any(HasStopFailure)) ||
        (exception.InnerException is { } inner && HasStopFailure(inner));

    public static async Task<SM7110MeasurementResult> RunAsync(
        SM7110TimedTestSettings settings,
        Func<string, CancellationToken, Task> send,
        Func<string, CancellationToken, Task<string>> query,
        Action<SM7110MeasurementResult>? report,
        CancellationToken cancellationToken)
    {
        settings.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.MaximumSeconds));
        var elapsed = Stopwatch.StartNew();
        SM7110MeasurementResult last = new(double.NaN, 1, "R", "");
        try
        {
            // START只发一次；每次TRG获得一个新样本，读数偏低时保持加电，不发送STOP。
            await send(":STARt", deadline.Token);
            while (true)
            {
                var response = await query("*TRG;*WAI;:MEASure:RESult? 3", deadline.Token);
                cancellationToken.ThrowIfCancellationRequested();
                last = SM7110Protocol.ParseMeasurementResult(response, "R") with { TestElapsedSeconds = elapsed.Elapsed.TotalSeconds };
                if (deadline.IsCancellationRequested || elapsed.Elapsed.TotalSeconds >= settings.MaximumSeconds)
                    break;
                report?.Invoke(last);
                if (last.IsSuccessful && last.Value >= settings.MinimumResistanceOhms)
                    return last;
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && deadline.IsCancellationRequested)
        {
            // 达到本轮总时限，由外层决定是否先执行机械复测再开始下一轮。
        }
        finally
        {
            try { await send(":STOP", CancellationToken.None); }
            catch (Exception exception)
            {
                throw new SM7110StopOutputException("SM7110停止输出/放电命令失败，禁止继续流转，请检查仪表。", exception);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return last with { TimedOut = true, TestElapsedSeconds = elapsed.Elapsed.TotalSeconds };
    }
}
