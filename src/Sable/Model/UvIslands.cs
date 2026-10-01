using System.Numerics;
using System.Runtime.CompilerServices;

namespace Sable.Model;

/// <summary>
/// UV islands and polygons (Blender's faces) of a part, rebuilt from its triangles. Triangles are joined across an
/// edge only when both of its ends match in position and in UV. Vertex indices aren't trusted for this: Assimp also
/// splits vertices at hard normals, which would break a flat-shaded model into single faces. Built once per part on
/// first use and cached.
/// </summary>
public static class UvIslands
{
    /// <summary>Most triangles <see cref="FaceTriangles"/> gathers, in case a big flat area has no seams to stop it.</summary>
    public const int MaxFaceTriangles = 64;

    /// <summary>Normals at least this aligned (about 1.8°) count as the same plane.</summary>
    private const float SamePlane = 0.9995f;

    /// <summary>Corner normals at least this aligned make a triangle flat-shaded.</summary>
    private const float SameCornerNormal = 0.99999f;

    private sealed class Data
    {
        public required int[] Island;
        public required int IslandCount;
        /// <summary>Triangles across each triangle's UV-continuous edges: <c>Neighbours[Start[t] .. Start[t + 1]]</c>.</summary>
        public required int[] Start;
        public required int[] Neighbours;
        /// <summary>Unit geometric normal per triangle; zero for a degenerate one.</summary>
        public required Vector3[] Normal;
        /// <summary>The vertex normal of a flat-shaded triangle (the same at all three corners); zero otherwise.</summary>
        public required Vector3[] FlatNormal;
    }

    private static readonly ConditionalWeakTable<MeshPart, Data> cache = new();

    /// <summary>
    /// The UV island of each triangle, numbered from 0 in order of first triangle. The array is shared by later
    /// calls; don't change it. Without UVs, islands are the pieces joined by position alone.
    /// </summary>
    public static int[] IslandOfTriangle(MeshPart part, out int count)
    {
        var data = Get(part);
        count = data.IslandCount;
        return data.Island;
    }

    /// <summary>
    /// The polygon <paramref name="triangle"/> was triangulated from, the triangle itself included, so a triangulated
    /// quad comes back as its two triangles and an n-gon whole (at most <see cref="MaxFaceTriangles"/>). The file
    /// no longer says where polygons were, so this rebuilds them from three things that hold for a polygon's
    /// triangles:
    /// <list type="bullet">
    /// <item>They are consecutive in the index buffer (Assimp and Blender's exporters triangulate polygon by
    /// polygon), so the result is a run of triangle numbers around <paramref name="triangle"/>.</item>
    /// <item>Each joins the run across an edge that matches in position and UV.</item>
    /// <item>They lie in its plane (normals within about 1.8°), or, for a flat-shaded polygon that isn't quite planar,
    /// carry the same vertex normal at every corner.</item>
    /// </list>
    /// Neighbouring polygons that are coplanar, share their UVs and happen to be consecutive come back together, and a
    /// model whose triangles were reordered after triangulation (a vertex-cache optimizer) comes back in pieces.
    /// </summary>
    public static List<int> FaceTriangles(MeshPart part, int triangle)
    {
        var data = Get(part);
        int lo = triangle, hi = triangle;
        if (data.Normal[triangle] != Vector3.Zero || data.FlatNormal[triangle] != Vector3.Zero)
        {
            bool grew = true;
            while (grew)
            {
                grew = false;
                if (hi - lo + 1 < MaxFaceTriangles && lo > 0 && Joins(data, triangle, lo - 1, lo, hi))
                {
                    lo--;
                    grew = true;
                }
                if (hi - lo + 1 < MaxFaceTriangles && hi + 1 < part.TriangleCount && Joins(data, triangle, hi + 1, lo, hi))
                {
                    hi++;
                    grew = true;
                }
            }
        }
        var result = new List<int>(hi - lo + 1);
        for (int t = lo; t <= hi; t++) result.Add(t);
        return result;
    }

    /// <summary>Whether triangle <paramref name="t"/> continues the polygon of <paramref name="seed"/>, whose run so far is lo..hi.</summary>
    private static bool Joins(Data data, int seed, int t, int lo, int hi)
    {
        bool plane = data.Normal[seed] != Vector3.Zero && Vector3.Dot(data.Normal[t], data.Normal[seed]) > SamePlane;
        bool flat = data.FlatNormal[seed] != Vector3.Zero && Vector3.Dot(data.FlatNormal[t], data.FlatNormal[seed]) > SamePlane;
        if (!plane && !flat) return false;
        for (int k = data.Start[t]; k < data.Start[t + 1]; k++)
            if (data.Neighbours[k] >= lo && data.Neighbours[k] <= hi) return true;
        return false;
    }

    private static Data Get(MeshPart part) => cache.GetValue(part, Build);

    private static Data Build(MeshPart part)
    {
        var positions = part.Positions;
        var uvs = part.Uvs;
        var indices = part.Indices;
        int triangles = part.TriangleCount;

        // Weld vertices that agree in position (on a grid 1e-5 of the part's size) and UV (1e-6).
        Vector3 extent = part.Max - part.Min;
        float cell = MathF.Max(MathF.Max(extent.X, extent.Y), extent.Z) * 1e-5f;
        if (!(cell > 0)) cell = 1e-5f;
        var weld = new int[positions.Length];
        var ids = new Dictionary<(long, long, long, long, long), int>(positions.Length);
        for (int i = 0; i < positions.Length; i++)
        {
            Vector3 p = (positions[i] - part.Min) / cell;
            Vector2 uv = uvs != null ? uvs[i] * 1e6f : Vector2.Zero;
            var key = ((long)MathF.Round(p.X), (long)MathF.Round(p.Y), (long)MathF.Round(p.Z), (long)MathF.Round(uv.X), (long)MathF.Round(uv.Y));
            if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
            weld[i] = id;
        }

        // Every edge keyed by its welded ends; sorting puts the triangles sharing an edge next to each other.
        var keys = new long[triangles * 3];
        var owners = new int[triangles * 3];
        int edges = 0;
        for (int t = 0; t < triangles; t++)
        {
            for (int e = 0; e < 3; e++)
            {
                int a = weld[indices[t * 3 + e]], b = weld[indices[t * 3 + (e + 1) % 3]];
                if (a == b) continue;
                keys[edges] = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                owners[edges++] = t;
            }
        }
        Array.Sort(keys, owners, 0, edges);

        var parent = new int[triangles];
        for (int i = 0; i < triangles; i++) parent[i] = i;
        int Find(int x)
        {
            while (parent[x] != x) x = parent[x] = parent[parent[x]];
            return x;
        }

        // Neighbour pairs (both directions), counted first so they land in one flat array per triangle.
        var count = new int[triangles + 1];
        for (int i = 0; i < edges;)
        {
            int j = i + 1;
            while (j < edges && keys[j] == keys[i]) j++;
            for (int u = i; u < j; u++)
            for (int v = u + 1; v < j; v++)
            {
                int ta = owners[u], tb = owners[v];
                if (ta == tb) continue;
                count[ta]++;
                count[tb]++;
                parent[Find(tb)] = Find(ta);
            }
            i = j;
        }
        var start = new int[triangles + 1];
        for (int t = 0; t < triangles; t++) start[t + 1] = start[t] + count[t];
        var neighbours = new int[start[triangles]];
        Array.Copy(start, count, triangles);
        for (int i = 0; i < edges;)
        {
            int j = i + 1;
            while (j < edges && keys[j] == keys[i]) j++;
            for (int u = i; u < j; u++)
            for (int v = u + 1; v < j; v++)
            {
                int ta = owners[u], tb = owners[v];
                if (ta == tb) continue;
                neighbours[count[ta]++] = tb;
                neighbours[count[tb]++] = ta;
            }
            i = j;
        }

        var island = new int[triangles];
        var label = new int[triangles];
        Array.Fill(label, -1);
        int islands = 0;
        for (int t = 0; t < triangles; t++)
        {
            int root = Find(t);
            if (label[root] < 0) label[root] = islands++;
            island[t] = label[root];
        }

        var normal = new Vector3[triangles];
        var flat = new Vector3[triangles];
        for (int t = 0; t < triangles; t++)
        {
            Vector3 a = positions[indices[t * 3]], b = positions[indices[t * 3 + 1]], c = positions[indices[t * 3 + 2]];
            Vector3 n = Vector3.Cross(b - a, c - a);
            float len = n.Length();
            normal[t] = len > 1e-20f && float.IsFinite(len) ? n / len : Vector3.Zero;

            Vector3 na = part.Normals[indices[t * 3]], nb = part.Normals[indices[t * 3 + 1]], nc = part.Normals[indices[t * 3 + 2]];
            bool same = Vector3.Dot(na, nb) > SameCornerNormal && Vector3.Dot(na, nc) > SameCornerNormal && float.IsFinite(na.X + na.Y + na.Z);
            flat[t] = same ? Vector3.Normalize(na + nb + nc) : Vector3.Zero;
        }

        return new Data { Island = island, IslandCount = islands, Start = start, Neighbours = neighbours, Normal = normal, FlatNormal = flat };
    }
}
