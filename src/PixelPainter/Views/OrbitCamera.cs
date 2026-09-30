using System.Numerics;
using Raylib_cs;

namespace PixelPainter.Views;

/// <summary>
/// Blender-style turntable camera: orbits a pivot with yaw (around world up) and pitch. Y is up, and "front"
/// (numpad 1) looks down -Z, matching how Blender's glTF/FBX export maps Blender's -Y (front) to +Z.
/// </summary>
public sealed class OrbitCamera
{
    public Vector3 Pivot;
    public float Yaw;
    public float Pitch = 0.35f;
    public float Distance = 10f;
    public bool Ortho;
    public float FovDegrees = 50f;

    /// <summary>From the pivot towards the camera.</summary>
    public Vector3 Back => new(MathF.Cos(Pitch) * MathF.Sin(Yaw), MathF.Sin(Pitch), MathF.Cos(Pitch) * MathF.Cos(Yaw));
    public Vector3 Forward => -Back;
    public Vector3 Up => new(-MathF.Sin(Pitch) * MathF.Sin(Yaw), MathF.Cos(Pitch), -MathF.Sin(Pitch) * MathF.Cos(Yaw));
    public Vector3 Right => Vector3.Normalize(Vector3.Cross(Forward, Up));
    public Vector3 Position => Pivot + Back * Distance;

    /// <summary>World-space height of the view at the pivot's distance (also the ortho view height).</summary>
    public float ViewHeight => 2f * Distance * MathF.Tan(FovDegrees * MathF.PI / 360f);

    public Camera3D ToRaylib() => new()
    {
        Position = Position,
        Target = Pivot,
        Up = Up,
        FovY = Ortho ? ViewHeight : FovDegrees,
        Projection = Ortho ? CameraProjection.Orthographic : CameraProjection.Perspective,
    };

    /// <summary>Projection with clip planes scaled to the scene (raylib's own are fixed at 0.01 to 1000).</summary>
    public Matrix4x4 Projection(float aspect, float sceneRadius)
    {
        float far = Distance * 4f + sceneRadius * 8f + 10f;
        if (Ortho)
        {
            float h = ViewHeight * 0.5f, w = h * aspect;
            return Raymath.MatrixOrtho(-w, w, -h, h, -far, far);
        }
        float near = MathF.Max(Distance * 0.005f, 0.0005f);
        return Raymath.MatrixPerspective(FovDegrees * MathF.PI / 180f, aspect, near, far);
    }

    /// <summary>
    /// Turntable orbit. With <paramref name="around"/>, the whole camera swings rigidly around that point instead of
    /// the pivot (Blender's "Orbit Around Selection"): the pivot keeps its place relative to the camera, so the view
    /// doesn't jump to look at the point.
    /// </summary>
    public void Orbit(Vector2 pixels, Vector3? around = null)
    {
        Vector3 right = Right, up = Up, back = Back;
        Yaw -= pixels.X * 0.008f;
        Pitch = Math.Clamp(Pitch + pixels.Y * 0.008f, -MathF.PI / 2f, MathF.PI / 2f);
        if (around is not { } center) return;

        Vector3 offset = Pivot - center;
        var local = new Vector3(Vector3.Dot(offset, right), Vector3.Dot(offset, up), Vector3.Dot(offset, back));
        Pivot = center + Right * local.X + Up * local.Y + Back * local.Z;
    }

    public void Pan(Vector2 pixels, float viewportHeight)
    {
        float worldPerPixel = ViewHeight / MathF.Max(viewportHeight, 1f);
        Pivot += (-Right * pixels.X + Up * pixels.Y) * worldPerPixel;
    }

    public void Zoom(float factor) => Distance = Math.Clamp(Distance * factor, 0.0005f, 1e6f);

    public void Frame(Vector3 min, Vector3 max)
    {
        Pivot = (min + max) * 0.5f;
        float radius = MathF.Max((max - min).Length() * 0.5f, 0.01f);
        Distance = radius / MathF.Sin(FovDegrees * MathF.PI / 360f) * 1.05f;
    }

    public (Vector3 Pivot, float Yaw, float Pitch, float Distance, bool Ortho) Save() => (Pivot, Yaw, Pitch, Distance, Ortho);

    public void Restore((Vector3 Pivot, float Yaw, float Pitch, float Distance, bool Ortho) saved) =>
        (Pivot, Yaw, Pitch, Distance, Ortho) = saved;

    public void SetView(float yawDegrees, float pitchDegrees)
    {
        Yaw = yawDegrees * MathF.PI / 180f;
        Pitch = pitchDegrees * MathF.PI / 180f;
    }
}
