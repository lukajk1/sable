# SPDX-License-Identifier: GPL-3.0-or-later
"""Link state, export of the link folder, Sable process management and the live-update handler/timer."""

import json
import os
import subprocess
import sys
import time
import zlib

import bpy
from bpy.app.handlers import persistent

from . import exporter

LINK_VERSION = 1
TIMER_INTERVAL = 0.25
DEBOUNCE = 0.4
IMAGE_POLL_INTERVAL = 1.0
# Material updates this soon after we reloaded an image come from the reload, not from the user.
IMAGE_RELOAD_QUIET = 0.75

SABLE_UNINSTALL_KEY = r"Software\Microsoft\Windows\CurrentVersion\Uninstall\{AF9EFD93-72DA-4AF7-9FE6-65043A096112}_is1"


class LinkState:
    def __init__(self):
        self.reset()
        self.process = None  # The Sable process this add-on launched (survives link resets).
        self.process_link_dir = None
        self.dir_revisions = {}  # link dir -> last revision written, so revisions never go back within a session.

    def reset(self):
        self.active = False
        self.object_uids = []  # session_uid of the linked objects, in link order.
        self.object_names = []  # Their names at the last lookup (fallback if an undo reallocated them).
        self.mesh_uids = set()  # session_uid of the meshes / materials the linked objects use, for the handler.
        self.material_uids = set()
        self.link_dir = None
        self.revision = 0
        self.dirty = False
        self.last_change = 0.0
        self.last_export_time = None  # time.time() of the last export.
        self.last_export_seconds = 0.0
        self.last_error = ""
        self.exported_names = []
        self.images = {}  # material name -> abs image path, as last written to link.json.
        self.image_mtimes = {}  # abs image path -> mtime last seen.
        self.next_image_poll = 0.0
        self.quiet_until = 0.0


state = LinkState()


# -----------------------------------------------------------------------------
# Paths and process


def blend_path():
    return bpy.data.filepath or ""


def compute_link_dir():
    path = blend_path()
    stem = os.path.splitext(os.path.basename(path))[0] if path else "untitled"
    key = "{}|{}".format(os.path.normcase(os.path.abspath(path)) if path else "", os.getpid())
    digest = "{:08x}".format(zlib.crc32(key.encode("utf-8")) & 0xFFFFFFFF)
    temp = os.environ.get("TEMP") or os.environ.get("TMP") or bpy.app.tempdir or os.getcwd()
    return os.path.join(temp, "Sable", "link", "{}-{}".format(stem, digest))


def link_json_path(link_dir=None):
    return os.path.join(link_dir or state.link_dir, "link.json")


def sable_running():
    return state.process is not None and state.process.poll() is None


def _registry_install_location():
    try:
        import winreg
    except ImportError:
        return None
    for hive in (winreg.HKEY_CURRENT_USER, winreg.HKEY_LOCAL_MACHINE):
        for view in (0, winreg.KEY_WOW64_64KEY, winreg.KEY_WOW64_32KEY):
            try:
                with winreg.OpenKey(hive, SABLE_UNINSTALL_KEY, 0, winreg.KEY_READ | view) as key:
                    value, _ = winreg.QueryValueEx(key, "InstallLocation")
            except OSError:
                continue
            if value:
                return str(value)
    return None


def find_sable_exe(pref_path):
    """Sable.exe from the preference, the default per-user install, or the installer's registry entry."""
    if pref_path:
        path = bpy.path.abspath(pref_path)
        if os.path.isfile(path):
            return os.path.abspath(path)
    local = os.environ.get("LOCALAPPDATA")
    if local:
        path = os.path.join(local, "Programs", "Sable", "Sable.exe")
        if os.path.isfile(path):
            return path
    location = _registry_install_location()
    if location:
        path = os.path.join(location, "Sable.exe")
        if os.path.isfile(path):
            return path
    return None


def launch_sable(exe, link_json):
    kwargs = dict(stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                  close_fds=True, cwd=os.path.dirname(exe))
    if sys.platform == "win32":
        kwargs["creationflags"] = subprocess.DETACHED_PROCESS | subprocess.CREATE_NEW_PROCESS_GROUP
    state.process = subprocess.Popen([exe, "--link", os.path.abspath(link_json)], **kwargs)
    state.process_link_dir = os.path.dirname(os.path.abspath(link_json))


# -----------------------------------------------------------------------------
# Linked objects and export


def linked_objects():
    """The linked objects that still exist, in link order (tracked by session_uid, falling back to the name)."""
    if not state.object_uids:
        return []
    by_uid = {}
    by_name = {}
    for obj in bpy.data.objects:
        if obj.type == 'MESH':
            by_uid[obj.session_uid] = obj
            by_name[obj.name] = obj
    result = []
    for i, uid in enumerate(state.object_uids):
        obj = by_uid.get(uid)
        if obj is None:
            obj = by_name.get(state.object_names[i])
            if obj is not None and obj.session_uid in state.object_uids:
                obj = None  # That name now belongs to another linked object.
            if obj is not None:
                state.object_uids[i] = obj.session_uid
        if obj is not None:
            state.object_names[i] = obj.name
            result.append(obj)
    return result


def refresh_dependencies(objects=None):
    """Cache the session_uids of the meshes and materials the linked objects use (read by the handler)."""
    if objects is None:
        objects = linked_objects()
    state.mesh_uids = {obj.data.session_uid for obj in objects if obj.data is not None}
    state.material_uids = {slot.material.session_uid
                           for obj in objects for slot in obj.material_slots if slot.material is not None}


def _write_json_atomic(path, data):
    tmp = path + ".tmp"
    with open(tmp, "w", encoding="utf-8", newline="\n") as f:
        json.dump(data, f, ensure_ascii=False, indent=2)
    os.replace(tmp, path)


def export_link(objects, link_dir, revision, depsgraph=None):
    """Write model.glb then link.json into link_dir. Returns (link dict, triangle count)."""
    os.makedirs(link_dir, exist_ok=True)
    if depsgraph is None:
        depsgraph = bpy.context.evaluated_depsgraph_get()
    materials, triangles = exporter.write_glb(os.path.join(link_dir, "model.glb"), objects, depsgraph)
    images = {}
    for mat in materials:
        if mat is None or mat.name in images:
            continue
        path = exporter.material_image_path(mat)
        if path:
            images[mat.name] = path
    data = {
        "version": LINK_VERSION,
        "revision": revision,
        "blend": os.path.abspath(blend_path()) if blend_path() else "",
        "model": "model.glb",
        "objects": [obj.name for obj in objects],
        "images": images,
    }
    _write_json_atomic(link_json_path(link_dir), data)
    return data, triangles


def export_now():
    """Export the linked set now, updating the state. Returns an error message or None."""
    objects = linked_objects()
    if not objects:
        state.last_error = "None of the linked objects exist any more"
        return state.last_error
    start = time.perf_counter()
    try:
        data, _ = export_link(objects, state.link_dir, state.revision + 1)
    except Exception as ex:  # Keep the timer alive; show the problem in the panel.
        state.last_error = "Export failed: {}".format(ex)
        state.dirty = False  # Retry on the next change rather than every tick.
        print("Sable Link:", state.last_error)
        return state.last_error
    state.revision = data["revision"]
    state.dir_revisions[state.link_dir] = state.revision
    state.last_export_seconds = time.perf_counter() - start
    state.last_export_time = time.time()
    state.last_error = ""
    state.dirty = False
    state.exported_names = list(data["objects"])
    refresh_dependencies(objects)
    _set_images(data["images"])
    return None


def _mtime(path):
    try:
        return os.stat(path).st_mtime_ns
    except OSError:
        return None


def _set_images(images):
    state.images = dict(images)
    old = state.image_mtimes
    state.image_mtimes = {path: old[path] if path in old else _mtime(path) for path in images.values()}


def poll_images():
    """Reload Blender images whose file Sable saved since the last poll."""
    changed = []
    for path, seen in state.image_mtimes.items():
        now = _mtime(path)
        if now is not None and now != seen:
            state.image_mtimes[path] = now
            changed.append(os.path.normcase(path))
    if not changed:
        return 0
    reloaded = 0
    for img in bpy.data.images:
        if img.source != 'FILE' or img.packed_file is not None:
            continue
        path = bpy.path.abspath(img.filepath, library=img.library)
        if path.startswith("//"):
            continue
        if os.path.normcase(os.path.normpath(os.path.abspath(path))) in changed:
            state.quiet_until = time.monotonic() + IMAGE_RELOAD_QUIET
            img.reload()
            reloaded += 1
    if reloaded:
        _redraw()
    return reloaded


def _redraw():
    wm = bpy.context.window_manager
    if wm is None:
        return
    for window in wm.windows:
        for area in window.screen.areas:
            if area.type in {'VIEW_3D', 'IMAGE_EDITOR'}:
                area.tag_redraw()


# -----------------------------------------------------------------------------
# Start / stop


def start_link(objects):
    """Make objects the linked set and export them. Returns an error message or None."""
    # Keep writing where the running Sable is listening; a fresh launch uses the current .blend's folder.
    link_dir = state.process_link_dir if sable_running() and state.process_link_dir else compute_link_dir()
    if state.active and state.link_dir and state.link_dir != link_dir:
        _remove_link_json(state.link_dir)
    revision = state.dir_revisions.get(link_dir, 0)
    state.reset()
    state.active = True
    state.link_dir = link_dir
    state.revision = revision
    state.object_uids = [obj.session_uid for obj in objects]
    state.object_names = [obj.name for obj in objects]
    error = export_now()
    if error:
        state.active = False
        return error
    ensure_timer()
    return None


def _remove_link_json(link_dir):
    for name in ("link.json", "link.json.tmp"):
        try:
            os.remove(os.path.join(link_dir, name))
        except OSError:
            pass


def stop_link():
    if state.link_dir:
        _remove_link_json(state.link_dir)
    state.reset()


# -----------------------------------------------------------------------------
# Handler and timer


@persistent
def on_depsgraph_update(scene, depsgraph):
    if state.active and _relevant(depsgraph):
        state.dirty = True
        state.last_change = time.monotonic()


def _relevant(depsgraph):
    object_uids = state.object_uids
    quiet = time.monotonic() < state.quiet_until
    for update in depsgraph.updates:
        id_ = update.id.original
        if isinstance(id_, bpy.types.Object):
            if id_.session_uid in object_uids and (update.is_updated_geometry or update.is_updated_transform):
                return True
        elif isinstance(id_, bpy.types.Mesh):
            # Geometry only: edit-mode selection changes come through as shading-only mesh updates.
            if id_.session_uid in state.mesh_uids and update.is_updated_geometry:
                return True
        elif isinstance(id_, bpy.types.Material):
            # An image reload reports the materials using it (without the shading flag, but be safe).
            if id_.session_uid in state.material_uids and update.is_updated_shading and not quiet:
                return True
    return False


@persistent
def on_load_post(*_args):
    stop_link()


def _timer():
    if not state.active:
        return None
    now = time.monotonic()
    objects = linked_objects()
    refresh_dependencies(objects)
    if not state.dirty:
        # Renames don't come through as geometry updates; catch them here.
        names = [obj.name for obj in objects]
        if names != state.exported_names:
            state.dirty = True
            state.last_change = now - DEBOUNCE
    if state.dirty and now - state.last_change >= DEBOUNCE:
        if objects:
            export_now()
            _redraw_sidebar()
        else:
            state.dirty = False
    if now >= state.next_image_poll:
        state.next_image_poll = now + IMAGE_POLL_INTERVAL
        try:
            poll_images()
        except Exception as ex:
            print("Sable Link: image reload failed:", ex)
    return TIMER_INTERVAL


def _redraw_sidebar():
    wm = bpy.context.window_manager
    if wm is None:
        return
    for window in wm.windows:
        for area in window.screen.areas:
            if area.type == 'VIEW_3D':
                area.tag_redraw()


def ensure_timer():
    if not bpy.app.timers.is_registered(_timer):
        bpy.app.timers.register(_timer, first_interval=TIMER_INTERVAL, persistent=True)


def register():
    if on_depsgraph_update not in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.append(on_depsgraph_update)
    if on_load_post not in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.append(on_load_post)


def unregister():
    if on_depsgraph_update in bpy.app.handlers.depsgraph_update_post:
        bpy.app.handlers.depsgraph_update_post.remove(on_depsgraph_update)
    if on_load_post in bpy.app.handlers.load_post:
        bpy.app.handlers.load_post.remove(on_load_post)
    if bpy.app.timers.is_registered(_timer):
        bpy.app.timers.unregister(_timer)
