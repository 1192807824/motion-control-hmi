using System.Reflection;
using System.Windows.Controls;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckDdTestStationInterlock()
    {
        var raw = DispatchProxy.Create<IMotionCard, DdRecordingCardProxy>();
        var recorder = (DdRecordingCardProxy)raw;
        var waits = new Dictionary<int, double> { [13] = -100, [14] = 0, [15] = 100 };
        void Reset() { foreach (var (axis, wait) in waits) recorder.States[axis] = DdSnapshot(axis, wait); }
        Reset();
        var guardCalls = 0;
        var card = new DdInterlockedMotionCard(raw, () =>
        {
            guardCalls++;
            DdTestStationInterlock.EnsureSafe(waits, raw.ReadAxis);
        });
        var ddActions = new Action[]
        {
            () => card.Home(0), () => card.Home(0, new MotionHomeProfile()),
            () => card.Jog(0, 100), () => card.Jog(0, -100),
            () => card.MoveRelative(0, 100, 100), () => card.MoveAbsolute(0, 100, 100),
            () => card.MoveRelativeSynchronized([1, 0], [100, 100], [100, 100]),
            () => card.MoveLinearAbsolute(0, [1, 0], [100, 100], [100, 100])
        };
        foreach (var start in ddActions)
        {
            Reset();
            var count = recorder.Commands.Count;
            start();
            Require(recorder.Commands.Count == count + 1, "Equality at each wait position must allow DD motion.");
            foreach (var axis in waits.Keys)
            foreach (var offset in new[] { 100.001d, 1000d })
            {
                Reset();
                recorder.States[axis] = DdSnapshot(axis, waits[axis] + offset);
                count = recorder.Commands.Count;
                var message = DdExpectBlocked(start);
                Require(recorder.Commands.Count == count, "Unsafe DD action reached the motion card.");
                Require(message.Contains($"轴{axis}") && message.Contains("当前位置") && message.Contains("实时位置均≤各自等待位+100 pulse"),
                    "Warning must identify the unsafe station, current position and wait position.");
            }
            foreach (var offset in new[] { -100000d, -1000d, -100.001d, -100d, -99.999d, -10d, 0d, 0.001d, 99.999d, 100d })
            {
                Reset();
                foreach (var axis in waits.Keys) recorder.States[axis] = DdSnapshot(axis, waits[axis] + offset);
                count = recorder.Commands.Count;
                start();
                Require(recorder.Commands.Count == count + 1,
                    "Feedback <= wait + 100 must allow DD, including coordinates far below wait - 100.");
            }
        }
        foreach (var axis in waits.Keys)
        foreach (var offset in new[] { -1000d, -100d, 100d })
        foreach (var invalid in new[]
        {
            DdSnapshot(axis, double.NaN), DdSnapshot(axis, double.PositiveInfinity), DdSnapshot(axis, double.NegativeInfinity),
            DdSnapshot(axis, waits[axis] + offset) with { IsMoving = true },
            DdSnapshot(axis, waits[axis] + offset) with { Alarm = true },
            DdSnapshot(axis, waits[axis] + offset) with { EmergencyInput = true }, DdSnapshot(axis + 1, waits[axis])
        })
        {
            Reset();
            recorder.States[axis] = invalid;
            var count = recorder.Commands.Count;
            DdExpectBlocked(() => card.Jog(0, 100));
            Require(recorder.Commands.Count == count, "Tolerance must never bypass invalid feedback, motion, alarm or emergency states.");
        }
        Reset();
        recorder.States.Remove(14);
        Require(DdExpectBlocked(() => card.Home(0)).Contains("无法读取实时位置"), "Read failures must fail closed.");
        DdExpectBlocked(() => DdTestStationInterlock.EnsureSafe(new Dictionary<int, double>(), raw.ReadAxis));
        Reset();
        recorder.States[15] = DdSnapshot(15, waits[15] + 101);
        var checksBeforeRecovery = guardCalls;
        card.MoveAbsolute(15, 100, 100);
        card.Jog(13, 100);
        card.Home(14);
        card.MoveRelativeSynchronized([13, 14], [10, 10], [100, 100]);
        card.MoveLinearAbsolute(1, [1, 2], [10, 10], [100, 100]);
        card.Stop(0); card.Stop(0, true); card.StopLinearInterpolation(0, true); card.EmergencyStop();
        Require(guardCalls == checksBeforeRecovery, "Recovery axes and stop/emergency commands must remain available.");

        var home = new HomePage();
        Set(home, "_loadingPresetPositions", true);
        foreach (var station in new[] { 5, 6, 7 })
        {
            var editors = ((TextBox, TextBox))Invoke(home, "GetTestStationPositionEditors", station)!;
            editors.Item2.Text = (station * 10).ToString();
        }
        var settings = (IReadOnlyDictionary<int, double>)Invoke(home, "ReadDdTestStationWaitPositions")!;
        Require(settings.Count == 3 && settings[13] == 50 && settings[14] == 60 && settings[15] == 70,
            "Manual DD must use all three currently configured wait positions.");
        Set(home, "_testStationSettings", new Dictionary<int, TestStationSettings>
        {
            [5] = new() { Enabled = false, WaitPosition = -10 },
            [6] = new() { Enabled = false, WaitPosition = -20 },
            [7] = new() { Enabled = false, WaitPosition = -30 }
        });
        Set(home, "_startSequenceRunning", true);
        settings = (IReadOnlyDictionary<int, double>)Invoke(home, "ReadDdTestStationWaitPositions")!;
        Require(settings.Count == 3 && settings[15] == -30, "Production must use frozen waits even for disabled test stations.");
        Set(home, "_oneKeyCollectRunning", true);
        settings = (IReadOnlyDictionary<int, double>)Invoke(home, "ReadDdTestStationWaitPositions")!;
        Require(settings[15] == 70, "One-key collect must use current waits, not stale production settings.");
        Console.WriteLine("PASS: every DD motion API, all three stations, feedback <= wait + 100, no lower bound, +100.001 rejection, invalid/moving/alarm/emergency states, recovery/stop bypass and production/collect settings. No hardware opened.");
    }

    private static MotionAxisSnapshot DdSnapshot(int axis, double feedback) =>
        new(axis, 999999, feedback, 999999, 0, false, true, true, false, false, false, false, false, 4, 0, 0, 0);

    private static string DdExpectBlocked(Action action)
    {
        try { action(); }
        catch (InvalidOperationException exception) { return exception.Message; }
        throw new Exception("Unsafe DD motion was allowed.");
    }
}

public class DdRecordingCardProxy : DispatchProxy
{
    public Dictionary<int, MotionAxisSnapshot> States { get; } = new();
    public List<string> Commands { get; } = new();
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        if (method!.Name == nameof(IMotionCard.ReadAxis)) return States[(int)args![0]!];
        Commands.Add(method.Name);
        return null;
    }
}
