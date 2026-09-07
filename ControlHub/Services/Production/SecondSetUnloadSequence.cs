namespace ControlHub.Services.Production;

/// <summary>
/// 第二套按批次串行使用自身轴组；排队不阻塞第一套，取料完成信号仍约束下一次DD转动。
/// </summary>
internal static class SecondSetUnloadSequence
{
    public static (Task UnloadTask, Task PickupTask) Start(
        Task previousUnloadTask,
        Func<TaskCompletionSource<bool>, CancellationToken, Task> runUnloadAsync,
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
                // 保持调用方的调度上下文，后续生产动作仍在原来的UI上下文中执行。
                await previousUnloadTask;
                cancellationToken.ThrowIfCancellationRequested();
                await runUnloadAsync(pickupCompletion, cancellationToken);
                // 无料时不启动运动，也必须释放下一次DD所等待的信号。
                pickupCompletion.TrySetResult(true);
            }
            catch (OperationCanceledException exception)
            {
                pickupCompletion.TrySetCanceled(exception.CancellationToken);
                throw;
            }
            catch (Exception exception)
            {
                // 包括上一批失败、启动失败，避免DD永久等待一个无人完成的取料信号。
                pickupCompletion.TrySetException(exception);
                throw;
            }
        }
    }
}
