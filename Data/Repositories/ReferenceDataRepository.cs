using MySqlConnector;

namespace CMS_CSharp.Data.Repositories;

internal sealed class ReferenceDataRepository : IReferenceDataRepository
{
    public async Task<bool> ChannelExistsByCodeAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string code,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT EXISTS(
                SELECT 1
                FROM Channels
                WHERE code = @code
            );
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@code", code.Trim());

        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) != 0;
    }

    public async Task<IReadOnlyList<string>> GetDeviceChannelCodesByModelAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string model,
        IReadOnlyList<string> selectedChannelCodes,
        CancellationToken cancellationToken)
    {
        if (selectedChannelCodes.Count == 0)
        {
            return [];
        }

        var channelParameters = selectedChannelCodes
            .Select((_, index) => $"@channel{index}")
            .ToArray();
        await using var command = new MySqlCommand(
            $"""
            SELECT DISTINCT channel_code
            FROM Devices
            WHERE model = @model
              AND channel_code IN ({string.Join(", ", channelParameters)});
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@model", model.Trim());
        for (var index = 0; index < selectedChannelCodes.Count; index++)
        {
            command.Parameters.AddWithValue(channelParameters[index], selectedChannelCodes[index]);
        }

        var result = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return result;
    }

    public async Task<int?> FindGiftIdByAliasAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        string alias,
        CancellationToken cancellationToken)
    {
        await using var command = new MySqlCommand(
            """
            SELECT id
            FROM Gifts
            WHERE alias = @alias
            LIMIT 1;
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("@alias", alias.Trim());

        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null ? null : Convert.ToInt32(result);
    }
}
