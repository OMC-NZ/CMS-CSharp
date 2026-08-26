using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace CMS_CSharp.Services.Storage;

internal sealed class R2StorageService : IR2StorageService, IDisposable
{
    private const int CopyBufferSize = 81920;

    private readonly Lazy<R2ClientContext> _context;

    public R2StorageService(IConfiguration configuration)
    {
        _context = new Lazy<R2ClientContext>(
            () => CreateContext(configuration),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public async Task<R2UploadResult> UploadAsync(
        Stream content,
        string objectKey,
        string? contentType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var context = _context.Value;

        if (!content.CanRead)
        {
            throw new ArgumentException("The upload stream must be readable.", nameof(content));
        }

        var normalizedObjectKey = NormalizeObjectKey(objectKey);
        Stream uploadStream = content;
        MemoryStream? bufferedStream = null;

        try
        {
            long size;
            if (content.CanSeek)
            {
                size = content.Length - content.Position;
                EnsureAllowedSize(size);
            }
            else
            {
                bufferedStream = await BufferWithinLimitAsync(content, cancellationToken);
                uploadStream = bufferedStream;
                size = bufferedStream.Length;
            }

            var request = new PutObjectRequest
            {
                BucketName = context.Bucket,
                Key = normalizedObjectKey,
                InputStream = uploadStream,
                ContentType = string.IsNullOrWhiteSpace(contentType)
                    ? "application/octet-stream"
                    : contentType,
                AutoCloseStream = false,
                UseChunkEncoding = false,
                DisablePayloadSigning = true,
                DisableDefaultChecksumValidation = true
            };

            var response = await context.Client.PutObjectAsync(request, cancellationToken);

            return new R2UploadResult(
                normalizedObjectKey,
                BuildPublicUrl(context.PublicAssetsUrl, normalizedObjectKey),
                response.ETag,
                size);
        }
        finally
        {
            if (bufferedStream is not null)
            {
                await bufferedStream.DisposeAsync();
            }
        }
    }

    public async Task DeleteAsync(
        string objectKey,
        CancellationToken cancellationToken = default)
    {
        var normalizedObjectKey = NormalizeObjectKey(objectKey);
        var context = _context.Value;

        await context.Client.DeleteObjectAsync(
            new DeleteObjectRequest
            {
                BucketName = context.Bucket,
                Key = normalizedObjectKey
            },
            cancellationToken);
    }

    public void Dispose()
    {
        if (_context.IsValueCreated)
        {
            _context.Value.Client.Dispose();
        }
    }

    private async Task<MemoryStream> BufferWithinLimitAsync(
        Stream source,
        CancellationToken cancellationToken)
    {
        var destination = new MemoryStream();
        var buffer = new byte[CopyBufferSize];
        long totalBytes = 0;

        try
        {
            while (true)
            {
                var bytesRead = await source.ReadAsync(buffer, cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                totalBytes += bytesRead;
                EnsureAllowedSize(totalBytes);
                await destination.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
            }

            destination.Position = 0;
            return destination;
        }
        catch
        {
            await destination.DisposeAsync();
            throw;
        }
    }

    private void EnsureAllowedSize(long size)
    {
        var uploadMaxBytes = _context.Value.UploadMaxBytes;
        if (size > uploadMaxBytes)
        {
            throw new InvalidOperationException(
                $"The file size ({size} bytes) exceeds R2_UPLOAD_MAX_BYTES ({uploadMaxBytes} bytes).");
        }
    }

    private static string BuildPublicUrl(string publicAssetsUrl, string objectKey)
    {
        var encodedKey = string.Join(
            '/',
            objectKey.Split('/').Select(Uri.EscapeDataString));

        return $"{publicAssetsUrl}/{encodedKey}";
    }

    private static string NormalizeObjectKey(string objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey))
        {
            throw new ArgumentException("The R2 object key is required.", nameof(objectKey));
        }

        var normalized = objectKey.Trim().Replace('\\', '/').TrimStart('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
        {
            throw new ArgumentException("The R2 object key is invalid.", nameof(objectKey));
        }

        return string.Join('/', segments);
    }

    private static string GetRequiredSetting(IConfiguration configuration, string key)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{key} is not configured.");
        }

        return value;
    }

    private static R2ClientContext CreateContext(IConfiguration configuration)
    {
        var endpoint = GetRequiredSetting(configuration, "R2_ENDPOINT");
        var bucket = GetRequiredSetting(configuration, "R2_BUCKET");
        var publicAssetsUrl = GetRequiredSetting(configuration, "R2_PUBLIC_ASSETS_URL")
            .TrimEnd('/');
        var accessKeyId = GetRequiredSetting(configuration, "R2_ACCESS_KEY_ID");
        var secretAccessKey = GetRequiredSetting(configuration, "R2_SECRET_ACCESS_KEY");
        var uploadMaxBytes = configuration.GetValue<long>("R2_UPLOAD_MAX_BYTES");

        if (uploadMaxBytes <= 0)
        {
            throw new InvalidOperationException(
                "R2_UPLOAD_MAX_BYTES must be configured with a value greater than zero.");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException("R2_ENDPOINT must be a valid absolute URL.");
        }

        if (!Uri.TryCreate(publicAssetsUrl, UriKind.Absolute, out _))
        {
            throw new InvalidOperationException(
                "R2_PUBLIC_ASSETS_URL must be a valid absolute URL.");
        }

        var credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
        var clientConfiguration = new AmazonS3Config
        {
            ServiceURL = endpoint.TrimEnd('/'),
            ForcePathStyle = true,
            AuthenticationRegion = "auto"
        };

        return new R2ClientContext(
            new AmazonS3Client(credentials, clientConfiguration),
            bucket,
            publicAssetsUrl,
            uploadMaxBytes);
    }

    private sealed record R2ClientContext(
        AmazonS3Client Client,
        string Bucket,
        string PublicAssetsUrl,
        long UploadMaxBytes);
}
