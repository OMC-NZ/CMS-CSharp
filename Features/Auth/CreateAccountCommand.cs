namespace CMS_CSharp.Features.Auth;

internal sealed record CreateAccountCommand(
    string Email,
    string Password,
    string FirstName,
    string LastName,
    string? DisplayName,
    IReadOnlyList<string>? RoleCodes);

internal sealed record CreateAccountResult(
    bool Success,
    int AccountId,
    int UserId,
    IReadOnlyList<string> RoleCodes);
