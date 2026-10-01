using System.Numerics;
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
}
