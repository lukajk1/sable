using System.Numerics;
using Raylib_cs;

namespace Sable.Rendering;

/// <summary>
/// The selection outline, as in Blender: the scene is drawn again into a mask (the outlined object white,
/// everything else black, depth-tested, so what's in front of the object hides its outline), and a shader draws a
/// band just outside the mask's white areas over the view.
/// </summary>
public sealed unsafe class OutlineRenderer : IDisposable
{
    private const string FlatFragment = @"#version 330
uniform vec4 colDiffuse;
out vec4 finalColor;
void main() { finalColor = colDiffuse; }";

    private const string FlatVertex = @"#version 330
in vec3 vertexPosition;
uniform mat4 mvp;
void main() { gl_Position = mvp * vec4(vertexPosition, 1.0); }";

    private const string EdgeFragment = @"#version 330
in vec2 fragTexCoord;
uniform sampler2D texture0;
uniform vec2 texel;
uniform vec4 outlineColor;
out vec4 finalColor;
void main()
{
    if (texture(texture0, fragTexCoord).r > 0.5) { finalColor = vec4(0.0); return; }
    float near = 0.0;
    for (int x = -2; x <= 2; x++)
    for (int y = -2; y <= 2; y++)
    {
        if (x * x + y * y > 5) continue;
        near = max(near, texture(texture0, fragTexCoord + vec2(x, y) * texel).r);
    }
    finalColor = near > 0.5 ? outlineColor : vec4(0.0);
}";

    private readonly Shader flat, edge;
    private readonly int texelLoc, colorLoc;
    private Material white, black;
    private RenderTexture2D mask;
    private int width, height;

    public OutlineRenderer()
    {
        flat = Raylib.LoadShaderFromMemory(FlatVertex, FlatFragment);
        edge = Raylib.LoadShaderFromMemory(null, EdgeFragment);
        texelLoc = Raylib.GetShaderLocation(edge, "texel");
        colorLoc = Raylib.GetShaderLocation(edge, "outlineColor");
        white = MakeMaterial(Color.White);
        black = MakeMaterial(Color.Black);
    }

    private Material MakeMaterial(Color color)
    {
        var material = Raylib.LoadMaterialDefault();
        material.Shader = flat;
        material.Maps[(int)MaterialMapIndex.Albedo].Color = color;
        return material;
    }

    /// <summary>Draws the mask: <paramref name="isOutlined"/> parts white, the other visible parts black.</summary>
    public void RenderMask(int w, int h, Camera3D camera, Matrix4x4 projection, GpuModel model,
        Func<int, bool> isVisible, Func<int, bool> isOutlined)
    {
        if (w != width || h != height)
        {
            if (width > 0) Raylib.UnloadRenderTexture(mask);
            mask = Raylib.LoadRenderTexture(w, h);
            width = w;
            height = h;
        }
        Raylib.BeginTextureMode(mask);
        Raylib.ClearBackground(Color.Black);
        Raylib.BeginMode3D(camera);
        Rlgl.SetMatrixProjection(projection);
        for (int i = 0; i < model.Source.Parts.Count; i++)
            if (isVisible(i)) model.DrawPart(i, isOutlined(i) ? white : black);
        Raylib.EndMode3D();
        Raylib.EndTextureMode();
    }

    /// <summary>Draws the outline over the current render target (the view), in <paramref name="color"/>.</summary>
    public void DrawOutline(Color color)
    {
        if (width <= 0) return;
        Raylib.SetShaderValue(edge, texelLoc, new Vector2(1f / width, 1f / height), ShaderUniformDataType.Vec2);
        Raylib.SetShaderValue(edge, colorLoc, new Vector4(color.R, color.G, color.B, color.A) / 255f, ShaderUniformDataType.Vec4);
        Raylib.BeginShaderMode(edge);
        // Render textures are stored upside down; drawing with a negative height puts the mask back the right way.
        Raylib.DrawTextureRec(mask.Texture, new Rectangle(0, 0, width, -height), Vector2.Zero, Color.White);
        Raylib.EndShaderMode();
    }

    public void Dispose()
    {
        if (width > 0) Raylib.UnloadRenderTexture(mask);
        // Not UnloadMaterial: that would unload the shared shader; the shaders are unloaded once below.
        Raylib.MemFree(white.Maps);
        Raylib.MemFree(black.Maps);
        Raylib.UnloadShader(flat);
        Raylib.UnloadShader(edge);
    }
}
