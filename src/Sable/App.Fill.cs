using System.Numerics;
using Raylib_cs;
using Sable.Model;
using Sable.Paint;
using Sable.UI;

namespace Sable;

/// <summary>The fill tool's shape modes: a whole UV island, or a whole face, picked in either view.</summary>
internal sealed partial class App
{
    private enum FillMode { Similar, Island, Face }
    private static readonly string[] FillModeNames = { "Similar colour", "UV island", "Face" };
    private FillMode fillMode;

    /// <summary>
    /// Fills the UV island or the face (polygon) under <paramref name="hit"/> with the paint colour, and its mirror
    /// when mirroring. Texels the island only partly covers are included, so no gaps show along its edges. A
    /// texel selection still limits it. One undo step.
    /// </summary>
    private void FillShape(SurfaceHit hit)
    {
        int texture = Model!.TextureOf(hit.Part);
        var tex = Model.Textures[texture];
        state.ActiveTexture = texture;
        palette.Remember(ColorWheel.HsvToRgb(hsv));
        var fill = new Stroke(tex, PaintColor, 1f, MaskFor(texture));
        int triangles = FillTriangles(fill, hit);
        if (MirrorHit(hit, out var mirrored) && Model.TextureOf(mirrored.Part) == texture) triangles += FillTriangles(fill, mirrored);
        if (fill.Finish() is { } step) undo.Push(step);
        SetStatus($"Filled {(fillMode == FillMode.Island ? "the UV island" : "the face")} ({triangles} triangle{(triangles == 1 ? "" : "s")}).", error: false);
    }

    private int FillTriangles(Stroke fill, SurfaceHit hit)
    {
        var part = Model!.Source.Parts[hit.Part];
        List<int> triangles;
        if (fillMode == FillMode.Island)
        {
            var island = UvIslands.IslandOfTriangle(part, out _);
            int id = island[hit.Triangle];
            triangles = new List<int>();
            for (int t = 0; t < part.TriangleCount; t++)
                if (island[t] == id && part.TriangleVisible(t)) triangles.Add(t);
        }
        else
        {
            triangles = UvIslands.FaceTriangles(part, hit.Triangle).Where(part.TriangleVisible).ToList();
        }
        var tex = fill.Texture;
        UvRaster.Triangles(part, triangles, tex.Width, tex.Height, (x, y) => fill.Apply(x, y, 1f), dilate: 0.5f);
        return triangles.Count;
    }

    /// <summary>Texels of padding saved images get past the edge of their UV islands (0 = none).</summary>
    private int edgePadding = 4;

    /// <summary>
    /// What saving writes for a texture: the flattened layers with colour bled <see cref="edgePadding"/> texels past
    /// the UV islands of every part that uses it. Without padding, or UVs, it's the flattened layers as they are.
    /// </summary>
    private Color[] PaddedPixels(PaintTexture texture)
    {
        texture.EnsureComposite();
        int index = Model!.Textures.IndexOf(texture);
        if (edgePadding <= 0 || index < 0) return texture.Composite;
        var inside = UvRaster.Coverage(Model.Source, p => Model.TextureOf(p) == index, texture.Width, texture.Height);
        if (!inside.Contains(true)) return texture.Composite;
        return EdgePadding.Pad(texture.Composite, texture.Width, texture.Height, inside, edgePadding);
    }

    /// <summary>The surface point of the active object whose UVs are at <paramref name="texelPoint"/> on the active texture.</summary>
    private bool HitAtTexel(Vector2 texelPoint, out SurfaceHit hit)
    {
        hit = default;
        if (state.ActiveObject < 0 || state.ActiveTexture < 0) return false;
        var tex = Model!.Textures[state.ActiveTexture];
        Vector2 uv = texelPoint / new Vector2(tex.Width, tex.Height);
        foreach (int partIndex in Model.Source.Objects[state.ActiveObject].Parts)
        {
            var part = Model.Source.Parts[partIndex];
            if (part.Uvs == null || Model.TextureOf(partIndex) != state.ActiveTexture) continue;
            for (int t = 0; t < part.TriangleCount; t++)
            {
                if (!part.TriangleVisible(t)) continue;
                int i0 = part.Indices[t * 3], i1 = part.Indices[t * 3 + 1], i2 = part.Indices[t * 3 + 2];
                var b = Raycast.Barycentric2D(uv, part.Uvs[i0], part.Uvs[i1], part.Uvs[i2]);
                if (b.X < -1e-4f || b.Y < -1e-4f || b.Z < -1e-4f) continue;
                Vector3 a = part.Positions[i0], bb = part.Positions[i1], c = part.Positions[i2];
                hit = new SurfaceHit
                {
                    Part = partIndex,
                    Triangle = t,
                    Point = a * b.X + bb * b.Y + c * b.Z,
                    Normal = Vector3.Normalize(Vector3.Cross(bb - a, c - a)),
                    Barycentric = b,
                };
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Edge padding: texels inside the UV islands are saved exactly, some outside get bled colour, and layers
    /// saved beside a padded image still come back when it's reopened.
    /// </summary>
    private void SelfTestPadding(PaintTexture tex)
    {
        int index = Model!.Textures.IndexOf(tex);
        var inside = UvRaster.Coverage(Model.Source, p => Model.TextureOf(p) == index, tex.Width, tex.Height);
        int saved = edgePadding;
        edgePadding = 4;
        var padded = PaddedPixels(tex);
        edgePadding = saved;
        bool insideExact = true;
        int changedOutside = 0;
        for (int i = 0; i < padded.Length; i++)
        {
            if (inside[i]) insideExact &= padded[i].Equals(tex.Composite[i]);
            else if (!padded[i].Equals(tex.Composite[i])) changedOutside++;
        }

        string dir = Path.Combine(Path.GetTempPath(), "Sable", "selftest");
        Directory.CreateDirectory(dir);
        string png = Path.Combine(dir, "padded.png");
        tex.ExportTo(png, padded);
        LayerFile.Write(tex, png, padded);
        bool restored;
        using (var reopened = PaintTexture.FromEncoded("padded.png", ".png", File.ReadAllBytes(png), png))
            restored = LayerFile.TryLoad(reopened, png, out _) && reopened.Layers.Count == tex.Layers.Count;
        Console.WriteLine($"[selftest] edge padding (4): {inside.Count(b => b)} texels inside islands saved exactly {insideExact}, "
                          + $"{changedOutside} outside bled; layers restored beside the padded image {restored} ({tex.Layers.Count} layers)");
    }
}
