using ScreenCastPresenter.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace ScreenCastPresenter.Interop;

/// <summary>Finds the Open, Save and folder boxes among the windows on screen (FR-114).</summary>
internal static class FileBoxFinder
{
    /// <summary>Dialog boxes among <paramref name="visible"/> (front to back) that are file or folder boxes.</summary>
    public static List<VisibleWindow> Find(IReadOnlyList<VisibleWindow> visible)
    {
        var found = new List<VisibleWindow>();
        foreach (VisibleWindow w in visible)
        {
            var hwnd = (HWND)w.Handle;
            string cls = DesktopIcons.ClassOf(hwnd);
            if (cls != FileBoxes.DialogClass) continue;
            if (FileBoxes.IsFileBox(cls, ChildClasses(hwnd))) found.Add(w);
        }
        return found;
    }

    /// <summary>The classes of a window's descendants, at most a few hundred (a dialog has a few dozen).</summary>
    internal static List<string> ChildClasses(HWND hwnd)
    {
        var classes = new List<string>();
        PInvoke.EnumChildWindows(hwnd, (child, _) =>
        {
            classes.Add(DesktopIcons.ClassOf(child));
            return classes.Count < 400 && !FileBoxes.FileViewClasses.Contains(classes[^1]); // found one: stop looking
        }, default);
        return classes;
    }
}
