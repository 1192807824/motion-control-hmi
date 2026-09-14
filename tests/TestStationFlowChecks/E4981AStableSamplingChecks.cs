using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Services.Motion;

internal static partial class Program
{
    private static async Task CheckE4981AStableSamplingAsync()
    {
        var settings = new TcpConnectionSettings { StabilityTimeoutMilliseconds = 2000 };
        E4981AMeasurementResult Reading(double nf = 100, double d = 0.1, int bin = 2, int status = 0)
            => new(status, nf * 1e-9, d, bin, "test");
        async Task<E4981AMeasurementResult> Sequence(params E4981AMeasurementResult[] readings)
        {
            var queue = new Queue<E4981AMeasurementResult>(readings);
            return await E4981AStableSampling.RunAsync(settings,
                _ => Task.FromResult(queue.Dequeue()), CancellationToken.None);
        }

        // 无接触/BIN11/无效D清空连续窗口；重获接触后必须重新积累足够读数。
        var recovered = await Sequence(Reading(), Reading(status: 1), Reading(), Reading(bin: 11),
            Reading(d: double.NaN), Reading(100), Reading(100.2), Reading(100.3));
        Require(recovered.IsSuccessful && recovered.SampleCount == 8 && Math.Abs(recovered.CapacitanceNf - 100.3) < 1e-8,
            "Faults must reset the window; return the last stable reading, not a transient or an average.");

        // 每对相邻值看似稳定，但整个窗口漂移超过1%，不能提前结束。
        var drift = await Sequence(Reading(100), Reading(100.8), Reading(101.6), Reading(101.6), Reading(101.6));
        Require(drift.SampleCount == 4, "Stability must use the full window span rather than adjacent pairs.");
        var lossDrift = await Sequence(Reading(d: 0.1), Reading(d: 0.104), Reading(d: 0.108), Reading(d: 0.108));
        Require(lossDrift.SampleCount == 4, "D must stabilize as well as capacitance.");
        var crossing = await Sequence(Reading(bin: 1), Reading(bin: 2), Reading(bin: 2), Reading(bin: 2));
        Require(crossing.SampleCount == 4, "A window crossing BIN boundaries must not finalize.");

        var ng = await Sequence(Reading(bin: 0), Reading(bin: 0), Reading(bin: 0));
        Require(ng.IsSuccessful && ng.Bin == 0 && ng.SampleCount == 3, "Stable out-of-bin must end sampling as NG, not wait for OK.");
        settings.ComparatorEnabled = settings.LossLimitEnabled = true;
        settings.LossLower = 0.01;
        settings.LossUpper = 0.05;
        var lossNg = await Sequence(Reading(), Reading(), Reading());
        Require(lossNg.LossRejected && lossNg.SampleCount == 3, "Stable loss rejection must end sampling.");

        settings.StabilityTimeoutMilliseconds = 160;
        var timedOut = await E4981AStableSampling.RunAsync(settings,
            _ => Task.FromResult(Reading(status: 1)), CancellationToken.None);
        Require(timedOut.StabilityTimedOut && !timedOut.IsSuccessful && timedOut.Status == 1,
            "Persistent OVLD must time out while retaining its raw status.");
        var classified = Invoke(null, "ClassifyE4981AMeasurement", timedOut)!;
        Require(!(bool)Property(classified, "Passed")! && (string?)Property(classified, "Bin") == "BIN0",
            "Stable sampling timeout must feed existing failure/retest and NG routing.");
        var round = 0;
        var motion = new List<string>();
        var passed = await Retry(async token =>
        {
            motion.Add("sample");
            var currentRound = ++round;
            var result = await E4981AStableSampling.RunAsync(settings,
                _ => Task.FromResult(Reading(status: currentRound == 1 ? 1 : 0, d: 0.02)), token);
            return (bool)Property(Invoke(null, "ClassifyE4981AMeasurement", result)!, "Passed")!;
        }, 1, (_, token) => TestStationRetestSequence.RunAsync(13, -100, 100, 200,
            (_, position, _) => { motion.Add(position == -100 ? "wait" : "press"); return Task.CompletedTask; },
            (_, _) => { motion.Add("dwell"); return Task.CompletedTask; }, _ => { }, token));
        Require(passed && motion.SequenceEqual(new[] { "sample", "wait", "press", "dwell", "sample" }),
            "Timed-out stable sampling must complete one mechanical retry before the next complete sampling window.");
        var calls = 0;
        var silent = await E4981AStableSampling.RunAsync(settings, async token =>
        {
            calls++;
            await Task.Delay(Timeout.Infinite, token);
            return Reading();
        }, CancellationToken.None);
        Require(silent.StabilityTimedOut && silent.SampleCount == 0 && calls == 1,
            "Deadline must interrupt an in-flight read even without any response.");
        var late = await E4981AStableSampling.RunAsync(settings, async _ =>
        {
            await Task.Delay(200);
            return Reading();
        }, CancellationToken.None);
        Require(late.StabilityTimedOut && late.SampleCount == 0, "A late response must never be accepted.");
        using var cancel = new CancellationTokenSource(40);
        try
        {
            await E4981AStableSampling.RunAsync(settings, async token =>
            {
                await Task.Delay(Timeout.Infinite, token);
                return Reading();
            }, cancel.Token);
            throw new Exception("Operator cancellation was swallowed.");
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }

        var defaults = JsonSerializer.Deserialize<TcpConnectionSettings>("{}")!;
        Require(defaults.StabilityTimeoutMilliseconds == 2000 && defaults.StabilitySampleCount == 3,
            "Legacy settings must receive stable sampling defaults.");
        settings.StabilitySampleCount = 5;
        settings.StabilityCapacitancePercent = 0.3;
        settings.StabilityDissipationTolerance = 0.002;
        var restored = ProductRecipeStore.Clone(new ProductRecipe { E4981A = settings }).E4981A;
        Require(restored.StabilitySampleCount == 5 && restored.StabilityCapacitancePercent == 0.3 &&
                restored.StabilityDissipationTolerance == 0.002 && restored.StabilityTimeoutMilliseconds == 160,
            "All stability parameters must survive recipe round-trip.");
        settings.StabilitySampleCount = 1;
        try { E4981AProtocol.BuildSetupCommands(settings); throw new Exception("Invalid stability settings accepted."); }
        catch (InvalidOperationException) { }
        Console.WriteLine("PASS: E4981A stable C/D window, invalid reset, BIN consistency, stable NG, deadline, cancellation, late result, retry routing and recipe persistence.");
    }

    private static async Task CheckE4981ALateResponseAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var firstTrigger = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commands = new List<string>();
        var server = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            using var reader = new StreamReader(socket.GetStream());
            using var writer = new StreamWriter(socket.GetStream()) { AutoFlush = true, NewLine = "\n" };
            commands.Add((await reader.ReadLineAsync(deadline.Token))!);
            firstTrigger.SetResult();
            await releaseLate.Task.WaitAsync(deadline.Token);
            await writer.WriteLineAsync("0,9E-7,0.1,1"); // 上一颗料迟到的结果。
            commands.Add((await reader.ReadLineAsync(deadline.Token))!);
            await writer.WriteLineAsync("Keysight Technologies,E4981A,TEST,1.0");
            commands.Add((await reader.ReadLineAsync(deadline.Token))!);
            await writer.WriteLineAsync("0,1E-7,0.1,2");
        });
        using var client = new E4981ATcpClient();
        try
        {
            await client.ConnectAsync(new TcpConnectionSettings { Host = "127.0.0.1", Port = ((IPEndPoint)listener.LocalEndpoint).Port }, deadline.Token);
            using var interrupted = new CancellationTokenSource();
            var pending = client.QueryAsync("*TRG", 1000, interrupted.Token);
            await firstTrigger.Task.WaitAsync(deadline.Token);
            interrupted.Cancel();
            try { await pending; throw new Exception("Query cancellation was swallowed."); }
            catch (OperationCanceledException) { }
            releaseLate.SetResult();
            var fresh = E4981AProtocol.ParseMeasurement(await client.QueryAsync("*TRG", 1000, deadline.Token));
            await server;
            Require(fresh.Bin == 2 && commands.SequenceEqual(new[] { "*TRG", "*IDN?", "*TRG" }),
                "Interrupted query must discard previous product response using an IDN barrier before next trigger.");
        }
        finally { listener.Stop(); deadline.Cancel(); }
        Console.WriteLine("PASS: loopback E4981A interrupted read synchronizes late data before next product.");
    }
}
