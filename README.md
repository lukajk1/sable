# Sable

A local tool for painting pixel textures on 3D models, both on the model and on its UV layout.

It opens a model and shows it in two views, 3D and the UV layout over the texture, and paints the selected object in either one.

## Run

```bash
cd src/Sable
dotnet run -- path/to/model.fbx
```

Open models by dropping them on the window, with File > Open (Ctrl+O), or on the command line. It reads FBX, glTF/GLB, OBJ, DAE, 3DS and PLY through Assimp, and `.blend` files by running Blender in the background to export a temporary GLB, the same way Unity imports them. It uses the newest install under `Program Files/Blender Foundation`; set `SABLE_BLENDER` to use a different `blender.exe`.

`--screenshot out.png` renders a few frames, saves the window and exits. `--select name` makes the first object whose name contains `name` active. `--selftest` also paints a few test strokes (in memory; nothing is saved) before the screenshot.

## Controls

3D view (Blender's):

| Input | Action |
| --- | --- |
| Middle drag, or left drag from empty space | Orbit (around the selection, if there is one) |
| Shift + middle (or empty-space left) drag | Pan |
| Wheel, or Ctrl + middle drag | Zoom |
| 1 / 3 / 7 (numpad or number row) | Front / right / top (Ctrl: back / left / bottom) |
| 2 / 4 / 6 / 8 | Orbit in 15° steps |
| 9 | Flip to the other side |
| 5 | Perspective / orthographic |
| Numpad . or F | Frame the selection |
| Home | Frame everything |
| / (or numpad /) | Local view: only the active object, framed; again to go back |
| Shift+Z | Wireframe overlay (off by default). The selected object always gets an orange outline around its visible silhouette, and the selected submesh keeps its edges in Submesh mode |

UV view: middle drag pans, the wheel zooms around the cursor, and Home fits the texture. The status bar shows the texel under the cursor.

Selection:

| Input | Action |
| --- | --- |
| Tab | Object mode / Submesh mode (a submesh is a loose connected piece of an object; nothing edits geometry) |
| Left click (Select tool) | Select an object, or a submesh in Submesh mode (also in the UV view) |
| H | Hide the selected object / submesh |
| Shift+H | Hide everything else (other objects / the object's other submeshes) |
| Alt+H | Reveal hidden objects / the active object's hidden submeshes |
| Esc | Deselect |

Painting goes to the active object only, in either view:

| Input | Action |
| --- | --- |
| V | Select tool |
| N | Pencil: exactly one texel, fully opaque |
| B | Brush: round and soft, sized in texels, with Hardness (a solid core out to this share of the radius, then a thin feathered edge), Opacity and Flow. Each texel takes the share of it the brush covers, so even a 1-2 texel brush blends partial colour |
| I | Eyedropper: the unlit texel colour (the material colour where there's no texture) |
| Hold Alt | Eyedropper from any tool: hover a colour and let go of Alt to take it (no click). Its cursor shows a loupe of the texels around the one under it, with the sampled and current colours side by side |
| G | Fill bucket: fills the connected texels of similar colour (Tolerance, Contiguous) from the one clicked, in either view, inside the selection if there is one; Shift+click fills the whole selection |
| Z | Scrubby zoom: drag right to zoom in, left to zoom out, about where the drag started (in 3D, the surface under it); a click zooms in a step |
| M | Box select (UV view): like the lasso, snapped to whole texels |
| X | Lasso (UV view): drag to select texels, Shift adds, Ctrl subtracts; drag inside the selection to move those texels (leaves them transparent), Ctrl+drag to move a copy; click outside, Ctrl+D or Esc to deselect. Painting stays inside the selection |
| W / Q | Brush bigger / smaller |
| D | Colour picker (hue ring around a saturation/value square) at the cursor; D or Esc closes it |
| Ctrl+Z, Ctrl+Shift+Z / Ctrl+Y | Undo, redo (per stroke) |
| Ctrl+S | Save changed textures |

Opacity and Flow work as in Photoshop and Krita. Flow is how much each dab adds, so with low flow going over a spot again within a stroke deepens it; Opacity is the ceiling one stroke can reach. Both sliders are on a log scale, so most of their travel is in the low range.

With a Wacom (or any Windows Ink pen), pressure drives flow, and optionally size, through the pressure curve: above 1 spends more of the pen's range on light pressure, for washes, and full pressure still reaches full. The pencil ignores pressure and is always opaque. The Wacom driver's "Use Windows Ink" setting has to be on; the panel shows a live pressure bar once the pen is seen. While the pen is in use, Sable takes its position and touch straight from the pen's reports (every sample, including the ones between frames) instead of the mouse messages Windows makes from them, which only start after a tap/drag threshold and so swallow small movements.

Over the views, each tool has its own cursor: a pencil (its body in the paint colour), a crosshair with a small brush for the brush (the view draws its size around it), the pipette and loupe for the eyedropper, and a lasso that turns into a move cross over the selection. Select keeps the normal arrow.

The 3D view's **Texture view** (panel or View menu) chooses how textures are sampled: Pixel (nearest, for pixel art), Smooth (bilinear), or Smooth + mipmaps (trilinear with 16x anisotropic, the usual game setting for high-res textures; mipmaps stay current while painting). The UV view always shows exact texels. `--texview 0|1|2` picks one at launch.

If a frame takes over 40 ms, the status bar says so for a few seconds and a line goes to `%TEMP%\Sable\hitches.log`: which part of the frame was slow (tools, texture upload, 3D, UV, UI, present), how many dabs and ray casts it did, and whether the garbage collector ran.

The tools that only work in the UV view (lasso, box select) sit in a toolbar on the UV view itself; the panel holds the ones that work in both.

On the model the brush paints every texel whose point on the surface falls inside the brush, so strokes carry across UV seams; it only paints faces turned the same way as the one under the cursor. Objects with UVs but no texture get a **New texture** button in the panel, filled with the material colour.

Saving writes each changed texture back to its file. For a `.blend`, that is the image file the .blend itself uses (found by asking Blender), so Unity reimports it. Textures without a file (embedded or new) go next to the model as `<model>_<texture>.png`, and need hooking up to the material in Blender.

Settings are kept between sessions in `%APPDATA%\Sable\settings.json`: colour, brush size, hardness, opacity, flow, the pressure options, texture view, lighting, grid, wireframe, the UV view toggles, the split between the views, the new-texture size and the window. The scene isn't (camera, selection, hidden objects), and the tool always starts as Select. `--frames N` quits after N frames, saving settings as a normal close does; `SABLE_SETTINGS` points at a different settings file.

Ctrl+R reloads the model from disk.

## Layout

- `Model/`: loading, as plain CPU data (`ModelLoader`, `BlendConverter`, `LoadedModel`), submeshes (`Topology`) and ray casts (`Raycast`).
- `Paint/`: textures on the CPU (`PaintTexture`), strokes and undo (`Stroke`), and the pencil and brush (`Brush`).
- `Rendering/`: the raylib side (`GpuModel`, `LitShader`, `OutlineRenderer` for the selection outline).
- `Views/`: the 3D view with its camera, and the UV view.
- `UI/`: the colour wheel.
- `Input/`: pen pressure from Windows Ink (`PenInput`).
- `Diagnostics/`: the frame profiler behind the hitch log.
- `App.cs`: the window, tools, selection and panels; `EditorState.cs`: what's selected and shown.

Built with .NET 9, [Raylib-cs](https://github.com/ChrisDill/Raylib-cs), [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) via [rlImGui-cs](https://github.com/raylib-extras/rlImGui-cs), and [AssimpNet](https://bitbucket.org/Starnick/assimpnet).
