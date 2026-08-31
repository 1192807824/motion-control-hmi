using System.IO;
using System.Text.Json;

namespace ControlHub.Services.Motion;

public sealed class MotionCardOptions
{
    public int ConfigurationVersion { get; set; }

    public bool SimulationMode { get; init; }

    public ushort? CardNo { get; init; }

    public int AxisCount { get; init; } = 16;

    public int PollIntervalMilliseconds { get; init; } = 20;

    public int DigitalInputPort { get; init; }

    public int DigitalOutputPort { get; init; }

    public int DigitalInputStartBit { get; init; }

    public int DigitalOutputStartBit { get; init; }

    public bool ExternalEmergencyStopEnabled { get; init; }

    public int ExternalEmergencyStopInputPort { get; init; }

    public int ExternalEmergencyStopInputBit { get; init; } = 1;

    public bool ExternalEmergencyStopActiveLow { get; init; } = true;

    public int SimulationDigitalInputCount { get; init; } = 32;

    public int SimulationDigitalOutputCount { get; init; } = 32;

    public int SimulationAnalogInputCount { get; init; } = 8;

    public int SimulationAnalogOutputCount { get; init; } = 2;

    public double AnalogOutputMinimum { get; init; } = -10;

    public double AnalogOutputMaximum { get; init; } = 10;

    public int ServoEnableTimeoutMilliseconds { get; init; } = 5000;

    public int StopConfirmationTimeoutMilliseconds { get; init; } = 5000;

    public int HomeTimeoutSeconds { get; set; } = 60;

    public MotionMoveProfile MoveProfile { get; init; } = new();

    public Dictionary<int, MotionMoveProfile> AxisMoveProfiles { get; init; } = [];

    public MotionHomeProfile HomeProfile { get; init; } = new();

    public Dictionary<int, MotionHomeProfile> AxisHomeProfiles { get; init; } = [];

    public int[] HomeSequence { get; set; } = [];

    public Dictionary<string, string> IoPointNames { get; set; } = [];

    public void ApplyMigrations()
    {
        if (ConfigurationVersion >= 4)
        {
            return;
        }

        if (ConfigurationVersion < 3)
        {
            if (ConfigurationVersion < 2)
            {
                var previousMoveProfiles = AxisMoveProfiles.ToArray();
                AxisMoveProfiles.Clear();
                foreach (var item in previousMoveProfiles)
                {
                    AxisMoveProfiles[Math.Max(0, item.Key - 1)] = item.Value;
                }

                var previousHomeProfiles = AxisHomeProfiles.ToArray();
                AxisHomeProfiles.Clear();
                foreach (var item in previousHomeProfiles)
                {
                    AxisHomeProfiles[Math.Max(0, item.Key - 1)] = item.Value;
                }

                HomeSequence = HomeSequence.Select(axisNo => Math.Max(0, axisNo - 1)).ToArray();
            }

            AxisMoveProfiles[0] = new MotionMoveProfile
            {
                StartVelocity = 0,
                StopVelocity = 0,
                AccelerationSeconds = 0.1,
                DecelerationSeconds = 0.1,
                STimeSeconds = 0,
                DecelerationStopSeconds = 0.001,
                WaitForCompletion = true,
                CompletionTimeoutMilliseconds = 5000,
                CompletionTolerance = 0.01,
                AbsolutePositionMode = false
            };
            AxisHomeProfiles[0] = new MotionHomeProfile
            {
                Enabled = true,
                Mode = 33,
                LowVelocity = 10000,
                HighVelocity = 40000,
                AccelerationSeconds = 0.1,
                DecelerationSeconds = 0.1,
                OffsetPosition = 0
            };
        }

        ApplyOneKeyResetHomeModes();
        ConfigurationVersion = 4;
    }

    private void ApplyOneKeyResetHomeModes()
    {
        foreach (var axisNo in Enumerable.Range(0, Math.Min(AxisCount, 16)))
        {
            var existing = AxisHomeProfiles.GetValueOrDefault(axisNo);
            var defaultVelocity = GetOneKeyResetHomeVelocity(axisNo);
            AxisHomeProfiles[axisNo] = new MotionHomeProfile
            {
                Enabled = existing?.Enabled ?? true,
                Mode = GetOneKeyResetHomeMode(axisNo),
                LowVelocity = existing?.LowVelocity ?? defaultVelocity,
                HighVelocity = existing?.HighVelocity ?? defaultVelocity,
                AccelerationSeconds = existing?.AccelerationSeconds ?? 0.1,
                DecelerationSeconds = existing?.DecelerationSeconds ?? 0.1,
                OffsetPosition = existing?.OffsetPosition ?? 0
            };
        }
    }

    public static int GetOneKeyResetHomeMode(int hardwareAxisNo)
    {
        return hardwareAxisNo switch
        {
            0 => 33,
            >= 1 and <= 4 => 1,
            5 or 7 or 9 or 11 => -1,
            6 or 8 or 10 or 12 => 33,
            >= 13 and <= 15 => 21,
            _ => throw new ArgumentOutOfRangeException(
                nameof(hardwareAxisNo),
                hardwareAxisNo,
                "一键复位只配置硬件轴0到15。")
        };
    }

    public static double GetOneKeyResetHomeVelocity(int hardwareAxisNo)
    {
        return hardwareAxisNo switch
        {
            0 => 50_000d,
            >= 1 and <= 4 => 100_000d,
            >= 5 and <= 12 => 50_000d,
            >= 13 and <= 15 => 100_000d,
            _ => throw new ArgumentOutOfRangeException(
                nameof(hardwareAxisNo),
                hardwareAxisNo,
                "一键复位只配置硬件轴0到15。")
        };
    }

    public MotionHomeProfile GetHomeProfile(int hardwareAxisNo)
    {
        return AxisHomeProfiles.GetValueOrDefault(hardwareAxisNo) ?? HomeProfile;
    }

    public MotionMoveProfile GetMoveProfile(int hardwareAxisNo)
    {
        return AxisMoveProfiles.GetValueOrDefault(hardwareAxisNo) ?? MoveProfile;
    }

    public MotionMoveProfile GetOrCreateMoveProfile(int hardwareAxisNo)
    {
        if (!AxisMoveProfiles.TryGetValue(hardwareAxisNo, out var profile))
        {
            profile = MoveProfile.Clone();
            AxisMoveProfiles[hardwareAxisNo] = profile;
        }

        return profile;
    }

    public IReadOnlyList<int> GetHomeSequence()
    {
        return HomeSequence.ToArray();
    }

    public void Validate()
    {
        if (AxisCount is < 1 or > 64)
        {
            throw new InvalidDataException("AxisCount 必须在 1 到 64 之间。");
        }

        if (PollIntervalMilliseconds is < 20 or > 5000)
        {
            throw new InvalidDataException("PollIntervalMilliseconds 必须在 20 到 5000 之间。");
        }

        if (DigitalInputPort is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("DigitalInputPort 超出有效范围。");
        }

        if (DigitalOutputPort is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("DigitalOutputPort 超出有效范围。");
        }

        if (DigitalInputStartBit is < 0 or > 31)
        {
            throw new InvalidDataException("DigitalInputStartBit 必须在 0 到 31 之间。");
        }

        if (DigitalOutputStartBit is < 0 or > 31)
        {
            throw new InvalidDataException("DigitalOutputStartBit 必须在 0 到 31 之间。");
        }

        if (ExternalEmergencyStopInputPort is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("ExternalEmergencyStopInputPort 超出有效范围。");
        }

        if (ExternalEmergencyStopInputBit is < 0 or > 31)
        {
            throw new InvalidDataException("ExternalEmergencyStopInputBit 必须在 0 到 31 之间。");
        }

        foreach (var count in new[]
                 {
                     SimulationDigitalInputCount,
                     SimulationDigitalOutputCount,
                     SimulationAnalogInputCount,
                     SimulationAnalogOutputCount
                 })
        {
            if (count is < 0 or > 128)
            {
                throw new InvalidDataException("仿真 I/O 通道数量必须在 0 到 128 之间。");
            }
        }

        if (!double.IsFinite(AnalogOutputMinimum) ||
            !double.IsFinite(AnalogOutputMaximum) ||
            AnalogOutputMaximum <= AnalogOutputMinimum)
        {
            throw new InvalidDataException("AnalogOutputMinimum 必须小于 AnalogOutputMaximum，且都必须是有限数值。");
        }

        if (ServoEnableTimeoutMilliseconds is < 100 or > 30000)
        {
            throw new InvalidDataException("ServoEnableTimeoutMilliseconds 必须在 100 到 30000 之间。");
        }

        if (StopConfirmationTimeoutMilliseconds is < 500 or > 60000)
        {
            throw new InvalidDataException("StopConfirmationTimeoutMilliseconds 必须在 500 到 60000 ms 之间。");
        }

        if (HomeTimeoutSeconds is < 1 or > 3600)
        {
            throw new InvalidDataException("HomeTimeoutSeconds 必须在 1 到 3600 之间。");
        }

        if (IoPointNames is null ||
            IoPointNames.Count > 256 ||
            IoPointNames.Any(item =>
                string.IsNullOrWhiteSpace(item.Key) || item.Key.Length > 64 ||
                string.IsNullOrWhiteSpace(item.Value) || item.Value.Length > 64))
        {
            throw new InvalidDataException("I/O 名称配置无效：键和值不能为空，且长度不能超过 64 个字符。");
        }

        MoveProfile.Validate();
        foreach (var item in AxisMoveProfiles)
        {
            if (item.Key is < 0 or > 63)
            {
                throw new InvalidDataException($"AxisMoveProfiles 的硬件轴号 {item.Key} 超出 0 到 63 的范围。");
            }

            item.Value.Validate();
        }

        HomeProfile.Validate(requireEnabled: false);

        foreach (var item in AxisHomeProfiles)
        {
            if (item.Key is < 0 or > 63)
            {
                throw new InvalidDataException($"AxisHomeProfiles 的硬件轴号 {item.Key} 超出 0 到 63 的范围。");
            }

            item.Value.Validate(requireEnabled: false);
        }

        if (HomeSequence.Any(axisNo => axisNo < 0 || axisNo >= AxisCount))
        {
            throw new InvalidDataException("HomeSequence 使用硬件轴号，且每个轴号必须在 0 到 AxisCount - 1 之间。");
        }

        if (HomeSequence.Distinct().Count() != HomeSequence.Length)
        {
            throw new InvalidDataException("HomeSequence 不能包含重复轴号。");
        }
    }
}

public sealed class MotionMoveProfile
{
    public double StartVelocity { get; set; }

    public double StopVelocity { get; set; }

    public double AccelerationSeconds { get; set; } = 0.1;

    public double DecelerationSeconds { get; set; } = 0.1;

    public double STimeSeconds { get; set; }

    public double DecelerationStopSeconds { get; set; } = 0.1;

    public bool WaitForCompletion { get; set; } = true;

    public int CompletionTimeoutMilliseconds { get; set; } = 5000;

    public double CompletionTolerance { get; set; } = 0.01;

    public bool AbsolutePositionMode { get; set; }

    public MotionMoveProfile Clone()
    {
        return new MotionMoveProfile
        {
            StartVelocity = StartVelocity,
            StopVelocity = StopVelocity,
            AccelerationSeconds = AccelerationSeconds,
            DecelerationSeconds = DecelerationSeconds,
            STimeSeconds = STimeSeconds,
            DecelerationStopSeconds = DecelerationStopSeconds,
            WaitForCompletion = WaitForCompletion,
            CompletionTimeoutMilliseconds = CompletionTimeoutMilliseconds,
            CompletionTolerance = CompletionTolerance,
            AbsolutePositionMode = AbsolutePositionMode
        };
    }

    public void Validate()
    {
        ValidateFiniteNonNegative(StartVelocity, nameof(StartVelocity));
        ValidateFiniteNonNegative(StopVelocity, nameof(StopVelocity));
        ValidateFinitePositive(AccelerationSeconds, nameof(AccelerationSeconds));
        ValidateFinitePositive(DecelerationSeconds, nameof(DecelerationSeconds));
        ValidateFiniteNonNegative(STimeSeconds, nameof(STimeSeconds));
        ValidateFinitePositive(DecelerationStopSeconds, nameof(DecelerationStopSeconds));
        ValidateFinitePositive(CompletionTolerance, nameof(CompletionTolerance));

        if (CompletionTimeoutMilliseconds is < 100 or > 600000)
        {
            throw new InvalidDataException("CompletionTimeoutMilliseconds 必须在 100 到 600000 ms 之间。");
        }

        if (STimeSeconds > 1)
        {
            throw new InvalidDataException("STimeSeconds 必须在 0 到 1 秒之间。");
        }
    }

    internal static void ValidateFinitePositive(double value, string name)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new InvalidDataException($"{name} 必须是大于 0 的有限数值。");
        }
    }

    internal static void ValidateFiniteNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new InvalidDataException($"{name} 必须是大于或等于 0 的有限数值。");
        }
    }
}

public sealed class MotionHomeProfile
{
    public bool Enabled { get; init; }

    public int Mode { get; init; } = 33;

    public double LowVelocity { get; init; } = 5;

    public double HighVelocity { get; init; } = 25;

    public double AccelerationSeconds { get; init; } = 0.1;

    public double DecelerationSeconds { get; init; } = 0.1;

    public double OffsetPosition { get; init; }

    public void Validate(bool requireEnabled)
    {
        if (requireEnabled && !Enabled)
        {
            throw new InvalidDataException("该轴尚未启用回零配置。请按驱动器和机构要求设置 motion-settings.json。");
        }

        if (Mode is < short.MinValue or > ushort.MaxValue)
        {
            throw new InvalidDataException("Mode 必须是 -32768 到 65535 之间的整数。");
        }

        MotionMoveProfile.ValidateFinitePositive(LowVelocity, nameof(LowVelocity));
        MotionMoveProfile.ValidateFinitePositive(HighVelocity, nameof(HighVelocity));
        MotionMoveProfile.ValidateFinitePositive(AccelerationSeconds, nameof(AccelerationSeconds));
        MotionMoveProfile.ValidateFinitePositive(DecelerationSeconds, nameof(DecelerationSeconds));

        if (!double.IsFinite(OffsetPosition))
        {
            throw new InvalidDataException("OffsetPosition 必须是有限数值。");
        }

        if (HighVelocity < LowVelocity)
        {
            throw new InvalidDataException("HighVelocity 不能小于 LowVelocity。");
        }
    }
}

public sealed class MotionCardOptionsStore
{
    private readonly string _filePath;

    public MotionCardOptionsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(AppContext.BaseDirectory, "motion-settings.json");
    }

    public MotionCardOptions Load()
    {
        if (!File.Exists(_filePath))
        {
            var defaults = new MotionCardOptions();
            defaults.ApplyMigrations();
            defaults.Validate();
            return defaults;
        }

        try
        {
            return ReadAndValidate(_filePath);
        }
        catch (Exception primaryException) when (
            primaryException is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            var backupPath = _filePath + ".bak";
            if (File.Exists(backupPath))
            {
                try
                {
                    return ReadAndValidate(backupPath);
                }
                catch (Exception backupException) when (
                    backupException is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
                {
                    // Report the primary file error below; both files are unusable.
                }
            }

            throw new InvalidDataException(
                $"运动配置读取失败，主文件和备份均不可用：{primaryException.Message}",
                primaryException);
        }
    }

    public void Save(MotionCardOptions options)
    {
        options.Validate();
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(options, new JsonSerializerOptions { WriteIndented = true });
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        var backupPath = $"{_filePath}.bak";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 4096,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(json);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_filePath))
            {
                File.Replace(temporaryPath, _filePath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, _filePath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static MotionCardOptions ReadAndValidate(string path)
    {
        var options = JsonSerializer.Deserialize<MotionCardOptions>(File.ReadAllText(path), new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        }) ?? throw new InvalidDataException($"{Path.GetFileName(path)} 内容为空。");
        options.ApplyMigrations();
        options.Validate();
        return options;
    }
}
