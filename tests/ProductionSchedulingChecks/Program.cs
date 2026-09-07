using ControlHub.Services.Motion;
using ControlHub.Services.Production;

var checks = new (string Name, Func<Task> Run)[]
{
    ("Legacy polling migrates to 10 ms without changing commissioned motion profiles", CheckMigration),
    ("10 ms validates and sub-10 ms is rejected", CheckValidation),
    ("Placement is released while previous BIN unload remains pending", CheckOverlap),
    ("Empty pickup releases the next DD barrier", CheckEmpty),
    ("Previous BIN failure blocks next pickup and propagates to DD barrier", CheckPreviousFailure),
    ("Pickup failure propagates to both tasks", CheckPickupFailure),
    ("BIN failure after pickup prevents the following batch from starting", CheckBinFailure),
    ("Cancellation while queued never starts the next pickup", CheckCancellation)
};
foreach (var (name, run) in checks)
{
    await run().WaitAsync(TimeSpan.FromSeconds(5));
    Console.WriteLine($"PASS: {name}");
}
Console.WriteLine($"All {checks.Length} checks passed. No hardware was opened.");

static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task ExpectFailure(Task task, Exception expected)
{
    try { await task; }
    catch (Exception actual) when (ReferenceEquals(actual, expected)) { return; }
    throw new InvalidOperationException("Expected the original failure to propagate.");
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

static async Task CheckOverlap()
{
    var previousBin = Signal();
    var started = Signal();
    var allowPickup = Signal();
    var finishBin = Signal();
    var scheduled = SecondSetUnloadSequence.Start(previousBin.Task, async (pickup, _) =>
    {
        started.SetResult(true);
        await allowPickup.Task;
        pickup.SetResult(true);
        await finishBin.Task;
    }, CancellationToken.None);

    // Scheduling has returned to the first set, but neither the second set nor DD may advance.
    Require(!started.Task.IsCompleted && !scheduled.PickupTask.IsCompleted && !scheduled.UnloadTask.IsCompleted,
        "The next batch started before the previous BIN unload finished.");
    previousBin.SetResult(true);
    await started.Task;
    Require(!scheduled.PickupTask.IsCompleted, "DD was released before actual pickup completion.");
    allowPickup.SetResult(true);
    await scheduled.PickupTask;
    Require(!scheduled.UnloadTask.IsCompleted, "Pickup completion is incorrectly tied to BIN completion.");
    finishBin.SetResult(true);
    await scheduled.UnloadTask;
}

static async Task CheckEmpty()
{
    var scheduled = SecondSetUnloadSequence.Start(Task.CompletedTask, (_, _) => Task.CompletedTask, CancellationToken.None);
    await Task.WhenAll(scheduled.PickupTask, scheduled.UnloadTask);
}

static async Task CheckPreviousFailure()
{
    var failure = new InvalidOperationException("previous BIN failed");
    var started = false;
    var scheduled = SecondSetUnloadSequence.Start(Task.FromException(failure), (_, _) =>
    {
        started = true;
        return Task.CompletedTask;
    }, CancellationToken.None);
    await ExpectFailure(scheduled.PickupTask, failure);
    await ExpectFailure(scheduled.UnloadTask, failure);
    Require(!started, "Pickup started after the previous batch failed.");
}

static async Task CheckPickupFailure()
{
    var failure = new InvalidOperationException("pickup failed");
    var scheduled = SecondSetUnloadSequence.Start(Task.CompletedTask, (_, _) => Task.FromException(failure), CancellationToken.None);
    await ExpectFailure(scheduled.PickupTask, failure);
    await ExpectFailure(scheduled.UnloadTask, failure);
}

static async Task CheckBinFailure()
{
    var failure = new InvalidOperationException("BIN failed after pickup");
    var first = SecondSetUnloadSequence.Start(Task.CompletedTask, (pickup, _) =>
    {
        pickup.SetResult(true);
        return Task.FromException(failure);
    }, CancellationToken.None);
    await first.PickupTask;
    await ExpectFailure(first.UnloadTask, failure);
    var started = false;
    var next = SecondSetUnloadSequence.Start(first.UnloadTask, (_, _) =>
    {
        started = true;
        return Task.CompletedTask;
    }, CancellationToken.None);
    await ExpectFailure(next.PickupTask, failure);
    await ExpectFailure(next.UnloadTask, failure);
    Require(!started, "Next batch started after BIN failure.");
}

static async Task CheckCancellation()
{
    using var cancellation = new CancellationTokenSource();
    var previousBin = Signal();
    var started = false;
    var scheduled = SecondSetUnloadSequence.Start(previousBin.Task, (_, _) =>
    {
        started = true;
        return Task.CompletedTask;
    }, cancellation.Token);
    cancellation.Cancel();
    previousBin.SetResult(true);
    foreach (var task in new[] { scheduled.PickupTask, scheduled.UnloadTask })
    {
        try { await task; }
        catch (OperationCanceledException) { continue; }
        throw new InvalidOperationException("Queued task did not cancel.");
    }
    Require(!started, "Cancellation still started the next pickup.");
}
