using System.Numerics;
using Raylib_cs;

namespace Sable.Rendering;

/// <summary>
/// Texture x material colour with a soft directional light. <see cref="Shade"/> = 0 shows the texture exactly as
/// painted (flat), which is what you want while judging pixel colours. A texture's smoothness mask comes in as
/// <c>texture1</c> with its settings in <c>colSpecular</c> (see <see cref="GpuModel"/>): it tints the surface red where
/// painted while it's shown, and adds a highlight that tightens and brightens with smoothness (scaled by the lighting).
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
out vec3 fragPosition;
void main()
{
    fragTexCoord = vertexTexCoord;
    fragPosition = vertexPosition; // models are drawn with an identity transform
    fragNormal = normalize((matNormal * vec4(vertexNormal, 0.0)).xyz);
    gl_Position = mvp * vec4(vertexPosition, 1.0);
}";

    private const string Fragment = @"#version 330
in vec2 fragTexCoord;
in vec3 fragNormal;
in vec3 fragPosition;
uniform sampler2D texture0;
uniform sampler2D texture1;
uniform vec4 colDiffuse;
// The smoothness mask: r = red overlay strength, g = smoothness where fully painted, a = 1 when there is a mask.
uniform vec4 colSpecular;
uniform vec3 lightDir;
uniform vec3 viewPos;
uniform float shade;
out vec4 finalColor;
void main()
{
    vec4 color = texture(texture0, fragTexCoord) * colDiffuse;
    vec3 n = normalize(fragNormal);
    if (!gl_FrontFacing) n = -n;
    float light = 0.55 + 0.45 * max(dot(n, -lightDir), 0.0) + 0.08 * n.y;
    vec3 rgb = color.rgb * mix(1.0, light, shade);
    if (colSpecular.a > 0.5)
    {
        float mask = texture(texture1, fragTexCoord).a;
        float smoothness = mask * colSpecular.g;
        vec3 h = normalize(normalize(viewPos - fragPosition) - lightDir);
        // A tight highlight, plus a broad sheen so smooth parts show even on flat low-poly faces.
        float ndh = max(dot(n, h), 0.0);
        float spec = smoothness * (0.6 * pow(ndh, exp2(1.0 + 8.0 * smoothness)) + 0.3 * pow(ndh, 8.0));
        rgb += vec3(spec * shade * step(0.0, dot(n, -lightDir)));
        rgb = mix(rgb, vec3(1.0, 0.12, 0.12), mask * colSpecular.r);
    }
    finalColor = vec4(rgb, color.a);
}";

    public Shader Shader { get; }
    private readonly int lightDirLoc, shadeLoc, viewPosLoc;

    public unsafe LitShader()
    {
        Shader = Raylib.LoadShaderFromMemory(Vertex, Fragment);
        lightDirLoc = Raylib.GetShaderLocation(Shader, "lightDir");
        shadeLoc = Raylib.GetShaderLocation(Shader, "shade");
        viewPosLoc = Raylib.GetShaderLocation(Shader, "viewPos");
        // raylib has no default name for the specular colour; with the location set, DrawMesh sends each material's
        // specular map colour there (and binds its texture to texture1).
        Shader.Locs[(int)ShaderLocationIndex.ColorSpecular] = Raylib.GetShaderLocation(Shader, "colSpecular");
    }

    public void Set(Vector3 lightDir, float shade, Vector3 viewPos)
    {
        Raylib.SetShaderValue(Shader, lightDirLoc, Vector3.Normalize(lightDir), ShaderUniformDataType.Vec3);
        Raylib.SetShaderValue(Shader, shadeLoc, shade, ShaderUniformDataType.Float);
        Raylib.SetShaderValue(Shader, viewPosLoc, viewPos, ShaderUniformDataType.Vec3);
    }

    public void Dispose() => Raylib.UnloadShader(Shader);
}
