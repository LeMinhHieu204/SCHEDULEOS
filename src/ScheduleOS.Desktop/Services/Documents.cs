using System.Globalization;
using System.Printing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Microsoft.Extensions.Logging;
using ScheduleOS.Application.Appointments;
using ScheduleOS.Application.Payments;
using ScheduleOS.Desktop.Converters;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.Services;

public sealed class PrintService(ToastService toasts, ILogger<PrintService> logger)
{
    public bool Print(FlowDocument document, string description)
    {
        try
        {
            var dialog = new PrintDialog();
            if (dialog.ShowDialog() != true) return false;
            document.PageHeight = dialog.PrintableAreaHeight;
            document.PageWidth = dialog.PrintableAreaWidth;
            document.PagePadding = new Thickness(56);
            document.ColumnWidth = double.PositiveInfinity;
            dialog.PrintDocument(((IDocumentPaginatorSource)document).DocumentPaginator, description);
            toasts.Success("Sent to the printer.");
            return true;
        }
        catch (Exception ex) when (ex is PrintQueueException or PrintSystemException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Printing failed");
            toasts.Error("Printing failed: " + ex.Message);
            return false;
        }
    }
}

/// <summary>Builds printable FlowDocuments. Customer-facing documents never contain internal staff notes.</summary>
public static class DocumentBuilder
{
    private static readonly Brush Navy = Palette.Frozen((Color)ColorConverter.ConvertFromString("#14253D"));
    private static readonly Brush Muted = Palette.Frozen((Color)ColorConverter.ConvertFromString("#67798B"));
    private static readonly Brush Teal = Palette.Frozen((Color)ColorConverter.ConvertFromString("#008C95"));
    private static readonly Brush Line = Palette.Frozen((Color)ColorConverter.ConvertFromString("#E2EBEF"));
    private static readonly Brush Soft = Palette.Frozen((Color)ColorConverter.ConvertFromString("#F5F9FA"));

    private static FlowDocument NewDocument() => new()
    {
        FontFamily = new FontFamily("Segoe UI"),
        FontSize = 12.5,
        Foreground = Navy,
        Background = Brushes.White,
        PagePadding = new Thickness(48),
        ColumnWidth = double.PositiveInfinity,
    };

    private static Paragraph P(string text, double size = 12.5, bool bold = false, Brush? color = null, double after = 4, TextAlignment align = TextAlignment.Left) =>
        new(new Run(text)) { FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal, Foreground = color ?? Navy, Margin = new Thickness(0, 0, 0, after), TextAlignment = align };

    private static Table Header(string business, string? address, string? phone, string? email, string title, string number)
    {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 0, 0, 16) };
        t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
        var g = new TableRowGroup();
        var row = new TableRow();
        var left = new TableCell();
        left.Blocks.Add(P(business, 20, true, Navy, 2));
        foreach (var l in new[] { address, phone, email }.Where(x => !string.IsNullOrWhiteSpace(x))) left.Blocks.Add(P(l!, 11, false, Muted, 0));
        var right = new TableCell();
        right.Blocks.Add(P(title, 20, true, Teal, 2, TextAlignment.Right));
        right.Blocks.Add(P(number, 11.5, false, Muted, 0, TextAlignment.Right));
        row.Cells.Add(left);
        row.Cells.Add(right);
        g.Rows.Add(row);
        t.RowGroups.Add(g);
        return t;
    }

    private static Table Grid(string[] headers, IEnumerable<string[]> rows, double[] widths, bool[] right)
    {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 6, 0, 10) };
        foreach (var w in widths) t.Columns.Add(new TableColumn { Width = new GridLength(w, GridUnitType.Star) });
        var group = new TableRowGroup();
        var head = new TableRow { Background = Soft };
        for (var i = 0; i < headers.Length; i++)
            head.Cells.Add(new TableCell(P(headers[i], 10.5, true, Muted, 0, right[i] ? TextAlignment.Right : TextAlignment.Left)) { Padding = new Thickness(8, 7, 8, 7) });
        group.Rows.Add(head);
        foreach (var r in rows)
        {
            var row = new TableRow();
            for (var i = 0; i < r.Length; i++)
                row.Cells.Add(new TableCell(P(r[i], 12, false, Navy, 0, right[i] ? TextAlignment.Right : TextAlignment.Left))
                {
                    Padding = new Thickness(8, 8, 8, 8), BorderBrush = Line, BorderThickness = new Thickness(0, 0, 0, 1),
                });
            group.Rows.Add(row);
        }

        t.RowGroups.Add(group);
        return t;
    }

    private static Table Totals(IEnumerable<(string Label, string Value, bool Bold)> lines)
    {
        var t = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
        t.Columns.Add(new TableColumn { Width = new GridLength(3, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.4, GridUnitType.Star) });
        t.Columns.Add(new TableColumn { Width = new GridLength(1.2, GridUnitType.Star) });
        var g = new TableRowGroup();
        foreach (var (label, value, bold) in lines)
        {
            var row = new TableRow();
            row.Cells.Add(new TableCell());
            row.Cells.Add(new TableCell(P(label, 12, bold, bold ? Navy : Muted, 2)));
            row.Cells.Add(new TableCell(P(value, 12, bold, Navy, 2, TextAlignment.Right)));
            g.Rows.Add(row);
        }

        t.RowGroups.Add(g);
        return t;
    }

    public static FlowDocument Invoice(InvoiceDocument d)
    {
        var cf = d.Currency;
        var doc = NewDocument();
        doc.Blocks.Add(Header(d.BusinessName, d.BusinessAddress, d.BusinessPhone, d.BusinessEmail, "INVOICE", d.Number));
        if (d.IsDemo) doc.Blocks.Add(P("DEMO WORKSPACE — sample document with fictional data", 10.5, true, Muted, 8));
        if (d.Status == InvoiceStatus.Void) doc.Blocks.Add(P("VOID — superseded by a newer invoice", 12, true, Palette.Frozen(Colors.Crimson), 8));
        doc.Blocks.Add(P("BILL TO", 10, true, Muted, 2));
        doc.Blocks.Add(P(d.CustomerName, 14, true, Navy, 1));
        foreach (var l in new[] { d.CustomerEmail, d.CustomerPhone }.Where(x => !string.IsNullOrWhiteSpace(x))) doc.Blocks.Add(P(l!, 11, false, Muted, 0));
        doc.Blocks.Add(P($"Issued {d.IssuedAt:MMMM d, yyyy}   ·   Appointment #{d.AppointmentReference} on {d.AppointmentStart:MMMM d, yyyy h:mm tt}   ·   {StatusText(d.Status)}", 11, false, Muted, 8));
        doc.Blocks.Add(Grid(["SERVICE", "STAFF", "QTY", "UNIT PRICE", "DISCOUNT", "AMOUNT"],
            d.Lines.Select(l => new[] { l.Description, l.StaffName ?? "", l.Quantity.ToString(CultureInfo.InvariantCulture), cf.Format(l.UnitPriceMinor),
                l.DiscountMinor == 0 ? "—" : "-" + cf.Format(l.DiscountMinor), cf.Format(l.LineTotalMinor) }),
            [3, 2, 0.7, 1.4, 1.3, 1.4], [false, false, true, true, true, true]));
        doc.Blocks.Add(Totals(
        [
            ("Subtotal", cf.Format(d.SubtotalMinor), false),
            ("Discounts", d.DiscountMinor == 0 ? cf.Format(0) : "-" + cf.Format(d.DiscountMinor), false),
            ($"{d.TaxLabel} ({d.TaxRateBasisPoints / 100.0:0.##}%)", cf.Format(d.TaxMinor), false),
            ("Total", cf.Format(d.TotalMinor), true),
            ("Amount paid", cf.Format(d.PaidMinor), false),
            ("Balance due", cf.Format(d.BalanceMinor), true),
        ]));
        if (d.Payments.Count > 0)
        {
            doc.Blocks.Add(P("PAYMENTS", 10, true, Muted, 0));
            doc.Blocks.Add(Grid(["RECEIPT", "DATE", "TYPE", "METHOD", "AMOUNT"],
                d.Payments.Select(p => new[] { p.Reference, p.Date.ToString("MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture), p.Kind, p.Method, cf.Format(p.SignedAmountMinor) }),
                [1.5, 2, 1, 2, 1.3], [false, false, false, false, true]));
        }

        if (!string.IsNullOrWhiteSpace(d.CustomerNotes))
        {
            doc.Blocks.Add(P("NOTES", 10, true, Muted, 2));
            doc.Blocks.Add(P(d.CustomerNotes!, 11.5, false, Navy, 8));
        }

        doc.Blocks.Add(P(d.Footer, 11, false, Muted, 2));
        doc.Blocks.Add(P("Commercial invoice/receipt generated by ScheduleOS. Not a jurisdiction-specific tax invoice.", 9.5, false, Muted, 0));
        return doc;
    }

    public static FlowDocument Receipt(ReceiptDocument d)
    {
        var cf = d.Currency;
        var doc = NewDocument();
        doc.Blocks.Add(Header(d.BusinessName, d.BusinessAddress, null, null, d.Kind.ToUpperInvariant() + " RECEIPT", d.ReceiptNumber));
        doc.Blocks.Add(P($"Customer: {d.CustomerName}", 13, true, Navy, 2));
        if (d.AppointmentReference is not null) doc.Blocks.Add(P($"Appointment #{d.AppointmentReference}", 12, false, Navy, 2));
        doc.Blocks.Add(P($"Date: {d.RecordedAt:MMMM d, yyyy h:mm tt}", 12, false, Navy, 2));
        doc.Blocks.Add(P($"Method: {d.Method}", 12, false, Navy, 2));
        if (!string.IsNullOrWhiteSpace(d.ExternalReference)) doc.Blocks.Add(P($"Reference: {d.ExternalReference}", 12, false, Navy, 2));
        if (!string.IsNullOrWhiteSpace(d.Note)) doc.Blocks.Add(P($"Note: {d.Note}", 12, false, Navy, 2));
        doc.Blocks.Add(Totals(
        [
            ("Amount", cf.Format(d.AmountMinor), true),
            ("Appointment total", cf.Format(d.AppointmentTotalMinor), false),
            ("Paid to date", cf.Format(d.AppointmentPaidMinor), false),
            ("Remaining balance", cf.Format(d.AppointmentTotalMinor - d.AppointmentPaidMinor), false),
        ]));
        if (!string.IsNullOrWhiteSpace(d.RecordedBy)) doc.Blocks.Add(P($"Recorded by {d.RecordedBy}", 10.5, false, Muted, 2));
        doc.Blocks.Add(P("Card payments are processed on an external terminal and recorded manually in ScheduleOS.", 9.5, false, Muted, 0));
        return doc;
    }

    /// <summary>Customer-facing appointment summary (no internal notes).</summary>
    public static FlowDocument AppointmentSummary(AppointmentDetailDto a, string business)
    {
        var cf = Formats.Currency;
        var doc = NewDocument();
        doc.Blocks.Add(Header(business, null, null, null, "APPOINTMENT", "#" + a.Reference));
        doc.Blocks.Add(P(a.Customer.FullName, 14, true, Navy, 2));
        doc.Blocks.Add(P($"{a.Start:dddd, MMMM d, yyyy} · {a.Start:h:mm tt} – {a.End:h:mm tt} · {Formats.Status(a.Status)}", 12, false, Muted, 8));
        doc.Blocks.Add(Grid(["SERVICE", "STAFF", "ROOM", "TIME", "PRICE"],
            a.Segments.Select(s => new[] { s.ServiceName, s.StaffName, s.ResourceName ?? "—", $"{s.Start:h:mm tt} – {s.End:h:mm tt}", cf.Format(s.LineTotalMinor) }),
            [2.5, 1.8, 1.5, 1.8, 1.2], [false, false, false, false, true]));
        doc.Blocks.Add(Totals([("Total", cf.Format(a.TotalMinor), true), ("Paid", cf.Format(a.PaidMinor), false), ("Balance", cf.Format(a.BalanceMinor), true)]));
        if (!string.IsNullOrWhiteSpace(a.CustomerNotes)) doc.Blocks.Add(P("Notes: " + a.CustomerNotes, 11.5, false, Navy, 4));
        return doc;
    }

    private static string StatusText(InvoiceStatus s) => s == InvoiceStatus.PartiallyPaid ? "Partially paid" : s.ToString();
}
