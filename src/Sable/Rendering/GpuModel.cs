using System.Numerics;
using Sable.Model;
using Sable.Paint;
using Raylib_cs;

namespace Sable.Rendering;

/// <summary>
/// The GPU side of a <see cref="LoadedModel"/>: one raylib mesh per part (holding only its visible submeshes), one
/// material per model material, and the paintable textures. Main thread only.
/// </summary>
public sealed unsafe class GpuModel : IDisposable
{
    public LoadedModel Source { get; }
    public List<PaintTexture> Textures { get; } = new();
    /// <summary>Unique triangle edges per part.</summary>
    public IReadOnlyList<EdgeList> Edges => edges;

    private readonly Shader shader;
    private readonly List<Material> materials = new();
    private readonly Mesh?[] meshes;
    private readonly List<EdgeList> edges = new();

    public GpuModel(LoadedModel source, Shader shader)
    {
        Source = source;
        this.shader = shader;

        foreach (var texture in source.Textures)
            Textures.Add(PaintTexture.FromEncoded(texture.Name, texture.FileType, texture.Data, texture.FilePath));

        foreach (var info in source.Materials)
        {
            Material material = Raylib.LoadMaterialDefault();
            material.Shader = shader;
            materials.Add(material);
            ApplyMaterial(materials.Count - 1);
        }

        meshes = new Mesh?[source.Parts.Count];
        for (int i = 0; i < source.Parts.Count; i++)
        {
            RebuildPart(i);
            edges.Add(EdgeList.Build(source.Parts[i]));
        }
    }

    private void ApplyMaterial(int index)
    {
        var info = Source.Materials[index];
        var material = materials[index];
        material.Maps[(int)MaterialMapIndex.Albedo].Color = ToColor(info.Color);
        material.Maps[(int)MaterialMapIndex.Albedo].Texture = info.TextureIndex >= 0
            ? Textures[info.TextureIndex].Gpu
            : new Texture2D { Id = Rlgl.GetTextureIdDefault(), Width = 1, Height = 1, Mipmaps = 1, Format = PixelFormat.UncompressedR8G8B8A8 };
        materials[index] = material;
    }

    /// <summary>
    /// Re-uploads a part's mesh with only its visible submeshes. Non-indexed (three vertices per triangle), which
    /// also sidesteps raylib's 16-bit index limit.
    /// </summary>
    public void RebuildPart(int index)
    {
        if (meshes[index] is { } old) Raylib.UnloadMesh(old);
        meshes[index] = null;

        var part = Source.Parts[index];
        int visible = 0;
        for (int t = 0; t < part.TriangleCount; t++) if (part.TriangleVisible(t)) visible++;
        if (visible == 0) return;

        var mesh = new Mesh(visible * 3, visible);
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocTexCoords();
        var positions = mesh.VerticesAs<Vector3>();
        var normals = mesh.NormalsAs<Vector3>();
        var uvs = mesh.TexCoordsAs<Vector2>();
        int o = 0;
        for (int t = 0; t < part.TriangleCount; t++)
        {
            if (!part.TriangleVisible(t)) continue;
            for (int k = 0; k < 3; k++, o++)
            {
                int v = part.Indices[t * 3 + k];
                positions[o] = part.Positions[v];
                normals[o] = part.Normals[v];
                uvs[o] = part.Uvs != null ? part.Uvs[v] : Vector2.Zero;
            }
        }
        Raylib.UploadMesh(ref mesh, false);
        meshes[index] = mesh;
    }

    public void DrawPart(int index)
    {
        if (meshes[index] is not { } mesh) return;
        Raylib.DrawMesh(mesh, materials[Source.Parts[index].MaterialIndex], Matrix4x4.Identity);
    }

    /// <summary>Draws a part with another material (the outline's mask pass).</summary>
    public void DrawPart(int index, Material material)
    {
        if (meshes[index] is not { } mesh) return;
        Raylib.DrawMesh(mesh, material, Matrix4x4.Identity);
    }

    /// <summary>The texture a part is painted on, or -1.</summary>
    public int TextureOf(int partIndex) =>
        partIndex < 0 ? -1 : Source.Materials[Source.Parts[partIndex].MaterialIndex].TextureIndex;

    /// <summary>
    /// Gives a material a new texture filled with its colour; the material turns white so the texture shows as
    /// painted. Every part using the material gets it, as in Blender.
    /// </summary>
    public int CreateTexture(int materialIndex, int size)
    {
        var info = Source.Materials[materialIndex];
        var texture = PaintTexture.Create($"{info.Name}.png", size, size, ToColor(info.Color with { W = 1 }));
        Textures.Add(texture);
        info.TextureIndex = Textures.Count - 1;
        info.Color = Vector4.One;
        ApplyMaterial(materialIndex);
        return info.TextureIndex;
    }

    public void UploadTextures()
    {
        foreach (var texture in Textures) texture.Upload();
    }

    public static Color ToColor(Vector4 c) => new(
        (byte)Math.Clamp(c.X * 255f, 0, 255), (byte)Math.Clamp(c.Y * 255f, 0, 255),
        (byte)Math.Clamp(c.Z * 255f, 0, 255), (byte)Math.Clamp(c.W * 255f, 0, 255));

    public void Dispose()
    {
        foreach (var mesh in meshes) if (mesh is { } m) Raylib.UnloadMesh(m);
        // Not UnloadMaterial: it would also unload the shared shader and the textures, which are freed below.
        foreach (var material in materials) Raylib.MemFree(material.Maps);
        foreach (var texture in Textures) texture.Dispose();
        materials.Clear();
        Textures.Clear();
    }
}

/// <summary>A part's unique edges, each tagged with the submesh it belongs to.</summary>
public sealed class EdgeList
{
    public required int[] A { get; init; }
    public required int[] B { get; init; }
    public required int[] Component { get; init; }

    public static EdgeList Build(MeshPart part)
    {
        var seen = new HashSet<long>();
        var a = new List<int>();
        var b = new List<int>();
        var component = new List<int>();
        var indices = part.Indices;
        for (int t = 0; t < part.TriangleCount; t++)
        {
            for (int e = 0; e < 3; e++)
            {
                int i0 = indices[t * 3 + e], i1 = indices[t * 3 + (e + 1) % 3];
                long key = i0 < i1 ? ((long)i0 << 32) | (uint)i1 : ((long)i1 << 32) | (uint)i0;
                if (!seen.Add(key)) continue;
                a.Add(i0);
                b.Add(i1);
                component.Add(part.TriangleComponent[t]);
            }
        }
        return new EdgeList { A = a.ToArray(), B = b.ToArray(), Component = component.ToArray() };
    }
}
