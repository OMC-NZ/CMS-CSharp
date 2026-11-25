namespace CMS_CSharp.Features.Promotions.DuplicateDetection;

internal sealed record PromotionChannelPeriod(
    string Code,
    DateTime StartDate,
    DateTime EndDate);

internal sealed record PromotionComparisonData(
    IReadOnlyList<string> Models,
    IReadOnlyList<PromotionChannelPeriod> Channels,
    IReadOnlyList<int> GiftIds,
    IReadOnlyList<PromotionGiftSelection>? GiftSelections = null);

internal sealed record PromotionConflict(
    long PromotionId,
    string Name,
    string SlugUrl,
    IReadOnlyList<string> OverlappingChannelCodes);
