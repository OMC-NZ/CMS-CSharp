namespace CMS_CSharp.Features.Claims;

internal sealed record UpdateClaimCommand(
    string ClaimId,
    string? Email,
    string? Contact,
    string? Street,
    string? Suburb,
    string? City,
    string? Postcode,
    string? Instructions,
    bool InstructionsProvided,
    IReadOnlyList<string>? GiftAliases,
    IFormFile? Receipt,
    IFormFile? ImeiCopy);

internal sealed record UpdateClaimResult(
    bool Success,
    string ClaimId);
