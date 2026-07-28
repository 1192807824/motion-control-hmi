using System.Globalization;

namespace ControlHub.Services.Devices;

public sealed record E4981AMeasurementResult(
    int Status,
    double CapacitanceFarads,
    double DissipationFactor,
    int? Bin,
    string RawResponse)
{
    public double CapacitancePf => CapacitanceFarads * 1e12;

    public bool IsSuccessful => Status == 0 && double.IsFinite(CapacitanceFarads);

    public string StatusDescription => Status switch
    {
        0 => "测量正常",
        1 => "过载（OVLD）",
        2 => "低电容或无接触（Low C / NC）",
        _ => $"仪表状态 {Status}"
    };

    public string BinDescription => Bin switch
    {
        null => "比较器未启用",
        0 => "OUT_OF_BINS / BIN0",
        >= 1 and <= 9 => $"BIN{Bin}",
        10 => "AUX_BIN",
        11 => "过载或无接触",
        _ => $"未知BIN {Bin}"
    };
}

public static class E4981AProtocol
{
    private static readonly int[] AllowedApertureTimes = [1, 2, 4, 6, 8];
    private static readonly string[] AllowedFrequencies = ["120HZ", "1KHZ", "1MHZ"];
    private static readonly IReadOnlyDictionary<string, string[]> AllowedRanges =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["120HZ"] = ["10n", "22n", "47n", "100n", "220n", "470n", "1u", "2.2u", "4.7u", "10u", "22u", "47u", "100u", "220u", "470u", "1m"],
            ["1KHZ"] = ["100p", "220p", "470p", "1n", "2.2n", "4.7n", "10n", "22n", "47n", "100n", "220n", "470n", "1u", "2.2u", "4.7u", "10u", "22u", "47u", "100u"],
            ["1MHZ"] = ["1p", "2.2p", "4.7p", "10p", "22p", "47p", "100p", "220p", "470p", "1n"]
        };

    public static IReadOnlyList<string> BuildSetupCommands(TcpConnectionSettings settings)
    {
        ValidateSettings(settings);

        var commands = new List<string>
        {
            "*CLS",
            "FORM ASC",
            "FORM:STAT:EXT OFF",
            "CALC1:FORM CP",
            "CALC2:FORM D",
            $"SOUR:FREQ {NormalizeFrequency(settings.Frequency)}",
            $"SOUR:VOLT {FormatNumber(settings.SignalLevelVolts)}V",
            $"CAL:CABL {settings.CableLengthMeters}",
            $"APER:TIME {settings.ApertureTime}",
            $"AVER {(settings.AveragingEnabled ? "ON" : "OFF")}"
        };

        if (settings.AveragingEnabled)
        {
            commands.Add($"AVER:COUN {settings.AveragingCount}");
        }

        if (string.Equals(settings.MeasurementRange?.Trim(), "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            commands.Add("RANG:AUTO ON");
        }
        else
        {
            commands.Add($"RANG {settings.MeasurementRange?.Trim()}");
        }

        commands.Add("TRIG:SOUR BUS");
        commands.Add("INIT:CONT ON");
        commands.Add("CALC1:COMP:MODE ABS");

        if (settings.ComparatorEnabled)
        {
            AddBinCommands(commands, 1, settings.Bin1Enabled, settings.Bin1LowerPf, settings.Bin1UpperPf);
            AddBinCommands(commands, 2, settings.Bin2Enabled, settings.Bin2LowerPf, settings.Bin2UpperPf);
            AddBinCommands(commands, 3, settings.Bin3Enabled, settings.Bin3LowerPf, settings.Bin3UpperPf);
            for (var bin = 4; bin <= 9; bin++)
            {
                commands.Add($"CALC1:COMP:PRIM:BIN{bin}:STAT OFF");
            }

            commands.Add($"CALC1:COMP:SEC:STAT {(settings.LossLimitEnabled ? "ON" : "OFF")}");
            if (settings.LossLimitEnabled)
            {
                commands.Add($"CALC1:COMP:SEC:LIM {FormatNumber(settings.LossLower)},{FormatNumber(settings.LossUpper)}");
            }
        }
        else
        {
            for (var bin = 1; bin <= 9; bin++)
            {
                commands.Add($"CALC1:COMP:PRIM:BIN{bin}:STAT OFF");
            }
            commands.Add("CALC1:COMP:SEC:STAT OFF");
        }
        commands.Add($"CALC1:COMP {(settings.ComparatorEnabled ? "ON" : "OFF")}");

        return commands;
    }

    public static E4981AMeasurementResult ParseMeasurement(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new FormatException("E4981A返回了空测量结果。");
        }

        var fields = response.Trim().Split(',', StringSplitOptions.TrimEntries);
        if (fields.Length is not (3 or 4))
        {
            throw new FormatException($"E4981A测量结果应包含3项或4项，实际为{fields.Length}项：{response}");
        }

        if (!int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var status) ||
            !double.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var capacitance) ||
            !double.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var dissipation))
        {
            throw new FormatException($"无法解析E4981A测量结果：{response}");
        }

        int? bin = null;
        if (fields.Length == 4)
        {
            if (!int.TryParse(fields[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedBin))
            {
                throw new FormatException($"无法解析E4981A BIN结果：{response}");
            }
            bin = parsedBin;
        }

        return new E4981AMeasurementResult(status, capacitance, dissipation, bin, response.Trim());
    }

    public static bool ExpectsResponse(string command)
    {
        var trimmed = command.Trim();
        return trimmed.Contains('?', StringComparison.Ordinal) ||
               string.Equals(trimmed, "*TRG", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSettings(TcpConnectionSettings settings)
    {
        var frequency = NormalizeFrequency(settings.Frequency);
        if (!AllowedFrequencies.Contains(frequency, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("测试频率只能是120HZ、1KHZ或1MHZ。");
        }
        var measurementRange = settings.MeasurementRange?.Trim();
        if (string.IsNullOrWhiteSpace(measurementRange))
        {
            throw new InvalidOperationException("测量量程不能为空。");
        }
        if (!string.Equals(measurementRange, "AUTO", StringComparison.OrdinalIgnoreCase) &&
            !AllowedRanges[frequency].Contains(measurementRange, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"量程{measurementRange}不适用于测试频率{frequency}。");
        }
        if (!AllowedApertureTimes.Contains(settings.ApertureTime))
        {
            throw new InvalidOperationException("测量时间只能是1、2、4、6或8。");
        }
        if (settings.SignalLevelVolts is < 0.1 or > 1 || !double.IsFinite(settings.SignalLevelVolts))
        {
            throw new InvalidOperationException("测试电压必须在0.1V到1.0V之间。");
        }
        if (settings.CableLengthMeters is < 0 or > 2)
        {
            throw new InvalidOperationException("测试线长度只能是0、1或2米。");
        }
        if (settings.AveragingCount is < 1 or > 256)
        {
            throw new InvalidOperationException("平均次数必须在1到256之间。");
        }
        if (settings.CommandTimeoutMilliseconds is < 500 or > 60_000)
        {
            throw new InvalidOperationException("命令超时必须在500到60000毫秒之间。");
        }
        if (settings.ComparatorEnabled)
        {
            ValidateBin(1, settings.Bin1Enabled, settings.Bin1LowerPf, settings.Bin1UpperPf);
            ValidateBin(2, settings.Bin2Enabled, settings.Bin2LowerPf, settings.Bin2UpperPf);
            ValidateBin(3, settings.Bin3Enabled, settings.Bin3LowerPf, settings.Bin3UpperPf);
            if (settings.LossLimitEnabled &&
                (!double.IsFinite(settings.LossLower) || !double.IsFinite(settings.LossUpper) ||
                 settings.LossLower >= settings.LossUpper))
            {
                throw new InvalidOperationException("损耗D下限必须小于上限。");
            }
        }
    }

    private static void ValidateBin(int bin, bool enabled, double lowerPf, double upperPf)
    {
        if (enabled && (!double.IsFinite(lowerPf) || !double.IsFinite(upperPf) || lowerPf >= upperPf))
        {
            throw new InvalidOperationException($"BIN{bin}电容下限必须小于上限。");
        }
    }

    private static void AddBinCommands(List<string> commands, int bin, bool enabled, double lowerPf, double upperPf)
    {
        commands.Add($"CALC1:COMP:PRIM:BIN{bin}:STAT {(enabled ? "ON" : "OFF")}");
        if (enabled)
        {
            commands.Add(
                $"CALC1:COMP:PRIM:BIN{bin} {FormatNumber(lowerPf * 1e-12)},{FormatNumber(upperPf * 1e-12)}");
        }
    }

    private static string NormalizeFrequency(string? frequency)
    {
        return frequency?.Trim().Replace(" ", string.Empty, StringComparison.Ordinal).ToUpperInvariant() ?? string.Empty;
    }

    private static string FormatNumber(double value)
    {
        return value.ToString("G15", CultureInfo.InvariantCulture);
    }
}
