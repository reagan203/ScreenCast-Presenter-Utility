using System.Diagnostics;
using Windows.Media.Capture.Frames;

namespace ScreenCastPresenter.Interop;

/// <summary>
/// The cameras that can be shown (FR-68): webcams and document cameras with a colour picture.
/// Infrared and depth cameras (Windows Hello) are left out: they have no picture to show.
/// </summary>
internal static class Cameras
{
    private static (DateTime At, List<CameraDevice> List)? _cached;

    /// <summary>
    /// The cameras connected now, by name. Asking Windows takes a moment, so the answer is kept for
    /// a few seconds (the picker opens fast). Never throws: no cameras is an empty list.
    /// </summary>
    public static async Task<List<CameraDevice>> ListAsync(bool fresh = false)
    {
        if (!fresh && _cached is { } c && DateTime.UtcNow - c.At < TimeSpan.FromSeconds(10)) return c.List;
        var cameras = new List<CameraDevice>();
        try
        {
            IReadOnlyList<MediaFrameSourceGroup> groups = await MediaFrameSourceGroup.FindAllAsync();
            foreach (MediaFrameSourceGroup group in groups)
            {
                if (!group.SourceInfos.Any(i => i.SourceKind == MediaFrameSourceKind.Color)) continue;
                if (cameras.Any(x => x.Id == group.Id)) continue;
                cameras.Add(new CameraDevice(group.Id, group.DisplayName ?? ""));
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex); // no camera stack (Windows N without media features): no cameras
        }
        _cached = (DateTime.UtcNow, cameras);
        return cameras;
    }

    /// <summary>The camera whose name contains <paramref name="query"/> (like the search box: any case, accents ignored).</summary>
    public static CameraDevice? Find(IReadOnlyList<CameraDevice> cameras, string query)
    {
        string folded = Core.PickerLayout.Fold(query).Trim();
        if (folded.Length == 0) return null;
        return cameras.FirstOrDefault(c => Core.PickerLayout.Fold(c.Name) == folded)
            ?? cameras.FirstOrDefault(c => Core.PickerLayout.Fold(c.Name).Contains(folded, StringComparison.Ordinal));
    }
}
