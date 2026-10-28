using System.Windows;
using ControlHub.Services.Persistence;
using ControlHub.Views.Pages;

namespace ControlHub.Views.Dialogs;

public sealed class BatchQueryWindow : Window
{
    public BatchQueryWindow(BatchMeasurementStore store, string batch)
    {
        Title = "数据查询与导出";
        Width = 1150; Height = 800; MinWidth = 800; MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var page = new BatchQueryPage(store);
        Content = page;
        Loaded += async (_, _) => await page.ActivateAsync(batch);
    }
}
