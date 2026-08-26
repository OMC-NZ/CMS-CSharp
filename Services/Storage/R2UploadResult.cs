namespace CMS_CSharp.Services.Storage;

internal sealed record R2UploadResult(
    string ObjectKey,
    string PublicUrl,
    string? ETag,
    long Size);
