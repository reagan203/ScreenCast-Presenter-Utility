using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Remote;

/// <summary>
/// The phone remote's helper process (FR-90): the program started with <see cref="Switch"/>.
/// All the network code lives here, never in the app. It reads one start line from the app
/// ("start key port loopback"), then serves until its input closes. It tells the app what it
/// accepted ("cmd …"), and the app answers each with what's on ("status {json}").
///
/// App → helper: start &lt;key&gt; &lt;port&gt; &lt;0|1&gt; · status &lt;json&gt;.
/// Helper → app: listening &lt;port&gt; &lt;addresses&gt; · error &lt;text&gt; · cmd &lt;command&gt; · seen &lt;address&gt;.
/// </summary>
internal static class RemoteHost
{
    public const string Switch = "--remote";

    /// <summary>How long a command waits for the app's fresh status before answering with the last one.</summary>
    public static readonly TimeSpan StatusWait = TimeSpan.FromSeconds(2);

    private static readonly object WriteLock = new();
    private static readonly object StatusLock = new();
    private static string _status = "{\"status\":\"Connected\"}";
    private static long _statusVersion;

    public static int Run()
    {
        var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var output = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        string[] start = (input.ReadLine() ?? "").Split(' ');
        if (start.Length < 4 || start[0] != "start" || RemoteAuth.FromBase64Url(start[1]) is not { Length: RemoteAuth.KeyBytes } key
            || !int.TryParse(start[2], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int port) || port > IPEndPoint.MaxPort)
        {
            Say(output, "error The remote couldn't start.");
            return 2;
        }
        bool loopback = start[3] == "1";
        RemoteServer server;
        try
        {
            server = new RemoteServer(key, loopback ? IPAddress.Loopback : IPAddress.Any, port, command => Run(output, command));
        }
        catch (SocketException ex)
        {
            Say(output, "error The remote couldn't listen on the network (" + ex.SocketErrorCode + ").");
            return 3;
        }
        using (server)
        {
            server.Seen += address => Say(output, "seen " + address);
            Task serving = server.RunAsync();
            Say(output, $"listening {server.Port} {string.Join(',', loopback ? ["127.0.0.1"] : LocalAddresses())}");
            // The app's status lines; the app closing our input (or going away) stops everything.
            string? line;
            while ((line = input.ReadLine()) is not null)
            {
                if (!line.StartsWith("status ", StringComparison.Ordinal)) continue;
                lock (StatusLock)
                {
                    _status = line["status ".Length..];
                    _statusVersion++;
                    Monitor.PulseAll(StatusLock);
                }
            }
            server.Dispose(); // stop listening now; the using block's second Dispose does nothing
            serving.Wait(StatusWait);
        }
        return 0;
    }

    /// <summary>Passes an accepted command to the app and waits briefly for what's on afterwards.</summary>
    private static string Run(StreamWriter output, string command)
    {
        long before;
        lock (StatusLock) before = _statusVersion;
        Say(output, "cmd " + command);
        lock (StatusLock)
        {
            var deadline = DateTime.UtcNow + StatusWait;
            while (_statusVersion == before)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Monitor.Wait(StatusLock, left)) break;
            }
            return _status;
        }
    }

    private static void Say(StreamWriter output, string line)
    {
        lock (WriteLock)
        {
            try
            {
                output.WriteLine(CaptureOneLine(line));
            }
            catch (IOException)
            {
                // the app is gone; our input closes next
            }
        }
    }

    private static string CaptureOneLine(string line) => Capture.CaptureProtocol.OneLine(line);

    /// <summary>This PC's addresses on home or office networks (IPv4, up, with a gateway first): what the pairing code points the phone at.</summary>
    public static List<string> LocalAddresses()
    {
        var withGateway = new List<string>();
        var others = new List<string>();
        try
        {
            foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
                IPInterfaceProperties props = nic.GetIPProperties();
                bool gateway = props.GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any));
                foreach (UnicastIPAddressInformation a in props.UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || IPAddress.IsLoopback(a.Address) || !RemoteHttp.IsLocalNetwork(a.Address)) continue;
                    if (a.Address.GetAddressBytes()[0] == 169) continue; // link-local: no network handed out an address
                    (gateway ? withGateway : others).Add(a.Address.ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
            // none found: the app says so
        }
        return [.. withGateway.Concat(others).Distinct().Take(4)];
    }
}
