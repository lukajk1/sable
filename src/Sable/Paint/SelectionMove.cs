using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// Dragging the selected texels by whole texels. A move lifts them and leaves transparent texels behind (as in
/// Aseprite); a duplicate leaves the originals in place. Pixels pushed off the texture are dropped. One undo step.
/// </summary>
public sealed class SelectionMove
{
    private readonly PaintTexture texture;
    private readonly Layer layer;
    private readonly TexelSelection startSelection;
    private readonly Color[] before;
    private readonly Color[] underneath;
    private readonly List<(int X, int Y, Color Color)> lifted = new();
    private bool placed;
    public (int X, int Y) Offset { get; private set; }

    public SelectionMove(PaintTexture texture, TexelSelection selection, bool duplicate)
    {
        this.texture = texture;
        layer = texture.ActiveLayer;
        startSelection = selection.Clone();
        before = (Color[])layer.Pixels.Clone();
        underneath = (Color[])layer.Pixels.Clone();
        for (int y = 0; y < texture.Height; y++)
        for (int x = 0; x < texture.Width; x++)
        {
            int i = y * texture.Width + x;
            if (!selection.Mask[i]) continue;
            lifted.Add((x, y, before[i]));
            if (!duplicate) underneath[i] = new Color(0, 0, 0, 0);
        }
    }

    /// <summary>Places the lifted texels at <paramref name="dx"/>, <paramref name="dy"/> from where they started.</summary>
    public void MoveTo(int dx, int dy, TexelSelection selection)
    {
        if (placed && (dx, dy) == Offset) return;
        placed = true;
        Offset = (dx, dy);
        Array.Copy(underneath, layer.Pixels, underneath.Length);
        foreach (var (x, y, color) in lifted)
        {
            int nx = x + dx, ny = y + dy;
            if (nx < 0 || ny < 0 || nx >= texture.Width || ny >= texture.Height) continue;
            layer.Pixels[ny * texture.Width + nx] = color;
        }
        texture.Touch();

        Array.Copy(startSelection.Mask, selection.Mask, selection.Mask.Length);
        selection.Offset(dx, dy);
    }

    /// <summary>The undo step, or null if nothing moved.</summary>
    public UndoStep? Finish() =>
        Offset == (0, 0) ? null : UndoStep.Capture(texture, layer, before, 0, 0, texture.Width, texture.Height);
}
