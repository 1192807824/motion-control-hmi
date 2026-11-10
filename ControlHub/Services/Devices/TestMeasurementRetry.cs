namespace ControlHub.Services.Devices;

internal static class TestMeasurementRetry
{
    public const int MaximumRetryCount = 10;

    // retryCount counts additional attempts; cancellation must never start another measurement.
    public static async Task<T> ExecuteAsync<T>(
        Func<CancellationToken, Task<T>> measure,
        Func<T, bool> isSuccessful,
        int retryCount,
        Func<int, CancellationToken, Task> beforeRetry,
        CancellationToken cancellationToken,
        Func<Exception, bool>? canRetryException = null)
        => await ExecuteWithOverloadAsync(measure, isSuccessful, retryCount, beforeRetry,
            cancellationToken, canRetryException, _ => false, 0);

    public static async Task<T> ExecuteWithOverloadAsync<T>(
        Func<CancellationToken, Task<T>> measure,
        Func<T, bool> isSuccessful,
        int retryCount,
        Func<int, CancellationToken, Task> beforeRetry,
        CancellationToken cancellationToken,
        Func<Exception, bool>? canRetryException,
        Func<T, bool> isOverload,
        int overloadRetryCount)
    {
        if (retryCount is < 0 or > MaximumRetryCount)
            throw new ArgumentOutOfRangeException(nameof(retryCount));
        if (overloadRetryCount is < 0 or > MaximumRetryCount)
            throw new ArgumentOutOfRangeException(nameof(overloadRetryCount));
        var normalRetries = 0;
        var overloadRetries = 0;
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await measure(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (isSuccessful(result))
                    return result;
                // 过载独立计数，关闭或耗尽后不借用普通重试额度。
                if (isOverload(result))
                {
                    if (overloadRetries >= overloadRetryCount) return result;
                    overloadRetries++;
                }
                else
                {
                    if (normalRetries >= retryCount) return result;
                    normalRetries++;
                }
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException &&
                !cancellationToken.IsCancellationRequested && normalRetries < retryCount &&
                (canRetryException?.Invoke(exception) ?? true))
            {
                normalRetries++;
            }

            cancellationToken.ThrowIfCancellationRequested();
            await beforeRetry(attempt + 1, cancellationToken);
        }
    }
}
