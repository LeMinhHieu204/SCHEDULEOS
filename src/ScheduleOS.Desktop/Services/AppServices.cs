using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using ScheduleOS.Application.Settings;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;

namespace ScheduleOS.Desktop.Services;

public sealed partial class NavigationService(IServiceProvider services) : ObservableObject
{
    private readonly Stack<(Type Type, object? Parameter)> _history = new();
    private object? _currentParameter;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoBack))]
    private PageViewModel? _current;

    public bool CanGoBack => _history.Count > 0;

    public event EventHandler? Navigated;

    public Task NavigateAsync<T>(object? parameter = null, bool addToHistory = false) where T : PageViewModel =>
        NavigateAsync(typeof(T), parameter, addToHistory);

    public async Task NavigateAsync(Type pageType, object? parameter = null, bool addToHistory = false)
    {
        var vm = (PageViewModel)services.GetRequiredService(pageType);
        Current?.OnNavigatedFrom();
        if (addToHistory && Current is not null) _history.Push((Current.GetType(), _currentParameter));
        else if (!addToHistory) _history.Clear();
        Current = vm;
        _currentParameter = parameter;
        OnPropertyChanged(nameof(CanGoBack));
        Navigated?.Invoke(this, EventArgs.Empty);
        await vm.OnNavigatedToAsync(parameter);
    }

    public async Task GoBackAsync()
    {
        if (_history.Count == 0) return;
        var (type, parameter) = _history.Pop();
        var vm = (PageViewModel)services.GetRequiredService(type);
        Current?.OnNavigatedFrom();
        Current = vm;
        _currentParameter = parameter;
        OnPropertyChanged(nameof(CanGoBack));
        Navigated?.Invoke(this, EventArgs.Empty);
        await vm.OnNavigatedToAsync(parameter);
    }

    public Task ReloadAsync() => Current?.LoadAsync() ?? Task.CompletedTask;

    public void Reset()
    {
        Current?.OnNavigatedFrom();
        Current = null;
        _history.Clear();
    }
}

/// <summary>Application-wide UI state shared by pages (selected location).</summary>
public sealed partial class AppState : ObservableObject
{
    [ObservableProperty]
    private int? _selectedLocationId;

    [ObservableProperty]
    private string _selectedLocationName = "All locations";
}

/// <summary>Switches light/dark palettes and density tokens at runtime.</summary>
public sealed class ThemeService
{
    private static readonly Uri LightUri = new("pack://application:,,,/ScheduleOS;component/Themes/Colors.Light.xaml");
    private static readonly Uri DarkUri = new("pack://application:,,,/ScheduleOS;component/Themes/Colors.Dark.xaml");

    public ColorTheme Theme { get; private set; } = ColorTheme.Light;

    public void Apply(ColorTheme theme, DensityMode density)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        var dictionaries = app.Resources.MergedDictionaries;
        var existing = dictionaries.FirstOrDefault(d => d.Source is not null && d.Source.OriginalString.Contains("Colors.", StringComparison.Ordinal));
        var wanted = theme == ColorTheme.Dark ? DarkUri : LightUri;
        if (existing is null || existing.Source != wanted)
        {
            var dict = new ResourceDictionary { Source = wanted };
            if (existing is not null) dictionaries[dictionaries.IndexOf(existing)] = dict;
            else dictionaries.Insert(0, dict);
        }

        app.Resources["Size.ControlHeight"] = density == DensityMode.Compact ? 32.0 : 36.0;
        app.Resources["Size.RowHeight"] = density == DensityMode.Compact ? 38.0 : 46.0;
        Theme = theme;
    }
}

/// <summary>Locks the session after a period without keyboard or mouse input.</summary>
public sealed class IdleMonitor
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(15) };
    private DateTime _lastInput = DateTime.Now;
    private Func<int>? _timeoutMinutes;
    private Action? _onIdle;

    public void Start(Func<int> timeoutMinutes, Action onIdle)
    {
        _timeoutMinutes = timeoutMinutes;
        _onIdle = onIdle;
        _lastInput = DateTime.Now;
        InputManager.Current.PreProcessInput -= OnInput;
        InputManager.Current.PreProcessInput += OnInput;
        _timer.Tick -= OnTick;
        _timer.Tick += OnTick;
        _timer.Start();
    }

    public void Stop()
    {
        _timer.Stop();
        InputManager.Current.PreProcessInput -= OnInput;
    }

    public void Touch() => _lastInput = DateTime.Now;

    private void OnInput(object sender, PreProcessInputEventArgs e)
    {
        if (e.StagingItem.Input is MouseEventArgs or KeyboardEventArgs or TextCompositionEventArgs) _lastInput = DateTime.Now;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var minutes = _timeoutMinutes?.Invoke() ?? 15;
        if (DateTime.Now - _lastInput >= TimeSpan.FromMinutes(Math.Max(1, minutes)))
        {
            _timer.Stop();
            _onIdle?.Invoke();
        }
    }
}

public sealed class FileDialogs
{
    public string? Save(string title, string filter, string fileName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, AddExtension = true, OverwritePrompt = true };
        return dlg.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dlg.FileName : null;
    }

    public string? Open(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true };
        return dlg.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dlg.FileName : null;
    }

    public string? PickFolder(string title, string? initial = null)
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = initial ?? "" };
        return dlg.ShowDialog(System.Windows.Application.Current?.MainWindow) == true ? dlg.FolderName : null;
    }
}

public static class SettingsBridge
{
    /// <summary>Keeps static formatters in sync with business settings.</summary>
    public static void Attach(SettingsService settings)
    {
        settings.SettingsChanged += (_, _) => Formats.Currency = settings.Currency;
        Formats.Currency = settings.Currency;
    }
}
