"""Writer for the binary map that SuburbanTrainingMap.cs loads.

Layout (little endian):
  palette        256 x RGBA (texel i is MagicaVoxel colour index i + 1)
  static mesh    int32 faceCount, faces
  blocks         int32 count, per block:
                   int16 buildingId, blockX, blockY(horizontal), blockZ(vertical), material
                   (1 light: wood, plaster, furniture; 2 brick or sheet metal; 3 reinforced concrete)
                   int32 faceCount, faces
                   int32 boxCount, boxes (int16 x, y, z, sizeX, sizeY, sizeZ)
  windows        int32 count, per pane int16 buildingId, x, y, z, width, height, plane (0 = perpendicular to X)
  doors          int32 count, per door  int16 buildingId, x, y, z, width, height, plane, hinge (0 = at start),
                   swing (0 = away from the user, +1/-1 = always that way), colour (palette index, 0 = wood)
  spawns         float32 x, y, z of the human spawn, then of the drone spawn (voxel coordinates)
  prop kinds     int32 count, per kind: uint8 byte length + UTF-8 label
  props          int32 count, per prop: int16 kind, float32 mass (kg), int16 health,
                   int16 originX, originY, originZ (min corner), int32 faceCount, faces, int32 boxCount, boxes
  lights         int32 count, per room: int16 buildingId, float32 lamp x, y, z, switch x, y, z,
                   int8 normal x, y (the switch's wall faces this way)
A face is: uint8 axis, int8 sign, int16 plane, u, v, width, height, uint8 colour.
Axis 0: u = Y, v = Z. Axis 1: u = Z, v = X. Axis 2: u = X, v = Y. All in voxel units.
Building voxels start at z = 1; block (bx, by, bz) covers x in [6bx, 6bx+6), y likewise, z in [6bz+1, 6bz+7).
"""
import struct
from collections import defaultdict

BLOCK = 6
DIRS = ((1, 0, 0, 0, 1), (-1, 0, 0, 0, -1), (0, 1, 0, 1, 1), (0, -1, 0, 1, -1), (0, 0, 1, 2, 1), (0, 0, -1, 2, -1))


def block_key(x, y, z):
    return x // BLOCK, y // BLOCK, (z - 1) // BLOCK


def exposed_faces(voxels, skip_bottom_below=None):
    """Greedy-merged faces of `voxels` (dict pos -> colour), culled only against itself."""
    planes = defaultdict(dict)
    for (x, y, z), c in voxels.items():
        for dx, dy, dz, axis, sign in DIRS:
            if (x + dx, y + dy, z + dz) in voxels:
                continue
            if skip_bottom_below is not None and dz < 0 and z <= skip_bottom_below:
                continue
            if axis == 0:
                planes[(0, sign, x + (1 if sign > 0 else 0))][(y, z)] = c
            elif axis == 1:
                planes[(1, sign, y + (1 if sign > 0 else 0))][(z, x)] = c
            else:
                planes[(2, sign, z + (1 if sign > 0 else 0))][(x, y)] = c
    faces = []
    for (axis, sign, plane), cells in planes.items():
        for u, v, w, h, c in greedy_rects(cells):
            faces.append((axis, sign, plane, u, v, w, h, c))
    return faces


def greedy_rects(cells):
    used = set()
    out = []
    for (u, v) in sorted(cells, key=lambda k: (k[1], k[0])):
        if (u, v) in used:
            continue
        c = cells[(u, v)]
        w = 1
        while (u + w, v) in cells and (u + w, v) not in used and cells[(u + w, v)] == c:
            w += 1
        h = 1
        while all((u + i, v + h) in cells and (u + i, v + h) not in used and cells[(u + i, v + h)] == c
                  for i in range(w)):
            h += 1
        for i in range(w):
            for j in range(h):
                used.add((u + i, v + j))
        out.append((u, v, w, h, c))
    return out


def greedy_boxes(cells):
    remaining = set(cells)
    boxes = []
    for (x, y, z) in sorted(cells, key=lambda k: (k[2], k[1], k[0])):
        if (x, y, z) not in remaining:
            continue
        x1 = x + 1
        while (x1, y, z) in remaining:
            x1 += 1
        y1 = y + 1
        while all((i, y1, z) in remaining for i in range(x, x1)):
            y1 += 1
        z1 = z + 1
        while all((i, j, z1) in remaining for i in range(x, x1) for j in range(y, y1)):
            z1 += 1
        for i in range(x, x1):
            for j in range(y, y1):
                for k in range(z, z1):
                    remaining.discard((i, j, k))
        boxes.append((x, y, z, x1 - x, y1 - y, z1 - z))
    return boxes


def _faces(out, faces):
    out += struct.pack('<i', len(faces))
    for axis, sign, plane, u, v, w, h, c in faces:
        out += struct.pack('<BbhhhhhB', axis, sign, plane, u, v, w, h, c)


def write_map(path, palette, static_voxels, buildings, human_spawn, drone_spawn, structural, props, labels):
    """`structural`: palette indices of load-bearing shell colours; a block containing any of them
    gets its building's material, other blocks (partitions, furniture) are light.
    `props`: list of (kind id, mass, health, {pos: colour})."""
    out = bytearray(palette)
    static_faces = exposed_faces(static_voxels, skip_bottom_below=min(z for _, _, z in static_voxels)) if static_voxels else []
    _faces(out, static_faces)
    stats = dict(static_faces=len(static_faces), blocks=0, block_faces=0, boxes=0, windows=0, doors=0)
    block_out = bytearray()
    for b in buildings:
        groups = defaultdict(dict)
        for pos, c in b.vox.items():
            if pos[2] < 1:
                raise ValueError('Building %d has a voxel below z=1: %r' % (b.bid, pos))
            groups[block_key(*pos)][pos] = c
        for (bx, by, bz) in sorted(groups):
            cells = groups[(bx, by, bz)]
            faces = exposed_faces(cells)
            boxes = greedy_boxes(cells)
            # Playground structures have one material throughout (no load-bearing shell colours).
            material = b.material if b.kind == 'playground' or any(c in structural for c in cells.values()) else 1
            block_out += struct.pack('<hhhhh', b.bid, bx, by, bz, material)
            _faces(block_out, faces)
            block_out += struct.pack('<i', len(boxes))
            for box in boxes:
                block_out += struct.pack('<6h', *box)
            stats['blocks'] += 1
            stats['block_faces'] += len(faces)
            stats['boxes'] += len(boxes)
    out += struct.pack('<i', stats['blocks'])
    out += block_out
    windows = [(b.bid,) + w for b in buildings for w in b.windows]
    out += struct.pack('<i', len(windows))
    for w in windows:
        out += struct.pack('<7h', *w)
    doors = [(b.bid,) + d for b in buildings for d in b.doors]
    out += struct.pack('<i', len(doors))
    for d in doors:
        out += struct.pack('<10h', *d)
    out += struct.pack('<6f', *human_spawn, *drone_spawn)
    out += struct.pack('<i', len(labels))
    for label in labels:
        data = label.encode('utf-8')
        assert len(data) < 128, label   # BinaryReader.ReadString: one-byte 7-bit length prefix
        out += struct.pack('<B', len(data)) + data
    out += struct.pack('<i', len(props))
    for kind, mass, health, voxels in props:
        ox = min(p[0] for p in voxels)
        oy = min(p[1] for p in voxels)
        oz = min(p[2] for p in voxels)
        out += struct.pack('<hfhhhh', kind, mass, health, ox, oy, oz)
        _faces(out, exposed_faces(voxels))
        boxes = greedy_boxes(set(voxels))
        out += struct.pack('<i', len(boxes))
        for box in boxes:
            out += struct.pack('<6h', *box)
    stats['props'] = len(props)
    # Room lights: lamp position, switch position (all voxel coordinates) and the switch's wall normal.
    lights = [(b.bid,) + lamp + switch + normal for b in buildings for lamp, switch, normal in b.lights]
    out += struct.pack('<i', len(lights))
    for bid, lx, ly, lz, sx, sy, sz, nx, ny in lights:
        out += struct.pack('<h6f2b', bid, lx, ly, lz, sx, sy, sz, nx, ny)
    stats['lights'] = len(lights)
    sensors, offset = [], 0
    for b in buildings:
        sensors.extend(offset + i for i in sorted(b.motion_lights))
        offset += len(b.lights)
    out += struct.pack('<i', len(sensors))
    for index in sensors:
        out += struct.pack('<i', index)
    stats['windows'], stats['doors'] = len(windows), len(doors)
    stats['bytes'] = len(out)
    stats['data'] = bytes(out)
    if path:
        with open(path, 'wb') as f:
            f.write(out)
    return stats
