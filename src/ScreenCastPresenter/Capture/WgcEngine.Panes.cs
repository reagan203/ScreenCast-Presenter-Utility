using System.Text.RegularExpressions;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Rectangle = System.Drawing.Rectangle;
using Size = System.Drawing.Size;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// The pictures besides the main one: split-screen panes (FR-104) and the small picture (FR-72).
/// Each has its own hidden parts (FR-74, sent by the app for that source) and its own automatic
/// hiding (FR-88, FR-98, FR-100), so whatever is on the TV is covered the same way. A picture added
/// while showing stays black until its first look is done.
/// </summary>
internal sealed partial class WgcEngine
{
    private (SplitLayout Layout, List<SourceWindow> Others)? _splitWanted; // under _sync
    private bool _splitChanged;
    private readonly Dictionary<string, (List<CropRegion> Regions, RedactStyle Style)> _hiddenFor = new(StringComparer.Ordinal); // under _sync

    // Render thread only.
    private readonly List<Pane> _panes = [];
    private SplitLayout _splitLayout;
    private AutoCover? _insetCover;
    private SourceWindow? _insetCoverSource;
    private SensitiveKinds _paneKinds;
    private Regex? _panePattern;
    private RedactStyle _paneStyle;
    private readonly List<(CropRegion Region, RedactStyle Style)> _frameCovers = [];

    private sealed class Pane(SourceWindow source, InsetFeed feed) : IDisposable
    {
        public SourceWindow Source { get; } = source;
        public InsetFeed Feed { get; } = feed;
        public AutoCover? Cover { get; set; }
        public bool Fresh { get; set; }

        public void Dispose()
        {
            Cover?.Dispose();
            Feed.Dispose();
        }
    }

    /// <summary>Split screen (FR-104): the main picture and up to three others; an empty list goes back to one picture.</summary>
    public void Split(SplitLayout layout, IReadOnlyList<SourceWindow> others) => Update(() =>
    {
        _splitWanted = others.Count == 0 ? null : (layout, [.. others.Take(SplitScreen.MaxOthers)]);
        _splitChanged = true;
    });

    /// <summary>Hidden parts (FR-74) of another picture (a pane or the small picture), by its source; an empty list uncovers it.</summary>
    public void HiddenFor(SourceWindow source, IReadOnlyList<CropRegion> regions, RedactStyle style) => Update(() =>
    {
        string key = CaptureProtocol.CoverKey(source);
        if (regions.Count == 0) _hiddenFor.Remove(key);
        else _hiddenFor[key] = ([.. regions.Take(Redactions.MaxPerSource)], style);
    });

    /// <summary>How many pictures split screen shows now, with the main one (render thread; tests).</summary>
    internal int PanesShown => _panes.Count;

    /// <summary>Starts, changes or stops split-screen panes as asked, and takes their newest frames. True when there's something new to draw.</summary>
    private bool PullSplit()
    {
        (SplitLayout Layout, List<SourceWindow> Others)? wanted;
        bool changed, frozen;
        lock (_sync)
        {
            wanted = _splitWanted;
            changed = _splitChanged;
            _splitChanged = false;
            frozen = _frozen;
        }
        bool fresh = false;
        if (changed)
        {
            if (wanted is { } w) _splitLayout = w.Layout;
            List<SourceWindow> keep = wanted?.Others ?? [];
            for (int i = _panes.Count - 1; i >= 0; i--)
            {
                if (keep.Any(k => k.IsSameSource(_panes[i].Source))) continue;
                _panes[i].Dispose();
                _panes.RemoveAt(i);
            }
            var ordered = new List<Pane>(keep.Count);
            foreach (SourceWindow source in keep)
            {
                Pane? pane = _panes.FirstOrDefault(p => p.Source.IsSameSource(source));
                if (pane is null)
                {
                    try
                    {
                        _winrtDevice ??= Direct3D.CreateWinRTDevice(_device!);
                        pane = new Pane(source, InsetFeed.Start(source, _device!, _winrtDevice, _dc!, () => _wake.Set())) { Cover = Recover(null, source) };
                    }
                    catch (InvalidOperationException ex)
                    {
                        ReportInset(ex.Message);
                        continue;
                    }
                }
                ordered.Add(pane);
            }
            _panes.Clear();
            _panes.AddRange(ordered);
            fresh = true;
        }
        for (int i = _panes.Count - 1; i >= 0; i--)
        {
            Pane pane = _panes[i];
            pane.Fresh = pane.Feed.Failure is null && pane.Feed.Pull(_context!, _winrtDevice, frozen);
            fresh |= pane.Fresh;
            if (pane.Feed.Failure is not { } reason) continue;
            pane.Dispose();
            _panes.RemoveAt(i);
            lock (_sync)
            {
                if (_splitWanted is { } now)
                {
                    now.Others.RemoveAll(o => o.IsSameSource(pane.Source));
                    if (now.Others.Count == 0) _splitWanted = null;
                }
            }
            ReportInset(reason);
            fresh = true;
        }
        return fresh;
    }

    /// <summary>Automatic hiding changed: every other picture follows the main one.</summary>
    private void ConfigurePaneCovers(SensitiveKinds kinds, Regex? pattern, RedactStyle style)
    {
        _paneKinds = kinds;
        _panePattern = pattern;
        _paneStyle = style;
        foreach (Pane pane in _panes) pane.Cover = Recover(pane.Cover, pane.Source);
        if (_insetCoverSource is { } inset) _insetCover = Recover(_insetCover, inset);
    }

    /// <summary>A picture's automatic hiding made to match what's chosen: started, changed or stopped.</summary>
    private AutoCover? Recover(AutoCover? cover, SourceWindow source)
    {
        if (_paneKinds == SensitiveKinds.None)
        {
            cover?.Dispose();
            return null;
        }
        cover ??= new AutoCover(source, () => Update(static () => { }));
        cover.Configure(_paneKinds, _panePattern, _paneStyle);
        return cover;
    }

    /// <summary>On the render thread: the other pictures' automatic hiding reads them when due.</summary>
    private void ScanPanes()
    {
        if (_dc is null || _target is null || IsFrozen) return;
        // The small picture's own automatic hiding follows the small picture.
        if (_inset is null || _insetSource is null)
        {
            _insetCover?.Dispose();
            _insetCover = null;
            _insetCoverSource = null;
        }
        else if (!_insetSource.IsSameSource(_insetCoverSource))
        {
            _insetCover?.Dispose();
            _insetCover = null;
            _insetCoverSource = _insetSource;
            _insetCover = Recover(null, _insetSource);
        }
        if (_insetCover is { } insetCover && _inset?.Bitmap is { } insetBitmap) insetCover.MaybeScan(_dc, insetBitmap, _inset.WholeArea, true, _target);
        foreach (Pane pane in _panes)
            if (pane.Cover is { } cover && pane.Feed.Bitmap is { } bitmap) cover.MaybeScan(_dc, bitmap, pane.Feed.WholeArea, pane.Fresh, _target);
    }

    /// <summary>Every other picture has had its first look (for the first frame of something new).</summary>
    private bool PanesScanned() => (_insetCover?.FirstScanDone ?? true) && _panes.All(p => p.Cover?.FirstScanDone ?? true);

    /// <summary>Where the main picture goes: the whole canvas, or split screen's first place.</summary>
    private List<System.Drawing.RectangleF>? SplitCells(Size canvas) =>
        _panes.Count == 0 ? null : SplitScreen.Cells(new System.Drawing.SizeF(canvas.Width, canvas.Height), _splitLayout, _panes.Count + 1);

    /// <summary>The panes in their places, at the picture's brightness, each with its covers.</summary>
    private void DrawPanes(List<System.Drawing.RectangleF> cells, double opacity)
    {
        for (int i = 0; i < _panes.Count && i + 1 < cells.Count; i++)
        {
            Pane pane = _panes[i];
            if (pane.Feed.Bitmap is not { } bitmap || !(pane.Cover?.FirstScanDone ?? true)) continue; // not looked at yet: stays black
            Rectangle part = pane.Feed.ContentArea;
            System.Drawing.RectangleF dest = SplitScreen.Fit(part.Size, cells[i + 1]);
            if (dest.IsEmpty) continue;
            DrawPicture(bitmap, part, Rectangle.Round(dest), opacity, _nightNow); // the night filter on the pictures beside it too
            DrawCoversOn(pane.Source, pane.Cover, bitmap, pane.Feed.WholeArea, part, dest, opacity);
        }
    }

    /// <summary>Another picture's hidden parts and what its automatic hiding found, over it.</summary>
    /// <param name="area">All of the other picture (hidden parts are parts of it).</param>
    /// <param name="shown">The part of it shown (a screen's taskbar left out).</param>
    private void DrawCoversOn(SourceWindow source, AutoCover? cover, ID2D1Bitmap1 bitmap, Rectangle area, Rectangle shown, System.Drawing.RectangleF dest, double opacity)
    {
        _frameCovers.Clear();
        lock (_sync)
        {
            if (_hiddenFor.TryGetValue(CaptureProtocol.CoverKey(source), out (List<CropRegion> Regions, RedactStyle Style) hidden))
                foreach (CropRegion r in hidden.Regions) _frameCovers.Add((r, hidden.Style));
        }
        if (cover is not null) _frameCovers.AddRange(cover.Covers());
        if (_frameCovers.Count == 0) return;
        _effects.RenderCovers(_dc!, _d2dFactory!, bitmap, area, shown, dest, _frameCovers, (float)opacity);
    }

    private void DisposePanes()
    {
        foreach (Pane pane in _panes) pane.Dispose();
        _panes.Clear();
        _insetCover?.Dispose();
        _insetCover = null;
        _insetCoverSource = null;
    }
}
