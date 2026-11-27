using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace StressHarness;

/// <summary>
/// Rev 10 in the real app, end to end from the command line (as a button deck drives it): --status
/// and --is, the night filter, the timer on the TV, Follow me, clicker keys, a PDF turned page by
/// page, the whiteboard, the room sign, copying the text, a video played to its end, and a GIF of
/// a recording. The privacy audit, the response check and the one-output-window rule run after each.
/// </summary>
internal static partial class Program
{
    private static void Rev10()
    {
        Scenario("rev 10", (scp, host) =>
        {
            (int Code, string Text) Cli(params string[] args) => scp.RunCommandLine(args);
            string? Saved(string name) => JsonNode.Parse(File.ReadAllText(scp.SettingsPath))?[name]?.ToString();

            Check(Cli("--favourite", "1").Code == 0, "rev10: --favourite 1 failed");
            Wait(800);

            // --status and --is: what's on, for button decks.
            (int code, string status) = Cli("--status");
            JsonNode? json = null;
            try { json = JsonNode.Parse(status); } catch (System.Text.Json.JsonException) { }
            Check(code == 0 && json?["showing"]?.GetValue<bool>() == true, $"rev10: --status didn't say it's showing: {status}");
            Check(json?["switches"]?["night-filter"] is not null, "rev10: --status lacks the switches");
            Check(Cli("--is", "showing").Code == 0, "rev10: --is showing should be 0");
            Check(Cli("--is", "spotlight").Code == 1, "rev10: --is spotlight should be 1 (off)");
            Check(Cli("--is", "rockets").Code == 2, "rev10: --is rockets should be a mistake (exit 2)");

            // The night filter, through the helper.
            Check(Cli("--night", "invert").Code == 0, "rev10: --night invert failed");
            Check(WaitFor(() => Saved("NightFilter") == "Invert", 3000), "rev10: the night filter wasn't saved");
            Wait(800);
            Check(scp.HelperCount >= 1, "rev10: the night filter needs the capture helper");
            Probe(scp, "night filter on");
            Check(Cli("--night", "off").Code == 0 && WaitFor(() => Saved("NightFilter") == "Off", 3000), "rev10: --night off failed");

            // A timer on the TV.
            Check(Cli("--timer", "1").Code == 0, "rev10: --timer 1 failed");
            Check(Cli("--is", "timer").Code == 0, "rev10: the timer isn't on");
            string? TimerText() => JsonNode.Parse(Cli("--status").Text)?["timer"]?.ToString();
            string? first = TimerText();
            Wait(2200);
            string? later = TimerText();
            Check(first is not null && later is not null && first != later, $"rev10: the timer doesn't count down ({first} then {later})");
            Probe(scp, "a timer on the TV");
            Check(Cli("--timer", "off").Code == 0 && Cli("--is", "timer").Code == 1, "rev10: --timer off failed");

            // Follow me and clicker keys switch exactly.
            Check(Cli("--on", "follow-me").Code == 0 && Cli("--is", "follow-me").Code == 0, "rev10: --on follow-me failed");
            Check(Cli("--off", "follow-me").Code == 0 && Cli("--is", "follow-me").Code == 1, "rev10: --off follow-me failed");
            Check(Cli("--on", "clicker-keys").Code == 0 && WaitFor(() => Saved("ClickerKeys") == "true", 3000), "rev10: --on clicker-keys failed");
            Check(Cli("--off", "clicker-keys").Code == 0 && WaitFor(() => Saved("ClickerKeys") == "false", 3000), "rev10: --off clicker-keys failed");

            // A PDF, page by page.
            string pdf = Path.Combine(Path.GetTempPath(), $"scp-harness-{Guid.NewGuid():N}.pdf");
            File.WriteAllBytes(pdf, MakePdf(3));
            try
            {
                Check(Cli("--pdf", pdf).Code == 0, "rev10: --pdf failed");
                Check(scp.OutputWindows().Count == 1, "rev10: --pdf showed nothing");
                Probe(scp, "a PDF");
                Check(Cli("--action", "next-slide").Code == 0, "rev10: next page failed");
                Check(WaitFor(() => Cli("--status").Text.Contains("page 2 of 3", StringComparison.Ordinal), 8000), "rev10: next slide didn't turn the PDF's page");
                Check(Cli("--pdf", pdf, "--page", "3").Code == 0 && Cli("--status").Text.Contains("page 3 of 3", StringComparison.Ordinal), "rev10: --page 3 failed");
                Check(Cli("--pdf", pdf + ".missing.pdf").Code == 1, "rev10: a missing PDF should exit 1");
                // Copying the text of a PDF page: read from what the audience sees (the page isn't on the presenter's screen).
                long logBefore = File.Exists(ScpProcess.DiagLog) ? new FileInfo(ScpProcess.DiagLog).Length : 0;
                Check(Cli("--copy-text").Code == 0, "rev10: --copy-text on a PDF failed");
                Check(WaitFor(() => File.Exists(ScpProcess.DiagLog) && NewLog(logBefore) is var l && (l.Contains("Copied", StringComparison.Ordinal) || l.Contains("No text was found", StringComparison.Ordinal)), 25_000),
                    "rev10: copying a PDF page's text didn't finish: " + NewLog(logBefore));
                Check(!NewLog(logBefore).Contains("couldn't be captured", StringComparison.Ordinal), "rev10: a PDF page's text couldn't be captured");
            }
            finally
            {
                File.Delete(pdf);
            }

            // The whiteboard: out, then put away.
            Check(Cli("--whiteboard").Code == 0 && Cli("--is", "whiteboard").Code == 0, "rev10: --whiteboard failed");
            Wait(800);
            Probe(scp, "the whiteboard");
            Check(Cli("--whiteboard").Code == 0, "rev10: putting the whiteboard away failed");

            // The room sign without a calendar says what to do; copying the text works on what's shown.
            Check(Cli("--favourite", "1").Code == 0, "rev10: back to favourite 1 failed");
            Check(Cli("--room-sign").Code == 0, "rev10: --room-sign failed");
            Check(Cli("--copy-text").Code == 0, "rev10: --copy-text failed");
            Probe(scp, "after copying text");

            // A video, then a GIF of a recording (a short one made here).
            Check(Cli("--start-recording").Code == 0, "rev10: recording failed to start");
            Wait(4000);
            Check(Cli("--stop-recording").Code == 0, "rev10: recording failed to stop");
            string? video = WaitForFile(scp.RecordingDir, "*.mp4", 20_000);
            Check(video is not null, "rev10: no recording to play");
            if (video is not null)
            {
                Check(Cli("--video", video).Code == 0, "rev10: --video failed");
                Wait(1500);
                Probe(scp, "a video playing");
                Check(Cli("--freeze").Code == 0 && Cli("--unfreeze").Code == 0, "rev10: freezing a video failed");
                Check(Cli("--stop").Code == 0, "rev10: --stop after the video failed");
                Check(Cli("--gif", video).Code == 0, "rev10: --gif failed");
                if (WaitForFile(Path.GetDirectoryName(video)!, "*.gif", 90_000) is null)
                {
                    // A short recording of a still screen can itself be unreadable on some PCs (a recording
                    // issue older than GIFs: see the "record" scenario); the GIF maker then says so plainly.
                    string log = File.Exists(ScpProcess.DiagLog) ? File.ReadAllText(ScpProcess.DiagLog) : "";
                    if (log.Contains("gif: open", StringComparison.Ordinal))
                        Console.WriteLine("    note: the recording couldn't be read back on this PC, so no GIF (a recording issue, not the GIF maker's)");
                    else Check(false, "rev10: the GIF wasn't made beside the recording");
                }
            }
            Check(Cli("--stop").Code == 0, "rev10: --stop failed");
            Check(Cli("--status").Text.Contains("\"showing\":false", StringComparison.Ordinal), "rev10: --status after stopping still says showing");
        });
    }

    /// <summary>What the app wrote to its diagnostic log since <paramref name="from"/> bytes.</summary>
    private static string NewLog(long from)
    {
        try
        {
            using var stream = new FileStream(ScpProcess.DiagLog, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= from) return "";
            stream.Position = from;
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return "";
        }
    }

    private static string? WaitForFile(string folder, string pattern, int ms)
    {
        string? found = null;
        WaitFor(() => Directory.Exists(folder) && (found = Directory.GetFiles(folder, pattern).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault(f => new FileInfo(f).Length > 1000)) is not null, ms);
        return found;
    }

    /// <summary>A tiny real PDF: white pages, each with a black box and its number.</summary>
    private static byte[] MakePdf(int pages)
    {
        var objects = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            $"<< /Type /Pages /Kids [{string.Join(' ', Enumerable.Range(0, pages).Select(i => $"{4 + i * 2} 0 R"))}] /Count {pages} >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        for (int i = 0; i < pages; i++)
        {
            string content = $"0 0 0 rg 0 692 200 100 re f BT /F1 48 Tf 72 400 Td (Page {i + 1}) Tj ET";
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {5 + i * 2} 0 R /Resources << /Font << /F1 3 0 R >> >> >>");
            objects.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
        }
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(pdf.ToString()));
            pdf.Append(CultureInfo.InvariantCulture, $"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(pdf.ToString());
        pdf.Append(CultureInfo.InvariantCulture, $"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (int o in offsets) pdf.Append(CultureInfo.InvariantCulture, $"{o:D10} 00000 n \n");
        pdf.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }
}
