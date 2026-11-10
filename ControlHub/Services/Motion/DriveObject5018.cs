using System.Globalization;

namespace ControlHub.Services.Motion;

public static class DriveObject5018
{
    public const ushort Index = 0x5018;
    public const ushort SubIndex = 0;
    public const ushort BitLength = 16;

    public static IReadOnlyList<Drive5018AxisResult> WriteAll(ushort value, Func<int, ushort, ushort> writeAndRead)
    {
        var results = new List<Drive5018AxisResult>(4);
        foreach (var axis in new[] { 5, 7, 9, 11 })
        {
            try
            {
                var actual = writeAndRead(axis, value);
                results.Add(actual == value
                    ? new(axis, actual, null)
                    : new(axis, actual, $"回读不一致：写入{value}，回读{actual}"));
            }
            catch (Exception exception) { results.Add(new(axis, null, exception.Message)); }
        }
        return results;
    }

    public static string DescribeResults(IReadOnlyList<Drive5018AxisResult> results) =>
        (results.Count == 4 && results.All(result => result.Error is null) ? "四轴写入成功" : "四轴写入未全部成功") +
        Environment.NewLine + string.Join(Environment.NewLine, results.Select(result => result.Error is null
            ? $"轴{result.Axis}：成功，回读 {result.Actual}"
            : $"轴{result.Axis}：未确认成功，{result.Error}"));

    public static ushort ParseValue(string text) =>
        ushort.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : throw new ArgumentException("请输入0～65535之间的UINT整数。");

    public static void ValidateAxis(int axis)
    {
        if (axis is not (5 or 7 or 9 or 11)) throw new ArgumentOutOfRangeException(nameof(axis), "请选择四个Z轴之一。");
    }

    public static ushort WriteAndVerify(ushort value, Action<ushort> write, Func<uint> read)
    {
        write(value);
        uint actual;
        try { actual = read(); }
        catch (Exception exception)
        {
            throw new MotionCardException($"0x5018:00写入接口已返回成功，但回读失败，未确认写入结果：{exception.Message}",
                "5018回读", innerException: exception);
        }
        if (actual != value)
            throw new MotionCardException($"0x5018:00回读不一致：写入{value}，回读{actual}，未确认写入成功。", "5018回读校验");
        return (ushort)actual;
    }
}

public sealed record Drive5018AxisResult(int Axis, ushort? Actual, string? Error);
