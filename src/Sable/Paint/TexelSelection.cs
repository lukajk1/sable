using System.Numerics;

namespace Sable.Paint;

public enum SelectionOp { Replace, Add, Subtract }

/// <summary>
/// A set of texels of one texture, made with the lasso. Texels are in when their centre is inside the lasso
/// (even-odd rule, so a self-crossing lasso behaves predictably). Keeps its outline as texel-edge segments for
/// drawing.
/// </summary>
public sealed class TexelSelection
{
    public int Texture { get; set; }
    public int Width { get; }
    public int Height { get; }
    public bool[] Mask { get; private set; }
    public bool Any { get; private set; }
    /// <summary>Outline segments in texel coordinates, between selected and unselected texels.</summary>
    public List<(Vector2 A, Vector2 B)> Outline { get; } = new();

    public TexelSelection(int texture, int width, int height)
    {
        Texture = texture;
        Width = width;
        Height = height;
        Mask = new bool[width * height];
    }

    public bool Contains(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Mask[y * Width + x];

    /// <summary>Combines the lasso polygon (texel coordinates) into the selection.</summary>
    public void Apply(IReadOnlyList<Vector2> polygon, SelectionOp op)
    {
        if (op == SelectionOp.Replace) Array.Clear(Mask);
        if (polygon.Count >= 3)
        {
            var crossings = new List<float>();
            for (int y = 0; y < Height; y++)
            {
                float cy = y + 0.5f;
                crossings.Clear();
                for (int i = 0; i < polygon.Count; i++)
                {
                    Vector2 a = polygon[i], b = polygon[(i + 1) % polygon.Count];
                    // Half-open on y so a vertex exactly on a row counts once.
                    if ((a.Y <= cy && b.Y > cy) || (b.Y <= cy && a.Y > cy))
                        crossings.Add(a.X + (cy - a.Y) / (b.Y - a.Y) * (b.X - a.X));
                }
                crossings.Sort();
                for (int k = 0; k + 1 < crossings.Count; k += 2)
                {
                    // Texels whose centre x + 0.5 lies in [left, right).
                    int x0 = Math.Max(0, (int)MathF.Ceiling(crossings[k] - 0.5f));
                    int x1 = Math.Min(Width - 1, (int)MathF.Ceiling(crossings[k + 1] - 0.5f) - 1);
                    for (int x = x0; x <= x1; x++) Mask[y * Width + x] = op != SelectionOp.Subtract;
                }
            }
        }
        Refresh();
    }

    /// <summary>Shifts the selection by whole texels; texels pushed off the texture are dropped.</summary>
    public void Offset(int dx, int dy)
    {
        var moved = new bool[Mask.Length];
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (!Mask[y * Width + x]) continue;
            int nx = x + dx, ny = y + dy;
            if (nx >= 0 && ny >= 0 && nx < Width && ny < Height) moved[ny * Width + nx] = true;
        }
        Mask = moved;
        Refresh();
    }

    public TexelSelection Clone()
    {
        var copy = new TexelSelection(Texture, Width, Height) { Mask = (bool[])Mask.Clone() };
        copy.Refresh();
        return copy;
    }

    private void Refresh()
    {
        Any = Array.IndexOf(Mask, true) >= 0;
        Outline.Clear();
        if (!Any) return;
        for (int y = 0; y < Height; y++)
        for (int x = 0; x < Width; x++)
        {
            if (!Mask[y * Width + x]) continue;
            if (!Contains(x, y - 1)) Outline.Add((new Vector2(x, y), new Vector2(x + 1, y)));
            if (!Contains(x, y + 1)) Outline.Add((new Vector2(x, y + 1), new Vector2(x + 1, y + 1)));
            if (!Contains(x - 1, y)) Outline.Add((new Vector2(x, y), new Vector2(x, y + 1)));
            if (!Contains(x + 1, y)) Outline.Add((new Vector2(x + 1, y), new Vector2(x + 1, y + 1)));
        }
    }
}
