# SPDX-License-Identifier: GPL-3.0-or-later
"""Stack identical UV islands: move every copy of a UV shape exactly onto one master island.

An island is a set of faces connected through edges whose two sides have the same UVs (Blender's definition). Two
islands match when they have the same face and UV-vertex counts, about the same area, and the copy's UV points land
within the tolerance of the master's after a rigid 2D transform (rotation + translation, optionally mirrored).

The transform is found by centring both point sets at their area-weighted centroids and lining up their principal
axes (0/180 degrees, or 0/90/180/270 for near-isotropic shapes), falling back to lining up edges of equal length
(squares, circles); the best candidate is refined with a least-squares rigid fit on the nearest-neighbour pairs.
Moved UVs are snapped onto the master's exact coordinates, so stacked islands are identical to the bit.

Edit Mode reads and writes the edit-mesh through bmesh; Object Mode reads and writes the mesh's UV map directly,
so neither mode, selection nor the active object changes.
"""

import math
import time

import bmesh
import numpy as np
from mathutils import kdtree

# Covariance anisotropy (l1 - l2) / (l1 + l2) below which the principal axes are ambiguous: try 90-degree steps.
ISOTROPIC = 0.1
# Coarse candidates pass with a looser gate (in tolerances); the least-squares fit then has to meet the tolerance.
COARSE_GATE = 4.0
# Most edge-alignment candidates tried per mirror state, for near-isotropic shapes such as circles.
MAX_EDGE_CANDIDATES = 128
# Islands with fewer points than this compare by brute force (numpy) instead of a KD-tree.
BRUTE_FORCE_POINTS = 64
# Least-squares refinements (each re-pairs nearest neighbours) before giving up on a start rotation.
REFINE_STEPS = 3
# Islands with up to this many points are compared by all their pairwise distances before matching.
SIGNATURE_PAIRS_POINTS = 32
# Largest (angles x copy points x master points) distance table for the vectorised coarse check.
BATCH_LIMIT = 1 << 18
# The tolerance never goes below this (UVs are float32).
MIN_TOLERANCE = 1e-6


class StackResult:
    def __init__(self):
        self.error = ""
        self.objects = 0
        self.islands = 0
        self.groups = 0  # Groups with a master and at least one copy.
        self.stacked = 0  # Copies placed on a master (including ones that were already there).
        self.moved = 0  # Copies whose UVs changed.
        self.mirrored = 0  # Moved copies that were flipped.
        self.seconds = 0.0


# -----------------------------------------------------------------------------
# Objects and UV data


def target_objects(context):
    """The mesh objects the operator works on: the ones in Edit Mode, else the selected ones (one per editable mesh)."""
    if context.mode == 'EDIT_MESH':
        objects = list(context.objects_in_mode_unique_data or ())
    else:
        objects = [obj for obj in (context.selected_objects or ()) if obj.type == 'MESH']
    result, seen = [], set()
    for obj in objects:
        mesh = obj.data
        if obj.type != 'MESH' or mesh is None or mesh.session_uid in seen:
            continue
        if not getattr(mesh, "is_editable", mesh.library is None):
            continue  # Linked from a library.
        seen.add(mesh.session_uid)
        result.append(obj)
    return result


def _uv_bits(uv32):
    """The float32 UVs as int64 bit patterns (with -0.0 folded into 0.0), for exact comparisons."""
    clean = np.ascontiguousarray(uv32, dtype=np.float32) + np.float32(0.0)
    return clean.view(np.int32).astype(np.int64)


class _Source:
    """One mesh's faces and corners as flat arrays (corners grouped by face, in face order)."""

    def __init__(self, obj):
        self.mesh = obj.data
        self.bm = None
        self.uv_layer = None
        self.loops = None  # Edit Mode: the BMLoop of each flat corner.
        self.corner_ids = None  # Object Mode: the mesh corner index of each flat corner.
        self.face_size = None
        self.face_used = None
        self.active_face = -1
        self.vert = None
        self.edge = None
        self.uv32 = None

    def read_edit(self, selected_only, uv_sync, is_active):
        bm = bmesh.from_edit_mesh(self.mesh)
        layer = bm.loops.layers.uv.active
        if layer is None:
            return False
        self.bm, self.uv_layer = bm, layer
        bm.verts.index_update()
        bm.edges.index_update()
        bm.faces.index_update()
        has_uv_select = hasattr(bmesh.types.BMLoop, "uv_select_vert")  # Blender 5.0+; before, on BMLoopUV.
        loops, uvs, verts, edges = [], [], [], []
        sizes, used = [], []
        for face in bm.faces:
            face_loops = face.loops
            sizes.append(len(face_loops))
            if face.hide:
                used.append(False)
            elif not selected_only:
                used.append(True)
            elif uv_sync:
                used.append(face.select)
            elif has_uv_select:  # The UV editor shows selected faces only; then their selected UVs count.
                used.append(face.select and any(loop.uv_select_vert for loop in face_loops))
            else:
                used.append(face.select and any(loop[layer].select for loop in face_loops))
            for loop in face_loops:
                loops.append(loop)
                uvs.extend(loop[layer].uv)
                verts.append(loop.vert.index)
                edges.append(loop.edge.index)
        self.loops = loops
        self.face_size = np.array(sizes, dtype=np.int64)
        self.face_used = np.array(used, dtype=bool)
        self.vert = np.array(verts, dtype=np.int64)
        self.edge = np.array(edges, dtype=np.int64)
        self.uv32 = np.array(uvs, dtype=np.float32).reshape(-1, 2)
        active = bm.faces.active
        if is_active and active is not None and active.is_valid and self.face_used[active.index]:
            self.active_face = active.index
        return True

    def read_object(self):
        mesh = self.mesh
        layer = mesh.uv_layers.active
        if layer is None:
            return False
        self.uv_layer = layer
        face_count, corner_count = len(mesh.polygons), len(mesh.loops)
        start = np.empty(face_count, dtype=np.int64)
        size = np.empty(face_count, dtype=np.int64)
        mesh.polygons.foreach_get("loop_start", start)
        mesh.polygons.foreach_get("loop_total", size)
        offsets = np.cumsum(size) - size
        self.corner_ids = np.repeat(start, size) + (np.arange(int(size.sum())) - np.repeat(offsets, size))
        vert = np.empty(corner_count, dtype=np.int64)
        edge = np.empty(corner_count, dtype=np.int64)
        uv = np.empty(corner_count * 2, dtype=np.float32)
        mesh.loops.foreach_get("vertex_index", vert)
        mesh.loops.foreach_get("edge_index", edge)
        layer.uv.foreach_get("vector", uv)
        self.face_size = size
        self.face_used = np.ones(face_count, dtype=bool)  # Object Mode: hide and selection don't apply.
        self.vert = vert[self.corner_ids]
        self.edge = edge[self.corner_ids]
        self.uv32 = uv.reshape(-1, 2)[self.corner_ids]
        return True

    def write(self, corners, uv):
        """Set the UVs of flat corners (int array) to uv (N x 2)."""
        if self.bm is not None:
            layer = self.uv_layer
            loops = self.loops
            for c, (u, v) in zip(corners.tolist(), uv.tolist()):
                loops[c][layer].uv = (u, v)
            bmesh.update_edit_mesh(self.mesh, loop_triangles=False, destructive=False)
        else:
            corner_count = len(self.mesh.loops)
            data = np.empty(corner_count * 2, dtype=np.float32)
            self.uv_layer.uv.foreach_get("vector", data)
            data = data.reshape(-1, 2)
            data[self.corner_ids[corners]] = uv
            self.uv_layer.uv.foreach_set("vector", data.reshape(-1))
            self.mesh.update()


# -----------------------------------------------------------------------------
# Islands


def _components(count, a, b):
    """Connected-component labels (the smallest member index) of count nodes joined by the pairs a[i]-b[i]."""
    labels = np.arange(count)
    while len(a):
        ra, rb = labels[a], labels[b]
        differ = ra != rb
        if not differ.any():
            break
        ra, rb = ra[differ], rb[differ]
        a, b = a[differ], b[differ]
        np.minimum.at(labels, np.maximum(ra, rb), np.minimum(ra, rb))  # Hook the larger root under the smaller.
        while True:
            jumped = labels[labels]
            if np.array_equal(jumped, labels):
                break
            labels = jumped
    return labels


class _Island:
    __slots__ = ("source", "index", "corners", "corner_point", "next_local", "points", "faces", "area",
                 "perimeter", "centroid", "bbox_min", "first_face", "has_active", "_axis", "_edges", "_kd")

    def __init__(self, source, index):
        self.source = source
        self.index = index
        self._axis = None
        self._edges = None
        self._kd = None
        self.has_active = False

    def axis(self):
        """(principal axis angle, anisotropy) of the points about the area centroid."""
        if self._axis is None:
            self._axis = _principal_axis(self.points - self.centroid)
        return self._axis

    def edges(self):
        """(point index pairs, direction vectors, lengths) of the island's unique UV edges."""
        if self._edges is None:
            pairs = self.corner_point_pairs()
            vectors = self.points[pairs[:, 1]] - self.points[pairs[:, 0]]
            self._edges = (pairs, vectors, np.linalg.norm(vectors, axis=1))
        return self._edges

    def corner_point_pairs(self):
        a, b = self.corner_point, self.corner_point[self.next_local]
        pairs = np.unique(np.sort(np.stack([a, b], axis=1), axis=1), axis=0)
        return pairs[pairs[:, 0] != pairs[:, 1]]

    def nearest(self, xy):
        """(distance, index) of the nearest island point to each row of xy."""
        if len(self.points) < BRUTE_FORCE_POINTS:
            return _nearest_brute(self.points, xy)
        if self._kd is None:
            self._kd = _kd_tree(self.points)
        return _nearest_kd(self._kd, xy)


def _nearest_brute(points, xy):
    dx = xy[:, 0, None] - points[:, 0]
    dy = xy[:, 1, None] - points[:, 1]
    d2 = dx * dx + dy * dy
    index = d2.argmin(axis=1)
    return np.sqrt(np.take_along_axis(d2, index[:, None], axis=1)[:, 0]), index


def _kd_tree(points):
    kd = kdtree.KDTree(len(points))
    for i, (x, y) in enumerate(points.tolist()):
        kd.insert((x, y, 0.0), i)
    kd.balance()
    return kd


def _nearest_kd(kd, xy):
    find = kd.find
    found = [find((x, y, 0.0)) for x, y in xy.tolist()]
    return (np.fromiter((f[2] for f in found), dtype=np.float64, count=len(found)),
            np.fromiter((f[1] for f in found), dtype=np.int64, count=len(found)))


def _nearest(points, xy):
    if len(points) < BRUTE_FORCE_POINTS:
        return _nearest_brute(points, xy)
    return _nearest_kd(_kd_tree(points), xy)


def _principal_axis(centered):
    cov = centered.T @ centered / max(len(centered), 1)
    a, b, c = cov[0, 0], cov[0, 1], cov[1, 1]
    spread = a + c
    split = math.hypot(a - c, 2.0 * b)
    anisotropy = split / spread if spread > 1e-30 else 0.0
    return 0.5 * math.atan2(2.0 * b, a - c), anisotropy


def _find_islands(src, source_index):
    """The islands of one source's used faces."""
    sizes = src.face_size
    face_count = len(sizes)
    corner_count = int(sizes.sum())
    if corner_count == 0:
        return []
    starts = np.cumsum(sizes) - sizes
    face_of = np.repeat(np.arange(face_count), sizes)
    nxt = np.arange(1, corner_count + 1)
    nxt[starts + sizes - 1] = starts
    bits = _uv_bits(src.uv32)

    used = src.face_used[face_of]
    corners = np.nonzero(used)[0]
    if len(corners) == 0:
        return []

    # Corners along the same edge with the same UVs at both of its vertices connect their faces.
    c, n = corners, nxt[corners]
    swap = src.vert[c] > src.vert[n]
    lo = np.where(swap[:, None], bits[n], bits[c])
    hi = np.where(swap[:, None], bits[c], bits[n])
    keys = np.column_stack([src.edge[c], lo, hi])
    order = np.lexsort(keys.T[::-1])
    keys = keys[order]
    same = np.all(keys[1:] == keys[:-1], axis=1)
    fa = face_of[c[order[:-1][same]]]
    fb = face_of[c[order[1:][same]]]
    labels = _components(face_count, fa, fb)

    used_faces = np.nonzero(src.face_used)[0]
    roots, face_island = np.unique(labels[used_faces], return_inverse=True)
    island_count = len(roots)
    island_of_face = np.full(face_count, -1, dtype=np.int64)
    island_of_face[used_faces] = face_island
    corner_island = island_of_face[face_of[corners]]

    # UV vertices: corners of one island at the same vertex with the same UV.
    rows = np.column_stack([corner_island, src.vert[corners], bits[corners]])
    uniq, first, corner_point = np.unique(rows, axis=0, return_index=True, return_inverse=True)
    corner_point = corner_point.reshape(-1)
    point_island = uniq[:, 0]
    uv64 = src.uv32.astype(np.float64)
    point_xy = uv64[corners[first]]
    point_start = np.searchsorted(point_island, np.arange(island_count))
    point_count = np.bincount(point_island, minlength=island_count)

    # Area, area-weighted centroid and summed face perimeters (for the area bounds).
    x, y = uv64[:, 0], uv64[:, 1]
    xn, yn = x[nxt], y[nxt]
    cross = (x * yn - xn * y)[corners]
    fc = face_of[corners]
    area2 = np.bincount(fc, cross, minlength=face_count)
    sx = np.bincount(fc, ((x + xn)[corners]) * cross, minlength=face_count)
    sy = np.bincount(fc, ((y + yn)[corners]) * cross, minlength=face_count)
    sign = np.sign(area2)
    fi = island_of_face[used_faces]
    island_area = np.bincount(fi, np.abs(area2[used_faces]), minlength=island_count) * 0.5
    island_sx = np.bincount(fi, (sign * sx)[used_faces], minlength=island_count) / 6.0
    island_sy = np.bincount(fi, (sign * sy)[used_faces], minlength=island_count) / 6.0
    edge_length = np.hypot(xn - x, yn - y)[corners]
    island_perimeter = np.bincount(corner_island, edge_length, minlength=island_count)
    island_faces = np.bincount(fi, minlength=island_count)
    first_face = np.full(island_count, face_count, dtype=np.int64)
    np.minimum.at(first_face, fi, used_faces)

    corner_order = np.argsort(corner_island, kind="stable")
    corner_start = np.searchsorted(corner_island[corner_order], np.arange(island_count + 1))
    local_of_flat = np.full(corner_count, -1, dtype=np.int64)

    islands = []
    for k in range(island_count):
        isl = _Island(source_index, k)
        sel = corner_order[corner_start[k]:corner_start[k + 1]]
        flat = corners[sel]
        isl.corners = flat
        isl.corner_point = corner_point[sel] - point_start[k]
        p0 = point_start[k]
        isl.points = point_xy[p0:p0 + point_count[k]]
        local_of_flat[flat] = np.arange(len(flat))
        isl.next_local = local_of_flat[nxt[flat]]
        isl.faces = int(island_faces[k])
        isl.area = float(island_area[k])
        isl.perimeter = float(island_perimeter[k])
        if isl.area > 1e-14:
            isl.centroid = np.array([island_sx[k], island_sy[k]]) / isl.area
        else:
            isl.centroid = isl.points.mean(axis=0)
        isl.bbox_min = isl.points.min(axis=0)
        isl.first_face = int(first_face[k])
        isl.has_active = src.active_face >= 0 and island_of_face[src.active_face] == k
        islands.append(isl)
    return islands


# -----------------------------------------------------------------------------
# Matching


def _rotation(angle):
    c, s = math.cos(angle), math.sin(angle)
    return np.array([[c, -s], [s, c]])


def _wrap(angle):
    return math.atan2(math.sin(angle), math.cos(angle))


def _sorted_angles(angles, limit=None):
    """Unique angles in (-pi, pi], smallest rotation first (so consistent unwraps keep their orientation)."""
    unique = {}
    for angle in angles:
        angle = _wrap(angle)
        unique.setdefault(round(angle, 9), angle)
    result = sorted(unique.values(), key=abs)
    return result[:limit] if limit else result


def _axis_angles(master, local):
    """Rotations lining up the principal axes of local (a centred copy) with the master's, and the anisotropy."""
    master_angle, master_anisotropy = master.axis()
    local_angle, local_anisotropy = _principal_axis(local)
    anisotropy = min(master_anisotropy, local_anisotropy)
    step = math.pi / 2.0 if anisotropy < ISOTROPIC else math.pi
    base = master_angle - local_angle
    return _sorted_angles(base + k * step for k in range(round(2.0 * math.pi / step))), anisotropy


def _edge_angles(master, copy, mirror, tol):
    """Rotations lining up the copy's longest edge with each master edge of the same length (either way round)."""
    _, vectors, lengths = copy.edges()
    if not len(lengths):
        return []
    longest = int(lengths.argmax())
    vx, vy = vectors[longest]
    if mirror:
        vx = -vx
    copy_angle = math.atan2(vy, vx)
    _, master_vectors, master_lengths = master.edges()
    same = np.nonzero(np.abs(master_lengths - lengths[longest]) <= 2.0 * tol)[0]
    angles = []
    for mx, my in master_vectors[same].tolist():
        angle = math.atan2(my, mx) - copy_angle
        angles.append(angle)
        angles.append(angle + math.pi)
    return _sorted_angles(angles, MAX_EDGE_CANDIDATES)


def _fit(local, target):
    """The rotation r and offset t minimising |local @ r.T + t - target| (2D Kabsch, in closed form)."""
    count = len(local)
    local_mean, target_mean = local.sum(axis=0) / count, target.sum(axis=0) / count
    a, b = local - local_mean, target - target_mean
    dot = float(np.dot(a[:, 0], b[:, 0]) + np.dot(a[:, 1], b[:, 1]))
    cross = float(np.dot(a[:, 0], b[:, 1]) - np.dot(a[:, 1], b[:, 0]))
    r = _rotation(math.atan2(cross, dot))
    return r, target_mean - r @ local_mean


def _coarse(master, local, angles, gate):
    """The angles (in order) that put every point of local within gate of a master point."""
    if not angles:
        return []
    angle = np.array(angles)
    c, s = np.cos(angle)[:, None], np.sin(angle)[:, None]
    x, y = local[:, 0], local[:, 1]
    mx = c * x - s * y + master.centroid[0]  # (angles, points)
    my = s * x + c * y + master.centroid[1]
    pts = master.points
    if len(angles) * len(local) * len(pts) <= BATCH_LIMIT:
        d2 = (mx[:, :, None] - pts[:, 0]) ** 2 + (my[:, :, None] - pts[:, 1]) ** 2
        ok = d2.min(axis=2).max(axis=1) <= gate * gate
        return [a for a, keep in zip(angles, ok.tolist()) if keep]
    # Large islands: reject on a few spread-out points by brute force, then check all of them with the KD-tree.
    probe = np.linspace(0, len(local) - 1, min(len(local), 16)).astype(np.int64)
    hits = []
    for i, a in enumerate(angles):
        d2 = (mx[i, probe, None] - pts[:, 0]) ** 2 + (my[i, probe, None] - pts[:, 1]) ** 2
        if d2.min(axis=1).max() > gate * gate:
            continue
        dist, _ = master.nearest(np.column_stack([mx[i], my[i]]))
        if dist.max() <= gate:
            hits.append(a)
    return hits


def _refine(master, local, angle, tol):
    """Fit local (the centred, maybe mirrored copy) onto master from a start rotation: the placed points, or None."""
    moved = local @ _rotation(angle).T + master.centroid
    _, index = master.nearest(moved)
    for _ in range(REFINE_STEPS):
        r, t = _fit(local, master.points[index])
        moved = local @ r.T + t
        dist, fitted = master.nearest(moved)
        settled = np.array_equal(fitted, index)
        index = fitted
        if settled:
            break
    if dist.max() > tol:
        return None
    if np.bincount(index).max() > 1:
        # Not one-to-one: every master point must also have a copy point near it.
        back, _ = _nearest(moved, master.points)
        if back.max() > tol:
            return None
    # Snap onto the master's exact coordinates, so the stacked islands are identical.
    placed = moved.copy()
    close = dist <= tol
    placed[close] = master.points[index[close]]
    return placed


def _match(master, copy, tol, allow_mirror):
    """(mirrored, the copy's points placed on the master) or None."""
    centred = copy.points - copy.centroid
    flips = [(False, centred)]
    if allow_mirror:
        flips.append((True, centred * np.array([-1.0, 1.0])))
    isotropic = False
    for mirror, local in flips:
        angles, anisotropy = _axis_angles(master, local)
        isotropic = anisotropy < ISOTROPIC
        # Moving points by tol turns the principal axes by about 2 * tol / (anisotropy * radius): allow for that.
        gate = tol * (COARSE_GATE + 2.0 / max(anisotropy, ISOTROPIC))
        for angle in _coarse(master, local, angles, gate):
            placed = _refine(master, local, angle, tol)
            if placed is not None:
                return mirror, placed
    if isotropic:
        # The principal axes of near-isotropic shapes (squares, circles) mean little: line up edges instead.
        for mirror, local in flips:
            for angle in _coarse(master, local, _edge_angles(master, copy, mirror, tol), tol * COARSE_GATE):
                placed = _refine(master, local, angle, tol)
                if placed is not None:
                    return mirror, placed
    return None


def _signatures(bucket):
    """A rotation/mirror invariant signature per island of a bucket (all with the same point count), one per row.

    Sorted pairwise point distances for small islands, else sorted distances from the point mean. Moving every point
    by at most tol changes each entry by at most 2 * tol, so matching islands' signatures are that close.
    """
    count = len(bucket[0].points)
    if count > SIGNATURE_PAIRS_POINTS:
        points = np.stack([island.points for island in bucket])
        return np.sort(np.linalg.norm(points - points.mean(axis=1, keepdims=True), axis=2), axis=1)
    upper = np.triu_indices(count, 1)
    chunk = max(1, 1_000_000 // (count * count))
    rows = []
    for first in range(0, len(bucket), chunk):
        points = np.stack([island.points for island in bucket[first:first + chunk]])
        a, b = points[:, upper[0]], points[:, upper[1]]
        rows.append(np.sort(np.linalg.norm(a - b, axis=2), axis=1))
    return np.concatenate(rows)


def _preference(island):
    """Master order: the island with the active face, then the lowest-left bounding box."""
    u, v = island.bbox_min.tolist()
    return (not island.has_active, u + v, v, u, island.source, island.first_face)


def _group(islands, tol, allow_mirror):
    """[(master, [(copy, mirrored, placed points), ...]), ...] for every master with at least one copy."""
    buckets = {}
    for island in islands:
        buckets.setdefault((island.faces, len(island.points)), []).append(island)
    groups = []
    for bucket in buckets.values():
        if len(bucket) < 2:
            continue
        bucket.sort(key=_preference)
        areas = np.array([island.area for island in bucket])
        by_area = np.argsort(areas, kind="stable")
        sorted_areas = areas[by_area]
        signatures = _signatures(bucket)
        assigned = np.zeros(len(bucket), dtype=bool)
        for i, master in enumerate(bucket):
            if assigned[i]:
                continue
            assigned[i] = True
            # Moving each point by up to tol changes a face's area by at most about its perimeter * tol.
            bound = tol * master.perimeter + 4.0 * master.faces * tol * tol + 1e-12
            lo = np.searchsorted(sorted_areas, master.area - bound, side="left")
            hi = np.searchsorted(sorted_areas, master.area + bound, side="right")
            candidates = by_area[lo:hi]
            candidates = candidates[~assigned[candidates]]
            if not len(candidates):
                continue
            close = np.abs(signatures[candidates] - signatures[i]).max(axis=1) <= 2.0 * tol + 1e-9
            members = []
            for j in np.sort(candidates[close]).tolist():
                found = _match(master, bucket[j], tol, allow_mirror)
                if found is not None:
                    assigned[j] = True
                    members.append((bucket[j], found[0], found[1]))
            if members:
                groups.append((master, members))
    return groups


# -----------------------------------------------------------------------------
# Entry point


def stack_identical(context, tolerance=0.002, allow_mirror=True, selected_only=False):
    """Stack identical UV islands of the context's mesh objects. Returns a StackResult."""
    start = time.perf_counter()
    result = StackResult()
    objects = target_objects(context)
    if not objects:
        result.error = "Select one or more mesh objects"
        return result
    edit = context.mode == 'EDIT_MESH'
    uv_sync = context.scene.tool_settings.use_uv_select_sync
    active = context.active_object
    sources = []
    for obj in objects:
        src = _Source(obj)
        if edit and obj.mode == 'EDIT':
            ok = src.read_edit(selected_only, uv_sync, obj == active)
        else:
            ok = src.read_object()
        if ok:
            sources.append(src)
    result.objects = len(sources)
    if not sources:
        result.error = "The selected meshes have no UV map"
        return result

    islands = []
    for index, src in enumerate(sources):
        islands.extend(_find_islands(src, index))
    result.islands = len(islands)

    tol = max(float(tolerance), MIN_TOLERANCE)
    updates = {}
    for _master, members in _group(islands, tol, allow_mirror):
        result.groups += 1
        for copy, mirrored, placed in members:
            result.stacked += 1
            src = sources[copy.source]
            uv = placed[copy.corner_point].astype(np.float32)
            if np.array_equal(uv, src.uv32[copy.corners]):
                continue  # Already on its master.
            result.moved += 1
            result.mirrored += int(mirrored)
            corners, uvs = updates.setdefault(copy.source, ([], []))
            corners.append(copy.corners)
            uvs.append(uv)
    for index, (corners, uvs) in updates.items():
        sources[index].write(np.concatenate(corners), np.concatenate(uvs))
    result.seconds = time.perf_counter() - start
    return result
