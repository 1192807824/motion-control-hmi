namespace ControlHub.Services.Motion;

public static class ProductionZStartInterlock
{
    public const double SafetyTolerancePulses = 100d;

    public static void EnsureSafe(IReadOnlyDictionary<int, double> safePositions, Func<int, MotionAxisSnapshot> readAxis)
    {
        var failures = new List<string>();
        foreach (var (axis, label) in new[] { (5, "第一套Z1"), (7, "第一套Z2"), (9, "第二套Z1"), (11, "第二套Z2") })
        {
            if (!safePositions.TryGetValue(axis, out var safe) || !double.IsFinite(safe))
            {
                failures.Add($"{label}（轴{axis}）：安全高度未配置或无效。");
                continue;
            }

            try
            {
                var state = readAxis(axis);
                if (state.HardwareAxisNo != axis || !double.IsFinite(state.FeedbackPosition))
                    failures.Add($"{label}（轴{axis}）：实时位置无效。");
                else if (state.FeedbackPosition > safe + SafetyTolerancePulses)
                    failures.Add($"{label}（轴{axis}）：实时位置 {state.FeedbackPosition:G9} pulse，" +
                        $"安全高度 {safe:G9} pulse，允许上限 {safe + SafetyTolerancePulses:G9} pulse。");
            }
            catch (Exception exception)
            {
                failures.Add($"{label}（轴{axis}）：无法读取实时位置（{exception.Message}）。");
            }
        }

        if (failures.Count > 0)
            throw new InvalidOperationException("禁止开始运行：四个Z轴的实时位置必须均≤各自安全高度+100 pulse。\n\n" +
                string.Join("\n", failures) + "\n\n请先将Z轴抬到安全范围，再重新开始。");
    }
}
