using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vinette.Core.Mail;

/// <summary>
/// Identifies a message: the folder, the folder's <c>UIDVALIDITY</c> and the message's UID. Never a sequence number,
/// which changes when another message is deleted. When the folder's <c>UIDVALIDITY</c> changes (the folder was recreated
/// or restored) every UID of it means something else, so an id with the old value must be refused, see
/// <see cref="EnsureCurrent"/>.
/// </summary>
/// <remarks>
/// In the API an id is one opaque string, <c>base64url(folder).uidValidity.uid</c>, for example
/// <c>SU5CT1g.1731000000.42</c>. The form is canonical (one text per id), so ids can be compared and cached as text.
/// </remarks>
[JsonConverter(typeof(MessageIdJsonConverter))]
public readonly record struct MessageId : IParsable<MessageId>
{
    public MessageId(string folder, uint uidValidity, uint uid)
    {
        ArgumentException.ThrowIfNullOrEmpty(folder);
        ArgumentOutOfRangeException.ThrowIfZero(uidValidity);
        ArgumentOutOfRangeException.ThrowIfZero(uid);
        Folder = folder;
        UidValidity = uidValidity;
        Uid = uid;
    }

    /// <summary>The full IMAP name of the folder, with its hierarchy separators, as the server reports it (not modified UTF-7).</summary>
    public string Folder { get; }

    /// <summary>The folder's <c>UIDVALIDITY</c>; never zero.</summary>
    public uint UidValidity { get; }

    /// <summary>The message's UID in the folder; never zero.</summary>
    public uint Uid { get; }

    /// <summary>Reads an id written by <see cref="ToString"/>. Anything else, including a non-canonical spelling, is refused.</summary>
    public static bool TryParse([NotNullWhen(true)] string? text, out MessageId id)
    {
        id = default;
        if (string.IsNullOrEmpty(text) || text.Length > MaxLength)
        {
            return false;
        }

        var parts = text.Split('.');
        if (parts.Length != 3 || !TryDecodeFolder(parts[0], out var folder)
            || !TryParseNumber(parts[1], out var uidValidity) || !TryParseNumber(parts[2], out var uid))
        {
            return false;
        }

        id = new MessageId(folder, uidValidity, uid);
        return true;
    }

    /// <inheritdoc />
    public static bool TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out MessageId result) => TryParse(s, out result);

    /// <inheritdoc />
    public static MessageId Parse(string s, IFormatProvider? provider) =>
        TryParse(s, out var id) ? id : throw new FormatException("Not a message id.");

    /// <summary>
    /// Refuses the id when the folder's <c>UIDVALIDITY</c> is no longer the one it was issued with.
    /// </summary>
    /// <exception cref="UidValidityChangedException">The folder was recreated: the id points at another message or none.</exception>
    public void EnsureCurrent(uint currentUidValidity)
    {
        if (currentUidValidity != UidValidity)
        {
            throw new UidValidityChangedException(Folder, UidValidity, currentUidValidity);
        }
    }

    /// <summary>The canonical text of the id.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Convert.ToBase64String(Encoding.UTF8.GetBytes(Folder)).TrimEnd('=').Replace('+', '-').Replace('/', '_')}.{UidValidity}.{Uid}");

    // A folder name is at most a few hundred bytes in practice; the limit keeps a crafted id from costing anything.
    private const int MaxLength = 1100;

    private static bool TryParseNumber(string text, out uint value)
    {
        // Digits only, no sign, no leading zero: "01" and "+1" would be a second spelling of the same id.
        value = 0;
        return text.Length is > 0 and <= 10
            && text.All(char.IsAsciiDigit)
            && (text.Length == 1 || text[0] != '0')
            && uint.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value != 0;
    }

    private static bool TryDecodeFolder(string text, [NotNullWhen(true)] out string? folder)
    {
        folder = null;
        if (text.Length == 0 || text.Length % 4 == 1 || !text.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            return false;
        }

        try
        {
            var padded = text.Replace('-', '+').Replace('_', '/').PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
            folder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(Convert.FromBase64String(padded));
        }
        catch (Exception exception) when (exception is FormatException or ArgumentException)
        {
            return false;
        }

        // Rejects spellings whose unused trailing bits are not zero, so every id has exactly one text.
        return folder.Length > 0 && new MessageId(folder, 1, 1).ToString().StartsWith(text + ".", StringComparison.Ordinal);
    }
}

/// <summary>Writes a <see cref="MessageId"/> as its text and reads it back; a malformed text is a JSON error, not a default id.</summary>
public sealed class MessageIdJsonConverter : JsonConverter<MessageId>
{
    public override MessageId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && MessageId.TryParse(reader.GetString(), out var id)
            ? id
            : throw new JsonException("Not a message id.");

    public override void Write(Utf8JsonWriter writer, MessageId value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString());
}

/// <summary>
/// The <c>UIDVALIDITY</c> of a folder changed since a message id was issued (the folder was deleted and created again, or
/// restored from a backup): the UIDs of the folder now mean other messages. Whatever the client cached about the folder
/// is stale and must be dropped.
/// </summary>
public sealed class UidValidityChangedException : Exception
{
    public UidValidityChangedException()
    {
    }

    public UidValidityChangedException(string message)
        : base(message)
    {
    }

    public UidValidityChangedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public UidValidityChangedException(string folder, uint requested, uint current)
        : base($"The UIDVALIDITY of a folder changed from {requested} to {current}.")
    {
        Folder = folder;
        Requested = requested;
        Current = current;
    }

    /// <summary>The folder concerned (never written to the logs of the answer: names are the user's data).</summary>
    public string? Folder { get; }

    public uint Requested { get; }

    public uint Current { get; }
}
