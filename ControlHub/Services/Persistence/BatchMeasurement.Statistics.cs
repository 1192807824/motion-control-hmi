using ControlHub.Services.Devices;

namespace ControlHub.Services.Persistence;

public sealed partial record BatchMeasurement
{
    // Derive this for old records too; retain the original reading and wire response.
    public bool IsUnmeasured => Instrument == "E4981A" &&
        (!ValidReading || InstrumentStatus != 0 ||
         !Value.HasValue || !E4981AMeasurementResult.IsUsableValue(Value.Value / 1e9) ||
         DissipationFactor is double loss && !E4981AMeasurementResult.IsUsableValue(loss));

    public bool CountsAsOk => !IsUnmeasured && StationPassed;
    public bool CountsAsNg => !IsUnmeasured && !StationPassed;
    public bool HasDistributionSample => !IsUnmeasured && ValidReading &&
        Value.HasValue && double.IsFinite(Value.Value);
}
