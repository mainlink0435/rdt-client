using Microsoft.Extensions.Logging;
using System.Threading.RateLimiting;

namespace RdtClient.Service.Helpers;

/// <summary>
/// HTTP message handler that enforces a per-minute request rate limit.
/// TorBox allows 300 req/min — we budget 250 to leave headroom.
/// Static rate limiter shared across all transient instances.
/// </summary>
public class ConcurrencyLimitHandler : DelegatingHandler
{
    private readonly ILogger<ConcurrencyLimitHandler> _logger;

    private static readonly SlidingWindowRateLimiter _rateLimiter = new(
        new SlidingWindowRateLimiterOptions
        {
            PermitLimit = 250,
            QueueLimit = 5,
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            Window = TimeSpan.FromMinutes(1),
            SegmentsPerWindow = 5
        });

    public ConcurrencyLimitHandler(ILogger<ConcurrencyLimitHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var lease = await _rateLimiter.AcquireAsync(1, cancellationToken);

        if (lease.IsAcquired)
            return await base.SendAsync(request, cancellationToken);

        _logger.LogWarning("Local rate limit exceeded, rejecting {Method} {Url}", request.Method, request.RequestUri);

        throw new HttpRequestException("Local rate limit exceeded for TorBox API. Retry later.", null, System.Net.HttpStatusCode.TooManyRequests);
    }
}
