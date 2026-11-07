"""Procedural buildings with interiors, in a local frame, committed to world coordinates.

Local frame: x runs along the building length L, y across its width W, z up from z = 1.
The front (main entrance) is the y = 0 side. Walls and slabs are two voxels thick
(0.33 m); a storey is 18 voxels (2 slab + 16 clear, 2.64 m). One voxel is 0.165 m.
"""
from furniture import Furnisher

WALL = 2
SLAB = 2
BASE = 1
STORY = 20
CLEAR = STORY - SLAB
DOOR_W, DOOR_H = 6, 14
STEP_RISE, STEP_RUN, STEPS = 2, 3, 10
RUN = STEP_RUN * STEPS            # 30 voxels of stairs per storey
CORRIDOR = 10                     # apartment corridor width (1.65 m)


class Canvas:
    def __init__(self, length, width, colours):
        self.L, self.W = length, width
        self.col = colours
        self.vox = {}
        self.windows = []    # (x, y, z, w, h, plane, side)  plane 0: perpendicular to x
        self.doors = []      # (x, y, z, w, h, plane, hinge)
        self.keep = {}       # floor z -> {cell: zone}
        self.low = {}        # floor z -> cells in front of windows
        self.blocked = {}    # floor z -> cells furniture must avoid (stairs, holes)
        self.exits = []      # (x, y, outward dx, dy) exterior doors, for garden paths
        self.ground = {}     # (x, y) -> colour: static voxels at z = 0 (tyre contact patches)
        self.props = []      # (kind, {pos: colour}) movable physics props
        self.under = {}      # (x, y, z) -> colour: static voxels at z <= 0 (basements; indestructible)
        self.openings = []   # (axis, s, p, width, zf): every doorway, for placing light switches
        self.lights = []     # ((lamp x, y, z), (switch x, y, z), (normal dx, dy)) one per room, continuous coords
        self.motion_lights = set()  # only shared corridors and landings of apartment blocks
        self.dig = set()     # (x, y) terrain cells removed down to the basement floor
        self.zone = 0

    # ---- primitives
    def fill(self, x0, y0, z0, x1, y1, z1, colour):
        c = self.col[colour] if isinstance(colour, str) else colour
        v = self.vox
        for x in range(x0, x1):
            for y in range(y0, y1):
                for z in range(z0, z1):
                    v[(x, y, z)] = c

    def clear(self, x0, y0, z0, x1, y1, z1):
        v = self.vox
        for x in range(x0, x1):
            for y in range(y0, y1):
                for z in range(z0, z1):
                    v.pop((x, y, z), None)

    def recolour(self, x0, y0, z0, x1, y1, z1, colour):
        c = self.col[colour]
        v = self.vox
        for x in range(x0, x1):
            for y in range(y0, y1):
                for z in range(z0, z1):
                    if (x, y, z) in v:
                        v[(x, y, z)] = c

    def ring(self, x0, y0, x1, y1, z0, z1, colour, t=WALL):
        self.fill(x0, y0, z0, x1, y0 + t, z1, colour)
        self.fill(x0, y1 - t, z0, x1, y1, z1, colour)
        self.fill(x0, y0, z0, x0 + t, y1, z1, colour)
        self.fill(x1 - t, y0, z0, x1, y1, z1, colour)

    def slab(self, x0, y0, x1, y1, z0, floor, ceiling='ceiling'):
        self.fill(x0, y0, z0, x1, y1, z0 + 1, ceiling)
        self.fill(x0, y0, z0 + 1, x1, y1, z0 + 2, floor)

    def keep_zone(self, zf, x0, y0, x1, y1):
        self.zone += 1
        zone = self.keep.setdefault(zf, {})
        for x in range(x0, x1):
            for y in range(y0, y1):
                zone[(x, y)] = self.zone

    def block(self, zf, x0, y0, x1, y1):
        cells = self.blocked.setdefault(zf, set())
        for x in range(x0, x1):
            for y in range(y0, y1):
                cells.add((x, y))

    # ---- openings
    def doorway(self, axis, s, p, zf, door=False, hinge=0, width=DOOR_W, height=DOOR_H, outward=None, colour=None):
        """Opening in a 2-thick wall. axis 0: wall occupies x in [s, s+2); the opening spans y [p, p+width).
        `outward` (dx, dy) makes the door always swing to that side (vehicle doors); `colour` tints its panel."""
        tint = self.col[colour] if colour else 0
        self.openings.append((axis, s, p, width, zf))
        if axis == 0:
            self.clear(s, p, zf, s + WALL, p + width, zf + height)
            self.keep_zone(zf, s - 5, p - 1, s, p + width + 1)
            self.zone -= 1  # Both sides are one zone.
            self.keep_zone(zf, s + WALL, p - 1, s + WALL + 5, p + width + 1)
            if door:
                self.doors.append((s + 1, p, zf, width, height, 0, hinge, outward, tint))
        else:
            self.clear(p, s, zf, p + width, s + WALL, zf + height)
            self.keep_zone(zf, p - 1, s - 5, p + width + 1, s)
            self.zone -= 1
            self.keep_zone(zf, p - 1, s + WALL, p + width + 1, s + WALL + 5)
            if door:
                self.doors.append((p, s + 1, zf, width, height, 1, hinge, outward, tint))

    def exterior_door(self, side, p, zf, door=True, width=DOOR_W, height=DOOR_H):
        s = {'S': 0, 'N': self.W - WALL, 'W': 0, 'E': self.L - WALL}[side]
        axis = 1 if side in 'SN' else 0
        self.doorway(axis, s, p, zf, door, hinge=p % 2, width=width, height=height)
        mid = p + width // 2
        out = {'S': (mid, 0, 0, -1), 'N': (mid, self.W - 1, 0, 1), 'W': (0, mid, -1, 0), 'E': (self.L - 1, mid, 1, 0)}
        if zf == BASE + SLAB:
            self.exits.append(out[side] + (width,))

    def window(self, side, p, zf, width, height, sill):
        z = zf + sill
        if side in 'SN':
            s = 0 if side == 'S' else self.W - WALL
            self.clear(p, s, z, p + width, s + WALL, z + height)
            self.windows.append((p, s + 1, z, width, height, 1, side))
            inner = (p, WALL, p + width, WALL + 4) if side == 'S' else (p, self.W - WALL - 4, p + width, self.W - WALL)
        else:
            s = 0 if side == 'W' else self.L - WALL
            self.clear(s, p, z, s + WALL, p + width, z + height)
            self.windows.append((s + 1, p, z, width, height, 0, side))
            inner = (WALL, p, WALL + 4, p + width) if side == 'W' else (self.L - WALL - 4, p, self.L - WALL, p + width)
        low = self.low.setdefault(zf, set())
        for x in range(inner[0], inner[2]):
            for y in range(inner[1], inner[3]):
                low.add((x, y))

    def windows_along(self, side, zf, blocked, width, height, sill, period, rng, start=None, end=None):
        """Evenly spaced windows on one exterior wall, skipping blocked intervals."""
        length = self.L if side in 'SN' else self.W
        lo = WALL + 1 if start is None else start
        hi = length - WALL - 1 if end is None else end
        free, run = [], None
        for i in range(lo, hi):
            ok = not any(a <= i < b for a, b in blocked)
            if ok and run is None:
                run = i
            if (not ok or i == hi - 1) and run is not None:
                stop = i + 1 if ok else i
                free.append((run, stop))
                run = None
        for a, b in free:
            span = b - a
            if span < width + 2:
                continue
            n = max(1, span // period)
            step = span / n
            for k in range(n):
                p = int(a + step * k + (step - width) / 2)
                if p >= a and p + width <= b:
                    self.window(side, p, zf, width, height, sill)

    # ---- interior walls and rooms
    def wall(self, axis, s, a0, a1, zf, colour='int_wall', height=CLEAR):
        if axis == 0:
            self.fill(s, a0, zf, s + WALL, a1, zf + height, colour)
        else:
            self.fill(a0, s, zf, a1, s + WALL, zf + height, colour)

    def furnish(self, rect, zf, room_type, rng):
        keep = self.keep.get(zf, {})
        low = self.low.get(zf, set())
        f = Furnisher(self, rect, zf, keep, low, rng, self.col)
        f.used |= {c for c in self.blocked.get(zf, ()) if f.inside(*c)}
        f.furnish(room_type)
        self.room_light(rect, zf)

    def room_light(self, rect, zf, motion=False):
        """A ceiling lamp in the middle of the room and its switch on the wall beside a doorway into it."""
        x0, y0, x1, y1 = rect
        cx, cy = (x0 + x1) / 2.0, (y0 + y1) / 2.0
        top = zf
        while top < zf + 40 and (int(cx), int(cy), top) not in self.vox:
            top += 1
        if top >= zf + 40:
            return                      # open to the sky: no ceiling to hang a lamp from
        lamp = (cx, cy, top - 0.6)
        h = zf + 8.5                    # switch at 1.4 m
        switch = None
        for axis, s, p, width, dzf in self.openings:
            if dzf != zf:
                continue
            lo, hi = (y0, y1) if axis == 0 else (x0, x1)
            if not (lo <= p and p + width <= hi):
                continue
            if axis == 0 and s == x1:
                surface, normal = x1, (-1, 0)
            elif axis == 0 and s + WALL == x0:
                surface, normal = x0, (1, 0)
            elif axis == 1 and s == y1:
                surface, normal = y1, (0, -1)
            elif axis == 1 and s + WALL == y0:
                surface, normal = y0, (0, 1)
            else:
                continue
            along = p - 1.5 if p - 2 >= lo + 1 else p + width + 1.5
            if not (lo + 1 <= along <= hi - 1):
                continue
            switch = ((surface, along) if axis == 0 else (along, surface)) + (h,)
            break
        if switch is None:
            # No doorway on this level (a cellar): the switch goes on the room's west wall.
            switch, normal = (x0, cy, h), (1, 0)
        if motion:
            self.motion_lights.add(len(self.lights))
        self.lights.append((lamp, switch, normal))


def bsp(rect, rng, min_side, max_area, avoid=()):
    """Split a rectangle into rooms. Returns (rooms, walls); wall = (axis, s, a0, a1)."""
    x0, y0, x1, y1 = rect
    w, d = x1 - x0, y1 - y0
    if w * d <= max_area or (w * d <= max_area * 1.7 and rng.random() < 0.35):
        return [rect], []
    for axis in ((0, 1) if w >= d else (1, 0)):
        lo, hi = (x0, x1) if axis == 0 else (y0, y1)
        cands = [s for s in range(lo + min_side, hi - min_side - WALL + 1)
                 if not any(_crosses(r, axis, s, rect) for r in avoid)]
        if cands:
            mid = (lo + hi - WALL) / 2
            cands.sort(key=lambda s: abs(s - mid))
            s = rng.choice(cands[:max(1, len(cands) // 3)])
            if axis == 0:
                a, b, wall = (x0, y0, s, y1), (s + WALL, y0, x1, y1), (0, s, y0, y1)
            else:
                a, b, wall = (x0, y0, x1, s), (x0, s + WALL, x1, y1), (1, s, x0, x1)
            ra, wa = bsp(a, rng, min_side, max_area, avoid)
            rb, wb = bsp(b, rng, min_side, max_area, avoid)
            return ra + rb, [wall] + wa + wb
    return [rect], []


def _crosses(r, axis, s, rect):
    if r[0] >= rect[2] or r[2] <= rect[0] or r[1] >= rect[3] or r[3] <= rect[1]:
        return False
    lo, hi = (r[0], r[2]) if axis == 0 else (r[1], r[3])
    return s < hi and s + WALL > lo


def doorway_spot(wall, walls, avoid, rng, width=DOOR_W):
    """A doorway position on `wall` away from wall junctions and reserved areas."""
    axis, s, a0, a1 = wall
    blocked = []
    for other in walls:
        o_axis, o_s, b0, b1 = other
        if o_axis == axis:
            continue
        if a0 <= o_s < a1 and (b1 == s or b0 == s + WALL or b0 <= s < b1):
            blocked.append((o_s - 2, o_s + WALL + 2))
    for r in avoid:
        lo, hi = (r[0], r[2]) if axis == 0 else (r[1], r[3])
        if lo - 1 <= s + WALL and s <= hi + 1:
            blocked.append((r[1] - 1, r[3] + 1) if axis == 0 else (r[0] - 1, r[2] + 1))
    options = [p for p in range(a0 + 1, a1 - width)
               if not any(p < b and p + width > a for a, b in blocked)]
    return rng.choice(options) if options else None


def room_at(rooms, x, y):
    for r in rooms:
        if r[0] <= x < r[2] and r[1] <= y < r[3]:
            return r
    return None


PRIVATE = ('bedroom', 'bathroom', 'closet', 'restroom', 'storage', 'study')


def connect_rooms(cv, rooms, walls, types, zf, rng, avoid=(), door_chance=0.8):
    for wall in walls:
        p = doorway_spot(wall, walls, avoid, rng)
        if p is None:
            continue
        axis, s = wall[0], wall[1]
        if axis == 0:
            a, b = room_at(rooms, s - 1, p + 3), room_at(rooms, s + WALL, p + 3)
        else:
            a, b = room_at(rooms, p + 3, s - 1), room_at(rooms, p + 3, s + WALL)
        private = any(types.get(r) in PRIVATE for r in (a, b) if r)
        cv.doorway(axis, s, p, zf, door=private and rng.random() < door_chance, hinge=rng.randint(0, 1))


def assign_types(rooms, order, bathroom=True, closet_area=100):
    """Largest rooms take `order`; with 3+ rooms the smallest is a bathroom; tiny rooms are closets."""
    types = {}
    by_area = sorted(rooms, key=lambda r: (r[2] - r[0]) * (r[3] - r[1]), reverse=True)
    if bathroom and len(by_area) >= 3:
        types[by_area.pop()] = 'bathroom'
    queue = list(order)
    for r in by_area:
        if (r[2] - r[0]) * (r[3] - r[1]) <= closet_area or min(r[2] - r[0], r[3] - r[1]) < 8:
            types[r] = 'closet'
        else:
            types[r] = queue.pop(0) if queue else order[-1]
    return types


# ------------------------------------------------------------------ buildings

def stairs_flight(cv, x0, y0, y1, zf, direction, colour='stair', hollow_from=None):
    """One flight of STEPS steps rising STORY voxels over RUN along x, between y0 and y1."""
    for i in range(STEPS):
        if direction > 0:
            sx = x0 + STEP_RUN * i
        else:
            sx = x0 + RUN - STEP_RUN * (i + 1)
        top = zf + STEP_RISE * (i + 1)
        bottom = zf if hollow_from is None or i < hollow_from else top - 2
        cv.fill(sx, y0, bottom, sx + STEP_RUN, y1, top, colour)


def shell(cv, stories, story_h, style, floor='floor_wood', roof_slab='roof_flat'):
    L, W = cv.L, cv.W
    top = BASE + stories * story_h
    cv.ring(0, 0, L, W, BASE, top + SLAB, style['ext'])
    for s in range(stories):
        cv.slab(WALL, WALL, L - WALL, W - WALL, BASE + s * story_h, floor if s else style.get('ground', floor))
    cv.slab(WALL, WALL, L - WALL, W - WALL, top, roof_slab)
    # Trim bands at every slab, a plinth and corner pillars give the facade some structure.
    cv.ring(0, 0, L, W, BASE, BASE + 2, style['trim'])
    for s in range(1, stories + 1):
        z = BASE + s * story_h
        cv.ring(0, 0, L, W, z, z + SLAB, style['trim'])
    for x, y in ((0, 0), (L - WALL, 0), (0, W - WALL), (L - WALL, W - WALL)):
        cv.fill(x, y, BASE, x + WALL, y + WALL, top + SLAB, style['trim'])
    return top


def gable_roof(cv, top, style):
    L, W = cv.L, cv.W
    z = top + SLAB
    i = 0
    while True:
        lo, hi = -2 + i, W + 1 - i
        if hi - lo < 3:
            cv.fill(-1, lo, z + i, L + 1, hi + 1, z + i + 1, style['roof'])
            break
        cv.fill(-1, lo, z + i, L + 1, lo + 2, z + i + 1, style['roof'])
        cv.fill(-1, hi - 1, z + i, L + 1, hi + 1, z + i + 1, style['roof'])
        if hi - 1 > lo + 2:
            cv.fill(0, lo + 2, z + i, WALL, hi - 1, z + i + 1, style['ext'])
            cv.fill(L - WALL, lo + 2, z + i, L, hi - 1, z + i + 1, style['ext'])
        i += 1
    cx = L // 4
    cv.fill(cx, W // 2 + 2, z, cx + 4, W // 2 + 6, z + i + 6, 'brick')


def flat_roof(cv, top, rng, parapet=4, units=0):
    L, W = cv.L, cv.W
    z = top + SLAB
    cv.ring(0, 0, L, W, z, z + parapet, 'parapet')
    for _ in range(units):
        x = rng.randint(4, max(4, L - 12))
        y = rng.randint(4, max(4, W - 12))
        cv.fill(x, y, z, x + 7, y + 6, z + 5, 'metal')
        cv.fill(x + 1, y + 1, z + 5, x + 6, y + 5, z + 6, 'black')


def house(rng, colours, length, width, style, stories=2, basement=False):
    """House of 2-3 storeys: rooms on every floor, a stair along the back wall (a switchback for a third
    floor), gable roof. A basement is reached by a flight down under the main stair."""
    cv = Canvas(length, width, colours)
    L, W = length, width
    top = shell(cv, stories, STORY, style)
    ix0, iy0, ix1, iy1 = WALL, WALL, L - WALL, W - WALL
    zfs = [BASE + s * STORY + SLAB for s in range(stories)]
    zf0 = zfs[0]
    # Flights: even ones rise along +x in the strip against the back wall, odd ones along -x in the strip in
    # front of it, so they stack. Cut each stairwell first: the top step is level with the floor above.
    sx = ix0 + 4
    sy0, sy1 = iy1 - 6, iy1
    by0 = sy0 - 6
    for s in range(stories - 1):
        y0, y1, direction = (sy0, sy1, +1) if s % 2 == 0 else (by0, sy0, -1)
        cut = BASE + (s + 1) * STORY
        cv.clear(sx, y0, cut, sx + RUN, y1, cut + SLAB)
        # Over a basement flight the steps hang from the first one, giving headroom on the way down.
        stairs_flight(cv, sx, y0, y1, zfs[s], direction, hollow_from=1 if basement and s == 0 else 4)
    well_y0 = sy0 if stories == 2 else by0
    cv.block(zf0, sx, sy0, sx + RUN, sy1)
    cv.keep_zone(zf0, ix0, sy0, sx, sy1)
    cv.keep_zone(zf0, sx + 12, sy0 - 4, sx + RUN, sy0)                           # under-stair cubby
    for s in range(1, stories):
        zf = zfs[s]
        if stories == 2:
            cv.fill(sx, sy0 - 1, zf, sx + RUN - 6, sy0, zf + 6, 'wood_dark')     # railing
            cv.fill(sx - 1, sy0 - 1, zf, sx, sy1, zf + 6, 'wood_dark')
            cv.block(zf, sx - 1, sy0 - 1, sx + RUN, sy1)
            cv.keep_zone(zf, sx + RUN - 6, sy0 - 5, min(ix1, sx + RUN + 4), sy1)
        elif s == 1:
            # Middle floor: the first well behind, the next flight rising from the landing past its top.
            cv.fill(sx - 1, sy0, zf, sx, sy1, zf + 6, 'wood_dark')
            cv.block(zf, sx - 1, by0, sx + RUN, sy1)
            cv.keep_zone(zf, sx + RUN, by0, min(ix1, sx + RUN + 5), sy1)
        else:
            # Top floor: railings round the second well, open at the end the flight arrives at.
            cv.fill(sx + 3, by0 - 1, zf, sx + RUN + 1, by0, zf + 6, 'wood_dark')
            cv.fill(sx + 3, sy0, zf, sx + RUN + 1, sy0 + 1, zf + 6, 'wood_dark')
            cv.fill(sx + RUN, by0, zf, sx + RUN + 1, sy0, zf + 6, 'wood_dark')
            cv.block(zf, sx, by0 - 1, sx + RUN + 1, sy0 + 1)
            cv.keep_zone(zf, ix0, by0 - 4, sx, sy0 + 1)
    if basement:
        _basement(cv, rng, sx, sy0, sy1, zf0)
    plans = []
    upstairs = ['kids', 'study', 'gym', 'bedroom']
    rng.shuffle(upstairs)
    for s, zf in enumerate(zfs):
        if s == 0:
            order, stair_room, bathroom = ['kitchen', 'dining', rng.choice(['study', 'laundry', 'games'])], 'living', False
            # A basement flight is entered from its end: keep partitions well clear of it.
            reserve = (ix0, sy0 - 5, min(ix1, sx + RUN + (10 if basement else 4)), iy1)
        else:
            order = ['bedroom'] + upstairs[(s - 1) * 2:] + upstairs[:(s - 1) * 2]
            stair_room, bathroom = rng.choice(['library', 'games', 'study']), True
            reserve = (ix0, well_y0 - 5, min(ix1, sx + RUN + 5), iy1)
        rooms, walls = bsp((ix0, iy0, ix1, iy1), rng, 20, 1000, avoid=[reserve])
        # The room around the stair is the living room downstairs and an open lounge upstairs.
        stair = [r for r in rooms if r[0] <= reserve[0] and r[1] <= reserve[1] and r[2] >= reserve[2] and r[3] >= reserve[3]]
        types = assign_types([r for r in rooms if r not in stair], order, bathroom=bathroom and len(rooms) >= 3)
        for r in stair:
            types[r] = stair_room
        for w in walls:
            cv.wall(w[0], w[1], w[2], w[3], zf)
        connect_rooms(cv, rooms, walls, types, zf, rng, avoid=[reserve])
        plans.append((zf, rooms, walls, types))
    # Front door into the biggest ground-floor room, optional side door.
    zf, rooms, walls, types = plans[0]
    front = [r for r in rooms if r[1] == iy0]
    front.sort(key=lambda r: (r[2] - r[0]) * (r[3] - r[1]), reverse=True)
    junctions = [(w[1] - 2, w[1] + 4) for w in walls if w[0] == 0 and w[2] == iy0]
    door_p = None
    for r in front:
        opts = [p for p in range(r[0] + 2, r[2] - DOOR_W - 2) if not any(p < b and p + DOOR_W > a for a, b in junctions)]
        if opts:
            door_p = rng.choice(opts)
            break
    if door_p is None:
        door_p = L // 2 - DOOR_W // 2
    cv.exterior_door('S', door_p, zf0)
    side_rooms = [r for r in rooms if r[2] == ix1 and r[3] <= sy0 - 5]
    side_door = None
    if side_rooms and rng.random() < 0.7:
        r = side_rooms[0]
        if r[3] - r[1] >= DOOR_W + 4:
            side_door = rng.randint(r[1] + 2, r[3] - DOOR_W - 2)
            cv.exterior_door('E', side_door, zf0)
    # Windows on every outer wall except over the stair run.
    for zf, rooms, walls, types in plans:
        def junction_bands(side):
            out = []
            for w in walls:
                axis, s, a0, a1 = w
                if side == 'S' and axis == 0 and a0 == iy0 or side == 'N' and axis == 0 and a1 == iy1:
                    out.append((s - 1, s + WALL + 1))
                if side == 'W' and axis == 1 and a0 == ix0 or side == 'E' and axis == 1 and a1 == ix1:
                    out.append((s - 1, s + WALL + 1))
            return out
        south = junction_bands('S') + ([(door_p - 2, door_p + DOOR_W + 2)] if zf == zf0 else [])
        cv.windows_along('S', zf, south, 8, 10, 5, 15, rng)
        cv.windows_along('N', zf, junction_bands('N') + [(sx - 1, sx + RUN + 1)], 8, 10, 5, 15, rng)
        cv.windows_along('W', zf, junction_bands('W') + [(well_y0 - 1, iy1 + 1)], 8, 10, 5, 15, rng)
        east = junction_bands('E') + ([(side_door - 2, side_door + DOOR_W + 2)] if side_door and zf == zf0 else [])
        cv.windows_along('E', zf, east, 8, 10, 5, 15, rng)
    for zf, rooms, walls, types in plans:
        for r in rooms:
            if types[r] == 'bathroom':
                cv.recolour(r[0], r[1], zf - 1, r[2], r[3], zf, 'floor_tile')
            cv.furnish(r, zf, types[r], rng)
    gable_roof(cv, top, style)
    return cv


DOWN_STEPS = 9      # basement flight: 9 x 2 voxels down from the ground floor


def _basement(cv, rng, sx, sy0, sy1, zf0):
    """A concrete cellar under the back of the house, entered by a flight that runs down beneath the
    main stair (towards -x, from its top end). Its shell is static terrain: the drone cannot dig it out."""
    L, W = cv.L, cv.W
    iy1 = W - WALL
    zb = zf0 - STEP_RISE * DOWN_STEPS                  # basement floor level
    bx0, bx1 = WALL, min(L - WALL, sx + RUN + 14)
    by0, by1 = max(WALL + 6, iy1 - 26), iy1
    under = cv.under
    c = cv.col

    def put(x0, y0, z0, x1, y1, z1, colour):
        for x in range(x0, x1):
            for y in range(y0, y1):
                for z in range(z0, z1):
                    under[(x, y, z)] = c[colour]

    # Walls under the house's own walls, a two-voxel floor, then the air is dug out of the terrain.
    put(bx0 - WALL, by0 - WALL, zb - 2, bx1 + WALL, by1 + WALL, 1, 'concrete')
    for x in range(bx0, bx1):
        for y in range(by0, by1):
            for z in range(zb, 1):
                under.pop((x, y, z), None)
            cv.dig.add((x, y))
    # The flight: step k (from the top) is 2 voxels lower than the one before; the last one is the floor.
    top_x = sx + RUN
    for k in range(DOWN_STEPS - 1):
        put(top_x - STEP_RUN * (k + 1), sy0, zb, top_x - STEP_RUN * k, sy1, zf0 - STEP_RISE * (k + 1), 'stair')
    hole_x0 = top_x - STEP_RUN * (DOWN_STEPS - 1)
    cv.clear(hole_x0, sy0, BASE, top_x, sy1, BASE + SLAB)
    # Ground floor: a low wall under the hanging steps keeps people from walking off the edge; the
    # well is entered from its open end and the last metre of its side.
    for i in range(1, 8):
        x = sx + STEP_RUN * i
        cv.fill(x, sy0 - 1, zf0, x + STEP_RUN, sy0, zf0 + min(5, STEP_RISE * i), 'wood_dark')
    cv.block(zf0, sx, sy0 - 1, sx + RUN - 6, sy0)
    cv.keep_zone(zf0, top_x, sy0, min(L - WALL, top_x + 6), sy1)
    cv.block(zb, hole_x0, sy0, top_x, sy1)
    cv.keep_zone(zb, bx0, sy0 - 3, hole_x0, sy1)
    cv.furnish((bx0, by0, bx1, by1), zb, 'basement', rng)


def shed(rng, colours, length, width, style, garage=False):
    """One-storey shed or garage at the back of a lot: workshop clutter, planks and a hiding wardrobe."""
    cv = Canvas(length, width, colours)
    L, W = length, width
    top = shell(cv, 1, STORY, dict(style, ground='concrete'), floor='concrete')
    ix0, iy0, ix1, iy1 = WALL, WALL, L - WALL, W - WALL
    zf = BASE + SLAB
    if garage:
        # Double doors: two leaves hinged at the outer edges.
        p = L // 2 - DOOR_W
        cv.doorway(1, 0, p, zf, door=True, hinge=0)
        cv.doorway(1, 0, p + DOOR_W, zf, door=True, hinge=1)
        cv.exits.append((L // 2, 0, 0, -1, DOOR_W * 2))
        blocked = [(p - 2, p + 2 * DOOR_W + 2)]
    else:
        p = rng.randint(ix0 + 2, ix1 - DOOR_W - 2)
        cv.exterior_door('S', p, zf)
        blocked = [(p - 2, p + DOOR_W + 2)]
    cv.windows_along('S', zf, blocked, 8, 8, 6, 16, rng)
    cv.windows_along('W', zf, [], 8, 8, 6, 16, rng)
    cv.windows_along('E', zf, [], 8, 8, 6, 16, rng)
    cv.furnish((ix0, iy0, ix1, iy1), zf, 'garage' if garage else 'shed', rng)
    flat_roof(cv, top, rng, parapet=2)
    return cv


def apartment(rng, colours, length, width, stories, style):
    """Apartment block: stair hall with a switchback stair, corridor, flats with 2-4 rooms."""
    cv = Canvas(length, width, colours)
    L, W = length, width
    top = shell(cv, stories, STORY, style, floor='floor_wood')
    ix0, iy0, ix1, iy1 = WALL, WALL, L - WALL, W - WALL
    hall_x1 = ix0 + RUN + 14
    run0 = ix0 + 6
    strip_a, strip_b = (iy1 - 12, iy1 - 6), (iy1 - 6, iy1)
    double = iy1 - iy0 >= 56
    if double:
        cy0 = iy0 + (iy1 - iy0 - CORRIDOR) // 2
        sides = [(iy0, cy0 - WALL), (cy0 + CORRIDOR + WALL, iy1)]
        corridor_walls = [cy0 - WALL, cy0 + CORRIDOR]
    else:
        cy0 = iy0
        sides = [(iy0 + CORRIDOR + WALL, iy1)]
        corridor_walls = [iy0 + CORRIDOR]
    unit_cuts, flats = [], []
    x = hall_x1 + WALL
    while ix1 - x > 60:
        cut = x + rng.randint(40, 48)
        unit_cuts.append(cut)
        x = cut + WALL
    for s in range(stories):
        zf = BASE + s * STORY + SLAB
        # Stair hall: open landings at both ends, flights alternate strips so they can stack.
        if s < stories - 1:
            strip = strip_a if s % 2 == 0 else strip_b
            cv.clear(run0, strip[0], BASE + (s + 1) * STORY, run0 + RUN, strip[1], BASE + (s + 1) * STORY + SLAB)
            stairs_flight(cv, run0, strip[0], strip[1], zf, +1 if s % 2 == 0 else -1)
        cv.block(zf, run0, strip_a[0], run0 + RUN, strip_b[1])
        cv.keep_zone(zf, ix0, strip_a[0], run0, iy1)
        cv.keep_zone(zf, run0 + RUN, strip_a[0], run0 + RUN + 6, iy1)
        cv.keep_zone(zf, run0, strip_a[0] - 4, run0 + RUN, strip_a[0])
        cv.wall(0, hall_x1, iy0, iy1, zf)
        cv.clear(hall_x1, cy0, zf, hall_x1 + WALL, cy0 + CORRIDOR, zf + CLEAR - 2)
        cv.keep_zone(zf, hall_x1 - 6, cy0, hall_x1 + 8, cy0 + CORRIDOR)
        for wy in corridor_walls:
            cv.wall(1, wy, hall_x1 + WALL, ix1, zf)
        cv.keep_zone(zf, hall_x1 + WALL, cy0 + 1, ix1, cy0 + CORRIDOR - 1)
        for side_y0, side_y1 in sides:
            starts = [hall_x1 + WALL] + [c + WALL for c in unit_cuts]
            ends = unit_cuts + [ix1]
            for c in unit_cuts:
                cv.wall(0, c, side_y0, side_y1, zf)
            for ux0, ux1 in zip(starts, ends):
                rect = (ux0, side_y0, ux1, side_y1)
                rooms, walls = bsp(rect, rng, 18, 600)
                types = assign_types(rooms, ['living', 'bedroom', 'kitchen', rng.choice(['kids', 'study', 'bedroom'])])
                for w in walls:
                    cv.wall(w[0], w[1], w[2], w[3], zf)
                connect_rooms(cv, rooms, walls, types, zf, rng)
                # Flat entrance from the corridor, clear of internal wall junctions.
                cwall_y = side_y0 - WALL if side_y0 > cy0 else side_y1
                entrance = doorway_spot((1, cwall_y, ux0, ux1), walls + [(0, ux0 - WALL, 0, 0)], [], rng)
                if entrance is not None:
                    cv.doorway(1, cwall_y, entrance, zf, door=True, hinge=rng.randint(0, 1))
                for r in rooms:
                    if types[r] == 'bathroom':
                        cv.recolour(r[0], r[1], zf - 1, r[2], r[3], zf, 'floor_tile')
                    flats.append((zf, r, types[r]))
        # Windows: units on the long walls, corridor ends, hall end wall.
        cuts = [(c - 1, c + WALL + 1) for c in unit_cuts] + [(hall_x1 - 1, hall_x1 + WALL + 1)]
        cv.windows_along('S', zf, cuts, 8, 10, 5, 15, rng)
        cv.windows_along('N', zf, cuts + [(ix0, hall_x1 + WALL + 1)], 8, 10, 5, 15, rng)
        ends = [(w - 1, w + WALL + 1) for w in corridor_walls]
        if s == 0:
            lobby_mid = (iy0 + strip_a[0]) // 2 - DOOR_W // 2
            cv.exterior_door('W', lobby_mid, zf)
            cv.exterior_door('E', cy0 + 2, zf)
            cv.windows_along('W', zf, ends + [(lobby_mid - 2, lobby_mid + DOOR_W + 2), (strip_a[0] - 1, iy1)], 8, 10, 5, 15, rng)
            cv.windows_along('E', zf, ends + [(cy0 - 1, cy0 + CORRIDOR + 1)], 8, 10, 5, 15, rng)
        else:
            cv.windows_along('W', zf, ends + [(strip_a[0] - 1, iy1)], 8, 10, 5, 15, rng)
            cv.windows_along('E', zf, ends, 8, 10, 5, 15, rng)
    # Build the ceiling before placing top-floor lamps.
    flat_roof(cv, top, rng, units=4)
    # Furnish after every opening exists, so windows and doors are respected.
    for s in range(stories):
        cv.furnish((ix0, iy0, hall_x1, strip_a[0]), BASE + s * STORY + SLAB, 'lobby', rng)
        # Shared landings and the corridor get sensors; private rooms keep manual switches.
        zf = BASE + s * STORY + SLAB
        cv.motion_lights.add(len(cv.lights) - 1)
        cv.room_light((ix0, strip_a[0], run0, iy1), zf, motion=True)
        for x0 in range(hall_x1 + WALL, ix1, 32):
            cv.room_light((x0, cy0, min(ix1, x0 + 32), cy0 + CORRIDOR), zf, motion=True)
    for zf, r, kind in flats:
        cv.furnish(r, zf, kind, rng)
    return cv


def shop(rng, colours, length, width, style):
    """Single tall storey: dining hall at the front, kitchen, storage and restroom behind."""
    cv = Canvas(length, width, colours)
    L, W = length, width
    story = 24
    top = shell(cv, 1, story, style, floor='floor_shop')
    ix0, iy0, ix1, iy1 = WALL, WALL, L - WALL, W - WALL
    zf = BASE + SLAB
    clear = story - SLAB
    back = iy1 - 22
    cv.wall(1, back - WALL, ix0, ix1, zf, height=clear)
    rest_x = ix1 - 16
    store_x = rest_x - WALL - 20
    cv.wall(0, rest_x - WALL, back, iy1, zf, height=clear)
    cv.wall(0, store_x - WALL, back, iy1, zf, height=clear)
    kitchen, storage, restroom = (ix0, back, store_x - WALL, iy1), (store_x, back, rest_x - WALL, iy1), (rest_x, back, ix1, iy1)
    hall = (ix0, iy0, ix1, back - WALL)
    cv.doorway(1, back - WALL, ix0 + 6, zf, door=True)
    cv.doorway(1, back - WALL, rest_x + 2, zf, door=True)
    cv.doorway(0, store_x - WALL, back + 4, zf, door=True)
    # Storefront: wide glazing, central entrance, back service door.
    mid = L // 2 - DOOR_W // 2
    cv.exterior_door('S', mid, zf)
    cv.exterior_door('N', ix0 + 4, zf)
    cv.windows_along('S', zf, [(mid - 2, mid + DOOR_W + 2)], 10, 12, 3, 14, rng)
    cv.windows_along('W', zf, [(back - 3, iy1)], 8, 10, 5, 13, rng)
    cv.windows_along('E', zf, [(back - 3, iy1)], 8, 10, 5, 13, rng)
    cv.fill(ix0 + 14, back - WALL - 5, zf, ix0 + 28, back - WALL, zf + 6, 'shop_counter')
    cv.fill(ix0 + 14, back - WALL - 5, zf + 6, ix0 + 28, back - WALL, zf + 7, 'counter_top')
    cv.block(zf, ix0 + 13, back - WALL - 6, ix0 + 29, back - WALL)
    cv.recolour(restroom[0], restroom[1], zf - 1, restroom[2], restroom[3], zf, 'floor_tile')
    cv.recolour(kitchen[0], kitchen[1], zf - 1, kitchen[2], kitchen[3], zf, 'floor_tile')
    cv.furnish(hall, zf, 'cafe', rng)
    cv.furnish(kitchen, zf, 'shop_kitchen', rng)
    cv.furnish(storage, zf, 'storage', rng)
    cv.furnish(restroom, zf, 'restroom', rng)
    # Sign band over the storefront.
    cv.recolour(4, 0, top - 5, L - 4, 1, top - 1, style['sign'])
    flat_roof(cv, top, rng, units=2)
    return cv


# ------------------------------------------------------------------ world placement

class Building:
    def __init__(self, bid, kind, vox, windows, doors, rect, exits):
        self.bid, self.kind, self.vox = bid, kind, vox
        self.windows, self.doors, self.rect, self.exits = windows, doors, rect, exits
        self.ground = {}
        self.under = {}
        self.dig = set()
        self.props = []
        self.lights = []
        self.material = 1


def commit(cv, bid, kind, rot, wx, wy):
    """Rotate the canvas by rot x 90 degrees counter-clockwise and put its footprint's min corner at (wx, wy)."""
    def edge(x, y):
        return ((x, y), (-y, x), (-x, -y), (y, -x))[rot]

    corners = [edge(0, 0), edge(cv.L, 0), edge(0, cv.W), edge(cv.L, cv.W)]
    ox, oy = wx - min(c[0] for c in corners), wy - min(c[1] for c in corners)

    def cell(x, y):
        ex, ey = ((x, y), (-y - 1, x), (-x - 1, -y - 1), (y, -x - 1))[rot]
        return ex + ox, ey + oy

    def point(x, y):
        ex, ey = edge(x, y)
        return ex + ox, ey + oy

    vox = {}
    for (x, y, z), c in cv.vox.items():
        nx, ny = cell(x, y)
        vox[(nx, ny, z)] = c

    def pane(x, y, w, plane, hinge=None):
        a = point(x, y)
        b = point(x, y + w) if plane == 0 else point(x + w, y)
        new_plane = 0 if a[0] == b[0] else 1
        idx = 1 if new_plane == 0 else 0
        start, end = (a, b) if a[idx] <= b[idx] else (b, a)
        flipped = start is not a
        if hinge is not None and flipped:
            hinge = 1 - hinge
        return start[0], start[1], new_plane, hinge

    windows = []
    for x, y, z, w, h, plane, _side in cv.windows:
        px, py, pl, _ = pane(x, y, w, plane)
        windows.append((px, py, z, w, h, pl))
    doors = []
    for x, y, z, w, h, plane, hinge, outward, tint in cv.doors:
        px, py, pl, hg = pane(x, y, w, plane, hinge)
        swing = 0
        if outward is not None:
            # InteractiveDoor swings "positive" towards cross(up, span); in voxel x/y that is (sy, -sx).
            odx, ody = ((outward[0], outward[1]), (-outward[1], outward[0]), (-outward[0], -outward[1]),
                        (outward[1], -outward[0]))[rot]
            sx, sy = (0, w) if pl == 0 else (w, 0)
            if hg:
                sx, sy = -sx, -sy
            swing = 1 if odx * sy - ody * sx > 0 else -1
        doors.append((px, py, z, w, h, pl, hg, swing, tint))
    exits = []
    for x, y, dx, dy, w in cv.exits:
        nx, ny = cell(x, y)
        ndx, ndy = ((dx, dy), (-dy, dx), (-dx, -dy), (dy, -dx))[rot]
        exits.append((nx, ny, ndx, ndy, w))
    xs = [c[0] for c in corners]
    ys = [c[1] for c in corners]
    rect = (min(xs) + ox, min(ys) + oy, max(xs) + ox, max(ys) + oy)
    building = Building(bid, kind, vox, windows, doors, rect, exits)
    for (x, y), colour in cv.ground.items():
        building.ground[cell(x, y)] = cv.col[colour]
    for (x, y, z), c in cv.under.items():
        nx, ny = cell(x, y)
        building.under[(nx, ny, z)] = c
    building.dig = {cell(x, y) for x, y in cv.dig}
    for (lx, ly, lz), (sx, sy, sz), (dx, dy) in cv.lights:
        ndx, ndy = ((dx, dy), (-dy, dx), (-dx, -dy), (dy, -dx))[rot]
        building.lights.append((point(lx, ly) + (lz,), point(sx, sy) + (sz,), (ndx, ndy)))
    building.motion_lights = set(cv.motion_lights)
    for kind, pvox in cv.props:
        moved = {}
        for (x, y, z), c in pvox.items():
            nx, ny = cell(x, y)
            moved[(nx, ny, z)] = c
        building.props.append((kind, moved))
    return building
