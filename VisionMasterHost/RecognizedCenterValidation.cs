namespace VisionMasterHost;

internal static class RecognizedCenterValidation
{
    internal static string? GetScriptError(int moduleStatus, int resultCount)
    {
        if (moduleStatus != 1) return "110脚本1返回NG，请检查脚本及上游模块";
        if (resultCount == 0) return "110脚本1未返回有效X/Y/R坐标";
        if (resultCount > 1) return "110脚本1返回多个目标，请保证只有一个目标";
        if (resultCount < 0) return "110脚本1结果数量无效";
        return null;
    }
}

internal sealed class RecognizedCenterStageException : InvalidOperationException
{
    internal RecognizedCenterStageException(string stage, string detail, Exception inner)
        : base($"步骤【{stage}】失败：{detail}", inner) { }
}
