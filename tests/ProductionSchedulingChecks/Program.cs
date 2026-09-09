using ControlHub.Services.Motion;

var checks = new (string Name, Func<Task> Run)[]
{
    ("Legacy polling migrates to 10 ms without changing commissioned motion profiles", CheckMigration),
    ("10 ms validates and sub-10 ms is rejected", CheckValidation)
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
