using System.Numerics;
using Sable.Paint;
using Raylib_cs;

namespace Sable.Rendering;

/// <summary>
/// Writes the UV layout as a PNG, as Blender's Export UV Layout does: a template to paint over elsewhere. Either
/// black lines on transparent, or white lines over the texture, at a multiple of the texture's size.
/// </summary>
public static unsafe class UvLayoutExport
{
    /// <param name="baseSize">The texture's size (or the UV view's placeholder size when there is no texture).</param>
    /// <param name="texture">Drawn underneath when <paramref name="overTexture"/> is set; may be null.</param>
    public static void Export(GpuModel model, IEnumerable<int> parts, Vector2 baseSize, PaintTexture? texture, int scale,
        bool overTexture, string path)
    {
        int width = (int)baseSize.X * scale, height = (int)baseSize.Y * scale;
        Image image;
        if (overTexture && texture != null)
        {
            image = Raylib.GenImageColor(texture.Width, texture.Height, Color.Blank);
            texture.EnsureComposite();
            fixed (Color* pixels = texture.Composite)
                Buffer.MemoryCopy(pixels, image.Data, texture.Composite.Length * 4L, texture.Composite.Length * 4L);
            Raylib.ImageResizeNN(ref image, width, height);
        }
        else
        {
            image = Raylib.GenImageColor(width, height, Color.Blank);
        }

        var line = overTexture ? Color.White : Color.Black;
        var size = new Vector2(width, height);
        foreach (int p in parts)
        {
            var part = model.Source.Parts[p];
            if (part.Uvs == null) continue;
            var edges = model.Edges[p];
            for (int e = 0; e < edges.A.Length; e++)
            {
                if (part.ComponentHidden[edges.Component[e]]) continue;
                Raylib.ImageDrawLineV(ref image, part.Uvs[edges.A[e]] * size, part.Uvs[edges.B[e]] * size, line);
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        bool ok = Raylib.ExportImage(image, path);
        Raylib.UnloadImage(image);
        if (!ok) throw new IOException($"Couldn't write {path}");
    }

    /// <summary>The UV wireframe of <paramref name="parts"/> as pixels: <paramref name="line"/> on transparent.</summary>
    public static Color[] Lines(GpuModel model, IEnumerable<int> parts, int width, int height, Color line)
    {
        Image image = Raylib.GenImageColor(width, height, Color.Blank);
        var size = new Vector2(width, height);
        foreach (int p in parts)
        {
            var part = model.Source.Parts[p];
            if (part.Uvs == null) continue;
            var edges = model.Edges[p];
            for (int e = 0; e < edges.A.Length; e++)
                Raylib.ImageDrawLineV(ref image, part.Uvs[edges.A[e]] * size, part.Uvs[edges.B[e]] * size, line);
        }
        var pixels = new Span<Color>(image.Data, width * height).ToArray();
        Raylib.UnloadImage(image);
        return pixels;
    }
}
