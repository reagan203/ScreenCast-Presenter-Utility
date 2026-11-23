using System.Diagnostics;

namespace ScreenCastPresenter.Output;

/// <summary>Time-based value animation on the UI thread, used for all fades through black.</summary>
internal static class Fader
{
    /// <summary>Smoothstep: gentle start and end, so the fade has no visible "kick".</summary>
    public static double Ease(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    /// <summary>
    /// Animates from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>,
    /// calling <paramref name="apply"/> on each tick. Progress follows the clock, not the tick count,
    /// so a slow tick never stretches the fade. The last call is always exactly <paramref name="to"/>.
    /// </summary>
    public static Task RunAsync(double from, double to, TimeSpan duration, Action<double> apply)
    {
        if (duration <= TimeSpan.Zero || from == to)
        {
            apply(to);
            return Task.CompletedTask;
        }

        var done = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        var timer = new System.Windows.Forms.Timer { Interval = 8 }; // ~one tick per display refresh in practice

        timer.Tick += (_, _) =>
        {
            double t = Math.Min(1, clock.Elapsed / duration);
            try
            {
                apply(from + (to - from) * Ease(t));
            }
            catch (Exception ex)
            {
                timer.Dispose();
                done.TrySetException(ex);
                return;
            }
            if (t >= 1)
            {
                timer.Dispose();
                done.TrySetResult();
            }
        };

        apply(from);
        timer.Start();
        return done.Task;
    }
}
