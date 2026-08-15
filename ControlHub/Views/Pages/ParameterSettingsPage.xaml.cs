using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ControlHub.Services.Devices;
using ControlHub.Services.Persistence;
using ControlHub.Services.Vision;
using ControlHub.ViewModels;

namespace ControlHub.Views.Pages;

public partial class ParameterSettingsPage : UserControl
{
    private readonly ProductRecipeStore _recipeStore = new();
    private readonly ObservableCollection<ProductRecipe> _recipes = [];
    private HomePage? _homePage;
    private MotionControlPage? _motionPage;
    private VisualCalibrationPage? _visualCalibrationPage;
    private MainWindowViewModel? _viewModel;
    private bool _recipeBusy;

    public event EventHandler<string?>? ActiveRecipeChanged;

    public ParameterSettingsPage()
    {
        InitializeComponent();
        RecipeListBox.ItemsSource = _recipes;
        ShowVisionProcedureNames(null);
    }

    public void AttachRecipeContext(
        HomePage homePage,
        MotionControlPage motionPage,
        VisualCalibrationPage visualCalibrationPage,
        MainWindowViewModel viewModel)
    {
        _homePage = homePage ?? throw new ArgumentNullException(nameof(homePage));
        _motionPage = motionPage ?? throw new ArgumentNullException(nameof(motionPage));
        _visualCalibrationPage = visualCalibrationPage
                                 ?? throw new ArgumentNullException(nameof(visualCalibrationPage));
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        var activeRecipeId = _recipeStore.GetActiveRecipeId();
        if (!string.IsNullOrWhiteSpace(activeRecipeId))
        {
            var activeRecipe = _recipeStore.LoadAll().FirstOrDefault(recipe =>
                string.Equals(recipe.Id, activeRecipeId, StringComparison.OrdinalIgnoreCase));
            if (activeRecipe is not null)
            {
                _visualCalibrationPage.ConfigureRecipeVisionProcedureNames(activeRecipe.VisionProcedureNames);
            }
        }
        RefreshRecipeList(activeRecipeId);
        RaiseActiveRecipeChanged();
    }

    private ProductRecipe? SelectedRecipe => RecipeListBox.SelectedItem as ProductRecipe;

    private void RecipeListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedRecipe is not { } recipe)
        {
            RecipeNameTextBox.Text = "";
            ShowVisionProcedureNames(null);
            return;
        }

        RecipeNameTextBox.Text = recipe.Name;
        ShowVisionProcedureNames(recipe.VisionProcedureNames);
        SetRecipeStatus(
            $"已选择“{recipe.Name}”；包含生产参数、{recipe.Axes.Count}根轴、视觉标定、" +
            $"7个视觉流程名称及仪表参数。",
            success: true);
    }

    private void CreateRecipe_Click(object sender, RoutedEventArgs e)
    {
        RunRecipeOperation(() =>
        {
            var recipe = _recipeStore.Create(RecipeNameTextBox.Text);
            CaptureCurrentConfiguration(recipe);
            _recipeStore.Save(recipe);
            RefreshRecipeList(recipe.Id);
            SetRecipeStatus($"配方“{recipe.Name}”已新建并保存。", success: true);
        });
    }

    private void SaveCurrentRecipe_Click(object sender, RoutedEventArgs e)
    {
        CommitPendingInput();
        RunRecipeOperation(() =>
        {
            var recipe = SelectedRecipe;
            if (recipe is null)
            {
                recipe = _recipeStore.Create(RecipeNameTextBox.Text);
            }
            else if (!string.IsNullOrWhiteSpace(RecipeNameTextBox.Text))
            {
                recipe.Name = RecipeNameTextBox.Text.Trim();
            }

            CaptureCurrentConfiguration(recipe);
            _recipeStore.Save(recipe);
            RefreshRecipeList(recipe.Id);
            SetRecipeStatus($"当前整机参数和视觉方案已保存到“{recipe.Name}”。", success: true);
        });
    }

    private async void ApplyRecipe_Click(object sender, RoutedEventArgs e)
    {
        await ApplySelectedRecipeAsync();
    }

    private void RenameRecipe_Click(object sender, RoutedEventArgs e)
    {
        RunRecipeOperation(() =>
        {
            var recipe = SelectedRecipe
                         ?? throw new InvalidOperationException("请先选择需要重命名的配方。");
            recipe.Name = RecipeNameTextBox.Text.Trim();
            _recipeStore.Save(recipe);
            RefreshRecipeList(recipe.Id);
            RaiseActiveRecipeChanged();
            SetRecipeStatus($"配方已重命名为“{recipe.Name}”。", success: true);
        });
    }

    private void DeleteRecipe_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRecipe is not { } recipe)
        {
            SetRecipeStatus("请先选择需要删除的配方。", success: false);
            return;
        }

        if (MessageBox.Show(
                Window.GetWindow(this),
                $"确定删除配方“{recipe.Name}”吗？",
                "删除产品配方",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        RunRecipeOperation(() =>
        {
            _recipeStore.Delete(recipe.Id);
            RefreshRecipeList();
            RaiseActiveRecipeChanged();
            SetRecipeStatus($"配方“{recipe.Name}”已删除。", success: true);
        });
    }

    private void SaveVisionProcedureNames_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedRecipe is not { } recipe)
        {
            SetRecipeStatus("请先选择需要编辑视觉流程的配方。", success: false);
            return;
        }

        RunRecipeOperation(() =>
        {
            recipe.VisionProcedureNames = ReadVisionProcedureNames();
            _recipeStore.Save(recipe);
            RefreshRecipeList(recipe.Id);
            SetRecipeStatus("视觉流程名称已保存；点击“应用”后按新名称加载流程。", success: true);
        });
    }

    private void EmergencyStopAllAxes_Click(object sender, RoutedEventArgs e)
    {
        if (_motionPage is null)
        {
            SetRecipeStatus("运动控制未连接，无法下发全轴急停。", success: false);
            return;
        }

        var issued = _motionPage.EmergencyStopAllAxes("参数配置页操作员请求全轴急停");
        SetRecipeStatus(
            issued ? "全轴急停已下发，正在确认所有轴停止。" : "急停下发失败，请立即按硬件急停。",
            success: issued);
    }

    private void ShowVisionProcedureNames(VisionProcedureNames? names)
    {
        var enabled = names is not null;
        names ??= new VisionProcedureNames
        {
            Inspection = "",
            NozzleTeaching = "",
            Calibration = "",
            LowerCameraCalibration = "",
            RotationPoint = "",
            RotationCenter = "",
            LowerCameraCorrection = ""
        };

        InspectionProcedureNameTextBox.Text = names.Inspection;
        NozzleTeachingProcedureNameTextBox.Text = names.NozzleTeaching;
        CalibrationProcedureNameTextBox.Text = names.Calibration;
        LowerCalibrationProcedureNameTextBox.Text = names.LowerCameraCalibration;
        RotationPointProcedureNameTextBox.Text = names.RotationPoint;
        RotationCenterProcedureNameTextBox.Text = names.RotationCenter;
        LowerCorrectionProcedureNameTextBox.Text = names.LowerCameraCorrection;

        InspectionProcedureNameTextBox.IsEnabled = enabled;
        NozzleTeachingProcedureNameTextBox.IsEnabled = enabled;
        CalibrationProcedureNameTextBox.IsEnabled = enabled;
        LowerCalibrationProcedureNameTextBox.IsEnabled = enabled;
        RotationPointProcedureNameTextBox.IsEnabled = enabled;
        RotationCenterProcedureNameTextBox.IsEnabled = enabled;
        LowerCorrectionProcedureNameTextBox.IsEnabled = enabled;
        SaveVisionProcedureNamesButton.IsEnabled = enabled;
    }

    private VisionProcedureNames ReadVisionProcedureNames()
    {
        var names = new VisionProcedureNames
        {
            Inspection = InspectionProcedureNameTextBox.Text.Trim(),
            NozzleTeaching = NozzleTeachingProcedureNameTextBox.Text.Trim(),
            Calibration = CalibrationProcedureNameTextBox.Text.Trim(),
            LowerCameraCalibration = LowerCalibrationProcedureNameTextBox.Text.Trim(),
            RotationPoint = RotationPointProcedureNameTextBox.Text.Trim(),
            RotationCenter = RotationCenterProcedureNameTextBox.Text.Trim(),
            LowerCameraCorrection = LowerCorrectionProcedureNameTextBox.Text.Trim()
        };
        names.Validate();
        return names;
    }

    private async Task ApplySelectedRecipeAsync()
    {
        if (_recipeBusy)
        {
            return;
        }

        if (SelectedRecipe is not { } recipe)
        {
            SetRecipeStatus("请先选择需要应用的配方。", success: false);
            return;
        }

        _recipeBusy = true;
        IsEnabled = false;
        try
        {
            SetRecipeStatus($"正在应用“{recipe.Name}”并重载视觉流程…", success: true);
            _homePage!.ApplyRecipeSettings(recipe.Production);
            _motionPage!.ApplyRecipeAxisSettings(recipe.Axes);
            _motionPage.ApplyRecipeMotionSettings(recipe.Motion);
            VisionCalibrationService.Shared.ApplyRecipeSettings(recipe.VisionCalibration);
            _visualCalibrationPage!.RefreshRecipeSettings();
            ApplyDeviceSettings(recipe);

            await _visualCalibrationPage.ApplyRecipeVisionSolutionAsync(
                _visualCalibrationPage.GetCurrentVisionSolutionPath(),
                recipe.VisionProcedureNames);

            _recipeStore.SetActiveRecipeId(recipe.Id);
            RaiseActiveRecipeChanged();
            SetRecipeStatus($"配方“{recipe.Name}”已应用，视觉流程已重载。", success: true);
        }
        catch (Exception exception)
        {
            SetRecipeStatus($"应用配方失败：{exception.Message}", success: false);
        }
        finally
        {
            IsEnabled = true;
            _recipeBusy = false;
        }
    }

    private void CaptureCurrentConfiguration(ProductRecipe recipe)
    {
        recipe.Production = _homePage!.CaptureRecipeSettings();
        recipe.Axes = _motionPage!.CaptureRecipeAxisSettings().ToDictionary(
            pair => pair.Key,
            pair => pair.Value);
        recipe.Motion = _motionPage.CaptureRecipeMotionSettings();
        recipe.VisionCalibration = VisionCalibrationService.Shared.CaptureRecipeSettings();
        recipe.VisionProcedureNames = _visualCalibrationPage!.GetCurrentVisionProcedureNames();
        recipe.E4981A = ProductRecipeStore.Clone(_viewModel!.TcpConnectionSettings);
        recipe.SM7110 = ProductRecipeStore.Clone(_viewModel.SerialConnectionSettings);
        recipe.VibrationFeeder = ProductRecipeStore.Clone(_viewModel.FeederSettings);
    }

    private void CommitPendingInput()
    {
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), this);
        Keyboard.ClearFocus();
    }

    private void ApplyDeviceSettings(ProductRecipe recipe)
    {
        CopyWritableProperties(recipe.E4981A, _viewModel!.TcpConnectionSettings);
        CopyWritableProperties(recipe.SM7110, _viewModel.SerialConnectionSettings);
        CopyWritableProperties(recipe.VibrationFeeder, _viewModel.FeederSettings);
        new TcpConnectionSettingsStore().Save(_viewModel.TcpConnectionSettings);
        new SerialConnectionSettingsStore().Save(_viewModel.SerialConnectionSettings);
        new VibrationFeederSettingsStore().Save(_viewModel.FeederSettings);
    }

    private static void CopyWritableProperties<T>(T source, T destination)
    {
        foreach (var property in typeof(T).GetProperties()
                     .Where(property => property.CanRead && property.CanWrite))
        {
            property.SetValue(destination, property.GetValue(source));
        }
    }

    private void RefreshRecipeList(string? selectedRecipeId = null)
    {
        var targetId = selectedRecipeId ?? SelectedRecipe?.Id;
        _recipes.Clear();
        foreach (var recipe in _recipeStore.LoadAll())
        {
            _recipes.Add(recipe);
        }

        RecipeListBox.SelectedItem = _recipes.FirstOrDefault(recipe =>
            string.Equals(recipe.Id, targetId, StringComparison.OrdinalIgnoreCase))
            ?? _recipes.FirstOrDefault(recipe => string.Equals(
                recipe.Id,
                _recipeStore.GetActiveRecipeId(),
                StringComparison.OrdinalIgnoreCase))
            ?? _recipes.FirstOrDefault();
    }

    private void RaiseActiveRecipeChanged()
    {
        var activeId = _recipeStore.GetActiveRecipeId();
        var activeName = _recipes.FirstOrDefault(recipe => string.Equals(
            recipe.Id,
            activeId,
            StringComparison.OrdinalIgnoreCase))?.Name;
        ActiveRecipeChanged?.Invoke(this, activeName);
    }

    private void RunRecipeOperation(Action operation)
    {
        if (_recipeBusy)
        {
            return;
        }

        try
        {
            operation();
        }
        catch (Exception exception)
        {
            SetRecipeStatus(exception.Message, success: false);
        }
    }

    private void SetRecipeStatus(string message, bool success)
    {
        RecipeStatusText.Text = message;
        RecipeStatusText.Foreground = new SolidColorBrush(
            success ? Color.FromRgb(73, 209, 125) : Color.FromRgb(242, 122, 128));
    }

    public void AttachSettingsContent(FrameworkElement content)
    {
        if (content.Parent is Panel panel)
        {
            panel.Children.Remove(content);
        }
        else if (content.Parent is ContentControl contentControl)
        {
            contentControl.Content = null;
        }

        PrepareCardLayout(content);
        SettingsContentHost.Content = content;
        content.Visibility = Visibility.Visible;
    }

    private static void PrepareCardLayout(FrameworkElement content)
    {
        if (content is not ScrollViewer scrollViewer ||
            scrollViewer.Content is not Panel sourcePanel)
        {
            return;
        }

        var cardGrid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top
        };
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition());
        cardGrid.ColumnDefinitions.Add(new ColumnDefinition());

        var rowIndex = 0;
        var columnIndex = 0;
        while (sourcePanel.Children.Count > 0)
        {
            var child = sourcePanel.Children[0];
            sourcePanel.Children.RemoveAt(0);
            if (child is Border card)
            {
                card.Margin = new Thickness(8);
                card.Padding = new Thickness(18, 14, 18, 16);
                card.CornerRadius = new CornerRadius(8);
                card.MinHeight = 202;
            }

            var fullWidth =
                child is FrameworkElement element &&
                string.Equals(element.Tag as string, "FullWidth", StringComparison.Ordinal);
            if (fullWidth && columnIndex != 0)
            {
                rowIndex++;
                columnIndex = 0;
            }

            while (cardGrid.RowDefinitions.Count <= rowIndex)
            {
                cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetRow(child, rowIndex);
            Grid.SetColumn(child, columnIndex);
            Grid.SetColumnSpan(child, fullWidth ? 2 : 1);
            cardGrid.Children.Add(child);
            if (fullWidth || columnIndex == 1)
            {
                rowIndex++;
                columnIndex = 0;
            }
            else
            {
                columnIndex = 1;
            }
        }

        scrollViewer.Content = cardGrid;
        scrollViewer.Padding = new Thickness(0);
        PolishControls(cardGrid);
    }

    private static void PolishControls(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            switch (child)
            {
                case TextBox textBox when
                    string.Equals(textBox.Tag as string, "CompactParameter", StringComparison.Ordinal):
                    textBox.Height = 30;
                    textBox.FontSize = 12;
                    textBox.Padding = new Thickness(5, 0, 5, 0);
                    textBox.BorderThickness = new Thickness(1);
                    textBox.Background = new SolidColorBrush(Color.FromRgb(23, 52, 74));
                    textBox.Foreground = new SolidColorBrush(Color.FromRgb(234, 242, 247));
                    textBox.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 95, 120));
                    textBox.CaretBrush = Brushes.White;
                    textBox.SelectionBrush = new SolidColorBrush(Color.FromRgb(31, 117, 200));
                    break;
                case TextBox textBox:
                    textBox.Height = 34;
                    textBox.FontSize = 13;
                    textBox.Padding = new Thickness(8, 0, 8, 0);
                    textBox.BorderThickness = new Thickness(1);
                    textBox.Background = new SolidColorBrush(Color.FromRgb(23, 52, 74));
                    textBox.Foreground = new SolidColorBrush(Color.FromRgb(234, 242, 247));
                    textBox.BorderBrush = new SolidColorBrush(Color.FromRgb(59, 95, 120));
                    textBox.CaretBrush = Brushes.White;
                    textBox.SelectionBrush = new SolidColorBrush(Color.FromRgb(31, 117, 200));
                    break;
                case Button button when button.Height <= 25:
                    button.Height = 34;
                    button.FontSize = 12;
                    button.FontWeight = FontWeights.SemiBold;
                    break;
                case TextBlock textBlock when textBlock.FontSize <= 9:
                    textBlock.FontSize = 11;
                    break;
                case TextBlock textBlock when textBlock.FontSize <= 10:
                    textBlock.FontSize = 12;
                    break;
                case TextBlock textBlock when
                    textBlock.FontSize <= 12 &&
                    textBlock.FontWeight == FontWeights.Bold:
                    textBlock.FontSize = 16;
                    break;
            }

            PolishControls(child);
        }
    }
}
