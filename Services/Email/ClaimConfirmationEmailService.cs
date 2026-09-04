using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace CMS_CSharp.Services.Email;

internal sealed partial class ClaimConfirmationEmailService(
    IConfiguration configuration,
    ILogger<ClaimConfirmationEmailService> logger) : IClaimConfirmationEmailService
{
    private const string Subject = "OPPONZ Promotions Claim Confirmation";
    private const string TrackingUrl = "https://oppopromotions.co.nz";

    public async Task<bool> SendAsync(
        string claimId,
        CancellationToken cancellationToken = default)
    {
        ClaimEmailData? data = null;
        try
        {
            var settings = GetSettings();
            data = await LoadClaimDataAsync(claimId, cancellationToken);
            if (data is null)
            {
                throw new InvalidOperationException(
                    $"Claim confirmation email data was not found for claim '{claimId}'.");
            }

            using var message = BuildCustomerMessage(settings, data);
            await SendMessageAsync(settings, message, cancellationToken);
            await UpdateEmailStatusAsync(claimId, emailStatus: true, cancellationToken);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Claim confirmation email failed for claim {ClaimId} and recipient {Recipient}.",
                claimId,
                data?.RecipientEmail ?? "unknown");

            try
            {
                await UpdateEmailStatusAsync(claimId, emailStatus: false, cancellationToken);
            }
            catch (Exception statusException)
            {
                logger.LogError(
                    statusException,
                    "Failed to record email failure status for claim {ClaimId}.",
                    claimId);
            }

            await TrySendAdminFailureAlertAsync(
                claimId,
                data?.RecipientEmail,
                exception,
                cancellationToken);
            return false;
        }
    }

    private async Task<ClaimEmailData?> LoadClaimDataAsync(
        string claimId,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            SELECT
                cu.first_name,
                cu.last_name,
                cu.email,
                da.street,
                da.suburb,
                da.city,
                da.postcode,
                g.name,
                g.color
            FROM Claims c
            INNER JOIN Customers cu ON cu.id = c.customer_id
            LEFT JOIN Deliver_Addresses da
                ON da.claim_id = c.id AND da.is_current = 1
            LEFT JOIN Claim_Gifts cg ON cg.claim_id = c.id
            LEFT JOIN Gifts g ON g.id = cg.gift_id
            WHERE c.id = @claimId
            ORDER BY cg.id;
            """,
            connection);
        command.Parameters.AddWithValue("@claimId", claimId);

        string? firstName = null;
        string? lastName = null;
        string? recipientEmail = null;
        string? deliveryAddress = null;
        var gifts = new List<string>();

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            firstName ??= reader.GetString(0);
            lastName ??= reader.GetString(1);
            recipientEmail ??= reader.GetString(2);
            deliveryAddress ??= string.Join(", ", new[]
            {
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)
            }.Where(value => !string.IsNullOrWhiteSpace(value)));

            if (!reader.IsDBNull(7))
            {
                var giftName = string.Join(" ", new[]
                {
                    reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8)
                }.Where(value => !string.IsNullOrWhiteSpace(value)));
                if (!gifts.Contains(giftName, StringComparer.OrdinalIgnoreCase))
                {
                    gifts.Add(giftName);
                }
            }
        }

        if (recipientEmail is null)
        {
            return null;
        }

        return new ClaimEmailData(
            claimId,
            $"{firstName} {lastName}".Trim(),
            recipientEmail,
            gifts.Count == 0 ? string.Empty : string.Join(", ", gifts),
            deliveryAddress ?? string.Empty);
    }

    private static MailMessage BuildCustomerMessage(
        EmailSettings settings,
        ClaimEmailData data)
    {
        var message = new MailMessage
        {
            From = new MailAddress(settings.From, "OPPO NZ Promotions"),
            Subject = Subject,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };
        message.To.Add(new MailAddress(data.RecipientEmail));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            BuildText(data, settings.User),
            Encoding.UTF8,
            MediaTypeNames.Text.Plain));
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(
            BuildHtml(data, settings.User),
            Encoding.UTF8,
            MediaTypeNames.Text.Html));
        return message;
    }

    private static string BuildHtml(ClaimEmailData data, string serviceEmail)
    {
        var fullName = WebUtility.HtmlEncode(
            string.IsNullOrWhiteSpace(data.FullName) ? "there" : data.FullName);
        var claimId = WebUtility.HtmlEncode(data.ClaimId);
        var gift = WebUtility.HtmlEncode(data.Gift);
        var deliveryAddress = WebUtility.HtmlEncode(data.DeliveryAddress);
        var safeTrackingUrl = WebUtility.HtmlEncode(TrackingUrl);
        var safeServiceEmail = WebUtility.HtmlEncode(serviceEmail);

        return $$"""
            <!doctype html>
            <html>
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{Subject}}</title>
            </head>
            <body style="margin:0;padding:0;background:#ffffff;font-family:Arial,Helvetica,sans-serif;color:#111111;">
              <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#ffffff;margin:0;padding:0;">
                <tr><td align="left">
                  <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="background:#ffffff;">
                    <tr><td style="padding:0 42px 42px 42px;font-size:18px;line-height:1.55;">
                      <h1 style="margin:0 0 18px 0;font-size:28px;line-height:1.25;font-weight:700;color:#000000;">Hi {{fullName}},</h1>
                      <p style="margin:0 0 20px 0;">We have received your OPPO Promotions claim.</p>
                      <p style="margin:0 0 30px 0;">Our team will review your claim and, once approved, pass the relevant information to our logistics partner for delivery.</p>
                      <table role="presentation" width="100%" cellspacing="0" cellpadding="0" style="border-collapse:separate;border-spacing:0;border:1px solid #dddddd;border-radius:8px;overflow:hidden;margin:0 0 36px 0;">
                        <tr><td style="width:34%;padding:20px 26px;border-right:1px solid #dddddd;border-bottom:1px solid #dddddd;font-weight:700;">Claim reference</td><td style="padding:20px 26px;border-bottom:1px solid #dddddd;">{{claimId}}</td></tr>
                        <tr><td style="width:34%;padding:20px 26px;border-right:1px solid #dddddd;border-bottom:1px solid #dddddd;font-weight:700;">Selected gift</td><td style="padding:20px 26px;border-bottom:1px solid #dddddd;font-weight:700;">{{gift}}</td></tr>
                        <tr><td style="width:34%;padding:20px 26px;border-right:1px solid #dddddd;font-weight:700;">Delivery address</td><td style="padding:20px 26px;">{{deliveryAddress}}</td></tr>
                      </table>
                      <div style="height:18px;line-height:18px;">&nbsp;</div>
                      <h2 style="margin:0 0 18px 0;font-size:24px;line-height:1.3;font-weight:700;color:#000000;">Track your claim</h2>
                      <p style="margin:0 0 22px 0;">You can use your claim reference to track the progress of your claim at <a href="{{safeTrackingUrl}}" style="color:#0057B8;text-decoration:none;">oppopromotions.co.nz</a>. Simply enter the claim reference in the tracking field provided on the website.</p>
                      <p style="margin:0 0 34px 0;">Please allow up to 20 working days for processing and delivery. We will do our best to complete this as soon as possible.</p>
                      <p style="margin:0 0 34px 0;">If you have any questions, please contact us at <a href="mailto:{{safeServiceEmail}}" style="color:#0057B8;text-decoration:none;">{{safeServiceEmail}}</a>.</p>
                      <p style="margin:0 0 24px 0;">Thank you for your purchase.</p>
                      <p style="margin:0;">Warm regards,<br><br><strong>OPPO New Zealand</strong></p>
                    </td></tr>
                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string BuildText(ClaimEmailData data, string serviceEmail) =>
        string.Join('\n',
        [
            $"Hi {(string.IsNullOrWhiteSpace(data.FullName) ? "there" : data.FullName)},",
            "",
            "We have received your OPPO Promotions claim.",
            "Our team will review your claim and, once approved, pass the relevant information to our logistics partner for delivery.",
            "",
            $"Claim reference: {data.ClaimId}",
            $"Selected gift: {data.Gift}",
            $"Delivery address: {data.DeliveryAddress}",
            "",
            "Track your claim",
            $"You can use your claim reference to track the progress of your claim at {TrackingUrl}.",
            "",
            "Please allow up to 20 working days for processing and delivery. We will do our best to complete this as soon as possible.",
            "",
            $"If you have any questions, please contact us at {serviceEmail}.",
            "",
            "Thank you for your purchase.",
            "",
            "Warm regards,",
            "",
            "OPPO New Zealand"
        ]);

    private static async Task SendMessageAsync(
        EmailSettings settings,
        MailMessage message,
        CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = true,
            UseDefaultCredentials = false,
            Credentials = new NetworkCredential(settings.User, settings.Password)
        };
        await client.SendMailAsync(message, cancellationToken);
    }

    private async Task UpdateEmailStatusAsync(
        string claimId,
        bool emailStatus,
        CancellationToken cancellationToken)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken);
        await using var command = new MySqlCommand(
            """
            UPDATE Claims
            SET email_status = @emailStatus, updated_at = @updatedAt
            WHERE id = @claimId;
            """,
            connection);
        command.Parameters.AddWithValue("@emailStatus", emailStatus);
        command.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
        command.Parameters.AddWithValue("@claimId", claimId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task TrySendAdminFailureAlertAsync(
        string claimId,
        string? recipientEmail,
        Exception originalException,
        CancellationToken cancellationToken)
    {
        try
        {
            var settings = GetSettings();
            if (string.IsNullOrWhiteSpace(settings.Admin))
            {
                return;
            }

            using var message = new MailMessage
            {
                From = new MailAddress(settings.From, "OPPO NZ Promotions"),
                Subject = $"[OPPO Promotions] Claim confirmation email failed - {claimId}",
                Body = string.Join('\n',
                [
                    "A claim confirmation email failed to send.",
                    "",
                    $"Claim ID: {claimId}",
                    $"Recipient: {recipientEmail ?? "N/A"}",
                    "",
                    $"Error: {originalException.Message}"
                ])
            };
            message.To.Add(new MailAddress(settings.Admin));
            await SendMessageAsync(settings, message, cancellationToken);
        }
        catch (Exception alertException)
        {
            logger.LogError(
                alertException,
                "Claim confirmation admin alert failed for claim {ClaimId}.",
                claimId);
        }
    }

    private EmailSettings GetSettings()
    {
        var host = GetRequiredSetting("EMAIL_HOST");
        var user = GetRequiredSetting("EMAIL_USER");
        var password = GetRequiredSetting("EMAIL_PASS");
        var from = GetRequiredSetting("EMAIL_FROM");
        var admin = configuration["EMAIL_ADMIN"]?.Trim() ?? string.Empty;
        if (!int.TryParse(configuration["EMAIL_PORT"], out var port) || port is < 1 or > 65535)
        {
            throw new InvalidOperationException("EMAIL_PORT must be a valid TCP port.");
        }

        return new EmailSettings(host, port, user, password, from, admin);
    }

    private string GetRequiredSetting(string key)
    {
        var value = configuration[key];
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"{key} is not configured.")
            : value.Trim();
    }

    private async Task<MySqlConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");
        }

        var connection = new MySqlConnection(NormalizeMySqlConnectionString(connectionString));
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string NormalizeMySqlConnectionString(string connectionString)
    {
        var normalized = ConnectionPortRegex().Replace(connectionString, "Server=$1;Port=$2");
        normalized = normalized.Replace(
            "Encrypt=True;", "SslMode=Preferred;", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "Encrypt=False;", "SslMode=None;", StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "TrustServerCertificate=True;", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "TrustServerCertificate=False;", string.Empty, StringComparison.OrdinalIgnoreCase);
        var builder = new MySqlConnectionStringBuilder(normalized)
        {
            TreatTinyAsBoolean = false
        };
        return builder.ConnectionString;
    }

    [GeneratedRegex(@"(?i)Server=([^;,]+),(\d+)")]
    private static partial Regex ConnectionPortRegex();

    private sealed record EmailSettings(
        string Host,
        int Port,
        string User,
        string Password,
        string From,
        string Admin);

    private sealed record ClaimEmailData(
        string ClaimId,
        string FullName,
        string RecipientEmail,
        string Gift,
        string DeliveryAddress);
}
