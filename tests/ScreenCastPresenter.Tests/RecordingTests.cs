using System.Drawing;
using ScreenCastPresenter.Recording;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Tests;

public class AudioMixerTests
{
    private static float[] Frames(int frames, float value)
    {
        float[] samples = new float[frames * AudioMixer.Channels];
        Array.Fill(samples, value);
        return samples;
    }

    [Fact]
    public void Sources_are_summed_and_missing_audio_is_silence()
    {
        var mixer = new AudioMixer(2);
        mixer.Write(0, [0.25f, 0.25f, 0.5f, 0.5f]);
        mixer.Write(1, [0.25f, -0.25f]);
        float[] output = new float[6];
        mixer.Read(3, output);
        Assert.Equal([0.5f, 0f, 0.5f, 0.5f, 0f, 0f], output);
    }

    [Fact]
    public void Gain_is_applied_per_source()
    {
        var mixer = new AudioMixer(2, [1f, 0.5f]);
        mixer.Write(0, [0.2f, 0.2f]);
        mixer.Write(1, [0.4f, 0.4f]);
        float[] output = new float[2];
        mixer.Read(1, output);
        Assert.Equal(0.4f, output[0], 5);
    }

    [Fact]
    public void A_source_that_runs_ahead_is_trimmed_back_so_audio_never_drifts()
    {
        var mixer = new AudioMixer(1);
        mixer.Write(0, Frames(AudioMixer.SampleRate, 0f)); // a whole second queued: the device clock ran fast
        mixer.Write(0, Frames(2, 0.5f)); // the newest audio must survive the trim
        Assert.Equal(AudioMixer.HardLimitFrames, mixer.Buffered(0));

        float[] output = new float[2];
        mixer.Read(1, output);
        Assert.Equal(AudioMixer.TrimToFrames - 1, mixer.Buffered(0));

        float[] rest = new float[AudioMixer.TrimToFrames * AudioMixer.Channels];
        mixer.Read(AudioMixer.TrimToFrames - 1, rest);
        Assert.Equal(0.5f, rest[(AudioMixer.TrimToFrames - 1) * AudioMixer.Channels - 1]);
    }

    [Fact]
    public void A_small_lead_is_left_alone()
    {
        var mixer = new AudioMixer(1);
        mixer.Write(0, Frames(AudioMixer.MaxBufferedFrames, 0.1f)); // exactly at the limit: no trim
        mixer.Read(10, new float[20]);
        Assert.Equal(AudioMixer.MaxBufferedFrames - 10, mixer.Buffered(0));
    }

    [Fact]
    public void A_source_that_ran_dry_waits_for_a_cushion_instead_of_clicking()
    {
        var mixer = new AudioMixer(1);
        mixer.Write(0, Frames(2, 0.5f));
        float[] output = new float[8];
        mixer.Read(4, output);
        Assert.Equal([0.5f, 0.5f, 0.5f, 0.5f, 0f, 0f, 0f, 0f], output); // played what it had, then ran dry

        mixer.Write(0, Frames(10, 0.5f)); // a trickle: not enough to resume
        mixer.Read(4, output);
        Assert.All(output, v => Assert.Equal(0f, v));
        Assert.Equal(10, mixer.Buffered(0));

        mixer.Write(0, Frames(AudioMixer.RefillFrames, 0.5f)); // cushion rebuilt: plays again
        mixer.Read(4, output);
        Assert.All(output, v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public void Nothing_queues_beyond_a_second_while_nobody_reads()
    {
        var mixer = new AudioMixer(1);
        for (int i = 0; i < 5; i++) mixer.Write(0, Frames(AudioMixer.SampleRate, 0f));
        Assert.Equal(AudioMixer.HardLimitFrames, mixer.Buffered(0));
    }

    [Fact]
    public void Odd_sample_counts_never_swap_left_and_right()
    {
        var mixer = new AudioMixer(1);
        float[] stereo = new float[(AudioMixer.HardLimitFrames + 3) * 2];
        for (int i = 0; i < stereo.Length; i += 2)
        {
            stereo[i] = -1f; // left
            stereo[i + 1] = 1f; // right
        }
        mixer.Write(0, stereo);
        float[] output = new float[4];
        mixer.Read(2, output);
        Assert.Equal([-1f, 1f, -1f, 1f], output);
    }

    [Fact]
    public void Discard_drops_buffered_audio_while_paused()
    {
        var mixer = new AudioMixer(1);
        mixer.Write(0, [1f, 1f]);
        mixer.Discard();
        float[] output = new float[2];
        mixer.Read(1, output);
        Assert.Equal([0f, 0f], output);
    }

    [Fact]
    public void Pcm_conversion_clips_instead_of_wrapping()
    {
        short[] pcm = new short[4];
        AudioMixer.ToPcm16([2f, -2f, 0f, 0.5f], pcm);
        Assert.Equal([short.MaxValue, -short.MaxValue, 0, 16384], pcm);
    }

    [Fact]
    public void Frames_follow_the_wall_clock()
    {
        Assert.Equal(72_000, AudioMixer.FramesFor(TimeSpan.FromSeconds(1.5)));
        Assert.Equal(0, AudioMixer.FramesFor(TimeSpan.FromMilliseconds(-100))); // the audio lag at the very start
    }

    /// <summary>
    /// The drift of eight hours at 50 ppm (1.44 s), compressed into five minutes at 5000 ppm: the
    /// backlog (= how far sound lags the picture) stays bounded instead of growing.
    /// </summary>
    [Fact]
    public void Clock_drift_of_a_long_webinar_stays_bounded()
    {
        var mixer = new AudioMixer(1);
        const int readFrames = AudioMixer.SampleRate / 25; // 40 ms reads
        float[] output = new float[readFrames * AudioMixer.Channels];
        double owed = 0;
        int maxBuffered = 0;
        float[] delivery = new float[readFrames * 2 * AudioMixer.Channels];
        for (long tick = 0; tick < 5 * 60 * 25; tick++)
        {
            owed += readFrames * 1.005; // the device delivers faster than real time
            int deliver = (int)owed;
            owed -= deliver;
            mixer.Write(0, delivery.AsSpan(0, deliver * AudioMixer.Channels));
            mixer.Read(readFrames, output);
            maxBuffered = Math.Max(maxBuffered, mixer.Buffered(0));
        }
        Assert.True(maxBuffered <= AudioMixer.MaxBufferedFrames + readFrames, $"backlog reached {maxBuffered} frames");
    }
}

public class Mp4WriterTests
{
    [Theory]
    [InlineData(1920, 1080, 1920, 1080)]
    [InlineData(1921, 1081, 1920, 1080)]
    [InlineData(5120, 2880, 3840, 2160)]
    [InlineData(1, 1, 2, 2)]
    public void Sizes_fit_and_are_even(int w, int h, int ew, int eh) =>
        Assert.Equal(new Size(ew, eh), Mp4Writer.FitSize(new Size(w, h), new Size(3840, 2160)));

    [Fact]
    public void Bitrate_is_sensible_for_screen_content()
    {
        Assert.InRange(Mp4Writer.VideoBitrate(1920, 1080), 5_000_000, 8_000_000);
        Assert.Equal(1_000_000, Mp4Writer.VideoBitrate(64, 64));
        Assert.Equal(24_000_000, Mp4Writer.VideoBitrate(7680, 4320));
    }

    /// <summary>
    /// Encodes three seconds of changing colour plus a tone on the real GPU and reads the file
    /// back: proves the hardware path, the fragmented container and audio/video sync end to end.
    /// </summary>
    [Fact]
    public void Synthetic_recording_plays_back_with_video_and_audio()
    {
        string path = Path.Combine(Path.GetTempPath(), $"scp-test-{Guid.NewGuid():N}.mp4");
        try
        {
            const int seconds = 3;
            using (ID3D11Device device = Mp4Writer.CreateDevice())
            using (var writer = new Mp4Writer(path, 640, 360, device, withAudio: true))
            {
                var textures = new ID3D11Texture2D[4];
                var views = new ID3D11RenderTargetView[4];
                for (int i = 0; i < textures.Length; i++)
                {
                    textures[i] = device.CreateTexture2D(new Texture2DDescription(Format.B8G8R8A8_UNorm, 640, 360, 1, 1, BindFlags.RenderTarget | BindFlags.ShaderResource));
                    views[i] = device.CreateRenderTargetView(textures[i]);
                }
                short[] pcm = new short[AudioMixer.SampleRate / Mp4Writer.FrameRate * AudioMixer.Channels];
                for (int f = 0; f < seconds * Mp4Writer.FrameRate; f++)
                {
                    while (writer.VideoInFlight >= textures.Length - 1) Thread.Sleep(1); // the writer's contract: never reuse a texture still being encoded
                    int slot = f % textures.Length;
                    float t = f / (float)(seconds * Mp4Writer.FrameRate);
                    device.ImmediateContext.ClearRenderTargetView(views[slot], new Color4(t, 1 - t, 0.5f, 1));
                    writer.WriteVideo(textures[slot], f * Mp4Writer.FrameDuration, Mp4Writer.FrameDuration);

                    for (int i = 0; i < pcm.Length / 2; i++)
                    {
                        short v = (short)(Math.Sin(2 * Math.PI * 440 * (f * pcm.Length / 2 + i) / AudioMixer.SampleRate) * 8000);
                        pcm[2 * i] = pcm[2 * i + 1] = v;
                    }
                    writer.WriteAudio(pcm, f * Mp4Writer.FrameDuration);
                }
                writer.Finish();
                foreach (ID3D11RenderTargetView v in views) v.Dispose();
                foreach (ID3D11Texture2D tex in textures) tex.Dispose();
            }

            Mp4Info info = Mp4Info.Read(path);
            Assert.True(info.HasVideo);
            Assert.True(info.HasAudio);
            Assert.Equal(640, info.Width);
            Assert.Equal(360, info.Height);
            Assert.InRange(info.Duration.TotalSeconds, seconds - 0.2, seconds + 0.2);
            Assert.InRange(Mp4Info.CountVideoFrames(path), seconds * Mp4Writer.FrameRate - 2, seconds * Mp4Writer.FrameRate);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
