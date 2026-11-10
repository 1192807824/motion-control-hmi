using System.Collections;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.Services.Vision;
using ControlHub.ViewModels;
using ControlHub.Views.Pages;

internal static partial class Program
{
    private static void CheckParameterPositionActions(string directory)
    {
        var confirmation = new PositionConfirmation();
        var home = (HomePage)Activator.CreateInstance(typeof(HomePage), PrivateInstance, null,
            new object[] { new Func<string, string, bool>(confirmation.Confirm) }, null)!;
        var path = Path.Combine(directory, "position-actions.json");
        var store = new HomePageSettingsStore(path);
        SetPositionField(home, "_homeSettingsStore", store);
        home.ApplyRecipeSettings(new HomePageSettings());

        var settings = new VisualCalibrationSettings
        {
            ActiveAxisSet = "Second", // First-set buttons must not follow the active calibration tab.
            VelocityPulsesPerSecond = 12345,
            SecondVelocityPulsesPerSecond = 23456,
            LowerCameraVelocityPulsesPerSecond = 34567
        };
        var calibration = (VisionCalibrationService)RuntimeHelpers.GetUninitializedObject(typeof(VisionCalibrationService));
        typeof(VisionCalibrationService).GetField("<Settings>k__BackingField", PrivateInstance)!.SetValue(calibration, settings);
        SetPositionField(home, "_visionCalibration", calibration);

        var card = DispatchProxy.Create<IMotionCard, PositionRecordingCard>();
        var recorder = (PositionRecordingCard)card;
        var viewModel = new MainWindowViewModel(new Dictionary<int, AxisSettings>());
        viewModel.PopulateMotionAxes(16);
        foreach (var axis in viewModel.Axes)
        {
            axis.IsAvailable = true;
            axis.StatusReadHealthy = true;
        }
        var motion = new MotionControlPage { DataContext = viewModel };
        typeof(MotionControlPage).GetField("_motionCard", PrivateInstance)!.SetValue(motion, card);
        typeof(MotionControlPage).GetField("_motionOptions", PrivateInstance)!.SetValue(motion,
            new MotionCardOptions { SimulationMode = true });
        SetPositionField(home, "_motionController", motion);

        var cases = new (string Name, string SavedName, int XAxis, double Velocity)[]
        {
            ("FirstSetTeachingCenter", "FirstSetTeachingCenter", 1, 12345),
            ("FirstSetTeachingPressPosition", "FirstSetTeachingPressPosition", 1, 12345),
            ("LowerCameraPhotoPosition1", "LowerCameraPhotoPosition1", 1, 34567),
            ("LowerCameraPhotoPosition2", "LowerCameraPhotoPosition2", 1, 34567),
            ("PresetPosition1", "PresetPosition1", 1, 12345),
            ("PresetPosition2", "PresetPosition2", 1, 12345),
            ("SecondSetPosition1", "SecondSetPickupPosition1", 3, 23456),
            ("SecondSetPosition2", "SecondSetPickupPosition2", 3, 23456)
        };

        void Click(string name)
        {
            ((Button)home.FindName(name + "Button")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!(bool)typeof(HomePage).GetField("_presetPositionMoveRunning", PrivateInstance)!.GetValue(home)!,
                "Instant fake motion must complete before assertions.");
        }
        void Edit(string name, string x, string y)
        {
            SetPositionField(home, "_loadingPresetPositions", true);
            ((TextBox)home.FindName(name + "XTextBox")).Text = x;
            ((TextBox)home.FindName(name + "YTextBox")).Text = y;
            SetPositionField(home, "_loadingPresetPositions", false);
            typeof(HomePage).GetMethod("UpdateHomeCommandState", PrivateInstance)!.Invoke(home, null);
        }

        foreach (var item in cases)
        {
            foreach (var existing in new[] { false, true })
            {
                Edit(item.Name, existing ? "1000" : "", existing ? "2000" : "");
                var before = File.ReadAllText(path);
                var reads = recorder.Reads;
                var confirmations = confirmation.Count;
                confirmation.Approve = false;
                Click("Record" + item.Name);
                Require(confirmation.Count == confirmations + 1 && recorder.Reads == reads,
                    $"{item.Name}: initial and overwrite records must confirm before reading feedback.");
                Require(File.ReadAllText(path) == before &&
                        ((TextBox)home.FindName(item.Name + "XTextBox")).Text == (existing ? "1000" : ""),
                    $"{item.Name}: cancelled recording changed coordinates or disk.");
            }

            confirmation.Approve = true;
            recorder.Positions[item.XAxis] = 4567;
            recorder.Positions[item.XAxis + 1] = 5678;
            Click("Record" + item.Name);
            var saved = store.Load();
            Require((double?)typeof(HomePageSettings).GetProperty(item.SavedName + "X")!.GetValue(saved) == 4567 &&
                    (double?)typeof(HomePageSettings).GetProperty(item.SavedName + "Y")!.GetValue(saved) == 5678,
                $"{item.Name}: approved recording used the wrong axes or failed to persist.");

            Edit(item.Name, "1000", "2000");
            var commands = recorder.Moves.Count;
            var prompts = confirmation.Count;
            confirmation.Approve = false;
            Click("Move" + item.Name);
            Require(confirmation.Count == prompts + 1 && recorder.Moves.Count == commands,
                $"{item.Name}: cancelled movement reached the card or skipped confirmation.");
            Require(confirmation.LastMessage.Contains("X=1000") && confirmation.LastMessage.Contains("Y=2000") &&
                    confirmation.LastMessage.Contains(item.Velocity.ToString(CultureInfo.CurrentCulture)),
                $"{item.Name}: confirmation must show the target and matching calibration speed.");

            // An invalid production speed must neither disable nor supply these manual moves.
            var editors = (IDictionary)typeof(HomePage).GetField("_productionAxisMotionEditors", PrivateInstance)!.GetValue(home)!;
            var editor = editors[0]!;
            ((TextBox)editor.GetType().GetProperty("RunVelocity")!.GetValue(editor)!).Text = "invalid";
            Require(((Button)home.FindName("Move" + item.Name + "Button")).IsEnabled,
                $"{item.Name}: production speed still gates manual movement.");
            foreach (var interpolation in new[] { false, true })
            {
                ((CheckBox)home.FindName("XyLinearInterpolationCheckBox")).IsChecked = interpolation;
                recorder.Positions[item.XAxis] = recorder.Positions[item.XAxis + 1] = 0;
                recorder.Moves.Clear();
                confirmation.Approve = true;
                Click("Move" + item.Name);
                Require(recorder.Moves.Count == 2 &&
                        recorder.Moves[0] == (item.XAxis, 1000d, item.Velocity) &&
                        recorder.Moves[1] == (item.XAxis + 1, 2000d, item.Velocity) &&
                        recorder.LastInterpolated == interpolation,
                    $"{item.Name}: wrong axes, coordinates or calibration speed (interpolation={interpolation}).");
            }
        }
        Console.WriteLine("PASS: all 16 actual buttons confirm; cancellation preserves coordinates/files and issues no motion; approved records persist correct axes; both move modes use matching calibration speeds independently of production settings and the active calibration tab.");

        foreach (var invalid in new[] { 0d, -1d, double.NaN, double.PositiveInfinity })
        {
            settings.VelocityPulsesPerSecond = invalid;
            settings.SecondVelocityPulsesPerSecond = invalid;
            settings.LowerCameraVelocityPulsesPerSecond = invalid;
            foreach (var item in cases)
            {
                recorder.Moves.Clear();
                Click("Move" + item.Name);
                Require(recorder.Moves.Count == 0, $"{item.Name}: invalid calibration speed reached the card.");
            }
        }
        Console.WriteLine("PASS: zero, negative and nonfinite calibration speeds reject all manual moves. No hardware connected.");
    }

    private static void SetPositionField(HomePage page, string name, object value) =>
        typeof(HomePage).GetField(name, PrivateInstance)!.SetValue(page, value);

    private sealed class PositionConfirmation
    {
        public bool Approve { get; set; }
        public int Count { get; private set; }
        public string LastMessage { get; private set; } = "";
        public bool Confirm(string message, string title)
        {
            Count++;
            LastMessage = message;
            return Approve;
        }
    }
}

public class PositionRecordingCard : DispatchProxy
{
    public Dictionary<int, double> Positions { get; } = [];
    public List<(int Axis, double Target, double Velocity)> Moves { get; } = [];
    public int Reads { get; private set; }
    public bool LastInterpolated { get; private set; }

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        switch (method!.Name)
        {
            case "get_IsOpen": return true;
            case "get_AxisCount": return 16;
            case nameof(IMotionCard.ReadAxis):
                Reads++;
                var axis = (int)args![0]!;
                var position = Positions.GetValueOrDefault(axis);
                return new MotionAxisSnapshot(axis, position, position, position, 0,
                    false, true, true, false, false, false, false, false, 4, 0, 0, 0);
            case nameof(IMotionCard.MoveAbsolute):
                LastInterpolated = false;
                Record((int)args![0]!, (double)args[1]!, (double)args[2]!);
                return null;
            case nameof(IMotionCard.MoveLinearAbsolute):
                LastInterpolated = true;
                var axes = (IReadOnlyList<int>)args![1]!;
                var targets = (IReadOnlyList<double>)args[2]!;
                var velocities = (IReadOnlyList<double>)args[3]!;
                for (var i = 0; i < axes.Count; i++) Record(axes[i], targets[i], velocities[i]);
                return null;
            default: throw new InvalidOperationException($"Unexpected card call: {method.Name}");
        }
    }

    private void Record(int axis, double target, double velocity)
    {
        Moves.Add((axis, target, velocity));
        Positions[axis] = target;
    }
}
