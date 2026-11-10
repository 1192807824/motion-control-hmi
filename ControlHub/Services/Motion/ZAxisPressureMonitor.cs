namespace ControlHub.Services.Motion;

public sealed record ZAxisPressureReading(int AxisNo, string Label, int? RawValue, string Status, string Detail);

/// <summary>仅采集反馈；不修改 PDO、不切换运动模式，也不参与生产互锁。</summary>
public static class ZAxisPressureMonitor
{
    private static readonly (int Axis, string Label)[] Axes =
        [(5, "第一套 Z1"), (7, "第一套 Z2"), (9, "第二套 Z1"), (11, "第二套 Z2")];

    public static ZAxisPressureReading[] Unavailable(string status, string detail = "") =>
        Axes.Select(axis => new ZAxisPressureReading(axis.Axis, axis.Label, null, status, detail)).ToArray();

    public static ZAxisPressureReading[] Read(IMotionCard card, bool simulation, CancellationToken cancellationToken = default)
    {
        if (!card.IsOpen)
            return Unavailable("未连接", "运动控制卡尚未连接。");
        if (simulation)
            return Unavailable("模拟模式", "模拟模式不采集真实压力反馈。");

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var busError = card.ReadBusErrorCode();
            // 与现有运动监控一致：未使用环网冗余时允许 0x0228 警告。
            if (busError != 0 && busError != 0x0228)
                return Unavailable("总线异常", $"EtherCAT 总线错误 0x{busError:X4}。");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            return Unavailable("读取失败", exception.Message);
        }

        var readings = new List<ZAxisPressureReading>();
        foreach (var (axis, label) in Axes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var value = card.ReadActualTorque(axis);
                readings.Add(new(axis, label, value, "实时", "驱动器实际转矩原始反馈；压力单位尚未标定。"));
            }
            catch (Exception exception)
            {
                var status = exception is NotSupportedException or EntryPointNotFoundException ? "不支持读取" : "读取失败";
                readings.Add(new(axis, label, null, status, exception.Message));
            }
        }

        // 采集中断开时不保留本轮较早取得的数值。
        return card.IsOpen ? readings.ToArray() : Unavailable("未连接", "运动控制卡已断开。");
    }
}
