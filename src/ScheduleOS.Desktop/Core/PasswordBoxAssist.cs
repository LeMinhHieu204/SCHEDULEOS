using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ScheduleOS.Desktop.Core;

/// <summary>Allows binding a PasswordBox's password to a view model property.</summary>
public static class PasswordBoxAssist
{
    public static readonly DependencyProperty BoundPasswordProperty = DependencyProperty.RegisterAttached(
        "BoundPassword", typeof(string), typeof(PasswordBoxAssist),
        // Default is null (not "") so that binding the initial empty value still raises the change callback that attaches the handler.
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundPasswordChanged));

    private static readonly DependencyProperty IsUpdatingProperty =
        DependencyProperty.RegisterAttached("IsUpdating", typeof(bool), typeof(PasswordBoxAssist));

    public static string? GetBoundPassword(DependencyObject d) => (string?)d.GetValue(BoundPasswordProperty);
    public static void SetBoundPassword(DependencyObject d, string? value) => d.SetValue(BoundPasswordProperty, value);

    private static void OnBoundPasswordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not PasswordBox box) return;
        box.PasswordChanged -= OnPasswordChanged;
        if (!(bool)box.GetValue(IsUpdatingProperty)) box.Password = e.NewValue as string ?? "";
        box.PasswordChanged += OnPasswordChanged;
    }

    private static void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        var box = (PasswordBox)sender;
        box.SetValue(IsUpdatingProperty, true);
        SetBoundPassword(box, box.Password);
        box.SetValue(IsUpdatingProperty, false);
    }
}

/// <summary>Focus helpers usable from XAML.</summary>
public static class FocusAssist
{
    public static readonly DependencyProperty FocusOnLoadProperty = DependencyProperty.RegisterAttached(
        "FocusOnLoad", typeof(bool), typeof(FocusAssist), new PropertyMetadata(false, OnFocusOnLoadChanged));

    public static bool GetFocusOnLoad(DependencyObject d) => (bool)d.GetValue(FocusOnLoadProperty);
    public static void SetFocusOnLoad(DependencyObject d, bool value) => d.SetValue(FocusOnLoadProperty, value);

    private static void OnFocusOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is FrameworkElement fe && e.NewValue is true)
            fe.Loaded += (_, _) => fe.Dispatcher.BeginInvoke(() =>
            {
                fe.Focus();
                Keyboard.Focus(fe);
            }, System.Windows.Threading.DispatcherPriority.Input);
    }
}

/// <summary>Executes a command when a list item is double-clicked or Enter is pressed.</summary>
public static class ItemActivation
{
    public static readonly DependencyProperty CommandProperty = DependencyProperty.RegisterAttached(
        "Command", typeof(ICommand), typeof(ItemActivation), new PropertyMetadata(null, OnCommandChanged));

    public static ICommand? GetCommand(DependencyObject d) => (ICommand?)d.GetValue(CommandProperty);
    public static void SetCommand(DependencyObject d, ICommand? value) => d.SetValue(CommandProperty, value);

    private static void OnCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not System.Windows.Controls.Primitives.Selector selector) return;
        selector.MouseDoubleClick -= OnDoubleClick;
        selector.KeyDown -= OnKeyDown;
        if (e.NewValue is null) return;
        selector.MouseDoubleClick += OnDoubleClick;
        selector.KeyDown += OnKeyDown;
    }

    private static void OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && FindAncestor<System.Windows.Controls.Primitives.ScrollBar>(src) is not null) return;
        Execute((System.Windows.Controls.Primitives.Selector)sender);
    }

    private static void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Execute((System.Windows.Controls.Primitives.Selector)sender);
        e.Handled = true;
    }

    private static void Execute(System.Windows.Controls.Primitives.Selector selector)
    {
        var item = selector.SelectedItem;
        var cmd = GetCommand(selector);
        if (item is not null && cmd?.CanExecute(item) == true) cmd.Execute(item);
    }

    private static T? FindAncestor<T>(DependencyObject d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        }

        return null;
    }
}
