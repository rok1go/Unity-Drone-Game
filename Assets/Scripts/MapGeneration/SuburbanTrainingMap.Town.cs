using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

// A new town for every round, built from prebuilt templates (Resources/TownTemplates.bytes, exported by
// Tools/suburb_map/export_town_templates.py): houses, apartment blocks, shops, sheds, vehicles, playground
// pieces and street decoration, each with its full interior, doors, lights and props. The layout comes
// from the round's seed alone (the host shares it), so every peer builds the identical town.
//
// Everything is in voxel units (1 voxel = 0.165 m) on the map's ground plan: x east, y north, z up. A
// template's front (its local y = 0 side) faces south at rotation 0; rotation r turns it by r x 90 degrees
// counter-clockwise, so r = 2 faces north. Templates are placed as rotated instances, so their blocks,
// windows, doors and lights keep their own local layout.
public sealed partial class SuburbanTrainingMap
{
    private const int Road = 18, Walk = 4, Setback = 6;

    private sealed class Template
    {
        public string kind;
        public int length, width, height;
        public readonly List<(int x, int y, int dx, int dy, int w)> exits = new List<(int, int, int, int, int)>();
        public readonly List<(int x, int y, byte texel)> ground = new List<(int, int, byte)>();
        public readonly List<(int x, int y)> dig = new List<(int, int)>();
        public byte[] blob;
    }

    private struct Placement
    {
        public Template template;
        public int x, y, rotation;       // footprint min corner after rotation
        public RectInt Rect => new RectInt(x, y, rotation % 2 == 0 ? template.length : template.width,
            rotation % 2 == 0 ? template.width : template.length);
    }

    public static SuburbanTrainingMap CreateTown(Transform parent)
    {
        var data = Resources.Load<TextAsset>("TownTemplates");
        if (data == null) return null;
        var root = new GameObject("Generated town");
        root.transform.SetParent(parent, false);
        var map = root.AddComponent<SuburbanTrainingMap>();
        map.BuildTown(data.bytes, MatchRules.Current);
        map.ConfigureShadows();
        return map;
    }

    // ---------------- template library ----------------

    private Dictionary<string, byte> colours;

    private List<Template> ReadLibrary(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "TWN1") throw new InvalidDataException("Not a town template file.");
        InitMaterial(reader.ReadBytes(1024));
        colours = new Dictionary<string, byte>();
        int colourCount = reader.ReadInt32();
        for (int i = 0; i < colourCount; i++)
        {
            string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte()));
            colours[name] = reader.ReadByte();
        }
        var list = new List<Template>();
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            var t = new Template { kind = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadByte())) };
            t.length = reader.ReadInt16(); t.width = reader.ReadInt16(); t.height = reader.ReadInt16();
            int exits = reader.ReadInt32();
            for (int e = 0; e < exits; e++)
                t.exits.Add((reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16(), reader.ReadInt16()));
            int ground = reader.ReadInt32();
            for (int g = 0; g < ground; g++) t.ground.Add((reader.ReadInt16(), reader.ReadInt16(), reader.ReadByte()));
            int dig = reader.ReadInt32();
            for (int d = 0; d < dig; d++) t.dig.Add((reader.ReadInt16(), reader.ReadInt16()));
            t.blob = reader.ReadBytes(reader.ReadInt32());
            list.Add(t);
        }
        return list;
    }

    // ---------------- layout ----------------

    private sealed class Ground
    {
        public readonly int x0, y0, width, depth;
        public readonly sbyte[] height;   // top plane: 0 asphalt, 1 raised (sidewalk, lawn, lot), -1 dug out
        public readonly byte[] texel;
        public Ground(int x0, int y0, int width, int depth, byte asphalt)
        {
            this.x0 = x0; this.y0 = y0; this.width = width; this.depth = depth;
            height = new sbyte[width * depth]; texel = new byte[width * depth];
            for (int i = 0; i < texel.Length; i++) texel[i] = asphalt;
        }
        public bool Inside(int x, int y) => x >= x0 && y >= y0 && x < x0 + width && y < y0 + depth;
        public int Index(int x, int y) => (y - y0) * width + (x - x0);
        public void Set(int x, int y, sbyte h, byte c) { if (Inside(x, y)) { int i = Index(x, y); height[i] = h; texel[i] = c; } }
        public void Paint(int x, int y, byte c) { if (Inside(x, y) && height[Index(x, y)] > 0) texel[Index(x, y)] = c; }
        public sbyte HeightAt(int x, int y) => Inside(x, y) ? height[Index(x, y)] : (sbyte)0;
    }

    private void BuildTown(byte[] library, PrototypeLobbySettings rules)
    {
        var templates = ReadLibrary(library);
        var random = new System.Random(rules.ResolvedSeed);
        byte C(string name) => colours.TryGetValue(name, out byte texel) ? texel : (byte)0;
        List<Template> Kind(string kind) => templates.FindAll(t => t.kind == kind);
        var houses = Kind("house"); var small = Kind("house_small"); var shops = Kind("shop");
        var apartments = Kind("apartment"); var sheds = Kind("shed"); sheds.AddRange(Kind("garage"));
        var vehicles = Kind("vehicle"); var trees = Kind("tree"); var playground = Kind("playground");
        var lightPole = Kind("street_light").Find(_ => true); var hydrant = Kind("hydrant").Find(_ => true);
        var bench = Kind("bench").Find(_ => true);

        // Street grid: small 2 x 2 blocks, medium 3 x 3. Block sizes vary a little per seed.
        int columns = rules.MapSize == 0 ? 2 : 3, rows = columns;
        var widths = new int[columns]; var depths = new int[rows];
        for (int i = 0; i < columns; i++) widths[i] = 144 + random.Next(0, 4) * 6;
        for (int i = 0; i < rows; i++) depths[i] = 120 + random.Next(0, 3) * 6;
        int townW = Road * (columns + 1), townD = Road * (rows + 1);
        foreach (int w in widths) townW += w;
        foreach (int d in depths) townD += d;
        int ox = -(townW / 2), oy = -(townD / 2);
        var ground = new Ground(ox, oy, townW, townD, C("asphalt"));
        var blocks = new List<RectInt>();
        for (int r = 0, y = oy + Road; r < rows; y += depths[r] + Road, r++)
            for (int c = 0, x = ox + Road; c < columns; x += widths[c] + Road, c++)
                blocks.Add(new RectInt(x, y, widths[c], depths[r]));
        // Raised blocks: sidewalk ring with a curb, lawn inside.
        foreach (var b in blocks)
            for (int x = b.xMin; x < b.xMax; x++)
                for (int y = b.yMin; y < b.yMax; y++)
                {
                    int edge = Mathf.Min(Mathf.Min(x - b.xMin, b.xMax - 1 - x), Mathf.Min(y - b.yMin, b.yMax - 1 - y));
                    ground.Set(x, y, 1, edge == 0 ? C("curb") : edge < Walk ? C("sidewalk") : C("grass"));
                }
        // Dashed centre lines.
        for (int x = ox; x < ox + townW; x++)
            for (int y = oy; y < oy + townD; y++)
                if (ground.HeightAt(x, y) == 0)
                {
                    bool alongX = blocks.TrueForAll(b => y < b.yMin || y >= b.yMax);
                    bool alongY = blocks.TrueForAll(b => x < b.xMin || x >= b.xMax);
                    if (alongX && !alongY && (x / 8) % 2 == 0 && IsRoadCentre(y, oy, depths)) ground.texel[ground.Index(x, y)] = C("road_line");
                    if (alongY && !alongX && (y / 8) % 2 == 0 && IsRoadCentre(x, ox, widths)) ground.texel[ground.Index(x, y)] = C("road_line");
                }

        // Zoning: 0 private houses, 1 apartment district, 2 mixed, 3 random per block.
        int zoning = rules.Zoning == 3 ? -1 : rules.Zoning;
        int apartmentsLeft = rules.Apartments;
        int parkBlock = columns == 3 ? random.Next(blocks.Count) : -1;
        int density = rules.Density;
        var placements = new List<Placement>();
        var occupied = new List<RectInt>();
        bool Free(RectInt rect, int margin)
        {
            var grown = new RectInt(rect.x - margin, rect.y - margin, rect.width + margin * 2, rect.height + margin * 2);
            foreach (var o in occupied) if (o.Overlaps(grown)) return false;
            return true;
        }
        void Place(Template t, int x, int y, int rotation)
        {
            var p = new Placement { template = t, x = x, y = y, rotation = rotation };
            placements.Add(p); occupied.Add(p.Rect);
        }
        // Places a template so that its local cell (0, 0) (a pole's foot) lands on (x, y).
        void PlaceAt(Template t, int x, int y, int rotation)
        {
            var origin = Cell(new Placement { template = t, x = 0, y = 0, rotation = rotation }, 0, 0);
            Place(t, x - origin.x, y - origin.y, rotation);
        }
        Template Pick(List<Template> from) => from.Count == 0 ? null : from[random.Next(from.Count)];

        for (int i = 0; i < blocks.Count; i++)
        {
            var block = blocks[i];
            var lot = new RectInt(block.x + Walk, block.y + Walk, block.width - Walk * 2, block.height - Walk * 2);
            if (i == parkBlock) { BuildPark(lot, playground, bench, trees, random, Place, Free); continue; }
            int blockZoning = zoning >= 0 ? zoning : random.Next(3);
            // Front row faces the southern street, back row the northern one; they share the block's depth.
            int frontDepth = FillRow(lot, true, lot.height - Setback * 2 - 4, blockZoning);
            if (density > 0 || random.NextDouble() < 0.4)
                FillRow(lot, false, lot.height - Setback * 2 - 4 - frontDepth, density >= 2 ? blockZoning : 9);
        }

        // One row of buildings along a block side. kindSet 9 = only small back-yard buildings.
        int FillRow(RectInt lot, bool south, int maxDepth, int kindSet)
        {
            int deepest = 0;
            int cursor = lot.xMin + 3 + random.Next(0, 6);
            int gapMin = density == 0 ? 10 : density == 1 ? 6 : 3, gapMax = density == 0 ? 24 : density == 1 ? 14 : 8;
            for (int guard = 0; guard < 12; guard++)
            {
                var choice = ChooseBuilding(kindSet, maxDepth, lot.xMax - 3 - cursor);
                if (choice == null) break;
                int y = south ? lot.yMin + Setback : lot.yMax - Setback - choice.width;
                var rect = new RectInt(cursor, y, choice.length, choice.width);
                if (Free(rect, 2))
                {
                    Place(choice, cursor, y, south ? 0 : 2);
                    deepest = Mathf.Max(deepest, choice.width);
                    if (choice.kind == "apartment") apartmentsLeft--;
                }
                cursor += choice.length + random.Next(gapMin, gapMax + 1);
            }
            return deepest;
        }

        Template ChooseBuilding(int kindSet, int maxDepth, int room)
        {
            for (int attempt = 0; attempt < 8; attempt++)
            {
                double roll = random.NextDouble();
                List<Template> pool;
                if (kindSet == 9) pool = roll < 0.55 ? sheds : small;
                else if (kindSet == 1) pool = roll < 0.6 && apartmentsLeft > 0 ? apartments : roll < 0.75 ? shops : roll < 0.9 ? small : houses;
                else if (kindSet == 2) pool = roll < 0.22 && apartmentsLeft > 0 ? apartments : roll < 0.4 ? shops : roll < 0.75 ? houses : small;
                else pool = roll < 0.65 ? houses : roll < 0.9 ? small : shops;
                var t = Pick(pool);
                if (t != null && t.width <= maxDepth && t.length <= room) return t;
            }
            // Something always fits a gap: a shed.
            var shed = Pick(sheds);
            return shed != null && shed.width <= maxDepth && shed.length <= room ? shed : null;
        }

        // Parked vehicles along the curbs, never two side by side, so every street stays passable.
        int segments = 0;
        foreach (var b in blocks)
        {
            ParkAlong(b.xMin, b.xMax, b.yMin - Road, true);
            if (b.yMax + Road >= oy + townD) ParkAlong(b.xMin, b.xMax, b.yMax, true);
            ParkAlong(b.yMin, b.yMax, b.xMin - Road, false);
            if (b.xMax + Road >= ox + townW) ParkAlong(b.yMin, b.yMax, b.xMax, false);
        }
        void ParkAlong(int lo, int hi, int roadStart, bool alongX)
        {
            segments++;
            int count = random.NextDouble() < 0.25 ? 0 : random.NextDouble() < 0.35 ? 2 : 1;
            int cursor = lo + random.Next(6, 30), lane = random.Next(2);
            for (int i = 0; i < count; i++)
            {
                var v = Pick(vehicles);
                if (v == null || cursor + v.length > hi - 6) break;
                if (alongX) Place(v, cursor, lane == 0 ? roadStart + 1 : roadStart + Road - 1 - v.width, lane == 0 ? 0 : 2);
                else Place(v, lane == 1 ? roadStart + Road - 1 - v.width : roadStart + 1, cursor, lane == 1 ? 1 : 3);
                cursor += v.length + random.Next(12, 40);
                lane = 1 - lane;
            }
        }

        // Paths from front doors to the sidewalk, foundations, painted ground and basements.
        foreach (var p in placements)
        {
            string kind = p.template.kind;
            if (kind == "vehicle" || kind == "tree" || kind == "bench" || kind == "street_light" || kind == "hydrant") continue;
            var rect = p.Rect;
            if (p.template.kind != "playground")
                for (int x = rect.xMin; x < rect.xMax; x++)
                    for (int y = rect.yMin; y < rect.yMax; y++) ground.Paint(x, y, C("foundation"));
            foreach (var g in p.template.ground) { var c = Cell(p, g.x, g.y); ground.Paint(c.x, c.y, g.texel); }
            foreach (var d in p.template.dig) { var c = Cell(p, d.x, d.y); ground.Set(c.x, c.y, -1, 0); }
            foreach (var e in p.template.exits)
            {
                var c = Cell(p, e.x, e.y); var dir = Turn(e.dx, e.dy, p.rotation);
                var side = new Vector2Int(dir.y != 0 ? 1 : 0, dir.x != 0 ? 1 : 0);
                for (int step = 0; step < 24; step++)
                {
                    int cx = c.x + dir.x * step, cy = c.y + dir.y * step;
                    if (!ground.Inside(cx, cy) || ground.HeightAt(cx, cy) <= 0) break;
                    int t = ground.texel[ground.Index(cx, cy)];
                    if (t == C("sidewalk") || t == C("curb")) break;
                    for (int k = -e.w / 2; k <= e.w / 2; k++)
                    {
                        int px = cx + side.x * k, py = cy + side.y * k;
                        if (ground.Inside(px, py) && ground.texel[ground.Index(px, py)] == C("grass")) ground.Paint(px, py, C("path"));
                    }
                }
            }
        }

        // Street lights every ~45 voxels on every block's sidewalk, arms over the road; a hydrant per side.
        foreach (var b in blocks)
        {
            DecorAlong(b.xMin + 18, b.xMax - 10, x => (x, b.yMin + 1), 2);
            DecorAlong(b.xMin + 18, b.xMax - 10, x => (x, b.yMax - 2), 0);
            DecorAlong(b.yMin + 18, b.yMax - 10, y => (b.xMin + 1, y), 1);
            DecorAlong(b.yMin + 18, b.yMax - 10, y => (b.xMax - 2, y), 3);
        }
        void DecorAlong(int from, int to, System.Func<int, (int x, int y)> at, int rotation)
        {
            if (lightPole != null)
                for (int t = from; t < to; t += 45)
                {
                    var (x, y) = at(t);
                    if (Free(new RectInt(x, y, 1, 1), 1)) PlaceAt(lightPole, x, y, rotation);
                }
            if (hydrant != null)
            {
                var (hx, hy) = at(from + random.Next(5, Mathf.Max(6, to - from - 5)));
                if (Free(new RectInt(hx, hy, 1, 1), 1)) Place(hydrant, hx, hy, 0);
            }
        }

        // Trees in free yard space: cover from the drone's camera, never on a path or in front of a door.
        foreach (var b in blocks)
        {
            var lot = new RectInt(b.x + Walk + 3, b.y + Walk + 3, b.width - Walk * 2 - 6, b.height - Walk * 2 - 6);
            int target = density == 0 ? 9 : density == 1 ? 6 : 4;
            for (int attempt = 0, placed = 0; attempt < 60 && placed < target; attempt++)
            {
                var tree = random.NextDouble() < 0.75 ? Pick(trees) : trees[trees.Count - 1];
                if (tree == null) break;
                int x = random.Next(lot.xMin, Mathf.Max(lot.xMin + 1, lot.xMax - tree.length));
                int y = random.Next(lot.yMin, Mathf.Max(lot.yMin + 1, lot.yMax - tree.width));
                var rect = new RectInt(x, y, tree.length, tree.width);
                if (!Free(rect, 3) || !ground.Inside(x, y)) continue;
                bool onPath = false;
                for (int cx = rect.xMin; cx < rect.xMax && !onPath; cx += 2)
                    for (int cy = rect.yMin; cy < rect.yMax && !onPath; cy += 2)
                        onPath = !ground.Inside(cx, cy) || ground.texel[ground.Index(cx, cy)] != C("grass");
                if (onPath) continue;
                Place(tree, x, y, random.Next(4));
                placed++;
            }
        }

        // ---------------- build ----------------
        var groundMesh = GroundMesh(ground, C("fence"), C("wood_dark"));
        SetGround(groundMesh);
        var structures = new Dictionary<int, BuildingStructure>();
        var power = gameObject.AddComponent<PowerSystem>();
        int buildingId = 1;
        var instances = new GameObject("Buildings").transform;
        instances.SetParent(transform, false);
        foreach (var p in placements)
        {
            var instance = new GameObject(p.template.kind + " " + buildingId).transform;
            instance.SetParent(instances, false);
            // Rotation r maps local (x, y) to (-y, x) r times; the offset puts the footprint's min corner at (x, y).
            var o = Offset(p);
            instance.localPosition = new Vector3(o.x, 0f, o.y) * VoxelSize;
            instance.localRotation = Quaternion.Euler(0f, -90f * p.rotation, 0f);
            LoadTemplate(p.template.blob, instance, buildingId++, structures, power);
        }
        // People start on the sidewalk at the south edge of the middle block; the drone above the town.
        var start = blocks[blocks.Count / 2];
        HumanSpawn = transform.TransformPoint(ToWorld(start.xMin + 12, start.yMin + 2, 1.2f));
        DroneSpawn = transform.TransformPoint(ToWorld(0f, 0f, 140f));
        FinishBuild(groundMesh, structures, power);
    }

    private static bool IsRoadCentre(int v, int origin, int[] sizes)
    {
        int start = origin;
        foreach (int size in sizes)
        {
            if (v == start + Road / 2) return true;
            start += Road + size;
        }
        return v == start + Road / 2;
    }

    private void BuildPark(RectInt lot, List<Template> pieces, Template bench, List<Template> trees, System.Random random,
        System.Action<Template, int, int, int> place, System.Func<RectInt, int, bool> free)
    {
        // Playground structures spread over the park's middle, benches along its paths, trees round the edge.
        int x = lot.xMin + 12;
        foreach (var piece in pieces)
        {
            int y = lot.yMin + 14 + random.Next(0, Mathf.Max(1, lot.height - 28 - piece.width));
            var rect = new RectInt(x, y, piece.length, piece.width);
            if (rect.xMax < lot.xMax - 8 && free(rect, 3)) place(piece, x, y, 0);
            x += piece.length + 8 + random.Next(0, 10);
        }
        if (bench != null)
            for (int i = 0; i < 4; i++)
            {
                int bx = lot.xMin + 20 + i * (lot.width - 40) / 3;
                if (free(new RectInt(bx, lot.yMin + 3, bench.length, bench.width), 1)) place(bench, bx, lot.yMin + 3, 0);
            }
        for (int i = 0; i < 8 && trees.Count > 0; i++)
        {
            var tree = trees[random.Next(trees.Count)];
            int tx = random.Next(lot.xMin + 2, lot.xMax - tree.length - 2), ty = random.NextDouble() < 0.5 ? lot.yMax - tree.width - 2 : lot.yMin + 8;
            var rect = new RectInt(tx, ty, tree.length, tree.width);
            if (free(rect, 2)) place(tree, tx, ty, 0);
        }
    }

    // Cell (x, y) of a template after placement (Python's commit(): the same mapping).
    private static Vector2Int Cell(Placement p, int x, int y)
    {
        var o = Offset(p);
        return p.rotation switch
        {
            1 => new Vector2Int(-y - 1 + o.x, x + o.y),
            2 => new Vector2Int(-x - 1 + o.x, -y - 1 + o.y),
            3 => new Vector2Int(y + o.x, -x - 1 + o.y),
            _ => new Vector2Int(x + o.x, y + o.y)
        };
    }

    private static Vector2Int Turn(int dx, int dy, int rotation) => rotation switch
    {
        1 => new Vector2Int(-dy, dx), 2 => new Vector2Int(-dx, -dy), 3 => new Vector2Int(dy, -dx), _ => new Vector2Int(dx, dy)
    };

    // Translation that puts the rotated footprint's min corner at the placement's (x, y).
    private static Vector2Int Offset(Placement p)
    {
        int L = p.template.length, W = p.template.width;
        int minX, minY;
        switch (p.rotation)
        {
            case 1: minX = -W; minY = 0; break;
            case 2: minX = -L; minY = -W; break;
            case 3: minX = 0; minY = -L; break;
            default: minX = 0; minY = 0; break;
        }
        return new Vector2Int(p.x - minX, p.y - minY);
    }

    // ---------------- ground mesh ----------------

    // Greedy-merged top faces of the ground plan, curb risers, and the perimeter fence, written in the map's
    // face format and read back by ReadMesh, so the ground behaves like every other voxel surface.
    private Mesh GroundMesh(Ground g, byte fence, byte post)
    {
        var faces = new List<(byte axis, sbyte sign, short plane, short u, short v, short w, short h, byte c)>();
        var used = new bool[g.height.Length];
        for (int y = 0; y < g.depth; y++)
            for (int x = 0; x < g.width; x++)
            {
                int i = y * g.width + x;
                if (used[i] || g.height[i] < 0) continue;
                sbyte h = g.height[i]; byte c = g.texel[i];
                int w = 1;
                while (x + w < g.width && !used[i + w] && g.height[i + w] == h && g.texel[i + w] == c) w++;
                int d = 1;
                while (y + d < g.depth)
                {
                    bool row = true;
                    for (int k = 0; k < w && row; k++)
                    {
                        int j = (y + d) * g.width + x + k;
                        row = !used[j] && g.height[j] == h && g.texel[j] == c;
                    }
                    if (!row) break;
                    d++;
                }
                for (int dy = 0; dy < d; dy++) for (int dx = 0; dx < w; dx++) used[(y + dy) * g.width + x + dx] = true;
                faces.Add((2, 1, (short)h, (short)(g.x0 + x), (short)(g.y0 + y), (short)w, (short)d, (byte)(c + 1)));
            }
        // Risers where a raised cell meets the road (one voxel high), in the raised cell's colour.
        for (int y = 0; y < g.depth; y++)
            for (int x = 0; x < g.width; x++)
            {
                int i = y * g.width + x;
                if (g.height[i] != 1) continue;
                byte c = (byte)(g.texel[i] + 1);
                if (x + 1 < g.width && g.height[i + 1] == 0) faces.Add((0, 1, (short)(g.x0 + x + 1), (short)(g.y0 + y), 0, 1, 1, c));
                if (x > 0 && g.height[i - 1] == 0) faces.Add((0, -1, (short)(g.x0 + x), (short)(g.y0 + y), 0, 1, 1, c));
                if (y + 1 < g.depth && g.height[i + g.width] == 0) faces.Add((1, 1, (short)(g.y0 + y + 1), 0, (short)(g.x0 + x), 1, 1, c));
                if (y > 0 && g.height[i - g.width] == 0) faces.Add((1, -1, (short)(g.y0 + y), 0, (short)(g.x0 + x), 1, 1, c));
            }
        // Board fence round the edge, 14 voxels (2.3 m) high, with posts every 12.
        int fx0 = g.x0, fy0 = g.y0, fx1 = g.x0 + g.width, fy1 = g.y0 + g.depth;
        void Box(int x0, int y0, int z0, int x1, int y1, int z1, byte c)
        {
            faces.Add((2, 1, (short)z1, (short)x0, (short)y0, (short)(x1 - x0), (short)(y1 - y0), c));
            faces.Add((0, 1, (short)x1, (short)y0, (short)z0, (short)(y1 - y0), (short)(z1 - z0), c));
            faces.Add((0, -1, (short)x0, (short)y0, (short)z0, (short)(y1 - y0), (short)(z1 - z0), c));
            faces.Add((1, 1, (short)y1, (short)z0, (short)x0, (short)(z1 - z0), (short)(x1 - x0), c));
            faces.Add((1, -1, (short)y0, (short)z0, (short)x0, (short)(z1 - z0), (short)(x1 - x0), c));
        }
        Box(fx0, fy0, 0, fx1, fy0 + 1, 15, (byte)(fence + 1));
        Box(fx0, fy1 - 1, 0, fx1, fy1, 15, (byte)(fence + 1));
        Box(fx0, fy0 + 1, 0, fx0 + 1, fy1 - 1, 15, (byte)(fence + 1));
        Box(fx1 - 1, fy0 + 1, 0, fx1, fy1 - 1, 15, (byte)(fence + 1));
        for (int x = fx0 + 6; x < fx1 - 6; x += 12) { Box(x, fy0 + 1, 0, x + 1, fy0 + 2, 16, (byte)(post + 1)); Box(x, fy1 - 2, 0, x + 1, fy1 - 1, 16, (byte)(post + 1)); }
        for (int y = fy0 + 6; y < fy1 - 6; y += 12) { Box(fx0 + 1, y, 0, fx0 + 2, y + 1, 16, (byte)(post + 1)); Box(fx1 - 2, y, 0, fx1 - 1, y + 1, 16, (byte)(post + 1)); }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write(faces.Count);
            foreach (var f in faces)
            {
                writer.Write(f.axis); writer.Write(f.sign); writer.Write(f.plane); writer.Write(f.u); writer.Write(f.v);
                writer.Write(f.w); writer.Write(f.h); writer.Write(f.c);
            }
        }
        stream.Position = 0;
        using var reader = new BinaryReader(stream);
        return ReadMesh(reader, Vector3.zero);
    }
}
