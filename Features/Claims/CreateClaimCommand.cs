namespace CMS_CSharp.Features.Claims;

internal sealed record CreateClaimCommand(
    int PromotionId,
    string Imei,
    string PurchaseDate,
    string FirstName,
    string LastName,
    string Email,
    string Contact,
    string Street,
    string Suburb,
    string City,
    string Postcode,
    string? Instructions,
    IReadOnlyList<string> GiftAliases,
    IFormFile Receipt,
    IFormFile Screenshot);

internal sealed record CreateClaimResult(
    string Id,
    int PromotionId,
    int CustomerId,
    string Imei,
    IReadOnlyList<int> GiftIds,
    string ReceiptUrl,
    string ScreenshotUrl,
    bool EmailQueued);
