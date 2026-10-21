using ControlHub.Services.Persistence;
using ControlHub.Services.Devices;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private Action<SecondSetXyPositions>? _applyResumedPlacementPositions;

    private void ApplyPausedProductionParameters()
    {
        // Parse every draft before replacing any active snapshot or releasing a waiter.
        var motion = _motionController ?? throw new InvalidOperationException("运动控制组件未连接。");
        var axes = ReadProductionAxisMotionSettings();
        var positions = ReadProductionZPositions();
        var dwell = ReadProductionZDwellTimesForPositions(positions);
        var secondXy = ReadSecondSetXyPositions();
        var bins = ReadBinDropPositions();
        var firstXy = new SecondSetXyPositions(
            ParseFiniteCoordinate(PresetPosition1XTextBox.Text, "位置1 X"),
            ParseFiniteCoordinate(PresetPosition1YTextBox.Text, "位置1 Y"),
            ParseFiniteCoordinate(PresetPosition2XTextBox.Text, "位置2 X"),
            ParseFiniteCoordinate(PresetPosition2YTextBox.Text, "位置2 Y"));
        var stations = ReadTestStationSettings();
        if (_testStationSettings is { } previous && stations.Any(pair =>
                !previous.TryGetValue(pair.Key, out var old) ||
                old.Enabled != pair.Value.Enabled || old.Instrument != pair.Value.Instrument))
            throw new InvalidOperationException("暂停中不能改变测试工位启用状态或仪表分配，请结束流程后修改。");
        var testDwell = ReadTestStationDwellMilliseconds();
        var retries = ReadTestRetryCount();
        var capacitanceRange = ReadE4981ARetryRange();
        var instruments = stations.Values.Where(value => value.Enabled == true)
            .Select(value => value.Instrument ?? TestStationInstrument.None).Distinct().ToArray();
        var resistanceRange = instruments.Contains(TestStationInstrument.SM7110) ? ReadSM7110AcceptanceRange() : null;
        if (instruments.Length > 0)
        {
            var connection = _connectionConfigController ?? throw new InvalidOperationException("仪表控制组件未连接。");
            connection.ValidateProductionInstrumentParameters(instruments);
            resistanceRange?.ValidateMeasurementMode(connection.SM7110MeasurementMode);
        }

        // ConfigureAxisMoveParameters only updates profiles used by future commands.
        // Commands already issued to the card retain their current trajectory.
        ApplyProductionAxisMotionSettings(motion, axes);
        _productionAxisMotionSettings = axes;
        _productionZPositions = positions;
        _productionZDwellTimes = dwell;
        _secondSetXyPositions = secondXy;
        _binDropPositions = bins;
        _testStationSettings = stations;
        _testStationDwellMilliseconds = testDwell;
        _testRetryCount = retries;
        _e4981ARetryRange = capacitanceRange;
        _sm7110AcceptanceRange = resistanceRange;
        _applyResumedPlacementPositions?.Invoke(firstXy);
    }
}
