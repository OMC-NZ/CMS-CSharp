using MySqlConnector;

namespace CMS_CSharp.Data.Repositories;

internal interface IReferenceDataRepository
{
    Task<bool> ChannelExistsByCodeAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        string code,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<string>> GetDeviceChannelCodesByModelAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        string model,
        IReadOnlyList<string> selectedChannelCodes,
        CancellationToken cancellationToken);

    Task<int?> FindGiftIdByAliasAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        string alias,
        CancellationToken cancellationToken);

    Task<IReadOnlySet<string>> FindExistingChannelCodesAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        IReadOnlyList<string> codes,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetDeviceChannelCodesByModelsAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        IReadOnlyList<string> models,
        IReadOnlyList<string> selectedChannelCodes,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, int>> FindGiftIdsByAliasesAsync(
        MySqlConnection connection,
        MySqlTransaction? transaction,
        IReadOnlyList<string> aliases,
        CancellationToken cancellationToken);
}
