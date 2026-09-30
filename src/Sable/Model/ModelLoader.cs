using System.Numerics;
using Assimp;

namespace Sable.Model;

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
        var blendImages = new Dictionary<string, string>();
        string import = Path.GetExtension(source).Equals(".blend", StringComparison.OrdinalIgnoreCase)
            ? BlendConverter.Convert(source, report, out blendImages)
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
        var textures = new TextureContext(scene, Path.GetDirectoryName(source)!, model, textureLookup, blendImages);

        foreach (var material in scene.Materials)
            model.Materials.Add(ReadMaterial(material, textures));
        if (model.Materials.Count == 0) model.Materials.Add(new MaterialInfo { Name = "(default)" });

        report("Finding submeshes...");
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        Walk(scene.RootNode, System.Numerics.Matrix4x4.Identity);
        void Walk(Node node, System.Numerics.Matrix4x4 parent)
        {
            var world = ToNumerics(node.Transform) * parent;
            System.Numerics.Matrix4x4.Invert(world, out var inverse);
            var normalMatrix = System.Numerics.Matrix4x4.Transpose(inverse);

            SceneObject? obj = null;
            foreach (int meshIndex in node.MeshIndices)
            {
                obj ??= new SceneObject { Name = string.IsNullOrWhiteSpace(node.Name) ? "(unnamed)" : node.Name };
                var part = ReadPart(scene.Meshes[meshIndex], node, obj, model.Objects.Count, world, normalMatrix, model);
                if (part == null) continue;
                obj.Parts.Add(model.Parts.Count);
                model.Parts.Add(part);
                obj.Min = obj.Parts.Count == 1 ? part.Min : Vector3.Min(obj.Min, part.Min);
                obj.Max = obj.Parts.Count == 1 ? part.Max : Vector3.Max(obj.Max, part.Max);
                min = Vector3.Min(min, part.Min);
                max = Vector3.Max(max, part.Max);
            }
            if (obj is { Parts.Count: > 0 }) model.Objects.Add(obj);
            foreach (var child in node.Children) Walk(child, world);
        }

        if (model.Parts.Count == 0) throw new InvalidOperationException("The file has no triangle meshes.");
        model.Min = min;
        model.Max = max;
        return model;
    }

    private static MeshPart? ReadPart(Mesh mesh, Node node, SceneObject obj, int objectIndex, System.Numerics.Matrix4x4 world,
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
        else if (obj.Parts.Count == 0)
        {
            model.Warnings.Add($"{obj.Name}: no UVs");
        }

        int materialIndex = mesh.MaterialIndex >= 0 && mesh.MaterialIndex < model.Materials.Count ? mesh.MaterialIndex : 0;
        var indexArray = indices.ToArray();
        var components = Topology.Components(positions, indexArray, out int componentCount);
        // An object with several material slots is drawn as one part per slot.
        string name = node.MeshCount > 1 ? $"{obj.Name} [{model.Materials[materialIndex].Name}]" : obj.Name;

        return new MeshPart
        {
            Name = name,
            ObjectIndex = objectIndex,
            MaterialIndex = materialIndex,
            Positions = positions,
            Normals = normals,
            Uvs = uvs,
            Indices = indexArray,
            Min = min,
            Max = max,
            TriangleComponent = components,
            ComponentCount = componentCount,
            ComponentHidden = new bool[componentCount],
        };
    }

    private sealed record TextureContext(Scene Scene, string ModelDir, LoadedModel Model, Dictionary<string, int> Lookup,
        Dictionary<string, string> BlendImages);

    private static MaterialInfo ReadMaterial(Material material, TextureContext context)
    {
        var color = material.HasColorDiffuse
            ? new Vector4(material.ColorDiffuse.R, material.ColorDiffuse.G, material.ColorDiffuse.B, material.ColorDiffuse.A)
            : Vector4.One;

        int textureIndex = -1;
        if (material.GetMaterialTexture(TextureType.Diffuse, 0, out var slot) ||
            material.GetMaterialTexture(TextureType.BaseColor, 0, out slot))
        {
            textureIndex = FindTexture(slot.FilePath, material.Name, context);
        }

        return new MaterialInfo
        {
            Name = string.IsNullOrWhiteSpace(material.Name) ? "(unnamed)" : material.Name,
            Color = color,
            TextureIndex = textureIndex,
        };
    }

    private static int FindTexture(string? reference, string materialName, TextureContext context)
    {
        if (string.IsNullOrEmpty(reference)) return -1;
        // A .blend's images are embedded in the exported .glb; prefer the file the .blend reads, so saves land there.
        string key = context.BlendImages.TryGetValue(materialName ?? "", out string? blendFile) ? blendFile : reference;
        if (context.Lookup.TryGetValue(key, out int known)) return known;

        var model = context.Model;
        TextureSource? source = null;
        var embedded = blendFile == null ? context.Scene.GetEmbeddedTexture(reference) : null;
        if (blendFile != null)
        {
            source = FromFile(blendFile);
        }
        else if (embedded != null)
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
                Path.Combine(context.ModelDir, reference),
                Path.Combine(context.ModelDir, Path.GetFileName(reference)),
            }.FirstOrDefault(File.Exists);

            if (file != null) source = FromFile(Path.GetFullPath(file));
            else model.Warnings.Add($"texture not found: {reference}");
        }

        int index = -1;
        if (source != null)
        {
            index = model.Textures.Count;
            model.Textures.Add(source);
        }
        context.Lookup[key] = index;
        return index;
    }

    private static TextureSource FromFile(string file) => new()
    {
        Name = Path.GetFileName(file),
        Data = File.ReadAllBytes(file),
        FileType = Path.GetExtension(file).ToLowerInvariant(),
        FilePath = file,
    };

    private static Vector3 ToNumerics(Vector3D v) => new(v.X, v.Y, v.Z);

    // Assimp matrices are row-major for column vectors; System.Numerics uses row vectors, so transpose.
    private static System.Numerics.Matrix4x4 ToNumerics(Assimp.Matrix4x4 m) => new(
        m.A1, m.B1, m.C1, m.D1,
        m.A2, m.B2, m.C2, m.D2,
        m.A3, m.B3, m.C3, m.D3,
        m.A4, m.B4, m.C4, m.D4);
}
