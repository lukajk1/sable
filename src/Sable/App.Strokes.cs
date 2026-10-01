using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using Sable.Model;
using Sable.Paint;

namespace Sable;

/// <summary>Stroke helpers: mirror painting, Shift+click straight lines, and the stroke stabilizer.</summary>
internal sealed partial class App
{
    // ---------- mirror ----------

    /// <summary>Paint mirrored across the active object's centre on X (left/right), in both views.</summary>
    private bool mirrorX;

    /// <summary>The X of the plane strokes mirror across: the middle of the active object's bounds.</summary>
    private float MirrorPlane => state.ActiveObject >= 0
        ? (Model!.Source.Objects[state.ActiveObject].Min.X + Model.Source.Objects[state.ActiveObject].Max.X) * 0.5f : 0f;

    /// <summary>How far from the mirrored point the surface may be and still count (asymmetric details get none).</summary>
    private float MirrorReach
    {
        get
        {
            var obj = Model!.Source.Objects[state.ActiveObject];
            return MathF.Max(Vector3.Distance(obj.Min, obj.Max) * 0.03f, 1e-4f);
        }
    }

    /// <summary>
    /// The surface point mirrored across the plane from <paramref name="hit"/>, on the active object. False on the
    /// plane itself (the dab already covers it), or where the mirror image has no surface facing the same way.
    /// </summary>
    private bool MirrorHit(SurfaceHit hit, out SurfaceHit mirrored)
    {
        mirrored = default;
        if (!mirrorX || state.ActiveObject < 0) return false;
        float plane = MirrorPlane;
        if (MathF.Abs(hit.Point.X - plane) < MirrorReach * 0.05f) return false;
        var target = hit.Point with { X = 2f * plane - hit.Point.X };
        if (!Raycast.Nearest(Model!.Source, state.ActiveObject, target, MirrorReach, out mirrored)) return false;
        var expected = hit.Normal with { X = -hit.Normal.X };
        return Vector3.Dot(mirrored.Normal, expected) > 0.2f;
    }

    /// <summary>
    /// A UV-view point (in texels of the active texture) mirrored through the model: onto the surface, across the
    /// plane, and back into the texture. Null where the point isn't on the active object's UVs or has no mirror.
    /// </summary>
    private Vector2? MirrorTexel(Vector2 texelPoint)
    {
        if (!mirrorX || state.ActiveObject < 0 || state.ActiveTexture < 0) return null;
        if (!HitAtTexel(texelPoint, out var hit)) return null;
        if (!MirrorHit(hit, out var mirrored) || Model!.TextureOf(mirrored.Part) != state.ActiveTexture) return null;
        var tex = Model.Textures[state.ActiveTexture];
        return Raycast.UvAt(Model.Source.Parts[mirrored.Part], mirrored.Triangle, mirrored.Barycentric) * new Vector2(tex.Width, tex.Height);
    }

    // ---------- Shift+click straight lines ----------

    // Where the last stroke ended, per view, so Shift+click can join a straight line to it.
    private Vector3? lastStrokeEnd3D;
    private int lastStrokeEndTexture3D = -1;
    private Vector2? lastStrokeEndUv;
    private int lastStrokeEndTextureUv = -1;
    private Vector3 lastDabPoint;

    private static bool ShiftDown => Raylib.IsKeyDown(KeyboardKey.LeftShift) || Raylib.IsKeyDown(KeyboardKey.RightShift);

    /// <summary>Remembers where the stroke that just finished ended.</summary>
    private void RememberStrokeEnd()
    {
        if (strokeIn3D)
        {
            lastStrokeEnd3D = lastDabPoint;
            lastStrokeEndTexture3D = strokeTexture;
        }
        else
        {
            lastStrokeEndUv = tool == Tool.Pencil ? new Vector2(lastTexel.X + 0.5f, lastTexel.Y + 0.5f) : lastDab;
            lastStrokeEndTextureUv = strokeTexture;
        }
    }

    // ---------- stabilizer ----------

    /// <summary>0..1: how long the stabilizer's string is (up to <see cref="MaxSmoothing"/> screen pixels).</summary>
    private float smoothing;
    private const float MaxSmoothing = 60f;
    private Vector2 smoothPoint, smoothRaw;
    private float SmoothingRadius => smoothing * MaxSmoothing;

    /// <summary>Starts the stabilizer at the pointer where a stroke begins.</summary>
    private void StartSmoothing() => smoothPoint = smoothRaw = pointer;

    /// <summary>
    /// The "pulled string" stabilizer (Krita's, Lazy Nezumi's): the painted point only moves once the pen is more
    /// than the string's length away, and then just far enough to keep it taut, so wobble shorter than the string
    /// never reaches the canvas. False while the string is slack (nothing to paint).
    /// </summary>
    private bool Stabilize(Vector2 raw, out Vector2 point)
    {
        smoothRaw = raw;
        float radius = SmoothingRadius;
        if (radius < 0.5f)
        {
            point = smoothPoint = raw;
            return true;
        }
        Vector2 pull = raw - smoothPoint;
        float length = pull.Length();
        if (length <= radius)
        {
            point = smoothPoint;
            return false;
        }
        smoothPoint += pull * ((length - radius) / length);
        point = smoothPoint;
        return true;
    }

    /// <summary>While stabilizing, the string from the painted point to the pen.</summary>
    private void DrawSmoothingString()
    {
        if (stroke == null || SmoothingRadius < 0.5f) return;
        var draw = ImGui.GetForegroundDrawList();
        draw.AddLine(smoothPoint, smoothRaw, U32(new Vector4(0, 0, 0, 0.6f)), 3f);
        draw.AddLine(smoothPoint, smoothRaw, U32(new Vector4(1, 1, 1, 0.9f)), 1f);
        draw.AddCircleFilled(smoothPoint, 3f, U32(new Vector4(1, 1, 1, 0.9f)));
    }

    // ---------- self-test ----------

    /// <summary>Mirror: a hit's mirror lands across the plane, UV mirroring round-trips; the stabilizer's string.</summary>
    private void SelfTestStrokes()
    {
        var obj = Model!.Source.Objects[state.ActiveObject];
        mirrorX = true;
        var camera = view3d.Camera.ToRaylib();
        string mirror = "no hit";
        foreach (float dx in new[] { -0.15f, -0.08f })
        {
            var p = new Vector2(view3d.Width * (0.5f + dx), view3d.Height * 0.5f);
            var ray = Raylib.GetScreenToWorldRayEx(p, camera, view3d.Width, view3d.Height);
            if (!Raycast.Cast(Model.Source, ray.Position, ray.Direction, i => i == state.ActiveObject, out var hit)) continue;
            float plane = MirrorPlane;
            if (!MirrorHit(hit, out var m)) { mirror = $"hit at x={hit.Point.X:0.000} has no mirror"; break; }
            float error = Vector3.Distance(m.Point, hit.Point with { X = 2 * plane - hit.Point.X });
            var tex = Model.Textures[Model.TextureOf(hit.Part)];
            var size = new Vector2(tex.Width, tex.Height);
            Vector2 uv = Raycast.UvAt(Model.Source.Parts[hit.Part], hit.Triangle, hit.Barycentric) * size;
            state.ActiveTexture = Model.TextureOf(hit.Part);
            var back = MirrorTexel(uv) is { } mt ? MirrorTexel(mt) : null;
            mirror = $"plane x={plane:0.000}, hit x={hit.Point.X:0.000} -> mirror x={m.Point.X:0.000} (error {error:0.0000}); "
                     + $"UV {uv:0.0} -> mirrored and back {(back is { } b ? $"{b:0.0} (off by {Vector2.Distance(b, uv):0.00} texels)" : "none")}";
            break;
        }
        mirrorX = false;

        // Island and face fills from the middle of the view.
        string fills = "no hit";
        {
            var ray = Raylib.GetScreenToWorldRayEx(new Vector2(view3d.Width, view3d.Height) * 0.5f, camera, view3d.Width, view3d.Height);
            if (Raycast.Cast(Model.Source, ray.Position, ray.Direction, i => i == state.ActiveObject, out var hit) && Model.TextureOf(hit.Part) >= 0)
            {
                var tex = Model.Textures[Model.TextureOf(hit.Part)];
                int Changed(Color[] before) => before.Where((c, i) => !c.Equals(tex.Pixels[i])).Count();
                var saveHsv = hsv;
                var saveMode = fillMode;
                hsv = new Vector3(0.8f, 1f, 1f);
                var before = (Color[])tex.Pixels.Clone();
                fillMode = FillMode.Face;
                FillShape(hit);
                int face = Changed(before);
                fillMode = FillMode.Island;
                FillShape(hit);
                int island = Changed(before);
                undo.Undo();
                undo.Undo();
                fills = $"face filled {face} texels, island {island} (island >= face), undone {Changed(before) == 0}";
                hsv = saveHsv;
                fillMode = saveMode;
            }
        }

        float saved = smoothing;
        smoothing = 0.5f;
        pointer = Vector2.Zero;
        StartSmoothing();
        bool slack = !Stabilize(new Vector2(10, 0), out _);
        Stabilize(new Vector2(50, 0), out var pulled);
        smoothing = saved;
        Console.WriteLine($"[selftest] fills on {obj.Name}: {fills}");
        Console.WriteLine($"[selftest] mirror on {obj.Name}: {mirror}; stabilizer (30 px string): 10 px slack {slack}, pulled to {pulled.X:0} (expect 20)");
    }
}
