using System.Text.RegularExpressions;
using ScreenCastPresenter.Core;
using ScreenCastPresenter.Interop;
using Vortice.Direct2D1;
using Rectangle = System.Drawing.Rectangle;

namespace ScreenCastPresenter.Capture;

/// <summary>
/// Automatic hiding for one picture (the main one, a small one, a split-screen pane, a recording):
/// text read from the picture (FR-88, FR-92), password boxes Windows knows about (FR-98) and faces
/// (FR-100), together. Each part runs only while chosen. What to cover is kept as parts of the
/// picture's content, with how each looks (faces always blurred).
/// </summary>
internal sealed class AutoCover : IDisposable
{
    private readonly SourceWindow _source;
    private readonly Action _changed;
    private readonly Action<string>? _found;
    private readonly object _lock = new();
    private AutoRedactor? _text;
    private PasswordFieldFinder? _fields;
    private FaceFinder? _faces;
    private SensitiveKinds _kinds;
    private RedactStyle _style;
    private List<CropRegion> _textRegions = [], _faceRegions = [];
    private List<SensitiveMatch> _textFound = [];
    private string _lastWords = "";

    /// <param name="changed">What's covered changed (any thread).</param>
    /// <param name="found">Something new was covered, in words ("2 email addresses and a password box"); any thread.</param>
    public AutoCover(SourceWindow source, Action changed, Action<string>? found = null)
    {
        _source = source;
        _changed = changed;
        _found = found;
    }

    public SensitiveKinds Kinds => _kinds;

    /// <summary>What to look for, the words on the presenter's list, and how covered text looks. On the render thread.</summary>
    public void Configure(SensitiveKinds kinds, Regex? words, RedactStyle style)
    {
        _kinds = kinds;
        lock (_lock) _style = style;
        if ((kinds & SensitiveKinds.TextKinds) != 0)
        {
            _text ??= new AutoRedactor(kinds, OnText);
            _text.Kinds = kinds;
            _text.Words = words;
        }
        else if (_text is not null)
        {
            _text.Dispose();
            _text = null;
            lock (_lock)
            {
                _textRegions = [];
                _textFound = [];
            }
        }
        if (kinds.HasFlag(SensitiveKinds.PasswordFields) && !_source.IsOffScreen) _fields ??= new PasswordFieldFinder(_source, OnFields);
        else
        {
            _fields?.Dispose();
            _fields = null;
        }
        if (kinds.HasFlag(SensitiveKinds.Faces))
        {
            _faces ??= new FaceFinder(kinds.HasFlag(SensitiveKinds.KeepLargestFace), OnFaces);
            _faces.KeepLargest = kinds.HasFlag(SensitiveKinds.KeepLargestFace);
        }
        else if (_faces is not null)
        {
            _faces.Dispose();
            _faces = null;
            lock (_lock) _faceRegions = [];
        }
        _changed();
    }

    /// <summary>On the render thread, after a frame is drawn: text and faces are read when due (password boxes are asked on their own thread).</summary>
    public void MaybeScan(ID2D1DeviceContext dc, ID2D1Bitmap1 picture, Rectangle area, bool changed, ID2D1Image? restore)
    {
        _text?.MaybeScan(dc, picture, area, changed, restore);
        _faces?.MaybeScan(dc, picture, area, changed, restore);
    }

    /// <summary>Every part has had its first look: something new may be shown.</summary>
    public bool FirstScanDone => (_text?.FirstScanDone ?? true) && (_fields?.FirstScanDone ?? true) && (_faces?.FirstScanDone ?? true);

    /// <summary>Everything to cover now, with how each looks.</summary>
    public List<(CropRegion Region, RedactStyle Style)> Covers()
    {
        List<CropRegion> fields = _fields?.Regions ?? [];
        lock (_lock)
        {
            var covers = new List<(CropRegion, RedactStyle)>(_textRegions.Count + fields.Count + _faceRegions.Count);
            foreach (CropRegion r in _textRegions) covers.Add((r, _style));
            foreach (CropRegion r in fields) covers.Add((r, _style));
            foreach (CropRegion r in _faceRegions) covers.Add((r, RedactStyle.Blurred));
            return covers;
        }
    }

    private void OnText(List<CropRegion> regions, List<SensitiveMatch> found)
    {
        lock (_lock)
        {
            _textRegions = regions;
            _textFound = found;
        }
        Report();
        _changed();
    }

    private void OnFields()
    {
        Report();
        _changed();
    }

    private void OnFaces(List<CropRegion> regions)
    {
        lock (_lock) _faceRegions = regions;
        _changed(); // faces come and go all the time: covered, never announced
    }

    private void Report()
    {
        if (_found is null) return;
        int boxes = _fields?.Regions.Count ?? 0;
        string words;
        lock (_lock)
        {
            var all = new List<SensitiveMatch>(_textFound);
            for (int i = 0; i < boxes; i++) all.Add(new SensitiveMatch(SensitiveKinds.PasswordFields, RectangleF.Empty));
            words = all.Count == 0 ? "" : SensitiveText.Words(all);
            if (words == _lastWords) return;
            _lastWords = words;
        }
        if (words.Length > 0) _found(words);
    }

    /// <summary>On the render thread.</summary>
    public void Dispose()
    {
        _text?.Dispose();
        _fields?.Dispose();
        _faces?.Dispose();
        _text = null;
        _fields = null;
        _faces = null;
    }
}
