using System.Reflection;
using System.Windows.Threading;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckArrivalSafetyBounds()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        (MotionControlPage Page, IMotionCard Card, ResetRecordingCard Recorder, MainWindowViewModel Vm) Create()
        {
            var card = DispatchProxy.Create<IMotionCard, ResetRecordingCard>();
            var vm = new MainWindowViewModel(new Dictionary<int, AxisSettings>());
            vm.PopulateMotionAxes(16);
            foreach (var axis in vm.Axes) { axis.IsAvailable = true; axis.StatusReadHealthy = true; axis.JogSpeed = 10000; }
            var page = new MotionControlPage { DataContext = vm };
            typeof(MotionControlPage).GetField("_motionCard", Private)!.SetValue(page, card);
            var options = new MotionCardOptions { SimulationMode = true };
            options.GetOrCreateMoveProfile(13).CompletionTolerance = 1000;
            typeof(MotionControlPage).GetField("_motionOptions", Private)!.SetValue(page, options);
            return (page, card, (ResetRecordingCard)card, vm);
        }
        T Finish<T>(Task<T> task)
        {
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                var dispatcher = Dispatcher.CurrentDispatcher;
                _ = task.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
                Dispatcher.PushFrame(frame);
            }
            return task.GetAwaiter().GetResult();
        }

        var (motion, card, recorder, _) = Create();
        recorder.Positions[13] = 125;
        var waits = new Dictionary<int, double> { [13] = 0, [14] = 0, [15] = 0 };
        var oldResult = Finish(motion.MoveAxesAbsoluteAsync(new Dictionary<int, double> { [13] = 0 },
            CancellationToken.None, minimumCompletionTolerance: 150));
        Require(oldResult.Single().FeedbackPosition == 125, "Reproduce general arrival allowing +125 feedback");
        var message = DdExpectBlocked(() => DdTestStationInterlock.EnsureSafe(waits, card.ReadAxis));
        Require(message.Split('\n')[0].Contains("轴13") && message.Split('\n')[0].Contains("125"),
            "DD alarm summary must show the actual failing axis and feedback immediately");

        recorder.MoveFeedbackOffset = 125;
        var returning = motion.MoveAxesAbsoluteAsync(new Dictionary<int, double> { [13] = 0 },
            CancellationToken.None, minimumCompletionTolerance: 150,
            completionUpperBounds: new Dictionary<int, double> { [13] = 100 });
        Require(!returning.IsCompleted && recorder.Moves.Count == 1,
            "+125 must issue the return command and keep waiting even when the profile tolerance is 1000");
        recorder.Positions[13] = 100;
        Require(Finish(returning).Single().FeedbackPosition == 100, "Return completes at the actual DD-safe boundary");
        DdTestStationInterlock.EnsureSafe(waits, card.ReadAxis);
        recorder.Positions[13] = -125;
        var safeSide = motion.MoveAxesAbsoluteAsync(new Dictionary<int, double> { [13] = 0 },
            CancellationToken.None, minimumCompletionTolerance: 150,
            completionUpperBounds: new Dictionary<int, double> { [13] = 100 });
        Require(safeSide.IsCompletedSuccessfully, "Safe-side feedback must not acquire a new artificial 100-pulse lower bound");
        DdTestStationInterlock.EnsureSafe(waits, card.ReadAxis);

        var (xy, _, xyRecorder, vm) = Create();
        xyRecorder.Positions[1] = 1655998;
        xyRecorder.Positions[2] = 946155;
        var actual = Finish(xy.MoveCalibrationAxesToAsync(1, 2, 1655920.114, 946038.684,
            10000, 150, 100, CancellationToken.None));
        Require(actual.ActualX == 1655998 && actual.ActualY == 946155,
            "User's first XY sample is within 150 and must pass when stopped");
        try
        {
            typeof(MotionControlPage).GetMethod("ReadSettledCalibrationPosition", Private)!.Invoke(xy,
                [vm.Axes.Single(axis => axis.HardwareAxisNo == 1), vm.Axes.Single(axis => axis.HardwareAxisNo == 2),
                 1655920.114, 946038.684, 100d]);
            throw new Exception("100-pulse calibration tolerance incorrectly accepted 116.316");
        }
        catch (TargetInvocationException exception) when (exception.InnerException is InvalidOperationException)
        {
            Require(exception.InnerException.Message.Contains("±100") && exception.InnerException.Message.Contains("116.316") &&
                exception.InnerException.Message.Contains("运动=False"), "Stability failure must disclose tolerance, error and moving flags");
        }
        xyRecorder.Positions[1] = 1634695;
        xyRecorder.Positions[2] = 1074220;
        xyRecorder.MoveFeedbackOffset = -151.593;
        try
        {
            Finish(xy.MoveCalibrationAxesToAsync(1, 2, 1634693.598, 1074371.593,
                10000, 150, 100, CancellationToken.None));
            throw new Exception("Outside-tolerance XY sample accepted");
        }
        catch (TimeoutException exception)
        {
            Require(exception.Message.Contains("±150") && exception.Message.Contains("-151.593") &&
                exception.Message.Contains("运动=False"), "Timeout must distinguish stopped feedback error from an axis still moving");
        }
        Console.WriteLine("PASS: DD +125 arrival mismatch, upper safety bound despite wide profiles, safe-side feedback, and both reported XY samples with actionable diagnostics. No hardware opened.");
    }
}
