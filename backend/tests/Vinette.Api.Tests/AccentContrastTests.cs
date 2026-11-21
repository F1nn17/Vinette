using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Vinette.Api.Configuration;

namespace Vinette.Api.Tests;

public sealed class AccentContrastTests
{
    [Theory]
    [InlineData("#FFFFFF", 1.0)]
    [InlineData("#000000", 21.0)]
    [InlineData("#767676", 4.54)] // the lightest gray that is readable on white
    public void The_Contrast_With_White_Is_The_Wcag_Ratio(string color, double expected)
    {
        Assert.Equal(expected, AccentContrast.WithWhite(color), 2);
    }

    [Fact]
    public void The_Default_Accent_Is_Dark_Enough()
    {
        Assert.True(AccentContrast.WithWhite(new BrandingOptions().Accent) >= AccentContrast.Minimum);
    }

    [Theory]
    [InlineData("#8E2043")] // the default
    [InlineData("#000000")]
    [InlineData("#1F6FEB")] // the blue of the example config
    [InlineData("#0A5C36")]
    [InlineData("#767676")]
    public void A_Dark_Accent_Is_Left_As_It_Is(string color)
    {
        Assert.Equal(color, AccentContrast.Ensure(color));
    }

    [Theory]
    [InlineData("#FFFFFF")]
    [InlineData("#FFFF00")] // yellow
    [InlineData("#7FDBFF")] // light blue
    [InlineData("#AAAAAA")]
    [InlineData("#FFB6C1")] // pink
    [InlineData("#00FF00")]
    public void A_Light_Accent_Is_Darkened_Until_It_Is_Readable(string color)
    {
        var adjusted = AccentContrast.Ensure(color);

        Assert.NotEqual(color, adjusted);
        Assert.True(AccentContrast.WithWhite(adjusted) >= AccentContrast.Minimum, $"{color} -> {adjusted}");
        // only as dark as needed: one step lighter would not do
        Assert.True(AccentContrast.WithWhite(adjusted) < AccentContrast.Minimum + 1.5, $"{color} -> {adjusted}");
    }

    [Fact]
    public void Darkening_Keeps_The_Hue()
    {
        var adjusted = AccentContrast.Ensure("#FFFF00"); // yellow stays yellowish: red and green stay equal, blue stays zero

        Assert.Equal(adjusted[1..3], adjusted[3..5]);
        Assert.Equal("00", adjusted[5..]);
    }

    [Fact]
    public void A_Light_Accent_Is_Replaced_At_Start_And_The_Warning_Names_Both_Colors()
    {
        var logs = new List<string>();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Branding:Accent", "#FFFF00");
            b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(new ListLoggerProvider(logs))));
        });

        var accent = factory.Services.GetRequiredService<IOptions<BrandingOptions>>().Value.Accent;

        Assert.NotEqual("#FFFF00", accent);
        Assert.True(AccentContrast.WithWhite(accent) >= AccentContrast.Minimum);
        var warning = Assert.Single(logs, l => l.Contains("too light", StringComparison.Ordinal));
        Assert.Contains("#FFFF00", warning, StringComparison.Ordinal);
        Assert.Contains(accent, warning, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Good_Accent_Is_Not_Warned_About()
    {
        var logs = new List<string>();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("Branding:Accent", "#0A5C36");
            b.ConfigureServices(s => s.AddLogging(l => l.AddProvider(new ListLoggerProvider(logs))));
        });

        var accent = factory.Services.GetRequiredService<IOptions<BrandingOptions>>().Value.Accent;

        Assert.Equal("#0A5C36", accent);
        Assert.DoesNotContain(logs, l => l.Contains("too light", StringComparison.Ordinal));
    }

    private sealed class ListLoggerProvider(List<string> lines) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new ListLogger(lines);

        public void Dispose()
        {
        }

        private sealed class ListLogger(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add(formatter(state, exception));
                }
            }
        }
    }
}
