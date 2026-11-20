using Vinette.Api.Errors;
using Vinette.Api.Sessions;
using Vinette.Core.Mail;

namespace Vinette.Api.Mail;

/// <summary>What the reader chose to do about a request for a read receipt.</summary>
/// <param name="Send">True sends the receipt to the sender; false declines. Either way the message is marked, so that the question is not asked again.</param>
public sealed record ReceiptDecision(bool Send);

/// <summary>
/// Read receipts (VIN-106), the receiving side: a message that asks for one (<c>Disposition-Notification-To</c>, RFC 8098) is shown with the
/// question, and only the reader's answer sends anything: <c>POST /messages/{id}/receipt</c>. Nothing is ever sent without it.
/// </summary>
public static partial class ReceiptEndpoints
{
    public static IEndpointRouteBuilder MapReceipts(this IEndpointRouteBuilder api)
    {
        api.MapPost("/messages/{id}/receipt", DecideAsync)
            .WithName("AnswerReceiptRequest")
            .WithTags("Mail")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
        return api;
    }

    /// <summary>
    /// Who asked for a read receipt of this message, if the reader should be asked: the message names an address in <c>Disposition-Notification-To</c>,
    /// that address is the sender's own (a request that points somewhere else would tell a third party when the message was opened, so it is
    /// ignored), it is not a draft, and it has not been dealt with already (<c>$MDNSent</c>).
    /// </summary>
    public static MailAddressInfo? Requester(ParsedMessage parsed, MessageSource source)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(source);
        return parsed.ReceiptTo is { } to
            && parsed.From is { } from
            && !string.IsNullOrWhiteSpace(to.Address)
            && string.Equals(to.Address.Trim(), from.Address?.Trim(), StringComparison.OrdinalIgnoreCase)
            && !source.ReceiptSent
            && !source.Draft
            ? to
            : null;
    }

    /// <summary>
    /// Sends the receipt or declines it, then marks the message (<c>$MDNSent</c>). 400 if the message asks for none (or it was dealt with already);
    /// 422 and 503 as when sending a message (<c>recipient_rejected</c>, <c>send_refused</c>, <c>send_uncertain</c>): then the message is not marked and the
    /// reader can try again.
    /// </summary>
    private static async Task<IResult> DecideAsync(
        MessageId id,
        ReceiptDecision? decision,
        HttpContext http,
        SessionService sessions,
        IMailGateway gateway,
        IMessageParser parser,
        IMessageComposer composer,
        IMailSender sender,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        if (decision is null)
        {
            return Problem(StatusCodes.Status400BadRequest, "The body must say whether to send the receipt.", ErrorCodes.BadRequest);
        }

        var credentials = http.GetMailCredentials(sessions);
        var source = await gateway.ExecuteAsync(credentials, (session, token) => session.GetMessageAsync(id, token), cancellationToken);
        ParsedMessage parsed;
        using (var stream = new MemoryStream(source.Content, writable: false))
        {
            parsed = await parser.ParseAsync(stream, cancellationToken);
        }

        if (Requester(parsed, source) is not { } requester)
        {
            return Problem(StatusCodes.Status400BadRequest, "This message asks for no read receipt, or the question was answered already.", ErrorCodes.BadRequest);
        }

        if (decision.Send)
        {
            try
            {
                var receipt = composer.ComposeReceipt(new ReceiptRequest(new MailAddressInfo(string.Empty, credentials.Address), requester, parsed.MessageId, parsed.Subject, parsed.Date));
                await sender.SendAsync(credentials, receipt.Envelope, receipt.Raw, cancellationToken);
            }
            catch (ArgumentException)
            {
                return Problem(StatusCodes.Status400BadRequest, "The address that asked for the receipt is not an address.", ErrorCodes.BadRequest);
            }
            catch (MailRejectedException rejected)
            {
                return rejected.Reason == SendRejection.Recipient
                    ? Results.Problem(
                        statusCode: StatusCodes.Status422UnprocessableEntity,
                        title: "The mail server does not accept the sender of the message as a recipient.",
                        extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.RecipientRejected, ["address"] = rejected.Address ?? requester.Address })
                    : Problem(StatusCodes.Status422UnprocessableEntity, "The mail server refused the receipt.", ErrorCodes.SendRefused);
            }
            catch (MailUnavailableException unavailable) when (unavailable.MessageMayHaveBeenSent)
            {
                return Problem(StatusCodes.Status503ServiceUnavailable, "The connection broke while the receipt was being handed over; it may have been sent.", ErrorCodes.SendUncertain);
            }
        }

        // The receipt is out (or declined): marking the message must not fail the answer, but if it fails the question may come back.
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await gateway.ExecuteAsync(
                credentials,
                async (session, token) =>
                {
                    await session.MarkReceiptSentAsync(id, token);
                    return 0;
                },
                timeout.Token);
        }
        catch (Exception exception) when (exception is MailUnavailableException or MailBusyException or MailAccessRevokedException or MailMessageNotFoundException or OperationCanceledException)
        {
            NotMarked(loggers.CreateLogger("Vinette.Api.Mail.Receipt"), exception.GetType().Name);
        }

        return Results.NoContent();
    }

    private static IResult Problem(int status, string title, string code) =>
        Results.Problem(statusCode: status, title: title, extensions: new Dictionary<string, object?> { ["code"] = code });

    [LoggerMessage(Level = LogLevel.Warning, Message = "A read receipt was dealt with, but the message could not be marked ({Reason})")]
    private static partial void NotMarked(ILogger logger, string reason);
}
