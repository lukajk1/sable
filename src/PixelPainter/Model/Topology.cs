using System.Numerics;

namespace PixelPainter.Model;

/// <summary>Splits a mesh into loose connected pieces (submeshes).</summary>
public static class Topology
{
    /// <summary>
    /// Labels each triangle with its piece. Vertices are welded by position first: exporters split vertices along UV
    /// seams and hard edges, and those shouldn't break a piece apart.
    /// </summary>
    public static int[] Components(Vector3[] positions, int[] indices, out int count)
    {
        // Weld positions on a grid far finer than any modelled detail.
        var weld = new int[positions.Length];
        var ids = new Dictionary<(long, long, long), int>();
        const float cell = 1e-4f;
        for (int i = 0; i < positions.Length; i++)
        {
            var p = positions[i];
            var key = ((long)MathF.Round(p.X / cell), (long)MathF.Round(p.Y / cell), (long)MathF.Round(p.Z / cell));
            if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
            weld[i] = id;
        }

        var parent = new int[ids.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        int triangles = indices.Length / 3;
        for (int t = 0; t < triangles; t++)
        {
            int a = Find(weld[indices[t * 3]]);
            int b = Find(weld[indices[t * 3 + 1]]);
            int c = Find(weld[indices[t * 3 + 2]]);
            parent[b] = a;
            parent[Find(c)] = a;
        }

        var label = new Dictionary<int, int>();
        var result = new int[triangles];
        for (int t = 0; t < triangles; t++)
        {
            int root = Find(weld[indices[t * 3]]);
            if (!label.TryGetValue(root, out int l)) label[root] = l = label.Count;
            result[t] = l;
        }
        count = label.Count;
        return result;
    }
}
