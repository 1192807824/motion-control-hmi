using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using ControlHub.Services.Devices;
using ControlHub.Services.Motion;
using ControlHub.Services.Persistence;
using ControlHub.Services.Vision;
using ControlHub.Views.Controls;
using Microsoft.Win32;
using ShapeLine = System.Windows.Shapes.Line;
using ShapeRectangle = System.Windows.Shapes.Rectangle;

namespace ControlHub.Views.Pages;

public partial class HomePage : UserControl
{
    private const string ChipInspectionProcedureName = "找芯片流程";
    private const string ChipInspectionResultModuleName = "脚本1";
    private const int DefaultVisionPickupCount = 20;
    private const int MinVisionPickupCount = 2;
    private const int MaxVisionPickupCount = 100;
    private const int EmptyTraySingleChipVibrationThreshold = 3;
    private const int FirstSetZ1VacuumOutputBit = 15;
    private const int FirstSetZ1BreakVacuumOutputBit = 14;
    private const int FirstSetZ2VacuumOutputBit = 17;
    private const int FirstSetZ2BreakVacuumOutputBit = 16;
    private const int SecondSetZ1BreakVacuumOutputBit = 18;
    private const int SecondSetZ1VacuumOutputBit = 19;
    private const int SecondSetZ2BreakVacuumOutputBit = 20;
    private const int SecondSetZ2VacuumOutputBit = 21;
    private const int Station13BreakVacuumOutputBit = 22;
    private const int Station14BreakVacuumOutputBit = 23;
    private const int CarouselUpperVacuumOutputBit = 24;
    private const int CarouselLowerSprayOutputBit = 25;
    private const int LowerCameraLightOutputBit = 10;
    private const int DefaultVacuumBreakPulseMilliseconds = 30;
    private const int DefaultVacuumValveSwitchDelayMilliseconds = 20;
    private const int DefaultVacuumPickupDwellMilliseconds = 500;
    private const double DefaultFirstSetNozzle1XyReleaseLiftPulses = 1_000d;
    private const double DefaultFirstSetNozzle2PreDropPulses = 0d;
    private const double DefaultFirstSetNozzle2PlacePreDropPulses = 0d;
    private const double DefaultSecondSetNozzle1PreDropPulses = 0d;
    private const int FirstSetNozzle1ZHardwareAxisNo = 5;
    private const int FirstSetNozzle1RHardwareAxisNo = 6;
    private const int FirstSetNozzle2ZHardwareAxisNo = 7;
    private const int FirstSetNozzle2RHardwareAxisNo = 8;
    private const int SecondSetNozzle1ZHardwareAxisNo = 9;
    private const int SecondSetNozzle1RHardwareAxisNo = 10;
    private const int SecondSetNozzle2ZHardwareAxisNo = 11;
    private const int SecondSetNozzle2RHardwareAxisNo = 12;
    private const double DefaultNozzlePickupZPosition = 30_000d;
    private const double DefaultNozzleDropZPosition = 4_800d;
    private const double DefaultNozzleSafeZPosition = -5_000d;
    private const double DefaultNozzleZVelocity = 20_000d;
    private const double DefaultNozzleRVelocity = 50_000d;
    private const int FirstSetXyInterpolationCoordinateSystemNo = 0;
    private const int SecondSetXyInterpolationCoordinateSystemNo = 1;
    private const int StartupNozzleRHomeMode = 33;
    private const double StartupNozzleRHomeVelocity = 50_000d;
    private const double StartupNozzleRHomeOffset = 0d;
    private const double LowerCameraLinearPulsePerMillimeter = 10_000d;
    private const double NozzleRPulsesPerRevolution = 131_072d;
    // 产品为正方形，任意相差90°的边都是同一摆放方向。
    // 用90°周期可避免两次检测分别选中相邻边时，两个吸嘴最终相差90°。
    private const double SquareOrientationPeriodDegrees = 90d;
    private const double UnifiedChipTargetAngleDegrees = 0d;
    // 现场R1/R2的电机指令方向都与上相机角度正方向相反。
    // 下相机为对向观察，同样默认使用反向系数。
    // 每个吸嘴均可通过主页配置单独改为 -1/1，避免把电机安装方向写死。
    private const double DefaultUpperCameraNozzleRotationSign = -1d;
    private const double DefaultLowerCameraNozzleRotationSign = -1d;
    private const double DdMotorPulsePerTurn = 22_500d;
    private const double Axis0Velocity = 10_000d;
    private const double HomePageCompletionTolerance = 100d;
    private const double DefaultTestStationPressPosition = 250_000d;
    private const double DefaultTestStationWaitPosition = 200_000d;
    private const int SecondSetNozzle2UnloadStation = 13;
    private const int SecondSetNozzle1UnloadStation = 14;
    private const double FirstSetXyVelocity = 100_000d;
    private const double SecondSetXyVelocity = 100_000d;
    private const double DefaultSecondSetPickupPosition1X = 1_606_631d;
    private const double DefaultSecondSetPickupPosition1Y = 330_321d;
    private const double DefaultSecondSetPickupPosition2X = 1_606_271d;
    private const double DefaultSecondSetPickupPosition2Y = -222_828d;
    private const double SecondSetNozzleBinYOffset = 77_000d;
    private const int TestStationMoveTimeoutMilliseconds = 60_000;
    private const double TestStationPressVelocity = 800_000d;
    private const int CarouselStationCount = 16;
    private const int CarouselVacuumSprayPulseMilliseconds = 30;
    private const int MoveAwayBeforeDdMilliseconds = 50;
    private const string CarouselStatusLoaded = "有料";
    private const string CarouselStatusPressing = "下压";
    private const string CarouselStatusDwelling = "停留";
    private const string CarouselStatusReturning = "回待机位";
    private static readonly int[] MoveOutAxisNos = [13, 14, 15];
    private static readonly int[] FirstSetAxisNos =
        [VisionCalibrationService.FirstSetXHardwareAxisNo, VisionCalibrationService.FirstSetYHardwareAxisNo];
    private static readonly int[] SecondSetAxisNos =
        [VisionCalibrationService.SecondSetXHardwareAxisNo, VisionCalibrationService.SecondSetYHardwareAxisNo];
    private static readonly int[] FirstSetZAxisNos =
        [FirstSetNozzle1ZHardwareAxisNo, FirstSetNozzle2ZHardwareAxisNo];
    private static readonly int[] FirstSetRAxisNos =
        [FirstSetNozzle1RHardwareAxisNo, FirstSetNozzle2RHardwareAxisNo];
    private static readonly int[] SecondSetZAxisNos =
        [SecondSetNozzle1ZHardwareAxisNo, SecondSetNozzle2ZHardwareAxisNo];
    private static readonly int[] SecondSetRAxisNos =
        [SecondSetNozzle1RHardwareAxisNo, SecondSetNozzle2RHardwareAxisNo];
    private static readonly int[] FirstSetProductionPeerAxisNos =
        [0, .. SecondSetAxisNos, .. SecondSetZAxisNos, .. SecondSetRAxisNos, .. MoveOutAxisNos];
    private static readonly int[] FirstSetNozzle1EarlyReleasePeerAxisNos =
        [.. FirstSetProductionPeerAxisNos, FirstSetNozzle1ZHardwareAxisNo];
    private static readonly int[] SecondSetProductionPeerAxisNos =
        [0, .. FirstSetAxisNos, .. FirstSetZAxisNos, .. FirstSetRAxisNos, .. MoveOutAxisNos];
    private static readonly int[] SecondSetPickupOverlapPeerAxisNos =
        [.. SecondSetProductionPeerAxisNos, SecondSetNozzle1ZHardwareAxisNo, SecondSetNozzle2ZHardwareAxisNo];
    private static readonly int[] ProductionHandlingAxisNos =
        [
            .. FirstSetAxisNos,
            .. SecondSetAxisNos,
            .. FirstSetZAxisNos,
            .. SecondSetZAxisNos,
            .. FirstSetRAxisNos,
            .. SecondSetRAxisNos
        ];
    private static readonly ProductionAxisDefinition[] ProductionAxisDefinitions =
    [
        new(0, "DD转盘", "DD马达", Axis0Velocity),
        new(VisionCalibrationService.FirstSetXHardwareAxisNo, "第一套XY", "上料X", FirstSetXyVelocity),
        new(VisionCalibrationService.FirstSetYHardwareAxisNo, "第一套XY", "上料Y", FirstSetXyVelocity),
        new(VisionCalibrationService.SecondSetXHardwareAxisNo, "第二套XY", "下料X", SecondSetXyVelocity),
        new(VisionCalibrationService.SecondSetYHardwareAxisNo, "第二套XY", "下料Y", SecondSetXyVelocity),
        new(FirstSetNozzle1ZHardwareAxisNo, "第一套Z轴", "上料Z1", DefaultNozzleZVelocity),
        new(FirstSetNozzle1RHardwareAxisNo, "第一套R轴", "上料R1", DefaultNozzleRVelocity),
        new(FirstSetNozzle2ZHardwareAxisNo, "第一套Z轴", "上料Z2", DefaultNozzleZVelocity),
        new(FirstSetNozzle2RHardwareAxisNo, "第一套R轴", "上料R2", DefaultNozzleRVelocity),
        new(SecondSetNozzle1ZHardwareAxisNo, "第二套Z轴", "下料Z1", DefaultNozzleZVelocity),
        new(SecondSetNozzle1RHardwareAxisNo, "第二套R轴", "下料R3", DefaultNozzleRVelocity),
        new(SecondSetNozzle2ZHardwareAxisNo, "第二套Z轴", "下料Z2", DefaultNozzleZVelocity),
        new(SecondSetNozzle2RHardwareAxisNo, "第二套R轴", "下料R4", DefaultNozzleRVelocity),
        new(13, "测试站", "5号测试站", TestStationPressVelocity),
        new(14, "测试站", "6号测试站", TestStationPressVelocity),
        new(15, "测试站", "7号测试站", TestStationPressVelocity)
    ];
    private static readonly Point[] CarouselStationCardSlots =
    [
        new(241, 16),
        new(327, 33),
        new(400, 82),
        new(449, 155),
        new(466, 241),
        new(449, 327),
        new(400, 400),
        new(327, 449),
        new(241, 466),
        new(155, 449),
        new(82, 400),
        new(33, 327),
        new(16, 241),
        new(33, 155),
        new(82, 82),
        new(155, 33)
    ];
    private static readonly IReadOnlyDictionary<int, int> TestStationAxisByStation =
        new Dictionary<int, int>
        {
            [5] = 13,
            [6] = 14,
            [7] = 15
        };
    private static readonly TestStationDefinition[] TestStationDefinitions =
    [
        new(5, 13, "测试站 01"),
        new(6, 14, "测试站 02"),
        new(7, 15, "测试站 03")
    ];
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private readonly HomePageSettingsStore _homeSettingsStore = new();
    private HomePageSettings _homeSettings = new();
    private MotionControlPage? _motionController;
    private VisualCalibrationPage? _visualCalibrationController;
    private ConnectionConfigPage? _connectionConfigController;
    private UsbMicroscopePage? _usbMicroscopeController;
    private bool _presetPositionMoveRunning;
    private bool _oneKeyResetRunning;
    private bool _startSequenceRunning;
    private bool _productionXyLinearInterpolationEnabled;
    private bool _oneKeyCollectRunning;
    private readonly SemaphoreSlim _testStationRetryMotionLock = new(1, 1);
    private CancellationTokenSource? _productionCancellation;
    private TaskCompletionSource<bool>? _productionCompletion;
    private bool _productionStopRequested;
    private bool _productionPauseRequested;
    private TaskCompletionSource<bool>? _productionResumeSignal;
    private readonly Stopwatch _uphStopwatch = new();
    private readonly DispatcherTimer _uphRefreshTimer;
    private ProductionDiagnosticSession? _productionDiagnostics;
    private long _uphCompletedUnitCount;
    private volatile bool _preserveIoOnEmergencyStop;
    private VisionCalibrationAxisSet? _productionAxisSet;
    private ProductionZPositions? _productionZPositions;
    private ProductionZDwellTimes? _productionZDwellTimes;
    private SecondSetXyPositions? _secondSetXyPositions;
    private BinDropPositions? _binDropPositions;
    private LowerCameraPhotoPositions? _lowerCameraPhotoPositions;
    private IReadOnlyDictionary<int, ProductionAxisMotionSettings>? _productionAxisMotionSettings;
    private IReadOnlyDictionary<int, TestStationSettings>? _testStationSettings;
    private int _testStationDwellMilliseconds = 200;
    private int _testRetryCount = 1;
    private SM7110AcceptanceRange? _sm7110AcceptanceRange;
    private readonly Dictionary<int, ProductionAxisMotionEditors> _productionAxisMotionEditors = [];
    private readonly bool[,] _nozzleVacuumEnabledBySet = new bool[2, 3];
    private VisionMotionTarget? _blob1Nozzle1Target;
    private VisionMotionTarget? _blob2Nozzle2Target;
    private CarouselStationState[] _carouselStations = CreateCarouselStationStates();
    private bool _loadingPresetPositions = true;
    private bool _updatingTestStationConfiguration;

    public HomePage()
    {
        InitializeComponent();
        _uphRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uphRefreshTimer.Tick += (_, _) =>
        {
            UpdateUphDisplay();
            if (_distributionDirty) RefreshDistributionCharts();
        };
        InitializeProductionMotionParameterEditors();
        LoadPresetPositions();
        UpdateCarouselStationDisplay(_carouselStations);
        RefreshDistributionCharts();
    }

    private void InitializeProductionMotionParameterEditors()
    {
        var motionTable = new StackPanel();
        motionTable.Children.Add(CreateParameterTableHeader(
            [
                "轴 / 机构",
                "运行速度\npulse/s",
                "初始速度\npulse/s",
                "停止速度\npulse/s",
                "加速\nms",
                "减速\nms",
                "S曲线\nms",
                "减停\nms"
            ],
            8));
        for (var index = 0; index < ProductionAxisDefinitions.Length; index++)
        {
            var definition = ProductionAxisDefinitions[index];
            var editors = CreateProductionAxisMotionEditors(definition, index);
            _productionAxisMotionEditors[definition.AxisNo] = editors;
            motionTable.Children.Add(editors.Container);
        }

        ProductionMotionParametersPanel.Children.Add(motionTable);
    }

    private static Border CreateParameterTableHeader(
        IReadOnlyList<string> labels,
        int columnCount)
    {
        var grid = CreateParameterTableGrid(columnCount);
        for (var column = 0; column < labels.Count; column++)
        {
            var label = new TextBlock
            {
                Text = labels[column],
                Margin = new Thickness(6, 0, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(159, 190, 213)),
                FontSize = 10,
                FontWeight = FontWeights.SemiBold
            };
            Grid.SetColumn(label, column);
            grid.Children.Add(label);
        }

        return new Border
        {
            Height = 38,
            Margin = new Thickness(0, 0, 0, 3),
            Background = new SolidColorBrush(Color.FromRgb(20, 57, 82)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(52, 82, 105)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = grid
        };
    }

    private ProductionAxisMotionEditors CreateProductionAxisMotionEditors(
        ProductionAxisDefinition definition,
        int rowIndex)
    {
        var row = CreateParameterTableGrid(8);
        AddAxisNameCell(row, definition);
        var runVelocity = AddCompactParameterInput(row, 1, "运行速度，pulse/s");
        var startVelocity = AddCompactParameterInput(row, 2, "初始速度，pulse/s");
        var stopVelocity = AddCompactParameterInput(row, 3, "停止速度，pulse/s");
        var acceleration = AddCompactParameterInput(row, 4, "加速时间，ms");
        var deceleration = AddCompactParameterInput(row, 5, "减速时间，ms");
        var sTime = AddCompactParameterInput(row, 6, "S曲线时间，0–1000 ms");
        var decelerationStop = AddCompactParameterInput(row, 7, "减速停止时间，ms");

        return new ProductionAxisMotionEditors(
            CreateParameterTableRow(row, rowIndex),
            runVelocity,
            startVelocity,
            stopVelocity,
            acceleration,
            deceleration,
            sTime,
            decelerationStop);
    }

    private static Grid CreateParameterTableGrid(int columnCount)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(172) });
        for (var index = 1; index < columnCount; index++)
        {
            grid.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        return grid;
    }

    private static Border CreateParameterTableRow(Grid row, int rowIndex)
    {
        return new Border
        {
            Height = 38,
            Margin = new Thickness(0, 1, 0, 1),
            Padding = new Thickness(0, 2, 0, 2),
            Background = new SolidColorBrush(
                rowIndex % 2 == 0
                    ? Color.FromRgb(17, 43, 62)
                    : Color.FromRgb(19, 48, 69)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(39, 68, 89)),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = row
        };
    }

    private static void AddAxisNameCell(
        Grid row,
        ProductionAxisDefinition definition)
    {
        var label = new TextBlock
        {
            Text = $"{definition.DisplayName}  ·  轴{definition.AxisNo}",
            Margin = new Thickness(10, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(226, 237, 244)),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold
        };
        Grid.SetColumn(label, 0);
        row.Children.Add(label);
    }

    private TextBox AddCompactParameterInput(
        Grid row,
        int column,
        string tooltip)
    {
        var textBox = new TextBox
        {
            Tag = "CompactParameter",
            Height = 30,
            Margin = new Thickness(5, 1, 5, 1),
            TextAlignment = TextAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Color.FromRgb(234, 242, 247)),
            Background = new SolidColorBrush(Color.FromRgb(23, 52, 74)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(59, 95, 120)),
            BorderThickness = new Thickness(1),
            ToolTip = tooltip
        };
        textBox.TextChanged += ProductionAxisParameterTextBox_TextChanged;
        Grid.SetColumn(textBox, column);
        row.Children.Add(textBox);
        return textBox;
    }

    public FrameworkElement DetachParameterSettingsPanel()
    {
        if (ParameterSettingsPanel.Parent is Panel parent)
        {
            parent.Children.Remove(ParameterSettingsPanel);
        }

        return ParameterSettingsPanel;
    }

    public HomePageSettings CaptureRecipeSettings()
    {
        SaveVisionPickupCountFromInput(throwOnInvalid: true);
        SaveSM7110RangeFromInputs(throwOnInvalid: true);
        SaveObservationCompensationFromInputs(throwOnInvalid: true);
        SaveFirstSetTeachingPositionsFromInputs(throwOnInvalid: true);
        SavePresetPositionsFromInputs(throwOnInvalid: true);
        SaveLowerCameraPhotoPositionsFromInputs(throwOnInvalid: true);
        SaveLowerCameraRotationCentersFromInputs(throwOnInvalid: true);
        SaveProductionAxisParametersFromInputs(throwOnInvalid: true);
        SaveTestStationParametersFromInputs(throwOnInvalid: true);
        SaveProductionZPositionsFromInputs(throwOnInvalid: true);
        SaveSecondSetXyPositionsFromInputs(throwOnInvalid: true);
        SaveBinPositionsFromInputs(throwOnInvalid: true);
        return ProductRecipeStore.Clone(_homeSettings);
    }

    public HomePageSettings GetCurrentParameterSettings()
    {
        var settings = ProductRecipeStore.Clone(_homeSettings);
        // 文本框刷新期间可能短暂为空或处于未完成输入状态；此时保留已保存值，
        // 避免视觉标定把有效的中心位/下压位误判成“未配置”。真正清空输入时，
        // TextChanged 保存逻辑会先把 _homeSettings 对应字段置空。
        if (TryParseCoordinate(FirstSetTeachingCenterXTextBox.Text, out var centerX))
            settings.FirstSetTeachingCenterX = centerX;
        if (TryParseCoordinate(FirstSetTeachingCenterYTextBox.Text, out var centerY))
            settings.FirstSetTeachingCenterY = centerY;
        if (TryParseCoordinate(FirstSetTeachingPressPositionXTextBox.Text, out var pressPositionX))
            settings.FirstSetTeachingPressPositionX = pressPositionX;
        if (TryParseCoordinate(FirstSetTeachingPressPositionYTextBox.Text, out var pressPositionY))
            settings.FirstSetTeachingPressPositionY = pressPositionY;
        settings.LowerCameraNozzle1RotationCenterX =
            TryParseCoordinate(LowerCameraNozzle1RotationCenterXTextBox.Text, out var nozzle1CenterX)
                ? nozzle1CenterX
                : null;
        settings.LowerCameraNozzle1RotationCenterY =
            TryParseCoordinate(LowerCameraNozzle1RotationCenterYTextBox.Text, out var nozzle1CenterY)
                ? nozzle1CenterY
                : null;
        settings.LowerCameraNozzle2RotationCenterX =
            TryParseCoordinate(LowerCameraNozzle2RotationCenterXTextBox.Text, out var nozzle2CenterX)
                ? nozzle2CenterX
                : null;
        settings.LowerCameraNozzle2RotationCenterY =
            TryParseCoordinate(LowerCameraNozzle2RotationCenterYTextBox.Text, out var nozzle2CenterY)
                ? nozzle2CenterY
                : null;
        return settings;
    }

    public void SetLowerCameraRotationCenter(int nozzleNumber, double centerX, double centerY)
    {
        if (nozzleNumber is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(nozzleNumber), "下相机吸嘴编号必须为1或2。");
        }

        if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            throw new ArgumentOutOfRangeException(nameof(centerX), "下相机旋转中心必须是有效数字。");
        }

        _loadingPresetPositions = true;
        try
        {
            var xText = centerX.ToString("0.#####", CultureInfo.CurrentCulture);
            var yText = centerY.ToString("0.#####", CultureInfo.CurrentCulture);
            if (nozzleNumber == 1)
            {
                LowerCameraNozzle1RotationCenterXTextBox.Text = xText;
                LowerCameraNozzle1RotationCenterYTextBox.Text = yText;
                _homeSettings.LowerCameraNozzle1RotationCenterX = centerX;
                _homeSettings.LowerCameraNozzle1RotationCenterY = centerY;
            }
            else
            {
                LowerCameraNozzle2RotationCenterXTextBox.Text = xText;
                LowerCameraNozzle2RotationCenterYTextBox.Text = yText;
                _homeSettings.LowerCameraNozzle2RotationCenterX = centerX;
                _homeSettings.LowerCameraNozzle2RotationCenterY = centerY;
            }
        }
        finally
        {
            _loadingPresetPositions = false;
        }

        _homeSettingsStore.Save(_homeSettings);
        SetLowerCameraPhotoPositionStatus(
            $"吸嘴{nozzleNumber}旋转中心已自动写入参数设置：X={centerX:0.#####}，Y={centerY:0.#####} pixel。",
            true);
        UpdateHomeCommandState();
    }

    public void ClearLowerCameraRotationCenter(int nozzleNumber)
    {
        if (nozzleNumber is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(nameof(nozzleNumber), "下相机吸嘴编号必须为1或2。");
        }

        _loadingPresetPositions = true;
        try
        {
            if (nozzleNumber == 1)
            {
                LowerCameraNozzle1RotationCenterXTextBox.Text = "";
                LowerCameraNozzle1RotationCenterYTextBox.Text = "";
                _homeSettings.LowerCameraNozzle1RotationCenterX = null;
                _homeSettings.LowerCameraNozzle1RotationCenterY = null;
            }
            else
            {
                LowerCameraNozzle2RotationCenterXTextBox.Text = "";
                LowerCameraNozzle2RotationCenterYTextBox.Text = "";
                _homeSettings.LowerCameraNozzle2RotationCenterX = null;
                _homeSettings.LowerCameraNozzle2RotationCenterY = null;
            }
        }
        finally
        {
            _loadingPresetPositions = false;
        }

        _homeSettingsStore.Save(_homeSettings);
        SetLowerCameraPhotoPositionStatus(
            $"吸嘴{nozzleNumber}重新完成九点标定，请重新计算旋转中心。",
            true);
        UpdateHomeCommandState();
    }

    public void ApplyRecipeSettings(HomePageSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_startSequenceRunning || _oneKeyResetRunning || _presetPositionMoveRunning)
        {
            throw new InvalidOperationException("设备正在运行，不能切换产品配方。");
        }

        _homeSettingsStore.Save(ProductRecipeStore.Clone(settings));
        LoadPresetPositions();
        UpdateHomeCommandState();
    }

    public void AttachMotionController(MotionControlPage motionController)
    {
        ArgumentNullException.ThrowIfNull(motionController);
        if (_motionController is not null)
        {
            _motionController.EmergencyStopIssued -= MotionController_EmergencyStopIssued;
        }

        _motionController = motionController;
        _motionController.AttachDdTestStationInterlock(ReadDdTestStationWaitPositions);
        _motionController.EmergencyStopIssued += MotionController_EmergencyStopIssued;
        UpdateHomeCommandState();
    }

    private void MotionController_EmergencyStopIssued(object? sender, EventArgs e)
    {
        // 该事件可能来自独立急停监控线程。这里只做无界面依赖的IO冻结，
        // 让控制卡急停命令不必等待UI线程；生产取消与状态刷新投递回UI线程执行。
        _preserveIoOnEmergencyStop = true;
        if (Dispatcher.CheckAccess())
        {
            _ = RequestProductionStop();
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Send,
            new Action(() => _ = RequestProductionStop()));
    }

    public void AttachVisionCalibrationController(VisualCalibrationPage visualCalibrationController)
    {
        _visualCalibrationController = visualCalibrationController
            ?? throw new ArgumentNullException(nameof(visualCalibrationController));
        UpdateHomeCommandState();
    }

    public void AttachConnectionConfigController(ConnectionConfigPage connectionConfigController)
    {
        _connectionConfigController = connectionConfigController
            ?? throw new ArgumentNullException(nameof(connectionConfigController));
        if (SM7110RangeCard.Parent is Panel parent)
            parent.Children.Remove(SM7110RangeCard);
        connectionConfigController.AttachSM7110RangeEditor(
            SM7110RangeCard, SM7110LowerLimitTextBox, SM7110UpperLimitTextBox, SM7110LimitModeComboBox,
            SM7110MaximumTimeTextBox, ReadSM7110TimedTestSettings);
    }

    public void AttachUsbMicroscopeController(UsbMicroscopePage usbMicroscopeController)
    {
        ArgumentNullException.ThrowIfNull(usbMicroscopeController);
        if (_usbMicroscopeController is not null)
        {
            _usbMicroscopeController.PreviewChanged -= UsbMicroscopeController_PreviewChanged;
        }

        _usbMicroscopeController = usbMicroscopeController;
        _usbMicroscopeController.AttachHomePreviewHost(HomeMicroscopePreviewHost);
        _usbMicroscopeController.PreviewChanged += UsbMicroscopeController_PreviewChanged;
        UpdateHomeMicroscopePreview();
    }

    private void UsbMicroscopeController_PreviewChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.CheckAccess())
        {
            UpdateHomeMicroscopePreview();
            return;
        }

        _ = Dispatcher.BeginInvoke(
            DispatcherPriority.Render,
            new Action(UpdateHomeMicroscopePreview));
    }

    private void UpdateHomeMicroscopePreview()
    {
        if (HomeMicroscopeStatusIndicator is null)
        {
            return;
        }

        var controller = _usbMicroscopeController;
        var frame = controller?.LatestPreviewFrame;
        HomeMicroscopeStatusIndicator.ToolTip = controller?.PreviewStatus ?? "等待显微镜连接";
        HomeMicroscopeStatusIndicator.Fill = new SolidColorBrush(
            frame is not null
                ? Color.FromRgb(57, 197, 107)
                : controller?.IsMicroscopeConnected == true
                    ? Color.FromRgb(224, 162, 26)
                    : Color.FromRgb(111, 129, 144));
    }

    public async Task DeactivateProductionAsync()
    {
        var completion = _productionCompletion;
        if (!_startSequenceRunning || completion is null)
        {
            return;
        }

        _ = RequestProductionStop();
        await completion.Task;
    }

    public void RequestProductionStopNoWait()
    {
        _ = RequestProductionStop();
    }

    public async void RefreshVisionInspectionDisplay(VisualCalibrationPage visualCalibrationController)
    {
        if (BlobInspectionVisionDisplayHost.Visibility != Visibility.Visible ||
            BlobInspectionVisionDisplayHost.HostWindow == IntPtr.Zero)
        {
            return;
        }

        try
        {
            await visualCalibrationController.ActivateInspectionViewAsync(
                BlobInspectionVisionDisplayHost.HostWindow);
        }
        catch (Exception exception)
        {
            BlobInspectionImageStatusText.Text = $"VM显示恢复失败：{exception.Message}";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(242, 122, 128));
        }
    }

    /// <summary>
    /// 主页生产逻辑读取的视觉标定快照，包括标定文件和两个吸嘴的偏移。
    /// </summary>
    public VisionCalibrationSnapshot VisionCalibration => _visionCalibration.GetSnapshot();

    private IReadOnlyCollection<int>? AllowedProductionPeerAxisNos =>
        _startSequenceRunning && _productionAxisSet == VisionCalibrationAxisSet.First
            ? FirstSetProductionPeerAxisNos
            : null;

    /// <summary>
    /// 将视觉计算出的相机轴坐标转换为相机/吸嘴1/吸嘴2的实际轴目标。
    /// </summary>
    public VisionMotionTarget CalculateVisionTarget(
        double cameraTargetX,
        double cameraTargetY,
        VisionTargetTool targetTool)
    {
        return _visionCalibration.CalculateTarget(cameraTargetX, cameraTargetY, targetTool);
    }

    private bool SetNozzleVacuumOutputs(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        bool vacuumEnabled,
        bool breakVacuumEnabled)
    {
        if (_preserveIoOnEmergencyStop)
        {
            return true;
        }

        if (vacuumEnabled && breakVacuumEnabled)
        {
            throw new ArgumentException("同一个吸嘴不能同时开启真空吸和真空破。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");

        if (axisSet == VisionCalibrationAxisSet.Second)
        {
            return SetSecondSetNozzleVacuumOutputs(
                motionController,
                nozzleNumber,
                vacuumEnabled,
                breakVacuumEnabled);
        }

        var (vacuumBit, breakVacuumBit) = (axisSet, nozzleNumber) switch
        {
            (VisionCalibrationAxisSet.First, 1) =>
                (FirstSetZ1VacuumOutputBit, FirstSetZ1BreakVacuumOutputBit),
            (VisionCalibrationAxisSet.First, 2) =>
                (FirstSetZ2VacuumOutputBit, FirstSetZ2BreakVacuumOutputBit),
            _ => throw new ArgumentOutOfRangeException(nameof(nozzleNumber), "吸嘴编号只能是 1 或 2。")
        };

        var axisSetIndex = (int)axisSet;
        var nextNozzle1VacuumEnabled = nozzleNumber == 1
            ? vacuumEnabled
            : _nozzleVacuumEnabledBySet[axisSetIndex, 1];
        var nextNozzle2VacuumEnabled = nozzleNumber == 2
            ? vacuumEnabled
            : _nozzleVacuumEnabledBySet[axisSetIndex, 2];
        var vacuumSet = false;
        var breakVacuumSet = false;
        Exception? firstFailure = null;
        try
        {
            vacuumSet = motionController.SetDigitalOutputHardwareBit(vacuumBit, !vacuumEnabled);
            if (vacuumSet)
            {
                _nozzleVacuumEnabledBySet[axisSetIndex, 1] = nextNozzle1VacuumEnabled;
                _nozzleVacuumEnabledBySet[axisSetIndex, 2] = nextNozzle2VacuumEnabled;
            }
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        try
        {
            breakVacuumSet = motionController.SetDigitalOutputHardwareBit(breakVacuumBit, !breakVacuumEnabled);
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        if (firstFailure is not null)
        {
            throw new InvalidOperationException(
                $"Z{nozzleNumber}真空IO写入失败：吸Y{vacuumBit:00}={FormatIoState(vacuumEnabled)}，破Y{breakVacuumBit:00}={FormatIoState(breakVacuumEnabled)}。",
                firstFailure);
        }

        return vacuumSet & breakVacuumSet;
    }

    private bool SetSecondSetNozzleVacuumOutputs(
        MotionControlPage motionController,
        int nozzleNumber,
        bool vacuumEnabled,
        bool breakVacuumEnabled)
    {
        var outputSet = false;
        Exception? firstFailure = null;
        try
        {
            var (breakVacuumBit, vacuumBit) = nozzleNumber switch
            {
                1 => (SecondSetZ1BreakVacuumOutputBit, SecondSetZ1VacuumOutputBit),
                2 => (SecondSetZ2BreakVacuumOutputBit, SecondSetZ2VacuumOutputBit),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(nozzleNumber),
                    "吸嘴编号只能是 1 或 2。")
            };

            // 第二套两个吸嘴均为低电平有效：
            // Z1：Y18=破、Y19=吸；Z2：Y20=破、Y21=吸。0=开，1=关。
            // 切换时先把相反阀写 1 关闭，再把目标阀写 0 打开，
            // 避免真空吸和真空破在切换瞬间同时开启。
            bool oppositeSet;
            bool requestedSet;
            if (vacuumEnabled)
            {
                oppositeSet = motionController.SetDigitalOutputHardwareBit(
                    breakVacuumBit,
                    true);
                requestedSet = motionController.SetDigitalOutputHardwareBit(
                    vacuumBit,
                    false);
            }
            else if (breakVacuumEnabled)
            {
                oppositeSet = motionController.SetDigitalOutputHardwareBit(
                    vacuumBit,
                    true);
                requestedSet = motionController.SetDigitalOutputHardwareBit(
                    breakVacuumBit,
                    false);
            }
            else
            {
                oppositeSet = motionController.SetDigitalOutputHardwareBit(
                    vacuumBit,
                    true);
                requestedSet = motionController.SetDigitalOutputHardwareBit(
                    breakVacuumBit,
                    true);
            }

            outputSet = oppositeSet & requestedSet;
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }

        if (firstFailure is not null)
        {
            var expectedOutput = nozzleNumber switch
            {
                1 => $"破Y{SecondSetZ1BreakVacuumOutputBit:00}={(breakVacuumEnabled ? 0 : 1)}，吸Y{SecondSetZ1VacuumOutputBit:00}={(vacuumEnabled ? 0 : 1)}",
                2 => $"破Y{SecondSetZ2BreakVacuumOutputBit:00}={(breakVacuumEnabled ? 0 : 1)}，吸Y{SecondSetZ2VacuumOutputBit:00}={(vacuumEnabled ? 0 : 1)}",
                _ => $"吸嘴{nozzleNumber}"
            };
            throw new InvalidOperationException(
                $"第二套Z{nozzleNumber}真空IO写入失败：{expectedOutput}。",
                firstFailure);
        }

        if (outputSet)
        {
            _nozzleVacuumEnabledBySet[(int)VisionCalibrationAxisSet.Second, nozzleNumber] =
                vacuumEnabled;
        }

        return outputSet;
    }

    private static string FormatIoState(bool enabled) => enabled ? "ON" : "OFF";

    private async Task PickWithActiveSetNozzleAsync(
        int nozzleNumber,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        var axisSet = _productionAxisSet ?? _visionCalibration.ActiveAxisSet;
        var positions = GetProductionZPositions().Resolve(axisSet, nozzleNumber);
        await PickWithNozzleAsync(
            axisSet,
            nozzleNumber,
            positions.Pickup,
            positions.Safe,
            cancellationToken,
            allowedMovingAxisNos);
    }

    private async Task PickWithNozzleAsync(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        double pickupPosition,
        double safePosition,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        await MoveNozzleZToAsync(
            axisSet,
            nozzleNumber,
            pickupPosition,
            "取料位",
            cancellationToken,
            allowedMovingAxisNos);

        EnableNozzleVacuum(axisSet, nozzleNumber, cancellationToken);

        var pickupDwellMilliseconds = GetProductionZDwellTimes().PickupMilliseconds;
        SetFirstSetPositionStatus(
            $"Z{nozzleNumber}真空吸已开启，保持 {pickupDwellMilliseconds} ms 等待吸附稳定…",
            true);
        await Task.Delay(pickupDwellMilliseconds, cancellationToken);

        await MoveNozzleZToAsync(
            axisSet,
            nozzleNumber,
            safePosition,
            "安全位",
            cancellationToken,
            allowedMovingAxisNos);
    }

    private async Task<Task> PickFirstSetNozzle1UntilXyReleaseAsync(
        CancellationToken cancellationToken)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        var zPositions = GetProductionZPositions();
        var positions = zPositions.FirstSetNozzle1;
        var dwellTimes = GetProductionZDwellTimes();
        var releaseLiftPulses = dwellTimes.FirstSetNozzle1XyReleaseLiftPulses;
        var nozzle2PreDropPulses = dwellTimes.FirstSetNozzle2PreDropPulses;

        if (nozzle2PreDropPulses > 0)
        {
            await MoveFirstSetNozzle1ToPickupWithNozzle2PreDropAsync(
                positions.Pickup,
                zPositions.FirstSetNozzle2.Safe + nozzle2PreDropPulses,
                nozzle2PreDropPulses,
                cancellationToken);
        }
        else
        {
            await MoveNozzleZToAsync(
                VisionCalibrationAxisSet.First,
                1,
                positions.Pickup,
                "取料位",
                cancellationToken);
        }

        EnableNozzleVacuum(VisionCalibrationAxisSet.First, 1, cancellationToken);
        var pickupDwellMilliseconds = dwellTimes.PickupMilliseconds;
        SetFirstSetPositionStatus(
            $"Z1真空吸已开启，保持 {pickupDwellMilliseconds} ms 等待吸附稳定…",
            true);
        await Task.Delay(pickupDwellMilliseconds, cancellationToken);

        var safePositionTask = MoveNozzleZToAsync(
            VisionCalibrationAxisSet.First,
            1,
            positions.Safe,
            "安全位",
            cancellationToken);
        if (releaseLiftPulses <= 0)
        {
            await safePositionTask;
            return Task.CompletedTask;
        }

        // 第一套Z轴上升方向固定为负方向：配置1000时，放行位置=取料位置-1000。
        var releasePosition = positions.Pickup - releaseLiftPulses;
        try
        {
            var released = await motionController.WaitForAxisFeedbackAtOrBelowAsync(
                FirstSetNozzle1ZHardwareAxisNo,
                releasePosition,
                safePositionTask,
                cancellationToken);
            SetFirstSetPositionStatus(
                $"Z1已沿负方向上升 {releaseLiftPulses:0.###} pulse，" +
                $"当前位置 {released.FeedbackPosition:0.###}，XY已放行；Z1继续回安全位。",
                true);
            return safePositionTask;
        }
        catch
        {
            await ObserveTaskNoThrowAsync(safePositionTask);
            throw;
        }
    }

    private async Task MoveFirstSetNozzle1ToPickupWithNozzle2PreDropAsync(
        double nozzle1PickupPosition,
        double nozzle2PreDropPosition,
        double nozzle2PreDropPulses,
        CancellationToken cancellationToken)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var targets = new Dictionary<int, double>
        {
            [FirstSetNozzle1ZHardwareAxisNo] = nozzle1PickupPosition,
            [FirstSetNozzle2ZHardwareAxisNo] = nozzle2PreDropPosition
        };
        SetFirstSetPositionStatus(
            $"Z1正在下降到取料位 {nozzle1PickupPosition:0.###}；" +
            $"Z2同步沿+Z预下降 {nozzle2PreDropPulses:0.###} pulse " +
            $"至 {nozzle2PreDropPosition:0.###}…",
            true);

        var actual = await motionController.MoveAxesAbsoluteAsync(
            targets,
            cancellationToken,
            allowedMovingAxisNos: FirstSetProductionPeerAxisNos,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverrides: GetProductionAxisVelocities(targets.Keys));
        var actualByAxis = actual.ToDictionary(item => item.HardwareAxisNo);
        SetFirstSetPositionStatus(
            $"Z1已到取料位 {actualByAxis[FirstSetNozzle1ZHardwareAxisNo].FeedbackPosition:0.###}；" +
            $"Z2已完成预下降 {actualByAxis[FirstSetNozzle2ZHardwareAxisNo].FeedbackPosition:0.###} pulse。",
            true);
    }

    private async Task PlaceWithActiveSetNozzleAsync(
        int nozzleNumber,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        var axisSet = _productionAxisSet ?? _visionCalibration.ActiveAxisSet;
        var positions = GetProductionZPositions().Resolve(axisSet, nozzleNumber);
        await PlaceWithNozzleAsync(
            axisSet,
            nozzleNumber,
            positions.Drop,
            positions.Safe,
            cancellationToken,
            allowedMovingAxisNos);
    }

    private async Task PlaceWithNozzleAsync(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        double dropPosition,
        double safePosition,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        await MoveNozzleZToAsync(
            axisSet,
            nozzleNumber,
            dropPosition,
            "放料位",
            cancellationToken,
            allowedMovingAxisNos);

        await PulseNozzleBreakVacuumAsync(axisSet, nozzleNumber, cancellationToken);

        await MoveNozzleZToAsync(
            axisSet,
            nozzleNumber,
            safePosition,
            "安全位",
            cancellationToken,
            allowedMovingAxisNos);
    }

    private async Task<Task> PlaceFirstSetNozzle1UntilXyReleaseAsync(
        CancellationToken cancellationToken)
    {
        var positions = GetProductionZPositions();
        var nozzle1Positions = positions.FirstSetNozzle1;
        var placePreDropPulses = GetProductionZDwellTimes().FirstSetNozzle2PlacePreDropPulses;

        if (placePreDropPulses > 0)
        {
            await MoveFirstSetNozzle1ToDropWithNozzle2PreDropAsync(
                nozzle1Positions.Drop,
                positions.FirstSetNozzle2.Safe + placePreDropPulses,
                placePreDropPulses,
                cancellationToken);
        }
        else
        {
            await MoveNozzleZToAsync(
                VisionCalibrationAxisSet.First,
                1,
                nozzle1Positions.Drop,
                "放料位",
                cancellationToken);
        }

        await PulseNozzleBreakVacuumAsync(
            VisionCalibrationAxisSet.First,
            1,
            cancellationToken);

        // 破真空完成即放行XY；Z1沿-Z回安全位的动作继续在后台执行。
        var safePositionTask = MoveNozzleZToAsync(
            VisionCalibrationAxisSet.First,
            1,
            nozzle1Positions.Safe,
            "安全位",
            cancellationToken);
        SetFirstSetPositionStatus(
            "Z1破真空已完成，XY立即放行前往位置2；Z1继续沿-Z回安全位。",
            true);
        return safePositionTask;
    }

    private async Task MoveFirstSetNozzle1ToDropWithNozzle2PreDropAsync(
        double nozzle1DropPosition,
        double nozzle2PreDropPosition,
        double nozzle2PreDropPulses,
        CancellationToken cancellationToken)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var targets = new Dictionary<int, double>
        {
            [FirstSetNozzle1ZHardwareAxisNo] = nozzle1DropPosition,
            [FirstSetNozzle2ZHardwareAxisNo] = nozzle2PreDropPosition
        };
        SetFirstSetPositionStatus(
            $"Z1正在下降到放料位 {nozzle1DropPosition:0.###}；" +
            $"Z2同步沿+Z执行放料预下降 {nozzle2PreDropPulses:0.###} pulse " +
            $"至 {nozzle2PreDropPosition:0.###}…",
            true);

        var actual = await motionController.MoveAxesAbsoluteAsync(
            targets,
            cancellationToken,
            allowedMovingAxisNos: FirstSetProductionPeerAxisNos,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverrides: GetProductionAxisVelocities(targets.Keys));
        var actualByAxis = actual.ToDictionary(item => item.HardwareAxisNo);
        SetFirstSetPositionStatus(
            $"Z1已到放料位 {actualByAxis[FirstSetNozzle1ZHardwareAxisNo].FeedbackPosition:0.###}；" +
            $"Z2已完成放料预下降 {actualByAxis[FirstSetNozzle2ZHardwareAxisNo].FeedbackPosition:0.###} pulse。",
            true);
    }

    private async Task MoveNozzleZToAsync(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        double targetPosition,
        string positionName,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        using var diagnosticStep = _productionDiagnostics?.Begin($"Z-{axisSet}-{nozzleNumber}",
            $"{positionName} target={targetPosition}");
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var zHardwareAxisNo = GetNozzleZHardwareAxisNo(axisSet, nozzleNumber);
        var zVelocity = GetProductionAxisMotionSettings(zHardwareAxisNo).RunVelocity;
        SetFirstSetPositionStatus(
            $"Z{nozzleNumber}正在移动到{positionName}{targetPosition:0.###} pulse，速度 {zVelocity:0.###}…",
            true);

        var result = await motionController.MoveAxesAbsoluteAsync(
            new Dictionary<int, double> { [zHardwareAxisNo] = targetPosition },
            cancellationToken,
            allowedMovingAxisNos: allowedMovingAxisNos ?? GetProductionPeerAxisNos(axisSet),
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: zVelocity);
        var actual = result.Single();
        SetFirstSetPositionStatus(
            $"Z{nozzleNumber}已到{positionName}：{actual.FeedbackPosition:0.###} pulse。",
            true);
    }

    private async Task MoveNozzleRToAsync(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        double targetPosition,
        string positionName,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(targetPosition))
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetPosition),
                $"R{nozzleNumber}的{positionName}目标必须是有效数字。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var rHardwareAxisNo = GetNozzleRHardwareAxisNo(axisSet, nozzleNumber);
        var rVelocity = GetProductionAxisMotionSettings(rHardwareAxisNo).RunVelocity;
        SetFirstSetPositionStatus(
            $"R{nozzleNumber}正在移动到{positionName}{targetPosition:0.###} pulse，速度 {rVelocity:0.###}…",
            true);

        var result = await motionController.MoveAxesAbsoluteAsync(
            new Dictionary<int, double> { [rHardwareAxisNo] = targetPosition },
            cancellationToken,
            allowedMovingAxisNos: GetProductionPeerAxisNos(axisSet),
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: rVelocity);
        var actual = result.Single();
        SetFirstSetPositionStatus(
            $"R{nozzleNumber}已到{positionName}：{actual.FeedbackPosition:0.###} pulse。",
            true);
    }

    private async Task MoveCorrectedPlacementAxesAsync(
        int nozzleNumber,
        string positionName,
        LowerCameraPlacementTarget target,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        if (nozzleNumber is not (1 or 2))
        {
            throw new ArgumentOutOfRangeException(
                nameof(nozzleNumber),
                "纠偏放料的吸嘴编号只能是 1 或 2。");
        }

        if (!double.IsFinite(target.X) ||
            !double.IsFinite(target.Y) ||
            !double.IsFinite(target.R) ||
            !double.IsFinite(target.RCorrectionPulses))
        {
            throw new ArgumentOutOfRangeException(
                nameof(target),
                $"{positionName}的纠偏 X/Y/R 目标必须是有效数字。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);

        var xHardwareAxisNo = VisionCalibrationService.FirstSetXHardwareAxisNo;
        var yHardwareAxisNo = VisionCalibrationService.FirstSetYHardwareAxisNo;
        var rHardwareAxisNo = GetNozzleRHardwareAxisNo(
            VisionCalibrationAxisSet.First,
            nozzleNumber);
        var targets = new Dictionary<int, double>
        {
            [xHardwareAxisNo] = target.X,
            [yHardwareAxisNo] = target.Y,
            [rHardwareAxisNo] = target.R
        };

        SetFirstSetPositionStatus(
            $"正在同步移动{positionName}纠偏目标：X={target.X:0.###}，Y={target.Y:0.###}，" +
            $"R{nozzleNumber}={target.R:0.###} pulse" +
            $"（本次角度纠偏 {target.RCorrectionPulses:0.###} pulse）…",
            true);
        var actual = await motionController.MoveAxesAbsoluteAsync(
            targets,
            cancellationToken,
            allowedMovingAxisNos: allowedMovingAxisNos ??
                                  GetProductionPeerAxisNos(VisionCalibrationAxisSet.First),
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverrides: GetProductionAxisVelocities(targets.Keys));
        var actualByAxis = actual.ToDictionary(item => item.HardwareAxisNo);

        SetFirstSetPositionStatus(
            $"{positionName}纠偏目标已同步到位：" +
            $"X={actualByAxis[xHardwareAxisNo].FeedbackPosition:0.###}，" +
            $"Y={actualByAxis[yHardwareAxisNo].FeedbackPosition:0.###}，" +
            $"R{nozzleNumber}={actualByAxis[rHardwareAxisNo].FeedbackPosition:0.###} pulse。",
            true);
    }

    private async Task EnsureActiveSetNozzlesAtSafeZAsync(CancellationToken cancellationToken)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var axisSet = _productionAxisSet ?? _visionCalibration.ActiveAxisSet;
        var positions = GetProductionZPositions();
        var nozzle1SafePosition = positions.Resolve(axisSet, 1).Safe;
        var nozzle2SafePosition = positions.Resolve(axisSet, 2).Safe;
        var z1HardwareAxisNo = GetNozzleZHardwareAxisNo(axisSet, 1);
        var z2HardwareAxisNo = GetNozzleZHardwareAxisNo(axisSet, 2);
        var zVelocities = GetProductionAxisVelocities(
            [z1HardwareAxisNo, z2HardwareAxisNo]);
        SetFirstSetPositionStatus(
            $"XY放料前正在确认 Z1={nozzle1SafePosition:0.###}、Z2={nozzle2SafePosition:0.###} 的安全位…",
            true);

        await motionController.MoveAxesAbsoluteAsync(
            new Dictionary<int, double>
            {
                [z1HardwareAxisNo] = nozzle1SafePosition,
                [z2HardwareAxisNo] = nozzle2SafePosition
            },
            cancellationToken,
            allowedMovingAxisNos: GetProductionPeerAxisNos(axisSet),
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverrides: zVelocities);

        SetFirstSetPositionStatus(
            $"Z1已到 {nozzle1SafePosition:0.###}、Z2已到 {nozzle2SafePosition:0.###} pulse 安全位，允许 XY 前往位置1/2。",
            true);
    }

    private ProductionZPositions GetProductionZPositions()
    {
        return _productionZPositions
            ?? throw new InvalidOperationException("本轮生产的 Z 轴高度参数尚未锁定。");
    }

    private ProductionZDwellTimes GetProductionZDwellTimes()
    {
        return _productionZDwellTimes ?? ReadProductionZDwellTimes();
    }

    private SecondSetXyPositions GetSecondSetXyPositions()
    {
        return _secondSetXyPositions
            ?? throw new InvalidOperationException("本轮生产的第二套 XY 取料位置尚未锁定。");
    }

    private BinDropPositions GetBinDropPositions()
    {
        return _binDropPositions
            ?? throw new InvalidOperationException("本轮生产的 BIN0-BIN3 下料位置尚未锁定。");
    }

    private LowerCameraPhotoPositions GetLowerCameraPhotoPositions()
    {
        return _lowerCameraPhotoPositions
            ?? throw new InvalidOperationException("本轮生产的下相机拍照位1/2尚未锁定。");
    }

    private void SetLowerCameraLight(bool enabled)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        if (!motionController.SetDigitalOutputHardwareBit(LowerCameraLightOutputBit, enabled))
        {
            throw new InvalidOperationException(
                $"下相机光源Y{LowerCameraLightOutputBit:00}{(enabled ? "开启" : "关闭")}失败。");
        }
    }

    private void TurnOffLowerCameraLightNoThrow()
    {
        try
        {
            _motionController?.SetDigitalOutputHardwareBit(
                LowerCameraLightOutputBit,
                enabled: false);
        }
        catch
        {
            // 光源关闭兜底不能掩盖原始的视觉、运动、取消或停机原因。
        }
    }

    private async Task<LowerCameraCorrectionResults> RunLowerCameraCorrectionsAsync(
        VisualCalibrationPage visualCalibrationController,
        LowerCameraCorrectionProfile nozzle1Profile,
        LowerCameraCorrectionProfile nozzle2Profile,
        NozzlePickupBatch pickupBatch,
        CalibrationCenterPosition pickupRPositions,
        bool upperCameraCorrectionEnabled,
        CancellationToken cancellationToken)
    {
        if (VisionCalibration.XHardwareAxisNo != VisionCalibrationService.FirstSetXHardwareAxisNo ||
            VisionCalibration.YHardwareAxisNo != VisionCalibrationService.FirstSetYHardwareAxisNo)
        {
            throw new InvalidOperationException("下相机纠偏只允许使用第一套XY（轴1/2）。");
        }

        var nozzle1Required = pickupBatch.Nozzle1.HasValue;
        var nozzle2Required = pickupBatch.Nozzle2.HasValue;
        if (!nozzle1Required && !nozzle2Required)
        {
            throw new InvalidOperationException("本批没有需要下相机纠偏的吸嘴。");
        }

        var positions = GetLowerCameraPhotoPositions();
        var firstNozzleNumber = nozzle1Required ? 1 : 2;
        var firstPhotoPositionX = nozzle1Required ? positions.Position1X : positions.Position2X;
        var firstPhotoPositionY = nozzle1Required ? positions.Position1Y : positions.Position2Y;
        LowerCameraCorrectionResultText.Text =
            $"下相机纠偏：吸嘴{firstNozzleNumber}正在移动到拍照位{firstNozzleNumber}…";
        LowerCameraCorrectionResultText.Foreground =
            new SolidColorBrush(Color.FromRgb(242, 181, 68));
        SetStartProductionStatus(
            $"{(nozzle1Required && nozzle2Required ? "两个吸嘴已取料" : $"仅吸嘴{firstNozzleNumber}已取料")}，" +
            $"XY正在前往拍照位{firstNozzleNumber}({firstPhotoPositionX:0.###}, {firstPhotoPositionY:0.###})…",
            Color.FromRgb(242, 181, 68));
        await MoveToFirstLowerCameraPositionWithPickupAnglesAsync(
            $"下相机拍照位{firstNozzleNumber}",
            firstPhotoPositionX,
            firstPhotoPositionY,
            pickupBatch,
            pickupRPositions,
            upperCameraCorrectionEnabled,
            cancellationToken);
        var lightMayBeOn = true;
        try
        {
            // 只有拍照位1确认到位后才开光，并保持到拍照位2的视觉流程完成。
            SetLowerCameraLight(enabled: true);
            VisionLowerCameraCorrectionResult? nozzle1Result = null;
            string? nozzle1Error = null;
            if (nozzle1Required)
            {
                ShowLowerCameraCorrectionVisionStatus(1);
                try
                {
                    nozzle1Result = await visualCalibrationController.RunLowerCameraCorrectionAsync(
                        nozzle1Profile.RotationCenterX,
                        nozzle1Profile.RotationCenterY,
                        nozzle1Profile.CalibrationFilePath,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    nozzle1Error = exception.Message;
                }

                LowerCameraCorrectionResultText.Text = nozzle1Result is not null
                    ? $"吸嘴1：X偏差={nozzle1Result.CorrectionX:0.00000} Y偏差={nozzle1Result.CorrectionY:0.00000} " +
                      $"夹角={nozzle1Result.MeasuredAngle:0.00000}°" +
                      (nozzle2Required ? "；吸嘴2正在拍照位2纠偏…" : "；本批吸嘴2无料")
                    : $"吸嘴1纠偏失败，位置1将使用原设定坐标；" +
                      (nozzle2Required ? $"继续执行吸嘴2纠偏。{nozzle1Error}" : nozzle1Error);
                LowerCameraCorrectionResultText.Foreground = new SolidColorBrush(
                    nozzle1Result is not null
                        ? Color.FromRgb(242, 181, 68)
                        : Color.FromRgb(242, 122, 128));
            }

            VisionLowerCameraCorrectionResult? nozzle2Result = null;
            string? nozzle2Error = null;
            if (nozzle2Required)
            {
                if (nozzle1Required)
                {
                    SetStartProductionStatus(
                        nozzle1Result is not null
                            ? $"吸嘴1纠偏完成，XY正在前往拍照位2({positions.Position2X:0.###}, {positions.Position2Y:0.###})…"
                            : $"吸嘴1纠偏失败，XY仍前往拍照位2执行吸嘴2纠偏…",
                        nozzle1Result is not null
                            ? Color.FromRgb(242, 181, 68)
                            : Color.FromRgb(242, 122, 128));
                    await MovePresetPositionCoreAsync(
                        "下相机拍照位2",
                        positions.Position2X,
                        positions.Position2Y,
                        cancellationToken);
                }

                ShowLowerCameraCorrectionVisionStatus(2);
                try
                {
                    nozzle2Result = await visualCalibrationController.RunLowerCameraCorrectionAsync(
                        nozzle2Profile.RotationCenterX,
                        nozzle2Profile.RotationCenterY,
                        nozzle2Profile.CalibrationFilePath,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    nozzle2Error = exception.Message;
                }
            }

            // 两个位置都完成拍照/纠偏后立即关闭光源，再更新完成状态。
            SetLowerCameraLight(enabled: false);
            lightMayBeOn = false;

            var nozzle1Summary = !nozzle1Required
                ? "吸嘴1本批无料，已跳过"
                : nozzle1Result is not null
                    ? $"吸嘴1 X偏差={nozzle1Result.CorrectionX:0.00000} Y偏差={nozzle1Result.CorrectionY:0.00000} " +
                      $"夹角={nozzle1Result.MeasuredAngle:0.00000}°"
                    : $"吸嘴1纠偏失败，位置1使用原设定坐标（{nozzle1Error}）";
            var nozzle2Summary = !nozzle2Required
                ? "吸嘴2本批无料，已跳过"
                : nozzle2Result is not null
                    ? $"吸嘴2 X偏差={nozzle2Result.CorrectionX:0.00000} Y偏差={nozzle2Result.CorrectionY:0.00000} " +
                      $"夹角={nozzle2Result.MeasuredAngle:0.00000}°"
                    : $"吸嘴2纠偏失败，位置2使用原设定坐标（{nozzle2Error}）";
            var hasFailure =
                (nozzle1Required && nozzle1Result is null) ||
                (nozzle2Required && nozzle2Result is null);
            LowerCameraCorrectionResultText.Text =
                $"下相机纠偏｜{nozzle1Summary}｜{nozzle2Summary}";
            LowerCameraCorrectionResultText.Foreground = new SolidColorBrush(
                hasFailure
                    ? Color.FromRgb(242, 122, 128)
                    : Color.FromRgb(73, 209, 125));
            BlobInspectionImageStatusText.Text = hasFailure ? "纠偏失败" : "纠偏完成";
            BlobInspectionImageStatusText.Foreground = new SolidColorBrush(
                hasFailure
                    ? Color.FromRgb(242, 122, 128)
                    : Color.FromRgb(73, 209, 125));
            SetStartProductionStatus(
                hasFailure
                    ? $"下相机纠偏部分或全部失败：失败吸嘴使用原设定位置，成功吸嘴仍应用各自纠偏。"
                    : $"下相机纠偏完成：{nozzle1Summary}；{nozzle2Summary}。",
                hasFailure
                    ? Color.FromRgb(242, 122, 128)
                    : Color.FromRgb(73, 209, 125));
            return new LowerCameraCorrectionResults(
                nozzle1Result,
                nozzle1Error,
                nozzle2Result,
                nozzle2Error,
                nozzle1Required,
                nozzle2Required);
        }
        finally
        {
            if (lightMayBeOn)
            {
                TurnOffLowerCameraLightNoThrow();
            }
        }
    }

    private LowerCameraCorrectionResults SkipLowerCameraCorrections(
        NozzlePickupBatch pickupBatch)
    {
        var nozzle1Summary = pickupBatch.Nozzle1.HasValue
            ? "吸嘴1使用位置1原设定坐标"
            : "吸嘴1本批无料";
        var nozzle2Summary = pickupBatch.Nozzle2.HasValue
            ? "吸嘴2使用位置2原设定坐标"
            : "吸嘴2本批无料";
        var skippedMessage =
            $"下相机纠偏1未勾选，已跳过两个吸嘴的下相机纠偏：{nozzle1Summary}；{nozzle2Summary}。";
        LowerCameraCorrectionResultText.Text = skippedMessage;
        LowerCameraCorrectionResultText.Foreground =
            new SolidColorBrush(Color.FromRgb(159, 177, 191));
        BlobInspectionImageStatusText.Text = "下相机纠偏已跳过";
        BlobInspectionImageStatusText.Foreground =
            new SolidColorBrush(Color.FromRgb(159, 177, 191));
        SetStartProductionStatus(skippedMessage, Color.FromRgb(159, 177, 191));
        return new LowerCameraCorrectionResults(
            null,
            null,
            null,
            null,
            false,
            false);
    }

    private LowerCameraCorrectionProfile ReadLowerCameraCorrectionProfile(int nozzleNumber)
    {
        var rotationCenterX = nozzleNumber == 2
            ? _homeSettings.LowerCameraNozzle2RotationCenterX
            : _homeSettings.LowerCameraNozzle1RotationCenterX;
        var rotationCenterY = nozzleNumber == 2
            ? _homeSettings.LowerCameraNozzle2RotationCenterY
            : _homeSettings.LowerCameraNozzle1RotationCenterY;
        if (rotationCenterX is not { } centerX ||
            rotationCenterY is not { } centerY ||
            !double.IsFinite(centerX) ||
            !double.IsFinite(centerY))
        {
            throw new InvalidOperationException(
                $"参数设置中的下相机吸嘴{nozzleNumber}旋转中心不完整，请先完成该吸嘴的旋转中心计算。");
        }

        var configuredPath = nozzleNumber == 2
            ? _visionCalibration.Settings.LowerCameraNozzle2CalibrationFilePath
            : string.IsNullOrWhiteSpace(_visionCalibration.Settings.LowerCameraNozzle1CalibrationFilePath)
                ? _visionCalibration.Settings.LowerCameraCalibrationFilePath
                : _visionCalibration.Settings.LowerCameraNozzle1CalibrationFilePath;
        var calibrationPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "标定文件",
                $"下相机吸嘴{nozzleNumber}标定.xml")
            : Path.GetFullPath(configuredPath.Trim());
        if (!File.Exists(calibrationPath))
        {
            throw new FileNotFoundException(
                $"下相机吸嘴{nozzleNumber}标定文件不存在。",
                calibrationPath);
        }

        return new LowerCameraCorrectionProfile(
            calibrationPath,
            centerX,
            centerY);
    }

    private static LowerCameraPlacementTarget CalculateLowerCameraPlacementTarget(
        double configuredX,
        double configuredY,
        double originalR,
        VisionLowerCameraCorrectionResult correction,
        double rotationSign)
    {
        // 下相机返回的是把产品移回视觉中心所需的运动量，现场确认其方向
        // 与 X/Y 轴实际补偿方向一致，因此放料时叠加到示教目标上。
        var correctedX = configuredX +
                          correction.CorrectionX * LowerCameraLinearPulsePerMillimeter;
        var correctedY = configuredY +
                          correction.CorrectionY * LowerCameraLinearPulsePerMillimeter;
        var rCorrectionPulses = ConvertLowerCameraMeasuredAngleToRCorrectionPulses(
            correction.MeasuredAngle,
            rotationSign,
            out _);
        var correctedR = originalR + rCorrectionPulses;
        if (!double.IsFinite(correctedX) ||
            !double.IsFinite(correctedY) ||
            !double.IsFinite(correctedR))
        {
            throw new InvalidOperationException("纠偏换算出的X/Y/R目标无效。");
        }

        return new LowerCameraPlacementTarget(
            correctedX,
            correctedY,
            correctedR,
            rCorrectionPulses);
    }

    private static int GetNozzleZHardwareAxisNo(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber)
    {
        return (axisSet, nozzleNumber) switch
        {
            (VisionCalibrationAxisSet.First, 1) => FirstSetNozzle1ZHardwareAxisNo,
            (VisionCalibrationAxisSet.First, 2) => FirstSetNozzle2ZHardwareAxisNo,
            (VisionCalibrationAxisSet.Second, 1) => SecondSetNozzle1ZHardwareAxisNo,
            (VisionCalibrationAxisSet.Second, 2) => SecondSetNozzle2ZHardwareAxisNo,
            _ => throw new ArgumentOutOfRangeException(nameof(nozzleNumber), "吸嘴编号只能是 1 或 2。")
        };
    }

    private static int GetNozzleRHardwareAxisNo(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber)
    {
        return (axisSet, nozzleNumber) switch
        {
            (VisionCalibrationAxisSet.First, 1) => FirstSetNozzle1RHardwareAxisNo,
            (VisionCalibrationAxisSet.First, 2) => FirstSetNozzle2RHardwareAxisNo,
            (VisionCalibrationAxisSet.Second, 1) => SecondSetNozzle1RHardwareAxisNo,
            (VisionCalibrationAxisSet.Second, 2) => SecondSetNozzle2RHardwareAxisNo,
            _ => throw new ArgumentOutOfRangeException(nameof(nozzleNumber), "吸嘴编号只能是 1 或 2。")
        };
    }

    private static IReadOnlyCollection<int> GetProductionPeerAxisNos(
        VisionCalibrationAxisSet axisSet)
    {
        return axisSet == VisionCalibrationAxisSet.First
            ? FirstSetProductionPeerAxisNos
            : SecondSetProductionPeerAxisNos;
    }

    private void EnableNozzleVacuum(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!SetNozzleVacuumOutputs(
                axisSet,
                nozzleNumber,
                vacuumEnabled: true,
                breakVacuumEnabled: false))
        {
            throw new InvalidOperationException($"Z{nozzleNumber}真空吸开启失败。");
        }

        SetFirstSetPositionStatus($"Z{nozzleNumber}真空吸已开启。", true);
    }

    private async Task PulseNozzleBreakVacuumAsync(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        CancellationToken cancellationToken)
    {
        using var diagnosticStep = _productionDiagnostics?.Begin($"valve-{axisSet}-{nozzleNumber}", "break vacuum");
        cancellationToken.ThrowIfCancellationRequested();
        var dwellTimes = GetProductionZDwellTimes();
        var breakPulseMilliseconds = dwellTimes.BreakVacuumMilliseconds;
        var valveSwitchDelayMilliseconds = dwellTimes.ValveSwitchDelayMilliseconds;

        // 放料必须先完全停止真空吸，再给破真空阀一个短脉冲；两阀禁止重叠开启。
        if (!SetNozzleVacuumOutputs(
                axisSet,
                nozzleNumber,
                vacuumEnabled: false,
                breakVacuumEnabled: false))
        {
            throw new InvalidOperationException($"Z{nozzleNumber}停止真空吸失败。");
        }

        SetFirstSetPositionStatus(
            $"Z{nozzleNumber}真空吸已关闭，等待阀切换 {valveSwitchDelayMilliseconds} ms…",
            true);
        await Task.Delay(valveSwitchDelayMilliseconds, cancellationToken);

        try
        {
            if (!SetNozzleVacuumOutputs(
                    axisSet,
                    nozzleNumber,
                    vacuumEnabled: false,
                    breakVacuumEnabled: true))
            {
                throw new InvalidOperationException($"Z{nozzleNumber}真空破开启失败。");
            }

            await Task.Delay(breakPulseMilliseconds, cancellationToken);
        }
        finally
        {
            if (!SetNozzleVacuumOutputs(
                    axisSet,
                    nozzleNumber,
                    vacuumEnabled: false,
                    breakVacuumEnabled: false))
            {
                throw new InvalidOperationException($"Z{nozzleNumber}真空破和真空吸关闭失败。");
            }
        }

        SetFirstSetPositionStatus(
            $"Z{nozzleNumber}已先关闭真空吸，再破真空 {breakPulseMilliseconds} ms；两阀均已关闭。",
            true);
    }

    private void SetUnloadStationBreakVacuum(int stationNumber, bool enabled)
    {
        if (_preserveIoOnEmergencyStop)
        {
            return;
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        var outputBit = stationNumber switch
        {
            SecondSetNozzle2UnloadStation => Station13BreakVacuumOutputBit,
            SecondSetNozzle1UnloadStation => Station14BreakVacuumOutputBit,
            _ => throw new ArgumentOutOfRangeException(
                nameof(stationNumber),
                "下料破真空工位只能是13或14。")
        };

        // 13/14工位的破真空输出为低电平有效：13号=Y22，14号=Y23；0=打开，1=关闭。
        if (!motionController.SetDigitalOutputHardwareBit(outputBit, !enabled))
        {
            throw new InvalidOperationException(
                $"{stationNumber}号工位真空破{(enabled ? "打开" : "关闭")}失败。");
        }

        SetFirstSetPositionStatus(
            $"{stationNumber}号工位真空破已{(enabled ? "打开" : "关闭")}：Y{outputBit:00}={(enabled ? 0 : 1)}。",
            true);
    }

    private async Task PickSecondSetNozzleFromStationAsync(
        int nozzleNumber,
        int stationNumber,
        double pickupPosition,
        double safePosition,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        await MoveNozzleZToAsync(
            VisionCalibrationAxisSet.Second,
            nozzleNumber,
            pickupPosition,
            "取料位",
            cancellationToken,
            allowedMovingAxisNos);

        SetUnloadStationBreakVacuum(stationNumber, enabled: true);
        try
        {
            EnableNozzleVacuum(
                VisionCalibrationAxisSet.Second,
                nozzleNumber,
                cancellationToken);

            var pickupDwellMilliseconds = GetProductionZDwellTimes().PickupMilliseconds;
            SetFirstSetPositionStatus(
                $"Z{nozzleNumber}真空吸已开启，保持 {pickupDwellMilliseconds} ms 等待吸附稳定…",
                true);
            await Task.Delay(pickupDwellMilliseconds, cancellationToken);

            await MoveNozzleZToAsync(
                VisionCalibrationAxisSet.Second,
                nozzleNumber,
                safePosition,
                "安全位",
                cancellationToken,
                allowedMovingAxisNos);
        }
        finally
        {
            // 正常流程在Z轴回到安全高度后才关闭工位破真空。
            SetUnloadStationBreakVacuum(stationNumber, enabled: false);
        }
    }

    private async Task<SecondSetNozzle2PickupOverlapTasks>
        PickSecondSetNozzle2UntilXyReleaseAsync(
            double pickupPosition,
            double safePosition,
            CancellationToken cancellationToken)
    {
        await MoveNozzleZToAsync(
            VisionCalibrationAxisSet.Second,
            2,
            pickupPosition,
            "取料位",
            cancellationToken);

        SetUnloadStationBreakVacuum(SecondSetNozzle2UnloadStation, enabled: true);
        Task nozzle2SafeTask = Task.CompletedTask;
        Task nozzle1PreDropTask = Task.CompletedTask;
        try
        {
            EnableNozzleVacuum(
                VisionCalibrationAxisSet.Second,
                2,
                cancellationToken);
            var pickupDwellMilliseconds = GetProductionZDwellTimes().PickupMilliseconds;
            SetFirstSetPositionStatus(
                $"下料Z2真空吸已开启，保持 {pickupDwellMilliseconds} ms 等待吸附稳定…",
                true);
            await Task.Delay(pickupDwellMilliseconds, cancellationToken);

            // 吸附稳定后不再等待Z2上升量：Z2回安全位、Z1预下降和XY去14工位立即并行。
            nozzle2SafeTask = CompleteSecondSetNozzle2SafeAndReleaseStationAsync(
                safePosition,
                cancellationToken);
            var preDropPulses = GetProductionZDwellTimes().SecondSetNozzle1PreDropPulses;
            nozzle1PreDropTask = preDropPulses > 0
                ? MoveNozzleZToAsync(
                    VisionCalibrationAxisSet.Second,
                    1,
                    GetProductionZPositions().SecondSetNozzle1.Safe + preDropPulses,
                    "预下降位",
                    cancellationToken,
                    SecondSetPickupOverlapPeerAxisNos)
                : Task.CompletedTask;
            SetFirstSetPositionStatus(
                preDropPulses > 0
                    ? $"下料Z2吸附完成，XY立即放行；Z2沿-Z回安全位，Z1同时沿+Z预下降 {preDropPulses:0.###} pulse。"
                    : "下料Z2吸附完成，XY立即放行；Z2继续沿-Z回安全位。",
                true);
            return new SecondSetNozzle2PickupOverlapTasks(
                nozzle2SafeTask,
                nozzle1PreDropTask);
        }
        catch
        {
            await ObserveTaskNoThrowAsync(nozzle2SafeTask);
            await ObserveTaskNoThrowAsync(nozzle1PreDropTask);
            SetUnloadStationBreakVacuum(SecondSetNozzle2UnloadStation, enabled: false);
            throw;
        }
    }

    private async Task CompleteSecondSetNozzle2SafeAndReleaseStationAsync(
        double safePosition,
        CancellationToken cancellationToken)
    {
        try
        {
            await MoveNozzleZToAsync(
                VisionCalibrationAxisSet.Second,
                2,
                safePosition,
                "安全位",
                cancellationToken,
                SecondSetPickupOverlapPeerAxisNos);
        }
        finally
        {
            SetUnloadStationBreakVacuum(SecondSetNozzle2UnloadStation, enabled: false);
        }
    }

    private void CloseAllActiveSetNozzleVacuumOutputs()
    {
        CloseAllNozzleVacuumOutputs(_productionAxisSet ?? _visionCalibration.ActiveAxisSet);
    }

    private void CloseAllNozzleVacuumOutputs(VisionCalibrationAxisSet axisSet)
    {
        if (_preserveIoOnEmergencyStop)
        {
            return;
        }

        var z1Closed = false;
        var z2Closed = false;
        Exception? firstFailure = null;
        try
        {
            z1Closed = SetNozzleVacuumOutputs(
                axisSet,
                1,
                vacuumEnabled: false,
                breakVacuumEnabled: false);
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        try
        {
            z2Closed = SetNozzleVacuumOutputs(
                axisSet,
                2,
                vacuumEnabled: false,
                breakVacuumEnabled: false);
        }
        catch (Exception exception)
        {
            firstFailure ??= exception;
        }

        if (firstFailure is not null)
        {
            throw new InvalidOperationException("Z1/Z2真空吸和真空破关闭失败，已尝试关闭当前轴组的全部真空 IO。", firstFailure);
        }

        if (!z1Closed || !z2Closed)
        {
            throw new InvalidOperationException("Z1/Z2真空吸和真空破关闭失败。");
        }

        SetFirstSetPositionStatus("Z1/Z2真空吸和真空破已全部关闭。", true);
    }

    private void CloseAllActiveSetNozzleVacuumOutputsNoThrow()
    {
        foreach (var axisSet in new[] { VisionCalibrationAxisSet.First, VisionCalibrationAxisSet.Second })
        {
            CloseNozzleVacuumOutputsNoThrow(axisSet);
        }
    }

    private void CloseNozzleVacuumOutputsNoThrow(VisionCalibrationAxisSet axisSet)
    {
        try
        {
            CloseAllNozzleVacuumOutputs(axisSet);
        }
        catch
        {
            // 收尾兜底不能掩盖原始停止或故障原因，另一套轴组仍需继续尝试关闭。
        }
    }

    /// <summary>
    /// 从视觉标定页的共享配置中加载第一套 XY 标定文件。
    /// </summary>
    private VisionCalibrationSnapshot GetFirstSetCalibrationFileSnapshot()
    {
        var snapshot = _visionCalibration.GetSnapshot();
        if (string.IsNullOrWhiteSpace(snapshot.CalibrationFilePath))
        {
            throw new InvalidOperationException("第一套 XY 尚未设置标定文件。");
        }

        if (!snapshot.CalibrationFileExists)
        {
            throw new FileNotFoundException(
                "第一套 XY 标定文件不存在，请先在视觉标定页完成九点标定。",
                snapshot.CalibrationFilePath);
        }

        return snapshot;
    }

    /// <summary>
    /// 加载需要同时使用标定文件和双吸嘴偏移的完整配置。
    /// </summary>
    private VisionCalibrationSnapshot GetFirstSetCalibrationSnapshot()
    {
        var snapshot = GetFirstSetCalibrationFileSnapshot();
        if (!snapshot.CalibrationProfileExists)
        {
            throw new FileNotFoundException(
                "第一套 XY 配置尚未保存，请先在视觉标定页完成双吸嘴验证并保存配置。",
                snapshot.CalibrationProfilePath);
        }

        return snapshot;
    }

    /// <summary>
    /// 每轮由第一套XY向1/2工位上两个新料；13/14到达收料节拍时，第二套XY并行完成双吸嘴收料。
    /// 缓存已空时XY回中心拍照，仍有缓存时从放料位直接去吸料；
    /// DD固定推进两个工位，停稳即可上下料，末次测试并行完成。
    /// </summary>
    private async void StartProduction_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Task activeSecondSetUnloadTask = Task.CompletedTask;
        Task activeSecondSetPickupTask = Task.CompletedTask;
        Task<CalibrationCenterPosition>? activeFirstSetReturnToCenterTask = null;
        Task<CarouselAdvanceResult>? activeCarouselAdvanceTask = null;
        Task activeFeederVibrationTask = Task.CompletedTask;
        Task<bool>? activeFeederLightOnTask = null;
        Task<int> activeFinalTestTask = Task.FromResult(0);

        // 如果当前已经在连续生产，再次点击按钮用于在安全节点暂停或继续。
        if (_startSequenceRunning)
        {
            // 一键收料共用生产取消和安全门，但不允许“开始运行”按钮改变它的节拍。
            if (_oneKeyCollectRunning)
            {
                return;
            }

            if (_productionPauseRequested)
            {
                ResumeProduction();
            }
            else
            {
                RequestProductionPause();
            }

            // 本次点击只切换现有流程的暂停状态，不创建新的生产任务。
            return;
        }

        // 只要已有其它运动命令在执行，就不允许启动连续生产，避免多个轴命令互相抢控制权。
        if (_oneKeyResetRunning ||
            _presetPositionMoveRunning)
        {
            // 当前设备还没空下来，直接忽略本次开始请求。
            return;
        }

        // 在生产初始化和清理逻辑之外检查，拒绝启动时不改变真空等输出。
        try
        {
            PrepareProductionBatch();
            var controller = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            controller.EnsureProductionZStartSafe(ReadProductionZSafePositions());
        }
        catch (Exception exception)
        {
            SetStartProductionStatus($"开始流程失败：{exception.Message}", Color.FromRgb(242, 122, 128));
            MessageBox.Show(Window.GetWindow(this), exception.Message, "生产启动检查",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        try
        {
            // 连续生产必须依赖运动控制页面，未绑定时直接给出明确错误。
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");

            // 连续生产必须依赖视觉标定页面，未绑定时直接给出明确错误。
            var visualCalibrationController = _visualCalibrationController
                ?? throw new InvalidOperationException("主页尚未连接视觉标定组件。");

            // 自动上料和下相机流程固定使用第一套 XY/Z/R，避免误选第二套后驱动错轴。
            if (_visionCalibration.ActiveAxisSet != VisionCalibrationAxisSet.First)
            {
                throw new InvalidOperationException("自动生产必须先在视觉标定页选择第一套XY（轴1/2）。");
            }

            // 启动前锁定全部逐轴参数，并把控制卡曲线参数应用到对应硬件轴。
            _productionAxisMotionSettings = ReadProductionAxisMotionSettings();
            ApplyProductionAxisMotionSettings(
                motionController,
                _productionAxisMotionSettings);
            _testStationSettings = ReadTestStationSettings();
            _testStationDwellMilliseconds = ReadTestStationDwellMilliseconds();
            _testRetryCount = ReadTestRetryCount();
            _sm7110AcceptanceRange = IsSM7110TestEnabled() ? ReadSM7110AcceptanceRange() : null;
            EnsureAssignedTestInstrumentsConnected();
            var velocity = GetProductionAxisMotionSettings(
                VisionCalibrationService.FirstSetXHardwareAxisNo).RunVelocity;
            var firstSetYVelocity = GetProductionAxisMotionSettings(
                VisionCalibrationService.FirstSetYHardwareAxisNo).RunVelocity;

            // 生产启动时一次性校验并锁定两套 Z 轴取料、放料和安全高度。
            _productionZPositions = ReadProductionZPositions();
            _productionZDwellTimes = ReadProductionZDwellTimes();

            // 第二套取料前XY位置同样在启动时锁定，运行中修改不会影响当前生产轮次。
            _secondSetXyPositions = ReadSecondSetXyPositions();

            // BIN配置点是两个吸嘴的中间位置，启动时锁定，避免运行中修改导致下料点变化。
            _binDropPositions = ReadBinDropPositions();

            // 上、下相机纠偏开关在启动时一次性锁定，运行中修改不影响当前生产。
            var visionPickupCount = ParseVisionPickupCount(
                VisionPickupCountTextBox.Text,
                "单次视觉抓取颗数");
            var upperCameraCorrectionEnabled = _homeSettings.UpperCameraCorrectionEnabled ?? true;
            var lowerCameraCorrectionEnabled = _homeSettings.LowerCameraCorrectionEnabled ?? true;
            var lowerCameraRotationDelayEnabled = _homeSettings.LowerCameraRotationDelayEnabled;
            // 固定补偿在启动时锁定，运行中不重新读取界面或配方。
            var observationCompensation = ReadObservationCompensationFromInputs();
            _productionXyLinearInterpolationEnabled =
                _homeSettings.XyLinearInterpolationEnabled ?? false;
            _lowerCameraPhotoPositions = lowerCameraCorrectionEnabled
                ? ReadLowerCameraPhotoPositions()
                : null;
            var lowerCameraNozzle1Profile = lowerCameraCorrectionEnabled
                ? ReadLowerCameraCorrectionProfile(1)
                : null;
            var lowerCameraNozzle2Profile = lowerCameraCorrectionEnabled
                ? ReadLowerCameraCorrectionProfile(2)
                : null;

            // 在任何轴开始运动前读取并验证完整自动流程参数，避免流程中途才发现输入缺失。
            var position1X = ParseFiniteCoordinate(PresetPosition1XTextBox.Text, "位置 1 X 轴绝对脉冲");

            // 读取位置1的 Y 轴目标脉冲。
            var position1Y = ParseFiniteCoordinate(PresetPosition1YTextBox.Text, "位置 1 Y 轴绝对脉冲");

            // 读取位置2的 X 轴目标脉冲。
            var position2X = ParseFiniteCoordinate(PresetPosition2XTextBox.Text, "位置 2 X 轴绝对脉冲");

            // 读取位置2的 Y 轴目标脉冲。
            var position2Y = ParseFiniteCoordinate(PresetPosition2YTextBox.Text, "位置 2 Y 轴绝对脉冲");

            // DD 马达每次转动的脉冲固定写在代码中，不再从界面输入读取。
            var axis0PulseDistance = DdMotorPulsePerTurn;

            // 标定文件只在开始动作被明确触发后检查；路径失效时让用户重新选择一次。
            // 主页需要找芯片时按需加载桌面的“新纳方案.sol”，标定页离开后方案会关闭。
            var calibrationFile = GetOrSelectFirstSetCalibrationFile();

            // 从第一套九点标定文件中读取机械中心点，作为每轮开始前的初始位置。
            var center = ReadFirstSetCalibrationCenter(calibrationFile.FilePath);

            // 如果本次是用户重新选择的标定文件，就把新路径写回设置。
            if (calibrationFile.WasSelected)
            {
                // 保存新的标定文件路径，后续启动时优先复用。
                if (_visionCalibration.ActiveAxisSet == VisionCalibrationAxisSet.Second)
                {
                    _visionCalibration.Settings.SecondCalibrationFilePath = calibrationFile.FilePath;
                }
                else
                {
                    _visionCalibration.Settings.CalibrationFilePath = calibrationFile.FilePath;
                }

                // 持久化视觉标定设置。
                _visionCalibration.Save();
            }

            // 创建本轮连续生产专用的取消源，停止按钮和急停都会通过它通知循环退出。
            _productionCancellation = new CancellationTokenSource();
            _preserveIoOnEmergencyStop = false;

            // 创建完成信号，方便页面切换或停用时等待生产流程完全收尾。
            _productionCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // 清空停止标记，表示新的连续生产流程还没有收到停止请求。
            _productionStopRequested = false;
            _productionPauseRequested = false;
            _productionResumeSignal = null;

            // 锁定本轮生产所使用的轴组，防止运行中切换视觉页时误写另一套真空 IO。
            _productionAxisSet = _visionCalibration.ActiveAxisSet;

            // 标记连续生产已进入运行状态。
            _startSequenceRunning = true;
            _productionDiagnostics = new ProductionDiagnosticSession(Dispatcher, () =>
                $"paused={_productionPauseRequested};stop={_productionStopRequested};" +
                (_motionController?.CaptureProductionDiagnosticState() ?? "no motion controller"));
            ResetUphTracking();

            // 刷新主页按钮状态，把“开始运行”切成“停止循环”，并锁住其它会冲突的操作。
            UpdateHomeCommandState();

            // 正式启动前先复用一键收料的完整16工位清料节拍，随后让DD重新寻找原点并
            // 确认编码器回到0。两步严格串行，任一步失败都不会进入原生产启动流程。
            motionController.EnsureDdTestStationsSafe();
            SetStartProductionStatus(
                "启动准备 1/3：正在执行一键收料，DD将转满16个工位…",
                Color.FromRgb(242, 181, 68));
            await RunOneKeyCollectCoreAsync(
                _productionCancellation.Token,
                SetStartProductionStatus,
                "启动收料");

            SetStartProductionStatus(
                "启动准备 2/3：一键收料完成，DD马达正在回0…",
                Color.FromRgb(242, 181, 68));
            await motionController.HomeDdAxisAsync(_productionCancellation.Token);
            SetStartProductionStatus(
                "启动准备 2/3：DD马达已回0，正在进入原启动准备流程…",
                Color.FromRgb(73, 209, 125));

            // 收料完成且DD回0后，软件工位状态与空盘物理状态重新对齐。
            _carouselStations = CreateCarouselStationStates();
            UpdateCarouselStationDisplay(_carouselStations);

            // 先清空旧画面，并在启动轴动作期间并行预热主页视觉显示。
            // 后续每轮XY到拍照位时直接执行找芯片流程，不再重复激活和布局VisionMaster窗口。
            ClearBlobInspectionResult();
            var prepareInspectionViewTask = PrepareBlobInspectionVisionDisplayAsync(
                visualCalibrationController);

            // 启动生产前先让R1/R2回原，同时经过位置2这个安全过渡点。
            // XY仍必须严格先走Y、确认到位后再走X；视觉预热与这些动作并行。
            var productionAxes = VisionCalibration;
            var startupAxesTask = PrepareProductionStartupAxesAsync(
                motionController,
                productionAxes.XHardwareAxisNo,
                productionAxes.YHardwareAxisNo,
                position2X,
                position2Y,
                velocity,
                firstSetYVelocity,
                _productionCancellation.Token);
            await Task.WhenAll(prepareInspectionViewTask, startupAxesTask);
            var startupSafePosition = await startupAxesTask;
            await WaitIfProductionPausedAsync(_productionCancellation.Token);
            SetStartProductionStatus(
                $"启动准备完成：R1/R2已回原，XY已按Y后X到达位置2" +
                $"({startupSafePosition.ActualX:0.###}, {startupSafePosition.ActualY:0.###})，准备进入取料流程…",
                Color.FromRgb(73, 209, 125));
            StartUphTracking();

            // 从第 0 轮开始计数，进入循环后先自增为第 1 轮。
            var cycleNumber = 0;

            // 转盘工位占料状态。启动时按空盘处理；放料到 1/2 后，后续每次 DD 转动推进一个工位。
            _carouselStations = CreateCarouselStationStates();
            var carouselStations = _carouselStations;
            UpdateCarouselStationDisplay(carouselStations);
            var pendingPickupBatches = new Queue<NozzlePickupBatch>();
            var vibrateAfterPickupCachePlaced = false;
            var singleChipRemainsAfterPickupCache = false;
            var consecutiveLowMaterialVibrations = 0;
            var stopAfterCurrentBatch = false;

            // 连续生产会一直循环，直到用户请求停止或流程抛出异常。
            while (true)
            {
                await WaitIfProductionPausedAsync(_productionCancellation.Token);

                // 每轮开始前先检查是否已经收到停止请求。
                _productionCancellation.Token.ThrowIfCancellationRequested();

                // 记录当前正在执行第几轮，便于状态栏提示和现场排查。
                cycleNumber++;

                // 清空上一轮缓存的吸嘴目标，避免异常重试时使用过期坐标。
                ClearAssignedNozzleTargets();

                // 缓存为空说明本轮需要拍照。振动一结束就提前开灯，
                // 与XY回拍照位并行，避免到位后才开始等待光源通信。
                if (pendingPickupBatches.Count == 0)
                {
                    activeFeederLightOnTask ??= OpenProductionLightAfterVibrationAsync(
                        activeFeederVibrationTask,
                        _productionCancellation.Token);
                }

                CalibrationCenterPosition actual;
                if (pendingPickupBatches.Count > 0)
                {
                    // 本次复用上次拍照的绝对目标：保留放料后的XY位置，
                    // 不再绕回拍照中心，下面直接移动到本批第一个吸料目标。
                    actual = motionController.CaptureCalibrationFeedback(
                        VisionCalibration.XHardwareAxisNo,
                        VisionCalibration.YHardwareAxisNo,
                        AllowedProductionPeerAxisNos);
                    SetFirstSetPositionStatus(
                        $"XY保持放料后位置：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse；" +
                        "本轮无需拍照，将直接去吸料。",
                        true);
                }
                else if (activeFirstSetReturnToCenterTask is not null)
                {
                    // 上一轮放料后已经启动回中心；只等XY到拍照位，不等待DD完成。
                    actual = await activeFirstSetReturnToCenterTask;
                    activeFirstSetReturnToCenterTask = null;
                    SetFirstSetPositionStatus(
                        $"XY已回到中心：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse；DD可继续并行。",
                        true);
                }
                else
                {
                    // 首轮尚无后台回中心任务，按当前位置计算超时并移动到拍照中心。
                    var current = motionController.CaptureCalibrationFeedback(
                        VisionCalibration.XHardwareAxisNo,
                        VisionCalibration.YHardwareAxisNo,
                        AllowedProductionPeerAxisNos);
                    var timeoutMilliseconds = CalculateStartMoveTimeout(
                        current.ActualX,
                        current.ActualY,
                        center.X,
                        center.Y,
                        Math.Min(velocity, firstSetYVelocity));
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：XY正在回初始中心({center.X:0.###}, {center.Y:0.###})…",
                        Color.FromRgb(242, 181, 68));
                    actual = await motionController.MoveCalibrationAxesToAsync(
                        VisionCalibration.XHardwareAxisNo,
                        VisionCalibration.YHardwareAxisNo,
                        center.X,
                        center.Y,
                        velocity,
                        positionTolerance: HomePageCompletionTolerance,
                        moveTimeoutMilliseconds: timeoutMilliseconds,
                        cancellationToken: _productionCancellation.Token,
                        allowedMovingAxisNos: AllowedProductionPeerAxisNos,
                        yVelocityOverride: firstSetYVelocity,
                        linearInterpolationCoordinateSystemNo: GetFirstSetXyInterpolationCoordinateSystemNo());
                }
                await WaitIfProductionPausedAsync(_productionCancellation.Token);

                if (pendingPickupBatches.Count == 0)
                {
                    // 把“等待震动结束、最终停振、开灯及相机返回”整体作为独立任务启动。
                    // 主流程等待结果；上一批已启动的DD、测试和第二套下料任务继续完成。
                    var inspectionTask = RunProductionUpperCameraInspectionAsync(
                        visualCalibrationController,
                        actual,
                        activeFeederVibrationTask,
                        activeFeederLightOnTask,
                        cycleNumber,
                        _productionCancellation.Token);
                    _productionDiagnostics?.Track("upper-camera", inspectionTask);
                    activeFeederVibrationTask = Task.CompletedTask;
                    VisionRectangleBlobResult blobResult;
                    try
                    {
                        // 等待本轮拍照时，上一批已启动的DD/测试/下料任务可以自行完成。
                        // 不追加新的DD节拍；正常生产只有本批放料完成后才允许再次转动。
                        blobResult = await inspectionTask;
                    }
                    finally
                    {
                        activeFeederLightOnTask = null;
                    }
                    SetBlobInspectionResult(blobResult);
                    var selectedChipCount = Math.Min(
                        blobResult.Rectangles.Count,
                        visionPickupCount);

                    if (selectedChipCount == 0)
                    {
                        if (consecutiveLowMaterialVibrations >=
                            EmptyTraySingleChipVibrationThreshold)
                        {
                            SetStartProductionStatus(
                                $"连续震动{consecutiveLowMaterialVibrations}次后视野仍为0颗，" +
                                "确认料盘缺料；停止上料，正在排空转盘在制品…",
                                Color.FromRgb(242, 181, 68));

                            // 上一批的DD任务可能仍在震动期间并行执行。
                            // 先接管它产生的末次测试和第二套下料任务，再继续排空。
                            if (activeCarouselAdvanceTask is not null)
                            {
                                var carouselAdvanceResult = await activeCarouselAdvanceTask;
                                activeCarouselAdvanceTask = null;
                                activeFinalTestTask = carouselAdvanceResult.FinalTestTask;
                                activeSecondSetUnloadTask = carouselAdvanceResult.SecondSetUnloadTask;
                                activeSecondSetPickupTask = carouselAdvanceResult.SecondSetPickupTask;
                            }

                            var emptyDrainFinalTestTask = activeFinalTestTask;
                            activeFinalTestTask = Task.FromResult(0);
                            var emptyDrainSecondSetPickupTask = activeSecondSetPickupTask;
                            activeSecondSetPickupTask = Task.CompletedTask;
                            var emptyDrainSecondSetUnloadTask = activeSecondSetUnloadTask;
                            activeSecondSetUnloadTask = Task.CompletedTask;

                            await DrainCarouselAfterFeederEmptyAsync(
                                carouselStations,
                                axis0PulseDistance,
                                emptyDrainFinalTestTask,
                                emptyDrainSecondSetPickupTask,
                                emptyDrainSecondSetUnloadTask,
                                ProductionHandlingAxisNos,
                                _productionCancellation.Token);

                            SetStartProductionStatus(
                                $"连续震动{consecutiveLowMaterialVibrations}次后确认料盘无料；" +
                                "转盘全部在制品已完成测试和BIN下料，程序正常停止。",
                                Color.FromRgb(73, 209, 125));
                            return;
                        }

                        var vibrationNumber = consecutiveLowMaterialVibrations + 1;
                        SetStartProductionStatus(
                            $"视野内未找到芯片，正在执行第{vibrationNumber}/" +
                            $"{EmptyTraySingleChipVibrationThreshold}次连续震动后重拍。",
                            Color.FromRgb(242, 181, 68));
                        await RunProductionVibrationAsync(
                            cycleNumber,
                            $"视野内未找到芯片（第{vibrationNumber}次）",
                            _productionCancellation.Token);
                        consecutiveLowMaterialVibrations = vibrationNumber;
                        continue;
                    }

                    try
                    {
                        if (selectedChipCount == 1)
                        {
                            if (consecutiveLowMaterialVibrations >=
                                EmptyTraySingleChipVibrationThreshold)
                            {
                                pendingPickupBatches.Enqueue(CalculateFinalSingleChipBatch(
                                    blobResult,
                                    calibrationFile.FilePath,
                                    actual.ActualX,
                                    actual.ActualY));
                                stopAfterCurrentBatch = true;
                                SetStartProductionStatus(
                                    $"连续震动{consecutiveLowMaterialVibrations}次后视野仍只剩1颗，" +
                                    "判定缺料：改由吸嘴2取料，放到位置2后停止上料并排空转盘。",
                                    Color.FromRgb(242, 181, 68));
                            }
                            else
                            {
                                var vibrationNumber = consecutiveLowMaterialVibrations + 1;
                                SetStartProductionStatus(
                                    $"视野只剩1颗，暂不抓取；正在执行第{vibrationNumber}/" +
                                    $"{EmptyTraySingleChipVibrationThreshold}次连续震动后重拍。",
                                    Color.FromRgb(242, 181, 68));
                                await RunProductionVibrationAsync(
                                    cycleNumber,
                                    $"视野只剩1颗（第{vibrationNumber}次）",
                                    _productionCancellation.Token);
                                consecutiveLowMaterialVibrations = vibrationNumber;
                                continue;
                            }
                        }
                        else
                        {
                            consecutiveLowMaterialVibrations = 0;
                            foreach (var batch in CalculateAssignedNozzleBatches(
                                         blobResult,
                                         calibrationFile.FilePath,
                                         actual.ActualX,
                                         actual.ActualY,
                                         visionPickupCount))
                            {
                                pendingPickupBatches.Enqueue(batch);
                            }

                            vibrateAfterPickupCachePlaced = true;
                            singleChipRemainsAfterPickupCache = selectedChipCount % 2 == 1;
                            var ignoredChipCount = blobResult.Rectangles.Count - selectedChipCount;
                            SetStartProductionStatus(
                                $"找芯片返回 {blobResult.Rectangles.Count} 条，本次按原顺序取" +
                                $" {selectedChipCount} 条" +
                                (ignoredChipCount > 0
                                    ? $"（超出上限的 {ignoredChipCount} 条不缓存）"
                                    : string.Empty) +
                                (singleChipRemainsAfterPickupCache
                                    ? "；最后1颗留在料盘，前面成对物料放完后震动。"
                                    : "；本批全部放完后震动。"),
                                Color.FromRgb(73, 209, 125));
                        }
                    }
                    catch (Exception exception)
                    {
                        ClearAssignedNozzleTargets();
                        SetFirstSetPositionStatus($"吸嘴分配失败：{exception.Message}", false);
                        SetStartProductionStatus(
                            $"Blob已显示；吸嘴目标换算失败：{exception.Message}",
                            Color.FromRgb(242, 181, 68));
                        return;
                    }

                }
                else
                {
                    var cachedChipCount = pendingPickupBatches.Sum(batch => batch.Count);
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：复用上次拍照缓存，剩余 {cachedChipCount} 颗，不再拍照。",
                        Color.FromRgb(73, 209, 125));
                }

                var assignedTargets = pendingPickupBatches.Dequeue();
                var remainingCachedChipCount = pendingPickupBatches.Sum(batch => batch.Count);

                SetAssignedNozzleTargets(assignedTargets, remainingCachedChipCount);
                var nozzle1HasPart = assignedTargets.Nozzle1.HasValue;
                var nozzle2HasPart = assignedTargets.Nozzle2.HasValue;
                await WaitIfProductionPausedAsync(_productionCancellation.Token);

                Task nozzle1SafePositionTask = Task.CompletedTask;
                try
                {
                    if (nozzle1HasPart)
                    {
                        SetStartProductionStatus(
                            $"第{cycleNumber}轮：Blob识别完成，吸嘴1正在对位本批第1颗…",
                            Color.FromRgb(242, 181, 68));
                        await MoveNozzleToAssignedTargetAsync(1, _productionCancellation.Token);
                        await WaitIfProductionPausedAsync(_productionCancellation.Token);
                        nozzle1SafePositionTask = nozzle2HasPart
                            ? await PickFirstSetNozzle1UntilXyReleaseAsync(
                                _productionCancellation.Token)
                            : PickWithActiveSetNozzleAsync(1, _productionCancellation.Token);
                        if (!nozzle2HasPart)
                        {
                            await nozzle1SafePositionTask;
                        }
                        await WaitIfProductionPausedAsync(_productionCancellation.Token);
                    }

                    if (nozzle2HasPart)
                    {
                        SetStartProductionStatus(
                            nozzle1HasPart
                                ? $"第{cycleNumber}轮：Z1已达到XY放行上升量并继续回安全位，吸嘴2正在对位本批第2颗…"
                                : $"第{cycleNumber}轮：缺料收尾，吸嘴2正在对位最后1颗…",
                            Color.FromRgb(242, 181, 68));
                        await MoveNozzleToAssignedTargetAsync(
                            2,
                            _productionCancellation.Token,
                            nozzle1HasPart
                                ? FirstSetNozzle1EarlyReleasePeerAxisNos
                                : null);
                        await WaitIfProductionPausedAsync(_productionCancellation.Token);
                        await PickWithActiveSetNozzleAsync(
                            2,
                            _productionCancellation.Token,
                            nozzle1HasPart
                                ? FirstSetNozzle1EarlyReleasePeerAxisNos
                                : null);
                        await WaitIfProductionPausedAsync(_productionCancellation.Token);
                    }

                    // XY可以在Z1达到提前放行高度后移动，但去下相机和放料前仍必须等Z1完整到安全位。
                    await nozzle1SafePositionTask;
                }
                catch
                {
                    await ObserveTaskNoThrowAsync(nozzle1SafePositionTask);
                    throw;
                }

                // 放料 XY 动作的安全门：必须再次确认两根 Z 轴都在本轮配置的安全高度。
                await EnsureActiveSetNozzlesAtSafeZAsync(_productionCancellation.Token);
                await WaitIfProductionPausedAsync(_productionCancellation.Token);

                // 本次拍照缓存的最后一批已经离开振动盘，立即在后台启动下一次震动。
                // 震动与下相机纠偏、DD转动、上料及回拍照位并行，拍照前再统一确认已停振。
                if (vibrateAfterPickupCachePlaced && pendingPickupBatches.Count == 0)
                {
                    var vibrationReason = singleChipRemainsAfterPickupCache
                        ? "本次缓存成对物料已全部取完，料盘最后还剩1颗"
                        : $"本次最多{visionPickupCount}条缓存已全部取完";
                    activeFeederVibrationTask = RunProductionVibrationAsync(
                        cycleNumber,
                        vibrationReason,
                        _productionCancellation.Token);
                    consecutiveLowMaterialVibrations = singleChipRemainsAfterPickupCache ? 1 : 0;
                    vibrateAfterPickupCachePlaced = false;
                    singleChipRemainsAfterPickupCache = false;
                }

                // 保存找芯片角度粗校正之前的R轴基准。放料后两根R轴必须回到这里，
                // 不能回到已经包含粗校正量的位置，否则多轮生产会持续累加旋转。
                var pickupRPositions = motionController.CaptureCalibrationFeedback(
                    FirstSetNozzle1RHardwareAxisNo,
                    FirstSetNozzle2RHardwareAxisNo,
                    FirstSetProductionPeerAxisNos);

                // 总开关启用时依次到拍照位1/2执行两个吸嘴的下相机纠偏；
                // 关闭时两个吸嘴都跳过下相机，并继续使用各自的原设定放料坐标。
                LowerCameraCorrectionResults correctionResults;
                if (lowerCameraCorrectionEnabled)
                {
                    correctionResults = await RunLowerCameraCorrectionsAsync(
                        visualCalibrationController,
                        lowerCameraNozzle1Profile!,
                        lowerCameraNozzle2Profile!,
                        assignedTargets,
                        pickupRPositions,
                        upperCameraCorrectionEnabled,
                        _productionCancellation.Token);
                }
                else
                {
                    if (upperCameraCorrectionEnabled)
                    {
                        await ApplyUpperCameraRotationCorrectionsAsync(
                            assignedTargets,
                            pickupRPositions,
                            _productionCancellation.Token);
                    }

                    correctionResults = SkipLowerCameraCorrections(assignedTargets);
                }
                if (lowerCameraCorrectionEnabled &&
                    lowerCameraRotationDelayEnabled &&
                    correctionResults.HasAnySuccess)
                {
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：下相机纠偏完成，调试等待3秒后再执行XY移动和R轴角度纠偏…",
                        Color.FromRgb(242, 181, 68));
                    await Task.Delay(3000, _productionCancellation.Token);
                }
                await WaitIfProductionPausedAsync(_productionCancellation.Token);

                var position1Target = new LowerCameraPlacementTarget(position1X, position1Y, 0d, 0d);
                var position2Target = new LowerCameraPlacementTarget(position2X, position2Y, 0d, 0d);
                var position1TargetDescription = "原设定目标";
                var position2TargetDescription = "原设定目标";
                double? nozzle1OriginalR = null;
                double? nozzle2OriginalR = null;
                if (correctionResults is { HasAnySuccess: true } corrections)
                {
                    var originalRPositions = motionController.CaptureCalibrationFeedback(
                        FirstSetNozzle1RHardwareAxisNo,
                        FirstSetNozzle2RHardwareAxisNo,
                        FirstSetProductionPeerAxisNos);
                    if (corrections.Nozzle1 is { } nozzle1Correction)
                    {
                        position1TargetDescription = "吸嘴1纠偏目标";
                        nozzle1OriginalR = originalRPositions.ActualX;
                        position1Target = CalculateLowerCameraPlacementTarget(
                            position1X,
                            position1Y,
                            nozzle1OriginalR.Value,
                            nozzle1Correction,
                            GetLowerCameraRotationSign(1));
                    }

                    if (corrections.Nozzle2 is { } nozzle2Correction)
                    {
                        position2TargetDescription = "吸嘴2纠偏目标";
                        nozzle2OriginalR = originalRPositions.ActualY;
                        position2Target = CalculateLowerCameraPlacementTarget(
                            position2X,
                            position2Y,
                            nozzle2OriginalR.Value,
                            nozzle2Correction,
                            GetLowerCameraRotationSign(2));
                    }
                }

                position1Target = ApplyObservationCompensation(
                    position1Target, 1, nozzle1OriginalR.HasValue, observationCompensation);
                if (nozzle1OriginalR.HasValue && observationCompensation.Enabled)
                {
                    position1TargetDescription += "＋统一观测位补偿";
                }

                async Task WaitForCarouselBeforePlacementAsync(string placementName)
                {
                    if (activeCarouselAdvanceTask is null)
                    {
                        return;
                    }

                    if (!activeCarouselAdvanceTask.IsCompleted)
                    {
                        SetStartProductionStatus(
                            $"第{cycleNumber}轮：XY已到{placementName}并保持Z轴安全高度，" +
                            "正在等待DD完成两次转动及已启用测试站动作…",
                            Color.FromRgb(242, 181, 68));
                    }

                    var carouselAdvanceResult = await activeCarouselAdvanceTask;
                    activeCarouselAdvanceTask = null;
                    activeFinalTestTask = carouselAdvanceResult.FinalTestTask;
                    activeSecondSetUnloadTask = carouselAdvanceResult.SecondSetUnloadTask;
                    activeSecondSetPickupTask = carouselAdvanceResult.SecondSetPickupTask;
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：DD已完成 {carouselAdvanceResult.Turns} 次转动并停稳，" +
                        $"XY已在{placementName}，立即下压放料…",
                        Color.FromRgb(73, 209, 125));
                }

                Task nozzle1PlaceSafePositionTask = Task.CompletedTask;
                try
                {
                if (nozzle1HasPart)
                {
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：Z1/Z2均已回到配置安全位，" +
                        $"正在按{position1TargetDescription}前往1工位(X={position1Target.X:0.###}, Y={position1Target.Y:0.###}" +
                        $"{(nozzle1OriginalR.HasValue ? $", R={position1Target.R:0.###}" : string.Empty)})…",
                        Color.FromRgb(242, 181, 68));

                    if (nozzle1OriginalR.HasValue)
                    {
                        await MoveCorrectedPlacementAxesAsync(
                            1,
                            "位置 1",
                            position1Target,
                            _productionCancellation.Token);
                    }
                    else
                    {
                        await MovePresetPositionCoreAsync(
                            "位置 1",
                            position1Target.X,
                            position1Target.Y,
                            _productionCancellation.Token);
                    }
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);
                    await WaitForCarouselBeforePlacementAsync("1工位");
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);

                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：1工位已到位，Z1正在下降到配置放料位…",
                        Color.FromRgb(242, 181, 68));
                    nozzle1PlaceSafePositionTask = nozzle2HasPart
                        ? await PlaceFirstSetNozzle1UntilXyReleaseAsync(
                            _productionCancellation.Token)
                        : PlaceWithActiveSetNozzleAsync(
                            1,
                            _productionCancellation.Token);
                    if (!nozzle2HasPart)
                    {
                        await nozzle1PlaceSafePositionTask;
                        await MoveNozzleRToAsync(
                            VisionCalibrationAxisSet.First,
                            1,
                            pickupRPositions.ActualX,
                            "本批取料基准位",
                            _productionCancellation.Token);
                    }
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);
                }

                if (nozzle2HasPart)
                {
                    SetStartProductionStatus(
                        (nozzle1HasPart
                            ? $"第{cycleNumber}轮：Z1已破真空并开始沿-Z回安全位，"
                            : $"第{cycleNumber}轮：缺料收尾，") +
                        $"正在按{position2TargetDescription}前往2工位(X={position2Target.X:0.###}, Y={position2Target.Y:0.###}" +
                        $"{(nozzle2OriginalR.HasValue ? $", R={position2Target.R:0.###}" : string.Empty)})…",
                        Color.FromRgb(242, 181, 68));

                    if (nozzle2OriginalR.HasValue)
                    {
                        // 纠偏放料时将 X/Y/R2 一次下发，三轴同时运动并共同等待到位。
                        await MoveCorrectedPlacementAxesAsync(
                            2,
                            "位置 2",
                            position2Target,
                            _productionCancellation.Token,
                            nozzle1HasPart
                                ? FirstSetNozzle1EarlyReleasePeerAxisNos
                                : null);
                    }
                    else
                    {
                        await MovePresetPositionCoreAsync(
                            "位置 2",
                            position2Target.X,
                            position2Target.Y,
                            _productionCancellation.Token,
                            nozzle1HasPart
                                ? FirstSetNozzle1EarlyReleasePeerAxisNos
                                : null);
                    }
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);
                    await WaitForCarouselBeforePlacementAsync("2工位");
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);

                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：2工位已到位，Z2正在下降到配置放料位…",
                        Color.FromRgb(242, 181, 68));
                    await PlaceWithActiveSetNozzleAsync(
                        2,
                        _productionCancellation.Token,
                        nozzle1HasPart
                            ? FirstSetNozzle1EarlyReleasePeerAxisNos
                            : null);
                    await nozzle1PlaceSafePositionTask;
                    if (nozzle1HasPart)
                    {
                        await MoveNozzleRToAsync(
                            VisionCalibrationAxisSet.First,
                            1,
                            pickupRPositions.ActualX,
                            "本批取料基准位",
                            _productionCancellation.Token);
                    }
                    await MoveNozzleRToAsync(
                        VisionCalibrationAxisSet.First,
                        2,
                        pickupRPositions.ActualY,
                        "本批取料基准位",
                        _productionCancellation.Token);
                }
                await nozzle1PlaceSafePositionTask;
                }
                catch
                {
                    await ObserveTaskNoThrowAsync(nozzle1PlaceSafePositionTask);
                    throw;
                }
                CloseAllActiveSetNozzleVacuumOutputs();
                await WaitIfProductionPausedAsync(_productionCancellation.Token);

                if (nozzle1HasPart)
                {
                    carouselStations[1].SetLoaded();
                }
                if (nozzle2HasPart)
                {
                    carouselStations[2].SetLoaded();
                }
                UpdateCarouselStationDisplay(carouselStations);

                if (stopAfterCurrentBatch)
                {
                    SetStartProductionStatus(
                        $"连续震动{consecutiveLowMaterialVibrations}次后仍剩的最后1颗" +
                        "已由吸嘴2放到位置2，确认料盘缺料；停止继续上料，正在回中心并排空转盘…",
                        Color.FromRgb(242, 181, 68));

                    // 缺料只允许停止第一套继续拍照上料，不能直接退出主循环。
                    // 先把第一套XY移出1/2上料位，再继续驱动DD、测试站和第二套下料，
                    // 直到转盘缓存及第二套吸嘴上的最后一批产品全部进入BIN。
                    activeFirstSetReturnToCenterTask = StartFirstSetReturnToCenterAsync(
                        motionController,
                        center,
                        velocity,
                        firstSetYVelocity,
                        _productionCancellation.Token);
                    _ = await activeFirstSetReturnToCenterTask;
                    activeFirstSetReturnToCenterTask = null;

                    var drainFinalTestTask = activeFinalTestTask;
                    activeFinalTestTask = Task.FromResult(0);
                    var drainSecondSetPickupTask = activeSecondSetPickupTask;
                    activeSecondSetPickupTask = Task.CompletedTask;
                    var drainSecondSetUnloadTask = activeSecondSetUnloadTask;
                    activeSecondSetUnloadTask = Task.CompletedTask;

                    await DrainCarouselAfterFeederEmptyAsync(
                        carouselStations,
                        axis0PulseDistance,
                        drainFinalTestTask,
                        drainSecondSetPickupTask,
                        drainSecondSetUnloadTask,
                        ProductionHandlingAxisNos,
                        _productionCancellation.Token);

                    SetStartProductionStatus(
                        $"连续震动{consecutiveLowMaterialVibrations}次后确认料盘缺料；" +
                        "最后1颗及转盘全部在制品已完成测试和BIN下料，程序正常停止。",
                        Color.FromRgb(73, 209, 125));
                    return;
                }

                var nextCycleNeedsPhoto = pendingPickupBatches.Count == 0;
                if (!activeSecondSetPickupTask.IsCompleted)
                {
                    SetStartProductionStatus(
                        nextCycleNeedsPhoto
                            ? $"第{cycleNumber}轮：第一套已放完并立即回中心；DD只等待第二套从13/14工位吸走两个料，不等待第二套放料…"
                            : $"第{cycleNumber}轮：第一套已放完，下轮复用拍照缓存并直接去吸料；" +
                              "DD只等待第二套从13/14工位吸走两个料…",
                        Color.FromRgb(242, 181, 68));
                }

                if (nextCycleNeedsPhoto)
                {
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮第一套放料完成，XY立即回中心准备下一轮拍照；第二套继续独立收料…",
                        Color.FromRgb(73, 209, 125));
                    activeFirstSetReturnToCenterTask = StartFirstSetReturnToCenterAsync(
                        motionController,
                        center,
                        velocity,
                        firstSetYVelocity,
                        _productionCancellation.Token);
                }
                else
                {
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮第一套放料完成；下一轮无需拍照，XY不回中心并将直接去吸料；" +
                        "第二套继续独立收料…",
                        Color.FromRgb(73, 209, 125));
                }

                // 第二套本批取料信号移交给DD安全门；主循环直接进入下一轮。
                // 只有拍照缓存用完时才回中心重拍，否则从当前位置直接去吸料。
                var requiredFinalTestTask = activeFinalTestTask;
                activeFinalTestTask = Task.FromResult(0);
                var requiredSecondSetPickupTask = activeSecondSetPickupTask;
                activeSecondSetPickupTask = Task.CompletedTask;
                var requiredPreviousSecondSetUnloadTask = activeSecondSetUnloadTask;
                activeSecondSetUnloadTask = Task.CompletedTask;
                var xyMoveAwayDelayTask = Task.Delay(
                    MoveAwayBeforeDdMilliseconds,
                    _productionCancellation.Token);
                activeCarouselAdvanceTask = StartCarouselAfterSafetyBarrierAsync(
                    requiredFinalTestTask,
                    requiredSecondSetPickupTask,
                    requiredPreviousSecondSetUnloadTask,
                    xyMoveAwayDelayTask,
                    carouselStations,
                    axis0PulseDistance,
                    ProductionHandlingAxisNos,
                    _productionCancellation.Token);
                _productionDiagnostics?.Track("carousel-advance", activeCarouselAdvanceTask);

                SetStartProductionStatus(
                    nextCycleNeedsPhoto
                        ? $"第{cycleNumber}轮放料完成，DD在第二套取料完成后固定转动两次；" +
                          $"已启用测试站同步执行，XY立即准备第{cycleNumber + 1}轮拍照吸料…"
                        : $"第{cycleNumber}轮放料完成，DD在第二套取料完成后固定转动两次；" +
                          $"已启用测试站同步执行，XY第{cycleNumber + 1}轮不拍照、直接去吸料…",
                    Color.FromRgb(73, 209, 125));

                // 主动让出一次 UI 调度机会，避免连续循环把界面刷新挤在一起。
                await Task.Yield();
            }
        }
        catch (OperationCanceledException)
        {
            // 用户正常停止时显示绿色完成状态，不按异常处理。
            SetStartProductionStatus(
                "连续运行已停止。",
                Color.FromRgb(73, 209, 125));
        }
        catch (Exception exception)
        {
            // 非取消异常统一显示为启动或运行失败，保留底层异常信息方便排查。
            _productionDiagnostics?.Write("PRODUCTION-FAILED", exception.ToString());
            SetStartProductionStatus(
                $"开始流程失败：{exception.Message}",
                Color.FromRgb(242, 122, 128));
        }
        finally
        {
            _productionCancellation?.Cancel();
            _productionResumeSignal?.TrySetResult(true);
            await ObserveTaskNoThrowAsync(activeSecondSetUnloadTask);
            await ObserveTaskNoThrowAsync(activeSecondSetPickupTask);
            if (activeFirstSetReturnToCenterTask is not null)
            {
                await ObserveTaskNoThrowAsync(activeFirstSetReturnToCenterTask);
            }

            await ObserveCarouselAdvanceTaskNoThrowAsync(activeCarouselAdvanceTask);
            await ObserveTaskNoThrowAsync(activeFeederVibrationTask);
            if (activeFeederLightOnTask is not null)
            {
                await ObserveTaskNoThrowAsync(activeFeederLightOnTask);
            }
            if (_connectionConfigController?.IsVibrationFeederLightEnabled == true)
            {
                _ = await _connectionConfigController.SetProductionLightAsync(enabled: false);
            }
            await ObserveTaskNoThrowAsync(activeFinalTestTask);
            _productionDiagnostics?.Dispose();
            _productionDiagnostics = null;
            StopUphTracking();
            CloseAllActiveSetNozzleVacuumOutputsNoThrow();
            CloseCarouselVacuumSprayOutputsNoThrow();
            // 急停后的 IO 冻结保持到下一次明确启动生产，不能在本轮 finally 收尾时提前解除。
            // 否则尚未退出的取消回调仍可能把低电平有效的真空输出写成相反状态。
            _productionAxisSet = null;
            _productionZPositions = null;
            _productionZDwellTimes = null;
            _secondSetXyPositions = null;
            _binDropPositions = null;
            _lowerCameraPhotoPositions = null;
            _productionAxisMotionSettings = null;
            _testStationSettings = null;
            _sm7110AcceptanceRange = null;
            _productionXyLinearInterpolationEnabled = false;
            // 无论正常停止、异常退出还是中途 return，都要退出运行状态。
            _startSequenceRunning = false;

            // 清除停止请求标记，保证下次启动从干净状态开始。
            _productionStopRequested = false;
            _productionPauseRequested = false;
            _productionResumeSignal = null;

            // 释放取消源，避免持有旧流程资源。
            _productionCancellation?.Dispose();

            // 清空取消源字段，表示当前没有正在运行的生产流程。
            _productionCancellation = null;

            // 先取出完成信号，避免清空字段后无法通知等待方。
            var productionCompletion = _productionCompletion;

            // 清空完成信号字段，表示当前没有可等待的生产流程。
            _productionCompletion = null;

            // 恢复位置1按钮文字。
            MovePresetPosition1Button.Content = "移动";

            // 恢复位置2按钮文字。
            MovePresetPosition2Button.Content = "移动";

            // 重新计算主页所有命令按钮的启用状态。
            UpdateHomeCommandState();

            // 通知所有等待 DeactivateProductionAsync 的调用方：生产流程已经完全收尾。
            productionCompletion?.TrySetResult(true);
        }
    }

    private bool RequestProductionStop()
    {
        if (!_startSequenceRunning || _productionStopRequested)
        {
            return false;
        }

        _productionStopRequested = true;
        _productionPauseRequested = false;
        _productionResumeSignal?.TrySetResult(true);
        _productionResumeSignal = null;
        _productionCancellation?.Cancel();
        SetStartProductionStatus("正在停止循环，请等待当前轴确认停止…", Color.FromRgb(242, 181, 68));
        UpdateHomeCommandState();
        return true;
    }

    private async Task RunProductionVibrationAsync(
        int cycleNumber,
        string reason,
        CancellationToken cancellationToken)
    {
        var connectionController = _connectionConfigController
            ?? throw new InvalidOperationException("振动盘控制组件未连接，无法执行自动震动。");
        SetStartProductionStatus(
            $"第{cycleNumber}轮：{reason}，正在执行“震散 → 向左 → 上下聚拢100 ms”…",
            Color.FromRgb(242, 181, 68));
        var vibrationCompleted = await connectionController.RunProductionScatterThenLeftAsync(
            cancellationToken);
        if (!vibrationCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "“震散 → 向左 → 上下聚拢”未执行，请检查振动盘连接和方向震动参数。");
        }

        SetStartProductionStatus(
            $"第{cycleNumber}轮：{reason}，“震散 → 向左 → 上下聚拢”完成，下一轮重新拍照。",
            Color.FromRgb(73, 209, 125));
    }

    private async Task<VisionRectangleBlobResult> RunProductionUpperCameraInspectionAsync(
        VisualCalibrationPage visualCalibrationController,
        CalibrationCenterPosition actual,
        Task feederVibrationTask,
        Task<bool>? earlyLightOnTask,
        int cycleNumber,
        CancellationToken cancellationToken)
    {
        using var diagnosticStep = _productionDiagnostics?.Begin("upper-camera", $"cycle={cycleNumber}");
        // 最后一批物料取走后，振动已在后续纠偏/上料期间并行执行。
        // 本任务只约束第一套XY继续取料，不约束下方转盘、测试和第二套下料。
        if (!feederVibrationTask.IsCompleted)
        {
            SetStartProductionStatus(
                $"第{cycleNumber}轮：XY已回拍照位，正在等待并行震动结束并停稳；下方流水线继续运行…",
                Color.FromRgb(242, 181, 68));
        }

        await feederVibrationTask;
        await WaitIfProductionPausedAsync(cancellationToken);

        var connectionController = _connectionConfigController
            ?? throw new InvalidOperationException("振动盘控制组件未连接，无法确认拍照前停振。");
        SetStartProductionStatus(
            $"第{cycleNumber}轮：震动序列已结束，正在执行最终停振并等待振动盘停稳；下方流水线继续运行…",
            Color.FromRgb(242, 181, 68));
        if (!await connectionController.EnsureStoppedForProductionPhotoAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("拍照前最终停振失败，已取消本次拍照。");
        }
        await WaitIfProductionPausedAsync(cancellationToken);

        SetStartProductionStatus(
            $"第{cycleNumber}轮：XY已到初始位置({actual.ActualX:0.###}, {actual.ActualY:0.###})，" +
            $"振动盘已停稳，正在运行{ChipInspectionProcedureName} → {ChipInspectionResultModuleName}；" +
            "下方流水线继续运行…",
            Color.FromRgb(242, 181, 68));
        return await RunChipInspectionWithFeederLightAsync(
            visualCalibrationController,
            earlyLightOnTask,
            cancellationToken);
    }

    private async Task<VisionRectangleBlobResult> RunChipInspectionWithFeederLightAsync(
        VisualCalibrationPage visualCalibrationController,
        Task<bool>? earlyLightOnTask,
        CancellationToken cancellationToken)
    {
        var connectionController = _connectionConfigController
            ?? throw new InvalidOperationException("振动盘控制组件未连接，无法控制拍照光源。");

        SetStartProductionStatus(
            $"{ChipInspectionProcedureName}视觉已预热，正在等待提前开启的光源并立即执行流程…",
            Color.FromRgb(242, 181, 68));
        var lightOnTask = earlyLightOnTask ?? connectionController.SetProductionLightAsync(enabled: true);
        var prepareVisionTask =
            BlobInspectionVisionDisplayHost.Visibility == Visibility.Visible &&
            BlobInspectionVisionDisplayHost.HostWindow != IntPtr.Zero
                ? Task.CompletedTask
                : PrepareBlobInspectionVisionDisplayAsync(visualCalibrationController);
        var lightTurnedOn = false;
        var inspectionCompleted = false;
        _productionDiagnostics?.Track("feeder-light-on", lightOnTask);
        _productionDiagnostics?.Track("vision-display-ready", prepareVisionTask);
        try
        {
            await Task.WhenAll(lightOnTask, prepareVisionTask);
            _productionDiagnostics?.Write("upper-camera", "light and display preparation completed");
            lightTurnedOn = await lightOnTask;
            if (!lightTurnedOn)
            {
                throw new InvalidOperationException("振动盘拍照光源打开失败，已取消本次拍照。");
            }

            VisionRectangleBlobResult result;
            using (_productionDiagnostics?.Begin("vision-result", "await RUN_RECTANGLE_BLOB response"))
            {
                result = await visualCalibrationController.RunRectangleBlobInspectionAsync(cancellationToken);
            }
            inspectionCompleted = true;
            return result;
        }
        finally
        {
            // 开灯与视觉显示准备并行；任一步异常时也必须关闭已经打开的光源。
            if (lightTurnedOn || connectionController.IsVibrationFeederLightEnabled)
            {
                var lightTurnedOff = await connectionController.SetProductionLightAsync(enabled: false);
                if (!lightTurnedOff &&
                    inspectionCompleted &&
                    !cancellationToken.IsCancellationRequested)
                {
                    throw new InvalidOperationException("振动盘已拍照，但光源关闭失败，已停止自动生产。");
                }
            }
        }
    }

    private async Task<bool> OpenProductionLightAfterVibrationAsync(
        Task feederVibrationTask,
        CancellationToken cancellationToken)
    {
        await feederVibrationTask;
        await WaitIfProductionPausedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var connectionController = _connectionConfigController
            ?? throw new InvalidOperationException("振动盘控制组件未连接，无法提前打开拍照光源。");
        return await connectionController.SetProductionLightAsync(enabled: true);
    }

    private bool RequestProductionPause()
    {
        if (!_startSequenceRunning ||
            _productionStopRequested ||
            _productionPauseRequested)
        {
            return false;
        }

        _productionPauseRequested = true;
        _productionResumeSignal = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        SetStartProductionStatus(
            "暂停请求已接收：当前动作完成并到达安全节点后暂停；再次点击可继续。",
            Color.FromRgb(242, 181, 68));
        UpdateHomeCommandState();
        return true;
    }

    private bool ResumeProduction()
    {
        if (!_startSequenceRunning ||
            _productionStopRequested ||
            !_productionPauseRequested)
        {
            return false;
        }

        _productionPauseRequested = false;
        var resumeSignal = _productionResumeSignal;
        _productionResumeSignal = null;
        SetStartProductionStatus(
            "正在从暂停位置继续运行…",
            Color.FromRgb(73, 209, 125));
        UpdateHomeCommandState();
        resumeSignal?.TrySetResult(true);
        return true;
    }

    private async Task WaitIfProductionPausedAsync(CancellationToken cancellationToken)
    {
        if (!_productionPauseRequested)
        {
            return;
        }

        var resumeSignal = _productionResumeSignal;
        if (resumeSignal is null)
        {
            return;
        }

        SetStartProductionStatus(
            "生产流程已在安全节点暂停；点击“继续运行”将从下一步接着执行。",
            Color.FromRgb(242, 181, 68));
        StopUphTracking();
        await resumeSignal.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        StartUphTracking();
    }

    private async Task<CalibrationCenterPosition> MoveToStartupPosition2SafelyAsync(
        MotionControlPage motionController,
        int xHardwareAxisNo,
        int yHardwareAxisNo,
        double targetX,
        double targetY,
        double xVelocity,
        double yVelocity,
        CancellationToken cancellationToken)
    {
        var current = motionController.CaptureCalibrationFeedback(
            xHardwareAxisNo,
            yHardwareAxisNo,
            FirstSetRAxisNos);

        SetStartProductionStatus(
            $"启动安全定位：Y轴先移动到位置2 Y={targetY:0.###}，X轴保持不动…",
            Color.FromRgb(242, 181, 68));
        SetFirstSetPositionStatus(
            $"正在先走Y到位置2：当前X={current.ActualX:0.###}，目标Y={targetY:0.###} pulse。",
            false);
        var yTimeoutMilliseconds = CalculateStartMoveTimeout(
            current.ActualX,
            current.ActualY,
            current.ActualX,
            targetY,
            yVelocity);
        await motionController.MoveAxesAbsoluteAsync(
            new Dictionary<int, double> { [yHardwareAxisNo] = targetY },
            cancellationToken,
            minimumTimeoutMilliseconds: yTimeoutMilliseconds,
            allowedMovingAxisNos: FirstSetRAxisNos,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: yVelocity);

        var afterY = motionController.CaptureCalibrationFeedback(
            xHardwareAxisNo,
            yHardwareAxisNo,
            FirstSetRAxisNos);
        var yError = Math.Abs(afterY.ActualY - targetY);
        if (yError > HomePageCompletionTolerance)
        {
            throw new InvalidOperationException(
                $"启动安全定位Y轴误差为 {yError:0.###} 脉冲，超过允许值 ±{HomePageCompletionTolerance:0.###}，X轴未启动。");
        }

        SetStartProductionStatus(
            $"启动安全定位：Y轴已到位置2，X轴开始移动到 X={targetX:0.###}…",
            Color.FromRgb(242, 181, 68));
        SetFirstSetPositionStatus(
            $"Y已到位({afterY.ActualY:0.###})，正在走X到位置2：目标X={targetX:0.###} pulse。",
            false);
        var xTimeoutMilliseconds = CalculateStartMoveTimeout(
            afterY.ActualX,
            afterY.ActualY,
            targetX,
            afterY.ActualY,
            xVelocity);
        await motionController.MoveAxesAbsoluteAsync(
            new Dictionary<int, double> { [xHardwareAxisNo] = targetX },
            cancellationToken,
            minimumTimeoutMilliseconds: xTimeoutMilliseconds,
            allowedMovingAxisNos: FirstSetRAxisNos,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: xVelocity);

        var actual = motionController.CaptureCalibrationFeedback(
            xHardwareAxisNo,
            yHardwareAxisNo,
            FirstSetRAxisNos);
        var xError = Math.Abs(actual.ActualX - targetX);
        yError = Math.Abs(actual.ActualY - targetY);
        if (xError > HomePageCompletionTolerance || yError > HomePageCompletionTolerance)
        {
            throw new InvalidOperationException(
                $"启动安全位置2到位误差超限：X误差={xError:0.###}，Y误差={yError:0.###} 脉冲，生产流程未启动。");
        }

        SetFirstSetPositionStatus(
            $"启动安全位置2已到位（先Y后X）：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse。",
            true);
        return actual;
    }

    private async Task<CalibrationCenterPosition> PrepareProductionStartupAxesAsync(
        MotionControlPage motionController,
        int xHardwareAxisNo,
        int yHardwareAxisNo,
        double targetX,
        double targetY,
        double xVelocity,
        double yVelocity,
        CancellationToken cancellationToken)
    {
        using var startupCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 先启动XY安全定位；其首次等待前已经下发Y轴命令。随后R1/R2回原入口
        // 显式放行这两根XY轴，因此两组独立机构可以同时运行。
        var safePositionTask = MoveToStartupPosition2SafelyAsync(
            motionController,
            xHardwareAxisNo,
            yHardwareAxisNo,
            targetX,
            targetY,
            xVelocity,
            yVelocity,
            startupCancellation.Token);
        var rHomeTask = motionController.HomeAxesAsync(
            FirstSetRAxisNos,
            StartupNozzleRHomeMode,
            StartupNozzleRHomeOffset,
            startupCancellation.Token,
            lowVelocityOverride: StartupNozzleRHomeVelocity,
            highVelocityOverride: StartupNozzleRHomeVelocity,
            allowedMovingAxisNos: [xHardwareAxisNo, yHardwareAxisNo]);

        SetStartProductionStatus(
            $"启动准备 3/3：R1/R2正在回原，XY同时按Y后X移动到位置2({targetX:0.###}, {targetY:0.###})…",
            Color.FromRgb(242, 181, 68));

        try
        {
            var firstCompletedTask = await Task.WhenAny(safePositionTask, rHomeTask);
            if (firstCompletedTask.IsFaulted || firstCompletedTask.IsCanceled)
            {
                startupCancellation.Cancel();
            }

            await Task.WhenAll(safePositionTask, rHomeTask);
            return await safePositionTask;
        }
        catch
        {
            // 任一并行分支失败时立即取消另一分支，并等待其完成安全停止后再向上抛错。
            startupCancellation.Cancel();
            await ObserveTaskNoThrowAsync(safePositionTask);
            await ObserveTaskNoThrowAsync(rHomeTask);
            throw;
        }
    }

    private Task<CalibrationCenterPosition> StartFirstSetReturnToCenterAsync(
        MotionControlPage motionController,
        (double X, double Y) center,
        double xVelocity,
        double yVelocity,
        CancellationToken cancellationToken)
    {
        var current = motionController.CaptureCalibrationFeedback(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            FirstSetProductionPeerAxisNos);
        var timeoutMilliseconds = CalculateStartMoveTimeout(
            current.ActualX,
            current.ActualY,
            center.X,
            center.Y,
            Math.Min(xVelocity, yVelocity));

        SetFirstSetPositionStatus(
            $"放料后XY正在回中心：X={center.X:0.###}，Y={center.Y:0.###} pulse。",
            true);
        return motionController.MoveCalibrationAxesToAsync(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            center.X,
            center.Y,
            xVelocity,
            positionTolerance: HomePageCompletionTolerance,
            moveTimeoutMilliseconds: timeoutMilliseconds,
            cancellationToken: cancellationToken,
            allowedMovingAxisNos: FirstSetProductionPeerAxisNos,
            yVelocityOverride: yVelocity,
            linearInterpolationCoordinateSystemNo: GetFirstSetXyInterpolationCoordinateSystemNo());
    }

    private bool IsXyLinearInterpolationEnabledForCurrentOperation()
    {
        return _startSequenceRunning
            ? _productionXyLinearInterpolationEnabled
            : XyLinearInterpolationCheckBox?.IsChecked == true;
    }

    private int? GetFirstSetXyInterpolationCoordinateSystemNo()
    {
        return IsXyLinearInterpolationEnabledForCurrentOperation()
            ? FirstSetXyInterpolationCoordinateSystemNo
            : null;
    }

    private int? GetSecondSetXyInterpolationCoordinateSystemNo()
    {
        return IsXyLinearInterpolationEnabledForCurrentOperation()
            ? SecondSetXyInterpolationCoordinateSystemNo
            : null;
    }

    private static async Task ObserveTaskNoThrowAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private static async Task ObserveCarouselAdvanceTaskNoThrowAsync(
        Task<CarouselAdvanceResult>? carouselAdvanceTask)
    {
        if (carouselAdvanceTask is null)
        {
            return;
        }

        try
        {
            var result = await carouselAdvanceTask;
            await ObserveTaskNoThrowAsync(result.FinalTestTask);
            await ObserveTaskNoThrowAsync(result.SecondSetUnloadTask);
            await ObserveTaskNoThrowAsync(result.SecondSetPickupTask);
        }
        catch
        {
        }
    }

    private async Task<CarouselAdvanceResult> StartCarouselAfterSafetyBarrierAsync(
        Task<int> requiredFinalTestTask,
        Task requiredSecondSetPickupTask,
        Task requiredPreviousSecondSetUnloadTask,
        Task xyMoveAwayDelayTask,
        CarouselStationState[] carouselStations,
        double axis0PulseDistance,
        IReadOnlyCollection<int>? allowedMovingAxisNos,
        CancellationToken cancellationToken)
    {
        using var diagnosticStep = _productionDiagnostics?.Begin("carousel", "safety barrier and two turns");
        if (!requiredFinalTestTask.IsCompleted || !requiredSecondSetPickupTask.IsCompleted)
        {
            SetStartProductionStatus(
                "XY正在离开放料点准备下一轮；已启用测试站将随DD节拍执行，" +
                "下一次DD同时等待测试轴和第二套取料安全条件。",
                Color.FromRgb(242, 181, 68));
        }

        // DD必须等待已启用测试站回到等待位、第二套完成取料，并确认XY已离开放料点50 ms。
        // 第二套后续移动到两个收料位置并放料，不再阻塞DD。
        await Task.WhenAll(
            requiredFinalTestTask,
            requiredSecondSetPickupTask,
            xyMoveAwayDelayTask);
        await WaitIfProductionPausedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var carouselAdvanceResult = await AdvanceCarouselExactlyTwoStationsAsync(
            carouselStations,
            axis0PulseDistance,
            allowedMovingAxisNos,
            cancellationToken);

        // 13/14工位状态已更新。把上一批BIN放料及回程的等待交给第二套，
        // 本任务在DD停稳后即可返回，允许第一套下压放料。
        // 第二套下一批仍须串行等待自己的轴释放；其取料完成信号约束下一次DD。
        if (!requiredPreviousSecondSetUnloadTask.IsCompleted)
        {
            SetFirstSetPositionStatus(
                "DD已停稳，第一套可放料；第二套独立等待上一批BIN放料及回程完成后取料。",
                true);
        }

        var secondSetUnloadTask = StartSecondSetUnloadIfReadyAsync(
            requiredPreviousSecondSetUnloadTask,
            carouselStations,
            cancellationToken,
            out var secondSetPickupTask);

        return carouselAdvanceResult with
        {
            SecondSetUnloadTask = secondSetUnloadTask,
            SecondSetPickupTask = secondSetPickupTask
        };
    }

    private async Task DrainCarouselAfterFeederEmptyAsync(
        CarouselStationState[] carouselStations,
        double axis0PulseDistance,
        Task<int> requiredFinalTestTask,
        Task requiredSecondSetPickupTask,
        Task requiredPreviousSecondSetUnloadTask,
        IReadOnlyCollection<int>? allowedMovingAxisNos,
        CancellationToken cancellationToken)
    {
        var finalTestTask = requiredFinalTestTask;
        var secondSetPickupTask = requiredSecondSetPickupTask;
        var secondSetUnloadTask = requiredPreviousSecondSetUnloadTask;
        var drainStep = 0;
        try
        {
            while (true)
            {
                // 工位13/14的产品只有在第二套完成吸取后才会从缓存移除；
                // 最后一轮测试也必须结束，才能准确判断是否已经完全排空。
                await Task.WhenAll(finalTestTask, secondSetPickupTask);
                await WaitIfProductionPausedAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                var remainingProductCount = CountOccupiedCarouselStations(carouselStations);
                if (remainingProductCount == 0)
                {
                    // pickup完成只代表产品已离开转盘；还要等第二套把手中产品放入BIN。
                    await secondSetUnloadTask;
                    UpdateCarouselStationDisplay(carouselStations);
                    return;
                }

                drainStep++;
                SetStartProductionStatus(
                    $"缺料排空第{drainStep}步：转盘仍有 {remainingProductCount} 件在制品，" +
                    "继续DD转动、测试及13/14工位下料…",
                    Color.FromRgb(242, 181, 68));

                var carouselAdvanceResult = await StartCarouselAfterSafetyBarrierAsync(
                    finalTestTask,
                    secondSetPickupTask,
                    secondSetUnloadTask,
                    Task.CompletedTask,
                    carouselStations,
                    axis0PulseDistance,
                    allowedMovingAxisNos,
                    cancellationToken);
                finalTestTask = carouselAdvanceResult.FinalTestTask;
                secondSetUnloadTask = carouselAdvanceResult.SecondSetUnloadTask;
                secondSetPickupTask = carouselAdvanceResult.SecondSetPickupTask;
            }
        }
        finally
        {
            // 异常或人工停止时也要观察所有并行任务，避免遗留未观察异常。
            await ObserveTaskNoThrowAsync(finalTestTask);
            await ObserveTaskNoThrowAsync(secondSetPickupTask);
            await ObserveTaskNoThrowAsync(secondSetUnloadTask);
        }
    }

    private Task StartSecondSetUnloadIfReadyAsync(
        Task previousUnloadTask,
        CarouselStationState[] carouselStations,
        CancellationToken cancellationToken,
        out Task pickupCompletedTask)
    {
        var sequence = ProductionUnloadSequence.StartAfter(
            previousUnloadTask,
            async pickupCompletion =>
            {
                await WaitIfProductionPausedAsync(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();

                // 下一次DD仍被本批pickup信号拦住，13/14工位不会在等待期间推进。
                // 任一工位有料即启动对应吸嘴，保留奇数批次收尾行为。
                if (carouselStations.Length <= SecondSetNozzle1UnloadStation ||
                    (!carouselStations[SecondSetNozzle2UnloadStation].Occupied &&
                     !carouselStations[SecondSetNozzle1UnloadStation].Occupied))
                {
                    return;
                }

                if (_productionAxisSet != VisionCalibrationAxisSet.First)
                {
                    throw new InvalidOperationException("第二套XY正在被主页上料流程占用，不能同时执行13/14工位收料。");
                }

                SetFirstSetPositionStatus(
                    "DD已停稳，第二套轴已释放，立即取13/14工位产品；第一套可独立上料。",
                    true);
                await RunSecondSetUnloadAsync(
                    carouselStations,
                    pickupCompletion,
                    cancellationToken);
            },
            cancellationToken);
        pickupCompletedTask = sequence.PickupTask;
        _productionDiagnostics?.Track("second-set-unload", sequence.UnloadTask);
        _productionDiagnostics?.Track("second-set-pickup", pickupCompletedTask);
        return sequence.UnloadTask;
    }

    private async Task RunSecondSetUnloadAsync(
        CarouselStationState[] carouselStations,
        TaskCompletionSource<bool> pickupCompletion,
        CancellationToken cancellationToken)
    {
        using var diagnosticStep = _productionDiagnostics?.Begin("second-set", "pickup and BIN placement");
        try
        {
            var pickWithNozzle2 = carouselStations[SecondSetNozzle2UnloadStation].Occupied;
            var pickWithNozzle1 = carouselStations[SecondSetNozzle1UnloadStation].Occupied;
            var zPositions = GetProductionZPositions();
            var nozzle1ZPositions = zPositions.Resolve(VisionCalibrationAxisSet.Second, 1);
            var nozzle2ZPositions = zPositions.Resolve(VisionCalibrationAxisSet.Second, 2);
            var xyPositions = GetSecondSetXyPositions();
            var binDropPositions = GetBinDropPositions();
            var sm7110Enabled = IsSM7110TestEnabled();
            var nozzle2Bin = pickWithNozzle2
                ? carouselStations[SecondSetNozzle2UnloadStation].GetUnloadDestination(sm7110Enabled)
                : null;
            var nozzle1Bin = pickWithNozzle1
                ? carouselStations[SecondSetNozzle1UnloadStation].GetUnloadDestination(sm7110Enabled)
                : null;
            var nozzle2BinCenter = pickWithNozzle2
                ? binDropPositions.Resolve(nozzle2Bin, SecondSetNozzle2UnloadStation)
                : default;
            var nozzle1BinCenter = pickWithNozzle1
                ? binDropPositions.Resolve(nozzle1Bin, SecondSetNozzle1UnloadStation)
                : default;

            if (pickWithNozzle2)
            {
                await MoveSecondSetUnloadAxesToAsync(
                    "吸嘴2取13工位",
                    xyPositions.Position1X,
                    xyPositions.Position1Y,
                    cancellationToken);
                await WaitIfProductionPausedAsync(cancellationToken);
            }

            var nozzle2OverlapTasks = SecondSetNozzle2PickupOverlapTasks.Completed;
            try
            {
                if (pickWithNozzle2)
                {
                    if (pickWithNozzle1)
                    {
                        nozzle2OverlapTasks = await PickSecondSetNozzle2UntilXyReleaseAsync(
                            nozzle2ZPositions.Pickup,
                            nozzle2ZPositions.Safe,
                            cancellationToken);
                    }
                    else
                    {
                        await PickSecondSetNozzleFromStationAsync(
                            2,
                            SecondSetNozzle2UnloadStation,
                            nozzle2ZPositions.Pickup,
                            nozzle2ZPositions.Safe,
                            cancellationToken);
                    }
                    await WaitIfProductionPausedAsync(cancellationToken);
                }

                if (pickWithNozzle1)
                {
                    await MoveSecondSetUnloadAxesToAsync(
                        "吸嘴1取14工位",
                        xyPositions.Position2X,
                        xyPositions.Position2Y,
                        cancellationToken,
                        pickWithNozzle2 ? SecondSetPickupOverlapPeerAxisNos : null);
                    await WaitIfProductionPausedAsync(cancellationToken);

                    // XY到达14工位后，先确认Z1预下降已完成，再走完剩余行程取料。
                    await nozzle2OverlapTasks.Nozzle1PreDropTask;
                    await PickSecondSetNozzleFromStationAsync(
                        1,
                        SecondSetNozzle1UnloadStation,
                        nozzle1ZPositions.Pickup,
                        nozzle1ZPositions.Safe,
                        cancellationToken,
                        pickWithNozzle2 ? SecondSetPickupOverlapPeerAxisNos : null);
                    await WaitIfProductionPausedAsync(cancellationToken);
                }

                // DD取料安全门：两根Z轴都完成回安全位后，才允许释放工位并进入BIN放料。
                await Task.WhenAll(
                    nozzle2OverlapTasks.Nozzle2SafeTask,
                    nozzle2OverlapTasks.Nozzle1PreDropTask);
            }
            catch
            {
                await ObserveTaskNoThrowAsync(nozzle2OverlapTasks.Nozzle2SafeTask);
                await ObserveTaskNoThrowAsync(nozzle2OverlapTasks.Nozzle1PreDropTask);
                throw;
            }

            EnsureSecondSetNozzlesHolding(pickWithNozzle1, pickWithNozzle2);

            // 待收料产品已经离开对应工位，此刻即可释放DD安全门；后续BIN放料独立执行。
            if (pickWithNozzle2)
            {
                carouselStations[SecondSetNozzle2UnloadStation] = CarouselStationState.Empty();
            }

            if (pickWithNozzle1)
            {
                carouselStations[SecondSetNozzle1UnloadStation] = CarouselStationState.Empty();
            }

            UpdateCarouselStationDisplay(carouselStations);
            pickupCompletion.TrySetResult(true);

            if (pickWithNozzle1)
            {
                await MoveSecondSetUnloadAxesToAsync(
                    $"吸嘴1放料到{nozzle1Bin}",
                    nozzle1BinCenter.X,
                    nozzle1BinCenter.Y + SecondSetNozzleBinYOffset,
                    cancellationToken);
                await WaitIfProductionPausedAsync(cancellationToken);
                await PlaceWithNozzleAsync(
                    VisionCalibrationAxisSet.Second,
                    1,
                    nozzle1ZPositions.Drop,
                    nozzle1ZPositions.Safe,
                    cancellationToken);
                RecordCompletedUphUnit();
                await WaitIfProductionPausedAsync(cancellationToken);
            }

            if (pickWithNozzle2)
            {
                await MoveSecondSetUnloadAxesToAsync(
                    $"吸嘴2放料到{nozzle2Bin}",
                    nozzle2BinCenter.X,
                    nozzle2BinCenter.Y - SecondSetNozzleBinYOffset,
                    cancellationToken);
                await WaitIfProductionPausedAsync(cancellationToken);
                await PlaceWithNozzleAsync(
                    VisionCalibrationAxisSet.Second,
                    2,
                    nozzle2ZPositions.Drop,
                    nozzle2ZPositions.Safe,
                    cancellationToken);
                RecordCompletedUphUnit();
                await WaitIfProductionPausedAsync(cancellationToken);
            }

            CloseAllNozzleVacuumOutputs(VisionCalibrationAxisSet.Second);

            // 本批BIN放料全部完成后，立即回到下一轮首先使用的取料位等待，
            // 避免第二套XY停留在最后一个BIN位置，下一轮才开始长距离回程。
            await MoveSecondSetUnloadAxesToAsync(
                "BIN放料完成后回13工位取料位等待",
                xyPositions.Position1X,
                xyPositions.Position1Y,
                cancellationToken);
            await WaitIfProductionPausedAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            pickupCompletion.TrySetCanceled(cancellationToken);
            CloseNozzleVacuumOutputsNoThrow(VisionCalibrationAxisSet.Second);
            throw;
        }
        catch (Exception exception)
        {
            pickupCompletion.TrySetException(exception);
            CloseNozzleVacuumOutputsNoThrow(VisionCalibrationAxisSet.Second);
            throw;
        }
    }

    private void EnsureSecondSetNozzlesHolding(bool nozzle1Required, bool nozzle2Required)
    {
        var secondSetIndex = (int)VisionCalibrationAxisSet.Second;
        if ((nozzle1Required && !_nozzleVacuumEnabledBySet[secondSetIndex, 1]) ||
            (nozzle2Required && !_nozzleVacuumEnabledBySet[secondSetIndex, 2]))
        {
            throw new InvalidOperationException(
                "第二套所需吸嘴尚未全部完成吸料，禁止进入放料流程。");
        }
    }

    private async Task MoveSecondSetUnloadAxesToAsync(
        string actionName,
        double targetX,
        double targetY,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        using var diagnosticStep = _productionDiagnostics?.Begin("second-set-XY", $"{actionName} target=({targetX},{targetY})");
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var xVelocity = GetProductionAxisMotionSettings(
            VisionCalibrationService.SecondSetXHardwareAxisNo).RunVelocity;
        var yVelocity = GetProductionAxisMotionSettings(
            VisionCalibrationService.SecondSetYHardwareAxisNo).RunVelocity;

        var current = motionController.CaptureCalibrationFeedback(
            VisionCalibrationService.SecondSetXHardwareAxisNo,
            VisionCalibrationService.SecondSetYHardwareAxisNo,
            allowedMovingAxisNos ?? SecondSetProductionPeerAxisNos);
        var timeoutMilliseconds = CalculateStartMoveTimeout(
            current.ActualX,
            current.ActualY,
            targetX,
            targetY,
            Math.Min(xVelocity, yVelocity));
        try
        {
            await motionController.MoveCalibrationAxesToAsync(
                VisionCalibrationService.SecondSetXHardwareAxisNo,
                VisionCalibrationService.SecondSetYHardwareAxisNo,
                targetX,
                targetY,
                xVelocity,
                positionTolerance: HomePageCompletionTolerance,
                moveTimeoutMilliseconds: timeoutMilliseconds,
                cancellationToken: cancellationToken,
                allowedMovingAxisNos: allowedMovingAxisNos ?? SecondSetProductionPeerAxisNos,
                yVelocityOverride: yVelocity,
                linearInterpolationCoordinateSystemNo: GetSecondSetXyInterpolationCoordinateSystemNo());
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"第二套XY执行“{actionName}”失败，目标({targetX:0.###}, {targetY:0.###})：{exception.Message}",
                exception);
        }
    }

    private async Task<CarouselAdvanceResult> AdvanceCarouselExactlyTwoStationsAsync(
        CarouselStationState[] carouselStations,
        double axis0PulseDistance,
        IReadOnlyCollection<int>? allowedMovingAxisNos,
        CancellationToken cancellationToken)
    {
        if (carouselStations.Length <= CarouselStationCount)
        {
            throw new ArgumentException("转盘工位缓存长度无效。", nameof(carouselStations));
        }

        Task<int> finalTestTask = Task.FromResult(0);
        var enabledTestStations = GetEnabledTestStationAxisByStation();
        const int maximumTurnsBeforeReload = 2;
        for (var turn = 1; turn <= maximumTurnsBeforeReload; turn++)
        {
            await WaitIfProductionPausedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var loadedTestStationCount = CountLoadedTestStations(carouselStations);
            SetStartProductionStatus(
                loadedTestStationCount > 0
                    ? $"DD马达正在第 {turn}/{maximumTurnsBeforeReload} 次转动 {axis0PulseDistance:0.###} pulse，测试站已有料，转后执行测试…"
                    : $"DD马达正在第 {turn}/{maximumTurnsBeforeReload} 次转动 {axis0PulseDistance:0.###} pulse，有料工位未进测试站则继续补转…",
                Color.FromRgb(242, 181, 68));
            await MoveAxis0RelativeCoreAsync(
                axis0PulseDistance,
                cancellationToken,
                allowedMovingAxisNos);

            SetStartProductionStatus(
                $"DD马达第 {turn}/{maximumTurnsBeforeReload} 次转动完成，正在短时开启工位上方吸和下方喷…",
                Color.FromRgb(242, 181, 68));
            await PulseCarouselVacuumAndSprayAsync(cancellationToken);

            AdvanceCarouselOccupancy(carouselStations);
            UpdateCarouselStationDisplay(carouselStations);
            SetStartProductionStatus(
                $"DD马达第 {turn}/{maximumTurnsBeforeReload} 次转动完成，正在检查" +
                $" {string.Join("/", enabledTestStations.Keys)} 号测试工位…",
                Color.FromRgb(242, 181, 68));
            if (turn < maximumTurnsBeforeReload)
            {
                _ = await RunOccupiedTestStationsAsync(
                    carouselStations,
                    cancellationToken);
            }
            else
            {
                finalTestTask = RunOccupiedTestStationsAsync(
                    carouselStations,
                    cancellationToken);
            }
            await WaitIfProductionPausedAsync(cancellationToken);
        }

        SetStartProductionStatus(
            $"DD已固定转动 {maximumTurnsBeforeReload} 次并停稳；最后一轮测试并行执行，XY可直接上下料。",
            Color.FromRgb(73, 209, 125));
        return new CarouselAdvanceResult(
            maximumTurnsBeforeReload,
            finalTestTask,
            Task.CompletedTask,
            Task.CompletedTask);
    }

    private async Task PulseCarouselVacuumAndSprayAsync(CancellationToken cancellationToken)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");

        try
        {
            SetCarouselVacuumSprayOutput(
                motionController,
                CarouselUpperVacuumOutputBit,
                enabled: false,
                "开启工位上方吸 Y24=0");
            SetCarouselVacuumSprayOutput(
                motionController,
                CarouselLowerSprayOutputBit,
                enabled: false,
                "开启工位下方喷 Y25=0");
            await Task.Delay(CarouselVacuumSprayPulseMilliseconds, cancellationToken);
        }
        finally
        {
            Exception? closeFailure = null;
            try
            {
                SetCarouselVacuumSprayOutput(
                    motionController,
                    CarouselUpperVacuumOutputBit,
                    enabled: true,
                    "关闭工位上方吸 Y24=1");
            }
            catch (Exception exception)
            {
                closeFailure = exception;
            }

            try
            {
                SetCarouselVacuumSprayOutput(
                    motionController,
                    CarouselLowerSprayOutputBit,
                    enabled: true,
                    "关闭工位下方喷 Y25=1");
            }
            catch (Exception exception)
            {
                closeFailure ??= exception;
            }

            if (closeFailure is not null)
            {
                throw new InvalidOperationException(
                    "DD工位脉冲结束后，Y24/Y25未能全部关闭。",
                    closeFailure);
            }
        }
    }

    private static void SetCarouselVacuumSprayOutput(
        MotionControlPage motionController,
        int bitNo,
        bool enabled,
        string actionName)
    {
        if (!motionController.SetDigitalOutputHardwareBit(bitNo, enabled))
        {
            throw new InvalidOperationException($"DD工位IO操作失败：{actionName}。");
        }
    }

    private int CountLoadedTestStations(IReadOnlyList<CarouselStationState> carouselStations)
    {
        return GetEnabledTestStationAxisByStation().Keys.Count(station =>
            station < carouselStations.Count && carouselStations[station].Occupied);
    }

    private IReadOnlyDictionary<int, int> GetEnabledTestStationAxisByStation()
    {
        var settings = _testStationSettings ?? ReadTestStationSettings();
        return TestStationAxisByStation
            .Where(pair =>
            {
                var stationSettings = settings.GetValueOrDefault(pair.Key);
                return stationSettings?.Enabled == true &&
                       stationSettings.Instrument is not null and not TestStationInstrument.None;
            })
            .ToDictionary(pair => pair.Key, pair => pair.Value);
    }

    private async Task<int> RunOccupiedTestStationsAsync(
        CarouselStationState[] carouselStations,
        CancellationToken cancellationToken)
    {
        using var diagnosticStep = _productionDiagnostics?.Begin("test-stations", "press, measure, return");
        await WaitIfProductionPausedAsync(cancellationToken);
        var enabledTestStations = GetEnabledTestStationAxisByStation();
        var activeStationParameters = enabledTestStations
            .Where(pair => carouselStations[pair.Key].Occupied)
            .ToDictionary(
                pair => pair.Value,
                pair => GetTestStationSettings(pair.Key));
        var axisTargets = activeStationParameters.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.PressPosition);
        if (axisTargets.Count == 0)
        {
            SetStartProductionStatus(
                "已启用测试站当前无料，跳过本次下压。",
                Color.FromRgb(159, 177, 191));
            return 0;
        }

        var activeStationNumbers = enabledTestStations
            .Where(pair => carouselStations[pair.Key].Occupied)
            .Select(pair => pair.Key)
            .ToArray();
        var stations = enabledTestStations
            .Where(pair => carouselStations[pair.Key].Occupied)
            .Select(pair => $"{pair.Key}号→轴{pair.Value}")
            .ToArray();

        foreach (var stationNumber in activeStationNumbers)
        {
            SetTestStationRuntimeDisplay(
                stationNumber,
                "正在下压",
                "下压",
                "等待下压到位…",
                Color.FromRgb(242, 181, 68));
        }

        UpdateCarouselStationDisplay(carouselStations, axisTargets.Keys, CarouselStatusPressing);
        SetStartProductionStatus(
            $"{string.Join("，", stations)} 已进测试站，不等待XY回中心，立即下压测试…",
            Color.FromRgb(242, 181, 68));
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        var pressVelocities = GetProductionAxisVelocities(axisTargets.Keys);
        await motionController.MoveAxesAbsoluteAsync(
            axisTargets,
            cancellationToken,
            TestStationMoveTimeoutMilliseconds,
            ProductionHandlingAxisNos,
            HomePageCompletionTolerance,
            velocityOverrides: pressVelocities);
        UpdateCarouselStationDisplay(carouselStations, axisTargets.Keys, CarouselStatusDwelling);
        SetStartProductionStatus(
            $"{string.Join("，", stations)} 下压到位，等待接触稳定后触发已分配仪表…",
            Color.FromRgb(242, 181, 68));
        foreach (var stationNumber in activeStationNumbers)
        {
            SetTestStationRuntimeDisplay(
                stationNumber,
                "下压到位",
                "准备测试",
                $"保持下压，接触稳定等待 {_testStationDwellMilliseconds} ms…",
                Color.FromRgb(98, 181, 255));
        }
        await Task.Delay(_testStationDwellMilliseconds, cancellationToken);

        var measurementTasks = enabledTestStations.Keys
            .Where(station => carouselStations[station].Occupied)
            .ToDictionary(
                station => station,
                station => MeasureTestStationAsync(
                    station,
                    carouselStations[station],
                    cancellationToken));
        var measurementResults = new Dictionary<int, TestStationMeasurementResult>();
        Exception? measurementFailure = null;
        try
        {
            await Task.WhenAll(measurementTasks.Values);
            foreach (var measurement in measurementTasks)
            {
                measurementResults[measurement.Key] = measurement.Value.Result;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 仪表异常时仍先让测试轴安全回到等待位，再把错误交给主生产流程停机显示。
            measurementFailure = exception;
        }

        if (measurementTasks.Values.Any(task => task.Exception is { } exception && SM7110TimedTest.HasStopFailure(exception)))
        {
            const string message = "SM7110停止输出/放电失败：测试轴保持下压，禁止上抬和DD流转，请检查仪表。";
            SetStartProductionStatus(message, Color.FromRgb(242, 122, 128));
            throw new InvalidOperationException(message, measurementFailure);
        }

        foreach (var stationNumber in activeStationNumbers)
        {
            var controls = GetTestStationConfigurationControls(stationNumber);
            if (measurementFailure is not null)
            {
                SetTestStationRuntimeDisplay(
                    stationNumber,
                    "测试异常，安全上抬",
                    "上抬",
                    controls.ResultText.Text,
                    Color.FromRgb(242, 122, 128));
            }
            else
            {
                var measurement = measurementResults[stationNumber];
                SetTestStationRuntimeDisplay(
                    stationNumber,
                    "已收到结果，正在上抬",
                    "上抬",
                    measurement.DisplayText,
                    measurement.Passed && !measurement.IsNg
                        ? Color.FromRgb(73, 209, 125)
                        : Color.FromRgb(242, 122, 128));
            }
        }

        var returningAxes = axisTargets.Keys.ToArray();
        var returnTargets = activeStationParameters.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.WaitPosition);
        UpdateCarouselStationDisplay(
            carouselStations,
            returningAxes,
            CarouselStatusReturning);
        SetStartProductionStatus(
            $"轴{string.Join("/", returningAxes)}停留完成，正在返回各自等待位，DD等待全部返回…",
            Color.FromRgb(242, 181, 68));
        await motionController.MoveAxesAbsoluteAsync(
            returnTargets,
            cancellationToken,
            TestStationMoveTimeoutMilliseconds,
            ProductionHandlingAxisNos,
            HomePageCompletionTolerance,
            velocityOverrides: pressVelocities);

        if (measurementFailure is not null)
        {
            SetStartProductionStatus(
                $"{string.Join("，", stations)} 仪表测试异常，测试轴已安全上抬；流程停止，禁止DD继续。",
                Color.FromRgb(242, 122, 128));
            foreach (var stationNumber in activeStationNumbers)
            {
                var controls = GetTestStationConfigurationControls(stationNumber);
                SetTestStationRuntimeDisplay(
                    stationNumber,
                    "仪表异常，流程停止",
                    "异常",
                    controls.ResultText.Text,
                    Color.FromRgb(242, 122, 128));
            }
            throw measurementFailure;
        }

        foreach (var measurementResult in measurementResults)
        {
            carouselStations[measurementResult.Key].SetMeasurement(measurementResult.Value);
            SetTestStationRuntimeDisplay(
                measurementResult.Key,
                $"测试完成 · {measurementResult.Value.StatusDescription}",
                measurementResult.Value.ResultLabel,
                measurementResult.Value.DisplayText,
                measurementResult.Value.Passed && !measurementResult.Value.IsNg
                    ? Color.FromRgb(73, 209, 125)
                    : Color.FromRgb(242, 122, 128));
        }

        UpdateCarouselStationDisplay(carouselStations);

        SetStartProductionStatus(
            $"{string.Join("，", stations)} 已收到有效返回并上抬到等待位，DD可继续下一步。",
            Color.FromRgb(73, 209, 125));
        await WaitIfProductionPausedAsync(cancellationToken);
        return axisTargets.Count;
    }

    private async Task<TestStationMeasurementResult> MeasureTestStationAsync(
        int stationNumber,
        CarouselStationState stationState,
        CancellationToken cancellationToken)
    {
        var settings = GetTestStationSettings(stationNumber);
        var connectionController = _connectionConfigController
            ?? throw new InvalidOperationException("主页尚未连接仪表控制组件。");
        try
        {
            var instrumentName = FormatTestStationInstrument(
                settings.Instrument ?? TestStationInstrument.None);
            SetTestStationRuntimeDisplay(
                stationNumber,
                $"等待 {instrumentName} 返回",
                "测试中",
                "等待仪表返回…",
                Color.FromRgb(98, 181, 255));
            var attempts = 0;
            var finalResult = await TestMeasurementRetry.ExecuteAsync(
                MeasureOnceAsync,
                result => result.Passed,
                _testRetryCount,
                async (retryNumber, token) =>
                {
                    var motionController = _motionController
                        ?? throw new InvalidOperationException("主页尚未连接运动控制组件，不能执行机械复测。");
                    var axisNo = TestStationAxisByStation[stationNumber];
                    var velocities = GetProductionAxisVelocities([axisNo]);
                    // 复测动作只移动失败站；串行使用运动控制组件，其它站的仪表测量仍可继续。
                    await _testStationRetryMotionLock.WaitAsync(token);
                    try
                    {
                        await TestStationRetestSequence.RunAsync(axisNo, settings.WaitPosition, settings.PressPosition,
                            _testStationDwellMilliseconds,
                            async (axis, target, movementToken) =>
                            {
                                await WaitIfProductionPausedAsync(movementToken);
                                await motionController.MoveAxesAbsoluteAsync(
                                    new Dictionary<int, double> { [axis] = target }, movementToken,
                                    TestStationMoveTimeoutMilliseconds, ProductionHandlingAxisNos,
                                    HomePageCompletionTolerance, velocityOverrides: velocities);
                            },
                            (milliseconds, delayToken) => Task.Delay(milliseconds, delayToken),
                            phase => SetTestStationRuntimeDisplay(stationNumber,
                                $"重试 {retryNumber}/{_testRetryCount} · {phase}", "机械复测",
                                $"{instrumentName} · 轴{axisNo} · {phase}", Color.FromRgb(242, 181, 68)), token);
                        await WaitIfProductionPausedAsync(token);
                    }
                    finally { _testStationRetryMotionLock.Release(); }
                },
                cancellationToken,
                canRetryException: exception => !SM7110TimedTest.HasStopFailure(exception));

            // 保存位于重测循环之外；写盘失败不能触发再次测量。另一站失败也不会丢失本站结果。
            await SaveStationMeasurementAsync(stationNumber, stationState, finalResult, attempts);
            return finalResult;

            async Task<TestStationMeasurementResult> MeasureOnceAsync(CancellationToken token)
            {
                attempts++;
                switch (settings.Instrument)
                {
                    case TestStationInstrument.E4981A:
                    {
                        var result = await connectionController.MeasureE4981AAsync(token);
                        return ClassifyE4981AMeasurement(result);
                    }
                    case TestStationInstrument.SM7110:
                    {
                        var bin = stationState.Bin;
                        if (string.IsNullOrWhiteSpace(bin))
                        {
                            throw new InvalidOperationException(
                                "产品尚无E4981A分BIN结果，SM7110不能生成或替代分BIN结果。");
                        }

                        var range = _sm7110AcceptanceRange
                            ?? throw new InvalidOperationException("本轮生产尚未锁定SM7110合格区间。");
                        range.ValidateMeasurementMode(connectionController.SM7110MeasurementMode);
                        var timedTest = range.MeasurementMode == "R" ? range.ToTimedTestSettings() : null;
                        var result = await connectionController.MeasureSM7110Async(token, timedTest, reading =>
                            SetTestStationRuntimeDisplay(stationNumber,
                                $"持续加电 · {reading.TestElapsedSeconds:0.###}秒", "测试中",
                                $"R={reading.DisplayValue:G9}GΩ · 达标门限≥{SM7110Protocol.ToDisplayValue(range.Lower, "R"):G9}GΩ · {reading.StatusDescription}",
                                Color.FromRgb(242, 181, 68)));
                        var measurement = ClassifySM7110Measurement(result, bin, range);
                        return stationState.E4981ALossRejected
                            ? measurement with
                            {
                                E4981ALossRejected = true,
                                E4981ALossFailureReason = stationState.E4981ALossFailureReason,
                                StatusDescription = "整体NG · 损耗超限",
                                DisplayText = $"{measurement.DisplayText} · {stationState.E4981ALossFailureReason} · NG→BIN0"
                            }
                            : measurement;
                    }
                    default:
                        throw new InvalidOperationException($"{stationNumber}号工位未分配仪表。");
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetTestStationRuntimeDisplay(
                stationNumber,
                "测试流程异常",
                "异常",
                exception.Message,
                Color.FromRgb(242, 122, 128));
            throw new InvalidOperationException(
                $"{stationNumber}号工位的{FormatTestStationInstrument(settings.Instrument ?? TestStationInstrument.None)}测试失败：{exception.Message}",
                exception);
        }
    }

    private static TestStationMeasurementResult ClassifyE4981AMeasurement(
        E4981AMeasurementResult result)
    {
        // 仪表已明确返回测量失败时按不良品流转，即使该次结果不含比较器BIN。
        // 正常测量缺少BIN仍属于配置异常，不能把未分档的产品当作正常生产结果。
        if (result.IsSuccessful && result.Bin is null)
        {
            throw new InvalidOperationException(
                "E4981A未返回比较器BIN，请在连接配置页启用仪表比较器并配置BIN范围。");
        }

        var bin = result.IsSuccessful && result.Bin is >= 1 and <= 3
            ? $"BIN{result.Bin}"
            : "BIN0";
        return new TestStationMeasurementResult(
            bin,
            $"C={result.CapacitanceNf:0.######}nF · D={result.DissipationFactor:G6} · " +
            (result.LossRejected ? $"{result.LossFailureReason} · NG→BIN0" : $"{bin}（仪表：{result.BinDescription}）"),
            result.LossRejected ? "NG · 损耗超限"
                : result.IsSuccessful && bin == "BIN0" ? "NG · 未落入BIN1～BIN3" : result.StatusDescription,
            result.IsSuccessful && !result.LossRejected,
            E4981ALossRejected: result.LossRejected,
            E4981ALossFailureReason: result.LossFailureReason,
            E4981AReading: result);
    }

    private static TestStationMeasurementResult ClassifySM7110Measurement(
        SM7110MeasurementResult result, string bin, SM7110AcceptanceRange range)
    {
        range.ValidateMeasurementMode(result.MeasurementMode);
        var isThreshold = range.MeasurementMode == "R";
        var passed = !result.TimedOut && result.IsSuccessful && result.Value >= range.Lower && (isThreshold || result.Value <= range.Upper);
        var status = result.TimedOut ? "NG · 超时未达标"
            : passed ? isThreshold ? "OK · 已达标" : "OK · 区间内"
            : result.IsSuccessful ? isThreshold ? "NG · 未达门限" : "NG · 超出合格区间" : $"NG · {result.StatusDescription}";
        var criterion = isThreshold
            ? $"达标门限≥{SM7110Protocol.ToDisplayValue(range.Lower, range.MeasurementMode):G9}{result.DisplayUnit}"
            : $"合格区间[{SM7110Protocol.ToDisplayValue(range.Lower, range.MeasurementMode):G9}, {SM7110Protocol.ToDisplayValue(range.Upper, range.MeasurementMode):G9}]{result.DisplayUnit}";
        return new TestStationMeasurementResult(
            bin,
            $"{result.MeasurementMode}={result.DisplayValue:G9}{result.DisplayUnit} · {criterion} · {status} · E4981A:{bin}",
            passed && bin == "BIN0" ? "整体NG · E4981A为BIN0" : status,
            passed,
            passed,
            result.Value,
            SM7110Reading: result);
    }

    private void AdvanceCarouselOccupancy(CarouselStationState[] carouselStations)
    {
        var station16 = carouselStations[CarouselStationCount];
        for (var station = CarouselStationCount; station >= 2; station--)
        {
            carouselStations[station] = carouselStations[station - 1];
        }

        carouselStations[1] = station16;
    }

    private void UpdateCarouselStationDisplay(
        IReadOnlyList<CarouselStationState> carouselStations,
        IEnumerable<int>? activeAxisNos = null,
        string? activeStatus = null)
    {
        if (CarouselStationCanvas is null || CarouselSummaryText is null)
        {
            return;
        }

        var activeStations = (activeAxisNos ?? [])
            .Select(axisNo => TestStationAxisByStation.FirstOrDefault(pair => pair.Value == axisNo).Key)
            .Where(station => station > 0)
            .ToHashSet();
        var cards = CarouselStationCanvas.Children
            .OfType<Border>()
            .Where(border => border.Child is StackPanel)
            .Take(CarouselStationCount)
            .ToArray();
        for (var index = 0; index < cards.Length; index++)
        {
            var station = index + 1;
            var state = station < carouselStations.Count ? carouselStations[station] : CarouselStationState.Empty();
            var occupied = state.Occupied;
            var isActive = activeStations.Contains(station);
            var card = cards[index];
            // 卡片表示固定机械工位；产品状态已在DD到位后推进，不能再把卡片偏移一次。
            var slot = CarouselStationCardSlots[index];
            Canvas.SetLeft(card, slot.X);
            Canvas.SetTop(card, slot.Y);
            var textBlocks = ((StackPanel)card.Child).Children.OfType<TextBlock>().ToArray();
            var resultText = textBlocks.ElementAtOrDefault(1);
            var objectText = textBlocks.ElementAtOrDefault(2);

            if (isActive && activeStatus is not null)
            {
                card.Background = new SolidColorBrush(Color.FromRgb(54, 50, 24));
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(231, 163, 43));
                if (resultText is not null)
                {
                    resultText.Text = activeStatus;
                    resultText.Foreground = new SolidColorBrush(Color.FromRgb(255, 210, 99));
                }

                if (objectText is not null)
                {
                    objectText.Text = "测试站";
                    objectText.Foreground = new SolidColorBrush(Color.FromRgb(232, 219, 171));
                }

                continue;
            }

            if (occupied)
            {
                var waitingForSM7110 = state.Tested && IsSM7110TestEnabled() && state.SM7110Passed is null;
                var rejected = state.Bin == "BIN0" || state.E4981ALossRejected || state.SM7110Passed == false;
                var complete = state.Tested && !waitingForSM7110;
                var stateColor = rejected ? Color.FromRgb(242, 122, 128)
                    : complete ? Color.FromRgb(73, 209, 125) : Color.FromRgb(242, 181, 68);
                card.Background = new SolidColorBrush(
                    rejected ? Color.FromRgb(61, 31, 35)
                    : complete ? Color.FromRgb(21, 61, 47) : Color.FromRgb(53, 45, 24));
                card.BorderBrush = new SolidColorBrush(stateColor);
                if (resultText is not null)
                {
                    resultText.Text = rejected ? "NG → 0号盒"
                        : waitingForSM7110 ? "待SM判定" : complete ? "已测试" : CarouselStatusLoaded;
                    resultText.Foreground = new SolidColorBrush(stateColor);
                }

                if (objectText is not null)
                {
                    objectText.Text = state.Tested
                        ? state.E4981ALossRejected ? "损耗超限" : rejected ? "NG · BIN0"
                            : state.SM7110Passed == true ? $"OK · {state.Bin}" : state.Bin ?? "BIN?"
                        : TestStationAxisByStation.ContainsKey(station) ? "待测试" : "有物体";
                    objectText.Foreground = new SolidColorBrush(stateColor);
                }
            }
            else
            {
                card.Background = new SolidColorBrush(Color.FromRgb(18, 40, 58));
                card.BorderBrush = new SolidColorBrush(Color.FromRgb(52, 82, 105));
                if (resultText is not null)
                {
                    resultText.Text = "空闲";
                    resultText.Foreground = new SolidColorBrush(Color.FromRgb(111, 131, 146));
                }

                if (objectText is not null)
                {
                    objectText.Text = "空工位";
                    objectText.Foreground = new SolidColorBrush(Color.FromRgb(159, 177, 191));
                }
            }
        }

        var occupiedCount = Enumerable.Range(1, Math.Min(CarouselStationCount, carouselStations.Count - 1))
            .Count(station => carouselStations[station].Occupied);
        var testedCount = Enumerable.Range(1, Math.Min(CarouselStationCount, carouselStations.Count - 1))
            .Count(station => carouselStations[station].Occupied && carouselStations[station].Tested);
        var activeText = activeStations.Count > 0 && activeStatus is not null
            ? $"｜测试站{activeStatus}"
            : "";
        CarouselSummaryText.Text = $"有料 {occupiedCount}/16｜已测 {testedCount}{activeText}";
        CarouselSummaryText.Foreground = new SolidColorBrush(
            activeStations.Count > 0 ? Color.FromRgb(242, 181, 68) : Color.FromRgb(54, 196, 106));
    }

    private static CarouselStationState[] CreateCarouselStationStates()
    {
        var states = new CarouselStationState[CarouselStationCount + 1];
        for (var index = 0; index < states.Length; index++)
        {
            states[index] = CarouselStationState.Empty();
        }

        return states;
    }

    private sealed record CarouselAdvanceResult(
        int Turns,
        Task<int> FinalTestTask,
        Task SecondSetUnloadTask,
        Task SecondSetPickupTask);

    private sealed class CarouselStationState
    {
        public Guid ProductId { get; private set; }

        public bool Occupied { get; private set; }

        public bool Tested { get; private set; }

        public string? Bin { get; private set; }

        public bool? SM7110Passed { get; private set; }

        public double? SM7110Value { get; private set; }

        public bool E4981ALossRejected { get; private set; }

        public string? E4981ALossFailureReason { get; private set; }

        public static CarouselStationState Empty() => new();

        public void SetLoaded()
        {
            ProductId = Guid.NewGuid();
            Occupied = true;
            Tested = false;
            Bin = null;
            SM7110Passed = null;
            SM7110Value = null;
            E4981ALossRejected = false;
            E4981ALossFailureReason = null;
        }

        public void SetTested(string bin)
        {
            Occupied = true;
            Tested = true;
            Bin = bin;
        }

        public void SetMeasurement(TestStationMeasurementResult result)
        {
            SetTested(result.Bin);
            // 只有第一站的最终结果可以更新损耗判定；后续SM7110合格不能清除NG。
            if (result.SM7110Passed is null)
            {
                E4981ALossRejected = result.E4981ALossRejected;
                E4981ALossFailureReason = result.E4981ALossFailureReason;
            }
            if (result.SM7110Passed.HasValue)
            {
                SM7110Passed = result.SM7110Passed;
                SM7110Value = result.SM7110Value;
            }
        }

        public string GetUnloadDestination(bool sm7110Enabled)
        {
            if (!Occupied || !Tested || Bin is not ("BIN0" or "BIN1" or "BIN2" or "BIN3"))
                throw new InvalidOperationException("产品尚无有效E4981A分档结果，禁止下料。");
            if (sm7110Enabled && SM7110Passed is null)
                throw new InvalidOperationException("产品尚未完成SM7110判定，禁止下料。");
            if (Bin == "BIN0" || E4981ALossRejected || (sm7110Enabled && SM7110Passed == false))
                return "NG";
            return Bin;
        }
    }

    /// <summary>
    /// 从第一套九点标定 XML 的 WorldPointLst 读取机械网格中心，并换算为控制卡脉冲。
    /// 直接读取标定文件可保证“回初始位置”不依赖当前固定方案内正在运行的流程。
    /// </summary>
    private static (double X, double Y) ReadFirstSetCalibrationCenter(string calibrationFilePath)
    {
        return VisionCalibrationFileReader.ReadNinePointCenterPulses(calibrationFilePath);
    }

    private static int CountOccupiedCarouselStations(IReadOnlyList<CarouselStationState> carouselStations)
    {
        return Enumerable.Range(1, Math.Min(CarouselStationCount, carouselStations.Count - 1))
            .Count(station => carouselStations[station].Occupied);
    }

    private (string FilePath, bool WasSelected) GetOrSelectFirstSetCalibrationFile()
    {
        var configuredPath = _visionCalibration.GetSnapshot().CalibrationFilePath.Trim();
        if (IsExistingFileWithExtension(configuredPath, ".xml"))
        {
            return (Path.GetFullPath(configuredPath), false);
        }

        return (
            SelectFile(
                "选择第一套 XY 标定文件",
                "VisionMaster 标定文件 (*.xml)|*.xml|所有文件 (*.*)|*.*",
                configuredPath,
                "已取消开始：未选择第一套 XY 标定文件。"),
            true);
    }

    private string SelectFile(
        string title,
        string filter,
        string configuredPath,
        string cancelledMessage)
    {
        var dialog = new OpenFileDialog
        {
            Title = title,
            Filter = filter,
            CheckFileExists = true,
            Multiselect = false,
            InitialDirectory = GetExistingDirectory(configuredPath)
        };
        var owner = Window.GetWindow(this);
        var accepted = owner is null ? dialog.ShowDialog() : dialog.ShowDialog(owner);
        if (accepted != true)
        {
            throw new OperationCanceledException(cancelledMessage);
        }

        return Path.GetFullPath(dialog.FileName);
    }

    private static string GetExistingDirectory(string configuredPath)
    {
        try
        {
            var configuredDirectory = string.IsNullOrWhiteSpace(configuredPath)
                ? ""
                : Path.GetDirectoryName(Path.GetFullPath(configuredPath)) ?? "";
            if (Directory.Exists(configuredDirectory))
            {
                return configuredDirectory;
            }
        }
        catch
        {
            // 路径格式失效时回退到桌面，让用户重新选择。
        }

        return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
    }

    private static bool IsExistingFileWithExtension(string path, string extension)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) &&
                   string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase) &&
                   File.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把相机图像中的像素坐标转换成第一套 XY 两个吸嘴各自的机械目标坐标。
    /// 此方法只读取当前轴坐标，不下发任何运动命令。
    /// </summary>
    public async Task<DualNozzleMechanicalTargets> ConvertPixelToMechanicalTargetsAsync(
        double pixelX,
        double pixelY,
        CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(pixelX) || !double.IsFinite(pixelY) || pixelX < 0 || pixelY < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pixelX), "像素坐标必须是大于等于0的有效数字。");
        }

        var snapshot = EnsureFirstSetToolsReady();
        var visualCalibrationController = _visualCalibrationController
            ?? throw new InvalidOperationException("主页尚未连接视觉标定组件。");
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");

        var transformed = await visualCalibrationController.TransformPixelAsync(
            pixelX,
            pixelY,
            snapshot.CalibrationFilePath,
            cancellationToken);
        var current = motionController.CaptureCalibrationFeedback(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo);
        var cameraTargetX = current.ActualX +
            (transformed.CenterTransformedX - transformed.TransformedX) *
            VisionCalibrationService.PulsesPerVisionUnit;
        var cameraTargetY = current.ActualY +
            (transformed.CenterTransformedY - transformed.TransformedY) *
            VisionCalibrationService.PulsesPerVisionUnit;

        return new DualNozzleMechanicalTargets(
            CalculateVisionTarget(cameraTargetX, cameraTargetY, VisionTargetTool.Nozzle1),
            CalculateVisionTarget(cameraTargetX, cameraTargetY, VisionTargetTool.Nozzle2));
    }

    /// <summary>
    /// 按 VisionMaster 原始结果顺序读取配置数量，每两个组成一批：
    /// 每批第一个给吸嘴1，第二个给吸嘴2。奇数最后1条不缓存，留在盘中震动后重拍。
    /// 全部目标都使用同一次拍照时的轴绝对位置换算。
    /// </summary>
    private IReadOnlyList<NozzlePickupBatch> CalculateAssignedNozzleBatches(
        VisionRectangleBlobResult blobResult,
        string calibrationFilePath,
        double captureX,
        double captureY,
        int maxCachedChipCount)
    {
        _ = EnsureFirstSetToolsReady();
        if (blobResult.ImageWidth <= 0 || blobResult.ImageHeight <= 0)
        {
            throw new InvalidOperationException("本次Blob结果没有有效图像尺寸，无法计算吸嘴目标。");
        }

        var selectedCount = Math.Min(blobResult.Rectangles.Count, maxCachedChipCount);
        for (var index = 0; index < selectedCount; index++)
        {
            ValidateBlobPixel(
                blobResult.Rectangles[index],
                blobResult.ImageWidth,
                blobResult.ImageHeight,
                $"Blob结果{index + 1}");
        }

        var calibrationMatrix = ReadCalibrationMatrix(calibrationFilePath);
        var imageCenterX = (blobResult.ImageWidth - 1d) / 2d;
        var imageCenterY = (blobResult.ImageHeight - 1d) / 2d;
        var centerWorld = TransformCalibrationPoint(calibrationMatrix, imageCenterX, imageCenterY);

        VisionMotionTarget CalculateTarget(VisionBlobRectangle blob, VisionTargetTool tool)
        {
            var blobWorld = TransformCalibrationPoint(calibrationMatrix, blob.X, blob.Y);
            var cameraTargetX = captureX +
                (centerWorld.X - blobWorld.X) * VisionCalibrationService.PulsesPerVisionUnit;
            var cameraTargetY = captureY +
                (centerWorld.Y - blobWorld.Y) * VisionCalibrationService.PulsesPerVisionUnit;
            return CalculateVisionTarget(cameraTargetX, cameraTargetY, tool) with
            {
                RotationDegrees = blob.RotationDegrees
            };
        }

        var pairedCount = selectedCount - selectedCount % 2;
        var batches = new List<NozzlePickupBatch>(pairedCount / 2);
        for (var index = 0; index < pairedCount; index += 2)
        {
            var nozzle1Target = CalculateTarget(
                blobResult.Rectangles[index],
                VisionTargetTool.Nozzle1);
            var nozzle2Target = CalculateTarget(
                blobResult.Rectangles[index + 1],
                VisionTargetTool.Nozzle2);
            batches.Add(new NozzlePickupBatch(nozzle1Target, nozzle2Target));
        }

        return batches;
    }

    private NozzlePickupBatch CalculateFinalSingleChipBatch(
        VisionRectangleBlobResult blobResult,
        string calibrationFilePath,
        double captureX,
        double captureY)
    {
        if (blobResult.Rectangles.Count != 1)
        {
            throw new InvalidOperationException("只有视野连续剩1颗时才能执行吸嘴2缺料收尾。");
        }

        var duplicatedSingleResult = blobResult with
        {
            Rectangles = [blobResult.Rectangles[0], blobResult.Rectangles[0]]
        };
        var batches = CalculateAssignedNozzleBatches(
            duplicatedSingleResult,
            calibrationFilePath,
            captureX,
            captureY,
            MinVisionPickupCount);
        return new NozzlePickupBatch(null, batches[0].Nozzle2);
    }

    private static void ValidateBlobPixel(
        VisionBlobRectangle blob,
        int imageWidth,
        int imageHeight,
        string name)
    {
        if (!double.IsFinite(blob.X) ||
            !double.IsFinite(blob.Y) ||
            !double.IsFinite(blob.RotationDegrees) ||
            blob.X < 0 ||
            blob.Y < 0 ||
            blob.X >= imageWidth ||
            blob.Y >= imageHeight)
        {
            throw new InvalidOperationException($"{name}的像素质心超出当前图像范围。");
        }
    }

    private static double[] ReadCalibrationMatrix(string calibrationFilePath)
    {
        XDocument document;
        try
        {
            document = XDocument.Load(calibrationFilePath);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            throw new InvalidDataException("第一套 XY 标定文件无法读取。", exception);
        }

        var matrixElement = document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibFloatListParam", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "ParamName", StringComparison.Ordinal))?.Value,
                    "CalibMatrix",
                    StringComparison.Ordinal));
        if (matrixElement is null)
        {
            throw new InvalidDataException("第一套 XY 标定文件中未找到 CalibMatrix。");
        }

        var values = matrixElement
            .Descendants()
            .Where(element => string.Equals(element.Name.LocalName, "ParamValue", StringComparison.Ordinal))
            .Select(element =>
            {
                if (!double.TryParse(
                        element.Value,
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var value) ||
                    !double.IsFinite(value))
                {
                    throw new InvalidDataException("第一套 XY 标定矩阵包含无效数值。");
                }

                return value;
            })
            .ToArray();
        if (values.Length != 9)
        {
            throw new InvalidDataException("第一套 XY 标定矩阵不是有效的3×3矩阵。");
        }

        return values;
    }

    private static (double X, double Y) TransformCalibrationPoint(
        IReadOnlyList<double> matrix,
        double pixelX,
        double pixelY)
    {
        var denominator = matrix[6] * pixelX + matrix[7] * pixelY + matrix[8];
        if (!double.IsFinite(denominator) || Math.Abs(denominator) < 1e-12)
        {
            throw new InvalidOperationException("第一套 XY 标定矩阵无法转换当前像素坐标。");
        }

        var worldX = (matrix[0] * pixelX + matrix[1] * pixelY + matrix[2]) / denominator;
        var worldY = (matrix[3] * pixelX + matrix[4] * pixelY + matrix[5]) / denominator;
        if (!double.IsFinite(worldX) || !double.IsFinite(worldY))
        {
            throw new InvalidOperationException("第一套 XY 标定矩阵返回了无效机械坐标。");
        }

        return (worldX, worldY);
    }

    private void SetAssignedNozzleTargets(NozzlePickupBatch targets, int remainingChipCount)
    {
        _blob1Nozzle1Target = targets.Nozzle1;
        _blob2Nozzle2Target = targets.Nozzle2;
        UpdateAssignedNozzleVisionText();
        var assignmentMessage = targets switch
        {
            { Nozzle1: { } nozzle1, Nozzle2: { } nozzle2 } =>
                $"本批按脚本顺序分配2颗：本批第1条→吸嘴1(R={nozzle1.RotationDegrees:0.###}°)，" +
                $"本批第2条→吸嘴2(R={nozzle2.RotationDegrees:0.###}°)；" +
                $"本次拍照缓存还剩 {remainingChipCount} 颗。",
            { Nozzle1: null, Nozzle2: { } nozzle2 } =>
                $"连续震动{EmptyTraySingleChipVibrationThreshold}次后仍剩1颗：" +
                $"改由吸嘴2收尾(R={nozzle2.RotationDegrees:0.###}°)，放到位置2后停止上料并排空转盘。",
            _ => throw new InvalidOperationException("本批没有可用的吸嘴目标。")
        };
        SetFirstSetPositionStatus(assignmentMessage, true);
        UpdateHomeCommandState();
    }

    private void ClearAssignedNozzleTargets()
    {
        _blob1Nozzle1Target = null;
        _blob2Nozzle2Target = null;
        UpdateAssignedNozzleVisionText();
        UpdateHomeCommandState();
    }

    private void UpdateAssignedNozzleVisionText()
    {
        if (Nozzle1RawVisionResultText is null || Nozzle2RawVisionResultText is null)
        {
            return;
        }

        Nozzle1RawVisionResultText.Text = _blob1Nozzle1Target is { } nozzle1
            ? FormatAssignedNozzleVisionText(1, nozzle1)
            : "吸嘴1：等待分配";
        Nozzle2RawVisionResultText.Text = _blob2Nozzle2Target is { } nozzle2
            ? FormatAssignedNozzleVisionText(2, nozzle2)
            : _blob1Nozzle1Target is null
                ? "吸嘴2：等待分配"
                : "吸嘴2：本批无产品";
    }

    private static string FormatAssignedNozzleVisionText(
        int nozzleNumber,
        VisionMotionTarget target)
    {
        var normalizedRotation = NozzleRotationMath.NormalizePeriodicAngleDegrees(
            target.RotationDegrees,
            SquareOrientationPeriodDegrees);
        return $"吸嘴{nozzleNumber}：X={target.X:0.###}  Y={target.Y:0.###}  " +
               $"原始R={target.RotationDegrees:0.#####}°  归一化R={normalizedRotation:0.#####}°";
    }

    private async Task MoveNozzleToAssignedTargetAsync(
        int nozzleNumber,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        var target = nozzleNumber switch
        {
            1 => _blob1Nozzle1Target,
            2 => _blob2Nozzle2Target,
            _ => null
        };
        if (target is null)
        {
            throw new InvalidOperationException("当前吸嘴目标尚未由Blob结果生成。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var xVelocity = GetProductionAxisMotionSettings(
            VisionCalibrationService.FirstSetXHardwareAxisNo).RunVelocity;
        var yVelocity = GetProductionAxisMotionSettings(
            VisionCalibrationService.FirstSetYHardwareAxisNo).RunVelocity;

        var nozzleName = nozzleNumber == 1 ? "吸嘴1" : "吸嘴2";
        var objectName = nozzleNumber == 1 ? "物体1" : "物体2";
        SetFirstSetPositionStatus(
            $"正在移动{nozzleName}到{objectName}：X={target.Value.X:0.###}，Y={target.Value.Y:0.###}",
            true);

        var productionPeerAxisNos = allowedMovingAxisNos ?? AllowedProductionPeerAxisNos;
        var current = motionController.CaptureCalibrationCenter(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            productionPeerAxisNos);
        var timeoutMilliseconds = CalculateStartMoveTimeout(
            current.ActualX,
            current.ActualY,
            target.Value.X,
            target.Value.Y,
            Math.Min(xVelocity, yVelocity));
        var actual = await motionController.MoveCalibrationAxesToAsync(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            target.Value.X,
            target.Value.Y,
            xVelocity,
            positionTolerance: HomePageCompletionTolerance,
            moveTimeoutMilliseconds: timeoutMilliseconds,
            cancellationToken: cancellationToken,
            allowedMovingAxisNos: productionPeerAxisNos,
            yVelocityOverride: yVelocity,
            linearInterpolationCoordinateSystemNo: GetFirstSetXyInterpolationCoordinateSystemNo());

        SetFirstSetPositionStatus(
            $"{nozzleName}已到{objectName}({actual.ActualX:0.###}, {actual.ActualY:0.###})。",
            true);
    }

    public Task<MotionAxisSnapshot> RotateDdOnceAsync(CancellationToken cancellationToken)
    {
        if (_startSequenceRunning ||
            _presetPositionMoveRunning ||
            _oneKeyResetRunning)
        {
            throw new InvalidOperationException("当前存在生产、复位或示教运动，不能单独转动 DD 马达。");
        }

        return MoveAxis0RelativeCoreAsync(DdMotorPulsePerTurn, cancellationToken);
    }

    private async Task<MotionAxisSnapshot> MoveAxis0RelativeCoreAsync(
        double pulseDistance,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        if (!double.IsFinite(pulseDistance) || pulseDistance == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pulseDistance), "DD马达脉冲必须是非零有效数字。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var velocity = GetProductionAxisMotionSettings(0).RunVelocity;
        return await motionController.MoveAxisRelativeAsync(
            hardwareAxisNo: 0,
            pulseDistance: pulseDistance,
            cancellationToken: cancellationToken,
            allowedMovingAxisNos: allowedMovingAxisNos,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: velocity);
    }

    private void RecordFirstSetTeachingCenter_Click(object sender, RoutedEventArgs e)
    {
        RecordFirstSetTeachingPosition(
            "中心位",
            FirstSetTeachingCenterXTextBox,
            FirstSetTeachingCenterYTextBox,
            isCenter: true);
    }

    private async void MoveFirstSetTeachingCenter_Click(object sender, RoutedEventArgs e)
    {
        await MoveFirstSetTeachingPositionAsync(
            "中心位",
            FirstSetTeachingCenterXTextBox,
            FirstSetTeachingCenterYTextBox,
            MoveFirstSetTeachingCenterButton);
    }

    private void RecordFirstSetTeachingPressPosition_Click(object sender, RoutedEventArgs e)
    {
        RecordFirstSetTeachingPosition(
            "示教下压位",
            FirstSetTeachingPressPositionXTextBox,
            FirstSetTeachingPressPositionYTextBox,
            isCenter: false);
    }

    private async void MoveFirstSetTeachingPressPosition_Click(object sender, RoutedEventArgs e)
    {
        await MoveFirstSetTeachingPositionAsync(
            "示教下压位",
            FirstSetTeachingPressPositionXTextBox,
            FirstSetTeachingPressPositionYTextBox,
            MoveFirstSetTeachingPressPositionButton);
    }

    private void RecordFirstSetTeachingPosition(
        string positionName,
        TextBox xInput,
        TextBox yInput,
        bool isCenter)
    {
        try
        {
            var existingX = 0d;
            var existingY = 0d;
            var positionAlreadyConfigured =
                TryParseCoordinate(xInput.Text, out existingX) &&
                TryParseCoordinate(yInput.Text, out existingY);
            if (positionAlreadyConfigured &&
                MessageBox.Show(
                    Window.GetWindow(this),
                    $"{positionName}当前为 X={existingX:0.###}、Y={existingY:0.###} pulse。\n\n" +
                    "确定用第一套 XY 的当前反馈位置覆盖吗？",
                    $"确认覆盖{positionName}",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning,
                    MessageBoxResult.No) != MessageBoxResult.Yes)
            {
                SetFirstSetTeachingPositionStatus($"已取消覆盖{positionName}。", true);
                return;
            }

            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var current = motionController.CaptureCalibrationFeedback(
                VisionCalibrationService.FirstSetXHardwareAxisNo,
                VisionCalibrationService.FirstSetYHardwareAxisNo);

            _loadingPresetPositions = true;
            xInput.Text = current.ActualX.ToString("0.###", CultureInfo.CurrentCulture);
            yInput.Text = current.ActualY.ToString("0.###", CultureInfo.CurrentCulture);
            _loadingPresetPositions = false;
            if (isCenter)
            {
                _homeSettings.FirstSetTeachingCenterX = current.ActualX;
                _homeSettings.FirstSetTeachingCenterY = current.ActualY;
            }
            else
            {
                _homeSettings.FirstSetTeachingPressPositionX = current.ActualX;
                _homeSettings.FirstSetTeachingPressPositionY = current.ActualY;
            }

            _homeSettingsStore.Save(_homeSettings);
            SetFirstSetTeachingPositionStatus(
                $"{positionName}已记录并保存：X={current.ActualX:0.###}、Y={current.ActualY:0.###} pulse。",
                true);
        }
        catch (Exception exception)
        {
            _loadingPresetPositions = false;
            SetFirstSetTeachingPositionStatus($"记录{positionName}失败：{exception.Message}", false);
        }
        finally
        {
            UpdateHomeCommandState();
        }
    }

    private async Task MoveFirstSetTeachingPositionAsync(
        string positionName,
        TextBox xInput,
        TextBox yInput,
        Button moveButton)
    {
        if (_presetPositionMoveRunning ||
            _oneKeyResetRunning ||
            _startSequenceRunning)
        {
            return;
        }

        try
        {
            var targetX = ParseFiniteCoordinate(xInput.Text, $"{positionName} X轴绝对脉冲");
            var targetY = ParseFiniteCoordinate(yInput.Text, $"{positionName} Y轴绝对脉冲");
            _ = ReadProductionAxisMotionSettings();
            _presetPositionMoveRunning = true;
            UpdateHomeCommandState();
            moveButton.Content = "移动中";
            SetFirstSetTeachingPositionStatus(
                $"正在移动到{positionName}：X={targetX:0.###}、Y={targetY:0.###} pulse…",
                true);
            await MovePresetPositionCoreAsync(
                positionName,
                targetX,
                targetY,
                CancellationToken.None);
            SetFirstSetTeachingPositionStatus(
                $"已移动到{positionName}：X={targetX:0.###}、Y={targetY:0.###} pulse。",
                true);
        }
        catch (Exception exception)
        {
            SetFirstSetTeachingPositionStatus($"移动{positionName}失败：{exception.Message}", false);
        }
        finally
        {
            _presetPositionMoveRunning = false;
            moveButton.Content = "移动";
            UpdateHomeCommandState();
        }
    }

    private async void MoveLowerCameraPhotoPosition1_Click(object sender, RoutedEventArgs e)
    {
        await MoveLowerCameraPhotoPositionAsync(
            "下相机拍照位1",
            LowerCameraPhotoPosition1XTextBox,
            LowerCameraPhotoPosition1YTextBox,
            MoveLowerCameraPhotoPosition1Button);
    }

    private void RecordLowerCameraPhotoPosition1_Click(object sender, RoutedEventArgs e)
    {
        RecordLowerCameraPhotoPosition(
            1,
            "下相机拍照位1",
            LowerCameraPhotoPosition1XTextBox,
            LowerCameraPhotoPosition1YTextBox);
    }

    private async void MoveLowerCameraPhotoPosition2_Click(object sender, RoutedEventArgs e)
    {
        await MoveLowerCameraPhotoPositionAsync(
            "下相机拍照位2",
            LowerCameraPhotoPosition2XTextBox,
            LowerCameraPhotoPosition2YTextBox,
            MoveLowerCameraPhotoPosition2Button);
    }

    private void RecordLowerCameraPhotoPosition2_Click(object sender, RoutedEventArgs e)
    {
        RecordLowerCameraPhotoPosition(
            2,
            "下相机拍照位2",
            LowerCameraPhotoPosition2XTextBox,
            LowerCameraPhotoPosition2YTextBox);
    }

    private void RecordLowerCameraPhotoPosition(
        int positionNumber,
        string positionName,
        TextBox xInput,
        TextBox yInput)
    {
        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var current = motionController.CaptureCalibrationFeedback(
                VisionCalibrationService.FirstSetXHardwareAxisNo,
                VisionCalibrationService.FirstSetYHardwareAxisNo);

            _loadingPresetPositions = true;
            xInput.Text = current.ActualX.ToString("0.###", CultureInfo.CurrentCulture);
            yInput.Text = current.ActualY.ToString("0.###", CultureInfo.CurrentCulture);
            _loadingPresetPositions = false;
            if (positionNumber == 1)
            {
                _homeSettings.LowerCameraPhotoPosition1X = current.ActualX;
                _homeSettings.LowerCameraPhotoPosition1Y = current.ActualY;
            }
            else
            {
                _homeSettings.LowerCameraPhotoPosition2X = current.ActualX;
                _homeSettings.LowerCameraPhotoPosition2Y = current.ActualY;
            }

            _homeSettingsStore.Save(_homeSettings);
            SetLowerCameraPhotoPositionStatus(
                $"{positionName}已记录：X={current.ActualX:0.###}，Y={current.ActualY:0.###} pulse。",
                true);
        }
        catch (Exception exception)
        {
            _loadingPresetPositions = false;
            SetLowerCameraPhotoPositionStatus($"{positionName}记录失败：{exception.Message}", false);
        }
        finally
        {
            UpdateHomeCommandState();
        }
    }

    private async Task MoveLowerCameraPhotoPositionAsync(
        string positionName,
        TextBox xInput,
        TextBox yInput,
        Button moveButton)
    {
        if (_presetPositionMoveRunning || _oneKeyResetRunning ||
            _startSequenceRunning)
        {
            return;
        }

        try
        {
            var targetX = ParseFiniteCoordinate(xInput.Text, $"{positionName} X轴绝对脉冲");
            var targetY = ParseFiniteCoordinate(yInput.Text, $"{positionName} Y轴绝对脉冲");
            _ = ReadProductionAxisMotionSettings();
            _presetPositionMoveRunning = true;
            moveButton.Content = "移动中";
            UpdateHomeCommandState();
            SetLowerCameraPhotoPositionStatus(
                $"正在移动{positionName}：X={targetX:0.###}，Y={targetY:0.###} pulse…",
                true);
            await MovePresetPositionCoreAsync(positionName, targetX, targetY, CancellationToken.None);
            SetLowerCameraPhotoPositionStatus(
                $"{positionName}已到位：X={targetX:0.###}，Y={targetY:0.###} pulse。",
                true);
        }
        catch (Exception exception)
        {
            SetLowerCameraPhotoPositionStatus($"{positionName}移动失败：{exception.Message}", false);
        }
        finally
        {
            _presetPositionMoveRunning = false;
            moveButton.Content = "移动";
            UpdateHomeCommandState();
        }
    }

    private async void MovePresetPosition1_Click(object sender, RoutedEventArgs e)
    {
        await MovePresetPositionAsync(
            "位置 1",
            PresetPosition1XTextBox,
            PresetPosition1YTextBox,
            MovePresetPosition1Button);
    }

    private void RecordPresetPosition1_Click(object sender, RoutedEventArgs e)
    {
        RecordPresetPosition(
            1,
            "位置 1",
            PresetPosition1XTextBox,
            PresetPosition1YTextBox);
    }

    private async void MovePresetPosition2_Click(object sender, RoutedEventArgs e)
    {
        await MovePresetPositionAsync(
            "位置 2",
            PresetPosition2XTextBox,
            PresetPosition2YTextBox,
            MovePresetPosition2Button);
    }

    private void RecordPresetPosition2_Click(object sender, RoutedEventArgs e)
    {
        RecordPresetPosition(
            2,
            "位置 2",
            PresetPosition2XTextBox,
            PresetPosition2YTextBox);
    }

    private async void MoveSecondSetPosition1_Click(object sender, RoutedEventArgs e)
    {
        await MoveSecondSetPositionAsync(
            "第二套位置 1（吸嘴2取13）",
            SecondSetPosition1XTextBox,
            SecondSetPosition1YTextBox,
            MoveSecondSetPosition1Button);
    }

    private void RecordSecondSetPosition1_Click(object sender, RoutedEventArgs e)
    {
        RecordSecondSetPosition(
            1,
            "第二套位置 1（吸嘴2取13）",
            SecondSetPosition1XTextBox,
            SecondSetPosition1YTextBox);
    }

    private async void MoveSecondSetPosition2_Click(object sender, RoutedEventArgs e)
    {
        await MoveSecondSetPositionAsync(
            "第二套位置 2（吸嘴1取14）",
            SecondSetPosition2XTextBox,
            SecondSetPosition2YTextBox,
            MoveSecondSetPosition2Button);
    }

    private void RecordSecondSetPosition2_Click(object sender, RoutedEventArgs e)
    {
        RecordSecondSetPosition(
            2,
            "第二套位置 2（吸嘴1取14）",
            SecondSetPosition2XTextBox,
            SecondSetPosition2YTextBox);
    }

    private void RecordSecondSetPosition(
        int positionNumber,
        string positionName,
        TextBox xInput,
        TextBox yInput)
    {
        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var current = motionController.CaptureCalibrationFeedback(
                VisionCalibrationService.SecondSetXHardwareAxisNo,
                VisionCalibrationService.SecondSetYHardwareAxisNo);

            _loadingPresetPositions = true;
            xInput.Text = current.ActualX.ToString("0.###", CultureInfo.CurrentCulture);
            yInput.Text = current.ActualY.ToString("0.###", CultureInfo.CurrentCulture);
            _loadingPresetPositions = false;

            if (positionNumber == 1)
            {
                _homeSettings.SecondSetPickupPosition1X = current.ActualX;
                _homeSettings.SecondSetPickupPosition1Y = current.ActualY;
            }
            else
            {
                _homeSettings.SecondSetPickupPosition2X = current.ActualX;
                _homeSettings.SecondSetPickupPosition2Y = current.ActualY;
            }

            _homeSettingsStore.Save(_homeSettings);
            SetFirstSetPositionStatus(
                $"{positionName}已记录并保存：X={current.ActualX:0.###}，Y={current.ActualY:0.###} pulse。",
                true);
        }
        catch (Exception exception)
        {
            _loadingPresetPositions = false;
            SetFirstSetPositionStatus($"{positionName}记录失败：{exception.Message}", false);
        }
        finally
        {
            UpdateHomeCommandState();
        }
    }

    private async Task MoveSecondSetPositionAsync(
        string positionName,
        TextBox xInput,
        TextBox yInput,
        Button moveButton)
    {
        if (_presetPositionMoveRunning ||
            _oneKeyResetRunning ||
            _startSequenceRunning)
        {
            return;
        }

        try
        {
            var targetX = ParseFiniteCoordinate(xInput.Text, $"{positionName} X轴绝对脉冲");
            var targetY = ParseFiniteCoordinate(yInput.Text, $"{positionName} Y轴绝对脉冲");

            _presetPositionMoveRunning = true;
            UpdateHomeCommandState();
            moveButton.Content = "移动中";
            SetFirstSetPositionStatus(
                $"正在绝对移动{positionName}：X={targetX:0.###}，Y={targetY:0.###} pulse…",
                true);
            await MoveSecondSetUnloadAxesToAsync(
                positionName,
                targetX,
                targetY,
                CancellationToken.None);
            SetFirstSetPositionStatus(
                $"{positionName}已到位：X={targetX:0.###}，Y={targetY:0.###} pulse。",
                true);
        }
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"{positionName}移动失败：{exception.Message}", false);
        }
        finally
        {
            _presetPositionMoveRunning = false;
            moveButton.Content = "移动";
            UpdateHomeCommandState();
        }
    }

    private void RecordPresetPosition(
        int positionNumber,
        string positionName,
        TextBox xInput,
        TextBox yInput)
    {
        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var current = motionController.CaptureCalibrationFeedback(
                VisionCalibration.XHardwareAxisNo,
                VisionCalibration.YHardwareAxisNo);

            _loadingPresetPositions = true;
            xInput.Text = current.ActualX.ToString("0.###", CultureInfo.CurrentCulture);
            yInput.Text = current.ActualY.ToString("0.###", CultureInfo.CurrentCulture);
            _loadingPresetPositions = false;

            if (positionNumber == 1)
            {
                _homeSettings.PresetPosition1X = current.ActualX;
                _homeSettings.PresetPosition1Y = current.ActualY;
            }
            else
            {
                _homeSettings.PresetPosition2X = current.ActualX;
                _homeSettings.PresetPosition2Y = current.ActualY;
            }

            _homeSettingsStore.Save(_homeSettings);
            SetFirstSetPositionStatus(
                $"{positionName}已记录并保存：X={current.ActualX:0.###}，Y={current.ActualY:0.###} pulse。",
                true);
        }
        catch (Exception exception)
        {
            _loadingPresetPositions = false;
            SetFirstSetPositionStatus($"{positionName}记录失败：{exception.Message}", false);
        }
        finally
        {
            UpdateHomeCommandState();
        }
    }

    private async Task MovePresetPositionAsync(
        string positionName,
        TextBox xInput,
        TextBox yInput,
        Button moveButton)
    {
        if (_presetPositionMoveRunning ||
            _startSequenceRunning)
        {
            return;
        }

        try
        {
            var targetX = ParseFiniteCoordinate(xInput.Text, $"{positionName} X 轴绝对脉冲");
            var targetY = ParseFiniteCoordinate(yInput.Text, $"{positionName} Y 轴绝对脉冲");
            _ = ReadProductionAxisMotionSettings();

            _presetPositionMoveRunning = true;
            UpdateHomeCommandState();
            moveButton.Content = "移动中";
            await MovePresetPositionCoreAsync(
                positionName,
                targetX,
                targetY,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"{positionName}移动失败：{exception.Message}", false);
        }
        finally
        {
            _presetPositionMoveRunning = false;
            moveButton.Content = "移动";
            UpdateHomeCommandState();
        }
    }

    private async Task MovePresetPositionCoreAsync(
        string positionName,
        double targetX,
        double targetY,
        CancellationToken cancellationToken,
        IReadOnlyCollection<int>? allowedMovingAxisNos = null)
    {
        if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
        {
            throw new ArgumentOutOfRangeException(nameof(targetX), $"{positionName}的XY目标必须是有效数字。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var xVelocity = GetProductionAxisMotionSettings(
            VisionCalibrationService.FirstSetXHardwareAxisNo).RunVelocity;
        var yVelocity = GetProductionAxisMotionSettings(
            VisionCalibrationService.FirstSetYHardwareAxisNo).RunVelocity;

        SetFirstSetPositionStatus(
            $"正在绝对移动{positionName}：X={targetX:0.###}，Y={targetY:0.###} pulse…",
            true);
        var current = motionController.CaptureCalibrationFeedback(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            allowedMovingAxisNos ?? AllowedProductionPeerAxisNos);
        var timeoutMilliseconds = CalculateStartMoveTimeout(
            current.ActualX,
            current.ActualY,
            targetX,
            targetY,
            Math.Min(xVelocity, yVelocity));
        var actual = await motionController.MoveCalibrationAxesToAsync(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            targetX,
            targetY,
            xVelocity,
            positionTolerance: HomePageCompletionTolerance,
            moveTimeoutMilliseconds: timeoutMilliseconds,
            cancellationToken: cancellationToken,
            allowedMovingAxisNos: allowedMovingAxisNos ?? AllowedProductionPeerAxisNos,
            yVelocityOverride: yVelocity,
            linearInterpolationCoordinateSystemNo: GetFirstSetXyInterpolationCoordinateSystemNo());
        SetFirstSetPositionStatus(
            $"{positionName}已到位：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse。",
            true);
    }

    private async Task MoveToFirstLowerCameraPositionWithPickupAnglesAsync(
        string positionName,
        double targetX,
        double targetY,
        NozzlePickupBatch pickupBatch,
        CalibrationCenterPosition pickupRPositions,
        bool upperCameraCorrectionEnabled,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
        {
            throw new ArgumentOutOfRangeException(nameof(targetX), $"{positionName}的XY目标必须是有效数字。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);

        var absoluteTargets = new Dictionary<int, double>
        {
            [VisionCalibrationService.FirstSetXHardwareAxisNo] = targetX,
            [VisionCalibrationService.FirstSetYHardwareAxisNo] = targetY
        };
        var rotationTargets = upperCameraCorrectionEnabled
            ? CalculateUpperCameraRotationTargets(pickupBatch, pickupRPositions)
            : UpperCameraRotationTargets.Empty;
        foreach (var (axisNo, target) in rotationTargets.AbsoluteTargets)
        {
            absoluteTargets[axisNo] = target;
        }

        SetFirstSetPositionStatus(
            (upperCameraCorrectionEnabled
                ? $"正在同步移动{positionName}并执行上相机旋转纠偏：X={targetX:0.###}，Y={targetY:0.###}"
                : $"正在移动{positionName}；上相机旋转纠偏未勾选：X={targetX:0.###}，Y={targetY:0.###}") +
            (rotationTargets.Nozzle1Pulses.HasValue
                ? $"，吸嘴1 测量{pickupBatch.Nozzle1!.Value.RotationDegrees:0.###}°" +
                  $"→校正{rotationTargets.Nozzle1CorrectionDegrees!.Value:0.###}°" +
                  $"（R目标{rotationTargets.Nozzle1TargetR!.Value:0.###} pulse）"
                : string.Empty) +
            (rotationTargets.Nozzle2Pulses.HasValue
                ? $"，吸嘴2 测量{pickupBatch.Nozzle2!.Value.RotationDegrees:0.###}°" +
                  $"→校正{rotationTargets.Nozzle2CorrectionDegrees!.Value:0.###}°" +
                  $"（R目标{rotationTargets.Nozzle2TargetR!.Value:0.###} pulse）"
                : string.Empty) +
            "…",
            true);

        var movingAxisNos = absoluteTargets.Keys.ToArray();
        var interpolationCoordinateSystemNo = GetFirstSetXyInterpolationCoordinateSystemNo();
        await motionController.MoveAxesSynchronizedAsync(
            absoluteTargets,
            new Dictionary<int, double>(),
            cancellationToken,
            allowedMovingAxisNos: AllowedProductionPeerAxisNos,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverrides: GetProductionAxisVelocities(movingAxisNos),
            linearInterpolationCoordinateSystemNo: interpolationCoordinateSystemNo,
            linearInterpolationAxisNos: interpolationCoordinateSystemNo is null
                ? null
                : FirstSetAxisNos);

        var actualXy = motionController.CaptureCalibrationFeedback(
            VisionCalibrationService.FirstSetXHardwareAxisNo,
            VisionCalibrationService.FirstSetYHardwareAxisNo,
            AllowedProductionPeerAxisNos);
        SetFirstSetPositionStatus(
            upperCameraCorrectionEnabled
                ? $"{positionName}与上相机旋转纠偏已同步完成：" +
                  $"X={actualXy.ActualX:0.###}，Y={actualXy.ActualY:0.###}，" +
                  FormatUpperCameraRotationCompletion(rotationTargets)
                : $"{positionName}已到位，上相机旋转纠偏已跳过：" +
                  $"X={actualXy.ActualX:0.###}，Y={actualXy.ActualY:0.###}。",
            true);
    }

    private async Task ApplyUpperCameraRotationCorrectionsAsync(
        NozzlePickupBatch pickupBatch,
        CalibrationCenterPosition pickupRPositions,
        CancellationToken cancellationToken)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);
        var rotationTargets = CalculateUpperCameraRotationTargets(pickupBatch, pickupRPositions);
        if (rotationTargets.AbsoluteTargets.Count == 0)
        {
            return;
        }

        SetFirstSetPositionStatus(
            "下相机纠偏已关闭，正在单独执行上相机R轴旋转纠偏…",
            true);
        var movingAxisNos = rotationTargets.AbsoluteTargets.Keys.ToArray();
        await motionController.MoveAxesSynchronizedAsync(
            rotationTargets.AbsoluteTargets,
            new Dictionary<int, double>(),
            cancellationToken,
            allowedMovingAxisNos: AllowedProductionPeerAxisNos,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverrides: GetProductionAxisVelocities(movingAxisNos));
        SetFirstSetPositionStatus(
            $"上相机R轴旋转纠偏完成：{FormatUpperCameraRotationCompletion(rotationTargets)}",
            true);
    }

    private UpperCameraRotationTargets CalculateUpperCameraRotationTargets(
        NozzlePickupBatch pickupBatch,
        CalibrationCenterPosition pickupRPositions)
    {
        // 找芯片脚本返回的是正方形某条边相对水平线的当前姿态，不是电机相对量。
        // 正方形边方向每90°等价：先求到统一方向的最短校正角，再构造R轴绝对目标。
        var absoluteTargets = new Dictionary<int, double>();
        double? nozzle1Pulses = null;
        double? nozzle1CorrectionDegrees = null;
        double? nozzle1TargetR = null;
        if (pickupBatch.Nozzle1 is { } nozzle1Target)
        {
            nozzle1Pulses = ConvertUpperCameraMeasuredAngleToRCorrectionPulses(
                nozzle1Target.RotationDegrees,
                GetUpperCameraRotationSign(1),
                out var correctionDegrees);
            nozzle1CorrectionDegrees = correctionDegrees;
            nozzle1TargetR = pickupRPositions.ActualX + nozzle1Pulses.Value;
            absoluteTargets[FirstSetNozzle1RHardwareAxisNo] = nozzle1TargetR.Value;
        }

        double? nozzle2Pulses = null;
        double? nozzle2CorrectionDegrees = null;
        double? nozzle2TargetR = null;
        if (pickupBatch.Nozzle2 is { } nozzle2Target)
        {
            nozzle2Pulses = ConvertUpperCameraMeasuredAngleToRCorrectionPulses(
                nozzle2Target.RotationDegrees,
                GetUpperCameraRotationSign(2),
                out var correctionDegrees);
            nozzle2CorrectionDegrees = correctionDegrees;
            nozzle2TargetR = pickupRPositions.ActualY + nozzle2Pulses.Value;
            absoluteTargets[FirstSetNozzle2RHardwareAxisNo] = nozzle2TargetR.Value;
        }

        return new UpperCameraRotationTargets(
            absoluteTargets,
            nozzle1Pulses,
            nozzle1CorrectionDegrees,
            nozzle1TargetR,
            nozzle2Pulses,
            nozzle2CorrectionDegrees,
            nozzle2TargetR);
    }

    private static string FormatUpperCameraRotationCompletion(
        UpperCameraRotationTargets targets)
    {
        return (targets.Nozzle1Pulses.HasValue
                   ? $"R1校正{targets.Nozzle1CorrectionDegrees!.Value:0.###}°/" +
                     $"{targets.Nozzle1Pulses.Value:0.###} pulse"
                   : "R1无料") +
               (targets.Nozzle2Pulses.HasValue
                   ? $"，R2校正{targets.Nozzle2CorrectionDegrees!.Value:0.###}°/" +
                     $"{targets.Nozzle2Pulses.Value:0.###} pulse。"
                   : "。");
    }

    private static double ConvertUpperCameraMeasuredAngleToRCorrectionPulses(
        double measuredAngle,
        double rotationSign,
        out double correctionDegrees)
    {
        correctionDegrees = ValidateRotationSign(rotationSign) *
            NozzleRotationMath.CalculateShortestCorrectionDegrees(
            measuredAngle,
            UnifiedChipTargetAngleDegrees,
            SquareOrientationPeriodDegrees);
        return NozzleRotationMath.ConvertDegreesToPulses(
            correctionDegrees,
            NozzleRPulsesPerRevolution);
    }

    private static double ConvertLowerCameraMeasuredAngleToRCorrectionPulses(
        double measuredAngle,
        double rotationSign,
        out double correctionDegrees)
    {
        correctionDegrees = ValidateRotationSign(rotationSign) *
            NozzleRotationMath.CalculateShortestCorrectionDegrees(
            measuredAngle,
            UnifiedChipTargetAngleDegrees,
            SquareOrientationPeriodDegrees);
        return NozzleRotationMath.ConvertDegreesToPulses(
            correctionDegrees,
            NozzleRPulsesPerRevolution);
    }

    private double GetUpperCameraRotationSign(int nozzleNumber)
    {
        return ValidateRotationSign(nozzleNumber switch
        {
            1 => _homeSettings.UpperCameraNozzle1RotationSign ??
                 DefaultUpperCameraNozzleRotationSign,
            2 => _homeSettings.UpperCameraNozzle2RotationSign ??
                 DefaultUpperCameraNozzleRotationSign,
            _ => throw new ArgumentOutOfRangeException(nameof(nozzleNumber), "吸嘴编号只能是1或2。")
        });
    }

    private double GetLowerCameraRotationSign(int nozzleNumber)
    {
        return ValidateRotationSign(nozzleNumber switch
        {
            1 => _homeSettings.LowerCameraNozzle1RotationSign ??
                 DefaultLowerCameraNozzleRotationSign,
            2 => _homeSettings.LowerCameraNozzle2RotationSign ??
                 DefaultLowerCameraNozzleRotationSign,
            _ => throw new ArgumentOutOfRangeException(nameof(nozzleNumber), "吸嘴编号只能是1或2。")
        });
    }

    private static double ValidateRotationSign(double rotationSign)
    {
        if (rotationSign is not (-1d or 1d))
        {
            throw new InvalidOperationException("视觉R方向系数只能配置为1或-1。");
        }

        return rotationSign;
    }

    private void PresetPositionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SavePresetPositionsFromInputs();
        UpdateHomeCommandState();
    }

    private void FirstSetTeachingPositionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveFirstSetTeachingPositionsFromInputs();
        if (!_loadingPresetPositions)
        {
            UpdateHomeCommandState();
        }
    }

    private void ProductionAxisParameterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingPresetPositions)
        {
            SaveProductionAxisParametersFromInputs();
            UpdateHomeCommandState();
        }
    }

    private void VisionPickupCountTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveVisionPickupCountFromInput();
        if (!_loadingPresetPositions)
        {
            UpdateHomeCommandState();
        }
    }

    private void TestStationPositionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingPresetPositions)
        {
            SaveTestStationParametersFromInputs();
            UpdateHomeCommandState();
        }
    }

    private void LowerCameraPhotoPositionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveLowerCameraPhotoPositionsFromInputs();
        UpdateHomeCommandState();
    }

    private void LowerCameraCorrection1CheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPresetPositions || LowerCameraCorrection1CheckBox is null)
        {
            return;
        }

        _homeSettings.LowerCameraCorrectionEnabled =
            LowerCameraCorrection1CheckBox.IsChecked == true;
        _homeSettingsStore.Save(_homeSettings);
        SetLowerCameraPhotoPositionStatus(
            LowerCameraCorrection1CheckBox.IsChecked == true
                ? "下相机纠偏1已启用：两个吸嘴将依次拍照纠偏后放料。"
                : "下相机纠偏1已关闭：两个吸嘴将分别按位置1、位置2原坐标放料。",
            true);
        UpdateHomeCommandState();
    }

    private void LowerCameraRotationDelayCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPresetPositions || LowerCameraRotationDelayCheckBox is null)
        {
            return;
        }

        _homeSettings.LowerCameraRotationDelayEnabled =
            LowerCameraRotationDelayCheckBox.IsChecked == true;
        _homeSettingsStore.Save(_homeSettings);
        SetLowerCameraPhotoPositionStatus(
            _homeSettings.LowerCameraRotationDelayEnabled
                ? "调试等待已启用：每批下相机纠偏完成后等待3秒，再移动XY和旋转R轴。"
                : "调试等待已关闭：下相机纠偏完成后按正常节拍运动。",
            true);
        UpdateHomeCommandState();
    }

    private void XyLinearInterpolationCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPresetPositions || XyLinearInterpolationCheckBox is null)
        {
            return;
        }

        _homeSettings.XyLinearInterpolationEnabled =
            XyLinearInterpolationCheckBox.IsChecked == true;
        _homeSettingsStore.Save(_homeSettings);
        SetFirstSetPositionStatus(
            XyLinearInterpolationCheckBox.IsChecked == true
                ? "生产XY直线插补已启用：两套XY将沿直线运动。"
                : "生产XY直线插补已关闭：两套XY恢复原同步点位运动。",
            true);
        UpdateHomeCommandState();
    }

    private void UpperCameraCorrectionCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_loadingPresetPositions || UpperCameraCorrectionCheckBox is null)
        {
            return;
        }

        _homeSettings.UpperCameraCorrectionEnabled =
            UpperCameraCorrectionCheckBox.IsChecked == true;
        _homeSettingsStore.Save(_homeSettings);
        SetLowerCameraPhotoPositionStatus(
            UpperCameraCorrectionCheckBox.IsChecked == true
                ? "上相机纠偏已启用：R1/R2将按上相机测得角度旋转。"
                : "上相机纠偏已关闭：保留拍照和XY对位，R1/R2不执行上相机角度旋转。",
            true);
        UpdateHomeCommandState();
    }

    private void LowerCameraRotationCenterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveLowerCameraRotationCentersFromInputs();
        UpdateHomeCommandState();
    }

    private void ProductionZPositionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveProductionZPositionsFromInputs();
        if (!_loadingPresetPositions)
        {
            UpdateHomeCommandState();
        }
    }

    private void SecondSetXyPositionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveSecondSetXyPositionsFromInputs();
        if (!_loadingPresetPositions)
        {
            UpdateHomeCommandState();
        }
    }

    private void BinPositionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SaveBinPositionsFromInputs();
        if (!_loadingPresetPositions)
        {
            UpdateHomeCommandState();
        }
    }

    private void LoadPresetPositions()
    {
        _homeSettings = _homeSettingsStore.Load();
        _loadingPresetPositions = true;
        XyLinearInterpolationCheckBox.IsChecked =
            _homeSettings.XyLinearInterpolationEnabled ?? false;
        UpperCameraCorrectionCheckBox.IsChecked =
            _homeSettings.UpperCameraCorrectionEnabled ?? true;
        LowerCameraCorrection1CheckBox.IsChecked =
            _homeSettings.LowerCameraCorrectionEnabled ?? true;
        LowerCameraRotationDelayCheckBox.IsChecked =
            _homeSettings.LowerCameraRotationDelayEnabled;
        LoadObservationCompensationInputs();
        var visionPickupCount = _homeSettings.VisionPickupCount ?? DefaultVisionPickupCount;
        _homeSettings.VisionPickupCount = visionPickupCount;
        VisionPickupCountTextBox.Text = visionPickupCount.ToString(CultureInfo.CurrentCulture);
        FirstSetTeachingCenterXTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetTeachingCenterX);
        FirstSetTeachingCenterYTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetTeachingCenterY);
        FirstSetTeachingPressPositionXTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetTeachingPressPositionX);
        FirstSetTeachingPressPositionYTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetTeachingPressPositionY);
        PresetPosition1XTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition1X);
        PresetPosition1YTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition1Y);
        PresetPosition2XTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition2X);
        PresetPosition2YTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition2Y);
        LowerCameraPhotoPosition1XTextBox.Text = FormatPresetCoordinate(
            _homeSettings.LowerCameraPhotoPosition1X);
        LowerCameraPhotoPosition1YTextBox.Text = FormatPresetCoordinate(
            _homeSettings.LowerCameraPhotoPosition1Y);
        LowerCameraPhotoPosition2XTextBox.Text = FormatPresetCoordinate(
            _homeSettings.LowerCameraPhotoPosition2X);
        LowerCameraPhotoPosition2YTextBox.Text = FormatPresetCoordinate(
            _homeSettings.LowerCameraPhotoPosition2Y);
        LowerCameraNozzle1RotationCenterXTextBox.Text = FormatPresetCoordinate(
            _homeSettings.LowerCameraNozzle1RotationCenterX);
        LowerCameraNozzle1RotationCenterYTextBox.Text = FormatPresetCoordinate(
            _homeSettings.LowerCameraNozzle1RotationCenterY);
        LowerCameraNozzle2RotationCenterXTextBox.Text = FormatPresetCoordinate(
            _homeSettings.LowerCameraNozzle2RotationCenterX);
        LowerCameraNozzle2RotationCenterYTextBox.Text = FormatPresetCoordinate(
            _homeSettings.LowerCameraNozzle2RotationCenterY);
        LoadProductionAxisParameterEditors();
        LoadTestStationPositionEditors();
        FirstSetNozzle1PickupZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetNozzle1PickupZPosition
            ?? _homeSettings.FirstSetPickupZPosition
            ?? DefaultNozzlePickupZPosition);
        FirstSetNozzle1DropZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetNozzle1DropZPosition
            ?? _homeSettings.FirstSetDropZPosition
            ?? DefaultNozzleDropZPosition);
        FirstSetNozzle1SafeZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetNozzle1SafeZPosition
            ?? _homeSettings.FirstSetSafeZPosition
            ?? DefaultNozzleSafeZPosition);
        FirstSetNozzle2PickupZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetNozzle2PickupZPosition
            ?? _homeSettings.FirstSetPickupZPosition
            ?? DefaultNozzlePickupZPosition);
        FirstSetNozzle2DropZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetNozzle2DropZPosition
            ?? _homeSettings.FirstSetDropZPosition
            ?? DefaultNozzleDropZPosition);
        FirstSetNozzle2SafeZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetNozzle2SafeZPosition
            ?? _homeSettings.FirstSetSafeZPosition
            ?? DefaultNozzleSafeZPosition);
        SecondSetNozzle1PickupZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetNozzle1PickupZPosition
            ?? _homeSettings.SecondSetPickupZPosition
            ?? DefaultNozzlePickupZPosition);
        SecondSetNozzle1DropZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetNozzle1DropZPosition
            ?? _homeSettings.SecondSetDropZPosition
            ?? DefaultNozzleDropZPosition);
        SecondSetNozzle1SafeZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetNozzle1SafeZPosition
            ?? _homeSettings.SecondSetSafeZPosition
            ?? DefaultNozzleSafeZPosition);
        SecondSetNozzle2PickupZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetNozzle2PickupZPosition
            ?? _homeSettings.SecondSetPickupZPosition
            ?? DefaultNozzlePickupZPosition);
        SecondSetNozzle2DropZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetNozzle2DropZPosition
            ?? _homeSettings.SecondSetDropZPosition
            ?? DefaultNozzleDropZPosition);
        SecondSetNozzle2SafeZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetNozzle2SafeZPosition
            ?? _homeSettings.SecondSetSafeZPosition
            ?? DefaultNozzleSafeZPosition);
        VacuumPickupDwellTextBox.Text = (
            _homeSettings.VacuumPickupDwellMilliseconds
            ?? DefaultVacuumPickupDwellMilliseconds).ToString(CultureInfo.CurrentCulture);
        var vacuumBreakPulseMilliseconds =
            _homeSettings.VacuumBreakPulseMilliseconds
            ?? DefaultVacuumBreakPulseMilliseconds;
        _homeSettings.VacuumBreakPulseMilliseconds = vacuumBreakPulseMilliseconds;
        VacuumBreakPulseTextBox.Text = vacuumBreakPulseMilliseconds.ToString(CultureInfo.CurrentCulture);
        VacuumValveSwitchDelayTextBox.Text = (
            _homeSettings.VacuumValveSwitchDelayMilliseconds
            ?? DefaultVacuumValveSwitchDelayMilliseconds).ToString(CultureInfo.CurrentCulture);
        FirstSetNozzle1XyReleaseLiftTextBox.Text = (
            _homeSettings.FirstSetNozzle1XyReleaseLiftPulses
            ?? DefaultFirstSetNozzle1XyReleaseLiftPulses).ToString(CultureInfo.CurrentCulture);
        FirstSetNozzle2PreDropTextBox.Text = (
            _homeSettings.FirstSetNozzle2PreDropPulses
            ?? DefaultFirstSetNozzle2PreDropPulses).ToString(CultureInfo.CurrentCulture);
        FirstSetNozzle2PlacePreDropTextBox.Text = (
            _homeSettings.FirstSetNozzle2PlacePreDropPulses
            ?? DefaultFirstSetNozzle2PlacePreDropPulses).ToString(CultureInfo.CurrentCulture);
        SecondSetNozzle1PreDropTextBox.Text = (
            _homeSettings.SecondSetNozzle1PreDropPulses
            ?? DefaultSecondSetNozzle1PreDropPulses).ToString(CultureInfo.CurrentCulture);
        SecondSetPosition1XTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetPickupPosition1X ?? DefaultSecondSetPickupPosition1X);
        SecondSetPosition1YTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetPickupPosition1Y ?? DefaultSecondSetPickupPosition1Y);
        SecondSetPosition2XTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetPickupPosition2X ?? DefaultSecondSetPickupPosition2X);
        SecondSetPosition2YTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetPickupPosition2Y ?? DefaultSecondSetPickupPosition2Y);
        Bin0PositionXTextBox.Text = FormatPresetCoordinate(_homeSettings.Bin0PositionX);
        Bin0PositionYTextBox.Text = FormatPresetCoordinate(_homeSettings.Bin0PositionY);
        Bin1PositionXTextBox.Text = FormatPresetCoordinate(_homeSettings.Bin1PositionX);
        Bin1PositionYTextBox.Text = FormatPresetCoordinate(_homeSettings.Bin1PositionY);
        Bin2PositionXTextBox.Text = FormatPresetCoordinate(_homeSettings.Bin2PositionX);
        Bin2PositionYTextBox.Text = FormatPresetCoordinate(_homeSettings.Bin2PositionY);
        Bin3PositionXTextBox.Text = FormatPresetCoordinate(_homeSettings.Bin3PositionX);
        Bin3PositionYTextBox.Text = FormatPresetCoordinate(_homeSettings.Bin3PositionY);
        _loadingPresetPositions = false;
    }

    private void LoadProductionAxisParameterEditors()
    {
        _homeSettings.ProductionAxisMotionSettings ??= [];
        foreach (var definition in ProductionAxisDefinitions)
        {
            var settings = _homeSettings.ProductionAxisMotionSettings.GetValueOrDefault(
                               definition.AxisNo)
                           ?? CreateDefaultAxisMotionSettings(definition);
            var editors = _productionAxisMotionEditors[definition.AxisNo];
            editors.RunVelocity.Text = FormatPresetCoordinate(settings.RunVelocity);
            editors.StartVelocity.Text = FormatPresetCoordinate(settings.StartVelocity);
            editors.StopVelocity.Text = FormatPresetCoordinate(settings.StopVelocity);
            editors.AccelerationMilliseconds.Text =
                FormatPresetCoordinate(settings.AccelerationMilliseconds);
            editors.DecelerationMilliseconds.Text =
                FormatPresetCoordinate(settings.DecelerationMilliseconds);
            editors.STimeMilliseconds.Text =
                FormatPresetCoordinate(settings.STimeMilliseconds);
            editors.DecelerationStopMilliseconds.Text =
                FormatPresetCoordinate(settings.DecelerationStopMilliseconds);
        }
    }

    private void LoadTestStationPositionEditors()
    {
        _homeSettings.TestStationSettings ??= [];
        TestStationDwellTextBox.Text = _homeSettings.TestStationDwellMilliseconds
            .ToString(CultureInfo.CurrentCulture);
        TestRetryCountTextBox.Text = _homeSettings.TestRetryCount.ToString(CultureInfo.CurrentCulture);
        SM7110LowerLimitTextBox.Text = _homeSettings.SM7110LowerLimit is { } lower
            ? SM7110Protocol.ToDisplayValue(lower, _homeSettings.SM7110LimitMeasurementMode).ToString("R", CultureInfo.CurrentCulture) : "";
        SM7110UpperLimitTextBox.Text = _homeSettings.SM7110UpperLimit is { } upper
            ? SM7110Protocol.ToDisplayValue(upper, _homeSettings.SM7110LimitMeasurementMode).ToString("R", CultureInfo.CurrentCulture) : "";
        SM7110LimitModeComboBox.SelectedValue = _homeSettings.SM7110LimitMeasurementMode;
        SM7110MaximumTimeTextBox.Text = _homeSettings.SM7110MaximumTestSeconds?.ToString("R", CultureInfo.CurrentCulture) ?? "";
        _updatingTestStationConfiguration = true;
        try
        {
            foreach (var definition in TestStationDefinitions)
            {
                var settings = _homeSettings.TestStationSettings.GetValueOrDefault(
                                   definition.StationNumber)
                               ?? CreateDefaultTestStationSettings(definition.StationNumber);
                var editors = GetTestStationPositionEditors(definition.StationNumber);
                editors.PressPosition.Text = FormatPresetCoordinate(settings.PressPosition);
                editors.WaitPosition.Text = FormatPresetCoordinate(settings.WaitPosition);

                var controls = GetTestStationConfigurationControls(definition.StationNumber);
                var enabled = settings.Enabled ?? definition.StationNumber != 7;
                var instrument = settings.Instrument
                                 ?? GetDefaultTestStationInstrument(definition.StationNumber);
                controls.EnabledToggle.IsChecked = enabled;
                controls.InstrumentComboBox.SelectedIndex = (int)instrument;
            }
        }
        finally
        {
            _updatingTestStationConfiguration = false;
        }

        UpdateTestStationConfigurationDisplay();
    }

    private static TestStationSettings CreateDefaultTestStationSettings(int stationNumber)
    {
        return new TestStationSettings
        {
            PressPosition = DefaultTestStationPressPosition,
            WaitPosition = DefaultTestStationWaitPosition,
            Enabled = stationNumber != 7,
            Instrument = GetDefaultTestStationInstrument(stationNumber)
        };
    }

    private static TestStationInstrument GetDefaultTestStationInstrument(int stationNumber)
    {
        return stationNumber switch
        {
            5 => TestStationInstrument.E4981A,
            6 => TestStationInstrument.SM7110,
            _ => TestStationInstrument.None
        };
    }

    private (ToggleButton EnabledToggle, ComboBox InstrumentComboBox, Border Card,
        TextBlock StatusText, TextBlock BadgeText, TextBlock ResultText)
        GetTestStationConfigurationControls(
        int stationNumber)
    {
        return stationNumber switch
        {
            5 => (Station1EnabledToggle, Station1InstrumentComboBox, Station1Card,
                Station1StatusText, Station1StateBadgeText, Station1ResultText),
            6 => (Station2EnabledToggle, Station2InstrumentComboBox, Station2Card,
                Station2StatusText, Station2StateBadgeText, Station2ResultText),
            7 => (Station3EnabledToggle, Station3InstrumentComboBox, Station3Card,
                Station3StatusText, Station3StateBadgeText, Station3ResultText),
            _ => throw new ArgumentOutOfRangeException(
                nameof(stationNumber),
                stationNumber,
                "测试站编号必须为5、6或7。")
        };
    }

    private int GetStationNumber(DependencyObject control)
    {
        if (ReferenceEquals(control, Station1EnabledToggle) ||
            ReferenceEquals(control, Station1InstrumentComboBox))
        {
            return 5;
        }
        if (ReferenceEquals(control, Station2EnabledToggle) ||
            ReferenceEquals(control, Station2InstrumentComboBox))
        {
            return 6;
        }
        if (ReferenceEquals(control, Station3EnabledToggle) ||
            ReferenceEquals(control, Station3InstrumentComboBox))
        {
            return 7;
        }

        throw new ArgumentException("无法识别测试站配置控件。", nameof(control));
    }

    private static TestStationInstrument GetSelectedTestStationInstrument(ComboBox comboBox)
    {
        return Enum.IsDefined(typeof(TestStationInstrument), comboBox.SelectedIndex)
            ? (TestStationInstrument)comboBox.SelectedIndex
            : TestStationInstrument.None;
    }

    private void TestStationEnabledToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_loadingPresetPositions || _updatingTestStationConfiguration ||
            sender is not ToggleButton toggle)
        {
            return;
        }

        var stationNumber = GetStationNumber(toggle);
        var controls = GetTestStationConfigurationControls(stationNumber);
        _updatingTestStationConfiguration = true;
        try
        {
            if (toggle.IsChecked == true &&
                GetSelectedTestStationInstrument(controls.InstrumentComboBox) ==
                TestStationInstrument.None)
            {
                var availableInstrument = FindAvailableTestStationInstrument(stationNumber);
                if (availableInstrument == TestStationInstrument.None)
                {
                    toggle.IsChecked = false;
                    SetStartProductionStatus(
                        "两个仪表均已分配；请先停用其他测试站或调整仪表分配。",
                        Color.FromRgb(242, 181, 68));
                }
                else
                {
                    controls.InstrumentComboBox.SelectedIndex = (int)availableInstrument;
                }
            }
            else if (toggle.IsChecked != true)
            {
                controls.InstrumentComboBox.SelectedIndex = (int)TestStationInstrument.None;
            }
        }
        finally
        {
            _updatingTestStationConfiguration = false;
        }

        PersistTestStationConfiguration();
    }

    private void TestStationInstrumentComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (_loadingPresetPositions || _updatingTestStationConfiguration ||
            sender is not ComboBox comboBox)
        {
            return;
        }

        var stationNumber = GetStationNumber(comboBox);
        var instrument = GetSelectedTestStationInstrument(comboBox);
        _updatingTestStationConfiguration = true;
        try
        {
            var currentControls = GetTestStationConfigurationControls(stationNumber);
            currentControls.EnabledToggle.IsChecked = instrument != TestStationInstrument.None;
            if (instrument != TestStationInstrument.None)
            {
                foreach (var otherStation in TestStationDefinitions
                             .Select(definition => definition.StationNumber)
                             .Where(number => number != stationNumber))
                {
                    var otherControls = GetTestStationConfigurationControls(otherStation);
                    if (GetSelectedTestStationInstrument(otherControls.InstrumentComboBox) != instrument)
                    {
                        continue;
                    }

                    otherControls.InstrumentComboBox.SelectedIndex =
                        (int)TestStationInstrument.None;
                    otherControls.EnabledToggle.IsChecked = false;
                }
            }
        }
        finally
        {
            _updatingTestStationConfiguration = false;
        }

        PersistTestStationConfiguration();
    }

    private TestStationInstrument FindAvailableTestStationInstrument(int stationNumber)
    {
        var assigned = TestStationDefinitions
            .Select(definition => definition.StationNumber)
            .Where(number => number != stationNumber)
            .Select(number => GetSelectedTestStationInstrument(
                GetTestStationConfigurationControls(number).InstrumentComboBox))
            .ToHashSet();
        return new[] { TestStationInstrument.E4981A, TestStationInstrument.SM7110 }
            .FirstOrDefault(instrument => !assigned.Contains(instrument));
    }

    private void PersistTestStationConfiguration()
    {
        SaveTestStationParametersFromInputs();
        UpdateTestStationConfigurationDisplay();
        UpdateHomeCommandState();
    }

    private void UpdateTestStationConfigurationDisplay()
    {
        var enabledCount = 0;
        foreach (var definition in TestStationDefinitions)
        {
            var controls = GetTestStationConfigurationControls(definition.StationNumber);
            var enabled = controls.EnabledToggle.IsChecked == true;
            var instrument = GetSelectedTestStationInstrument(controls.InstrumentComboBox);
            if (enabled && instrument != TestStationInstrument.None)
            {
                enabledCount++;
            }

            controls.Card.Opacity = enabled ? 1d : 0.58d;
            controls.StatusText.Text = enabled
                ? $"已分配 {FormatTestStationInstrument(instrument)}"
                : "已关闭";
            controls.StatusText.Foreground = new SolidColorBrush(
                enabled ? Color.FromRgb(242, 246, 249) : Color.FromRgb(159, 177, 191));
            controls.BadgeText.Text = enabled ? "待机" : "停用";
            controls.BadgeText.Foreground = new SolidColorBrush(
                enabled ? Color.FromRgb(54, 196, 106) : Color.FromRgb(159, 177, 191));
            controls.ResultText.Text = enabled ? "暂无结果" : "—";
            controls.ResultText.Foreground = new SolidColorBrush(Color.FromRgb(159, 177, 191));
        }

        EnabledStationSummaryText.Text = $"{enabledCount} / 3 站已启用";
        ProductionModeSummaryText.Text = $"{enabledCount}站测试 · 自动摆盘";
        var summaryColor = enabledCount > 0
            ? Color.FromRgb(54, 196, 106)
            : Color.FromRgb(242, 181, 68);
        EnabledStationSummaryText.Foreground = new SolidColorBrush(summaryColor);
        EnabledStationSummaryIndicator.Fill = new SolidColorBrush(summaryColor);
        var sm7110Enabled = IsSM7110TestEnabled();
        BinRoutingSummaryText.Text = sm7110Enabled ? "双站 · NG + BIN1～BIN3" : "单站 · NG + BIN1～BIN3";
        Bin0PositionLabel.Text = "0号盒 · NG（BIN0）";
        Bin1PositionLabel.Text = sm7110Enabled ? "1号盒 · OK · BIN1" : "1号盒 · BIN1";
        Bin2PositionLabel.Text = sm7110Enabled ? "2号盒 · OK · BIN2" : "2号盒 · BIN2";
        Bin3PositionLabel.Text = sm7110Enabled ? "3号盒 · OK · BIN3" : "3号盒 · BIN3";
        UpdateSM7110RangeDisplay();
        RefreshDistributionCharts();
    }

    private bool IsSM7110TestEnabled()
    {
        if (_testStationSettings is { } settings)
            return settings.Values.Any(value => value.Enabled == true && value.Instrument == TestStationInstrument.SM7110);
        return TestStationDefinitions.Any(definition =>
        {
            var controls = GetTestStationConfigurationControls(definition.StationNumber);
            return controls.EnabledToggle.IsChecked == true &&
                   GetSelectedTestStationInstrument(controls.InstrumentComboBox) == TestStationInstrument.SM7110;
        });
    }

    private SM7110AcceptanceRange ReadSM7110AcceptanceRange()
    {
        var lower = ParseFiniteCoordinate(SM7110LowerLimitTextBox.Text, "SM7110合格下限");
        var mode = SM7110LimitModeComboBox.SelectedValue as string;
        if (mode is not ("R" or "A" or "RS" or "RV" or "RL"))
            throw new ArgumentException("请选择SM7110合格区间的单位和测量模式。");
        if (mode == "R")
        {
            var range = new SM7110AcceptanceRange(SM7110Protocol.FromDisplayValue(lower, mode), double.PositiveInfinity, mode)
            {
                MaximumTestSeconds = ParseFiniteCoordinate(SM7110MaximumTimeTextBox.Text, "SM7110最长测试时间（秒）")
            };
            _ = range.ToTimedTestSettings();
            return range;
        }
        var upper = ParseFiniteCoordinate(SM7110UpperLimitTextBox.Text, "SM7110合格上限");
        if (lower > upper)
            throw new ArgumentException("SM7110合格下限不能大于上限。");
        return new SM7110AcceptanceRange(
            SM7110Protocol.FromDisplayValue(lower, mode), SM7110Protocol.FromDisplayValue(upper, mode), mode);
    }

    private SM7110TimedTestSettings ReadSM7110TimedTestSettings() => ReadSM7110AcceptanceRange().ToTimedTestSettings();

    private void SaveSM7110RangeFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
            return;
        try
        {
            var mode = SM7110LimitModeComboBox.SelectedValue as string
                ?? throw new ArgumentException("请选择SM7110合格区间的测量模式。");
            if (!TryParseOptionalCoordinate(SM7110LowerLimitTextBox.Text, out var lower))
                throw new ArgumentException("SM7110门限必须为有限数值。");
            double? upper = null;
            if (mode != "R" && (!TryParseOptionalCoordinate(SM7110UpperLimitTextBox.Text, out upper) || lower > upper))
                throw new ArgumentException("SM7110上下限必须为有限数值，且下限不能大于上限。");
            if (!TryParseOptionalCoordinate(SM7110MaximumTimeTextBox.Text, out var maximumTime) ||
                maximumTime is <= 0 or > 3600 || (mode == "R" && lower is <= 0))
                throw new ArgumentException("SM7110门限必须大于0；最长测试时间须大于0且不超过3600秒。");
            if (throwOnInvalid && IsSM7110TestEnabled())
                _ = ReadSM7110AcceptanceRange();
            var baseLower = lower.HasValue ? SM7110Protocol.FromDisplayValue(lower.Value, mode) : (double?)null;
            var baseUpper = upper.HasValue ? SM7110Protocol.FromDisplayValue(upper.Value, mode) : (double?)null;
            _homeSettings.SM7110LowerLimit = baseLower;
            if (mode != "R") _homeSettings.SM7110UpperLimit = baseUpper;
            _homeSettings.SM7110MaximumTestSeconds = maximumTime;
            _homeSettings.SM7110LimitMeasurementMode = mode;
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SM7110RangeStatusText.Text = exception.Message;
            SM7110RangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(242, 122, 128));
        }
    }

    private void SM7110LimitTextBox_TextChanged(object sender, TextChangedEventArgs e) =>
        SM7110RangeInputChanged();

    private void SM7110LimitModeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        SM7110RangeInputChanged();

    private void SM7110RangeInputChanged()
    {
        if (_loadingPresetPositions)
            return;
        UpdateSM7110RangeDisplay();
        SaveSM7110RangeFromInputs();
        RefreshDistributionCharts();
        UpdateHomeCommandState();
    }

    private void UpdateSM7110RangeDisplay()
    {
        if (SM7110MaximumTimePanel is null) return;
        var threshold = SM7110LimitModeComboBox.SelectedValue as string == "R";
        SM7110UpperLimitPanel.Visibility = threshold ? Visibility.Collapsed : Visibility.Visible;
        SM7110MaximumTimePanel.Visibility = threshold ? Visibility.Visible : Visibility.Collapsed;
        SM7110LowerLimitLabel.Text = threshold ? "达标门限（GΩ）" : "合格下限";
        SM7110CriteriaDescription.Text = threshold
            ? "持续加电并反复采样，电阻≥门限即OK；到最长测试时间仍未达标则NG，上限不参与判定。"
            : "下限 ≤ 测量值 ≤ 上限为OK；超出区间按测试重试次数重测。";
        try
        {
            if (IsSM7110TestEnabled())
            {
                _ = ReadSM7110AcceptanceRange();
                SM7110RangeStatusText.Text = threshold
                    ? "每轮保持加电至达标或超时，再停止并放电；失败按重试次数执行回等待位、下压、稳定等待后复测。"
                    : "双站判定已启用：区间包含上下限；参数自动保存，下次启动生产生效。";
            }
            else
            {
                SM7110RangeStatusText.Text = "SM7110未启用：由E4981A分BIN；电容未落入BIN1～BIN3或启用的损耗限制超限，均判NG进入0号盒。";
            }
            SM7110RangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(159, 177, 191));
        }
        catch (ArgumentException exception)
        {
            SM7110RangeStatusText.Text = $"双站生产前请完成配置：{exception.Message}";
            SM7110RangeStatusText.Foreground = new SolidColorBrush(Color.FromRgb(242, 122, 128));
        }
    }

    private static string FormatTestStationInstrument(TestStationInstrument instrument)
    {
        return instrument switch
        {
            TestStationInstrument.E4981A => "E4981A",
            TestStationInstrument.SM7110 => "SM7110",
            _ => "未分配"
        };
    }

    private void SetTestStationRuntimeDisplay(
        int stationNumber,
        string status,
        string badge,
        string resultText,
        Color color)
    {
        var controls = GetTestStationConfigurationControls(stationNumber);
        controls.StatusText.Text = status;
        controls.StatusText.Foreground = new SolidColorBrush(color);
        controls.BadgeText.Text = badge;
        controls.BadgeText.Foreground = new SolidColorBrush(color);
        controls.ResultText.Text = resultText;
        controls.ResultText.Foreground = new SolidColorBrush(color);
    }

    private (TextBox PressPosition, TextBox WaitPosition) GetTestStationPositionEditors(
        int stationNumber)
    {
        return stationNumber switch
        {
            5 => (TestStation1PressPositionTextBox, TestStation1WaitPositionTextBox),
            6 => (TestStation2PressPositionTextBox, TestStation2WaitPositionTextBox),
            7 => (TestStation3PressPositionTextBox, TestStation3WaitPositionTextBox),
            _ => throw new ArgumentOutOfRangeException(
                nameof(stationNumber),
                stationNumber,
                "测试站编号必须为5、6或7。")
        };
    }

    private ProductionAxisMotionSettings CreateDefaultAxisMotionSettings(
        ProductionAxisDefinition definition)
    {
        var legacyZVelocity =
            FirstSetZAxisNos.Contains(definition.AxisNo) ||
            SecondSetZAxisNos.Contains(definition.AxisNo)
                ? _homeSettings.NozzleZVelocity
                : null;
        return new ProductionAxisMotionSettings
        {
            RunVelocity = legacyZVelocity ?? definition.DefaultRunVelocity,
            StartVelocity = 0,
            StopVelocity = 0,
            AccelerationMilliseconds = 100,
            DecelerationMilliseconds = 100,
            STimeMilliseconds = 0,
            DecelerationStopMilliseconds = 100
        };
    }

    private IReadOnlyDictionary<int, double> ReadProductionZSafePositions() => new Dictionary<int, double>
    {
        [FirstSetNozzle1ZHardwareAxisNo] = ParseFiniteCoordinate(FirstSetNozzle1SafeZPositionTextBox.Text, "第一套吸嘴1安全Z高度"),
        [FirstSetNozzle2ZHardwareAxisNo] = ParseFiniteCoordinate(FirstSetNozzle2SafeZPositionTextBox.Text, "第一套吸嘴2安全Z高度"),
        [SecondSetNozzle1ZHardwareAxisNo] = ParseFiniteCoordinate(SecondSetNozzle1SafeZPositionTextBox.Text, "第二套吸嘴1安全Z高度"),
        [SecondSetNozzle2ZHardwareAxisNo] = ParseFiniteCoordinate(SecondSetNozzle2SafeZPositionTextBox.Text, "第二套吸嘴2安全Z高度")
    };

    private ProductionZPositions ReadProductionZPositions()
    {
        return new ProductionZPositions(
            new NozzleZPositions(
                ParseFiniteCoordinate(FirstSetNozzle1PickupZPositionTextBox.Text, "第一套吸嘴1取料Z高度"),
                ParseFiniteCoordinate(FirstSetNozzle1DropZPositionTextBox.Text, "第一套吸嘴1放料Z高度"),
                ParseFiniteCoordinate(FirstSetNozzle1SafeZPositionTextBox.Text, "第一套吸嘴1安全Z高度")),
            new NozzleZPositions(
                ParseFiniteCoordinate(FirstSetNozzle2PickupZPositionTextBox.Text, "第一套吸嘴2取料Z高度"),
                ParseFiniteCoordinate(FirstSetNozzle2DropZPositionTextBox.Text, "第一套吸嘴2放料Z高度"),
                ParseFiniteCoordinate(FirstSetNozzle2SafeZPositionTextBox.Text, "第一套吸嘴2安全Z高度")),
            new NozzleZPositions(
                ParseFiniteCoordinate(SecondSetNozzle1PickupZPositionTextBox.Text, "第二套吸嘴1取料Z高度"),
                ParseFiniteCoordinate(SecondSetNozzle1DropZPositionTextBox.Text, "第二套吸嘴1放料Z高度"),
                ParseFiniteCoordinate(SecondSetNozzle1SafeZPositionTextBox.Text, "第二套吸嘴1安全Z高度")),
            new NozzleZPositions(
                ParseFiniteCoordinate(SecondSetNozzle2PickupZPositionTextBox.Text, "第二套吸嘴2取料Z高度"),
                ParseFiniteCoordinate(SecondSetNozzle2DropZPositionTextBox.Text, "第二套吸嘴2放料Z高度"),
                ParseFiniteCoordinate(SecondSetNozzle2SafeZPositionTextBox.Text, "第二套吸嘴2安全Z高度")));
    }

    private ProductionZDwellTimes ReadProductionZDwellTimes()
    {
        var releaseLiftPulses = ParseNonNegativeCoordinate(
            FirstSetNozzle1XyReleaseLiftTextBox.Text,
            "第一套吸嘴1 XY放行上升量");
        var nozzle2PreDropPulses = ParseNonNegativeCoordinate(
            FirstSetNozzle2PreDropTextBox.Text,
            "第一套吸嘴2预下降量");
        var nozzle2PlacePreDropPulses = ParseNonNegativeCoordinate(
            FirstSetNozzle2PlacePreDropTextBox.Text,
            "第一套放料吸嘴2预下降量");
        var secondSetNozzle1PreDropPulses = ParseNonNegativeCoordinate(
            SecondSetNozzle1PreDropTextBox.Text,
            "下料吸嘴1预下降量");
        var positions = GetProductionZPositions();
        ValidateFirstSetNozzle1XyReleaseLift(
            positions.FirstSetNozzle1,
            releaseLiftPulses);
        ValidateFirstSetNozzle2PreDrop(
            positions.FirstSetNozzle2,
            nozzle2PreDropPulses);
        ValidateFirstSetNozzle2PlacePreDrop(
            positions.FirstSetNozzle2,
            nozzle2PlacePreDropPulses);
        ValidateSecondSetNozzle1PreDrop(
            positions.SecondSetNozzle1,
            secondSetNozzle1PreDropPulses);
        return new ProductionZDwellTimes(
            ParseMilliseconds(
                VacuumPickupDwellTextBox.Text,
                "吸料停留时间"),
            ParseMilliseconds(
                VacuumBreakPulseTextBox.Text,
                "破真空停留时间"),
            ParseMilliseconds(
                VacuumValveSwitchDelayTextBox.Text,
                "真空阀切换间隔"),
            releaseLiftPulses,
            nozzle2PreDropPulses,
            nozzle2PlacePreDropPulses,
            secondSetNozzle1PreDropPulses);
    }

    private static void ValidateFirstSetNozzle1XyReleaseLift(
        NozzleZPositions positions,
        double releaseLiftPulses)
    {
        if (releaseLiftPulses <= 0)
        {
            return;
        }

        var negativeDirectionTravel = positions.Pickup - positions.Safe;
        if (negativeDirectionTravel <= 0)
        {
            throw new InvalidOperationException(
                "第一套吸嘴1安全Z必须小于取料Z，才能按负方向上升并提前放行XY。");
        }

        if (releaseLiftPulses > negativeDirectionTravel)
        {
            throw new InvalidOperationException(
                $"第一套吸嘴1 XY放行上升量 {releaseLiftPulses:0.###} pulse " +
                $"超过取料位到安全位的负方向行程 {negativeDirectionTravel:0.###} pulse。");
        }
    }

    private static void ValidateFirstSetNozzle2PreDrop(
        NozzleZPositions positions,
        double preDropPulses)
    {
        if (preDropPulses <= 0)
        {
            return;
        }

        var positiveDirectionTravel = positions.Pickup - positions.Safe;
        if (positiveDirectionTravel <= 0)
        {
            throw new InvalidOperationException(
                "第一套吸嘴2取料Z必须大于安全Z，才能按正方向提前下降。");
        }

        if (preDropPulses > positiveDirectionTravel)
        {
            throw new InvalidOperationException(
                $"第一套吸嘴2预下降量 {preDropPulses:0.###} pulse " +
                $"超过安全位到取料位的正方向行程 {positiveDirectionTravel:0.###} pulse。");
        }
    }

    private static void ValidateFirstSetNozzle2PlacePreDrop(
        NozzleZPositions positions,
        double preDropPulses)
    {
        if (preDropPulses <= 0)
        {
            return;
        }

        var positiveDirectionTravel = positions.Drop - positions.Safe;
        if (positiveDirectionTravel <= 0)
        {
            throw new InvalidOperationException(
                "第一套吸嘴2放料Z必须大于安全Z，才能按正方向提前下降。");
        }

        if (preDropPulses > positiveDirectionTravel)
        {
            throw new InvalidOperationException(
                $"第一套放料吸嘴2预下降量 {preDropPulses:0.###} pulse " +
                $"超过安全位到放料位的正方向行程 {positiveDirectionTravel:0.###} pulse。");
        }
    }

    private static void ValidateSecondSetNozzle1PreDrop(
        NozzleZPositions positions,
        double preDropPulses)
    {
        if (preDropPulses <= 0)
        {
            return;
        }

        var positiveDirectionTravel = positions.Pickup - positions.Safe;
        if (positiveDirectionTravel <= 0)
        {
            throw new InvalidOperationException(
                "第二套吸嘴1取料Z必须大于安全Z，才能按正方向提前下降。");
        }

        if (preDropPulses > positiveDirectionTravel)
        {
            throw new InvalidOperationException(
                $"下料吸嘴1预下降量 {preDropPulses:0.###} pulse " +
                $"超过安全位到取料位的正方向行程 {positiveDirectionTravel:0.###} pulse。");
        }
    }

    private SecondSetXyPositions ReadSecondSetXyPositions()
    {
        return new SecondSetXyPositions(
            ParseFiniteCoordinate(SecondSetPosition1XTextBox.Text, "第二套位置1（吸嘴2取13）X轴绝对脉冲"),
            ParseFiniteCoordinate(SecondSetPosition1YTextBox.Text, "第二套位置1（吸嘴2取13）Y轴绝对脉冲"),
            ParseFiniteCoordinate(SecondSetPosition2XTextBox.Text, "第二套位置2（吸嘴1取14）X轴绝对脉冲"),
            ParseFiniteCoordinate(SecondSetPosition2YTextBox.Text, "第二套位置2（吸嘴1取14）Y轴绝对脉冲"));
    }

    private LowerCameraPhotoPositions ReadLowerCameraPhotoPositions()
    {
        return new LowerCameraPhotoPositions(
            ParseFiniteCoordinate(
                LowerCameraPhotoPosition1XTextBox.Text,
                "下相机拍照位1 X轴绝对脉冲"),
            ParseFiniteCoordinate(
                LowerCameraPhotoPosition1YTextBox.Text,
                "下相机拍照位1 Y轴绝对脉冲"),
            ParseFiniteCoordinate(
                LowerCameraPhotoPosition2XTextBox.Text,
                "下相机拍照位2 X轴绝对脉冲"),
            ParseFiniteCoordinate(
                LowerCameraPhotoPosition2YTextBox.Text,
                "下相机拍照位2 Y轴绝对脉冲"));
    }

    private BinDropPositions ReadBinDropPositions()
    {
        return new BinDropPositions(
            new BinDropPosition(
                ParseFiniteCoordinate(Bin0PositionXTextBox.Text, "BIN0位置X轴绝对脉冲"),
                ParseFiniteCoordinate(Bin0PositionYTextBox.Text, "BIN0位置Y轴绝对脉冲")),
            new BinDropPosition(
                ParseFiniteCoordinate(Bin1PositionXTextBox.Text, "BIN1位置X轴绝对脉冲"),
                ParseFiniteCoordinate(Bin1PositionYTextBox.Text, "BIN1位置Y轴绝对脉冲")),
            new BinDropPosition(
                ParseFiniteCoordinate(Bin2PositionXTextBox.Text, "BIN2位置X轴绝对脉冲"),
                ParseFiniteCoordinate(Bin2PositionYTextBox.Text, "BIN2位置Y轴绝对脉冲")),
            new BinDropPosition(
                ParseFiniteCoordinate(Bin3PositionXTextBox.Text, "BIN3位置X轴绝对脉冲"),
                ParseFiniteCoordinate(Bin3PositionYTextBox.Text, "BIN3位置Y轴绝对脉冲")));
    }

    private IReadOnlyDictionary<int, ProductionAxisMotionSettings>
        ReadProductionAxisMotionSettings()
    {
        var settingsByAxis = new Dictionary<int, ProductionAxisMotionSettings>();
        foreach (var definition in ProductionAxisDefinitions)
        {
            settingsByAxis[definition.AxisNo] = ReadProductionAxisMotionSettings(definition);
        }

        return settingsByAxis;
    }

    private ProductionAxisMotionSettings ReadProductionAxisMotionSettings(
        ProductionAxisDefinition definition)
    {
        var editors = _productionAxisMotionEditors[definition.AxisNo];
        var settings = new ProductionAxisMotionSettings
        {
            RunVelocity = ParseProductionVelocity(
                editors.RunVelocity.Text,
                $"轴{definition.AxisNo}运行速度"),
            StartVelocity = ParseNonNegativeCoordinate(
                editors.StartVelocity.Text,
                $"轴{definition.AxisNo}初始速度"),
            StopVelocity = ParseNonNegativeCoordinate(
                editors.StopVelocity.Text,
                $"轴{definition.AxisNo}停止速度"),
            AccelerationMilliseconds = ParseProductionVelocity(
                editors.AccelerationMilliseconds.Text,
                $"轴{definition.AxisNo}加速时间"),
            DecelerationMilliseconds = ParseProductionVelocity(
                editors.DecelerationMilliseconds.Text,
                $"轴{definition.AxisNo}减速时间"),
            STimeMilliseconds = ParseNonNegativeCoordinate(
                editors.STimeMilliseconds.Text,
                $"轴{definition.AxisNo}S曲线时间"),
            DecelerationStopMilliseconds = ParseProductionVelocity(
                editors.DecelerationStopMilliseconds.Text,
                $"轴{definition.AxisNo}减速停止时间")
        };
        ValidateProductionAxisMotionSettings(definition, settings);
        return settings;
    }

    private IReadOnlyDictionary<int, double> ReadDdTestStationWaitPositions()
    {
        return TestStationDefinitions.ToDictionary(definition => definition.AxisNo, definition =>
            _startSequenceRunning && !_oneKeyCollectRunning && _testStationSettings is { } snapshot
                ? snapshot.TryGetValue(definition.StationNumber, out var settings)
                    ? settings.WaitPosition
                    : throw new InvalidOperationException($"{definition.DisplayName}等待位未配置，禁止启动DD马达。")
                : ParseFiniteCoordinate(GetTestStationPositionEditors(definition.StationNumber).WaitPosition.Text,
                    $"{definition.DisplayName}等待位"));
    }

    private IReadOnlyDictionary<int, TestStationSettings> ReadTestStationSettings()
    {
        _ = ReadTestStationDwellMilliseconds();
        _ = ReadTestRetryCount();
        var settingsByStation = new Dictionary<int, TestStationSettings>();
        foreach (var definition in TestStationDefinitions)
        {
            var editors = GetTestStationPositionEditors(definition.StationNumber);
            var controls = GetTestStationConfigurationControls(definition.StationNumber);
            settingsByStation[definition.StationNumber] = new TestStationSettings
            {
                PressPosition = ParseFiniteCoordinate(
                    editors.PressPosition.Text,
                    $"{definition.DisplayName}下压位"),
                WaitPosition = ParseFiniteCoordinate(
                    editors.WaitPosition.Text,
                    $"{definition.DisplayName}等待位"),
                Enabled = controls.EnabledToggle.IsChecked == true,
                Instrument = GetSelectedTestStationInstrument(controls.InstrumentComboBox)
            };
        }

        return settingsByStation;
    }

    private int ReadTestStationDwellMilliseconds() =>
        ParseMilliseconds(TestStationDwellTextBox.Text, "测试站下压稳定等待");

    private int ReadTestRetryCount()
    {
        if (!int.TryParse(TestRetryCountTextBox.Text, NumberStyles.Integer,
                CultureInfo.CurrentCulture, out var count) ||
            count is < 0 or > TestMeasurementRetry.MaximumRetryCount)
        {
            throw new ArgumentException($"测试重试次数必须是0～{TestMeasurementRetry.MaximumRetryCount}之间的整数。");
        }

        return count;
    }

    private TestStationSettings GetTestStationSettings(int stationNumber)
    {
        var current = _testStationSettings ?? ReadTestStationSettings();
        return current.GetValueOrDefault(stationNumber)
               ?? throw new InvalidOperationException($"{stationNumber}号测试站参数不存在。");
    }

    private void EnsureAssignedTestInstrumentsConnected()
    {
        if (IsSM7110TestEnabled())
        {
            var range = _sm7110AcceptanceRange ?? ReadSM7110AcceptanceRange();
            range.ValidateMeasurementMode(_connectionConfigController?.SM7110MeasurementMode ?? "");
        }
        var connectionController = _connectionConfigController
            ?? throw new InvalidOperationException("主页尚未连接仪表控制组件。");
        var testStationSettings = _testStationSettings ?? ReadTestStationSettings();
        ValidateTestStationInstrumentFlow(testStationSettings);
        var enabledInstruments = testStationSettings
            .Values
            .Where(settings => settings.Enabled == true)
            .Select(settings => settings.Instrument ?? TestStationInstrument.None)
            .ToHashSet();
        var disconnected = new List<string>();
        if (enabledInstruments.Contains(TestStationInstrument.E4981A) &&
            !connectionController.IsE4981AConnected)
        {
            disconnected.Add("E4981A");
        }
        if (enabledInstruments.Contains(TestStationInstrument.SM7110) &&
            !connectionController.IsSM7110Connected)
        {
            disconnected.Add("SM7110");
        }

        if (disconnected.Count > 0)
        {
            throw new InvalidOperationException(
                $"已分配仪表尚未连接：{string.Join("、", disconnected)}。请先在连接配置页完成连接。");
        }
    }

    private static void ValidateTestStationInstrumentFlow(
        IReadOnlyDictionary<int, TestStationSettings> settings)
    {
        var enabledStations = settings
            .Where(pair => pair.Value.Enabled == true &&
                           pair.Value.Instrument is not null and not TestStationInstrument.None)
            .ToArray();
        if (enabledStations.Length == 0)
        {
            return;
        }

        var e4981AStation = enabledStations
            .Where(pair => pair.Value.Instrument == TestStationInstrument.E4981A)
            .Select(pair => (int?)pair.Key)
            .SingleOrDefault();
        if (e4981AStation is null)
        {
            throw new InvalidOperationException(
                "分BIN必须由E4981A完成，请启用并分配E4981A测试站。");
        }

        var sm7110Station = enabledStations
            .Where(pair => pair.Value.Instrument == TestStationInstrument.SM7110)
            .Select(pair => (int?)pair.Key)
            .SingleOrDefault();
        if (sm7110Station is not null && sm7110Station <= e4981AStation)
        {
            throw new InvalidOperationException(
                "SM7110必须分配在E4981A之后的下游测试站，确保产品先获得E4981A BIN。");
        }
    }

    private static void ValidateProductionAxisMotionSettings(
        ProductionAxisDefinition definition,
        ProductionAxisMotionSettings settings)
    {
        if (settings.StartVelocity > settings.RunVelocity)
        {
            throw new ArgumentException(
                $"轴{definition.AxisNo}初始速度不能大于运行速度。");
        }

        if (settings.StopVelocity > settings.RunVelocity)
        {
            throw new ArgumentException(
                $"轴{definition.AxisNo}停止速度不能大于运行速度。");
        }

        if (settings.STimeMilliseconds > 1000)
        {
            throw new ArgumentException(
                $"轴{definition.AxisNo}S曲线时间必须在0–1000 ms之间。");
        }
    }

    private bool AllProductionAxisParametersValid()
    {
        try
        {
            _ = ReadProductionAxisMotionSettings();
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private bool ProductionAxisParametersValid(int axisNo)
    {
        try
        {
            var definition = ProductionAxisDefinitions.Single(item => item.AxisNo == axisNo);
            _ = ReadProductionAxisMotionSettings(definition);
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private bool AllTestStationParametersValid()
    {
        try
        {
            var settings = ReadTestStationSettings();
            var enabledStations = settings.Values
                .Where(value => value.Enabled == true)
                .ToArray();
            if (enabledStations.Any(value =>
                    value.Instrument is null or TestStationInstrument.None))
            {
                return false;
            }

            var assignedInstruments = enabledStations
                .Select(value => value.Instrument)
                .ToArray();
            if (assignedInstruments.Distinct().Count() != assignedInstruments.Length)
            {
                return false;
            }

            ValidateTestStationInstrumentFlow(settings);
            if (settings.Values.Any(value => value.Enabled == true && value.Instrument == TestStationInstrument.SM7110))
                _ = ReadSM7110AcceptanceRange();
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void SaveTestStationParametersFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        try
        {
            var stationSettings = ReadTestStationSettings();
            _homeSettings.TestStationDwellMilliseconds = ReadTestStationDwellMilliseconds();
            _homeSettings.TestRetryCount = ReadTestRetryCount();
            _homeSettings.TestStationSettings = stationSettings.ToDictionary(
                pair => pair.Key,
                pair => pair.Value);
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (ArgumentException) when (!throwOnInvalid)
        {
            // 输入尚未完成时等待用户继续编辑。
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetFirstSetPositionStatus($"保存测试站位置失败：{exception.Message}", false);
        }
    }

    private void SaveProductionAxisParametersFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        try
        {
            _homeSettings.ProductionAxisMotionSettings =
                ReadProductionAxisMotionSettings().ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value);
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (ArgumentException) when (!throwOnInvalid)
        {
            // 输入尚未完成时只保持按钮禁用，等待用户继续编辑。
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetFirstSetPositionStatus($"保存生产轴参数失败：{exception.Message}", false);
        }
    }

    private void SaveVisionPickupCountFromInput(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions || VisionPickupCountTextBox is null)
        {
            return;
        }

        if (!TryParseVisionPickupCount(VisionPickupCountTextBox.Text, out var pickupCount))
        {
            // Typing may temporarily leave an invalid draft, but an explicit recipe save must reject it.
            if (throwOnInvalid)
            {
                _ = ParseVisionPickupCount(VisionPickupCountTextBox.Text, "抓取颗数");
            }
            return;
        }

        _homeSettings.VisionPickupCount = pickupCount;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetFirstSetPositionStatus($"保存视觉抓取颗数失败：{exception.Message}", false);
        }
    }

    private void ApplyProductionAxisMotionSettings(
        MotionControlPage motionController,
        IReadOnlyDictionary<int, ProductionAxisMotionSettings> settingsByAxis)
    {
        foreach (var (axisNo, settings) in settingsByAxis)
        {
            motionController.ConfigureAxisMoveParameters(
                axisNo,
                settings.StartVelocity,
                settings.StopVelocity,
                settings.AccelerationMilliseconds / 1000d,
                settings.DecelerationMilliseconds / 1000d,
                settings.STimeMilliseconds / 1000d,
                settings.DecelerationStopMilliseconds / 1000d);
        }
    }

    private void ApplyCurrentProductionAxisMotionSettings(
        MotionControlPage motionController)
    {
        ApplyProductionAxisMotionSettings(
            motionController,
            _productionAxisMotionSettings ?? ReadProductionAxisMotionSettings());
    }

    private ProductionAxisMotionSettings GetProductionAxisMotionSettings(int axisNo)
    {
        if (_productionAxisMotionSettings?.GetValueOrDefault(axisNo) is { } locked)
        {
            return locked;
        }

        var current = ReadProductionAxisMotionSettings();
        return current.GetValueOrDefault(axisNo)
               ?? throw new InvalidOperationException($"轴{axisNo}生产运动参数不存在。");
    }

    private IReadOnlyDictionary<int, double> GetProductionAxisVelocities(
        IEnumerable<int> axisNos)
    {
        return axisNos
            .Distinct()
            .ToDictionary(
                axisNo => axisNo,
                axisNo => GetProductionAxisMotionSettings(axisNo).RunVelocity);
    }

    private void SaveProductionZPositionsFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        if (FirstSetNozzle1PickupZPositionTextBox is null ||
            FirstSetNozzle1DropZPositionTextBox is null ||
            FirstSetNozzle1SafeZPositionTextBox is null ||
            FirstSetNozzle2PickupZPositionTextBox is null ||
            FirstSetNozzle2DropZPositionTextBox is null ||
            FirstSetNozzle2SafeZPositionTextBox is null ||
            SecondSetNozzle1PickupZPositionTextBox is null ||
            SecondSetNozzle1DropZPositionTextBox is null ||
            SecondSetNozzle1SafeZPositionTextBox is null ||
            SecondSetNozzle2PickupZPositionTextBox is null ||
            SecondSetNozzle2DropZPositionTextBox is null ||
            SecondSetNozzle2SafeZPositionTextBox is null ||
            VacuumPickupDwellTextBox is null ||
            VacuumBreakPulseTextBox is null ||
            VacuumValveSwitchDelayTextBox is null ||
            FirstSetNozzle1XyReleaseLiftTextBox is null ||
            FirstSetNozzle2PreDropTextBox is null ||
            FirstSetNozzle2PlacePreDropTextBox is null ||
            SecondSetNozzle1PreDropTextBox is null ||
            !TryParseCoordinate(FirstSetNozzle1PickupZPositionTextBox.Text, out var firstSetNozzle1Pickup) ||
            !TryParseCoordinate(FirstSetNozzle1DropZPositionTextBox.Text, out var firstSetNozzle1Drop) ||
            !TryParseCoordinate(FirstSetNozzle1SafeZPositionTextBox.Text, out var firstSetNozzle1Safe) ||
            !TryParseCoordinate(FirstSetNozzle2PickupZPositionTextBox.Text, out var firstSetNozzle2Pickup) ||
            !TryParseCoordinate(FirstSetNozzle2DropZPositionTextBox.Text, out var firstSetNozzle2Drop) ||
            !TryParseCoordinate(FirstSetNozzle2SafeZPositionTextBox.Text, out var firstSetNozzle2Safe) ||
            !TryParseCoordinate(SecondSetNozzle1PickupZPositionTextBox.Text, out var secondSetNozzle1Pickup) ||
            !TryParseCoordinate(SecondSetNozzle1DropZPositionTextBox.Text, out var secondSetNozzle1Drop) ||
            !TryParseCoordinate(SecondSetNozzle1SafeZPositionTextBox.Text, out var secondSetNozzle1Safe) ||
            !TryParseCoordinate(SecondSetNozzle2PickupZPositionTextBox.Text, out var secondSetNozzle2Pickup) ||
            !TryParseCoordinate(SecondSetNozzle2DropZPositionTextBox.Text, out var secondSetNozzle2Drop) ||
            !TryParseCoordinate(SecondSetNozzle2SafeZPositionTextBox.Text, out var secondSetNozzle2Safe) ||
            !TryParseMilliseconds(VacuumPickupDwellTextBox.Text, out var pickupDwell) ||
            !TryParseMilliseconds(VacuumBreakPulseTextBox.Text, out var breakPulse) ||
            !TryParseMilliseconds(VacuumValveSwitchDelayTextBox.Text, out var valveSwitchDelay) ||
            !TryParseCoordinate(FirstSetNozzle1XyReleaseLiftTextBox.Text, out var releaseLiftPulses) ||
            releaseLiftPulses < 0 ||
            !TryParseCoordinate(FirstSetNozzle2PreDropTextBox.Text, out var nozzle2PreDropPulses) ||
            nozzle2PreDropPulses < 0 ||
            (nozzle2PreDropPulses > 0 &&
             (firstSetNozzle2Pickup <= firstSetNozzle2Safe ||
              nozzle2PreDropPulses > firstSetNozzle2Pickup - firstSetNozzle2Safe)) ||
            !TryParseCoordinate(
                FirstSetNozzle2PlacePreDropTextBox.Text,
                out var nozzle2PlacePreDropPulses) ||
            nozzle2PlacePreDropPulses < 0 ||
            (nozzle2PlacePreDropPulses > 0 &&
             (firstSetNozzle2Drop <= firstSetNozzle2Safe ||
              nozzle2PlacePreDropPulses > firstSetNozzle2Drop - firstSetNozzle2Safe)) ||
            !TryParseCoordinate(
                SecondSetNozzle1PreDropTextBox.Text,
                out var secondSetNozzle1PreDropPulses) ||
            secondSetNozzle1PreDropPulses < 0 ||
            (secondSetNozzle1PreDropPulses > 0 &&
             (secondSetNozzle1Pickup <= secondSetNozzle1Safe ||
              secondSetNozzle1PreDropPulses > secondSetNozzle1Pickup - secondSetNozzle1Safe)))
        {
            if (throwOnInvalid)
            {
                throw new ArgumentException("Z轴高度、真空时间或预下降参数无效：高度须为有限数值，时间须为0–60000整数毫秒，提前量须非负且预下降量不能超过对应行程。");
            }
            return;
        }

        _homeSettings.FirstSetNozzle1PickupZPosition = firstSetNozzle1Pickup;
        _homeSettings.FirstSetNozzle1DropZPosition = firstSetNozzle1Drop;
        _homeSettings.FirstSetNozzle1SafeZPosition = firstSetNozzle1Safe;
        _homeSettings.FirstSetNozzle2PickupZPosition = firstSetNozzle2Pickup;
        _homeSettings.FirstSetNozzle2DropZPosition = firstSetNozzle2Drop;
        _homeSettings.FirstSetNozzle2SafeZPosition = firstSetNozzle2Safe;
        _homeSettings.SecondSetNozzle1PickupZPosition = secondSetNozzle1Pickup;
        _homeSettings.SecondSetNozzle1DropZPosition = secondSetNozzle1Drop;
        _homeSettings.SecondSetNozzle1SafeZPosition = secondSetNozzle1Safe;
        _homeSettings.SecondSetNozzle2PickupZPosition = secondSetNozzle2Pickup;
        _homeSettings.SecondSetNozzle2DropZPosition = secondSetNozzle2Drop;
        _homeSettings.SecondSetNozzle2SafeZPosition = secondSetNozzle2Safe;
        _homeSettings.VacuumPickupDwellMilliseconds = pickupDwell;
        _homeSettings.VacuumBreakPulseMilliseconds = breakPulse;
        _homeSettings.VacuumValveSwitchDelayMilliseconds = valveSwitchDelay;
        _homeSettings.FirstSetNozzle1XyReleaseLiftPulses = releaseLiftPulses;
        _homeSettings.FirstSetNozzle2PreDropPulses = nozzle2PreDropPulses;
        _homeSettings.FirstSetNozzle2PlacePreDropPulses = nozzle2PlacePreDropPulses;
        _homeSettings.SecondSetNozzle1PreDropPulses = secondSetNozzle1PreDropPulses;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetFirstSetPositionStatus($"保存Z轴高度或停留时间失败：{exception.Message}", false);
        }
    }

    private void SaveSecondSetXyPositionsFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        if (SecondSetPosition1XTextBox is null ||
            SecondSetPosition1YTextBox is null ||
            SecondSetPosition2XTextBox is null ||
            SecondSetPosition2YTextBox is null ||
            !TryParseCoordinate(SecondSetPosition1XTextBox.Text, out var position1X) ||
            !TryParseCoordinate(SecondSetPosition1YTextBox.Text, out var position1Y) ||
            !TryParseCoordinate(SecondSetPosition2XTextBox.Text, out var position2X) ||
            !TryParseCoordinate(SecondSetPosition2YTextBox.Text, out var position2Y))
        {
            if (throwOnInvalid)
            {
                throw new ArgumentException("第二套XY下料位置未保存：位置1、位置2的X/Y必须为有限数值。");
            }
            return;
        }

        _homeSettings.SecondSetPickupPosition1X = position1X;
        _homeSettings.SecondSetPickupPosition1Y = position1Y;
        _homeSettings.SecondSetPickupPosition2X = position2X;
        _homeSettings.SecondSetPickupPosition2Y = position2Y;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetFirstSetPositionStatus($"保存第二套XY下料位置失败：{exception.Message}", false);
        }
    }

    private void SaveBinPositionsFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        if (Bin0PositionXTextBox is null ||
            Bin0PositionYTextBox is null ||
            Bin1PositionXTextBox is null ||
            Bin1PositionYTextBox is null ||
            Bin2PositionXTextBox is null ||
            Bin2PositionYTextBox is null ||
            Bin3PositionXTextBox is null ||
            Bin3PositionYTextBox is null ||
            !TryParseOptionalCoordinate(Bin0PositionXTextBox.Text, out var bin0PositionX) ||
            !TryParseOptionalCoordinate(Bin0PositionYTextBox.Text, out var bin0PositionY) ||
            !TryParseOptionalCoordinate(Bin1PositionXTextBox.Text, out var bin1PositionX) ||
            !TryParseOptionalCoordinate(Bin1PositionYTextBox.Text, out var bin1PositionY) ||
            !TryParseOptionalCoordinate(Bin2PositionXTextBox.Text, out var bin2PositionX) ||
            !TryParseOptionalCoordinate(Bin2PositionYTextBox.Text, out var bin2PositionY) ||
            !TryParseOptionalCoordinate(Bin3PositionXTextBox.Text, out var bin3PositionX) ||
            !TryParseOptionalCoordinate(Bin3PositionYTextBox.Text, out var bin3PositionY))
        {
            if (throwOnInvalid)
            {
                throw new ArgumentException("BIN分料位置未保存：各料盒X/Y须为有限数值，未配置的位置可留空。");
            }
            return;
        }

        _homeSettings.Bin0PositionX = bin0PositionX;
        _homeSettings.Bin0PositionY = bin0PositionY;
        _homeSettings.Bin1PositionX = bin1PositionX;
        _homeSettings.Bin1PositionY = bin1PositionY;
        _homeSettings.Bin2PositionX = bin2PositionX;
        _homeSettings.Bin2PositionY = bin2PositionY;
        _homeSettings.Bin3PositionX = bin3PositionX;
        _homeSettings.Bin3PositionY = bin3PositionY;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetFirstSetPositionStatus($"保存BIN分料位置失败：{exception.Message}", false);
        }
    }

    private void SavePresetPositionsFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        if (PresetPosition1XTextBox is null ||
            PresetPosition1YTextBox is null ||
            PresetPosition2XTextBox is null ||
            PresetPosition2YTextBox is null ||
            !TryParseOptionalCoordinate(PresetPosition1XTextBox.Text, out var position1X) ||
            !TryParseOptionalCoordinate(PresetPosition1YTextBox.Text, out var position1Y) ||
            !TryParseOptionalCoordinate(PresetPosition2XTextBox.Text, out var position2X) ||
            !TryParseOptionalCoordinate(PresetPosition2YTextBox.Text, out var position2Y))
        {
            if (throwOnInvalid)
            {
                throw new ArgumentException("绝对位置未保存：位置1、位置2的X/Y须为有限数值，未配置的位置可留空。");
            }
            return;
        }

        _homeSettings.PresetPosition1X = position1X;
        _homeSettings.PresetPosition1Y = position1Y;
        _homeSettings.PresetPosition2X = position2X;
        _homeSettings.PresetPosition2Y = position2Y;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetFirstSetPositionStatus($"保存绝对位置失败：{exception.Message}", false);
        }
    }

    private void SaveFirstSetTeachingPositionsFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        if (FirstSetTeachingCenterXTextBox is null ||
            FirstSetTeachingCenterYTextBox is null ||
            FirstSetTeachingPressPositionXTextBox is null ||
            FirstSetTeachingPressPositionYTextBox is null ||
            !TryParseOptionalCoordinate(
                FirstSetTeachingCenterXTextBox.Text,
                out var centerX) ||
            !TryParseOptionalCoordinate(
                FirstSetTeachingCenterYTextBox.Text,
                out var centerY) ||
            !TryParseOptionalCoordinate(
                FirstSetTeachingPressPositionXTextBox.Text,
                out var pressPositionX) ||
            !TryParseOptionalCoordinate(
                FirstSetTeachingPressPositionYTextBox.Text,
                out var pressPositionY))
        {
            if (throwOnInvalid)
            {
                throw new ArgumentException("第一套XY示教位置未保存：中心及下压位置的X/Y须为有限数值，未配置的位置可留空。");
            }
            return;
        }

        _homeSettings.FirstSetTeachingCenterX = centerX;
        _homeSettings.FirstSetTeachingCenterY = centerY;
        _homeSettings.FirstSetTeachingPressPositionX = pressPositionX;
        _homeSettings.FirstSetTeachingPressPositionY = pressPositionY;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetFirstSetTeachingPositionStatus($"保存第一套XY示教位置失败：{exception.Message}", false);
        }
    }

    private void HomeEmergencyStop_Click(object sender, RoutedEventArgs e)
    {
        var motionController = _motionController;
        if (motionController is null)
        {
            HomeEmergencyStopHintText.Text = "运动控制未连接";
            return;
        }

        // 必须先冻结IO再向控制卡下发急停。原先依赖控制器急停完成后的事件通知，
        // 取消回调可能抢先进入finally并改写真空输出，形成急停瞬间IO变化的竞态。
        _preserveIoOnEmergencyStop = true;
        _ = RequestProductionStop();
        var issued = motionController.EmergencyStopAllAxes("主页操作员请求全轴急停");

        HomeEmergencyStopHintText.Text = issued
            ? "急停已下发，正在确认所有轴停止"
            : "急停下发失败，请立即按硬件急停";
        HomeEmergencyStopHintText.Foreground = new SolidColorBrush(
            issued ? Color.FromRgb(255, 220, 220) : Color.FromRgb(255, 188, 93));
    }

    private async void OneKeyCollect_Click(object sender, RoutedEventArgs e)
    {
        if (_oneKeyCollectRunning)
        {
            _ = RequestProductionStop();
            return;
        }

        if (_startSequenceRunning ||
            _oneKeyResetRunning ||
            _presetPositionMoveRunning)
        {
            return;
        }

        try
        {
            (_motionController ?? throw new InvalidOperationException("主页尚未连接运动控制组件。"))
                .EnsureDdTestStationsSafe();
        }
        catch (Exception exception)
        {
            SetOneKeyCollectStatus($"一键收料未启动：{exception.Message}", Color.FromRgb(242, 122, 128));
            return;
        }

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            "一键收料只转动DD：每次转一个工位，停稳后执行与生产流程完全相同的" +
            "Y24上方吸/Y25下方喷IO和相同时长，共转16次。\n\n" +
            "第二套下料XY/Z/R、第一套XY/Z/R和测试轴均不移动。\n" +
            "请确认DD转盘可以安全转动。",
            "一键收料安全确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var ddAxisDefinition = ProductionAxisDefinitions.Single(definition => definition.AxisNo == 0);
            _productionAxisMotionSettings = new Dictionary<int, ProductionAxisMotionSettings>
            {
                [0] = ReadProductionAxisMotionSettings(ddAxisDefinition)
            };
            ApplyProductionAxisMotionSettings(motionController, _productionAxisMotionSettings);

            _productionCancellation = new CancellationTokenSource();
            _productionCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _preserveIoOnEmergencyStop = false;
            _productionStopRequested = false;
            _productionPauseRequested = false;
            _productionResumeSignal = null;
            _oneKeyCollectRunning = true;
            _startSequenceRunning = true;
            UpdateHomeCommandState();

            await RunOneKeyCollectCoreAsync(
                _productionCancellation.Token,
                SetOneKeyCollectStatus,
                "一键收料");

            SetOneKeyCollectStatus(
                "一键收料完成：DD已转满一圈，每次停稳后均已执行生产流程Y24/Y25 IO；下料XY全程未动。",
                Color.FromRgb(73, 209, 125));
        }
        catch (OperationCanceledException)
        {
            SetOneKeyCollectStatus("一键收料已停止。", Color.FromRgb(242, 181, 68));
        }
        catch (Exception exception)
        {
            SetOneKeyCollectStatus(
                $"一键收料失败：{exception.Message}",
                Color.FromRgb(242, 122, 128));
        }
        finally
        {
            _productionCancellation?.Cancel();
            _productionResumeSignal?.TrySetResult(true);
            CloseCarouselVacuumSprayOutputsNoThrow();
            _productionAxisMotionSettings = null;
            _productionStopRequested = false;
            _productionPauseRequested = false;
            _productionResumeSignal = null;
            _oneKeyCollectRunning = false;
            _startSequenceRunning = false;

            _productionCancellation?.Dispose();
            _productionCancellation = null;
            var productionCompletion = _productionCompletion;
            _productionCompletion = null;
            UpdateHomeCommandState();
            productionCompletion?.TrySetResult(true);
        }
    }

    private async Task RunOneKeyCollectCoreAsync(
        CancellationToken cancellationToken,
        Action<string, Color> setStatus,
        string statusPrefix)
    {
        ArgumentNullException.ThrowIfNull(setStatus);
        for (var turn = 1; turn <= CarouselStationCount; turn++)
        {
            await WaitIfProductionPausedAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            setStatus(
                $"{statusPrefix} {turn}/{CarouselStationCount}：DD正在转动一个工位…",
                Color.FromRgb(242, 181, 68));
            _ = await MoveAxis0RelativeCoreAsync(
                DdMotorPulsePerTurn,
                cancellationToken);
            AdvanceCarouselOccupancy(_carouselStations);
            UpdateCarouselStationDisplay(_carouselStations);

            setStatus(
                $"{statusPrefix} {turn}/{CarouselStationCount}：DD已停稳，" +
                $"正在执行流程Y24/Y25 IO {CarouselVacuumSprayPulseMilliseconds} ms…",
                Color.FromRgb(242, 181, 68));
            await PulseCarouselVacuumAndSprayAsync(cancellationToken);
        }
    }

    private void CloseCarouselVacuumSprayOutputsNoThrow()
    {
        if (_preserveIoOnEmergencyStop || _motionController is not { } motionController)
        {
            return;
        }

        try
        {
            _ = motionController.SetDigitalOutputHardwareBit(
                CarouselUpperVacuumOutputBit,
                enabled: true);
            _ = motionController.SetDigitalOutputHardwareBit(
                CarouselLowerSprayOutputBit,
                enabled: true);
        }
        catch
        {
            // 收尾不覆盖原始IO异常；正常节拍已在Pulse的finally中关闭输出。
        }
    }

    private async void OneKeyReset_Click(object sender, RoutedEventArgs e)
    {
        if (_oneKeyResetRunning ||
            _startSequenceRunning ||
            _presetPositionMoveRunning)
        {
            return;
        }

        var confirmation = MessageBox.Show(
            Window.GetWindow(this),
            "请确认所有机构都在安全区域，并且本次复位涉及的硬件轴已使能。\n\n" +
            "复位顺序：R/Z同时 → 上料X → 上料Y、下料XY、三个测试站同时 → DD马达。",
            "一键复位安全确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        var loadingAxisConfirmation = MessageBox.Show(
            Window.GetWindow(this),
            "请确认上料上轴在中间。",
            "一键复位二级确认",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning,
            MessageBoxResult.Cancel);
        if (loadingAxisConfirmation != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");

            _oneKeyResetRunning = true;
            SetOneKeyResetStatus("正在执行：R/Z → 上料X → Y/下料XY/测试站同时 → DD…", Color.FromRgb(242, 181, 68));
            UpdateHomeCommandState();
            await motionController.RunOneKeyResetAsync(CancellationToken.None);
            SetOneKeyResetStatus("一键复位完成", Color.FromRgb(73, 209, 125));
        }
        catch (OperationCanceledException)
        {
            SetOneKeyResetStatus("一键复位已取消", Color.FromRgb(242, 181, 68));
        }
        catch (Exception exception)
        {
            SetOneKeyResetStatus($"复位失败：{exception.Message}", Color.FromRgb(242, 122, 128));
        }
        finally
        {
            _oneKeyResetRunning = false;
            UpdateHomeCommandState();
        }
    }

    private VisionCalibrationSnapshot EnsureFirstSetToolsReady()
    {
        var snapshot = GetFirstSetCalibrationSnapshot();
        if (!snapshot.Nozzle1Calibrated || !snapshot.Nozzle2Calibrated)
        {
            throw new InvalidOperationException("第一套 XY 的两个吸嘴偏移尚未全部标定。");
        }

        return snapshot;
    }

    private void UpdateHomeCommandState()
    {
        if (StartProductionButton is null ||
            StartProductionTitleText is null ||
            OneKeyResetButton is null ||
            OneKeyResetHintText is null ||
            OneKeyCollectButton is null ||
            OneKeyCollectTitleText is null ||
            OneKeyCollectHintText is null ||
            FirstSetTeachingCenterXTextBox is null ||
            FirstSetTeachingCenterYTextBox is null ||
            FirstSetTeachingPressPositionXTextBox is null ||
            FirstSetTeachingPressPositionYTextBox is null ||
            RecordFirstSetTeachingCenterButton is null ||
            MoveFirstSetTeachingCenterButton is null ||
            RecordFirstSetTeachingPressPositionButton is null ||
            MoveFirstSetTeachingPressPositionButton is null ||
            PresetPosition1XTextBox is null ||
            PresetPosition1YTextBox is null ||
            PresetPosition2XTextBox is null ||
            PresetPosition2YTextBox is null ||
            XyLinearInterpolationCheckBox is null ||
            UpperCameraCorrectionCheckBox is null ||
            LowerCameraCorrection1CheckBox is null ||
            LowerCameraRotationDelayCheckBox is null ||
            LowerCameraPhotoPosition1XTextBox is null ||
            LowerCameraPhotoPosition1YTextBox is null ||
            LowerCameraPhotoPosition2XTextBox is null ||
            LowerCameraPhotoPosition2YTextBox is null ||
            LowerCameraNozzle1RotationCenterXTextBox is null ||
            LowerCameraNozzle1RotationCenterYTextBox is null ||
            LowerCameraNozzle2RotationCenterXTextBox is null ||
            LowerCameraNozzle2RotationCenterYTextBox is null ||
            VisionPickupCountTextBox is null ||
            RecordLowerCameraPhotoPosition1Button is null ||
            RecordLowerCameraPhotoPosition2Button is null ||
            MoveLowerCameraPhotoPosition1Button is null ||
            MoveLowerCameraPhotoPosition2Button is null ||
            FirstSetNozzle1PickupZPositionTextBox is null ||
            FirstSetNozzle1DropZPositionTextBox is null ||
            FirstSetNozzle1SafeZPositionTextBox is null ||
            FirstSetNozzle2PickupZPositionTextBox is null ||
            FirstSetNozzle2DropZPositionTextBox is null ||
            FirstSetNozzle2SafeZPositionTextBox is null ||
            SecondSetNozzle1PickupZPositionTextBox is null ||
            SecondSetNozzle1DropZPositionTextBox is null ||
            SecondSetNozzle1SafeZPositionTextBox is null ||
            SecondSetNozzle2PickupZPositionTextBox is null ||
            SecondSetNozzle2DropZPositionTextBox is null ||
            SecondSetNozzle2SafeZPositionTextBox is null ||
             VacuumPickupDwellTextBox is null ||
             VacuumBreakPulseTextBox is null ||
             VacuumValveSwitchDelayTextBox is null ||
             FirstSetNozzle1XyReleaseLiftTextBox is null ||
             FirstSetNozzle2PreDropTextBox is null ||
             FirstSetNozzle2PlacePreDropTextBox is null ||
             SecondSetNozzle1PreDropTextBox is null ||
             SecondSetPosition1XTextBox is null ||
             SecondSetPosition1YTextBox is null ||
             SecondSetPosition2XTextBox is null ||
             SecondSetPosition2YTextBox is null ||
             Bin0PositionXTextBox is null ||
             Bin0PositionYTextBox is null ||
             Bin1PositionXTextBox is null ||
             Bin1PositionYTextBox is null ||
             Bin2PositionXTextBox is null ||
             Bin2PositionYTextBox is null ||
             Bin3PositionXTextBox is null ||
             Bin3PositionYTextBox is null ||
             RecordSecondSetPosition1Button is null ||
             RecordSecondSetPosition2Button is null ||
             MoveSecondSetPosition1Button is null ||
             MoveSecondSetPosition2Button is null ||
             RecordPresetPosition1Button is null ||
            RecordPresetPosition2Button is null ||
            MovePresetPosition1Button is null ||
            MovePresetPosition2Button is null ||
            HomeEmergencyStopButton is null)
        {
            return;
        }

        var visionControllersReady =
            _motionController is not null &&
            _visualCalibrationController is not null;
        var commandsIdle =
            !_oneKeyResetRunning &&
            !_presetPositionMoveRunning &&
            !_startSequenceRunning;
        UpdateObservationCompensationCommandState(commandsIdle && !_oneKeyCollectRunning);
        var allProductionAxisParametersValid = AllProductionAxisParametersValid();
        var ddAxisParametersValid = ProductionAxisParametersValid(0);
        var allTestStationParametersValid = AllTestStationParametersValid();
        var visionPickupCountValid =
            TryParseVisionPickupCount(VisionPickupCountTextBox.Text, out _);
        var allZPositionsValid =
            TryParseCoordinate(FirstSetNozzle1PickupZPositionTextBox.Text, out var firstSetNozzle1Pickup) &&
            TryParseCoordinate(FirstSetNozzle1DropZPositionTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetNozzle1SafeZPositionTextBox.Text, out var firstSetNozzle1Safe) &&
            TryParseCoordinate(FirstSetNozzle2PickupZPositionTextBox.Text, out var firstSetNozzle2Pickup) &&
            TryParseCoordinate(FirstSetNozzle2DropZPositionTextBox.Text, out var firstSetNozzle2Drop) &&
            TryParseCoordinate(FirstSetNozzle2SafeZPositionTextBox.Text, out var firstSetNozzle2Safe) &&
            TryParseCoordinate(SecondSetNozzle1PickupZPositionTextBox.Text, out var secondSetNozzle1Pickup) &&
            TryParseCoordinate(SecondSetNozzle1DropZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetNozzle1SafeZPositionTextBox.Text, out var secondSetNozzle1Safe) &&
            TryParseCoordinate(SecondSetNozzle2PickupZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetNozzle2DropZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetNozzle2SafeZPositionTextBox.Text, out _) &&
            TryParseMilliseconds(VacuumPickupDwellTextBox.Text, out _) &&
            TryParseMilliseconds(VacuumBreakPulseTextBox.Text, out _) &&
            TryParseMilliseconds(VacuumValveSwitchDelayTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetNozzle1XyReleaseLiftTextBox.Text, out var releaseLiftPulses) &&
            releaseLiftPulses >= 0 &&
            (releaseLiftPulses == 0 ||
             (firstSetNozzle1Pickup > firstSetNozzle1Safe &&
              releaseLiftPulses <= firstSetNozzle1Pickup - firstSetNozzle1Safe)) &&
            TryParseCoordinate(FirstSetNozzle2PreDropTextBox.Text, out var nozzle2PreDropPulses) &&
            nozzle2PreDropPulses >= 0 &&
            (nozzle2PreDropPulses == 0 ||
             (firstSetNozzle2Pickup > firstSetNozzle2Safe &&
              nozzle2PreDropPulses <= firstSetNozzle2Pickup - firstSetNozzle2Safe)) &&
            TryParseCoordinate(
                FirstSetNozzle2PlacePreDropTextBox.Text,
                out var nozzle2PlacePreDropPulses) &&
            nozzle2PlacePreDropPulses >= 0 &&
            (nozzle2PlacePreDropPulses == 0 ||
             (firstSetNozzle2Drop > firstSetNozzle2Safe &&
              nozzle2PlacePreDropPulses <= firstSetNozzle2Drop - firstSetNozzle2Safe)) &&
            TryParseCoordinate(
                SecondSetNozzle1PreDropTextBox.Text,
                out var secondSetNozzle1PreDropPulses) &&
            secondSetNozzle1PreDropPulses >= 0 &&
            (secondSetNozzle1PreDropPulses == 0 ||
             (secondSetNozzle1Pickup > secondSetNozzle1Safe &&
              secondSetNozzle1PreDropPulses <= secondSetNozzle1Pickup - secondSetNozzle1Safe));
        var allSecondSetXyPositionsValid =
            TryParseCoordinate(SecondSetPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition1YTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition2XTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition2YTextBox.Text, out _);
        var lowerCameraCorrectionEnabled = LowerCameraCorrection1CheckBox.IsChecked == true;
        var allLowerCameraPhotoPositionsValid =
            !lowerCameraCorrectionEnabled ||
            (TryParseCoordinate(LowerCameraPhotoPosition1XTextBox.Text, out _) &&
             TryParseCoordinate(LowerCameraPhotoPosition1YTextBox.Text, out _) &&
             TryParseCoordinate(LowerCameraPhotoPosition2XTextBox.Text, out _) &&
             TryParseCoordinate(LowerCameraPhotoPosition2YTextBox.Text, out _));
        var allLowerCameraRotationCentersValid =
            !lowerCameraCorrectionEnabled ||
            (TryParseCoordinate(LowerCameraNozzle1RotationCenterXTextBox.Text, out _) &&
             TryParseCoordinate(LowerCameraNozzle1RotationCenterYTextBox.Text, out _) &&
             TryParseCoordinate(LowerCameraNozzle2RotationCenterXTextBox.Text, out _) &&
             TryParseCoordinate(LowerCameraNozzle2RotationCenterYTextBox.Text, out _));
        var allBinDropPositionsValid =
            TryParseCoordinate(Bin0PositionXTextBox.Text, out _) &&
            TryParseCoordinate(Bin0PositionYTextBox.Text, out _) &&
            TryParseCoordinate(Bin1PositionXTextBox.Text, out _) &&
            TryParseCoordinate(Bin1PositionYTextBox.Text, out _) &&
            TryParseCoordinate(Bin2PositionXTextBox.Text, out _) &&
            TryParseCoordinate(Bin2PositionYTextBox.Text, out _) &&
            TryParseCoordinate(Bin3PositionXTextBox.Text, out _) &&
            TryParseCoordinate(Bin3PositionYTextBox.Text, out _);
        StartProductionButton.IsEnabled =
            !_oneKeyCollectRunning &&
            visionControllersReady &&
            allProductionAxisParametersValid &&
            allTestStationParametersValid &&
            visionPickupCountValid &&
            allZPositionsValid &&
            allSecondSetXyPositionsValid &&
            allLowerCameraPhotoPositionsValid &&
            allLowerCameraRotationCentersValid &&
            ObservationCompensationInputsValid() &&
            allBinDropPositionsValid &&
            (_startSequenceRunning ? !_productionStopRequested : commandsIdle);
        StartProductionTitleText.Text = _startSequenceRunning
            ? (_productionStopRequested
                ? "正在停止"
                : _productionPauseRequested
                    ? "继续运行"
                    : "暂停循环")
            : "开始运行";
        var productionButtonPaused = _startSequenceRunning && _productionPauseRequested;
        StartProductionButton.Background = new SolidColorBrush(
            productionButtonPaused || !_startSequenceRunning
                ? Color.FromRgb(22, 139, 80)
                : Color.FromRgb(181, 22, 35));
        StartProductionButton.BorderBrush = new SolidColorBrush(
            productionButtonPaused || !_startSequenceRunning
                ? Color.FromRgb(56, 185, 121)
                : Color.FromRgb(255, 98, 110));
        OneKeyResetButton.IsEnabled = _motionController is not null && commandsIdle;
        OneKeyCollectButton.IsEnabled =
            _motionController is not null &&
            ddAxisParametersValid &&
            (_oneKeyCollectRunning ? !_productionStopRequested : commandsIdle);
        OneKeyCollectTitleText.Text = _oneKeyCollectRunning
            ? (_productionStopRequested ? "正在停止" : "停止收料")
            : "一键收料";
        OneKeyCollectButton.Background = new SolidColorBrush(
            _oneKeyCollectRunning
                ? Color.FromRgb(181, 22, 35)
                : Color.FromRgb(23, 107, 135));
        OneKeyCollectButton.BorderBrush = new SolidColorBrush(
            _oneKeyCollectRunning
                ? Color.FromRgb(255, 98, 110)
                : Color.FromRgb(56, 163, 197));
        FirstSetTeachingCenterXTextBox.IsEnabled = commandsIdle;
        FirstSetTeachingCenterYTextBox.IsEnabled = commandsIdle;
        FirstSetTeachingPressPositionXTextBox.IsEnabled = commandsIdle;
        FirstSetTeachingPressPositionYTextBox.IsEnabled = commandsIdle;
        RecordFirstSetTeachingCenterButton.IsEnabled =
            _motionController is not null && commandsIdle;
        RecordFirstSetTeachingPressPositionButton.IsEnabled =
            _motionController is not null && commandsIdle;
        MoveFirstSetTeachingCenterButton.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            allProductionAxisParametersValid &&
            TryParseCoordinate(FirstSetTeachingCenterXTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetTeachingCenterYTextBox.Text, out _);
        MoveFirstSetTeachingPressPositionButton.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            allProductionAxisParametersValid &&
            TryParseCoordinate(FirstSetTeachingPressPositionXTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetTeachingPressPositionYTextBox.Text, out _);
        PresetPosition1XTextBox.IsEnabled = commandsIdle;
        PresetPosition1YTextBox.IsEnabled = commandsIdle;
        PresetPosition2XTextBox.IsEnabled = commandsIdle;
        PresetPosition2YTextBox.IsEnabled = commandsIdle;
        UpperCameraCorrectionCheckBox.IsEnabled = commandsIdle;
        LowerCameraCorrection1CheckBox.IsEnabled = commandsIdle;
        LowerCameraRotationDelayCheckBox.IsEnabled = commandsIdle;
        XyLinearInterpolationCheckBox.IsEnabled = commandsIdle;
        VisionPickupCountTextBox.IsEnabled = commandsIdle;
        LowerCameraPhotoPosition1XTextBox.IsEnabled = commandsIdle;
        LowerCameraPhotoPosition1YTextBox.IsEnabled = commandsIdle;
        LowerCameraPhotoPosition2XTextBox.IsEnabled = commandsIdle;
        LowerCameraPhotoPosition2YTextBox.IsEnabled = commandsIdle;
        LowerCameraNozzle1RotationCenterXTextBox.IsEnabled = commandsIdle;
        LowerCameraNozzle1RotationCenterYTextBox.IsEnabled = commandsIdle;
        LowerCameraNozzle2RotationCenterXTextBox.IsEnabled = commandsIdle;
        LowerCameraNozzle2RotationCenterYTextBox.IsEnabled = commandsIdle;
        foreach (var editors in _productionAxisMotionEditors.Values)
        {
            editors.SetEnabled(commandsIdle);
        }

        TestStationDwellTextBox.IsEnabled = commandsIdle;
        BatchNumberTextBox.IsEnabled = commandsIdle;
        TestRetryCountTextBox.IsEnabled = commandsIdle;
        SM7110LowerLimitTextBox.IsEnabled = commandsIdle;
        SM7110UpperLimitTextBox.IsEnabled = commandsIdle;
        SM7110MaximumTimeTextBox.IsEnabled = commandsIdle;
        SM7110LimitModeComboBox.IsEnabled = commandsIdle;
        foreach (var definition in TestStationDefinitions)
        {
            var editors = GetTestStationPositionEditors(definition.StationNumber);
            editors.PressPosition.IsEnabled = commandsIdle;
            editors.WaitPosition.IsEnabled = commandsIdle;
            var controls = GetTestStationConfigurationControls(definition.StationNumber);
            controls.EnabledToggle.IsEnabled = commandsIdle;
            controls.InstrumentComboBox.IsEnabled = commandsIdle;
        }

        FirstSetNozzle1PickupZPositionTextBox.IsEnabled = commandsIdle;
        FirstSetNozzle1DropZPositionTextBox.IsEnabled = commandsIdle;
        FirstSetNozzle1SafeZPositionTextBox.IsEnabled = commandsIdle;
        FirstSetNozzle2PickupZPositionTextBox.IsEnabled = commandsIdle;
        FirstSetNozzle2DropZPositionTextBox.IsEnabled = commandsIdle;
        FirstSetNozzle2SafeZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetNozzle1PickupZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetNozzle1DropZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetNozzle1SafeZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetNozzle2PickupZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetNozzle2DropZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetNozzle2SafeZPositionTextBox.IsEnabled = commandsIdle;
        VacuumPickupDwellTextBox.IsEnabled = commandsIdle;
        VacuumBreakPulseTextBox.IsEnabled = commandsIdle;
        VacuumValveSwitchDelayTextBox.IsEnabled = commandsIdle;
        FirstSetNozzle1XyReleaseLiftTextBox.IsEnabled = commandsIdle;
        FirstSetNozzle2PreDropTextBox.IsEnabled = commandsIdle;
        FirstSetNozzle2PlacePreDropTextBox.IsEnabled = commandsIdle;
        SecondSetNozzle1PreDropTextBox.IsEnabled = commandsIdle;
        SecondSetPosition1XTextBox.IsEnabled = commandsIdle;
        SecondSetPosition1YTextBox.IsEnabled = commandsIdle;
        SecondSetPosition2XTextBox.IsEnabled = commandsIdle;
        SecondSetPosition2YTextBox.IsEnabled = commandsIdle;
        Bin0PositionXTextBox.IsEnabled = commandsIdle;
        Bin0PositionYTextBox.IsEnabled = commandsIdle;
        Bin1PositionXTextBox.IsEnabled = commandsIdle;
        Bin1PositionYTextBox.IsEnabled = commandsIdle;
        Bin2PositionXTextBox.IsEnabled = commandsIdle;
        Bin2PositionYTextBox.IsEnabled = commandsIdle;
        Bin3PositionXTextBox.IsEnabled = commandsIdle;
        Bin3PositionYTextBox.IsEnabled = commandsIdle;
        RecordPresetPosition1Button.IsEnabled = _motionController is not null && commandsIdle;
        RecordPresetPosition2Button.IsEnabled = _motionController is not null && commandsIdle;
        RecordLowerCameraPhotoPosition1Button.IsEnabled = _motionController is not null && commandsIdle;
        RecordLowerCameraPhotoPosition2Button.IsEnabled = _motionController is not null && commandsIdle;
        RecordSecondSetPosition1Button.IsEnabled = _motionController is not null && commandsIdle;
        RecordSecondSetPosition2Button.IsEnabled = _motionController is not null && commandsIdle;
        MovePresetPosition1Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            allProductionAxisParametersValid &&
            TryParseCoordinate(PresetPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(PresetPosition1YTextBox.Text, out _);
        MovePresetPosition2Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            allProductionAxisParametersValid &&
            TryParseCoordinate(PresetPosition2XTextBox.Text, out _) &&
            TryParseCoordinate(PresetPosition2YTextBox.Text, out _);
        MoveLowerCameraPhotoPosition1Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            allProductionAxisParametersValid &&
            TryParseCoordinate(LowerCameraPhotoPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(LowerCameraPhotoPosition1YTextBox.Text, out _);
        MoveLowerCameraPhotoPosition2Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            allProductionAxisParametersValid &&
            TryParseCoordinate(LowerCameraPhotoPosition2XTextBox.Text, out _) &&
            TryParseCoordinate(LowerCameraPhotoPosition2YTextBox.Text, out _);
        MoveSecondSetPosition1Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            allProductionAxisParametersValid &&
            TryParseCoordinate(SecondSetPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition1YTextBox.Text, out _);
        MoveSecondSetPosition2Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            allProductionAxisParametersValid &&
            TryParseCoordinate(SecondSetPosition2XTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition2YTextBox.Text, out _);
        HomeEmergencyStopButton.IsEnabled = _motionController is not null;
    }

    private void SetStartProductionStatus(string message, Color color)
    {
        if (_oneKeyCollectRunning)
        {
            SetOneKeyCollectStatus(message, color);
            return;
        }

        StartProductionHintText.Text = message;
        StartProductionHintText.ToolTip = message;
        StartProductionHintText.Foreground = new SolidColorBrush(color);
    }

    private void SetOneKeyCollectStatus(string message, Color color)
    {
        OneKeyCollectHintText.Text = message;
        OneKeyCollectHintText.ToolTip = message;
        OneKeyCollectHintText.Foreground = new SolidColorBrush(color);
    }

    private void SaveLowerCameraPhotoPositionsFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        if (LowerCameraPhotoPosition1XTextBox is null ||
            LowerCameraPhotoPosition1YTextBox is null ||
            LowerCameraPhotoPosition2XTextBox is null ||
            LowerCameraPhotoPosition2YTextBox is null ||
            !TryParseOptionalCoordinate(LowerCameraPhotoPosition1XTextBox.Text, out var position1X) ||
            !TryParseOptionalCoordinate(LowerCameraPhotoPosition1YTextBox.Text, out var position1Y) ||
            !TryParseOptionalCoordinate(LowerCameraPhotoPosition2XTextBox.Text, out var position2X) ||
            !TryParseOptionalCoordinate(LowerCameraPhotoPosition2YTextBox.Text, out var position2Y))
        {
            if (throwOnInvalid)
            {
                throw new ArgumentException("下相机拍照位未保存：两个拍照位的X/Y须为有限数值，未配置的位置可留空。");
            }
            return;
        }

        _homeSettings.LowerCameraPhotoPosition1X = position1X;
        _homeSettings.LowerCameraPhotoPosition1Y = position1Y;
        _homeSettings.LowerCameraPhotoPosition2X = position2X;
        _homeSettings.LowerCameraPhotoPosition2Y = position2Y;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetLowerCameraPhotoPositionStatus(
                $"保存下相机拍照位失败：{exception.Message}",
                false);
        }
    }

    private void SaveLowerCameraRotationCentersFromInputs(bool throwOnInvalid = false)
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        if (LowerCameraNozzle1RotationCenterXTextBox is null ||
            LowerCameraNozzle1RotationCenterYTextBox is null ||
            LowerCameraNozzle2RotationCenterXTextBox is null ||
            LowerCameraNozzle2RotationCenterYTextBox is null ||
            !TryParseOptionalCoordinate(
                LowerCameraNozzle1RotationCenterXTextBox.Text,
                out var nozzle1CenterX) ||
            !TryParseOptionalCoordinate(
                LowerCameraNozzle1RotationCenterYTextBox.Text,
                out var nozzle1CenterY) ||
            !TryParseOptionalCoordinate(
                LowerCameraNozzle2RotationCenterXTextBox.Text,
                out var nozzle2CenterX) ||
            !TryParseOptionalCoordinate(
                LowerCameraNozzle2RotationCenterYTextBox.Text,
                out var nozzle2CenterY))
        {
            if (throwOnInvalid)
            {
                throw new ArgumentException("下相机旋转中心未保存：两个吸嘴的X/Y须为有限数值，未配置的中心可留空。");
            }
            return;
        }

        _homeSettings.LowerCameraNozzle1RotationCenterX = nozzle1CenterX;
        _homeSettings.LowerCameraNozzle1RotationCenterY = nozzle1CenterY;
        _homeSettings.LowerCameraNozzle2RotationCenterX = nozzle2CenterX;
        _homeSettings.LowerCameraNozzle2RotationCenterY = nozzle2CenterY;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
            SetLowerCameraPhotoPositionStatus(
                "下相机旋转中心参数已更新；计算流程会自动回填对应吸嘴的X/Y。",
                true);
        }
        catch (Exception exception) when (!throwOnInvalid)
        {
            SetLowerCameraPhotoPositionStatus(
                $"保存下相机旋转中心参数失败：{exception.Message}",
                false);
        }
    }

    private void ResetUphTracking()
    {
        _uphRefreshTimer.Stop();
        _uphStopwatch.Reset();
        _uphCompletedUnitCount = 0;
        UpdateUphDisplay();
    }

    private void StartUphTracking()
    {
        if (_uphStopwatch.IsRunning)
        {
            return;
        }

        _uphStopwatch.Start();
        _uphRefreshTimer.Start();
        UpdateUphDisplay();
    }

    private void StopUphTracking()
    {
        if (_uphStopwatch.IsRunning)
        {
            _uphStopwatch.Stop();
        }

        _uphRefreshTimer.Stop();
        UpdateUphDisplay();
        if (_distributionDirty) RefreshDistributionCharts();
    }

    private void RecordCompletedUphUnit()
    {
        if (_uphStopwatch.Elapsed == TimeSpan.Zero)
        {
            return;
        }

        _uphCompletedUnitCount++;
        UpdateUphDisplay();
    }

    private void UpdateUphDisplay()
    {
        if (UphValueText is null)
        {
            return;
        }

        var elapsedHours = _uphStopwatch.Elapsed.TotalHours;
        var uph = elapsedHours > 0
            ? _uphCompletedUnitCount / elapsedHours
            : 0d;
        UphValueText.Text = Math.Round(uph, MidpointRounding.AwayFromZero)
            .ToString("0", CultureInfo.InvariantCulture);
        UphValueText.ToolTip = _uphStopwatch.Elapsed == TimeSpan.Zero
            ? "等待正式生产后开始统计"
            : $"实际平均 · 已完成 {_uphCompletedUnitCount} 件 · " +
              $"有效生产时间 {_uphStopwatch.Elapsed:hh\\:mm\\:ss} · " +
              $"UPH {Math.Round(uph, MidpointRounding.AwayFromZero):0}";
    }

    private void SetOneKeyResetStatus(string message, Color color)
    {
        OneKeyResetHintText.Text = message;
        OneKeyResetHintText.ToolTip = message;
        OneKeyResetHintText.Foreground = new SolidColorBrush(color);
    }

    private async Task PrepareBlobInspectionVisionDisplayAsync(VisualCalibrationPage visualCalibrationController)
    {
        BlobInspectionImage.Source = null;
        BlobInspectionOverlayCanvas.Children.Clear();
        BlobInspectionImageViewbox.Visibility = Visibility.Collapsed;
        BlobInspectionImagePlaceholder.Visibility = Visibility.Collapsed;
        BlobInspectionVisionDisplayHost.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text = "找芯片流程 · 图像源1";
        BlobInspectionImageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(98, 181, 255));

        await Dispatcher.InvokeAsync(
            () => BlobInspectionVisionDisplayHost.UpdateLayout(),
            System.Windows.Threading.DispatcherPriority.Loaded);
        var displayHostWindow = BlobInspectionVisionDisplayHost.HostWindow;
        if (displayHostWindow == IntPtr.Zero)
        {
            throw new InvalidOperationException("主页 Blob 显示区域尚未创建。");
        }

        await visualCalibrationController.ActivateInspectionViewAsync(displayHostWindow);
    }

    private void ShowLowerCameraCorrectionVisionStatus(int nozzleNumber)
    {
        BlobInspectionImageStatusText.Text =
            $"下相机纠偏 · 吸嘴{nozzleNumber} · 当前流程结果（含渲染叠加）";
        BlobInspectionImageStatusText.Foreground =
            new SolidColorBrush(Color.FromRgb(242, 181, 68));
    }

    private void BlobInspectionVisionDisplayHost_HostSizeChanged(object? sender, EventArgs e)
    {
        if (BlobInspectionVisionDisplayHost.Visibility == Visibility.Visible)
        {
            _visualCalibrationController?.RefreshVisionDisplay();
        }
    }

    private void ClearBlobInspectionResult()
    {
        BlobInspectionImage.Source = null;
        BlobInspectionOverlayCanvas.Children.Clear();
        BlobInspectionVisionDisplayHost.Visibility = Visibility.Hidden;
        BlobInspectionImageViewbox.Visibility = Visibility.Collapsed;
        BlobInspectionImagePlaceholder.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text = "等待拍照";
        BlobInspectionImageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(98, 181, 255));
    }

    private void SetBlobInspectionResult(VisionRectangleBlobResult result)
    {
        if (BlobInspectionVisionDisplayHost.Visibility == Visibility.Visible)
        {
            if (!string.IsNullOrWhiteSpace(result.ImagePath))
            {
                try
                {
                    File.Delete(result.ImagePath);
                }
                catch
                {
                }
            }

            BlobInspectionImageStatusText.Text =
                result.ImageWidth > 0 && result.ImageHeight > 0
                    ? $"找芯片流程 · {result.Rectangles.Count}颗 · 图像源1 · {result.ImageWidth}×{result.ImageHeight}"
                    : $"找芯片流程 · {result.Rectangles.Count}颗 · 图像源1";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(73, 209, 125));
            return;
        }

        if (result.ImageWidth <= 0 ||
            result.ImageHeight <= 0 ||
            string.IsNullOrWhiteSpace(result.ImagePath))
        {
            BlobInspectionImageStatusText.Text = "XY已显示 · 本次未返回检测图";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(242, 181, 68));
            return;
        }

        try
        {
            ShowBlobInspectionImage(result);
        }
        catch
        {
            // 图片显示不是生产结果的前置条件；Blob 坐标已成功取得并保留在主页。
            BlobInspectionImageStatusText.Text = "XY已显示 · 检测图加载失败";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(242, 181, 68));
        }
    }

    /// <summary>
    /// 加载 VisionMaster 本次保存的相机图，并在同一像素坐标系中叠加全部 Blob 框和质心。
    /// BitmapCacheOption.OnLoad 会把文件完整读入内存，因此加载后即可删除临时文件。
    /// </summary>
    private void ShowBlobInspectionImage(VisionRectangleBlobResult result)
    {
        if (string.IsNullOrWhiteSpace(result.ImagePath) || !File.Exists(result.ImagePath))
        {
            throw new FileNotFoundException("VisionMaster 本次Blob检测图不存在。", result.ImagePath);
        }

        BitmapImage bitmap;
        try
        {
            using var stream = new FileStream(
                result.ImagePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
        }
        finally
        {
            try
            {
                File.Delete(result.ImagePath);
            }
            catch
            {
                // 图片已经完整载入内存；临时文件清理失败不影响本次结果显示。
            }
        }

        var imageWidth = bitmap.PixelWidth > 0 ? bitmap.PixelWidth : result.ImageWidth;
        var imageHeight = bitmap.PixelHeight > 0 ? bitmap.PixelHeight : result.ImageHeight;
        if (imageWidth <= 0 || imageHeight <= 0)
        {
            throw new InvalidDataException("VisionMaster 本次Blob检测图尺寸无效。");
        }

        BlobInspectionImageSurface.Width = imageWidth;
        BlobInspectionImageSurface.Height = imageHeight;
        BlobInspectionOverlayCanvas.Width = imageWidth;
        BlobInspectionOverlayCanvas.Height = imageHeight;
        BlobInspectionImage.Source = bitmap;
        BlobInspectionOverlayCanvas.Children.Clear();
        var overlayColors = new[]
        {
            Color.FromRgb(0, 230, 118),
            Color.FromRgb(64, 196, 255),
            Color.FromRgb(255, 193, 7),
            Color.FromRgb(255, 112, 67),
            Color.FromRgb(179, 136, 255)
        };
        for (var index = 0; index < result.Rectangles.Count; index++)
        {
            DrawBlobOverlay(
                result.Rectangles[index],
                index + 1,
                overlayColors[index % overlayColors.Length],
                imageWidth,
                imageHeight);
        }

        BlobInspectionImagePlaceholder.Visibility = Visibility.Collapsed;
        BlobInspectionImageViewbox.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text =
            $"已检测 {result.Rectangles.Count} 颗 · {imageWidth}×{imageHeight}";
        BlobInspectionImageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(73, 209, 125));
    }

    private void DrawBlobOverlay(
        VisionBlobRectangle blob,
        int number,
        Color color,
        double imageWidth,
        double imageHeight)
    {
        var left = Math.Clamp(blob.Left, 0d, Math.Max(0d, imageWidth - 1d));
        var top = Math.Clamp(blob.Top, 0d, Math.Max(0d, imageHeight - 1d));
        var width = Math.Clamp(blob.Width, 1d, Math.Max(1d, imageWidth - left));
        var height = Math.Clamp(blob.Height, 1d, Math.Max(1d, imageHeight - top));
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var strokeThickness = Math.Max(2d, Math.Min(imageWidth, imageHeight) / 500d);

        var rectangle = new ShapeRectangle
        {
            Width = width,
            Height = height,
            Stroke = brush,
            StrokeThickness = strokeThickness
        };
        Canvas.SetLeft(rectangle, left);
        Canvas.SetTop(rectangle, top);
        BlobInspectionOverlayCanvas.Children.Add(rectangle);

        var crosshairRadius = Math.Max(8d, Math.Min(imageWidth, imageHeight) / 80d);
        AddBlobCrosshairLine(
            blob.X - crosshairRadius,
            blob.Y,
            blob.X + crosshairRadius,
            blob.Y,
            brush,
            strokeThickness);
        AddBlobCrosshairLine(
            blob.X,
            blob.Y - crosshairRadius,
            blob.X,
            blob.Y + crosshairRadius,
            brush,
            strokeThickness);

        var label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(190, 5, 20, 30)),
            BorderBrush = brush,
            BorderThickness = new Thickness(strokeThickness),
            Padding = new Thickness(5d, 2d, 5d, 2d),
            Child = new TextBlock
            {
                Text = $"结果{number}  X={blob.X:0.0}  Y={blob.Y:0.0}  R={blob.RotationDegrees:0.00}°",
                Foreground = brush,
                FontSize = Math.Max(12d, Math.Min(imageWidth, imageHeight) / 55d),
                FontWeight = FontWeights.Bold
            }
        };
        Canvas.SetLeft(label, left);
        Canvas.SetTop(label, Math.Max(0d, top - Math.Min(imageWidth, imageHeight) / 28d));
        BlobInspectionOverlayCanvas.Children.Add(label);
    }

    private void AddBlobCrosshairLine(
        double x1,
        double y1,
        double x2,
        double y2,
        Brush brush,
        double strokeThickness)
    {
        BlobInspectionOverlayCanvas.Children.Add(new ShapeLine
        {
            X1 = x1,
            Y1 = y1,
            X2 = x2,
            Y2 = y2,
            Stroke = brush,
            StrokeThickness = strokeThickness
        });
    }

    /// <summary>
    /// 按当前距离和配置速度估算超时，额外预留5秒用于加减速和状态刷新。
    /// </summary>
    private static int CalculateStartMoveTimeout(
        double currentX,
        double currentY,
        double targetX,
        double targetY,
        double velocity)
    {
        var longestDistance = Math.Max(
            Math.Abs(targetX - currentX),
            Math.Abs(targetY - currentY));
        var estimatedMilliseconds = longestDistance / velocity * 1000d + 5000d;
        return (int)Math.Clamp(Math.Ceiling(estimatedMilliseconds), 10_000d, 120_000d);
    }

    private void SetFirstSetPositionStatus(string message, bool success)
    {
        PresetPositionStatusText.Text = message;
        PresetPositionStatusText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(73, 209, 125)
            : Color.FromRgb(242, 181, 68));
    }

    private void SetFirstSetTeachingPositionStatus(string message, bool success)
    {
        FirstSetTeachingPositionStatusText.Text = message;
        FirstSetTeachingPositionStatusText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(73, 209, 125)
            : Color.FromRgb(242, 181, 68));
    }

    private void SetLowerCameraPhotoPositionStatus(string message, bool success)
    {
        LowerCameraPhotoPositionStatusText.Text = message;
        LowerCameraPhotoPositionStatusText.Foreground = new SolidColorBrush(success
            ? Color.FromRgb(73, 209, 125)
            : Color.FromRgb(242, 181, 68));
    }

    private static double ParseFiniteCoordinate(string? value, string fieldName)
    {
        if (!TryParseCoordinate(value, out var parsed))
        {
            throw new ArgumentException($"{fieldName}必须是有效数字。");
        }

        return parsed;
    }

    private static double ParseProductionVelocity(string? value, string fieldName)
    {
        if (!TryParseProductionVelocity(value, out var velocity))
        {
            throw new ArgumentException($"{fieldName}必须是大于 0 的有效数字。");
        }

        return velocity;
    }

    private static double ParseNonNegativeCoordinate(string? value, string fieldName)
    {
        if (!TryParseCoordinate(value, out var parsed) || parsed < 0)
        {
            throw new ArgumentException($"{fieldName}必须是大于或等于0的有效数字。");
        }

        return parsed;
    }

    private static int ParseMilliseconds(string? value, string fieldName)
    {
        if (!TryParseMilliseconds(value, out var milliseconds))
        {
            throw new ArgumentException($"{fieldName}必须是0–60000之间的整数毫秒。");
        }

        return milliseconds;
    }

    private static int ParseVisionPickupCount(string? value, string fieldName)
    {
        if (!TryParseVisionPickupCount(value, out var pickupCount))
        {
            throw new ArgumentException(
                $"{fieldName}必须是{MinVisionPickupCount}–{MaxVisionPickupCount}之间的偶数。");
        }

        return pickupCount;
    }

    private static bool TryParseVisionPickupCount(string? value, out int pickupCount)
    {
        return int.TryParse(
                   value,
                   NumberStyles.Integer,
                   CultureInfo.CurrentCulture,
                   out pickupCount) &&
               pickupCount is >= MinVisionPickupCount and <= MaxVisionPickupCount &&
               pickupCount % 2 == 0;
    }

    private static bool TryParseMilliseconds(string? value, out int milliseconds)
    {
        return int.TryParse(
                   value,
                   NumberStyles.Integer,
                   CultureInfo.CurrentCulture,
                   out milliseconds) &&
               milliseconds is >= 0 and <= 60_000;
    }

    private static bool TryParseProductionVelocity(string? value, out double velocity)
    {
        return TryParseCoordinate(value, out velocity) && velocity > 0;
    }

    private static bool TryParseCoordinate(string? value, out double parsed)
    {
        return (double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.CurrentCulture,
                    out parsed) ||
                double.TryParse(
                    value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out parsed)) &&
               double.IsFinite(parsed);
    }

    private static string FormatPresetCoordinate(double? value)
    {
        return value is { } coordinate && double.IsFinite(coordinate)
            ? coordinate.ToString("R", CultureInfo.CurrentCulture)
            : string.Empty;
    }

    private static bool TryParseOptionalCoordinate(string? value, out double? parsed)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = null;
            return true;
        }

        if (TryParseCoordinate(value, out var coordinate))
        {
            parsed = coordinate;
            return true;
        }

        parsed = null;
        return false;
    }

    private readonly record struct ProductionZPositions(
        NozzleZPositions FirstSetNozzle1,
        NozzleZPositions FirstSetNozzle2,
        NozzleZPositions SecondSetNozzle1,
        NozzleZPositions SecondSetNozzle2)
    {
        public NozzleZPositions Resolve(
            VisionCalibrationAxisSet axisSet,
            int nozzleNumber)
        {
            return (axisSet, nozzleNumber) switch
            {
                (VisionCalibrationAxisSet.First, 1) => FirstSetNozzle1,
                (VisionCalibrationAxisSet.First, 2) => FirstSetNozzle2,
                (VisionCalibrationAxisSet.Second, 1) => SecondSetNozzle1,
                (VisionCalibrationAxisSet.Second, 2) => SecondSetNozzle2,
                _ => throw new ArgumentOutOfRangeException(
                    nameof(nozzleNumber),
                    "吸嘴编号只能是1或2。")
            };
        }
    }

    private readonly record struct NozzleZPositions(
        double Pickup,
        double Drop,
        double Safe);

    private readonly record struct ProductionZDwellTimes(
        int PickupMilliseconds,
        int BreakVacuumMilliseconds,
        int ValveSwitchDelayMilliseconds,
        double FirstSetNozzle1XyReleaseLiftPulses,
        double FirstSetNozzle2PreDropPulses,
        double FirstSetNozzle2PlacePreDropPulses,
        double SecondSetNozzle1PreDropPulses);

    private readonly record struct SecondSetNozzle2PickupOverlapTasks(
        Task Nozzle2SafeTask,
        Task Nozzle1PreDropTask)
    {
        public static SecondSetNozzle2PickupOverlapTasks Completed =>
            new(Task.CompletedTask, Task.CompletedTask);
    }

    private readonly record struct SecondSetXyPositions(
        double Position1X,
        double Position1Y,
        double Position2X,
        double Position2Y);

    private readonly record struct LowerCameraPhotoPositions(
        double Position1X,
        double Position1Y,
        double Position2X,
        double Position2Y);

    private sealed record LowerCameraCorrectionProfile(
        string CalibrationFilePath,
        double RotationCenterX,
        double RotationCenterY);

    private sealed record UpperCameraRotationTargets(
        Dictionary<int, double> AbsoluteTargets,
        double? Nozzle1Pulses,
        double? Nozzle1CorrectionDegrees,
        double? Nozzle1TargetR,
        double? Nozzle2Pulses,
        double? Nozzle2CorrectionDegrees,
        double? Nozzle2TargetR)
    {
        public static UpperCameraRotationTargets Empty { get; } = new(
            [],
            null,
            null,
            null,
            null,
            null,
            null);
    }

    private sealed record LowerCameraCorrectionResults(
        VisionLowerCameraCorrectionResult? Nozzle1,
        string? Nozzle1Error,
        VisionLowerCameraCorrectionResult? Nozzle2,
        string? Nozzle2Error,
        bool Nozzle1Required,
        bool Nozzle2Required)
    {
        public bool HasFailure =>
            (Nozzle1Required && Nozzle1 is null) ||
            (Nozzle2Required && Nozzle2 is null);

        public bool HasAnySuccess => Nozzle1 is not null || Nozzle2 is not null;
    }

    private readonly record struct LowerCameraPlacementTarget(
        double X,
        double Y,
        double R,
        double RCorrectionPulses);

    private readonly record struct NozzlePickupBatch(
        VisionMotionTarget? Nozzle1,
        VisionMotionTarget? Nozzle2)
    {
        public int Count => (Nozzle1.HasValue ? 1 : 0) + (Nozzle2.HasValue ? 1 : 0);
    }

    private readonly record struct BinDropPosition(double X, double Y);

    private readonly record struct TestStationMeasurementResult(
        string Bin,
        string DisplayText,
        string StatusDescription,
        bool Passed,
        bool? SM7110Passed = null,
        double? SM7110Value = null,
        bool E4981ALossRejected = false,
        string? E4981ALossFailureReason = null,
        E4981AMeasurementResult? E4981AReading = null,
        SM7110MeasurementResult? SM7110Reading = null)
    {
        // 产品分料判定独立于当前仪表的重试判定；SM7110合格不能覆盖E4981A的NG。
        public bool IsNg => Bin == "BIN0" || E4981ALossRejected || SM7110Passed == false;

        public string ResultLabel => IsNg ? "NG · BIN0" : SM7110Passed switch
        {
            true => $"OK · {Bin}",
            false => $"NG · {Bin}",
            null => Bin
        };
    }

    private sealed record SM7110AcceptanceRange(double Lower, double Upper, string MeasurementMode)
    {
        public double? MaximumTestSeconds { get; init; }

        public SM7110TimedTestSettings ToTimedTestSettings()
        {
            var settings = new SM7110TimedTestSettings(Lower, MaximumTestSeconds ?? double.NaN);
            settings.Validate();
            return settings;
        }
        public void ValidateMeasurementMode(string mode)
        {
            if (!string.Equals(mode?.Trim(), MeasurementMode, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"SM7110区间模式为{MeasurementMode}，仪表模式为{mode}，单位不一致，请检查参数设置。");
        }
    }

    private sealed record BinDropPositions(
        BinDropPosition Bin0,
        BinDropPosition Bin1,
        BinDropPosition Bin2,
        BinDropPosition Bin3)
    {
        public BinDropPosition Resolve(string? bin, int stationNumber)
        {
            return bin?.Trim().ToUpperInvariant() switch
            {
                "BIN0" or "NG" => Bin0,
                "BIN1" => Bin1,
                "BIN2" => Bin2,
                "BIN3" => Bin3,
                _ => throw new InvalidOperationException(
                    $"{stationNumber}工位的分BIN结果无效：{bin ?? "空"}。")
            };
        }
    }

    private readonly record struct ProductionAxisDefinition(
        int AxisNo,
        string GroupName,
        string DisplayName,
        double DefaultRunVelocity);

    private readonly record struct TestStationDefinition(
        int StationNumber,
        int AxisNo,
        string DisplayName);

    private sealed record ProductionAxisMotionEditors(
        Border Container,
        TextBox RunVelocity,
        TextBox StartVelocity,
        TextBox StopVelocity,
        TextBox AccelerationMilliseconds,
        TextBox DecelerationMilliseconds,
        TextBox STimeMilliseconds,
        TextBox DecelerationStopMilliseconds)
    {
        public void SetEnabled(bool enabled)
        {
            RunVelocity.IsEnabled = enabled;
            StartVelocity.IsEnabled = enabled;
            StopVelocity.IsEnabled = enabled;
            AccelerationMilliseconds.IsEnabled = enabled;
            DecelerationMilliseconds.IsEnabled = enabled;
            STimeMilliseconds.IsEnabled = enabled;
            DecelerationStopMilliseconds.IsEnabled = enabled;
        }
    }

}
