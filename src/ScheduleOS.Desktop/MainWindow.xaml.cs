using System.Windows;
using System.Windows.Input;
using ScheduleOS.Desktop.ViewModels;

namespace ScheduleOS.Desktop;

/// <summary>Window chrome and global keyboard handling only; all behaviour lives in view models.</summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        FitToWorkArea();
        StateChanged += (_, _) => UpdateChrome();
        Loaded += (_, _) => UpdateChrome();
        PreviewKeyDown += OnPreviewKeyDown;
        StartCaptureWatcher();
        Closed += (_, _) => _watcher?.Dispose();
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    /// <summary>Uses the preferred 1440×900 size where it fits; on smaller screens the window fills the work area.</summary>
    private void FitToWorkArea()
    {
        var area = SystemParameters.WorkArea;
        if (area.Width < Width + 40 || area.Height < Height + 40)
        {
            MinWidth = Math.Min(MinWidth, area.Width);
            MinHeight = Math.Min(MinHeight, area.Height);
            Width = Math.Min(Width, area.Width);
            Height = Math.Min(Height, area.Height);
            if (area.Width < 1440 || area.Height < 900) WindowState = WindowState.Maximized;
        }
    }

    private void UpdateChrome()
    {
        // A WindowChrome window overhangs the screen when maximised; compensate so nothing is clipped.
        RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
        RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
        MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    /// <summary>
    /// Diagnostic capture of the running UI (used for documentation and UI tests). Only active when
    /// SCHEDULEOS_CAPTURE_DIR is set; renders this window's visual tree, never the rest of the screen.
    /// </summary>
    private void CaptureWindow(string? fileName = null)
    {
        var dir = CaptureDirectory;
        if (dir is null) return;
        System.IO.Directory.CreateDirectory(dir);
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var root = (FrameworkElement)Content;
        var bmp = new System.Windows.Media.Imaging.RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(root.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, System.Windows.Media.PixelFormats.Pbgra32);
        bmp.Render(root);
        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
        var path = System.IO.Path.Combine(dir, string.IsNullOrWhiteSpace(fileName) ? $"capture-{DateTime.Now:HHmmss-fff}.png" : fileName);
        using var fs = System.IO.File.Create(path + ".tmp");
        encoder.Save(fs);
        fs.Close();
        System.IO.File.Move(path + ".tmp", path, overwrite: true);
    }

    private static string? CaptureDirectory =>
        Environment.GetEnvironmentVariable("SCHEDULEOS_CAPTURE_DIR") is { Length: > 0 } d ? d : null;

    /// <summary>Automation hook: writing "name.png.request" into the capture directory captures this window as name.png.</summary>
    private void StartCaptureWatcher()
    {
        if (CaptureDirectory is not { } dir) return;
        System.IO.Directory.CreateDirectory(dir);
        _watcher = new System.IO.FileSystemWatcher(dir, "*.request") { EnableRaisingEvents = true };
        _watcher.Created += (_, e) => Dispatcher.BeginInvoke(async () =>
        {
            string request;
            try
            {
                await System.Threading.Tasks.Task.Delay(150);
                request = await System.IO.File.ReadAllTextAsync(e.FullPath);
            }
            catch (System.IO.IOException)
            {
                request = "";
            }

            // Optional "size=WxH" (device-independent pixels) to verify layouts at specific window sizes.
            var match = System.Text.RegularExpressions.Regex.Match(request, @"size=(\d+)x(\d+)");
            if (match.Success)
            {
                WindowState = WindowState.Normal;
                MinWidth = Math.Min(MinWidth, 1000);
                MinHeight = Math.Min(MinHeight, 700);
                Width = double.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
                Height = double.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                await System.Threading.Tasks.Task.Delay(900);
            }

            await System.Threading.Tasks.Task.Delay(400); // let pending layout/rendering settle
            UpdateLayout();
            CaptureWindow(System.IO.Path.GetFileNameWithoutExtension(e.Name));
            try
            {
                System.IO.File.Delete(e.FullPath);
            }
            catch (System.IO.IOException)
            {
            }
        }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
    }

    private System.IO.FileSystemWatcher? _watcher;

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F12 && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            CaptureWindow();
            e.Handled = true;
            return;
        }

        if (Shell is not { } shell) return;
        if (e.Key == Key.Escape)
        {
            if (shell.Dialogs.CloseTop()) e.Handled = true;
            else if (shell.Search.IsOpen)
            {
                shell.Search.IsOpen = false;
                e.Handled = true;
            }
        }
        else if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control && shell.Phase == ShellPhase.Main && !shell.IsLocked && shell.Dialogs.Current is null)
        {
            TopBar.FocusSearch();
            e.Handled = true;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
