using System.Collections.ObjectModel;
using ControlHub.Models;
using ControlHub.Services.Persistence;

namespace ControlHub.Data;

public static class DashboardSeedData
{
    public static ObservableCollection<AxisStatus> CreateAxes(
        IReadOnlyDictionary<int, AxisSettings> savedSettings,
        int axisCount)
    {
        var names = new[]
        {
            "X1", "Y1", "Z1", "R1", "X2", "Y2", "Z2", "R2",
            "A1", "B1", "C1", "U1", "V1", "W1", "T1", "T2"
        };
        var rotaryAxes = new HashSet<string> { "R1", "R2", "A1", "B1", "C1" };

        var axes = new ObservableCollection<AxisStatus>();
        for (var index = 0; index < Math.Min(axisCount, names.Length); index++)
        {
            var axisNo = index;
            var settings = savedSettings.GetValueOrDefault(axisNo);
            var axisName = names[index];
            var jogSpeed = index == 0 && settings?.ConfigurationVersion is not >= 2
                ? 10000
                : settings?.JogSpeed ?? (index == 0 ? 10000 : 25);

            axes.Add(new AxisStatus
            {
                AxisNo = axisNo,
                Name = settings?.Name ?? axisName,
                JogSpeed = jogSpeed,
                JogDistance = settings?.JogDistance ?? 10,
                Unit = rotaryAxes.Contains(axisName) ? "deg" : "mm",
                State = "未连接"
            });
        }

        return axes;
    }
}
