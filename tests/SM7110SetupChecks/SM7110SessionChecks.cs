using ControlHub.Services.Devices;

internal static class SM7110SessionChecks
{
    public static async Task RunAsync()
    {
        var session = new SM7110Session();
        var settings = new SerialConnectionSettings();
        var wire = new List<string>();
        Task Send(string command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            wire.Add(command);
            return Task.CompletedTask;
        }
        Task<string> QuerySetup(string command, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            wire.Add(command);
            return Task.FromResult("0");
        }
        Task<bool> Apply() => session.ApplySettingsIfChangedAsync(
            SM7110Protocol.BuildSetupCommands(settings), Send, QuerySetup, CancellationToken.None);
        async Task Prepare()
        {
            await session.SynchronizeResponseAsync(_ =>
            {
                wire.Add("*IDN?");
                return Task.FromResult("HIOKI,SM7120,123,V2.13");
            }, CancellationToken.None);
            Check(!await Apply(), "An unchanged measurement must not re-send setup.");
        }

        Check(await Apply(), "Initial connection must apply and confirm setup.");
        wire.Clear();
        for (var i = 0; i < 2; i++)
        {
            await Prepare();
            var result = await SM7110TimedTest.RunAsync(new(5e8, 1), Send,
                (command, token) => session.QueryMeasurementAsync(command, (request, _) =>
                {
                    wire.Add(request);
                    return Task.FromResult("0,6E+8");
                }, token), null, CancellationToken.None);
            Check(!result.TimedOut, "Normal measurement must succeed.");
        }
        Check(wire.SequenceEqual(Enumerable.Repeat(new[] { ":STARt", "*TRG;*WAI;:MEASure:RESult? 3", ":STOP" }, 2)
            .SelectMany(commands => commands)), "Repeated starts must send only measurement commands.");

        // Reproduce a deadline expiring while the instrument still owes a response.
        wire.Clear();
        var timedOut = await SM7110TimedTest.RunAsync(new(5e8, 0.1), Send,
            (command, token) => session.QueryMeasurementAsync(command, async (request, ct) =>
            {
                wire.Add(request);
                await Task.Delay(Timeout.Infinite, ct);
                return "";
            }, token), null, CancellationToken.None);
        Check(timedOut.TimedOut && session.NeedsResponseSync && wire[^1] == ":STOP",
            "Timeout must stop output and require response synchronization.");
        wire.Clear();
        await Prepare();
        Check(wire.SequenceEqual(new[] { "*IDN?" }), "After timeout, only the identity barrier may be sent; no setup.");

        // An operator stop during a pending read follows the same recovery path.
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                await SM7110TimedTest.RunAsync(new(5e8, 1), Send,
                    (command, token) => session.QueryMeasurementAsync(command, async (_, ct) =>
                    {
                        cancellation.Cancel();
                        await Task.Delay(Timeout.Infinite, ct);
                        return "";
                    }, token), null, cancellation.Token);
                throw new Exception("Operator stop must cancel the test.");
            }
            catch (OperationCanceledException) { }
        }
        wire.Clear();
        var timeoutAttempts = 0;
        var retryReports = new List<int>();
        try
        {
            await session.SynchronizeResponseAsync(_ =>
            {
                timeoutAttempts++;
                return Task.FromException<string>(new TimeoutException());
            }, CancellationToken.None, (attempt, _) => retryReports.Add(attempt));
            throw new Exception("Failed synchronization must not continue.");
        }
        catch (TimeoutException) { }
        Check(timeoutAttempts == SM7110Session.ResponseSyncAttempts &&
              retryReports.SequenceEqual(Enumerable.Range(1, SM7110Session.ResponseSyncAttempts - 1)),
            "Synchronization must retry the identity barrier a bounded number of times.");
        Check(session.NeedsResponseSync, "Failed synchronization must remain pending.");

        // A slow instrument (still finishing the interrupted measurement) recovers on a retry.
        var slowAttempts = 0;
        var slowIdentity = await session.SynchronizeResponseAsync(_ => ++slowAttempts < 2
            ? Task.FromException<string>(new TimeoutException())
            : Task.FromResult("HIOKI,SM7110,123,V2.13"), CancellationToken.None);
        Check(slowIdentity == "HIOKI,SM7110,123,V2.13" && slowAttempts == 2 && !session.NeedsResponseSync,
            "A late identity reply on retry must complete synchronization.");

        // Non-timeout failures are not retried.
        await session.QueryMeasurementAsync("x", (_, _) => Task.FromException<string>(new TimeoutException()), CancellationToken.None)
            .ContinueWith(_ => { });
        var ioAttempts = 0;
        try
        {
            await session.SynchronizeResponseAsync(_ =>
            {
                ioAttempts++;
                return Task.FromException<string>(new IOException());
            }, CancellationToken.None);
            throw new Exception("I/O failure must not continue.");
        }
        catch (IOException) { }
        Check(ioAttempts == 1 && session.NeedsResponseSync, "Only timeouts may retry synchronization.");

        // Surplus identity replies from retries must never satisfy other queries.
        var measurementFilter = SM7110Protocol.ResponseFilterFor("*TRG;*WAI;:MEASure:RESult? 3")!;
        var esrFilter = SM7110Protocol.ResponseFilterFor("*ESR?")!;
        Check(!measurementFilter("HIOKI,SM7110,123,V2.13") && measurementFilter("0,6E+8") &&
              !esrFilter("HIOKI,SM7120,123,V2.13") && esrFilter("0") &&
              SM7110Protocol.ResponseFilterFor(" *idn? ") is null,
            "Non-identity queries must skip identity lines; identity queries must stay unfiltered.");

        wire.Clear();
        await Prepare();
        Check(wire.SequenceEqual(new[] { "*IDN?" }), "Recovery after operator stop must not re-send setup.");

        settings.AppliedVoltageVolts = 16;
        Check(await Apply() && !await Apply(), "Changed voltage must apply exactly once.");
        session.InvalidateSettings();
        Check(await Apply(), "Explicit apply must still re-send setup.");
        session.ResetConnection();
        Check(await Apply(), "A new connection must apply setup again.");

        session.InvalidateSettings();
        try
        {
            await session.ApplySettingsIfChangedAsync(SM7110Protocol.BuildSetupCommands(settings), Send,
                (_, _) => Task.FromResult("16"), CancellationToken.None);
            throw new Exception("Rejected setup must fail.");
        }
        catch (InvalidOperationException) { }
        Check(await Apply(), "A failed setup must never be cached as successfully applied.");
        Console.WriteLine("PASS: repeated starts, deadline/stop recovery without parameter re-send, bounded sync retries, stale identity filtering, changed settings, reconnect and explicit apply.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
