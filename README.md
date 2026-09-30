# Pixel Painter

A local tool for painting pixel textures on 3D models, both on the model and on its UV layout.

It opens a model and shows it in two views, 3D and the UV layout over the texture, and paints the selected object in either one.

## Run

```bash
cd src/PixelPainter
dotnet run -- path/to/model.fbx
```

Open models by dropping them on the window, with File > Open (Ctrl+O), or on the command line. It reads FBX, glTF/GLB, OBJ, DAE, 3DS and PLY through Assimp, and `.blend` files by running Blender in the background to export a temporary GLB, the same way Unity imports them. It uses the newest install under `Program Files/Blender Foundation`; set `PIXELPAINTER_BLENDER` to use a different `blender.exe`.

`--screenshot out.png` renders a few frames, saves the window and exits. `--select name` makes the first object whose name contains `name` active. `--selftest` also paints a few test strokes (in memory; nothing is saved) before the screenshot.

## Controls

3D view (Blender's):

| Input | Action |
| --- | --- |
| Middle drag | Orbit (around the selection, if there is one) |
| Shift + middle drag | Pan |
| Wheel, or Ctrl + middle drag | Zoom |
| 1 / 3 / 7 (numpad or number row) | Front / right / top (Ctrl: back / left / bottom) |
| 2 / 4 / 6 / 8 | Orbit in 15° steps |
| 9 | Flip to the other side |
| 5 | Perspective / orthographic |
| Numpad . or F | Frame the selection |
| Home | Frame everything |
| / (or numpad /) | Local view: only the active object, framed; again to go back |
| Z | Wireframe |

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
| N | Pencil: exactly one texel |
| B | Brush: round, sized in texels, with a hardness slider for the edge |
| I | Eyedropper: the unlit texel colour (the material colour where there's no texture) |
| Hold Alt, click | Eyedropper from any tool. Its cursor shows a loupe of the texels around the one under it, with the sampled and current colours side by side |
| Q / E | Brush bigger / smaller |
| D | Colour picker (hue ring around a saturation/value square) at the cursor; D or Esc closes it |
| Ctrl+Z, Ctrl+Shift+Z / Ctrl+Y | Undo, redo (per stroke) |
| Ctrl+S | Save changed textures |

On the model the brush paints every texel whose point on the surface falls inside the brush, so strokes carry across UV seams; it only paints faces turned the same way as the one under the cursor. Objects with UVs but no texture get a **New texture** button in the panel, filled with the material colour.

Saving writes each changed texture back to its file. For a `.blend`, that is the image file the .blend itself uses (found by asking Blender), so Unity reimports it. Textures without a file (embedded or new) go next to the model as `<model>_<texture>.png`, and need hooking up to the material in Blender.

Ctrl+R reloads the model from disk.

## Layout

- `Model/`: loading, as plain CPU data (`ModelLoader`, `BlendConverter`, `LoadedModel`), submeshes (`Topology`) and ray casts (`Raycast`).
- `Paint/`: textures on the CPU (`PaintTexture`), strokes and undo (`Stroke`), and the pencil and brush (`Brush`).
- `Rendering/`: the raylib side (`GpuModel`, `LitShader`).
- `Views/`: the 3D view with its camera, and the UV view.
- `UI/`: the colour wheel.
- `App.cs`: the window, tools, selection and panels; `EditorState.cs`: what's selected and shown.

Built with .NET 9, [Raylib-cs](https://github.com/ChrisDill/Raylib-cs), [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) via [rlImGui-cs](https://github.com/raylib-extras/rlImGui-cs), and [AssimpNet](https://bitbucket.org/Starnick/assimpnet).
