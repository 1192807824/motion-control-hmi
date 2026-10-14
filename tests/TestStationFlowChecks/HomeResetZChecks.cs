using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows.Controls;
using System.Windows.Threading;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckHomeResetZParking()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        AlarmHistory.Initialize(new AlarmHistoryStore(Path.Combine(Path.GetTempPath(),
            "home-reset-checks-" + Guid.NewGuid().ToString("N"), "alarms.db")));
        var card = DispatchProxy.Create<IMotionCard, ResetRecordingCard>();
        var recorder = (ResetRecordingCard)card;
        var vm = new MainWindowViewModel(new Dictionary<int, AxisSettings>());
        vm.PopulateMotionAxes(16);
        foreach (var axis in vm.Axes)
        {
            axis.IsAvailable = true;
            axis.StatusReadHealthy = true;
            axis.JogSpeed = 10_000;
        }
        var motion = new MotionControlPage { DataContext = vm };
        typeof(MotionControlPage).GetField("_motionCard", Private)!.SetValue(motion, card);
        typeof(MotionControlPage).GetField("_motionOptions", Private)!.SetValue(motion,
            new MotionCardOptions { SimulationMode = true });
        var home = (HomePage)RuntimeHelpers.GetUninitializedObject(typeof(HomePage));
        Set(home, "OneKeyResetHintText", new TextBlock());
        Task Run() => (Task)Invoke(home, "RunHomeOneKeyResetAsync", motion, CancellationToken.None)!;
        void Wait(Task task)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            task.GetAwaiter().GetResult();
        }

        // All axes start homed at zero. Completion must still wait for the four final moves.
        recorder.HoldMoves = true;
        var pending = Run();
        Require(!pending.IsCompleted, "Reset completed before Z feedback reached the targets.");
        Require(recorder.Moves.SequenceEqual(new[] { (5, -4000d), (7, -4000d), (9, -4000d), (11, -4000d) }),
            "Reset must issue absolute -4000 only to Z axes 5/7/9/11.");
        recorder.HoldMoves = false;
        Wait(pending);
        Require(new[] { 5, 7, 9, 11 }.All(axis => card.ReadAxis(axis).FeedbackPosition == -4000), "Z target feedback incorrect.");

        // A second reset must re-home Z before parking again, despite historical Homed=true.
        recorder.Trace.Clear();
        recorder.Positions[0] = 1000; // DD must finish before any final Z move.
        Wait(Run());
        Require(recorder.Trace.Take(5).SequenceEqual(new[] { "home:5", "home:7", "home:9", "home:11", "home:0" }) &&
                recorder.Trace.Skip(5).SequenceEqual(new[] { "move:5", "move:7", "move:9", "move:11" }),
            "Final Z moves started before the full reset sequence completed.");
        Console.WriteLine("PASS: four absolute Z targets, wait for arrival, repeated reset and DD-before-Z ordering.");

        recorder.Moves.Clear();
        recorder.BlockReset = true;
        try { Wait(Run()); throw new Exception("Invalid reset was accepted."); }
        catch (MotionCardException) { }
        Require(recorder.Moves.Count == 0, "Failed homing must not start final Z moves.");
        recorder.BlockReset = false;

        recorder.FailMove = true;
        try { Wait(Run()); throw new Exception("Failed Z positioning was reported as complete."); }
        catch (MotionCardException) { }
        Require(recorder.Stops.Contains(5) && recorder.Stops.Contains(11), "Failed positioning did not stop commanded axes.");
        Console.WriteLine("PASS: homing rejection prevents parking; Z positioning failure propagates and stops axes. No hardware opened.");
    }
}

public class ResetRecordingCard : DispatchProxy
{
    public readonly Dictionary<int, double> Positions = [];
    public readonly List<(int Axis, double Target)> Moves = [];
    public readonly List<double> MoveVelocities = [];
    public readonly List<string> Trace = [];
    public readonly HashSet<int> Stops = [];
    private readonly HashSet<int> _homePending = [];
    private readonly HashSet<int> _moving = [];
    public bool HoldMoves;
    public bool BlockReset;
    public bool FailMove;

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_IsOpen": return true;
            case "get_AxisCount": return 16;
            case nameof(IMotionCard.ReadAxis):
                var axis = (int)args![0]!;
                var homing = _homePending.Remove(axis);
                var moving = _moving.Contains(axis);
                var position = Positions.GetValueOrDefault(axis);
                var snapshot = new MotionAxisSnapshot(axis, position, position, position, 0,
                    homing || (moving && HoldMoves), true, !homing, BlockReset && axis == 0,
                    false, false, false, false, 4, 0, 0, (ushort)(FailMove && moving && axis == 9 ? 1 : 0));
                if (moving && !HoldMoves) _moving.Remove(axis);
                return snapshot;
            case nameof(IMotionCard.Home):
                var homeAxis = (int)args![0]!;
                Trace.Add($"home:{homeAxis}");
                Positions[homeAxis] = ((MotionHomeProfile)args[1]!).OffsetPosition;
                _homePending.Add(homeAxis);
                return null;
            case nameof(IMotionCard.MoveAbsolute):
                var moveAxis = (int)args![0]!;
                var target = (double)args[1]!;
                Moves.Add((moveAxis, target));
                MoveVelocities.Add((double)args[2]!);
                Trace.Add($"move:{moveAxis}");
                Positions[moveAxis] = target;
                _moving.Add(moveAxis);
                return null;
            case nameof(IMotionCard.Stop):
                Stops.Add((int)args![0]!);
                return null;
            default:
                return method.ReturnType == typeof(void) ? null :
                    method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }
}
