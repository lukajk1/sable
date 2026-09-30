using System.Numerics;

namespace Sable.Model;

/// <summary>
/// A bounding volume hierarchy over a part's triangles, so ray casts and brush dabs only look at triangles near
/// the ray or the brush instead of all of them. Built once when the model loads (median split on the longest axis,
/// up to four triangles per leaf).
/// </summary>
public sealed class TriangleBvh
{
    private struct Node
    {
        public Vector3 Min, Max;
        /// <summary>Leaf: first index into <see cref="order"/>. Inner: index of the left child (right is next).</summary>
        public int Start;
        /// <summary>Triangles in a leaf; 0 for inner nodes.</summary>
        public int Count;
    }

    private const int LeafSize = 4;
    private readonly Node[] nodes;
    private readonly int[] order;
    private int nodeCount;

    public TriangleBvh(Vector3[] positions, int[] indices)
    {
        int triangles = indices.Length / 3;
        order = new int[triangles];
        var centroids = new Vector3[triangles];
        var boxMin = new Vector3[triangles];
        var boxMax = new Vector3[triangles];
        for (int t = 0; t < triangles; t++)
        {
            order[t] = t;
            Vector3 a = positions[indices[t * 3]], b = positions[indices[t * 3 + 1]], c = positions[indices[t * 3 + 2]];
            boxMin[t] = Vector3.Min(Vector3.Min(a, b), c);
            boxMax[t] = Vector3.Max(Vector3.Max(a, b), c);
            centroids[t] = (a + b + c) / 3f;
        }
        nodes = new Node[Math.Max(1, triangles * 2)];
        nodeCount = 1;
        Build(0, 0, triangles, centroids, boxMin, boxMax);
    }

    private void Build(int node, int start, int count, Vector3[] centroids, Vector3[] boxMin, Vector3[] boxMax)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        Vector3 cMin = new(float.MaxValue), cMax = new(float.MinValue);
        for (int i = start; i < start + count; i++)
        {
            int t = order[i];
            min = Vector3.Min(min, boxMin[t]);
            max = Vector3.Max(max, boxMax[t]);
            cMin = Vector3.Min(cMin, centroids[t]);
            cMax = Vector3.Max(cMax, centroids[t]);
        }
        nodes[node].Min = min;
        nodes[node].Max = max;

        Vector3 extent = cMax - cMin;
        if (count <= LeafSize || MathF.Max(extent.X, MathF.Max(extent.Y, extent.Z)) <= 0)
        {
            nodes[node].Start = start;
            nodes[node].Count = count;
            return;
        }

        int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
        int half = count / 2;
        // Partial sort around the median along the axis (Array.Sort on the slice is simple and fast enough here).
        Array.Sort(order, start, count, Comparer<int>.Create((p, q) => Axis(centroids[p], axis).CompareTo(Axis(centroids[q], axis))));

        int left = nodeCount;
        nodeCount += 2;
        nodes[node].Start = left;
        nodes[node].Count = 0;
        Build(left, start, half, centroids, boxMin, boxMax);
        Build(left + 1, start + half, count - half, centroids, boxMin, boxMax);
    }

    private static float Axis(Vector3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;

    /// <summary>
    /// Nearest triangle along the ray that <paramref name="accept"/> takes (distance, barycentric u and v), closer
    /// than <paramref name="maxDistance"/>.
    /// </summary>
    public bool Raycast(Vector3 origin, Vector3 direction, float maxDistance, Vector3[] positions, int[] indices,
        Func<int, float, float, float, bool> accept, out int triangle, out float distance, out float u, out float v)
    {
        triangle = -1;
        distance = maxDistance;
        u = v = 0;
        var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);
        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;
        while (top > 0)
        {
            ref var node = ref nodes[stack[--top]];
            if (!RayBox(origin, inverse, node.Min, node.Max, distance)) continue;
            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++)
                {
                    int t = order[i];
                    Vector3 a = positions[indices[t * 3]], b = positions[indices[t * 3 + 1]], c = positions[indices[t * 3 + 2]];
                    if (!Intersect(origin, direction, a, b, c, out float d, out float tu, out float tv) || d >= distance) continue;
                    if (!accept(t, d, tu, tv)) continue;
                    triangle = t;
                    distance = d;
                    u = tu;
                    v = tv;
                }
            }
            else if (top < stack.Length - 2)
            {
                stack[top++] = node.Start;
                stack[top++] = node.Start + 1;
            }
        }
        return triangle >= 0;
    }

    /// <summary>Adds to <paramref name="results"/> every triangle whose bounds come within the sphere.</summary>
    public void QuerySphere(Vector3 center, float radius, List<int> results)
    {
        Span<int> stack = stackalloc int[64];
        int top = 0;
        stack[top++] = 0;
        float r2 = radius * radius;
        while (top > 0)
        {
            ref var node = ref nodes[stack[--top]];
            Vector3 nearest = Vector3.Clamp(center, node.Min, node.Max);
            if (Vector3.DistanceSquared(nearest, center) > r2) continue;
            if (node.Count > 0)
            {
                for (int i = node.Start; i < node.Start + node.Count; i++) results.Add(order[i]);
            }
            else if (top < stack.Length - 2)
            {
                stack[top++] = node.Start;
                stack[top++] = node.Start + 1;
            }
        }
    }

    private static bool RayBox(Vector3 origin, Vector3 inverse, Vector3 min, Vector3 max, float maxDistance)
    {
        Vector3 t0 = (min - origin) * inverse, t1 = (max - origin) * inverse;
        Vector3 lo = Vector3.Min(t0, t1), hi = Vector3.Max(t0, t1);
        float enter = MathF.Max(MathF.Max(lo.X, lo.Y), lo.Z);
        float exit = MathF.Min(MathF.Min(hi.X, hi.Y), hi.Z);
        return exit >= MathF.Max(enter, 0) && enter <= maxDistance;
    }

    // Möller-Trumbore, both sides.
    private static bool Intersect(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float t, out float u, out float v)
    {
        t = u = v = 0;
        Vector3 e1 = b - a, e2 = c - a;
        Vector3 p = Vector3.Cross(dir, e2);
        float det = Vector3.Dot(e1, p);
        if (MathF.Abs(det) < 1e-12f) return false;
        float inv = 1f / det;
        Vector3 s = origin - a;
        u = Vector3.Dot(s, p) * inv;
        if (u < 0 || u > 1) return false;
        Vector3 q = Vector3.Cross(s, e1);
        v = Vector3.Dot(dir, q) * inv;
        if (v < 0 || u + v > 1) return false;
        t = Vector3.Dot(e2, q) * inv;
        return t > 0;
    }
}
