using Raylib_cs;

namespace Sable.Paint;

/// <summary>How a layer mixes with the layers under it (the W3C / Photoshop separable blend modes).</summary>
public enum BlendMode { Normal, Multiply, Screen, Overlay, Add }

/// <summary>
/// What a layer is for. <see cref="Smoothness"/>: the texture's smoothness mask, at most one, kept at the top of the
/// stack and left out of the colour. Its texels' alpha says where the surface is smooth, and its
/// <see cref="Layer.Opacity"/> how smooth fully painted texels are; the colour painted on it doesn't matter.
/// </summary>
public enum LayerKind { Normal, Smoothness }

/// <summary>
/// One layer of a <see cref="PaintTexture"/>: its own RGBA pixels (straight alpha, row 0 at the top), shown over
/// the layers below it at <see cref="Opacity"/> with <see cref="Blend"/>.
/// </summary>
public sealed class Layer
{
    public string Name { get; set; }
    public Color[] Pixels { get; }
    public bool Visible { get; set; } = true;
    /// <summary>0..1, on top of each texel's own alpha.</summary>
    public float Opacity { get; set; } = 1f;
    public BlendMode Blend { get; set; }
    public LayerKind Kind { get; init; }
    /// <summary>The smoothness mask: not part of the colour (see <see cref="LayerKind.Smoothness"/>).</summary>
    public bool IsMask => Kind == LayerKind.Smoothness;

    /// <summary>The smoothness mask's name in Sable; it can't be renamed.</summary>
    public const string SmoothnessName = "Smoothness";
    /// <summary>A new mask's smoothness where fully painted (its opacity).</summary>
    public const float DefaultSmoothness = 0.8f;

    public Layer(string name, Color[] pixels)
    {
        Name = name;
        Pixels = pixels;
    }

    public static Layer Transparent(string name, int width, int height) => new(name, new Color[width * height]);

    public Layer Clone(string name) => WithPixels((Color[])Pixels.Clone(), name);

    /// <summary>A new layer with this one's settings and kind, holding <paramref name="pixels"/>.</summary>
    public Layer WithPixels(Color[] pixels, string? name = null) =>
        new(name ?? Name, pixels) { Visible = Visible, Opacity = Opacity, Blend = Blend, Kind = Kind };

    /// <summary>Plain: a single visible layer like this shows its pixels exactly as they are.</summary>
    public bool IsPlain => Visible && Opacity >= 1f && Blend == BlendMode.Normal;

    public static readonly string[] BlendNames = Enum.GetNames<BlendMode>();
}

/// <summary>Flattens a stack of layers, bottom first.</summary>
public static class Compositor
{
    /// <summary>
    /// Writes rows <paramref name="y0"/>..<paramref name="y1"/>, columns <paramref name="x0"/>..<paramref name="x1"/>
    /// (inclusive) of the flattened layers into <paramref name="output"/>. The smoothness mask isn't colour, so it's left out.
    /// </summary>
    public static void Composite(IReadOnlyList<Layer> layers, Color[] output, int width, int x0, int y0, int x1, int y1)
    {
        var visible = layers.Where(l => l.Visible && l.Opacity > 0f && !l.IsMask).ToArray();
        int w = x1 - x0 + 1;
        if (visible.Length == 0)
        {
            for (int y = y0; y <= y1; y++) Array.Clear(output, y * width + x0, w);
            return;
        }
        if (visible.Length == 1 && visible[0].IsPlain)
        {
            for (int y = y0; y <= y1; y++) Array.Copy(visible[0].Pixels, y * width + x0, output, y * width + x0, w);
            return;
        }

        void Row(int y)
        {
            for (int x = x0; x <= x1; x++)
            {
                int i = y * width + x;
                float r = 0, g = 0, b = 0, a = 0;
                foreach (var layer in visible) Over(layer.Pixels[i], layer.Opacity, layer.Blend, ref r, ref g, ref b, ref a);
                output[i] = ToColor(r, g, b, a);
            }
        }
        if ((long)w * (y1 - y0 + 1) * visible.Length < 60_000)
            for (int y = y0; y <= y1; y++) Row(y);
        else
            Parallel.For(y0, y1 + 1, Row);
    }

    /// <summary>Lays one texel over a backdrop (straight-alpha r, g, b, a in 0..1), in place.</summary>
    public static void Over(Color source, float opacity, BlendMode mode, ref float r, ref float g, ref float b, ref float a)
    {
        float sa = source.A / 255f * opacity;
        if (sa <= 0f) return;
        float sr = source.R / 255f, sg = source.G / 255f, sb = source.B / 255f;
        if (mode != BlendMode.Normal && a > 0f)
        {
            // Where there's backdrop the blended colour shows; where there isn't, the layer's own (W3C compositing).
            sr = (1f - a) * sr + a * Mix(r, sr, mode);
            sg = (1f - a) * sg + a * Mix(g, sg, mode);
            sb = (1f - a) * sb + a * Mix(b, sb, mode);
        }
        float outA = sa + a * (1f - sa);
        float keep = a * (1f - sa);
        r = (sr * sa + r * keep) / outA;
        g = (sg * sa + g * keep) / outA;
        b = (sb * sa + b * keep) / outA;
        a = outA;
    }

    private static float Mix(float backdrop, float source, BlendMode mode) => mode switch
    {
        BlendMode.Multiply => backdrop * source,
        BlendMode.Screen => backdrop + source - backdrop * source,
        BlendMode.Overlay => backdrop <= 0.5f ? 2f * backdrop * source : 1f - 2f * (1f - backdrop) * (1f - source),
        BlendMode.Add => MathF.Min(1f, backdrop + source),
        _ => source,
    };

    public static Color ToColor(float r, float g, float b, float a) => a <= 0f
        ? new Color(0, 0, 0, 0)
        : new Color(Byte(r), Byte(g), Byte(b), Byte(a));

    private static byte Byte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);
}

/// <summary>
/// A change to a texture's layer stack (add, delete, reorder, merge, rename, visibility, opacity, blend mode) as
/// before and after snapshots. Layers are never changed in place by these operations (a merge makes a new layer),
/// so the snapshots can hold the layer objects themselves.
/// </summary>
public sealed class LayerStep : IUndoStep
{
    private readonly PaintTexture texture;
    private readonly LayerState before, after;

    public LayerStep(PaintTexture texture, LayerState before, LayerState after)
    {
        this.texture = texture;
        this.before = before;
        this.after = after;
    }

    /// <summary>The pixels only this step keeps alive (layers that exist on one side only), for the stack's cap.</summary>
    public long Bytes => (before.Layers.Except(after.Layers).Count() + after.Layers.Except(before.Layers).Count())
                         * (long)texture.Width * texture.Height * 4 + 64;

    public void Undo() => texture.Restore(before);
    public void Redo() => texture.Restore(after);
}

/// <summary>A layer stack as it was: the layers bottom first, their settings, and which one is active.</summary>
public sealed record LayerState(Layer[] Layers, (string Name, bool Visible, float Opacity, BlendMode Blend)[] Settings, int Active)
{
    public bool SameAs(LayerState other) =>
        Active == other.Active && Layers.SequenceEqual(other.Layers) && Settings.SequenceEqual(other.Settings);
}
