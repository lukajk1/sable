using System.Text.Json;

namespace Sable.Model;

/// <summary>
/// The file the Sable Link add-on for Blender writes (<c>link.json</c>, beside the <c>model.glb</c> it exports):
/// which revision of the linked objects is on disk, the .blend they come from, and the image file each material
/// paints. Blender rewrites both files when the objects' geometry or UVs change; Sable watches the revision.
/// </summary>
public sealed class BlenderLink
{
    public int Version { get; set; }
    public int Revision { get; set; }
    /// <summary>The .blend's path, or empty while it has never been saved.</summary>
    public string Blend { get; set; } = "";
    public string Model { get; set; } = "model.glb";
    public List<string> Objects { get; set; } = new();
    public Dictionary<string, string> Images { get; set; } = new();

    public static bool IsLinkFile(string path) => Path.GetFileName(path).Equals("link.json", StringComparison.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    /// <summary>The link, or null if the file is gone, half-written or not a link.</summary>
    public static BlenderLink? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var link = JsonSerializer.Deserialize<BlenderLink>(stream, Options);
            return link is { Version: >= 1 } ? link : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
