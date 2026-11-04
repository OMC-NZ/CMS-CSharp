using System.Globalization;
using System.Text.RegularExpressions;
using CMS_CSharp.Data.Repositories;
using CMS_CSharp.Services.Storage;
using MySqlConnector;

namespace CMS_CSharp.Features.Events;

internal sealed class EventCreationService(
    IConfiguration configuration,
    IReferenceDataRepository referenceData,
    IR2StorageService storage,
    ILogger<EventCreationService> logger)
{
    public async Task<CreateEventResult> CreateAsync(
        CreateEventRequest request, IFormFile banner, string? termsText,
        IFormFile? termsFile, CancellationToken cancellationToken)
    {
        var name = Text(request.Name, "name", 255);
        var start = Date(request.StartDate, "startDate");
        var end = Date(request.EndDate, "endDate").AddHours(23).AddMinutes(59).AddSeconds(59);
        if (end < start) throw new EventValidationException("endDate must not precede startDate.");
        ValidateFile(banner, true);
        if (termsFile is not null)
        {
            ValidateFile(termsFile, false);
            if (!string.IsNullOrWhiteSpace(termsText))
                throw new EventValidationException("Provide either terms text or a terms file, not both.");
        }
        else if (termsText?.Trim() != "/terms")
            throw new EventValidationException("terms must be '/terms' or an uploaded file.");

        var models = (request.Models ?? throw new EventValidationException("models cannot be null."))
            .Select(x => Text(x, "model", 45).ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var codes = (request.ChannelCodes ?? throw new EventValidationException("channelCodes cannot be null."))
            .Select(x => Text(x, "channelCode", 4).ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (request.RequiresChannel && codes.Length == 0)
            throw new EventValidationException("channelCodes is required when requiresChannel is true.");
        ValidateForm(request);

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("ConnectionStrings:DefaultConnection is not configured.");
        var normalized = Regex.Replace(connectionString, @"(?i)Server=([^;,]+),(\d+)", "Server=$1;Port=$2")
            .Replace("Encrypt=True;", "SslMode=Preferred;", StringComparison.OrdinalIgnoreCase)
            .Replace("Encrypt=False;", "SslMode=None;", StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=True;", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=False;", string.Empty, StringComparison.OrdinalIgnoreCase);
        await using var connection = new MySqlConnection(new MySqlConnectionStringBuilder(normalized)
            { TreatTinyAsBoolean = false }.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var uploaded = new List<string>();
        var committed = false;
        try
        {
            var foundCodes = await referenceData.FindExistingChannelCodesAsync(
                connection, transaction, codes, cancellationToken);
            if (codes.Any(x => !foundCodes.Contains(x)))
                throw new EventValidationException("One or more channelCodes do not exist in Channels.");
            foreach (var model in models)
            {
                await using var check = new MySqlCommand(
                    "SELECT 1 FROM Devices WHERE model = @model LIMIT 1;", connection, transaction);
                check.Parameters.AddWithValue("@model", model);
                if (await check.ExecuteScalarAsync(cancellationToken) is null)
                    throw new EventValidationException($"Model '{model}' does not exist in Devices.");
            }

            var bannerName = $"{Guid.NewGuid():N}{Path.GetExtension(banner.FileName).ToLowerInvariant()}";
            await UploadAsync(banner, "banners/Events/imgs/" + bannerName);
            var termsUrl = "/terms";
            if (termsFile is not null)
            {
                var key = $"terms/Events/{Guid.NewGuid():N}{Path.GetExtension(termsFile.FileName).ToLowerInvariant()}";
                termsUrl = (await UploadAsync(termsFile, key)).PublicUrl;
            }
            var slugBase = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
            if (slugBase.Length > 200) slugBase = slugBase[..200].TrimEnd('-');
            if (slugBase.Length == 0) slugBase = "event";
            string slug;
            while (true)
            {
                slug = $"{slugBase}-{Guid.NewGuid():N}";
                await using var check = new MySqlCommand(
                    "SELECT 1 FROM Events WHERE slug_url = @slug LIMIT 1;", connection, transaction);
                check.Parameters.AddWithValue("@slug", slug);
                if (await check.ExecuteScalarAsync(cancellationToken) is null) break;
            }

            var eventId = await InsertAsync("""
                INSERT INTO Events
                    (name, terms_url, banner_url, slug_url, requires_imei, requires_channel,
                     requires_delivery, start_date, end_date, updated_at)
                VALUES (@name, @terms, @banner, @slug, @imei, @channel, @delivery, @start, @end, UTC_TIMESTAMP());
                """, ("@name", name), ("@terms", termsUrl), ("@banner", bannerName), ("@slug", slug),
                ("@imei", request.RequiresImei), ("@channel", request.RequiresChannel),
                ("@delivery", request.RequiresDelivery), ("@start", start), ("@end", end));
            foreach (var code in codes)
                await InsertAsync("INSERT INTO Event_Channels (event_id, channel_code, updated_at) VALUES (@event, @code, UTC_TIMESTAMP());",
                    ("@event", eventId), ("@code", code));
            foreach (var model in models)
                await InsertAsync("INSERT INTO Event_Models (event_id, eligible_model, updated_at) VALUES (@event, @model, UTC_TIMESTAMP());",
                    ("@event", eventId), ("@model", model));
            foreach (var section in request.Sections!)
            {
                var sectionId = await InsertAsync("""
                    INSERT INTO Event_Form_Sections (event_id, section_title, sort_order, updated_at)
                    VALUES (@event, @title, @sort, UTC_TIMESTAMP());
                    """, ("@event", eventId), ("@title", section.Title?.Trim()), ("@sort", section.SortOrder));
                foreach (var field in section.Fields ?? [])
                {
                    var fieldId = await InsertAsync("""
                        INSERT INTO Event_Form_Custom_Fields
                            (section_id, field_key, field_label, field_type, placeholder,
                             is_required, sort_order, validation_json, updated_at)
                        VALUES (@section, @key, @label, @type, @placeholder, @required, @sort, @validation, UTC_TIMESTAMP());
                        """, ("@section", sectionId), ("@key", field.Key!.Trim()), ("@label", field.Label!.Trim()),
                        ("@type", field.Type!.Trim()), ("@placeholder", field.Placeholder?.Trim()),
                        ("@required", field.Required), ("@sort", field.SortOrder),
                        ("@validation", field.Validation?.GetRawText()));
                    foreach (var option in field.Options ?? [])
                        await InsertAsync("""
                            INSERT INTO Event_Form_Field_Options (field_id, option_value, option_label, sort_order)
                            VALUES (@field, @value, @label, @sort);
                            """, ("@field", fieldId), ("@value", option.Value!.Trim()),
                            ("@label", option.Label!.Trim()), ("@sort", option.SortOrder));
                }
            }
            foreach (var upload in request.Uploads!)
                await InsertAsync("""
                    INSERT INTO Event_Form_Uploads (event_id, upload_key, upload_label, updated_at)
                    VALUES (@event, @key, @label, UTC_TIMESTAMP());
                    """, ("@event", eventId), ("@key", upload.Key!.Trim()), ("@label", upload.Label!.Trim()));
            await transaction.CommitAsync(cancellationToken);
            committed = true;
            return new CreateEventResult(true, eventId, slug);
        }
        catch
        {
            if (!committed)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception ex) { logger.LogError(ex, "Event creation rollback failed."); }
                foreach (var key in uploaded)
                {
                    try { await storage.DeleteAsync(key, CancellationToken.None); }
                    catch (Exception ex) { logger.LogError(ex, "Event creation cleanup failed for {Key}.", key); }
                }
            }
            throw;
        }

        async Task<R2UploadResult> UploadAsync(IFormFile file, string key)
        {
            await using var stream = file.OpenReadStream();
            var result = await storage.UploadAsync(stream, key, file.ContentType, cancellationToken);
            uploaded.Add(key);
            return result;
        }

        async Task<long> InsertAsync(string sql, params (string Name, object? Value)[] parameters)
        {
            await using var command = new MySqlCommand(sql, connection, transaction);
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return command.LastInsertedId;
        }
    }

    private static void ValidateForm(CreateEventRequest request)
    {
        if (request.Sections is null || request.Uploads is null)
            throw new EventValidationException("sections and uploads cannot be null.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var section in request.Sections)
        {
            if (section is null) throw new EventValidationException("sections cannot contain null.");
            if (section.Title?.Trim().Length > 255) throw new EventValidationException("Section title exceeds 255 characters.");
            foreach (var field in section.Fields ?? [])
            {
                if (field is null) throw new EventValidationException("fields cannot contain null.");
                var key = Text(field.Key, "field key", 100);
                if (!keys.Add(key)) throw new EventValidationException($"Duplicate form key '{key}'.");
                Text(field.Label, "field label", 255);
                Text(field.Type, "field type", 50);
                if (field.Placeholder?.Trim().Length > 255) throw new EventValidationException("Placeholder exceeds 255 characters.");
                if (field.Validation is { ValueKind: not (System.Text.Json.JsonValueKind.Object or System.Text.Json.JsonValueKind.Null) })
                    throw new EventValidationException("Field validation must be a JSON object or null.");
                var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var option in field.Options ?? [])
                {
                    if (option is null) throw new EventValidationException("options cannot contain null.");
                    var value = Text(option.Value, "option value", 255);
                    Text(option.Label, "option label", 255);
                    if (!values.Add(value)) throw new EventValidationException($"Duplicate option value '{value}'.");
                }
            }
        }
        foreach (var upload in request.Uploads)
        {
            if (upload is null) throw new EventValidationException("uploads cannot contain null.");
            var key = Text(upload.Key, "upload key", 100);
            Text(upload.Label, "upload label", 255);
            if (!keys.Add(key)) throw new EventValidationException($"Duplicate form key '{key}'.");
        }
    }

    private void ValidateFile(IFormFile file, bool isBanner)
    {
        var maxBytes = configuration.GetValue<long>("R2_UPLOAD_MAX_BYTES");
        if (maxBytes <= 0) throw new InvalidOperationException("R2_UPLOAD_MAX_BYTES must be configured.");
        if (file.Length == 0 || file.Length > maxBytes)
            throw new EventValidationException("Uploaded files must be non-empty and within R2_UPLOAD_MAX_BYTES.");
        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (isBanner)
        {
            if (extension is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".gif") ||
                !file.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                throw new EventValidationException("banner must be a JPG, JPEG, PNG, WEBP, or GIF image.");
        }
        else if (extension is not (".pdf" or ".jpg" or ".jpeg" or ".png"))
            throw new EventValidationException("terms file must be PDF, JPG, JPEG, or PNG.");
    }

    private static string Text(string? value, string field, int maxLength)
    {
        var text = value?.Trim();
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength)
            throw new EventValidationException($"{field} is required and must not exceed {maxLength} characters.");
        return text;
    }

    private static DateTime Date(string? value, string field)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
            throw new EventValidationException($"{field} must use yyyy-MM-dd.");
        return result;
    }
}
