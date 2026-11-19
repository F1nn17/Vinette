using System.Security.Claims;
using Vinette.Api.Errors;
using Vinette.Core.Settings;

namespace Vinette.Api.Settings;

/// <summary>A signature as the client sees it.</summary>
/// <param name="Id">The id, by which a message refers to it.</param>
/// <param name="Name">The name in the list.</param>
/// <param name="Text">The text, several lines.</param>
/// <param name="Default">The signature that goes under new messages unless another is chosen (at most one has it).</param>
public sealed record SignatureDto(string Id, string Name, string Text, bool Default);

/// <summary>A signature as the client sends it.</summary>
/// <param name="Id">The id of a signature that exists; absent (or unknown) makes a new one.</param>
/// <param name="Name">The name in the list.</param>
/// <param name="Text">The text, several lines.</param>
/// <param name="Default">Makes it the signature for new messages; if several say so, the first counts.</param>
public sealed record SignatureInput(string? Id, string? Name, string? Text, bool Default = false);

/// <summary>What the user has chosen in the interface.</summary>
/// <param name="Density"><c>comfortable</c> (a row has the preview) or <c>compact</c> (without it).</param>
/// <param name="Language">The interface language ("ru", "en"); absent follows the browser.</param>
/// <param name="LabelColors">The color of each label by its name; a color is a key of the design's palette.</param>
/// <param name="TrustedImageSenders">The sender addresses (lower case) whose pictures from the web are shown without asking, when the sender check says "verified".</param>
/// <param name="Signatures">The signatures, in the order they are listed.</param>
public sealed record SettingsDto(string Density, string? Language, IReadOnlyDictionary<string, string> LabelColors, IReadOnlyList<string> TrustedImageSenders, IReadOnlyList<SignatureDto> Signatures);

/// <summary>A change of settings: only the members that are present are changed.</summary>
/// <param name="Density"><c>comfortable</c> or <c>compact</c>.</param>
/// <param name="Language">A language tag; an empty string goes back to following the browser.</param>
/// <param name="LabelColors">Replaces all label colors.</param>
/// <param name="TrustImagesFrom">An address to add to the senders whose pictures are shown without asking.</param>
/// <param name="UntrustImagesFrom">An address to remove from them.</param>
/// <param name="Signatures">Replaces all signatures (send the whole list, the ones to keep with their ids); the one marked <c>default</c> becomes the default, none marked means no default.</param>
public sealed record SettingsPatch(
    string? Density,
    string? Language,
    Dictionary<string, string>? LabelColors,
    string? TrustImagesFrom = null,
    string? UntrustImagesFrom = null,
    List<SignatureInput>? Signatures = null);

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
                    if (patch.Signatures is not null)
                    {
                        changed = WithSignatures(changed, patch.Signatures);
                    }

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

    /// <summary>The settings with the signatures replaced: those that exist keep their ids, new ones get one, line ends are normalized.</summary>
    private static UserSettings WithSignatures(UserSettings current, List<SignatureInput> inputs)
    {
        var known = current.Signatures.Select(g => g.Id).ToHashSet(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<Signature>(inputs.Count);
        string? defaultId = null;
        foreach (var input in inputs)
        {
            var id = input.Id is { } given && known.Contains(given) && used.Add(given) ? given : NewId(used);
            list.Add(new Signature(id, (input.Name ?? string.Empty).Trim(), (input.Text ?? string.Empty).ReplaceLineEndings(((char)10).ToString()).TrimEnd()));
            if (input.Default && defaultId is null)
            {
                defaultId = id;
            }
        }

        return current with { Signatures = list, DefaultSignatureId = defaultId };
    }

    private static string NewId(HashSet<string> used)
    {
        string id;
        do
        {
            id = Guid.NewGuid().ToString("N");
        }
        while (!used.Add(id));
        return id;
    }

    private static SettingsDto ToDto(UserSettings settings) => new(
        settings.Density == ListDensity.Compact ? "compact" : "comfortable",
        settings.Language,
        settings.LabelColors,
        settings.TrustedImageSenders,
        [.. settings.Signatures.Select(g => new SignatureDto(g.Id, g.Name, g.Text, g.Id == settings.DefaultSignatureId))]);

    private static IResult Bad(string title) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: title, extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.BadRequest });
}
