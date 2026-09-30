# SPDX-License-Identifier: GPL-3.0-or-later
"""Minimal glTF 2.0 binary (GLB) writer for the Sable link.

Writes one node + one mesh per object (named after the object), with the object's world matrix baked into the
vertex data, per-corner normals and the active UV map, one primitive per used material slot. Everything per-vertex
goes through numpy so large meshes export quickly. Axis conversion matches Blender's glTF exporter (Y up).
"""

import json
import os
import struct

import bpy
import numpy as np

NONE_MATERIAL = "(none)"

_GLB_MAGIC = 0x46546C67  # "glTF"
_CHUNK_JSON = 0x4E4F534A  # "JSON"
_CHUNK_BIN = 0x004E4942  # "BIN\0"

_FLOAT = 5126
_ARRAY_BUFFER = 34962


def material_base_color(mat):
    """RGBA base color of a material: the Principled BSDF Base Color default, else the viewport diffuse color."""
    if mat is None:
        return [1.0, 1.0, 1.0, 1.0]
    # Blender 5.0 always uses nodes for materials (Material.use_nodes is deprecated and warns).
    uses_nodes = True if bpy.app.version >= (5, 0, 0) else mat.use_nodes
    tree = mat.node_tree if uses_nodes else None
    if tree is not None:
        for node in tree.nodes:
            if node.type == 'BSDF_PRINCIPLED':
                socket = node.inputs.get("Base Color")
                if socket is not None:
                    return [float(c) for c in socket.default_value]
    return [float(c) for c in mat.diffuse_color]


def material_image_path(mat):
    """Absolute path of the first image-texture node's file image in a material (not packed), or None."""
    if mat is None or mat.node_tree is None:
        return None
    for node in mat.node_tree.nodes:
        if node.type != 'TEX_IMAGE':
            continue
        img = node.image
        if img is None or img.source != 'FILE' or img.packed_file is not None:
            continue
        path = bpy.path.abspath(img.filepath, library=img.library)
        if not path or path.startswith("//"):
            continue  # Relative to an unsaved .blend: can't be resolved.
        return os.path.normpath(os.path.abspath(path))
    return None


def _foreach(collection, attr, count, width, dtype=np.float32):
    out = np.empty(count * width, dtype=dtype)
    if count:
        collection.foreach_get(attr, out)
    return out.reshape(count, width) if width > 1 else out


# Blender (x, y, z) -> glTF (x, z, -y).
_AXIS = np.array([[1.0, 0.0, 0.0],
                  [0.0, 0.0, 1.0],
                  [0.0, -1.0, 0.0]], dtype=np.float64)


def _mesh_arrays(obj, depsgraph):
    """Triangulated per-corner arrays of an object's evaluated mesh, in glTF space.

    Returns (positions[N*3,3], normals[N*3,3], uvs[N*3,2] or None, material_index[N]) as float32/int arrays,
    or None when the object has no triangles.
    """
    obj_eval = obj.evaluated_get(depsgraph)
    mesh = obj_eval.to_mesh()
    try:
        mesh.calc_loop_triangles()
        tri_count = len(mesh.loop_triangles)
        if tri_count == 0:
            return None
        corner_count = len(mesh.loops)

        tri_corners = _foreach(mesh.loop_triangles, "loops", tri_count, 3, np.int32)
        tri_mat = _foreach(mesh.loop_triangles, "material_index", tri_count, 1, np.int32)
        positions = _foreach(mesh.vertices, "co", len(mesh.vertices), 3)
        corner_vert = _foreach(mesh.loops, "vertex_index", corner_count, 1, np.int32)
        corner_normals = _foreach(mesh.corner_normals, "vector", corner_count, 3)
        uv_layer = mesh.uv_layers.active
        corner_uv = _foreach(uv_layer.uv, "vector", corner_count, 2) if uv_layer is not None else None
    finally:
        obj_eval.to_mesh_clear()

    world = np.array(obj.matrix_world, dtype=np.float64)
    linear = world[:3, :3]
    if np.linalg.det(linear) < 0.0:
        tri_corners = tri_corners[:, [0, 2, 1]]  # A mirrored transform flips the winding; flip it back.

    corners = tri_corners.reshape(-1)
    pos = positions[corner_vert[corners]].astype(np.float64)
    pos = pos @ linear.T + world[:3, 3]
    pos = pos @ _AXIS.T

    try:
        normal_matrix = np.linalg.inv(linear).T
    except np.linalg.LinAlgError:
        normal_matrix = linear
    nrm = corner_normals[corners].astype(np.float64) @ (_AXIS @ normal_matrix).T
    length = np.linalg.norm(nrm, axis=1, keepdims=True)
    nrm = np.divide(nrm, length, out=np.zeros_like(nrm), where=length > 1e-20)

    uv = None
    if corner_uv is not None:
        uv = corner_uv[corners].copy()
        uv[:, 1] = 1.0 - uv[:, 1]

    return (pos.astype(np.float32), nrm.astype(np.float32),
            None if uv is None else uv.astype(np.float32), tri_mat)


class _Builder:
    def __init__(self):
        self.chunks = []
        self.offset = 0
        self.buffer_views = []
        self.accessors = []

    def add(self, array, accessor_type, with_bounds=False):
        data = np.ascontiguousarray(array, dtype=np.float32)
        raw = data.tobytes()
        pad = (-len(raw)) % 4
        self.buffer_views.append({"buffer": 0, "byteOffset": self.offset, "byteLength": len(raw),
                                  "target": _ARRAY_BUFFER})
        self.chunks.append(raw)
        if pad:
            self.chunks.append(b"\0" * pad)
        self.offset += len(raw) + pad
        accessor = {"bufferView": len(self.buffer_views) - 1, "componentType": _FLOAT,
                    "count": int(data.shape[0]), "type": accessor_type}
        if with_bounds:
            accessor["min"] = [float(v) for v in data.min(axis=0)]
            accessor["max"] = [float(v) for v in data.max(axis=0)]
        self.accessors.append(accessor)
        return len(self.accessors) - 1


def write_glb(filepath, objects, depsgraph, generator="Sable Link"):
    """Write the objects to a GLB at filepath (via filepath + '.tmp' and os.replace).

    Returns the list of Blender materials (None for the "(none)" material) in glTF material order, and the total
    triangle count.
    """
    builder = _Builder()
    nodes, meshes = [], []
    materials = []  # Blender materials (or None) in glTF order.
    material_index = {}
    triangles = 0

    def gltf_material(mat):
        key = None if mat is None else mat.session_uid
        if key not in material_index:
            material_index[key] = len(materials)
            materials.append(mat)
        return material_index[key]

    for obj in objects:
        node = {"name": obj.name}
        arrays = _mesh_arrays(obj, depsgraph)
        if arrays is not None:
            pos, nrm, uv, tri_mat = arrays
            triangles += len(tri_mat)
            slots = obj.material_slots
            slot_count = len(slots)
            # Out-of-range indices clamp to the last slot, like Blender does.
            tri_mat = np.clip(tri_mat, 0, max(slot_count - 1, 0))
            order = np.argsort(tri_mat, kind="stable")
            sorted_mat = tri_mat[order]
            corner_order = (order[:, None] * 3 + np.arange(3)).reshape(-1)
            pos, nrm = pos[corner_order], nrm[corner_order]
            if uv is not None:
                uv = uv[corner_order]
            used, starts = np.unique(sorted_mat, return_index=True)
            ends = list(starts[1:]) + [len(sorted_mat)]
            primitives = []
            for slot_index, start, end in zip(used.tolist(), starts.tolist(), ends):
                mat = slots[slot_index].material if slot_index < slot_count else None
                a, b = start * 3, end * 3
                attributes = {"POSITION": builder.add(pos[a:b], "VEC3", with_bounds=True),
                              "NORMAL": builder.add(nrm[a:b], "VEC3")}
                if uv is not None:
                    attributes["TEXCOORD_0"] = builder.add(uv[a:b], "VEC2")
                primitives.append({"attributes": attributes, "mode": 4, "material": gltf_material(mat)})
            node["mesh"] = len(meshes)
            meshes.append({"name": obj.name, "primitives": primitives})
        nodes.append(node)

    gltf = {
        "asset": {"version": "2.0", "generator": generator},
        "scene": 0,
        "scenes": [{"nodes": list(range(len(nodes)))}],
        "nodes": nodes,
    }
    if meshes:
        gltf["meshes"] = meshes
    if materials:
        gltf["materials"] = [
            {"name": NONE_MATERIAL if mat is None else mat.name,
             "pbrMetallicRoughness": {"baseColorFactor": material_base_color(mat)}}
            for mat in materials
        ]
    if builder.accessors:
        gltf["accessors"] = builder.accessors
        gltf["bufferViews"] = builder.buffer_views
        gltf["buffers"] = [{"byteLength": builder.offset}]

    json_bytes = json.dumps(gltf, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    json_bytes += b" " * ((-len(json_bytes)) % 4)
    total = 12 + 8 + len(json_bytes)
    if builder.offset:
        total += 8 + builder.offset

    tmp = filepath + ".tmp"
    with open(tmp, "wb") as f:
        f.write(struct.pack("<III", _GLB_MAGIC, 2, total))
        f.write(struct.pack("<II", len(json_bytes), _CHUNK_JSON))
        f.write(json_bytes)
        if builder.offset:
            f.write(struct.pack("<II", builder.offset, _CHUNK_BIN))
            for chunk in builder.chunks:
                f.write(chunk)
    os.replace(tmp, filepath)
    return materials, triangles
