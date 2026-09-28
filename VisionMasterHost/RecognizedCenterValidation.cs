namespace VisionMasterHost;

internal static class RecognizedCenterValidation
{
    internal static string? GetMatchError(int moduleStatus, int matchCount, int rectangleCount)
    {
        if (moduleStatus != 1) return "108高精度匹配返回NG，请检查本次取图及匹配参数";
        if (matchCount == 0) return "108高精度匹配未找到目标";
        if (matchCount > 1) return "108高精度匹配找到多个目标，请保证只有一个目标";
        if (matchCount != 1 || rectangleCount != 1) return "108匹配数量与匹配框结果不一致";
        return null;
    }
}

internal sealed class RecognizedCenterStageException : InvalidOperationException
{
    internal RecognizedCenterStageException(string stage, string detail, Exception inner)
        : base($"步骤【{stage}】失败：{detail}", inner) { }
}
