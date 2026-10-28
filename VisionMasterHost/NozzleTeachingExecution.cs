namespace VisionMasterHost;

/// <summary>粗定位的算法结果只供参考；执行异常也必须继续尝试显示本次图像。</summary>
internal static class NozzleTeachingExecution
{
    public static string Run(Action run, Func<string> readFailure)
    {
        try
        {
            run();
            return readFailure();
        }
        catch (Exception exception)
        {
            return "自动定位执行异常：" + exception.Message;
        }
    }

    public static void RequireCurrentImage(bool currentImageAvailable, string algorithmFailure)
    {
        if (!currentImageAvailable)
        {
            throw new InvalidOperationException(
                "本次未获得有效的示教底图，已清除旧画面，请检查采图后重试。" + algorithmFailure);
        }
    }
}
