using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ScheduleOS.Desktop.Core;

/// <summary>Opens a button's ContextMenu on left click (dropdown menus).</summary>
public static class MenuButton
{
    public static readonly DependencyProperty OpenOnClickProperty = DependencyProperty.RegisterAttached(
        "OpenOnClick", typeof(bool), typeof(MenuButton), new PropertyMetadata(false, OnChanged));

    public static bool GetOpenOnClick(DependencyObject d) => (bool)d.GetValue(OpenOnClickProperty);
    public static void SetOpenOnClick(DependencyObject d, bool value) => d.SetValue(OpenOnClickProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase button) return;
        button.Click -= OnClick;
        if (e.NewValue is true) button.Click += OnClick;
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.ContextMenu is not { } menu) return;
        menu.DataContext = fe.DataContext;
        menu.PlacementTarget = fe;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
        e.Handled = true;
    }
}
