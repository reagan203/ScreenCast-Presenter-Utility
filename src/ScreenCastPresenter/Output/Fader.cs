using System.Diagnostics;

namespace ScreenCastPresenter.Output;

/// <summary>
/// Time-based value animation on the UI thread, used for all fades through black. All running
/// animations share one timer, which only ticks while something is animating: a fade allocates
/// no native resources and an idle app has no timer running at all.
/// </summary>
internal static class Fader
{
    private static readonly List<Animation> Running = [];
    private static System.Windows.Forms.Timer? _timer;

    /// <summary>Smoothstep: gentle start and end, so the fade has no visible "kick".</summary>
    public static double Ease(double t) => t <= 0 ? 0 : t >= 1 ? 1 : t * t * (3 - 2 * t);

    /// <summary>
    /// Animates from <paramref name="from"/> to <paramref name="to"/> over <paramref name="duration"/>,
    /// calling <paramref name="apply"/> on each tick. Progress follows the clock, not the tick count,
    /// so a slow tick never stretches the fade. The last call is always exactly <paramref name="to"/>.
    /// Must be called on the UI thread.
    /// </summary>
    public static Task RunAsync(double from, double to, TimeSpan duration, Action<double> apply)
    {
        if (duration <= TimeSpan.Zero || from == to)
        {
            apply(to);
            return Task.CompletedTask;
        }

        var animation = new Animation(from, to, duration, apply);
        apply(from);
        Running.Add(animation);
        EnsureTimer().Start();
        return animation.Done.Task;
    }

    private static System.Windows.Forms.Timer EnsureTimer()
    {
        if (_timer is not null) return _timer;
        _timer = new System.Windows.Forms.Timer { Interval = 8 }; // ~one tick per display refresh in practice
        _timer.Tick += (_, _) => Tick();
        return _timer;
    }

    private static void Tick()
    {
        // Snapshot: an apply callback may start another animation.
        foreach (Animation a in Running.ToArray())
        {
            if (a.Step()) Running.Remove(a);
        }
        if (Running.Count == 0) _timer?.Stop();
    }

    private sealed class Animation(double from, double to, TimeSpan duration, Action<double> apply)
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        public TaskCompletionSource Done { get; } = new();

        /// <summary>Advances the animation; true when finished.</summary>
        public bool Step()
        {
            double t = Math.Min(1, _clock.Elapsed / duration);
            try
            {
                apply(from + (to - from) * Ease(t));
            }
            catch (Exception ex)
            {
                Done.TrySetException(ex);
                return true;
            }
            if (t < 1) return false;
            Done.TrySetResult();
            return true;
        }
    }
}
