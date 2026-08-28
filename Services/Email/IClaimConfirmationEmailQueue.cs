namespace CMS_CSharp.Services.Email;

internal interface IClaimConfirmationEmailQueue
{
    bool TryQueue(string claimId);
}
