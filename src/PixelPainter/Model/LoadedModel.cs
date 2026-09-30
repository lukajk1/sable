using System.Numerics;

namespace PixelPainter.Model;

/// <summary>A model as plain CPU data, independent of raylib: what the loader produces off the main thread.</summary>
public sealed class LoadedModel
{
    public required string SourcePath { get; init; }
    /// <summary>The file Assimp actually read (a temporary .glb for .blend sources).</summary>
    public required string ImportedPath { get; init; }
    public List<MeshPart> Parts { get; } = new();
    public List<MaterialInfo> Materials { get; } = new();
    public List<TextureSource> Textures { get; } = new();
    public List<string> Warnings { get; } = new();
    public Vector3 Min { get; set; }
    public Vector3 Max { get; set; }

    public string Name => Path.GetFileName(SourcePath);
}

/// <summary>One drawable piece: an object (or one material slot of it) with world-space vertices.</summary>
public sealed class MeshPart
{
    public required string Name { get; init; }
    public required int MaterialIndex { get; init; }
    public required Vector3[] Positions { get; init; }
    public required Vector3[] Normals { get; init; }
    /// <summary>First UV channel, with v = 0 at the top of the image (image space); null if the mesh has none.</summary>
    public Vector2[]? Uvs { get; init; }
    public required int[] Indices { get; init; }
    public required Vector3 Min { get; init; }
    public required Vector3 Max { get; init; }
    public bool Visible { get; set; } = true;

    public int TriangleCount => Indices.Length / 3;
    public bool HasUvs => Uvs != null;
}

public sealed class MaterialInfo
{
    public required string Name { get; init; }
    public Vector4 Color { get; init; } = Vector4.One;
    /// <summary>Index into <see cref="LoadedModel.Textures"/>, or -1.</summary>
    public int TextureIndex { get; init; } = -1;
}

/// <summary>An encoded image (png, jpg, ...) found in or next to the model.</summary>
public sealed class TextureSource
{
    public required string Name { get; init; }
    public required byte[] Data { get; init; }
    /// <summary>Extension with the dot, e.g. ".png", as raylib's LoadImageFromMemory wants it.</summary>
    public required string FileType { get; init; }
}
