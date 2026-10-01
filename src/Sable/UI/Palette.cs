using System.Globalization;
using System.Numerics;
using System.Text;
using ImGuiNET;

namespace Sable.UI;

/// <summary>
/// The colour palette and recent colours in the panel. A palette is one of the built-ins (read-only) or Custom, the
/// user's own, which "+", right-click and loading a file change. Colours are RGB in 0..1 and count as the same when
/// they match at 8 bits per channel. Palette files: Lospec .hex, GIMP .gpl, JASC .pal and Paint.NET .txt to load;
/// .hex and .gpl to save.
/// </summary>
internal sealed class Palette
{
    public const string FileFilter = "Palettes|*.hex;*.gpl;*.pal;*.txt";
    public const string SaveFilter = "Lospec HEX|*.hex|GIMP palette|*.gpl";
    public const string CustomName = "Custom";
    public const int MaxHistory = 16;
    private const int MaxColours = 4096;
    private const float SwatchSize = 18f, Spacing = 2f;
    private const int MaxVisibleRows = 8;
    private const string ConfirmId = "Palette##paletteconfirm";

    private static readonly (string Name, Vector3[] Colours)[] BuiltIns =
    {
        ("PICO-8", FromHex("000000", "1D2B53", "7E2553", "008751", "AB5236", "5F574F", "C2C3C7", "FFF1E8",
                           "FF004D", "FFA300", "FFEC27", "00E436", "29ADFF", "83769C", "FF77A8", "FFCCAA")),
        ("Grayscale 8", Enumerable.Range(0, 8).Select(i => new Vector3(MathF.Round(i * 255f / 7f) / 255f)).ToArray()),
    };

    /// <summary>The palettes the combo offers: the built-ins, then Custom.</summary>
    public static IReadOnlyList<string> Names { get; } = BuiltIns.Select(b => b.Name).Append(CustomName).ToArray();

    /// <summary>The palette shown: a built-in's name or Custom (or, for one from <see cref="Load"/>, the file's).</summary>
    public string Name { get; private set; } = BuiltIns[0].Name;
    /// <summary>The colours shown. For Custom this is the Custom list itself; for a built-in, a copy.</summary>
    public List<Vector3> Swatches { get; private set; } = BuiltIns[0].Colours.ToList();
    /// <summary>Recently painted colours, newest first, at most <see cref="MaxHistory"/>, no two the same.</summary>
    public List<Vector3> History { get; } = new();
    public bool IsCustom => Name == CustomName;

    private List<Vector3> custom = new();
    private string? status;
    private bool statusIsError;
    private Pending? pending;
    private bool openConfirm;

    /// <summary>A change to Custom that would lose its colours, waiting for the user to choose.</summary>
    private sealed record Pending(string Question, string ReplaceLabel, List<Vector3> Replacement, List<Vector3> Addition);

    /// <summary>Shows a built-in or Custom. Unknown names are ignored.</summary>
    public void Select(string name)
    {
        if (name == CustomName)
        {
            Name = CustomName;
            Swatches = custom;
            return;
        }
        foreach (var (builtIn, colours) in BuiltIns)
        {
            if (builtIn != name) continue;
            Name = builtIn;
            Swatches = colours.ToList();
            return;
        }
    }

    /// <summary>Puts a colour at the front of the history, moving it there if it's already in it.</summary>
    public void Remember(Vector3 rgb)
    {
        rgb = Quantize(rgb);
        int key = Key(rgb);
        History.RemoveAll(c => Key(c) == key);
        History.Insert(0, rgb);
        if (History.Count > MaxHistory) History.RemoveRange(MaxHistory, History.Count - MaxHistory);
    }

    /// <summary>Replaces Custom's colours and shows Custom.</summary>
    public void SetCustom(IEnumerable<Vector3> colours)
    {
        var list = colours.Select(Quantize).ToList();
        custom.Clear();
        custom.AddRange(list);
        Select(CustomName);
    }

    /// <summary>Loads a palette file into Custom (replacing its colours) and shows Custom.</summary>
    public bool LoadIntoCustom(string path, out string? error)
    {
        var loaded = Load(path, out error);
        if (loaded == null) return false;
        SetCustom(loaded.Swatches);
        return true;
    }

    /// <summary>
    /// Reads a palette file: .hex (RRGGBB lines), .gpl (GIMP), .pal (JASC-PAL) or .txt (Paint.NET, AARRGGBB lines).
    /// The format is recognised by its header first, then by the extension. The palette's name is the GIMP Name
    /// line, or else the file name.
    /// </summary>
    public static Palette? Load(string path, out string? error)
    {
        string text;
        try
        {
            using (var stream = File.OpenRead(path))
            {
                var magic = new byte[4];
                if (stream.Read(magic, 0, 4) == 4 && Encoding.ASCII.GetString(magic) == "RIFF")
                {
                    error = "This is a Microsoft RIFF palette; only JASC-PAL .pal files can be read.";
                    return null;
                }
            }
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return null;
        }

        var lines = text.ReplaceLineEndings("\n").Split('\n');
        string? first = lines.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var colours = new List<Vector3>();
        string? name = null;
        if (first != null && first.StartsWith("GIMP Palette", StringComparison.OrdinalIgnoreCase))
            error = ParseGpl(lines, colours, ref name);
        else if (first != null && first.Equals("JASC-PAL", StringComparison.OrdinalIgnoreCase))
            error = ParseJasc(lines, colours);
        else if (ext == ".gpl")
            error = "Not a GIMP palette: the first line should be \"GIMP Palette\".";
        else if (ext == ".pal")
            error = "Not a JASC palette: the first line should be \"JASC-PAL\".";
        else
            error = ParseHexLines(lines, colours);

        if (error == null && colours.Count == 0) error = "No colours in the file.";
        if (error == null && colours.Count > MaxColours) error = $"{colours.Count} colours; Sable takes at most {MaxColours}.";
        if (error != null) return null;
        return new Palette
        {
            Name = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(path) : name.Trim(),
            Swatches = colours,
        };
    }

    /// <summary>Writes the shown colours as .gpl (by the extension) or .hex.</summary>
    public bool Save(string path, out string? error)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        var text = new StringBuilder();
        if (ext == ".gpl")
        {
            string name = IsCustom ? Path.GetFileNameWithoutExtension(path) : Name;
            text.Append("GIMP Palette\n").Append($"Name: {name}\n").Append($"Columns: {Math.Min(Swatches.Count, 16)}\n#\n");
            foreach (var c in Swatches)
            {
                var (r, g, b) = Bytes(c);
                text.Append($"{r,3} {g,3} {b,3}\t{ToHex(c)[1..]}\n");
            }
        }
        else if (ext == ".hex")
        {
            foreach (var c in Swatches) text.Append(ToHex(c)[1..].ToLowerInvariant()).Append('\n');
        }
        else
        {
            error = "Palettes save as .hex or .gpl.";
            return false;
        }
        try
        {
            File.WriteAllText(path, text.ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            error = e.Message;
            return false;
        }
        error = null;
        return true;
    }

    public void LoadFrom(Settings s)
    {
        custom = ParseHexList(s.PaletteCustom);
        History.Clear();
        foreach (var c in ParseHexList(s.ColorHistory).Take(MaxHistory))
            if (History.All(h => Key(h) != Key(c))) History.Add(c);
        Select(s.PaletteName != null && Names.Contains(s.PaletteName) ? s.PaletteName : Name);
    }

    public void SaveTo(Settings s)
    {
        s.PaletteName = Name;
        s.PaletteCustom = custom.Select(ToHex).ToList();
        s.ColorHistory = History.Select(ToHex).ToList();
    }

    /// <summary>
    /// The collapsible Palette section: the palette combo, + / Load / Save, the swatches and the recent colours.
    /// <paramref name="openFile"/> and <paramref name="saveFile"/> show a file browser and call the continuation
    /// with the chosen path (use <see cref="FileFilter"/> and <see cref="SaveFilter"/>).
    /// </summary>
    public void Draw(Vector3 currentRgb, Action<Vector3> pick, Action<Action<string>> openFile, Action<Action<string>> saveFile)
    {
        ImGui.PushID("##palette");
        if (ImGui.CollapsingHeader("Palette", ImGuiTreeNodeFlags.DefaultOpen))
        {
            int currentKey = Key(currentRgb);
            var style = ImGui.GetStyle();
            float ButtonWidth(string label) => ImGui.CalcTextSize(label).X + style.FramePadding.X * 2f;
            float buttons = ButtonWidth("+") + ButtonWidth("Load") + ButtonWidth("Save") + style.ItemSpacing.X * 3f;

            ImGui.SetNextItemWidth(MathF.Max(80f, ImGui.GetContentRegionAvail().X - buttons));
            if (ImGui.BeginCombo("##name", $"{Name}  ({Swatches.Count})"))
            {
                foreach (string name in Names)
                    if (ImGui.Selectable(name, name == Name)) { Select(name); status = null; }
                ImGui.EndCombo();
            }
            ImGui.SameLine();
            if (ImGui.Button("+")) Add(currentRgb);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(IsCustom ? "Add the paint colour to Custom." : $"Add the paint colour to Custom, starting from a copy of {Name}.");
            ImGui.SameLine();
            if (ImGui.Button("Load")) openFile(OnFileChosen);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Load a palette file into Custom: .hex (Lospec), .gpl (GIMP), .pal (JASC) or .txt (Paint.NET).");
            ImGui.SameLine();
            if (ImGui.Button("Save")) saveFile(OnSaveChosen);
            if (ImGui.IsItemHovered()) ImGui.SetTooltip("Save these colours as .hex or .gpl.");

            if (Swatches.Count == 0)
                ImGui.TextDisabled(IsCustom ? "Empty. + adds the paint colour." : "Empty.");
            else
                DrawPaletteGrid(currentKey, pick);

            if (status != null)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, statusIsError ? new Vector4(1f, 0.55f, 0.45f, 1f) : style.Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextWrapped(status);
                ImGui.PopStyleColor();
            }

            ImGui.TextDisabled("Recent");
            if (History.Count == 0)
            {
                ImGui.SameLine();
                ImGui.TextDisabled("(colours you paint with)");
            }
            else
            {
                float avail = ImGui.GetContentRegionAvail().X;
                int columns = SizeFor(avail, MaxHistory) >= 12f ? MaxHistory : Columns(avail, SwatchSize);
                DrawSwatches(History, columns, SizeFor(avail, columns), currentKey, pick, false, out _);
            }
        }
        DrawConfirm();
        ImGui.PopID();
    }

    private void DrawPaletteGrid(int currentKey, Action<Vector3> pick)
    {
        float avail = ImGui.GetContentRegionAvail().X;
        int columns = Columns(avail, SwatchSize);
        float size = SizeFor(avail, columns);
        int rows = (Swatches.Count + columns - 1) / columns;
        int removed = -1;
        if (rows <= MaxVisibleRows)
        {
            // Even rows: 16 colours as two rows of 8 rather than 14 and 2.
            DrawSwatches(Swatches, (Swatches.Count + rows - 1) / rows, size, currentKey, pick, IsCustom, out removed);
        }
        else
        {
            float inner = avail - ImGui.GetStyle().ScrollbarSize;
            columns = Columns(inner, SwatchSize);
            size = SizeFor(inner, columns);
            ImGui.BeginChild("##grid", new Vector2(avail, MaxVisibleRows * (size + Spacing) - Spacing));
            DrawSwatches(Swatches, columns, size, currentKey, pick, IsCustom, out removed);
            ImGui.EndChild();
        }
        if (removed >= 0)
        {
            custom.RemoveAt(removed);
            status = null;
        }
    }

    /// <summary>
    /// A grid of square swatch buttons, <paramref name="size"/> pixels each. Left click picks; right click reports the
    /// swatch in <paramref name="removed"/> when <paramref name="removable"/>.
    /// </summary>
    private static void DrawSwatches(List<Vector3> colours, int columns, float size, int currentKey, Action<Vector3> pick, bool removable, out int removed)
    {
        removed = -1;
        var drawList = ImGui.GetWindowDrawList();
        uint border = ImGui.GetColorU32(ImGuiCol.Border);
        uint white = ImGui.ColorConvertFloat4ToU32(Vector4.One), black = ImGui.ColorConvertFloat4ToU32(new Vector4(0, 0, 0, 1));
        uint hover = ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, 0.6f));

        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(Spacing));
        for (int i = 0; i < colours.Count; i++)
        {
            if (i % columns != 0) ImGui.SameLine();
            ImGui.PushID(i);
            var c = colours[i];
            Vector2 min = ImGui.GetCursorScreenPos(), max = min + new Vector2(size);
            if (ImGui.InvisibleButton("##swatch", new Vector2(size))) pick(c);
            bool hovered = ImGui.IsItemHovered();
            if (removable && ImGui.IsItemClicked(ImGuiMouseButton.Right)) removed = i;

            uint fill = ImGui.ColorConvertFloat4ToU32(new Vector4(c, 1f));
            if (Key(c) == currentKey)
            {
                drawList.AddRectFilled(min, max, white);
                drawList.AddRectFilled(min + new Vector2(2f), max - new Vector2(2f), black);
                drawList.AddRectFilled(min + new Vector2(3f), max - new Vector2(3f), fill);
            }
            else
            {
                drawList.AddRectFilled(min, max, fill);
                drawList.AddRect(min, max, hovered ? hover : border);
            }
            if (hovered) ImGui.SetTooltip(removable ? $"{ToHex(c)}\nRight-click: remove" : ToHex(c));
            ImGui.PopID();
        }
        ImGui.PopStyleVar();
    }

    private static int Columns(float width, float target) => Math.Max(1, (int)((width + Spacing) / (target + Spacing)));
    private static float SizeFor(float width, int columns) => MathF.Max(4f, MathF.Floor((width - Spacing * (columns - 1)) / columns));

    private void Add(Vector3 rgb)
    {
        rgb = Quantize(rgb);
        if (IsCustom)
        {
            if (custom.Any(c => Key(c) == Key(rgb))) SetStatus($"{ToHex(rgb)} is already in Custom.", false);
            else { custom.Add(rgb); status = null; }
            return;
        }
        var copy = Swatches.ToList();
        if (copy.All(c => Key(c) != Key(rgb))) copy.Add(rgb);
        string from = Name;
        if (custom.Count == 0 || SameColours(custom, Swatches))
        {
            SetCustom(copy);
            SetStatus($"Custom is now a copy of {from} with {ToHex(rgb)}.", false);
            return;
        }
        Confirm(new Pending(
            $"Custom has {custom.Count} colours of its own. Replace them with a copy of {from} and {ToHex(rgb)}, or add {ToHex(rgb)} to Custom as it is?",
            $"Copy {from}", copy, new List<Vector3> { rgb }));
    }

    private void OnFileChosen(string path)
    {
        var loaded = Load(path, out string? error);
        if (loaded == null)
        {
            SetStatus($"Couldn't load {Path.GetFileName(path)}: {error}", true);
            return;
        }
        var colours = loaded.Swatches.Select(Quantize).ToList();
        if (custom.Count == 0 || SameColours(custom, colours))
        {
            SetCustom(colours);
            SetStatus($"Loaded {colours.Count} colours from {Path.GetFileName(path)} into Custom.", false);
            return;
        }
        Confirm(new Pending(
            $"Replace the {custom.Count} colours in Custom with the {colours.Count} from {Path.GetFileName(path)}, or add them to the end?",
            "Replace", colours, colours));
    }

    private void OnSaveChosen(string path)
    {
        if (Save(path, out string? error)) SetStatus($"Saved {Swatches.Count} colours to {Path.GetFileName(path)}.", false);
        else SetStatus($"Couldn't save {Path.GetFileName(path)}: {error}", true);
    }

    private void Confirm(Pending p)
    {
        pending = p;
        openConfirm = true;
    }

    private void DrawConfirm()
    {
        if (openConfirm)
        {
            ImGui.OpenPopup(ConfirmId);
            openConfirm = false;
        }
        if (!ImGui.BeginPopupModal(ConfirmId, ImGuiWindowFlags.AlwaysAutoResize)) return;
        if (pending == null)
        {
            ImGui.CloseCurrentPopup();
            ImGui.EndPopup();
            return;
        }
        ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
        ImGui.TextWrapped(pending.Question);
        ImGui.PopTextWrapPos();
        ImGui.Spacing();
        bool done = false;
        if (ImGui.Button(pending.ReplaceLabel))
        {
            SetCustom(pending.Replacement);
            status = null;
            done = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Add to Custom"))
        {
            var merged = custom.ToList();
            foreach (var c in pending.Addition)
                if (merged.All(m => Key(m) != Key(c))) merged.Add(c);
            SetCustom(merged);
            status = null;
            done = true;
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel") || ImGui.IsKeyPressed(ImGuiKey.Escape)) done = true;
        if (done)
        {
            pending = null;
            ImGui.CloseCurrentPopup();
        }
        ImGui.EndPopup();
    }

    private void SetStatus(string text, bool error)
    {
        status = text;
        statusIsError = error;
    }

    /// <summary>"#RRGGBB".</summary>
    public static string ToHex(Vector3 rgb)
    {
        var (r, g, b) = Bytes(rgb);
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    /// <summary>RRGGBB with or without "#" (or "0x"); AARRGGBB too, ignoring the alpha.</summary>
    public static bool TryParseHex(string text, out Vector3 rgb)
    {
        rgb = default;
        string s = text.Trim();
        if (s.StartsWith('#')) s = s[1..];
        else if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if ((s.Length != 6 && s.Length != 8) || !uint.TryParse(s, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint v))
            return false;
        rgb = new Vector3((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF) / 255f;
        return true;
    }

    private static string? ParseHexLines(string[] lines, List<Vector3> colours)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith("//")) continue;
            if (!TryParseHex(line, out var c)) return $"line {i + 1}: \"{Clip(line)}\" isn't RRGGBB or AARRGGBB.";
            colours.Add(c);
        }
        return null;
    }

    private static string? ParseGpl(string[] lines, List<Vector3> colours, ref string? name)
    {
        bool header = true;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (header)
            {
                if (line.Length > 0) header = false;
                continue;
            }
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("Name:", StringComparison.OrdinalIgnoreCase))
            {
                name = line[5..].Trim();
                continue;
            }
            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (TryParseRgb(parts, out var c)) colours.Add(c);
            else if (!parts[0].EndsWith(':')) return $"line {i + 1}: \"{Clip(line)}\" isn't \"R G B name\".";
        }
        return null;
    }

    private static string? ParseJasc(string[] lines, List<Vector3> colours)
    {
        var rows = lines.Select((text, index) => (Text: text.Trim(), Line: index + 1)).Where(r => r.Text.Length > 0).ToList();
        if (rows.Count < 3) return "The JASC header is incomplete (JASC-PAL, 0100, the colour count).";
        if (!int.TryParse(rows[2].Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count < 0)
            return $"line {rows[2].Line}: \"{Clip(rows[2].Text)}\" isn't the colour count.";
        if (rows.Count - 3 < count) return $"The header says {count} colours, but there are {rows.Count - 3}.";
        for (int i = 3; i < 3 + count; i++)
        {
            if (!TryParseRgb(rows[i].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), out var c))
                return $"line {rows[i].Line}: \"{Clip(rows[i].Text)}\" isn't \"R G B\".";
            colours.Add(c);
        }
        return null;
    }

    private static bool TryParseRgb(string[] parts, out Vector3 rgb)
    {
        rgb = default;
        if (parts.Length < 3) return false;
        Span<int> v = stackalloc int[3];
        for (int k = 0; k < 3; k++)
            if (!int.TryParse(parts[k], NumberStyles.Integer, CultureInfo.InvariantCulture, out v[k]) || v[k] < 0 || v[k] > 255)
                return false;
        rgb = new Vector3(v[0], v[1], v[2]) / 255f;
        return true;
    }

    private static List<Vector3> ParseHexList(List<string>? hex)
    {
        var list = new List<Vector3>();
        if (hex == null) return list;
        foreach (string h in hex)
            if (TryParseHex(h, out var c)) list.Add(c);
        return list;
    }

    private static Vector3[] FromHex(params string[] hex) => hex.Select(h => TryParseHex(h, out var c) ? c : Vector3.Zero).ToArray();

    private static string Clip(string s) => s.Length <= 40 ? s : s[..40] + "...";

    private static (int R, int G, int B) Bytes(Vector3 c) => (Byte(c.X), Byte(c.Y), Byte(c.Z));
    private static int Byte(float v) => (int)MathF.Round(Math.Clamp(v, 0f, 1f) * 255f);
    private static int Key(Vector3 c) => (Byte(c.X) << 16) | (Byte(c.Y) << 8) | Byte(c.Z);
    private static Vector3 Quantize(Vector3 c) => new Vector3(Byte(c.X), Byte(c.Y), Byte(c.Z)) / 255f;

    private static bool SameColours(List<Vector3> a, List<Vector3> b) => a.Count == b.Count && a.Zip(b).All(p => Key(p.First) == Key(p.Second));
}
