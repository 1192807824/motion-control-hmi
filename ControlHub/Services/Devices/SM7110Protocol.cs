using System.Globalization;

namespace ControlHub.Services.Devices;

public sealed record SM7110MeasurementResult(
    double Value,
    int Status,
    string MeasurementMode,
    string RawResponse)
{
    public bool IsSuccessful => Status == 0 && double.IsFinite(Value);

    public string StatusDescription => Status switch
    {
        0 => "测量正常",
        1 => "尚无有效测量值",
        3 => "超出保证精度范围",
        5 => "接触检查失败",
        7 => "电压监测检查失败",
        9 => "电流超量程",
        _ => $"仪表状态 {Status}"
    };

    public string Unit => MeasurementMode.ToUpperInvariant() switch
    {
        "A" => "A",
        "R" => "Ω",
        "RS" => "Ω（表面电阻率）",
        "RV" => "Ω·m（体积电阻率）",
        "RL" => "Ω·m（液体体积电阻率）",
        _ => string.Empty
    };
}

public static class SM7110Protocol
{
    private static readonly string[] AllowedModes = ["R", "A", "RS", "RV", "RL"];
    private static readonly string[] AllowedSpeeds = ["SLOW2", "SLOW", "MED", "FAST2", "FAST"];
    private static readonly string[] AllowedRanges =
        ["AUTO", "20pA", "200pA", "2nA", "20nA", "200nA", "2uA", "20uA", "200uA", "2mA"];
    private static readonly string[] AllowedAverageModes = ["OFF", "HOLD", "AUTO"];
    private static readonly string[] AllowedCurrentLimits = ["1.8mA", "5mA", "10mA", "50mA"];

    public static IReadOnlyList<string> BuildSetupCommands(SerialConnectionSettings settings)
    {
        ValidateSettings(settings);

        var mode = Normalize(settings.MeasurementMode);
        var speed = Normalize(settings.MeasurementSpeed);
        var range = settings.MeasurementRange.Trim();
        var averageMode = Normalize(settings.AverageMode);
        var currentLimit = settings.CurrentLimit.Trim();

        var commands = new List<string>
        {
            "*CLS",
            ":HEADer OFF",
            ":STOP",
            ":STOP:CONDition DISCharge",
            $":MEASure:MODE {mode}",
            ":MEASure:FORMat EXP",
            ":MEASure:DIGit 6",
            $":VOLTage {FormatNumber(settings.AppliedVoltageVolts)}",
            $":SPEEd {speed}"
        };

        if (string.Equals(range, "AUTO", StringComparison.OrdinalIgnoreCase))
        {
            commands.Add(":RANGe:AUTO ON");
            commands.Add(":RANGe:AUTO:TIMeout ON");
        }
        else
        {
            commands.Add(":RANGe:AUTO OFF");
            commands.Add($":RANGe {range}");
        }

        commands.Add($":AVERage {averageMode}");
        if (averageMode == "HOLD")
        {
            commands.Add($":AVERage:COUNt {settings.AverageCount}");
        }

        commands.Add($":INTerlock {(settings.InterlockEnabled ? "ON" : "OFF")}");
        commands.Add($":CHARge:LIMit:CURRent {currentLimit}");
        commands.Add($":CHARge:LIMit {(settings.CurrentLimitEnabled ? "ON" : "OFF")}");
        commands.Add(":TRIGger EXTernal");
        return commands;
    }

    public static async Task ApplySetupCommandsAsync(
        IReadOnlyList<string> commands,
        Func<string, CancellationToken, Task> sendCommand,
        Func<string, CancellationToken, Task<string>> query,
        CancellationToken cancellationToken)
    {
        foreach (var command in commands)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await sendCommand(command, cancellationToken);

            // Query separately: an erroneous command can discard the remainder of a compound line.
            var response = await query("*ESR?", cancellationToken);
            if (!int.TryParse(response.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var status) || status is < 0 or > 255)
            {
                throw new InvalidOperationException(
                    $"SM7110/SM7120无法确认参数执行结果：命令【{command}】，*ESR? 返回【{response}】。参数未确认生效，已停止下发。");
            }

            var errors = new List<string>();
            if ((status & 32) != 0) errors.Add("命令或数据格式错误");
            if ((status & 16) != 0) errors.Add("执行错误（参数值或当前仪表状态不允许）");
            if ((status & 8) != 0) errors.Add("设备相关错误");
            if ((status & 4) != 0) errors.Add("查询错误");
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"SM7110/SM7120参数下发失败：命令【{command}】，*ESR?={status}，{string.Join("、", errors)}。已停止下发，未开始测量。");
            }
        }
    }

    public static SM7110MeasurementResult ParseMeasurementResult(string response, string measurementMode)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            throw new FormatException("SM7110返回了空测量结果。");
        }

        var fields = response.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 2)
        {
            throw new FormatException($"SM7110测量结果应包含测量值和状态，实际为{fields.Length}项：{response}");
        }

        string? valueField = null;
        int? status = null;
        foreach (var field in fields)
        {
            if (int.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedStatus) &&
                parsedStatus is 0 or 1 or 3 or 5 or 7 or 9)
            {
                status = parsedStatus;
            }
            else
            {
                valueField = field;
            }
        }

        if (status is null || valueField is null ||
            !double.TryParse(valueField, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"无法解析SM7110测量结果：{response}");
        }

        return new SM7110MeasurementResult(value, status.Value, Normalize(measurementMode), response.Trim());
    }

    public static bool ExpectsResponse(string command)
    {
        return command.Contains('?', StringComparison.Ordinal);
    }

    public static bool IsSupportedIdentity(string identity)
    {
        return identity.Contains("SM7110", StringComparison.OrdinalIgnoreCase) ||
               identity.Contains("SM7120", StringComparison.OrdinalIgnoreCase);
    }

    public static void ValidateSettings(SerialConnectionSettings settings)
    {
        if (!AllowedModes.Contains(settings.MeasurementMode?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SM7110测量模式必须是R、A、RS、RV或RL。");
        }
        if (!AllowedSpeeds.Contains(settings.MeasurementSpeed?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SM7110测量速度设置无效。");
        }
        if (!AllowedRanges.Contains(settings.MeasurementRange?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SM7110电流量程设置无效。");
        }
        if (!AllowedAverageModes.Contains(settings.AverageMode?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SM7110平均模式设置无效。");
        }
        if (!AllowedCurrentLimits.Contains(settings.CurrentLimit?.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("SM7110充电电流限制设置无效。");
        }
        if (settings.AppliedVoltageVolts is < 0.1 or > 1000 || !double.IsFinite(settings.AppliedVoltageVolts))
        {
            throw new InvalidOperationException("SM7110施加电压必须在0.1V到1000V之间。");
        }
        if (string.Equals(settings.AverageMode?.Trim(), "HOLD", StringComparison.OrdinalIgnoreCase) &&
            settings.AverageCount is < 2 or > 255)
        {
            throw new InvalidOperationException("SM7110移动平均次数必须在2到255之间。");
        }
        if (settings.CommandTimeoutMilliseconds is < 500 or > 60_000)
        {
            throw new InvalidOperationException("SM7110命令超时必须在500到60000毫秒之间。");
        }
        var terminator = DecodeNewLine(settings.NewLine);
        if (terminator is not ("\r" or "\n" or "\r\n"))
        {
            throw new InvalidOperationException("SM7110命令必须配置CR、LF或CRLF结束符。");
        }
    }

    public static string DecodeNewLine(string? value)
    {
        if (string.IsNullOrEmpty(value) ||
            string.Equals(value, "无", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "None", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return value
            .Replace("\\r", "\r", StringComparison.OrdinalIgnoreCase)
            .Replace("\\n", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("\\t", "\t", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string? value) => value?.Trim().ToUpperInvariant() ?? string.Empty;

    private static string FormatNumber(double value) => value.ToString("G15", CultureInfo.InvariantCulture);
}
