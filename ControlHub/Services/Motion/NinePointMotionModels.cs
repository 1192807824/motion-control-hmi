namespace ControlHub.Services.Motion;

public sealed record NinePointMotionRequest(
    int XHardwareAxisNo,
    int YHardwareAxisNo,
    double StepX,
    double StepY,
    double Velocity,
    double PositionTolerance,
    int MoveTimeoutMilliseconds,
    int SettleMilliseconds);

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
