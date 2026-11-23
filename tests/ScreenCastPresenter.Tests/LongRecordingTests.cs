using System.Diagnostics;
using ScreenCastPresenter.Recording;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Xunit.Abstractions;

namespace ScreenCastPresenter.Tests;

/// <summary>A test that only runs when SCP_LONG_TESTS=1 (it takes minutes and needs the GPU).</summary>
public sealed class LongFactAttribute : FactAttribute
{
    public LongFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SCP_LONG_TESTS") != "1")
            Skip = "Long test: set SCP_LONG_TESTS=1 to run it.";
    }
}

/// <summary>
/// NFR-09a: memory must not grow over a two-hour recording. Writes two hours of 1280×720 noise at
/// 30 fps plus audio as fast as the GPU allows (about 9 minutes) and compares memory after the
/// first minutes with memory at the end. Caught a leak of ~70 MB per two hours (a .NET COM
/// wrapper per sample) that short tests never showed.
/// </summary>
public class LongRecordingTests(ITestOutputHelper output)
{
    [LongFact]
    public void Two_hours_of_recording_dont_grow_memory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"scp-long-{Guid.NewGuid():N}.mp4");
        try
        {
            using ID3D11Device device = Mp4Writer.CreateDevice();
            using var writer = new Mp4Writer(path, 1280, 720, device, withAudio: true);
            var textures = new ID3D11Texture2D[6];
            var views = new ID3D11RenderTargetView[6];
            for (int i = 0; i < textures.Length; i++)
            {
                textures[i] = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 1280, 720, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
                views[i] = device.CreateRenderTargetView(textures[i]);
            }
            short[] pcm = new short[1600 * AudioMixer.Channels]; // 1/30 s
            var random = new Random(1);
            long frame = 0;
            double settled = 0;
            for (int minute = 1; minute <= 120; minute++)
            {
                for (int tick = 0; tick < 60 * Mp4Writer.FrameRate; tick++)
                {
                    long time = ((minute - 1) * 60L * Mp4Writer.FrameRate + tick) * Mp4Writer.FrameDuration;
                    while (writer.VideoInFlight >= textures.Length - 1) Thread.Sleep(1);
                    int slot = (int)(frame++ % textures.Length);
                    device.ImmediateContext.ClearRenderTargetView(views[slot], new Color4((float)random.NextDouble(), 0.3f, 0.5f, 1));
                    writer.WriteVideo(textures[slot], time, Mp4Writer.FrameDuration);
                    for (int i = 0; i < pcm.Length; i++) pcm[i] = (short)random.Next(-3000, 3000);
                    writer.WriteAudio(pcm, time);
                }
                double mb = PrivateMb();
                if (minute == 5) settled = mb;
                if (minute % 10 == 0) output.WriteLine($"minute {minute}: {mb:F0} MB, file {new FileInfo(path).Length >> 20} MB");
            }
            writer.Finish();
            double growth = PrivateMb() - settled;
            output.WriteLine($"growth after warm-up: {growth:F1} MB");
            Assert.True(growth < 10, $"memory grew {growth:F1} MB over two hours (NFR-09a allows 10)");
            foreach (ID3D11RenderTargetView v in views) v.Dispose();
            foreach (ID3D11Texture2D t in textures) t.Dispose();
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static double PrivateMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var self = Process.GetCurrentProcess();
        return self.PrivateMemorySize64 / (1024.0 * 1024.0);
    }
}
