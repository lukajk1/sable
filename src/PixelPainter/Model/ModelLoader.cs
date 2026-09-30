using System.Numerics;
using Assimp;

namespace PixelPainter.Model;

/// <summary>
/// Reads FBX / glTF / GLB / OBJ (anything Assimp knows) and .blend (via <see cref="BlendConverter"/>) into a
/// <see cref="LoadedModel"/>. Pure CPU work, safe to run off the main thread.
/// </summary>
public static class ModelLoader
{
    public static readonly string[] Extensions = { ".fbx", ".gltf", ".glb", ".obj", ".dae", ".3ds", ".ply", ".blend" };

    public static bool IsSupported(string path) =>
        Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static LoadedModel Load(string path, Action<string> report)
    {
        string source = Path.GetFullPath(path);
        string import = Path.GetExtension(source).Equals(".blend", StringComparison.OrdinalIgnoreCase)
            ? BlendConverter.Convert(source, report)
            : source;

        report("Importing...");
        using var context = new AssimpContext();
        // FlipUVs puts v = 0 at the top of the image, which is how raylib samples and how the UV view draws.
        var steps = PostProcessSteps.Triangulate
                    | PostProcessSteps.JoinIdenticalVertices
                    | PostProcessSteps.GenerateNormals
                    | PostProcessSteps.SortByPrimitiveType
                    | PostProcessSteps.FlipUVs;
        Scene scene = context.ImportFile(import, steps)
                      ?? throw new InvalidOperationException("Assimp returned no scene.");

        var model = new LoadedModel { SourcePath = source, ImportedPath = import };
        var textureLookup = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var material in scene.Materials)
            model.Materials.Add(ReadMaterial(material, scene, Path.GetDirectoryName(source)!, model, textureLookup));
        if (model.Materials.Count == 0) model.Materials.Add(new MaterialInfo { Name = "(default)" });

        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        Walk(scene.RootNode, System.Numerics.Matrix4x4.Identity);
        void Walk(Node node, System.Numerics.Matrix4x4 parent)
        {
            var world = ToNumerics(node.Transform) * parent;
            System.Numerics.Matrix4x4.Invert(world, out var inverse);
            var normalMatrix = System.Numerics.Matrix4x4.Transpose(inverse);

            foreach (int meshIndex in node.MeshIndices)
            {
                var part = ReadPart(scene, scene.Meshes[meshIndex], node, world, normalMatrix, model);
                if (part == null) continue;
                model.Parts.Add(part);
                min = Vector3.Min(min, part.Min);
                max = Vector3.Max(max, part.Max);
            }
            foreach (var child in node.Children) Walk(child, world);
        }

        if (model.Parts.Count == 0) throw new InvalidOperationException("The file has no triangle meshes.");
        model.Min = min;
        model.Max = max;
        return model;
    }

    private static MeshPart? ReadPart(Scene scene, Mesh mesh, Node node, System.Numerics.Matrix4x4 world,
        System.Numerics.Matrix4x4 normalMatrix, LoadedModel model)
    {
        if (!mesh.HasVertices) return null;
        var indices = new List<int>(mesh.FaceCount * 3);
        foreach (var face in mesh.Faces)
            if (face.IndexCount == 3) indices.AddRange(face.Indices);
        if (indices.Count == 0) return null;

        int count = mesh.VertexCount;
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        for (int i = 0; i < count; i++)
        {
            var p = Vector3.Transform(ToNumerics(mesh.Vertices[i]), world);
            positions[i] = p;
            min = Vector3.Min(min, p);
            max = Vector3.Max(max, p);
            normals[i] = mesh.HasNormals
                ? Vector3.Normalize(Vector3.TransformNormal(ToNumerics(mesh.Normals[i]), normalMatrix))
                : Vector3.UnitY;
        }

        Vector2[]? uvs = null;
        if (mesh.HasTextureCoords(0))
        {
            var channel = mesh.TextureCoordinateChannels[0];
            uvs = new Vector2[count];
            for (int i = 0; i < count; i++) uvs[i] = new Vector2(channel[i].X, channel[i].Y);
        }
        else
        {
            model.Warnings.Add($"{node.Name}: no UVs");
        }

        int materialIndex = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < model.Materials.Count ? mesh.MaterialIndex : 0;
        // An object with several material slots becomes one part per slot.
        string name = node.MeshCount > 1 ? $"{node.Name} [{model.Materials[materialIndex].Name}]" : node.Name;
        if (string.IsNullOrWhiteSpace(name)) name = string.IsNullOrWhiteSpace(mesh.Name) ? "(unnamed)" : mesh.Name;

        return new MeshPart
        {
            Name = name,
            MaterialIndex = materialIndex,
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Indices = indices.ToArray(),
            Min = min,
            Max = max,
        };
    }

    private static MaterialInfo ReadMaterial(Material material, Scene scene, string modelDir, LoadedModel model,
        Dictionary<string, int> textureLookup)
    {
        var color = material.HasColorDiffuse
            ? new Vector4(material.ColorDiffuse.R, material.ColorDiffuse.G, material.ColorDiffuse.B, material.ColorDiffuse.A)
            : Vector4.One;

        int textureIndex = -1;
        if (material.GetMaterialTexture(TextureType.Diffuse, 0, out var slot) ||
            material.GetMaterialTexture(TextureType.BaseColor, 0, out slot))
        {
            textureIndex = FindTexture(slot.FilePath, material.Name, scene, modelDir, model, textureLookup);
        }

        return new MaterialInfo
        {
            Name = string.IsNullOrWhiteSpace(material.Name) ? "(unnamed)" : material.Name,
            Color = color,
            TextureIndex = textureIndex,
        };
    }

    private static int FindTexture(string? reference, string materialName, Scene scene, string modelDir, LoadedModel model,
        Dictionary<string, int> lookup)
    {
        if (string.IsNullOrEmpty(reference)) return -1;
        if (lookup.TryGetValue(reference, out int known)) return known;

        TextureSource? source = null;
        var embedded = scene.GetEmbeddedTexture(reference);
        if (embedded != null)
        {
            if (embedded.IsCompressed && embedded.HasCompressedData)
            {
                string hint = string.IsNullOrEmpty(embedded.CompressedFormatHint) ? "png" : embedded.CompressedFormatHint;
                // Embedded references look like "*0"; name the image after its material when it has no file name.
                string name = !string.IsNullOrEmpty(embedded.Filename) ? Path.GetFileName(embedded.Filename)
                    : !string.IsNullOrWhiteSpace(materialName) ? $"{materialName} (embedded)" : reference;
                source = new TextureSource { Name = name, Data = embedded.CompressedData, FileType = "." + hint.TrimStart('.') };
            }
            else
            {
                model.Warnings.Add($"{reference}: uncompressed embedded textures aren't supported");
            }
        }
        else
        {
            string? file = new[]
            {
                reference,
                Path.Combine(modelDir, reference),
                Path.Combine(modelDir, Path.GetFileName(reference)),
            }.FirstOrDefault(File.Exists);

            if (file != null)
                source = new TextureSource { Name = Path.GetFileName(file), Data = File.ReadAllBytes(file), FileType = Path.GetExtension(file).ToLowerInvariant() };
            else
                model.Warnings.Add($"texture not found: {reference}");
        }

        int index = -1;
        if (source != null)
        {
            index = model.Textures.Count;
            model.Textures.Add(source);
        }
        lookup[reference] = index;
        return index;
    }

    private static Vector3 ToNumerics(Vector3D v) => new(v.X, v.Y, v.Z);

    // Assimp matrices are row-major for column vectors; System.Numerics uses row vectors, so transpose.
    private static System.Numerics.Matrix4x4 ToNumerics(Assimp.Matrix4x4 m) => new(
        m.A1, m.B1, m.C1, m.D1,
        m.A2, m.B2, m.C2, m.D2,
        m.A3, m.B3, m.C3, m.D3,
        m.A4, m.B4, m.C4, m.D4);
}
