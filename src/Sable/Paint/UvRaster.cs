using System.Numerics;
using Sable.Model;

namespace Sable.Paint;

/// <summary>
/// Finds the texels a mesh's UV triangles cover. A texel counts when its centre lies inside a triangle's UV footprint,
/// or within a dilation distance (in texels) of it. Texel coordinates wrap into the texture like
/// <see cref="PaintTexture.Wrap"/>, since UVs may lie outside 0..1.
/// </summary>
public static class UvRaster
{
    /// <summary>
    /// Calls <paramref name="texel"/> for every texel the given triangles of <paramref name="part"/> cover. A texel on
    /// an edge shared by two triangles may be reported for both. Nothing happens for a part without UVs.
    /// </summary>
    public static void Triangles(MeshPart part, IEnumerable<int> triangles, int width, int height, Action<int, int> texel, float dilate = 0f)
    {
        if (part.Uvs == null || width <= 0 || height <= 0) return;
        var uvs = part.Uvs;
        var idx = part.Indices;
        var size = new Vector2(width, height);
        foreach (int t in triangles)
            Texels(uvs[idx[t * 3]] * size, uvs[idx[t * 3 + 1]] * size, uvs[idx[t * 3 + 2]] * size, width, height, dilate, texel);
    }

    /// <summary>One triangle given by its UVs (0..1 across the texture, v = 0 at the top).</summary>
    public static void Triangle(Vector2 a, Vector2 b, Vector2 c, int width, int height, Action<int, int> texel, float dilate = 0f)
    {
        if (width <= 0 || height <= 0) return;
        var size = new Vector2(width, height);
        Texels(a * size, b * size, c * size, width, height, dilate, texel);
    }

    /// <summary>
    /// A width x height mask (row 0 at the top) of the texels covered by the UVs of every part
    /// <paramref name="partFilter"/> accepts. Parts without UVs are skipped.
    /// </summary>
    public static bool[] Coverage(LoadedModel model, Func<int, bool> partFilter, int width, int height, float dilate = 0f)
    {
        var mask = new bool[Math.Max(width, 0) * Math.Max(height, 0)];
        if (mask.Length == 0) return mask;
        var size = new Vector2(width, height);
        for (int p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p];
            if (part.Uvs == null || !partFilter(p)) continue;
            var uvs = part.Uvs;
            var idx = part.Indices;
            // Writes only ever set true, so overlapping triangles on different threads don't conflict.
            void Mark(int x, int y) => mask[y * width + x] = true;
            Action<int, int> mark = Mark;
            // Small batches: a few triangles can hold most of the work when they are large in UV space.
            int batch = Math.Clamp(part.TriangleCount / (Environment.ProcessorCount * 8), 1, 1024);
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, part.TriangleCount, batch), range =>
            {
                for (int t = range.Item1; t < range.Item2; t++)
                    Texels(uvs[idx[t * 3]] * size, uvs[idx[t * 3 + 1]] * size, uvs[idx[t * 3 + 2]] * size, width, height, dilate, mark);
            });
        }
        return mask;
    }

    /// <summary>
    /// The rasterizer: triangle (a, b, c) in texel units. Each edge's signed distance to the texel centre rejects
    /// texels more than <paramref name="dilate"/> outside it cheaply; the rest outside the triangle are measured
    /// against its edges exactly. A zero-area triangle covers no centres by itself, only its dilated outline.
    /// </summary>
    private static void Texels(Vector2 a, Vector2 b, Vector2 c, int width, int height, float dilate, Action<int, int> texel)
    {
        if (!float.IsFinite(a.X + a.Y + b.X + b.Y + c.X + c.Y)) return;
        dilate = MathF.Max(dilate, 0f);
        float area2 = Cross(b - a, c - a);
        if (area2 < 0)
        {
            (b, c) = (c, b);
            area2 = -area2;
        }
        bool degenerate = area2 < 1e-8f;
        if (degenerate && dilate <= 0) return;

        // Inward unit normals of the edges a->b, b->c, c->a (a zero-length edge gets none and never rejects).
        Vector2 n0 = InwardNormal(a, b), n1 = InwardNormal(b, c), n2 = InwardNormal(c, a);
        float d2 = dilate * dilate;

        Vector2 lo = Vector2.Min(Vector2.Min(a, b), c) - new Vector2(dilate);
        Vector2 hi = Vector2.Max(Vector2.Max(a, b), c) + new Vector2(dilate);
        // Texel x has its centre at x + 0.5: the centres inside [lo, hi].
        int x0 = (int)MathF.Ceiling(lo.X - 0.5f), x1 = (int)MathF.Floor(hi.X - 0.5f);
        int y0 = (int)MathF.Ceiling(lo.Y - 0.5f), y1 = (int)MathF.Floor(hi.Y - 0.5f);
        Span<Vector2> starts = stackalloc[] { a, b, c };
        Span<Vector2> normals = stackalloc[] { n0, n1, n2 };
        for (int y = y0; y <= y1; y++)
        {
            float py = y + 0.5f;
            // The run of centres on this row that no edge rejects: each edge bounds x from one side.
            float from = x0, to = x1;
            for (int e = 0; e < 3; e++)
            {
                Vector2 n = normals[e];
                // n.X * (px - v.X) + n.Y * (py - v.Y) >= -dilate
                float k = n.Y * (py - starts[e].Y) + dilate;
                if (n.X > 1e-6f) from = MathF.Max(from, starts[e].X - k / n.X - 0.5f);
                else if (n.X < -1e-6f) to = MathF.Min(to, starts[e].X - k / n.X - 0.5f);
                else if (k < -1e-6f) to = from - 1;
            }
            if (to < from) continue;
            // A texel of slack each side: the exact test below decides.
            int rx0 = Math.Max(x0, (int)MathF.Floor(from) - 1), rx1 = Math.Min(x1, (int)MathF.Ceiling(to) + 1);
            int wy = ((y % height) + height) % height;
            int wx = ((rx0 % width) + width) % width;
            for (int x = rx0; x <= rx1; x++, wx = wx + 1 == width ? 0 : wx + 1)
            {
                var p = new Vector2(x + 0.5f, py);
                float s0 = Vector2.Dot(p - a, n0), s1 = Vector2.Dot(p - b, n1), s2 = Vector2.Dot(p - c, n2);
                if (s0 < -dilate || s1 < -dilate || s2 < -dilate) continue;
                bool inside = !degenerate && s0 >= 0 && s1 >= 0 && s2 >= 0;
                if (!inside)
                {
                    if (dilate <= 0) continue;
                    float best = MathF.Min(SegmentDistanceSquared(p, a, b), MathF.Min(SegmentDistanceSquared(p, b, c), SegmentDistanceSquared(p, c, a)));
                    if (best > d2) continue;
                }
                texel(wx, wy);
            }
        }
    }

    private static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;

    /// <summary>For a triangle with positive <see cref="Cross"/> area: the unit normal of edge (p, q) facing inside.</summary>
    private static Vector2 InwardNormal(Vector2 p, Vector2 q)
    {
        Vector2 e = q - p;
        float len = e.Length();
        return len < 1e-12f ? Vector2.Zero : new Vector2(-e.Y, e.X) / len;
    }

    private static float SegmentDistanceSquared(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len = ab.LengthSquared();
        float t = len < 1e-12f ? 0f : Math.Clamp(Vector2.Dot(p - a, ab) / len, 0f, 1f);
        return Vector2.DistanceSquared(p, a + ab * t);
    }
}
