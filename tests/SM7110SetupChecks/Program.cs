using ControlHub.Services.Devices;

var commands = SM7110Protocol.BuildSetupCommands(new SerialConnectionSettings());
foreach (var rejected in commands.Skip(1))
{
    var sent = new List<string>();
    var queries = 0;
    try
    {
        await SM7110Protocol.ApplySetupCommandsAsync(commands,
            (command, _) => { sent.Add(command); return Task.CompletedTask; },
            (command, _) =>
            {
                Require(command == "*ESR?", "Error query must be a separate command.");
                queries++;
                return Task.FromResult(sent[^1] == rejected ? "16" : "0");
            }, CancellationToken.None);
        throw new Exception("Rejected setup was accepted.");
    }
    catch (InvalidOperationException error)
    {
        Require(error.Message.Contains($"【{rejected}】") && error.Message.Contains("*ESR?=16"),
            "Diagnostic must identify the exact rejected command and register value.");
        Require(sent[^1] == rejected && queries == sent.Count, "Must stop at the first failure.");
    }
}

foreach (var invalid in new[] { "", "garbage", "0,16", "-1", "256", "32", "48", "4", "8" })
{
    var sent = 0;
    try
    {
        await SM7110Protocol.ApplySetupCommandsAsync(commands,
            (_, _) => { sent++; return Task.CompletedTask; },
            (_, _) => Task.FromResult(invalid), CancellationToken.None);
        throw new Exception($"Bad status was accepted: {invalid}");
    }
    catch (InvalidOperationException)
    {
        Require(sent == 1, "Must stop on invalid or error status.");
    }
}

var successfulSends = 0;
await SM7110Protocol.ApplySetupCommandsAsync(commands,
    (_, _) => { successfulSends++; return Task.CompletedTask; },
    (_, _) => Task.FromResult(" +0\r\n"), CancellationToken.None);
Require(successfulSends == commands.Count, "All valid parameters must be applied.");

using var cancelled = new CancellationTokenSource();
cancelled.Cancel();
try
{
    await SM7110Protocol.ApplySetupCommandsAsync(commands,
        (_, _) => throw new Exception("Cancelled setup must not send commands."),
        (_, _) => throw new Exception("Cancelled setup must not query."), cancelled.Token);
    throw new Exception("Cancellation was ignored.");
}
catch (OperationCanceledException) { }

Console.WriteLine("SM7110 setup checks passed: exact failure attribution, stop on errors, invalid responses, success, cancellation.");

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
