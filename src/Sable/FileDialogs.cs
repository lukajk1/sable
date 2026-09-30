using System.Diagnostics;

namespace Sable;

/// <summary>
/// Open and save dialogs, run in a separate Sable process. Shell extensions that inject into the dialog (SHADE
/// Sandbox's shade.dll has crashed it) then take down only that helper, never the editor with unsaved work.
/// </summary>
internal static class FileDialogs
{
    public const string ModelFilter = "Models|*.fbx;*.gltf;*.glb;*.obj;*.blend;*.dae;*.3ds;*.ply|All files|*.*";
    public const string ImageFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tga|All files|*.*";
    public const string PngFilter = "PNG image|*.png";

    /// <summary>
    /// Shows the dialog in a helper process. Returns the chosen path, null if cancelled, or throws if the helper
    /// failed (crashed).
    /// </summary>
    public static Task<string?> PickAsync(bool save, string title, string filter, string? initialDirectory, string? fileName) =>
        Task.Run(() =>
        {
            var psi = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            foreach (string arg in new[] { "--pick", save ? "save" : "open", title, filter, initialDirectory ?? "", fileName ?? "" })
                psi.ArgumentList.Add(arg);
            using var process = Process.Start(psi) ?? throw new InvalidOperationException("Couldn't start the file dialog.");
            string output = process.StandardOutput.ReadToEnd().Trim();
            process.WaitForExit();
            if (process.ExitCode != 0)
                throw new InvalidOperationException($"The file dialog crashed (exit {process.ExitCode:X}); a shell extension such as SHADE Sandbox may be to blame. Drag files onto the window or use File > Open path instead.");
            return string.IsNullOrEmpty(output) ? null : output;
        });

    /// <summary>The helper side: <c>Sable.exe --pick open|save title filter dir name</c> prints the chosen path.</summary>
    public static int RunPicker(string[] args)
    {
        bool save = args.Length > 1 && args[1] == "save";
        string title = args.Length > 2 ? args[2] : "";
        string filter = args.Length > 3 ? args[3] : "All files|*.*";
        string directory = args.Length > 4 ? args[4] : "";
        string name = args.Length > 5 ? args[5] : "";

        System.Windows.Forms.FileDialog dialog = save
            ? new System.Windows.Forms.SaveFileDialog { OverwritePrompt = true, FileName = name }
            : new System.Windows.Forms.OpenFileDialog { FileName = name };
        using (dialog)
        {
            dialog.Title = title;
            dialog.Filter = filter;
            if (Directory.Exists(directory)) dialog.InitialDirectory = directory;
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) Console.Out.Write(dialog.FileName);
        }
        return 0;
    }
}
