using ControlHub.Services.Motion;

var checks = new (string Name, Func<Task> Run)[]
{
    ("Legacy polling migrates to 10 ms without changing commissioned motion profiles", CheckMigration),
    ("10 ms validates and sub-10 ms is rejected", CheckValidation),
    ("Slow BIN placement does not block scheduling or release the next pickup gate", CheckDeferredUnload),
    ("A queued batch cannot reuse axes when only the previous pickup has completed", CheckAxisOwnership),
    ("Previous BIN failure reaches both queued tasks without starting motion", CheckPreviousFailure),
    ("Pickup failure reaches both completion tasks", CheckPickupFailure),
    ("BIN failure after pickup blocks the following batch", CheckPlacementFailure),
    ("Cancellation waits for previous cleanup and prevents queued motion", CheckWaitingCancellation),
    ("Cancellation during pickup cancels both completion tasks", CheckPickupCancellation),
    ("An empty batch completes both gates", CheckEmptyBatch),
    ("A paused queued batch keeps its pickup gate closed", CheckPausedBatch)
};
foreach (var (name, run) in checks)
{
    await run().WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"PASS: {name}");
}
Console.WriteLine($"All {checks.Length} checks passed. No hardware was opened.");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static Task CheckMigration()
{
    var profile = new MotionMoveProfile { AccelerationSeconds = 0.023, StopVelocity = 17 };
    var home = new MotionHomeProfile { Mode = 33, HighVelocity = 12345 };
    var options = new MotionCardOptions
    {
        ConfigurationVersion = 4,
        PollIntervalMilliseconds = 50,
        AxisMoveProfiles = new() { [1] = profile },
        AxisHomeProfiles = new() { [1] = home }
    };
    options.ApplyMigrations();
    options.Validate();
    Require(options.PollIntervalMilliseconds == 10 && options.ConfigurationVersion == 5, "Migration did not select 10 ms.");
    Require(ReferenceEquals(profile, options.AxisMoveProfiles[1]) && profile.AccelerationSeconds == 0.023,
        "Migration changed the commissioned move profile.");
    Require(ReferenceEquals(home, options.AxisHomeProfiles[1]) && home.HighVelocity == 12345,
        "Migration changed the commissioned homing profile.");
    options.ApplyMigrations();
    Require(options.PollIntervalMilliseconds == 10, "Migration is not idempotent.");
    return Task.CompletedTask;
}

static Task CheckValidation()
{
    new MotionCardOptions().Validate();
    Require(new MotionCardOptions().PollIntervalMilliseconds == 10, "Incorrect polling default.");
    try { new MotionCardOptions { PollIntervalMilliseconds = 9 }.Validate(); }
    catch (InvalidDataException) { return Task.CompletedTask; }
    throw new InvalidOperationException("Polling below 10 ms was accepted.");
}

static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

static async Task CheckDeferredUnload()
{
    var previousPlacement = Signal();
    var started = Signal();
    var partsSafe = Signal();
    var placement = Signal();
    var sequence = ProductionUnloadSequence.StartAfter(previousPlacement.Task, async pickup =>
    {
        started.SetResult(true);
        await partsSafe.Task;
        pickup.SetResult(true);
        await placement.Task;
    }, CancellationToken.None);

    Require(!started.Task.IsCompleted && !sequence.PickupTask.IsCompleted && !sequence.UnloadTask.IsCompleted,
        "A pending previous placement must return tasks without starting the next batch or releasing DD.");
    previousPlacement.SetResult(true);
    await started.Task;
    Require(!sequence.PickupTask.IsCompleted, "Starting pickup released DD before parts were safe.");
    partsSafe.SetResult(true);
    await sequence.PickupTask;
    Require(!sequence.UnloadTask.IsCompleted, "Pickup completion waited for or completed BIN placement.");
    placement.SetResult(true);
    await sequence.UnloadTask;
}

static async Task CheckAxisOwnership()
{
    var placementAndReturn = Signal();
    var first = ProductionUnloadSequence.StartAfter(Task.CompletedTask, async pickup =>
    {
        pickup.SetResult(true);
        await placementAndReturn.Task;
    }, CancellationToken.None);
    await first.PickupTask;
    var nextStarted = false;
    var next = ProductionUnloadSequence.StartAfter(first.UnloadTask, _ =>
    {
        nextStarted = true;
        return Task.CompletedTask;
    }, CancellationToken.None);
    Require(!nextStarted && !next.PickupTask.IsCompleted, "Next batch reused axes before BIN return finished.");
    placementAndReturn.SetResult(true);
    await Task.WhenAll(first.UnloadTask, next.UnloadTask, next.PickupTask);
    Require(nextStarted, "Queued batch did not start after axes were released.");
}

static async Task CheckPreviousFailure()
{
    var previous = Signal();
    var failure = new InvalidOperationException("previous BIN failed");
    var started = false;
    var sequence = ProductionUnloadSequence.StartAfter(previous.Task, _ =>
    {
        started = true;
        return Task.CompletedTask;
    }, CancellationToken.None);
    previous.SetException(failure);
    await RequireFailure(sequence.UnloadTask, failure);
    await RequireFailure(sequence.PickupTask, failure);
    Require(!started, "Queued motion started after previous failure.");
}

static async Task CheckPickupFailure()
{
    var failure = new InvalidOperationException("pickup failed");
    var sequence = ProductionUnloadSequence.StartAfter(Task.CompletedTask,
        _ => throw failure, CancellationToken.None);
    await RequireFailure(sequence.UnloadTask, failure);
    await RequireFailure(sequence.PickupTask, failure);
}

static async Task CheckPlacementFailure()
{
    var placement = Signal();
    var failure = new InvalidOperationException("BIN failed after safe pickup");
    var first = ProductionUnloadSequence.StartAfter(Task.CompletedTask, async pickup =>
    {
        pickup.SetResult(true);
        await placement.Task;
    }, CancellationToken.None);
    await first.PickupTask;
    var nextStarted = false;
    var next = ProductionUnloadSequence.StartAfter(first.UnloadTask, _ =>
    {
        nextStarted = true;
        return Task.CompletedTask;
    }, CancellationToken.None);
    placement.SetException(failure);
    await RequireFailure(first.UnloadTask, failure);
    await RequireFailure(next.UnloadTask, failure);
    await RequireFailure(next.PickupTask, failure);
    Require(first.PickupTask.IsCompletedSuccessfully && !nextStarted,
        "Late BIN failure changed an already-safe pickup or allowed the next batch to move.");
}

static async Task CheckWaitingCancellation()
{
    using var stop = new CancellationTokenSource();
    var previousCleanup = Signal();
    var started = false;
    var sequence = ProductionUnloadSequence.StartAfter(previousCleanup.Task, _ =>
    {
        started = true;
        return Task.CompletedTask;
    }, stop.Token);
    stop.Cancel();
    Require(!sequence.UnloadTask.IsCompleted, "Stop abandoned previous motion/valve cleanup.");
    previousCleanup.SetResult(true);
    await RequireCanceled(sequence.UnloadTask);
    await RequireCanceled(sequence.PickupTask);
    Require(!started, "A canceled queued batch issued motion.");
}

static async Task CheckPickupCancellation()
{
    using var stop = new CancellationTokenSource();
    var sequence = ProductionUnloadSequence.StartAfter(Task.CompletedTask,
        _ => Task.Delay(Timeout.Infinite, stop.Token), stop.Token);
    stop.Cancel();
    await RequireCanceled(sequence.UnloadTask);
    await RequireCanceled(sequence.PickupTask);
}

static async Task CheckEmptyBatch()
{
    var sequence = ProductionUnloadSequence.StartAfter(Task.CompletedTask,
        _ => Task.CompletedTask, CancellationToken.None);
    await Task.WhenAll(sequence.UnloadTask, sequence.PickupTask);
}

static async Task CheckPausedBatch()
{
    var resume = Signal();
    var moved = false;
    var sequence = ProductionUnloadSequence.StartAfter(Task.CompletedTask, async pickup =>
    {
        await resume.Task;
        moved = true;
        pickup.SetResult(true);
    }, CancellationToken.None);
    Require(!moved && !sequence.PickupTask.IsCompleted, "Paused batch moved or released DD.");
    resume.SetResult(true);
    await Task.WhenAll(sequence.UnloadTask, sequence.PickupTask);
    Require(moved, "Batch did not continue on resume.");
}

static async Task RequireFailure(Task task, Exception expected)
{
    try { await task; }
    catch (Exception error) when (ReferenceEquals(error, expected)) { return; }
    throw new InvalidOperationException("Task did not propagate the expected failure.");
}

static async Task RequireCanceled(Task task)
{
    try { await task; }
    catch (OperationCanceledException) when (task.IsCanceled) { return; }
    throw new InvalidOperationException("Task did not propagate cancellation.");
}
