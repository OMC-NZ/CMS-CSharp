using System.Text.RegularExpressions;
using CMS_CSharp.Services.Storage;
using CMS_CSharp.Validation;
using MySqlConnector;

namespace CMS_CSharp.Features.Claims;

internal sealed partial class ClaimUpdateService(
    IConfiguration configuration,
    IR2StorageService r2Storage,
    ILogger<ClaimUpdateService> logger)
{
    private const long ClaimFileMaxBytes = 5 * 1024 * 1024;

    public async Task<UpdateClaimResult?> UpdateAsync(
        UpdateClaimCommand request,
        CancellationToken cancellationToken)
    {
        request = await NormalizeAndValidateAsync(request, cancellationToken);

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");
        }

        await using var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);

        var existing = await FindClaimAsync(connection, request.ClaimId, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        IReadOnlyList<int>? giftIds = null;
        if (request.GiftAliases is not null)
        {
            giftIds = await ResolveGiftIdsAsync(
                connection,
                existing.PromotionId,
                request.GiftAliases,
                cancellationToken);
        }

        var folder = $"claims/promotions/{existing.PromotionId}";
        R2UploadResult? receiptUpload = null;
        R2UploadResult? imeiCopyUpload = null;
        var uploadedKeys = new List<string>();

        try
        {
            var receiptTask = request.Receipt is null
                ? Task.FromResult<R2UploadResult?>(null)
                : UploadAsync(request.Receipt, folder, cancellationToken);
            var imeiCopyTask = request.ImeiCopy is null
                ? Task.FromResult<R2UploadResult?>(null)
                : UploadAsync(request.ImeiCopy, folder, cancellationToken);

            try
            {
                await Task.WhenAll(receiptTask, imeiCopyTask);
            }
            finally
            {
                if (receiptTask.IsCompletedSuccessfully && receiptTask.Result is not null)
                {
                    uploadedKeys.Add(receiptTask.Result.ObjectKey);
                }
                if (imeiCopyTask.IsCompletedSuccessfully && imeiCopyTask.Result is not null)
                {
                    uploadedKeys.Add(imeiCopyTask.Result.ObjectKey);
                }
            }

            receiptUpload = await receiptTask;
            imeiCopyUpload = await imeiCopyTask;

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                var now = DateTime.UtcNow;
                if (request.Email is not null || request.Contact is not null)
                {
                    await UpdateCustomerAsync(
                        connection, transaction, existing.CustomerId, request, now, cancellationToken);
                }

                if (HasAddressChange(request))
                {
                    await UpdateAddressAsync(
                        connection, transaction, request, now, cancellationToken);
                }

                if (giftIds is not null)
                {
                    await ReplaceGiftsAsync(
                        connection, transaction, request.ClaimId, giftIds, cancellationToken);
                }

                await UpdateClaimFilesAsync(
                    connection, transaction, request.ClaimId, receiptUpload, imeiCopyUpload,
                    now, cancellationToken);

                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }

            var deleteTasks = new List<Task>();
            if (receiptUpload is not null)
            {
                deleteTasks.Add(DeleteOldFileAsync(existing.ReceiptUrl, folder));
            }
            if (imeiCopyUpload is not null)
            {
                deleteTasks.Add(DeleteOldFileAsync(existing.ScreenshotUrl, folder));
            }
            await Task.WhenAll(deleteTasks);

            return new UpdateClaimResult(true, request.ClaimId);
        }
        catch
        {
            foreach (var objectKey in uploadedKeys)
            {
                try
                {
                    await r2Storage.DeleteAsync(objectKey, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    logger.LogError(exception, "Failed to remove replacement R2 object {ObjectKey}.", objectKey);
                }
            }
            throw;
        }
    }

    private static async Task<ExistingClaim?> FindClaimAsync(
        MySqlConnection connection,
        string claimId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT customer_id, promotion_id, receipt_url, screenshot_url FROM Claims WHERE id = @claimId LIMIT 1;",
            connection);
        command.Parameters.AddWithValue("@claimId", claimId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExistingClaim(
            reader.GetInt32("customer_id"),
            reader.GetInt32("promotion_id"),
            reader.GetString("receipt_url"),
            reader.GetString("screenshot_url"));
    }

    private static async Task<IReadOnlyList<int>> ResolveGiftIdsAsync(
        MySqlConnection connection,
        int promotionId,
        IReadOnlyList<string> aliases,
        CancellationToken cancellationToken)
    {
        var parameters = aliases.Select((_, index) => $"@alias{index}").ToArray();
        await using var command = new MySqlCommand(
            $"""
            SELECT g.alias, g.id,
                   EXISTS(SELECT 1 FROM Promotion_Gifts pg
                          WHERE pg.promotion_id = @promotionId AND pg.gift_id = g.id)
                       OR @promotionId = 0 AS is_available
            FROM Gifts g
            WHERE g.alias IN ({string.Join(", ", parameters)});
            """,
            connection);
        command.Parameters.AddWithValue("@promotionId", promotionId);
        for (var index = 0; index < aliases.Count; index++)
        {
            command.Parameters.AddWithValue(parameters[index], aliases[index]);
        }

        var found = new Dictionary<string, (int Id, bool Available)>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                found[reader.GetString(0)] = (reader.GetInt32(1), reader.GetBoolean(2));
            }
        }

        var ids = new List<int>(aliases.Count);
        foreach (var alias in aliases)
        {
            if (!found.TryGetValue(alias, out var gift))
            {
                throw new ClaimValidationException($"Gift alias '{alias}' does not exist.");
            }
            if (!gift.Available)
            {
                throw new ClaimValidationException(
                    $"Gift alias '{alias}' is not available for the Claim's promotion.");
            }
            ids.Add(gift.Id);
        }
        return ids;
    }

    private static async Task UpdateCustomerAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int customerId,
        UpdateClaimCommand request,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            UPDATE Customers
            SET email = COALESCE(@email, email),
                contact = COALESCE(@contact, contact),
                updated_at = @updatedAt
            WHERE id = @customerId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@email", (object?)request.Email ?? DBNull.Value);
        command.Parameters.AddWithValue("@contact", (object?)request.Contact ?? DBNull.Value);
        command.Parameters.AddWithValue("@updatedAt", now);
        command.Parameters.AddWithValue("@customerId", customerId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateAddressAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        UpdateClaimCommand request,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            UPDATE Deliver_Addresses
            SET street = COALESCE(@street, street),
                suburb = COALESCE(@suburb, suburb),
                city = COALESCE(@city, city),
                postcode = COALESCE(@postcode, postcode),
                instructions = CASE WHEN @instructionsProvided = 1 THEN @instructions ELSE instructions END,
                updated_at = @updatedAt
            WHERE claim_id = @claimId AND is_current = 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@street", (object?)request.Street ?? DBNull.Value);
        command.Parameters.AddWithValue("@suburb", (object?)request.Suburb ?? DBNull.Value);
        command.Parameters.AddWithValue("@city", (object?)request.City ?? DBNull.Value);
        command.Parameters.AddWithValue("@postcode", (object?)request.Postcode ?? DBNull.Value);
        command.Parameters.AddWithValue("@instructionsProvided", request.InstructionsProvided ? 1 : 0);
        command.Parameters.AddWithValue("@instructions", request.Instructions ?? string.Empty);
        command.Parameters.AddWithValue("@updatedAt", now);
        command.Parameters.AddWithValue("@claimId", request.ClaimId);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            throw new ClaimValidationException("The Claim does not have a current delivery address.");
        }
    }

    private static async Task ReplaceGiftsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string claimId,
        IReadOnlyList<int> giftIds,
        CancellationToken cancellationToken)
    {
        await using (var delete = new MySqlCommand(
            "DELETE FROM Claim_Gifts WHERE claim_id = @claimId;", connection, transaction))
        {
            delete.Parameters.AddWithValue("@claimId", claimId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var giftId in giftIds)
        {
            await using var insert = new MySqlCommand(
                "INSERT INTO Claim_Gifts (gift_id, claim_id) VALUES (@giftId, @claimId);",
                connection,
                transaction);
            insert.Parameters.AddWithValue("@giftId", giftId);
            insert.Parameters.AddWithValue("@claimId", claimId);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task UpdateClaimFilesAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string claimId,
        R2UploadResult? receipt,
        R2UploadResult? imeiCopy,
        DateTime now,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            UPDATE Claims
            SET receipt_url = COALESCE(@receiptUrl, receipt_url),
                screenshot_url = COALESCE(@screenshotUrl, screenshot_url),
                updated_at = @updatedAt
            WHERE id = @claimId;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@receiptUrl", receipt is null ? DBNull.Value : Path.GetFileName(receipt.ObjectKey));
        command.Parameters.AddWithValue("@screenshotUrl", imeiCopy is null ? DBNull.Value : Path.GetFileName(imeiCopy.ObjectKey));
        command.Parameters.AddWithValue("@updatedAt", now);
        command.Parameters.AddWithValue("@claimId", claimId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<R2UploadResult?> UploadAsync(
        IFormFile file,
        string folder,
        CancellationToken cancellationToken)
    {
        var objectKey = $"{folder}/{Guid.NewGuid():N}{Path.GetExtension(file.FileName).ToLowerInvariant()}";
        await using var stream = file.OpenReadStream();
        return await r2Storage.UploadAsync(stream, objectKey, file.ContentType, cancellationToken);
    }

    private async Task DeleteOldFileAsync(string storedValue, string folder)
    {
        var deleted = await r2Storage.DeleteByPrefixAsync(storedValue, folder, CancellationToken.None);
        if (!deleted)
        {
            logger.LogWarning("Old Claim asset {StoredValue} was not deleted after replacement.", storedValue);
        }
    }

    private static bool HasAddressChange(UpdateClaimCommand request) =>
        request.Street is not null || request.Suburb is not null || request.City is not null ||
        request.Postcode is not null || request.InstructionsProvided;

    private static async Task<UpdateClaimCommand> NormalizeAndValidateAsync(
        UpdateClaimCommand request,
        CancellationToken cancellationToken)
    {
        var claimId = request.ClaimId.Trim();
        if (claimId.Length == 0)
        {
            throw new ClaimValidationException("claimId is required.");
        }

        try
        {
            request = request with
            {
                ClaimId = claimId,
                Email = request.Email is null ? null : CommonInputRules.NormalizeEmail(request.Email),
                Contact = request.Contact is null ? null : CommonInputRules.NormalizeContact(request.Contact),
                Street = request.Street is null ? null : CommonInputRules.NormalizeTitle(request.Street, "street"),
                Suburb = request.Suburb is null ? null : CommonInputRules.NormalizeTitle(request.Suburb, "suburb"),
                City = request.City is null ? null : CommonInputRules.NormalizeTitle(request.City, "city"),
                Postcode = request.Postcode is null ? null : CommonInputRules.NormalizePostcode(request.Postcode),
                Instructions = request.InstructionsProvided
                    ? CommonInputRules.NormalizeOptionalAscii(request.Instructions, "instructions") ?? string.Empty
                    : null,
                GiftAliases = request.GiftAliases?.Select(alias =>
                    CommonInputRules.NormalizeRequiredAscii(alias, "gift alias"))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray()
            };
        }
        catch (InputValidationException exception)
        {
            throw new ClaimValidationException(exception.Message);
        }

        if (request.GiftAliases is { Count: 0 })
        {
            throw new ClaimValidationException("giftAliases must contain at least one Gift when provided.");
        }

        if (request.Email is null && request.Contact is null && !HasAddressChange(request) &&
            request.GiftAliases is null && request.Receipt is null && request.ImeiCopy is null)
        {
            throw new ClaimValidationException("At least one field or replacement file is required.");
        }

        if (request.Receipt is not null)
        {
            await ValidateFileAsync(request.Receipt, "receipt", cancellationToken);
        }
        if (request.ImeiCopy is not null)
        {
            await ValidateFileAsync(request.ImeiCopy, "imeiCopy", cancellationToken);
        }
        return request;
    }

    private static async Task ValidateFileAsync(
        IFormFile file,
        string fieldName,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0 || file.Length > ClaimFileMaxBytes)
        {
            throw new ClaimValidationException(
                file.Length == 0 ? $"The {fieldName} file must not be empty." : $"The {fieldName} file must not exceed 5 MB.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".pdf"))
        {
            throw new ClaimValidationException($"The {fieldName} file must be JPG, JPEG, PNG, or PDF.");
        }

        var header = new byte[8];
        await using var stream = file.OpenReadStream();
        var bytesRead = 0;
        while (bytesRead < header.Length)
        {
            var read = await stream.ReadAsync(
                header.AsMemory(bytesRead, header.Length - bytesRead),
                cancellationToken);
            if (read == 0)
            {
                break;
            }
            bytesRead += read;
        }
        var valid = extension switch
        {
            ".jpg" or ".jpeg" => bytesRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" => bytesRead >= 8 && header.AsSpan().SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".pdf" => bytesRead >= 5 && header.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
            _ => false
        };
        if (!valid)
        {
            throw new ClaimValidationException($"The {fieldName} file content does not match its extension.");
        }
    }

    private static string NormalizeMySqlConnectionString(string connectionString)
    {
        var normalized = ConnectionPortRegex().Replace(connectionString, "Server=$1;Port=$2")
            .Replace("Encrypt=True;", "SslMode=Preferred;", StringComparison.OrdinalIgnoreCase)
            .Replace("Encrypt=False;", "SslMode=None;", StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=True;", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=False;", string.Empty, StringComparison.OrdinalIgnoreCase);
        return new MySqlConnectionStringBuilder(normalized) { TreatTinyAsBoolean = false }.ConnectionString;
    }

    [GeneratedRegex(@"(?i)Server=([^;,]+),(\d+)")]
    private static partial Regex ConnectionPortRegex();

    private sealed record ExistingClaim(
        int CustomerId,
        int PromotionId,
        string ReceiptUrl,
        string ScreenshotUrl);
}
