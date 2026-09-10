namespace ControlHub.Services.Motion;

/// <summary>
/// Queues the next unload behind the previous BIN placement without delaying the
/// caller. Pickup completion remains a separate gate for the next carousel turn.
/// </summary>
internal static class ProductionUnloadSequence
{
    public static (Task UnloadTask, Task PickupTask) StartAfter(
        Task previousUnloadTask,
        Func<TaskCompletionSource<bool>, Task> runUnloadAsync,
        CancellationToken cancellationToken)
    {
        var pickupCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var unloadTask = RunAsync();
        return (unloadTask, pickupCompletion.Task);

        async Task RunAsync()
        {
            try
            {
                // Keep the caller's context: production callbacks own WPF state.
                // Observe the previous operation through completion even on stop;
                // it may still be finishing motion/valve cleanup.
                await previousUnloadTask;
                cancellationToken.ThrowIfCancellationRequested();
                await runUnloadAsync(pickupCompletion);
                // Empty stations complete without an early pickup signal.
                pickupCompletion.TrySetResult(true);
            }
            catch (OperationCanceledException exception)
            {
                pickupCompletion.TrySetCanceled(exception.CancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                // A failure while waiting must also fail the DD pickup gate;
                // otherwise the next turn/drain could wait forever.
                pickupCompletion.TrySetException(exception);
                throw;
            }
        }
    }
}
