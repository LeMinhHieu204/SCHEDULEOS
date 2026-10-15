using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using ScheduleOS.Desktop.ViewModels;

namespace ScheduleOS.Desktop.Views;

/// <summary>View-only behaviour for the top bar: search keyboard navigation and dropdown menus.</summary>
public partial class TopBarView : UserControl
{
    public TopBarView() => InitializeComponent();

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private async void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Shell is not { } shell) return;
        switch (e.Key)
        {
            case Key.Down:
                shell.Search.MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                shell.Search.MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                e.Handled = true;
                await shell.Search.OpenAsync(null);
                break;
        }
    }

    private async void SearchResults_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (Shell is { } shell && SearchResults.SelectedItem is not null) await shell.Search.OpenAsync(null);
    }

    private static void OpenMenu(Button button)
    {
        if (button.ContextMenu is not { } menu) return;
        menu.DataContext = button.DataContext;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void QuickAdd_Click(object sender, RoutedEventArgs e) => OpenMenu(QuickAddButton);

    private void Profile_Click(object sender, RoutedEventArgs e) => OpenMenu(ProfileButton);
}
