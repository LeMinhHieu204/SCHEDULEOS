using CommunityToolkit.Mvvm.ComponentModel;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Desktop.Services;

namespace ScheduleOS.Desktop.Core;

public abstract partial class ViewModelBase : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    /// <summary>Runs an operation with busy state and centralised, user-friendly error handling.</summary>
    protected async Task<bool> RunAsync(Func<Task> action, string? successMessage = null)
    {
        if (IsBusy) return false;
        IsBusy = true;
        try
        {
            await action();
            if (successMessage is not null) Ui.Toasts.Success(successMessage);
            return true;
        }
        catch (Exception ex)
        {
            await Ui.Errors.HandleAsync(ex);
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Same as <see cref="RunAsync(Func{Task}, string?)"/> but does not block concurrent runs (used for background refreshes).</summary>
    protected static async Task<bool> TryAsync(Func<Task> action)
    {
        try
        {
            await action();
            return true;
        }
        catch (Exception ex)
        {
            await Ui.Errors.HandleAsync(ex);
            return false;
        }
    }
}

public interface INavigationAware
{
    Task OnNavigatedToAsync(object? parameter);
    void OnNavigatedFrom();
}

/// <summary>A main workspace page. Reloads automatically (debounced) when relevant data changes elsewhere.</summary>
public abstract partial class PageViewModel : ViewModelBase, INavigationAware
{
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private IDataChangeNotifier? _notifier;
    private CancellationTokenSource? _refreshDebounce;
    private bool _reloadPending;

    [ObservableProperty]
    private bool _isLoaded;

    public abstract string Title { get; }

    /// <summary>Navigation key used to highlight the sidebar entry.</summary>
    public virtual string NavKey => GetType().Name.Replace("ViewModel", "");

    protected virtual DataArea RefreshOn => DataArea.None;

    public virtual async Task OnNavigatedToAsync(object? parameter)
    {
        if (RefreshOn != DataArea.None)
        {
            _notifier = Ui.Services.GetService(typeof(IDataChangeNotifier)) as IDataChangeNotifier;
            if (_notifier is not null) _notifier.DataChanged += OnDataChanged;
        }

        await ReloadAsync();
        IsLoaded = true;
        if (_reloadPending)
        {
            _reloadPending = false;
            await ReloadAsync();
        }
    }

    /// <summary>Loads the page; concurrent reload requests are serialised so lists are never rebuilt twice at once.</summary>
    protected async Task ReloadAsync()
    {
        await _loadGate.WaitAsync();
        try
        {
            await TryAsync(LoadAsync);
        }
        finally
        {
            _loadGate.Release();
        }
    }

    /// <summary>Reloads now, or right after the initial load if the page is still loading (filters changed early are not lost).</summary>
    protected void RequestReload()
    {
        if (!IsLoaded)
        {
            _reloadPending = true;
            return;
        }

        _ = ReloadAsync();
    }

    public virtual void OnNavigatedFrom()
    {
        if (_notifier is not null) _notifier.DataChanged -= OnDataChanged;
        _refreshDebounce?.Cancel();
    }

    public abstract Task LoadAsync();

    private void OnDataChanged(object? sender, DataChangedEventArgs e)
    {
        if ((e.Area & RefreshOn) == 0) return;
        _refreshDebounce?.Cancel();
        var cts = _refreshDebounce = new CancellationTokenSource();
        Ui.Dispatch(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                await ReloadAsync();
            }
            catch (OperationCanceledException)
            {
            }
        });
    }
}

/// <summary>Modal dialog shown as an overlay inside the main window.</summary>
public abstract partial class DialogViewModel : ViewModelBase
{
    private readonly TaskCompletionSource<object?> _tcs = new();

    public abstract string Title { get; }
    public virtual double DialogWidth => 520;
    public virtual bool CloseOnEscape => true;

    public Task<object?> Completion => _tcs.Task;

    public virtual Task InitializeAsync() => Task.CompletedTask;

    protected void Close(object? result) => _tcs.TrySetResult(result);

    [CommunityToolkit.Mvvm.Input.RelayCommand]
    public void Cancel() => Close(null);
}
