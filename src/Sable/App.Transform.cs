using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using Sable.Paint;

namespace Sable;

/// <summary>
/// Free transform of a texel selection (Ctrl+T, as in Photoshop): a box with handles in the UV view. Corners scale
/// keeping the proportions (Shift frees them), sides stretch one way, inside moves, just outside a corner rotates
/// (Shift snaps to 15 degrees); the toolbar turns by 90 degrees and flips. Enter or a click away applies, Esc cancels.
/// </summary>
internal sealed partial class App
{
    private enum Handle { None, Move, Rotate, N, S, E, W, NE, NW, SE, SW }

    private SelectionTransform? transform;
    private Handle transformDrag, transformHover;
    private Vector2 transformStartMouse;
    private TransformState transformStartState;
    private bool transformSmooth;

    private void BeginTransform()
    {
        if (transform != null) return;
        if (state.ActiveSelection is not { } selection || ActiveTextureObject is not { } texture)
        {
            SetStatus("Ctrl+T transforms a texel selection: make one first with the lasso (X) or box select (M) in the UV view.", error: true);
            return;
        }
        EndStroke();
        transform = new SelectionTransform(texture, selection);
        state.TransformCorners = transform.Current.Corners();
        SetStatus("Transform: corners scale (Shift: free ratio), sides stretch, inside moves, outside a corner rotates (Shift: 15°). Enter applies, Esc cancels.", error: false);
    }

    private void PlaceTransform(TransformState next)
    {
        if (transform == null || state.Selection == null || next == transform.Current) return;
        transform.Place(next, transformSmooth, state.Selection);
        state.TransformCorners = transform.Current.Corners();
    }

    private void CommitTransform()
    {
        if (transform == null) return;
        if (transform.Finish() is { } step) undo.Push(step);
        var (start, now) = (transform.Start, transform.Current);
        EndTransform();
        SetStatus(now == start ? "Transform: nothing changed."
            : $"Transformed {start.Width}x{start.Height} to {now.Width}x{now.Height}{(now.Angle != 0 ? $", turned {Degrees(now.Angle):0.#}°" : "")}"
              + $"{(now.FlipX ? ", flipped horizontally" : "")}{(now.FlipY ? ", flipped vertically" : "")}.", error: false);
    }

    private void CancelTransform()
    {
        if (transform == null) return;
        if (state.Selection != null) transform.Cancel(state.Selection);
        EndTransform();
        SetStatus("Transform cancelled.", error: false);
    }

    private void EndTransform()
    {
        transform = null;
        state.TransformCorners = null;
        transformDrag = transformHover = Handle.None;
    }

    private static float Degrees(float radians) => (radians * 180f / MathF.PI % 360f + 360f) % 360f;

    /// <summary>
    /// Per frame while transforming: the handles take the pointer in the UV view and every tool waits. Returns
    /// whether a transform is running.
    /// </summary>
    private bool UpdateTransform(bool free)
    {
        if (transform == null) return false;
        if (state.Selection == null || ActiveTextureObject == null)
        {
            CommitTransform();
            return false;
        }

        Vector2 mouse = uvView.MouseTexel;
        float texelsPerPixel = MathF.Abs(uvView.ScreenToTexel(pointer + Vector2.UnitX).X - uvView.ScreenToTexel(pointer).X);
        transformHover = uvView.Hovered && free ? HitHandle(mouse, texelsPerPixel) : Handle.None;

        if (pointerPressed && uvView.Hovered && free)
        {
            if (transformHover == Handle.None)
            {
                CommitTransform();
                return true;
            }
            transformDrag = transformHover;
            transformStartMouse = mouse;
            transformStartState = transform.Current;
        }
        if (transformDrag != Handle.None)
        {
            if (!pointerDown) transformDrag = Handle.None;
            else PlaceTransform(Drag(mouse, ShiftDown));
        }
        cursorIcon = transformHover == Handle.Rotate || transformDrag == Handle.Rotate ? CursorIcon.Rotate : CursorIcon.System;
        cursorTip = pointer;
        return true;
    }

    /// <summary>What's under the pointer: a handle (7 px), the inside, or the ring just outside a corner (rotate).</summary>
    private Handle HitHandle(Vector2 mouse, float texelsPerPixel)
    {
        var s = transform!.Current;
        var p = s.ToLocal(mouse);
        float hw = s.Width * 0.5f, hh = s.Height * 0.5f, reach = 7f * texelsPerPixel;
        var handles = new (Handle, Vector2)[]
        {
            (Handle.NW, new(-hw, -hh)), (Handle.NE, new(hw, -hh)), (Handle.SW, new(-hw, hh)), (Handle.SE, new(hw, hh)),
            (Handle.N, new(0, -hh)), (Handle.S, new(0, hh)), (Handle.W, new(-hw, 0)), (Handle.E, new(hw, 0)),
        };
        foreach (var (handle, at) in handles)
            if (MathF.Abs(p.X - at.X) <= reach && MathF.Abs(p.Y - at.Y) <= reach) return handle;
        if (MathF.Abs(p.X) <= hw && MathF.Abs(p.Y) <= hh) return Handle.Move;
        // Rotating: within about 30 px outside the box, near a corner.
        float rotateReach = 30f * texelsPerPixel;
        foreach (var (_, at) in handles.Take(4))
            if (Vector2.Distance(p, at) <= rotateReach) return Handle.Rotate;
        return Handle.None;
    }

    /// <summary>The transform after dragging the grabbed handle to <paramref name="mouse"/> (texels).</summary>
    private TransformState Drag(Vector2 mouse, bool shift)
    {
        var start = transformStartState;
        var delta = mouse - transformStartMouse;
        switch (transformDrag)
        {
            case Handle.Move:
            {
                var center = start.Center + delta;
                // Unturned (or quarter-turned) boxes stay on whole texels.
                if (start.QuarterTurn) center = start.Center + new Vector2(MathF.Round(delta.X), MathF.Round(delta.Y));
                return start with { Center = center };
            }
            case Handle.Rotate:
            {
                float from = MathF.Atan2(transformStartMouse.Y - start.Center.Y, transformStartMouse.X - start.Center.X);
                float to = MathF.Atan2(mouse.Y - start.Center.Y, mouse.X - start.Center.X);
                float angle = start.Angle + to - from;
                if (shift) angle = MathF.Round(angle / (MathF.PI / 12)) * (MathF.PI / 12);
                return (start with { Angle = angle }).Aligned();
            }
        }

        // Scaling happens in the box's own frame, so it works the same when the box is turned.
        var (c, s) = start.Rotation;
        var d = new Vector2(delta.X * c + delta.Y * s, -delta.X * s + delta.Y * c);
        float l = -start.Width * 0.5f, r = start.Width * 0.5f, t = -start.Height * 0.5f, b = start.Height * 0.5f;
        bool west = transformDrag is Handle.W or Handle.NW or Handle.SW, east = transformDrag is Handle.E or Handle.NE or Handle.SE;
        bool north = transformDrag is Handle.N or Handle.NW or Handle.NE, south = transformDrag is Handle.S or Handle.SW or Handle.SE;
        if (west) l += d.X;
        if (east) r += d.X;
        if (north) t += d.Y;
        if (south) b += d.Y;
        bool corner = (west || east) && (north || south);
        if (corner && !shift)
        {
            // Keep the proportions: scale by whichever side changed more, about the opposite corner.
            float sx = (r - l) / start.Width, sy = (b - t) / start.Height;
            float k = MathF.Max(MathF.Abs(sx - 1f) >= MathF.Abs(sy - 1f) ? sx : sy, 1f / MathF.Max(start.Width, start.Height));
            if (west) l = r - start.Width * k; else r = l + start.Width * k;
            if (north) t = b - start.Height * k; else b = t + start.Height * k;
        }
        // Whole texels, at least one, and the dragged side moves (the opposite one stays put).
        int width = Math.Max(1, (int)MathF.Round(r - l)), height = Math.Max(1, (int)MathF.Round(b - t));
        if (west) l = r - width; else r = l + width;
        if (north) t = b - height; else b = t + height;
        var mid = new Vector2((l + r) * 0.5f, (t + b) * 0.5f);
        return (start with { Width = width, Height = height, Center = start.ToWorld(mid) }).Aligned();
    }

    /// <summary>The system cursor for the handle under the pointer (rotation draws its own).</summary>
    private MouseCursor TransformCursor
    {
        get
        {
            var handle = transformDrag != Handle.None ? transformDrag : transformHover;
            if (handle is Handle.Move) return MouseCursor.ResizeAll;
            if (handle is Handle.None or Handle.Rotate) return MouseCursor.Default;
            // Pick the resize arrow nearest the handle's direction on screen, turned with the box.
            var s = transform!.Current;
            var dir = handle switch
            {
                Handle.N or Handle.S => new Vector2(0, 1), Handle.E or Handle.W => new Vector2(1, 0),
                Handle.NW or Handle.SE => new Vector2(1, 1), _ => new Vector2(1, -1),
            };
            var (c, sn) = s.Rotation;
            float angle = MathF.Atan2(dir.X * sn + dir.Y * c, dir.X * c - dir.Y * sn) * 180f / MathF.PI;
            angle = (angle % 180f + 180f) % 180f;
            return angle < 22.5f || angle >= 157.5f ? MouseCursor.ResizeEw
                : angle < 67.5f ? MouseCursor.ResizeNwse
                : angle < 112.5f ? MouseCursor.ResizeNs : MouseCursor.ResizeNesw;
        }
    }

    /// <summary>The rotate cursor: a curved arrow.</summary>
    private static void DrawRotateIcon(ImDrawListPtr draw, Vector2 center)
    {
        const int steps = 16;
        var points = new Vector2[steps + 1];
        for (int i = 0; i <= steps; i++)
        {
            float a = MathF.PI * (0.15f + 1.2f * i / steps);
            points[i] = center + new Vector2(MathF.Cos(a), -MathF.Sin(a)) * 9f;
        }
        draw.AddPolyline(ref points[0], points.Length, Black, ImDrawFlags.None, 4f);
        draw.AddPolyline(ref points[0], points.Length, White, ImDrawFlags.None, 2f);
        var tip = points[0];
        var arrow = new[] { tip + new Vector2(-1, -6), tip + new Vector2(5, 2), tip + new Vector2(-5, 3) };
        draw.AddConvexPolyFilled(ref arrow[0], 3, White);
        draw.AddPolyline(ref arrow[0], 3, Black, ImDrawFlags.Closed, 1f);
    }

    /// <summary>The UV toolbar's line while transforming.</summary>
    private void DrawTransformToolbar()
    {
        if (transform == null) return;
        var s = transform.Current;
        ImGui.TextColored(new Vector4(0.55f, 0.8f, 1f, 1f), $"Transform {s.Width}x{s.Height}  {Degrees(s.Angle):0.#}°");
        ImGui.SameLine();
        if (ImGui.Button("-90")) PlaceTransform((s with { Angle = s.Angle - MathF.PI / 2 }).Aligned());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turn a quarter anticlockwise (exact for pixel art)");
        ImGui.SameLine();
        if (ImGui.Button("+90")) PlaceTransform((s with { Angle = s.Angle + MathF.PI / 2 }).Aligned());
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Turn a quarter clockwise (exact for pixel art)");
        ImGui.SameLine();
        if (ImGui.Button("Flip H")) PlaceTransform(s with { FlipX = !s.FlipX });
        ImGui.SameLine();
        if (ImGui.Button("Flip V")) PlaceTransform(s with { FlipY = !s.FlipY });
        ImGui.SameLine();
        if (ImGui.Checkbox("Smooth", ref transformSmooth) && state.Selection != null)
        {
            transform.Place(transform.Current, transformSmooth, state.Selection);
            state.TransformCorners = transform.Current.Corners();
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Off: nearest texels, hard edges (pixel art). On: bilinear, for painted textures and free angles.");
        ImGui.SameLine();
        if (ImGui.Button("Apply (Enter)")) CommitTransform();
        ImGui.SameLine();
        if (ImGui.Button("Cancel (Esc)")) CancelTransform();
    }

    /// <summary>
    /// Ctrl+T: a 4x4 block scaled 2x (nearest) lands as exact 2x2 copies with the selection following; a quarter
    /// turn moves each texel where a clockwise turn should; undo and cancel restore.
    /// </summary>
    private void SelfTestTransform(PaintTexture tex)
    {
        int index = Model!.Textures.IndexOf(tex);
        state.ActiveTexture = index;
        var selection = new TexelSelection(index, tex.Width, tex.Height);
        var mask = new bool[tex.Width * tex.Height];
        for (int y = 4; y < 8; y++) for (int x = 4; x < 8; x++) mask[y * tex.Width + x] = true;
        selection.SetMask(mask);
        state.Selection = selection;
        var before = (Color[])tex.Pixels.Clone();
        for (int y = 4; y < 8; y++) for (int x = 4; x < 8; x++) tex.Pixels[y * tex.Width + x] = new Color(x * 20, y * 20, 77, 255);
        var source = (Color[])tex.Pixels.Clone();
        Color Source(int x, int y) => source[(4 + y) * tex.Width + 4 + x];
        Color At(int x, int y) => tex.Pixels[y * tex.Width + x];

        BeginTransform();
        var start = transform!.Current;
        PlaceTransform(start with { Width = 8, Height = 8, Center = new Vector2(8, 8) });
        bool scaled = true;
        for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) scaled &= At(4 + x, 4 + y).Equals(Source(x / 2, y / 2));
        int selected = selection.Mask.Count(m => m);
        CommitTransform();
        undo.Undo();
        bool undone = tex.Pixels.SequenceEqual(source);

        // A quarter turn clockwise in place: the texel at (x, y) of the block goes to (3 - y, x).
        selection.SetMask(mask);
        BeginTransform();
        PlaceTransform((transform!.Current with { Angle = MathF.PI / 2 }).Aligned());
        bool turned = true;
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) turned &= At(4 + 3 - y, 4 + x).Equals(Source(x, y));
        PlaceTransform(transform.Current with { Angle = 0, FlipX = true });
        bool flipped = true;
        for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) flipped &= At(4 + 3 - x, 4 + y).Equals(Source(x, y));
        CancelTransform();
        bool cancelled = tex.Pixels.SequenceEqual(source) && selection.Mask.Count(m => m) == 16;

        Array.Copy(before, tex.Pixels, before.Length);
        tex.Touch();
        state.Selection = null;
        Console.WriteLine($"[selftest] transform: 4x4 -> 8x8 nearest exact {scaled}, selection {selected} texels (expect 64); undo restores {undone}; "
                          + $"+90 exact {turned}; flip H exact {flipped}; cancel restores {cancelled}");
    }
}
