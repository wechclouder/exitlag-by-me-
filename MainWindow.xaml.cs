using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using wetwiLAG.Models;
using wetwiLAG.ViewModels;

namespace wetwiLAG;

public partial class MainWindow : Window
{
    private Point _dragStartPoint;
    private DashboardWidget? _dragWidget;
    private bool _shutdownStarted;
    private bool _allowClose;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void WidgetsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(WidgetsList);
        _dragWidget = e.OriginalSource is DependencyObject source &&
                      FindVisualParent<Button>(source) is null
            ? FindWidget(source)
            : null;
    }

    private void WidgetsList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragWidget is null)
        {
            return;
        }

        var current = e.GetPosition(WidgetsList);
        if (Math.Abs(current.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(current.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        var source = _dragWidget;
        _dragWidget = null;
        DragDrop.DoDragDrop(WidgetsList, source.Id, DragDropEffects.Move);
    }

    private void WidgetsList_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(typeof(string)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void WidgetsList_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(string)) is string sourceId &&
            e.OriginalSource is DependencyObject targetSource &&
            FindWidget(targetSource) is { } target &&
            DataContext is MainViewModel viewModel)
        {
            viewModel.ReorderWidgets(sourceId, target.Id);
            e.Effects = DragDropEffects.Move;
        }

        _dragWidget = null;
        e.Handled = true;
    }

    private static DashboardWidget? FindWidget(DependencyObject source)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is FrameworkElement element && element.DataContext is DashboardWidget widget)
            {
                return widget;
            }
        }

        return null;
    }

    private static T? FindVisualParent<T>(DependencyObject source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (current is T match)
            {
                return match;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject current) =>
        current switch
        {
            Visual visual => VisualTreeHelper.GetParent(visual),
            Visual3D visual3D => VisualTreeHelper.GetParent(visual3D),
            FrameworkContentElement contentElement => contentElement.Parent,
            ContentElement content => ContentOperations.GetParent(content),
            _ => null
        };

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose)
        {
            return;
        }

        e.Cancel = true;
        if (_shutdownStarted)
        {
            return;
        }

        _shutdownStarted = true;
        IsEnabled = false;
        if (DataContext is MainViewModel viewModel)
        {
            await viewModel.DisposeAsync();
        }

        _allowClose = true;
        Close();
    }
}
