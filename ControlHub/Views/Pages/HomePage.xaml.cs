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
    private const int MaxCachedChipCount = 10;
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
    private const int LowerCameraLightOutputBit = 10;
    private const int DefaultVacuumBreakPulseMilliseconds = 30;
    private const int DefaultVacuumValveSwitchDelayMilliseconds = 20;
    private const int DefaultVacuumPickupDwellMilliseconds = 500;
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
    private const int DefaultTestStationDwellMilliseconds = 100;
    private const int MoveAwayBeforeDdMilliseconds = 500;
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
    private static readonly int[] SecondSetProductionPeerAxisNos =
        [0, .. FirstSetAxisNos, .. FirstSetZAxisNos, .. FirstSetRAxisNos, .. MoveOutAxisNos];
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
    private bool _presetPositionMoveRunning;
    private bool _oneKeyResetRunning;
    private bool _startSequenceRunning;
    private bool _assignedNozzleMoveRunning;
    private CancellationTokenSource? _productionCancellation;
    private TaskCompletionSource<bool>? _productionCompletion;
    private bool _productionStopRequested;
    private bool _productionPauseRequested;
    private TaskCompletionSource<bool>? _productionResumeSignal;
    private readonly Stopwatch _uphStopwatch = new();
    private readonly DispatcherTimer _uphRefreshTimer;
    private long _uphCompletedUnitCount;
    private bool _preserveIoOnEmergencyStop;
    private VisionCalibrationAxisSet? _productionAxisSet;
    private ProductionZPositions? _productionZPositions;
    private ProductionZDwellTimes? _productionZDwellTimes;
    private SecondSetXyPositions? _secondSetXyPositions;
    private BinDropPositions? _binDropPositions;
    private LowerCameraPhotoPositions? _lowerCameraPhotoPositions;
    private IReadOnlyDictionary<int, ProductionAxisMotionSettings>? _productionAxisMotionSettings;
    private IReadOnlyDictionary<int, TestStationSettings>? _testStationSettings;
    private readonly Dictionary<int, ProductionAxisMotionEditors> _productionAxisMotionEditors = [];
    private readonly bool[,] _nozzleVacuumEnabledBySet = new bool[2, 3];
    private VisionMotionTarget? _blob1Nozzle1Target;
    private VisionMotionTarget? _blob2Nozzle2Target;
    private int _nextAssignedNozzleMoveStep;
    private int _carouselVisualStepOffset;
    private bool _loadingPresetPositions = true;
    private bool _updatingTestStationConfiguration;

    public HomePage()
    {
        InitializeComponent();
        _uphRefreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _uphRefreshTimer.Tick += (_, _) => UpdateUphDisplay();
        InitializeProductionMotionParameterEditors();
        LoadPresetPositions();
        UpdateCarouselStationDisplay(CreateCarouselStationStates());
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
        SaveFirstSetTeachingPositionsFromInputs();
        SavePresetPositionsFromInputs();
        SaveLowerCameraPhotoPositionsFromInputs();
        SaveLowerCameraRotationCentersFromInputs();
        SaveProductionAxisParametersFromInputs();
        SaveTestStationParametersFromInputs();
        SaveProductionZPositionsFromInputs();
        SaveSecondSetXyPositionsFromInputs();
        SaveBinPositionsFromInputs();
        return ProductRecipeStore.Clone(_homeSettings);
    }

    public HomePageSettings GetCurrentParameterSettings()
    {
        var settings = ProductRecipeStore.Clone(_homeSettings);
        settings.FirstSetTeachingCenterX =
            TryParseCoordinate(FirstSetTeachingCenterXTextBox.Text, out var centerX)
                ? centerX
                : null;
        settings.FirstSetTeachingCenterY =
            TryParseCoordinate(FirstSetTeachingCenterYTextBox.Text, out var centerY)
                ? centerY
                : null;
        settings.FirstSetTeachingPressPositionX =
            TryParseCoordinate(FirstSetTeachingPressPositionXTextBox.Text, out var pressPositionX)
                ? pressPositionX
                : null;
        settings.FirstSetTeachingPressPositionY =
            TryParseCoordinate(FirstSetTeachingPressPositionYTextBox.Text, out var pressPositionY)
                ? pressPositionY
                : null;
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
        if (_startSequenceRunning || _oneKeyResetRunning || _presetPositionMoveRunning ||
            _assignedNozzleMoveRunning)
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
        _motionController.EmergencyStopIssued += MotionController_EmergencyStopIssued;
        UpdateHomeCommandState();
    }

    private void MotionController_EmergencyStopIssued(object? sender, EventArgs e)
    {
        _preserveIoOnEmergencyStop = true;
        _ = RequestProductionStop();
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
        CancellationToken cancellationToken)
    {
        var axisSet = _productionAxisSet ?? _visionCalibration.ActiveAxisSet;
        var positions = GetProductionZPositions().Resolve(axisSet, nozzleNumber);
        await PickWithNozzleAsync(
            axisSet,
            nozzleNumber,
            positions.Pickup,
            positions.Safe,
            cancellationToken);
    }

    private async Task PickWithNozzleAsync(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        double pickupPosition,
        double safePosition,
        CancellationToken cancellationToken)
    {
        await MoveNozzleZToAsync(
            axisSet,
            nozzleNumber,
            pickupPosition,
            "取料位",
            cancellationToken);

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
            cancellationToken);
    }

    private async Task PlaceWithActiveSetNozzleAsync(
        int nozzleNumber,
        CancellationToken cancellationToken)
    {
        var axisSet = _productionAxisSet ?? _visionCalibration.ActiveAxisSet;
        var positions = GetProductionZPositions().Resolve(axisSet, nozzleNumber);
        await PlaceWithNozzleAsync(
            axisSet,
            nozzleNumber,
            positions.Drop,
            positions.Safe,
            cancellationToken);
    }

    private async Task PlaceWithNozzleAsync(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        double dropPosition,
        double safePosition,
        CancellationToken cancellationToken)
    {
        await MoveNozzleZToAsync(
            axisSet,
            nozzleNumber,
            dropPosition,
            "放料位",
            cancellationToken);

        await PulseNozzleBreakVacuumAsync(axisSet, nozzleNumber, cancellationToken);

        await MoveNozzleZToAsync(
            axisSet,
            nozzleNumber,
            safePosition,
            "安全位",
            cancellationToken);
    }

    private async Task MoveNozzleZToAsync(
        VisionCalibrationAxisSet axisSet,
        int nozzleNumber,
        double targetPosition,
        string positionName,
        CancellationToken cancellationToken)
    {
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
            allowedMovingAxisNos: GetProductionPeerAxisNos(axisSet),
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
        CancellationToken cancellationToken)
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
            allowedMovingAxisNos: GetProductionPeerAxisNos(VisionCalibrationAxisSet.First),
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
        CancellationToken cancellationToken)
    {
        await MoveNozzleZToAsync(
            VisionCalibrationAxisSet.Second,
            nozzleNumber,
            pickupPosition,
            "取料位",
            cancellationToken);

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
                cancellationToken);
        }
        finally
        {
            // 正常流程在Z轴回到安全高度后才关闭工位破真空。
            SetUnloadStationBreakVacuum(stationNumber, enabled: false);
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
        Task<int> activeFinalTestTask = Task.FromResult(0);

        // 如果当前已经在连续生产，再次点击按钮用于在安全节点暂停或继续。
        if (_startSequenceRunning)
        {
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
            _presetPositionMoveRunning ||
            _assignedNozzleMoveRunning)
        {
            // 当前设备还没空下来，直接忽略本次开始请求。
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

            // 两个下相机拍照位、两个吸嘴的旋转中心和标定文件在启动时一次性锁定。
            _lowerCameraPhotoPositions = ReadLowerCameraPhotoPositions();
            var lowerCameraNozzle1Profile = ReadLowerCameraCorrectionProfile(1);
            var lowerCameraNozzle2Profile = ReadLowerCameraCorrectionProfile(2);

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
            ResetUphTracking();

            // 刷新主页按钮状态，把“开始运行”切成“停止循环”，并锁住其它会冲突的操作。
            UpdateHomeCommandState();

            // 启动生产前先让R1/R2回原，同时经过位置2这个安全过渡点。
            // XY仍必须严格先走Y、确认到位后再走X；两项都完成后才进入第一轮取料。
            var productionAxes = VisionCalibration;
            var startupSafePosition = await PrepareProductionStartupAxesAsync(
                motionController,
                productionAxes.XHardwareAxisNo,
                productionAxes.YHardwareAxisNo,
                position2X,
                position2Y,
                velocity,
                firstSetYVelocity,
                _productionCancellation.Token);
            await WaitIfProductionPausedAsync(_productionCancellation.Token);
            SetStartProductionStatus(
                $"启动准备完成：R1/R2已回原，XY已按Y后X到达位置2" +
                $"({startupSafePosition.ActualX:0.###}, {startupSafePosition.ActualY:0.###})，准备进入取料流程…",
                Color.FromRgb(73, 209, 125));

            // 从第 0 轮开始计数，进入循环后先自增为第 1 轮。
            var cycleNumber = 0;

            // 转盘工位占料状态。启动时按空盘处理；放料到 1/2 后，后续每次 DD 转动推进一个工位。
            var carouselStations = CreateCarouselStationStates();
            _carouselVisualStepOffset = 0;
            UpdateCarouselStationDisplay(carouselStations);
            var pendingPickupBatches = new Queue<NozzlePickupBatch>();
            var vibrateAfterPickupCachePlaced = false;
            var singleChipRemainsAfterPickupCache = false;
            var consecutiveSingleChipVibrations = 0;
            var stopAfterCurrentBatch = false;
            ClearBlobInspectionResult();

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
                        yVelocityOverride: firstSetYVelocity);
                }
                await WaitIfProductionPausedAsync(_productionCancellation.Token);

                if (pendingPickupBatches.Count == 0)
                {
                    // 只有缓存已经取空时才重新执行找芯片流程；一次接收脚本1返回的全部X/Y/R结果。
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：XY已到初始位置({actual.ActualX:0.###}, {actual.ActualY:0.###})，" +
                        $"缓存已空，正在运行{ChipInspectionProcedureName} → {ChipInspectionResultModuleName}…",
                        Color.FromRgb(242, 181, 68));
                    await PrepareBlobInspectionVisionDisplayAsync(visualCalibrationController);
                    var blobResult = await RunChipInspectionWithFeederLightAsync(
                        visualCalibrationController,
                        _productionCancellation.Token);
                    SetBlobInspectionResult(blobResult);
                    var selectedChipCount = Math.Min(
                        blobResult.Rectangles.Count,
                        MaxCachedChipCount);

                    if (selectedChipCount == 0)
                    {
                        consecutiveSingleChipVibrations = 0;
                        SetStartProductionStatus(
                            "本次找芯片流程返回0条，正在震动料盘后重新拍照。",
                            Color.FromRgb(242, 181, 68));
                        await RunProductionVibrationAsync(
                            cycleNumber,
                            "视野内未找到芯片",
                            _productionCancellation.Token);
                        continue;
                    }

                    try
                    {
                        if (selectedChipCount == 1)
                        {
                            if (consecutiveSingleChipVibrations >=
                                EmptyTraySingleChipVibrationThreshold)
                            {
                                pendingPickupBatches.Enqueue(CalculateFinalSingleChipBatch(
                                    blobResult,
                                    calibrationFile.FilePath,
                                    actual.ActualX,
                                    actual.ActualY));
                                stopAfterCurrentBatch = true;
                                SetStartProductionStatus(
                                    $"连续震动{consecutiveSingleChipVibrations}次后视野仍只剩1颗，" +
                                    "判定缺料：改由吸嘴2取料，放到位置2后停机。",
                                    Color.FromRgb(242, 181, 68));
                            }
                            else
                            {
                                var vibrationNumber = consecutiveSingleChipVibrations + 1;
                                SetStartProductionStatus(
                                    $"视野只剩1颗，暂不抓取；正在执行第{vibrationNumber}/" +
                                    $"{EmptyTraySingleChipVibrationThreshold}次连续震动后重拍。",
                                    Color.FromRgb(242, 181, 68));
                                await RunProductionVibrationAsync(
                                    cycleNumber,
                                    $"视野只剩1颗（第{vibrationNumber}次）",
                                    _productionCancellation.Token);
                                consecutiveSingleChipVibrations = vibrationNumber;
                                continue;
                            }
                        }
                        else
                        {
                            consecutiveSingleChipVibrations = 0;
                            foreach (var batch in CalculateAssignedNozzleBatches(
                                         blobResult,
                                         calibrationFile.FilePath,
                                         actual.ActualX,
                                         actual.ActualY))
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

                if (nozzle1HasPart)
                {
                    // 正常成对取料先用吸嘴1，到位后 Z1 下探取料并安全回缩。
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：Blob识别完成，吸嘴1正在对位本批第1颗…",
                        Color.FromRgb(242, 181, 68));
                    await MoveAssignedNozzleStepAsync(1, _productionCancellation.Token);
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);
                    StartUphTracking();
                    await PickWithActiveSetNozzleAsync(1, _productionCancellation.Token);
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);
                }

                if (nozzle2HasPart)
                {
                    SetStartProductionStatus(
                        nozzle1HasPart
                            ? $"第{cycleNumber}轮：Z1已取料并回安全位，吸嘴2正在对位本批第2颗…"
                            : $"第{cycleNumber}轮：缺料收尾，吸嘴2正在对位最后1颗…",
                        Color.FromRgb(242, 181, 68));
                    await MoveAssignedNozzleStepAsync(2, _productionCancellation.Token);
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);
                    StartUphTracking();
                    await PickWithActiveSetNozzleAsync(2, _productionCancellation.Token);
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);
                }

                // 放料 XY 动作的安全门：必须再次确认两根 Z 轴都在本轮配置的安全高度。
                await EnsureActiveSetNozzlesAtSafeZAsync(_productionCancellation.Token);
                await WaitIfProductionPausedAsync(_productionCancellation.Token);

                // 保存找芯片角度粗校正之前的R轴基准。放料后两根R轴必须回到这里，
                // 不能回到已经包含粗校正量的位置，否则多轮生产会持续累加旋转。
                var pickupRPositions = motionController.CaptureCalibrationFeedback(
                    FirstSetNozzle1RHardwareAxisNo,
                    FirstSetNozzle2RHardwareAxisNo,
                    FirstSetProductionPeerAxisNos);

                // 两个吸嘴都取料并回安全Z后，依次到拍照位1/2执行各自的下相机纠偏。
                // 视觉执行失败已在方法内部按吸嘴降级；运动、IO等异常必须继续抛出并停机，
                // 不能把“未到拍照位”误判成普通纠偏失败后继续放料。
                var correctionResults = await RunLowerCameraCorrectionsAsync(
                    visualCalibrationController,
                    lowerCameraNozzle1Profile,
                    lowerCameraNozzle2Profile,
                    assignedTargets,
                    pickupRPositions,
                    _productionCancellation.Token);
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

                // 拍照和双吸嘴取料不等待DD；真正放料前只等待DD完成固定两次转动。
                if (activeCarouselAdvanceTask is not null)
                {
                    if (!activeCarouselAdvanceTask.IsCompleted)
                    {
                        SetStartProductionStatus(
                            $"第{cycleNumber}轮：本批{assignedTargets.Count}颗已吸取，正在等待DD完成两次转动及已启用测试站动作…",
                            Color.FromRgb(242, 181, 68));
                    }

                    var carouselAdvanceResult = await activeCarouselAdvanceTask;
                    activeCarouselAdvanceTask = null;
                    activeFinalTestTask = carouselAdvanceResult.FinalTestTask;
                    activeSecondSetUnloadTask = carouselAdvanceResult.SecondSetUnloadTask;
                    activeSecondSetPickupTask = carouselAdvanceResult.SecondSetPickupTask;
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：DD已完成 {carouselAdvanceResult.Turns} 次转动，测试任务已启动，立即开始上下料…",
                        Color.FromRgb(73, 209, 125));
                }
                await WaitIfProductionPausedAsync(_productionCancellation.Token);

                if (nozzle1HasPart)
                {
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：Z1/Z2均已回到配置安全位，" +
                        $"正在按{position1TargetDescription}放料到1工位(X={position1Target.X:0.###}, Y={position1Target.Y:0.###}" +
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

                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：1工位已到位，Z1正在下降到配置放料位…",
                        Color.FromRgb(242, 181, 68));
                    await PlaceWithActiveSetNozzleAsync(1, _productionCancellation.Token);
                    await MoveNozzleRToAsync(
                        VisionCalibrationAxisSet.First,
                        1,
                        pickupRPositions.ActualX,
                        "本批取料基准位",
                        _productionCancellation.Token);
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);
                }

                if (nozzle2HasPart)
                {
                    SetStartProductionStatus(
                        (nozzle1HasPart
                            ? $"第{cycleNumber}轮：Z1已放料、R1已回本批取料基准位，"
                            : $"第{cycleNumber}轮：缺料收尾，") +
                        $"正在按{position2TargetDescription}放料到2工位(X={position2Target.X:0.###}, Y={position2Target.Y:0.###}" +
                        $"{(nozzle2OriginalR.HasValue ? $", R={position2Target.R:0.###}" : string.Empty)})…",
                        Color.FromRgb(242, 181, 68));

                    if (nozzle2OriginalR.HasValue)
                    {
                        // 纠偏放料时将 X/Y/R2 一次下发，三轴同时运动并共同等待到位。
                        await MoveCorrectedPlacementAxesAsync(
                            2,
                            "位置 2",
                            position2Target,
                            _productionCancellation.Token);
                    }
                    else
                    {
                        await MovePresetPositionCoreAsync(
                            "位置 2",
                            position2Target.X,
                            position2Target.Y,
                            _productionCancellation.Token);
                    }
                    await WaitIfProductionPausedAsync(_productionCancellation.Token);

                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：2工位已到位，Z2正在下降到配置放料位…",
                        Color.FromRgb(242, 181, 68));
                    await PlaceWithActiveSetNozzleAsync(2, _productionCancellation.Token);
                    await MoveNozzleRToAsync(
                        VisionCalibrationAxisSet.First,
                        2,
                        pickupRPositions.ActualY,
                        "本批取料基准位",
                        _productionCancellation.Token);
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
                        $"连续震动{consecutiveSingleChipVibrations}次后仍剩的最后1颗" +
                        "已由吸嘴2放到位置2，确认料盘缺料，程序已停止。",
                        Color.FromRgb(73, 209, 125));
                    return;
                }

                if (vibrateAfterPickupCachePlaced && pendingPickupBatches.Count == 0)
                {
                    var vibrationReason = singleChipRemainsAfterPickupCache
                        ? "本次缓存成对物料已全部放完，料盘最后还剩1颗"
                        : "本次最多10条缓存已全部放完";
                    await RunProductionVibrationAsync(
                        cycleNumber,
                        vibrationReason,
                        _productionCancellation.Token);
                    consecutiveSingleChipVibrations = singleChipRemainsAfterPickupCache ? 1 : 0;
                    vibrateAfterPickupCachePlaced = false;
                    singleChipRemainsAfterPickupCache = false;
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
            await ObserveTaskNoThrowAsync(activeFinalTestTask);
            StopUphTracking();
            CloseAllActiveSetNozzleVacuumOutputsNoThrow();
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

            // 根据当前吸嘴步骤刷新吸嘴对位按钮文字。
            UpdateAssignedNozzleButtonText();

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
            $"第{cycleNumber}轮：{reason}，正在执行“震散 → 向左”…",
            Color.FromRgb(242, 181, 68));
        var vibrationCompleted = await connectionController.RunProductionScatterThenLeftAsync(
            cancellationToken);
        if (!vibrationCompleted)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException(
                "“震散 → 向左”未执行，请检查振动盘连接和方向震动参数。");
        }

        SetStartProductionStatus(
            $"第{cycleNumber}轮：{reason}，“震散 → 向左”完成，下一轮重新拍照。",
            Color.FromRgb(73, 209, 125));
    }

    private async Task<VisionRectangleBlobResult> RunChipInspectionWithFeederLightAsync(
        VisualCalibrationPage visualCalibrationController,
        CancellationToken cancellationToken)
    {
        var connectionController = _connectionConfigController
            ?? throw new InvalidOperationException("振动盘控制组件未连接，无法控制拍照光源。");

        SetStartProductionStatus(
            $"{ChipInspectionProcedureName}拍照前正在打开振动盘光源…",
            Color.FromRgb(242, 181, 68));
        if (!await connectionController.SetProductionLightAsync(enabled: true))
        {
            throw new InvalidOperationException("振动盘拍照光源打开失败，已取消本次拍照。");
        }

        var inspectionCompleted = false;
        try
        {
            var result = await visualCalibrationController.RunRectangleBlobInspectionAsync(
                cancellationToken);
            inspectionCompleted = true;
            return result;
        }
        finally
        {
            // 无论拍照成功、视觉异常还是用户停止，都必须尝试关灯。
            var lightTurnedOff = await connectionController.SetProductionLightAsync(enabled: false);
            if (!lightTurnedOff &&
                inspectionCompleted &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("振动盘已拍照，但光源关闭失败，已停止自动生产。");
            }
        }
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
        await resumeSignal.Task.WaitAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
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
            $"启动准备：R1/R2正在回原，XY同时按Y后X移动到位置2({targetX:0.###}, {targetY:0.###})…",
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
            yVelocityOverride: yVelocity);
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
        if (!requiredFinalTestTask.IsCompleted || !requiredSecondSetPickupTask.IsCompleted)
        {
            SetStartProductionStatus(
                "XY正在离开放料点准备下一轮；已启用测试站将随DD节拍执行，" +
                "下一次DD同时等待测试轴和第二套取料安全条件。",
                Color.FromRgb(242, 181, 68));
        }

        // DD必须等待已启用测试站回到等待位、第二套完成取料，并确认XY已离开放料点0.5秒。
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

        // 13/14工位状态在DD停稳的这一刻已经更新。这里直接启动第二套收料，
        // 不经过主循环，因此不等待第一套下一轮的拍照、取料或放料动作。
        // 若上一批第二套仍在向BIN位放料，只等待它释放轴3/4，避免同一轴组冲突。
        if (!requiredPreviousSecondSetUnloadTask.IsCompleted)
        {
            SetFirstSetPositionStatus(
                "DD已停稳且13/14工位已更新；等待第二套完成上一批BIN放料后立即取料。",
                true);
        }

        await requiredPreviousSecondSetUnloadTask;
        await WaitIfProductionPausedAsync(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var secondSetUnloadTask = StartSecondSetUnloadIfReadyAsync(
            carouselStations,
            cancellationToken,
            out var secondSetPickupTask);
        if (!secondSetPickupTask.IsCompleted)
        {
            SetFirstSetPositionStatus(
                "DD已停稳，13/14工位存在待收料产品；第二套已立即启动取料，不等待第一套上料。",
                true);
        }

        return carouselAdvanceResult with
        {
            SecondSetUnloadTask = secondSetUnloadTask,
            SecondSetPickupTask = secondSetPickupTask
        };
    }

    private Task StartSecondSetUnloadIfReadyAsync(
        CarouselStationState[] carouselStations,
        CancellationToken cancellationToken,
        out Task pickupCompletedTask)
    {
        // DD每次转动后会同步更新工位缓存。13、14任一工位有料即启动对应吸嘴收料，
        // 这样奇数批次的最后1颗也能独立完成下料。
        if (carouselStations.Length <= SecondSetNozzle1UnloadStation ||
            (!carouselStations[SecondSetNozzle2UnloadStation].Occupied &&
             !carouselStations[SecondSetNozzle1UnloadStation].Occupied))
        {
            pickupCompletedTask = Task.CompletedTask;
            return Task.CompletedTask;
        }

        if (_productionAxisSet != VisionCalibrationAxisSet.First)
        {
            throw new InvalidOperationException("第二套XY正在被主页上料流程占用，不能同时执行13/14工位收料。");
        }

        var pickupCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        pickupCompletedTask = pickupCompletion.Task;
        return RunSecondSetUnloadAsync(
            carouselStations,
            pickupCompletion,
            cancellationToken);
    }

    private async Task RunSecondSetUnloadAsync(
        CarouselStationState[] carouselStations,
        TaskCompletionSource<bool> pickupCompletion,
        CancellationToken cancellationToken)
    {
        try
        {
            var pickWithNozzle2 = carouselStations[SecondSetNozzle2UnloadStation].Occupied;
            var pickWithNozzle1 = carouselStations[SecondSetNozzle1UnloadStation].Occupied;
            var zPositions = GetProductionZPositions();
            var nozzle1ZPositions = zPositions.Resolve(VisionCalibrationAxisSet.Second, 1);
            var nozzle2ZPositions = zPositions.Resolve(VisionCalibrationAxisSet.Second, 2);
            var xyPositions = GetSecondSetXyPositions();
            var binDropPositions = GetBinDropPositions();
            var nozzle2Bin = pickWithNozzle2
                ? carouselStations[SecondSetNozzle2UnloadStation].Bin
                : null;
            var nozzle1Bin = pickWithNozzle1
                ? carouselStations[SecondSetNozzle1UnloadStation].Bin
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
                await PickSecondSetNozzleFromStationAsync(
                    2,
                    SecondSetNozzle2UnloadStation,
                    nozzle2ZPositions.Pickup,
                    nozzle2ZPositions.Safe,
                    cancellationToken);
                await WaitIfProductionPausedAsync(cancellationToken);
            }

            if (pickWithNozzle1)
            {
                await MoveSecondSetUnloadAxesToAsync(
                    "吸嘴1取14工位",
                    xyPositions.Position2X,
                    xyPositions.Position2Y,
                    cancellationToken);
                await WaitIfProductionPausedAsync(cancellationToken);
                await PickSecondSetNozzleFromStationAsync(
                    1,
                    SecondSetNozzle1UnloadStation,
                    nozzle1ZPositions.Pickup,
                    nozzle1ZPositions.Safe,
                    cancellationToken);
                await WaitIfProductionPausedAsync(cancellationToken);
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
        CancellationToken cancellationToken)
    {
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
            SecondSetProductionPeerAxisNos);
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
                allowedMovingAxisNos: SecondSetProductionPeerAxisNos,
                yVelocityOverride: yVelocity);
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
                "接触稳定中…",
                Color.FromRgb(98, 181, 255));
        }
        await Task.Delay(DefaultTestStationDwellMilliseconds, cancellationToken);

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
                    measurement.Passed
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

        SetStartProductionStatus(
            $"{string.Join("，", stations)} 已收到有效返回并上抬到等待位，DD可继续下一步。",
            Color.FromRgb(73, 209, 125));

        foreach (var measurementResult in measurementResults)
        {
            carouselStations[measurementResult.Key].SetTested(measurementResult.Value.Bin);
            SetTestStationRuntimeDisplay(
                measurementResult.Key,
                $"测试完成 · {measurementResult.Value.StatusDescription}",
                measurementResult.Value.Bin,
                measurementResult.Value.DisplayText,
                measurementResult.Value.Passed
                    ? Color.FromRgb(73, 209, 125)
                    : Color.FromRgb(242, 122, 128));
        }

        UpdateCarouselStationDisplay(carouselStations);
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
            switch (settings.Instrument)
            {
                case TestStationInstrument.E4981A:
                {
                    var result = await connectionController.MeasureE4981AAsync(cancellationToken);
                    if (result.Bin is null)
                    {
                        throw new InvalidOperationException(
                            "E4981A未返回比较器BIN，请在连接配置页启用仪表比较器并配置BIN范围。");
                    }

                    var bin = !result.IsSuccessful
                        ? "BIN0"
                        : result.Bin is >= 0 and <= 3
                            ? $"BIN{result.Bin}"
                            : "BIN0";
                    return new TestStationMeasurementResult(
                        bin,
                        $"C={result.CapacitancePf:0.######}pF · D={result.DissipationFactor:G6} · {bin}",
                        result.StatusDescription,
                        result.IsSuccessful &&
                        !string.Equals(bin, "BIN0", StringComparison.OrdinalIgnoreCase));
                }
                case TestStationInstrument.SM7110:
                {
                    var result = await connectionController.MeasureSM7110Async(cancellationToken);
                    var bin = stationState.Bin;
                    if (string.IsNullOrWhiteSpace(bin))
                    {
                        throw new InvalidOperationException(
                            "产品尚无E4981A分BIN结果，SM7110不能生成或替代分BIN结果。");
                    }

                    return new TestStationMeasurementResult(
                        bin,
                        $"{result.MeasurementMode}={result.Value:G9}{result.Unit} · E4981A:{bin}",
                        result.StatusDescription,
                        result.IsSuccessful);
                }
                default:
                    throw new InvalidOperationException($"{stationNumber}号工位未分配仪表。");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            SetTestStationRuntimeDisplay(
                stationNumber,
                "仪表通讯异常",
                "异常",
                exception.Message,
                Color.FromRgb(242, 122, 128));
            throw new InvalidOperationException(
                $"{stationNumber}号工位的{FormatTestStationInstrument(settings.Instrument ?? TestStationInstrument.None)}测试失败：{exception.Message}",
                exception);
        }
    }

    private void AdvanceCarouselOccupancy(CarouselStationState[] carouselStations)
    {
        var station16 = carouselStations[CarouselStationCount];
        for (var station = CarouselStationCount; station >= 2; station--)
        {
            carouselStations[station] = carouselStations[station - 1];
        }

        carouselStations[1] = station16;
        _carouselVisualStepOffset = (_carouselVisualStepOffset + 1) % CarouselStationCount;
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
            var slot = CarouselStationCardSlots[(index + _carouselVisualStepOffset) % CarouselStationCount];
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
                card.Background = new SolidColorBrush(
                    state.Tested ? Color.FromRgb(21, 61, 47) : Color.FromRgb(53, 45, 24));
                card.BorderBrush = new SolidColorBrush(
                    state.Tested ? Color.FromRgb(54, 196, 106) : Color.FromRgb(231, 163, 43));
                if (resultText is not null)
                {
                    resultText.Text = state.Tested ? "已测试" : CarouselStatusLoaded;
                    resultText.Foreground = new SolidColorBrush(
                        state.Tested ? Color.FromRgb(73, 209, 125) : Color.FromRgb(242, 181, 68));
                }

                if (objectText is not null)
                {
                    objectText.Text = state.Tested
                        ? state.Bin ?? "BIN?"
                        : TestStationAxisByStation.ContainsKey(station) ? "待测试" : "有物体";
                    objectText.Foreground = new SolidColorBrush(
                        state.Tested ? Color.FromRgb(194, 246, 214) : Color.FromRgb(229, 214, 175));
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
        public bool Occupied { get; private set; }

        public bool Tested { get; private set; }

        public string? Bin { get; private set; }

        public static CarouselStationState Empty() => new();

        public void SetLoaded()
        {
            Occupied = true;
            Tested = false;
            Bin = null;
        }

        public void SetTested(string bin)
        {
            Occupied = true;
            Tested = true;
            Bin = bin;
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
    /// 按 VisionMaster 原始结果顺序最多取10条，每两个组成一批：
    /// 每批第一个给吸嘴1，第二个给吸嘴2。奇数最后1条不缓存，留在盘中震动后重拍。
    /// 全部目标都使用同一次拍照时的轴绝对位置换算。
    /// </summary>
    private IReadOnlyList<NozzlePickupBatch> CalculateAssignedNozzleBatches(
        VisionRectangleBlobResult blobResult,
        string calibrationFilePath,
        double captureX,
        double captureY)
    {
        _ = EnsureFirstSetToolsReady();
        if (blobResult.ImageWidth <= 0 || blobResult.ImageHeight <= 0)
        {
            throw new InvalidOperationException("本次Blob结果没有有效图像尺寸，无法计算吸嘴目标。");
        }

        var selectedCount = Math.Min(blobResult.Rectangles.Count, MaxCachedChipCount);
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
            captureY);
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
        _nextAssignedNozzleMoveStep = targets.Nozzle1.HasValue ? 1 : 2;
        UpdateAssignedNozzleVisionText();
        var assignmentMessage = targets switch
        {
            { Nozzle1: { } nozzle1, Nozzle2: { } nozzle2 } =>
                $"本批按脚本顺序分配2颗：本批第1条→吸嘴1(R={nozzle1.RotationDegrees:0.###}°)，" +
                $"本批第2条→吸嘴2(R={nozzle2.RotationDegrees:0.###}°)；" +
                $"本次拍照缓存还剩 {remainingChipCount} 颗。",
            { Nozzle1: null, Nozzle2: { } nozzle2 } =>
                $"连续震动{EmptyTraySingleChipVibrationThreshold}次后仍剩1颗：" +
                $"改由吸嘴2收尾(R={nozzle2.RotationDegrees:0.###}°)，放到位置2后停机。",
            _ => throw new InvalidOperationException("本批没有可用的吸嘴目标。")
        };
        SetFirstSetPositionStatus(assignmentMessage, true);
        UpdateAssignedNozzleButtonText();
        UpdateHomeCommandState();
    }

    private void ClearAssignedNozzleTargets()
    {
        _blob1Nozzle1Target = null;
        _blob2Nozzle2Target = null;
        _nextAssignedNozzleMoveStep = 0;
        UpdateAssignedNozzleVisionText();
        UpdateAssignedNozzleButtonText();
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
            : _nextAssignedNozzleMoveStep == 0
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

    /// <summary>
    /// 正常生产由“开始运行”自动连续执行两个步骤；此按钮仅用于异常后的当前步骤重试。
    /// </summary>
    private async void MoveAssignedNozzle_Click(object sender, RoutedEventArgs e)
    {
        if (_assignedNozzleMoveRunning ||
            _presetPositionMoveRunning ||
            _startSequenceRunning)
        {
            return;
        }

        var step = _nextAssignedNozzleMoveStep;
        if (step is not (1 or 2))
        {
            SetFirstSetPositionStatus("请先点击“开始运行”完成Blob识别和吸嘴分配。", false);
            return;
        }

        try
        {
            _assignedNozzleMoveRunning = true;
            UpdateHomeCommandState();
            await MoveAssignedNozzleStepAsync(step, CancellationToken.None);
        }
        catch (Exception exception)
        {
            var nozzleName = step == 1 ? "吸嘴1" : "吸嘴2";
            SetFirstSetPositionStatus($"{nozzleName}对位失败：{exception.Message}", false);
        }
        finally
        {
            _assignedNozzleMoveRunning = false;
            UpdateAssignedNozzleButtonText();
            UpdateHomeCommandState();
        }
    }

    private async Task MoveAssignedNozzleStepAsync(int step, CancellationToken cancellationToken)
    {
        var target = step switch
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

        var nozzleName = step == 1 ? "吸嘴1" : "吸嘴2";
        var objectName = step == 1 ? "物体1" : "物体2";
        MoveAssignedNozzleTitleText.Text = $"{nozzleName}移动中";
        MoveAssignedNozzleHintText.Text = $"正在自动对位{objectName}…";
        SetFirstSetPositionStatus(
            $"正在移动{nozzleName}到{objectName}：X={target.Value.X:0.###}，Y={target.Value.Y:0.###}",
            true);

        var current = motionController.CaptureCalibrationCenter(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            AllowedProductionPeerAxisNos);
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
            allowedMovingAxisNos: AllowedProductionPeerAxisNos,
            yVelocityOverride: yVelocity);

        if (step == 1)
        {
            _nextAssignedNozzleMoveStep = 2;
            SetFirstSetPositionStatus(
                $"吸嘴1已到物体1({actual.ActualX:0.###}, {actual.ActualY:0.###})；即将自动对位吸嘴2。",
                true);
        }
        else
        {
            _nextAssignedNozzleMoveStep = 3;
            SetFirstSetPositionStatus(
                $"吸嘴2已到物体2({actual.ActualX:0.###}, {actual.ActualY:0.###})；两次顺序对位完成。",
                true);
        }

        UpdateAssignedNozzleButtonText();
        UpdateHomeCommandState();
    }

    public Task<MotionAxisSnapshot> RotateDdOnceAsync(CancellationToken cancellationToken)
    {
        if (_startSequenceRunning ||
            _presetPositionMoveRunning ||
            _oneKeyResetRunning ||
            _assignedNozzleMoveRunning)
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
            _startSequenceRunning ||
            _assignedNozzleMoveRunning)
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
            _startSequenceRunning || _assignedNozzleMoveRunning)
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
            _startSequenceRunning ||
            _assignedNozzleMoveRunning)
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
            _startSequenceRunning ||
            _assignedNozzleMoveRunning)
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
        CancellationToken cancellationToken)
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
            AllowedProductionPeerAxisNos);
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
            allowedMovingAxisNos: AllowedProductionPeerAxisNos,
            yVelocityOverride: yVelocity);
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
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(targetX) || !double.IsFinite(targetY))
        {
            throw new ArgumentOutOfRangeException(nameof(targetX), $"{positionName}的XY目标必须是有效数字。");
        }

        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        ApplyCurrentProductionAxisMotionSettings(motionController);

        // 找芯片脚本返回的是正方形某条边相对水平线的当前姿态，不是电机相对量。
        // 正方形边方向每90°等价：先求到统一水平/垂直方向的最短校正角，
        // 再从本批取料R基准构造绝对目标。
        var absoluteTargets = new Dictionary<int, double>
        {
            [VisionCalibrationService.FirstSetXHardwareAxisNo] = targetX,
            [VisionCalibrationService.FirstSetYHardwareAxisNo] = targetY
        };

        double? nozzle1RPulses = null;
        double? nozzle1CorrectionDegrees = null;
        double? nozzle1TargetR = null;
        if (pickupBatch.Nozzle1 is { } nozzle1PickupTarget)
        {
            nozzle1RPulses = ConvertUpperCameraMeasuredAngleToRCorrectionPulses(
                nozzle1PickupTarget.RotationDegrees,
                GetUpperCameraRotationSign(1),
                out var correctionDegrees);
            nozzle1CorrectionDegrees = correctionDegrees;
            nozzle1TargetR = pickupRPositions.ActualX + nozzle1RPulses.Value;
            absoluteTargets[FirstSetNozzle1RHardwareAxisNo] = nozzle1TargetR.Value;
        }

        double? nozzle2RPulses = null;
        double? nozzle2CorrectionDegrees = null;
        double? nozzle2TargetR = null;
        if (pickupBatch.Nozzle2 is { } nozzle2Target)
        {
            nozzle2RPulses = ConvertUpperCameraMeasuredAngleToRCorrectionPulses(
                nozzle2Target.RotationDegrees,
                GetUpperCameraRotationSign(2),
                out var correctionDegrees);
            nozzle2CorrectionDegrees = correctionDegrees;
            nozzle2TargetR = pickupRPositions.ActualY + nozzle2RPulses.Value;
            absoluteTargets[FirstSetNozzle2RHardwareAxisNo] = nozzle2TargetR.Value;
        }

        SetFirstSetPositionStatus(
            $"正在同步移动{positionName}并将已取芯片粗校正到0°：X={targetX:0.###}，Y={targetY:0.###}" +
            (nozzle1RPulses.HasValue
                ? $"，吸嘴1 测量{pickupBatch.Nozzle1!.Value.RotationDegrees:0.###}°" +
                  $"→校正{nozzle1CorrectionDegrees!.Value:0.###}°" +
                  $"（R目标{nozzle1TargetR!.Value:0.###} pulse）"
                : string.Empty) +
            (nozzle2RPulses.HasValue
                ? $"，吸嘴2 测量{pickupBatch.Nozzle2!.Value.RotationDegrees:0.###}°" +
                  $"→校正{nozzle2CorrectionDegrees!.Value:0.###}°" +
                  $"（R目标{nozzle2TargetR!.Value:0.###} pulse）"
                : string.Empty) +
            "…",
            true);

        var movingAxisNos = absoluteTargets.Keys.ToArray();
        await motionController.MoveAxesSynchronizedAsync(
            absoluteTargets,
            new Dictionary<int, double>(),
            cancellationToken,
            allowedMovingAxisNos: AllowedProductionPeerAxisNos,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverrides: GetProductionAxisVelocities(movingAxisNos));

        var actualXy = motionController.CaptureCalibrationFeedback(
            VisionCalibrationService.FirstSetXHardwareAxisNo,
            VisionCalibrationService.FirstSetYHardwareAxisNo,
            AllowedProductionPeerAxisNos);
        SetFirstSetPositionStatus(
            $"{positionName}与芯片统一角度粗校正已同步完成：" +
            $"X={actualXy.ActualX:0.###}，Y={actualXy.ActualY:0.###}，" +
            (nozzle1RPulses.HasValue
                ? $"R1校正{nozzle1CorrectionDegrees!.Value:0.###}°/{nozzle1RPulses.Value:0.###} pulse"
                : "R1无料") +
            (nozzle2RPulses.HasValue
                ? $"，R2校正{nozzle2CorrectionDegrees!.Value:0.###}°/" +
                  $"{nozzle2RPulses.Value:0.###} pulse。"
                : "。"),
            true);
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
        return new ProductionZDwellTimes(
            ParseMilliseconds(
                VacuumPickupDwellTextBox.Text,
                "吸料停留时间"),
            ParseMilliseconds(
                VacuumBreakPulseTextBox.Text,
                "破真空停留时间"),
            ParseMilliseconds(
                VacuumValveSwitchDelayTextBox.Text,
                "真空阀切换间隔"));
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
            settingsByAxis[definition.AxisNo] = settings;
        }

        return settingsByAxis;
    }

    private IReadOnlyDictionary<int, TestStationSettings> ReadTestStationSettings()
    {
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

    private TestStationSettings GetTestStationSettings(int stationNumber)
    {
        var current = _testStationSettings ?? ReadTestStationSettings();
        return current.GetValueOrDefault(stationNumber)
               ?? throw new InvalidOperationException($"{stationNumber}号测试站参数不存在。");
    }

    private void EnsureAssignedTestInstrumentsConnected()
    {
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
            return true;
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private void SaveTestStationParametersFromInputs()
    {
        if (_loadingPresetPositions)
        {
            return;
        }

        try
        {
            var stationSettings = ReadTestStationSettings();
            _homeSettings.TestStationSettings = stationSettings.ToDictionary(
                pair => pair.Key,
                pair => pair.Value);
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (ArgumentException)
        {
            // 输入尚未完成时等待用户继续编辑。
        }
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"保存测试站位置失败：{exception.Message}", false);
        }
    }

    private void SaveProductionAxisParametersFromInputs()
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
        catch (ArgumentException)
        {
            // 输入尚未完成时只保持按钮禁用，等待用户继续编辑。
        }
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"保存生产轴参数失败：{exception.Message}", false);
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

    private void SaveProductionZPositionsFromInputs()
    {
        if (_loadingPresetPositions ||
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
            !TryParseMilliseconds(VacuumValveSwitchDelayTextBox.Text, out var valveSwitchDelay))
        {
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
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"保存Z轴高度或停留时间失败：{exception.Message}", false);
        }
    }

    private void SaveSecondSetXyPositionsFromInputs()
    {
        if (_loadingPresetPositions ||
            SecondSetPosition1XTextBox is null ||
            SecondSetPosition1YTextBox is null ||
            SecondSetPosition2XTextBox is null ||
            SecondSetPosition2YTextBox is null ||
            !TryParseCoordinate(SecondSetPosition1XTextBox.Text, out var position1X) ||
            !TryParseCoordinate(SecondSetPosition1YTextBox.Text, out var position1Y) ||
            !TryParseCoordinate(SecondSetPosition2XTextBox.Text, out var position2X) ||
            !TryParseCoordinate(SecondSetPosition2YTextBox.Text, out var position2Y))
        {
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
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"保存第二套XY下料位置失败：{exception.Message}", false);
        }
    }

    private void SaveBinPositionsFromInputs()
    {
        if (_loadingPresetPositions ||
            Bin0PositionXTextBox is null ||
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
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"保存BIN分料位置失败：{exception.Message}", false);
        }
    }

    private void SavePresetPositionsFromInputs()
    {
        if (_loadingPresetPositions ||
            PresetPosition1XTextBox is null ||
            PresetPosition1YTextBox is null ||
            PresetPosition2XTextBox is null ||
            PresetPosition2YTextBox is null ||
            !TryParseOptionalCoordinate(PresetPosition1XTextBox.Text, out var position1X) ||
            !TryParseOptionalCoordinate(PresetPosition1YTextBox.Text, out var position1Y) ||
            !TryParseOptionalCoordinate(PresetPosition2XTextBox.Text, out var position2X) ||
            !TryParseOptionalCoordinate(PresetPosition2YTextBox.Text, out var position2Y))
        {
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
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"保存绝对位置失败：{exception.Message}", false);
        }
    }

    private void SaveFirstSetTeachingPositionsFromInputs()
    {
        if (_loadingPresetPositions ||
            FirstSetTeachingCenterXTextBox is null ||
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
        catch (Exception exception)
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

    private async void OneKeyReset_Click(object sender, RoutedEventArgs e)
    {
        if (_oneKeyResetRunning ||
            _startSequenceRunning ||
            _presetPositionMoveRunning ||
            _assignedNozzleMoveRunning)
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
            MoveAssignedNozzleButton is null ||
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
            LowerCameraPhotoPosition1XTextBox is null ||
            LowerCameraPhotoPosition1YTextBox is null ||
            LowerCameraPhotoPosition2XTextBox is null ||
            LowerCameraPhotoPosition2YTextBox is null ||
            LowerCameraNozzle1RotationCenterXTextBox is null ||
            LowerCameraNozzle1RotationCenterYTextBox is null ||
            LowerCameraNozzle2RotationCenterXTextBox is null ||
            LowerCameraNozzle2RotationCenterYTextBox is null ||
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
            !_startSequenceRunning &&
            !_assignedNozzleMoveRunning;
        var allProductionAxisParametersValid = AllProductionAxisParametersValid();
        var allTestStationParametersValid = AllTestStationParametersValid();
        var allZPositionsValid =
            TryParseCoordinate(FirstSetNozzle1PickupZPositionTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetNozzle1DropZPositionTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetNozzle1SafeZPositionTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetNozzle2PickupZPositionTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetNozzle2DropZPositionTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetNozzle2SafeZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetNozzle1PickupZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetNozzle1DropZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetNozzle1SafeZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetNozzle2PickupZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetNozzle2DropZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetNozzle2SafeZPositionTextBox.Text, out _) &&
            TryParseMilliseconds(VacuumPickupDwellTextBox.Text, out _) &&
            TryParseMilliseconds(VacuumBreakPulseTextBox.Text, out _) &&
            TryParseMilliseconds(VacuumValveSwitchDelayTextBox.Text, out _);
        var allSecondSetXyPositionsValid =
            TryParseCoordinate(SecondSetPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition1YTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition2XTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition2YTextBox.Text, out _);
        var allLowerCameraPhotoPositionsValid =
            TryParseCoordinate(LowerCameraPhotoPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(LowerCameraPhotoPosition1YTextBox.Text, out _) &&
            TryParseCoordinate(LowerCameraPhotoPosition2XTextBox.Text, out _) &&
            TryParseCoordinate(LowerCameraPhotoPosition2YTextBox.Text, out _);
        var allLowerCameraRotationCentersValid =
            TryParseCoordinate(LowerCameraNozzle1RotationCenterXTextBox.Text, out _) &&
            TryParseCoordinate(LowerCameraNozzle1RotationCenterYTextBox.Text, out _) &&
            TryParseCoordinate(LowerCameraNozzle2RotationCenterXTextBox.Text, out _) &&
            TryParseCoordinate(LowerCameraNozzle2RotationCenterYTextBox.Text, out _);
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
            visionControllersReady &&
            allProductionAxisParametersValid &&
            allTestStationParametersValid &&
            allZPositionsValid &&
            allSecondSetXyPositionsValid &&
            allLowerCameraPhotoPositionsValid &&
            allLowerCameraRotationCentersValid &&
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
        MoveAssignedNozzleButton.IsEnabled =
            visionControllersReady &&
            commandsIdle &&
            allProductionAxisParametersValid &&
            ((_nextAssignedNozzleMoveStep == 1 && _blob1Nozzle1Target is not null) ||
              (_nextAssignedNozzleMoveStep == 2 && _blob2Nozzle2Target is not null));
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

    private void UpdateAssignedNozzleButtonText()
    {
        if (MoveAssignedNozzleTitleText is null || MoveAssignedNozzleHintText is null)
        {
            return;
        }

        switch (_nextAssignedNozzleMoveStep)
        {
            case 1:
                MoveAssignedNozzleTitleText.Text = "吸嘴1 → 物体1";
                MoveAssignedNozzleHintText.Text = "自动步骤1；失败时可点击重试";
                break;
            case 2:
                MoveAssignedNozzleTitleText.Text = "吸嘴2 → 物体2";
                MoveAssignedNozzleHintText.Text = "自动步骤2；失败时可点击重试";
                break;
            case 3:
                MoveAssignedNozzleTitleText.Text = "顺序对位完成";
                MoveAssignedNozzleHintText.Text = "重新开始识别后可再次执行";
                break;
            default:
                MoveAssignedNozzleTitleText.Text = "自动顺序对位";
                MoveAssignedNozzleHintText.Text = "开始运行后自动执行";
                break;
        }
    }

    private void SetStartProductionStatus(string message, Color color)
    {
        StartProductionHintText.Text = message;
        StartProductionHintText.ToolTip = message;
        StartProductionHintText.Foreground = new SolidColorBrush(color);
    }

    private void SaveLowerCameraPhotoPositionsFromInputs()
    {
        if (_loadingPresetPositions ||
            LowerCameraPhotoPosition1XTextBox is null ||
            LowerCameraPhotoPosition1YTextBox is null ||
            LowerCameraPhotoPosition2XTextBox is null ||
            LowerCameraPhotoPosition2YTextBox is null ||
            !TryParseOptionalCoordinate(LowerCameraPhotoPosition1XTextBox.Text, out var position1X) ||
            !TryParseOptionalCoordinate(LowerCameraPhotoPosition1YTextBox.Text, out var position1Y) ||
            !TryParseOptionalCoordinate(LowerCameraPhotoPosition2XTextBox.Text, out var position2X) ||
            !TryParseOptionalCoordinate(LowerCameraPhotoPosition2YTextBox.Text, out var position2Y))
        {
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
        catch (Exception exception)
        {
            SetLowerCameraPhotoPositionStatus(
                $"保存下相机拍照位失败：{exception.Message}",
                false);
        }
    }

    private void SaveLowerCameraRotationCentersFromInputs()
    {
        if (_loadingPresetPositions ||
            LowerCameraNozzle1RotationCenterXTextBox is null ||
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
        catch (Exception exception)
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
        if (_uphStopwatch.IsRunning || _uphStopwatch.Elapsed > TimeSpan.Zero)
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
            ? "从首次取料开始统计"
            : $"从首次取料开始 · 已完成 {_uphCompletedUnitCount} 件 · " +
              $"累计 {_uphStopwatch.Elapsed:hh\\:mm\\:ss}";
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
            $"下相机纠偏 · 吸嘴{nozzleNumber} · 图像源1";
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
        if (result.ImageWidth <= 0 ||
            result.ImageHeight <= 0 ||
            string.IsNullOrWhiteSpace(result.ImagePath))
        {
            BlobInspectionImageStatusText.Text = "XY已显示 · 本次未返回检测图";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(242, 181, 68));
            return;
        }

        if (BlobInspectionVisionDisplayHost.Visibility == Visibility.Visible)
        {
            try
            {
                File.Delete(result.ImagePath);
            }
            catch
            {
            }

            BlobInspectionImageStatusText.Text =
                $"找芯片流程 · {result.Rectangles.Count}颗 · 图像源1 · {result.ImageWidth}×{result.ImageHeight}";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(73, 209, 125));
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
            ? coordinate.ToString("0.###", CultureInfo.CurrentCulture)
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
        int ValveSwitchDelayMilliseconds);

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
        bool Passed);

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
                "BIN0" => Bin0,
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
