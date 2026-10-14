using System.IO;
using ControlHub.Services.Devices;
using ControlHub.Services.Motion;

internal static partial class Program
{
    private static async Task CheckMechanicalRetestsAsync()
    {
        foreach (var axis in new[] { 13, 14, 15 })
        foreach (var retries in new[] { 0, 1, 3 })
        foreach (var recover in new[] { false, true })
        {
            var events = new List<string>();
            var attempts = 0;
            var passed = await Retry(_ =>
            {
                events.Add("measure");
                return Task.FromResult(recover && ++attempts == 2);
            }, retries, (_, token) => TestStationRetestSequence.RunAsync(axis, -100, 500, 350,
                (actualAxis, target, _) =>
                {
                    Require(actualAxis == axis, "Mechanical retry moved a different station.");
                    events.Add(target == -100 ? "wait" : target == 500 ? "press" : "bad target");
                    return Task.CompletedTask;
                },
                (milliseconds, _) => { Require(milliseconds == 350, "Configured dwell was not used."); events.Add("dwell"); return Task.CompletedTask; },
                _ => { }, token));
            var actualRetries = recover && retries > 0 ? 1 : retries;
            var expected = new List<string> { "measure" };
            for (var i = 0; i < actualRetries; i++) expected.AddRange(["wait", "press", "dwell", "measure"]);
            Require(events.SequenceEqual(expected) && passed == (recover && retries > 0),
                "Every additional attempt must perform wait-position, press-position, dwell, then measurement.");
        }

        var waitReached = new TaskCompletionSource<bool>();
        var pressReached = new TaskCompletionSource<bool>();
        var dwellFinished = new TaskCompletionSource<bool>();
        var sequence = new List<string>();
        var measures = 0;
        var pending = Retry(_ => { sequence.Add("measure"); return Task.FromResult(++measures == 2); }, 1,
            (_, token) => TestStationRetestSequence.RunAsync(13, 0, 1000, 200,
                async (_, target, _) =>
                {
                    sequence.Add(target == 0 ? "wait" : "press");
                    await (target == 0 ? waitReached.Task : pressReached.Task);
                }, async (_, _) => { sequence.Add("dwell"); await dwellFinished.Task; }, _ => { }, token));
        Require(sequence.SequenceEqual(new[] { "measure", "wait" }), "Press was issued before the wait position was reached.");
        waitReached.SetResult(true);
        Require(sequence.SequenceEqual(new[] { "measure", "wait", "press" }), "Dwell started before press completion.");
        pressReached.SetResult(true);
        Require(sequence.SequenceEqual(new[] { "measure", "wait", "press", "dwell" }) && measures == 1,
            "Measurement was triggered before settling completed.");
        dwellFinished.SetResult(true);
        Require(await pending && measures == 2, "Completed mechanical retry did not allow the next measurement.");

        foreach (var failureStage in new[] { "wait", "press", "dwell" })
        foreach (var cancel in new[] { false, true })
        {
            measures = 0;
            using var cancellation = new CancellationTokenSource();
            Task Stage(string name)
            {
                if (name != failureStage) return Task.CompletedTask;
                if (cancel) { cancellation.Cancel(); return Task.FromCanceled(cancellation.Token); }
                return Task.FromException(new IOException("movement failed"));
            }
            try
            {
                await Retry(_ => { measures++; return Task.FromResult(false); }, 3,
                    (_, token) => TestStationRetestSequence.RunAsync(14, 0, 1000, 200,
                        (_, target, _) => Stage(target == 0 ? "wait" : "press"),
                        (_, _) => Stage("dwell"), _ => { }, token), cancellation.Token);
                throw new Exception("Failed or canceled preparation allowed a new measurement.");
            }
            catch (OperationCanceledException) when (cancel) { }
            catch (IOException) when (!cancel) { }
            Require(measures == 1, "A retry command was sent after a movement/dwell failure.");
        }

        var preparations = 0;
        try
        {
            await Retry(_ => Task.FromException<bool>(new SM7110StopOutputException("STOP failed", new IOException())), 3,
                (_, _) => { preparations++; return Task.CompletedTask; },
                canRetryException: exception => !SM7110TimedTest.HasStopFailure(exception));
            throw new Exception("Failed STOP allowed a mechanical retry.");
        }
        catch (SM7110StopOutputException) { }
        Require(preparations == 0, "Do not raise a station when SM7110 STOP failed.");

        var poweredEvents = new List<string>();
        var rounds = 0;
        var recovered = await Retry(async token =>
        {
            rounds++;
            var reading = await SM7110TimedTest.RunAsync(new(5e10, 0.02),
                (command, _) => { poweredEvents.Add(command); return Task.CompletedTask; },
                (_, _) => Task.FromResult(rounds == 1 ? "0,1E+10" : "0,5E+10"), null, token);
            return !reading.TimedOut;
        }, 1, (_, token) => TestStationRetestSequence.RunAsync(14, 0, 1000, 200,
            (_, target, _) => { poweredEvents.Add(target == 0 ? "wait" : "press"); return Task.CompletedTask; },
            (_, _) => { poweredEvents.Add("dwell"); return Task.CompletedTask; }, _ => { }, token));
        Require(recovered && poweredEvents.SequenceEqual(new[] { ":STARt", ":STOP", "wait", "press", "dwell", ":STARt", ":STOP" }),
            "SM7110 retry must finish the powered round and STOP before retract/press/dwell/restart.");
        Console.WriteLine("PASS: per-station mechanical retry ordering/counts, awaited movement/dwell, cancellation, STOP interlock and complete SM7110 round retries.");
    }
}
