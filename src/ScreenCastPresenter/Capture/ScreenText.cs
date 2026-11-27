using System.Runtime.InteropServices;
using ScreenCastPresenter.Core;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// --read-text &lt;png&gt;: the words in a picture of what's shown (FR-130), read by Windows' own text
/// recognizer on this PC, in a helper process. The picture has the hidden parts and automatic
/// hiding already covered, so what the audience can't see is never copied either. One "line …" per
/// line of text, or "failed" and why.
/// </summary>
internal static class ScreenText
{
    public static int Run(string[] args, Action<string> send)
    {
        if (args.Length < 2 || !File.Exists(args[1])) return 2;
        Bitmap image;
        try
        {
            using var stream = new MemoryStream(File.ReadAllBytes(args[1]));
            image = new Bitmap(stream);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException)
        {
            send("failed The picture of what's shown couldn't be read.");
            return 4;
        }
        using (image)
        {
            double scale = Math.Min(1, (double)AutoRedactor.ScanLimit / Math.Max(image.Width, image.Height));
            int w = Math.Max(1, (int)(image.Width * scale)), h = Math.Max(1, (int)(image.Height * scale));
            using var scan = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using (Graphics g = Graphics.FromImage(scan))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(image, new Rectangle(0, 0, w, h));
            }
            System.Drawing.Imaging.BitmapData data = scan.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            var bgra = new byte[w * h * 4];
            try
            {
                for (int y = 0; y < h; y++) Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * w * 4, w * 4);
            }
            finally
            {
                scan.UnlockBits(data);
            }
            List<OcrLine>? lines = AutoRedactor.ReadAsync(bgra, w, h).GetAwaiter().GetResult();
            if (lines is null)
            {
                send("failed Windows has no text recognition for your language. Add one in Settings › Time & language › Language.");
                return 4;
            }
            foreach (string line in CopiedText.Lines(lines)) send("line " + line);
        }
        return 0;
    }

    /// <summary>Runs the helper from the app on a picture: the lines of text, or why there are none.</summary>
    public static async Task<(List<string> Lines, string? Problem)> ReadAsync(string png, TimeSpan timeout)
    {
        var info = new System.Diagnostics.ProcessStartInfo(CaptureHost.HelperPath) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        info.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
        info.ArgumentList.Add(CaptureProtocol.ReadTextSwitch);
        info.ArgumentList.Add(png);
        using System.Diagnostics.Process? helper = System.Diagnostics.Process.Start(info);
        if (helper is null) return ([], "Text recognition couldn't start.");
        using var cancel = new CancellationTokenSource(timeout);
        var lines = new List<string>();
        string? problem = null;
        try
        {
            string? line;
            while ((line = await helper.StandardOutput.ReadLineAsync(cancel.Token)) is not null)
            {
                if (line.StartsWith("line ", StringComparison.Ordinal)) lines.Add(line[5..]);
                else if (line.StartsWith("failed ", StringComparison.Ordinal)) problem ??= line[7..];
            }
            await helper.WaitForExitAsync(cancel.Token);
        }
        catch (OperationCanceledException)
        {
            try { helper.Kill(); } catch (InvalidOperationException) { }
            problem ??= "Reading the text took too long.";
        }
        return (lines, problem);
    }
}
