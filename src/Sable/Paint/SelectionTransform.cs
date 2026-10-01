using System.Numerics;
using Raylib_cs;

namespace Sable.Paint;

/// <summary>
/// Where a transformed selection sits: a box of <see cref="Width"/> x <see cref="Height"/> texels around
/// <see cref="Center"/>, turned <see cref="Angle"/> radians (clockwise on screen, as the UV view's y points down)
/// and optionally mirrored. Without rotation its edges sit on texel edges.
/// </summary>
public readonly record struct TransformState(Vector2 Center, int Width, int Height, float Angle, bool FlipX, bool FlipY)
{
    /// <summary>A quarter turn (0, 90, 180, 270 degrees), which maps texels onto texels exactly.</summary>
    public bool QuarterTurn => MathF.Abs(MathF.IEEERemainder(Angle, MathF.PI / 2)) < 1e-4f;

    /// <summary>Cosine and sine of the angle, exact for quarter turns so texels don't drift by rounding.</summary>
    public (float Cos, float Sin) Rotation
    {
        get
        {
            if (!QuarterTurn) return (MathF.Cos(Angle), MathF.Sin(Angle));
            int quarter = ((int)MathF.Round(Angle / (MathF.PI / 2)) % 4 + 4) % 4;
            return quarter switch { 0 => (1, 0), 1 => (0, 1), 2 => (-1, 0), _ => (0, -1) };
        }
    }

    /// <summary>A point in the box's own frame (texels from its centre, before turning) to texture texels.</summary>
    public Vector2 ToWorld(Vector2 local)
    {
        var (c, s) = Rotation;
        return Center + new Vector2(local.X * c - local.Y * s, local.X * s + local.Y * c);
    }

    /// <summary>Texture texels to the box's own frame.</summary>
    public Vector2 ToLocal(Vector2 world)
    {
        var (c, s) = Rotation;
        var d = world - Center;
        return new Vector2(d.X * c + d.Y * s, -d.X * s + d.Y * c);
    }

    /// <summary>Top-left, top-right, bottom-right, bottom-left, in texture texels.</summary>
    public Vector2[] Corners()
    {
        float hw = Width * 0.5f, hh = Height * 0.5f;
        return new[] { ToWorld(new(-hw, -hh)), ToWorld(new(hw, -hh)), ToWorld(new(hw, hh)), ToWorld(new(-hw, hh)) };
    }

    /// <summary>
    /// Moves the centre the least amount (half a texel at most) so that, after a quarter turn, the box's edges
    /// land on texel edges again (a 4x3 box turned 90 degrees is 3 wide).
    /// </summary>
    public TransformState Aligned()
    {
        if (!QuarterTurn) return this;
        bool sideways = MathF.Abs(Rotation.Sin) > 0.5f;
        float w = sideways ? Height : Width, h = sideways ? Width : Height;
        return this with
        {
            Center = new Vector2(MathF.Round(Center.X - w * 0.5f) + w * 0.5f, MathF.Round(Center.Y - h * 0.5f) + h * 0.5f),
        };
    }
}

/// <summary>
/// Free transform of the selected texels (Ctrl+T): they're lifted off the active layer, then scaled, turned,
/// mirrored and moved, previewed in place; the old spot is left transparent as with a move. Nearest-neighbour keeps
/// texels hard (pixel art; quarter turns and flips are exact); smooth samples bilinearly. One undo step, or nothing
/// on cancel.
/// </summary>
public sealed class SelectionTransform
{
    private readonly PaintTexture texture;
    private readonly Layer layer;
    private readonly TexelSelection startSelection;
    private readonly Color[] before, underneath, lifted;
    private readonly bool[] liftedMask;
    private readonly int liftedWidth, liftedHeight;

    public TransformState Start { get; }
    public TransformState Current { get; private set; }

    public SelectionTransform(PaintTexture texture, TexelSelection selection)
    {
        this.texture = texture;
        layer = texture.ActiveLayer;
        startSelection = selection.Clone();
        int w = texture.Width;
        int x0 = int.MaxValue, y0 = int.MaxValue, x1 = -1, y1 = -1;
        for (int i = 0; i < selection.Mask.Length; i++)
        {
            if (!selection.Mask[i]) continue;
            int x = i % w, y = i / w;
            x0 = Math.Min(x0, x); y0 = Math.Min(y0, y);
            x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
        }
        liftedWidth = x1 - x0 + 1;
        liftedHeight = y1 - y0 + 1;
        Start = Current = new TransformState(new Vector2(x0 + liftedWidth * 0.5f, y0 + liftedHeight * 0.5f), liftedWidth, liftedHeight, 0f, false, false);

        before = (Color[])layer.Pixels.Clone();
        underneath = (Color[])layer.Pixels.Clone();
        lifted = new Color[liftedWidth * liftedHeight];
        liftedMask = new bool[lifted.Length];
        for (int y = 0; y < liftedHeight; y++)
        for (int x = 0; x < liftedWidth; x++)
        {
            int i = (y0 + y) * w + x0 + x;
            if (!selection.Mask[i]) continue;
            lifted[y * liftedWidth + x] = before[i];
            liftedMask[y * liftedWidth + x] = true;
            underneath[i] = new Color(0, 0, 0, 0);
        }
    }

    /// <summary>Places the lifted texels as <paramref name="state"/> says and moves the selection with them.</summary>
    public void Place(TransformState state, bool smooth, TexelSelection selection)
    {
        state = state with { Width = Math.Max(1, state.Width), Height = Math.Max(1, state.Height) };
        Current = state;
        int w = texture.Width, h = texture.Height;
        var pixels = layer.Pixels;
        Array.Copy(underneath, pixels, pixels.Length);
        var mask = new bool[w * h];

        var corners = state.Corners();
        int bx0 = Math.Max(0, (int)MathF.Floor(corners.Min(c => c.X))), bx1 = Math.Min(w - 1, (int)MathF.Ceiling(corners.Max(c => c.X)));
        int by0 = Math.Max(0, (int)MathF.Floor(corners.Min(c => c.Y))), by1 = Math.Min(h - 1, (int)MathF.Ceiling(corners.Max(c => c.Y)));
        float scaleX = liftedWidth / (float)state.Width, scaleY = liftedHeight / (float)state.Height;
        float halfW = state.Width * 0.5f, halfH = state.Height * 0.5f;

        Parallel.For(by0, by1 + 1, ty =>
        {
            for (int tx = bx0; tx <= bx1; tx++)
            {
                // Each target texel's centre, back into the lifted texels.
                var local = state.ToLocal(new Vector2(tx + 0.5f, ty + 0.5f));
                float lx = local.X + halfW, ly = local.Y + halfH;
                if (lx < 0 || ly < 0 || lx >= state.Width || ly >= state.Height) continue;
                if (state.FlipX) lx = state.Width - lx;
                if (state.FlipY) ly = state.Height - ly;
                float sx = lx * scaleX, sy = ly * scaleY;
                int nx = Math.Clamp((int)sx, 0, liftedWidth - 1), ny = Math.Clamp((int)sy, 0, liftedHeight - 1);
                int i = ty * w + tx;
                bool selected = liftedMask[ny * liftedWidth + nx];
                if (selected) mask[i] = true;
                if (!smooth)
                {
                    if (selected) pixels[i] = lifted[ny * liftedWidth + nx];
                    continue;
                }
                var c = Bilinear(sx - 0.5f, sy - 0.5f);
                if (c.A == 0) continue;
                var under = pixels[i];
                float r = under.R / 255f, g = under.G / 255f, b = under.B / 255f, a = under.A / 255f;
                Compositor.Over(c, 1f, BlendMode.Normal, ref r, ref g, ref b, ref a);
                pixels[i] = Compositor.ToColor(r, g, b, a);
            }
        });
        texture.Touch();
        selection.SetMask(mask);
    }

    // Bilinear over the lifted texels, alpha-weighted (texels outside the selection are transparent).
    private Color Bilinear(float u, float v)
    {
        int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
        float fx = u - x0, fy = v - y0;
        float r = 0, g = 0, b = 0, a = 0;
        for (int k = 0; k < 4; k++)
        {
            int dx = k & 1, dy = k >> 1;
            float weight = (dx == 1 ? fx : 1f - fx) * (dy == 1 ? fy : 1f - fy);
            int x = Math.Clamp(x0 + dx, 0, liftedWidth - 1), y = Math.Clamp(y0 + dy, 0, liftedHeight - 1);
            var c = lifted[y * liftedWidth + x];
            float wa = weight * c.A;
            r += c.R * wa; g += c.G * wa; b += c.B * wa; a += wa;
        }
        return a <= 0 ? new Color(0, 0, 0, 0)
            : new Color((byte)Math.Clamp(r / a, 0, 255), (byte)Math.Clamp(g / a, 0, 255), (byte)Math.Clamp(b / a, 0, 255), (byte)Math.Clamp(a, 0, 255));
    }

    /// <summary>Puts everything back as it was before the transform.</summary>
    public void Cancel(TexelSelection selection)
    {
        Array.Copy(before, layer.Pixels, before.Length);
        texture.Touch();
        selection.SetMask(startSelection.Mask);
    }

    /// <summary>The undo step, or null if nothing changed.</summary>
    public UndoStep? Finish() =>
        Current == Start ? null : UndoStep.Capture(texture, layer, before, 0, 0, texture.Width, texture.Height);
}
