using System.Numerics;

namespace Sable.Model;

/// <summary>
/// Per-submesh geometry checks, printed by <c>--check</c>: triangles whose winding disagrees with their vertex normals
/// (flipped faces), and triangles with no UV area.
/// </summary>
public static class MeshCheck
{
    public static void Print(LoadedModel model)
    {
        foreach (var (a, b, shared) in UvOverlaps(model, 256))
            Console.WriteLine($"[check] UV overlap between materials {model.Materials[a].Name} and {model.Materials[b].Name}: {shared} of 256x256 cells");
        foreach (var part in model.Parts)
        {
            var tris = new int[part.ComponentCount];
            var flipped = new int[part.ComponentCount];
            var noUv = new int[part.ComponentCount];
            var uvArea = new double[part.ComponentCount];
            var min = new Vector3[part.ComponentCount];
            var max = new Vector3[part.ComponentCount];
            Array.Fill(min, new Vector3(float.MaxValue));
            Array.Fill(max, new Vector3(float.MinValue));
            for (int t = 0; t < part.TriangleCount; t++)
            {
                int c = part.TriangleComponent[t];
                int i0 = part.Indices[t * 3], i1 = part.Indices[t * 3 + 1], i2 = part.Indices[t * 3 + 2];
                Vector3 a = part.Positions[i0], b = part.Positions[i1], d = part.Positions[i2];
                tris[c]++;
                min[c] = Vector3.Min(min[c], Vector3.Min(a, Vector3.Min(b, d)));
                max[c] = Vector3.Max(max[c], Vector3.Max(a, Vector3.Max(b, d)));
                var winding = Vector3.Cross(b - a, d - a);
                var smooth = part.Normals[i0] + part.Normals[i1] + part.Normals[i2];
                if (Vector3.Dot(winding, smooth) < 0) flipped[c]++;
                if (part.Uvs != null)
                {
                    Vector2 u0 = part.Uvs[i0], u1 = part.Uvs[i1], u2 = part.Uvs[i2];
                    float area = MathF.Abs((u1.X - u0.X) * (u2.Y - u0.Y) - (u2.X - u0.X) * (u1.Y - u0.Y)) * 0.5f;
                    uvArea[c] += area;
                    if (area < 1e-9f) noUv[c]++;
                }
            }
            for (int c = 0; c < part.ComponentCount; c++)
                Console.WriteLine($"[check] {part.Name} #{c}: {tris[c]} tris, flipped {flipped[c]}, zero-UV {noUv[c]}, "
                                  + $"UV area {uvArea[c]:0.00000}, centre {(min[c] + max[c]) * 0.5f:0.00}, size {max[c] - min[c]:0.00}");
        }
    }

    /// <summary>
    /// Pairs of materials whose UVs cover the same cells of a <paramref name="grid"/>-square raster of 0..1 UV space
    /// (so one texture shared between them would paint both), with how many cells they share.
    /// </summary>
    public static List<(int A, int B, int Cells)> UvOverlaps(LoadedModel model, int grid)
    {
        var owners = new Dictionary<int, HashSet<int>>();
        foreach (var part in model.Parts)
        {
            if (part.Uvs == null) continue;
            for (int t = 0; t < part.TriangleCount; t++)
            {
                Vector2 a = part.Uvs[part.Indices[t * 3]] * grid, b = part.Uvs[part.Indices[t * 3 + 1]] * grid, c = part.Uvs[part.Indices[t * 3 + 2]] * grid;
                int x0 = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), x1 = (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X)));
                int y0 = (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), y1 = (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y)));
                for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++)
                {
                    // Cell centre inside the triangle (edge functions, either winding).
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    float e0 = Edge(a, b, p), e1 = Edge(b, c, p), e2 = Edge(c, a, p);
                    if (!((e0 >= 0 && e1 >= 0 && e2 >= 0) || (e0 <= 0 && e1 <= 0 && e2 <= 0))) continue;
                    int key = (((y % grid) + grid) % grid) * grid + (((x % grid) + grid) % grid);
                    if (!owners.TryGetValue(key, out var set)) owners[key] = set = new HashSet<int>();
                    set.Add(part.MaterialIndex);
                }
            }
        }
        var pairs = new Dictionary<(int, int), int>();
        foreach (var set in owners.Values)
        {
            if (set.Count < 2) continue;
            var list = set.OrderBy(m => m).ToList();
            for (int i = 0; i < list.Count; i++)
            for (int j = i + 1; j < list.Count; j++)
                pairs[(list[i], list[j])] = pairs.GetValueOrDefault((list[i], list[j])) + 1;
        }
        return pairs.Select(kv => (kv.Key.Item1, kv.Key.Item2, kv.Value)).ToList();
    }

    private static float Edge(Vector2 a, Vector2 b, Vector2 p) => (b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X);
}
