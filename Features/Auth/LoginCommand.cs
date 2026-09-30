namespace CMS_CSharp.Features.Auth;

internal sealed record LoginCommand(string Email, string Password);

internal sealed record LoginResult(
    bool Success,
    string AccessToken,
    string RefreshToken,
    string TokenType,
    DateTime ExpiresAt,
    LoginUserResult User,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions);

internal sealed record LoginUserResult(
    int Id,
    int AccountId,
    string Email,
    string FirstName,
    string LastName,
    string DisplayName);
