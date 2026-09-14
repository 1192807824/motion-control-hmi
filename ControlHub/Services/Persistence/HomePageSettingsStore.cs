using System.IO;
using System.Text.Json;

namespace ControlHub.Services.Persistence;

public sealed class HomePageSettingsStore
{
    private readonly string _filePath;

    public HomePageSettingsStore(string? filePath = null)
    {
        _filePath = filePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ControlHub",
            "home-page-settings.json");
    }

    public HomePageSettings Load()
    {
        if (!File.Exists(_filePath))
        {
            return new HomePageSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<HomePageSettings>(File.ReadAllText(_filePath))
                ?? new HomePageSettings();
        }
        catch (IOException)
        {
            return new HomePageSettings();
        }
        catch (JsonException)
        {
            return new HomePageSettings();
        }
    }

    public void Save(HomePageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(_filePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        }));
    }
}

public sealed class HomePageSettings
{
    // 单次上相机识别后最多缓存并抓取的芯片数；空值兼容旧配置并使用当前默认值。
    public int? VisionPickupCount { get; set; }

    // 可空用于兼容旧配置：旧配置没有该字段时继续使用原同步点位运动。
    public bool? XyLinearInterpolationEnabled { get; set; }

    // 可空用于兼容旧配置：旧配置没有该字段时，按启用上相机旋转纠偏处理。
    public bool? UpperCameraCorrectionEnabled { get; set; }

    // 可空用于兼容旧配置：旧配置没有该字段时，界面和生产流程按启用处理。
    public bool? LowerCameraCorrectionEnabled { get; set; }

    // 调试选项：旧配置和新配置均默认不等待。
    public bool LowerCameraRotationDelayEnabled { get; set; }

    // 统一观测位：仅在吸嘴1下相机纠偏成功后叠加。旧配置默认关闭、补偿为0。
    public bool ObservationCompensationEnabled { get; set; }

    public double ObservationCompensationXPulses { get; set; }

    public double ObservationCompensationYPulses { get; set; }

    public double ObservationCompensationRDegrees { get; set; }

    public double? FirstSetTeachingCenterX { get; set; }

    public double? FirstSetTeachingCenterY { get; set; }

    public double? FirstSetTeachingPressPositionX { get; set; }

    public double? FirstSetTeachingPressPositionY { get; set; }

    public double? PresetPosition1X { get; set; }

    public double? PresetPosition1Y { get; set; }

    public double? PresetPosition2X { get; set; }

    public double? PresetPosition2Y { get; set; }

    public double? LowerCameraPhotoPosition1X { get; set; }

    public double? LowerCameraPhotoPosition1Y { get; set; }

    public double? LowerCameraPhotoPosition2X { get; set; }

    public double? LowerCameraPhotoPosition2Y { get; set; }

    public double? LowerCameraNozzle1RotationCenterX { get; set; }

    public double? LowerCameraNozzle1RotationCenterY { get; set; }

    public double? LowerCameraNozzle2RotationCenterX { get; set; }

    public double? LowerCameraNozzle2RotationCenterY { get; set; }

    public double? Axis0RelativePulse { get; set; }

    public double? FirstSetPickupZPosition { get; set; }

    public double? FirstSetDropZPosition { get; set; }

    public double? FirstSetSafeZPosition { get; set; }

    public double? SecondSetPickupZPosition { get; set; }

    public double? SecondSetDropZPosition { get; set; }

    public double? SecondSetSafeZPosition { get; set; }

    public double? FirstSetNozzle1PickupZPosition { get; set; }

    public double? FirstSetNozzle1DropZPosition { get; set; }

    public double? FirstSetNozzle1SafeZPosition { get; set; }

    public double? FirstSetNozzle2PickupZPosition { get; set; }

    public double? FirstSetNozzle2DropZPosition { get; set; }

    public double? FirstSetNozzle2SafeZPosition { get; set; }

    public double? SecondSetNozzle1PickupZPosition { get; set; }

    public double? SecondSetNozzle1DropZPosition { get; set; }

    public double? SecondSetNozzle1SafeZPosition { get; set; }

    public double? SecondSetNozzle2PickupZPosition { get; set; }

    public double? SecondSetNozzle2DropZPosition { get; set; }

    public double? SecondSetNozzle2SafeZPosition { get; set; }

    public double? NozzleZVelocity { get; set; }

    public int? VacuumPickupDwellMilliseconds { get; set; }

    public int? VacuumBreakPulseMilliseconds { get; set; }

    public int? VacuumValveSwitchDelayMilliseconds { get; set; }

    // 第一套吸嘴1取料后沿Z轴负方向上升此距离即可放行XY；0表示等待完整安全位。
    public double? FirstSetNozzle1XyReleaseLiftPulses { get; set; }

    // 第一套吸嘴1取料时，吸嘴2从安全位沿Z轴正方向提前下降的距离；0表示禁用。
    public double? FirstSetNozzle2PreDropPulses { get; set; }

    // 第一套吸嘴1放料时，吸嘴2从安全位沿Z轴正方向提前下降的距离；0表示禁用。
    public double? FirstSetNozzle2PlacePreDropPulses { get; set; }

    // 第二套吸嘴2取13工位时，吸嘴1从安全位沿Z轴正方向提前下降的距离；0表示禁用。
    public double? SecondSetNozzle1PreDropPulses { get; set; }

    public double? SecondSetPickupPosition1X { get; set; }

    public double? SecondSetPickupPosition1Y { get; set; }

    public double? SecondSetPickupPosition2X { get; set; }

    public double? SecondSetPickupPosition2Y { get; set; }

    public double? Bin0PositionX { get; set; }

    public double? Bin0PositionY { get; set; }

    public double? Bin1PositionX { get; set; }

    public double? Bin1PositionY { get; set; }

    public double? Bin2PositionX { get; set; }

    public double? Bin2PositionY { get; set; }

    public double? Bin3PositionX { get; set; }

    public double? Bin3PositionY { get; set; }

    // 视觉角度到R轴命令方向的可标定系数，只允许1或-1；空值使用设备默认值。
    public double? UpperCameraNozzle1RotationSign { get; set; }

    public double? UpperCameraNozzle2RotationSign { get; set; }

    public double? LowerCameraNozzle1RotationSign { get; set; }

    public double? LowerCameraNozzle2RotationSign { get; set; }

    public Dictionary<int, ProductionAxisMotionSettings> ProductionAxisMotionSettings { get; set; } = [];

    public Dictionary<int, TestStationSettings> TestStationSettings { get; set; } = [];

    // 所有测试站下压到位后的接触稳定时间；旧配置缺少此项时使用200 ms。
    public int TestStationDwellMilliseconds { get; set; } = 200;

    // 两个仪表共用的追加测试次数；0表示不重试，旧配置默认重试1次。
    public int TestRetryCount { get; set; } = 1;

    // 留空表示尚未配置；双站生产必须设置合格区间，不能默认为全部合格。
    public double? SM7110LowerLimit { get; set; }

    public double? SM7110UpperLimit { get; set; }

    public string SM7110LimitMeasurementMode { get; set; } = "R";
}

public sealed class ProductionAxisMotionSettings
{
    public double RunVelocity { get; set; }

    public double StartVelocity { get; set; }

    public double StopVelocity { get; set; }

    public double AccelerationMilliseconds { get; set; } = 100;

    public double DecelerationMilliseconds { get; set; } = 100;

    public double STimeMilliseconds { get; set; }

    public double DecelerationStopMilliseconds { get; set; } = 100;
}

public sealed class TestStationSettings
{
    public double PressPosition { get; set; }

    public double WaitPosition { get; set; }

    public bool? Enabled { get; set; }

    public TestStationInstrument? Instrument { get; set; }
}

public enum TestStationInstrument
{
    None = 0,
    E4981A = 1,
    SM7110 = 2
}
