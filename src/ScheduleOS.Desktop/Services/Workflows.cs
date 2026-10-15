using Microsoft.Extensions.DependencyInjection;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Payments;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.ViewModels.Dialogs;
using ScheduleOS.Desktop.ViewModels.Pages;
using ScheduleOS.Domain;
using ScheduleOS.Domain.Scheduling;

namespace ScheduleOS.Desktop.Services;

public sealed record AppointmentPrefill(DateTime? Start = null, int? StaffId = null, int? ResourceId = null, int? CustomerId = null,
    IReadOnlyList<ServiceLineRequest>? Services = null);

/// <summary>Business workflows shared by several screens (booking, rescheduling, payments, documents).</summary>
public sealed class Workflows(IServiceProvider sp, NavigationService nav, DialogService dialogs, ToastService toasts,
    AppointmentService appointments, InvoiceService invoices, ICurrentUser user, SettingsService settings)
{
    private T Create<T>(params object[] args) where T : notnull => ActivatorUtilities.CreateInstance<T>(sp, args);

    public async Task NewAppointmentAsync(AppointmentPrefill? prefill = null)
    {
        if (!user.Has(Permission.ManageAppointments))
        {
            toasts.Error("You do not have permission to create appointments.");
            return;
        }

        var vm = Create<AppointmentEditorViewModel>();
        vm.Prefill = prefill;
        if (await dialogs.ShowAsync(vm) is AppointmentRef created)
            toasts.Success($"Appointment {created.Reference} booked.");
    }

    public async Task EditServicesAsync(int appointmentId)
    {
        var vm = Create<AppointmentEditorViewModel>();
        vm.EditAppointmentId = appointmentId;
        if (await dialogs.ShowAsync(vm) is not null) toasts.Success("Services updated.");
    }

    public Task OpenAppointmentAsync(int id) => nav.NavigateAsync<AppointmentDetailViewModel>(id, addToHistory: true);
    public Task OpenCustomerAsync(int id) => nav.NavigateAsync<CustomerDetailViewModel>(id, addToHistory: true);

    public async Task<int?> NewCustomerAsync(bool openAfter)
    {
        var vm = Create<CustomerEditorViewModel>();
        var id = await dialogs.ShowAsync(vm) as int?;
        if (id is null) return null;
        toasts.Success("Customer added.");
        if (openAfter) await OpenCustomerAsync(id.Value);
        return id;
    }

    public async Task<bool> EditCustomerAsync(int id)
    {
        var vm = Create<CustomerEditorViewModel>();
        vm.CustomerId = id;
        if (await dialogs.ShowAsync(vm) is null) return false;
        toasts.Success("Customer updated.");
        return true;
    }

    public async Task NewStaffAsync()
    {
        if (await dialogs.ShowAsync(Create<StaffEditorViewModel>()) is int) toasts.Success("Staff member added.");
    }

    public async Task<bool> EditStaffAsync(int id)
    {
        var vm = Create<StaffEditorViewModel>();
        vm.StaffId = id;
        if (await dialogs.ShowAsync(vm) is null) return false;
        toasts.Success("Staff profile updated.");
        return true;
    }

    public async Task NewServiceAsync()
    {
        if (await dialogs.ShowAsync(Create<ServiceEditorViewModel>()) is int) toasts.Success("Service added.");
    }

    public async Task EditServiceAsync(int id)
    {
        var vm = Create<ServiceEditorViewModel>();
        vm.ServiceId = id;
        if (await dialogs.ShowAsync(vm) is not null) toasts.Success("Service updated.");
    }

    public async Task NewResourceAsync()
    {
        if (await dialogs.ShowAsync(Create<ResourceEditorViewModel>()) is int) toasts.Success("Resource added.");
    }

    public async Task EditResourceAsync(int id)
    {
        var vm = Create<ResourceEditorViewModel>();
        vm.ResourceId = id;
        if (await dialogs.ShowAsync(vm) is not null) toasts.Success("Resource updated.");
    }

    public async Task RecordPaymentAsync(int? appointmentId)
    {
        if (!user.Has(Permission.RecordPayments))
        {
            toasts.Error("You do not have permission to record payments.");
            return;
        }

        var vm = Create<RecordPaymentViewModel>();
        vm.InitialAppointmentId = appointmentId;
        if (await dialogs.ShowAsync(vm) is string receipt) toasts.Success($"Payment recorded ({receipt}).");
    }

    public async Task RescheduleAsync(int appointmentId)
    {
        var vm = Create<RescheduleViewModel>();
        vm.AppointmentId = appointmentId;
        if (await dialogs.ShowAsync(vm) is true) toasts.Success("Appointment rescheduled.");
    }

    /// <summary>
    /// Saves a scheduling change; when only overridable warnings are returned and the user is authorised,
    /// asks whether to override and retries with the override flag.
    /// </summary>
    public async Task<bool> SaveScheduleAsync(Func<bool, Task> save)
    {
        try
        {
            await save(false);
            return true;
        }
        catch (SchedulingConflictException ex)
        {
            var canOverride = user.Has(Permission.OverrideSchedulingWarnings) && settings.Current.AllowOvertimeOverride &&
                              ex.Conflicts.All(c => c.Severity == ConflictSeverity.Warning);
            if (!await dialogs.ShowConflictsAsync(ex.Conflicts, canOverride)) return false;
            await save(true);
            return true;
        }
    }

    /// <summary>Applies a lifecycle transition, asking for confirmation or a reason where appropriate.</summary>
    public async Task<bool> ChangeStatusAsync(int id, int version, AppointmentStatus target, string reference)
    {
        string? reason = null;
        switch (target)
        {
            case AppointmentStatus.Cancelled:
                reason = await dialogs.PromptAsync("Cancel appointment", $"Cancel #{reference}? {settings.Current.CancellationPolicy}",
                    "Cancellation reason", true, "Cancel appointment");
                if (reason is null) return false;
                break;
            case AppointmentStatus.NoShow:
                if (!await dialogs.ConfirmAsync("Mark as no-show", $"Record that the customer did not arrive for #{reference}? {settings.Current.NoShowPolicy}", "Mark no-show", true))
                    return false;
                break;
            case AppointmentStatus.Completed:
                if (!await dialogs.ConfirmAsync("Complete appointment", $"Mark #{reference} as completed?", "Complete"))
                    return false;
                break;
        }

        await appointments.ChangeStatusAsync(id, version, target, reason);
        toasts.Success($"#{reference} is now {Formats.Status(target)}.");
        return true;
    }

    public async Task ShowInvoiceAsync(int appointmentId)
    {
        var invoiceId = await invoices.IssueAsync(appointmentId);
        await ShowInvoiceByIdAsync(invoiceId);
    }

    public async Task ShowInvoiceByIdAsync(int invoiceId)
    {
        var doc = await invoices.GetDocumentAsync(invoiceId);
        await dialogs.ShowAsync(Create<DocumentPreviewViewModel>(DocumentPreviewViewModel.ForInvoice(doc)));
    }

    public async Task ShowReceiptAsync(int paymentId)
    {
        var doc = await invoices.GetReceiptAsync(paymentId);
        await dialogs.ShowAsync(Create<DocumentPreviewViewModel>(DocumentPreviewViewModel.ForReceipt(doc)));
    }

    public async Task FindTimeAsync(IReadOnlyList<int>? serviceIds = null, Action<DateTime, IReadOnlyList<ServiceLineRequest>>? onPick = null)
    {
        var vm = Create<FindTimeViewModel>();
        vm.InitialServiceIds = serviceIds;
        var result = await dialogs.ShowAsync(vm);
        if (result is SlotSuggestion slot)
        {
            var lines = slot.Assignments.Select(a => new ServiceLineRequest(a.ServiceId, a.StaffMemberId, a.ResourceId)).ToList();
            if (onPick is not null) onPick(slot.Start, lines);
            else await NewAppointmentAsync(new AppointmentPrefill(slot.Start, Services: lines));
        }
    }

    public async Task CheckConflictsAsync(DateTime from, DateTime to, int? locationId)
    {
        var vm = Create<ConflictReportViewModel>(from, to);
        vm.LocationId = locationId;
        await dialogs.ShowAsync(vm);
    }
}
