using System.IO;
using System.Text.Json;

namespace ControlHub.Services.Motion;

public sealed class MotionCardOptions
{
    public bool SimulationMode { get; init; }

    public ushort? CardNo { get; init; }

    public int AxisCount { get; init; } = 16;

    public int PollIntervalMilliseconds { get; init; } = 200;

    public int DigitalInputPort { get; init; }

    public int DigitalOutputPort { get; init; }

    public int SimulationDigitalInputCount { get; init; } = 20;

    public int SimulationDigitalOutputCount { get; init; } = 20;

    public int SimulationAnalogInputCount { get; init; } = 8;

    public int SimulationAnalogOutputCount { get; init; } = 2;

    public double AnalogOutputMinimum { get; init; } = -10;

    public double AnalogOutputMaximum { get; init; } = 10;

    public int ServoEnableTimeoutMilliseconds { get; init; } = 1500;

    public int StopConfirmationTimeoutMilliseconds { get; init; } = 5000;

    public int HomeTimeoutSeconds { get; set; } = 60;

    public MotionMoveProfile MoveProfile { get; init; } = new();

    public Dictionary<int, MotionMoveProfile> AxisMoveProfiles { get; init; } = [];

    public MotionHomeProfile HomeProfile { get; init; } = new();

    public Dictionary<int, MotionHomeProfile> AxisHomeProfiles { get; init; } = [];

    public int[] HomeSequence { get; set; } = [];

    public MotionHomeProfile GetHomeProfile(int hardwareAxisNo)
    {
        var displayAxisNo = hardwareAxisNo + 1;
        return AxisHomeProfiles.GetValueOrDefault(displayAxisNo) ?? HomeProfile;
    }

    public MotionMoveProfile GetMoveProfile(int hardwareAxisNo)
    {
        return AxisMoveProfiles.GetValueOrDefault(hardwareAxisNo + 1) ?? MoveProfile;
    }

    public MotionMoveProfile GetOrCreateMoveProfile(int hardwareAxisNo)
    {
        var displayAxisNo = hardwareAxisNo + 1;
        if (!AxisMoveProfiles.TryGetValue(displayAxisNo, out var profile))
        {
            profile = MoveProfile.Clone();
            AxisMoveProfiles[displayAxisNo] = profile;
        }

        return profile;
    }

    public IReadOnlyList<int> GetHomeSequence()
    {
        return HomeSequence
            .Select(displayAxisNo => displayAxisNo - 1)
            .ToArray();
    }

    public void Validate()
    {
        if (AxisCount is < 1 or > 64)
        {
            throw new InvalidDataException("AxisCount 必须在 1 到 64 之间。");
        }

        if (PollIntervalMilliseconds is < 50 or > 5000)
        {
            throw new InvalidDataException("PollIntervalMilliseconds 必须在 50 到 5000 之间。");
        }

        if (DigitalInputPort is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("DigitalInputPort 超出有效范围。");
        }

        if (DigitalOutputPort is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException("DigitalOutputPort 超出有效范围。");
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

        MoveProfile.Validate();
        foreach (var item in AxisMoveProfiles)
        {
            if (item.Key is < 1 or > 64)
            {
                throw new InvalidDataException($"AxisMoveProfiles 的轴号 {item.Key} 超出 1 到 64 的范围。");
            }

            item.Value.Validate();
        }

        HomeProfile.Validate(requireEnabled: false);

        foreach (var item in AxisHomeProfiles)
        {
            if (item.Key is < 1 or > 64)
            {
                throw new InvalidDataException($"AxisHomeProfiles 的轴号 {item.Key} 超出 1 到 64 的范围。");
            }

            item.Value.Validate(requireEnabled: false);
        }

        if (HomeSequence.Any(axisNo => axisNo < 1 || axisNo > AxisCount))
        {
            throw new InvalidDataException("HomeSequence 使用界面轴号，且每个轴号必须在 1 到 AxisCount 之间。");
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

    public ushort Mode { get; init; } = 33;

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
        options.Validate();
        return options;
    }
}
