using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Security;
using ScheduleOS.Application.Settings;
using ScheduleOS.Desktop.Core;
using ScheduleOS.Domain;

namespace ScheduleOS.Desktop.ViewModels;

public static class BrandText
{
    public const string Feature1 = "Smart calendar with conflict-safe drag & drop";
    public const string Feature2 = "Staff schedules, rooms and maintenance blocks";
    public const string Feature3 = "Exact payments, invoices and reports";
    public const string Feature4 = "Local backups, roles and audit trail";
}

public sealed record CurrencyOption(string Code, string Name, int Decimals)
{
    public override string ToString() => $"{Code} — {Name}";

    public static IReadOnlyList<CurrencyOption> All { get; } =
    [
        new("USD", "US Dollar", 2), new("EUR", "Euro", 2), new("GBP", "British Pound", 2), new("VND", "Vietnamese Dong", 0),
        new("AUD", "Australian Dollar", 2), new("CAD", "Canadian Dollar", 2), new("SGD", "Singapore Dollar", 2), new("JPY", "Japanese Yen", 0),
        new("KRW", "South Korean Won", 0), new("THB", "Thai Baht", 2), new("INR", "Indian Rupee", 2), new("CHF", "Swiss Franc", 2),
    ];
}

/// <summary>First-run setup: business profile, the first Owner account and the choice between an empty or demo workspace.</summary>
public sealed partial class OnboardingViewModel(AuthService auth, IDemoDataService demo, Func<Task> completed) : ViewModelBase
{
    public IReadOnlyList<CurrencyOption> Currencies => CurrencyOption.All;

    [ObservableProperty] private int _step = 1;
    [ObservableProperty] private string _businessName = "";
    [ObservableProperty] private CurrencyOption _currency = CurrencyOption.All[0];
    [ObservableProperty] private string _ownerName = "";
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _confirmPassword = "";
    [ObservableProperty] private bool _createDemo;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _progress = "";

    public bool CreateEmpty
    {
        get => !CreateDemo;
        set => CreateDemo = !value;
    }

    partial void OnCreateDemoChanged(bool value) => OnPropertyChanged(nameof(CreateEmpty));

    [RelayCommand]
    private void Next()
    {
        Error = "";
        if (Step == 1)
        {
            if (string.IsNullOrWhiteSpace(BusinessName))
            {
                Error = "Enter your business name.";
                return;
            }
        }
        else if (Step == 2)
        {
            var errors = new List<string>();
            if (string.IsNullOrWhiteSpace(OwnerName)) errors.Add("Enter your name.");
            if (string.IsNullOrWhiteSpace(Username) || Username.Trim().Length < 3) errors.Add("Choose a username with at least 3 characters.");
            errors.AddRange(PasswordHasher.Validate(Password, Username));
            if (Password != ConfirmPassword) errors.Add("The passwords do not match.");
            if (errors.Count > 0)
            {
                Error = string.Join(Environment.NewLine, errors);
                return;
            }
        }

        Step = Math.Min(3, Step + 1);
    }

    [RelayCommand]
    private void Back()
    {
        Error = "";
        Step = Math.Max(1, Step - 1);
    }

    [RelayCommand]
    private async Task FinishAsync()
    {
        Error = "";
        IsBusy = true;
        try
        {
            Progress = "Creating your workspace…";
            await auth.CompleteOnboardingAsync(new OnboardingRequest(BusinessName, OwnerName, Username, Password, Currency.Code, Currency.Decimals, CreateDemo));
            if (CreateDemo)
            {
                Progress = "Generating demo data (fictional customers, staff and appointments)…";
                await Task.Run(() => demo.SeedAsync());
            }

            Password = ConfirmPassword = "";
            await completed();
        }
        catch (DomainException ex)
        {
            Error = ex.Message;
        }
        catch (Exception ex)
        {
            Error = "Setup failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            Progress = "";
        }
    }
}

public sealed partial class LoginViewModel(AuthService auth, SettingsService settings, Func<Task> signedIn) : ViewModelBase
{
    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    public string BusinessName => string.IsNullOrWhiteSpace(settings.Business.Name) ? "ScheduleOS" : settings.Business.Name;
    public bool IsDemo => settings.Business.IsDemoWorkspace;

    [RelayCommand]
    private async Task SignInAsync()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrEmpty(Password))
        {
            Error = "Enter your username and password.";
            return;
        }

        IsBusy = true;
        try
        {
            var result = await auth.LoginAsync(Username, Password);
            Password = "";
            if (result.Outcome != LoginOutcome.Success)
            {
                Error = result.Message;
                return;
            }

            await signedIn();
        }
        catch (Exception ex)
        {
            Error = "Sign-in failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

public sealed partial class LockViewModel(AuthService auth, UserSession session, Action unlocked, Action signOut) : ViewModelBase
{
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _error = "";

    public string UserName => session.DisplayName;

    public void Reset()
    {
        Password = "";
        Error = "";
        OnPropertyChanged(nameof(UserName));
    }

    [RelayCommand]
    private async Task UnlockAsync()
    {
        IsBusy = true;
        try
        {
            if (await auth.VerifyCurrentUserPasswordAsync(Password))
            {
                Reset();
                unlocked();
            }
            else
            {
                Error = "The password is incorrect.";
                Password = "";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void SignOut()
    {
        Reset();
        signOut();
    }
}
