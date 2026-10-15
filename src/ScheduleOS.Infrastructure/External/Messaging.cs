using System.Net;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using ScheduleOS.Application.Abstractions;
using ScheduleOS.Application.Settings;

namespace ScheduleOS.Infrastructure.External;

/// <summary>Protects local secrets with Windows DPAPI (current user scope).</summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScheduleOS.Secrets.v1");

    public string Protect(string plainText)
    {
        if (string.IsNullOrEmpty(plainText)) return "";
        var bytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(plainText), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(bytes);
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrEmpty(protectedText)) return "";
        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedText), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return "";
        }
    }
}

/// <summary>SMTP e-mail delivery configured in Settings → Notifications. Nothing is sent unless configured.</summary>
public sealed class SmtpEmailSender(SettingsService settings, ISecretProtector secrets) : IEmailSender
{
    public bool IsConfigured
    {
        get
        {
            var s = settings.Current;
            return !string.IsNullOrWhiteSpace(s.SmtpHost) && !string.IsNullOrWhiteSpace(s.SmtpFromAddress);
        }
    }

    public async Task<DeliveryResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (!IsConfigured) return DeliveryResult.Unconfigured("SMTP is not configured.");
        if (string.IsNullOrWhiteSpace(message.To)) return DeliveryResult.Failure("The customer has no e-mail address.");
        var s = settings.Current;
        try
        {
            using var client = new SmtpClient(s.SmtpHost, s.SmtpPort)
            {
                EnableSsl = s.SmtpUseSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = 20_000,
            };
            if (!string.IsNullOrWhiteSpace(s.SmtpUser))
                client.Credentials = new NetworkCredential(s.SmtpUser, secrets.Unprotect(s.SmtpPasswordProtected));
            using var mail = new MailMessage(new MailAddress(s.SmtpFromAddress, string.IsNullOrWhiteSpace(s.SmtpFromName) ? settings.Business.Name : s.SmtpFromName),
                new MailAddress(message.To))
            {
                Subject = message.Subject,
                Body = message.Body,
                IsBodyHtml = false,
                BodyEncoding = Encoding.UTF8,
                SubjectEncoding = Encoding.UTF8,
            };
            await client.SendMailAsync(mail, ct);
            return DeliveryResult.Success();
        }
        catch (Exception ex) when (ex is SmtpException or FormatException or InvalidOperationException or IOException)
        {
            return DeliveryResult.Failure(ex.Message);
        }
    }
}

/// <summary>
/// Placeholder SMS adapter: ScheduleOS does not ship with an SMS provider. Implement <see cref="ISmsSender"/>
/// for a provider (Twilio, Vonage, ...) and register it to enable real delivery.
/// </summary>
public sealed class UnconfiguredSmsSender : ISmsSender
{
    public bool IsConfigured => false;
    public string ProviderName => "None";

    public Task<DeliveryResult> SendAsync(string phone, string text, CancellationToken ct = default) =>
        Task.FromResult(DeliveryResult.Unconfigured("No SMS provider is configured."));
}
