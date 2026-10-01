using System.Diagnostics;
using System.Numerics;
using Raylib_cs;
using Sable.Model;

namespace Sable.Paint;

/// <summary>
/// Checks <see cref="UvIslands"/>, <see cref="UvRaster"/> and <see cref="EdgePadding"/> on a loaded model and on
/// synthetic data, as readable lines (failures start with "FAIL").
/// </summary>
public static class UvToolsCheck
{
    public static IEnumerable<string> Run(LoadedModel model)
    {
        var lines = new List<string>();
        Islands(model, lines);
        Coverage(model, lines);
        PaddingRules(lines);
        PaddingTiming(model, lines);
        return lines;
    }

    private static void Islands(LoadedModel model, List<string> lines)
    {
        foreach (var part in model.Parts)
        {
            if (part.Uvs == null)
            {
                lines.Add($"{part.Name}: no UVs");
                continue;
            }
            var clock = Stopwatch.StartNew();
            var island = UvIslands.IslandOfTriangle(part, out int islands);
            double build = clock.Elapsed.TotalMilliseconds;
            clock.Restart();
            UvIslands.IslandOfTriangle(part, out _);
            double cached = clock.Elapsed.TotalMilliseconds;

            var sizes = new int[islands];
            foreach (int i in island) sizes[i]++;
            int single = sizes.Count(s => s == 1);

            // Faces of up to 4000 triangles spread over the part, by size.
            int samples = Math.Min(part.TriangleCount, 4000);
            var bySize = new SortedDictionary<int, int>();
            bool sameIsland = true, containsSelf = true;
            double polygons = 0;
            clock.Restart();
            for (int s = 0; s < samples; s++)
            {
                int t = (int)((long)s * part.TriangleCount / samples);
                var face = UvIslands.FaceTriangles(part, t);
                bySize[face.Count] = bySize.GetValueOrDefault(face.Count) + 1;
                polygons += 1.0 / face.Count;
                containsSelf &= face.Contains(t);
                sameIsland &= face.All(f => island[f] == island[t]);
            }
            double faceMs = clock.Elapsed.TotalMilliseconds;
            var sizeText = string.Join(", ", bySize.Select(p => $"{p.Key}:{p.Value}"));
            lines.Add($"{part.Name}: {part.TriangleCount} tris, {islands} UV islands ({single} single-triangle, largest {sizes.DefaultIfEmpty(0).Max()} tris), "
                      + $"built in {build:0.0} ms (cached {cached:0.000} ms)");
            string estimate = samples == part.TriangleCount ? $", about {polygons:0} polygons" : "";
            lines.Add($"  face size of {samples} triangles, size:triangles {sizeText}{estimate} ({faceMs / Math.Max(samples, 1) * 1000:0.0} us each)");
            if (!containsSelf || !sameIsland) lines.Add($"FAIL {part.Name}: a face left its triangle or its island");
        }
    }

    private static void Coverage(LoadedModel model, List<string> lines)
    {
        foreach (int size in new[] { 64, 256 })
        {
            int covered = Count(UvRaster.Coverage(model, _ => true, size, size));
            int dilated = Count(UvRaster.Coverage(model, _ => true, size, size, 1f));
            // The texels the UV area alone would fill, ignoring overlaps: an upper bound (roughly) for the undilated count.
            double area = 0;
            foreach (var part in model.Parts)
            {
                if (part.Uvs == null) continue;
                for (int t = 0; t < part.TriangleCount; t++)
                {
                    Vector2 a = part.Uvs[part.Indices[t * 3]], b = part.Uvs[part.Indices[t * 3 + 1]], c = part.Uvs[part.Indices[t * 3 + 2]];
                    area += MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (c.X - a.X) * (b.Y - a.Y)) * 0.5f;
                }
            }
            lines.Add($"coverage {size}x{size}: {covered} texels at the centre, {dilated} dilated by 1 texel; UV area {area * size * size:0} texels");
            if (dilated < covered) lines.Add($"FAIL coverage {size}: dilating lost texels");
        }

        // A unit square as two triangles covers every texel exactly; a zero-area triangle covers none undilated.
        var mask = new bool[16 * 16];
        void Mark(int x, int y) => mask[y * 16 + x] = true;
        UvRaster.Triangle(new Vector2(0, 0), new Vector2(1, 0), new Vector2(1, 1), 16, 16, Mark);
        UvRaster.Triangle(new Vector2(0, 0), new Vector2(1, 1), new Vector2(0, 1), 16, 16, Mark);
        int square = Count(mask);
        int line = 0, lineDilated = 0;
        UvRaster.Triangle(new Vector2(0.1f, 0.5f), new Vector2(0.9f, 0.5f), new Vector2(0.5f, 0.5f), 16, 16, (_, _) => line++);
        UvRaster.Triangle(new Vector2(0.1f, 0.5f), new Vector2(0.9f, 0.5f), new Vector2(0.5f, 0.5f), 16, 16, (_, _) => lineDilated++, 0.5f);
        // Shifted by whole UV units: the same texels, wrapped.
        var shifted = new bool[16 * 16];
        UvRaster.Triangle(new Vector2(2.1f, -0.9f), new Vector2(2.6f, -0.9f), new Vector2(2.1f, -0.4f), 16, 16, (x, y) => shifted[y * 16 + x] = true);
        var plain = new bool[16 * 16];
        UvRaster.Triangle(new Vector2(0.1f, 0.1f), new Vector2(0.6f, 0.1f), new Vector2(0.1f, 0.6f), 16, 16, (x, y) => plain[y * 16 + x] = true);
        bool wraps = shifted.AsSpan().SequenceEqual(plain);

        // Random triangles (some degenerate) against a brute-force distance test over every texel.
        var random = new Random(7);
        int mismatches = 0, tested = 0;
        var hit = new bool[64 * 64];
        for (int r = 0; r < 300; r++)
        {
            Vector2 Next() => new((float)random.NextDouble() * 0.8f + 0.1f, (float)random.NextDouble() * 0.8f + 0.1f);
            Vector2 a = Next(), b = Next(), c = r % 5 == 0 ? Vector2.Lerp(a, b, (float)random.NextDouble()) : Next();
            if (r % 7 == 0) b = c = a;
            float dilate = r % 3 == 0 ? 0f : (float)random.NextDouble() * 3f;
            Array.Clear(hit);
            UvRaster.Triangle(a, b, c, 64, 64, (x, y) => hit[y * 64 + x] = true, dilate);
            Vector2 ta = a * 64, tb = b * 64, tc = c * 64;
            for (int i = 0; i < hit.Length; i++)
            {
                var p = new Vector2(i % 64 + 0.5f, i / 64 + 0.5f);
                float d = DistanceToTriangle(p, ta, tb, tc);
                // Skip centres within float noise of the boundary.
                if (MathF.Abs(d - dilate) < 1e-3f) continue;
                tested++;
                if ((d <= dilate) != hit[i]) mismatches++;
            }
        }
        lines.Add($"raster: unit square {square}/256 texels, zero-area line {line} texels (dilated 0.5: {lineDilated}), wrapped UVs match: {wraps}, "
                  + $"300 random triangles vs brute force: {mismatches} of {tested} texels differ");
        if (square != 256 || line != 0 || lineDilated == 0 || !wraps || mismatches > 0) lines.Add("FAIL raster");
    }

    /// <summary>0 inside a non-degenerate triangle, else the distance to its outline.</summary>
    private static float DistanceToTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        static float Cross(Vector2 u, Vector2 v) => u.X * v.Y - u.Y * v.X;
        float area = Cross(b - a, c - a);
        if (MathF.Abs(area) >= 1e-8f)
        {
            float e0 = Cross(b - a, p - a) * area, e1 = Cross(c - b, p - b) * area, e2 = Cross(a - c, p - c) * area;
            if (e0 >= 0 && e1 >= 0 && e2 >= 0) return 0;
        }
        static float Segment(Vector2 p, Vector2 a, Vector2 b)
        {
            Vector2 ab = b - a;
            float len = ab.LengthSquared();
            float t = len < 1e-12f ? 0 : Math.Clamp(Vector2.Dot(p - a, ab) / len, 0, 1);
            return Vector2.Distance(p, a + ab * t);
        }
        return MathF.Min(Segment(p, a, b), MathF.Min(Segment(p, b, c), Segment(p, c, a)));
    }

    private static void PaddingRules(List<string> lines)
    {
        // One opaque red texel in the middle of 16x16: Chebyshev rings 1-2 turn red, ring 3 keeps its colour.
        const int n = 16;
        var pixels = new Color[n * n];
        Array.Fill(pixels, new Color(0, 0, 255, 0));
        var inside = new bool[n * n];
        pixels[8 * n + 8] = new Color(255, 0, 0, 255);
        inside[8 * n + 8] = true;
        var before = (Color[])pixels.Clone();
        var padded = EdgePadding.Pad(pixels, n, n, inside, 2);
        bool ok = pixels.AsSpan().SequenceEqual(before);
        for (int y = 0; y < n; y++)
        for (int x = 0; x < n; x++)
        {
            int d = Math.Max(Math.Abs(x - 8), Math.Abs(y - 8));
            var c = padded[y * n + x];
            ok &= d <= 2 ? c.R == 255 && c.A == 255 && c.B == 0 : c.Equals(before[y * n + x]);
        }

        // Alpha weighting: between an opaque red and a transparent black inside texel, the bleed stays pure red.
        var pair = new Color[3];
        pair[0] = new Color(255, 0, 0, 255);
        pair[2] = new Color(0, 0, 0, 0);
        var mid = EdgePadding.Pad(pair, 3, 1, new[] { true, false, true }, 1)[1];
        bool weighted = mid.R == 255 && mid.G == 0 && mid.B == 0 && mid.A is >= 127 and <= 128;

        // Wrapping: an inside texel at the left edge pads the right edge only when asked to.
        var edge = new Color[8];
        edge[0] = new Color(0, 255, 0, 255);
        var edgeInside = new bool[8];
        edgeInside[0] = true;
        bool wraps = EdgePadding.Pad(edge, 8, 1, edgeInside, 1, wrap: true)[7].G == 255
                     && EdgePadding.Pad(edge, 8, 1, edgeInside, 1)[7].G == 0;
        lines.Add($"padding rules: rings and untouched input {Pass(ok)}, alpha-weighted average {Pass(weighted)} ({mid.R},{mid.G},{mid.B},{mid.A}), wrap {Pass(wraps)}");
        if (!ok || !weighted || !wraps) lines.Add("FAIL padding rules");
    }

    private static void PaddingTiming(LoadedModel model, List<string> lines)
    {
        const int size = 4096;
        var clock = Stopwatch.StartNew();
        var inside = UvRaster.Coverage(model, _ => true, size, size);
        double coverMs = clock.Elapsed.TotalMilliseconds;
        string source = "the model's UVs";
        if (Count(inside) == 0)
        {
            // No UVs: discs scattered over the texture instead.
            source = "synthetic discs";
            var random = new Random(1);
            for (int d = 0; d < 400; d++)
            {
                int cx = random.Next(size), cy = random.Next(size), r = random.Next(8, 120);
                for (int y = Math.Max(cy - r, 0); y < Math.Min(cy + r, size); y++)
                for (int x = Math.Max(cx - r, 0); x < Math.Min(cx + r, size); x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) inside[y * size + x] = true;
            }
        }

        var pixels = new Color[size * size];
        for (int i = 0; i < pixels.Length; i++)
            pixels[i] = inside[i] ? new Color((byte)(i & 255), (byte)((i >> 8) & 255), (byte)((i >> 16) & 255), (byte)255) : new Color(0, 0, 0, 0);

        foreach (int distance in new[] { 16, 64 })
        {
            clock.Restart();
            var padded = EdgePadding.Pad(pixels, size, size, inside, distance);
            double padMs = clock.Elapsed.TotalMilliseconds;

            // Inside untouched; every outside texel next to an inside one filled (opaque, as every inside texel is).
            bool untouched = true, ringFilled = true;
            int changed = 0;
            Parallel.For(0, size, () => (true, true, 0), (y, _, local) =>
            {
                for (int x = 0; x < size; x++)
                {
                    int i = y * size + x;
                    if (inside[i])
                    {
                        local.Item1 &= padded[i].Equals(pixels[i]);
                        continue;
                    }
                    if (padded[i].A != 0) local.Item3++;
                    bool touches = false;
                    for (int dy = -1; dy <= 1 && !touches; dy++)
                    for (int dx = -1; dx <= 1 && !touches; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        touches = nx >= 0 && ny >= 0 && nx < size && ny < size && inside[ny * size + nx];
                    }
                    if (touches) local.Item2 &= padded[i].A == 255;
                }
                return local;
            }, local =>
            {
                lock (lines)
                {
                    untouched &= local.Item1;
                    ringFilled &= local.Item2;
                    changed += local.Item3;
                }
            });
            lines.Add($"padding {size}x{size} ({source}, {Count(inside)} inside, mask built in {coverMs:0} ms), {distance} rings: {padMs:0} ms, "
                      + $"{changed} texels padded; inside untouched {Pass(untouched)}, first ring filled {Pass(ringFilled)}");
            if (!untouched || !ringFilled) lines.Add($"FAIL padding {distance}");
        }
    }

    private static string Pass(bool ok) => ok ? "ok" : "failed";

    private static int Count(bool[] mask)
    {
        int count = 0;
        foreach (bool b in mask) if (b) count++;
        return count;
    }
}
