using System.Collections.Concurrent;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;
using Vinette.Api.Mail.Images;

namespace Vinette.Api.Tests;

/// <summary>
/// The fetcher against a real server on this machine. The default policy refuses this machine, so most tests let the
/// loopback address through (and nothing else) to have something to talk to; the rest say what the default does.
/// </summary>
public sealed class ExternalImageFetcherTests : IAsyncLifetime
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private WebApplication app = null!;
    private int port;
    private readonly ConcurrentQueue<IHeaderDictionary> seen = new();
    private int hits;

    /// <summary>A PNG signature followed by filler, <paramref name="length"/> bytes in all.</summary>
    private static byte[] Png(int length = 200)
    {
        var bytes = new byte[length];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    public async ValueTask InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        app = builder.Build();
        app.MapGet("/ok.png", (HttpContext c) =>
        {
            Interlocked.Increment(ref hits);
            seen.Enqueue(new HeaderDictionary(c.Request.Headers.ToDictionary(h => h.Key, h => h.Value)));
            return Results.Bytes(Png(), "image/png");
        });
        app.MapGet("/n/{i}", () => Results.Bytes(Png(1500), "image/png"));
        app.MapGet("/redirect-ok", () => Results.Redirect("/ok.png"));
        app.MapGet("/redirect-metadata", () => Results.Redirect("http://169.254.169.254/latest/meta-data/"));
        app.MapGet("/redirect-other-port", () => Results.Redirect("http://127.0.0.1:1/ok.png"));
        app.MapGet("/redirect-userinfo", () => Results.Redirect($"http://user:pass@127.0.0.1:{port}/ok.png"));
        app.MapGet("/redirect-file", () => Results.Redirect("file:///etc/passwd"));
        app.MapGet("/loop", () => Results.Redirect("/loop"));
        app.MapGet("/big-declared.png", (HttpContext c) =>
        {
            c.Response.ContentType = "image/png";
            c.Response.ContentLength = 5_000;
            return c.Response.Body.WriteAsync(Png(5_000), c.RequestAborted).AsTask();
        });
        app.MapGet("/big-streamed.png", async (HttpContext c) =>
        {
            c.Response.ContentType = "image/png"; // no length: it is only known by counting
            await c.Response.Body.WriteAsync(Png(5_000), c.RequestAborted);
        });
        app.MapGet("/slow.png", async (HttpContext c) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(5), c.RequestAborted);
            return Results.Bytes(Png(), "image/png");
        });
        app.MapGet("/page.html", () => Results.Text("<html><script>alert(1)</script></html>", "text/html"));
        app.MapGet("/liar.png", () => Results.Text("<html><script>alert(1)</script></html>", "image/png"));
        app.MapGet("/vector.svg", () => Results.Text("<svg xmlns='http://www.w3.org/2000/svg'><script>alert(1)</script></svg>", "image/svg+xml"));
        app.MapGet("/gone.png", () => Results.NotFound());
        await app.StartAsync(Token);
        port = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First()).Port;
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();

    private string Url(string path) => $"http://127.0.0.1:{port}{path}";

    private ExternalImageFetcher Fetcher(Action<ExternalImagesOptions>? configure = null, IDnsResolver? dns = null, bool loopbackOnly = true)
    {
        var options = new ExternalImagesOptions { AllowedPorts = [port], RequestTimeout = TimeSpan.FromSeconds(3), TotalTimeout = TimeSpan.FromSeconds(8) };
        configure?.Invoke(options);
        return new ExternalImageFetcher(
            Options.Create(options),
            dns ?? new SystemDnsResolver(),
            NullLogger<ExternalImageFetcher>.Instance,
            loopbackOnly ? IPAddress.IsLoopback : null);
    }

    private static async Task<IReadOnlyDictionary<string, string>> FetchAsync(ExternalImageFetcher fetcher, params string[] urls)
    {
        using (fetcher)
        {
            return await fetcher.FetchAsync(urls, Token);
        }
    }

    [Fact]
    public async Task A_Picture_Is_Fetched_And_Returned_As_Data()
    {
        var result = await FetchAsync(Fetcher(), Url("/ok.png"));

        var uri = Assert.Single(result).Value;
        Assert.StartsWith("data:image/png;base64,", uri, StringComparison.Ordinal);
        Assert.Equal(Png(), Convert.FromBase64String(uri["data:image/png;base64,".Length..]));
    }

    [Fact]
    public async Task The_Request_Tells_The_Sender_Nothing_About_The_Reader()
    {
        await FetchAsync(Fetcher(), Url("/ok.png"));

        var headers = Assert.Single(seen);
        Assert.False(headers.ContainsKey("Cookie"));
        Assert.False(headers.ContainsKey("Referer"));
        Assert.False(headers.ContainsKey("Authorization"));
        Assert.Equal("identity", headers.AcceptEncoding.ToString());
        Assert.DoesNotContain("Vinette", headers.UserAgent.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_Redirect_Is_Followed_When_Its_Target_Is_Fine()
    {
        var result = await FetchAsync(Fetcher(), Url("/redirect-ok"));

        Assert.Single(result);
    }

    [Theory]
    [InlineData("/redirect-metadata")] // an address that is not public
    [InlineData("/redirect-other-port")] // a port that is not allowed
    [InlineData("/redirect-userinfo")] // credentials in the address
    [InlineData("/redirect-file")] // not http
    [InlineData("/loop")] // never ends
    public async Task A_Redirect_To_Somewhere_That_Is_Not_Fine_Gets_Nothing(string path)
    {
        var result = await FetchAsync(Fetcher(), Url(path));

        Assert.Empty(result);
    }

    [Fact]
    public async Task By_Default_The_Machine_Itself_Is_Out_Of_Reach()
    {
        var before = Volatile.Read(ref hits);

        var result = await FetchAsync(Fetcher(loopbackOnly: false), Url("/ok.png"));

        Assert.Empty(result);
        Assert.Equal(before, Volatile.Read(ref hits)); // not even a request was made
    }

    [Theory]
    [InlineData("http://[::1]:{0}/ok.png")]
    [InlineData("http://[::ffff:127.0.0.1]:{0}/ok.png")]
    [InlineData("http://2130706433:{0}/ok.png")]
    [InlineData("http://0x7f.0.0.1:{0}/ok.png")]
    [InlineData("http://127.1:{0}/ok.png")]
    [InlineData("http://localhost:{0}/ok.png")]
    public async Task However_The_Machine_Is_Written_It_Is_Out_Of_Reach(string template)
    {
        var result = await FetchAsync(Fetcher(loopbackOnly: false), string.Format(System.Globalization.CultureInfo.InvariantCulture, template, port));

        Assert.Empty(result);
    }

    private sealed class FakeDns(params string[] answers) : IDnsResolver
    {
        public int Calls;

        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return Task.FromResult(answers.Select(IPAddress.Parse).ToArray());
        }
    }

    [Fact]
    public async Task A_Name_That_Resolves_To_A_Private_Address_Is_Refused()
    {
        var dns = new FakeDns("10.0.0.5");

        var result = await FetchAsync(Fetcher(loopbackOnly: false, dns: dns), $"http://pictures.example:{port}/ok.png");

        Assert.Empty(result);
        Assert.Equal(1, dns.Calls);
    }

    [Fact]
    public async Task A_Name_With_A_Private_Address_Among_Its_Answers_Is_Connected_Only_To_The_Allowed_One()
    {
        // The name answers with a private address and a public one; only the public one may ever be used. Here the
        // "public" one is this machine (allowed for the test), the private one is not even tried: nothing listens there.
        var dns = new FakeDns("10.0.0.5", "127.0.0.1");

        var result = await FetchAsync(Fetcher(dns: dns), $"http://pictures.example:{port}/ok.png");

        Assert.Single(result);
        Assert.Equal(1, dns.Calls);
    }

    [Fact]
    public async Task The_Name_Is_Looked_Up_Once_Per_Connection_And_The_Answer_Is_The_One_That_Was_Checked()
    {
        var dns = new FakeDns("127.0.0.1");

        await FetchAsync(Fetcher(dns: dns), $"http://pictures.example:{port}/ok.png");

        Assert.Equal(1, dns.Calls); // no second lookup for the connection to use a different answer
    }

    [Theory]
    [InlineData("/big-declared.png")] // too big by its own word
    [InlineData("/big-streamed.png")] // says nothing, and sends too much
    [InlineData("/slow.png")]
    [InlineData("/page.html")]
    [InlineData("/liar.png")] // calls itself a picture, is a page
    [InlineData("/vector.svg")] // never embedded
    [InlineData("/gone.png")]
    public async Task What_Is_Not_A_Small_Quick_Raster_Picture_Gets_Nothing(string path)
    {
        var result = await FetchAsync(Fetcher(o => { o.MaxImageBytes = 2_000; o.RequestTimeout = TimeSpan.FromMilliseconds(600); }), Url(path));

        Assert.Empty(result);
    }

    [Fact]
    public async Task One_Slow_Picture_Does_Not_Hold_Back_The_Others_Beyond_Its_Own_Time()
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var result = await FetchAsync(Fetcher(o => o.RequestTimeout = TimeSpan.FromMilliseconds(600)), Url("/slow.png"), Url("/ok.png"));

        Assert.Single(result);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(4), $"Took {watch.Elapsed}");
    }

    [Fact]
    public async Task A_Port_That_Is_Not_Allowed_Is_Refused()
    {
        var result = await FetchAsync(Fetcher(o => o.AllowedPorts = [80, 443]), Url("/ok.png"));

        Assert.Empty(result);
    }

    [Theory]
    [InlineData("ftp://127.0.0.1/ok.png")]
    [InlineData("file:///etc/passwd")]
    [InlineData("gopher://127.0.0.1/")]
    [InlineData("not an address")]
    public async Task Only_Web_Addresses_Are_Fetched(string url)
    {
        var result = await FetchAsync(Fetcher(), url);

        Assert.Empty(result);
    }

    [Fact]
    public async Task An_Address_With_A_Password_In_It_Is_Refused()
    {
        var result = await FetchAsync(Fetcher(), $"http://user:pass@127.0.0.1:{port}/ok.png");

        Assert.Empty(result);
    }

    [Fact]
    public async Task What_Does_Not_Fit_The_Budget_Of_The_Message_Is_Left_Out_Whole()
    {
        var urls = Enumerable.Range(0, 5).Select(i => Url($"/n/{i}")).ToArray();

        var result = await FetchAsync(Fetcher(o => { o.MaxImageBytes = 2_000; o.MaxTotalBytes = 3_500; }), urls);

        Assert.Equal(2, result.Count); // 2 x 1500 fit in 3500, a third does not
    }

    [Fact]
    public async Task Only_The_Configured_Number_Of_Pictures_Is_Fetched()
    {
        var urls = Enumerable.Range(0, 8).Select(i => Url($"/n/{i}")).ToArray();

        var result = await FetchAsync(Fetcher(o => o.MaxImages = 3), urls);

        Assert.Equal(3, result.Count);
    }

    [Fact]
    public async Task The_Same_Address_Is_Fetched_Once()
    {
        var before = Volatile.Read(ref hits);

        var result = await FetchAsync(Fetcher(), Url("/ok.png"), Url("/ok.png"), Url("/ok.png"));

        Assert.Single(result);
        Assert.Equal(before + 1, Volatile.Read(ref hits));
    }

    [Fact]
    public async Task Nothing_Is_Fetched_When_The_Feature_Is_Off()
    {
        var before = Volatile.Read(ref hits);

        var result = await FetchAsync(Fetcher(o => o.Enabled = false), Url("/ok.png"));

        Assert.Empty(result);
        Assert.Equal(before, Volatile.Read(ref hits));
    }

    [Fact]
    public async Task Cancelling_Stops_The_Work()
    {
        using var fetcher = Fetcher(o => o.RequestTimeout = TimeSpan.FromSeconds(30));
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetcher.FetchAsync([Url("/slow.png")], cancel.Token));
    }
}
