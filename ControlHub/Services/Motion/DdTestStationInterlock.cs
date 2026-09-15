namespace ControlHub.Services.Motion;

public static class DdTestStationInterlock
{
    public const double WaitPositionTolerancePulses = 100d;

    public static void EnsureSafe(IReadOnlyDictionary<int, double> waitPositions, Func<int, MotionAxisSnapshot> readAxis)
    {
        var failures = new List<string>();
        // 物理避让检查始终覆盖全部测试轴，不能因测试站未启用而跳过。
        foreach (var axis in new[] { 13, 14, 15 })
        {
            var label = $"测试站{axis - 12:00}（轴{axis}）";
            if (!waitPositions.TryGetValue(axis, out var wait) || !double.IsFinite(wait))
            {
                failures.Add($"{label}：等待位未配置或无效。");
                continue;
            }
            try
            {
                var state = readAxis(axis);
                var exceedsWaitTolerance = Math.Abs(state.FeedbackPosition - wait) > WaitPositionTolerancePulses;
                if (state.HardwareAxisNo != axis || !double.IsFinite(state.FeedbackPosition))
                    failures.Add($"{label}：当前位置无效，等待位 {wait:G9} pulse。");
                // 反馈坐标必须位于等待位±100 pulse范围内，两个边界均包含。
                else if (exceedsWaitTolerance || state.IsMoving || state.Alarm || state.EmergencyInput)
                    failures.Add($"{label}：当前位置 {state.FeedbackPosition:G9} pulse，等待位 {wait:G9} pulse" +
                        (exceedsWaitTolerance ? $"；实时位置超出等待位±{WaitPositionTolerancePulses:G9} pulse允许范围" : "") +
                        (state.IsMoving ? "；轴仍在运动" : "") +
                        (state.Alarm || state.EmergencyInput ? "；轴报警或急停有效" : "") + "。");
            }
            catch (Exception exception)
            {
                failures.Add($"{label}：无法读取实时位置（{exception.Message}）。");
            }
        }
        if (failures.Count > 0)
            throw new InvalidOperationException($"禁止启动DD马达：三个测试站必须停稳，且实时位置均在各自等待位±{WaitPositionTolerancePulses:G9} pulse范围内。\n\n" +
                string.Join("\n", failures) + "\n\n请先将测试站移到等待位允许误差范围内并停稳，再重新启动。");
    }
}
