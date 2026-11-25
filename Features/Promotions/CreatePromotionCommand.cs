namespace CMS_CSharp.Features.Promotions;

internal sealed record CreatePromotionCommand(
    string Name,
    string Description,
    IReadOnlyList<PromotionProductInput> Products,
    IReadOnlyList<PromotionChannelInput> Channels,
    IReadOnlyList<PromotionGiftInput> Gifts,
    string? TermsPath,
    IFormFile? TermsFile,
    IFormFile Banner);

internal sealed record PromotionProductInput(string Model);

internal sealed record PromotionChannelInput(
    string Code,
    string StartDate,
    string EndDate);

internal sealed record PromotionGiftInput(
    string Alias,
    [property: System.Text.Json.Serialization.JsonPropertyName("gift_comb")] int GiftComb = 1);

internal sealed record PromotionGiftSelection(int GiftId, int GiftComb);

internal sealed record CreatePromotionResult(
    long Id,
    string Name,
    string SlugUrl,
    string TermsUrl,
    string BannerFileName,
    int ProductCount,
    int SkippedProductCount,
    int ChannelCount,
    int SkippedChannelCount,
    int GiftCount);
