namespace ControlHub.Services.Motion;

public sealed record MotionCardConnectionInfo(
    ushort CardNo,
    int DetectedCardCount,
    int AxisCount,
    int DigitalInputCount,
    int DigitalOutputCount,
    int AnalogInputCount,
    int AnalogOutputCount,
    bool IsSimulation);

public sealed record MotionAxisSnapshot(
    int HardwareAxisNo,
    double CommandPosition,
    double FeedbackPosition,
    double TargetPosition,
    double Speed,
    bool IsMoving,
    bool ServoEnabled,
    bool Homed,
    bool Alarm,
    bool PositiveLimit,
    bool NegativeLimit,
    bool EmergencyInput,
    bool OriginInput,
    ushort StateMachine,
    ushort RunMode,
    ushort AxisErrorCode,
    int StopReason)
{
    public string StateText
    {
        get
        {
            if (Alarm)
            {
                return AxisErrorCode == 0
                    ? "轴报警"
                    : $"轴报警 0x{AxisErrorCode:X4}";
            }

            if (EmergencyInput)
            {
                return "急停输入有效";
            }

            if (PositiveLimit)
            {
                return "正限位有效";
            }

            if (NegativeLimit)
            {
                return "负限位有效";
            }

            if (!ServoEnabled)
            {
                return $"未使能（状态机 {StateMachine}）";
            }

            if (IsMoving)
            {
                return RunMode switch
                {
                    1 => "定长运动中",
                    2 => "连续运动中",
                    3 => "回零中",
                    _ => $"运动中（模式 {RunMode}）"
                };
            }

            return Homed ? "已回零，待机" : "已使能，未回零";
        }
    }
}

public sealed class MotionCardException : Exception
{
    public MotionCardException(
        string message,
        string? operation = null,
        int? nativeErrorCode = null,
        ushort? busErrorCode = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
        NativeErrorCode = nativeErrorCode;
        BusErrorCode = busErrorCode;
    }

    public string? Operation { get; }

    public int? NativeErrorCode { get; }

    public ushort? BusErrorCode { get; }
}
