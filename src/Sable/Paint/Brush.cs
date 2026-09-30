using System.Numerics;
using Sable.Model;

namespace Sable.Paint;

public enum Tool { Select, Pencil, Brush, Eraser, Eyedropper, Fill, Zoom, Lasso, BoxSelect }

/// <summary>
/// How the paint tools mark texels. The pencil sets exactly one texel, fully opaque. The brush is round, sized in
/// texels, with a hardness from soft (0) to a hard edge (1); each texel takes the share of it the brush covers
/// (averaged over sample points inside the texel), so even a 1-2 texel brush lays down partial, blended colour.
/// On the model it measures distance along the surface, so a dab continues across UV seams onto whichever island
/// continues the surface.
/// </summary>
public static class Brush
{
    private static readonly float FalloffFloor = MathF.Exp(-4.5f);

    /// <summary>
    /// Brush shape at distance <paramref name="t"/> (0 centre, 1 rim): solid out to <paramref name="hardness"/>,
    /// then a Gaussian-like fade that drops quickly and trails off thin, so a soft brush has a real core and
    /// feathered edges rather than a wide band of mid values.
    /// </summary>
    public static float Falloff(float t, float hardness)
    {
        if (t > 1f) return 0f;
        if (t <= hardness || hardness >= 1f) return 1f;
        float x = (t - hardness) / (1f - hardness);
        return (MathF.Exp(-4.5f * x * x) - FalloffFloor) / (1f - FalloffFloor);
    }

    /// <summary>
    /// Snaps a brush centre (in texels) so odd sizes centre on a texel and even sizes on a texel corner, which keeps
    /// pixel circles symmetric.
    /// </summary>
    public static Vector2 SnapCenter(Vector2 texel, float size)
    {
        bool odd = ((int)MathF.Round(size) & 1) == 1;
        return odd
            ? new Vector2(MathF.Floor(texel.X) + 0.5f, MathF.Floor(texel.Y) + 0.5f)
            : new Vector2(MathF.Round(texel.X), MathF.Round(texel.Y));
    }

    /// <summary>
    /// Sample points per texel side: fine for small brushes, where one texel is a big share of the dab, and a single
    /// point for big ones, where the edge spans many texels anyway.
    /// </summary>
    private static int Samples(float size) => size <= 16 ? 4 : size <= 48 ? 2 : 1;

    // Triangles near the current dab; main thread only.
    private static readonly List<int> nearby = new();

    /// <summary>A round dab in texture space (the UV view).</summary>
    public static void DabTexels(Stroke stroke, Vector2 center, float size, float hardness)
    {
        Diagnostics.FrameProfiler.Dabs++;
        float r = MathF.Max(size * 0.5f, 0.5f);
        int n = Samples(size);
        int x0 = (int)MathF.Floor(center.X - r), x1 = (int)MathF.Ceiling(center.X + r);
        int y0 = (int)MathF.Floor(center.Y - r), y1 = (int)MathF.Ceiling(center.Y + r);
        var tex = stroke.Texture;
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            if (x < 0 || y < 0 || x >= tex.Width || y >= tex.Height) continue;
            float sum = 0;
            for (int sy = 0; sy < n; sy++)
            for (int sx = 0; sx < n; sx++)
            {
                var p = new Vector2(x + (sx + 0.5f) / n, y + (sy + 0.5f) / n);
                sum += Falloff(Vector2.Distance(p, center) / r, hardness);
            }
            stroke.Apply(x, y, sum / (n * n));
        }
    }

    /// <summary>
    /// Bucket fill from texel (<paramref name="x"/>, <paramref name="y"/>): every texel within
    /// <paramref name="tolerance"/> (0..1, the largest channel difference) of its colour, either only those connected
    /// to it through 4-way neighbours or, with <paramref name="contiguous"/> off, all of them. The stroke's mask (a
    /// selection) limits it; clicking outside the selection fills nothing.
    /// </summary>
    /// <param name="sample">The colours compared (all layers flattened); the painted layer's own when null.</param>
    public static void Flood(Stroke stroke, int x, int y, float tolerance, bool contiguous, bool[]? mask, Raylib_cs.Color[]? sample = null)
    {
        var tex = stroke.Texture;
        int w = tex.Width, h = tex.Height;
        if (x < 0 || y < 0 || x >= w || y >= h) return;
        if (mask != null && !mask[y * w + x]) return;

        var pixels = sample ?? stroke.Layer.Pixels;
        var target = pixels[y * w + x];
        int limit = (int)MathF.Round(Math.Clamp(tolerance, 0f, 1f) * 255f);
        bool Matches(int i)
        {
            var c = pixels[i];
            return (mask == null || mask[i])
                   && Math.Abs(c.R - target.R) <= limit && Math.Abs(c.G - target.G) <= limit
                   && Math.Abs(c.B - target.B) <= limit && Math.Abs(c.A - target.A) <= limit;
        }

        // Collect first: painting as we go would change what later texels are compared against.
        var fill = new List<int>();
        if (!contiguous)
        {
            for (int i = 0; i < pixels.Length; i++)
                if (Matches(i)) fill.Add(i);
        }
        else
        {
            var seen = new bool[pixels.Length];
            var queue = new Queue<int>();
            queue.Enqueue(y * w + x);
            seen[y * w + x] = true;
            while (queue.Count > 0)
            {
                int i = queue.Dequeue();
                fill.Add(i);
                int cx = i % w, cy = i / w;
                if (cx > 0) Visit(i - 1);
                if (cx < w - 1) Visit(i + 1);
                if (cy > 0) Visit(i - w);
                if (cy < h - 1) Visit(i + w);
            }

            void Visit(int n)
            {
                if (seen[n] || !Matches(n)) return;
                seen[n] = true;
                queue.Enqueue(n);
            }
        }
        foreach (int i in fill) stroke.Apply(i % w, i / w, 1f);
    }

    /// <summary>Texels along a line, Bresenham-style (pencil drags in the UV view).</summary>
    public static void Line(Stroke stroke, int x0, int y0, int x1, int y1, bool clip)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;
        var tex = stroke.Texture;
        while (true)
        {
            if (!clip || (x0 >= 0 && y0 >= 0 && x0 < tex.Width && y0 < tex.Height)) stroke.Apply(x0, y0, 1f);
            if (x0 == x1 && y0 == y1) break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    /// <summary>The texel under a surface hit.</summary>
    public static (int X, int Y) TexelAt(LoadedModel model, SurfaceHit hit, Vector2 textureSize)
    {
        var uv = Raycast.UvAt(model.Parts[hit.Part], hit.Triangle, hit.Barycentric) * textureSize;
        return ((int)MathF.Floor(uv.X), (int)MathF.Floor(uv.Y));
    }

    /// <summary>
    /// A round dab on the model around <paramref name="hit"/>, on every visible part of <paramref name="objectIndex"/>
    /// painted with <paramref name="textureIndex"/>. Only faces turned the same way as the hit face are painted, so a
    /// dab doesn't bleed through to the back of a thin wall.
    /// </summary>
    public static void DabSurface(Stroke stroke, LoadedModel model, int objectIndex, int textureIndex, SurfaceHit hit,
        float size, float hardness, Func<int, int> textureOfPart)
    {
        Diagnostics.FrameProfiler.Dabs++;
        var tex = stroke.Texture;
        var textureSize = new Vector2(tex.Width, tex.Height);
        var hitPart = model.Parts[hit.Part];
        float texelWorld = Raycast.TexelWorldSize(hitPart, hit.Triangle, textureSize);
        if (texelWorld <= 0)
        {
            // Degenerate UVs: nothing to measure a round brush against.
            var (tx, ty) = TexelAt(model, hit, textureSize);
            stroke.Apply(tx, ty, 1f);
            return;
        }

        float radius = MathF.Max(size * 0.5f, 0.5f) * texelWorld;
        int n = Samples(size);
        Vector3 center = hit.Point;

        // The triangles near the brush, on this object's parts that use this texture.
        candidates.Clear();
        foreach (int p in model.Objects[objectIndex].Parts)
        {
            var part = model.Parts[p];
            if (part.Uvs == null || textureOfPart(p) != textureIndex) continue;
            nearby.Clear();
            part.Bvh.QuerySphere(center, radius, nearby);
            foreach (int t in nearby)
                if (part.TriangleVisible(t)) candidates.Add((part, t));
        }

        var job = new DabJob(center, radius, texelWorld, hardness, n, hit.Normal, textureSize, tex.Width, tex.Height);
        if (candidates.Count < 64)
        {
            var local = TakeList();
            foreach (var (part, t) in candidates) DabTriangle(part, t, job, local);
            Merge(local);
        }
        else
        {
            // Big dabs: split the triangles across cores, each writing its own list, then merge.
            Parallel.ForEach(System.Collections.Concurrent.Partitioner.Create(0, candidates.Count, 32),
                () => TakeList(),
                (range, _, local) =>
                {
                    for (int i = range.Item1; i < range.Item2; i++) DabTriangle(candidates[i].Part, candidates[i].Triangle, job, local);
                    return local;
                },
                local => { lock (finished) finished.Add(local); });
            foreach (var local in finished) Merge(local);
            finished.Clear();
        }

        // Each texel once per dab, at the strongest coverage any triangle gave it.
        foreach (int i in touched)
        {
            stroke.Apply(i % tex.Width, i / tex.Width, dabShape[i]);
            dabShape[i] = 0;
        }
        touched.Clear();
    }

    private readonly record struct DabJob(Vector3 Center, float Radius, float TexelWorld, float Hardness, int Samples,
        Vector3 HitNormal, Vector2 TextureSize, int Width, int Height);

    private static readonly List<(MeshPart Part, int Triangle)> candidates = new();
    private static readonly List<List<(int Index, float Shape)>> finished = new();
    private static readonly System.Collections.Concurrent.ConcurrentBag<List<(int, float)>> listPool = new();
    private static float[] dabShape = Array.Empty<float>();
    private static readonly List<int> touched = new();

    private static List<(int, float)> TakeList()
    {
        if (!listPool.TryTake(out var list)) list = new List<(int, float)>(256);
        list.Clear();
        return list;
    }

    private static void Merge(List<(int Index, float Shape)> local)
    {
        foreach (var (index, shape) in local)
        {
            if (index >= dabShape.Length) Array.Resize(ref dabShape, Math.Max(index + 1, dabShape.Length * 2));
            if (dabShape[index] == 0) touched.Add(index);
            if (shape > dabShape[index]) dabShape[index] = shape;
        }
        listPool.Add(local);
    }

    /// <summary>One triangle's share of a dab: the texels its surface brings inside the brush, with coverage.</summary>
    private static void DabTriangle(MeshPart part, int t, in DabJob job, List<(int, float)> output)
    {
        var positions = part.Positions;
        var uvs = part.Uvs!;
        var idx = part.Indices;
        Vector3 center = job.Center;
        float radius = job.Radius;
        Vector3 a = positions[idx[t * 3]], b = positions[idx[t * 3 + 1]], c = positions[idx[t * 3 + 2]];
        Vector3 lo = Vector3.Min(Vector3.Min(a, b), c) - new Vector3(radius);
        Vector3 hi = Vector3.Max(Vector3.Max(a, b), c) + new Vector3(radius);
        if (center.X < lo.X || center.Y < lo.Y || center.Z < lo.Z || center.X > hi.X || center.Y > hi.Y || center.Z > hi.Z) return;

        Vector3 normal = Vector3.Cross(b - a, c - a);
        float len = normal.Length();
        if (len < 1e-12f) return;
        normal /= len;
        if (Vector3.Dot(normal, job.HitNormal) < 0.2f) return;

        Vector2 size = job.TextureSize;
        Vector2 ua = uvs[idx[t * 3]] * size, ub = uvs[idx[t * 3 + 1]] * size, uc = uvs[idx[t * 3 + 2]] * size;
        if (!TexelWindow(a, b, c, normal, ua, ub, uc, center, radius, out Vector2 wMin, out Vector2 wMax)) return;

        int n = job.Samples;
        int x0 = (int)MathF.Floor(wMin.X), x1 = (int)MathF.Ceiling(wMax.X);
        int y0 = (int)MathF.Floor(wMin.Y), y1 = (int)MathF.Ceiling(wMax.Y);
        for (int y = y0; y <= y1; y++)
        for (int x = x0; x <= x1; x++)
        {
            var texel = new Vector2(x + 0.5f, y + 0.5f);
            var bary = Raycast.Barycentric2D(texel, ua, ub, uc);
            if (bary.X < 0 || bary.Y < 0 || bary.Z < 0)
            {
                // Texels straddling the island's edge: use the nearest point of the triangle if it is
                // within half a texel diagonal, so island borders get painted too.
                var nearest = ClosestPointOnTriangle(texel, ua, ub, uc);
                if (Vector2.DistanceSquared(nearest, texel) > 0.5f) continue;
                bary = Raycast.Barycentric2D(nearest, ua, ub, uc);
            }
            Vector3 point = a * bary.X + b * bary.Y + c * bary.Z;
            // Skip texels that can't reach the brush (centre farther than the radius plus a texel diagonal).
            if (Vector3.Distance(point, center) > radius + job.TexelWorld * 0.75f) continue;

            // Coverage: sample points inside the texel, placed on the surface through this triangle's plane
            // (extrapolated past its edges, where the neighbouring triangle continues the same island).
            float sum = 0;
            for (int sy = 0; sy < n; sy++)
            for (int sx = 0; sx < n; sx++)
            {
                var q = Raycast.Barycentric2D(new Vector2(x + (sx + 0.5f) / n, y + (sy + 0.5f) / n), ua, ub, uc);
                sum += Falloff(Vector3.Distance(a * q.X + b * q.Y + c * q.Z, center) / radius, job.Hardness);
            }
            if (sum <= 0) continue;
            int wx = ((x % job.Width) + job.Width) % job.Width, wy = ((y % job.Height) + job.Height) % job.Height;
            output.Add((wy * job.Width + wx, sum / (n * n)));
        }
    }

    /// <summary>
    /// The texel rectangle of triangle (a, b, c) that the brush sphere can reach: the sphere's centre projected into
    /// the triangle's UV space, padded by the radius times the largest stretch of the surface-to-texel mapping, and
    /// clipped to the triangle's UV bounds.
    /// </summary>
    private static bool TexelWindow(Vector3 a, Vector3 b, Vector3 c, Vector3 normal, Vector2 ua, Vector2 ub, Vector2 uc,
        Vector3 center, float radius, out Vector2 min, out Vector2 max)
    {
        Vector3 e1 = b - a, e2 = c - a;
        Vector3 axisX = Vector3.Normalize(e1);
        Vector3 axisY = Vector3.Cross(normal, axisX);
        Vector2 w1 = new(e1.Length(), 0), w2 = new(Vector3.Dot(e2, axisX), Vector3.Dot(e2, axisY));
        Vector2 f1 = ub - ua, f2 = uc - ua;

        float det = w1.X * w2.Y - w2.X * w1.Y;
        min = max = default;
        if (MathF.Abs(det) < 1e-12f) return false;
        // M = [f1 f2] * inverse([w1 w2]): surface (in the triangle's plane) to texels.
        float i00 = w2.Y / det, i01 = -w2.X / det, i10 = -w1.Y / det, i11 = w1.X / det;
        float m00 = f1.X * i00 + f2.X * i10, m01 = f1.X * i01 + f2.X * i11;
        float m10 = f1.Y * i00 + f2.Y * i10, m11 = f1.Y * i01 + f2.Y * i11;
        float s = m00 * m00 + m01 * m01 + m10 * m10 + m11 * m11;
        float dm = m00 * m11 - m01 * m10;
        float stretch = MathF.Sqrt((s + MathF.Sqrt(MathF.Max(s * s - 4 * dm * dm, 0))) * 0.5f);

        Vector3 local = center - a;
        Vector2 planar = new(Vector3.Dot(local, axisX), Vector3.Dot(local, axisY));
        Vector2 projected = ua + new Vector2(m00 * planar.X + m01 * planar.Y, m10 * planar.X + m11 * planar.Y);
        float reach = radius * stretch + 1f;

        Vector2 triMin = Vector2.Min(Vector2.Min(ua, ub), uc) - Vector2.One, triMax = Vector2.Max(Vector2.Max(ua, ub), uc) + Vector2.One;
        min = Vector2.Max(projected - new Vector2(reach), triMin);
        max = Vector2.Min(projected + new Vector2(reach), triMax);
        return min.X <= max.X && min.Y <= max.Y;
    }

    private static Vector2 ClosestPointOnTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 best = ClosestOnSegment(p, a, b);
        Vector2 candidate = ClosestOnSegment(p, b, c);
        if (Vector2.DistanceSquared(p, candidate) < Vector2.DistanceSquared(p, best)) best = candidate;
        candidate = ClosestOnSegment(p, c, a);
        if (Vector2.DistanceSquared(p, candidate) < Vector2.DistanceSquared(p, best)) best = candidate;
        return best;
    }

    private static Vector2 ClosestOnSegment(Vector2 p, Vector2 a, Vector2 b)
    {
        Vector2 ab = b - a;
        float len = ab.LengthSquared();
        if (len < 1e-12f) return a;
        float t = Math.Clamp(Vector2.Dot(p - a, ab) / len, 0f, 1f);
        return a + ab * t;
    }
}
