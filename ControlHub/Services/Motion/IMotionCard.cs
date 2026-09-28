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

    /// <summary>读取驱动器实际转矩反馈（0x6077），保留原始有符号数值，不换算压力单位。</summary>
    int ReadActualTorque(int hardwareAxisNo) => throw new NotSupportedException("当前控制卡不支持实际转矩读取。");

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

    void MoveLinearAbsolute(
        int coordinateSystemNo,
        IReadOnlyList<int> hardwareAxisNos,
        IReadOnlyList<double> targetPositions,
        IReadOnlyList<double> maximumAxisVelocities);

    void StopLinearInterpolation(int coordinateSystemNo, bool emergency = false);

    void MoveAbsolute(int hardwareAxisNo, double position, double velocity);

    void Stop(int hardwareAxisNo, bool emergency = false);

    void EmergencyStop();

    void ClearAlarms(IEnumerable<int> hardwareAxisNumbers);
}
