namespace CMS_CSharp.Features.Promotions;

internal sealed record UpdatePromotionCommand(
    string? Name,
    string? Description,
    IReadOnlyList<PromotionProductInput>? Products,
    IReadOnlyList<PromotionChannelInput>? Channels,
    IReadOnlyList<PromotionGiftInput>? Gifts,
    string? TermsAction,
    IFormFile? TermsFile,
    IFormFile? Banner);

internal sealed record UpdatePromotionResult(
    bool Success,
    int PromotionId,
    IReadOnlyList<string> UpdatedFields,
    int ProductCount,
    int SkippedProductCount,
    int ChannelCount,
    int SkippedChannelCount,
    int GiftCount);
