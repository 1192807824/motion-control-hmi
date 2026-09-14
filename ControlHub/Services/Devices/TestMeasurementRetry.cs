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
        CancellationToken cancellationToken)
    {
        if (retryCount is < 0 or > MaximumRetryCount)
            throw new ArgumentOutOfRangeException(nameof(retryCount));

        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await measure(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (isSuccessful(result) || attempt == retryCount)
                    return result;
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException &&
                !cancellationToken.IsCancellationRequested && attempt < retryCount)
            {
                // Preserve the final exception for the station's existing safe-return/stop path.
            }

            cancellationToken.ThrowIfCancellationRequested();
            await beforeRetry(attempt + 1, cancellationToken);
        }
    }
}
