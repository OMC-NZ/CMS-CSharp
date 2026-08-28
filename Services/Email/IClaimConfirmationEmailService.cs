namespace CMS_CSharp.Services.Email;

internal interface IClaimConfirmationEmailService
{
    Task<bool> SendAsync(
        string claimId,
        CancellationToken cancellationToken = default);
}
