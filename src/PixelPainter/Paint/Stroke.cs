using Raylib_cs;

namespace PixelPainter.Paint;

/// <summary>
/// One press-drag-release of a paint tool on one texture. Each texel keeps the strongest coverage the stroke gave
/// it and is blended from its pre-stroke colour, so a soft brush doesn't build up where dabs overlap.
/// </summary>
public sealed class Stroke
{
    public PaintTexture Texture { get; }
    private readonly Color[] before;
    private readonly float[] coverage;
    private readonly Color color;
    private readonly bool[]? mask;
    private int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

    /// <param name="mask">When given, only these texels can be painted (the lasso selection).</param>
    public Stroke(PaintTexture texture, Color color, bool[]? mask = null)
    {
        Texture = texture;
        this.color = color;
        this.mask = mask;
        before = (Color[])texture.Pixels.Clone();
        coverage = new float[texture.Width * texture.Height];
    }

    /// <summary>Paints texel (x, y) (wrapped into the texture) with coverage 0..1.</summary>
    public void Apply(int x, int y, float alpha)
    {
        if (alpha <= 0) return;
        x = Texture.Wrap(x, Texture.Width);
        y = Texture.Wrap(y, Texture.Height);
        int i = y * Texture.Width + x;
        if (mask != null && !mask[i]) return;
        if (alpha <= coverage[i]) return;
        coverage[i] = alpha;

        Color from = before[i];
        float a = alpha * (color.A / 255f);
        Texture.Pixels[i] = a >= 0.999f
            ? color
            : new Color(Lerp(from.R, color.R, a), Lerp(from.G, color.G, a), Lerp(from.B, color.B, a),
                (byte)Math.Max(from.A, (int)MathF.Round(a * 255f)));

        minX = Math.Min(minX, x);
        minY = Math.Min(minY, y);
        maxX = Math.Max(maxX, x);
        maxY = Math.Max(maxY, y);
        Texture.Touch();
    }

    private static byte Lerp(byte a, byte b, float t) => (byte)MathF.Round(a + (b - a) * t);

    /// <summary>The undo step for this stroke, or null if it changed nothing.</summary>
    public UndoStep? Finish()
    {
        if (maxX < 0) return null;
        return UndoStep.Capture(Texture, before, minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}

/// <summary>A changed rectangle of one texture, before and after.</summary>
public sealed class UndoStep
{
    private readonly PaintTexture texture;
    private readonly int x, y, width, height;
    private readonly Color[] before, after;

    private UndoStep(PaintTexture texture, int x, int y, int width, int height, Color[] before, Color[] after)
    {
        this.texture = texture;
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
        this.before = before;
        this.after = after;
    }

    public long Bytes => (before.Length + after.Length) * 4L;

    public static UndoStep Capture(PaintTexture texture, Color[] fullBefore, int x, int y, int width, int height)
    {
        var before = new Color[width * height];
        var after = new Color[width * height];
        for (int row = 0; row < height; row++)
        {
            Array.Copy(fullBefore, (y + row) * texture.Width + x, before, row * width, width);
            Array.Copy(texture.Pixels, (y + row) * texture.Width + x, after, row * width, width);
        }
        return new UndoStep(texture, x, y, width, height, before, after);
    }

    public void Undo() => Write(before);
    public void Redo() => Write(after);

    private void Write(Color[] source)
    {
        for (int row = 0; row < height; row++)
            Array.Copy(source, row * width, texture.Pixels, (y + row) * texture.Width + x, width);
        texture.Touch();
    }
}

public sealed class UndoStack
{
    private const long MaxBytes = 256L * 1024 * 1024;
    private readonly LinkedList<UndoStep> undo = new();
    private readonly Stack<UndoStep> redo = new();
    private long bytes;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Push(UndoStep step)
    {
        undo.AddLast(step);
        bytes += step.Bytes;
        redo.Clear();
        while (bytes > MaxBytes && undo.Count > 1)
        {
            bytes -= undo.First!.Value.Bytes;
            undo.RemoveFirst();
        }
    }

    public void Undo()
    {
        if (undo.Last is not { } node) return;
        undo.RemoveLast();
        bytes -= node.Value.Bytes;
        node.Value.Undo();
        redo.Push(node.Value);
    }

    public void Redo()
    {
        if (redo.Count == 0) return;
        var step = redo.Pop();
        step.Redo();
        undo.AddLast(step);
        bytes += step.Bytes;
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        bytes = 0;
    }
}
