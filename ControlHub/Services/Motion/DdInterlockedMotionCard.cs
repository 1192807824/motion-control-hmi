namespace ControlHub.Services.Motion;

/// <summary>在每个DD运动指令下发前检查测试站，覆盖点动、定位、联动及回原；停止指令始终放行。</summary>
public sealed class DdInterlockedMotionCard(IMotionCard inner, Action ensureDdCanStart) : IMotionCard
{
    private void Check(int axis) { if (axis == 0) ensureDdCanStart(); }
    private void Check(IReadOnlyList<int> axes) { if (axes.Contains(0)) ensureDdCanStart(); }

    public void Home(int hardwareAxisNo) { Check(hardwareAxisNo); inner.Home(hardwareAxisNo); }
    public void Home(int hardwareAxisNo, MotionHomeProfile profile) { Check(hardwareAxisNo); inner.Home(hardwareAxisNo, profile); }
    public void Jog(int hardwareAxisNo, double velocity) { Check(hardwareAxisNo); inner.Jog(hardwareAxisNo, velocity); }
    public void MoveRelative(int hardwareAxisNo, double distance, double velocity)
    { Check(hardwareAxisNo); inner.MoveRelative(hardwareAxisNo, distance, velocity); }
    public void MoveAbsolute(int hardwareAxisNo, double position, double velocity)
    { Check(hardwareAxisNo); inner.MoveAbsolute(hardwareAxisNo, position, velocity); }
    public void MoveRelativeSynchronized(IReadOnlyList<int> hardwareAxisNos, IReadOnlyList<double> distances, IReadOnlyList<double> velocities)
    { Check(hardwareAxisNos); inner.MoveRelativeSynchronized(hardwareAxisNos, distances, velocities); }
    public void MoveLinearAbsolute(int coordinateSystemNo, IReadOnlyList<int> hardwareAxisNos, IReadOnlyList<double> targetPositions, IReadOnlyList<double> maximumAxisVelocities)
    { Check(hardwareAxisNos); inner.MoveLinearAbsolute(coordinateSystemNo, hardwareAxisNos, targetPositions, maximumAxisVelocities); }

    public bool IsOpen => inner.IsOpen;
    public int AxisCount => inner.AxisCount;
    public int DigitalInputCount => inner.DigitalInputCount;
    public int DigitalOutputCount => inner.DigitalOutputCount;
    public int AnalogInputCount => inner.AnalogInputCount;
    public int AnalogOutputCount => inner.AnalogOutputCount;
    public MotionCardConnectionInfo Open() => inner.Open();
    public void Close() => inner.Close();
    public ushort ReadBusErrorCode() => inner.ReadBusErrorCode();
    public MotionAxisSnapshot ReadAxis(int hardwareAxisNo) => inner.ReadAxis(hardwareAxisNo);
    public int ReadActualTorque(int hardwareAxisNo) => inner.ReadActualTorque(hardwareAxisNo);
    public uint ReadDigitalInputs(int portNo) => inner.ReadDigitalInputs(portNo);
    public uint ReadDigitalOutputs(int portNo) => inner.ReadDigitalOutputs(portNo);
    public void WriteDigitalOutput(int bitNo, bool enabled) => inner.WriteDigitalOutput(bitNo, enabled);
    public double ReadAnalogInput(int channel) => inner.ReadAnalogInput(channel);
    public double ReadAnalogOutput(int channel) => inner.ReadAnalogOutput(channel);
    public void WriteAnalogOutput(int channel, double value) => inner.WriteAnalogOutput(channel, value);
    public void ServoOn(int hardwareAxisNo, bool enabled) => inner.ServoOn(hardwareAxisNo, enabled);
    public void SetAllServos(bool enabled) => inner.SetAllServos(enabled);
    public void StopLinearInterpolation(int coordinateSystemNo, bool emergency = false) => inner.StopLinearInterpolation(coordinateSystemNo, emergency);
    public void Stop(int hardwareAxisNo, bool emergency = false) => inner.Stop(hardwareAxisNo, emergency);
    public void EmergencyStop() => inner.EmergencyStop();
    public void ClearAlarms(IEnumerable<int> hardwareAxisNumbers) => inner.ClearAlarms(hardwareAxisNumbers);
    public void Dispose() => inner.Dispose();
}
