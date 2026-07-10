using System.Collections.ObjectModel;
using System.Windows.Media;
using ControlHub.Data;
using ControlHub.Models;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Services.Vision;

namespace ControlHub.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly IReadOnlyDictionary<int, AxisSettings> _savedAxisSettings;
    private AxisStatus? _selectedAxis;
    private string _nowText = "";
    private string _userPermissionText = "用户权限：未登录";
    private string _visionStatusText = "未加载";
    private string _visionRunTimeText = "--";
    private string _feederConnectionStatusText = "\u672a\u8fde\u63a5";
    private string _motionConnectionText = "运动控制：未连接";
    private string _motionAxisSummaryText = "■ 轴状态监控";
    private bool _motionControlsEnabled;
    private ImageSource? _visionPreviewImage;

    public MainWindowViewModel()
        : this(new AxisSettingsStore().Load())
    {
    }

    public MainWindowViewModel(IReadOnlyDictionary<int, AxisSettings> savedAxisSettings)
    {
        _savedAxisSettings = savedAxisSettings;
        Axes = [];
        IoPoints = [];
        AlarmRecords = [];
        StationPoints = [];
        TrayCells = new ObservableCollection<int>(Enumerable.Range(1, 27));
        Logs = [];
        VisionSettings = new VisionMasterSettings();
        VisionOutputs = [];
        VisionLogs = [];
        FeederSettings = new VibrationFeederSettingsStore().Load();
        FeederConnectionLogs = [];

        NowText = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
    }

    public void PopulateMotionAxes(int axisCount)
    {
        Axes.Clear();
        foreach (var axis in DashboardSeedData.CreateAxes(_savedAxisSettings, axisCount))
        {
            Axes.Add(axis);
        }

        SelectedAxis = Axes.FirstOrDefault();
        MotionAxisSummaryText = $"■ 轴状态监控（{Axes.Count}轴）";
        MotionControlsEnabled = Axes.Count > 0;
    }

    public void ClearMotionData()
    {
        Axes.Clear();
        IoPoints.Clear();
        AlarmRecords.Clear();
        SelectedAxis = null;
        MotionAxisSummaryText = "■ 轴状态监控";
        MotionControlsEnabled = false;
    }

    public ObservableCollection<AxisStatus> Axes { get; }

    public ObservableCollection<IoPoint> IoPoints { get; }

    public ObservableCollection<AlarmInfo> AlarmRecords { get; }

    public ObservableCollection<StationPoint> StationPoints { get; }

    public ObservableCollection<int> TrayCells { get; }

    public ObservableCollection<string> Logs { get; }

    public VisionMasterSettings VisionSettings { get; }

    public ObservableCollection<VisionOutputItem> VisionOutputs { get; }

    public ObservableCollection<string> VisionLogs { get; }

    public VibrationFeederSettings FeederSettings { get; }

    public ObservableCollection<string> FeederConnectionLogs { get; }

    public AxisStatus? SelectedAxis
    {
        get => _selectedAxis;
        set => SetField(ref _selectedAxis, value);
    }

    public string NowText
    {
        get => _nowText;
        set => SetField(ref _nowText, value);
    }

    public string UserPermissionText
    {
        get => _userPermissionText;
        set => SetField(ref _userPermissionText, value);
    }

    public string VisionStatusText
    {
        get => _visionStatusText;
        set => SetField(ref _visionStatusText, value);
    }

    public string VisionRunTimeText
    {
        get => _visionRunTimeText;
        set => SetField(ref _visionRunTimeText, value);
    }

    public string FeederConnectionStatusText
    {
        get => _feederConnectionStatusText;
        set => SetField(ref _feederConnectionStatusText, value);
    }

    public ImageSource? VisionPreviewImage
    {
        get => _visionPreviewImage;
        set => SetField(ref _visionPreviewImage, value);
    }

    public string MotionConnectionText
    {
        get => _motionConnectionText;
        set => SetField(ref _motionConnectionText, value);
    }

    public string MotionAxisSummaryText
    {
        get => _motionAxisSummaryText;
        set => SetField(ref _motionAxisSummaryText, value);
    }

    public bool MotionControlsEnabled
    {
        get => _motionControlsEnabled;
        set => SetField(ref _motionControlsEnabled, value);
    }
}
