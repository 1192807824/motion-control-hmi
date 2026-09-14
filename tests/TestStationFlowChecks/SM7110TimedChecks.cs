using System.IO;
using System.Text;
using ControlHub.Services.Devices;

internal static partial class Program
{
    private static async Task CheckSM7110TimedTestsAsync()
    {
        var commands = new List<string>();
        Task Send(string command, CancellationToken token)
        {
            if (command == ":STOP") Require(!token.IsCancellationRequested, "STOP must survive timeout/cancellation.");
            commands.Add(command);
            return Task.CompletedTask;
        }
        var samples = new Queue<string>(["0,1E+10", "5,9E+30", "0,5E+10"]);
        var progress = new List<double>();
        var settings = new SM7110TimedTestSettings(5e10, 1);
        var result = await SM7110TimedTest.RunAsync(settings, Send, (command, _) =>
        {
            Require(command == "*TRG;*WAI;:MEASure:RESult? 3", "Every sample must trigger a fresh measurement.");
            Require(commands.SequenceEqual(new[] { ":STARt" }), "Output must remain on between low/invalid samples.");
            return Task.FromResult(samples.Dequeue());
        }, reading => progress.Add(reading.Value), CancellationToken.None);
        Require(!result.TimedOut && result.Value == 5e10 && progress.Count == 3,
            "A valid sample equal to the threshold must end the powered session.");
        Require(commands.SequenceEqual(new[] { ":STARt", ":STOP" }), "A complete test must start/stop voltage exactly once.");

        commands.Clear();
        var count = 0;
        result = await SM7110TimedTest.RunAsync(settings with { MaximumSeconds = 0.12 }, Send,
            (_, _) => { count++; return Task.FromResult("0,1E+10"); }, null, CancellationToken.None);
        Require(result.TimedOut && count >= 2 && commands.SequenceEqual(new[] { ":STARt", ":STOP" }),
            "Low values must continue sampling until one overall deadline, without repeated START/STOP.");
        var range = CreateNested("SM7110AcceptanceRange", 5e10, 6e10, "R");
        var quality = Invoke(null, "ClassifySM7110Measurement", result, "BIN2", range)!;
        var product = ((Array)Invoke(null, "CreateCarouselStationStates")!).GetValue(1)!;
        Call(product, "SetMeasurement", quality);
        Require((string)Call(product, "GetUnloadDestination", true)! == "NG", "Timeout must route to NG.");

        commands.Clear();
        result = await SM7110TimedTest.RunAsync(settings with { MaximumSeconds = 0.02 }, Send,
            async (_, _) => { await Task.Delay(60); return "0,9E+10"; }, null, CancellationToken.None);
        quality = Invoke(null, "ClassifySM7110Measurement", result, "BIN2", range)!;
        Require(result.TimedOut && !(bool)Property(quality, "Passed")!, "A late high sample must not pass after the deadline.");
        commands.Clear();
        result = await SM7110TimedTest.RunAsync(settings with { MaximumSeconds = 0.02 }, Send,
            async (_, token) => { await Task.Delay(Timeout.Infinite, token); return ""; }, null, CancellationToken.None);
        Require(result.TimedOut && !result.IsSuccessful && commands[^1] == ":STOP", "No-response deadline must still stop output.");

        foreach (var error in new Exception[] { new IOException("wire failed"), new FormatException("bad response") })
        {
            commands.Clear();
            try
            {
                await SM7110TimedTest.RunAsync(settings, Send, (_, _) => Task.FromException<string>(error), null, CancellationToken.None);
                throw new Exception("Communication/parse failure was hidden.");
            }
            catch (Exception caught) when (ReferenceEquals(caught, error)) { }
            Require(commands.SequenceEqual(new[] { ":STARt", ":STOP" }), "Failure must stop output without starting a retry session.");
        }
        commands.Clear();
        using (var cancellation = new CancellationTokenSource())
        {
            try
            {
                await SM7110TimedTest.RunAsync(settings, Send, async (_, token) =>
                {
                    cancellation.Cancel();
                    await Task.Delay(Timeout.Infinite, token);
                    return "";
                }, null, cancellation.Token);
                throw new Exception("Operator cancellation was treated as an NG or OK result.");
            }
            catch (OperationCanceledException) { }
            Require(commands.SequenceEqual(new[] { ":STARt", ":STOP" }), "Operator stop must stop and discharge.");
        }
        try
        {
            await SM7110TimedTest.RunAsync(settings, (command, _) => command == ":STOP"
                ? Task.FromException(new IOException("stop failed")) : Task.CompletedTask,
                (_, _) => Task.FromResult("0,9E+10"), null, CancellationToken.None);
            throw new Exception("A failed STOP was ignored.");
        }
        catch (SM7110StopOutputException exception)
        {
            Require(SM7110TimedTest.HasStopFailure(new AggregateException(new InvalidOperationException("station failed", exception))),
                "Production must recognize nested STOP failures before raising test axes.");
        }
        foreach (var invalid in new[] { settings with { MaximumSeconds = 0 }, settings with { MaximumSeconds = double.NaN },
                     settings with { MaximumSeconds = 3601 }, settings with { MinimumResistanceOhms = 0 } })
        {
            commands.Clear();
            try { await SM7110TimedTest.RunAsync(invalid, Send, (_, _) => Task.FromResult(""), null, CancellationToken.None); throw new Exception("Invalid test settings accepted."); }
            catch (ArgumentException) { }
            Require(commands.Count == 0, "Invalid settings must never energize output.");
        }

        // A canceled measurement may leave a late line on the serial stream. The IDN barrier must ignore it.
        using var client = new SerialConnectionClient();
        var reply = new TaskCompletionSource<string>();
        typeof(SerialConnectionClient).GetField("_pendingResponse", Private)!.SetValue(client, reply);
        typeof(SerialConnectionClient).GetField("_pendingResponseFilter", Private)!.SetValue(client,
            (Func<string, bool>)SM7110Protocol.IsSupportedIdentity);
        typeof(SerialConnectionClient).GetMethod("ProcessReceivedBytes", Private)!.Invoke(client,
            [Encoding.ASCII.GetBytes("0,9E+10\r\nHIOKI,SM7120,123,V2.13\r\n")]);
        Require(reply.Task.IsCompletedSuccessfully && reply.Task.Result.StartsWith("HIOKI,SM7120"),
            "Old measurement data must not satisfy the next session's synchronization query.");
        Console.WriteLine("PASS: sustained voltage, threshold/equality, invalid samples, deadline NG, late samples, cancellation, STOP failures and stale-response barrier.");
    }
}
