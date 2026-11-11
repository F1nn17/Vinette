using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Vinette.Mail.Tests;

/// <summary>
/// A tiny IMAP server for tests that need a server behaving in a way real ones rarely do (no extension, a refusal): it
/// greets, accepts any login and answers every command the same way unless a reply is given for it. Not a mail server.
/// </summary>
internal sealed class ScriptedImapServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly string capabilities;
    private readonly Dictionary<string, string[]> replies;
    private readonly CancellationTokenSource stopping = new();
    private readonly Task acceptLoop;

    /// <param name="capabilities">The capability list after "IMAP4rev1", e.g. "QUOTA".</param>
    /// <param name="replies">Commands (upper case, e.g. "GETQUOTAROOT") with the lines to send; "{tag}" stands for the command's tag.</param>
    public ScriptedImapServer(string capabilities, Dictionary<string, string[]>? replies = null)
    {
        this.capabilities = capabilities;
        this.replies = replies ?? [];
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    /// <summary>The commands received so far (upper case, without tag and arguments).</summary>
    public List<string> Commands { get; } = [];

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        try
        {
            await acceptLoop;
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopping.
        }

        stopping.Dispose();
    }

    private static async Task SendAsync(NetworkStream stream, params string[] lines)
    {
        var bytes = Encoding.ASCII.GetBytes(string.Concat(lines.Select(l => l + "\r\n")));
        await stream.WriteAsync(bytes);
    }

    private async Task AcceptAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stopping.Token);
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            await SendAsync(stream, $"* OK [CAPABILITY IMAP4rev1 {capabilities}] ready");
            while (await reader.ReadLineAsync(stopping.Token) is { } line)
            {
                var parts = line.Split(' ', 3);
                if (parts.Length < 2)
                {
                    continue;
                }

                var tag = parts[0];
                var command = parts[1].ToUpperInvariant();
                lock (Commands)
                {
                    Commands.Add(command);
                }

                if (replies.TryGetValue(command, out var custom))
                {
                    await SendAsync(stream, [.. custom.Select(l => l.Replace("{tag}", tag, StringComparison.Ordinal))]);
                }
                else if (command == "CAPABILITY")
                {
                    await SendAsync(stream, $"* CAPABILITY IMAP4rev1 {capabilities}", $"{tag} OK done");
                }
                else if (command == "LOGIN")
                {
                    await SendAsync(stream, $"{tag} OK [CAPABILITY IMAP4rev1 {capabilities}] logged in");
                }
                else if (command == "LOGOUT")
                {
                    await SendAsync(stream, "* BYE bye", $"{tag} OK done");
                    return;
                }
                else
                {
                    await SendAsync(stream, $"{tag} OK done");
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException)
        {
            // The client went away.
        }
    }
}
