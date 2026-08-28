using System.Threading.Channels;

namespace CMS_CSharp.Services.Email;

internal sealed class ClaimConfirmationEmailQueue(
    IServiceScopeFactory scopeFactory,
    ILogger<ClaimConfirmationEmailQueue> logger)
    : BackgroundService, IClaimConfirmationEmailQueue
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });

    public bool TryQueue(string claimId) => _queue.Writer.TryWrite(claimId);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var claimId in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var emailService = scope.ServiceProvider
                    .GetRequiredService<IClaimConfirmationEmailService>();
                await emailService.SendAsync(claimId, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    exception,
                    "Queued claim confirmation email failed for claim {ClaimId}.",
                    claimId);
            }
        }
    }
}
