using System.Globalization;
using System.Runtime.InteropServices;
using ScreenCastPresenter.Core;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// --pdf-pages &lt;pdf&gt; &lt;folder&gt; &lt;pages&gt;: makes PDF pages into pictures (FR-115) with Windows' own PDF
/// renderer, in a helper process so a damaged or hostile file can't take the app down. Says
/// "pages N" first, then "done K" as each page is written (written aside and moved into place), or
/// "failed" and why. Exit 0 when every page asked for was made.
/// </summary>
internal static class PdfRenderer
{
    private const int WrongPassword = unchecked((int)0x8007052B);

    public static int Run(string[] args, Action<string> send)
    {
        if (args.Length < 4) return 2;
        string path = args[1], folder = args[2];
        List<int> wanted = [.. args[3].Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out int n) ? n : 0).Where(n => n > 0).Distinct().Take(16)];
        if (PdfPages.Problem(path) is { } problem)
        {
            send("failed " + problem);
            return 4;
        }
        PdfDocument document;
        try
        {
            StorageFile file = StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            document = PdfDocument.LoadFromFileAsync(file).AsTask().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex.HResult == WrongPassword)
        {
            send($"failed “{PdfPages.Name(path)}” is protected with a password. Open it in your PDF reader and show that window instead.");
            return 4;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException or FileNotFoundException)
        {
            send($"failed “{PdfPages.Name(path)}” can't be read: it may be damaged, or not really a PDF.");
            return 4;
        }
        int pages = (int)Math.Min(document.PageCount, int.MaxValue);
        send(PdfPages.PagesLine(pages));
        if (pages <= 0)
        {
            send($"failed “{PdfPages.Name(path)}” has no pages.");
            return 4;
        }
        Directory.CreateDirectory(folder);
        int made = 0;
        foreach (int page in wanted.Where(p => p <= pages))
        {
            try
            {
                using PdfPage pdfPage = document.GetPage((uint)(page - 1));
                (int w, int h) = PdfPages.RenderSize(pdfPage.Size.Width, pdfPage.Size.Height);
                using var stream = new InMemoryRandomAccessStream();
                var options = new PdfPageRenderOptions { DestinationWidth = (uint)w, DestinationHeight = (uint)h, BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255) };
                pdfPage.RenderToStreamAsync(stream, options).AsTask().GetAwaiter().GetResult(); // a PNG by default
                byte[] bytes = new byte[stream.Size];
                using (DataReader reader = new(stream.GetInputStreamAt(0)))
                {
                    reader.LoadAsync((uint)stream.Size).AsTask().GetAwaiter().GetResult();
                    reader.ReadBytes(bytes);
                }
                string target = PdfPages.PagePath(folder, page), temporary = target + ".tmp";
                File.WriteAllBytes(temporary, bytes);
                File.Move(temporary, target, overwrite: true);
                send(string.Create(CultureInfo.InvariantCulture, $"done {page}"));
                made++;
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or ArgumentException)
            {
                send(string.Create(CultureInfo.InvariantCulture, $"failed Page {page} couldn't be made into a picture."));
            }
        }
        return made == wanted.Count(p => p <= pages) ? 0 : 5;
    }

    /// <summary>Runs the helper from the app: how many pages there are and which were made, or why not.</summary>
    public static async Task<(int Pages, IReadOnlyList<int> Done, string? Problem)> RenderAsync(string pdf, string folder, IReadOnlyList<int> pages, TimeSpan timeout)
    {
        var info = new System.Diagnostics.ProcessStartInfo(CaptureHost.HelperPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        CaptureHost.UseUtf8(info);
        foreach (string a in new[] { CaptureProtocol.PdfSwitch, pdf, folder, string.Join(',', pages.Select(p => p.ToString(CultureInfo.InvariantCulture))) })
            info.ArgumentList.Add(a);
        info.StandardInputEncoding = null;
        using System.Diagnostics.Process? helper = System.Diagnostics.Process.Start(info);
        if (helper is null) return (0, [], "The PDF couldn't be opened.");
        using var cancel = new CancellationTokenSource(timeout);
        int count = 0;
        var done = new List<int>();
        string? problem = null;
        try
        {
            string? line;
            while ((line = await helper.StandardOutput.ReadLineAsync(cancel.Token)) is not null)
            {
                if (PdfPages.ParsePages(line) is { } n) count = n;
                else if (line.StartsWith("done ", StringComparison.Ordinal) && int.TryParse(line.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out int d)) done.Add(d);
                else if (line.StartsWith("failed ", StringComparison.Ordinal)) problem ??= line[7..];
            }
            await helper.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            try { helper.Kill(); } catch (InvalidOperationException) { }
            problem ??= "Making the PDF's pages took too long.";
        }
        return (count, done, problem);
    }
}
