using System.Windows;
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
            if (_startSequenceRunning || _oneKeyCollectRunning || _oneKeyResetRunning || _presetPositionMoveRunning)
                throw new InvalidOperationException("请等待当前生产、复位或移动流程结束后写入。");
            var controller = _motionController ?? throw new InvalidOperationException("运动控制卡尚未连接。");
            _drive5018Writing = true;
            UpdateHomeCommandState();
            WriteDrive5018Button.IsEnabled = false;
            Drive5018ValueTextBox.IsEnabled = false;
            SetDrive5018Status($"四个Z轴正在写入{value}并逐轴回读…", null);
            var results = await controller.WriteAllZAxes5018Async(value);
            SetDrive5018Status(DriveObject5018.DescribeResults(results), results.Count == 4 && results.All(result => result.Error is null));
        }
        catch (Exception exception) { SetDrive5018Status(exception.Message, false); }
        finally
        {
            _drive5018Writing = false;
            WriteDrive5018Button.IsEnabled = true;
            Drive5018ValueTextBox.IsEnabled = true;
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
