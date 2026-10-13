using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ControlHub.Services.Motion;

namespace ControlHub.Views.Pages;

public partial class HomePage
{
    private bool _drive5018Writing;

    private async void WriteDrive5018_Click(object sender, RoutedEventArgs e)
    {
        if (_drive5018Writing) return;
        try
        {
            var value = DriveObject5018.ParseValue(Drive5018ValueTextBox.Text);
            var axis = int.Parse(((ComboBoxItem)Drive5018AxisComboBox.SelectedItem).Tag.ToString()!);
            if (_startSequenceRunning || _oneKeyCollectRunning || _oneKeyResetRunning || _presetPositionMoveRunning)
                throw new InvalidOperationException("请等待当前生产、复位或移动流程结束后写入。");
            var controller = _motionController ?? throw new InvalidOperationException("运动控制卡尚未连接。");
            _drive5018Writing = true;
            UpdateHomeCommandState();
            WriteDrive5018Button.IsEnabled = false;
            Drive5018AxisComboBox.IsEnabled = Drive5018ValueTextBox.IsEnabled = false;
            SetDrive5018Status($"轴{axis}正在写入{value}并回读…", null);
            var actual = await controller.WriteZAxis5018Async(axis, value);
            SetDrive5018Status($"写入成功：轴{axis}，回读值 {actual}（{DateTime.Now:HH:mm:ss}）", true);
        }
        catch (Exception exception) { SetDrive5018Status(exception.Message, false); }
        finally
        {
            _drive5018Writing = false;
            WriteDrive5018Button.IsEnabled = true;
            Drive5018AxisComboBox.IsEnabled = Drive5018ValueTextBox.IsEnabled = true;
            UpdateHomeCommandState();
        }
    }

    private void SetDrive5018Status(string text, bool? success)
    {
        Drive5018StatusText.Text = text;
        Drive5018StatusText.ToolTip = text;
        Drive5018StatusText.Foreground = new SolidColorBrush(success switch
        {
            true => Color.FromRgb(73, 209, 125),
            false => Color.FromRgb(242, 122, 128),
            _ => Color.FromRgb(242, 181, 68)
        });
    }
}
