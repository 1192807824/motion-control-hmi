using System.Collections.ObjectModel;
using System.IO;
using System.IO.Ports;
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
    private readonly ObservableCollection<string> _portNames = [];
    private readonly VibrationFeederSettingsStore _settingsStore = new();
    private readonly VibrationFeederSerialClient _serialClient = new();
    private bool _closed;
    private bool _loaded;

    static ConnectionConfigPage()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public ConnectionConfigPage()
    {
        InitializeComponent();
        PortNameComboBox.ItemsSource = _portNames;
        _serialClient.DataReceived += SerialClient_DataReceived;
    }

    private MainWindowViewModel? ViewModel => DataContext as MainWindowViewModel;

    private VibrationFeederSettings? Settings => ViewModel?.FeederSettings;

    public void Shutdown()
    {
        if (_closed)
        {
            return;
        }

        SaveSettings(writeLog: false);
        _serialClient.DataReceived -= SerialClient_DataReceived;
        _serialClient.Dispose();
        _closed = true;
    }

    private void ConnectionConfigPage_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        RefreshPortList();
        AddLog("\u8fde\u63a5\u914d\u7f6e\u9875\u5df2\u52a0\u8f7d");
        _loaded = true;
    }

    private void ConnectionConfigPage_Unloaded(object sender, RoutedEventArgs e)
    {
        Shutdown();
    }

    private void RefreshPorts_Click(object sender, RoutedEventArgs e)
    {
        RefreshPortList();
        AddLog("\u5df2\u5237\u65b0\u4e32\u53e3\u5217\u8868");
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        SaveSettings(writeLog: true);
    }

    private void ConnectFeeder_Click(object sender, RoutedEventArgs e)
    {
        if (Settings is not { } settings)
        {
            return;
        }

        SaveSettings(writeLog: false);

        try
        {
            _serialClient.Open(settings);
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = $"\u5df2\u8fde\u63a5\uff1a{settings.PortName}";
            }

            AddLog($"\u5df2\u6253\u5f00 {settings.PortName}  {settings.BaudRate},{settings.DataBits},{settings.Parity},{settings.StopBits}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = "\u8fde\u63a5\u5931\u8d25";
            }

            AddLog($"\u8fde\u63a5\u5931\u8d25\uff1a{ex.Message}");
        }
    }

    private void DisconnectFeeder_Click(object sender, RoutedEventArgs e)
    {
        _serialClient.Close();

        if (ViewModel is { } viewModel)
        {
            viewModel.FeederConnectionStatusText = "\u672a\u8fde\u63a5";
        }

        AddLog("\u5df2\u65ad\u5f00\u9707\u52a8\u76d8\u4e32\u53e3");
    }

    private void SendManualMessage_Click(object sender, RoutedEventArgs e)
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

        if (!_serialClient.IsOpen)
        {
            AddLog("\u53d1\u9001\u5931\u8d25\uff1a\u8bf7\u5148\u8fde\u63a5\u4e32\u53e3");
            return;
        }

        try
        {
            var payload = BuildPayload(settings);
            _serialClient.Write(payload);
            AddLog($"TX [{NormalizeFormat(settings.SendFormat)}]  {FormatPayload(payload, settings.SendFormat)}");
        }
        catch (Exception ex) when (ex is IOException or FormatException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = "\u901a\u8baf\u5f02\u5e38";
            }

            AddLog($"\u53d1\u9001\u5931\u8d25\uff1a{ex.Message}");
        }
    }

    private void StopVibration_Click(object sender, RoutedEventArgs e)
    {
        if (!_serialClient.IsOpen)
        {
            AddLog("\u505c\u6b62\u9707\u52a8\u5931\u8d25\uff1a\u8bf7\u5148\u8fde\u63a5\u4e32\u53e3");
            return;
        }

        try
        {
            var payload = Encoding.ASCII.GetBytes(StopVibrationCommand);
            _serialClient.Write(payload);
            AddLog($"TX [ASCII]  {StopVibrationCommand}  \u505c\u6b62\u9707\u52a8");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            if (ViewModel is { } viewModel)
            {
                viewModel.FeederConnectionStatusText = "\u901a\u8baf\u5f02\u5e38";
            }

            AddLog($"\u505c\u6b62\u9707\u52a8\u5931\u8d25\uff1a{ex.Message}");
        }
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e)
    {
        ViewModel?.FeederConnectionLogs.Clear();
    }

    private void RefreshPortList()
    {
        var selectedPort = Settings?.PortName;
        _portNames.Clear();

        foreach (var portName in SerialPort.GetPortNames().OrderBy(item => item, StringComparer.OrdinalIgnoreCase))
        {
            _portNames.Add(portName);
        }

        if (!string.IsNullOrWhiteSpace(selectedPort) &&
            !_portNames.Contains(selectedPort, StringComparer.OrdinalIgnoreCase))
        {
            _portNames.Insert(0, selectedPort);
        }
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

    private void SerialClient_DataReceived(byte[] payload)
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
