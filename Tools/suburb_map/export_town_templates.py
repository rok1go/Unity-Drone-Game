"""Exports every building, vehicle, playground piece and street decoration the town generator can place as
reusable templates: Assets/Resources/TownTemplates.bytes. The game lays out a new town from them for every
round (TownGenerator.cs), from a seed the host shares, so all players build the same map.

Each template is the existing builder output committed at rotation 0 with its footprint's min corner at
the origin, so its front (local y = 0 side) faces south; the generator rotates it to face its street.

File layout (little endian):
  int32 magic 'TWN1', palette 256 x RGBA
  int32 colourCount, per colour: uint8 name length + UTF-8 name, uint8 palette texel index
  int32 templateCount, per template:
    uint8 kind length + UTF-8 kind, int16 length (x), width (y), height (z)
    int32 exitCount, per exit int16 x, y, dx, dy, width
    int32 groundCount, per cell int16 x, y, uint8 texel      (painted on the terrain at z = 0)
    int32 digCount, per cell int16 x, y                       (terrain removed: basement stairs and walls)
    int32 byteCount, then a map blob in the SuburbanTrainingMap format with one building (id 1),
    its static (cellar) voxels and its props.
"""
import os
import random
import struct
import sys

sys.path.insert(0, os.path.dirname(__file__))
from voxfile import VoxFile  # noqa: E402
from mapformat import write_map  # noqa: E402
import buildings as B  # noqa: E402
import vehicles as V  # noqa: E402
from furniture import PROP_KINDS, PROP_IDS  # noqa: E402
import build_suburb_map as M  # noqa: E402

OUT = os.path.join(M.ROOT, 'Assets', 'Resources', 'TownTemplates.bytes')
MATERIAL = {'apartment': 3, 'shop': 2, 'house': 2, 'shed': 2, 'garage': 2, 'vehicle': 2}
# Colours the generator paints the streets and lots with.
TERRAIN_COLOURS = ('asphalt', 'road_line', 'sidewalk', 'curb', 'grass', 'path', 'foundation', 'fence', 'wood_dark',
                   'hedge', 'hedge_dark', 'pole', 'lamp')


def main():
    vox = VoxFile(M.VOX_PATH)
    palette, col = M.build_palette(vox)
    structural = {col[n] for n in col if n.startswith(('ext_', 'trim_', 'roof', 'paint_'))}
    structural |= {col[n] for n in ('parapet', 'ceiling', 'floor_wood', 'floor_tile', 'floor_shop', 'concrete',
                                     'stair', 'brick', 'car_glass', 'grille')}
    labels = [label for label, _, _ in PROP_KINDS.values()]
    rng = random.Random(7)
    templates = []

    def building(kind, canvas):
        b = B.commit(canvas, 1, 'vehicle' if kind == 'vehicle' else ('shed' if kind == 'garage' else kind), 0, 0, 0)
        b.material = MATERIAL.get(kind, 1)
        props = [(PROP_IDS[k], float(PROP_KINDS[k][1]), PROP_KINDS[k][2], cells) for k, cells in b.props]
        blob = write_map(None, palette, dict(b.under), [b], (0, 0, 0), (0, 0, 0), structural, props, labels)['data']
        L, W = b.rect[2] - b.rect[0], b.rect[3] - b.rect[1]
        height = max(z for _, _, z in b.vox) + 1
        templates.append((kind, L, W, height, b.exits, b.ground, b.dig, blob))

    def piece(kind, voxels, material=1, destructible=True):
        """A free-standing structure from raw voxels (playground parts as blocks; decor as static)."""
        x0 = min(p[0] for p in voxels); y0 = min(p[1] for p in voxels); z0 = min(p[2] for p in voxels)
        shift = 1 - z0 if destructible else 1 - z0
        cells = {(x - x0, y - y0, z + shift): c for (x, y, z), c in voxels.items()}
        L = max(p[0] for p in cells) + 1; W = max(p[1] for p in cells) + 1; H = max(p[2] for p in cells) + 1
        if destructible:
            b = B.Building(1, 'playground', cells, [], [], (0, 0, L, W), [])
            b.motion_lights = set()
            b.material = material
            blob = write_map(None, palette, {}, [b], (0, 0, 0), (0, 0, 0), structural, [], labels)['data']
        else:
            blob = write_map(None, palette, cells, [], (0, 0, 0), (0, 0, 0), structural, [], labels)['data']
        templates.append((kind, L, W, H, [], {}, set(), blob))

    styles = list(M.HOUSE_STYLES)
    hl, hw = M.HOUSE
    for i, style in enumerate(styles):
        building('house', B.house(rng, col, hl, hw, style, stories=2, basement=i % 3 == 0))
        building('house', B.house(rng, col, hl, hw, style, stories=3))
        building('house_small', B.house(rng, col, hl, M.BACK_HOUSE, style, stories=2 if i % 2 else 3))
    for i, style in enumerate(styles[:4]):
        building('shed', B.shed(rng, col, 28, 22, style))
        building('garage', B.shed(rng, col, 32, 22, style, garage=True))
    shop_styles = [dict(ext='ext_ochre', trim='trim_dark', sign='sign_teal'),
                   dict(ext='ext_red', trim='trim_white', sign='sign_yellow'),
                   dict(ext='ext_sage', trim='trim_white', sign='sign_teal')]
    for style in shop_styles:
        building('shop', B.shop(rng, col, 70, 52, style))
    for length, width, stories, style in ((132, 66, 5, dict(ext='ext_grey', trim='trim_white')),
                                          (120, 48, 4, dict(ext='ext_brick', trim='trim_white')),
                                          (104, 54, 6, dict(ext='ext_cream', trim='trim_dark'))):
        building('apartment', B.apartment(rng, col, length, width, stories, style))
    paints = ['paint_red', 'paint_blue', 'paint_silver', 'paint_green', 'paint_white', 'paint_black', 'paint_orange']
    for i, kind in enumerate(['sedan', 'hatch', 'suv', 'pickup', 'sedan', 'hatch', 'suv', 'sedan']):
        building('vehicle', V.car(col, kind, paints[i % len(paints)]))
    building('vehicle', V.van(col, 'paint_white', 'paint_blue'))
    building('vehicle', V.van(col, 'paint_silver', 'paint_orange'))

    # Playground structures from the source scene: destructible, steel frames and wooden benches.
    pieces = []
    M.park(vox, {}, pieces)
    for model, cells in pieces:
        piece('playground', cells, M.PARK_MATERIAL.get(model, 1))

    # Street decoration (static): street light with its arm towards +y, hydrant, bench, mailbox, trees.
    def box(x0, y0, z0, x1, y1, z1, colour):
        return {(x, y, z): col[colour] for x in range(x0, x1) for y in range(y0, y1) for z in range(z0, z1)}
    light = box(0, 0, 1, 1, 1, 30, 'pole')
    light.update({(0, i, 29): col['pole'] for i in range(1, 5)})
    light[(0, 4, 28)] = col['lamp']
    piece('street_light', light, destructible=False)
    hydrant = box(0, 0, 1, 1, 1, 4, 'hydrant'); hydrant[(0, 0, 4)] = col['metal']
    piece('hydrant', hydrant, destructible=False)
    bench = box(0, 0, 2, 8, 2, 3, 'bench'); bench.update(box(0, 1, 3, 8, 2, 5, 'bench'))
    bench.update(box(0, 0, 1, 1, 2, 2, 'pole')); bench.update(box(7, 0, 1, 8, 2, 2, 'pole'))
    piece('bench', bench, destructible=False)
    for m in M.TREE_MODELS:
        inst = next(i for i in vox.instances() if i['model'] == m)
        piece('tree', {(x, y, z): c for x, y, z, c in vox.voxels(inst)}, destructible=False)

    out = bytearray(b'TWN1') + bytearray(palette)
    names = [n for n in TERRAIN_COLOURS if n in col]
    out += struct.pack('<i', len(names))
    for n in names:
        data = n.encode('utf-8')
        out += struct.pack('<B', len(data)) + data + struct.pack('<B', col[n] - 1)
    out += struct.pack('<i', len(templates))
    for kind, L, W, H, exits, ground, dig, blob in templates:
        data = kind.encode('utf-8')
        out += struct.pack('<B', len(data)) + data + struct.pack('<hhh', L, W, H)
        out += struct.pack('<i', len(exits))
        for ex, ey, dx, dy, w in exits:
            out += struct.pack('<5h', ex, ey, dx, dy, w)
        out += struct.pack('<i', len(ground))
        for (x, y), c in ground.items():
            out += struct.pack('<hhB', x, y, c - 1)
        out += struct.pack('<i', len(dig))
        for x, y in sorted(dig):
            out += struct.pack('<hh', x, y)
        out += struct.pack('<i', len(blob)) + blob
    with open(OUT, 'wb') as f:
        f.write(out)
    kinds = {}
    for t in templates:
        kinds[t[0]] = kinds.get(t[0], 0) + 1
    print('Wrote %s: %d templates (%s), %d bytes' % (os.path.relpath(OUT, M.ROOT), len(templates),
                                                    ', '.join('%d %s' % (n, k) for k, n in sorted(kinds.items())), len(out)))


if __name__ == '__main__':
    main()
