using Raylib_cs;

namespace Sable.Paint;

/// <summary>Scales RGBA pixels to a new size.</summary>
public static class Resampler
{
    /// <summary>
    /// Nearest: each new texel takes the old texel under its centre (hard pixels, for pixel art). Smooth: bilinear,
    /// averaged over several samples per texel when shrinking, with colour weighted by alpha so transparent texels
    /// don't darken the edges. Edges clamp rather than wrap, as UV islands in an atlas shouldn't bleed across.
    /// </summary>
    public static Color[] Resample(Color[] source, int sourceWidth, int sourceHeight, int width, int height, bool smooth)
    {
        var result = new Color[width * height];
        float scaleX = sourceWidth / (float)width, scaleY = sourceHeight / (float)height;
        if (!smooth)
        {
            Parallel.For(0, height, y =>
            {
                int sy = Math.Min(sourceHeight - 1, (int)((y + 0.5f) * scaleY));
                for (int x = 0; x < width; x++)
                    result[y * width + x] = source[sy * sourceWidth + Math.Min(sourceWidth - 1, (int)((x + 0.5f) * scaleX))];
            });
            return result;
        }

        int samplesX = Math.Clamp((int)MathF.Ceiling(scaleX), 1, 8), samplesY = Math.Clamp((int)MathF.Ceiling(scaleY), 1, 8);
        Parallel.For(0, height, y =>
        {
            for (int x = 0; x < width; x++)
            {
                float r = 0, g = 0, b = 0, a = 0;
                for (int j = 0; j < samplesY; j++)
                for (int i = 0; i < samplesX; i++)
                {
                    // Sample point in source texel space (texel centres at +0.5).
                    float u = (x + (i + 0.5f) / samplesX) * scaleX - 0.5f;
                    float v = (y + (j + 0.5f) / samplesY) * scaleY - 0.5f;
                    Bilinear(source, sourceWidth, sourceHeight, u, v, ref r, ref g, ref b, ref a);
                }
                float n = samplesX * samplesY;
                result[y * width + x] = a <= 0f ? new Color(0, 0, 0, 0)
                    : new Color(Byte(r / a), Byte(g / a), Byte(b / a), Byte(a / n / 255f));
            }
        });
        return result;
    }

    // Adds one bilinear sample, premultiplied: colour sums weighted by alpha (0..255), alpha summed in 0..255.
    private static void Bilinear(Color[] source, int w, int h, float u, float v, ref float r, ref float g, ref float b, ref float a)
    {
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        for (int k = 0; k < 4; k++)
        {
            int dx = k & 1, dy = k >> 1;
            float weight = (dx == 1 ? fx : 1f - fx) * (dy == 1 ? fy : 1f - fy);
            if (weight <= 0f) continue;
            var c = source[Math.Clamp(y0 + dy, 0, h - 1) * w + Math.Clamp(x0 + dx, 0, w - 1)];
            float wa = weight * c.A;
            r += c.R * wa;
            g += c.G * wa;
            b += c.B * wa;
            a += wa;
        }
    }

    private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
}

/// <summary>A texture's size change: the size and every layer, before and after.</summary>
public sealed class ResizeStep : IUndoStep
{
    private readonly PaintTexture texture;
    private readonly int oldWidth, oldHeight, newWidth, newHeight;
    private readonly LayerState before, after;

    public ResizeStep(PaintTexture texture, int oldWidth, int oldHeight, LayerState before, int newWidth, int newHeight, LayerState after)
    {
        this.texture = texture;
        (this.oldWidth, this.oldHeight, this.before) = (oldWidth, oldHeight, before);
        (this.newWidth, this.newHeight, this.after) = (newWidth, newHeight, after);
    }

    public long Bytes => (long)(oldWidth * oldHeight * before.Layers.Length + newWidth * newHeight * after.Layers.Length) * 4;

    public void Undo() => texture.SetContents(oldWidth, oldHeight, before);
    public void Redo() => texture.SetContents(newWidth, newHeight, after);
}
