using System.ComponentModel.DataAnnotations;

namespace Vinette.Api.Configuration;

/// <summary>Bound to the "Security" section.</summary>
public sealed class RequestSecurityOptions : IValidatableObject
{
    public const string SectionName = "Security";

    /// <summary>
    /// Origins (scheme://host[:port], no path) whose pages may send state-changing requests, in addition to the
    /// application's own address. Needed when the browser address differs from the server's view of it
    /// (a front-end dev server, unusual proxy setups).
    /// </summary>
    public string[] TrustedOrigins { get; set; } = [];

    /// <summary>
    /// Reverse proxies (addresses or CIDR networks, e.g. "10.0.0.0/8") whose X-Forwarded-For/-Proto/-Host headers are
    /// believed. Empty (the default) means the headers are ignored and the address of the TCP peer is the client.
    /// </summary>
    public string[] TrustedProxies { get; set; } = [];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var proxy in TrustedProxies)
        {
            if (!TryParseProxy(proxy, out _, out _))
            {
                yield return new ValidationResult(
                    $"'{proxy}' is not a usable trusted proxy: give an IP address or a CIDR network (for example 10.0.0.0/8); "
                    + "a network of everything (0.0.0.0/0, ::/0) would let anyone forge the client address.",
                    [nameof(TrustedProxies)]);
            }
        }

        foreach (var origin in TrustedOrigins)
        {
            if (!IsOrigin(origin))
            {
                yield return new ValidationResult(
                    $"'{origin}' is not an origin: use scheme://host[:port] without a path.",
                    [nameof(TrustedOrigins)]);
            }
        }
    }

    /// <summary>Parses one entry: a single address or a network. The unspecified address and /0 networks are refused.</summary>
    internal static bool TryParseProxy(string value, out System.Net.IPAddress? address, out System.Net.IPNetwork network)
    {
        address = null;
        network = default;
        if (System.Net.IPAddress.TryParse(value, out var single))
        {
            address = single;
            return !single.Equals(System.Net.IPAddress.Any) && !single.Equals(System.Net.IPAddress.IPv6Any);
        }

        return System.Net.IPNetwork.TryParse(value, out network) && network.PrefixLength > 0;
    }

    internal static bool IsOrigin(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.Equals(value, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase);
}
