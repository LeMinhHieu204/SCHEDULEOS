using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Desktop.ViewModels.Dialogs;

public sealed partial class ConfirmDialogViewModel(string title, string message, string confirmText, bool danger, bool showCancel = true) : DialogViewModel
{
    public override string Title { get; } = title;
    public override double DialogWidth => 460;
    public string Message { get; } = message;
    public string ConfirmText { get; } = confirmText;
    public bool IsDanger { get; } = danger;
    public bool ShowCancel { get; } = showCancel;

    [RelayCommand]
    private void Confirm() => Close(true);
}

public sealed partial class PromptDialogViewModel : DialogViewModel
{
    public PromptDialogViewModel(string title, string message, string label, bool required, string confirmText, string? initial, bool multiline)
    {
        Title = title;
        Message = message;
        Label = label;
        Required = required;
        ConfirmText = confirmText;
        Multiline = multiline;
        _text = initial ?? "";
    }

    public override string Title { get; }
    public override double DialogWidth => 480;
    public string Message { get; }
    public string Label { get; }
    public bool Required { get; }
    public string ConfirmText { get; }
    public bool Multiline { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    private string _text;

    private bool CanConfirm() => !Required || !string.IsNullOrWhiteSpace(Text);

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private void Confirm() => Close(Text.Trim());
}

public sealed record ConflictRow(string Icon, string Message, string Severity, bool IsBlocking);

/// <summary>Explains scheduling conflicts; optionally lets an authorised user override warnings (never blocking rules).</summary>
public sealed partial class ConflictDialogViewModel(IReadOnlyList<ScheduleConflict> conflicts, bool canOverride) : DialogViewModel
{
    public override string Title => HasBlocking ? "This booking cannot be saved" : "Scheduling warnings";
    public override double DialogWidth => 560;

    public IReadOnlyList<ConflictRow> Rows { get; } = conflicts
        .Select(c => new ConflictRow(c.Severity == ConflictSeverity.Blocking ? "" : "", c.Message,
            c.Severity == ConflictSeverity.Blocking ? "Blocking" : "Warning", c.Severity == ConflictSeverity.Blocking))
        .DistinctBy(r => r.Message).ToList();

    public bool HasBlocking => conflicts.Any(c => c.Severity == ConflictSeverity.Blocking);
    public bool CanOverride => canOverride && !HasBlocking;

    public string Explanation => HasBlocking
        ? "Resolve the blocking conflicts below — choose another time, staff member or room. Use Find Available Time for suggestions."
        : canOverride
            ? "These rules can be overridden by an authorised user (for example to schedule overtime). The override is recorded in the appointment history."
            : "These warnings require an authorised user (Owner or Manager) to override.";

    [RelayCommand]
    private void Override() => Close(true);
}
