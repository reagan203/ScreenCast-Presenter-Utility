namespace ScreenCastPresenter.Core;

/// <summary>What the presenter can do to a video being shown (FR-116).</summary>
internal enum VideoCommand { Play, Pause, Seek, Restart }

/// <summary>
/// Videos as sources (FR-116): an MP4 (or another file Windows can play) shown on the TV like a
/// picture, with its sound on this PC's speakers, played by Windows' own media player in the capture
/// helper. Freezing or blanking pauses it; showing it again plays on. Next and previous slide jump
/// ten seconds. In a playlist it plays to its end, then the next item shows. The picture goes
/// through automatic hiding like any other, so a video of a screen recording is covered too.
/// </summary>
internal static class VideoSource
{
    public static readonly string[] Extensions = [".mp4", ".m4v", ".mov", ".wmv", ".avi", ".mkv", ".webm"];

    public const string Filter = "Videos (*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm)|*.mp4;*.m4v;*.mov;*.wmv;*.avi;*.mkv;*.webm";

    /// <summary>Next and previous slide jump this far.</summary>
    public const double JumpSeconds = 10;

    /// <summary>The app name a video is listed under.</summary>
    public const string App = "Video";

    public static bool IsVideo(string? path) => path is { Length: > 0 } && Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Why <paramref name="path"/> can't be shown, or null when it can (whether Windows can play it is found out when it starts).</summary>
    public static string? Problem(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "Choose a video.";
        if (!IsVideo(path)) return "Choose an MP4, MOV, WMV, AVI, MKV or WebM video.";
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return $"“{Path.GetFileName(path)}” can't be found.";
            if (info.Length == 0) return $"“{info.Name}” is empty.";
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return "That video can't be opened.";
        }
    }

    /// <summary>A video's name for messages and the tray: its file name without the extension.</summary>
    public static string Name(string path) => Path.GetFileNameWithoutExtension(path) is { Length: > 0 } name ? name : path;

    /// <summary>"1:05 of 3:20" for the now-showing bar.</summary>
    public static string Position(TimeSpan at, TimeSpan length) =>
        length > TimeSpan.Zero ? $"{BlankCountdown.Clock(at)} of {BlankCountdown.Clock(length)}" : BlankCountdown.Clock(at);
}
