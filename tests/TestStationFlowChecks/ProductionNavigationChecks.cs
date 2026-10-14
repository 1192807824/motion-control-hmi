using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ControlHub.Services.Persistence;
using ControlHub.Views;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckProductionNavigationAndPause()
    {
        // Construct controls without showing the window: no Loaded handlers/device connections.
        var window = new MainWindow();
        T WindowField<T>(string name) => (T)typeof(MainWindow).GetField(name, Private)!.GetValue(window)!;
        WindowField<DispatcherTimer>("_clockTimer").Stop();
        var home = WindowField<HomePage>("HomeContent");
        var vision = WindowField<VisualCalibrationPage>("VisualCalibrationContent");
        var root = Path.Combine(Path.GetTempPath(), "production-navigation-" + Guid.NewGuid().ToString("N"));
        Set(home, "_homeSettingsStore", new HomePageSettingsStore(Path.Combine(root, "settings.json")));
        Set(home, "_batchStore", new BatchMeasurementStore(root));
        AlarmHistory.Initialize(new AlarmHistoryStore(Path.Combine(root, "alarms.db")));
        var cancellation = new CancellationTokenSource();
        var completion = new TaskCompletionSource<bool>();
        Set(home, "_productionCancellation", cancellation);
        Set(home, "_productionCompletion", completion);
        Set(home, "_startSequenceRunning", true);
        Set(home, "_testRetryCount", 3);
        var button = (Button)home.FindName("StartProductionButton");
        var retry = (TextBox)home.FindName("TestRetryCountTextBox");
        Invoke(home, "UpdateHomeCommandState");
        Require(button.IsEnabled && !retry.IsEnabled, "Running flow must allow pause and freeze parameter editing.");
        Require(!vision.IsEnabled, "Production must prevent calibration procedure changes.");

        foreach (var paused in new[] { false, true })
        {
            if (paused) Require((bool)Invoke(home, "RequestProductionPause")!, "Pause was rejected.");
            foreach (var menu in new[] { "ParameterSettingsMenu_Click", "MotionMenu_Click", "ConnectionMenu_Click", "VisualCalibrationMenu_Click" })
            {
                typeof(MainWindow).GetMethod(menu, Private)!.Invoke(window, [window, new RoutedEventArgs()]);
                Require(home.IsProductionActive && !cancellation.IsCancellationRequested && !completion.Task.IsCompleted &&
                    !(bool)typeof(HomePage).GetField("_productionStopRequested", Private)!.GetValue(home)!,
                    $"Menu {menu} stopped or replaced the active flow.");
                Require((bool)typeof(HomePage).GetField("_productionPauseRequested", Private)!.GetValue(home)! == paused,
                    "Navigation changed the pause state.");
            }
            typeof(MainWindow).GetMethod("ShowHomePage", Private)!.Invoke(window, null);
        }
        Require(retry.IsEnabled && !((TextBox)home.FindName("BatchNumberTextBox")).IsEnabled,
            "Paused parameters should be editable without allowing a mid-flow batch switch.");
        var signal = (TaskCompletionSource<bool>)typeof(HomePage).GetField("_productionResumeSignal", Private)!.GetValue(home)!;
        retry.Text = "invalid";
        Require(button.IsEnabled, "Editing parameters disabled resume for the already-running task.");
        retry.Text = "4";
        Require((int)typeof(HomePage).GetField("_testRetryCount", Private)!.GetValue(home)! == 3,
            "Paused edits mutated the current production snapshot.");
        Invoke(home, "StartProduction_Click", button, new RoutedEventArgs());
        Require(signal.Task.IsCompletedSuccessfully && !cancellation.IsCancellationRequested && home.IsProductionActive &&
            !retry.IsEnabled, "Resume did not release the existing flow and freeze editors again.");
        Invoke(home, "StartProduction_Click", button, new RoutedEventArgs());
        Require((bool)typeof(HomePage).GetField("_productionPauseRequested", Private)!.GetValue(home)!, "Second pause failed.");
        Invoke(home, "MotionController_EmergencyStopIssued", window, EventArgs.Empty);
        Require(cancellation.IsCancellationRequested && !button.IsEnabled && !(bool)Invoke(home, "ResumeProduction")!,
            "Emergency-stopped flow must not be resumable.");
        Set(home, "_startSequenceRunning", false);
        Set(home, "_productionStopRequested", false);
        retry.Text = "invalid";
        Invoke(home, "UpdateHomeCommandState");
        Require(!button.IsEnabled && vision.IsEnabled, "New starts must retain validation; calibration must unlock after production.");
        cancellation.Dispose();
        Console.WriteLine("PASS: running/paused menu navigation, camera procedure protection, edit/resume snapshot, repeated pause and emergency-stop rejection. Window never shown; no hardware opened.");
    }
}
