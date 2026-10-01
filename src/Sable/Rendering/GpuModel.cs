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
    /// <summary>Set when a newer model took over the textures, so disposing this one leaves them alone.</summary>
    private bool texturesHandedOver;

    /// <param name="previous">
    /// The model this one replaces with new geometry (a Blender link update). Its textures carry over as they are,
    /// painting, layers and undo included: a texture from the same file (or of the same name) is kept instead of
    /// decoding it again, a material keeps the texture Sable gave it, and textures made in Sable stay available.
    /// </param>
    public GpuModel(LoadedModel source, Shader shader, GpuModel? previous = null)
    {
        Source = source;
        this.shader = shader;

        foreach (var texture in source.Textures)
        {
            var kept = previous?.Textures.FirstOrDefault(p => !Textures.Contains(p) && (texture.FilePath != null
                ? string.Equals(p.FilePath, texture.FilePath, StringComparison.OrdinalIgnoreCase)
                : p.FilePath == null && p.Name == texture.Name));
            Textures.Add(kept ?? PaintTexture.FromEncoded(texture.Name, texture.FileType, texture.Data, texture.FilePath));
        }
        if (previous != null)
        {
            foreach (var info in source.Materials)
            {
                var before = previous.Source.Materials.FirstOrDefault(m => m.Name == info.Name);
                if (before == null || before.TextureIndex < 0) continue;
                var texture = previous.Textures[before.TextureIndex];
                if (!Textures.Contains(texture)) Textures.Add(texture);
                info.TextureIndex = Textures.IndexOf(texture);
                info.Color = before.Color;
            }
            foreach (var texture in previous.Textures)
                if (!Textures.Contains(texture)) Textures.Add(texture);
            previous.texturesHandedOver = true;
        }

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
    /// Gives a material a new texture (replacing the one it had, which stays in <see cref="Textures"/>), filled with
    /// <paramref name="fill"/>, or with the material's colour when null. The material turns white so the texture
    /// shows as painted. Every part using the material gets it, as in Blender.
    /// </summary>
    public int CreateTexture(int materialIndex, int width, int height, Color? fill = null, string? name = null)
    {
        var info = Source.Materials[materialIndex];
        name ??= UniqueTextureName(info.Name);
        var texture = PaintTexture.Create(name, width, height, fill ?? ToColor(info.BaseColor with { W = 1 }));
        Textures.Add(texture);
        AssignTexture(materialIndex, Textures.Count - 1);
        return Textures.Count - 1;
    }

    /// <summary>"&lt;stem&gt;.png", or "&lt;stem&gt;_2.png" and so on when a texture already has that name.</summary>
    public string UniqueTextureName(string stem)
    {
        foreach (char c in Path.GetInvalidFileNameChars()) stem = stem.Replace(c, '_');
        string name = $"{stem}.png";
        for (int n = 2; Textures.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)); n++) name = $"{stem}_{n}.png";
        return name;
    }

    /// <summary>Makes a material use one of the model's textures, shown untinted (the material turns white).</summary>
    public void AssignTexture(int materialIndex, int textureIndex)
    {
        Source.Materials[materialIndex].TextureIndex = textureIndex;
        Source.Materials[materialIndex].Color = Vector4.One;
        ApplyMaterial(materialIndex);
    }

    /// <summary>Swaps in a different texture (an imported image) for texture <paramref name="index"/>.</summary>
    public void ReplaceTexture(int index, PaintTexture replacement)
    {
        Textures[index].Dispose();
        Textures[index] = replacement;
        for (int m = 0; m < Source.Materials.Count; m++)
            if (Source.Materials[m].TextureIndex == index) ApplyMaterial(m);
    }

    /// <summary>
    /// Paints the texels a material's UV islands cover (plus a texel around them, so seams don't show the colour
    /// underneath) with <paramref name="color"/>.
    /// </summary>
    public void FillUvIslands(PaintTexture texture, int materialIndex, Color color)
    {
        // A texel diagonal of dilation reaches every texel the island's outline touches.
        var mask = UvRaster.Coverage(Source, p => Source.Parts[p].MaterialIndex == materialIndex, texture.Width, texture.Height, 1.415f);
        var pixels = texture.Pixels;
        for (int i = 0; i < mask.Length; i++)
            if (mask[i]) pixels[i] = color;
        texture.Touch();
    }

    public void UploadTextures()
    {
        foreach (var texture in Textures) texture.Upload();
        // A resized texture (or its undo) has a new GPU texture: point its materials at it.
        for (int m = 0; m < materials.Count; m++)
        {
            int index = Source.Materials[m].TextureIndex;
            if (index >= 0 && materials[m].Maps[(int)MaterialMapIndex.Albedo].Texture.Id != Textures[index].Gpu.Id) ApplyMaterial(m);
        }
    }

    public static Color ToColor(Vector4 c) => new(
        (byte)Math.Clamp(c.X * 255f, 0, 255), (byte)Math.Clamp(c.Y * 255f, 0, 255),
        (byte)Math.Clamp(c.Z * 255f, 0, 255), (byte)Math.Clamp(c.W * 255f, 0, 255));

    public void Dispose()
    {
        foreach (var mesh in meshes) if (mesh is { } m) Raylib.UnloadMesh(m);
        // Not UnloadMaterial: it would also unload the shared shader and the textures, which are freed below.
        foreach (var material in materials) Raylib.MemFree(material.Maps);
        if (!texturesHandedOver) foreach (var texture in Textures) texture.Dispose();
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
    /// <summary>
    /// The edge is on the border of its UV island: no other triangle shares it with the same positions and UVs
    /// (comparing coordinates, not vertex indices, which Assimp splits at hard normals too).
    /// </summary>
    public required bool[] Boundary { get; init; }

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
        // Weld corners on position + UV, then count how many triangles use each welded edge.
        var welded = new int[part.Positions.Length];
        var ids = new Dictionary<(long, long, long, long, long), int>();
        var extent = part.Max - part.Min;
        float step = MathF.Max(MathF.Max(extent.X, extent.Y), MathF.Max(extent.Z, 1e-6f)) * 1e-5f;
        for (int v = 0; v < welded.Length; v++)
        {
            var p = part.Positions[v];
            var uv = part.Uvs != null ? part.Uvs[v] : Vector2.Zero;
            var key = ((long)MathF.Round(p.X / step), (long)MathF.Round(p.Y / step), (long)MathF.Round(p.Z / step),
                (long)MathF.Round(uv.X * 1e6f), (long)MathF.Round(uv.Y * 1e6f));
            if (!ids.TryGetValue(key, out int id)) ids[key] = id = ids.Count;
            welded[v] = id;
        }
        static long Key(int x, int y) => x < y ? ((long)x << 32) | (uint)y : ((long)y << 32) | (uint)x;
        var uses = new Dictionary<long, int>();
        for (int t = 0; t < part.TriangleCount; t++)
        for (int e = 0; e < 3; e++)
        {
            long k = Key(welded[indices[t * 3 + e]], welded[indices[t * 3 + (e + 1) % 3]]);
            uses[k] = uses.GetValueOrDefault(k) + 1;
        }
        var boundary = new bool[a.Count];
        for (int i = 0; i < a.Count; i++) boundary[i] = uses.GetValueOrDefault(Key(welded[a[i]], welded[b[i]])) < 2;
        return new EdgeList { A = a.ToArray(), B = b.ToArray(), Component = component.ToArray(), Boundary = boundary };
    }
}
