# Sable

A local Windows tool for painting pixel textures on 3D models, both on the model and on its UV layout.

It opens a model and shows it in two views side by side, 3D and the UV layout over the texture, and paints the selected object in either one.

## Run

```bash
cd src/Sable
dotnet run -- path/to/model.fbx
```

Open a model by dropping it on the window, with File > Open (Ctrl+O) or File > Open path, or on the command line. Sable reads FBX, glTF/GLB, OBJ, DAE, 3DS and PLY through Assimp. It opens `.blend` files the way Unity does, by running Blender in the background to export a temporary GLB. It uses the newest install under `Program Files/Blender Foundation`; set `SABLE_BLENDER` to use a different `blender.exe`. Ctrl+R reloads the model from disk.

## The window

- **Panel (left):** tools, colour, brush settings, display options, the object list, the active object's materials and textures, textures and warnings.
- **3D view (middle):** the model, lit or flat.
- **UV view (right):** the active texture with the UV layout over it, and a toolbar for the tools that only work there (lasso, box select, deselect).
- **Bar between the views:** drag it to resize them.
- **Status bar:** messages, the texel under the cursor, and a note when a frame hitched.

## Navigation

3D view (Blender's controls):

| Input | Action |
| --- | --- |
| Middle drag, or left drag from empty space | Orbit, around the selection if there is one |
| Shift + middle drag (or empty-space left drag) | Pan |
| Wheel, or Ctrl + middle drag | Zoom |
| Z, then drag | Scrubby zoom about the point under the cursor (see Tools) |
| 1 / 3 / 7 (numpad or number row) | Front / right / top (Ctrl: back / left / bottom) |
| 2 / 4 / 6 / 8 | Orbit in 15° steps |
| 9 | Flip to the other side |
| 5 | Perspective / orthographic |
| Numpad . or F | Frame the selection |
| Home | Frame everything |
| / (or numpad /) | Local view: only the active object, framed; again to go back |
| Shift+Z | Wireframe overlay (off by default) |

The selected object always has an orange outline around its visible silhouette, whatever the wireframe setting. In Submesh mode the outline is dimmer and the selected piece keeps its edges.

UV view: middle drag pans, the wheel zooms around the cursor, Z drags zoom, and Home fits the texture. A texel grid appears once texels are big enough to see.

## Selecting and hiding

| Input | Action |
| --- | --- |
| Tab | Object mode / Submesh mode. A submesh is a loose connected piece of an object, such as one barrel of a group; nothing edits geometry |
| Left click (Select tool) | Select an object, or a submesh in Submesh mode (also by clicking its UVs in the UV view) |
| H | Hide the selected object / submesh |
| Shift+H | Hide everything else: the other objects, or the object's other submeshes |
| Alt+H | Reveal hidden objects, or the active object's hidden submeshes |
| Esc | Close the colour picker, then clear the texel selection, then deselect |

The checkboxes in the object list hide and show objects too. Hiding and revealing can be undone.

## Tools

Painting only ever goes to the active (selected) object, in either view.

| Key | Tool |
| --- | --- |
| V | Select |
| N | Pencil: exactly one texel, fully opaque |
| B | Brush: round and soft, sized in texels (W bigger, Q smaller), with Hardness, Opacity and Flow |
| I | Eyedropper: the unlit texel colour (the material colour where there's no texture) |
| Hold Alt | Eyedropper from any tool: hover a colour and let go of Alt to take it, no click needed |
| G | Fill bucket: fills the texels of similar colour (Tolerance) connected to the one clicked (Contiguous), in either view. Shift+click fills the whole selection |
| Z | Scrubby zoom: drag right to zoom in, left to zoom out, about where the drag started (in 3D, the surface under it). A click zooms in a step |
| X | Lasso (UV view) |
| M | Box select (UV view): like the lasso, snapped to whole texels |
| D | Colour picker at the cursor: a hue ring around a saturation/value square, new and old swatches, hex. D or Esc closes it |

Other keys: Ctrl+Z undo, Ctrl+Shift+Z or Ctrl+Y redo (each stroke, fill, selection move and hide/reveal is one step), Ctrl+S save, Ctrl+D deselect texels.

Over the views each tool shows its own cursor:
- **Pencil:** a pencil with its body in the paint colour, plus the texel's outline on the model.
- **Brush:** a crosshair with a small brush; the view draws the brush's size around it.
- **Eyedropper:** a pipette whose bulb shows the colour under it, and a loupe of the texels around the sampled one with the sampled and current colours side by side.
- **Fill:** a bucket.
- **Zoom:** a magnifier.
- **Lasso and box select:** a lasso or a box, turning into a move cross over the selection.

### The brush

- **Coverage:** each texel takes the share of it the brush covers, so even a 1-2 texel brush lays down partial colour. The pencil is the tool for solid single texels.
- **Hardness:** a solid core out to that share of the radius, then a thin feathered edge.
- **Opacity and Flow** work as in Photoshop and Krita. Flow is how much each dab adds, so with low flow, going over a spot again within a stroke deepens it. Opacity is the most one stroke can reach. Both sliders are on a log scale, with most of their travel in the low range.
- **On the model:** the brush paints every texel whose point on the surface falls inside it, so strokes carry across UV seams. It only paints faces turned the same way as the one under the cursor.

### Pen pressure

With a Wacom (or any Windows Ink pen), pressure drives flow, and optionally size, through the pressure curve. Above 1, more of the pen's range goes to light pressure for washes, and full pressure still reaches full. The pencil ignores pressure. The panel shows a live pressure bar once the pen is seen.

The Wacom driver's **Use Windows Ink** setting has to be on. Sable reads the pen's position and touch straight from its reports, including the samples between frames, rather than from the mouse messages Windows makes from them. Those only start after a tap/drag threshold, which swallowed small movements.

### Texel selections

The lasso and box select make a selection of texels on the texture the UV view shows:
- **Drawing:** Shift adds and Ctrl subtracts.
- **Moving:** drag inside the selection to move those texels, which leaves the old spot transparent. Ctrl+drag moves a copy instead.
- **Clearing:** click outside the selection, press Ctrl+D or Esc, or use Deselect on the UV toolbar.
- **Painting:** while a selection exists, the pencil, brush and fill stay inside it, in both views.

## Display

These are in the panel and the View menu:
- **Lighting:** from lit to flat, for judging the painted colours as they are.
- **Texture view (3D only):** Pixel (nearest, for pixel art), Smooth (bilinear), or Smooth + mipmaps (trilinear with 16x anisotropic, the usual game setting for high-res textures; the mipmaps stay current while painting). The UV view always shows exact texels.
- **Wireframe and grid.**
- **UV texel grid, and showing other objects on the same texture** (dimmed) in the UV view.

## Textures and files

- **New texture:** objects with UVs but no texture get a **New texture** button in the panel, at a chosen size, filled with the material colour.
- **Ctrl+S** saves each changed texture back to its own file. For a `.blend` that's the image file the .blend itself uses (found by asking Blender), so Unity reimports it. Textures without a file (embedded or new) go next to the model as `<model>_<texture>.png` and need hooking up to the material in Blender.
- **File > Import image into texture:** replaces the active texture's pixels with an image, resizing the texture to it. Ctrl+S still saves to the texture's own file. Importing clears undo.
- **File > Export texture as:** writes a PNG copy without changing where Ctrl+S saves.
- **File > Export UV layout:** writes the UV wireframe the UV view shows, at 1x to 8x the texture size. It's either black lines on transparent or white lines over the texture: a template for painting elsewhere, like Blender's Export UV Layout.

File dialogs run in a separate helper process (`Sable.exe --pick ...`). Shell extensions that inject into them can crash (SHADE Sandbox's `shade.dll` did), and then only the helper dies. If a dialog fails, use File > Open path or drop the file on the window.

## Settings

Settings are kept between sessions in `%APPDATA%\Sable\settings.json`, written when the window closes:
- **Paint:** colour, brush size, hardness, opacity, flow, and the pressure options and curve.
- **Fill:** tolerance and contiguous.
- **Display:** texture view, lighting, grid, wireframe and the UV view toggles.
- **Layout:** the split between the views, the new-texture size, and the window's size, position and maximized state.

The scene (camera, selection, hidden objects) isn't kept, and the tool always starts as Select.

## Troubleshooting

- **The pen doesn't paint, or there's no pressure:** turn on Use Windows Ink in Wacom Tablet Properties.
- **Stutter:** frames over 40 ms show in the status bar for a few seconds and are logged to `%TEMP%\Sable\hitches.log`. The log gives which part of the frame was slow (tools, texture upload, 3D, UV, UI, present), how many dabs and ray casts ran, and whether the garbage collector did.
- **A crash:** Windows logs the faulting module in the Event Viewer (Windows Logs > Application, source Application Error).

## Command line

```
Sable [model] [--select name] [--texview 0|1|2] [--screenshot out.png] [--selftest] [--frames N]
```

| Option | What it does |
| --- | --- |
| `model` | The file to open |
| `--select name` | Make the first object whose name contains `name` active, and frame it |
| `--texview 0\|1\|2` | Start with the Pixel, Smooth or Smooth + mipmaps texture view |
| `--screenshot out.png` | Render a few frames, save the window as a PNG and exit (settings are left alone) |
| `--selftest` | Before the screenshot, run checks: test strokes, fill, box and lasso selection, flow build-up, hide undo, UV export and re-import. They print to the console, and nothing is saved |
| `--frames N` | Quit after N frames, saving settings as a normal close does |

Environment: `SABLE_BLENDER` (the Blender to use), `SABLE_SETTINGS` (a different settings file, for tests).

## Code layout

- `Model/`: loading, as plain CPU data (`ModelLoader`, `BlendConverter`, `LoadedModel`), submeshes (`Topology`) and ray casts (`Raycast`).
- `Paint/`: textures on the CPU (`PaintTexture`), strokes and the undo stack (`Stroke`), the pencil, brush and fill (`Brush`), and texel selections (`TexelSelection`, `SelectionMove`).
- `Rendering/`: the raylib side: `GpuModel`, `LitShader`, `OutlineRenderer` (selection outline), `UvLayoutExport`, and `VisibilityStep` (undoable hiding).
- `Views/`: the 3D view with its camera (`Viewport3D`, `OrbitCamera`) and the UV view (`UvView`).
- `UI/`: the colour wheel.
- `Input/`: pen input from Windows Ink (`PenInput`).
- `Diagnostics/`: the frame profiler behind the hitch log.
- `App.cs`: the window, tools, selection and panels. `EditorState.cs`: what's selected and shown. `Settings.cs`: what persists. `FileDialogs.cs`: the out-of-process dialogs. `Program.cs`: the command line.

Built with .NET 9, [Raylib-cs](https://github.com/ChrisDill/Raylib-cs), [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) via [rlImGui-cs](https://github.com/raylib-extras/rlImGui-cs), and [AssimpNet](https://bitbucket.org/Starnick/assimpnet).
