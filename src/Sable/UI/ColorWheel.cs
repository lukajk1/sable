using System.Numerics;
using ImGuiNET;

namespace Sable.UI;

/// <summary>
/// Photoshop-style picker: a hue ring around a saturation (left to right) / value (bottom to top) square.
/// Colours are HSV in 0..1.
/// </summary>
public static class ColorWheel
{
    private enum Target { None, Ring, Square }
    private static Target dragging;

    public static bool Draw(string id, ref Vector3 hsv, float size)
    {
        var drawList = ImGui.GetWindowDrawList();
        Vector2 origin = ImGui.GetCursorScreenPos();
        ImGui.InvisibleButton(id, new Vector2(size, size));

        Vector2 center = origin + new Vector2(size * 0.5f);
        float outer = size * 0.5f, inner = outer * 0.80f;
        float half = inner * 0.68f;
        Vector2 mouse = ImGui.GetIO().MousePos;

        if (ImGui.IsItemActivated())
        {
            float d = Vector2.Distance(mouse, center);
            dragging = d >= inner - 2f ? Target.Ring
                : MathF.Abs(mouse.X - center.X) <= half + 6f && MathF.Abs(mouse.Y - center.Y) <= half + 6f ? Target.Square
                : Target.None;
        }
        if (!ImGui.IsItemActive()) dragging = Target.None;

        bool changed = false;
        if (dragging == Target.Ring)
        {
            Vector2 v = mouse - center;
            hsv.X = (MathF.Atan2(v.Y, v.X) / (2f * MathF.PI) + 1f) % 1f;
            changed = true;
        }
        else if (dragging == Target.Square)
        {
            hsv.Y = Math.Clamp((mouse.X - (center.X - half)) / (2f * half), 0f, 1f);
            hsv.Z = Math.Clamp(1f - (mouse.Y - (center.Y - half)) / (2f * half), 0f, 1f);
            changed = true;
        }

        const int segments = 120;
        for (int i = 0; i < segments; i++)
        {
            float a0 = i / (float)segments * 2f * MathF.PI, a1 = (i + 1.3f) / segments * 2f * MathF.PI;
            uint color = ToU32(HsvToRgb(new Vector3((i + 0.5f) / segments, 1f, 1f)));
            Vector2 d0 = new(MathF.Cos(a0), MathF.Sin(a0)), d1 = new(MathF.Cos(a1), MathF.Sin(a1));
            drawList.AddQuadFilled(center + d0 * inner, center + d0 * outer, center + d1 * outer, center + d1 * inner, color);
        }

        Vector2 min = center - new Vector2(half), max = center + new Vector2(half);
        uint hue = ToU32(HsvToRgb(new Vector3(hsv.X, 1f, 1f)));
        uint white = ToU32(Vector3.One), black = ToU32(Vector3.Zero), clear = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 0));
        drawList.AddRectFilledMultiColor(min, max, white, hue, hue, white);
        drawList.AddRectFilledMultiColor(min, max, clear, clear, black, black);

        float ringMid = (inner + outer) * 0.5f, ringWidth = outer - inner;
        Vector2 hueAt = center + new Vector2(MathF.Cos(hsv.X * 2f * MathF.PI), MathF.Sin(hsv.X * 2f * MathF.PI)) * ringMid;
        drawList.AddCircle(hueAt, ringWidth * 0.42f, black, 0, 3f);
        drawList.AddCircle(hueAt, ringWidth * 0.42f, white, 0, 1.5f);

        Vector2 svAt = new(min.X + hsv.Y * 2f * half, min.Y + (1f - hsv.Z) * 2f * half);
        drawList.AddCircle(svAt, 6f, black, 0, 3f);
        drawList.AddCircle(svAt, 6f, white, 0, 1.5f);
        return changed;
    }

    private static uint ToU32(Vector3 rgb) => ImGui.ColorConvertFloat4ToU32(new Vector4(rgb, 1f));

    public static Vector3 HsvToRgb(Vector3 hsv)
    {
        float h = (hsv.X % 1f + 1f) % 1f * 6f, s = hsv.Y, v = hsv.Z;
        int i = (int)MathF.Floor(h) % 6;
        float f = h - MathF.Floor(h);
        float p = v * (1 - s), q = v * (1 - s * f), t = v * (1 - s * (1 - f));
        return i switch
        {
            0 => new Vector3(v, t, p),
            1 => new Vector3(q, v, p),
            2 => new Vector3(p, v, t),
            3 => new Vector3(p, q, v),
            4 => new Vector3(t, p, v),
            _ => new Vector3(v, p, q),
        };
    }

    /// <summary>RGB to HSV. Greys keep <paramref name="previousHue"/>, so sampling one doesn't reset the ring.</summary>
    public static Vector3 RgbToHsv(Vector3 rgb, float previousHue)
    {
        float max = MathF.Max(rgb.X, MathF.Max(rgb.Y, rgb.Z)), min = MathF.Min(rgb.X, MathF.Min(rgb.Y, rgb.Z));
        float delta = max - min;
        float h = previousHue;
        if (delta > 1e-5f)
        {
            if (max == rgb.X) h = (rgb.Y - rgb.Z) / delta / 6f;
            else if (max == rgb.Y) h = ((rgb.Z - rgb.X) / delta + 2f) / 6f;
            else h = ((rgb.X - rgb.Y) / delta + 4f) / 6f;
            h = (h % 1f + 1f) % 1f;
        }
        return new Vector3(h, max <= 0 ? 0 : delta / max, max);
    }
}
