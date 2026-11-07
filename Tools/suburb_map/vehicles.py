"""Parked vehicles: cars and a pickup as solid cover, and a van whose rear barn doors open
onto a cargo bay you can stand and hide in.

Local frame: x runs from the back (x = 0) to the nose, y across, z up. Bodies sit on round
wheels with ground clearance; the tyres' contact patches are static voxels on the road (z = 0),
returned in `canvas.ground`, because destructible voxels start at z = 1.
"""
from buildings import Canvas


def _cargo(cv, kind, x0, y0, z0, x1, y1, z1, colour):
    """Loose cargo: a physics prop, not part of the body, so it can be shoved, lifted and thrown."""
    c = cv.col[colour]
    cv.props.append((kind, {(x, y, z): c for x in range(x0, x1) for y in range(y0, y1) for z in range(z0, z1)}))

# name: length, width, body top, cabin top (roof layer above it), cabin start, cabin end
CARS = {
    'sedan': (26, 13, 6, 9, 7, 18),
    'hatch': (22, 13, 6, 9, 3, 15),
    'suv': (27, 14, 7, 10, 4, 20),
    'pickup': (30, 14, 7, 10, 14, 23),
}


def _wheel(cv, cx, width):
    """A wheel tucked under the body on each side at x = cx, in a wheel arch cut into the body."""
    rows = ((1, 1), (2, 2), (3, 2), (4, 1))          # (z, half width): a round 4-voxel wheel
    for y0, y1 in ((1, 3), (width - 3, width - 1)):
        cv.clear(cx - 2, y0, 2, cx + 3, y1, 5)       # tight arch: the body skirts the wheel top
        for z, half in rows:
            cv.fill(cx - half, y0, z, cx + half + 1, y1, z + 1, 'tyre')
        hub = y0 if y0 == 1 else y1 - 1               # hub cap on the outer face
        cv.fill(cx, hub, 2, cx + 1, hub + 1, 3, 'metal')
        for x in range(cx - 1, cx + 2):
            for y in range(y0, y1):
                cv.ground[(x, y)] = 'tyre'


def _body(cv, length, width, top, paint):
    # Lower body rides 3 voxels (0.5 m) above the road on its wheels.
    cv.fill(0, 1, 3, length, width - 1, top, paint)
    cv.fill(0, 1, 3, 1, width - 1, 5, 'black')                     # rear bumper
    cv.fill(length - 1, 1, 3, length, width - 1, 5, 'black')       # front bumper
    cv.fill(length - 1, 4, 4, length, width - 4, top, 'grille')
    cv.fill(length - 1, 2, 4, length, 4, 5, 'lamp')
    cv.fill(length - 1, width - 4, 4, length, width - 2, 5, 'lamp')
    cv.fill(0, 2, 4, 1, 4, 5, 'tail_light')
    cv.fill(0, width - 4, 4, 1, width - 2, 5, 'tail_light')


def _cabin(cv, x0, x1, width, z0, z1, paint):
    """Glasshouse: tinted glass all round, painted pillars and roof."""
    cv.fill(x0, 2, z0, x1, width - 2, z1, 'car_glass')
    for x in (x0, x1 - 1, (x0 + x1) // 2):
        for y in (2, width - 3):
            cv.fill(x, y, z0, x + 1, y + 1, z1, paint)
    cv.fill(x0, 2, z1, x1, width - 2, z1 + 1, paint)
    cv.fill(x1 - 1, 0, z0, x1, 1, z0 + 1, paint)                  # mirrors
    cv.fill(x1 - 1, width - 1, z0, x1, width, z0 + 1, paint)


def car(colours, kind, paint):
    length, width, top, cab_top, cab0, cab1 = CARS[kind]
    cv = Canvas(length, width, colours)
    _body(cv, length, width, top, paint)
    _cabin(cv, cab0, cab1, width, top, cab_top, paint)
    if kind == 'pickup':
        # Open load bed behind the cab.
        cv.clear(2, 3, top - 1, cab0 - 1, width - 3, top)
        cv.fill(1, 1, top, cab0, 3, top + 2, paint)
        cv.fill(1, width - 3, top, cab0, width - 1, top + 2, paint)
        cv.fill(0, 1, top, 2, width - 1, top + 2, paint)
        _cargo(cv, 'crate', 4, 4, top - 1, 9, 9, top + 3, 'crate')
    _wheel(cv, 5, width)
    _wheel(cv, length - 6, width)
    return cv


def van(colours, paint, stripe):
    L, W = 36, 16
    cv = Canvas(L, W, colours)
    cargo1, top = L - 10, 15
    # Rear step (x 0..2): the 0.5 m cargo floor is reached in two small steps.
    cv.fill(0, 3, 1, 2, W - 3, 2, 'black')
    for x in range(0, 2):
        for y in range(3, W - 3):
            cv.ground[(x, y)] = 'black'
    # Cargo box: floor, side walls, back wall with barn doors, bulkhead to the cab, roof.
    cv.fill(2, 1, 2, cargo1, W - 1, 3, 'black')
    cv.fill(2, 1, 3, cargo1, 3, top, paint)
    cv.fill(2, W - 3, 3, cargo1, W - 1, top, paint)
    cv.fill(2, 1, 3, 4, W - 1, top, paint)
    cv.fill(cargo1 - 2, 1, 3, cargo1, W - 1, top, paint)
    cv.fill(2, 1, top, cargo1, W - 1, top + 2, paint)
    cv.fill(4, 1, 8, cargo1, 2, 10, stripe)
    cv.fill(4, W - 2, 8, cargo1, W - 1, 10, stripe)
    cv.doorway(0, 2, 2, 3, door=True, hinge=0, width=6, height=12, outward=(-1, 0), colour=paint)
    cv.doorway(0, 2, 8, 3, door=True, hinge=1, width=6, height=12, outward=(-1, 0), colour=paint)
    # Cab and bonnet.
    cv.fill(cargo1, 1, 2, L, W - 1, 7, paint)
    cv.fill(L - 1, 1, 2, L, W - 1, 4, 'black')
    cv.fill(L - 1, 4, 4, L, W - 4, 7, 'grille')
    cv.fill(L - 1, 2, 4, L, 4, 5, 'lamp')
    cv.fill(L - 1, W - 4, 4, L, W - 2, 5, 'lamp')
    _cabin(cv, cargo1, L - 3, W, 7, 12, paint)
    # Cargo: crates along one wall and a rack by the bulkhead leave a hiding corner.
    _cargo(cv, 'crate', 6, W - 7, 3, 11, W - 3, 8, 'crate')
    _cargo(cv, 'box', 7, W - 6, 8, 11, W - 3, 11, 'crate_dark')
    _cargo(cv, 'crate', 13, W - 7, 3, 18, W - 3, 8, 'crate')
    # Shelving bolted to the bulkhead, a box on its middle shelf.
    cv.fill(cargo1 - 6, 3, 3, cargo1 - 2, 6, 4, 'metal')
    cv.fill(cargo1 - 6, 3, 8, cargo1 - 2, 6, 9, 'metal')
    cv.fill(cargo1 - 6, 3, 4, cargo1 - 5, 6, 14, 'metal')
    cv.fill(cargo1 - 3, 3, 4, cargo1 - 2, 6, 14, 'metal')
    _cargo(cv, 'box', cargo1 - 5, 3, 9, cargo1 - 3, 6, 12, 'crate_dark')
    _wheel(cv, 7, W)
    _wheel(cv, L - 6, W)
    return cv
