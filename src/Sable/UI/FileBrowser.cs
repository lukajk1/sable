using System.Numerics;
using ImGuiNET;

namespace Sable.UI;

/// <summary>
/// Sable's own open/save dialog, drawn with ImGui. The Windows file dialogs load every shell extension installed,
/// and one of them (SHADE Sandbox's shade.dll) crashes whatever process shows them, so Sable doesn't use them.
/// </summary>
internal sealed class FileBrowser
{
    public const string ModelFilter = "Models|*.fbx;*.gltf;*.glb;*.obj;*.blend;*.dae;*.3ds;*.ply|All files|*.*";
    public const string ImageFilter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.tga|All files|*.*";
    public const string PngFilter = "PNG image|*.png";
    private const string PopupId = "###filebrowser";
    private const int MaxRecent = 8;

    private sealed record Entry(string Name, string Path, bool IsFolder, long Size, DateTime Modified);

    /// <summary>Folders files were recently opened from or saved to, newest first (kept in the settings).</summary>
    public List<string> RecentFolders { get; } = new();
    public bool IsOpen => open || pendingOpen;

    private bool open, pendingOpen, save;
    private string title = "";
    private List<(string Label, string[] Extensions)> filters = new();
    private int filterIndex;
    private string directory = "";
    private string pathInput = "";
    private string nameInput = "";
    private string? error;
    private string? confirmOverwrite;
    private string? extraPlace;
    private List<Entry> entries = new();
    private int selected = -1;
    private bool scrollToTop;
    private Action<string>? onChosen;

    /// <summary>
    /// Shows the browser. <paramref name="onChosen"/> gets the full path of the file picked. It opens in
    /// <paramref name="openIn"/> when that folder exists, else in <paramref name="startDirectory"/> (the model's folder,
    /// also listed under Places).
    /// </summary>
    public void Open(bool save, string title, string filter, string? startDirectory, string? fileName, Action<string> onChosen, string? openIn = null)
    {
        this.save = save;
        this.title = title;
        this.onChosen = onChosen;
        filters = ParseFilter(filter);
        filterIndex = 0;
        nameInput = fileName ?? "";
        error = null;
        confirmOverwrite = null;
        extraPlace = startDirectory;
        string start = new[] { openIn, startDirectory, RecentFolders.FirstOrDefault(), Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) }
            .FirstOrDefault(d => !string.IsNullOrEmpty(d) && Directory.Exists(d)) ?? Path.GetPathRoot(Environment.SystemDirectory)!;
        Navigate(start);
        pendingOpen = true;
    }

    /// <summary>"Label|*.a;*.b|Label|*.*" pairs, as the Windows dialogs take them.</summary>
    private static List<(string, string[])> ParseFilter(string filter)
    {
        var parts = filter.Split('|');
        var result = new List<(string, string[])>();
        for (int i = 0; i + 1 < parts.Length; i += 2)
        {
            var extensions = parts[i + 1].Split(';', StringSplitOptions.RemoveEmptyEntries)
                .Select(p => p.Trim().TrimStart('*').ToLowerInvariant()).ToArray();
            result.Add(($"{parts[i]} ({parts[i + 1]})", extensions));
        }
        if (result.Count == 0) result.Add(("All files (*.*)", new[] { ".*" }));
        return result;
    }

    private bool Matches(string path)
    {
        var extensions = filters[filterIndex].Extensions;
        return extensions.Contains(".*") || extensions.Contains(Path.GetExtension(path).ToLowerInvariant());
    }

    private void Navigate(string path)
    {
        try
        {
            var info = new DirectoryInfo(path);
            var list = new List<Entry>();
            foreach (var d in info.EnumerateDirectories())
            {
                if ((d.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                list.Add(new Entry(d.Name, d.FullName, true, 0, d.LastWriteTime));
            }
            foreach (var f in info.EnumerateFiles())
            {
                if ((f.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                list.Add(new Entry(f.Name, f.FullName, false, f.Length, f.LastWriteTime));
            }
            entries = list.OrderBy(e => !e.IsFolder).ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase).ToList();
            directory = info.FullName;
            pathInput = directory;
            selected = -1;
            error = null;
            scrollToTop = true;
        }
        catch (Exception e)
        {
            error = $"Can't open {path}: {e.Message}";
            pathInput = directory;
        }
    }

    private void Remember(string folder)
    {
        RecentFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        RecentFolders.Insert(0, folder);
        if (RecentFolders.Count > MaxRecent) RecentFolders.RemoveRange(MaxRecent, RecentFolders.Count - MaxRecent);
    }

    /// <summary>Takes the typed name (or path) as the answer: navigates into folders, checks files, then closes.</summary>
    private void Confirm()
    {
        string name = nameInput.Trim().Trim('"');
        if (name.Length == 0) return;
        string path = Path.IsPathRooted(name) ? name : Path.Combine(directory, name);
        if (Directory.Exists(path))
        {
            Navigate(path);
            if (!save) nameInput = "";
            return;
        }
        if (save)
        {
            var extensions = filters[filterIndex].Extensions;
            if (Path.GetExtension(path).Length == 0 && extensions.Length > 0 && extensions[0] != ".*") path += extensions[0];
            if (!Directory.Exists(Path.GetDirectoryName(path)))
            {
                error = $"No folder {Path.GetDirectoryName(path)}";
                return;
            }
            if (File.Exists(path) && confirmOverwrite != path)
            {
                confirmOverwrite = path;
                return;
            }
        }
        else if (!File.Exists(path))
        {
            error = $"No file {path}";
            return;
        }
        Finish(path);
    }

    private void Finish(string path)
    {
        Remember(Path.GetDirectoryName(path)!);
        var then = onChosen;
        open = false;
        onChosen = null;
        ImGui.CloseCurrentPopup();
        then?.Invoke(path);
    }

    private void Cancel()
    {
        open = false;
        onChosen = null;
        ImGui.CloseCurrentPopup();
    }

    /// <summary>Draws the browser while it's open. Call once per frame inside the ImGui frame.</summary>
    public void Draw(Vector2 screen)
    {
        if (pendingOpen)
        {
            ImGui.OpenPopup(title + PopupId);
            pendingOpen = false;
            open = true;
        }
        if (!open) return;
        var size = new Vector2(MathF.Min(900, screen.X - 40), MathF.Min(560, screen.Y - 40));
        ImGui.SetNextWindowSize(size, ImGuiCond.Appearing);
        ImGui.SetNextWindowPos(screen * 0.5f, ImGuiCond.Appearing, new Vector2(0.5f));
        bool stillOpen = true;
        if (!ImGui.BeginPopupModal(title + PopupId, ref stillOpen, ImGuiWindowFlags.NoSavedSettings))
        {
            open = false;
            return;
        }
        if (!stillOpen)
        {
            Cancel();
            ImGui.EndPopup();
            return;
        }

        // Path bar.
        if (ImGui.Button("Up") || (ImGui.IsKeyPressed(ImGuiKey.Backspace) && !ImGui.GetIO().WantTextInput))
            if (Directory.GetParent(directory) is { } parent) Navigate(parent.FullName);
        ImGui.SameLine();
        if (ImGui.Button("Refresh")) Navigate(directory);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.InputText("##path", ref pathInput, 1024, ImGuiInputTextFlags.EnterReturnsTrue))
        {
            string typed = pathInput.Trim().Trim('"');
            if (File.Exists(typed))
            {
                nameInput = typed;
                Confirm();
            }
            else Navigate(typed);
        }

        float footer = ImGui.GetFrameHeightWithSpacing() * 2 + (error != null || confirmOverwrite != null ? ImGui.GetTextLineHeightWithSpacing() : 0) + 6;
        float body = ImGui.GetContentRegionAvail().Y - footer;

        // Places.
        ImGui.BeginChild("##places", new Vector2(190, body), ImGuiChildFlags.Borders);
        void Place(string label, string? path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            if (ImGui.Selectable($"{label}##{path}", string.Equals(path.TrimEnd('\\'), directory.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) Navigate(path);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(path);
        }
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (extraPlace != null) Place($"This model's folder", extraPlace);
        Place("Desktop", Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        Place("Documents", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments));
        Place("Downloads", Path.Combine(home, "Downloads"));
        ImGui.Separator();
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try { ready = drive.IsReady; } catch { ready = false; }
            if (ready) Place(drive.Name.TrimEnd('\\'), drive.RootDirectory.FullName);
        }
        if (RecentFolders.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextDisabled("Recent");
            foreach (string folder in RecentFolders.ToList()) Place(Path.GetFileName(folder.TrimEnd('\\')) is { Length: > 0 } n ? n : folder, folder);
        }
        ImGui.EndChild();
        ImGui.SameLine();

        // Files.
        ImGui.BeginChild("##files", new Vector2(0, body), ImGuiChildFlags.Borders);
        if (scrollToTop)
        {
            ImGui.SetScrollY(0);
            scrollToTop = false;
        }
        if (ImGui.BeginTable("##list", 3, ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthStretch, 1f);
            ImGui.TableSetupColumn("Size", ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn("Modified", ImGuiTableColumnFlags.WidthFixed, 130);
            ImGui.TableHeadersRow();
            var folderColour = new Vector4(0.95f, 0.8f, 0.45f, 1f);
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                if (!entry.IsFolder && !Matches(entry.Path)) continue;
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (entry.IsFolder) ImGui.PushStyleColor(ImGuiCol.Text, folderColour);
                bool clicked = ImGui.Selectable($"{entry.Name}{(entry.IsFolder ? "\\" : "")}##e{i}", selected == i,
                    ImGuiSelectableFlags.SpanAllColumns | ImGuiSelectableFlags.AllowDoubleClick);
                if (entry.IsFolder) ImGui.PopStyleColor();
                if (clicked)
                {
                    selected = i;
                    confirmOverwrite = null;
                    if (!entry.IsFolder) nameInput = entry.Name;
                    if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                    {
                        if (entry.IsFolder)
                        {
                            Navigate(entry.Path);
                            break;
                        }
                        Confirm();
                    }
                }
                ImGui.TableNextColumn();
                if (!entry.IsFolder) ImGui.TextDisabled(FormatSize(entry.Size));
                ImGui.TableNextColumn();
                ImGui.TextDisabled(entry.Modified.ToString("yyyy-MM-dd HH:mm"));
            }
            ImGui.EndTable();
        }
        if (!open)
        {
            ImGui.EndChild();
            ImGui.EndPopup();
            return;
        }
        ImGui.EndChild();

        // Name, filter, buttons.
        if (error != null) ImGui.TextColored(new Vector4(1f, 0.5f, 0.45f, 1f), error);
        else if (confirmOverwrite != null) ImGui.TextColored(new Vector4(1f, 0.8f, 0.4f, 1f), $"{Path.GetFileName(confirmOverwrite)} exists. {(save ? "Save" : "Open")} again to replace it.");
        ImGui.SetNextItemWidth(-260);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        if (ImGui.InputText("##name", ref nameInput, 1024, ImGuiInputTextFlags.EnterReturnsTrue)) Confirm();
        ImGui.SameLine();
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##filter", filters[filterIndex].Label))
        {
            for (int i = 0; i < filters.Count; i++)
                if (ImGui.Selectable(filters[i].Label, i == filterIndex)) filterIndex = i;
            ImGui.EndCombo();
        }
        float right = ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X;
        ImGui.TextDisabled(save ? "Name the file, or pick one to replace." : "Double-click a file, or type a name or a full path.");
        ImGui.SameLine(right - 210);
        if (ImGui.Button(confirmOverwrite != null ? "Replace" : save ? "Save" : "Open", new Vector2(100, 0)) && open) Confirm();
        ImGui.SameLine();
        if ((ImGui.Button("Cancel", new Vector2(100, 0)) || ImGui.IsKeyPressed(ImGuiKey.Escape)) && open) Cancel();
        ImGui.EndPopup();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / (1024.0 * 1024):0.#} MB",
    };
}
