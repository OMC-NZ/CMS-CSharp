namespace CMS_CSharp.Services.Email;

internal interface IClaimConfirmationEmailService
{
    Task<bool?> SendManualAsync(
        string claimId,
        string name,
        string email,
        string gift,
        string address,
        CancellationToken cancellationToken = default);

    Task<bool> SendAsync(
        string claimId,
        CancellationToken cancellationToken = default);
}
