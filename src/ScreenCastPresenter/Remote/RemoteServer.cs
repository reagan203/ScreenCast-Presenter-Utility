using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Remote;

/// <summary>
/// The phone remote's server (FR-90): one page, a session call and a command call, over plain
/// HTTP on the local network. One request per connection, small limits on everything, a few
/// connections at a time, private addresses only. Runs in the remote helper process (the app
/// itself never listens on the network); tests run it on the loopback address.
/// </summary>
internal sealed class RemoteServer : IDisposable
{
    /// <summary>The port tried first; a busy one falls back to any free port.</summary>
    public const int DefaultPort = 47820;

    public const int MaxConnections = 8;
    public const int CommandsPerSecond = 15;
    public static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly TcpListener _listener;
    private readonly RemoteGate _gate;
    private readonly object _gateLock = new();
    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);
    private readonly CancellationTokenSource _stop = new();
    private readonly Func<string, string> _command;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    /// <param name="command">Runs an accepted command and returns the status to send back (JSON), on any thread.</param>
    public RemoteServer(byte[] key, IPAddress address, int port, Func<string, string> command)
    {
        _command = command;
        _gate = new RemoteGate(key, RemoteAuth.NewSession(), CommandsPerSecond);
        _listener = Listen(address, port);
    }

    /// <summary>The port it ended up on.</summary>
    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>A phone talked to it (its address), for "Connected" on the pairing window.</summary>
    public event Action<IPAddress>? Seen;

    private static TcpListener Listen(IPAddress address, int port)
    {
        try
        {
            var listener = new TcpListener(address, port);
            listener.Start();
            return listener;
        }
        catch (SocketException) when (port != 0)
        {
            var any = new TcpListener(address, 0); // the usual port is taken: any free one (the pairing code has it)
            any.Start();
            return any;
        }
    }

    public async Task RunAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token);
                if (!await _slots.WaitAsync(0))
                {
                    client.Dispose(); // too many at once
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ServeAsync(client);
                    }
                    finally
                    {
                        client.Dispose();
                        _slots.Release();
                    }
                });
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // stopped
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        timeout.CancelAfter(ReadTimeout);
        NetworkStream stream = client.GetStream();
        try
        {
            if (client.Client.RemoteEndPoint is not IPEndPoint remote || !RemoteHttp.IsLocalNetwork(remote.Address))
            {
                await stream.WriteAsync(RemoteHttp.Response(403, "text/plain; charset=utf-8", "Only from the local network."), timeout.Token);
                return;
            }
            byte[] response = await HandleAsync(stream, remote.Address, timeout.Token);
            await stream.WriteAsync(response, timeout.Token);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
            // a phone that went away or never finished: nothing to do
        }
    }

    private async Task<byte[]> HandleAsync(NetworkStream stream, IPAddress from, CancellationToken token)
    {
        // The head, up to the blank line, within its limit.
        var buffer = new byte[RemoteHttp.MaxHeaderBytes + RemoteHttp.MaxBodyBytes];
        int read = 0, headEnd = -1;
        while (headEnd < 0)
        {
            if (read == RemoteHttp.MaxHeaderBytes) return RemoteHttp.Response(413, "text/plain; charset=utf-8", "Too large.");
            int n = await stream.ReadAsync(buffer.AsMemory(read, RemoteHttp.MaxHeaderBytes - read), token);
            if (n == 0) return RemoteHttp.Response(400, "text/plain; charset=utf-8", "Incomplete.");
            read += n;
            headEnd = IndexOfBlankLine(buffer, read);
        }
        RemoteRequest? request = RemoteHttp.ParseHead(Encoding.ASCII.GetString(buffer, 0, headEnd));
        if (request is null) return RemoteHttp.Response(400, "text/plain; charset=utf-8", "Not understood.");
        int length = RemoteHttp.BodyLength(request);
        if (length < 0) return RemoteHttp.Response(413, "text/plain; charset=utf-8", "Too large.");
        int bodyStart = headEnd + 4;
        while (read - bodyStart < length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, bodyStart + length - read), token);
            if (n == 0) return RemoteHttp.Response(400, "text/plain; charset=utf-8", "Incomplete.");
            read += n;
        }
        string body = Encoding.UTF8.GetString(buffer, bodyStart, length);
        return Route(request with { Body = body }, from);
    }

    private static int IndexOfBlankLine(byte[] buffer, int count)
    {
        for (int i = 3; i < count; i++)
            if (buffer[i - 3] == '\r' && buffer[i - 2] == '\n' && buffer[i - 1] == '\r' && buffer[i] == '\n') return i - 3;
        return -1;
    }

    /// <summary>The three things it serves; everything else is refused.</summary>
    internal byte[] Route(RemoteRequest request, IPAddress from)
    {
        switch (request.Path)
        {
            case "/" when request.Method == "GET":
                return RemoteHttp.Response(200, "text/html; charset=utf-8", RemotePage.Html, RemotePage.ContentSecurityPolicy);
            case "/session" when request.Method == "GET":
                return Json(200, $"{{\"s\":\"{_gate.Session}\"}}");
            case "/cmd" when request.Method == "POST":
                Dictionary<string, string> form = RemoteHttp.Form(request.Body);
                RemoteGate.Verdict verdict;
                long last;
                lock (_gateLock)
                {
                    verdict = _gate.Check(form, _clock.ElapsedMilliseconds);
                    last = _gate.LastCounter;
                }
                switch (verdict)
                {
                    case RemoteGate.Verdict.Accepted:
                        Seen?.Invoke(from);
                        return Json(200, _command(form["a"]));
                    case RemoteGate.Verdict.WrongSession: return Json(401, "{\"session\":\"stale\"}");
                    case RemoteGate.Verdict.BadSignature: return Json(401, "{\"error\":\"not paired\"}");
                    case RemoteGate.Verdict.Replayed: return Json(409, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{{\"last\":{last}}}"));
                    case RemoteGate.Verdict.TooMany: return Json(429, "{\"error\":\"too many\"}");
                    case RemoteGate.Verdict.NotAllowed: return Json(403, "{\"error\":\"not allowed\"}");
                    default: return Json(400, "{\"error\":\"bad request\"}");
                }
            case "/" or "/session" or "/cmd":
                return RemoteHttp.Response(405, "text/plain; charset=utf-8", "Not allowed.");
            default:
                return RemoteHttp.Response(404, "text/plain; charset=utf-8", "Not found.");
        }
    }

    private static byte[] Json(int status, string json) => RemoteHttp.Response(status, "application/json; charset=utf-8", json);

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
