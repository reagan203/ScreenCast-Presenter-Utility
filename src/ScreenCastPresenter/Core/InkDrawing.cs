using ScreenCastPresenter.Capture;

namespace ScreenCastPresenter.Core;

/// <summary>
/// The drawing on the TV (FR-71): its lines, in order, with undo. Each line goes to the engine as
/// it is drawn, and the whole drawing again to a new engine (after freezing or a fallback), so the
/// TV never loses it while drawing is on.
/// </summary>
internal sealed class InkDrawing
{
    /// <summary>Plenty for a session; past it the oldest line goes, so memory stays bounded.</summary>
    public const int MaxStrokes = 500;

    private readonly List<InkStroke> _strokes = [];
    private int _nextId = 1;

    public IReadOnlyList<InkStroke> Strokes => _strokes;

    /// <summary>A new, empty mark (with its words, for text); the oldest is dropped past <see cref="MaxStrokes"/>.</summary>
    public InkStroke Begin(InkTool tool, Color colour, float width, string? text = null)
    {
        var stroke = new InkStroke(_nextId++, tool, colour.ToArgb(), width) { Text = tool == InkTool.Text ? text : null };
        _strokes.Add(stroke);
        if (_strokes.Count > MaxStrokes) _strokes.RemoveAt(0);
        return stroke;
    }

    /// <summary>Takes away the last line; its id (to remove it from the TV), or null if there was none.</summary>
    public int? Undo()
    {
        if (_strokes.Count == 0) return null;
        int id = _strokes[^1].Id;
        _strokes.RemoveAt(_strokes.Count - 1);
        return id;
    }

    /// <summary>Takes one mark away (a shape that was only clicked, not dragged); true if it was there.</summary>
    public bool Remove(int id) => _strokes.RemoveAll(s => s.Id == id) > 0;

    public void Clear() => _strokes.Clear();

    /// <summary>Sends the whole drawing to <paramref name="engine"/>, replacing whatever it had.</summary>
    public void Replay(IEffectsEngine engine)
    {
        engine.InkClear();
        foreach (InkStroke stroke in _strokes)
        {
            engine.InkStart(stroke);
            if (stroke.Points.Count > 0) engine.InkAdd(stroke.Id, stroke.Points);
        }
    }
}
