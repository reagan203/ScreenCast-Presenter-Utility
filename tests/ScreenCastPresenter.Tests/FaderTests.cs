using ScreenCastPresenter.Output;

namespace ScreenCastPresenter.Tests;

public class FaderTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(0.5, 0.5)]
    [InlineData(1, 1)]
    [InlineData(2, 1)]
    public void Ease_is_clamped_and_symmetric(double t, double expected) =>
        Assert.Equal(expected, Fader.Ease(t), 6);

    [Fact]
    public void Ease_is_monotonic()
    {
        double previous = 0;
        for (int i = 1; i <= 100; i++)
        {
            double v = Fader.Ease(i / 100.0);
            Assert.True(v >= previous);
            previous = v;
        }
    }

    [Fact]
    public async Task Zero_duration_applies_target_immediately()
    {
        var applied = new List<double>();
        await Fader.RunAsync(0, 1, TimeSpan.Zero, applied.Add);
        Assert.Equal([1.0], applied);
    }
}
