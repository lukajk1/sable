using System.Numerics;
using Raylib_cs;

namespace PixelPainter.Rendering;

/// <summary>
/// Texture x material colour with a soft directional light. <see cref="Shade"/> = 0 shows the texture exactly as
/// painted (flat), which is what you want while judging pixel colours.
/// </summary>
public sealed class LitShader : IDisposable
{
    private const string Vertex = @"#version 330
in vec3 vertexPosition;
in vec2 vertexTexCoord;
in vec3 vertexNormal;
uniform mat4 mvp;
uniform mat4 matNormal;
out vec2 fragTexCoord;
out vec3 fragNormal;
void main()
{
    fragTexCoord = vertexTexCoord;
    fragNormal = normalize((matNormal * vec4(vertexNormal, 0.0)).xyz);
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}";

    private const string Fragment = @"#version 330
in vec2 fragTexCoord;
in vec3 fragNormal;
uniform sampler2D texture0;
uniform vec4 colDiffuse;
uniform vec3 lightDir;
uniform float shade;
out vec4 finalColor;
void main()
{
    vec4 color = texture(texture0, fragTexCoord) * colDiffuse;
    vec3 n = normalize(fragNormal);
    if (!gl_FrontFacing) n = -n;
    float light = 0.55 + 0.45 * max(dot(n, -lightDir), 0.0) + 0.08 * n.y;
    finalColor = vec4(color.rgb * mix(1.0, light, shade), color.a);
}";

    public Shader Shader { get; }
    private readonly int lightDirLoc, shadeLoc;

    public LitShader()
    {
        Shader = Raylib.LoadShaderFromMemory(Vertex, Fragment);
        lightDirLoc = Raylib.GetShaderLocation(Shader, "lightDir");
        shadeLoc = Raylib.GetShaderLocation(Shader, "shade");
    }

    public void Set(Vector3 lightDir, float shade)
    {
        Raylib.SetShaderValue(Shader, lightDirLoc, Vector3.Normalize(lightDir), ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(Shader, shadeLoc, shade, ShaderUniformDataType.Float);
    }

    public void Dispose() => Raylib.UnloadShader(Shader);
}
