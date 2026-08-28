using System.Globalization;
using System.Text.RegularExpressions;
using CMS_CSharp.Data.Repositories;
using CMS_CSharp.Features.Promotions.DuplicateDetection;
using CMS_CSharp.Services.Storage;
using MySqlConnector;

namespace CMS_CSharp.Features.Promotions;

internal sealed partial class PromotionCreationService(
    IConfiguration configuration,
    IR2StorageService r2Storage,
    IReferenceDataRepository referenceData,
    PromotionConflictDetector conflictDetector,
    ILogger<PromotionCreationService> logger)
{
    public async Task<CreatePromotionResult> CreateAsync(
        CreatePromotionCommand request,
        CancellationToken cancellationToken)
    {
        ValidateRequest(request);

        var uploadedObjectKeys = new List<string>();
        var bannerExtension = NormalizeExtension(request.Banner.FileName);
        var bannerFileName = $"{Guid.NewGuid():N}{bannerExtension}";
        var bannerObjectKey = $"banners/Promotions/{bannerFileName}";

        try
        {
            var bannerUploadTask = UploadFileAsync(request.Banner, bannerObjectKey, cancellationToken);
            Task<R2UploadResult>? termsUploadTask = null;
            string? termsObjectKey = null;
            if (request.TermsFile is not null)
            {
                var termsExtension = NormalizeExtension(request.TermsFile.FileName);
                var termsFileName = $"{Guid.NewGuid():N}{termsExtension}";
                termsObjectKey = $"terms/Promotions/{termsFileName}";
                termsUploadTask = UploadFileAsync(request.TermsFile, termsObjectKey, cancellationToken);
            }

            try
            {
                if (termsUploadTask is null)
                    await bannerUploadTask;
                else
                    await Task.WhenAll(bannerUploadTask, termsUploadTask);
            }
            finally
            {
                if (bannerUploadTask.IsCompletedSuccessfully) uploadedObjectKeys.Add(bannerObjectKey);
                if (termsUploadTask?.IsCompletedSuccessfully == true) uploadedObjectKeys.Add(termsObjectKey!);
            }

            var termsUrl = termsUploadTask is null
                ? request.TermsPath!
                : (await termsUploadTask).PublicUrl;

            return await SaveToDatabaseAsync(
                request,
                bannerFileName,
                termsUrl,
                cancellationToken);
        }
        catch
        {
            await DeleteUploadedObjectsAsync(uploadedObjectKeys);
            throw;
        }
    }

    private async Task<R2UploadResult> UploadFileAsync(
        IFormFile file, string objectKey, CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        return await r2Storage.UploadAsync(stream, objectKey, file.ContentType, cancellationToken);
    }

    private async Task<CreatePromotionResult> SaveToDatabaseAsync(
        CreatePromotionCommand request,
        string bannerFileName,
        string termsUrl,
        CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "ConnectionStrings:DefaultConnection is not configured.");
        }

        await using var connection = new MySqlConnection(
            NormalizeMySqlConnectionString(connectionString));
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            var products = request.Products
                .Select(product => product.Model.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var channels = request.Channels
                .DistinctBy(channel => channel.Code.Trim(), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var existingChannelCodes = await referenceData.FindExistingChannelCodesAsync(
                connection, transaction, channels.Select(x => x.Code.Trim()).ToArray(), cancellationToken);
            var validChannels = new List<PromotionChannelInput>();
            foreach (var channel in channels)
            {
                if (existingChannelCodes.Contains(channel.Code.Trim()))
                {
                    validChannels.Add(channel);
                }
                else
                {
                    logger.LogWarning(
                        "Skipping unmatched promotion channel code {Code}.",
                        channel.Code);
                }
            }

            var validChannelCodes = validChannels
                .Select(channel => channel.Code.Trim())
                .ToArray();
            var matchedChannelCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var validModels = new List<string>();
            var channelCodesByModel = await referenceData.GetDeviceChannelCodesByModelsAsync(
                connection, transaction, products, validChannelCodes, cancellationToken);
            foreach (var model in products)
            {
                if (!channelCodesByModel.TryGetValue(model, out var productChannelCodes) ||
                    productChannelCodes.Count == 0)
                {
                    logger.LogWarning(
                        "Skipping promotion device model {Model}; it does not match any selected channel.",
                        model);
                    continue;
                }

                validModels.Add(model);
                matchedChannelCodes.UnionWith(productChannelCodes);
            }

            var effectiveChannels = new List<PromotionChannelPeriod>();
            foreach (var channel in validChannels)
            {
                if (!matchedChannelCodes.Contains(channel.Code.Trim()))
                {
                    logger.LogWarning(
                        "Skipping promotion channel code {Code}; no submitted device model matches it.",
                        channel.Code);
                    continue;
                }

                var startDate = ParseDate(
                    channel.StartDate,
                    "channel startDate",
                    endOfDay: false);
                var endDate = ParseDate(
                    channel.EndDate,
                    "channel endDate",
                    endOfDay: true);

                if (endDate < startDate)
                {
                    throw new PromotionValidationException(
                        $"Channel '{channel.Code}' end date must not be earlier than its start date.");
                }

                effectiveChannels.Add(new PromotionChannelPeriod(
                    channel.Code.Trim(),
                    startDate,
                    endDate));
            }

            var gifts = request.Gifts
                .Select(gift => gift.Alias.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var giftIds = new List<int>();
            var giftIdsByAlias = await referenceData.FindGiftIdsByAliasesAsync(
                connection, transaction, gifts, cancellationToken);
            foreach (var giftAlias in gifts)
            {
                if (!giftIdsByAlias.TryGetValue(giftAlias, out var giftId))
                {
                    throw new PromotionValidationException(
                        $"Gift alias '{giftAlias}' does not exist.");
                }

                giftIds.Add(giftId);
            }

            var comparisonData = new PromotionComparisonData(
                validModels.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                effectiveChannels
                    .OrderBy(channel => channel.Code, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                giftIds.Distinct().Order().ToArray());
            var conflict = await conflictDetector.FindConflictAsync(
                connection,
                transaction,
                comparisonData,
                cancellationToken);
            if (conflict is not null)
            {
                throw new PromotionConflictException(conflict);
            }

            var slugUrl = await CreateUniqueSlugUrlAsync(
                connection,
                transaction,
                request.Name,
                cancellationToken);
            var updatedAt = DateTime.UtcNow;

            await using var promotionCommand = new MySqlCommand(
                """
                INSERT INTO Promotions
                    (name, description, banner_url, slug_url, terms_url, updated_at)
                VALUES
                    (@name, @description, @bannerUrl, @slugUrl, @termsUrl, @updatedAt);
                """,
                connection,
                transaction);
            promotionCommand.Parameters.AddWithValue("@name", request.Name.Trim());
            promotionCommand.Parameters.AddWithValue("@description", request.Description.Trim());
            promotionCommand.Parameters.AddWithValue("@bannerUrl", bannerFileName);
            promotionCommand.Parameters.AddWithValue("@slugUrl", slugUrl);
            promotionCommand.Parameters.AddWithValue("@termsUrl", termsUrl);
            promotionCommand.Parameters.AddWithValue("@updatedAt", updatedAt);
            await promotionCommand.ExecuteNonQueryAsync(cancellationToken);

            var promotionId = promotionCommand.LastInsertedId;

            foreach (var model in validModels)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO Promotion_Devices (promotion_id, eligible_model)
                    VALUES (@promotionId, @model);
                    """,
                    cancellationToken,
                    ("@promotionId", promotionId),
                    ("@model", model));
            }

            foreach (var channel in effectiveChannels)
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO Promotion_Channels
                        (promotion_id, channel_code, start_date, end_date, redeem_end_date, updated_at)
                    VALUES
                        (@promotionId, @channelCode, @startDate, @endDate, @redeemEndDate, @updatedAt);
                    """,
                    cancellationToken,
                    ("@promotionId", promotionId),
                    ("@channelCode", channel.Code),
                    ("@startDate", channel.StartDate),
                    ("@endDate", channel.EndDate),
                    ("@redeemEndDate", channel.EndDate.AddDays(14)),
                    ("@updatedAt", updatedAt));
            }

            foreach (var giftId in giftIds.Distinct())
            {
                await ExecuteAsync(
                    connection,
                    transaction,
                    """
                    INSERT INTO Promotion_Gifts (promotion_id, gift_id)
                    VALUES (@promotionId, @giftId);
                    """,
                    cancellationToken,
                    ("@promotionId", promotionId),
                    ("@giftId", giftId));
            }

            await transaction.CommitAsync(cancellationToken);

            return new CreatePromotionResult(
                promotionId,
                request.Name.Trim(),
                slugUrl,
                termsUrl,
                bannerFileName,
                validModels.Count,
                products.Length - validModels.Count,
                effectiveChannels.Count,
                channels.Length - effectiveChannels.Count,
                giftIds.Distinct().Count());
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
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
                    "Failed to remove R2 object {ObjectKey} after promotion creation failed.",
                    objectKey);
            }
        }
    }

    private static void ValidateRequest(CreatePromotionCommand request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
        {
            throw new PromotionValidationException("Promotion name is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Description))
        {
            throw new PromotionValidationException("Promotion description is required.");
        }

        if (request.Products.Count == 0)
        {
            throw new PromotionValidationException("At least one product is required.");
        }

        if (request.Products.Any(product => string.IsNullOrWhiteSpace(product.Model)))
        {
            throw new PromotionValidationException("Every product must include model.");
        }

        if (request.Channels.Count == 0)
        {
            throw new PromotionValidationException("At least one channel is required.");
        }

        if (request.Channels.Any(channel =>
                string.IsNullOrWhiteSpace(channel.Code) ||
                string.IsNullOrWhiteSpace(channel.StartDate) ||
                string.IsNullOrWhiteSpace(channel.EndDate)))
        {
            throw new PromotionValidationException(
                "Every channel must include code, startDate, and endDate.");
        }

        if (request.Gifts.Count == 0)
        {
            throw new PromotionValidationException("At least one gift is required.");
        }

        if (request.Gifts.Any(gift => string.IsNullOrWhiteSpace(gift.Alias)))
        {
            throw new PromotionValidationException("Every gift must include alias.");
        }

        if (request.Banner.Length == 0 ||
            !request.Banner.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new PromotionValidationException("A non-empty banner image is required.");
        }

        var hasTermsPath = !string.IsNullOrWhiteSpace(request.TermsPath);
        var hasTermsFile = request.TermsFile is { Length: > 0 };
        if (hasTermsPath == hasTermsFile)
        {
            throw new PromotionValidationException(
                "Provide either the terms text value '/terms' or a terms file, but not both.");
        }

        if (hasTermsPath && request.TermsPath != "/terms")
        {
            throw new PromotionValidationException(
                "The text terms value must be '/terms'.");
        }
    }

    private static async Task<string> CreateUniqueSlugUrlAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string promotionName,
        CancellationToken cancellationToken)
    {
        var baseSlug = Slugify(promotionName);

        while (true)
        {
            var uniqueSuffix = Guid.NewGuid().ToString("N")[..8];
            var candidate = $"/promotions/{baseSlug}-{uniqueSuffix}";

            await using var command = new MySqlCommand(
                "SELECT 1 FROM Promotions WHERE slug_url = @slugUrl LIMIT 1;",
                connection,
                transaction);
            command.Parameters.AddWithValue("@slugUrl", candidate);

            if (await command.ExecuteScalarAsync(cancellationToken) is null)
            {
                return candidate;
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

    private static DateTime ParseDate(
        string value,
        string fieldName,
        bool endOfDay)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var result))
        {
            throw new PromotionValidationException(
                $"Invalid {fieldName}; expected yyyy-MM-dd.");
        }

        return endOfDay
            ? result.Date.AddDays(1).AddSeconds(-1)
            : result.Date;
    }

    private static string Slugify(string value)
    {
        var slug = NonSlugCharacterRegex()
            .Replace(value.Trim().ToLowerInvariant(), "-")
            .Trim('-');

        if (slug.Length > 220)
        {
            slug = slug[..220].TrimEnd('-');
        }

        return string.IsNullOrWhiteSpace(slug)
            ? Guid.NewGuid().ToString("N")
            : slug;
    }

    private static string NormalizeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension) || extension.Length > 16)
        {
            return string.Empty;
        }

        return extension.ToLowerInvariant();
    }

    private static string NormalizeMySqlConnectionString(string connectionString)
    {
        var normalized = Regex.Replace(
            connectionString,
            @"(?i)Server=([^;,]+),(\d+)",
            "Server=$1;Port=$2");

        normalized = normalized.Replace(
            "Encrypt=True;",
            "SslMode=Preferred;",
            StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "Encrypt=False;",
            "SslMode=None;",
            StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "TrustServerCertificate=True;",
            string.Empty,
            StringComparison.OrdinalIgnoreCase);
        normalized = normalized.Replace(
            "TrustServerCertificate=False;",
            string.Empty,
            StringComparison.OrdinalIgnoreCase);

        return normalized;
    }

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonSlugCharacterRegex();
}
