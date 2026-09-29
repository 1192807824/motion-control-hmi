namespace ControlHub.Models;

public sealed class AlarmInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTimeOffset OccurredAt { get; init; } = DateTimeOffset.Now;
    public string Source { get; init; } = "";
    public string Time { get; init; } = "";
    public string LocalTime => OccurredAt.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss.fff");
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string Level { get; init; } = "";
    public string Status { get; init; } = "";
}
