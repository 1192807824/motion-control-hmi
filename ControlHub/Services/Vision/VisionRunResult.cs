using ControlHub.Models;

namespace ControlHub.Services.Vision;

public sealed class VisionRunResult
{
    public bool IsOk { get; init; }

    public double RunTimeMs { get; init; }

    public IReadOnlyList<VisionOutputItem> Outputs { get; init; } = [];

    public string Message { get; init; } = "";
}
