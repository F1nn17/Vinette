using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Options;

namespace Vinette.Api.Tests;

internal static class StartupFailure
{
    private const int Attempts = 5;

    /// <summary>
    /// Asserts that the application refuses to start with the given settings because an option is invalid.
    /// When the host fails to start, WebApplicationFactory sometimes reads the already disposed service provider first and
    /// throws <see cref="ObjectDisposedException"/> instead of the real error (a race inside the test host), so that outcome
    /// is retried with a fresh factory; any other outcome is final.
    /// </summary>
    public static void AssertOptionsInvalid(Action<IWebHostBuilder> configure)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(configure);
            try
            {
                factory.CreateClient();
            }
            catch (OptionsValidationException)
            {
                return;
            }
            catch (AggregateException exception) when (exception.Flatten().InnerExceptions.All(e => e is OptionsValidationException))
            {
                // Since .NET 11 the host reports a failed start as an AggregateException around the option errors.
                return;
            }
            catch (ObjectDisposedException) when (attempt < Attempts)
            {
                continue;
            }

            Assert.Fail("The application started although an option is invalid.");
        }
    }
}
