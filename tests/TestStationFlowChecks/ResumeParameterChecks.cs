using System.IO;
using System.Reflection;
using System.Windows.Controls;
using System.Windows.Threading;
using ControlHub.Services.Devices;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.Services.Vision;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckResumeParameterApplication()
    {
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
        var home = new HomePage();
        var root = Path.Combine(Path.GetTempPath(), "resume-parameters-" + Guid.NewGuid().ToString("N"));
        Set(home, "_homeSettingsStore", new HomePageSettingsStore(Path.Combine(root, "home.json")));
        home.ApplyRecipeSettings(CreateResumeTestSettings());
        var card = DispatchProxy.Create<IMotionCard, ResetRecordingCard>();
        var recorder = (ResetRecordingCard)card;
        var vm = new MainWindowViewModel(new Dictionary<int, AxisSettings>());
        vm.PopulateMotionAxes(16);
        foreach (var axis in vm.Axes) { axis.IsAvailable = true; axis.StatusReadHealthy = true; axis.JogSpeed = 1000; }
        var motion = new MotionControlPage { DataContext = vm };
        typeof(MotionControlPage).GetField("_motionCard", Private)!.SetValue(motion, card);
        typeof(MotionControlPage).GetField("_motionOptions", Private)!.SetValue(motion, new MotionCardOptions { SimulationMode = true });
        home.AttachMotionController(motion);
        Invoke(home, "ApplyPausedProductionParameters");
        Set(home, "_startSequenceRunning", true);
        Require((bool)Invoke(home, "RequestProductionPause")!, "Could not pause test flow.");
        object Field(string name) => typeof(HomePage).GetField(name, Private)!.GetValue(home)!;
        TextBox Input(string name) => (TextBox)home.FindName(name);
        var oldAxes = Field("_productionAxisMotionSettings");
        var editors = (System.Collections.IDictionary)Field("_productionAxisMotionEditors");
        ((TextBox)editors[5]!.GetType().GetProperty("RunVelocity")!.GetValue(editors[5])!).Text = "2345";
        Input("FirstSetNozzle1PickupZPositionTextBox").Text = "-250";
        Input("FirstSetNozzle1SafeZPositionTextBox").Text = "-4000";
        Input("VacuumPickupDwellTextBox").Text = "125";
        Input("E4981ARetryLowerTextBox").Text = "2";
        Input("E4981ARetryUpperTextBox").Text = "3";
        Input("TestRetryCountTextBox").Text = "4";
        Input("SecondSetPosition1XTextBox").Text = "invalid";
        var signal = (TaskCompletionSource<bool>)Field("_productionResumeSignal");
        Require(!(bool)Invoke(home, "ResumeProduction")! && !signal.Task.IsCompleted &&
            ReferenceEquals(oldAxes, Field("_productionAxisMotionSettings")), "Invalid late field partially applied parameters or released pause.");
        Input("SecondSetPosition1XTextBox").Text = "123";
        Require((bool)Invoke(home, "ResumeProduction")! && signal.Task.IsCompletedSuccessfully,
            "Corrected parameters did not resume the same task.");
        Require((double)Property(Invoke(home, "GetProductionAxisMotionSettings", 5)!, "RunVelocity")! == 2345 &&
            (int)Field("_testRetryCount") == 4, "Axis velocity/retry snapshot did not update.");
        var z = Property(Invoke(home, "GetProductionZPositions")!, "FirstSetNozzle1")!;
        Require((double)Property(z, "Pickup")! == -250 && (double)Property(z, "Safe")! == -4000 &&
            (int)Property(Invoke(home, "GetProductionZDwellTimes")!, "PickupMilliseconds")! == 125 &&
            (double)Property(Invoke(home, "GetSecondSetXyPositions")!, "Position1X")! == 123, "Z/XY/dwell remained stale.");
        var sample = Invoke(null, "ClassifyE4981AMeasurement", new E4981AMeasurementResult(0, 1e-9, .01, 2, ""))!;
        Require(!(bool)Invoke(null, "IsTestStationMeasurementComplete", sample, Field("_e4981ARetryRange"))!,
            "Next measurement did not use the edited capacitance range.");
        var move = (Task)Invoke(home, "MoveNozzleZToAsync", VisionCalibrationAxisSet.First, 1,
            (double)Property(z, "Pickup")!, "test", CancellationToken.None, null)!;
        if (!move.IsCompleted)
        {
            var frame = new DispatcherFrame();
            var dispatcher = Dispatcher.CurrentDispatcher;
            _ = move.ContinueWith(_ => dispatcher.BeginInvoke(() => frame.Continue = false), TaskScheduler.Default);
            Dispatcher.PushFrame(frame);
        }
        move.GetAwaiter().GetResult();
        Require(recorder.Moves.Single() == (5, -250d) && recorder.MoveVelocities.Single() == 2345,
            "Actual next simulated card command used stale target or velocity.");
        var originalRange = CreateNested("SM7110AcceptanceRange", 1e9, 9e9, "R");
        var measured = Invoke(null, "ClassifySM7110Measurement", new SM7110MeasurementResult(2e9, 0, "R", ""), "BIN2", originalRange)!;
        Set(home, "_sm7110AcceptanceRange", CreateNested("SM7110AcceptanceRange", 3e9, 9e9, "R"));
        Require(ReferenceEquals(Property(measured, "AppliedSM7110Range"), originalRange),
            "Finished measurement lost its original criterion when resume changed the live range.");
        Set(home, "_startSequenceRunning", false);
        Console.WriteLine("PASS: invalid drafts retain pause atomically; resume updates Z/XY/velocity/dwell/retries/range; next simulated move uses new settings; completed readings retain original limits.");
    }
}
