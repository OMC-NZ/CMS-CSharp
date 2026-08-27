using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using CMS_CSharp.Data.Repositories;
using CMS_CSharp.Services.Storage;
using MySqlConnector;

namespace CMS_CSharp.Features.Claims;

internal sealed partial class ClaimCreationService(
    IConfiguration configuration,
    IR2StorageService r2Storage,
    IReferenceDataRepository referenceData,
    ILogger<ClaimCreationService> logger)
{
    private const string ClaimIdPrefix = "OPNZPROCLM";
    private const string ClaimIdAlphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789";

    public async Task<CreateClaimResult> CreateAsync(
        CreateClaimCommand request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

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
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var purchaseDate = ParsePurchaseDate(request.PurchaseDate);
            var promotionName = await GetPromotionNameAsync(
                connection,
                transaction,
                request.PromotionId,
                cancellationToken);
            await ValidateDeviceAsync(
                connection,
                transaction,
                request.PromotionId,
                request.Imei,
                purchaseDate,
                cancellationToken);

            var giftIds = await ResolveGiftIdsAsync(
                connection,
                transaction,
                request.PromotionId,
                request.GiftAliases,
                cancellationToken);
            var claimId = await CreateUniqueClaimIdAsync(
                connection,
                transaction,
                cancellationToken);

            var promotionPath = SanitizePathSegment(promotionName);
            var receiptUpload = await UploadClaimFileAsync(
                request.Receipt,
                $"claims/promotions/{promotionPath}",
                uploadedObjectKeys,
                cancellationToken);
            var screenshotUpload = await UploadClaimFileAsync(
                request.Screenshot,
                $"claims/promotions/{promotionPath}",
                uploadedObjectKeys,
                cancellationToken);

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
                ("@receiptUrl", receiptUpload.PublicUrl),
                ("@screenshotUrl", screenshotUpload.PublicUrl),
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

            await using var redeemCommand = new MySqlCommand(
                """
                UPDATE Devices
                SET redemption_status = 1, updated_at = @updatedAt
                WHERE imei = @imei AND redemption_status = 0;
                """,
                connection,
                transaction);
            redeemCommand.Parameters.AddWithValue("@updatedAt", now);
            redeemCommand.Parameters.AddWithValue("@imei", request.Imei.Trim());
            if (await redeemCommand.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new ClaimConflictException(
                    "The device has already been redeemed by another claim.");
            }

            await transaction.CommitAsync(cancellationToken);

            return new CreateClaimResult(
                claimId,
                request.PromotionId,
                customerId,
                request.Imei.Trim(),
                giftIds,
                receiptUpload.PublicUrl,
                screenshotUpload.PublicUrl);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            await DeleteUploadedObjectsAsync(uploadedObjectKeys);
            throw;
        }
    }

    private async Task<IReadOnlyList<int>> ResolveGiftIdsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int promotionId,
        IReadOnlyList<string> giftAliases,
        CancellationToken cancellationToken)
    {
        var giftIds = new List<int>();
        foreach (var alias in giftAliases
                     .Select(value => value.Trim())
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var giftId = await referenceData.FindGiftIdByAliasAsync(
                connection,
                transaction,
                alias,
                cancellationToken);
            if (giftId is null)
            {
                throw new ClaimValidationException($"Gift alias '{alias}' does not exist.");
            }

            await using var command = new MySqlCommand(
                """
                SELECT EXISTS(
                    SELECT 1
                    FROM Promotion_Gifts
                    WHERE promotion_id = @promotionId AND gift_id = @giftId
                );
                """,
                connection,
                transaction);
            command.Parameters.AddWithValue("@promotionId", promotionId);
            command.Parameters.AddWithValue("@giftId", giftId.Value);
            if (Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) == 0)
            {
                throw new ClaimValidationException(
                    $"Gift alias '{alias}' is not available for the selected promotion.");
            }

            giftIds.Add(giftId.Value);
        }

        return giftIds.Distinct().ToArray();
    }

    private static async Task<string> GetPromotionNameAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
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

    private static async Task ValidateDeviceAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int promotionId,
        string imei,
        DateTime purchaseDate,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT d.redemption_status,
                   EXISTS(
                       SELECT 1 FROM Promotion_Devices pd
                       WHERE pd.promotion_id = @promotionId
                         AND pd.eligible_model = d.model
                   ) AS model_is_eligible,
                   EXISTS(
                       SELECT 1 FROM Promotion_Channels pc
                       WHERE pc.promotion_id = @promotionId
                         AND pc.channel_code = d.channel_code
                         AND @purchaseDate BETWEEN pc.start_date AND pc.end_date
                   ) AS channel_is_eligible
            FROM Devices d
            WHERE d.imei = @imei
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@promotionId", promotionId);
        command.Parameters.AddWithValue("@imei", imei.Trim());
        command.Parameters.AddWithValue("@purchaseDate", purchaseDate);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new ClaimValidationException($"Device IMEI '{imei}' does not exist.");
        }

        if (reader.GetBoolean(0))
        {
            throw new ClaimConflictException("The device has already been redeemed.");
        }

        if (!reader.GetBoolean(1))
        {
            throw new ClaimValidationException(
                "The device model is not eligible for the selected promotion.");
        }

        if (!reader.GetBoolean(2))
        {
            throw new ClaimValidationException(
                "The device channel or purchase date is not eligible for the selected promotion.");
        }
    }

    private async Task<R2UploadResult> UploadClaimFileAsync(
        IFormFile file,
        string folder,
        ICollection<string> uploadedObjectKeys,
        CancellationToken cancellationToken)
    {
        var fileName = $"{Guid.NewGuid():N}{NormalizeExtension(file.FileName)}";
        var objectKey = $"{folder}/{fileName}";
        await using var stream = file.OpenReadStream();
        var result = await r2Storage.UploadAsync(
            stream,
            objectKey,
            file.ContentType,
            cancellationToken);
        uploadedObjectKeys.Add(objectKey);
        return result;
    }

    private static async Task<int> InsertCustomerAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
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
        MySqlTransaction transaction,
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

    private static void ValidateRequest(CreateClaimCommand request)
    {
        if (request.PromotionId <= 0 ||
            string.IsNullOrWhiteSpace(request.Imei) ||
            request.Imei.Trim().Length != 15 ||
            request.Imei.Trim().Any(character => !char.IsAsciiDigit(character)) ||
            string.IsNullOrWhiteSpace(request.PurchaseDate) ||
            string.IsNullOrWhiteSpace(request.FirstName) ||
            string.IsNullOrWhiteSpace(request.LastName) ||
            string.IsNullOrWhiteSpace(request.Email) ||
            string.IsNullOrWhiteSpace(request.Contact) ||
            string.IsNullOrWhiteSpace(request.Street) ||
            string.IsNullOrWhiteSpace(request.Suburb) ||
            string.IsNullOrWhiteSpace(request.City) ||
            string.IsNullOrWhiteSpace(request.Postcode))
        {
            throw new ClaimValidationException(
                "Promotion, a 15-character IMEI, purchase date, customer details, and delivery address are required.");
        }

        if (request.GiftAliases.Count == 0 ||
            request.GiftAliases.Any(string.IsNullOrWhiteSpace))
        {
            throw new ClaimValidationException("At least one gift alias is required.");
        }

        if (request.Receipt.Length == 0 || request.Screenshot.Length == 0)
        {
            throw new ClaimValidationException(
                "Non-empty receipt and screenshot files are required.");
        }
    }

    private static string SanitizePathSegment(string value)
    {
        var sanitized = InvalidPathSegmentRegex().Replace(value.Trim(), "-").Trim(' ', '.');
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed-promotion" : sanitized;
    }

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
        return normalized;
    }

    [GeneratedRegex(@"[\\/\x00-\x1F]")]
    private static partial Regex InvalidPathSegmentRegex();
}
