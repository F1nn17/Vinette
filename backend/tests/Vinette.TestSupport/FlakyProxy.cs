using System.Net;
using System.Net.Sockets;

namespace Vinette.TestSupport;

/// <summary>
/// A TCP proxy between a client and a mail server that a test can break on purpose: cut every connection (a network
/// drop), refuse new ones (the server is down), slow the traffic down or hold it completely (a stalled server).
/// Nothing is parsed: bytes are forwarded as they are.
/// </summary>
public sealed class FlakyProxy : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly string targetHost;
    private readonly int targetPort;
    private readonly CancellationTokenSource stopping = new();
    private readonly List<TcpClient> open = [];
    private readonly Task acceptLoop;
    private volatile bool refusing;
    private volatile int delayMilliseconds;
    private TaskCompletionSource hold = Completed();

    public FlakyProxy(string targetHost, int targetPort)
    {
        this.targetHost = targetHost;
        this.targetPort = targetPort;
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        acceptLoop = Task.Run(AcceptLoopAsync);
    }

    /// <summary>Where the client connects.</summary>
    public int Port { get; }

    /// <summary>Number of connections that went through the proxy so far.</summary>
    public int Accepted { get; private set; }

    /// <summary>Closes every open connection at once, like a dropped network. New connections still work.</summary>
    public void CutAll()
    {
        lock (open)
        {
            foreach (var client in open)
            {
                Abort(client);
            }

            open.Clear();
        }
    }

    /// <summary>While true, new connections are closed at once (the server is down); open ones are not touched.</summary>
    public void Refuse(bool value) => refusing = value;

    /// <summary>Waits this long before forwarding every chunk of data (a slow network).</summary>
    public void Delay(TimeSpan perChunk) => delayMilliseconds = (int)perChunk.TotalMilliseconds;

    /// <summary>Holds all traffic until <see cref="Resume"/> (a server that stopped answering).</summary>
    public void Stall()
    {
        lock (open)
        {
            if (hold.Task.IsCompleted)
            {
                hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }

    public void Resume()
    {
        lock (open)
        {
            _ = hold.TrySetResult();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await stopping.CancelAsync();
        listener.Stop();
        Resume();
        CutAll();
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

    /// <summary>Closes at once, without a polite goodbye; a client that is already disposed is left alone.</summary>
    private static void Abort(TcpClient client)
    {
        try
        {
            client.Client?.Close(0);
        }
        catch (ObjectDisposedException)
        {
            // Already closed.
        }
    }

    private static TaskCompletionSource Completed()
    {
        var source = new TaskCompletionSource();
        source.SetResult();
        return source;
    }

    private async Task AcceptLoopAsync()
    {
        while (!stopping.IsCancellationRequested)
        {
            var client = await listener.AcceptTcpClientAsync(stopping.Token);
            if (refusing)
            {
                Abort(client);
                continue;
            }

            Accepted++;
            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var server = new TcpClient();
        try
        {
            await server.ConnectAsync(targetHost, targetPort, stopping.Token);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            Abort(client); // the real server is not there: the client sees a dropped connection
            return;
        }

        lock (open)
        {
            open.Add(client);
            open.Add(server);
        }

        await Task.WhenAny(Pump(client, server), Pump(server, client));
        client.Dispose();
        server.Dispose();
    }

    private async Task Pump(TcpClient from, TcpClient to)
    {
        var buffer = new byte[8192];
        try
        {
            var input = from.GetStream();
            var output = to.GetStream();
            while (true)
            {
                var read = await input.ReadAsync(buffer, stopping.Token);
                if (read == 0)
                {
                    return;
                }

                await HoldAsync();
                if (delayMilliseconds > 0)
                {
                    await Task.Delay(delayMilliseconds, stopping.Token);
                }

                await output.WriteAsync(buffer.AsMemory(0, read), stopping.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or SocketException or InvalidOperationException)
        {
            // A cut or a stop ends the pump.
        }
    }

    private Task HoldAsync()
    {
        lock (open)
        {
            return hold.Task;
        }
    }
}
