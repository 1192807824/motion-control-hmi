namespace ControlHub.Services.Devices;

internal static class TestMeasurementRetry
{
    public const int MaximumRetryCount = 10;

    // retryCount counts additional attempts; cancellation must never start another measurement.
    public static Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> measure,
        Func<T, bool> isSuccessful,
        int retryCount,
        Func<int, CancellationToken, Task> beforeRetry,
        CancellationToken cancellationToken,
        Func<Exception, bool>? canRetryException = null)
        => ExecuteWithRangeAsync(measure, isSuccessful, retryCount, beforeRetry,
            cancellationToken, _ => false, 0, canRetryException);

    // 区间命中优先使用独立额度；区间额度耗尽直接保留末次结果，不再追加失败重试。
    public static async Task<T> ExecuteWithRangeAsync<T>(
        Func<CancellationToken, Task<T>> measure,
        Func<T, bool> isSuccessful,
        int retryCount,
        Func<int, CancellationToken, Task> beforeRetry,
        CancellationToken cancellationToken,
        Func<T, bool> isInRetryRange,
        int rangeRetryCount,
        Func<Exception, bool>? canRetryException = null)
    {
        if (retryCount is < 0 or > MaximumRetryCount)
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        if (rangeRetryCount is < 0 or > MaximumRetryCount)
            throw new ArgumentOutOfRangeException(nameof(rangeRetryCount));

        var failureRetries = 0;
        var rangeRetries = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await measure(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (rangeRetryCount > 0 && isInRetryRange(result))
                {
                    if (rangeRetries == rangeRetryCount)
                        return result;
                    rangeRetries++;
                }
                else
                {
                    if (isSuccessful(result) || failureRetries == retryCount)
                        return result;
                    failureRetries++;
                }
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException &&
                !cancellationToken.IsCancellationRequested && failureRetries < retryCount &&
                (canRetryException?.Invoke(exception) ?? true))
            {
                // Preserve the final exception for the station's existing safe-return/stop path.
                failureRetries++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await beforeRetry(failureRetries + rangeRetries, cancellationToken);
        }
    }
}
