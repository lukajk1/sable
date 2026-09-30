using System.Numerics;
using PixelPainter.Model;
using Raylib_cs;

namespace PixelPainter.Rendering;

/// <summary>
/// The GPU side of a <see cref="LoadedModel"/>: one raylib mesh per part, one material per model material, and the
/// textures (point-filtered, as pixel art should be). Must be created and disposed on the main thread.
/// </summary>
public sealed unsafe class GpuModel : IDisposable
{
    public LoadedModel Source { get; }
    public IReadOnlyList<Texture2D> Textures => textures;
    /// <summary>CPU copies of the textures, kept for painting.</summary>
    public IReadOnlyList<Image> Images => images;
    /// <summary>Unique triangle edges per part, as index pairs into the part's vertices.</summary>
    public IReadOnlyList<int[]> Edges => edges;

    private readonly List<Texture2D> textures = new();
    private readonly List<Image> images = new();
    private readonly List<Material> materials = new();
    private readonly List<Mesh> meshes = new();
    private readonly List<int[]> edges = new();

    public GpuModel(LoadedModel source, Shader shader)
    {
        Source = source;

        foreach (var texture in source.Textures)
        {
            Image image = Raylib.LoadImageFromMemory(texture.FileType, texture.Data);
            if (image.Width == 0)
            {
                source.Warnings.Add($"{texture.Name}: could not decode");
                image = Raylib.GenImageColor(1, 1, Color.Magenta);
            }
            Raylib.ImageFormat(ref image, PixelFormat.UncompressedR8G8B8A8);
            images.Add(image);
            var gpu = Raylib.LoadTextureFromImage(image);
            Raylib.SetTextureFilter(gpu, TextureFilter.Point);
            Raylib.SetTextureWrap(gpu, TextureWrap.Repeat);
            textures.Add(gpu);
        }

        foreach (var info in source.Materials)
        {
            Material material = Raylib.LoadMaterialDefault();
            material.Shader = shader;
            material.Maps[(int)MaterialMapIndex.Albedo].Color = ToColor(info.Color);
            if (info.TextureIndex >= 0) material.Maps[(int)MaterialMapIndex.Albedo].Texture = textures[info.TextureIndex];
            materials.Add(material);
        }

        foreach (var part in source.Parts)
        {
            meshes.Add(BuildMesh(part));
            edges.Add(BuildEdges(part.Indices));
        }
    }

    /// <summary>
    /// Non-indexed (each triangle gets its own three vertices), which sidesteps raylib's 16-bit index limit.
    /// </summary>
    private static Mesh BuildMesh(MeshPart part)
    {
        int vertexCount = part.Indices.Length;
        var mesh = new Mesh(vertexCount, part.TriangleCount);
        mesh.AllocVertices();
        mesh.AllocNormals();
        mesh.AllocTexCoords();
        var positions = mesh.VerticesAs<Vector3>();
        var normals = mesh.NormalsAs<Vector3>();
        var uvs = mesh.TexCoordsAs<Vector2>();
        for (int i = 0; i < vertexCount; i++)
        {
            int v = part.Indices[i];
            positions[i] = part.Positions[v];
            normals[i] = part.Normals[v];
            uvs[i] = part.Uvs != null ? part.Uvs[v] : Vector2.Zero;
        }
        Raylib.UploadMesh(ref mesh, false);
        return mesh;
    }

    private static int[] BuildEdges(int[] indices)
    {
        var seen = new HashSet<long>();
        var result = new List<int>();
        for (int t = 0; t < indices.Length; t += 3)
        {
            for (int e = 0; e < 3; e++)
            {
                int a = indices[t + e], b = indices[t + (e + 1) % 3];
                long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                if (!seen.Add(key)) continue;
                result.Add(a);
                result.Add(b);
            }
        }
        return result.ToArray();
    }

    public void DrawPart(int index)
    {
        var part = Source.Parts[index];
        Raylib.DrawMesh(meshes[index], materials[part.MaterialIndex], Matrix4x4.Identity);
    }

    /// <summary>The nearest visible part the ray hits, or -1.</summary>
    public int Pick(Ray ray)
    {
        int best = -1;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < meshes.Count; i++)
        {
            if (!Source.Parts[i].Visible) continue;
            var hit = Raylib.GetRayCollisionMesh(ray, meshes[i], Matrix4x4.Identity);
            if (hit.Hit && hit.Distance < bestDistance)
            {
                bestDistance = hit.Distance;
                best = i;
            }
        }
        return best;
    }

    /// <summary>The texture a part is painted on, or -1.</summary>
    public int TextureOf(int partIndex) =>
        partIndex < 0 ? -1 : Source.Materials[Source.Parts[partIndex].MaterialIndex].TextureIndex;

    private static Color ToColor(Vector4 c) => new(
        (byte)Math.Clamp(c.X * 255f, 0, 255), (byte)Math.Clamp(c.Y * 255f, 0, 255),
        (byte)Math.Clamp(c.Z * 255f, 0, 255), (byte)Math.Clamp(c.W * 255f, 0, 255));

    public void Dispose()
    {
        foreach (var mesh in meshes) Raylib.UnloadMesh(mesh);
        // Not UnloadMaterial: it would also unload the shared shader and the textures, which are freed below.
        foreach (var material in materials) Raylib.MemFree(material.Maps);
        foreach (var texture in textures) Raylib.UnloadTexture(texture);
        foreach (var image in images) Raylib.UnloadImage(image);
        meshes.Clear();
        materials.Clear();
        textures.Clear();
        images.Clear();
    }
}
