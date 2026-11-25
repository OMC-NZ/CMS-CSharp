using MySqlConnector;

namespace CMS_CSharp.Features.Promotions.DuplicateDetection;

internal sealed class PromotionConflictDetector
{
    public async Task<PromotionConflict?> FindConflictAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        PromotionComparisonData incoming,
        CancellationToken cancellationToken,
        long? excludedPromotionId = null)
    {
        var candidates = await FindCandidatesWithEqualSetsAsync(
            connection,
            transaction,
            incoming,
            excludedPromotionId,
            cancellationToken);

        var overlapsByPromotion = await FindOverlappingChannelCodesAsync(
            connection,
            transaction,
            candidates.Select(candidate => candidate.PromotionId).ToArray(),
            incoming.Channels,
            cancellationToken);

        var giftSignatures = new Dictionary<long, List<PromotionGiftSelection>>();
        if (candidates.Count > 0)
        {
            var names = CreateParameterNames("comboPromotion", candidates.Count);
            await using var command = new MySqlCommand(
                $"SELECT promotion_id, gift_id, gift_comb FROM Promotion_Gifts WHERE promotion_id IN ({string.Join(',', names)});",
                connection, transaction);
            AddParameters(command, names, candidates.Select(x => (object)x.PromotionId).ToArray());
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = reader.GetInt64(0);
                if (!giftSignatures.TryGetValue(id, out var gifts))
                    giftSignatures[id] = gifts = [];
                gifts.Add(new PromotionGiftSelection(reader.GetInt32(1), reader.GetInt32(2)));
            }
        }
        var incomingSignature = ComboSignature(incoming.GiftSelections ??
            incoming.GiftIds.Select(id => new PromotionGiftSelection(id, 1)).ToArray());

        foreach (var candidate in candidates)
        {
            if (ComboSignature(giftSignatures.GetValueOrDefault(candidate.PromotionId) ?? []) != incomingSignature)
                continue;
            if (overlapsByPromotion.TryGetValue(candidate.PromotionId, out var overlappingCodes) &&
                overlappingCodes.Count > 0)
            {
                return new PromotionConflict(
                    candidate.PromotionId,
                    candidate.Name,
                    candidate.SlugUrl,
                    overlappingCodes);
            }
        }

        return null;
    }

    private static string ComboSignature(IEnumerable<PromotionGiftSelection> gifts) =>
        string.Join("|", gifts.GroupBy(gift => gift.GiftComb)
            .Select(group => string.Join(',', group.Select(gift => gift.GiftId).Distinct().Order()))
            .Distinct().Order(StringComparer.Ordinal));

    private static async Task<IReadOnlyList<PromotionCandidate>> FindCandidatesWithEqualSetsAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        PromotionComparisonData incoming,
        long? excludedPromotionId,
        CancellationToken cancellationToken)
    {
        var modelParameters = CreateParameterNames("model", incoming.Models.Count);
        var channelParameters = CreateParameterNames("channel", incoming.Channels.Count);
        var giftParameters = CreateParameterNames("gift", incoming.GiftIds.Count);

        var sql = $"""
            SELECT p.id, p.name, p.slug_url
            FROM Promotions p
            WHERE (@excludedPromotionId IS NULL OR p.id <> @excludedPromotionId)
              AND (SELECT COUNT(DISTINCT pd.eligible_model)
                   FROM Promotion_Devices pd
                   WHERE pd.promotion_id = p.id) = @modelCount
              {CreateNoOutsideValuesClause("Promotion_Devices", "eligible_model", modelParameters)}
              AND (SELECT COUNT(DISTINCT pc.channel_code)
                   FROM Promotion_Channels pc
                   WHERE pc.promotion_id = p.id) = @channelCount
              {CreateNoOutsideValuesClause("Promotion_Channels", "channel_code", channelParameters)}
              AND (SELECT COUNT(DISTINCT pg.gift_id)
                   FROM Promotion_Gifts pg
                   WHERE pg.promotion_id = p.id) = @giftCount
              {CreateNoOutsideValuesClause("Promotion_Gifts", "gift_id", giftParameters)};
            """;

        await using var command = new MySqlCommand(sql, connection, transaction);
        command.Parameters.AddWithValue("@modelCount", incoming.Models.Count);
        command.Parameters.AddWithValue("@channelCount", incoming.Channels.Count);
        command.Parameters.AddWithValue("@giftCount", incoming.GiftIds.Count);
        command.Parameters.AddWithValue(
            "@excludedPromotionId",
            excludedPromotionId.HasValue ? excludedPromotionId.Value : DBNull.Value);
        AddParameters(command, modelParameters, incoming.Models.Cast<object>().ToArray());
        AddParameters(
            command,
            channelParameters,
            incoming.Channels.Select(channel => (object)channel.Code).ToArray());
        AddParameters(command, giftParameters, incoming.GiftIds.Cast<object>().ToArray());

        var result = new List<PromotionCandidate>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new PromotionCandidate(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2)));
        }

        return result;
    }

    private static async Task<IReadOnlyDictionary<long, IReadOnlyList<string>>> FindOverlappingChannelCodesAsync(
        MySqlConnection connection,
        MySqlTransaction transaction,
        IReadOnlyList<long> promotionIds,
        IReadOnlyList<PromotionChannelPeriod> incomingChannels,
        CancellationToken cancellationToken)
    {
        if (incomingChannels.Count == 0 || promotionIds.Count == 0)
            return new Dictionary<long, IReadOnlyList<string>>();

        var periodsByCode = incomingChannels.ToDictionary(
            channel => channel.Code,
            StringComparer.OrdinalIgnoreCase);
        var channelParameters = CreateParameterNames("overlapChannel", incomingChannels.Count);
        var promotionParameters = CreateParameterNames("overlapPromotion", promotionIds.Count);

        await using var command = new MySqlCommand(
            $"""
            SELECT promotion_id, channel_code, start_date, end_date
            FROM Promotion_Channels
            WHERE promotion_id IN ({string.Join(", ", promotionParameters)})
              AND channel_code IN ({string.Join(", ", channelParameters)});
            """,
            connection,
            transaction);
        AddParameters(command, promotionParameters, promotionIds.Cast<object>().ToArray());
        AddParameters(
            command,
            channelParameters,
            incomingChannels.Select(channel => (object)channel.Code).ToArray());

        var result = new Dictionary<long, HashSet<string>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var promotionId = reader.GetInt64(0);
            var code = reader.GetString(1);
            var existingStart = reader.GetDateTime(2);
            var existingEnd = reader.GetDateTime(3);
            var incomingPeriod = periodsByCode[code];

            if (incomingPeriod.StartDate <= existingEnd &&
                incomingPeriod.EndDate >= existingStart)
            {
                if (!result.TryGetValue(promotionId, out var codes))
                    result[promotionId] = codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                codes.Add(code);
            }
        }

        return result.ToDictionary(
            item => item.Key,
            item => (IReadOnlyList<string>)item.Value.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string[] CreateParameterNames(string prefix, int count) =>
        Enumerable.Range(0, count).Select(index => $"@{prefix}{index}").ToArray();

    private static string CreateNoOutsideValuesClause(
        string table,
        string column,
        IReadOnlyList<string> parameterNames) =>
        parameterNames.Count == 0
            ? string.Empty
            : $"AND NOT EXISTS (SELECT 1 FROM {table} x WHERE x.promotion_id = p.id AND x.{column} NOT IN ({string.Join(", ", parameterNames)}))";

    private static void AddParameters(
        MySqlCommand command,
        IReadOnlyList<string> names,
        IReadOnlyList<object> values)
    {
        for (var index = 0; index < names.Count; index++)
        {
            command.Parameters.AddWithValue(names[index], values[index]);
        }
    }

    private sealed record PromotionCandidate(long PromotionId, string Name, string SlugUrl);
}
