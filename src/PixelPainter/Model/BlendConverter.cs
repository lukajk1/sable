using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace PixelPainter.Model;

/// <summary>
/// Opens .blend files the way Unity does: runs Blender in the background to export a .glb, which Assimp then reads.
/// Uses the newest "Blender Foundation/Blender x.y" install, or the PIXELPAINTER_BLENDER environment variable.
/// </summary>
public static class BlendConverter
{
    public static string? FindBlender()
    {
        string? env = Environment.GetEnvironmentVariable("PIXELPAINTER_BLENDER");
        if (!string.IsNullOrEmpty(env) && File.Exists(env)) return env;

        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
        if (!Directory.Exists(root)) return null;
        return Directory.GetDirectories(root)
            .Select(dir => (dir, version: ParseVersion(Path.GetFileName(dir))))
            .Where(x => File.Exists(Path.Combine(x.dir, "blender.exe")))
            .OrderByDescending(x => x.version)
            .Select(x => Path.Combine(x.dir, "blender.exe"))
            .FirstOrDefault();
    }

    private static Version ParseVersion(string folder)
    {
        string digits = new(folder.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return Version.TryParse(digits, out var v) ? v : new Version(0, 0);
    }

    /// <summary>
    /// Exports <paramref name="blendPath"/> to a temporary .glb and returns its path. Blocks until done.
    /// <paramref name="imageFiles"/> maps material names to the image file each one's texture node reads (unpacked
    /// images only), so painting can be saved back to the file the .blend (and Unity) actually use.
    /// </summary>
    public static string Convert(string blendPath, Action<string> report, out Dictionary<string, string> imageFiles)
    {
        string blender = FindBlender() ?? throw new InvalidOperationException(
            "Blender not found. Install it under Program Files/Blender Foundation or set PIXELPAINTER_BLENDER.");

        string full = Path.GetFullPath(blendPath);
        string hash = System.Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full)))[..8];
        string outDir = Path.Combine(Path.GetTempPath(), "PixelPainter");
        Directory.CreateDirectory(outDir);
        string outPath = Path.Combine(outDir, $"{Path.GetFileNameWithoutExtension(full)}-{hash}.glb");
        string mapPath = Path.ChangeExtension(outPath, ".images.json");
        if (File.Exists(outPath)) File.Delete(outPath);
        if (File.Exists(mapPath)) File.Delete(mapPath);

        // Everything in the file, modifiers applied; cameras and lights are left out by default.
        string script =
            "import bpy, json\n" +
            $"bpy.ops.export_scene.gltf(filepath=r'{outPath}', export_format='GLB', export_apply=True, use_visible=False)\n" +
            "images = {}\n" +
            "for mat in bpy.data.materials:\n" +
            "    tree = getattr(mat, 'node_tree', None)\n" +
            "    if not tree: continue\n" +
            "    for node in tree.nodes:\n" +
            "        img = getattr(node, 'image', None)\n" +
            "        if node.type == 'TEX_IMAGE' and img and img.source == 'FILE' and not img.packed_file:\n" +
            "            images[mat.name] = bpy.path.abspath(img.filepath, library=img.library)\n" +
            "            break\n" +
            $"json.dump(images, open(r'{mapPath}', 'w'))\n";

        report($"Converting with {Path.GetFileName(Path.GetDirectoryName(blender))}...");
        var psi = new ProcessStartInfo(blender)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string arg in new[] { "--background", full, "--python-expr", script }) psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Could not start Blender.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(2)))
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException("Blender took more than 2 minutes to export.");
        }
        if (!File.Exists(outPath))
        {
            string log = (stderr.Result + "\n" + stdout.Result).Trim();
            string tail = log.Length > 600 ? log[^600..] : log;
            throw new InvalidOperationException($"Blender did not produce a .glb (exit {process.ExitCode}).\n{tail}");
        }

        imageFiles = new Dictionary<string, string>();
        if (File.Exists(mapPath))
        {
            var map = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(mapPath));
            if (map != null)
                foreach (var (material, file) in map)
                    if (File.Exists(file)) imageFiles[material] = Path.GetFullPath(file);
        }
        return outPath;
    }
}
