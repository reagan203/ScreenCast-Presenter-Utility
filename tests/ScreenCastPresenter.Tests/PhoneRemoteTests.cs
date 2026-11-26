using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Remote;

namespace ScreenCastPresenter.Tests;

/// <summary>FR-90: the phone remote lets only a paired phone run only the listed commands, once each.</summary>
public class PhoneRemoteAuthTests
{
    private static readonly byte[] Key = Enumerable.Range(1, RemoteAuth.KeyBytes).Select(i => (byte)i).ToArray();

    [Fact]
    public void A_signature_holds_only_for_that_key_session_counter_and_command()
    {
        string mac = RemoteAuth.Sign(Key, "S1", 1000, "blank");
        Assert.True(RemoteAuth.Verify(Key, "S1", 1000, "blank", mac));
        Assert.False(RemoteAuth.Verify(RemoteAuth.NewKey(), "S1", 1000, "blank", mac));
        Assert.False(RemoteAuth.Verify(Key, "S2", 1000, "blank", mac));
        Assert.False(RemoteAuth.Verify(Key, "S1", 1001, "blank", mac));
        Assert.False(RemoteAuth.Verify(Key, "S1", 1000, "stop", mac));
        Assert.False(RemoteAuth.Verify(Key, "S1", 1000, "blank", mac[..63]));
        Assert.False(RemoteAuth.Verify(Key, "S1", 1000, "blank", "zz" + mac[2..]));
        Assert.False(RemoteAuth.Verify(Key, "S1", 1000, "blank", null));
        Assert.Equal(64, mac.Length);
    }

    [Fact]
    public void Keys_and_sessions_are_random_and_survive_the_address_bar()
    {
        Assert.NotEqual(RemoteAuth.NewKey(), RemoteAuth.NewKey());
        Assert.NotEqual(RemoteAuth.NewSession(), RemoteAuth.NewSession());
        byte[] key = RemoteAuth.NewKey();
        string text = RemoteAuth.Base64Url(key);
        Assert.DoesNotContain('+', text);
        Assert.DoesNotContain('/', text);
        Assert.DoesNotContain('=', text);
        Assert.Equal(key, RemoteAuth.FromBase64Url(text));
        Assert.Null(RemoteAuth.FromBase64Url("not base64 at all!"));
        Assert.Null(RemoteAuth.FromBase64Url(null));
        Assert.Null(RemoteAuth.FromBase64Url(new string('A', 500)));
    }

    [Fact]
    public void The_kept_key_is_encrypted_for_this_windows_user()
    {
        byte[] key = RemoteAuth.NewKey();
        string stored = RemoteAuth.Protect(key);
        Assert.DoesNotContain(RemoteAuth.Base64Url(key), stored);
        Assert.Equal(key, RemoteAuth.Unprotect(stored));
        Assert.Null(RemoteAuth.Unprotect("garbage"));
        Assert.Null(RemoteAuth.Unprotect(Convert.ToBase64String(new byte[40])));
        Assert.Null(RemoteAuth.Unprotect(null));
    }

    [Theory]
    [InlineData("status", true)]
    [InlineData("next-slide", true)]
    [InlineData("previous-slide", true)]
    [InlineData("blank", true)]
    [InlineData("freeze", true)]
    [InlineData("spotlight", true)]
    [InlineData("stop", true)]
    [InlineData("go", true)]
    [InlineData("favourite-1", true)]
    [InlineData("favourite-9", true)]
    [InlineData("scene-5", true)]
    [InlineData("favourite-0", false)]
    [InlineData("favourite-10", false)]
    [InlineData("scene-", false)]
    [InlineData("save-favourite-1", false)]
    [InlineData("quit", false)]
    [InlineData("settings", false)]
    [InlineData("picker", false)]
    [InlineData("phone-remote", false)]
    [InlineData("record", false)]
    [InlineData("screenshot", false)]
    [InlineData("hide-part", false)]
    [InlineData("show-hidden", false)]
    [InlineData("auto-hide", false)]
    [InlineData("BLANK", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_listed_commands_can_be_sent(string? command, bool allowed) => Assert.Equal(allowed, RemoteCommands.IsAllowed(command));

    [Fact]
    public void A_flood_is_cut_off_and_allowed_again_a_second_later()
    {
        var limit = new RemoteRateLimit(3);
        Assert.True(limit.Allow(0));
        Assert.True(limit.Allow(10));
        Assert.True(limit.Allow(20));
        Assert.False(limit.Allow(30));
        Assert.True(limit.Allow(1000));
        Assert.True(limit.Allow(1010));
        Assert.False(limit.Allow(1015));
    }

    private static Dictionary<string, string> Signed(string session, long counter, string command, byte[]? key = null) => new()
    {
        ["s"] = session,
        ["c"] = counter.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["a"] = command,
        ["m"] = RemoteAuth.Sign(key ?? Key, session, counter, command),
    };

    [Fact]
    public void The_gate_takes_each_signed_command_once_in_order()
    {
        var gate = new RemoteGate(Key, "S", perSecond: 100);
        Assert.Equal(RemoteGate.Verdict.Accepted, gate.Check(Signed("S", 5, "blank"), 0));
        Assert.Equal(RemoteGate.Verdict.Replayed, gate.Check(Signed("S", 5, "blank"), 1));   // the same request again
        Assert.Equal(RemoteGate.Verdict.Replayed, gate.Check(Signed("S", 4, "blank"), 2));   // an older one
        Assert.Equal(RemoteGate.Verdict.Accepted, gate.Check(Signed("S", 6, "freeze"), 3));
        Assert.Equal(6, gate.LastCounter);
        Assert.Equal(RemoteGate.Verdict.WrongSession, gate.Check(Signed("OLD", 7, "blank"), 4));
        Assert.Equal(RemoteGate.Verdict.BadSignature, gate.Check(Signed("S", 7, "blank", RemoteAuth.NewKey()), 5));
        Assert.Equal(RemoteGate.Verdict.NotAllowed, gate.Check(Signed("S", 8, "quit"), 6)); // signed, but not on the list
        var tampered = Signed("S", 9, "blank");
        tampered["a"] = "stop";
        Assert.Equal(RemoteGate.Verdict.BadSignature, gate.Check(tampered, 7));
        Assert.Equal(RemoteGate.Verdict.BadRequest, gate.Check(new Dictionary<string, string> { ["s"] = "S" }, 8));
        Assert.Equal(RemoteGate.Verdict.BadRequest, gate.Check(new Dictionary<string, string> { ["s"] = "S", ["c"] = "-1", ["a"] = "blank", ["m"] = "x" }, 9));
        Assert.Equal(6, gate.LastCounter); // nothing rejected moved it on
    }

    [Fact]
    public void The_gate_limits_how_fast_commands_come()
    {
        var gate = new RemoteGate(Key, "S", perSecond: 5);
        for (int i = 1; i <= 5; i++) Assert.Equal(RemoteGate.Verdict.Accepted, gate.Check(Signed("S", i, "status"), i));
        Assert.Equal(RemoteGate.Verdict.TooMany, gate.Check(Signed("S", 6, "status"), 10));
        Assert.Equal(RemoteGate.Verdict.Accepted, gate.Check(Signed("S", 7, "status"), 1500));
    }
}

/// <summary>FR-90: the remote's HTTP and the page.</summary>
public class PhoneRemoteHttpTests
{
    [Fact]
    public void Requests_are_read_with_their_limits()
    {
        RemoteRequest r = RemoteHttp.ParseHead("POST /cmd?x=1 HTTP/1.1\r\nHost: 192.168.1.5:47820\r\nContent-Length: 42\r\n")!;
        Assert.Equal("POST", r.Method);
        Assert.Equal("/cmd", r.Path);
        Assert.Equal(42, RemoteHttp.BodyLength(r));
        Assert.Equal(-1, RemoteHttp.BodyLength(RemoteHttp.ParseHead("POST /cmd HTTP/1.1\r\nContent-Length: 99999\r\n")!));
        Assert.Equal(-1, RemoteHttp.BodyLength(RemoteHttp.ParseHead("POST /cmd HTTP/1.1\r\n")!));
        Assert.Equal(-1, RemoteHttp.BodyLength(RemoteHttp.ParseHead("POST /cmd HTTP/1.1\r\nContent-Length: -5\r\n")!));
        Assert.Equal(0, RemoteHttp.BodyLength(RemoteHttp.ParseHead("GET / HTTP/1.1\r\n")!));
        Assert.Null(RemoteHttp.ParseHead("GET /\r\n"));
        Assert.Null(RemoteHttp.ParseHead("GET / SMTP/1.0\r\n"));
        Assert.Null(RemoteHttp.ParseHead("GET / HTTP/1.1\r\nno colon here\r\n"));
        Assert.Null(RemoteHttp.ParseHead("VERYLONGMETHOD / HTTP/1.1\r\n"));
    }

    [Fact]
    public void Forms_are_decoded()
    {
        Dictionary<string, string> form = RemoteHttp.Form("s=ab-_c&c=123&a=favourite-2&m=00ff&x=a%20b+c&s=second");
        Assert.Equal("ab-_c", form["s"]); // the first wins
        Assert.Equal("favourite-2", form["a"]);
        Assert.Equal("a b c", form["x"]);
        Assert.Empty(RemoteHttp.Form(""));
    }

    [Theory]
    [InlineData("192.168.1.20", true)]
    [InlineData("10.0.0.5", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("172.31.255.254", true)]
    [InlineData("169.254.10.10", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fd12:3456::1", true)]
    [InlineData("::ffff:192.168.0.9", true)]
    [InlineData("172.32.0.1", false)]
    [InlineData("8.8.8.8", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("2001:4860::8888", false)]
    [InlineData("::ffff:8.8.8.8", false)]
    public void Only_home_and_office_networks_are_answered(string address, bool local) =>
        Assert.Equal(local, RemoteHttp.IsLocalNetwork(IPAddress.Parse(address)));

    [Fact]
    public void Responses_are_never_cached_framed_or_sniffed()
    {
        string head = Encoding.ASCII.GetString(RemoteHttp.Response(200, "text/plain", "hi", "default-src 'none'"));
        foreach (string header in new[] { "Cache-Control: no-store", "X-Frame-Options: DENY", "X-Content-Type-Options: nosniff", "Referrer-Policy: no-referrer", "Connection: close", "Content-Length: 2", "Content-Security-Policy: default-src 'none'" })
            Assert.Contains(header, head);
    }

    [Fact]
    public void The_page_runs_only_its_own_script_and_style_and_loads_nothing_from_outside()
    {
        string html = RemotePage.Html;
        string csp = RemotePage.ContentSecurityPolicy;
        string script = html[(html.IndexOf("<script>", StringComparison.Ordinal) + 8)..html.IndexOf("</script>", StringComparison.Ordinal)];
        string style = html[(html.IndexOf("<style>", StringComparison.Ordinal) + 7)..html.IndexOf("</style>", StringComparison.Ordinal)];
        Assert.Contains($"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(script)))}'", csp);
        Assert.Contains($"'sha256-{Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(style)))}'", csp);
        Assert.Contains("default-src 'none'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
        Assert.DoesNotContain("src=", html);
        Assert.Contains("history.replaceState", html); // the key leaves the address bar
    }

    /// <summary>The page's own SHA-256 and HMAC (in JavaScript, run by Node) against .NET and RFC 4231.</summary>
    [Fact]
    public void The_pages_signing_matches_dotnet_and_the_standard()
    {
        if (FindNode() is not { } node) return; // no Node here: nothing to run the page's script with
        byte[] key = Enumerable.Range(0, 32).Select(i => (byte)(i * 7)).ToArray();
        string script = RemotePage.Crypto + """

            const enc = s => new TextEncoder().encode(s);
            const out = [];
            out.push(hex(sha256(enc(""))));
            out.push(hex(sha256(enc("abc"))));
            out.push(hex(sha256(new Uint8Array(1000).fill(97))));
            out.push(hex(hmac(new Uint8Array(20).fill(0x0b), enc("Hi There"))));
            out.push(hex(hmac(enc("Jefe"), enc("what do ya want for nothing?"))));
            out.push(hex(hmac(new Uint8Array(131).fill(0xaa), enc("Test Using Larger Than Block-Size Key - Hash Key First"))));
            out.push(hex(hmac(fromBase64Url(process.argv[2]), enc("SESSION\n1700000000000\nnext-slide"))));
            console.log(out.join("\n"));
            """;
        string file = Path.Combine(Path.GetTempPath(), $"scp-remote-{Guid.NewGuid():N}.js");
        File.WriteAllText(file, script);
        try
        {
            var info = new ProcessStartInfo(node, $"\"{file}\" {RemoteAuth.Base64Url(key)}") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using Process p = Process.Start(info)!;
            string[] lines = p.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            p.WaitForExit(10_000);
            Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", lines[0]);
            Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", lines[1]);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Enumerable.Repeat((byte)97, 1000).ToArray())), lines[2]);
            Assert.Equal("b0344c61d8db38535ca8afceaf0bf12b881dc200c9833da726e9376c2e32cff7", lines[3]); // RFC 4231 test 1
            Assert.Equal("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843", lines[4]); // test 2
            Assert.Equal("60e431591ee0b67f0d8a26aacbf5b77f8e0bc6213728c5140546040f0ee37f54", lines[5]); // test 6
            Assert.Equal(RemoteAuth.Sign(key, "SESSION", 1700000000000, "next-slide"), lines[6]);   // the page signs as the server checks
        }
        finally
        {
            File.Delete(file);
        }
    }

    internal static string? FindNode()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim('"'), "node.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException) { }
        }
        return null;
    }
}

/// <summary>FR-90 end to end: the real server on the loopback address, and the real helper process.</summary>
[Collection(SpeedCollection.Name)]
public class PhoneRemoteServerTests : IAsyncLifetime
{
    private static readonly byte[] Key = RemoteAuth.NewKey();
    private readonly List<string> _ran = [];
    private RemoteServer? _server;
    private Task? _serving;
    private HttpClient _http = null!;
    private string _session = "";

    public async Task InitializeAsync()
    {
        _server = new RemoteServer(Key, IPAddress.Loopback, 0, command =>
        {
            lock (_ran) _ran.Add(command);
            return "{\"status\":\"Showing on TV: Slides\"}";
        });
        _serving = _server.RunAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}") };
        string json = await _http.GetStringAsync("/session");
        _session = json.Split('"')[3];
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        _server?.Dispose();
        if (_serving is not null) await _serving.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private Task<HttpResponseMessage> Send(long counter, string command, byte[]? key = null, string? session = null)
    {
        string s = session ?? _session;
        string body = $"s={Uri.EscapeDataString(s)}&c={counter}&a={Uri.EscapeDataString(command)}&m={RemoteAuth.Sign(key ?? Key, s, counter, command)}";
        return _http.PostAsync("/cmd", new StringContent(body, Encoding.UTF8, "application/x-www-form-urlencoded"));
    }

    [Fact]
    public async Task The_page_comes_locked_down()
    {
        HttpResponseMessage page = await _http.GetAsync("/");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("ScreenCast Presenter remote", await page.Content.ReadAsStringAsync());
        Assert.Equal(RemotePage.ContentSecurityPolicy, page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal(HttpStatusCode.NotFound, (await _http.GetAsync("/favicon.ico")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await _http.GetAsync("/cmd")).StatusCode);
    }

    [Fact]
    public async Task A_signed_command_runs_once_and_answers_with_whats_on()
    {
        HttpResponseMessage ok = await Send(1000, "next-slide");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Contains("Showing on TV: Slides", await ok.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Conflict, (await Send(1000, "next-slide")).StatusCode); // overheard and sent again
        Assert.Contains("\"last\":1000", await (await Send(999, "blank")).Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(1001, "blank", RemoteAuth.NewKey())).StatusCode);
        HttpResponseMessage stale = await Send(1002, "blank", session: "an-old-session");
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Contains("stale", await stale.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(1003, "quit")).StatusCode);
        lock (_ran) Assert.Equal(["next-slide"], _ran);
    }

    [Fact]
    public async Task Garbage_and_floods_are_refused_without_harm()
    {
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, _server!.Port);
            NetworkStream s = raw.GetStream();
            try
            {
                await s.WriteAsync(Encoding.ASCII.GetBytes("GET /" + new string('a', 6000) + " HTTP/1.1\r\n\r\n"));
                string answer = await new StreamReader(s).ReadToEndAsync();
                Assert.StartsWith("HTTP/1.1 413", answer);
            }
            catch (IOException)
            {
                // Refused before it had all been read: the connection is reset instead. Also a refusal.
            }
        }
        Assert.Equal(HttpStatusCode.OK, (await _http.GetAsync("/session")).StatusCode); // and it carries on serving
        using (var raw = new TcpClient())
        {
            await raw.ConnectAsync(IPAddress.Loopback, _server.Port);
            NetworkStream s = raw.GetStream();
            await s.WriteAsync(Encoding.ASCII.GetBytes("HELLO\r\n\r\n"));
            Assert.StartsWith("HTTP/1.1 400", await new StreamReader(s).ReadToEndAsync());
        }
        int tooMany = 0;
        for (int i = 0; i < RemoteServer.CommandsPerSecond + 5; i++)
            if ((await Send(5000 + i, "status")).StatusCode == HttpStatusCode.TooManyRequests) tooMany++;
        Assert.True(tooMany >= 5, $"{tooMany} refused");
    }

    [Fact]
    public async Task Speed_a_command_round_trip_on_the_network_is_quick()
    {
        await Send(1, "status"); // warm up
        var clock = Stopwatch.StartNew();
        const int runs = 10;
        for (int i = 0; i < runs; i++) Assert.Equal(HttpStatusCode.OK, (await Send(10 + i, "status")).StatusCode);
        double each = clock.Elapsed.TotalMilliseconds / runs;
        Assert.True(each < 50, $"{each:F1} ms a command (budget 50 ms)");
    }

    [Fact]
    public async Task The_real_helper_process_serves_and_passes_on_only_what_was_accepted()
    {
        string exe = Path.Combine(AppContext.BaseDirectory, "ScreenCastPresenter.exe");
        if (!File.Exists(exe)) return;
        var info = new ProcessStartInfo(exe, RemoteHost.Switch) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
        using Process helper = Process.Start(info)!;
        try
        {
            byte[] key = RemoteAuth.NewKey();
            helper.StandardInput.WriteLine($"start {RemoteAuth.Base64Url(key)} 0 1");
            helper.StandardInput.Flush();
            string listening = (await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!;
            Assert.StartsWith("listening ", listening);
            int port = int.Parse(listening.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            string session = (await http.GetStringAsync("/session")).Split('"')[3];
            Task<HttpResponseMessage> post = http.PostAsync("/cmd", new StringContent($"s={session}&c=7&a=blank&m={RemoteAuth.Sign(key, session, 7, "blank")}", Encoding.UTF8, "application/x-www-form-urlencoded"));
            string? line;
            do line = await helper.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            while (line is not null && !line.StartsWith("cmd ", StringComparison.Ordinal));
            Assert.Equal("cmd blank", line);
            helper.StandardInput.WriteLine("status {\"status\":\"Blank\"}");
            helper.StandardInput.Flush();
            HttpResponseMessage answer = await post.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("Blank", await answer.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("/cmd", new StringContent($"s={session}&c=8&a=quit&m={RemoteAuth.Sign(key, session, 8, "quit")}", Encoding.UTF8, "application/x-www-form-urlencoded"))).StatusCode);
            helper.StandardInput.Close(); // the app going away stops it
            Assert.True(helper.WaitForExit(5000), "the helper stops when its input closes");
            Assert.Equal(0, helper.ExitCode); // cleanly, not crashed
        }
        finally
        {
            if (!helper.HasExited) helper.Kill();
        }
    }

    [Fact]
    public void The_app_side_forwards_only_listed_commands()
    {
        var calls = new List<string>();
        RemoteProcess? remote = typeof(RemoteProcess).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single().Invoke([Process.GetCurrentProcess()]) as RemoteProcess;
        remote!.Command += calls.Add;
        int port = 0;
        IReadOnlyList<string>? addresses = null;
        remote.Listening += (p, a) => { port = p; addresses = a; };
        remote.Handle("cmd blank");
        remote.Handle("cmd quit");
        remote.Handle("cmd favourite-3");
        remote.Handle("listening 47820 192.168.1.20,10.0.0.4");
        Assert.Equal(["blank", "favourite-3"], calls);
        Assert.Equal(47820, port);
        Assert.Equal(["192.168.1.20", "10.0.0.4"], addresses);
    }
}
