using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Customers;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.ViewModels.Dialogs;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Desktop.Services;

/// <summary>Facade over UI-level services (toasts, dialogs, navigation) for view models.</summary>
public static class Ui
{
    public static IServiceProvider Services { get; set; } = null!;
    public static ToastService Toasts => Services.GetRequiredService<ToastService>();
    public static ErrorHandler Errors => Services.GetRequiredService<ErrorHandler>();
    public static DialogService Dialogs => Services.GetRequiredService<DialogService>();
    public static NavigationService Navigation => Services.GetRequiredService<NavigationService>();
    public static T Get<T>() where T : notnull => Services.GetRequiredService<T>();

    public static void Dispatch(Func<Task> action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null) return;
        d.InvokeAsync(action, DispatcherPriority.Background);
    }

    public static void Dispatch(Action action)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null) return;
        if (d.CheckAccess()) action();
        else d.InvokeAsync(action);
    }
}

public enum ToastKind { Success, Info, Warning, Error }

public sealed partial class Toast : ObservableObject
{
    public required string Message { get; init; }
    public ToastKind Kind { get; init; }
    public string Icon => Kind switch
    {
        ToastKind.Success => "",
        ToastKind.Warning => "",
        ToastKind.Error => "",
        _ => "",
    };
}

public sealed partial class ToastService : ObservableObject
{
    public ObservableCollection<Toast> Items { get; } = [];

    public void Success(string message) => Show(message, ToastKind.Success);
    public void Info(string message) => Show(message, ToastKind.Info);
    public void Warning(string message) => Show(message, ToastKind.Warning);
    public void Error(string message) => Show(message, ToastKind.Error);

    public void Show(string message, ToastKind kind)
    {
        Ui.Dispatch(() =>
        {
            var toast = new Toast { Message = message, Kind = kind };
            Items.Add(toast);
            while (Items.Count > 4) Items.RemoveAt(0);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind is ToastKind.Error or ToastKind.Warning ? 7 : 4) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Items.Remove(toast);
            };
            timer.Start();
        });
    }

    [RelayCommand]
    private void Dismiss(Toast toast) => Items.Remove(toast);
}

/// <summary>Maps exceptions to friendly UI feedback. Raw stack traces are only written to the log.</summary>
public sealed class ErrorHandler(ToastService toasts, DialogService dialogs, ILogger<ErrorHandler> logger)
{
    public async Task HandleAsync(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return;
            case SchedulingConflictException sc:
                await dialogs.ShowAsync(new ConflictDialogViewModel(sc.Conflicts, canOverride: false));
                return;
            case DuplicateCustomerException dup:
                toasts.Warning(dup.Message);
                return;
            case ValidationException v:
                toasts.Warning(string.Join(Environment.NewLine, v.Errors));
                return;
            case PermissionDeniedException p:
                toasts.Error(p.Message);
                return;
            case ConcurrencyConflictException c:
                toasts.Warning(c.Message);
                return;
            case DuplicateSubmissionException d:
                toasts.Info(d.Message);
                return;
            case DomainException d:
                toasts.Error(d.Message);
                return;
            case System.IO.IOException or UnauthorizedAccessException:
                logger.LogWarning(ex, "File operation failed");
                toasts.Error("The file could not be accessed: " + ex.Message);
                return;
            default:
                logger.LogError(ex, "Unexpected error");
                await dialogs.AlertAsync("Something went wrong",
                    "The operation could not be completed. No data was changed by the failed step. Details were written to the diagnostic log (Settings → System).",
                    isError: true);
                return;
        }
    }
}

/// <summary>Shows dialogs as overlays in the main window. Dialogs can be nested.</summary>
public sealed partial class DialogService : ObservableObject
{
    public ObservableCollection<DialogViewModel> Stack { get; } = [];

    [ObservableProperty]
    private DialogViewModel? _current;

    public async Task<object?> ShowAsync(DialogViewModel dialog)
    {
        Stack.Add(dialog);
        Current = dialog;
        try
        {
            await dialog.InitializeAsync();
        }
        catch (Exception ex)
        {
            Stack.Remove(dialog);
            Current = Stack.LastOrDefault();
            await Ui.Errors.HandleAsync(ex);
            return null;
        }

        try
        {
            return await dialog.Completion;
        }
        finally
        {
            Stack.Remove(dialog);
            Current = Stack.LastOrDefault();
        }
    }

    public async Task<T?> ShowAsync<T>(DialogViewModel dialog) => await ShowAsync(dialog) is T t ? t : default;

    public async Task<bool> ConfirmAsync(string title, string message, string confirmText = "Confirm", bool danger = false) =>
        await ShowAsync(new ConfirmDialogViewModel(title, message, confirmText, danger)) is true;

    public Task AlertAsync(string title, string message, bool isError = false) =>
        ShowAsync(new ConfirmDialogViewModel(title, message, "OK", isError, showCancel: false));

    public async Task<string?> PromptAsync(string title, string message, string label, bool required = true, string confirmText = "OK", string? initial = null, bool multiline = true) =>
        await ShowAsync(new PromptDialogViewModel(title, message, label, required, confirmText, initial, multiline)) as string;

    /// <summary>Shows scheduling conflicts. Returns true when the user chose to override warnings.</summary>
    public async Task<bool> ShowConflictsAsync(IReadOnlyList<ScheduleConflict> conflicts, bool canOverride) =>
        await ShowAsync(new ConflictDialogViewModel(conflicts, canOverride)) is true;

    public bool CloseTop()
    {
        if (Current is { CloseOnEscape: true } d)
        {
            d.Cancel();
            return true;
        }

        return false;
    }
}
