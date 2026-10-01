using System.Numerics;

namespace Sable.Model;

public struct SurfaceHit
{
    public int Part;
    public int Triangle;
    public float Distance;
    public Vector3 Point;
    /// <summary>Geometric (face) normal of the hit triangle.</summary>
    public Vector3 Normal;
    /// <summary>Barycentric weights of the triangle's three corners.</summary>
    public Vector3 Barycentric;
}

/// <summary>CPU ray casts against the model's triangles, and the UV maths painting builds on.</summary>
public static class Raycast
{
    /// <summary>
    /// Nearest front-facing, visible triangle along the ray, among the objects <paramref name="objectFilter"/>
    /// accepts. Back faces are skipped because the 3D view culls them.
    /// </summary>
    public static bool Cast(LoadedModel model, Vector3 origin, Vector3 direction, Func<int, bool> objectFilter, out SurfaceHit hit)
    {
        Diagnostics.FrameProfiler.Raycasts++;
        hit = default;
        hit.Distance = float.MaxValue;
        bool found = false;
        var inverse = new Vector3(1f / direction.X, 1f / direction.Y, 1f / direction.Z);

        for (int p = 0; p < model.Parts.Count; p++)
        {
            var part = model.Parts[p];
            if (!objectFilter(part.ObjectIndex)) continue;
            if (!RayBox(origin, inverse, part.Min, part.Max, hit.Distance)) continue;

            var positions = part.Positions;
            var indices = part.Indices;
            bool Accept(int t, float distance, float u, float v)
            {
                if (!part.TriangleVisible(t)) return false;
                Vector3 a = positions[indices[t * 3]], b = positions[indices[t * 3 + 1]], c = positions[indices[t * 3 + 2]];
                return Vector3.Dot(Vector3.Cross(b - a, c - a), direction) < 0;
            }
            if (!part.Bvh.Raycast(origin, direction, hit.Distance, positions, indices, Accept, out int tri, out float dist, out float hu, out float hv))
                continue;
            Vector3 ta = positions[indices[tri * 3]], tb = positions[indices[tri * 3 + 1]], tc = positions[indices[tri * 3 + 2]];
            hit = new SurfaceHit
            {
                Part = p,
                Triangle = tri,
                Distance = dist,
                Point = origin + direction * dist,
                Normal = Vector3.Normalize(Vector3.Cross(tb - ta, tc - ta)),
                Barycentric = new Vector3(1 - hu - hv, hu, hv),
            };
            found = true;
        }
        return found;
    }

    /// <summary>
    /// The visible surface point of object <paramref name="objectIndex"/> nearest to <paramref name="point"/>, no
    /// further than <paramref name="maxDistance"/> (mirror painting looks for the surface across the plane).
    /// </summary>
    public static bool Nearest(LoadedModel model, int objectIndex, Vector3 point, float maxDistance, out SurfaceHit hit)
    {
        hit = default;
        float best = maxDistance * maxDistance;
        bool found = false;
        var candidates = new List<int>();
        foreach (int p in model.Objects[objectIndex].Parts)
        {
            var part = model.Parts[p];
            if (Vector3.DistanceSquared(Vector3.Clamp(point, part.Min, part.Max), point) > best) continue;
            candidates.Clear();
            part.Bvh.QuerySphere(point, MathF.Sqrt(best), candidates);
            var positions = part.Positions;
            var indices = part.Indices;
            foreach (int t in candidates)
            {
                if (!part.TriangleVisible(t)) continue;
                Vector3 a = positions[indices[t * 3]], b = positions[indices[t * 3 + 1]], c = positions[indices[t * 3 + 2]];
                Vector3 q = ClosestPoint(point, a, b, c, out Vector3 barycentric);
                float d = Vector3.DistanceSquared(q, point);
                if (d >= best) continue;
                var normal = Vector3.Cross(b - a, c - a);
                if (normal.LengthSquared() < 1e-20f) continue;
                best = d;
                found = true;
                hit = new SurfaceHit
                {
                    Part = p,
                    Triangle = t,
                    Distance = MathF.Sqrt(d),
                    Point = q,
                    Normal = Vector3.Normalize(normal),
                    Barycentric = barycentric,
                };
            }
        }
        return found;
    }

    // Closest point on a triangle (Ericson, Real-Time Collision Detection 5.1.5), with its barycentric weights.
    private static Vector3 ClosestPoint(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 barycentric)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) { barycentric = new Vector3(1, 0, 0); return a; }
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) { barycentric = new Vector3(0, 1, 0); return b; }
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float v = d1 / (d1 - d3);
            barycentric = new Vector3(1 - v, v, 0);
            return a + ab * v;
        }
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) { barycentric = new Vector3(0, 0, 1); return c; }
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float w = d2 / (d2 - d6);
            barycentric = new Vector3(1 - w, 0, w);
            return a + ac * w;
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
        {
            float w = (d4 - d3) / (d4 - d3 + (d5 - d6));
            barycentric = new Vector3(0, 1 - w, w);
            return b + (c - b) * w;
        }
        float denom = 1f / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        barycentric = new Vector3(1 - vv - ww, vv, ww);
        return a + ab * vv + ac * ww;
    }

    // Möller-Trumbore, both sides.
    private static bool IntersectTriangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float t, out float u, out float v)
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

    private static bool RayBox(Vector3 origin, Vector3 inverse, Vector3 min, Vector3 max, float maxDistance)
    {
        Vector3 t0 = (min - origin) * inverse, t1 = (max - origin) * inverse;
        Vector3 lo = Vector3.Min(t0, t1), hi = Vector3.Max(t0, t1);
        float enter = MathF.Max(MathF.Max(lo.X, lo.Y), lo.Z);
        float exit = MathF.Min(MathF.Min(hi.X, hi.Y), hi.Z);
        return exit >= MathF.Max(enter, 0) && enter <= maxDistance;
    }

    /// <summary>
    /// Barycentric weights of a point in the plane of a 2D triangle. Works outside the triangle too (weights go
    /// negative), which lets a texel's corners be mapped onto the surface.
    /// </summary>
    public static Vector3 Barycentric2D(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
    {
        Vector2 v0 = b - a, v1 = c - a, v2 = p - a;
        float d = v0.X * v1.Y - v1.X * v0.Y;
        if (MathF.Abs(d) < 1e-20f) return new Vector3(-1);
        float v = (v2.X * v1.Y - v1.X * v2.Y) / d;
        float w = (v0.X * v2.Y - v2.X * v0.Y) / d;
        return new Vector3(1 - v - w, v, w);
    }

    public static Vector2 UvAt(MeshPart part, int triangle, Vector3 barycentric)
    {
        var uvs = part.Uvs!;
        var i = part.Indices;
        return uvs[i[triangle * 3]] * barycentric.X + uvs[i[triangle * 3 + 1]] * barycentric.Y + uvs[i[triangle * 3 + 2]] * barycentric.Z;
    }

    public static Vector3 PositionAt(MeshPart part, int triangle, Vector3 barycentric)
    {
        var p = part.Positions;
        var i = part.Indices;
        return p[i[triangle * 3]] * barycentric.X + p[i[triangle * 3 + 1]] * barycentric.Y + p[i[triangle * 3 + 2]] * barycentric.Z;
    }

    /// <summary>World-space size of one texel on this triangle, for a texture of the given size.</summary>
    public static float TexelWorldSize(MeshPart part, int triangle, Vector2 textureSize)
    {
        var i = part.Indices;
        Vector3 a = part.Positions[i[triangle * 3]], b = part.Positions[i[triangle * 3 + 1]], c = part.Positions[i[triangle * 3 + 2]];
        Vector2 ua = part.Uvs![i[triangle * 3]] * textureSize, ub = part.Uvs[i[triangle * 3 + 1]] * textureSize, uc = part.Uvs[i[triangle * 3 + 2]] * textureSize;
        float worldArea = Vector3.Cross(b - a, c - a).Length() * 0.5f;
        float texelArea = MathF.Abs((ub.X - ua.X) * (uc.Y - ua.Y) - (uc.X - ua.X) * (ub.Y - ua.Y)) * 0.5f;
        return texelArea < 1e-9f ? 0f : MathF.Sqrt(worldArea / texelArea);
    }
}
