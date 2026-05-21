using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using ControlHub.Models;
using ControlHub.Services;

namespace ControlHub;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly IMotionCard _motionCard = new LeisaiMotionCard(simulationMode: true);
    private readonly DispatcherTimer _clockTimer;
    private AxisStatus? _selectedAxis;
    private string _nowText = "";
    private double _jogPercent = 25;

    public MainWindow()
    {
        InitializeComponent();

        Axes = CreateAxes();
        IoPoints = CreateIoPoints();
        AlarmRecords = CreateAlarmRecords();
        StationPoints = new ObservableCollection<StationPoint>();
        TrayCells = new ObservableCollection<int>(Enumerable.Range(1, 27));
        Logs = new ObservableCollection<string>();

        SelectedAxis = Axes.FirstOrDefault();
        NowText = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        DataContext = this;

        _motionCard.Open();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => NowText = DateTime.Now.ToString("yyyy-MM-dd  HH:mm:ss");
        _clockTimer.Start();
    }

    public ObservableCollection<AxisStatus> Axes { get; }
    public ObservableCollection<IoPoint> IoPoints { get; }
    public ObservableCollection<AlarmInfo> AlarmRecords { get; }
    public ObservableCollection<StationPoint> StationPoints { get; }
    public ObservableCollection<int> TrayCells { get; }
    public ObservableCollection<string> Logs { get; }

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

    public double JogPercent
    {
        get => _jogPercent;
        set => SetField(ref _jogPercent, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private static ObservableCollection<AxisStatus> CreateAxes()
    {
        var names = new[] { "X1", "Y1", "Z1", "R1", "X2", "Y2", "Z2", "R2", "A1", "B1", "C1", "U1", "V1", "W1", "T1", "T2" };
        var positions = new[] { 1250.320, 320.150, -45.000, 15.600, 850.000, 210.300, -10.000, 0.000, 30.000, -90.000, 180.000, 5.000, 12.500, -3.200, 100.000, 200.000 };
        var rotaryAxes = new HashSet<string> { "R1", "R2", "A1", "B1", "C1" };

        var axes = new ObservableCollection<AxisStatus>();
        for (var i = 0; i < names.Length; i++)
        {
            axes.Add(new AxisStatus
            {
                AxisNo = i + 1,
                Name = names[i],
                Unit = rotaryAxes.Contains(names[i]) ? "deg" : "mm",
                Position = positions[i],
                Target = positions[i],
                Speed = 0,
                ServoOn = true,
                Homed = true,
                Alarm = false,
                PositiveLimit = i == 14,
                NegativeLimit = i == 15,
                State = "待机"
            });
        }

        return axes;
    }

    private static ObservableCollection<IoPoint> CreateIoPoints()
    {
        var points = new ObservableCollection<IoPoint>();
        var onSet = new HashSet<int> { 0, 1, 2, 4, 5, 7, 8, 10, 11, 12, 15, 16, 18, 20, 21, 24, 25, 26, 28, 31, 32, 35, 36, 40, 42, 44, 45, 47, 50, 52, 57, 59, 60, 61 };

        for (var row = 0; row < 4; row++)
        {
            for (var col = 0; col < 16; col++)
            {
                var index = row * 16 + col;
                points.Add(new IoPoint
                {
                    Name = $"X{row}{col:X}",
                    IsOn = onSet.Contains(index)
                });
            }
        }

        return points;
    }

    private static ObservableCollection<AlarmInfo> CreateAlarmRecords()
    {
        return new ObservableCollection<AlarmInfo>
        {
            new() { Time = "2024-05-20 14:31:02", Code = "ALM-0021", Message = "Z1轴负限位触发", Level = "报警", Status = "未确认" },
            new() { Time = "2024-05-20 14:28:15", Code = "ALM-0010", Message = "X2轴伺服跟随误差过大", Level = "报警", Status = "未确认" },
            new() { Time = "2024-05-20 14:25:33", Code = "ALM-0008", Message = "气压不足", Level = "警告", Status = "已确认" },
            new() { Time = "2024-05-20 14:20:11", Code = "ALM-0015", Message = "加工单元2过载", Level = "报警", Status = "已确认" },
            new() { Time = "2024-05-20 14:18:05", Code = "ALM-0003", Message = "急停按钮被按下", Level = "报警", Status = "已确认" }
        };
    }

    private void JogNegative_Click(object sender, RoutedEventArgs e)
    {
        JogSelected(GetAxisFromSender(sender), -1);
    }

    private void JogPositive_Click(object sender, RoutedEventArgs e)
    {
        JogSelected(GetAxisFromSender(sender), 1);
    }

    private void Home_Click(object sender, RoutedEventArgs e)
    {
        var axis = GetAxisFromSender(sender);
        if (axis is null)
        {
            return;
        }

        _motionCard.Home(axis.AxisNo);
        axis.Homed = true;
        axis.Position = 0;
        axis.Target = 0;
        axis.State = "回零完成";
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        var axis = GetAxisFromSender(sender);
        if (axis is null)
        {
            foreach (var item in Axes)
            {
                _motionCard.Stop(item.AxisNo);
                item.State = "已停止";
            }

            return;
        }

        _motionCard.Stop(axis.AxisNo);
        axis.State = "已停止";
    }

    private void Step_Click(object sender, RoutedEventArgs e)
    {
        var axis = GetAxisFromSender(sender);
        if (axis is null)
        {
            return;
        }

        var step = axis.Unit == "deg" ? 1.0 : 5.0;
        axis.Target = axis.Position + step;
        axis.Position = axis.Target;
        axis.State = "单步完成";
    }

    private void ServoOn_Click(object sender, RoutedEventArgs e)
    {
        SetServo(GetAxisFromSender(sender), true);
    }

    private void ServoOff_Click(object sender, RoutedEventArgs e)
    {
        SetServo(GetAxisFromSender(sender), false);
    }

    private void EmergencyStop_Click(object sender, RoutedEventArgs e)
    {
        _motionCard.EmergencyStop();
        foreach (var axis in Axes)
        {
            axis.State = "急停";
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _clockTimer.Stop();
        _motionCard.Close();
        base.OnClosed(e);
    }

    private void AxisDataGrid_LayoutChanged(object sender, RoutedEventArgs e)
    {
        ResizeAxisRows();
    }

    private void AxisDataGrid_LayoutChanged(object sender, SizeChangedEventArgs e)
    {
        ResizeAxisRows();
    }

    private AxisStatus? GetAxisFromSender(object sender)
    {
        if (sender is FrameworkElement { DataContext: AxisStatus axisFromRow })
        {
            SelectedAxis = axisFromRow;
            return axisFromRow;
        }

        return SelectedAxis;
    }

    private void JogSelected(AxisStatus? axis, int direction)
    {
        if (axis is null)
        {
            return;
        }

        var velocity = direction * JogPercent;
        _motionCard.Jog(axis.AxisNo, velocity);
        axis.Speed = (int)Math.Abs(velocity);
        axis.Position += direction * (axis.Unit == "deg" ? 0.1 : 0.25);
        axis.Target = axis.Position;
        axis.State = direction > 0 ? "正向点动" : "负向点动";
    }

    private void SetServo(AxisStatus? axis, bool enabled)
    {
        if (axis is null)
        {
            return;
        }

        _motionCard.ServoOn(axis.AxisNo, enabled);
        axis.ServoOn = enabled;
        axis.State = enabled ? "伺服ON" : "伺服OFF";
    }

    private void ResizeAxisRows()
    {
        if (Axes.Count == 0 || AxisDataGrid.ActualHeight <= 0)
        {
            return;
        }

        const double headerAndBorders = 38;
        var rowHeight = Math.Floor((AxisDataGrid.ActualHeight - headerAndBorders) / Axes.Count);
        AxisDataGrid.RowHeight = Math.Max(34, rowHeight);
    }

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
