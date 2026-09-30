# Pixel Painter

A local tool for painting pixel textures on 3D models, both on the model and on its UV layout.

Right now it opens a model and shows it in two views: 3D, and the UV layout over the texture. Painting comes next.

## Run

```bash
cd src/PixelPainter
dotnet run -- path/to/model.fbx
```

Open models by dropping them on the window, with File > Open (Ctrl+O), or on the command line. It reads FBX, glTF/GLB, OBJ, DAE, 3DS and PLY through Assimp, and `.blend` files by running Blender in the background to export a temporary GLB, the same way Unity imports them. It uses the newest install under `Program Files/Blender Foundation`; set `PIXELPAINTER_BLENDER` to use a different `blender.exe`.

`--screenshot out.png` renders a few frames, saves the window and exits. `--select name` selects the first part whose name contains `name`.

## Controls

3D view (Blender's):

| Input | Action |
| --- | --- |
| Middle drag (or Alt + left drag) | Orbit |
| Shift + middle drag | Pan |
| Wheel, or Ctrl + middle drag | Zoom |
| Numpad 1 / 3 / 7 | Front / right / top (Ctrl: back / left / bottom) |
| Numpad 2 / 4 / 6 / 8 | Orbit in 15° steps |
| Numpad 9 | Flip to the other side |
| Numpad 5 | Perspective / orthographic |
| Numpad . or F | Frame the selection |
| Home | Frame everything |
| Left click | Select a part (Esc to deselect) |
| Z | Wireframe |

UV view: middle drag pans, the wheel zooms around the cursor, and Home fits the texture. The status bar shows the texel under the cursor.

Ctrl+R reloads the model from disk.

## Layout

- `Model/`: loading, as plain CPU data (`ModelLoader`, `BlendConverter`, `LoadedModel`).
- `Rendering/`: the raylib side (`GpuModel`, `LitShader`).
- `Views/`: the 3D view with its camera, and the UV view.
- `App.cs`: the window, the panels and the main loop.

Built with .NET 9, [Raylib-cs](https://github.com/ChrisDill/Raylib-cs), [ImGui.NET](https://github.com/ImGuiNET/ImGui.NET) via [rlImGui-cs](https://github.com/raylib-extras/rlImGui-cs), and [AssimpNet](https://bitbucket.org/Starnick/assimpnet).
