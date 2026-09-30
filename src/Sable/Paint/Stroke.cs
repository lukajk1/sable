using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// One press-drag-release of a paint tool on one texture, composited the way Photoshop and Krita do it. Each dab
/// builds up a texel's stroke coverage by <see cref="Flow"/> times the brush shape (so going over a spot again
/// within the stroke deepens it), and the stroke is laid over the pre-stroke texture at <c>opacity</c>, which is
/// therefore the most a single stroke can reach.
/// </summary>
public sealed class Stroke
{
    public PaintTexture Texture { get; }
    /// <summary>The layer painted: the active one when the stroke began.</summary>
    public Layer Layer { get; }
    /// <summary>How much each dab adds, 0..1 (set per dab, e.g. from pen pressure).</summary>
    public float Flow { get; set; } = 1f;
    private readonly Color[] pixels;
    private readonly Color[] before;
    private readonly float[] coverage;
    private readonly Color color;
    private readonly float opacity;
    private readonly bool[]? mask;
    private readonly bool erase;
    private int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;

    /// <param name="opacity">The ceiling for this stroke, 0..1.</param>
    /// <param name="mask">When given, only these texels can be painted (the lasso selection).</param>
    /// <param name="erase">Take alpha away instead of laying colour on.</param>
    public Stroke(PaintTexture texture, Color color, float opacity = 1f, bool[]? mask = null, bool erase = false)
    {
        Texture = texture;
        Layer = texture.ActiveLayer;
        pixels = Layer.Pixels;
        this.erase = erase;
        this.color = color with { A = 255 };
        this.opacity = Math.Clamp(opacity, 0f, 1f);
        this.mask = mask;
        (before, coverage) = texture.BeginStrokeBuffers();
    }

    /// <summary>Adds one dab's share <paramref name="shape"/> (0..1) to texel (x, y), wrapped into the texture.</summary>
    public void Apply(int x, int y, float shape)
    {
        float add = shape * Flow;
        if (add <= 0) return;
        x = Texture.Wrap(x, Texture.Width);
        y = Texture.Wrap(y, Texture.Height);
        int i = y * Texture.Width + x;
        if (mask != null && !mask[i]) return;
        float previous = coverage[i];
        float next = add >= 1f ? 1f : previous + (1f - previous) * add;
        if (next - previous < 1e-5f) return;
        coverage[i] = next;

        Color from = before[i];
        float a = next * opacity;
        if (erase)
        {
            pixels[i] = a >= 0.999f ? new Color(0, 0, 0, 0) : new Color(from.R, from.G, from.B, (byte)MathF.Round(from.A * (1f - a)));
        }
        else if (a >= 0.999f || from.A == 255)
        {
            pixels[i] = a >= 0.999f ? color
                : new Color(Lerp(from.R, color.R, a), Lerp(from.G, color.G, a), Lerp(from.B, color.B, a), (byte)255);
        }
        else
        {
            // Paint over a see-through texel: its colour only counts as much as it shows, so soft edges on a
            // transparent layer keep the paint colour instead of darkening toward the texel's hidden RGB.
            float fromA = from.A / 255f;
            float outA = a + fromA * (1f - a);
            float keep = fromA * (1f - a);
            pixels[i] = new Color(
                (byte)MathF.Round((color.R * a + from.R * keep) / outA), (byte)MathF.Round((color.G * a + from.G * keep) / outA),
                (byte)MathF.Round((color.B * a + from.B * keep) / outA), (byte)MathF.Round(outA * 255f));
        }

        minX = Math.Min(minX, x);
        minY = Math.Min(minY, y);
        maxX = Math.Max(maxX, x);
        maxY = Math.Max(maxY, y);
        Texture.Touch(x, y);
    }

    private static byte Lerp(byte a, byte b, float t) => (byte)MathF.Round(a + (b - a) * t);

    /// <summary>The undo step for this stroke, or null if it changed nothing.</summary>
    public UndoStep? Finish()
    {
        if (maxX < 0) return null;
        return UndoStep.Capture(Texture, Layer, before, minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}

/// <summary>Something Ctrl+Z can take back: a stroke, or a change to what's hidden.</summary>
public interface IUndoStep
{
    void Undo();
    void Redo();
    /// <summary>Roughly how much memory the step holds, for the stack's cap.</summary>
    long Bytes { get; }
}

/// <summary>A changed rectangle of one layer of a texture, before and after.</summary>
public sealed class UndoStep : IUndoStep
{
    private readonly PaintTexture texture;
    private readonly Layer layer;
    private readonly int x, y, width, height;
    private readonly Color[] before, after;

    private UndoStep(PaintTexture texture, Layer layer, int x, int y, int width, int height, Color[] before, Color[] after)
    {
        this.texture = texture;
        this.layer = layer;
        this.x = x;
        this.y = y;
        this.width = width;
        this.height = height;
        this.before = before;
        this.after = after;
    }

    public long Bytes => (before.Length + after.Length) * 4L;

    public static UndoStep Capture(PaintTexture texture, Layer layer, Color[] fullBefore, int x, int y, int width, int height)
    {
        var before = new Color[width * height];
        var after = new Color[width * height];
        for (int row = 0; row < height; row++)
        {
            Array.Copy(fullBefore, (y + row) * texture.Width + x, before, row * width, width);
            Array.Copy(layer.Pixels, (y + row) * texture.Width + x, after, row * width, width);
        }
        return new UndoStep(texture, layer, x, y, width, height, before, after);
    }

    public void Undo() => Write(before);
    public void Redo() => Write(after);

    private void Write(Color[] source)
    {
        for (int row = 0; row < height; row++)
            Array.Copy(source, row * width, layer.Pixels, (y + row) * texture.Width + x, width);
        texture.Touch(x, y, x + width - 1, y + height - 1);
    }
}

public sealed class UndoStack
{
    private const long MaxBytes = 256L * 1024 * 1024;
    private readonly LinkedList<IUndoStep> undo = new();
    private readonly Stack<IUndoStep> redo = new();
    private long bytes;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Push(IUndoStep step)
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

    /// <summary>Drops the steps that match (e.g. ones tied to a model that was replaced), keeping the rest in order.</summary>
    public void RemoveAll(Func<IUndoStep, bool> match)
    {
        for (var node = undo.First; node != null;)
        {
            var next = node.Next;
            if (match(node.Value))
            {
                bytes -= node.Value.Bytes;
                undo.Remove(node);
            }
            node = next;
        }
        var kept = redo.Where(step => !match(step)).Reverse().ToList();
        redo.Clear();
        foreach (var step in kept) redo.Push(step);
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        bytes = 0;
    }
}
