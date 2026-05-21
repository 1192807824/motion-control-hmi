namespace ControlHub.Models;

public sealed class AlarmInfo
{
    public string Time { get; init; } = "";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string Level { get; init; } = "";
    public string Status { get; init; } = "";
}
