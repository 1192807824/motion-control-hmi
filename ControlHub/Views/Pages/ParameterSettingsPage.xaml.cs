using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace ControlHub.Views.Pages;

public partial class ParameterSettingsPage : UserControl
{
    public ParameterSettingsPage()
    {
        InitializeComponent();
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

        var cardIndex = 0;
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

            var rowIndex = cardIndex / 2;
            if (cardIndex % 2 == 0)
            {
                cardGrid.RowDefinitions.Add(
                    new RowDefinition { Height = GridLength.Auto });
            }

            Grid.SetRow(child, rowIndex);
            Grid.SetColumn(child, cardIndex % 2);
            cardGrid.Children.Add(child);
            cardIndex++;
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
