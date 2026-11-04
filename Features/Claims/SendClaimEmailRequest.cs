namespace CMS_CSharp.Features.Claims;

internal sealed record SendClaimEmailRequest(
    string? Name,
    string? Email,
    string? Gift,
    string? Address);
