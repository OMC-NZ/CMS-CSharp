namespace CMS_CSharp.Features.Claims;

internal sealed class ClaimValidationException(string message)
    : Exception(message);

internal sealed class ClaimConflictException(string message)
    : Exception(message);
