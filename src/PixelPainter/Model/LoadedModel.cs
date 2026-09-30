using System.Numerics;

namespace PixelPainter.Model;

/// <summary>A model as plain CPU data, independent of raylib: what the loader produces off the main thread.</summary>
public sealed class LoadedModel
{
    public required string SourcePath { get; init; }
    /// <summary>The file Assimp actually read (a temporary .glb for .blend sources).</summary>
    public required string ImportedPath { get; init; }
    public List<SceneObject> Objects { get; } = new();
    public List<MeshPart> Parts { get; } = new();
    public List<MaterialInfo> Materials { get; } = new();
    public List<TextureSource> Textures { get; } = new();
    public List<string> Warnings { get; } = new();
    public Vector3 Min { get; set; }
    public Vector3 Max { get; set; }

    public string Name => Path.GetFileName(SourcePath);
}

/// <summary>An object as it was in Blender: one node, drawn as one part per material slot.</summary>
public sealed class SceneObject
{
    public required string Name { get; init; }
    public List<int> Parts { get; } = new();
    public bool Hidden { get; set; }
    public Vector3 Min { get; set; }
    public Vector3 Max { get; set; }
}

/// <summary>One material slot of an object, with world-space vertices.</summary>
public sealed class MeshPart
{
    public required string Name { get; init; }
    public required int ObjectIndex { get; init; }
    public required int MaterialIndex { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    /// <summary>First UV channel, with v = 0 at the top of the image (image space); null if the mesh has none.</summary>
    public Vector2[]? Uvs { get; init; }
    public required int[] Indices { get; init; }
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }

    /// <summary>
    /// Submesh (loose connected piece) of each triangle. Pieces are connected through shared positions, so UV seams
    /// and hard edges don't split them.
    /// </summary>
    public required int[] TriangleComponent { get; init; }
    public required int ComponentCount { get; init; }
    public required bool[] ComponentHidden { get; init; }

    public int TriangleCount => Indices.Length / 3;
    public bool HasUvs => Uvs != null;
    public bool TriangleVisible(int triangle) => !ComponentHidden[TriangleComponent[triangle]];
}

public sealed class MaterialInfo
{
    public required string Name { get; init; }
    public Vector4 Color { get; set; } = Vector4.One;
    /// <summary>Index into <see cref="LoadedModel.Textures"/>, or -1.</summary>
    public int TextureIndex { get; set; } = -1;
}

/// <summary>An encoded image (png, jpg, ...) found in or next to the model.</summary>
public sealed class TextureSource
{
    public required string Name { get; init; }
    public required byte[] Data { get; init; }
    /// <summary>Extension with the dot, e.g. ".png", as raylib's LoadImageFromMemory wants it.</summary>
    public required string FileType { get; init; }
    /// <summary>The image file on disk, when there is one: saving writes back to it. Null for embedded images.</summary>
    public string? FilePath { get; init; }
}
