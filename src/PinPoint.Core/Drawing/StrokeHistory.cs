namespace PinPoint.Core.Drawing;

/// <summary>
/// The strokes drawn on a snip plus undo/redo. Erasing and "clear all" are undoable too, so wiping
/// marks by mistake costs one click to get back. UI-agnostic: <typeparamref name="T"/> is whatever
/// the view uses to draw a stroke.
/// </summary>
public sealed class StrokeHistory<T>
    where T : class
{
    private readonly List<T> _strokes = [];
    private readonly Stack<Step> _undo = new();
    private readonly Stack<Step> _redo = new();

    /// <summary>Raised after any change so the view can redraw.</summary>
    public event EventHandler? Changed;

    public IReadOnlyList<T> Strokes => _strokes;

    public bool CanUndo => _undo.Count > 0;

    public bool CanRedo => _redo.Count > 0;

    public void Add(T stroke) => Do(new Step(StepKind.Add, [(stroke, _strokes.Count)]));

    /// <summary>Replaces everything with <paramref name="strokes"/> as the starting point (no undo history).</summary>
    public void Reset(IEnumerable<T> strokes)
    {
        _strokes.Clear();
        _strokes.AddRange(strokes);
        _undo.Clear();
        _redo.Clear();
        OnChanged();
    }

    /// <summary>Removes the given strokes as one undoable step (e.g. everything one eraser drag touched).</summary>
    public void Remove(IEnumerable<T> strokes)
    {
        var removed = strokes.Distinct()
            .Select(s => (Stroke: s, Index: _strokes.IndexOf(s)))
            .Where(p => p.Index >= 0)
            .OrderBy(p => p.Index)
            .ToList();
        if (removed.Count > 0) Do(new Step(StepKind.Remove, removed));
    }

    /// <summary>Removes every stroke as one undoable step. Does nothing if there's nothing to clear.</summary>
    public void Clear()
    {
        if (_strokes.Count > 0) Do(new Step(StepKind.Remove, _strokes.Select((s, i) => (s, i)).ToList()));
    }

    public void Undo()
    {
        if (!_undo.TryPop(out var step)) return;
        Revert(step);
        _redo.Push(step);
        OnChanged();
    }

    public void Redo()
    {
        if (!_redo.TryPop(out var step)) return;
        Apply(step);
        _undo.Push(step);
        OnChanged();
    }

    private void Do(Step step)
    {
        Apply(step);
        _undo.Push(step);
        _redo.Clear();
        OnChanged();
    }

    private void Apply(Step step)
    {
        if (step.Kind == StepKind.Add)
        {
            foreach (var (stroke, _) in step.Strokes) _strokes.Add(stroke);
        }
        else
        {
            foreach (var (stroke, _) in step.Strokes) _strokes.Remove(stroke);
        }
    }

    private void Revert(Step step)
    {
        if (step.Kind == StepKind.Add)
        {
            foreach (var (stroke, _) in step.Strokes) _strokes.Remove(stroke);
        }
        else
        {
            // Put strokes back where they were (ascending order keeps every recorded index valid).
            foreach (var (stroke, index) in step.Strokes) _strokes.Insert(Math.Min(index, _strokes.Count), stroke);
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private enum StepKind
    {
        Add,
        Remove,
    }

    /// <summary>One undoable step and the strokes it touched, with their positions in the list.</summary>
    private sealed record Step(StepKind Kind, List<(T Stroke, int Index)> Strokes);
}
