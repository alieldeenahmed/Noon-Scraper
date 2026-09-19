using System.Globalization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace NoonScraper.Api.Services;

public static class RateLimitPolicies
{
    // Endpoints that fire a GitHub Actions dispatch - metered, so limited harder.
    public const string Dispatch = "dispatch";
}

// Three layers, all fixed one-minute windows, all configurable under
// "RateLimiting:*" (defaults in brackets):
//  - every request, per client IP (GlobalPerMinute, 300)
//  - dispatch endpoints, per client IP (DispatchPerMinute, 5)
//  - dispatch endpoints, all clients combined (DispatchTotalPerMinute, 30)
//
// The combined bucket is the one that actually protects the GitHub Actions
// quota: per-IP limits are only as trustworthy as the client IP, and behind a
// proxy chain of unknown depth that comes from a spoofable X-Forwarded-For
// header (see Program.cs). The combined cap holds no matter what IP is claimed.
public static class RateLimitingExtensions
{
    public static IServiceCollection AddApiRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });

        // Configured through IConfiguration at first use rather than at
        // registration, so overrides (e.g. from integration tests) apply.
        services.AddOptions<RateLimiterOptions>()
            .Configure<IConfiguration>((options, configuration) =>
            {
                var globalPerMinute = configuration.GetValue("RateLimiting:GlobalPerMinute", 300);
                var dispatchPerMinute = configuration.GetValue("RateLimiting:DispatchPerMinute", 5);
                var dispatchTotalPerMinute = configuration.GetValue("RateLimiting:DispatchTotalPerMinute", 30);

                options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

                options.OnRejected = (context, _) =>
                {
                    if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                    {
                        context.HttpContext.Response.Headers.RetryAfter =
                            ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                    }

                    return ValueTask.CompletedTask;
                };

                options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                    PartitionedRateLimiter.Create<HttpContext, string>(context =>
                        RateLimitPartition.GetFixedWindowLimiter(
                            $"ip:{ClientKey(context)}", _ => PerMinute(globalPerMinute))),
                    PartitionedRateLimiter.Create<HttpContext, string>(context =>
                        IsDispatchEndpoint(context)
                            ? RateLimitPartition.GetFixedWindowLimiter("dispatch-total", _ => PerMinute(dispatchTotalPerMinute))
                            : RateLimitPartition.GetNoLimiter("not-dispatch")));

                options.AddPolicy(RateLimitPolicies.Dispatch, context =>
                    RateLimitPartition.GetFixedWindowLimiter(
                        $"dispatch-ip:{ClientKey(context)}", _ => PerMinute(dispatchPerMinute)));
            });

        return services;
    }

    private static string ClientKey(HttpContext context) =>
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    private static bool IsDispatchEndpoint(HttpContext context) =>
        context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
            == RateLimitPolicies.Dispatch;

    private static FixedWindowRateLimiterOptions PerMinute(int permitLimit) => new()
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromMinutes(1),
        QueueLimit = 0
    };
}
