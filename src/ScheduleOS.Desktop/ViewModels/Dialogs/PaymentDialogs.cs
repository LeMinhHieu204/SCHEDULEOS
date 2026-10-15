using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows.Documents;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Payments;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Desktop.Services;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.ViewModels.Dialogs;

/// <summary>Records a deposit or payment. Card payments are processed on an external terminal and recorded here manually.</summary>
public sealed partial class RecordPaymentViewModel(PaymentService payments, AppointmentService appointments) : DialogViewModel
{
    private readonly Guid _submissionKey = Guid.NewGuid();
    private CancellationTokenSource? _cts;

    public int? InitialAppointmentId { get; set; }
    public override string Title => "Record Payment";
    public override double DialogWidth => 620;

    public ObservableCollection<AppointmentListItem> Candidates { get; } = [];
    public IReadOnlyList<PaymentMethod> Methods { get; } = Enum.GetValues<PaymentMethod>();
    public IReadOnlyList<PaymentKind> Kinds { get; } = [PaymentKind.Payment, PaymentKind.Deposit];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private AppointmentListItem? _appointment;
    [ObservableProperty] private PaymentKind _kind = PaymentKind.Payment;
    [ObservableProperty] private PaymentMethod _method = PaymentMethod.Cash;
    [ObservableProperty] private string _amount = "";
    [ObservableProperty] private string _reference = "";
    [ObservableProperty] private string _note = "";
    [ObservableProperty] private long _balance;
    [ObservableProperty] private long _depositDue;
    [ObservableProperty] private bool _fixedAppointment;

    public string CardNotice => "ScheduleOS does not charge cards. Process the card on your terminal, then record the amount here.";

    public override async Task InitializeAsync()
    {
        if (InitialAppointmentId is { } id)
        {
            var d = await appointments.GetDetailAsync(id);
            var item = new AppointmentListItem(d.Id, d.Reference, d.Start, d.End, d.Status, d.Customer.Id, d.Customer.FullName,
                string.Join(", ", d.Segments.Select(s => s.ServiceName)), "", null, d.TotalMinor, d.PaidMinor, d.LocationName);
            Candidates.Add(item);
            Appointment = item;
            FixedAppointment = true;
            DepositDue = d.PaidMinor < d.DepositRequiredMinor ? d.DepositRequiredMinor - d.PaidMinor : 0;
            if (DepositDue > 0 && d.Status is AppointmentStatus.Pending or AppointmentStatus.Confirmed)
            {
                Kind = PaymentKind.Deposit;
                Amount = Formats.Currency.ToEditString(DepositDue);
            }
        }
        else await LoadCandidatesAsync("");
    }

    partial void OnSearchChanged(string value)
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        Ui.Dispatch(async () =>
        {
            try
            {
                await Task.Delay(250, cts.Token);
                await LoadCandidatesAsync(value);
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private async Task LoadCandidatesAsync(string term)
    {
        var list = await payments.FindPayableAppointmentsAsync(term);
        Candidates.Clear();
        foreach (var a in list) Candidates.Add(a);
    }

    partial void OnAppointmentChanged(AppointmentListItem? value)
    {
        Balance = value is null ? 0 : value.TotalMinor - value.PaidMinor;
        if (value is not null && string.IsNullOrWhiteSpace(Amount)) Amount = Formats.Currency.ToEditString(Balance);
    }

    [RelayCommand]
    private void PayFull() => Amount = Formats.Currency.ToEditString(Balance);

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Appointment is null)
        {
            Ui.Toasts.Warning("Choose the appointment being paid.");
            return;
        }

        if (!Formats.Currency.TryParse(Amount, out var amount) || amount <= 0)
        {
            Ui.Toasts.Warning("Enter a valid amount.");
            return;
        }

        string? receipt = null;
        var ok = await RunAsync(async () => receipt = await payments.RecordAsync(new RecordPaymentRequest
        {
            SubmissionKey = _submissionKey, AppointmentId = Appointment.Id, Kind = Kind, Method = Method, AmountMinor = amount,
            ExternalReference = Reference, Note = Note,
        }));
        if (ok) Close(receipt);
    }
}

public sealed partial class RefundViewModel(PaymentService payments, PaymentDto payment, bool reversal) : DialogViewModel
{
    public override string Title => IsReversal ? "Reverse Payment" : "Record Refund";
    public override double DialogWidth => 500;
    public PaymentDto Payment { get; } = payment;
    public bool IsReversal { get; } = reversal;
    public IReadOnlyList<PaymentMethod> Methods { get; } = Enum.GetValues<PaymentMethod>();
    public string Explanation => IsReversal
        ? "A reversal corrects a payment that was recorded by mistake. The original entry stays in the ledger and is offset by a reversal entry."
        : "Record money returned to the customer. Refunds cannot exceed the remaining refundable amount of the original payment.";

    [ObservableProperty] private string _amount = Formats.Currency.ToEditString(payment.RefundableMinor);
    [ObservableProperty] private PaymentMethod _method = payment.Method;
    [ObservableProperty] private string _reason = "";

    [RelayCommand]
    private async Task SaveAsync()
    {
        string? receipt = null;
        var ok = await RunAsync(async () =>
        {
            if (IsReversal) receipt = await payments.ReverseAsync(Payment.Id, Reason);
            else
            {
                if (!Formats.Currency.TryParse(Amount, out var amount)) throw new ValidationException("Enter a valid amount.");
                receipt = await payments.RefundAsync(Payment.Id, amount, Method, Reason);
            }
        });
        if (ok) Close(receipt);
    }
}

/// <summary>Invoice / receipt preview with print and PDF export.</summary>
public sealed partial class DocumentPreviewViewModel(DocumentPreviewViewModel.Source source, IDocumentPdfWriter pdf, FileDialogs files, PrintService printer) : DialogViewModel
{
    public sealed record Source(string Title, string FileName, Func<FlowDocument> Build, Func<IDocumentPdfWriter, string, Task> WritePdf);

    public static Source ForInvoice(InvoiceDocument d) =>
        new($"Invoice {d.Number}", $"Invoice-{d.Number}.pdf", () => DocumentBuilder.Invoice(d), (w, p) => w.WriteInvoiceAsync(p, d));

    public static Source ForReceipt(ReceiptDocument d) =>
        new($"Receipt {d.ReceiptNumber}", $"Receipt-{d.ReceiptNumber}.pdf", () => DocumentBuilder.Receipt(d), (w, p) => w.WriteReceiptAsync(p, d));

    public override string Title => source.Title;
    public override double DialogWidth => 860;

    [ObservableProperty] private FlowDocument? _document;

    public override Task InitializeAsync()
    {
        Document = source.Build();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void Print() => printer.Print(source.Build(), source.Title);

    [RelayCommand]
    private async Task SavePdfAsync()
    {
        var path = files.Save("Save as PDF", "PDF document (*.pdf)|*.pdf", source.FileName);
        if (path is null) return;
        if (await RunAsync(() => source.WritePdf(pdf, path), "PDF saved."))
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}

public sealed partial class FollowUpViewModel(AppointmentService appointments, int appointmentId, string customerName) : DialogViewModel
{
    public override string Title => "Add Follow-up Task";
    public override double DialogWidth => 480;
    public string CustomerName { get; } = customerName;
    [ObservableProperty] private DateTime? _date = DateTime.Today.AddDays(7);
    [ObservableProperty] private string _time = "10:00";
    [ObservableProperty] private string _note = $"Follow up with {customerName}";

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (Date is null || !TimeOnly.TryParse(Time, out var t))
        {
            Ui.Toasts.Warning("Enter a valid date and time.");
            return;
        }

        if (await RunAsync(() => appointments.AddFollowUpAsync(appointmentId, Date.Value.Date + t.ToTimeSpan(), Note), "Follow-up task added.")) Close(true);
    }
}
