using System.Buffers;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ScreenCastPresenter.Core;

/// <summary>
/// How a command-line launch talks to the copy that's already running (FR-43): a named pipe for
/// this Windows session that only the same user can open (<see cref="PipeOptions.CurrentUserOnly"/>,
/// which also checks the server belongs to that user). The launch sends its arguments as one line
/// of JSON; the running copy parses them itself, does the work and answers "ok" or "error" with a
/// sentence. Nothing here touches the network.
/// </summary>
internal static class CommandChannel
{
    public const int MaxLineBytes = 16 * 1024;

    public static string PipeName => $"ScreenCastPresenter.Commands.{Process.GetCurrentProcess().SessionId}";

    public static string EncodeArgs(IReadOnlyList<string> args)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartArray();
            foreach (string a in args) json.WriteStringValue(a);
            json.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The arguments, or null if the line isn't a JSON array of strings.</summary>
    public static string[]? DecodeArgs(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;
            var args = new List<string>();
            foreach (JsonElement e in doc.RootElement.EnumerateArray())
            {
                if (e.ValueKind != JsonValueKind.String) return null;
                args.Add(e.GetString()!);
            }
            return [.. args];
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string EncodeReply(CliReply reply) => (reply.Ok ? "ok\t" : "error\t") + CommandLine.Flatten(reply.Message);

    public static CliReply DecodeReply(string? line)
    {
        if (line is null) return new CliReply(false, "ScreenCast Presenter didn't answer.");
        int tab = line.IndexOf('\t', StringComparison.Ordinal);
        string head = tab < 0 ? line : line[..tab], text = tab < 0 ? "" : line[(tab + 1)..];
        return new CliReply(head == "ok", text);
    }

    /// <summary>
    /// Sends <paramref name="args"/> to the running copy and waits for its answer. Null when no
    /// copy is listening (it isn't running, or is still starting and never got there in time).
    /// </summary>
    /// <param name="pipeName">Tests use their own, so they never reach a real running copy.</param>
    public static async Task<CliReply?> SendAsync(IReadOnlyList<string> args, TimeSpan connectTimeout, TimeSpan replyTimeout, string? pipeName = null)
    {
        using var pipe = new NamedPipeClientStream(".", pipeName ?? PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        try
        {
            using var connect = new CancellationTokenSource(connectTimeout);
            await pipe.ConnectAsync(connect.Token);
        }
        catch (Exception ex) when (ex is OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
        try
        {
            using var reply = new CancellationTokenSource(replyTimeout);
            await WriteLineAsync(pipe, EncodeArgs(args), reply.Token);
            return DecodeReply(await ReadLineAsync(pipe, reply.Token));
        }
        catch (OperationCanceledException)
        {
            return new CliReply(false, "ScreenCast Presenter took too long to answer. It may still be doing it.");
        }
        catch (IOException)
        {
            return new CliReply(false, "ScreenCast Presenter closed the connection before answering.");
        }
    }

    public static async Task WriteLineAsync(Stream stream, string line, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    /// <summary>One line, at most <see cref="MaxLineBytes"/>; null if the other end closed first or sent too much.</summary>
    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken token)
    {
        var bytes = new List<byte>(256);
        byte[] one = new byte[1024];
        while (bytes.Count <= MaxLineBytes)
        {
            int read = await stream.ReadAsync(one, token);
            if (read == 0) return null;
            for (int i = 0; i < read; i++)
            {
                if (one[i] == (byte)'\n') return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
                bytes.Add(one[i]);
            }
        }
        return null;
    }
}

/// <summary>
/// The running copy's end of <see cref="CommandChannel"/>: one connection at a time, each answered
/// on the UI thread by <c>handle</c>. A client that connects and says nothing is dropped after a
/// few seconds, so it can't block the next one.
/// </summary>
internal sealed class CommandServer(Func<string[], Task<CliReply>> handle, string? pipeName = null) : IDisposable
{
    private readonly CancellationTokenSource _stop = new();

    /// <summary>Call on the UI thread: the handler then runs there too.</summary>
    public async Task RunAsync()
    {
        CancellationToken token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName ?? CommandChannel.PipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(token);
                string? line;
                using (var quiet = CancellationTokenSource.CreateLinkedTokenSource(token))
                {
                    quiet.CancelAfter(TimeSpan.FromSeconds(5));
                    line = await CommandChannel.ReadLineAsync(pipe, quiet.Token);
                }
                string[]? args = line is null ? null : CommandChannel.DecodeArgs(line);
                CliReply reply = args is null ? new CliReply(false, "That command couldn't be read.") : await handle(args);
                using var send = CancellationTokenSource.CreateLinkedTokenSource(token);
                send.CancelAfter(TimeSpan.FromSeconds(5));
                await CommandChannel.WriteLineAsync(pipe, CommandChannel.EncodeReply(reply), send.Token);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // A client that went away mid-answer, or the pipe name is briefly busy: carry on.
                Debug.WriteLine(ex);
                try
                {
                    await Task.Delay(200, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>Stops listening. The token source isn't disposed: the loop may still be reading it as it ends.</summary>
    public void Dispose() => _stop.Cancel();
}
