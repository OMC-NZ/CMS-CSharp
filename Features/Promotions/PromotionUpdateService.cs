using System.Globalization;
using System.Text.RegularExpressions;
using CMS_CSharp.Data.Repositories;
using CMS_CSharp.Features.Promotions.DuplicateDetection;
using CMS_CSharp.Services.Storage;
using MySqlConnector;

namespace CMS_CSharp.Features.Promotions;

internal sealed partial class PromotionUpdateService(
    IConfiguration configuration,
    IR2StorageService r2Storage,
    IReferenceDataRepository referenceData,
    PromotionConflictDetector conflictDetector,
    ILogger<PromotionUpdateService> logger)
{
    public async Task<UpdatePromotionResult?> UpdateAsync(
        int promotionId,
        UpdatePromotionCommand request,
        CancellationToken cancellationToken)
    {
        if (promotionId <= 0)
        {
            throw new PromotionValidationException(
                "promotionId must be greater than zero.");
        }

        var name = NormalizeOptionalText(request.Name, "name");
        var description = NormalizeOptionalText(request.Description, "description");
        var termsAction = ParseTermsAction(request.TermsAction);
        ValidateFilesAndChanges(request, name, description, termsAction);

        var uploadedKeys = new List<string>();
        string? newBannerFileName = null;
        string? newTermsUrl = null;

        try
        {
            Task<R2UploadResult>? bannerUpload = null;
            Task<R2UploadResult>? termsUpload = null;
            string? bannerKey = null;
            string? termsKey = null;

            if (request.Banner is not null)
            {
                newBannerFileName = $"{Guid.NewGuid():N}{NormalizeExtension(request.Banner.FileName)}";
                bannerKey = $"banners/Promotions/{newBannerFileName}";
                bannerUpload = UploadFileAsync(request.Banner, bannerKey, cancellationToken);
            }
            if (termsAction == PromotionTermsAction.Upload)
            {
                var termsFile = request.TermsFile!;
                var termsFileName = $"{Guid.NewGuid():N}{NormalizeExtension(termsFile.FileName)}";
                termsKey = $"terms/Promotions/{termsFileName}";
                termsUpload = UploadFileAsync(termsFile, termsKey, cancellationToken);
            }

            try
            {
                var uploads = new[] { bannerUpload, termsUpload }
                    .Where(task => task is not null)
                    .Cast<Task<R2UploadResult>>()
                    .ToArray();
                if (uploads.Length > 0)
                {
                    await Task.WhenAll(uploads);
                }
            }
            finally
            {
                if (bannerUpload?.IsCompletedSuccessfully == true)
                {
                    uploadedKeys.Add(bannerKey!);
                }
                if (termsUpload?.IsCompletedSuccessfully == true)
                {
                    uploadedKeys.Add(termsKey!);
                }
            }

            if (termsUpload is not null)
            {
                newTermsUrl = (await termsUpload).PublicUrl;
            }

            var databaseResult = await SaveAsync(
                promotionId,
                request,
                name,
                description,
                termsAction,
                newBannerFileName,
                newTermsUrl,
                cancellationToken);
            if (databaseResult is null)
            {
                await DeleteNewUploadsAsync(uploadedKeys);
                return null;
            }

            try
            {
                await DeleteReplacedAssetsAsync(databaseResult);
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Promotion {PromotionId} was updated, but an old R2 asset could not be removed.",
                    promotionId);
            }
            return databaseResult.Result;
        }
        catch
        {
            await DeleteNewUploadsAsync(uploadedKeys);
            throw;
        }
    }

    private async Task<DatabaseUpdateResult?> SaveAsync(
        int promotionId,
        UpdatePromotionCommand request,
        string? name,
        string? description,
        PromotionTermsAction termsAction,
        string? newBannerFileName,
        string? newTermsUrl,
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
            var current = await LoadPromotionAsync(
                connection, transaction, promotionId, cancellationToken);
            if (current is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var currentModels = await LoadModelsAsync(
                connection, transaction, promotionId, cancellationToken);
            var currentChannels = await LoadChannelsAsync(
                connection, transaction, promotionId, cancellationToken);
            var currentGiftIds = await LoadGiftIdsAsync(
                connection, transaction, promotionId, cancellationToken);

            var submittedModels = request.Products is null
                ? currentModels
                : NormalizeModels(request.Products);
            var submittedChannels = request.Channels is null
                ? currentChannels
                : NormalizeChannels(request.Channels);

            var relationChangeRequested = request.Products is not null || request.Channels is not null;
            IReadOnlyList<string> validModels;
            IReadOnlyList<PromotionChannelPeriod> effectiveChannels;
            int skippedProducts;
            int skippedChannels;
            if (relationChangeRequested)
            {
                (validModels, effectiveChannels, skippedProducts, skippedChannels) =
                    await ResolveDeviceAndChannelRelationsAsync(
                        connection,
                        transaction,
                        submittedModels,
                        submittedChannels,
                        cancellationToken);
            }
            else
            {
                validModels = currentModels;
                effectiveChannels = currentChannels;
                skippedProducts = 0;
                skippedChannels = 0;
            }

            var giftIds = request.Gifts is null
                ? currentGiftIds
                : await ResolveGiftIdsAsync(
                    connection, transaction, request.Gifts, cancellationToken);

            if (relationChangeRequested || request.Gifts is not null)
            {
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
                    cancellationToken,
                    promotionId);
                if (conflict is not null)
                {
                    throw new PromotionConflictException(conflict);
                }
            }

            var updatedFields = new List<string>();
            var assignments = new List<string>();
            await using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;

            if (name is not null)
            {
                assignments.Add("name = @name");
                updateCommand.Parameters.AddWithValue("@name", name);
                updatedFields.Add("name");
            }
            if (description is not null)
            {
                assignments.Add("description = @description");
                updateCommand.Parameters.AddWithValue("@description", description);
                updatedFields.Add("description");
            }
            if (newBannerFileName is not null)
            {
                assignments.Add("banner_url = @bannerUrl");
                updateCommand.Parameters.AddWithValue("@bannerUrl", newBannerFileName);
                updatedFields.Add("banner");
            }
            if (termsAction == PromotionTermsAction.Upload)
            {
                assignments.Add("terms_url = @termsUrl");
                updateCommand.Parameters.AddWithValue("@termsUrl", newTermsUrl!);
                updatedFields.Add("terms");
            }
            else if (termsAction == PromotionTermsAction.Delete)
            {
                assignments.Add("terms_url = '/terms'");
                updatedFields.Add("terms");
            }
            if (relationChangeRequested)
            {
                updatedFields.Add("products");
                updatedFields.Add("channels");
            }
            if (request.Gifts is not null)
            {
                updatedFields.Add("gifts");
            }

            assignments.Add("updated_at = @updatedAt");
            updateCommand.Parameters.AddWithValue("@updatedAt", DateTime.UtcNow);
            updateCommand.Parameters.AddWithValue("@promotionId", promotionId);
            updateCommand.CommandText = $"""
                UPDATE Promotions
                SET {string.Join(", ", assignments)}
                WHERE id = @promotionId;
                """;
            await updateCommand.ExecuteNonQueryAsync(cancellationToken);

            if (relationChangeRequested)
            {
                await ReplaceDeviceAndChannelRelationsAsync(
                    connection,
                    transaction,
                    promotionId,
                    validModels,
                    effectiveChannels,
                    cancellationToken);
            }
            if (request.Gifts is not null)
            {
                await ReplaceGiftRelationsAsync(
                    connection,
                    transaction,
                    promotionId,
                    giftIds,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            return new DatabaseUpdateResult(
                new UpdatePromotionResult(
                    true,
                    promotionId,
                    updatedFields,
                    validModels.Count,
                    skippedProducts,
                    effectiveChannels.Count,
                    skippedChannels,
                    giftIds.Distinct().Count()),
                newBannerFileName is null ? null : current.BannerUrl,
                (termsAction is PromotionTermsAction.Upload or PromotionTermsAction.Delete) &&
                !string.Equals(current.TermsUrl, "/terms", StringComparison.OrdinalIgnoreCase)
                    ? current.TermsUrl
                    : null);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<(IReadOnlyList<string> Models,
        IReadOnlyList<PromotionChannelPeriod> Channels,
        int SkippedProducts,
        int SkippedChannels)> ResolveDeviceAndChannelRelationsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        IReadOnlyList<string> models,
        IReadOnlyList<PromotionChannelPeriod> channels,
        CancellationToken cancellationToken)
    {
        var channelCodes = channels.Select(channel => channel.Code).ToArray();
        var existingCodes = await referenceData.FindExistingChannelCodesAsync(
            connection, transaction, channelCodes, cancellationToken);
        var validSubmittedChannels = channels
            .Where(channel => existingCodes.Contains(channel.Code))
            .ToArray();
        var skippedMissingChannels = channels.Count - validSubmittedChannels.Length;

        var validChannelCodes = validSubmittedChannels.Select(channel => channel.Code).ToArray();
        var channelsByModel = await referenceData.GetDeviceChannelCodesByModelsAsync(
            connection, transaction, models, validChannelCodes, cancellationToken);
        var validModels = new List<string>();
        var matchedChannelCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            if (!channelsByModel.TryGetValue(model, out var codes) || codes.Count == 0)
            {
                logger.LogWarning(
                    "Skipping promotion device model {Model}; it does not match any selected channel.",
                    model);
                continue;
            }
            validModels.Add(model);
            matchedChannelCodes.UnionWith(codes);
        }

        var effectiveChannels = validSubmittedChannels
            .Where(channel => matchedChannelCodes.Contains(channel.Code))
            .ToArray();
        return (
            validModels,
            effectiveChannels,
            models.Count - validModels.Count,
            skippedMissingChannels + validSubmittedChannels.Length - effectiveChannels.Length);
    }

    private async Task<IReadOnlyList<int>> ResolveGiftIdsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        IReadOnlyList<PromotionGiftInput> gifts,
        CancellationToken cancellationToken)
    {
        if (gifts.Any(gift => string.IsNullOrWhiteSpace(gift.Alias)))
        {
            throw new PromotionValidationException("Every gift must include alias.");
        }
        var aliases = gifts.Select(gift => gift.Alias.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var idsByAlias = await referenceData.FindGiftIdsByAliasesAsync(
            connection, transaction, aliases, cancellationToken);
        var ids = new List<int>();
        foreach (var alias in aliases)
        {
            if (!idsByAlias.TryGetValue(alias, out var id))
            {
                throw new PromotionValidationException(
                    $"Gift alias '{alias}' does not exist.");
            }
            ids.Add(id);
        }
        return ids;
    }

    private static IReadOnlyList<string> NormalizeModels(
        IReadOnlyList<PromotionProductInput> products)
    {
        if (products.Any(product => string.IsNullOrWhiteSpace(product.Model)))
        {
            throw new PromotionValidationException("Every product must include model.");
        }
        return products.Select(product => product.Model.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<PromotionChannelPeriod> NormalizeChannels(
        IReadOnlyList<PromotionChannelInput> channels)
    {
        if (channels.Any(channel =>
                string.IsNullOrWhiteSpace(channel.Code) ||
                string.IsNullOrWhiteSpace(channel.StartDate) ||
                string.IsNullOrWhiteSpace(channel.EndDate)))
        {
            throw new PromotionValidationException(
                "Every channel must include code, startDate, and endDate.");
        }

        var result = new List<PromotionChannelPeriod>();
        foreach (var channel in channels.DistinctBy(
                     item => item.Code.Trim(), StringComparer.OrdinalIgnoreCase))
        {
            var startDate = ParseDate(channel.StartDate, "channel startDate", false);
            var endDate = ParseDate(channel.EndDate, "channel endDate", true);
            if (endDate < startDate)
            {
                throw new PromotionValidationException(
                    $"Channel '{channel.Code}' end date must not be earlier than its start date.");
            }
            result.Add(new PromotionChannelPeriod(channel.Code.Trim(), startDate, endDate));
        }
        return result;
    }

    private static async Task<CurrentPromotion?> LoadPromotionAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int promotionId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT banner_url, terms_url
            FROM Promotions
            WHERE id = @promotionId
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@promotionId", promotionId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new CurrentPromotion(
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? "/terms" : reader.GetString(1))
            : null;
    }

    private static async Task<IReadOnlyList<string>> LoadModelsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int promotionId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT eligible_model FROM Promotion_Devices WHERE promotion_id = @promotionId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@promotionId", promotionId);
        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<IReadOnlyList<PromotionChannelPeriod>> LoadChannelsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int promotionId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT channel_code, start_date, end_date FROM Promotion_Channels WHERE promotion_id = @promotionId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@promotionId", promotionId);
        var result = new List<PromotionChannelPeriod>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PromotionChannelPeriod(
                reader.GetString(0), reader.GetDateTime(1), reader.GetDateTime(2)));
        }
        return result;
    }

    private static async Task<IReadOnlyList<int>> LoadGiftIdsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int promotionId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            "SELECT gift_id FROM Promotion_Gifts WHERE promotion_id = @promotionId;",
            connection,
            transaction);
        command.Parameters.AddWithValue("@promotionId", promotionId);
        var result = new List<int>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetInt32(0));
        return result;
    }

    private static async Task ReplaceDeviceAndChannelRelationsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int promotionId,
        IReadOnlyList<string> models,
        IReadOnlyList<PromotionChannelPeriod> channels,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "DELETE FROM Promotion_Devices WHERE promotion_id = @promotionId;",
            cancellationToken, ("@promotionId", promotionId));
        await ExecuteAsync(connection, transaction,
            "DELETE FROM Promotion_Channels WHERE promotion_id = @promotionId;",
            cancellationToken, ("@promotionId", promotionId));

        foreach (var model in models)
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO Promotion_Devices (promotion_id, eligible_model) VALUES (@promotionId, @model);",
                cancellationToken, ("@promotionId", promotionId), ("@model", model));
        }
        var updatedAt = DateTime.UtcNow;
        foreach (var channel in channels)
        {
            await ExecuteAsync(connection, transaction,
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
    }

    private static async Task ReplaceGiftRelationsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        int promotionId,
        IReadOnlyList<int> giftIds,
        CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, transaction,
            "DELETE FROM Promotion_Gifts WHERE promotion_id = @promotionId;",
            cancellationToken, ("@promotionId", promotionId));
        foreach (var giftId in giftIds.Distinct())
        {
            await ExecuteAsync(connection, transaction,
                "INSERT INTO Promotion_Gifts (promotion_id, gift_id) VALUES (@promotionId, @giftId);",
                cancellationToken, ("@promotionId", promotionId), ("@giftId", giftId));
        }
    }

    private async Task<R2UploadResult> UploadFileAsync(
        IFormFile file,
        string objectKey,
        CancellationToken cancellationToken)
    {
        await using var stream = file.OpenReadStream();
        return await r2Storage.UploadAsync(stream, objectKey, file.ContentType, cancellationToken);
    }

    private async Task DeleteNewUploadsAsync(IEnumerable<string> keys)
    {
        foreach (var key in keys.Reverse())
        {
            try { await r2Storage.DeleteAsync(key, CancellationToken.None); }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to remove new R2 object {ObjectKey}.", key);
            }
        }
    }

    private async Task DeleteReplacedAssetsAsync(DatabaseUpdateResult result)
    {
        if (!string.IsNullOrWhiteSpace(result.OldBannerUrl))
        {
            await r2Storage.DeleteByPrefixAsync(
                result.OldBannerUrl, "banners/Promotions", CancellationToken.None);
        }
        if (!string.IsNullOrWhiteSpace(result.OldTermsUrl))
        {
            await r2Storage.DeleteByPrefixAsync(
                result.OldTermsUrl, "terms/Promotions", CancellationToken.None);
        }
    }

    private static void ValidateFilesAndChanges(
        UpdatePromotionCommand request,
        string? name,
        string? description,
        PromotionTermsAction termsAction)
    {
        if (request.Banner is { Length: 0 } ||
            request.Banner is not null &&
            !request.Banner.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            throw new PromotionValidationException(
                "banner must be a non-empty image when provided.");
        }
        if (termsAction == PromotionTermsAction.Upload &&
            request.TermsFile is not { Length: > 0 })
        {
            throw new PromotionValidationException(
                "termsAction 'upload' requires a non-empty terms file.");
        }
        if (termsAction != PromotionTermsAction.Upload && request.TermsFile is not null)
        {
            throw new PromotionValidationException(
                "A terms file is allowed only when termsAction is 'upload'.");
        }
        if (name is null && description is null && request.Products is null &&
            request.Channels is null && request.Gifts is null && request.Banner is null &&
            termsAction == PromotionTermsAction.Keep)
        {
            throw new PromotionValidationException(
                "At least one Promotion field or file change is required.");
        }
    }

    private static PromotionTermsAction ParseTermsAction(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Equals("keep", StringComparison.OrdinalIgnoreCase))
        {
            return PromotionTermsAction.Keep;
        }
        if (value.Equals("upload", StringComparison.OrdinalIgnoreCase))
        {
            return PromotionTermsAction.Upload;
        }
        if (value.Equals("delete", StringComparison.OrdinalIgnoreCase))
        {
            return PromotionTermsAction.Delete;
        }
        throw new PromotionValidationException(
            "termsAction must be 'keep', 'upload', or 'delete'.");
    }

    private static string? NormalizeOptionalText(string? value, string fieldName)
    {
        if (value is null) return null;
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new PromotionValidationException($"{fieldName} must not be empty when provided.");
        }
        return normalized;
    }

    private static DateTime ParseDate(string value, string fieldName, bool endOfDay)
    {
        if (!DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var result))
        {
            throw new PromotionValidationException(
                $"Invalid {fieldName}; expected yyyy-MM-dd.");
        }
        return endOfDay ? result.Date.AddDays(1).AddSeconds(-1) : result.Date;
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

    private static string NormalizeExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return string.IsNullOrWhiteSpace(extension) || extension.Length > 16
            ? string.Empty
            : extension.ToLowerInvariant();
    }

    private static string NormalizeMySqlConnectionString(string connectionString)
    {
        var normalized = ConnectionPortRegex().Replace(connectionString, "Server=$1;Port=$2")
            .Replace("Encrypt=True;", "SslMode=Preferred;", StringComparison.OrdinalIgnoreCase)
            .Replace("Encrypt=False;", "SslMode=None;", StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=True;", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("TrustServerCertificate=False;", string.Empty, StringComparison.OrdinalIgnoreCase);
        return new MySqlConnectionStringBuilder(normalized)
        {
            TreatTinyAsBoolean = false
        }.ConnectionString;
    }

    [GeneratedRegex(@"(?i)Server=([^;,]+),(\d+)")]
    private static partial Regex ConnectionPortRegex();

    private enum PromotionTermsAction { Keep, Upload, Delete }

    private sealed record CurrentPromotion(string BannerUrl, string TermsUrl);

    private sealed record DatabaseUpdateResult(
        UpdatePromotionResult Result,
        string? OldBannerUrl,
        string? OldTermsUrl);
}
