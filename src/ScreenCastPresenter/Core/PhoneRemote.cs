using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace ScreenCastPresenter.Core;

/// <summary>
/// The phone remote (FR-90), the parts that decide what is allowed, kept pure so every rule is
/// tested. Off unless switched on. The phone opens a page served on the local network by a helper
/// process; the pairing key is only ever in the address's #fragment, which browsers never send,
/// so it never crosses the network. Every command is signed with it (HMAC-SHA256) together with
/// the helper's session (new each time it starts) and a counter that must keep rising, so a
/// command overheard on the Wi-Fi can't be forged or replayed. Only the commands listed here can
/// be sent, and only from a private (home or office) network address.
/// </summary>
internal static class RemoteAuth
{
    public const int KeyBytes = 32;
    public const int SessionBytes = 16;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeyBytes);

    public static string NewSession() => Base64Url(RandomNumberGenerator.GetBytes(SessionBytes));

    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public static byte[]? FromBase64Url(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 200) return null;
        string padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Like <see cref="FromBase64Url"/>, for longer data (encrypted notes, FR-111).</summary>
    public static byte[]? FromBase64UrlLong(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 16_000) return null;
        string padded = text.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        try
        {
            return Convert.FromBase64String(padded);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>What is signed: the session, the counter and the command, one per line.</summary>
    public static byte[] Message(string session, long counter, string command) =>
        Encoding.UTF8.GetBytes(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{session}\n{counter}\n{command}"));

    public static string Sign(byte[] key, string session, long counter, string command) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key, Message(session, counter, command)));

    /// <summary>True when <paramref name="mac"/> is the right signature (compared in constant time).</summary>
    public static bool Verify(byte[] key, string session, long counter, string command, string? mac)
    {
        if (mac is not { Length: 64 }) return false;
        byte[] expected = HMACSHA256.HashData(key, Message(session, counter, command));
        byte[] given;
        try
        {
            given = Convert.FromHexString(mac);
        }
        catch (FormatException)
        {
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(expected, given);
    }

    /// <summary>The key kept in the settings file, encrypted for this Windows user (DPAPI), so it can't be read from a copy of the file.</summary>
    public static string Protect(byte[] key) =>
        Convert.ToBase64String(ProtectedData.Protect(key, Entropy, DataProtectionScope.CurrentUser));

    public static byte[]? Unprotect(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        try
        {
            byte[] key = ProtectedData.Unprotect(Convert.FromBase64String(stored), Entropy, DataProtectionScope.CurrentUser);
            return key.Length == KeyBytes ? key : null;
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // another user's or a damaged key: pair again
        }
    }

    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScreenCastPresenter.PhoneRemote");
}

/// <summary>The commands a phone may send (FR-90), and the app action each runs.</summary>
internal static class RemoteCommands
{
    /// <summary>Asks only for what's on now (no action).</summary>
    public const string Status = "status";

    private static readonly HashSet<string> Fixed = new(StringComparer.Ordinal)
    {
        Status, "next-slide", "previous-slide", "blank", "freeze", "spotlight", "stop", "previous-source", "next-window", "go", "stop-playlist",
        "zoom-in", "caption", "live-captions", "blank-countdown",
    };

    public const string Favourite = "favourite-", Scene = "scene-", Playlist = "playlist-";

    /// <summary>
    /// Only these, favourites and scenes 1–9, and playlists by number: nothing that opens a window
    /// or changes a setting.
    /// </summary>
    public static bool IsAllowed(string? command) =>
        command is not null && (Fixed.Contains(command) || Number(command, Favourite, AppSettings.SlotCount) is not null
            || Number(command, Scene, Core.Scene.Slots) is not null || Number(command, Playlist, Core.Playlist.Max) is not null);

    /// <summary>The number in "playlist-3" (1 to <paramref name="max"/>, no leading zero), or null.</summary>
    public static int? Number(string command, string prefix, int max)
    {
        if (!command.StartsWith(prefix, StringComparison.Ordinal)) return null;
        ReadOnlySpan<char> digits = command.AsSpan(prefix.Length);
        if (digits.Length is 0 or > 2 || digits[0] == '0') return null;
        int n = 0;
        foreach (char c in digits)
        {
            if (c is < '0' or > '9') return null;
            n = n * 10 + (c - '0');
        }
        return n <= max ? n : null;
    }
}

/// <summary>Limits how often commands are taken (FR-90): a stuck button or a flood can't overwhelm the app.</summary>
internal sealed class RemoteRateLimit(int perSecond)
{
    private readonly Queue<long> _recent = new();

    /// <summary>True when one more is allowed at <paramref name="nowMs"/> (milliseconds, any clock that only goes forward).</summary>
    public bool Allow(long nowMs)
    {
        while (_recent.Count > 0 && nowMs - _recent.Peek() >= 1000) _recent.Dequeue();
        if (_recent.Count >= perSecond) return false;
        _recent.Enqueue(nowMs);
        return true;
    }
}

/// <summary>One HTTP request as the remote's small server reads it.</summary>
internal sealed record RemoteRequest(string Method, string Path, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>The remote's HTTP: just enough of HTTP/1.1 for one page and two calls (FR-90).</summary>
internal static class RemoteHttp
{
    public const int MaxHeaderBytes = 4096;
    public const int MaxBodyBytes = 1024;

    /// <summary>The request line and headers (up to the blank line); null when it isn't HTTP we take.</summary>
    public static RemoteRequest? ParseHead(string head)
    {
        string[] lines = head.Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || first[0].Length is 0 or > 8) return null;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            if (lines[i].Length == 0) break;
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) return null;
            headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
        }
        string path = first[1];
        int query = path.IndexOf('?');
        if (query >= 0) path = path[..query];
        return new RemoteRequest(first[0], path, headers, "");
    }

    /// <summary>The body's length, or -1 when it's missing, odd or too long.</summary>
    public static int BodyLength(RemoteRequest request) =>
        request.Headers.TryGetValue("Content-Length", out string? text) && int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int n) && n <= MaxBodyBytes ? n : request.Method == "POST" ? -1 : 0;

    /// <summary>"a=1&amp;b=two" into its parts (percent-decoded).</summary>
    public static Dictionary<string, string> Form(string body)
    {
        var form = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string pair in body.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            string name = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).Replace('+', ' '));
            string value = eq < 0 ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            form.TryAdd(name, value);
        }
        return form;
    }

    /// <summary>Home and office networks only: private IPv4 ranges, link-local, loopback, and IPv6 unique-local and link-local.</summary>
    public static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 169 && b[1] == 254;
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte[] b = address.GetAddressBytes();
            return (b[0] & 0xFE) == 0xFC || b[0] == 0xFE && (b[1] & 0xC0) == 0x80;
        }
        return false;
    }

    /// <summary>A whole response: status line, safe headers (no caching, no framing, no referrer), and the body.</summary>
    public static byte[] Response(int status, string contentType, string body, string? contentSecurityPolicy = null)
    {
        byte[] content = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder();
        head.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n")
            .Append("Content-Type: ").Append(contentType).Append("\r\n")
            .Append("Content-Length: ").Append(content.Length).Append("\r\n")
            .Append("Cache-Control: no-store\r\n")
            .Append("X-Content-Type-Options: nosniff\r\n")
            .Append("X-Frame-Options: DENY\r\n")
            .Append("Referrer-Policy: no-referrer\r\n")
            .Append("Connection: close\r\n");
        if (contentSecurityPolicy is not null) head.Append("Content-Security-Policy: ").Append(contentSecurityPolicy).Append("\r\n");
        head.Append("\r\n");
        return [.. Encoding.ASCII.GetBytes(head.ToString()), .. content];
    }

    public static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        413 => "Payload Too Large",
        429 => "Too Many Requests",
        _ => "Error",
    };
}

/// <summary>
/// The remote's decisions for one signed command (FR-90): wrong session, bad signature, an old
/// counter (a replay), not allowed, too many. Holds the session and the last counter.
/// </summary>
internal sealed class RemoteGate(byte[] key, string session, int perSecond)
{
    private readonly RemoteRateLimit _limit = new(perSecond);

    public string Session { get; } = session;
    public long LastCounter { get; private set; }

    public enum Verdict { Accepted, BadRequest, WrongSession, BadSignature, Replayed, NotAllowed, TooMany }

    public Verdict Check(IReadOnlyDictionary<string, string> form, long nowMs)
    {
        if (!form.TryGetValue("s", out string? session) || !form.TryGetValue("c", out string? counterText) || !form.TryGetValue("a", out string? command) || !form.TryGetValue("m", out string? mac)
            || !long.TryParse(counterText, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out long counter))
            return Verdict.BadRequest;
        if (!string.Equals(session, Session, StringComparison.Ordinal)) return Verdict.WrongSession;
        if (!RemoteAuth.Verify(key, session, counter, command, mac)) return Verdict.BadSignature;
        if (counter <= LastCounter) return Verdict.Replayed;
        if (!RemoteCommands.IsAllowed(command)) return Verdict.NotAllowed;
        if (!_limit.Allow(nowMs)) return Verdict.TooMany;
        LastCounter = counter;
        return Verdict.Accepted;
    }
}
