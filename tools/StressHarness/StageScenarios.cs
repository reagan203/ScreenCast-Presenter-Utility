using System.Buffers.Binary;
using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;

namespace StressHarness;

internal static partial class Program
{
    private const string StudioTitle = "ScreenCast Presenter: what the audience sees";

    /// <summary>
    /// The stage tools (FR-74–FR-85) in the real app, by their shortcuts, the mouse and the
    /// command line: hiding part of a window, the spotlight, the name banner, a QR code, the logo
    /// on the blank screen, the audience view, "get ready, then go", live captions, a room and
    /// (with three screens) a second TV. The privacy audit runs while each is on.
    /// </summary>
    private static void Stage()
    {
        string logo = Path.Combine(Path.GetTempPath(), $"scp-harness-logo-{Guid.NewGuid():N}.png");
        File.WriteAllBytes(logo, SolidPng(320, 120, 0x46, 0x82, 0xB4));
        try
        {
            Scenario("stage tools", (scp, host) =>
            {
                Native.CtrlAlt('1'); Wait(900);
                Check(scp.OutputWindows().Count == 1, "stage: favourite 1 didn't show");
                Native.GetWindowRect(host["SCP dark A"], out Native.RECT r);
                host.Send($"front {host["SCP dark A"]}"); Wait(300);
                Point middle = new((r.Left + r.Right) / 2, (r.Top + r.Bottom) / 2);

                // Hide part of the window: drag a box over it. The helper takes over to cover it.
                Native.CtrlAlt('X'); Wait(700);
                Probe(scp, "choosing a part to hide");
                Native.SetCursorPos(middle.X - 80, middle.Y - 40);
                Native.mouse_event(0x0002, 0, 0, 0, 0);
                for (int i = 0; i < 10; i++) { Native.SetCursorPos(middle.X - 80 + i * 16, middle.Y - 40 + i * 8); Wait(20); }
                Native.mouse_event(0x0004, 0, 0, 0, 0);
                Wait(1500);
                Check(scp.HelperCount >= 1, "stage: a hidden part needs the capture helper, and none is running");
                Check(JsonNode.Parse(File.ReadAllText(scp.SettingsPath))?["HiddenParts"] is JsonObject { Count: > 0 }, "stage: the hidden part wasn't remembered for the app");
                Probe(scp, "a part hidden");

                // The spotlight following the pointer.
                Native.CtrlAltShift('F'); Wait(500);
                for (int i = 0; i < 30; i++) { Native.SetCursorPos(middle.X - 60 + i * 4, middle.Y + (i % 5) * 4); Wait(16); }
                Probe(scp, "spotlight on");
                Native.CtrlAltShift('F'); Wait(300);

                // The name banner (the name is set up already, so no question is asked).
                Native.CtrlAltShift('N'); Wait(600);
                Probe(scp, "name banner on");
                Native.CtrlAltShift('N'); Wait(300);

                // A QR code from the command line, then off.
                Check(scp.RunCommandLine("--qr", "https://example.com/agenda").ExitCode == 0, "stage: --qr failed");
                Wait(400);
                Probe(scp, "QR code on");
                Check(scp.RunCommandLine("--qr", "off").ExitCode == 0, "stage: --qr off failed");

                // The blank screen with its message and the logo.
                Native.CtrlAltShift('B'); Wait(900);
                Probe(scp, "blank with the logo");
                Native.CtrlAlt('B'); Wait(600);

                // The audience view: open, audited (it must be hidden from capture), closed.
                Native.CtrlAlt('V'); Wait(1200);
                Check(Studio(scp) != 0, "stage: Ctrl+Alt+V opened no audience view");
                Probe(scp, "audience view open");

                // Get ready (the picker in its "next" mode), nothing chosen; Go with nothing ready just says so.
                Native.CtrlAltShift('G'); Wait(700);
                Probe(scp, "choosing what's next");
                Native.Chord(Native.VK_ESCAPE); Wait(300);
                Native.CtrlAlt('G'); Wait(400);
                Native.CtrlAlt('V'); Wait(500);
                Check(Studio(scp) == 0, "stage: Ctrl+Alt+V again left the audience view open");

                // Live captions: the speech helper starts, and goes when they're turned off.
                int helpers = scp.HelperCount;
                Native.CtrlAltShift('T'); Wait(2500);
                Check(scp.HelperCount > helpers || !HasMicrophone(), "stage: live captions started no speech helper");
                Probe(scp, "live captions on");
                Native.CtrlAltShift('T');
                Check(WaitFor(() => scp.HelperCount <= helpers, 3000), "stage: the speech helper kept running after live captions went off");

                // A room, by the command line.
                Check(scp.RunCommandLine("--room", "Lobby").ExitCode == 0, "stage: --room Lobby failed");
                Check(scp.RunCommandLine("--room", "Nowhere").ExitCode == 1, "stage: an unknown room should exit 1");
                Probe(scp, "after the room");

                // Show the hidden parts again.
                Native.CtrlAltShift('X'); Wait(500);
                Probe(scp, "hidden parts shown again");

                // A second TV, when there are three screens to try it on.
                if (FlashMonitor.ListScreens().Count >= 3)
                {
                    Native.CtrlAlt('2'); Wait(600);
                    Probe(scp, "before the second TV");
                }
            }, settings: s =>
            {
                s["CaptionName"] = "Test Presenter";
                s["CaptionSecond"] = "Stress harness";
                s["BlankLogo"] = logo;
                s["HiddenPartLook"] = "Blurred";
                s["Rooms"] = new JsonArray(new JsonObject { ["Name"] = "Lobby", ["Dim"] = 0.1, ["OutputDevice"] = _outputDevice });
            });
        }
        finally
        {
            File.Delete(logo);
        }

        static nint Studio(ScpProcess scp) =>
            Native.TopLevelWindows(scp.Id, visibleOnly: true).FirstOrDefault(h => Native.Title(h) == StudioTitle);
    }

    /// <summary>A PNG of one colour, written by hand (the harness has no imaging library).</summary>
    private static byte[] SolidPng(int width, int height, byte r, byte g, byte b)
    {
        var raw = new MemoryStream();
        for (int y = 0; y < height; y++)
        {
            raw.WriteByte(0); // no filter
            for (int x = 0; x < width; x++)
            {
                raw.WriteByte(r);
                raw.WriteByte(g);
                raw.WriteByte(b);
            }
        }
        var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) raw.WriteTo(z);

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = 8; // bits per channel
        header[9] = 2; // RGB
        Chunk(png, "IHDR", header);
        Chunk(png, "IDAT", packed.ToArray());
        Chunk(png, "IEND", []);
        return png.ToArray();

        static void Chunk(Stream s, string type, byte[] data)
        {
            Span<byte> length = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
            s.Write(length);
            byte[] typed = [.. Encoding.ASCII.GetBytes(type), .. data];
            s.Write(typed);
            BinaryPrimitives.WriteUInt32BigEndian(length, Crc32(typed));
            s.Write(length);
        }

        static uint Crc32(byte[] bytes)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte value in bytes)
            {
                crc ^= value;
                for (int k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320 ^ (crc >> 1) : crc >> 1;
            }
            return ~crc;
        }
    }

    /// <summary>
    /// Running the room (FR-86–FR-90) in the real app: a scene by its key and by the command line,
    /// a playlist switching on its own and stopped, automatic hiding on and off (the helper reads
    /// the screen), and next / previous slide. The privacy audit runs throughout.
    /// </summary>
    private static void RoomRunning()
    {
        Scenario("running the room", (scp, host) =>
        {
            const byte F1 = 0x70;
            Native.Chord(Native.VK_CONTROL, Native.VK_MENU, F1); Wait(1200);
            Check(scp.OutputWindows().Count == 1, "room: Ctrl+Alt+F1 (scene 1) showed nothing");
            Probe(scp, "scene 1 by its key");
            Check(scp.RunCommandLine("--scene", "1").ExitCode == 0, "room: --scene 1 failed");
            Check(scp.RunCommandLine("--scene", "12").ExitCode == 2, "room: --scene 12 should be a mistake (exit 2)");

            // A playlist of two favourites, ten seconds each: it switches on its own, then stops.
            Check(scp.RunCommandLine("--playlist", "Lobby").ExitCode == 0, "room: --playlist Lobby failed");
            Wait(11_500);
            Probe(scp, "playlist switched");
            Check(scp.RunCommandLine("--playlist", "off").ExitCode == 0, "room: --playlist off failed");
            Check(scp.OutputWindows().Count == 1, "room: stopping the playlist stopped showing");

            // Automatic hiding: the helper reads what's shown; on, a moment, off.
            Check(scp.RunCommandLine("--action", "auto-hide").ExitCode == 0, "room: auto-hide on failed");
            Wait(2500);
            Check(scp.HelperCount >= 1, "room: automatic hiding needs the capture helper");
            Probe(scp, "automatic hiding on");
            Check(scp.RunCommandLine("--action", "auto-hide").ExitCode == 0, "room: auto-hide off failed");

            // Next and previous slide go to the window shown, without taking the focus.
            Check(scp.RunCommandLine("--action", "next-slide").ExitCode == 0, "room: next-slide failed");
            Check(scp.RunCommandLine("--action", "previous-slide").ExitCode == 0, "room: previous-slide failed");
            Probe(scp, "after slide keys");
        }, settings: s =>
        {
            s["Scenes"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Dark B",
                ["Source"] = new JsonObject { ["AppName"] = "TestWindowHost", ["Title"] = "SCP dark B" },
                ["Dim"] = 0.1,
            });
            s["Playlists"] = new JsonArray(new JsonObject
            {
                ["Name"] = "Lobby",
                ["Items"] = new JsonArray(new JsonObject { ["Slot"] = 1, ["Seconds"] = 10 }, new JsonObject { ["Slot"] = 2, ["Seconds"] = 10 }),
            });
            s["AutoHide"] = "None";
        });
    }

    /// <summary>Whether Windows lists a microphone (without one, live captions say so instead of starting).</summary>
    private static bool HasMicrophone()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture");
            return key?.GetSubKeyNames().Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return true;
        }
    }

    /// <summary>
    /// How fast the stage tools answer in the real app, end to end: from the shortcut or the
    /// command line to the result on screen. Numbers are printed; each has a budget.
    /// </summary>
    private static void Speed()
    {
        Scenario("speed", (scp, host) =>
        {
            double Time(Action act, Func<bool> done, int timeoutMs)
            {
                var clock = Stopwatch.StartNew();
                act();
                while (!done() && clock.ElapsedMilliseconds < timeoutMs) Thread.Sleep(5);
                return done() ? clock.Elapsed.TotalMilliseconds : double.PositiveInfinity;
            }

            // Showing something from nothing (fade in included), five times.
            var show = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                show.Add(Time(() => Native.CtrlAlt('1'), () => scp.OutputWindows().Count == 1, 3000));
                Native.CtrlAlt('S'); WaitFor(() => scp.OutputWindows().Count == 0, 3000); Wait(300);
            }
            Report("show favourite 1 (shortcut to output on screen)", show, 1000);

            Native.CtrlAlt('1'); Wait(900);
            // Turning on a mark: the picture moves to the helper through black.
            double helper = Time(() => Native.CtrlAltShift('F'), () => scp.HelperCount >= 1, 4000);
            Report("spotlight on (shortcut to capture helper running)", [helper], 2500);
            Native.CtrlAltShift('F'); Wait(300);

            // The audience view opening, five times.
            var studio = new List<double>();
            for (int i = 0; i < 5; i++)
            {
                studio.Add(Time(() => Native.CtrlAlt('V'), () => Native.TopLevelWindows(scp.Id, visibleOnly: true).Any(h => Native.Title(h) == StudioTitle), 3000));
                Native.CtrlAlt('V'); Wait(400);
            }
            Report("audience view opening", studio, 600);

            // Command-line requests, exe start included.
            var room = new List<double>();
            for (int i = 0; i < 10; i++) room.Add(Time(() => Check(scp.RunCommandLine("--room", "Lobby").ExitCode == 0, "speed: --room failed"), () => true, 10_000));
            Report("--room Lobby (whole command line)", room, 1500);
            var qr = new List<double>();
            for (int i = 0; i < 10; i++) qr.Add(Time(() => Check(scp.RunCommandLine("--qr", i % 2 == 0 ? "https://example.com" : "off").ExitCode == 0, "speed: --qr failed"), () => true, 10_000));
            Report("--qr (whole command line)", qr, 1500);
            Probe(scp, "after the speed runs");
        }, settings: s => s["Rooms"] = new JsonArray(new JsonObject { ["Name"] = "Lobby", ["OutputDevice"] = _outputDevice }));

        static void Report(string what, List<double> ms, double budgetMs)
        {
            double average = ms.Average(), worst = ms.Max();
            Console.WriteLine($"    {what}: average {average:F0} ms, worst {worst:F0} ms (budget {budgetMs:F0} ms)");
            Check(average < budgetMs, $"speed: {what} took {average:F0} ms on average (budget {budgetMs:F0} ms)");
        }
    }
}
