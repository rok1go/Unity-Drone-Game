
const fs = require('node:fs');

const [input, output, blenderData] = process.argv.slice(2);
if (!input || !output) {
    throw new Error('Usage: node Tools/bake-suburban-town.cjs input.vox output.bytes [blender-data.json]');
}

const bytes = fs.readFileSync(input);
if (bytes.toString('ascii', 0, 4) !== 'VOX ') throw new Error('Expected a VOX scene');
let cursor = 20;
let size, palette;
const models = [];
const nodes = new Map();
const hiddenLayers = new Set();

function readInt() {
    const value = bytes.readInt32LE(cursor);
    cursor += 4;
    return value;
}

function readString() {
    const size = readInt();
    const value = bytes.toString('utf8', cursor, cursor + size);
    cursor += size;
    return value;
}

function readAttributes() {
    const count = readInt();
    const values = {};
    for (let i = 0; i < count; i++) values[readString()] = readString();
    return values;
}

while (cursor + 12 <= bytes.length) {
    const tag = bytes.toString('ascii', cursor, cursor + 4);
    cursor += 4;
    const length = readInt();
    readInt(); // Children length.
    const end = cursor + length;
    if (tag === 'SIZE') size = [readInt(), readInt(), readInt()];
    if (tag === 'XYZI') {
        const count = readInt();
        models.push({ size, data: bytes.subarray(cursor, cursor + 4 * count) });
    }
    if (tag === 'RGBA') palette = bytes.subarray(cursor, cursor + 1024);
    if (tag === 'LAYR') {
        const id = readInt();
        if (readAttributes()._hidden === '1') hiddenLayers.add(id);
    }
    if (tag === 'nTRN' || tag === 'nGRP' || tag === 'nSHP') {
        const id = readInt();
        const node = { tag, attributes: readAttributes() };
        nodes.set(id, node);
        if (tag === 'nTRN') {
            node.child = readInt();
            readInt(); // Reserved.
            node.layer = readInt();
            node.frame = readInt() > 0 ? readAttributes() : {};
        } else {
            const count = readInt();
            node.children = [];
            for (let i = 0; i < count; i++) {
                node.children.push(readInt());
                if (tag === 'nSHP') readAttributes();
            }
        }
    }
    cursor = end;
}

const cells = new Map();
const objectNames = [];
const destructibleObjects = new Set();
const panes = [];
const paneHeight = 9;
const sillHeight = 6;
const wallDepth = 2;
const key = (x, y, z) => ((z + 64) * 1024 + y + 512) * 1024 + x + 512;

// One shared palette for the plaster, steel, timber and stone used throughout the district.
const finishes = {
    chalk: [207, 194, 172], stone: [181, 172, 159], sage: [162, 173, 155],
    ochre: [184, 143, 104], clay: [168, 124, 111], steel: [62, 76, 80],
    zinc: [79, 96, 101], timber: [143, 104, 77], plinth: [109, 112, 106],
    paving: [154, 145, 132], interior: [182, 169, 150], accent: [89, 121, 118],
    awning: [148, 73, 55], coping: [118, 127, 123], roadMark: [150, 144, 121]
};
const landscapeColors = {
    8: [137, 132, 119], 14: [63, 66, 63], 18: [68, 102, 81], 20: [112, 139, 98],
    21: [102, 128, 80], 22: [119, 145, 97], 23: [79, 115, 89], 24: [88, 123, 80],
    25: [74, 105, 73], 26: [58, 87, 66], 29: [103, 121, 81], 30: [124, 145, 96],
    37: [100, 77, 61], 47: [72, 57, 45]
};
const finish = {};
palette = Buffer.from(palette);
for (const [index, rgb] of Object.entries(landscapeColors)) {
    Buffer.from([...rgb, 255]).copy(palette, (index - 1) * 4);
}
Object.entries(finishes).forEach(([name, rgb], i) => {
    const color = 240 + i;
    finish[name] = color;
    Buffer.from([...rgb, 255]).copy(palette, (color - 1) * 4);
});

// Half-open plots, at the original road frontage. Houses use the same materials and
// proportions, with alternating roof orientations and muted wall colors.
const plots = {
    4:  { name: 'Cedar Court', bounds: [-128, 169, -41, 205], front: [1, -1], wall: 'chalk', kind: 'tower' },
    5:  { name: 'Alder Court', bounds: [109, 12, 155, 118], front: [0, -1], wall: 'stone', kind: 'tower' },
    6:  { name: 'Corner Market', bounds: [10, 181, 64, 205], front: [1, -1], wall: 'stone', kind: 'shop' },
    7:  { name: 'Cedar Cafe', bounds: [-134, -84, -111, -36], front: [0, 1], wall: 'ochre', kind: 'shop' },
    19: { name: 'Cedar House', bounds: [-80, -84, -43, -46], front: [0, 1], wall: 'chalk', kind: 'house' },
    20: { name: 'Willow House', bounds: [8, 7, 43, 46], front: [1, 1], wall: 'sage', kind: 'house' },
    21: { name: 'Rowan House', bounds: [55, 7, 90, 46], front: [1, 1], wall: 'clay', kind: 'house' },
    22: { name: 'Birch House', bounds: [-80, 2, -43, 41], front: [0, 1], wall: 'stone', kind: 'house' },
    23: { name: 'Alder House', bounds: [-2, 83, 35, 122], front: [0, -1], wall: 'ochre', kind: 'house' }
};

function fillBox(geometry, x0, y0, z0, x1, y1, z1, color) {
    for (let z = z0; z < z1; z++) {
        for (let y = y0; y < y1; y++) {
            for (let x = x0; x < x1; x++) {
                const id = key(x, y, z);
                if (color === undefined) geometry.delete(id);
                else geometry.set(id, [x, y, z, color]);
            }
        }
    }
}

function fillFace(geometry, side, u0, u1, z0, z1, d0, d1, color) {
    const { axis, edge, sign } = side;
    const n0 = sign < 0 ? edge + d0 : edge - d1;
    const n1 = sign < 0 ? edge + d1 : edge - d0;
    if (axis === 0) fillBox(geometry, n0, u0, z0, n1, u1, z1, color);
    else fillBox(geometry, u0, n0, z0, u1, n1, z1, color);
}

function addPane(geometry, object, side, start, width, floor) {
    const bottom = floor + sillHeight;
    const top = bottom + paneHeight;
    // Dark casing is a surface finish on the full shell, leaving the opening entirely clear.
    fillFace(geometry, side, start - 1, start + width + 1, bottom - 1, top + 1, 0, wallDepth, finish.steel);
    fillFace(geometry, side, start, start + width, bottom, top, 0, wallDepth);
    const { axis, edge } = side;
    panes.push([object, axis === 0 ? edge : start, axis === 0 ? start : edge,
        bottom, width, paneHeight, axis]);
}

function addEntrance(geometry, side, start, width, floor, kind) {
    const top = floor + 15;
    fillFace(geometry, side, start - 1, start + width + 1, floor, top + 2, 0, 1, finish.timber);
    fillFace(geometry, side, start, start + width, floor, top, 0, wallDepth);
    // Paving and a single shallow riser are reserved for the door, away from window sills.
    fillFace(geometry, side, start - 1, start + width + 1, -1, floor, -5, 0, finish.paving);
    const awning = kind === 'shop' ? finish.awning : finish.steel;
    fillFace(geometry, side, start - 2, start + width + 2, top + 1, top + 2, -4, 1, awning);
    fillFace(geometry, side, start - 2, start + width + 2, top, top + 1, -4, -3, awning);
}

function addStairs(geometry, plot, floors) {
    const [x0, y0, x1, y1] = plot.bounds;
    const runsAlongX = x1 - x0 > y1 - y0;
    const runStart = (runsAlongX ? x0 : y0) + 13;
    const sideStart = (runsAlongX ? y0 : x0) + 13;
    const stairWidth = 9;
    const treadDepth = 2;
    const stairBox = (r0, s0, z0, r1, s1, z1, color) => {
        if (runsAlongX) fillBox(geometry, r0, s0, z0, r1, s1, z1, color);
        else fillBox(geometry, s0, r0, z0, s1, r1, z1, color);
    };

    for (let level = 1; level < floors.length; level++) {
        const upper = floors[level];
        const lower = floors[level - 1];
        const rise = upper - lower;
        const runEnd = runStart + (rise - 1) * treadDepth;
        const holeStart = runStart + (rise - 14) * treadDepth - 4;
        fillBox(geometry, x0 + wallDepth, y0 + wallDepth, upper - 2,
            x1 - wallDepth, y1 - wallDepth, upper, finish.interior);
        stairBox(holeStart, sideStart - 1, upper - 2, runEnd, sideStart + stairWidth + 1, upper);

        for (let i = 0; i < rise - 1; i++) {
            const run = runStart + i * treadDepth;
            stairBox(run, sideStart, lower + i - 1, run + treadDepth,
                sideStart + stairWidth, lower + i + 1, finish.paving);
        }

        // Rail panels flank the cut in the upper deck, leaving the landing end open.
        for (const side of [sideStart - 2, sideStart + stairWidth + 1]) {
            stairBox(holeStart, side, upper + 4, runEnd, side + 1, upper + 5, finish.steel);
            for (let run = holeStart; run < runEnd; run += 4) {
                stairBox(run, side, upper, run + 1, side + 1, upper + 4, finish.steel);
            }
        }
    }
}

function addGable(geometry, plot, wall, ceiling) {
    const [x0, y0, x1, y1] = plot.bounds;
    const ridgeAlongX = plot.front[0] === 1;
    const eaveStart = ridgeAlongX ? y0 : x0;
    const eaveEnd = ridgeAlongX ? y1 : x1;
    const ridgeStart = ridgeAlongX ? x0 : y0;
    const ridgeEnd = ridgeAlongX ? x1 : y1;
    const roofStrip = (ridge0, eave0, z0, ridge1, eave1, z1, color) => {
        if (ridgeAlongX) fillBox(geometry, ridge0, eave0, z0, ridge1, eave1, z1, color);
        else fillBox(geometry, eave0, ridge0, z0, eave1, ridge1, z1, color);
    };

    for (let eave = eaveStart - 2; eave < eaveEnd + 2; eave++) {
        const rise = Math.max(0, Math.floor(Math.min(eave - eaveStart, eaveEnd - eave - 1) / 3));
        const roof = ceiling + rise;
        if (eave >= eaveStart && eave < eaveEnd) {
            roofStrip(ridgeStart, eave, ceiling, ridgeStart + wallDepth, eave + 1, roof, wall);
            roofStrip(ridgeEnd - wallDepth, eave, ceiling, ridgeEnd, eave + 1, roof, wall);
        }
        roofStrip(ridgeStart - 2, eave, roof, ridgeEnd + 2, eave + 1, roof + 2, finish.steel);
    }
}

function buildBuilding(plot, object) {
    const [x0, y0, x1, y1] = plot.bounds;
    const wall = finish[plot.wall];
    const tower = plot.kind === 'tower';
    const house = plot.kind === 'house';
    const floors = tower ? [1, 20, 39, 58, 77] : [1];
    const ceiling = tower ? 96 : house ? 21 : 23;
    const geometry = new Map();
    const sides = [
        { axis: 0, sign: -1, edge: x0, start: y0, end: y1 },
        { axis: 0, sign: 1, edge: x1, start: y0, end: y1 },
        { axis: 1, sign: -1, edge: y0, start: x0, end: x1 },
        { axis: 1, sign: 1, edge: y1, start: x0, end: x1 }
    ];
    fillBox(geometry, x0, y0, -1, x1, y1, 1, finish.interior);

    for (const side of sides) {
        fillFace(geometry, side, side.start, side.end, 1, ceiling, 0, wallDepth, wall);
        fillFace(geometry, side, side.start, side.end, 1, 4, 0, 1, finish.plinth);
    }

    for (const floor of floors) {
        for (const side of sides) {
            const frontage = side.axis === plot.front[0] && side.sign === plot.front[1];
            const center = Math.floor((side.start + side.end) / 2);
            const length = side.end - side.start;
            const doorway = floor === 1 && frontage;
            const houseWidth = doorway ? Math.min(10, Math.floor((length - 19) / 2)) : 10;
            const width = tower ? 9 : house ? houseWidth : length >= 40 ? 12 : 9;
            let starts;
            if (doorway && length < 35) {
                // Short cafe wall keeps the main entrance on a long face.
                starts = [];
            } else if (doorway) {
                const sideMargin = length >= 75 ? 6 : 3;
                starts = [side.start + sideMargin, side.end - sideMargin - width];
                if (length >= 75) starts.push(center - width - 10, center + 10);
            } else {
                const count = length >= 90 ? 6 : length >= 75 ? 5 : length >= 35 ? 2 : 1;
                starts = Array.from({ length: count }, (_, i) =>
                    Math.round(side.start + ((i + 0.5) / count) * length - width / 2));
            }

            if (tower) {
                fillFace(geometry, side, side.start, side.end, floor + 2, floor + 5, 0, 1, finish.zinc);
                fillFace(geometry, side, side.start, side.end, floor + 17, floor + 18, -1, 1, finish.coping);
            } else if (!house) {
                fillFace(geometry, side, side.start, side.end, ceiling - 5, ceiling - 2, 0, 1, finish.accent);
            }
            for (const start of starts) addPane(geometry, object, side, start, width, floor);
            if (doorway) addEntrance(geometry, side, center - 4, 8, floor, plot.kind);
        }
    }

    if (tower) addStairs(geometry, plot, floors);
    if (house) {
        addGable(geometry, plot, wall, ceiling);
        // A low divider defines an entry nook without blocking either route to the windows.
        const midX = Math.floor((x0 + x1) / 2);
        const midY = Math.floor((y0 + y1) / 2);
        fillBox(geometry, midX - 4, midY + 2, 1, midX + 4, midY + 3, 7, finish.timber);
    } else {
        fillBox(geometry, x0 - 1, y0 - 1, ceiling, x1 + 1, y1 + 1, ceiling + 2, finish.steel);
        for (const side of sides) {
            fillFace(geometry, side, side.start, side.end, ceiling + 2, ceiling + 4, 0, 1, finish.zinc);
        }
        if (tower) {
            const midX = Math.floor((x0 + x1) / 2);
            const midY = Math.floor((y0 + y1) / 2);
            fillBox(geometry, midX - 5, midY - 5, ceiling + 2, midX + 5, midY + 5, ceiling + 8, finish.stone);
            fillBox(geometry, midX - 6, midY - 6, ceiling + 8, midX + 6, midY + 6, ceiling + 9, finish.steel);
        }
    }
    return geometry.values();
}

function visit(id, transform, name) {
    const node = nodes.get(id);
    if (node.attributes._hidden === '1') return;
    if (node.tag === 'nTRN') {
        if (hiddenLayers.has(node.layer)) return;
        const translation = (node.frame._t ?? '0 0 0').split(' ').map(Number);
        const rotation = Number(node.frame._r ?? 4);
        const first = rotation & 3;
        const second = (rotation >> 2) & 3;
        const axes = [first, second, 3 - first - second];
        visit(node.child, point => transform(axes.map((axis, index) =>
            translation[index] + point[axis] * ((rotation & (16 << index)) ? -1 : 1))),
            node.attributes._name ?? name);
        return;
    }
    if (node.tag === 'nGRP') {
        for (const child of node.children) visit(child, transform, name);
        return;
    }
    if (name === 'Collider Square') return;

    const modelId = node.children[0];
    if ([7, 28, 29].includes(modelId)) return; // Roadside billboard, moped, red car.
    const model = models[modelId];
    const object = objectNames.length;
    const plot = plots[object];
    objectNames.push(plot?.name ?? (name || 'Town model ' + modelId));
    if (plot) {
        destructibleObjects.add(object);
        return;
    }

    if (modelId === 10 || modelId === 14) return; // These trees overlap the enlarged houses or their windows.
    const points = [];
    for (let i = 0; i < model.data.length; i += 4) {
        const local = model.size.map((extent, axis) => model.data[i + axis] - Math.floor(extent / 2) + 0.5);
        points.push([...transform(local).map(Math.floor), model.data[i + 3]]);
    }

    for (const point of points) cells.set(key(...point), [...point, object]);
}
visit(0, point => point, '');
for (const object of destructibleObjects) {
    for (const point of buildBuilding(plots[object], object)) cells.set(key(...point), [...point, object]);
}

function paintAsphalt(x, y) {
    for (let z = 2; z >= -2; z--) {
        const cell = cells.get(key(x, y, z));
        if (!cell) continue;
        if (cell[3] === 14) cell[3] = finish.roadMark;
        return;
    }
}

// Mark the two through streets and leave the junctions open.
for (let y = -92; y < 210; y++) {
    if ((y + 92) % 13 < 6 && !(y >= 132 && y <= 150) && !(y >= 50 && y <= 77))
        paintAsphalt(-17, y);
}
for (let x = -143; x < 166; x++) {
    if ((x + 143) % 13 < 6 && Math.abs(x + 17) > 11)
        paintAsphalt(x, 141);
}
for (let x = -32; x <= -4; x += 4) {
    for (let y = 126; y < 130; y++) paintAsphalt(x, y);
}

const platform = objectNames.length;
objectNames.push('Drone launch platform');
// Deck top is voxel height 111 (18.15 m above the street).
for (let z = 107; z < 111; z++) {
    for (let y = 8; y < 32; y++) {
        for (let x = -12; x < 12; x++) {
            const edge = x === -12 || x === 11 || y === 8 || y === 31;
            const marker = (x === -3 || x === 2) && y >= 16 && y <= 23 ||
                (y === 19 || y === 20) && x >= -3 && x <= 2;
            const color = z === 110 && (edge || marker) ? 33 : 86;
            cells.set(key(x, y, z), [x, y, z, color, platform]);
        }
    }
}

const blockSize = 6;
const groups = [{ faces: [] }]; // Region zero stays static.
const blockLookup = new Map();
for (const cell of cells.values()) {
    const [x, y, z, , object] = cell;
    if (!destructibleObjects.has(object) || z < 1) continue;

    const grid = [Math.floor(x / blockSize), Math.floor(y / blockSize), Math.floor((z - 1) / blockSize)];
    const id = [object, ...grid].join('/');
    let block = blockLookup.get(id);
    if (!block) {
        block = { index: groups.length, object, grid, voxels: [], faces: [] };
        blockLookup.set(id, block);
        groups.push(block);
    }
    cell[5] = block.index;
    block.voxels.push(cell);
}

function surfaceFaces(splitBlocks) {
    const faces = [];
    for (let axis = 0; axis < 3; axis++) {
        const u = (axis + 1) % 3;
        const v = (axis + 2) % 3;
        for (const sign of [-1, 1]) {
            const planes = new Map();
            for (const cell of cells.values()) {
                const neighbor = cell.slice(0, 3);
                neighbor[axis] += sign;
                const region = splitBlocks ? cell[5] ?? 0 : cell[4];
                const adjacent = cells.get(key(...neighbor));
                if (adjacent && (!splitBlocks || (adjacent[5] ?? 0) === region)) continue;
                const plane = cell[axis] + (sign > 0 ? 1 : 0);
                if (!planes.has(plane)) planes.set(plane, new Map());
                planes.get(plane).set((cell[v] + 512) * 1024 + cell[u] + 512, region * 256 + cell[3]);
            }
            for (const [plane, mask] of planes) {
                for (const [index, material] of mask) {
                    let width = 1;
                    let height = 1;
                    while (mask.get(index + width) === material) width++;
                    rows: while (true) {
                        for (let col = 0; col < width; col++) {
                            if (mask.get(index + height * 1024 + col) !== material) break rows;
                        }
                        height++;
                    }
                    for (let row = 0; row < height; row++) {
                        for (let col = 0; col < width; col++) mask.delete(index + row * 1024 + col);
                    }
                    const face = [axis, sign, plane, index % 1024 - 512, Math.floor(index / 1024) - 512,
                        width, height, material % 256];
                    const region = Math.floor(material / 256);
                    if (splitBlocks) groups[region].faces.push(face);
                    else faces.push([...face, region]);
                }
            }
        }
    }
    return faces;
}

function collisionBoxes(voxels) {
    const pending = new Map(voxels.map(cell => [key(...cell), cell]));
    const boxes = [];
    for (const [x, y, z] of pending.values()) {
        let width = 1, depth = 1, height = 1;
        while (pending.has(key(x + width, y, z))) width++;
        rows: while (true) {
            for (let dx = 0; dx < width; dx++) {
                if (!pending.has(key(x + dx, y + depth, z))) break rows;
            }
            depth++;
        }
        floors: while (true) {
            for (let dy = 0; dy < depth; dy++) {
                for (let dx = 0; dx < width; dx++) {
                    if (!pending.has(key(x + dx, y + dy, z + height))) break floors;
                }
            }
            height++;
        }
        for (let dz = 0; dz < height; dz++) {
            for (let dy = 0; dy < depth; dy++) {
                for (let dx = 0; dx < width; dx++) pending.delete(key(x + dx, y + dy, z + dz));
            }
        }
        boxes.push([x, y, z, width, depth, height]);
    }
    return boxes;
}

surfaceFaces(true);
const blocks = groups.slice(1);
for (const block of blocks) block.boxes = collisionBoxes(block.voxels);
const dataSize = 1024 + 4 + groups[0].faces.length * 13 + 4 + blocks.reduce((size, block) =>
    size + 4 * 2 + 4 + block.faces.length * 13 + 4 + block.boxes.length * 12, 0) + 4 + panes.length * 14;
const data = Buffer.alloc(dataSize);
palette.copy(data);
let offset = 1024;

function writeCount(value) {
    data.writeInt32LE(value, offset);
    offset += 4;
}

function writeShorts(values) {
    for (const value of values) {
        data.writeInt16LE(value, offset);
        offset += 2;
    }
}

function writeFaces(faces) {
    writeCount(faces.length);
    for (const [axis, sign, plane, u, v, width, height, color] of faces) {
        data.writeUInt8(axis, offset++);
        data.writeInt8(sign, offset++);
        writeShorts([plane, u, v, width, height]);
        data.writeUInt8(color, offset++);
    }
}

writeFaces(groups[0].faces);
writeCount(blocks.length);
for (const block of blocks) {
    writeShorts([block.object, ...block.grid]);
    writeFaces(block.faces);
    writeCount(block.boxes.length);
    for (const box of block.boxes) writeShorts(box);
}
writeCount(panes.length);
for (const pane of panes) writeShorts(pane);
fs.writeFileSync(output, data);

if (blenderData) {
    fs.writeFileSync(blenderData, JSON.stringify({
        names: objectNames, palette: [...palette], faces: surfaceFaces(false), panes
    }));
}
console.log(blocks.length, 'destructible blocks across', destructibleObjects.size, 'buildings with', panes.length, 'windows');

