using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// Edge padding ("bleed", "dilation"): extends the colours at the border of the painted area outwards, so filtering
/// and mipmaps don't pull in the background along UV seams. It grows one ring of texels at a time; each texel of a
/// new ring takes the average of its neighbours already filled, weighted by their alpha so transparent texels don't
/// darken it, which is what Substance's and Blender's bake margins do.
/// </summary>
/// <remarks>
/// Rings are 8-connected: ring k holds the texels k steps away counting diagonal steps as one (Chebyshev distance).
/// A 4-connected ring would leave the padding only k/√2 wide along the diagonal edges UV islands mostly have, and the
/// 2x2 footprint of bilinear filtering and mipmaps reaches diagonally too.
/// </remarks>
public static class EdgePadding
{
    private const float DiagonalWeight = 0.70710678f;

    /// <summary>
    /// A padded copy of <paramref name="pixels"/> (row 0 at the top): texels in <paramref name="inside"/> keep their
    /// colour, texels outside it up to <paramref name="maxDistance"/> rings away take the bled colour, and texels
    /// farther out keep theirs. The input isn't changed.
    /// </summary>
    /// <param name="wrap">Let the padding cross the texture's edges onto the opposite side (tiling textures).
    /// Off for atlases, where the far side is unrelated.</param>
    public static Color[] Pad(Color[] pixels, int width, int height, bool[] inside, int maxDistance, bool wrap = false)
    {
        int count = width * height;
        if (width < 0 || height < 0 || pixels.Length < count || inside.Length < count)
            throw new ArgumentException("pixels and inside must hold width x height texels");
        var result = (Color[])pixels.Clone();
        if (maxDistance <= 0 || count == 0) return result;

        var job = new Job(result, width, height, wrap);
        int chunks = Math.Max(1, Environment.ProcessorCount * 2);
        var lists = new List<int>[chunks];
        for (int c = 0; c < chunks; c++) lists[c] = new List<int>();

        // filled: has a colour neighbours may read. claimed: filled or already in the next ring, a bit per texel so
        // threads building a ring can claim texels with Interlocked.Or.
        Parallel.For(0, job.Claimed.Length, word =>
        {
            int bits = 0, first = word << 5;
            int end = Math.Min(first + 32, count);
            for (int i = first; i < end; i++)
            {
                if (!inside[i]) continue;
                bits |= 1 << (i - first);
                job.Filled[i] = 1;
            }
            job.Claimed[word] = bits;
        });

        // The first ring, by bands of rows: the texels outside with a filled texel among their 8 neighbours.
        Parallel.For(0, chunks, c => job.FirstRing((int)((long)height * c / chunks), (int)((long)height * (c + 1) / chunks), lists[c]));
        int[] ring = Gather(lists, Array.Empty<int>(), out int ringCount);
        foreach (int i in ring.AsSpan(0, ringCount)) job.Claimed[i >> 5] |= 1 << (i & 31);
        int[] next = Array.Empty<int>();

        for (int distance = 1; ringCount > 0; distance++)
        {
            int n = ringCount;
            var current = ring;
            // Colours first, from the texels filled by earlier rings only; then the ring counts as filled.
            Parallel.For(0, chunks, c => job.Colour(current, (int)((long)n * c / chunks), (int)((long)n * (c + 1) / chunks)));
            for (int k = 0; k < n; k++) job.Filled[current[k]] = 1;
            if (distance >= maxDistance) break;

            Parallel.For(0, chunks, c => job.Expand(current, (int)((long)n * c / chunks), (int)((long)n * (c + 1) / chunks), lists[c]));
            next = Gather(lists, next, out ringCount);
            (ring, next) = (next, ring);
        }
        return result;
    }

    /// <summary>Concatenates the per-chunk lists into <paramref name="buffer"/> (grown when too small).</summary>
    private static int[] Gather(List<int>[] lists, int[] buffer, out int count)
    {
        count = 0;
        foreach (var list in lists) count += list.Count;
        if (buffer.Length < count) buffer = new int[Math.Max(count, buffer.Length * 2)];
        int o = 0;
        foreach (var list in lists)
        {
            list.CopyTo(buffer, o);
            o += list.Count;
        }
        return buffer;
    }

    /// <summary>One padding run's buffers and the per-texel work, kept out of the lambdas so it compiles tight.</summary>
    private sealed class Job
    {
        public readonly Color[] Result;
        public readonly byte[] Filled;
        public readonly int[] Claimed;
        private readonly int width, height;
        private readonly bool wrap;
        /// <summary>Index offsets of the 8 neighbours of a texel away from the texture's edges.</summary>
        private readonly int[] offsets;

        public Job(Color[] result, int width, int height, bool wrap)
        {
            Result = result;
            this.width = width;
            this.height = height;
            this.wrap = wrap;
            Filled = new byte[width * height];
            Claimed = new int[(width * height + 31) >> 5];
            offsets = new[] { -width - 1, -width, -width + 1, -1, 1, width - 1, width, width + 1 };
        }

        private static float Weight(int k) => k is 0 or 2 or 5 or 7 ? DiagonalWeight : 1f;

        /// <summary>
        /// Neighbour <paramref name="k"/> (in <see cref="offsets"/> order) of texel (x, y), wrapped, or -1 off the
        /// texture. The slow path, for texels on the texture's edges.
        /// </summary>
        private int NeighbourAt(int x, int y, int k)
        {
            int nx = x + (k is 0 or 3 or 5 ? -1 : k is 2 or 4 or 7 ? 1 : 0);
            int ny = y + (k < 3 ? -1 : k > 4 ? 1 : 0);
            if (wrap)
            {
                if (nx < 0) nx += width; else if (nx >= width) nx -= width;
                if (ny < 0) ny += height; else if (ny >= height) ny -= height;
            }
            else if (nx < 0 || ny < 0 || nx >= width || ny >= height) return -1;
            return ny * width + nx;
        }

        public void FirstRing(int y0, int y1, List<int> list)
        {
            list.Clear();
            ref byte filled = ref MemoryMarshal.GetArrayDataReference(Filled);
            for (int y = y0; y < y1; y++)
            {
                bool edgeRow = y == 0 || y == height - 1;
                for (int x = 0; x < width; x++)
                {
                    int i = y * width + x;
                    if (Unsafe.Add(ref filled, i) != 0) continue;
                    bool any = false;
                    if (!edgeRow && x > 0 && x < width - 1)
                    {
                        any = (Unsafe.Add(ref filled, i - width - 1) | Unsafe.Add(ref filled, i - width) | Unsafe.Add(ref filled, i - width + 1)
                               | Unsafe.Add(ref filled, i - 1) | Unsafe.Add(ref filled, i + 1)
                               | Unsafe.Add(ref filled, i + width - 1) | Unsafe.Add(ref filled, i + width) | Unsafe.Add(ref filled, i + width + 1)) != 0;
                    }
                    else
                    {
                        for (int k = 0; k < 8 && !any; k++)
                        {
                            int j = NeighbourAt(x, y, k);
                            any = j >= 0 && Filled[j] != 0;
                        }
                    }
                    if (any) list.Add(i);
                }
            }
        }

        /// <summary>
        /// Sets each ring texel to the alpha-weighted average of its filled neighbours, diagonals counting less.
        /// Where every such neighbour is fully transparent, their plain colour average, at alpha 0.
        /// </summary>
        public void Colour(int[] ring, int start, int end)
        {
            ref Color pixels = ref MemoryMarshal.GetArrayDataReference(Result);
            ref byte filled = ref MemoryMarshal.GetArrayDataReference(Filled);
            for (int r = start; r < end; r++)
            {
                int i = ring[r];
                int x = i % width, y = i / width;
                bool interior = x > 0 && y > 0 && x < width - 1 && y < height - 1;
                float sr = 0, sg = 0, sb = 0, sa = 0, weight = 0, pr = 0, pg = 0, pb = 0;
                for (int k = 0; k < 8; k++)
                {
                    int j = interior ? i + offsets[k] : NeighbourAt(x, y, k);
                    if (j < 0 || Unsafe.Add(ref filled, j) == 0) continue;
                    float w = Weight(k);
                    Color c = Unsafe.Add(ref pixels, j);
                    float wa = w * c.A;
                    sr += c.R * wa;
                    sg += c.G * wa;
                    sb += c.B * wa;
                    sa += wa;
                    pr += c.R * w;
                    pg += c.G * w;
                    pb += c.B * w;
                    weight += w;
                }
                if (weight <= 0) continue;
                Unsafe.Add(ref pixels, i) = sa > 0
                    ? new Color((byte)(sr / sa + 0.5f), (byte)(sg / sa + 0.5f), (byte)(sb / sa + 0.5f), (byte)(sa / weight + 0.5f))
                    : new Color((byte)(pr / weight + 0.5f), (byte)(pg / weight + 0.5f), (byte)(pb / weight + 0.5f), (byte)0);
            }
        }

        /// <summary>The next ring: unclaimed neighbours of this one, each claimed by whichever thread gets it first.</summary>
        public void Expand(int[] ring, int start, int end, List<int> list)
        {
            list.Clear();
            ref int claimed = ref MemoryMarshal.GetArrayDataReference(Claimed);
            for (int r = start; r < end; r++)
            {
                int i = ring[r];
                int x = i % width, y = i / width;
                bool interior = x > 0 && y > 0 && x < width - 1 && y < height - 1;
                for (int k = 0; k < 8; k++)
                {
                    int j = interior ? i + offsets[k] : NeighbourAt(x, y, k);
                    if (j < 0) continue;
                    ref int word = ref Unsafe.Add(ref claimed, j >> 5);
                    int bit = 1 << (j & 31);
                    if ((Volatile.Read(ref word) & bit) != 0) continue;
                    if ((Interlocked.Or(ref word, bit) & bit) == 0) list.Add(j);
                }
            }
        }
    }
}
