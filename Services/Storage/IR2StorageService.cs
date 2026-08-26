namespace CMS_CSharp.Services.Storage;

internal interface IR2StorageService
{
    Task<R2UploadResult> UploadAsync(
        Stream content,
        string objectKey,
        string? contentType = null,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default);
}
