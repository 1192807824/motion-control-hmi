namespace ControlHub.Services.Motion;

public sealed record NinePointMotionRequest(
    int XHardwareAxisNo,
    int YHardwareAxisNo,
    double CenterX,
    double CenterY,
    double StepX,
    double StepY,
    NinePointMovePriority MovePriority,
    double Velocity,
    double PositionTolerance,
    int MoveTimeoutMilliseconds,
    int SettleMilliseconds);

public enum NinePointMovePriority
{
    XFirst,
    YFirst
}

public sealed record CalibrationCenterPosition(
    int XHardwareAxisNo,
    int YHardwareAxisNo,
    double ActualX,
    double ActualY);

public sealed record NinePointMotionPosition(
    int Index,
    double TargetX,
    double TargetY,
    double ActualX,
    double ActualY);

public sealed record NinePointMotionProgress(
    int CompletedPoints,
    int TotalPoints,
    string Message);
