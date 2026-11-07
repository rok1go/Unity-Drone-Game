#!/usr/bin/env python3
"""Builds the voxel suburb used by the game (Assets/Resources/SmallSuburbanTown.bytes).

The town is a grid of blocks with roads, sidewalks and lawns, sized for full-size houses.
The playground park and the tree models are reused from Assets/MapSource/Small suburban town.vox;
every building and vehicle is generated here with interiors: rooms, stairs, furniture, windows
and interactive doors. The source .vox is never modified.

    python Tools/suburb_map/build_suburb_map.py [--seed N] [--preview DIR]

Standard library only. Run it again after changing the layout; Unity picks the new file up.
"""
import argparse
import os
import random
import struct
import sys
import time
import zlib

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from voxfile import VoxFile  # noqa: E402
from mapformat import write_map  # noqa: E402
import buildings as B  # noqa: E402
import vehicles as V  # noqa: E402
from furniture import PROP_KINDS, PROP_IDS  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
VOX_PATH = os.path.join(ROOT, 'Assets', 'MapSource', 'Small suburban town.vox')


PARK_MODELS = (0, 1, 2, 3)     # playground, benches, slide: reused as the town park
# Playground model -> block material (1 wood/plastic, 2 metal): the climbing frame and slide are steel.
PARK_MATERIAL = {0: 2, 1: 1, 2: 2, 3: 1}
PARK_PIECES = []               # filled by static_scene: destructible playground structures
TREE_MODELS = (9, 15)          # small tree, big tree: reused (always whole) to green the yards

# Town grid in voxels (1 voxel = 0.165 m): block widths west to east, depths south to north.
ROAD = 20                      # 3.3 m carriageway: two narrow parking/traffic lanes
WALK = 4                       # sidewalk ring around every block
COLUMNS = (150, 160, 150)
ROWS = (128, 116, 128)         # deep enough for a front and a back row of buildings
SETBACK = 6                    # front lawn between the sidewalk and a building (1 m)
BACK_HOUSE = 46                # depth of the smaller houses in the back rows
TOWN_W = sum(COLUMNS) + ROAD * (len(COLUMNS) + 1)
TOWN_D = sum(ROWS) + ROAD * (len(ROWS) + 1)
ORIGIN = (-(TOWN_W // 2), -(TOWN_D // 2))
HOUSE = (64, 52)               # 10.6 m x 8.6 m, two storeys

COLOURS = {
    'asphalt': (52, 54, 58), 'road_line': (226, 222, 206), 'sidewalk': (168, 166, 160), 'curb': (140, 138, 134),
    'grass': (110, 176, 84), 'pole': (70, 74, 80), 'hydrant': (200, 40, 36), 'bin_green': (52, 110, 62),
    'bin_grey': (110, 114, 118), 'cone': (240, 120, 30), 'cone_band': (240, 240, 236), 'pallet': (170, 140, 96),
    'barrel': (40, 90, 150), 'mailbox': (40, 70, 140), 'bench': (140, 100, 60), 'hedge': (66, 128, 58),
    'hedge_dark': (52, 106, 48), 'fence': (214, 206, 190),
    'foundation': (112, 108, 100), 'path': (176, 170, 158), 'concrete': (150, 150, 146),
    'floor_wood': (152, 108, 70), 'floor_tile': (204, 206, 204), 'floor_shop': (214, 200, 176),
    'ceiling': (236, 232, 224), 'roof_flat': (96, 98, 102), 'parapet': (130, 128, 124),
    'int_wall': (228, 220, 202), 'stair': (126, 90, 60), 'brick': (150, 74, 56),
    'wood': (132, 90, 56), 'wood_dark': (86, 58, 38), 'white': (238, 238, 234), 'black': (34, 34, 38),
    'metal': (160, 166, 172), 'mattress': (236, 236, 242), 'pillow': (250, 250, 250), 'cushion': (210, 200, 180),
    'fabric1': (70, 100, 150), 'fabric2': (150, 62, 62), 'fabric3': (86, 128, 90), 'fabric4': (200, 168, 92),
    'lamp': (250, 226, 140), 'screen': (22, 24, 30), 'fridge': (226, 230, 232), 'counter_top': (58, 58, 62),
    'water': (120, 180, 210), 'plant': (62, 140, 64), 'pot': (156, 92, 60), 'crate': (176, 134, 82),
    'crate_dark': (140, 100, 60), 'book1': (180, 60, 60), 'book2': (60, 90, 170), 'book3': (220, 180, 60),
    'book4': (70, 140, 92), 'rug1': (140, 70, 70), 'rug2': (70, 90, 130), 'rug3': (170, 150, 100),
    'seat': (196, 58, 48), 'shop_counter': (200, 160, 60),
    # Vehicles
    'tyre': (28, 28, 30), 'seat_dark': (60, 56, 54), 'tail_light': (200, 30, 30),
    'paint_red': (176, 36, 36), 'paint_blue': (44, 84, 160), 'paint_silver': (176, 180, 186),
    'paint_green': (52, 110, 70), 'paint_white': (230, 232, 230), 'paint_black': (40, 42, 48),
    'paint_orange': (220, 120, 40), 'car_glass': (46, 62, 78), 'grille': (54, 56, 60),
    'mirror': (190, 214, 226), 'glass_block': (170, 206, 222), 'screen_on': (80, 200, 230), 'felt': (40, 120, 70),
    # Facades
    'ext_cream': (222, 208, 178), 'ext_blue': (166, 186, 200), 'ext_rose': (204, 162, 146),
    'ext_sage': (176, 192, 158), 'ext_white': (232, 230, 222), 'ext_grey': (150, 156, 164),
    'ext_brick': (164, 92, 72), 'ext_ochre': (210, 176, 112), 'ext_red': (186, 58, 48),
    'trim_white': (242, 240, 234), 'trim_dark': (82, 84, 90),
    'roof_red': (150, 68, 52), 'roof_grey': (88, 90, 98), 'roof_brown': (116, 78, 58), 'roof_blue': (70, 90, 112),
    'sign_yellow': (238, 196, 40), 'sign_teal': (40, 150, 150),
}

HOUSE_STYLES = [
    dict(ext='ext_cream', trim='trim_white', roof='roof_red'),
    dict(ext='ext_blue', trim='trim_white', roof='roof_grey'),
    dict(ext='ext_rose', trim='trim_white', roof='roof_brown'),
    dict(ext='ext_sage', trim='trim_white', roof='roof_red'),
    dict(ext='ext_white', trim='trim_dark', roof='roof_blue'),
    dict(ext='ext_ochre', trim='trim_white', roof='roof_grey'),
]


def build_palette(vox):
    rgba = bytearray(vox.palette)
    used = vox.used_colors()
    free = [i for i in range(254, 0, -1) if i not in used]
    if len(free) < len(COLOURS):
        raise SystemExit('Not enough free palette entries in the .vox file.')
    index = {}
    for name, rgb in COLOURS.items():
        i = free.pop(0)
        rgba[(i - 1) * 4:(i - 1) * 4 + 4] = bytes((*rgb, 255))
        index[name] = i
    return rgba, index


def blocks():
    """Block rectangles (x0, y0, x1, y1) including their sidewalk ring, indexed [row][column]."""
    out = []
    y = ORIGIN[1] + ROAD
    for depth in ROWS:
        row = []
        x = ORIGIN[0] + ROAD
        for width in COLUMNS:
            row.append((x, y, x + width, y + depth))
            x += width + ROAD
        out.append(row)
        y += depth + ROAD
    return out


def interior(block):
    return block[0] + WALK, block[1] + WALK, block[2] - WALK, block[3] - WALK


def layout(rng, col, density=2):
    """Every generated structure: (canvas, kind, rotation, world min x, world min y).

    Rotation r turns the local front (y = 0 side) to face: 0 south, 1 east, 2 north, 3 west.
    Every block is built up on both sides: a front row facing one street and a back row (smaller houses,
    sheds, garages) facing the other, with narrow passages between them instead of open lawns.
    """
    styles = list(HOUSE_STYLES)
    rng.shuffle(styles)
    style = iter(styles * 8)
    out = []
    grid = blocks()
    hl, hw = HOUSE
    # 2-3 houses, chosen at random, get a basement (two-storey houses only). The first six exist at every
    # density, so the count holds for all of them.
    basements = set(rng.sample(range(6), rng.randint(2, 3)))
    house_count = [0]

    def add(canvas, kind, rot, x, y):
        out.append((canvas, kind, rot, x, y))

    def house(rot, x, y, depth=hw, stories=2):
        cellar = stories == 2 and house_count[0] in basements
        if stories == 2:
            house_count[0] += 1
        add(B.house(rng, col, hl, depth, next(style), stories=stories, basement=cellar), 'house', rot, x, y)

    def shed(rot, x, y, length, width, garage=False):
        add(B.shed(rng, col, length, width, next(style), garage=garage), 'shed', rot, x, y)

    def front_houses(block, south, tall=(False, False)):
        """Two houses on the front of a block, facing its southern (or northern) street."""
        x0, y0, x1, y1 = interior(block)
        y = y0 + SETBACK if south else y1 - SETBACK - hw
        rot = 0 if south else 2
        house(rot, x0 + 3, y, stories=3 if tall[0] else 2)
        house(rot, x1 - 3 - hl, y, stories=3 if tall[1] else 2)

    def back_row(block, south, items):
        """Smaller buildings on the far side of a block, facing the other street. items: (kind, x offset)."""
        # Density 0: open back yards; 1: only sheds and garages there; 2: houses too.
        if density == 0:
            return
        x0, y0, x1, y1 = interior(block)
        rot = 2 if south else 0
        for kind, offset in items:
            x = x0 + offset if offset >= 0 else x1 + offset
            if kind == 'house' and density == 1:
                kind = 'shed'
            if kind == 'house':
                house(rot, x, y1 - SETBACK - BACK_HOUSE if south else y0 + SETBACK, depth=BACK_HOUSE,
                      stories=2 if rng.random() < 0.5 else 3)
            else:
                length, width = (32, 22) if kind == 'garage' else (28, 22)
                shed(rot, x, y1 - SETBACK - width if south else y0 + SETBACK, length, width, kind == 'garage')

    # South row: houses and a restaurant facing the southern street, a second row behind them.
    front_houses(grid[0][0], True, tall=(False, True))
    back_row(grid[0][0], True, [('house', 3), ('shed', -3 - 28)])
    x0, y0, x1, y1 = interior(grid[0][1])
    add(B.shop(rng, col, 70, 52, dict(ext='ext_ochre', trim='trim_dark', sign='sign_teal')), 'shop', 0, x0 + 4, y0 + SETBACK)
    house(0, x1 - 3 - hl, y0 + SETBACK)
    back_row(grid[0][1], True, [('garage', 6), ('house', -3 - hl)])
    front_houses(grid[0][2], True, tall=(True, False))
    back_row(grid[0][2], True, [('shed', 3), ('house', -3 - hl)])
    # Middle row: a big apartment block with garages behind, the park, two houses with sheds behind.
    x0, y0, x1, y1 = interior(grid[1][0])
    add(B.apartment(rng, col, 132, 66, 5, dict(ext='ext_grey', trim='trim_white')), 'apartment', 0, x0 + 5, y0 + SETBACK)
    back_row(grid[1][0], True, [('garage', 6), ('garage', 52), ('garage', 98)])
    front_houses(grid[1][2], True)
    back_row(grid[1][2], True, [('shed', 8), ('garage', -3 - 28 - 8 - 32), ('shed', -3 - 28)])
    # North row: mirrored, the fronts face the northern street.
    front_houses(grid[2][0], False, tall=(True, False))
    back_row(grid[2][0], False, [('house', 3), ('garage', -3 - 32)])
    x0, y0, x1, y1 = interior(grid[2][1])
    add(B.shop(rng, col, 70, 52, dict(ext='ext_red', trim='trim_white', sign='sign_yellow')), 'shop', 2, x0 + 4,
        y1 - SETBACK - 52)
    house(2, x1 - 3 - hl, y1 - SETBACK - hw, stories=3)
    back_row(grid[2][1], False, [('shed', 6), ('house', -3 - hl)])
    x0, y0, x1, y1 = interior(grid[2][2])
    add(B.apartment(rng, col, 120, 48, 4, dict(ext='ext_brick', trim='trim_white')), 'apartment', 2, x0 + 10,
        y1 - SETBACK - 48)
    back_row(grid[2][2], False, [('house', 3), ('house', -3 - hl)])

    # Traffic: cars and vans standing in both lanes of every street segment.
    traffic = random.Random(rng.random())
    paints = ['paint_red', 'paint_blue', 'paint_silver', 'paint_green', 'paint_white', 'paint_black', 'paint_orange']
    kinds = ['sedan', 'sedan', 'hatch', 'suv', 'pickup']

    def make_vehicle():
        if traffic.random() < 0.18:
            return V.van(col, traffic.choice(['paint_white', 'paint_silver']), traffic.choice(['paint_blue', 'paint_orange']))
        return V.car(col, traffic.choice(kinds), traffic.choice(paints))

    def segment(lo, hi, lane_lo, lane_hi, along_x):
        """Up to two vehicles between lo and hi, in opposite lanes, never side by side."""
        count = 0 if traffic.random() < 0.2 else (2 if traffic.random() < 0.35 else 1)
        cursor = lo + traffic.randint(6, 30)
        lane = traffic.randint(0, 1)
        for _ in range(count):
            canvas = make_vehicle()
            if cursor + canvas.L > hi - 6:
                break
            if along_x:
                rot = 0 if lane == 0 else 2
                y = lane_lo if lane == 0 else lane_hi - canvas.W
                add(canvas, 'vehicle', rot, cursor, y)
            else:
                rot = 1 if lane == 1 else 3
                x = lane_hi - canvas.W if lane == 1 else lane_lo
                add(canvas, 'vehicle', rot, x, cursor)
            cursor += canvas.L + traffic.randint(12, 40)
            lane = 1 - lane

    for r in range(len(ROWS) + 1):
        y0 = (grid[r][0][1] if r < len(ROWS) else grid[-1][0][3] + ROAD) - ROAD
        for c in range(len(COLUMNS)):
            segment(grid[0][c][0], grid[0][c][2], y0, y0 + ROAD, True)
    for c in range(len(COLUMNS) + 1):
        x0 = (grid[0][c][0] if c < len(COLUMNS) else grid[0][-1][2] + ROAD) - ROAD
        for r in range(len(ROWS)):
            segment(grid[r][0][1], grid[r][0][3], x0, x0 + ROAD, False)
    return out


def terrain(col):
    """Roads with centre lines, raised sidewalks with curbs, lawns inside every block."""
    static = {}
    x0, y0 = ORIGIN
    for x in range(x0, x0 + TOWN_W):
        for y in range(y0, y0 + TOWN_D):
            static[(x, y, -1)] = col['asphalt']
    for row in blocks():
        for b in row:
            for x in range(b[0], b[2]):
                for y in range(b[1], b[3]):
                    edge = min(x - b[0], b[2] - 1 - x, y - b[1], b[3] - 1 - y)
                    if edge < WALK:
                        static[(x, y, 0)] = col['curb' if edge == 0 else 'sidewalk']
                        static[(x, y, -1)] = col['sidewalk']
                    else:
                        static[(x, y, -1)] = col['grass']
    # Dashed centre lines.
    grid = blocks()
    for r in range(len(ROWS) + 1):
        y = (grid[r][0][1] if r < len(ROWS) else grid[-1][0][3] + ROAD) - ROAD // 2
        for x in range(x0, x0 + TOWN_W):
            if (x // 8) % 2 == 0 and (x, y, -1) in static and static[(x, y, -1)] == col['asphalt']:
                static[(x, y, -1)] = col['road_line']
    for c in range(len(COLUMNS) + 1):
        x = (grid[0][c][0] if c < len(COLUMNS) else grid[0][-1][2] + ROAD) - ROAD // 2
        for y in range(y0, y0 + TOWN_D):
            if (y // 8) % 2 == 0 and (x, y, -1) in static and static[(x, y, -1)] == col['asphalt']:
                static[(x, y, -1)] = col['road_line']
    return static


def park(vox, static, pieces=None):
    """The source playground, moved into the middle block. With `pieces`, every model above the ground
    (swings, slide, benches, railings) is returned there as its own {pos: colour} so it can be built from
    destructible blocks; only ground-level voxels stay static."""
    insts = vox.instances()
    voxels = []
    groups = []
    for inst in insts:
        if inst['model'] in PARK_MODELS:
            vs = list(vox.voxels(inst))
            voxels.extend(vs)
            groups.append((inst['model'], vs))
    min_x = min(v[0] for v in voxels)
    max_x = max(v[0] for v in voxels)
    min_y = min(v[1] for v in voxels)
    max_y = max(v[1] for v in voxels)
    x0, y0, x1, y1 = interior(blocks()[1][1])
    dx = (x0 + x1) // 2 - (min_x + max_x) // 2
    dy = (y0 + y1) // 2 - (min_y + max_y) // 2
    if pieces is None:
        for x, y, z, c in voxels:
            static[(x + dx, y + dy, z)] = c
    else:
        for model, vs in groups:
            piece = {}
            for x, y, z, c in vs:
                if z < 1:
                    static[(x + dx, y + dy, z)] = c
                else:
                    piece[(x + dx, y + dy, z)] = c
            if piece:
                pieces.append((model, piece))
    return (min_x + dx - 2, min_y + dy - 2, max_x + dx + 3, max_y + dy + 3)


PERIMETER_HEIGHT = 14          # 2.3 m: above a standing jump (2 m) and the vault limit (1.35 m)


def perimeter(static, col):
    """A board fence with posts along the outer edge of the town: the edge of the playable area.
    It stands on the outermost voxel of the ring road, so no road or path is narrowed by it."""
    x0, y0 = ORIGIN
    x1, y1 = x0 + TOWN_W - 1, y0 + TOWN_D - 1
    ring = [(x, y0) for x in range(x0, x1 + 1)] + [(x, y1) for x in range(x0, x1 + 1)] +            [(x0, y) for y in range(y0 + 1, y1)] + [(x1, y) for y in range(y0 + 1, y1)]
    for x, y in ring:
        post = (x - x0) % 12 == 0 and (y - y0) % 12 == 0 or (x in (x0, x1) and (y - y0) % 12 == 0) or             (y in (y0, y1) and (x - x0) % 12 == 0)
        for z in range(0, PERIMETER_HEIGHT + (1 if post else 0)):
            static[(x, y, z)] = col['wood_dark' if post or z == 0 else 'fence']


def static_scene(vox, reserved, col, buildings):
    """Terrain, the park, whole trees in the yards, foundations, garden paths, tyre patches and
    street furniture. Returns the static voxels and the movable street props."""
    static = terrain(col)
    perimeter(static, col)
    park_rect = park(vox, static, PARK_PIECES)
    _scatter_trees(vox, vox.instances(), static, reserved + [park_rect], buildings)
    for b in buildings:
        x0, y0, x1, y1 = b.rect
        for (x, y), c in b.ground.items():
            static[(x, y, 0)] = c
        if b.kind == 'vehicle':
            continue
        for x in range(x0, x1):
            for y in range(y0, y1):
                static[(x, y, 0)] = col['foundation']
        # Basements: the terrain is dug out and replaced by the cellar's static shell.
        for x, y in b.dig:
            static.pop((x, y, 0), None)
            static.pop((x, y, -1), None)
        static.update(b.under)
        for ex, ey, dx, dy, width in b.exits:
            _path(static, col, ex, ey, dx, dy, width)
    props = street_details(static, reserved + [park_rect], col, buildings)
    return static, props


def _free(static, reserved, cells):
    return all((x, y, z) not in static and not _in_any(reserved, x, y) for x, y, z in cells)


def _box(x0, y0, z0, x1, y1, z1):
    return [(x, y, z) for x in range(x0, x1) for y in range(y0, y1) for z in range(z0, z1)]


def street_details(static, reserved, col, buildings):
    """Street lights, hydrants, benches, a bus stop, mailboxes, front hedges and back fences
    (all static and standing on the ground), plus movable bins, crates, pallets, barrels and cones."""
    rng = random.Random(23)
    props = []
    taken = set()       # ground cells under movable props: fixed street furniture must not overlap them

    def put(cells, colour):
        for c in cells:
            static[c] = col[colour] if isinstance(colour, str) else colour

    def prop(kind, parts):
        vox = {}
        for cells, colour in parts:
            for c in cells:
                vox[c] = col[colour]
        if _free(static, reserved, vox) and not any(_overlaps(vox, other) for _, other in props):
            props.append((kind, vox))
            taken.update((x, y) for x, y, _ in vox)
            return True
        return False

    grid = blocks()
    for r, row in enumerate(grid):
        for c, b in enumerate(row):
            x0, y0, x1, y1 = b
            sides = (('S', y0 + 1, (x0, x1), (0, -1)), ('N', y1 - 2, (x0, x1), (0, 1)),
                     ('W', x0 + 1, (y0, y1), (-1, 0)), ('E', x1 - 2, (y0, y1), (1, 0)))
            for side, line, (a0, a1), (dx, dy) in sides:
                # Street lights every 45 voxels, the arm reaching over the road.
                for t in range(a0 + 18, a1 - 10, 45):
                    x, y = (t, line) if side in 'SN' else (line, t)
                    if not _free(static, reserved, _box(x, y, 1, x + 1, y + 1, 30)):
                        continue
                    put(_box(x, y, 1, x + 1, y + 1, 30), 'pole')
                    put([(x + dx * i, y + dy * i, 29) for i in range(1, 5)], 'pole')
                    put([(x + dx * 4, y + dy * 4, 28)], 'lamp')
                # One hydrant per side.
                t = rng.randint(a0 + 10, a1 - 10)
                x, y = (t, line) if side in 'SN' else (line, t)
                cells = _box(x, y, 1, x + 1, y + 1, 4)
                if _free(static, reserved, cells):
                    put(cells, 'hydrant')
                    put([(x, y, 4)], 'metal')
    # Benches around the park and a bus stop on its south side.
    px0, py0, px1, py1 = grid[1][1]
    for bx, by in ((px0 + 30, py0 + 2), (px1 - 40, py0 + 2), (px0 + 30, py1 - 4), (px1 - 40, py1 - 4)):
        cells = _box(bx, by, 1, bx + 8, by + 2, 6)
        if _free(static, reserved, cells):
            put(_box(bx, by, 2, bx + 8, by + 2, 3), 'bench')
            put(_box(bx, by + (1 if by < py0 + 3 else 0), 3, bx + 8, by + (2 if by < py0 + 3 else 1), 5), 'bench')
            put(_box(bx, by, 1, bx + 1, by + 2, 2) + _box(bx + 7, by, 1, bx + 8, by + 2, 2), 'pole')
    sx = (px0 + px1) // 2 + 20
    shelter = _box(sx, py0, 1, sx + 14, py0 + 4, 15)
    if _free(static, reserved, shelter):
        put(_box(sx, py0 + 3, 1, sx + 14, py0 + 4, 13), 'glass_block')
        put(_box(sx, py0, 1, sx + 1, py0 + 4, 13) + _box(sx + 13, py0, 1, sx + 14, py0 + 4, 13), 'pole')
        put(_box(sx - 1, py0, 13, sx + 15, py0 + 4, 14), 'pole')
        put(_box(sx + 2, py0 + 2, 3, sx + 12, py0 + 3, 4), 'bench')
    # Houses: mailbox and bins at the end of the path, a hedge along the front lawn, a back fence.
    for b in buildings:
        if b.kind != 'house':
            continue
        for ex, ey, dx, dy, width in b.exits[:1]:
            # Walk out to the sidewalk along the path, then step back onto the lawn.
            x, y = ex, ey
            while (x + dx, y + dy, 0) in static and static[(x + dx, y + dy, 0)] == col['path']:
                x, y = x + dx, y + dy
            sideways = (1, 0) if dx == 0 else (0, 1)
            mx, my = x + sideways[0] * (width // 2 + 3), y + sideways[1] * (width // 2 + 3)
            cells = _box(mx, my, 0, mx + 1, my + 1, 6)
            if _free(static, reserved, cells):
                put(cells[:-1], 'pole')
                put(_box(mx, my, 5, mx + 2, my + 1, 7), 'mailbox')
            bx, by = x - sideways[0] * (width // 2 + 6), y - sideways[1] * (width // 2 + 6)
            for i in range(2):
                ox, oy = bx - sideways[0] * 4 * i, by - sideways[1] * 4 * i
                prop('bin', [(_box(ox, oy, 0, ox + 3, oy + 3, 5), 'bin_green' if i == 0 else 'bin_grey'),
                             (_box(ox, oy, 5, ox + 3, oy + 3, 6), 'black')])
            # Front hedge: along the lawn edge facing the street, broken by the path.
            hx0, hy0, hx1, hy1 = b.rect
            if dy != 0:
                line = y - dy * 1
                for x in range(hx0 - 4, hx1 + 4):
                    if abs(x - ex) <= width // 2 + 2 or rng.random() < 0.08:
                        continue
                    cells = _box(x, line, 0, x + 1, line + 2, 5)
                    if _free(static, reserved, cells) and not any((cx, cy) in taken for cx, cy, _ in cells):
                        put(cells, 'hedge' if (x // 3) % 2 else 'hedge_dark')
    # Shops: crates, pallets and barrels round the back door.
    for b in buildings:
        if b.kind != 'shop':
            continue
        x0, y0, x1, y1 = b.rect
        back_y = y1 + 3 if (b.exits and b.exits[0][3] < 0) else y0 - 8
        for i in range(5):
            x = x0 + 4 + i * 7
            kind = ('crate', 'pallet', 'barrel', 'crate', 'box')[i]
            z = 0
            if kind == 'crate':
                prop('crate', [(_box(x, back_y, z, x + 5, back_y + 5, z + 5), 'crate')])
            elif kind == 'pallet':
                prop('pallet', [(_box(x, back_y, z, x + 6, back_y + 5, z + 1), 'pallet')])
            elif kind == 'barrel':
                prop('barrel', [(_box(x, back_y, z, x + 4, back_y + 4, z + 7), 'barrel'),
                                (_box(x, back_y, z + 3, x + 4, back_y + 4, z + 4), 'black')])
            else:
                prop('box', [(_box(x, back_y, z, x + 4, back_y + 3, z + 3), 'crate_dark')])
    # Road works: a ring of cones on one street.
    cx, cy = grid[1][0][2] + 4, grid[1][0][1] + 30
    for i in range(5):
        x, y = cx + (i % 2) * 6, cy + i * 6
        prop('cone', [(_box(x, y, 0, x + 2, y + 2, 1), 'black'), (_box(x, y, 1, x + 2, y + 2, 4), 'cone'),
                      (_box(x, y, 2, x + 2, y + 2, 3), 'cone_band')])
    return props


def _overlaps(a, b):
    return any(p in b for p in a)


def _scatter_trees(vox, insts, static, reserved, buildings, per_lot=7):
    """Copies of the source trees in free yard space: cover from the drone's camera."""
    rng = random.Random(11)
    models = []
    for m in TREE_MODELS:
        inst = next(i for i in insts if i['model'] == m)
        vs = list(vox.voxels(inst))
        cx = (min(v[0] for v in vs) + max(v[0] for v in vs)) // 2
        cy = (min(v[1] for v in vs) + max(v[1] for v in vs)) // 2
        models.append([(x - cx, y - cy, z, c) for x, y, z, c in vs])
    exits = [(ex, ey) for b in buildings for ex, ey, *_ in b.exits]
    for lot in [interior(b) for row in blocks() for b in row]:
        placed = 0
        spots = [(x, y) for x in range(lot[0] + 8, lot[2] - 7, 4) for y in range(lot[1] + 8, lot[3] - 7, 4)]
        rng.shuffle(spots)
        for px, py in spots:
            if placed >= per_lot:
                break
            tree = models[0] if rng.random() < 0.75 else models[1]
            cells = [(px + x, py + y, z, c) for x, y, z, c in tree]
            if any(_in_any(reserved, x, y) or not (lot[0] + 3 <= x < lot[2] - 3 and lot[1] + 3 <= y < lot[3] - 3)
                   for x, y, _, _ in cells):
                continue
            if any((x, y, z) in static for x, y, z, _ in cells if z >= 0):
                continue
            if any(abs(px - ex) < 12 and abs(py - ey) < 12 for ex, ey in exits):
                continue
            for x, y, z, c in cells:
                static[(x, y, z)] = c
            placed += 1


def _path(static, col, x, y, dx, dy, width):
    """Garden path from a door out to the sidewalk (sidewalk tops are at z = 0)."""
    half = width // 2 + 1
    for step in range(1, 40):
        px, py = x + dx * step, y + dy * step
        cells = [(px + (i if dx == 0 else 0), py + (i if dy == 0 else 0)) for i in range(-half, half)]
        if step > 2 and any((cx, cy, 0) in static and static[(cx, cy, 0)] != col['path'] for cx, cy in cells):
            return
        for cx, cy in cells:
            if (cx, cy, 1) in static:
                continue
            static[(cx, cy, 0)] = col['path']


def _in_any(rects, x, y):
    for r in rects:
        if r[0] <= x < r[2] and r[1] <= y < r[3]:
            return True
    return False


def preview(directory, palette, static, buildings):
    os.makedirs(directory, exist_ok=True)
    x0, y0 = ORIGIN
    x1, y1 = x0 + TOWN_W, y0 + TOWN_D
    w, h = x1 - x0, y1 - y0

    def colour(c):
        return palette[(c - 1) * 4:(c - 1) * 4 + 3]

    def save(name, pixels):
        raw = b''.join(b'\x00' + bytes(pixels[r * w * 3:(r + 1) * w * 3]) for r in range(h))

        def chunk(t, data):
            return struct.pack('>I', len(data)) + t + data + struct.pack('>I', zlib.crc32(t + data) & 0xffffffff)
        with open(os.path.join(directory, name), 'wb') as f:
            f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0)) +
                    chunk(b'IDAT', zlib.compress(raw, 6)) + chunk(b'IEND', b''))

    def put(pixels, x, y, rgb):
        px, py = x - x0, h - 1 - (y - y0)
        if 0 <= px < w and 0 <= py < h:
            pixels[(py * w + px) * 3:(py * w + px) * 3 + 3] = rgb

    top = {}
    for (x, y, z), c in static.items():
        if (x, y) not in top or top[(x, y)][0] < z:
            top[(x, y)] = (z, c)
    for b in buildings:
        for (x, y, z), c in b.vox.items():
            if (x, y) not in top or top[(x, y)][0] < z:
                top[(x, y)] = (z, c)
    pixels = bytearray(w * h * 3)
    for (x, y), (z, c) in top.items():
        put(pixels, x, y, colour(c))
    save('top.png', pixels)
    for level, z in (('ground', 9), ('first', 27), ('third', 63)):
        pixels = bytearray(w * h * 3)
        for (x, y, zz), c in static.items():
            if zz == 0:
                put(pixels, x, y, bytes(v // 3 for v in colour(c)))
        for b in buildings:
            for (x, y, zz), c in b.vox.items():
                if zz == z:
                    put(pixels, x, y, colour(c))
            for wx, wy, wz, ww, wh, plane in b.windows:
                if wz <= z < wz + wh:
                    for i in range(ww):
                        put(pixels, wx if plane == 0 else wx + i, wy + i if plane == 0 else wy, b'\x40\xb0\xff')
            for dx, dy, dz, dw, dh, plane, *_ in b.doors:
                if dz <= z < dz + dh:
                    for i in range(dw):
                        put(pixels, dx if plane == 0 else dx + i, dy + i if plane == 0 else dy, b'\xff\x60\x20')
        save('slice_%s.png' % level, pixels)


def main():
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument('--seed', type=int, default=7)
    parser.add_argument('--preview', help='write top-down and floor-slice PNGs to this directory')
    parser.add_argument('--out', help='output file (one density only); default: all three into Resources')
    parser.add_argument('--density', type=int, choices=(0, 1, 2), help='0 sparse, 1 normal, 2 dense; default: all')
    args = parser.parse_args()
    densities = [args.density] if args.density is not None else [2, 1, 0]
    for density in densities:
        out = args.out or os.path.join(ROOT, 'Assets', 'Resources', 'SmallSuburbanTown' + DENSITY_SUFFIX[density] + '.bytes')
        build(args.seed, density, out, args.preview)


# The game loads SmallSuburbanTown<suffix> for the lobby's density setting.
DENSITY_SUFFIX = {0: '_Sparse', 1: '_Normal', 2: ''}


def build(seed, density, out_path, preview_dir):
    started = time.time()
    vox = VoxFile(VOX_PATH)
    palette, col = build_palette(vox)
    rng = random.Random(seed)
    buildings = []
    for bid, (canvas, kind, rot, x, y) in enumerate(layout(rng, col, density), start=1):
        buildings.append(B.commit(canvas, bid, kind, rot, x, y))
    park_block = blocks()[1][1]
    human_spawn = ((park_block[0] + park_block[2]) / 2, park_block[1] + 2.5, 1.2)
    drone_spawn = ((park_block[0] + park_block[2]) / 2, (park_block[1] + park_block[3]) / 2, 140.0)
    reserved = []
    for b in buildings:
        x0, y0, x1, y1 = b.rect
        pad = 2 if b.kind == 'vehicle' else 3
        reserved.append((x0 - pad, y0 - pad, x1 + pad, y1 + pad))
    for i, a in enumerate(buildings):
        for b in buildings[i + 1:]:
            if a.rect[0] < b.rect[2] and b.rect[0] < a.rect[2] and a.rect[1] < b.rect[3] and b.rect[1] < a.rect[3]:
                raise SystemExit('Buildings %d (%s) and %d (%s) overlap.' % (a.bid, a.kind, b.bid, b.kind))
    del PARK_PIECES[:]
    static, street_props = static_scene(vox, reserved, col, buildings)
    # Playground structures are destructible: each is a structure of blocks grounded on the park.
    for model, piece in PARK_PIECES:
        xs = [p[0] for p in piece]
        ys = [p[1] for p in piece]
        b = B.Building(len(buildings) + 1, 'playground', piece, [], [], (min(xs), min(ys), max(xs) + 1, max(ys) + 1), [])
        b.motion_lights = set()
        b.park_material = PARK_MATERIAL.get(model, 1)
        buildings.append(b)
    # Building voxels win over any static voxel sharing a cell (roof eaves over a tree, etc.).
    for b in buildings:
        for pos in b.vox:
            static.pop(pos, None)
    for b in buildings:
        b.material = {'apartment': 3, 'shop': 2, 'house': 2, 'shed': 2, 'vehicle': 2}.get(b.kind, 1)
        if b.kind == 'playground':
            b.material = b.park_material
    structural = {col[n] for n in col if n.startswith(('ext_', 'trim_', 'roof', 'paint_'))}
    structural |= {col[n] for n in ('parapet', 'ceiling', 'floor_wood', 'floor_tile', 'floor_shop', 'concrete',
                                     'stair', 'brick', 'car_glass', 'grille')}
    props = []
    for kind, vox_cells in [p for b in buildings for p in b.props] + street_props:
        _, mass, health = PROP_KINDS[kind]
        props.append((PROP_IDS[kind], float(mass), health, vox_cells))
    labels = [label for label, _, _ in PROP_KINDS.values()]
    stats = write_map(out_path, palette, static, buildings, human_spawn, drone_spawn, structural, props, labels)
    if preview_dir:
        preview(os.path.join(preview_dir, 'density%d' % density), palette, static, buildings)
    kinds = {}
    for b in buildings:
        kinds[b.kind] = kinds.get(b.kind, 0) + 1
    print('Wrote %s in %.1fs' % (os.path.relpath(out_path, ROOT), time.time() - started))
    print('  structures: %s' % ', '.join('%d %s' % (n, k) for k, n in sorted(kinds.items())))
    print('  blocks %(blocks)d, block faces %(block_faces)d, colliders %(boxes)d, static faces %(static_faces)d' % stats)
    print('  windows %(windows)d, doors %(doors)d, props %(props)d, lights %(lights)d, %(bytes)d bytes' % stats)


if __name__ == '__main__':
    main()
