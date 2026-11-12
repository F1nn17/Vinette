using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail.Images;

/// <summary>Turns names into addresses; a seam so that tests can say what a name "resolves to".</summary>
public interface IDnsResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

/// <summary>The resolver of the machine.</summary>
public sealed class SystemDnsResolver : IDnsResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) => Dns.GetHostAddressesAsync(host, cancellationToken);
}

/// <summary>Fetches the pictures a mail takes from the web, on behalf of a reader who asked to see them.</summary>
public interface IExternalImageFetcher
{
    /// <summary>
    /// Fetches <paramref name="urls"/> and returns, for those that could be fetched and are real pictures, the
    /// <c>data:image</c> address to put into the message. The rest is simply missing from the answer.
    /// </summary>
    Task<IReadOnlyDictionary<string, string>> FetchAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken);
}

/// <summary>
/// Fetches external pictures without being turned against its own network (SSRF) and without telling the sender anything
/// about the reader. The rules, each of which has a test:
/// <list type="bullet">
/// <item>Only http and https, only the configured ports, no credentials in the address.</item>
/// <item>The address is resolved <b>once, at the moment of connecting</b>, and the connection goes to an address that was
/// checked (<see cref="AddressPolicy"/>): there is no second lookup that could be answered differently (DNS rebinding), and
/// an address written as a number is checked like any other.</item>
/// <item>Redirects are followed by hand, a few times, and every target goes through the same checks again.</item>
/// <item>No cookies, no credentials, no referrer, no proxy, no compression; a time limit and a size limit on every picture
/// and on the whole message.</item>
/// <item>What comes back is judged by its first bytes: only PNG, JPEG, GIF and WebP are kept (never SVG), whatever the
/// server calls them.</item>
/// </list>
/// </summary>
public sealed partial class ExternalImageFetcher : IExternalImageFetcher, IDisposable
{
    private const int Concurrency = 6;

    private readonly ExternalImagesOptions options;
    private readonly IDnsResolver dns;
    private readonly Func<IPAddress, bool> allowed;
    private readonly ILogger<ExternalImageFetcher> logger;
    private readonly HttpClient client;

    public ExternalImageFetcher(IOptions<ExternalImagesOptions> options, IDnsResolver dns, ILogger<ExternalImageFetcher> logger, Func<IPAddress, bool>? allowedAddress = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
        this.dns = dns;
        this.logger = logger;
        allowed = allowedAddress ?? (this.options.AllowPrivateAddresses ? _ => true : AddressPolicy.IsPublic);
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = this.options.RequestTimeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(1),
            ConnectCallback = ConnectAsync,
        };
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<IReadOnlyDictionary<string, string>> FetchAsync(IReadOnlyList<string> urls, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var wanted = urls.Distinct(StringComparer.Ordinal).Take(options.MaxImages).ToList();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (wanted.Count == 0 || !options.Enabled)
        {
            return result;
        }

        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        total.CancelAfter(options.TotalTimeout);
        using var gate = new SemaphoreSlim(Concurrency);
        long bytes = 0;

        var tasks = wanted.Select(async url =>
        {
            try
            {
                await gate.WaitAsync(total.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            try
            {
                var content = await FetchOneAsync(url, total.Token).ConfigureAwait(false);
                if (content is null || InlineImage.ToDataUri(content, options.MaxImageBytes) is not { } uri)
                {
                    return;
                }

                // The whole message has a budget too; what does not fit is left out, not cut.
                if (Interlocked.Add(ref bytes, content.Length) > options.MaxTotalBytes)
                {
                    Interlocked.Add(ref bytes, -content.Length);
                    return;
                }

                lock (result)
                {
                    result[url] = uri;
                }
            }
            finally
            {
                gate.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        LogFetched(result.Count, wanted.Count);
        return result;
    }

    public void Dispose() => client.Dispose();

    /// <summary>Whether this address may be fetched at all: the scheme, the credentials and the port (the host is checked on connecting).</summary>
    internal bool IsAcceptable(Uri uri) =>
        uri.IsAbsoluteUri
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo)
        && !string.IsNullOrEmpty(uri.Host)
        && options.AllowedPorts.Contains(uri.Port);

    private async Task<byte[]?> FetchOneAsync(string address, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);
        try
        {
            if (!Uri.TryCreate(address, UriKind.Absolute, out var uri))
            {
                return null;
            }

            for (var hop = 0; ; hop++)
            {
                if (!IsAcceptable(uri))
                {
                    return null;
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (compatible)");
                request.Headers.Accept.ParseAdd("image/png,image/jpeg,image/gif,image/webp,*/*;q=0.5");
                request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
                request.Headers.Referrer = null;

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } location)
                {
                    if (hop >= options.MaxRedirects)
                    {
                        return null;
                    }

                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    continue;
                }

                return response.IsSuccessStatusCode ? await ReadLimitedAsync(response, timeout.Token).ConfigureAwait(false) : null;
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException or IOException or UriFormatException or InvalidOperationException or SocketException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null; // unreachable, refused, too slow, not an address: the picture is simply not shown
        }
    }

    private async Task<byte[]?> ReadLimitedAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is { } declared && declared > options.MaxImageBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > options.MaxImageBytes)
            {
                return null; // the server said less (or nothing) and sent more
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>Resolves the name once and connects to an address that passed the policy; there is no other way out.</summary>
    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await dns.ResolveAsync(host, cancellationToken).ConfigureAwait(false);

        var candidates = addresses.Where(allowed).ToList();
        if (candidates.Count == 0)
        {
            throw new HttpRequestException("The address is not one that pictures are fetched from.");
        }

        SocketException? last = null;
        foreach (var address in candidates)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException exception)
            {
                socket.Dispose();
                last = exception;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        throw new HttpRequestException("The server could not be reached.", last);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "External pictures of a message: {Fetched} of {Requested} fetched")]
    private partial void LogFetched(int fetched, int requested);
}
