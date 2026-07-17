namespace ControlHub.Services.Motion;

public interface IMotionCard : IDisposable
{
    bool IsOpen { get; }

    int AxisCount { get; }

    int DigitalInputCount { get; }

    int DigitalOutputCount { get; }

    int AnalogInputCount { get; }

    int AnalogOutputCount { get; }

    MotionCardConnectionInfo Open();

    void Close();

    ushort ReadBusErrorCode();

    MotionAxisSnapshot ReadAxis(int hardwareAxisNo);

    uint ReadDigitalInputs(int portNo);

    uint ReadDigitalOutputs(int portNo);

    void WriteDigitalOutput(int bitNo, bool enabled);

    double ReadAnalogInput(int channel);

    double ReadAnalogOutput(int channel);

    void WriteAnalogOutput(int channel, double value);

    void ServoOn(int hardwareAxisNo, bool enabled);

    void SetAllServos(bool enabled);

    void Home(int hardwareAxisNo);

    void Home(int hardwareAxisNo, MotionHomeProfile profile);

    void Jog(int hardwareAxisNo, double velocity);

    void MoveRelative(int hardwareAxisNo, double distance, double velocity);

    void MoveRelativeSynchronized(
        IReadOnlyList<int> hardwareAxisNos,
        IReadOnlyList<double> distances,
        IReadOnlyList<double> velocities);

    void MoveAbsolute(int hardwareAxisNo, double position, double velocity);

    void Stop(int hardwareAxisNo, bool emergency = false);

    void EmergencyStop();

    void ClearAlarms(IEnumerable<int> hardwareAxisNumbers);
}
