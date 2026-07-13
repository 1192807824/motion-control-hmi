using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.ViewModels;

namespace ControlHub.Views.Pages;

public partial class ConnectionConfigPage : UserControl
{
    private const int MaxConnectionLogCount = 300;
    private const string StopVibrationCommand = "&04$";
    private const string LightControlCommandName = "\u5149\u6e90\u63a7\u5236";
    private const string LightOnCommand = "&07,1$";
    private const string LightOffCommand = "&07,0$";
    private readonly VibrationFeederSettingsStore _settingsStore = new();
    private readonly VibrationFeederTcpClient _tcpClient = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private bool _closed;
    private bool _connecting;
    private bool _loaded;
    private bool _vibrationSequenceRunning;

    static ConnectionConfigPage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ConnectionConfigPage()
    {
        InitializeComponent();
        _tcpClient.DataReceived += TcpClient_DataReceived;
        _tcpClient.ConnectionClosed += TcpClient_ConnectionClosed;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private VibrationFeederSettings? Settings => ViewModel?.FeederSettings;

    public void Shutdown()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        SaveSettings(writeLog: false);
        _lifetimeCancellation.Cancel();
        _tcpClient.DataReceived -= TcpClient_DataReceived;
        _tcpClient.ConnectionClosed -= TcpClient_ConnectionClosed;
        _tcpClient.Dispose();
        _lifetimeCancellation.Dispose();
    }

    private void ConnectionConfigPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        AddLog("\u8fde\u63a5\u914d\u7f6e\u9875\u5df2\u52a0\u8f7d");
        _loaded = true;
    }

    private void ConnectionConfigPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Shutdown();
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
        _connecting = true;

        try
        {
            await _tcpClient.ConnectAsync(settings, _lifetimeCancellation.Token);
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = $"\u5df2\u8fde\u63a5\uff1a{settings.Host}:{settings.Port}";
            }

            AddLog($"TCP \u5df2\u8fde\u63a5 {settings.Host}:{settings.Port}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidOperationException or ArgumentException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = "\u8fde\u63a5\u5931\u8d25";
            }

            AddLog($"\u8fde\u63a5\u5931\u8d25\uff1a{ex.Message}");
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        catch (OperationCanceledException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = "\u672a\u8fde\u63a5";
            }

            AddLog("TCP \u8fde\u63a5\u5df2\u53d6\u6d88");
        }
        finally
        {
            _connecting = false;
        }
    }

    private void DisconnectFeeder_Click(object sender, RoutedEventArgs e)
    {
        _tcpClient.Close();

        if (ViewModel is { } viewModel)
        {
            viewModel.FeederConnectionStatusText = "\u672a\u8fde\u63a5";
        }

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
            AddLog($"TX [{NormalizeFormat(settings.SendFormat)}]  {FormatPayload(payload, settings.SendFormat)}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or FormatException or InvalidOperationException or ObjectDisposedException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = "\u901a\u8baf\u5f02\u5e38";
            }

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

        try
        {
            var payload = Encoding.ASCII.GetBytes(StopVibrationCommand);
            await _tcpClient.WriteAsync(payload);
            AddLog($"TX [ASCII]  {StopVibrationCommand}  \u505c\u6b62\u9707\u52a8");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = "\u901a\u8baf\u5f02\u5e38";
            }

            AddLog($"\u505c\u6b62\u9707\u52a8\u5931\u8d25\uff1a{ex.Message}");
        }
    }

    private async void OneKeyVibration_Click(object sender, RoutedEventArgs e)
    {
        if (!_tcpClient.IsConnected)
        {
            AddLog("\u4e00\u952e\u9707\u52a8\u5931\u8d25\uff1a\u8bf7\u5148\u5efa\u7acb TCP \u8fde\u63a5");
            return;
        }

        if (_vibrationSequenceRunning)
        {
            AddLog("\u4e00\u952e\u9707\u52a8\u6b63\u5728\u6267\u884c\uff0c\u8bf7\u7a0d\u5019");
            return;
        }

        _vibrationSequenceRunning = true;
        try
        {
            AddLog("\u4e00\u952e\u9707\u52a8\u5f00\u59cb\uff1a\u8f7b\u9707\u6563 -> \u5de6\u53f3\u805a\u62e2 -> \u5411\u4e0a\u9707\u52a8");
            await SendAsciiProtocolCommandAsync("&05,00$", "\u5207\u6362\u6b63\u5e38\u6a21\u5f0f");

            await RunVibrationPulseAsync(
                "&02,060,025,1,060,025,1,060,025,1,060,025,1,04$",
                "&03,04$",
                300,
                "\u8f7b\u9707\u6563");

            await RunVibrationPulseAsync(
                "&02,080,045,1,080,045,1,080,045,1,080,045,1,05$",
                "&03,05$",
                1800,
                "\u5de6\u53f3\u805a\u62e2");

            await RunVibrationPulseAsync(
                "&02,050,024,1,050,024,1,050,024,1,050,024,1,01$",
                "&03,01$",
                1000,
                "\u5411\u4e0a\u9707\u52a8");

            AddLog("\u4e00\u952e\u9707\u52a8\u5b8c\u6210");
        }
        catch (OperationCanceledException) when (_closed)
        {
        }
        finally
        {
            _vibrationSequenceRunning = false;
        }
    }

    private async void LightOn_Click(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        SaveSettings(writeLog: false);
        var normalizedBrightness = Math.Clamp(settings.LightOnBrightness, 0, 99);
        if (settings.LightOnBrightness != normalizedBrightness)
        {
            settings.LightOnBrightness = normalizedBrightness;
            _settingsStore.Save(settings);
        }

        await SendAsciiProtocolCommandAsync($"&06,{normalizedBrightness:00}$", "\u8bbe\u7f6e\u5149\u6e90\u4eae\u5ea6");
        await SendAsciiProtocolCommandAsync(LightOnCommand, "\u5149\u6e90\u6253\u5f00");
    }

    private async void LightOff_Click(object sender, RoutedEventArgs e)
    {
        await SendAsciiProtocolCommandAsync(LightOffCommand, "\u5149\u6e90\u5173\u95ed");
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

    private async Task SendAsciiProtocolCommandAsync(string command, string actionName)
    {
        if (!_tcpClient.IsConnected)
        {
            AddLog($"{actionName}\u5931\u8d25\uff1a\u8bf7\u5148\u5efa\u7acb TCP \u8fde\u63a5");
            return;
        }

        try
        {
            var payload = Encoding.ASCII.GetBytes(command);
            await _tcpClient.WriteAsync(payload);
            AddLog($"TX [ASCII]  {command}  {LightControlCommandName}-{actionName}");
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or InvalidOperationException or ObjectDisposedException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = "\u901a\u8baf\u5f02\u5e38";
            }

            AddLog($"{actionName}\u5931\u8d25\uff1a{ex.Message}");
        }
    }

    private async Task RunVibrationPulseAsync(string parameterCommand, string startCommand, int durationMs, string actionName)
    {
        await SendAsciiProtocolCommandAsync(parameterCommand, $"{actionName}-\u4e0b\u53d1\u53c2\u6570");
        await SendAsciiProtocolCommandAsync(startCommand, $"{actionName}-\u542f\u52a8");
        await Task.Delay(durationMs, _lifetimeCancellation.Token);
        await SendAsciiProtocolCommandAsync(StopVibrationCommand, $"{actionName}-\u505c\u6b62");
        await Task.Delay(120, _lifetimeCancellation.Token);
    }

    private void TcpClient_DataReceived(byte[] payload)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Settings is not { } settings)
            {
                return;
            }

            AddLog($"RX [{NormalizeFormat(settings.ReceiveFormat)}]  {FormatPayload(payload, settings.ReceiveFormat)}");
        }));
    }

    private void TcpClient_ConnectionClosed(Exception? exception)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = "\u672a\u8fde\u63a5";
            }

            AddLog(exception is null
                ? "TCP \u8fde\u63a5\u5df2\u7531\u5bf9\u7aef\u5173\u95ed"
                : $"TCP \u8fde\u63a5\u4e2d\u65ad\uff1a{exception.Message}");
        }));
    }

    private static byte[] BuildPayload(VibrationFeederSettings settings)
    {
        var format = NormalizeFormat(settings.SendFormat);
        var payload = format == "HEX"
            ? ParseHex(settings.ManualSendText)
            : ResolveEncoding(format).GetBytes(settings.ManualSendText);

        if (!settings.AppendNewLine)
        {
            return payload;
        }

        var newLineBytes = ResolveEncoding(format).GetBytes(DecodeNewLine(settings.NewLine));
        return [.. payload, .. newLineBytes];
    }

    private static string FormatPayload(byte[] payload, string? format)
    {
        var normalizedFormat = NormalizeFormat(format);
        return normalizedFormat == "HEX"
            ? ToHex(payload)
            : ResolveEncoding(normalizedFormat).GetString(payload)
                .Replace("\r", "\\r", StringComparison.Ordinal)
                .Replace("\n", "\\n", StringComparison.Ordinal)
                .Replace("\t", "\\t", StringComparison.Ordinal);
    }

    private static byte[] ParseHex(string text)
    {
        var normalized = text
            .Replace("0x", "", StringComparison.OrdinalIgnoreCase)
            .Replace(",", " ", StringComparison.Ordinal)
            .Replace(";", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal);

        var compact = string.Concat(normalized.Where(item => !char.IsWhiteSpace(item)));
        if (compact.Length == 0)
        {
            return [];
        }

        if (compact.Length % 2 != 0)
        {
            throw new FormatException("HEX\u683c\u5f0f\u9700\u8981\u5076\u6570\u4e2a\u5b57\u7b26\uff0c\u4f8b\u5982 01 03 00 00\u3002");
        }

        var bytes = new byte[compact.Length / 2];
        for (var index = 0; index < bytes.Length; index++)
        {
            bytes[index] = Convert.ToByte(compact.Substring(index * 2, 2), 16);
        }

        return bytes;
    }

    private static string ToHex(byte[] payload)
    {
        return string.Join(" ", payload.Select(item => item.ToString("X2")));
    }

    private static string NormalizeFormat(string? format)
    {
        return string.IsNullOrWhiteSpace(format) ? "ASCII" : format.Trim().ToUpperInvariant();
    }

    private static Encoding ResolveEncoding(string format)
    {
        return format switch
        {
            "UTF-8" => Encoding.UTF8,
            "GB2312" => Encoding.GetEncoding("GB2312"),
            _ => Encoding.ASCII
        };
    }

    private static string DecodeNewLine(string? value)
    {
        if (string.IsNullOrEmpty(value))
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
            else if (child is ComboBox comboBox)
            {
                comboBox.GetBindingExpression(ComboBox.TextProperty)?.UpdateSource();
                comboBox.GetBindingExpression(Selector.SelectedValueProperty)?.UpdateSource();
            }

            CommitInputBindings(child);
        }
    }
}
