using System.Security.Claims;
using Vinette.Api.Errors;
using Vinette.Core.Settings;

namespace Vinette.Api.Settings;

/// <summary>What the user has chosen in the interface.</summary>
/// <param name="Density"><c>comfortable</c> (a row has the preview) or <c>compact</c> (without it).</param>
/// <param name="Language">The interface language ("ru", "en"); absent follows the browser.</param>
/// <param name="LabelColors">The color of each label by its name; a color is a key of the design's palette.</param>
/// <param name="TrustedImageSenders">The sender addresses (lower case) whose pictures from the web are shown without asking, when the sender check says "verified".</param>
public sealed record SettingsDto(string Density, string? Language, IReadOnlyDictionary<string, string> LabelColors, IReadOnlyList<string> TrustedImageSenders);

/// <summary>A change of settings: only the members that are present are changed.</summary>
/// <param name="Density"><c>comfortable</c> or <c>compact</c>.</param>
/// <param name="Language">A language tag; an empty string goes back to following the browser.</param>
/// <param name="LabelColors">Replaces all label colors.</param>
/// <param name="TrustImagesFrom">An address to add to the senders whose pictures are shown without asking.</param>
/// <param name="UntrustImagesFrom">An address to remove from them.</param>
public sealed record SettingsPatch(string? Density, string? Language, Dictionary<string, string>? LabelColors, string? TrustImagesFrom = null, string? UntrustImagesFrom = null);

public static class SettingsEndpoints
{
    public static IEndpointRouteBuilder MapSettings(this IEndpointRouteBuilder api)
    {
        api.MapGet("/settings", GetAsync)
            .WithName("GetSettings")
            .WithTags("Settings")
            .Produces<SettingsDto>()
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        api.MapPatch("/settings", PatchAsync)
            .WithName("UpdateSettings")
            .WithTags("Settings")
            .Produces<SettingsDto>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized);
        return api;
    }

    private static async Task<IResult> GetAsync(ClaimsPrincipal user, IUserSettingsStore store, CancellationToken cancellationToken) =>
        Results.Ok(ToDto(await store.GetAsync(user.Identity!.Name!, cancellationToken)));

    private static async Task<IResult> PatchAsync(SettingsPatch? patch, ClaimsPrincipal user, IUserSettingsStore store, CancellationToken cancellationToken)
    {
        if (patch is null)
        {
            return Bad("The body must be a JSON object with the settings to change.");
        }

        ListDensity? density = null;
        if (patch.Density is not null)
        {
            density = patch.Density switch
            {
                "comfortable" => ListDensity.Comfortable,
                "compact" => ListDensity.Compact,
                _ => null,
            };
            if (density is null)
            {
                return Bad("density must be comfortable or compact.");
            }
        }

        if (string.IsNullOrWhiteSpace(patch.TrustImagesFrom ?? "x") || string.IsNullOrWhiteSpace(patch.UntrustImagesFrom ?? "x"))
        {
            return Bad("An address to trust or to stop trusting must not be empty.");
        }

        try
        {
            var saved = await store.UpdateAsync(
                user.Identity!.Name!,
                current =>
                {
                    var changed = current with
                    {
                        Density = density ?? current.Density,
                        Language = patch.Language is null ? current.Language : patch.Language.Length == 0 ? null : patch.Language,
                        LabelColors = patch.LabelColors is null ? current.LabelColors : new Dictionary<string, string>(patch.LabelColors),
                    };
                    if (patch.UntrustImagesFrom is not null)
                    {
                        changed = changed.WithImageSender(patch.UntrustImagesFrom, trusted: false);
                    }

                    return patch.TrustImagesFrom is null ? changed : changed.WithImageSender(patch.TrustImagesFrom, trusted: true);
                },
                cancellationToken);
            return Results.Ok(ToDto(saved));
        }
        catch (InvalidSettingsException exception)
        {
            return Bad(exception.Message);
        }
    }

    private static SettingsDto ToDto(UserSettings settings) => new(
        settings.Density == ListDensity.Compact ? "compact" : "comfortable",
        settings.Language,
        settings.LabelColors,
        settings.TrustedImageSenders);

    private static IResult Bad(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.BadRequest });
}
