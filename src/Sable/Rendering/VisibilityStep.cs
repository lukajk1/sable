using Sable.Paint;

namespace Sable.Rendering;

/// <summary>
/// An undoable change to what's hidden (H, Shift+H, Alt+H, the object list's checkboxes): which objects and which
/// submeshes were hidden before and after. Part meshes whose submeshes change are rebuilt.
/// </summary>
public sealed class VisibilityStep : IUndoStep
{
    public sealed record State(bool[] Objects, bool[][] Components);

    private readonly GpuModel model;
    private readonly State before, after;

    private VisibilityStep(GpuModel model, State before, State after)
    {
        this.model = model;
        this.before = before;
        this.after = after;
    }

    public long Bytes => (before.Objects.Length + before.Components.Sum(c => c.Length)) * 2L;

    public static State Snapshot(GpuModel model) => new(
        model.Source.Objects.Select(o => o.Hidden).ToArray(),
        model.Source.Parts.Select(p => (bool[])p.ComponentHidden.Clone()).ToArray());

    /// <summary>The step from <paramref name="before"/> to now, or null if nothing changed.</summary>
    public static VisibilityStep? IfChanged(GpuModel model, State before)
    {
        var now = Snapshot(model);
        bool changed = !now.Objects.SequenceEqual(before.Objects)
                       || now.Components.Where((c, i) => !c.SequenceEqual(before.Components[i])).Any();
        return changed ? new VisibilityStep(model, before, now) : null;
    }

    public void Undo() => Apply(before);
    public void Redo() => Apply(after);

    private void Apply(State state)
    {
        var objects = model.Source.Objects;
        for (int i = 0; i < objects.Count; i++) objects[i].Hidden = state.Objects[i];
        var parts = model.Source.Parts;
        for (int p = 0; p < parts.Count; p++)
        {
            if (parts[p].ComponentHidden.SequenceEqual(state.Components[p])) continue;
            Array.Copy(state.Components[p], parts[p].ComponentHidden, state.Components[p].Length);
            model.RebuildPart(p);
        }
    }
}
