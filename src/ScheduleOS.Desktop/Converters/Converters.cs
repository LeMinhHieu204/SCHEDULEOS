using System.Collections;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using ScheduleOS.Application.Common;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.Converters;

/// <summary>Shared formatting context, updated whenever business settings change.</summary>
public static class Formats
{
    public static CurrencyFormat Currency { get; set; } = new("USD", 2);

    public static string Money(long minor) => Currency.Format(minor);

    public static string Duration(int minutes) => minutes switch
    {
        < 60 => $"{minutes} min",
        _ when minutes % 60 == 0 => minutes / 60 == 1 ? "1 hour" : $"{minutes / 60} hours",
        _ => $"{minutes / 60} h {minutes % 60} min",
    };

    public static string Status(AppointmentStatus s) => s switch
    {
        AppointmentStatus.CheckedIn => "Checked In",
        AppointmentStatus.InProgress => "In Progress",
        AppointmentStatus.NoShow => "No Show",
        _ => s.ToString(),
    };

    public static string Relative(DateTime when, DateTime now)
    {
        var span = now - when;
        if (span.TotalSeconds < 0) return when.ToString("MMM d, h:mm tt", CultureInfo.InvariantCulture);
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} hour{((int)span.TotalHours == 1 ? "" : "s")} ago";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} day{((int)span.TotalDays == 1 ? "" : "s")} ago";
        return when.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    public static string Humanize(Enum value) => Regex.Replace(value.ToString(), "(?<=[a-z])([A-Z])", " $1");
}

public static class Palette
{
    public static Brush Resource(string key) =>
        System.Windows.Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    public static (string Fg, string Bg) StatusKeys(AppointmentStatus s) => s switch
    {
        AppointmentStatus.Pending => ("Brush.WarningText", "Brush.WarningLight"),
        AppointmentStatus.Confirmed => ("Brush.Success", "Brush.SuccessLight"),
        AppointmentStatus.CheckedIn => ("Brush.Info", "Brush.InfoLight"),
        AppointmentStatus.InProgress => ("Brush.Purple", "Brush.PurpleLight"),
        AppointmentStatus.Completed => ("Brush.Primary", "Brush.PrimaryLight"),
        AppointmentStatus.Cancelled => ("Brush.Danger", "Brush.DangerLight"),
        _ => ("Brush.Neutral", "Brush.NeutralLight"),
    };

    public static Color StatusColor(AppointmentStatus s) => s switch
    {
        AppointmentStatus.Pending => (Color)ColorConverter.ConvertFromString("#E7A23A"),
        AppointmentStatus.Confirmed => (Color)ColorConverter.ConvertFromString("#15A77F"),
        AppointmentStatus.CheckedIn => (Color)ColorConverter.ConvertFromString("#3689E6"),
        AppointmentStatus.InProgress => (Color)ColorConverter.ConvertFromString("#8B5CF6"),
        AppointmentStatus.Completed => (Color)ColorConverter.ConvertFromString("#008C95"),
        AppointmentStatus.Cancelled => (Color)ColorConverter.ConvertFromString("#E45662"),
        _ => (Color)ColorConverter.ConvertFromString("#9AA9B4"),
    };

    private static readonly string[] AvatarColors = ["#008C95", "#3689E6", "#8B5CF6", "#E7A23A", "#E45662", "#15A77F", "#0EA5E9", "#D946EF", "#F97316", "#64748B"];

    public static Color FromName(string? name)
    {
        var hash = 0;
        foreach (var ch in name ?? "") hash = unchecked(hash * 31 + ch);
        return Parse(AvatarColors[Math.Abs(hash % AvatarColors.Length)]);
    }

    public static Color Parse(string? hex)
    {
        try
        {
            return (Color)ColorConverter.ConvertFromString(string.IsNullOrWhiteSpace(hex) ? "#008C95" : hex);
        }
        catch (FormatException)
        {
            return (Color)ColorConverter.ConvertFromString("#008C95");
        }
    }

    public static Color Tint(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));

    public static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public sealed class StatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not AppointmentStatus s) return Brushes.Transparent;
        var (fg, bg) = Palette.StatusKeys(s);
        return Palette.Resource(parameter as string == "bg" ? bg : fg);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class StatusTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is AppointmentStatus s ? Formats.Status(s) : value?.ToString() ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class MoneyConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        long l => parameter as string == "compact" ? Formats.Currency.FormatCompact(l) : Formats.Money(l),
        int i => Formats.Money(i),
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DurationConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        int m => Formats.Duration(m),
        TimeSpan t => Formats.Duration((int)t.TotalMinutes),
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Visible when the value is truthy (true, non-null, non-empty, non-zero). Parameter "invert" flips it.</summary>
public sealed class VisibleWhenConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var truthy = value switch
        {
            null => false,
            bool b => b,
            string s => !string.IsNullOrWhiteSpace(s),
            int i => i != 0,
            long l => l != 0,
            double d => d != 0,
            ICollection c => c.Count > 0,
            IEnumerable e => e.GetEnumerator().MoveNext(),
            _ => true,
        };
        if (parameter as string == "invert") truthy = !truthy;
        return truthy ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is bool b ? !b : value;
}

/// <summary>Two-way binding helper for radio buttons bound to an enum or string.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not true || parameter is null) return Binding.DoNothing;
        return targetType.IsEnum ? Enum.Parse(targetType, parameter.ToString()!) : parameter;
    }
}

public sealed class NameBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var c = Palette.FromName(value as string);
        return Palette.Frozen(parameter as string == "light" ? Palette.Tint(c, 0.85) : c);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class HexBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var c = Palette.Parse(value as string);
        return Palette.Frozen(parameter switch
        {
            "light" => Palette.Tint(c, 0.86),
            "soft" => Palette.Tint(c, 0.93),
            _ => c,
        });
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class EnumTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is Enum e ? Formats.Humanize(e) : value?.ToString() ?? "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PercentConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        double d => d.ToString(parameter as string ?? "P0", CultureInfo.InvariantCulture),
        _ => "",
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Formats a change percentage ("↑ 12%") and exposes the colour via parameter "brush".</summary>
public sealed class ChangeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double d) return parameter as string == "brush" ? Palette.Resource("Brush.TextSecondary") : "—";
        if (parameter as string == "brush") return Palette.Resource(d >= 0 ? "Brush.Success" : "Brush.Danger");
        return $"{(d >= 0 ? "↑" : "↓")} {Math.Abs(d):0.#}%";
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DateFormatConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var format = parameter as string ?? "g";
        return value switch
        {
            DateTime d => d.ToString(format, CultureInfo.InvariantCulture),
            DateOnly d => d.ToString(format, CultureInfo.InvariantCulture),
            TimeOnly t => t.ToString(format, CultureInfo.InvariantCulture),
            _ => "",
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class InitialsConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Domain.Entities.NameHelper.Initials(value as string);
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class DialogMaxHeightConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is double h ? Math.Max(200, h - 56) : double.PositiveInfinity;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ToastBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Services.ToastKind.Success => Palette.Resource("Brush.Success"),
        Services.ToastKind.Warning => Palette.Resource("Brush.Warning"),
        Services.ToastKind.Error => Palette.Resource("Brush.Danger"),
        _ => Palette.Resource("Brush.Info"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Resolves a resource key (string) to a brush, e.g. "Brush.SuccessLight".</summary>
public sealed class ResourceBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string key ? Palette.Resource(key) : Brushes.Transparent;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class SearchIconConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Application.Reporting.SearchKind k ? ViewModels.Pages.SearchViewModel.IconFor(k) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ResourceStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        Application.Resources.ResourceState.Available => Palette.Resource("Brush.Success"),
        Application.Resources.ResourceState.InUse => Palette.Resource("Brush.Warning"),
        _ => Palette.Resource("Brush.Danger"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ReminderStatusBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var bg = parameter as string == "bg";
        var key = value switch
        {
            ReminderStatus.Sent => "Brush.Success",
            ReminderStatus.Scheduled => "Brush.Info",
            ReminderStatus.NotSent => "Brush.Warning",
            ReminderStatus.Failed => "Brush.Danger",
            _ => "Brush.Neutral",
        };
        if (key == "Brush.Warning" && !bg) key = "Brush.WarningText";
        return Palette.Resource(bg ? key.Replace("Text", "") + "Light" : key);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PaymentKindBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
    {
        PaymentKind.Refund or PaymentKind.Reversal => Palette.Resource("Brush.Danger"),
        PaymentKind.Deposit => Palette.Resource("Brush.Info"),
        _ => Palette.Resource("Brush.Success"),
    };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class PaymentMethodTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is PaymentMethod m ? Application.Payments.PaymentService.MethodName(m) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class ResourceTypeTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is ResourceType t ? Domain.Scheduling.ResourceTypeNames.Display(t) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is string s ? System.IO.Path.GetFileName(s) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is long b
        ? b >= 1024 * 1024 ? $"{b / 1024d / 1024d:0.0} MB" : $"{b / 1024d:0} KB"
        : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is DateTime d ? Formats.Relative(d, DateTime.Now) : "";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
