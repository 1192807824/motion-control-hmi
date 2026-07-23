using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
    private const string ChipInspectionBlobModuleName = "Blob分析1";
    private const int FirstSetZ1VacuumOutputBit = 15;
    private const int FirstSetZ1BreakVacuumOutputBit = 14;
    private const int FirstSetZ2VacuumOutputBit = 17;
    private const int FirstSetZ2BreakVacuumOutputBit = 16;
    private const int SecondSetZ1BreakVacuumOutputBit = 18;
    private const int SecondSetZ1VacuumOutputBit = 19;
    private const int SecondSetZ2VacuumControlOutputBit = 21;
    private const int Station12BreakVacuumOutputBit = 22;
    private const int Station13BreakVacuumOutputBit = 23;
    private const int VacuumBreakPulseMilliseconds = 150;
    private const int VacuumPickupDwellMilliseconds = 500;
    private const int FirstSetNozzle1ZHardwareAxisNo = 5;
    private const int FirstSetNozzle2ZHardwareAxisNo = 7;
    private const int SecondSetNozzle1ZHardwareAxisNo = 9;
    private const int SecondSetNozzle2ZHardwareAxisNo = 11;
    private const double DefaultNozzlePickupZPosition = 30_000d;
    private const double DefaultNozzleDropZPosition = 4_800d;
    private const double DefaultNozzleSafeZPosition = -5_000d;
    private const double NozzleZVelocity = 10_000d;
    private const double DdMotorPulsePerTurn = 22_500d;
    private const double Axis0Velocity = 10_000d;
    private const double HomePageCompletionTolerance = 100d;
    private const double MoveOutAbsolutePosition = 250_000d;
    private const int SecondSetNozzle2UnloadStation = 12;
    private const int SecondSetNozzle1UnloadStation = 13;
    private const double FirstSetXyVelocity = 100_000d;
    private const double SecondSetXyVelocity = 100_000d;
    private const double DefaultSecondSetPickupPosition1X = 1_606_631d;
    private const double DefaultSecondSetPickupPosition1Y = 330_321d;
    private const double DefaultSecondSetPickupPosition2X = 1_606_271d;
    private const double DefaultSecondSetPickupPosition2Y = -222_828d;
    private const double SecondSetNozzle1DropX = 592_474d;
    private const double SecondSetNozzle1DropY = 204_106d;
    private const double SecondSetNozzle2DropX = 592_498d;
    private const double SecondSetNozzle2DropY = 1_374_787d;
    private const int TestStationMoveTimeoutMilliseconds = 60_000;
    private const double TestStationPressVelocity = 800_000d;
    private const int CarouselStationCount = 16;
    private const int TestStationHomeMode = 21;
    private const double TestStationHomeVelocity = 600_000d;
    private const double TestStationHomeOffsetPosition = 0d;
    private const int TestStationDwellMilliseconds = 100;
    private const int MoveAwayBeforeDdMilliseconds = 500;
    private const string CarouselStatusLoaded = "有料";
    private const string CarouselStatusPressing = "下压";
    private const string CarouselStatusDwelling = "停留";
    private const string CarouselStatusHoming = "回原";
    private static readonly int[] MoveOutAxisNos = [13, 14, 15];
    private static readonly int[] FirstSetAxisNos =
        [VisionCalibrationService.FirstSetXHardwareAxisNo, VisionCalibrationService.FirstSetYHardwareAxisNo];
    private static readonly int[] SecondSetAxisNos =
        [VisionCalibrationService.SecondSetXHardwareAxisNo, VisionCalibrationService.SecondSetYHardwareAxisNo];
    private static readonly int[] FirstSetProductionPeerAxisNos =
        [0, .. SecondSetAxisNos, .. MoveOutAxisNos];
    private static readonly int[] SecondSetProductionPeerAxisNos =
        [0, .. FirstSetAxisNos, .. MoveOutAxisNos];
    private static readonly int[] ProductionXyAxisNos =
        [.. FirstSetAxisNos, .. SecondSetAxisNos];
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
    private readonly VisionCalibrationService _visionCalibration = VisionCalibrationService.Shared;
    private readonly HomePageSettingsStore _homeSettingsStore = new();
    private HomePageSettings _homeSettings = new();
    private MotionControlPage? _motionController;
    private VisualCalibrationPage? _visualCalibrationController;
    private bool _presetPositionMoveRunning;
    private bool _oneKeyResetRunning;
    private bool _startSequenceRunning;
    private bool _assignedNozzleMoveRunning;
    private CancellationTokenSource? _productionCancellation;
    private TaskCompletionSource<bool>? _productionCompletion;
    private bool _productionStopRequested;
    private VisionCalibrationAxisSet? _productionAxisSet;
    private ProductionZPositions? _productionZPositions;
    private SecondSetXyPositions? _secondSetXyPositions;
    private readonly bool[,] _nozzleVacuumEnabledBySet = new bool[2, 3];
    private VisionMotionTarget? _blob1Nozzle1Target;
    private VisionMotionTarget? _blob2Nozzle2Target;
    private int _nextAssignedNozzleMoveStep;
    private int _carouselVisualStepOffset;
    private bool _loadingPresetPositions = true;

    public HomePage()
    {
        InitializeComponent();
        LoadPresetPositions();
        UpdateCarouselStationDisplay(CreateCarouselStationStates());
    }

    public void AttachMotionController(MotionControlPage motionController)
    {
        _motionController = motionController ?? throw new ArgumentNullException(nameof(motionController));
        UpdateHomeCommandState();
    }

    public void AttachVisionCalibrationController(VisualCalibrationPage visualCalibrationController)
    {
        _visualCalibrationController = visualCalibrationController
            ?? throw new ArgumentNullException(nameof(visualCalibrationController));
        UpdateHomeCommandState();
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
        if (vacuumEnabled && breakVacuumEnabled)
        {
            throw new ArgumentException("同一个吸嘴不能同时开启真空吸和真空破。");
        }

        var outputSet = false;
        Exception? firstFailure = null;
        try
        {
            if (nozzleNumber == 1)
            {
                // 第二套 Z1：Y18=1 为破真空，Y19=1 为真空吸。
                // 切换状态时先关闭相反输出，避免两个电磁阀短暂同时得电。
                var oppositeSet = vacuumEnabled
                    ? motionController.SetDigitalOutputHardwareBit(
                        SecondSetZ1BreakVacuumOutputBit,
                        false)
                    : motionController.SetDigitalOutputHardwareBit(
                        SecondSetZ1VacuumOutputBit,
                        false);
                var requestedSet = vacuumEnabled
                    ? motionController.SetDigitalOutputHardwareBit(
                        SecondSetZ1VacuumOutputBit,
                        true)
                    : motionController.SetDigitalOutputHardwareBit(
                        SecondSetZ1BreakVacuumOutputBit,
                        breakVacuumEnabled);
                outputSet = oppositeSet & requestedSet;
            }
            else if (nozzleNumber == 2)
            {
                // 第二套 Z2 使用单点换向：Y21=1 为吸，Y21=0 为破。
                outputSet = motionController.SetDigitalOutputHardwareBit(
                    SecondSetZ2VacuumControlOutputBit,
                    vacuumEnabled);
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(nozzleNumber), "吸嘴编号只能是 1 或 2。");
            }
        }
        catch (Exception exception)
        {
            firstFailure = exception;
        }

        if (firstFailure is not null)
        {
            var expectedOutput = nozzleNumber == 1
                ? $"破Y{SecondSetZ1BreakVacuumOutputBit:00}={(breakVacuumEnabled ? 1 : 0)}，吸Y{SecondSetZ1VacuumOutputBit:00}={(vacuumEnabled ? 1 : 0)}"
                : $"Y{SecondSetZ2VacuumControlOutputBit:00}={(vacuumEnabled ? 1 : 0)}";
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
        var positions = GetProductionZPositions();
        var pickupPosition = axisSet == VisionCalibrationAxisSet.First
            ? positions.FirstSetPickup
            : positions.SecondSetPickup;
        var safePosition = axisSet == VisionCalibrationAxisSet.First
            ? positions.FirstSetSafe
            : positions.SecondSetSafe;
        await PickWithNozzleAsync(
            axisSet,
            nozzleNumber,
            pickupPosition,
            safePosition,
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

        SetFirstSetPositionStatus(
            $"Z{nozzleNumber}真空吸已开启，保持 {VacuumPickupDwellMilliseconds} ms 等待吸附稳定…",
            true);
        await Task.Delay(VacuumPickupDwellMilliseconds, cancellationToken);

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
        var positions = GetProductionZPositions();
        var dropPosition = axisSet == VisionCalibrationAxisSet.First
            ? positions.FirstSetDrop
            : positions.SecondSetDrop;
        var safePosition = axisSet == VisionCalibrationAxisSet.First
            ? positions.FirstSetSafe
            : positions.SecondSetSafe;
        await PlaceWithNozzleAsync(
            axisSet,
            nozzleNumber,
            dropPosition,
            safePosition,
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
        var zHardwareAxisNo = GetNozzleZHardwareAxisNo(axisSet, nozzleNumber);
        SetFirstSetPositionStatus(
            $"Z{nozzleNumber}正在移动到{positionName}{targetPosition:0.###} pulse，速度 {NozzleZVelocity:0.###}…",
            true);

        var result = await motionController.MoveAxesAbsoluteAsync(
            new Dictionary<int, double> { [zHardwareAxisNo] = targetPosition },
            cancellationToken,
            allowedMovingAxisNos: GetProductionPeerAxisNos(axisSet),
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: NozzleZVelocity);
        var actual = result.Single();
        SetFirstSetPositionStatus(
            $"Z{nozzleNumber}已到{positionName}：{actual.FeedbackPosition:0.###} pulse。",
            true);
    }

    private async Task EnsureActiveSetNozzlesAtSafeZAsync(CancellationToken cancellationToken)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        var axisSet = _productionAxisSet ?? _visionCalibration.ActiveAxisSet;
        var positions = GetProductionZPositions();
        var safePosition = axisSet == VisionCalibrationAxisSet.First
            ? positions.FirstSetSafe
            : positions.SecondSetSafe;
        var z1HardwareAxisNo = GetNozzleZHardwareAxisNo(axisSet, 1);
        var z2HardwareAxisNo = GetNozzleZHardwareAxisNo(axisSet, 2);
        SetFirstSetPositionStatus(
            $"XY放料前正在确认 Z1/Z2 的 {safePosition:0.###} 安全位…",
            true);

        await motionController.MoveAxesAbsoluteAsync(
            new Dictionary<int, double>
            {
                [z1HardwareAxisNo] = safePosition,
                [z2HardwareAxisNo] = safePosition
            },
            cancellationToken,
            allowedMovingAxisNos: GetProductionPeerAxisNos(axisSet),
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: NozzleZVelocity);

        SetFirstSetPositionStatus(
            $"Z1/Z2均已到 {safePosition:0.###} pulse 安全位，允许 XY 前往位置1/2。",
            true);
    }

    private ProductionZPositions GetProductionZPositions()
    {
        return _productionZPositions
            ?? throw new InvalidOperationException("本轮生产的 Z 轴高度参数尚未锁定。");
    }

    private SecondSetXyPositions GetSecondSetXyPositions()
    {
        return _secondSetXyPositions
            ?? throw new InvalidOperationException("本轮生产的第二套 XY 取料位置尚未锁定。");
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
        if (!SetNozzleVacuumOutputs(
                axisSet,
                nozzleNumber,
                vacuumEnabled: false,
                breakVacuumEnabled: true))
        {
            throw new InvalidOperationException($"Z{nozzleNumber}真空破开启失败。");
        }

        try
        {
            await Task.Delay(VacuumBreakPulseMilliseconds, cancellationToken);
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
            axisSet == VisionCalibrationAxisSet.Second && nozzleNumber == 2
                ? $"Z2已切换为真空破Y{SecondSetZ2VacuumControlOutputBit:00}=0。"
                : $"Z{nozzleNumber}真空破已脉冲 {VacuumBreakPulseMilliseconds} ms，真空破和真空吸均已关闭。",
            true);
    }

    private void SetUnloadStationBreakVacuum(int stationNumber, bool enabled)
    {
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        var outputBit = stationNumber switch
        {
            SecondSetNozzle2UnloadStation => Station12BreakVacuumOutputBit,
            SecondSetNozzle1UnloadStation => Station13BreakVacuumOutputBit,
            _ => throw new ArgumentOutOfRangeException(
                nameof(stationNumber),
                "下料破真空工位只能是12或13。")
        };

        // 12/13工位的破真空输出为低电平有效：0=打开，1=关闭。
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

            SetFirstSetPositionStatus(
                $"Z{nozzleNumber}真空吸已开启，保持 {VacuumPickupDwellMilliseconds} ms 等待吸附稳定…",
                true);
            await Task.Delay(VacuumPickupDwellMilliseconds, cancellationToken);

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
    /// 每轮由第一套XY向1/2工位上两个新料；12/13同时有料时，第二套XY并行完成双吸嘴收料。
    /// XY回中心后立即准备下一轮物料；DD固定推进两个工位，停稳即可上下料，末次测试并行完成。
    /// </summary>
    private async void StartProduction_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        Task activeSecondSetUnloadTask = Task.CompletedTask;
        Task activeSecondSetPickupTask = Task.CompletedTask;
        Task<CalibrationCenterPosition>? activeFirstSetReturnToCenterTask = null;
        Task<CarouselAdvanceResult>? activeCarouselAdvanceTask = null;
        Task<int> activeFinalTestTask = Task.FromResult(0);

        // 如果当前已经在连续生产，再次点击按钮表示请求停止。
        if (_startSequenceRunning)
        {
            // 下发停止请求，但不阻塞 UI 线程等待生产循环收尾。
            _ = RequestProductionStop();

            // 停止请求已经发出，本次点击不再继续启动新流程。
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

            // 主页生产流程固定使用第一套 XY 轴速度，不受视觉标定页配置影响。
            var velocity = ParseProductionVelocity(
                FirstSetXyVelocityTextBox.Text,
                "轴1/2第一套XY速度");

            // 速度必须是有效正数，否则后续移动超时和下发速度都不可信。
            if (!double.IsFinite(velocity) || velocity <= 0)
            {
                // 用异常中断启动流程，并统一进入下方错误提示。
                throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
            }

            // 生产启动时一次性校验并锁定两套 Z 轴取料、放料和安全高度。
            _productionZPositions = ReadProductionZPositions();

            // 第二套取料前XY位置同样在启动时锁定，运行中修改不会影响当前生产轮次。
            _secondSetXyPositions = ReadSecondSetXyPositions();

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

            // 创建完成信号，方便页面切换或停用时等待生产流程完全收尾。
            _productionCompletion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            // 清空停止标记，表示新的连续生产流程还没有收到停止请求。
            _productionStopRequested = false;

            // 锁定本轮生产所使用的轴组，防止运行中切换视觉页时误写另一套真空 IO。
            _productionAxisSet = _visionCalibration.ActiveAxisSet;

            // 标记连续生产已进入运行状态。
            _startSequenceRunning = true;

            // 刷新主页按钮状态，把“开始运行”切成“停止循环”，并锁住其它会冲突的操作。
            UpdateHomeCommandState();

            // 启动生产前先经过位置2这个安全过渡点。必须严格先走Y、确认到位后再走X，
            // 避免从任意停机位置直接沿XY斜线路径前往拍照取料中心。
            var productionAxes = VisionCalibration;
            var startupSafePosition = await MoveToStartupPosition2SafelyAsync(
                motionController,
                productionAxes.XHardwareAxisNo,
                productionAxes.YHardwareAxisNo,
                position2X,
                position2Y,
                velocity,
                _productionCancellation.Token);
            SetStartProductionStatus(
                $"启动安全定位完成：已按Y后X到达位置2" +
                $"({startupSafePosition.ActualX:0.###}, {startupSafePosition.ActualY:0.###})，准备进入取料流程…",
                Color.FromRgb(73, 209, 125));

            // 从第 0 轮开始计数，进入循环后先自增为第 1 轮。
            var cycleNumber = 0;

            // 转盘工位占料状态。启动时按空盘处理；放料到 1/2 后，后续每次 DD 转动推进一个工位。
            var carouselStations = CreateCarouselStationStates();
            _carouselVisualStepOffset = 0;
            UpdateCarouselStationDisplay(carouselStations);

            // 连续生产会一直循环，直到用户请求停止或流程抛出异常。
            while (true)
            {
                // 每轮开始前先检查是否已经收到停止请求。
                _productionCancellation.Token.ThrowIfCancellationRequested();

                // 记录当前正在执行第几轮，便于状态栏提示和现场排查。
                cycleNumber++;

                // 清空上一轮的 Blob 识别显示，避免操作员误看旧结果。
                ClearBlobInspectionResult();

                // 清空上一轮缓存的吸嘴目标，避免异常重试时使用过期坐标。
                ClearAssignedNozzleTargets();

                CalibrationCenterPosition actual;
                if (activeFirstSetReturnToCenterTask is not null)
                {
                    // 上一轮放料后已经启动回中心；只等XY到拍照位，不等待DD或测试站完成。
                    actual = await activeFirstSetReturnToCenterTask;
                    activeFirstSetReturnToCenterTask = null;
                    SetFirstSetPositionStatus(
                        $"XY已回到中心：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse；DD/测试站可继续并行。",
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
                        velocity);
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
                        allowedMovingAxisNos: AllowedProductionPeerAxisNos);
                }

                // 必须等轴1、轴2均确认到位后，才允许单次执行固定方案中的找芯片流程。
                // 流程名和模块名都采用固定名称，避免误跑标定流程或实时流程。
                SetStartProductionStatus(
                    $"第{cycleNumber}轮：XY已到初始位置({actual.ActualX:0.###}, {actual.ActualY:0.###})，" +
                    $"正在运行{ChipInspectionProcedureName} → {ChipInspectionBlobModuleName}…",
                    Color.FromRgb(242, 181, 68));

                // 确保主页上的 VisionMaster 显示窗口已经切到 Blob 检测画面。
                await PrepareBlobInspectionVisionDisplayAsync(visualCalibrationController);

                // 执行一次矩形 Blob 检测，并把取消令牌传进去保证停止时能退出等待。
                var blobResult = await visualCalibrationController.RunRectangleBlobInspectionAsync(
                    _productionCancellation.Token);

                // 把本次 Blob 识别结果显示到主页，方便操作员确认相机结果。
                SetBlobInspectionResult(blobResult);

                // 先声明吸嘴分配结果，后面 try 成功后再写入。
                DualNozzleMechanicalTargets assignedTargets;

                // 吸嘴目标换算失败时，本轮不能继续移动吸嘴，需要单独给出温和提示。
                try
                {
                    // 两个目标必须在本次拍照位置立即换算并缓存。后续吸嘴1移动后，
                    // 不能再用已经变化的当前轴位置去计算吸嘴2，否则第二个绝对目标会产生偏差。
                    assignedTargets = CalculateAssignedNozzleTargets(
                        blobResult,
                        calibrationFile.FilePath,
                        actual.ActualX,
                        actual.ActualY);
                }
                catch (Exception exception)
                {
                    // 换算失败后清掉吸嘴目标，防止手动重试按钮拿到旧坐标。
                    ClearAssignedNozzleTargets();

                    // 在第一套 XY 状态区显示失败原因。
                    SetFirstSetPositionStatus($"吸嘴分配失败：{exception.Message}", false);

                    // 在连续生产状态区提示 Blob 已经显示，但吸嘴目标换算未通过。
                    SetStartProductionStatus(
                        $"Blob已显示；吸嘴目标换算失败：{exception.Message}",
                        Color.FromRgb(242, 181, 68));

                    // 本轮已经无法安全继续，退出连续生产流程。
                    return;
                }

                // 缓存本轮换算出的吸嘴目标，并同步更新吸嘴对位 UI。
                SetAssignedNozzleTargets(assignedTargets);

                // 提示第 3 步开始：吸嘴1对位物体1，到位后 Z1 下探取料并安全回缩。
                SetStartProductionStatus(
                    $"第{cycleNumber}轮：Blob识别完成，吸嘴1正在对位物体1…",
                    Color.FromRgb(242, 181, 68));

                // 执行吸嘴1对位动作。
                await MoveAssignedNozzleStepAsync(1, _productionCancellation.Token);

                // 吸嘴1到达物体1后，Z1 到取料位、开吸，然后回到安全位。
                await PickWithActiveSetNozzleAsync(1, _productionCancellation.Token);

                // 提示第 4 步开始：吸嘴2对位物体2，到位后 Z2 下探取料并安全回缩。
                SetStartProductionStatus(
                    $"第{cycleNumber}轮：Z1已取料并回到配置安全位，吸嘴2正在对位物体2…",
                    Color.FromRgb(242, 181, 68));

                // 执行吸嘴2对位动作。
                await MoveAssignedNozzleStepAsync(2, _productionCancellation.Token);

                // 吸嘴2到达物体2后，Z2 到取料位、开吸，然后回到安全位。
                await PickWithActiveSetNozzleAsync(2, _productionCancellation.Token);

                // 放料 XY 动作的安全门：必须再次确认两根 Z 轴都在本轮配置的安全高度。
                await EnsureActiveSetNozzlesAtSafeZAsync(_productionCancellation.Token);

                // 拍照和双吸嘴取料不等待DD或测试站；真正放料前只等待DD完成固定两次转动。
                if (activeCarouselAdvanceTask is not null)
                {
                    if (!activeCarouselAdvanceTask.IsCompleted)
                    {
                        SetStartProductionStatus(
                            $"第{cycleNumber}轮：两个料已吸取，正在等待DD完成两次转动；测试站完成状态不阻塞放料…",
                            Color.FromRgb(242, 181, 68));
                    }

                    var carouselAdvanceResult = await activeCarouselAdvanceTask;
                    activeCarouselAdvanceTask = null;
                    activeFinalTestTask = carouselAdvanceResult.FinalTestTask;
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：DD已完成 {carouselAdvanceResult.Turns} 次转动，立即开始上下料；最后一轮测试可并行继续…",
                        Color.FromRgb(73, 209, 125));
                }

                // 上一轮第二套放料通常已在DD转动和第一套取料期间完成；启动新收料前只确认第二套自身空闲。
                if (!activeSecondSetUnloadTask.IsCompleted)
                {
                    SetFirstSetPositionStatus(
                        "第一套两个料已吸取；第二套正在完成上一轮放料，完成后立即开始本轮收料。",
                        true);
                }

                await activeSecondSetUnloadTask;
                activeSecondSetUnloadTask = Task.CompletedTask;

                // DD停稳、工位状态确定后启动第二套收料；它与第一套向1/2工位放料并行。
                activeSecondSetUnloadTask = StartSecondSetUnloadIfReadyAsync(
                    carouselStations,
                    _productionCancellation.Token,
                    out activeSecondSetPickupTask);

                // 提示第 5 步开始：第一套 XY 移动到预设位置1。
                SetStartProductionStatus(
                    $"第{cycleNumber}轮：Z1/Z2均已回到配置安全位，XY正在放料到1工位({position1X:0.###}, {position1Y:0.###})…",
                    Color.FromRgb(242, 181, 68));

                // 执行位置1的绝对移动。
                await MovePresetPositionCoreAsync(
                    "位置 1",
                    position1X,
                    position1Y,
                    _productionCancellation.Token);

                // 到达位置1后，Z1 下降到配置放料高度，破真空后回到配置安全高度。
                SetStartProductionStatus(
                    $"第{cycleNumber}轮：1工位已到位，Z1正在下降到配置放料位…",
                    Color.FromRgb(242, 181, 68));
                await PlaceWithActiveSetNozzleAsync(1, _productionCancellation.Token);

                // 提示第 7 步开始：第一套 XY 移动到预设位置2。
                SetStartProductionStatus(
                    $"第{cycleNumber}轮：Z1已放料并回到配置安全位，XY正在放料到2工位({position2X:0.###}, {position2Y:0.###})…",
                    Color.FromRgb(242, 181, 68));

                // 执行位置2的绝对移动。
                await MovePresetPositionCoreAsync(
                    "位置 2",
                    position2X,
                    position2Y,
                    _productionCancellation.Token);

                // 到达位置2后，Z2 下降到配置放料高度，破真空后回到配置安全高度。
                SetStartProductionStatus(
                    $"第{cycleNumber}轮：2工位已到位，Z2正在下降到配置放料位…",
                    Color.FromRgb(242, 181, 68));
                await PlaceWithActiveSetNozzleAsync(2, _productionCancellation.Token);
                CloseAllActiveSetNozzleVacuumOutputs();

                carouselStations[1].SetLoaded();
                carouselStations[2].SetLoaded();
                UpdateCarouselStationDisplay(carouselStations);

                if (!activeSecondSetPickupTask.IsCompleted)
                {
                    SetStartProductionStatus(
                        $"第{cycleNumber}轮：第一套已放完并立即回中心；DD只等待第二套从12/13工位吸走两个料，不等待第二套放料…",
                        Color.FromRgb(242, 181, 68));
                }

                SetStartProductionStatus(
                    $"第{cycleNumber}轮第一套放料完成，XY立即回中心准备下一轮拍照；第二套继续独立收料…",
                    Color.FromRgb(73, 209, 125));
                activeFirstSetReturnToCenterTask = StartFirstSetReturnToCenterAsync(
                    motionController,
                    center,
                    velocity,
                    _productionCancellation.Token);

                // 测试回原与第二套双取料信号移交给DD安全门；主循环直接进入下一轮回中、拍照和吸料。
                var requiredFinalTestTask = activeFinalTestTask;
                activeFinalTestTask = Task.FromResult(0);
                var requiredSecondSetPickupTask = activeSecondSetPickupTask;
                activeSecondSetPickupTask = Task.CompletedTask;
                var xyMoveAwayDelayTask = Task.Delay(
                    MoveAwayBeforeDdMilliseconds,
                    _productionCancellation.Token);
                activeCarouselAdvanceTask = StartCarouselAfterSafetyBarrierAsync(
                    requiredFinalTestTask,
                    requiredSecondSetPickupTask,
                    xyMoveAwayDelayTask,
                    carouselStations,
                    axis0PulseDistance,
                    ProductionXyAxisNos,
                    _productionCancellation.Token);

                SetStartProductionStatus(
                    $"第{cycleNumber}轮放料完成，DD在测试回原且第二套双取料完成后固定转动两次；XY立即准备第{cycleNumber + 1}轮拍照吸料…",
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
            await ObserveTaskNoThrowAsync(activeSecondSetUnloadTask);
            await ObserveTaskNoThrowAsync(activeSecondSetPickupTask);
            if (activeFirstSetReturnToCenterTask is not null)
            {
                await ObserveTaskNoThrowAsync(activeFirstSetReturnToCenterTask);
            }

            await ObserveCarouselAdvanceTaskNoThrowAsync(activeCarouselAdvanceTask);
            await ObserveTaskNoThrowAsync(activeFinalTestTask);
            CloseAllActiveSetNozzleVacuumOutputsNoThrow();
            _productionAxisSet = null;
            _productionZPositions = null;
            _secondSetXyPositions = null;

            // 无论正常停止、异常退出还是中途 return，都要退出运行状态。
            _startSequenceRunning = false;

            // 清除停止请求标记，保证下次启动从干净状态开始。
            _productionStopRequested = false;

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
        _productionCancellation?.Cancel();
        SetStartProductionStatus("正在停止循环，请等待当前轴确认停止…", Color.FromRgb(242, 181, 68));
        UpdateHomeCommandState();
        return true;
    }

    private async Task<CalibrationCenterPosition> MoveToStartupPosition2SafelyAsync(
        MotionControlPage motionController,
        int xHardwareAxisNo,
        int yHardwareAxisNo,
        double targetX,
        double targetY,
        double velocity,
        CancellationToken cancellationToken)
    {
        var current = motionController.CaptureCalibrationFeedback(
            xHardwareAxisNo,
            yHardwareAxisNo);

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
            velocity);
        await motionController.MoveAxesAbsoluteAsync(
            new Dictionary<int, double> { [yHardwareAxisNo] = targetY },
            cancellationToken,
            minimumTimeoutMilliseconds: yTimeoutMilliseconds,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: velocity);

        var afterY = motionController.CaptureCalibrationFeedback(
            xHardwareAxisNo,
            yHardwareAxisNo);
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
            velocity);
        await motionController.MoveAxesAbsoluteAsync(
            new Dictionary<int, double> { [xHardwareAxisNo] = targetX },
            cancellationToken,
            minimumTimeoutMilliseconds: xTimeoutMilliseconds,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: velocity);

        var actual = motionController.CaptureCalibrationFeedback(
            xHardwareAxisNo,
            yHardwareAxisNo);
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

    private Task<CalibrationCenterPosition> StartFirstSetReturnToCenterAsync(
        MotionControlPage motionController,
        (double X, double Y) center,
        double velocity,
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
            velocity);

        SetFirstSetPositionStatus(
            $"放料后XY正在回中心：X={center.X:0.###}，Y={center.Y:0.###} pulse。",
            true);
        return motionController.MoveCalibrationAxesToAsync(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            center.X,
            center.Y,
            velocity,
            positionTolerance: HomePageCompletionTolerance,
            moveTimeoutMilliseconds: timeoutMilliseconds,
            cancellationToken: cancellationToken,
            allowedMovingAxisNos: FirstSetProductionPeerAxisNos);
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
        }
        catch
        {
        }
    }

    private async Task<CarouselAdvanceResult> StartCarouselAfterSafetyBarrierAsync(
        Task<int> requiredFinalTestTask,
        Task requiredSecondSetPickupTask,
        Task xyMoveAwayDelayTask,
        CarouselStationState[] carouselStations,
        double axis0PulseDistance,
        IReadOnlyCollection<int>? allowedMovingAxisNos,
        CancellationToken cancellationToken)
    {
        if (!requiredFinalTestTask.IsCompleted || !requiredSecondSetPickupTask.IsCompleted)
        {
            SetStartProductionStatus(
                "XY正在回中心准备下一轮拍照；下一次DD只等待测试轴回原及第二套完成双取料。",
                Color.FromRgb(242, 181, 68));
        }

        // DD必须同时满足：上一轮测试轴已回原、第二套已从12/13取走两个料、XY已离开放料点0.5秒。
        // 第二套后续移动到两个收料位置并放料，不再阻塞DD。
        await Task.WhenAll(
            requiredFinalTestTask,
            requiredSecondSetPickupTask,
            xyMoveAwayDelayTask);
        cancellationToken.ThrowIfCancellationRequested();
        return await AdvanceCarouselExactlyTwoStationsAsync(
            carouselStations,
            axis0PulseDistance,
            allowedMovingAxisNos,
            cancellationToken);
    }

    private Task StartSecondSetUnloadIfReadyAsync(
        CarouselStationState[] carouselStations,
        CancellationToken cancellationToken,
        out Task pickupCompletedTask)
    {
        if (!carouselStations[SecondSetNozzle2UnloadStation].Occupied ||
            !carouselStations[SecondSetNozzle1UnloadStation].Occupied)
        {
            pickupCompletedTask = Task.CompletedTask;
            return Task.CompletedTask;
        }

        if (_productionAxisSet != VisionCalibrationAxisSet.First)
        {
            throw new InvalidOperationException("第二套XY正在被主页上料流程占用，不能同时执行12/13工位收料。");
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
            var zPositions = GetProductionZPositions();
            var xyPositions = GetSecondSetXyPositions();
            await MoveSecondSetUnloadAxesToAsync(
                "吸嘴2取12工位",
                xyPositions.Position1X,
                xyPositions.Position1Y,
                cancellationToken);
            await PickSecondSetNozzleFromStationAsync(
                2,
                SecondSetNozzle2UnloadStation,
                zPositions.SecondSetPickup,
                zPositions.SecondSetSafe,
                cancellationToken);

            await MoveSecondSetUnloadAxesToAsync(
                "吸嘴1取13工位",
                xyPositions.Position2X,
                xyPositions.Position2Y,
                cancellationToken);
            await PickSecondSetNozzleFromStationAsync(
                1,
                SecondSetNozzle1UnloadStation,
                zPositions.SecondSetPickup,
                zPositions.SecondSetSafe,
                cancellationToken);

            // 第二套必须先完成两次取料。任一吸嘴未确认持料时，禁止进入任何放料动作。
            EnsureBothSecondSetNozzlesHolding();

            // 两个产品已经离开12/13工位，此刻即可释放DD安全门；后续第二套放料继续独立执行。
            carouselStations[SecondSetNozzle2UnloadStation] = CarouselStationState.Empty();
            carouselStations[SecondSetNozzle1UnloadStation] = CarouselStationState.Empty();
            UpdateCarouselStationDisplay(carouselStations);
            pickupCompletion.TrySetResult(true);

            await MoveSecondSetUnloadAxesToAsync(
                "吸嘴1放料",
                SecondSetNozzle1DropX,
                SecondSetNozzle1DropY,
                cancellationToken);
            await PlaceWithNozzleAsync(
                VisionCalibrationAxisSet.Second,
                1,
                zPositions.SecondSetDrop,
                zPositions.SecondSetSafe,
                cancellationToken);

            await MoveSecondSetUnloadAxesToAsync(
                "吸嘴2放料",
                SecondSetNozzle2DropX,
                SecondSetNozzle2DropY,
                cancellationToken);
            await PlaceWithNozzleAsync(
                VisionCalibrationAxisSet.Second,
                2,
                zPositions.SecondSetDrop,
                zPositions.SecondSetSafe,
                cancellationToken);
            CloseAllNozzleVacuumOutputs(VisionCalibrationAxisSet.Second);
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

    private void EnsureBothSecondSetNozzlesHolding()
    {
        var secondSetIndex = (int)VisionCalibrationAxisSet.Second;
        if (!_nozzleVacuumEnabledBySet[secondSetIndex, 1] ||
            !_nozzleVacuumEnabledBySet[secondSetIndex, 2])
        {
            throw new InvalidOperationException(
                "第二套两个吸嘴尚未全部完成吸料，禁止进入统一放料流程。");
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
        var velocity = ParseProductionVelocity(
            SecondSetXyVelocityTextBox.Text,
            "轴3/4第二套XY速度");
        if (!double.IsFinite(velocity) || velocity <= 0)
        {
            throw new InvalidOperationException("第二套XY的移动速度配置无效。");
        }

        var current = motionController.CaptureCalibrationFeedback(
            VisionCalibrationService.SecondSetXHardwareAxisNo,
            VisionCalibrationService.SecondSetYHardwareAxisNo,
            SecondSetProductionPeerAxisNos);
        var timeoutMilliseconds = CalculateStartMoveTimeout(
            current.ActualX,
            current.ActualY,
            targetX,
            targetY,
            velocity);
        try
        {
            await motionController.MoveCalibrationAxesToAsync(
                VisionCalibrationService.SecondSetXHardwareAxisNo,
                VisionCalibrationService.SecondSetYHardwareAxisNo,
                targetX,
                targetY,
                velocity,
                positionTolerance: HomePageCompletionTolerance,
                moveTimeoutMilliseconds: timeoutMilliseconds,
                cancellationToken: cancellationToken,
                allowedMovingAxisNos: SecondSetProductionPeerAxisNos);
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
        const int maximumTurnsBeforeReload = 2;
        for (var turn = 1; turn <= maximumTurnsBeforeReload; turn++)
        {
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
                $"DD马达第 {turn}/{maximumTurnsBeforeReload} 次转动完成，正在检查 5/6/7 测试站…",
                Color.FromRgb(242, 181, 68));
            if (turn < maximumTurnsBeforeReload)
            {
                // 第一次转动后的测试必须完成并回原，才允许DD执行第二次转动。
                _ = await RunOccupiedTestStationsAsync(
                    carouselStations,
                    cancellationToken);
            }
            else
            {
                // 第二次转动后DD已经停稳；测试站继续并行，不阻塞XY放料。
                finalTestTask = RunOccupiedTestStationsAsync(
                    carouselStations,
                    cancellationToken);
            }
        }

        SetStartProductionStatus(
            $"DD已固定转动 {maximumTurnsBeforeReload} 次并停稳；最后一轮测试并行执行，XY可直接上下料。",
            Color.FromRgb(73, 209, 125));
        return new CarouselAdvanceResult(
            maximumTurnsBeforeReload,
            finalTestTask);
    }

    private int CountLoadedTestStations(IReadOnlyList<CarouselStationState> carouselStations)
    {
        return TestStationAxisByStation.Keys.Count(station =>
            station < carouselStations.Count && carouselStations[station].Occupied);
    }

    private async Task<int> RunOccupiedTestStationsAsync(
        CarouselStationState[] carouselStations,
        CancellationToken cancellationToken)
    {
        var pressVelocity = ParseProductionVelocity(
            TestStationPressVelocityTextBox.Text,
            "轴13–15下压速度");
        var homeVelocity = ParseProductionVelocity(
            TestStationHomeVelocityTextBox.Text,
            "轴13–15回原速度");
        var axisTargets = TestStationAxisByStation
            .Where(pair => carouselStations[pair.Key].Occupied)
            .ToDictionary(pair => pair.Value, _ => MoveOutAbsolutePosition);
        if (axisTargets.Count == 0)
        {
            SetStartProductionStatus("5/6/7工位当前无料，跳过测试站下压。", Color.FromRgb(159, 177, 191));
            return 0;
        }

        var stations = TestStationAxisByStation
            .Where(pair => carouselStations[pair.Key].Occupied)
            .Select(pair => $"{pair.Key}号→轴{pair.Value}")
            .ToArray();

        UpdateCarouselStationDisplay(carouselStations, axisTargets.Keys, CarouselStatusPressing);
        SetStartProductionStatus(
            $"{string.Join("，", stations)} 已进测试站，不等待XY回中心，立即下压测试…",
            Color.FromRgb(242, 181, 68));
        var motionController = _motionController
            ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
        await motionController.MoveAxesAbsoluteAsync(
            axisTargets,
            cancellationToken,
            TestStationMoveTimeoutMilliseconds,
            ProductionXyAxisNos,
            HomePageCompletionTolerance,
            pressVelocity);
        UpdateCarouselStationDisplay(carouselStations, axisTargets.Keys, CarouselStatusDwelling);
        SetStartProductionStatus(
            $"{string.Join("，", stations)} 下压到位，停留 {TestStationDwellMilliseconds} ms…",
            Color.FromRgb(242, 181, 68));

        await Task.Delay(TestStationDwellMilliseconds, cancellationToken);

        UpdateCarouselStationDisplay(carouselStations, axisTargets.Keys, CarouselStatusHoming);
        SetStartProductionStatus(
            $"{string.Join("，", stations)} 停留完成，测试站正在回原，DD等待回原完成…",
            Color.FromRgb(242, 181, 68));
        SetStartProductionStatus(
            $"{string.Join("，", stations)} 正在下发 21 模式回原点命令，速度 {homeVelocity:0.###} pulse/s…",
            Color.FromRgb(242, 181, 68));
        await motionController.HomeAxesAsync(
            axisTargets.Keys.ToArray(),
            TestStationHomeMode,
            TestStationHomeOffsetPosition,
            cancellationToken,
            homeVelocity,
            homeVelocity,
            ProductionXyAxisNos);
        SetStartProductionStatus(
            $"{string.Join("，", stations)} 21 模式回原点完成，DD可继续下一步。",
            Color.FromRgb(73, 209, 125));
        foreach (var station in TestStationAxisByStation.Keys.Where(station => carouselStations[station].Occupied))
        {
            carouselStations[station].SetTested($"BIN{Random.Shared.Next(0, 4)}");
        }

        UpdateCarouselStationDisplay(carouselStations);
        return axisTargets.Count;
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
        Task<int> FinalTestTask);

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

        var worldPointList = document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibPointFListParam", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "ParamName", StringComparison.Ordinal))?.Value,
                    "WorldPointLst",
                    StringComparison.Ordinal));
        if (worldPointList is null)
        {
            throw new InvalidDataException("第一套 XY 标定文件中未找到 WorldPointLst。");
        }

        var calibrationType = ReadCalibrationParameter(document, "CalibType");
        var calibrationPointCountText = ReadCalibrationParameter(document, "TransNum");
        var calibrationErrorStatusText = ReadCalibrationParameter(document, "CalibErrStatus");
        if (!string.Equals(calibrationType, "NPointCalib", StringComparison.Ordinal) ||
            !int.TryParse(
                calibrationPointCountText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var calibrationPointCount) ||
            calibrationPointCount != 9 ||
            !int.TryParse(
                calibrationErrorStatusText,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var calibrationErrorStatus) ||
            calibrationErrorStatus != 0)
        {
            throw new InvalidDataException("所选文件不是成功完成的九点标定文件，禁止移动到其中的坐标。");
        }

        var worldPoints = worldPointList
            .Elements()
            .Where(element => string.Equals(element.Name.LocalName, "PointF", StringComparison.Ordinal))
            .Select(ReadWorldPoint)
            .ToArray();
        if (worldPoints.Length != 9)
        {
            throw new InvalidDataException("第一套 XY 标定文件的机械标定点数量不是9，禁止移动。");
        }

        // 本程序的九点采集顺序固定把第5点记录为 (0,0) 偏移，因此它就是当时记录的初始中心。
        // 使用实测中心点而不是重新求平均，避免非对称误差把绝对运动目标悄悄改掉。
        var recordedCenter = worldPoints[4];
        var centerX = recordedCenter.X *
            VisionCalibrationService.PulsesPerVisionUnit;
        var centerY = recordedCenter.Y *
            VisionCalibrationService.PulsesPerVisionUnit;
        if (!double.IsFinite(centerX) || !double.IsFinite(centerY))
        {
            throw new InvalidOperationException("第一套 XY 标定中心坐标无效。");
        }

        return (centerX, centerY);
    }

    private static string? ReadCalibrationParameter(XDocument document, string parameterName)
    {
        return document
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "CalibParam", StringComparison.Ordinal) &&
                string.Equals(
                    element.Attributes().FirstOrDefault(attribute =>
                        string.Equals(attribute.Name.LocalName, "ParamName", StringComparison.Ordinal))?.Value,
                    parameterName,
                    StringComparison.Ordinal))?
            .Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "ParamValue", StringComparison.Ordinal))?
            .Value;
    }

    private static (double X, double Y) ReadWorldPoint(XElement pointElement)
    {
        var xText = pointElement.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "X", StringComparison.Ordinal))?.Value;
        var yText = pointElement.Elements().FirstOrDefault(element =>
            string.Equals(element.Name.LocalName, "Y", StringComparison.Ordinal))?.Value;
        if (!double.TryParse(xText, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(yText, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.IsFinite(x) ||
            !double.IsFinite(y))
        {
            throw new InvalidDataException("第一套 XY 标定文件包含无效的 WorldPointLst 坐标。");
        }

        return (x, y);
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
    /// 把 Blob结果1 固定分配给吸嘴1、Blob结果2 固定分配给吸嘴2。
    /// 计算基准使用拍照完成时的轴绝对位置，而不是后续可能已经移动过的当前位置。
    /// </summary>
    private DualNozzleMechanicalTargets CalculateAssignedNozzleTargets(
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

        ValidateBlobPixel(blobResult.Rectangle1, blobResult.ImageWidth, blobResult.ImageHeight, "Blob结果1");
        ValidateBlobPixel(blobResult.Rectangle2, blobResult.ImageWidth, blobResult.ImageHeight, "Blob结果2");

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
            return CalculateVisionTarget(cameraTargetX, cameraTargetY, tool);
        }

        return new DualNozzleMechanicalTargets(
            CalculateTarget(blobResult.Rectangle1, VisionTargetTool.Nozzle1),
            CalculateTarget(blobResult.Rectangle2, VisionTargetTool.Nozzle2));
    }

    private static void ValidateBlobPixel(
        VisionBlobRectangle blob,
        int imageWidth,
        int imageHeight,
        string name)
    {
        if (!double.IsFinite(blob.X) ||
            !double.IsFinite(blob.Y) ||
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

    private void SetAssignedNozzleTargets(DualNozzleMechanicalTargets targets)
    {
        _blob1Nozzle1Target = targets.Nozzle1;
        _blob2Nozzle2Target = targets.Nozzle2;
        _nextAssignedNozzleMoveStep = 1;
        SetFirstSetPositionStatus(
            "已分配：Blob结果1 → 吸嘴1，Blob结果2 → 吸嘴2；即将自动顺序对位。",
            true);
        UpdateAssignedNozzleButtonText();
        UpdateHomeCommandState();
    }

    private void ClearAssignedNozzleTargets()
    {
        _blob1Nozzle1Target = null;
        _blob2Nozzle2Target = null;
        _nextAssignedNozzleMoveStep = 0;
        UpdateAssignedNozzleButtonText();
        UpdateHomeCommandState();
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
        var velocity = ParseProductionVelocity(
            FirstSetXyVelocityTextBox.Text,
            "轴1/2第一套XY速度");
        if (!double.IsFinite(velocity) || velocity <= 0)
        {
            throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
        }

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
            velocity);
        var actual = await motionController.MoveCalibrationAxesToAsync(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            target.Value.X,
            target.Value.Y,
            velocity,
            positionTolerance: HomePageCompletionTolerance,
            moveTimeoutMilliseconds: timeoutMilliseconds,
            cancellationToken: cancellationToken,
            allowedMovingAxisNos: AllowedProductionPeerAxisNos);

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

    private async Task MoveAxis0RelativeCoreAsync(
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
        var velocity = ParseProductionVelocity(
            Axis0VelocityTextBox.Text,
            "轴0 DD马达速度");
        await motionController.MoveAxisRelativeAsync(
            hardwareAxisNo: 0,
            pulseDistance: pulseDistance,
            cancellationToken: cancellationToken,
            allowedMovingAxisNos: allowedMovingAxisNos,
            minimumCompletionTolerance: HomePageCompletionTolerance,
            velocityOverride: velocity);
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
            "第二套位置 1（吸嘴2取12）",
            SecondSetPosition1XTextBox,
            SecondSetPosition1YTextBox,
            MoveSecondSetPosition1Button);
    }

    private void RecordSecondSetPosition1_Click(object sender, RoutedEventArgs e)
    {
        RecordSecondSetPosition(
            1,
            "第二套位置 1（吸嘴2取12）",
            SecondSetPosition1XTextBox,
            SecondSetPosition1YTextBox);
    }

    private async void MoveSecondSetPosition2_Click(object sender, RoutedEventArgs e)
    {
        await MoveSecondSetPositionAsync(
            "第二套位置 2（吸嘴1取13）",
            SecondSetPosition2XTextBox,
            SecondSetPosition2YTextBox,
            MoveSecondSetPosition2Button);
    }

    private void RecordSecondSetPosition2_Click(object sender, RoutedEventArgs e)
    {
        RecordSecondSetPosition(
            2,
            "第二套位置 2（吸嘴1取13）",
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
            _ = ParseProductionVelocity(
                SecondSetXyVelocityTextBox.Text,
                "轴3/4第二套XY速度");

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
            var motionController = _motionController
                ?? throw new InvalidOperationException("主页尚未连接运动控制组件。");
            var velocity = ParseProductionVelocity(
                FirstSetXyVelocityTextBox.Text,
                "轴1/2第一套XY速度");
            if (!double.IsFinite(velocity) || velocity <= 0)
            {
                throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
            }

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
        var velocity = ParseProductionVelocity(
            FirstSetXyVelocityTextBox.Text,
            "轴1/2第一套XY速度");
        if (!double.IsFinite(velocity) || velocity <= 0)
        {
            throw new InvalidOperationException("第一套 XY 的移动速度配置无效。");
        }

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
            velocity);
        var actual = await motionController.MoveCalibrationAxesToAsync(
            VisionCalibration.XHardwareAxisNo,
            VisionCalibration.YHardwareAxisNo,
            targetX,
            targetY,
            velocity,
            positionTolerance: HomePageCompletionTolerance,
            moveTimeoutMilliseconds: timeoutMilliseconds,
            cancellationToken: cancellationToken,
            allowedMovingAxisNos: AllowedProductionPeerAxisNos);
        SetFirstSetPositionStatus(
            $"{positionName}已到位：X={actual.ActualX:0.###}，Y={actual.ActualY:0.###} pulse。",
            true);
    }

    private void PresetPositionTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SavePresetPositionsFromInputs();
        UpdateHomeCommandState();
    }

    private void ProductionVelocityTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loadingPresetPositions)
        {
            UpdateHomeCommandState();
        }
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

    private void LoadPresetPositions()
    {
        _homeSettings = _homeSettingsStore.Load();
        _loadingPresetPositions = true;
        PresetPosition1XTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition1X);
        PresetPosition1YTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition1Y);
        PresetPosition2XTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition2X);
        PresetPosition2YTextBox.Text = FormatPresetCoordinate(_homeSettings.PresetPosition2Y);
        Axis0VelocityTextBox.Text = FormatPresetCoordinate(Axis0Velocity);
        FirstSetXyVelocityTextBox.Text = FormatPresetCoordinate(FirstSetXyVelocity);
        SecondSetXyVelocityTextBox.Text = FormatPresetCoordinate(SecondSetXyVelocity);
        TestStationPressVelocityTextBox.Text = FormatPresetCoordinate(TestStationPressVelocity);
        TestStationHomeVelocityTextBox.Text = FormatPresetCoordinate(TestStationHomeVelocity);
        FirstSetPickupZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetPickupZPosition ?? DefaultNozzlePickupZPosition);
        FirstSetDropZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetDropZPosition ?? DefaultNozzleDropZPosition);
        FirstSetSafeZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.FirstSetSafeZPosition ?? DefaultNozzleSafeZPosition);
        SecondSetPickupZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetPickupZPosition ?? DefaultNozzlePickupZPosition);
        SecondSetDropZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetDropZPosition ?? DefaultNozzleDropZPosition);
        SecondSetSafeZPositionTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetSafeZPosition ?? DefaultNozzleSafeZPosition);
        SecondSetPosition1XTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetPickupPosition1X ?? DefaultSecondSetPickupPosition1X);
        SecondSetPosition1YTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetPickupPosition1Y ?? DefaultSecondSetPickupPosition1Y);
        SecondSetPosition2XTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetPickupPosition2X ?? DefaultSecondSetPickupPosition2X);
        SecondSetPosition2YTextBox.Text = FormatPresetCoordinate(
            _homeSettings.SecondSetPickupPosition2Y ?? DefaultSecondSetPickupPosition2Y);
        _loadingPresetPositions = false;
    }

    private ProductionZPositions ReadProductionZPositions()
    {
        return new ProductionZPositions(
            ParseFiniteCoordinate(FirstSetPickupZPositionTextBox.Text, "第一套取料Z高度"),
            ParseFiniteCoordinate(FirstSetDropZPositionTextBox.Text, "第一套放料Z高度"),
            ParseFiniteCoordinate(FirstSetSafeZPositionTextBox.Text, "第一套安全Z高度"),
            ParseFiniteCoordinate(SecondSetPickupZPositionTextBox.Text, "第二套取料Z高度"),
            ParseFiniteCoordinate(SecondSetDropZPositionTextBox.Text, "第二套放料Z高度"),
            ParseFiniteCoordinate(SecondSetSafeZPositionTextBox.Text, "第二套安全Z高度"));
    }

    private SecondSetXyPositions ReadSecondSetXyPositions()
    {
        return new SecondSetXyPositions(
            ParseFiniteCoordinate(SecondSetPosition1XTextBox.Text, "第二套位置1（吸嘴2取12）X轴绝对脉冲"),
            ParseFiniteCoordinate(SecondSetPosition1YTextBox.Text, "第二套位置1（吸嘴2取12）Y轴绝对脉冲"),
            ParseFiniteCoordinate(SecondSetPosition2XTextBox.Text, "第二套位置2（吸嘴1取13）X轴绝对脉冲"),
            ParseFiniteCoordinate(SecondSetPosition2YTextBox.Text, "第二套位置2（吸嘴1取13）Y轴绝对脉冲"));
    }

    private void SaveProductionZPositionsFromInputs()
    {
        if (_loadingPresetPositions ||
            FirstSetPickupZPositionTextBox is null ||
            FirstSetDropZPositionTextBox is null ||
            FirstSetSafeZPositionTextBox is null ||
            SecondSetPickupZPositionTextBox is null ||
            SecondSetDropZPositionTextBox is null ||
            SecondSetSafeZPositionTextBox is null ||
            !TryParseCoordinate(FirstSetPickupZPositionTextBox.Text, out var firstSetPickup) ||
            !TryParseCoordinate(FirstSetDropZPositionTextBox.Text, out var firstSetDrop) ||
            !TryParseCoordinate(FirstSetSafeZPositionTextBox.Text, out var firstSetSafe) ||
            !TryParseCoordinate(SecondSetPickupZPositionTextBox.Text, out var secondSetPickup) ||
            !TryParseCoordinate(SecondSetDropZPositionTextBox.Text, out var secondSetDrop) ||
            !TryParseCoordinate(SecondSetSafeZPositionTextBox.Text, out var secondSetSafe))
        {
            return;
        }

        _homeSettings.FirstSetPickupZPosition = firstSetPickup;
        _homeSettings.FirstSetDropZPosition = firstSetDrop;
        _homeSettings.FirstSetSafeZPosition = firstSetSafe;
        _homeSettings.SecondSetPickupZPosition = secondSetPickup;
        _homeSettings.SecondSetDropZPosition = secondSetDrop;
        _homeSettings.SecondSetSafeZPosition = secondSetSafe;
        try
        {
            _homeSettingsStore.Save(_homeSettings);
        }
        catch (Exception exception)
        {
            SetFirstSetPositionStatus($"保存Z轴生产高度失败：{exception.Message}", false);
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

    private void HomeEmergencyStop_Click(object sender, RoutedEventArgs e)
    {
        _ = RequestProductionStop();
        var motionController = _motionController;
        if (motionController is null)
        {
            HomeEmergencyStopHintText.Text = "运动控制未连接";
            return;
        }

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
            "请确认所有机构都在安全区域，并且硬件轴 0～15 已使能。\n\n" +
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
            PresetPosition1XTextBox is null ||
            PresetPosition1YTextBox is null ||
            PresetPosition2XTextBox is null ||
            PresetPosition2YTextBox is null ||
            Axis0VelocityTextBox is null ||
            FirstSetXyVelocityTextBox is null ||
            SecondSetXyVelocityTextBox is null ||
            TestStationPressVelocityTextBox is null ||
            TestStationHomeVelocityTextBox is null ||
            FirstSetPickupZPositionTextBox is null ||
            FirstSetDropZPositionTextBox is null ||
            FirstSetSafeZPositionTextBox is null ||
             SecondSetPickupZPositionTextBox is null ||
             SecondSetDropZPositionTextBox is null ||
             SecondSetSafeZPositionTextBox is null ||
             SecondSetPosition1XTextBox is null ||
             SecondSetPosition1YTextBox is null ||
             SecondSetPosition2XTextBox is null ||
             SecondSetPosition2YTextBox is null ||
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
        var axis0VelocityValid = TryParseProductionVelocity(Axis0VelocityTextBox.Text, out _);
        var firstSetVelocityValid = TryParseProductionVelocity(FirstSetXyVelocityTextBox.Text, out _);
        var secondSetVelocityValid = TryParseProductionVelocity(SecondSetXyVelocityTextBox.Text, out _);
        var pressVelocityValid = TryParseProductionVelocity(TestStationPressVelocityTextBox.Text, out _);
        var homeVelocityValid = TryParseProductionVelocity(TestStationHomeVelocityTextBox.Text, out _);
        var allZPositionsValid =
            TryParseCoordinate(FirstSetPickupZPositionTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetDropZPositionTextBox.Text, out _) &&
            TryParseCoordinate(FirstSetSafeZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPickupZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetDropZPositionTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetSafeZPositionTextBox.Text, out _);
        var allSecondSetXyPositionsValid =
            TryParseCoordinate(SecondSetPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition1YTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition2XTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition2YTextBox.Text, out _);
        var allProductionVelocitiesValid =
            axis0VelocityValid &&
            firstSetVelocityValid &&
            secondSetVelocityValid &&
            pressVelocityValid &&
            homeVelocityValid;
        StartProductionButton.IsEnabled =
            visionControllersReady &&
            allProductionVelocitiesValid &&
            allZPositionsValid &&
            allSecondSetXyPositionsValid &&
            (_startSequenceRunning ? !_productionStopRequested : commandsIdle);
        StartProductionTitleText.Text = _startSequenceRunning
            ? (_productionStopRequested ? "正在停止" : "停止循环")
            : "开始运行";
        StartProductionButton.Background = new SolidColorBrush(
            _startSequenceRunning ? Color.FromRgb(181, 22, 35) : Color.FromRgb(22, 139, 80));
        StartProductionButton.BorderBrush = new SolidColorBrush(
            _startSequenceRunning ? Color.FromRgb(255, 98, 110) : Color.FromRgb(56, 185, 121));
        OneKeyResetButton.IsEnabled = _motionController is not null && commandsIdle;
        MoveAssignedNozzleButton.IsEnabled =
            visionControllersReady &&
            commandsIdle &&
            firstSetVelocityValid &&
            ((_nextAssignedNozzleMoveStep == 1 && _blob1Nozzle1Target is not null) ||
             (_nextAssignedNozzleMoveStep == 2 && _blob2Nozzle2Target is not null));
        PresetPosition1XTextBox.IsEnabled = commandsIdle;
        PresetPosition1YTextBox.IsEnabled = commandsIdle;
        PresetPosition2XTextBox.IsEnabled = commandsIdle;
        PresetPosition2YTextBox.IsEnabled = commandsIdle;
        Axis0VelocityTextBox.IsEnabled = commandsIdle;
        FirstSetXyVelocityTextBox.IsEnabled = commandsIdle;
        SecondSetXyVelocityTextBox.IsEnabled = commandsIdle;
        TestStationPressVelocityTextBox.IsEnabled = commandsIdle;
        TestStationHomeVelocityTextBox.IsEnabled = commandsIdle;
        FirstSetPickupZPositionTextBox.IsEnabled = commandsIdle;
        FirstSetDropZPositionTextBox.IsEnabled = commandsIdle;
        FirstSetSafeZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetPickupZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetDropZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetSafeZPositionTextBox.IsEnabled = commandsIdle;
        SecondSetPosition1XTextBox.IsEnabled = commandsIdle;
        SecondSetPosition1YTextBox.IsEnabled = commandsIdle;
        SecondSetPosition2XTextBox.IsEnabled = commandsIdle;
        SecondSetPosition2YTextBox.IsEnabled = commandsIdle;
        RecordPresetPosition1Button.IsEnabled = _motionController is not null && commandsIdle;
        RecordPresetPosition2Button.IsEnabled = _motionController is not null && commandsIdle;
        RecordSecondSetPosition1Button.IsEnabled = _motionController is not null && commandsIdle;
        RecordSecondSetPosition2Button.IsEnabled = _motionController is not null && commandsIdle;
        MovePresetPosition1Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            firstSetVelocityValid &&
            TryParseCoordinate(PresetPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(PresetPosition1YTextBox.Text, out _);
        MovePresetPosition2Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            firstSetVelocityValid &&
            TryParseCoordinate(PresetPosition2XTextBox.Text, out _) &&
            TryParseCoordinate(PresetPosition2YTextBox.Text, out _);
        MoveSecondSetPosition1Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            secondSetVelocityValid &&
            TryParseCoordinate(SecondSetPosition1XTextBox.Text, out _) &&
            TryParseCoordinate(SecondSetPosition1YTextBox.Text, out _);
        MoveSecondSetPosition2Button.IsEnabled =
            _motionController is not null &&
            commandsIdle &&
            secondSetVelocityValid &&
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
        BlobInspectionImageStatusText.Text = "正在运行 Blob分析1";
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
                $"VM组件 · Blob分析1 · {result.ImageWidth}×{result.ImageHeight}";
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
            // 图片显示不是生产结果的前置条件；Blob 的两组 X/Y 已成功取得并保留在主页。
            BlobInspectionImageStatusText.Text = "XY已显示 · 检测图加载失败";
            BlobInspectionImageStatusText.Foreground =
                new SolidColorBrush(Color.FromRgb(242, 181, 68));
        }
    }

    /// <summary>
    /// 加载 VisionMaster 本次保存的相机图，并在同一像素坐标系中叠加两个 Blob 框和质心。
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
        DrawBlobOverlay(result.Rectangle1, 1, Color.FromRgb(0, 230, 118), imageWidth, imageHeight);
        DrawBlobOverlay(result.Rectangle2, 2, Color.FromRgb(64, 196, 255), imageWidth, imageHeight);

        BlobInspectionImagePlaceholder.Visibility = Visibility.Collapsed;
        BlobInspectionImageViewbox.Visibility = Visibility.Visible;
        BlobInspectionImageStatusText.Text = $"已检测 · {imageWidth}×{imageHeight}";
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
                Text = $"矩形{number}  X={blob.X:0.0}  Y={blob.Y:0.0}",
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
        double FirstSetPickup,
        double FirstSetDrop,
        double FirstSetSafe,
        double SecondSetPickup,
        double SecondSetDrop,
        double SecondSetSafe);

    private readonly record struct SecondSetXyPositions(
        double Position1X,
        double Position1Y,
        double Position2X,
        double Position2Y);
}
