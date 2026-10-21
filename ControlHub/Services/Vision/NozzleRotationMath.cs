namespace ControlHub.Services.Vision;

internal static class NozzleRotationMath
{
    public static double NormalizePeriodicAngleDegrees(
        double angleDegrees,
        double periodDegrees)
    {
        if (!double.IsFinite(angleDegrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(angleDegrees),
                "角度必须是有限数值。");
        }

        if (!double.IsFinite(periodDegrees) || periodDegrees <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(periodDegrees),
                "角度周期必须是大于0的有限数值。");
        }

        var halfPeriod = periodDegrees / 2d;
        var normalized = angleDegrees % periodDegrees;
        if (normalized >= halfPeriod)
        {
            normalized -= periodDegrees;
        }
        else if (normalized < -halfPeriod)
        {
            normalized += periodDegrees;
        }

        // 避免状态文字和运动目标中出现 -0。
        return normalized == 0d ? 0d : normalized;
    }

    public static double CalculateShortestCorrectionDegrees(
        double measuredAngleDegrees,
        double targetAngleDegrees,
        double periodDegrees)
    {
        if (!double.IsFinite(targetAngleDegrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetAngleDegrees),
                "目标角度必须是有限数值。");
        }

        return NormalizePeriodicAngleDegrees(
            targetAngleDegrees - measuredAngleDegrees,
            periodDegrees);
    }

    public static double ConvertDegreesToPulses(
        double angleDegrees,
        double pulsesPerRevolution)
    {
        if (!double.IsFinite(angleDegrees))
        {
            throw new ArgumentOutOfRangeException(
                nameof(angleDegrees),
                "换算角度必须是有限数值。");
        }

        if (!double.IsFinite(pulsesPerRevolution) || pulsesPerRevolution <= 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pulsesPerRevolution),
                "每圈脉冲数必须是大于0的有限数值。");
        }

        return angleDegrees / 360d * pulsesPerRevolution;
    }
}
