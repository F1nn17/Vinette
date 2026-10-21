using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;

namespace Vinette.Api.Security;

/// <summary>
/// Real client address, scheme and host behind a reverse proxy. Forwarded headers are believed only from the proxies
/// listed in <c>Security:TrustedProxies</c>; with no list the middleware is not installed at all, so a client can
/// never choose its own address by sending X-Forwarded-For.
/// </summary>
public static class ForwardedHeadersSetup
{
    public static IServiceCollection AddVinetteForwardedHeaders(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>()
            .Configure<IOptions<RequestSecurityOptions>>((forwarded, security) =>
            {
                forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;

                // Walk the X-Forwarded-For chain from the right while the hops are trusted: the first untrusted address
                // is the client. Whatever the client itself put at the start of the chain is never reached.
                forwarded.ForwardLimit = null;

                // ASP.NET trusts loopback by default; only what the administrator lists counts here.
                forwarded.KnownProxies.Clear();
                forwarded.KnownIPNetworks.Clear();
                foreach (var entry in security.Value.TrustedProxies)
                {
                    if (!RequestSecurityOptions.TryParseProxy(entry, out var address, out var network))
                    {
                        continue; // refused at startup by the options validation
                    }

                    if (address is not null)
                    {
                        forwarded.KnownProxies.Add(address);
                    }
                    else
                    {
                        forwarded.KnownIPNetworks.Add(network);
                    }
                }
            });
        return services;
    }

    /// <summary>Add first in the pipeline: everything after sees the real client address.</summary>
    public static IApplicationBuilder UseVinetteForwardedHeaders(this IApplicationBuilder app)
    {
        var security = app.ApplicationServices.GetRequiredService<IOptions<RequestSecurityOptions>>().Value;
        return security.TrustedProxies.Length == 0 ? app : app.UseForwardedHeaders();
    }
}
