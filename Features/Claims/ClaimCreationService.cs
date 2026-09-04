using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CMS_CSharp.Services.Email;
using CMS_CSharp.Services.Storage;
using CMS_CSharp.Validation;
using MySqlConnector;

namespace CMS_CSharp.Features.Claims;

internal sealed partial class ClaimCreationService(
    IConfiguration configuration,
    IR2StorageService r2Storage,
    IClaimConfirmationEmailQueue confirmationEmailQueue,
    ILogger<ClaimCreationService> logger)
{
    private const string ClaimIdPrefix = "OPNZPROCLM";
    private const string ClaimIdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";
    private const long ClaimFileMaxBytes = 5 * 1024 * 1024;

    public async Task<CreateClaimResult> CreateAsync(
        CreateClaimCommand request,
        CancellationToken cancellationToken)
    {
        request = NormalizeAndValidateRequest(request);
        await ValidateUploadFileAsync(request.Receipt, "receipt", cancellationToken);
        await ValidateUploadFileAsync(request.Screenshot, "screenshot", cancellationToken);

        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");
        }

        var uploadedObjectKeys = new List<string>();
        await using var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);
        MySqlTransaction? transaction = null;

        try
        {
            var purchaseDate = ParsePurchaseDate(request.PurchaseDate);
            _ = await GetPromotionNameAsync(
                connection,
                null,
                request.PromotionId,
                cancellationToken);
            await ValidateDeviceExistsAsync(
                connection,
                null,
                request.Imei,
                cancellationToken);

            var giftIds = await ResolveGiftIdsAsync(
                connection,
                null,
                request.PromotionId,
                request.GiftAliases,
                cancellationToken);
            var claimId = await CreateUniqueClaimIdAsync(
                connection,
                null,
                cancellationToken);

            var claimFolder = $"claims/promotions/{request.PromotionId}";
            var receiptUploadTask = UploadClaimFileAsync(
                request.Receipt,
                claimFolder,
                cancellationToken);
            var screenshotUploadTask = UploadClaimFileAsync(
                request.Screenshot,
                claimFolder,
                cancellationToken);
            try
            {
                await Task.WhenAll(receiptUploadTask, screenshotUploadTask);
            }
            finally
            {
                if (receiptUploadTask.IsCompletedSuccessfully)
                {
                    uploadedObjectKeys.Add(receiptUploadTask.Result.ObjectKey);
                }

                if (screenshotUploadTask.IsCompletedSuccessfully)
                {
                    uploadedObjectKeys.Add(screenshotUploadTask.Result.ObjectKey);
                }
            }

            var receiptUpload = await receiptUploadTask;
            var screenshotUpload = await screenshotUploadTask;

            transaction = await connection.BeginTransactionAsync(cancellationToken);

            var now = DateTime.UtcNow;
            var customerId = await InsertCustomerAsync(
                connection,
                transaction,
                request,
                now,
                cancellationToken);

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO Claims
                    (id, promotion_id, imei, customer_id, purchase_date, status,
                     receipt_url, screenshot_url, email_status, created_at, updated_at)
                VALUES
                    (@id, @promotionId, @imei, @customerId, @purchaseDate, 0,
                     @receiptUrl, @screenshotUrl, 0, @createdAt, @updatedAt);
                """,
                cancellationToken,
                ("@id", claimId),
                ("@promotionId", request.PromotionId),
                ("@imei", request.Imei.Trim()),
                ("@customerId", customerId),
                ("@purchaseDate", purchaseDate),
                ("@receiptUrl", ToStoredClaimPath(receiptUpload.ObjectKey)),
                ("@screenshotUrl", ToStoredClaimPath(screenshotUpload.ObjectKey)),
                ("@createdAt", now),
                ("@updatedAt", now));

            foreach (var giftId in giftIds)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO Claim_Gifts (gift_id, claim_id)
                    VALUES (@giftId, @claimId);
                    """,
                    cancellationToken,
                    ("@giftId", giftId),
                    ("@claimId", claimId));
            }

            await ExecuteAsync(
                connection,
                transaction,
                """
                INSERT INTO Deliver_Addresses
                    (claim_id, street, suburb, city, postcode, instructions,
                     is_current, event_claim_id, updated_at)
                VALUES
                    (@claimId, @street, @suburb, @city, @postcode, @instructions,
                     1, NULL, @updatedAt);
                """,
                cancellationToken,
                ("@claimId", claimId),
                ("@street", request.Street.Trim()),
                ("@suburb", request.Suburb.Trim()),
                ("@city", request.City.Trim()),
                ("@postcode", request.Postcode.Trim()),
                ("@instructions", string.IsNullOrWhiteSpace(request.Instructions)
                    ? DBNull.Value
                    : request.Instructions.Trim()),
                ("@updatedAt", now));

            await ExecuteAsync(
                connection,
                transaction,
                """
                UPDATE Devices
                SET redemption_status = 1, updated_at = @updatedAt
                WHERE imei = @imei;
                """,
                cancellationToken,
                ("@updatedAt", now),
                ("@imei", request.Imei));

            await transaction.CommitAsync(cancellationToken);

            var emailQueued = confirmationEmailQueue.TryQueue(claimId);
            if (!emailQueued)
            {
                logger.LogError(
                    "Claim confirmation email could not be queued for claim {ClaimId}.",
                    claimId);
            }

            return new CreateClaimResult(
                claimId,
                request.PromotionId,
                customerId,
                request.Imei.Trim(),
                giftIds,
                ToStoredClaimPath(receiptUpload.ObjectKey),
                ToStoredClaimPath(screenshotUpload.ObjectKey),
                emailQueued);
        }
        catch
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }
            await DeleteUploadedObjectsAsync(uploadedObjectKeys);
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync();
            }
        }
    }

    private async Task<IReadOnlyList<int>> ResolveGiftIdsAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        int promotionId,
        IReadOnlyList<string> giftAliases,
        CancellationToken cancellationToken)
    {
        var aliases = giftAliases
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var aliasParameters = aliases
            .Select((_, index) => $"@alias{index}")
            .ToArray();

        await using var command = new MySqlCommand(
            $"""
            SELECT
                g.alias,
                g.id,
                EXISTS(
                    SELECT 1
                    FROM Promotion_Gifts pg
                    WHERE pg.promotion_id = @promotionId
                      AND pg.gift_id = g.id
                ) AS is_available
            FROM Gifts g
            WHERE g.alias IN ({string.Join(", ", aliasParameters)});
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@promotionId", promotionId);
        for (var index = 0; index < aliases.Length; index++)
        {
            command.Parameters.AddWithValue(aliasParameters[index], aliases[index]);
        }

        var resolved = new Dictionary<string, (int Id, bool IsAvailable)>(
            StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                resolved[reader.GetString(0)] = (reader.GetInt32(1), reader.GetBoolean(2));
            }
        }

        var giftIds = new List<int>(aliases.Length);
        foreach (var alias in aliases)
        {
            if (!resolved.TryGetValue(alias, out var gift))
            {
                throw new ClaimValidationException($"Gift alias '{alias}' does not exist.");
            }

            if (!gift.IsAvailable)
            {
                throw new ClaimValidationException(
                    $"Gift alias '{alias}' is not available for the selected promotion.");
            }

            giftIds.Add(gift.Id);
        }

        return giftIds;
    }

    private static async Task<string> GetPromotionNameAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        int promotionId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT name FROM Promotions WHERE id = @promotionId LIMIT 1;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@promotionId", promotionId);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result as string ?? throw new ClaimValidationException(
            $"Promotion '{promotionId}' does not exist.");
    }

    private static async Task ValidateDeviceExistsAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        string imei,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT 1
            FROM Devices
            WHERE imei = @imei
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@imei", imei.Trim());

        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new ClaimValidationException($"Device IMEI '{imei}' does not exist.");
        }
    }

    private async Task<R2UploadResult> UploadClaimFileAsync(
        IFormFile file,
        string folder,
        CancellationToken cancellationToken)
    {
        var fileName = $"{Guid.NewGuid():N}{NormalizeExtension(file.FileName)}";
        var objectKey = $"{folder}/{fileName}";
        await using var stream = file.OpenReadStream();
        return await r2Storage.UploadAsync(
            stream,
            objectKey,
            file.ContentType,
            cancellationToken);
    }

    private static async Task<int> InsertCustomerAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        CreateClaimCommand request,
        DateTime updatedAt,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            INSERT INTO Customers (first_name, last_name, email, contact, updated_at)
            VALUES (@firstName, @lastName, @email, @contact, @updatedAt);
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@firstName", request.FirstName.Trim());
        command.Parameters.AddWithValue("@lastName", request.LastName.Trim());
        command.Parameters.AddWithValue("@email", request.Email.Trim());
        command.Parameters.AddWithValue("@contact", request.Contact.Trim());
        command.Parameters.AddWithValue("@updatedAt", updatedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return checked((int)command.LastInsertedId);
    }

    private static async Task<string> CreateUniqueClaimIdAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var suffix = string.Create(8, 0, static (characters, _) =>
            {
                for (var index = 0; index < characters.Length; index++)
                {
                    characters[index] = ClaimIdAlphabet[
                        RandomNumberGenerator.GetInt32(ClaimIdAlphabet.Length)];
                }
            });
            var newZealandDate = TimeZoneInfo.ConvertTimeBySystemTimeZoneId(
                DateTimeOffset.UtcNow,
                "Pacific/Auckland");
            var candidate = $"{ClaimIdPrefix}-{newZealandDate:yyMMdd}-{suffix}";

            await using var command = new MySqlCommand(
                "SELECT 1 FROM Claims WHERE id = @id LIMIT 1;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@id", candidate);
            if (await command.ExecuteScalarAsync(cancellationToken) is null)
            {
                return candidate;
            }
        }
    }

    private async Task DeleteUploadedObjectsAsync(IEnumerable<string> objectKeys)
    {
        foreach (var objectKey in objectKeys.Reverse())
        {
            try
            {
                await r2Storage.DeleteAsync(objectKey, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Failed to remove R2 object {ObjectKey} after claim creation failed.",
                    objectKey);
            }
        }
    }

    private static async Task ExecuteAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DateTime ParsePurchaseDate(string value)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result))
        {
            throw new ClaimValidationException(
                "Invalid purchaseDate; expected yyyy-MM-dd.");
        }

        return result.Date;
    }

    private static CreateClaimCommand NormalizeAndValidateRequest(
        CreateClaimCommand request)
    {
        CreateClaimCommand normalized;
        try
        {
            normalized = request with
            {
                Imei = CommonInputRules.NormalizeDigits(request.Imei, "imei", 15),
                PurchaseDate = CommonInputRules.NormalizeRequiredAscii(
                    request.PurchaseDate,
                    "purchaseDate"),
                FirstName = CommonInputRules.NormalizeTitle(request.FirstName, "firstName"),
                LastName = CommonInputRules.NormalizeTitle(request.LastName, "lastName"),
                Email = CommonInputRules.NormalizeEmail(request.Email),
                Contact = CommonInputRules.NormalizeContact(request.Contact),
                Street = CommonInputRules.NormalizeTitle(request.Street, "street"),
                Suburb = CommonInputRules.NormalizeTitle(request.Suburb, "suburb"),
                City = CommonInputRules.NormalizeTitle(request.City, "city"),
                Postcode = CommonInputRules.NormalizePostcode(request.Postcode),
                Instructions = CommonInputRules.NormalizeOptionalAscii(
                    request.Instructions,
                    "instructions"),
                GiftAliases = request.GiftAliases
                    .Select(alias => CommonInputRules.NormalizeRequiredAscii(alias, "gift alias"))
                    .ToArray()
            };
        }
        catch (InputValidationException exception)
        {
            throw new ClaimValidationException(exception.Message);
        }

        if (normalized.PromotionId <= 0)
        {
            throw new ClaimValidationException("promotionId must be a positive integer.");
        }

        if (normalized.GiftAliases.Count == 0 ||
            normalized.GiftAliases.Any(string.IsNullOrWhiteSpace))
        {
            throw new ClaimValidationException("At least one gift alias is required.");
        }

        return normalized;
    }

    private static async Task ValidateUploadFileAsync(
        IFormFile file,
        string fieldName,
        CancellationToken cancellationToken)
    {
        if (file.Length == 0)
        {
            throw new ClaimValidationException(
                $"The {fieldName} file must not be empty.");
        }

        if (file.Length > ClaimFileMaxBytes)
        {
            throw new ClaimValidationException(
                $"The {fieldName} file must not exceed 5 MB.");
        }

        var extension = Path.GetExtension(file.FileName).ToLowerInvariant();
        if (extension is not (".jpg" or ".jpeg" or ".png" or ".pdf"))
        {
            throw new ClaimValidationException(
                $"The {fieldName} file must be JPG, JPEG, PNG, or PDF.");
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
        var hasExpectedSignature = extension switch
        {
            ".jpg" or ".jpeg" =>
                bytesRead >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF,
            ".png" =>
                bytesRead >= 8 && header.AsSpan().SequenceEqual(
                    new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }),
            ".pdf" =>
                bytesRead >= 5 && header.AsSpan(0, 5).SequenceEqual("%PDF-"u8),
            _ => false
        };

        if (!hasExpectedSignature)
        {
            throw new ClaimValidationException(
                $"The {fieldName} file content does not match its extension.");
        }
    }

    private static string ToStoredClaimPath(string objectKey) =>
        Path.GetFileName(objectKey.Replace('\\', '/'));


    private static string NormalizeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension) || extension.Length > 16
            ? string.Empty
            : extension.ToLowerInvariant();
    }

    private static string NormalizeMySqlConnectionString(string connectionString)
    {
        var normalized = Regex.Replace(
            connectionString,
            @"(?i)Server=([^;,]+),(\d+)",
            "Server=$1;Port=$2");
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

}
