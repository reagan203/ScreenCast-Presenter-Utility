using ScreenCastPresenter.Interop;

namespace ScreenCastPresenter.Core;

/// <summary>
/// Where the output goes (FR-10–FR-12): fullscreen on a screen (a TV, a second monitor, or a
/// virtual "Zoom screen"), or a floating window on the presenter's screen that Zoom can share
/// by name.
/// </summary>
/// <param name="Monitor">The screen it is on (for a window: the screen it opens on).</param>
/// <param name="TopMost">Fullscreen only: stay above everything (off when it's the presenter's only screen).</param>
/// <param name="Window">A floating window's bounds; null for fullscreen.</param>
internal sealed record OutputPlacement(DisplayMonitor Monitor, bool TopMost, Rectangle? Window = null)
{
    public bool IsFloating => Window is not null;

    /// <summary>Smallest floating window: still readable when shared.</summary>
    public static readonly Size MinimumWindow = new(320, 180);

    /// <summary>
    /// A first floating window: 16:9, about 40% of the screen's width, in the bottom-right corner
    /// of the work area, clear of the taskbar.
    /// </summary>
    public static Rectangle DefaultWindow(DisplayMonitor screen)
    {
        Rectangle area = screen.WorkArea;
        int width = Math.Max(MinimumWindow.Width, area.Width * 2 / 5);
        int height = Math.Max(MinimumWindow.Height, width * 9 / 16 + (int)(32 * screen.Scale)); // + title bar
        int margin = (int)(24 * screen.Scale);
        width = Math.Min(width, area.Width - 2 * margin);
        height = Math.Min(height, area.Height - 2 * margin);
        return new Rectangle(area.Right - width - margin, area.Bottom - height - margin, width, height);
    }

    /// <summary>
    /// A saved window position, if it is still usable: big enough, and with its title bar on a
    /// connected screen so it can be dragged. Otherwise null (use <see cref="DefaultWindow"/>).
    /// </summary>
    public static Rectangle? Restore(Rectangle? saved, IReadOnlyList<DisplayMonitor> monitors)
    {
        if (saved is not { } r || r.Width < MinimumWindow.Width || r.Height < MinimumWindow.Height) return null;
        // The middle of the title bar must be on some screen's work area.
        var grip = new Point(r.Left + r.Width / 2, r.Top + 12);
        bool reachable = monitors.Any(m => m.WorkArea.Contains(grip));
        if (!reachable) return null;
        DisplayMonitor host = monitors.First(m => m.WorkArea.Contains(grip));
        // Never larger than the screen it's on.
        if (r.Width > host.WorkArea.Width || r.Height > host.WorkArea.Height) return null;
        return r;
    }
}

/// <summary>One choice in the output selector: a screen (fullscreen), or the floating window.</summary>
internal sealed record OutputOption(DisplayMonitor? Screen)
{
    public static readonly OutputOption Window = new((DisplayMonitor?)null);

    public bool IsWindow => Screen is null;

    /// <summary>Every choice in selector order: each screen, then the floating window.</summary>
    public static List<OutputOption> All(IReadOnlyList<DisplayMonitor> monitors) =>
        [.. monitors.Select(m => new OutputOption(m)), Window];

    /// <summary>FR-21 Ctrl+Alt+O: the choice after <paramref name="current"/>, wrapping around.</summary>
    public static OutputOption Next(IReadOnlyList<DisplayMonitor> monitors, OutputOption? current)
    {
        List<OutputOption> all = All(monitors);
        int index = current is null ? -1 : all.FindIndex(o => o.IsWindow ? current.IsWindow : !current.IsWindow && o.Screen!.IsSameScreen(current.Screen!));
        return all[(index + 1) % all.Count];
    }
}
