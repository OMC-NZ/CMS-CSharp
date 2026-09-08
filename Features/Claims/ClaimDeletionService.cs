using System.Text.RegularExpressions;
using CMS_CSharp.Services.Storage;
using MySqlConnector;

namespace CMS_CSharp.Features.Claims;

internal sealed partial class ClaimDeletionService(
    IConfiguration configuration,
    IR2StorageService r2Storage,
    ILogger<ClaimDeletionService> logger)
{
    public async Task<DeleteClaimResult?> DeleteAsync(
        string claimId,
        CancellationToken cancellationToken)
    {
        var normalizedClaimId = claimId.Trim();
        if (normalizedClaimId.Length == 0)
        {
            throw new ClaimValidationException("claimId is required.");
        }

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
            var claimFiles = await FindClaimFilesAsync(
                connection,
                transaction,
                normalizedClaimId,
                cancellationToken);
            if (claimFiles is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var claimGiftsDeleted = await ExecuteDeleteAsync(
                connection,
                transaction,
                "DELETE FROM Claim_Gifts WHERE claim_id = @claimId;",
                "@claimId",
                normalizedClaimId,
                cancellationToken);
            var deliveryAddressesDeleted = await ExecuteDeleteAsync(
                connection,
                transaction,
                "DELETE FROM Deliver_Addresses WHERE claim_id = @claimId;",
                "@claimId",
                normalizedClaimId,
                cancellationToken);
            var claimsDeleted = await ExecuteDeleteAsync(
                connection,
                transaction,
                "DELETE FROM Claims WHERE id = @claimId;",
                "@claimId",
                normalizedClaimId,
                cancellationToken);
            var customersDeleted = await ExecuteDeleteAsync(
                connection,
                transaction,
                """
                DELETE FROM Customers
                WHERE id = @customerId
                  AND NOT EXISTS (
                      SELECT 1
                      FROM Claims
                      WHERE customer_id = @customerId
                  );
                """,
                "@customerId",
                claimFiles.CustomerId,
                cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var r2Prefix = $"claims/promotions/{claimFiles.PromotionId}";
            var receiptDeleteTask = r2Storage.DeleteByPrefixAsync(
                claimFiles.ReceiptUrl,
                r2Prefix,
                CancellationToken.None);
            var screenshotDeleteTask = r2Storage.DeleteByPrefixAsync(
                claimFiles.ScreenshotUrl,
                r2Prefix,
                CancellationToken.None);
            await Task.WhenAll(receiptDeleteTask, screenshotDeleteTask);
            var r2FilesDeleted =
                (await receiptDeleteTask ? 1 : 0) +
                (await screenshotDeleteTask ? 1 : 0);

            logger.LogInformation(
                "Deleted claim {ClaimId}: Claims={ClaimsDeleted}, Customers={CustomersDeleted}, DeliveryAddresses={DeliveryAddressesDeleted}, ClaimGifts={ClaimGiftsDeleted}, R2Files={R2FilesDeleted}.",
                normalizedClaimId,
                claimsDeleted,
                customersDeleted,
                deliveryAddressesDeleted,
                claimGiftsDeleted,
                r2FilesDeleted);

            return new DeleteClaimResult(
                true,
                normalizedClaimId,
                r2FilesDeleted == 2);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private static async Task<ClaimFiles?> FindClaimFilesAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string claimId,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT customer_id, promotion_id, receipt_url, screenshot_url
            FROM Claims
            WHERE id = @claimId
            LIMIT 1
            FOR UPDATE;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@claimId", claimId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ClaimFiles(
            reader.GetInt32("customer_id"),
            reader.GetInt32("promotion_id"),
            reader.GetString("receipt_url"),
            reader.GetString("screenshot_url"));
    }

    private static async Task<int> ExecuteDeleteAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string sql,
        string parameterName,
        object parameterValue,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue(parameterName, parameterValue);
        return await command.ExecuteNonQueryAsync(cancellationToken);
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

    private sealed record ClaimFiles(
        int CustomerId,
        int PromotionId,
        string ReceiptUrl,
        string ScreenshotUrl);
}

internal sealed record DeleteClaimResult(
    bool Success,
    string ClaimId,
    bool AssetsCleanupSucceeded);
