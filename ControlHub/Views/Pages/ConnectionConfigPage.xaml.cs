using System.IO;
using System.IO.Ports;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;

namespace ControlHub.Views.Pages;

public partial class ConnectionConfigPage : UserControl
{
    private enum ConnectionTarget
    {
        Feeder,
        Tcp,
        Serial
    }

    private const int MaxConnectionLogCount = 300;
    private const int BrightnessSendDebounceMs = 150;
    private const string StopVibrationCommand = "&04$";
    private const string ProtocolCommandName = "\u632f\u52a8\u76d8\u534f\u8bae";
    private const string LightOnCommand = "&07,1$";
    private const string LightOffCommand = "&07,0$";
    private const int OneKeyGatherCycleCount = 1;
    private const int LeftRightGatherPulseDurationMs = 1500;
    private const int UpDownGatherPulseDurationMs = 1500;
    private const string LeftRightGatherParameterCommand = "&02,044,040,1,044,040,1,044,040,1,044,040,1,05$";
    private const string LeftRightGatherStartCommand = "&03,05$";
    private const string UpDownGatherParameterCommand = "&02,044,060,1,044,060,1,044,060,1,044,060,1,06$";
    private const string UpDownGatherStartCommand = "&03,06$";
    private static readonly IReadOnlyDictionary<string, string> VibrationDirectionNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["00"] = "左上移动",
            ["01"] = "向上移动",
            ["02"] = "右上移动",
            ["03"] = "向左移动",
            ["04"] = "震散",
            ["05"] = "左右聚拢",
            ["06"] = "上下聚拢",
            ["07"] = "向右移动",
            ["08"] = "左下移动",
            ["09"] = "向下移动",
            ["10"] = "右下移动"
        };
    private readonly VibrationFeederSettingsStore _settingsStore = new();
    private readonly VibrationFeederTcpClient _tcpClient = new();
    private readonly TcpConnectionSettingsStore _tcpSettingsStore = new();
    private readonly E4981ATcpClient _generalTcpClient = new();
    private readonly SerialConnectionSettingsStore _serialSettingsStore = new();
    private readonly SerialConnectionClient _serialClient = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly SemaphoreSlim _protocolWriteLock = new(1, 1);
    private readonly DispatcherTimer _brightnessSendTimer;
    private bool _closed;
    private bool _connecting;
    private bool _tcpConnecting;
    private bool _meterOperationRunning;
    private bool _serialConnecting;
    private bool _serialMeterOperationRunning;
    private bool _loaded;
    private bool _vibrationSequenceRunning;
    private CancellationTokenSource? _vibrationOperationCancellation;
    private ConnectionTarget _selectedTarget = ConnectionTarget.Feeder;

    public ConnectionConfigPage()
    {
        _brightnessSendTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(BrightnessSendDebounceMs)
        };
        _brightnessSendTimer.Tick += BrightnessSendTimer_Tick;
        InitializeComponent();
        _tcpClient.DataReceived += TcpClient_DataReceived;
        _tcpClient.ConnectionClosed += TcpClient_ConnectionClosed;
        _generalTcpClient.ConnectionClosed += GeneralTcpClient_ConnectionClosed;
        _serialClient.ResponseReceived += SerialClient_ResponseReceived;
        _serialClient.ConnectionClosed += SerialClient_ConnectionClosed;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private VibrationFeederSettings? Settings => ViewModel?.FeederSettings;

    private TcpConnectionSettings? TcpSettings => ViewModel?.TcpConnectionSettings;

    private SerialConnectionSettings? SerialSettings => ViewModel?.SerialConnectionSettings;

    public void Shutdown()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _brightnessSendTimer.Stop();
        _brightnessSendTimer.Tick -= BrightnessSendTimer_Tick;
        SaveSettings(writeLog: false);
        SaveTcpSettings(writeLog: false);
        SaveSerialSettings(writeLog: false);
        _vibrationOperationCancellation?.Cancel();
        _lifetimeCancellation.Cancel();
        _tcpClient.DataReceived -= TcpClient_DataReceived;
        _tcpClient.ConnectionClosed -= TcpClient_ConnectionClosed;
        _generalTcpClient.ConnectionClosed -= GeneralTcpClient_ConnectionClosed;
        _serialClient.ResponseReceived -= SerialClient_ResponseReceived;
        _serialClient.ConnectionClosed -= SerialClient_ConnectionClosed;
        _tcpClient.Dispose();
        _generalTcpClient.Dispose();
        _serialClient.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private async void ConnectionConfigPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        AddLog("\u8fde\u63a5\u914d\u7f6e\u9875\u5df2\u52a0\u8f7d");
        AddTcpLog("E4981A连接配置已加载");
        AddSerialLog("SM7110串口连接配置已加载");
        RefreshSerialOptionLists();
        RefreshSerialPorts();
        await AutoConnectAsync();
    }

    private void ConnectionConfigPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    private void FeederConnectionItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectConnectionTarget(ConnectionTarget.Feeder);
    }

    private void TcpConnectionItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        SelectConnectionTarget(ConnectionTarget.Tcp);
    }

    private void SerialConnectionItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        RefreshSerialPorts();
        SelectConnectionTarget(ConnectionTarget.Serial);
    }

    private void SerialPortComboBox_DropDownOpened(object sender, EventArgs e)
    {
        RefreshSerialPorts();
    }

    private void RefreshSerialPorts()
    {
        var configuredPort = SerialSettings?.PortName?.Trim();
        var ports = SerialPort.GetPortNames()
            .OrderBy(portName => portName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 保留尚未接入的已保存端口，避免刷新列表时清空用户配置。
        if (!string.IsNullOrWhiteSpace(configuredPort) &&
            !ports.Contains(configuredPort, StringComparer.OrdinalIgnoreCase))
        {
            ports.Insert(0, configuredPort);
        }

        SerialPortComboBox.ItemsSource = ports;
        if (!string.IsNullOrWhiteSpace(configuredPort))
        {
            SerialPortComboBox.SelectedItem = ports.FirstOrDefault(
                portName => string.Equals(portName, configuredPort, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RefreshSerialOptionLists()
    {
        if (SerialSettings is not { } settings)
        {
            return;
        }

        SerialBaudRateComboBox.ItemsSource = AddConfiguredOption(
            [300, 600, 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600],
            settings.BaudRate);
        SerialDataBitsComboBox.ItemsSource = AddConfiguredOption([5, 6, 7, 8], settings.DataBits);
        SerialParityComboBox.ItemsSource = AddConfiguredOption(
            ["None", "Odd", "Even", "Mark", "Space"],
            settings.Parity);
        SerialStopBitsComboBox.ItemsSource = AddConfiguredOption(
            ["One", "OnePointFive", "Two"],
            settings.StopBits);
        SerialNewLineComboBox.ItemsSource = AddConfiguredOption(
            ["\\r\\n", "\\r", "\\n", "无"],
            settings.NewLine);
    }

    private static IReadOnlyList<T> AddConfiguredOption<T>(IEnumerable<T> standardOptions, T configuredOption)
    {
        var options = standardOptions.Distinct().ToList();
        if (configuredOption is not null && !options.Contains(configuredOption))
        {
            options.Add(configuredOption);
        }
        return options;
    }

    private void SelectConnectionTarget(ConnectionTarget target)
    {
        _selectedTarget = target;

        FeederConnectionItem.Style = (Style)FindResource(
            target == ConnectionTarget.Feeder ? "ActiveConnectionItem" : "ConnectionItem");
        TcpConnectionItem.Style = (Style)FindResource(
            target == ConnectionTarget.Tcp ? "ActiveConnectionItem" : "ConnectionItem");
        SerialConnectionItem.Style = (Style)FindResource(
            target == ConnectionTarget.Serial ? "ActiveConnectionItem" : "ConnectionItem");

        FeederDetailPanel.Visibility = target == ConnectionTarget.Feeder
            ? Visibility.Visible
            : Visibility.Collapsed;
        TcpDetailPanel.Visibility = target == ConnectionTarget.Tcp
            ? Visibility.Visible
            : Visibility.Collapsed;
        SerialDetailPanel.Visibility = target == ConnectionTarget.Serial
            ? Visibility.Visible
            : Visibility.Collapsed;

        RefreshActiveStatus();
    }

    private void RefreshActiveStatus()
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        ActiveConnectionStatusText.Text = _selectedTarget switch
        {
            ConnectionTarget.Tcp => viewModel.TcpConnectionStatusText,
            ConnectionTarget.Serial => viewModel.SerialConnectionStatusText,
            _ => viewModel.FeederConnectionStatusText
        };
    }

    private void SaveTcpSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveTcpSettings(writeLog: true);
    }

    private async void ConnectTcp_Click(object sender, RoutedEventArgs e)
    {
        if (TcpSettings is not { } settings)
        {
            return;
        }

        if (_tcpConnecting)
        {
            AddTcpLog("E4981A正在连接，请稍候");
            return;
        }

        SaveTcpSettings(writeLog: false);
        await ConnectTcpAsync(settings);
    }

    private async Task ConnectTcpAsync(TcpConnectionSettings settings)
    {
        _tcpConnecting = true;
        try
        {
            await _generalTcpClient.ConnectAsync(settings, _lifetimeCancellation.Token);
            var identity = await QueryMeterAsync("*IDN?");
            if (!identity.Contains("E4981A", StringComparison.OrdinalIgnoreCase))
            {
                _generalTcpClient.Close();
                throw new InvalidDataException($"已连接的设备不是E4981A：{identity}");
            }
            UpdateMeterIdentity(identity);
            settings.LastSuccessfulConnectionSignature = CreateTcpConnectionSignature(settings.Host, settings.Port);
            _tcpSettingsStore.Save(settings);
            SetTcpStatus($"E4981A已连接：{settings.Host}:{settings.Port}");
            AddTcpLog($"E4981A身份确认成功：{identity}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            SetTcpStatus("连接失败");
            AddTcpLog($"连接失败：{ex.Message}");
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (OperationCanceledException)
        {
            SetTcpStatus("未连接");
            AddTcpLog("E4981A连接已取消");
        }
        finally
        {
            _tcpConnecting = false;
        }
    }

    private void DisconnectTcp_Click(object sender, RoutedEventArgs e)
    {
        _generalTcpClient.Close();
        SetTcpStatus("未连接");
        AddTcpLog("已断开E4981A连接");
    }

    private async void SendTcpMessage_Click(object sender, RoutedEventArgs e)
    {
        if (TcpSettings is not { } settings)
        {
            return;
        }

        SaveTcpSettings(writeLog: false);
        if (string.IsNullOrWhiteSpace(settings.ManualSendText))
        {
            AddTcpLog("发送失败：内容为空");
            return;
        }
        if (!_generalTcpClient.IsConnected)
        {
            AddTcpLog("发送失败：请先建立 TCP 连接");
            return;
        }

        await RunMeterOperationAsync("手动发送", async () =>
        {
            var command = settings.ManualSendText.Trim();
            if (E4981AProtocol.ExpectsResponse(command))
            {
                var response = await QueryMeterAsync(command);
                if (string.Equals(command, "*IDN?", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateMeterIdentity(response);
                }
                else if (string.Equals(command, "*TRG", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateMeterResult(E4981AProtocol.ParseMeasurement(response));
                }
            }
            else
            {
                await SendMeterCommandAsync(command);
            }
        });
    }

    private async void IdentifyMeter_Click(object sender, RoutedEventArgs e)
    {
        await RunMeterOperationAsync("读取仪表型号", async () =>
        {
            var identity = await QueryMeterAsync("*IDN?");
            UpdateMeterIdentity(identity);
        });
    }

    private async void ApplyMeterSettings_Click(object sender, RoutedEventArgs e)
    {
        if (TcpSettings is not { } settings)
        {
            return;
        }

        CommitInputBindings(this);
        SaveTcpSettings(writeLog: false);
        await RunMeterOperationAsync("下发测试参数", async () =>
        {
            var commands = E4981AProtocol.BuildSetupCommands(settings);
            foreach (var command in commands)
            {
                await SendMeterCommandAsync(command);
            }

            var instrumentError = await QueryMeterAsync("SYST:ERR?");
            if (!instrumentError.StartsWith("0", StringComparison.OrdinalIgnoreCase) &&
                !instrumentError.StartsWith("+0", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"仪表参数错误：{instrumentError}");
            }
            AddTcpLog($"E4981A测试参数下发完成，共{commands.Count}条命令");
        });
    }

    private async void TriggerMeterTest_Click(object sender, RoutedEventArgs e)
    {
        await RunMeterOperationAsync("单次测试", async () =>
        {
            var response = await QueryMeterAsync("*TRG");
            UpdateMeterResult(E4981AProtocol.ParseMeasurement(response));
        });
    }

    private async void ReadMeterError_Click(object sender, RoutedEventArgs e)
    {
        await RunMeterOperationAsync("读取仪表错误", async () =>
        {
            var response = await QueryMeterAsync("SYST:ERR?");
            MeterErrorText.Text = response;
        });
    }

    private void ClearTcpLog_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.TcpConnectionLogs.Clear();
    }

    private void SaveSerialSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSerialSettings(writeLog: true);
    }

    private async void ConnectSerial_Click(object sender, RoutedEventArgs e)
    {
        if (SerialSettings is not { } settings)
        {
            return;
        }

        SaveSerialSettings(writeLog: false);
        await ConnectSerialAsync(settings);
    }

    private async Task ConnectSerialAsync(SerialConnectionSettings settings)
    {
        if (_serialConnecting)
        {
            AddSerialLog("SM7110正在连接，请稍候");
            return;
        }

        _serialConnecting = true;
        try
        {
            SM7110Protocol.ValidateSettings(settings);
            _serialClient.Connect(settings);
            AddSerialLog($"串口已打开 {settings.PortName}，{settings.BaudRate} bps，正在识别仪表");
            var identity = await QuerySerialMeterAsync("*IDN?");
            if (!SM7110Protocol.IsSupportedIdentity(identity))
            {
                _serialClient.Close();
                throw new InvalidDataException($"已连接的设备不是SM7110/SM7120：{identity}");
            }

            UpdateSerialMeterIdentity(identity);
            await SendSerialMeterCommandAsync(":STOP:CONDition DISCharge");
            await SendSerialMeterCommandAsync(":STOP");
            settings.LastSuccessfulConnectionSignature = CreateSerialConnectionSignature(settings);
            _serialSettingsStore.Save(settings);
            SetSerialStatus($"SM7110已连接：{settings.PortName}");
            AddSerialLog($"SM7110身份确认成功：{identity}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or
                                   InvalidOperationException or ArgumentException or ObjectDisposedException)
        {
            _serialClient.Close();
            SetSerialStatus("连接失败");
            AddSerialLog($"连接失败：{ex.Message}");
        }
        finally
        {
            _serialConnecting = false;
        }
    }

    private void DisconnectSerial_Click(object sender, RoutedEventArgs e)
    {
        _serialClient.Close();
        SetSerialStatus("未连接");
        AddSerialLog("已断开SM7110串口连接");
    }

    private async void SendSerialMessage_Click(object sender, RoutedEventArgs e)
    {
        if (SerialSettings is not { } settings)
        {
            return;
        }

        SaveSerialSettings(writeLog: false);
        if (string.IsNullOrWhiteSpace(settings.ManualSendText))
        {
            AddSerialLog("发送失败：内容为空");
            return;
        }
        if (!_serialClient.IsConnected)
        {
            AddSerialLog("发送失败：请先建立串口连接");
            return;
        }

        var command = settings.ManualSendText.Trim();
        await RunSerialMeterOperationAsync("手动命令", async () =>
        {
            if (!settings.AppendNewLine)
            {
                throw new InvalidOperationException("SM7110命令必须追加结束符。");
            }

            if (SM7110Protocol.ExpectsResponse(command))
            {
                var response = await QuerySerialMeterAsync(command);
                if (string.Equals(command, "*IDN?", StringComparison.OrdinalIgnoreCase))
                {
                    UpdateSerialMeterIdentity(response);
                }
            }
            else
            {
                await SendSerialMeterCommandAsync(command);
            }
        });
    }

    private async void ApplySerialMeterSettings_Click(object sender, RoutedEventArgs e)
    {
        if (SerialSettings is not { } settings)
        {
            return;
        }

        CommitInputBindings(this);
        SaveSerialSettings(writeLog: false);
        await RunSerialMeterOperationAsync("下发测试参数", async () =>
        {
            var commands = SM7110Protocol.BuildSetupCommands(settings);
            foreach (var command in commands)
            {
                await SendSerialMeterCommandAsync(command);
            }
            AddSerialLog($"SM7110测试参数下发完成，共{commands.Count}条命令，当前保持停止放电状态");
        });
    }

    private async void TriggerSerialMeterTest_Click(object sender, RoutedEventArgs e)
    {
        if (SerialSettings is not { } settings)
        {
            return;
        }

        CommitInputBindings(this);
        SaveSerialSettings(writeLog: false);
        await RunSerialMeterOperationAsync("单次测量", async () =>
        {
            var setupCommands = SM7110Protocol.BuildSetupCommands(settings);
            foreach (var command in setupCommands)
            {
                await SendSerialMeterCommandAsync(command);
            }
            await SendSerialMeterCommandAsync(":STARt");
            try
            {
                var response = await QuerySerialMeterAsync("*TRG;*WAI;:MEASure:RESult? 3");
                UpdateSerialMeterResult(SM7110Protocol.ParseMeasurementResult(response, settings.MeasurementMode));
            }
            finally
            {
                try
                {
                    await SendSerialMeterCommandAsync(":STOP");
                    AddSerialLog("测量结束，已停止输出并进入放电状态");
                }
                catch (Exception ex) when (ex is IOException or TimeoutException or InvalidOperationException or ObjectDisposedException)
                {
                    AddSerialLog($"警告：测量结束后停止放电命令发送失败：{ex.Message}");
                }
            }
        });
    }

    private async void StopSerialMeter_Click(object sender, RoutedEventArgs e)
    {
        await RunSerialMeterOperationAsync("停止并放电", async () =>
        {
            await SendSerialMeterCommandAsync(":STOP");
            SerialMeterResultStatusText.Text = "已停止并放电";
            SerialMeterResultStatusText.Foreground = new SolidColorBrush(Color.FromRgb(98, 181, 255));
        });
    }

    private void ClearSerialLog_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.SerialConnectionLogs.Clear();
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings(writeLog: true);
    }

    private async void ConnectFeeder_Click(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        if (_connecting)
        {
            AddLog("TCP \u6b63\u5728\u8fde\u63a5\uff0c\u8bf7\u7a0d\u5019");
            return;
        }

        SaveSettings(writeLog: false);
        await ConnectFeederAsync(settings);
    }

    private async Task ConnectFeederAsync(VibrationFeederSettings settings)
    {
        _connecting = true;

        try
        {
            await _tcpClient.ConnectAsync(settings, _lifetimeCancellation.Token);
            settings.LastSuccessfulConnectionSignature = CreateTcpConnectionSignature(settings.Host, settings.Port);
            _settingsStore.Save(settings);
            SetFeederStatus($"\u5df2\u8fde\u63a5\uff1a{settings.Host}:{settings.Port}");

            AddLog($"TCP \u5df2\u8fde\u63a5 {settings.Host}:{settings.Port}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            SetFeederStatus("\u8fde\u63a5\u5931\u8d25");

            AddLog($"\u8fde\u63a5\u5931\u8d25\uff1a{ex.Message}");
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (OperationCanceledException)
        {
            SetFeederStatus("\u672a\u8fde\u63a5");

            AddLog("TCP \u8fde\u63a5\u5df2\u53d6\u6d88");
        }
        finally
        {
            _connecting = false;
        }
    }

    private async Task AutoConnectAsync()
    {
        if (SerialSettings is { } serialSettings &&
            HasSuccessfulSerialConnection(serialSettings))
        {
            AddSerialLog("程序启动，正在自动连接SM7110");
        }
        else
        {
            AddSerialLog("未自动连接：当前串口配置尚未成功连接过");
        }

        var connectionTasks = new List<Task>(3);
        if (SerialSettings is { } autoSerialSettings && HasSuccessfulSerialConnection(autoSerialSettings))
        {
            connectionTasks.Add(ConnectSerialAsync(autoSerialSettings));
        }
        if (Settings is { } feederSettings &&
            HasSuccessfulTcpConnection(
                feederSettings.LastSuccessfulConnectionSignature,
                feederSettings.Host,
                feederSettings.Port))
        {
            AddLog("程序启动，正在自动连接振动盘 TCP");
            connectionTasks.Add(ConnectFeederAsync(feederSettings));
        }
        else
        {
            AddLog("未自动连接：当前振动盘配置尚未成功连接过");
        }

        if (TcpSettings is { } tcpSettings &&
            HasSuccessfulTcpConnection(
                tcpSettings.LastSuccessfulConnectionSignature,
                tcpSettings.Host,
                tcpSettings.Port))
        {
            AddTcpLog("程序启动，正在自动连接E4981A");
            connectionTasks.Add(ConnectTcpAsync(tcpSettings));
        }
        else
        {
            AddTcpLog("未自动连接：当前E4981A配置尚未成功连接过");
        }

        await Task.WhenAll(connectionTasks);
    }

    private static bool HasSuccessfulTcpConnection(string? successfulSignature, string? host, int port)
    {
        return string.Equals(
            successfulSignature,
            CreateTcpConnectionSignature(host, port),
            StringComparison.Ordinal);
    }

    private static bool HasSuccessfulSerialConnection(SerialConnectionSettings settings)
    {
        return string.Equals(
            settings.LastSuccessfulConnectionSignature,
            CreateSerialConnectionSignature(settings),
            StringComparison.Ordinal);
    }

    private static string CreateTcpConnectionSignature(string? host, int port)
    {
        return $"{host?.Trim().ToUpperInvariant()}\n{port}";
    }

    private static string CreateSerialConnectionSignature(SerialConnectionSettings settings)
    {
        return string.Join(
            '\n',
            settings.PortName?.Trim().ToUpperInvariant(),
            settings.BaudRate,
            settings.DataBits,
            settings.Parity?.Trim().ToUpperInvariant(),
            settings.StopBits?.Trim().ToUpperInvariant());
    }

    private void DisconnectFeeder_Click(object sender, RoutedEventArgs e)
    {
        _tcpClient.Close();

        SetFeederStatus("\u672a\u8fde\u63a5");

        AddLog("\u5df2\u65ad\u5f00\u9707\u52a8\u76d8 TCP \u8fde\u63a5");
    }

    private async void SendManualMessage_Click(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        SaveSettings(writeLog: false);

        if (string.IsNullOrWhiteSpace(settings.ManualSendText))
        {
            AddLog("\u53d1\u9001\u5931\u8d25\uff1a\u5185\u5bb9\u4e3a\u7a7a");
            return;
        }

        if (!_tcpClient.IsConnected)
        {
            AddLog("\u53d1\u9001\u5931\u8d25\uff1a\u8bf7\u5148\u5efa\u7acb TCP \u8fde\u63a5");
            return;
        }

        try
        {
            var payload = BuildPayload(settings);
            await _tcpClient.WriteAsync(payload);
            AddLog($"TX [ASCII]  {FormatPayload(payload)}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or FormatException or InvalidOperationException or ObjectDisposedException)
        {
            SetFeederStatus("\u901a\u8baf\u5f02\u5e38");

            AddLog($"\u53d1\u9001\u5931\u8d25\uff1a{ex.Message}");
        }
    }

    private async void StopVibration_Click(object sender, RoutedEventArgs e)
    {
        if (!_tcpClient.IsConnected)
        {
            AddLog("\u505c\u6b62\u9707\u52a8\u5931\u8d25\uff1a\u8bf7\u5148\u5efa\u7acb TCP \u8fde\u63a5");
            return;
        }

        _vibrationOperationCancellation?.Cancel();
        await SendAsciiProtocolCommandAsync(StopVibrationCommand, "手动停止震动");
    }

    private async void DirectionalVibration_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string mode } ||
            !VibrationDirectionNames.TryGetValue(mode, out var directionName))
        {
            AddLog("方向震动失败：未知的方向模式");
            return;
        }

        await RunDirectionalVibrationAsync(mode, directionName, _lifetimeCancellation.Token);
    }

    private async Task<bool> RunDirectionalVibrationAsync(
        string mode,
        string directionName,
        CancellationToken cancellationToken)
    {
        if (!_tcpClient.IsConnected)
        {
            AddLog($"{directionName}失败：请先建立 TCP 连接");
            return false;
        }

        if (_vibrationSequenceRunning)
        {
            AddLog($"{directionName}未执行：当前震动尚未结束，可先点“停止震动”");
            return false;
        }

        if (Settings is not { } settings)
        {
            return false;
        }

        CommitInputBindings(this);
        _settingsStore.Save(settings);

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        _vibrationOperationCancellation = operationCancellation;
        _vibrationSequenceRunning = true;

        try
        {
            AddLog(
                $"方向震动：{directionName}，频率 {settings.DirectionalVibrationFrequency}，" +
                $"振幅 {settings.DirectionalVibrationAmplitude}%，持续 {settings.DirectionalVibrationDurationMilliseconds} ms");

            if (!await SendAsciiProtocolCommandAsync("&05,00$", "切换正常模式"))
            {
                return false;
            }

            return await RunVibrationPulseAsync(
                BuildDirectionalVibrationParameterCommand(settings, mode),
                $"&03,{mode}$",
                settings.DirectionalVibrationDurationMilliseconds,
                directionName,
                operationCancellation.Token);
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            AddLog($"{directionName}已停止");
            return false;
        }
        finally
        {
            if (ReferenceEquals(_vibrationOperationCancellation, operationCancellation))
            {
                _vibrationOperationCancellation = null;
            }

            _vibrationSequenceRunning = false;
        }
    }

    private async void OneKeyVibration_Click(object sender, RoutedEventArgs e)
    {
        await RunOneKeyVibrationAsync(_lifetimeCancellation.Token);
    }

    public async Task<bool> RunOneKeyVibrationAsync(CancellationToken cancellationToken)
    {
        if (!_tcpClient.IsConnected)
        {
            AddLog("\u4e00\u952e\u9707\u52a8\u5931\u8d25\uff1a\u8bf7\u5148\u5efa\u7acb TCP \u8fde\u63a5");
            return false;
        }

        if (_vibrationSequenceRunning)
        {
            AddLog("\u4e00\u952e\u9707\u52a8\u6b63\u5728\u6267\u884c\uff0c\u8bf7\u7a0d\u5019");
            return false;
        }

        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        _vibrationOperationCancellation = operationCancellation;
        _vibrationSequenceRunning = true;
        try
        {
            AddLog($"\u4e00\u952e\u9707\u52a8\u5f00\u59cb\uff1a\u5de6\u53f3\u805a\u62e2 -> \u4e0a\u4e0b\u805a\u62e2\uff0c\u5faa\u73af {OneKeyGatherCycleCount} \u6b21");
            if (!await SendAsciiProtocolCommandAsync(
                    "&05,00$",
                    "\u5207\u6362\u6b63\u5e38\u6a21\u5f0f"))
            {
                return false;
            }

            for (var cycleIndex = 1; cycleIndex <= OneKeyGatherCycleCount; cycleIndex++)
            {
                AddLog($"\u4e00\u952e\u805a\u62e2\u7b2c {cycleIndex}/{OneKeyGatherCycleCount} \u8f6e");

                if (!await RunVibrationPulseAsync(
                        LeftRightGatherParameterCommand,
                        LeftRightGatherStartCommand,
                        LeftRightGatherPulseDurationMs,
                        $"{cycleIndex}/{OneKeyGatherCycleCount}-\u5de6\u53f3\u805a\u62e2",
                        operationCancellation.Token) ||
                    !await RunVibrationPulseAsync(
                        UpDownGatherParameterCommand,
                        UpDownGatherStartCommand,
                        UpDownGatherPulseDurationMs,
                        $"{cycleIndex}/{OneKeyGatherCycleCount}-\u4e0a\u4e0b\u805a\u62e2",
                        operationCancellation.Token))
                {
                    return false;
                }
            }

            AddLog("\u4e00\u952e\u9707\u52a8\u5b8c\u6210");
            return true;
        }
        catch (OperationCanceledException) when (
            _closed ||
            operationCancellation.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(_vibrationOperationCancellation, operationCancellation))
            {
                _vibrationOperationCancellation = null;
            }

            _vibrationSequenceRunning = false;
        }
    }

    private static string BuildDirectionalVibrationParameterCommand(
        VibrationFeederSettings settings,
        string mode)
    {
        var frequency = Math.Clamp(settings.DirectionalVibrationFrequency, 1, 999);
        var amplitude = Math.Clamp(settings.DirectionalVibrationAmplitude, 0, 100);
        var channel = $"{frequency:000},{amplitude:000},1";
        return $"&02,{channel},{channel},{channel},{channel},{mode}$";
    }

    private async void LightOn_Click(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        if (!_tcpClient.IsConnected)
        {
            AddLog("\u5149\u6e90\u6253\u5f00\u5931\u8d25\uff1a\u8bf7\u5148\u5efa\u7acb TCP \u8fde\u63a5");
            return;
        }

        _brightnessSendTimer.Stop();
        SaveSettings(writeLog: false);
        await SendAsciiProtocolCommandAsync(LightOnCommand, "\u5149\u6e90\u6253\u5f00");
        await ApplyLightBrightnessAsync("\u6253\u5f00\u540e\u8bbe\u7f6e\u4eae\u5ea6");
    }

    private async void LightOff_Click(object sender, RoutedEventArgs e)
    {
        _brightnessSendTimer.Stop();
        await SendAsciiProtocolCommandAsync(LightOffCommand, "\u5149\u6e90\u5173\u95ed");
    }

    private void DecreaseLightBrightness_Click(object sender, RoutedEventArgs e)
    {
        AdjustLightBrightness(-1);
    }

    private void IncreaseLightBrightness_Click(object sender, RoutedEventArgs e)
    {
        AdjustLightBrightness(1);
    }

    private void AdjustLightBrightness(int delta)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        settings.LightOnBrightness = Math.Clamp(settings.LightOnBrightness + delta, 0, 99);
    }

    private void LightBrightnessSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        var normalizedBrightness = (int)Math.Round(Math.Clamp(e.NewValue, 0, 99));
        if (settings.LightOnBrightness != normalizedBrightness)
        {
            settings.LightOnBrightness = normalizedBrightness;
        }

        if (!_loaded || _closed || !_tcpClient.IsConnected)
        {
            return;
        }

        _brightnessSendTimer.Stop();
        _brightnessSendTimer.Start();
    }

    private async void BrightnessSendTimer_Tick(object? sender, EventArgs e)
    {
        _brightnessSendTimer.Stop();
        if (_closed || !_tcpClient.IsConnected)
        {
            return;
        }

        await ApplyLightBrightnessAsync("\u62d6\u52a8\u8bbe\u7f6e\u4eae\u5ea6");
    }

    private async Task ApplyLightBrightnessAsync(string actionName)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        var normalizedBrightness = Math.Clamp(settings.LightOnBrightness, 0, 99);
        if (settings.LightOnBrightness != normalizedBrightness)
        {
            settings.LightOnBrightness = normalizedBrightness;
        }

        _settingsStore.Save(settings);
        await SendAsciiProtocolCommandAsync(
            $"&06,{normalizedBrightness:00},XX$",
            $"{actionName} {normalizedBrightness:00}%");
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.FeederConnectionLogs.Clear();
    }

    private void SaveSettings(bool writeLog)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        CommitInputBindings(this);
        _settingsStore.Save(settings);

        if (writeLog)
        {
            AddLog("\u9707\u52a8\u76d8\u8fde\u63a5\u914d\u7f6e\u5df2\u4fdd\u5b58");
        }
    }

    private void SaveTcpSettings(bool writeLog)
    {
        if (TcpSettings is not { } settings)
        {
            return;
        }

        CommitInputBindings(this);
        _tcpSettingsStore.Save(settings);
        if (writeLog)
        {
            AddTcpLog("E4981A连接与测试参数已保存");
        }
    }

    private void SaveSerialSettings(bool writeLog)
    {
        if (SerialSettings is not { } settings)
        {
            return;
        }

        CommitInputBindings(this);
        _serialSettingsStore.Save(settings);
        if (writeLog)
        {
            AddSerialLog("SM7110串口连接与测试参数已保存");
        }
    }

    private void AddLog(string message)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var logItem = $"{DateTime.Now:HH:mm:ss}  {message}";
        viewModel.FeederConnectionLogs.Add(logItem);
        while (viewModel.FeederConnectionLogs.Count > MaxConnectionLogCount)
        {
            viewModel.FeederConnectionLogs.RemoveAt(0);
        }

        ConnectionLogListBox.ScrollIntoView(logItem);
    }

    private void AddTcpLog(string message)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var logItem = $"{DateTime.Now:HH:mm:ss}  {message}";
        viewModel.TcpConnectionLogs.Add(logItem);
        while (viewModel.TcpConnectionLogs.Count > MaxConnectionLogCount)
        {
            viewModel.TcpConnectionLogs.RemoveAt(0);
        }
        TcpConnectionLogListBox.ScrollIntoView(logItem);
    }

    private void AddSerialLog(string message)
    {
        if (ViewModel is not { } viewModel)
        {
            return;
        }

        var logItem = $"{DateTime.Now:HH:mm:ss}  {message}";
        viewModel.SerialConnectionLogs.Add(logItem);
        while (viewModel.SerialConnectionLogs.Count > MaxConnectionLogCount)
        {
            viewModel.SerialConnectionLogs.RemoveAt(0);
        }
        SerialConnectionLogListBox.ScrollIntoView(logItem);
    }

    private void SetFeederStatus(string status)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.FeederConnectionStatusText = status;
        }
        if (_selectedTarget == ConnectionTarget.Feeder)
        {
            RefreshActiveStatus();
        }
    }

    private void SetTcpStatus(string status)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.TcpConnectionStatusText = status;
        }
        if (_selectedTarget == ConnectionTarget.Tcp)
        {
            RefreshActiveStatus();
        }
    }

    private void SetSerialStatus(string status)
    {
        if (ViewModel is { } viewModel)
        {
            viewModel.SerialConnectionStatusText = status;
        }
        if (_selectedTarget == ConnectionTarget.Serial)
        {
            RefreshActiveStatus();
        }
    }

    private async Task<bool> SendAsciiProtocolCommandAsync(string command, string actionName)
    {
        await _protocolWriteLock.WaitAsync();
        try
        {
            if (!_tcpClient.IsConnected)
            {
                AddLog($"{actionName}\u5931\u8d25\uff1a\u8bf7\u5148\u5efa\u7acb TCP \u8fde\u63a5");
                return false;
            }

            try
            {
                var payload = Encoding.ASCII.GetBytes(command);
                await _tcpClient.WriteAsync(payload);
                AddLog($"TX [ASCII]  {command}  {ProtocolCommandName}-{actionName}");
                return true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidOperationException or ObjectDisposedException)
            {
                SetFeederStatus("\u901a\u8baf\u5f02\u5e38");

                AddLog($"{actionName}\u5931\u8d25\uff1a{ex.Message}");
                return false;
            }
        }
        finally
        {
            _protocolWriteLock.Release();
        }
    }

    private async Task<bool> RunVibrationPulseAsync(
        string parameterCommand,
        string startCommand,
        int durationMs,
        string actionName,
        CancellationToken cancellationToken)
    {
        if (!await SendAsciiProtocolCommandAsync(
                parameterCommand,
                $"{actionName}-\u4e0b\u53d1\u53c2\u6570") ||
            !await SendAsciiProtocolCommandAsync(
                startCommand,
                $"{actionName}-\u542f\u52a8"))
        {
            return false;
        }

        var stopped = false;
        try
        {
            await Task.Delay(durationMs, cancellationToken);
        }
        finally
        {
            // 停止生产或关闭页面时也先下发停止，避免振动脉冲停留在启动状态。
            stopped = await SendAsciiProtocolCommandAsync(
                StopVibrationCommand,
                $"{actionName}-\u505c\u6b62");
        }

        await Task.Delay(120, cancellationToken);
        return stopped;
    }

    private async Task RunMeterOperationAsync(string actionName, Func<Task> operation)
    {
        if (_meterOperationRunning)
        {
            AddTcpLog($"{actionName}未执行：仪表正在处理上一条命令");
            return;
        }
        if (!_generalTcpClient.IsConnected)
        {
            AddTcpLog($"{actionName}失败：请先连接E4981A");
            return;
        }

        _meterOperationRunning = true;
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or
                                   InvalidOperationException or FormatException or ObjectDisposedException)
        {
            if (!_generalTcpClient.IsConnected)
            {
                SetTcpStatus("通讯异常");
            }
            AddTcpLog($"{actionName}失败：{ex.Message}");
        }
        finally
        {
            _meterOperationRunning = false;
        }
    }

    private async Task RunSerialMeterOperationAsync(string actionName, Func<Task> operation)
    {
        if (_serialMeterOperationRunning)
        {
            AddSerialLog($"{actionName}未执行：SM7110正在处理上一条命令");
            return;
        }
        if (!_serialClient.IsConnected)
        {
            AddSerialLog($"{actionName}失败：请先连接SM7110");
            return;
        }

        _serialMeterOperationRunning = true;
        try
        {
            await operation();
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException or
                                   InvalidOperationException or FormatException or ObjectDisposedException)
        {
            if (!_serialClient.IsConnected)
            {
                SetSerialStatus("通讯异常");
            }
            AddSerialLog($"{actionName}失败：{ex.Message}");
        }
        finally
        {
            _serialMeterOperationRunning = false;
        }
    }

    private async Task SendSerialMeterCommandAsync(string command)
    {
        var settings = SerialSettings ?? throw new InvalidOperationException("SM7110串口参数未加载。");
        var terminator = SM7110Protocol.DecodeNewLine(settings.NewLine);
        AddSerialLog($"TX [SCPI]  {command}");
        await _serialClient.SendCommandAsync(
            command,
            terminator,
            settings.CommandTimeoutMilliseconds,
            _lifetimeCancellation.Token);
    }

    private async Task<string> QuerySerialMeterAsync(string command)
    {
        var settings = SerialSettings ?? throw new InvalidOperationException("SM7110串口参数未加载。");
        var terminator = SM7110Protocol.DecodeNewLine(settings.NewLine);
        AddSerialLog($"TX [SCPI]  {command}");
        return await _serialClient.QueryAsync(
            command,
            terminator,
            settings.CommandTimeoutMilliseconds,
            _lifetimeCancellation.Token);
    }

    private void UpdateSerialMeterIdentity(string identity)
    {
        SerialMeterIdentityText.Text = identity;
        SerialMeterIdentityText.ToolTip = identity;
    }

    private void UpdateSerialMeterResult(SM7110MeasurementResult result)
    {
        SerialMeterResultStatusText.Text = result.StatusDescription;
        SerialMeterResultStatusText.Foreground = new SolidColorBrush(
            result.IsSuccessful ? Color.FromRgb(73, 209, 125) : Color.FromRgb(242, 122, 128));
        SerialMeterValueText.Text = result.Value.ToString("G9");
        SerialMeterUnitText.Text = result.Unit;
        SerialMeterRawResultText.Text = result.RawResponse;
        SerialMeterRawResultText.ToolTip = result.RawResponse;
        AddSerialLog($"测试结果：{result.StatusDescription}，数值={result.Value:G9} {result.Unit}");
    }

    private async Task SendMeterCommandAsync(string command)
    {
        var timeout = TcpSettings?.CommandTimeoutMilliseconds ?? 5_000;
        AddTcpLog($"TX [SCPI]  {command}");
        await _generalTcpClient.SendCommandAsync(command, timeout, _lifetimeCancellation.Token);
    }

    private async Task<string> QueryMeterAsync(string command)
    {
        var timeout = TcpSettings?.CommandTimeoutMilliseconds ?? 5_000;
        AddTcpLog($"TX [SCPI]  {command}");
        var response = await _generalTcpClient.QueryAsync(command, timeout, _lifetimeCancellation.Token);
        AddTcpLog($"RX [SCPI]  {response}");
        return response;
    }

    private void UpdateMeterIdentity(string identity)
    {
        MeterIdentityText.Text = identity;
        MeterIdentityText.ToolTip = identity;
    }

    private void UpdateMeterResult(E4981AMeasurementResult result)
    {
        MeterResultStatusText.Text = result.StatusDescription;
        MeterResultStatusText.Foreground = new SolidColorBrush(
            result.IsSuccessful ? Color.FromRgb(73, 209, 125) : Color.FromRgb(242, 122, 128));
        MeterCapacitanceText.Text = $"{result.CapacitancePf:0.######} pF";
        MeterDissipationText.Text = result.DissipationFactor.ToString("G9");
        MeterBinText.Text = result.BinDescription;
        MeterRawResultText.Text = result.RawResponse;
        MeterRawResultText.ToolTip = result.RawResponse;
        AddTcpLog(
            $"测试结果：{result.StatusDescription}，C={result.CapacitancePf:0.######} pF，" +
            $"D={result.DissipationFactor:G9}，{result.BinDescription}");
    }

    private void TcpClient_DataReceived(byte[] payload)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Settings is not { } settings)
            {
                return;
            }

            AddLog($"RX [ASCII]  {FormatPayload(payload)}");
        }));
    }

    private void TcpClient_ConnectionClosed(Exception? exception)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetFeederStatus("\u672a\u8fde\u63a5");

            AddLog(exception is null
                ? "TCP \u8fde\u63a5\u5df2\u7531\u5bf9\u7aef\u5173\u95ed"
                : $"TCP \u8fde\u63a5\u4e2d\u65ad\uff1a{exception.Message}");
        }));
    }

    private void GeneralTcpClient_ConnectionClosed(Exception? exception)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetTcpStatus("未连接");
            AddTcpLog(exception is null
                ? "E4981A连接已由仪表关闭"
                : $"E4981A连接中断：{exception.Message}");
        }));
    }

    private void SerialClient_ResponseReceived(string response)
    {
        Dispatcher.BeginInvoke(new Action(() =>
            AddSerialLog($"RX [SCPI]  {response}")));
    }

    private void SerialClient_ConnectionClosed(Exception? exception)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SetSerialStatus("未连接");
            AddSerialLog(exception is null
                ? "SM7110串口连接已关闭"
                : $"SM7110串口连接中断：{exception.Message}");
        }));
    }

    private static byte[] BuildPayload(VibrationFeederSettings settings)
    {
        return BuildPayload(settings.ManualSendText, settings.AppendNewLine, settings.NewLine);
    }

    private static byte[] BuildPayload(string text, bool appendNewLine, string? newLine)
    {
        var payload = Encoding.ASCII.GetBytes(text);

        if (!appendNewLine)
        {
            return payload;
        }

        var newLineBytes = Encoding.ASCII.GetBytes(DecodeNewLine(newLine));
        return [.. payload, .. newLineBytes];
    }

    private static string FormatPayload(byte[] payload)
    {
        return Encoding.ASCII.GetString(payload)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    private static string DecodeNewLine(string? value)
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

    private static void CommitInputBindings(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is TextBox textBox)
            {
                textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            }
            CommitInputBindings(child);
        }
    }
}
