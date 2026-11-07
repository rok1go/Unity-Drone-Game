using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public sealed partial class SuburbanTrainingMap : MonoBehaviour
{
    private const float VoxelSize = 0.165f;
    // Read from the map file; the defaults match maps built before spawns were stored.
    public static Vector3 HumanSpawn { get; private set; } = ToWorld(-35.5f, -65.5f, 1.2f);
    public static Vector3 DroneSpawn { get; private set; } = ToWorld(0f, 20f, 112.5f);
    // Humans line up along this direction (the sidewalk) from the spawn point.
    public static Vector3 SpawnLine => Vector3.right;
    private const int BlockSize = 6;
    private readonly List<Mesh> meshes = new List<Mesh>();
    private Texture2D palette;
    private byte[] paletteBytes;
    private Material material;

    public static SuburbanTrainingMap Create(Transform parent)
    {
        // A new town from the template library for every round; the prebuilt map only if it is missing.
        var town = CreateTown(parent);
        if (town != null) return town;
        string suffix = MatchRules.Current.Density == 0 ? "_Sparse" : MatchRules.Current.Density == 1 ? "_Normal" : "";
        var data = Resources.Load<TextAsset>("SmallSuburbanTown" + suffix);
        if (data == null) return null;

        var root = new GameObject("Small suburban town");
        root.transform.SetParent(parent, false);
        var map = root.AddComponent<SuburbanTrainingMap>();
        map.Build(data.bytes);
        map.ConfigureShadows();
        return map;
    }

    private static Vector3 ToWorld(float x, float y, float z) =>
        new Vector3(x, z - 1f, y) * VoxelSize;

    // Content is read into `frame` (the map itself, or one placed template instance); a template's own
    // building id is replaced by the instance's.
    private Transform frame;
    private int buildingOverride = -1;
    private Transform debris;

    private void InitMaterial(byte[] paletteData)
    {
        palette = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        palette.name = "Suburban town palette";
        palette.filterMode = FilterMode.Point;
        palette.wrapMode = TextureWrapMode.Clamp;
        paletteBytes = paletteData;
        palette.LoadRawTextureData(paletteBytes);
        palette.Apply(false, true);
        material = new Material(Shader.Find("Standard"));
        material.mainTexture = palette;
        material.SetFloat("_Glossiness", 0f);
        debris = new GameObject("Debris").transform;
        debris.SetParent(transform, false);
        frame = transform;
    }

    private void SetGround(Mesh ground)
    {
        GetComponent<MeshFilter>().sharedMesh = ground;
        GetComponent<MeshCollider>().sharedMesh = ground;
        var renderer = GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.TwoSided;
    }

    // A whole prebuilt town (the older single-file maps).
    private void Build(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        InitMaterial(reader.ReadBytes(1024));
        var staticMesh = ReadMesh(reader, Vector3.zero);
        SetGround(staticMesh);
        var structures = new Dictionary<int, BuildingStructure>();
        var power = gameObject.AddComponent<PowerSystem>();
        frame = transform; buildingOverride = -1;
        ReadContent(reader, structures, power, true);
        FinishBuild(staticMesh, structures, power);
    }

    // One template (building, vehicle, playground piece or decoration) into a placed, rotated instance.
    private void LoadTemplate(byte[] blob, Transform instance, int buildingId, Dictionary<int, BuildingStructure> structures,
        PowerSystem power)
    {
        using var reader = new BinaryReader(new MemoryStream(blob));
        reader.ReadBytes(1024);
        var shell = ReadMesh(reader, Vector3.zero);
        if (shell.vertexCount > 0)
        {
            var part = new GameObject("Static");
            part.transform.SetParent(instance, false);
            part.AddComponent<MeshFilter>().sharedMesh = shell;
            var shellRenderer = part.AddComponent<MeshRenderer>();
            shellRenderer.sharedMaterial = material;
            shellRenderer.shadowCastingMode = ShadowCastingMode.TwoSided;
            part.AddComponent<MeshCollider>().sharedMesh = shell;
        }
        frame = instance; buildingOverride = buildingId;
        ReadContent(reader, structures, power, false);
        frame = transform; buildingOverride = -1;
    }

    private void ReadContent(BinaryReader reader, Dictionary<int, BuildingStructure> structures, PowerSystem power, bool spawns)
    {
        int blockCount = reader.ReadInt32();
        for (int i = 0; i < blockCount; i++)
        {
            int buildingId = reader.ReadInt16();
            if (buildingOverride >= 0) buildingId = buildingOverride;
            int x = reader.ReadInt16();
            int z = reader.ReadInt16();
            int y = reader.ReadInt16();
            int blockMaterial = reader.ReadInt16();
            if (!structures.TryGetValue(buildingId, out var structure))
            {
                var building = new GameObject($"Building {buildingId}");
                building.transform.SetParent(frame, false);
                structure = building.AddComponent<BuildingStructure>();
                structures.Add(buildingId, structure);
            }

            string id = $"Suburb/{buildingId}/{x}/{y}/{z}";
            var obj = new GameObject(id);
            obj.transform.SetParent(structure.transform, false);
            Vector3 origin = ToWorld(x * BlockSize + BlockSize * 0.5f,
                z * BlockSize + BlockSize * 0.5f, y * BlockSize + 1 + BlockSize * 0.5f);
            obj.transform.localPosition = origin;
            obj.AddComponent<MeshFilter>().sharedMesh = ReadMesh(reader, origin);
            var blockRenderer = obj.AddComponent<MeshRenderer>();
            blockRenderer.sharedMaterial = material;
            blockRenderer.shadowCastingMode = ShadowCastingMode.TwoSided;
            var block = obj.AddComponent<DestructibleBlock>();
            block.Initialize(id, debris);
            block.SetStructure(structure, new Vector3Int(x, y, z));
            block.SetMaterial((byte)blockMaterial);

            int boxCount = reader.ReadInt32();
            for (int boxIndex = 0; boxIndex < boxCount; boxIndex++)
            {
                int bx = reader.ReadInt16(), bz = reader.ReadInt16(), by = reader.ReadInt16();
                int width = reader.ReadInt16(), depth = reader.ReadInt16(), height = reader.ReadInt16();
                var collider = boxIndex == 0 ? obj.GetComponent<BoxCollider>() : obj.AddComponent<BoxCollider>();
                collider.size = new Vector3(width, height, depth) * VoxelSize;
                collider.center = ToWorld(bx + width * 0.5f, bz + depth * 0.5f, by + height * 0.5f) - origin;
            }
        }
        if (reader.BaseStream.Position < reader.BaseStream.Length)
            ReadWindows(reader);
        if (reader.BaseStream.Position < reader.BaseStream.Length)
            ReadDoors(reader);
        if (reader.BaseStream.Length - reader.BaseStream.Position >= 24)
        {
            Vector3 human = ToWorld(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            Vector3 drone = ToWorld(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            if (spawns) { HumanSpawn = human; DroneSpawn = drone; }
        }
        if (reader.BaseStream.Position < reader.BaseStream.Length) ReadProps(reader);
        if (reader.BaseStream.Position < reader.BaseStream.Length) ReadLights(reader, structures, power);
    }

    // Shared by both kinds of map once every building is in place.
    private void FinishBuild(Mesh ground, Dictionary<int, BuildingStructure> structures, PowerSystem power)
    {
        // Which doors lock, and from which side, follows from the finished geometry.
        Physics.SyncTransforms();
        foreach (var door in GetComponentsInChildren<InteractiveDoor>()) door.ConfigureLock();
        foreach (var prop in GetComponentsInChildren<PhysicsProp>())
        {
            bool tv = prop.Label.Contains("Телевизор");
            if (!tv && !prop.Label.Contains("Радио")) continue;
            var device = prop.gameObject.AddComponent<PoweredDevice>();
            DestructibleBlock nearest = null;
            float distance = float.PositiveInfinity;
            foreach (var structure in structures.Values)
                foreach (var block in structure.GetComponentsInChildren<DestructibleBlock>())
                {
                    float d = (block.transform.position - prop.transform.position).sqrMagnitude;
                    if (d < distance) { nearest = block; distance = d; }
                }
            int building = nearest != null ? int.Parse(nearest.Structure.name.Substring(9)) : 0;
            power.Register(device, building, nearest);
            var display = GameObject.CreatePrimitive(PrimitiveType.Cube);
            display.name = "Power indicator"; Destroy(display.GetComponent<Collider>());
            display.transform.SetParent(prop.transform, false);
            var bounds = prop.GetComponent<MeshFilter>().sharedMesh.bounds;
            display.transform.localPosition = bounds.center + Vector3.forward * (bounds.extents.z + 0.008f);
            display.transform.localScale = tv ? new Vector3(bounds.size.x * 0.7f, bounds.size.y * 0.65f, 0.012f) : new Vector3(0.04f, 0.04f, 0.012f);
            device.Initialize(tv ? "телевизор" : "радио", null, tv ? SoundBank.TvLoop : SoundBank.RadioLoop,
                display.GetComponent<Renderer>());
        }
        power.InitializeStates();

        CreateBounds(ground);
        Inventory.Reset();
        if (MatchRules.Current.Items) ScatterItems(new System.Random(MatchRules.Current.ResolvedSeed));
        gameObject.AddComponent<MapRenderBatcher>().ShadowMode = ShadowCastingMode.TwoSided;
    }

    // ---------------- play area ----------------

    // The town's ground plan; the fence at its edge is part of the map geometry.
    public static Bounds PlayArea { get; private set; }

    // Behind the perimeter fence, out of every street: tall walls nobody can climb or jump past,
    // so a person cannot leave the town even through a blown-out fence.
    private void CreateBounds(Mesh ground)
    {
        Bounds local = ground.bounds;
        Vector3 min = transform.TransformPoint(local.min), max = transform.TransformPoint(local.max);
        var area = new Bounds((min + max) * 0.5f, new Vector3(Mathf.Abs(max.x - min.x), 0f, Mathf.Abs(max.z - min.z)));
        PlayArea = area;
        var root = new GameObject("Play area bounds");
        root.transform.SetParent(transform, true);
        const float thickness = 2f, height = 80f;
        Vector3 c = area.center, e = area.extents;
        void Wall(Vector3 centre, Vector3 size)
        {
            var box = root.AddComponent<BoxCollider>();
            box.center = root.transform.InverseTransformPoint(centre);
            box.size = size;
        }
        float y = c.y + height * 0.5f - 5f;
        Wall(new Vector3(c.x, y, area.max.z + thickness * 0.5f), new Vector3(e.x * 2f + thickness * 2f, height, thickness));
        Wall(new Vector3(c.x, y, area.min.z - thickness * 0.5f), new Vector3(e.x * 2f + thickness * 2f, height, thickness));
        Wall(new Vector3(area.max.x + thickness * 0.5f, y, c.z), new Vector3(thickness, height, e.z * 2f));
        Wall(new Vector3(area.min.x - thickness * 0.5f, y, c.z), new Vector3(thickness, height, e.z * 2f));
    }

    // ---------------- pocket items ----------------

    private static Material stoneMaterial, flashlightMaterial, panelMaterial, strangeMaterial;

    // Stones on lawns and pavements, flashlights on the floors of some rooms. Placed from the round's
    // seed and the map geometry only, so every peer builds the same props with the same indices.
    private void ScatterItems(System.Random random)
    {
        if (stoneMaterial == null)
        {
            stoneMaterial = new Material(Shader.Find("Standard")) { name = "Stone", color = new Color(0.46f, 0.45f, 0.43f) };
            stoneMaterial.SetFloat("_Glossiness", 0.05f);
            flashlightMaterial = new Material(Shader.Find("Standard")) { name = "Flashlight", color = new Color(0.08f, 0.08f, 0.09f) };
            flashlightMaterial.SetFloat("_Glossiness", 0.5f);
        }
        Physics.SyncTransforms();
        var root = new GameObject("Pocket items").transform;
        root.SetParent(transform, false);
        Bounds area = PlayArea;
        int stones = 0;
        for (int attempt = 0; attempt < 400 && stones < 45; attempt++)
        {
            float x = Mathf.Lerp(area.min.x + 3f, area.max.x - 3f, (float)random.NextDouble());
            float z = Mathf.Lerp(area.min.z + 3f, area.max.z - 3f, (float)random.NextDouble());
            // Outdoors only: the first thing straight down must be the town's ground.
            if (!Physics.Raycast(new Vector3(x, area.center.y + 60f, z), Vector3.down, out var hit, 80f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || hit.collider.gameObject != gameObject ||
                hit.normal.y < 0.8f) continue;
            float size = Mathf.Lerp(0.09f, 0.15f, (float)random.NextDouble());
            var stone = Item(root, "Stone " + stones, PrimitiveType.Cube, hit.point + Vector3.up * (size * 0.5f + 0.01f),
                Quaternion.Euler(0f, (float)random.NextDouble() * 360f, (float)random.NextDouble() * 20f),
                new Vector3(size * 1.2f, size * 0.75f, size), stoneMaterial);
            stone.AddComponent<PhysicsProp>().Initialize(-1, "Камень", Mathf.Lerp(0.6f, 1.6f, size / 0.15f), 400f);
            stones++;
        }
        // One strange stone out on the street: violet, faintly glowing, and a gamble to pick up.
        for (int attempt = 0; attempt < 200; attempt++)
        {
            float x = Mathf.Lerp(area.min.x + 6f, area.max.x - 6f, (float)random.NextDouble());
            float z = Mathf.Lerp(area.min.z + 6f, area.max.z - 6f, (float)random.NextDouble());
            if (!Physics.Raycast(new Vector3(x, area.center.y + 60f, z), Vector3.down, out var hit, 80f,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || hit.collider.gameObject != gameObject ||
                hit.normal.y < 0.8f) continue;
            if (strangeMaterial == null)
            {
                strangeMaterial = new Material(Shader.Find("Standard")) { name = "Strange stone", color = new Color(0.45f, 0.1f, 0.85f) };
                strangeMaterial.EnableKeyword("_EMISSION");
                strangeMaterial.SetColor("_EmissionColor", new Color(0.55f, 0.12f, 1f) * 1.6f);
                strangeMaterial.globalIlluminationFlags = MaterialGlobalIlluminationFlags.RealtimeEmissive;
            }
            var strange = Item(root, "Strange stone", PrimitiveType.Cube, hit.point + Vector3.up * 0.08f,
                Quaternion.Euler(0f, (float)random.NextDouble() * 360f, 12f), new Vector3(0.17f, 0.12f, 0.15f), strangeMaterial);
            strange.AddComponent<PhysicsProp>().Initialize(-1, "Странный камень", 1.4f, 400f);
            strange.AddComponent<CursedStone>();
            var glow = new GameObject("Glow").AddComponent<Light>();
            glow.transform.SetParent(strange.transform, false);
            glow.type = LightType.Point; glow.color = new Color(0.6f, 0.15f, 1f);
            glow.range = 2.5f; glow.intensity = 1.6f; glow.shadows = LightShadows.None;
            break;
        }
        int flashlights = 0;
        foreach (var device in GetComponentsInChildren<PoweredDevice>())
        {
            if (device.Lamp == null || device.MotionSensor || random.NextDouble() > 0.2) continue;
            Vector3 above = device.Lamp.transform.position + new Vector3((float)random.NextDouble() - 0.5f, -0.2f,
                (float)random.NextDouble() - 0.5f) * 1.6f;
            if (!Physics.Raycast(above, Vector3.down, out var floor, 3.5f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) ||
                floor.normal.y < 0.8f) continue;
            var torch = Item(root, "Flashlight " + flashlights, PrimitiveType.Cylinder, floor.point + Vector3.up * 0.03f,
                Quaternion.Euler(90f, (float)random.NextDouble() * 360f, 0f), new Vector3(0.05f, 0.1f, 0.05f), flashlightMaterial);
            Destroy(torch.GetComponent<Collider>());
            var box = torch.AddComponent<BoxCollider>();
            box.size = new Vector3(1f, 2f, 1f);
            torch.AddComponent<PhysicsProp>().Initialize(-1, "Фонарик", 0.5f, 300f);
            flashlights++;
        }
    }

    private static GameObject Item(Transform parent, string name, PrimitiveType shape, Vector3 position, Quaternion rotation,
        Vector3 scale, Material look)
    {
        var item = GameObject.CreatePrimitive(shape);
        item.name = name;
        item.transform.SetParent(parent, true);
        item.transform.SetPositionAndRotation(position, rotation);
        item.transform.localScale = scale;
        item.GetComponent<Renderer>().sharedMaterial = look;
        item.AddComponent<Rigidbody>();
        return item;
    }

    private void ReadWindows(BinaryReader reader)
    {
        const float glassThickness = 0.025f;
        int paneCount = reader.ReadInt32();
        if (paneCount == 0) return;

        var windows = new GameObject("Window glass").transform;
        windows.SetParent(frame, false);
        for (int i = 0; i < paneCount; i++)
        {
            int buildingId = reader.ReadInt16();
            int x = reader.ReadInt16();
            int y = reader.ReadInt16();
            int z = reader.ReadInt16();
            int width = reader.ReadInt16();
            int height = reader.ReadInt16();
            bool facesX = reader.ReadInt16() == 0;
            Vector3 center = ToWorld(x + (facesX ? 0f : width * 0.5f),
                y + (facesX ? width * 0.5f : 0f), z + height * 0.5f);
            Quaternion orientation = facesX ? Quaternion.Euler(0f, 90f, 0f) : Quaternion.identity;
            var size = new Vector3(width * VoxelSize, height * VoxelSize, glassThickness);
            if (buildingOverride >= 0) buildingId = buildingOverride;
            BreakableWindow.Create(windows, $"Window {buildingId} ({x}, {y}, {z})", center, orientation, size);
        }
    }

    private void ReadDoors(BinaryReader reader)
    {
        const float panelThickness = 0.07f;
        int count = reader.ReadInt32();
        if (count == 0) return;
        var doors = new GameObject("Doors").transform;
        doors.SetParent(frame, false);
        for (int i = 0; i < count; i++)
        {
            int buildingId = reader.ReadInt16();
            int x = reader.ReadInt16(), y = reader.ReadInt16(), z = reader.ReadInt16();
            int width = reader.ReadInt16(), height = reader.ReadInt16();
            bool facesX = reader.ReadInt16() == 0;
            bool hingeAtEnd = reader.ReadInt16() != 0;
            int swing = reader.ReadInt16();
            int colour = reader.ReadInt16();
            // The pane lies in the middle of the wall; the hinge is at one end of its span.
            Vector3 start = ToWorld(x, y, z);
            Vector3 end = facesX ? ToWorld(x, y + width, z) : ToWorld(x + width, y, z);
            Color? tint = colour > 0 ? new Color32(paletteBytes[(colour - 1) * 4], paletteBytes[(colour - 1) * 4 + 1],
                paletteBytes[(colour - 1) * 4 + 2], 255) : (Color?)null;
            start = frame.TransformPoint(start); end = frame.TransformPoint(end);
            if (buildingOverride >= 0) buildingId = buildingOverride;
            InteractiveDoor.Create(doors, $"Door {buildingId} ({x}, {y}, {z})", hingeAtEnd ? end : start,
                hingeAtEnd ? start : end, height * VoxelSize, panelThickness, Mathf.Clamp(swing, -1, 1), tint);
        }
    }

    // Loose physics objects: their own mesh and colliders, a real mass and health.
    private void ReadProps(BinaryReader reader)
    {
        // Kind names come with the map, so adding a kind of object needs no code change.
        var labels = new string[reader.ReadInt32()];
        for (int i = 0; i < labels.Length; i++) labels[i] = reader.ReadString();
        int count = reader.ReadInt32();
        if (count == 0) return;
        var root = new GameObject("Props").transform;
        root.SetParent(frame, false);
        for (int i = 0; i < count; i++)
        {
            int kind = reader.ReadInt16();
            float mass = reader.ReadSingle();
            int health = reader.ReadInt16();
            int ox = reader.ReadInt16(), oy = reader.ReadInt16(), oz = reader.ReadInt16();
            Vector3 origin = ToWorld(ox, oy, oz);
            var obj = new GameObject($"Prop {kind} ({ox}, {oy}, {oz})");
            obj.transform.SetParent(root, false);
            obj.transform.localPosition = origin;
            obj.AddComponent<MeshFilter>().sharedMesh = ReadMesh(reader, origin);
            var propRenderer = obj.AddComponent<MeshRenderer>();
            propRenderer.sharedMaterial = material;
            int boxCount = reader.ReadInt32();
            for (int b = 0; b < boxCount; b++)
            {
                int bx = reader.ReadInt16(), bz = reader.ReadInt16(), by = reader.ReadInt16();
                int width = reader.ReadInt16(), depth = reader.ReadInt16(), height = reader.ReadInt16();
                var collider = obj.AddComponent<BoxCollider>();
                collider.size = new Vector3(width, height, depth) * VoxelSize;
                collider.center = ToWorld(bx + width * 0.5f, bz + depth * 0.5f, by + height * 0.5f) - origin;
            }
            obj.AddComponent<Rigidbody>();
            obj.AddComponent<PhysicsProp>().Initialize(kind, kind < labels.Length ? labels[kind] : "Предмет", mass, health);
            if (!MatchRules.Current.Items) { obj.SetActive(false); Destroy(obj); }
        }
    }

    private void ReadLights(BinaryReader reader, Dictionary<int, BuildingStructure> structures, PowerSystem power)
    {
        int count = reader.ReadInt32();
        // Sensor indices in the file count from this file's first lamp.
        int firstDevice = power.DeviceCount;
        // Block colliders were created this frame: queries below must see them.
        Physics.SyncTransforms();
        for (int i = 0; i < count; i++)
        {
            int building = reader.ReadInt16();
            if (buildingOverride >= 0) building = buildingOverride;
            Vector3 lampAt = frame.TransformPoint(ToWorld(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
            Vector3 switchAt = frame.TransformPoint(ToWorld(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle()));
            Vector3 normal = frame.TransformDirection(new Vector3(reader.ReadSByte(), 0f, reader.ReadSByte())).normalized;
            if (normal.sqrMagnitude < 0.5f) normal = frame.forward;
            structures.TryGetValue(building, out var structure);

            // The lamp hangs from the ceiling block above it and comes down with it.
            var fixture = GameObject.CreatePrimitive(PrimitiveType.Cube);
            fixture.name = "Room lamp " + i;
            Destroy(fixture.GetComponent<Collider>());
            Transform ceiling = frame;
            if (Physics.Raycast(lampAt - Vector3.up * 0.05f, Vector3.up, out var above, 1.2f, Physics.DefaultRaycastLayers,
                    QueryTriggerInteraction.Ignore))
            {
                lampAt = above.point - Vector3.up * 0.03f;
                var block = above.collider.GetComponent<DestructibleBlock>();
                if (block != null) ceiling = block.transform;
            }
            fixture.transform.SetParent(ceiling, false);
            fixture.transform.position = lampAt;
            fixture.transform.rotation = frame.rotation;
            fixture.transform.localScale = Vector3.Scale(new Vector3(0.32f, 0.06f, 0.32f), InverseScale(ceiling));
            var light = fixture.AddComponent<Light>(); light.type = LightType.Point;
            light.color = new Color(1f, 0.83f, 0.58f); light.range = 6f; light.intensity = 1.4f;
            light.shadows = LightShadows.None;

            // The switch is mounted on the actual wall surface (the authored point can sit in a doorway
            // or a gap), and is a child of that wall block, so it never floats once the wall is gone.
            bool mounted = FindMount(switchAt, normal, structure, out var support, out Vector3 mountPoint, out Vector3 mountNormal);
            var toggle = GameObject.CreatePrimitive(PrimitiveType.Cube);
            toggle.name = "Light switch " + i;
            toggle.transform.SetParent(support != null ? support.transform : frame, false);
            toggle.transform.position = mountPoint + mountNormal * 0.024f;
            toggle.transform.rotation = Quaternion.LookRotation(mountNormal);
            toggle.transform.localScale = Vector3.Scale(new Vector3(0.1f, 0.16f, 0.045f), InverseScale(toggle.transform.parent));
            if (!mounted)
            {
                // No wall anywhere near: a switch in mid-air is worse than none. The lamp keeps its circuit.
                toggle.GetComponent<Renderer>().enabled = false;
                toggle.GetComponent<Collider>().enabled = false;
            }
            var device = toggle.AddComponent<PoweredDevice>();
            power.Register(device, building, support);
            device.Initialize("свет", light, null, fixture.GetComponent<Renderer>());
            // Every lit building gets a breaker panel outside on its facade (placed after all lamps).
            if (structure != null) panelSpots[building] = structure;
        }
        if (reader.BaseStream.Position < reader.BaseStream.Length)
        {
            int sensors = reader.ReadInt32();
            for (int i = 0; i < sensors; i++) power.SetMotionSensor(firstDevice + reader.ReadInt32());
        }
        // Breakers come after every lamp so the lamp indices stored in the map stay valid.
        var buildingIds = new List<int>(panelSpots.Keys);
        buildingIds.Sort();
        foreach (int building in buildingIds) CreateBreaker(building, panelSpots[building], power);
        panelSpots.Clear();
    }

    private readonly Dictionary<int, BuildingStructure> panelSpots = new Dictionary<int, BuildingStructure>();

    private static bool Indoors(Vector3 point) =>
        Physics.Raycast(point, Vector3.up, 6f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);

    // A small electrical panel outside on the building's ground-floor facade, at chest height: on the street
    // side next to the entrance where the building has a front (generated towns), else the first outer wall.
    // E cuts or restores power to the whole building. Found from geometry only, so every peer agrees.
    private void CreateBreaker(int building, BuildingStructure structure, PowerSystem power)
    {
        Bounds ground = default;
        bool any = false;
        foreach (var block in structure.GetComponentsInChildren<DestructibleBlock>())
        {
            if (block.GridCell.y != 0 || block.IsDetached) continue;
            foreach (var box in block.GetComponents<BoxCollider>())
            {
                if (!any) { ground = box.bounds; any = true; }
                else ground.Encapsulate(box.bounds);
            }
        }
        if (!any) return;
        // Templates face their street along local -z; the old single-file map has no notion of a front.
        Vector3 front = frame != transform ? -frame.forward : Vector3.back;
        var directions = new[] { front, Vector3.Cross(Vector3.up, front), -Vector3.Cross(Vector3.up, front), -front };
        float height = ground.min.y + 1.3f;
        DestructibleBlock support = null;
        Vector3 at = default, normal = default;
        foreach (var dir in directions)
        {
            Vector3 right = Vector3.Cross(Vector3.up, dir);
            float reach = Mathf.Abs(Vector3.Dot(ground.extents, new Vector3(Mathf.Abs(dir.x), 0f, Mathf.Abs(dir.z)))) + 1.5f;
            float span = Mathf.Abs(Vector3.Dot(ground.extents, new Vector3(Mathf.Abs(right.x), 0f, Mathf.Abs(right.z))));
            // Beside the entrance rather than in its opening: try points along the facade away from its middle.
            foreach (float offset in new[] { 1.2f, -1.2f, 2.2f, -2.2f, 3.2f, -3.2f, 0.4f, -0.4f })
            {
                if (Mathf.Abs(offset) > span - 0.4f) continue;
                Vector3 start = new Vector3(ground.center.x, height, ground.center.z) + dir * reach + right * offset;
                if (!Physics.Raycast(start, -dir, out var hit, reach + 1f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                    continue;
                var block = hit.collider.GetComponent<DestructibleBlock>();
                if (block == null || block.Structure != structure || Vector3.Dot(hit.normal, dir) < 0.9f ||
                    Indoors(hit.point + hit.normal * 0.35f)) continue;
                support = block; at = hit.point; normal = hit.normal;
                break;
            }
            if (support != null) break;
        }
        if (support == null) return;
        var panel = GameObject.CreatePrimitive(PrimitiveType.Cube);
        panel.name = "Electrical panel " + building;
        panel.transform.SetParent(support.transform, false);
        // A small flush box (20 x 28 x 4 cm), like a household meter cabinet.
        panel.transform.position = at + normal * 0.021f;
        panel.transform.rotation = Quaternion.LookRotation(normal);
        panel.transform.localScale = Vector3.Scale(new Vector3(0.2f, 0.28f, 0.04f), InverseScale(panel.transform.parent));
        var spot = (point: at, normal, support);
        var lamp = GameObject.CreatePrimitive(PrimitiveType.Cube);
        lamp.name = "Indicator";
        Destroy(lamp.GetComponent<Collider>());
        lamp.transform.SetParent(panel.transform, false);
        lamp.transform.localPosition = new Vector3(0.3f, 0.35f, 0.52f);
        lamp.transform.localScale = new Vector3(0.12f, 0.08f, 0.05f);
        // Its own painted-steel look: the voxel palette is a 256 x 1 strip and smears into stripes on a plain cube.
        if (panelMaterial == null)
        {
            panelMaterial = new Material(Shader.Find("Standard")) { name = "Electrical panel", color = new Color(0.58f, 0.6f, 0.6f) };
            panelMaterial.SetFloat("_Metallic", 0.55f);
            panelMaterial.SetFloat("_Glossiness", 0.35f);
        }
        panel.GetComponent<Renderer>().sharedMaterial = panelMaterial;
        var device = panel.AddComponent<PoweredDevice>();
        device.Breaker = true;
        power.Register(device, building, spot.support);
        device.Initialize("щиток", null, null, lamp.GetComponent<Renderer>());
    }

    // ---------------- surfaces (footsteps) ----------------

    public static SuburbanTrainingMap Current { get; private set; }
    private void Awake() => Current = this;

    // What a foot lands on, from the voxel colour under it: lawns and earth are soft, wood tones are boards,
    // painted vehicle panels ring like metal, everything else (asphalt, tiles, concrete) is hard.
    public static StepSurface Surface(RaycastHit hit)
    {
        var map = Current;
        if (map == null || hit.collider == null) return StepSurface.Concrete;
        var block = hit.collider.GetComponent<DestructibleBlock>();
        int colour = -1;
        if (block == null && hit.collider is MeshCollider && hit.collider.GetComponentInParent<SuburbanTrainingMap>() == map)
            colour = Mathf.RoundToInt(hit.textureCoord.x * 256f + 0.5f) - 1;
        else if (block != null) colour = TopColour(block, hit.point);
        if (colour < 0 || colour > 255 || map.paletteBytes == null)
            return block != null && block.Material == 3 ? StepSurface.Concrete : block != null ? StepSurface.Wood : StepSurface.Concrete;
        float r = map.paletteBytes[colour * 4] / 255f, g = map.paletteBytes[colour * 4 + 1] / 255f, b = map.paletteBytes[colour * 4 + 2] / 255f;
        Color.RGBToHSV(new Color(r, g, b), out float hue, out float saturation, out float value);
        // Vehicles are the only metal you walk on in the town (roofs, bonnets).
        if (block != null && block.Structure != null && block.Structure.transform.parent != null &&
            block.Structure.transform.parent.name.StartsWith("vehicle")) return StepSurface.Metal;
        if (saturation > 0.25f && hue > 0.17f && hue < 0.45f)                                       // lawn, hedge, moss
            return block != null ? StepSurface.Soft : StepSurface.Grass;
        if (saturation > 0.3f && hue >= 0.03f && hue <= 0.12f && value < 0.75f)                     // boards, parquet, earth
            return block != null ? StepSurface.Wood : StepSurface.Grass;
        // Indoors a coloured floor is a rug or carpet; grey ones are tiles or concrete.
        if (block != null && saturation > 0.3f) return StepSurface.Soft;
        return block != null && block.Material != 3 && value > 0.25f && saturation > 0.15f ? StepSurface.Wood : StepSurface.Concrete;
    }

    // Palette index of the upward face of a block under a point (blocks use boxes, which carry no UVs).
    private static int TopColour(DestructibleBlock block, Vector3 point)
    {
        var filter = block.GetComponent<MeshFilter>();
        var mesh = filter != null ? filter.sharedMesh : null;
        if (mesh == null || !mesh.isReadable) return -1;
        Vector3 local = block.transform.InverseTransformPoint(point);
        var vertices = mesh.vertices;
        var uv = mesh.uv;
        float best = float.PositiveInfinity;
        int colour = -1;
        for (int i = 0; i + 3 < vertices.Length; i += 4)
        {
            Vector3 a = vertices[i], c = vertices[i + 2];
            if (Mathf.Abs(a.y - c.y) > 0.001f || Mathf.Abs(vertices[i + 1].y - a.y) > 0.001f) continue;
            if (local.x < Mathf.Min(a.x, c.x) - 0.01f || local.x > Mathf.Max(a.x, c.x) + 0.01f ||
                local.z < Mathf.Min(a.z, c.z) - 0.01f || local.z > Mathf.Max(a.z, c.z) + 0.01f) continue;
            float gap = Mathf.Abs(local.y - a.y);
            if (gap >= best) continue;
            best = gap;
            colour = Mathf.RoundToInt(uv[i].x * 256f + 0.5f) - 1;
        }
        return best < 0.15f ? colour : -1;
    }

    private static Vector3 InverseScale(Transform parent)
    {
        Vector3 s = parent.lossyScale;
        return new Vector3(1f / Mathf.Max(0.0001f, s.x), 1f / Mathf.Max(0.0001f, s.y), 1f / Mathf.Max(0.0001f, s.z));
    }

    private static readonly RaycastHit[] mountHits = new RaycastHit[16];

    // The wall a switch authored at `at` (facing `normal`) really sits on: first straight back into the wall,
    // then the nearest wall in any horizontal direction (a switch authored mid-corridor). A building's own
    // block, or the map's static shell (cellars); `block` is null for the latter. False when there is no wall.
    private bool FindMount(Vector3 at, Vector3 normal, BuildingStructure structure, out DestructibleBlock best,
        out Vector3 point, out Vector3 surfaceNormal)
    {
        point = at; surfaceNormal = normal;
        bool found = false;
        Vector3 side = Vector3.Cross(Vector3.up, normal);
        var directions = new[] { -normal, normal, side, -side };
        best = null;
        float bestDistance = float.PositiveInfinity;
        for (int d = 0; d < directions.Length; d++)
        {
            Vector3 direction = directions[d];
            // Start slightly in front of the authored face so the wall itself is hit from the room side.
            Vector3 origin = at - direction * 0.3f;
            int count = Physics.RaycastNonAlloc(origin, direction, mountHits, 1.6f, Physics.DefaultRaycastLayers,
                QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var hit = mountHits[i];
                if (Mathf.Abs(hit.normal.y) > 0.3f || hit.distance <= 0f) continue;
                var block = hit.collider.GetComponent<DestructibleBlock>();
                bool shell = block == null && hit.collider.gameObject == gameObject;
                if (!shell && (block == null || block.IsDetached || (structure != null && block.Structure != structure))) continue;
                // The authored wall wins outright; other walls only when it is missing.
                float score = hit.distance + d * 0.5f;
                if (score >= bestDistance) continue;
                bestDistance = score; best = block; found = true; point = hit.point; surfaceNormal = hit.normal;
            }
            if (found && d == 0) break;
        }
        return found;
    }

    private Mesh ReadMesh(BinaryReader reader, Vector3 origin)
    {
        int count = reader.ReadInt32();
        var vertices = new Vector3[count * 4];
        var uv = new Vector2[vertices.Length];
        var indices = new int[count * 6];
        for (int i = 0; i < count; i++)
        {
            int axis = reader.ReadByte();
            int sign = reader.ReadSByte();
            int plane = reader.ReadInt16();
            int u = reader.ReadInt16();
            int v = reader.ReadInt16();
            int width = reader.ReadInt16();
            int height = reader.ReadInt16();
            float color = (reader.ReadByte() - 0.5f) / 256f;

            int first = i * 4;
            vertices[first] = Corner(axis, plane, u, v) - origin;
            vertices[first + 1] = Corner(axis, plane, u + width, v) - origin;
            vertices[first + 2] = Corner(axis, plane, u + width, v + height) - origin;
            vertices[first + 3] = Corner(axis, plane, u, v + height) - origin;
            for (int corner = 0; corner < 4; corner++) uv[first + corner] = new Vector2(color, 0.5f);

            // Swapping VOX Y/Z reverses the winding in Unity coordinates.
            int start = i * 6;
            indices[start] = indices[start + 3] = first;
            indices[start + 1] = first + (sign > 0 ? 2 : 1);
            indices[start + 2] = first + (sign > 0 ? 1 : 2);
            indices[start + 4] = first + (sign > 0 ? 3 : 2);
            indices[start + 5] = first + (sign > 0 ? 2 : 3);
        }

        var mesh = new Mesh { name = "Suburban geometry", indexFormat = vertices.Length > 65535
            ? IndexFormat.UInt32 : IndexFormat.UInt16 };
        mesh.vertices = vertices;
        mesh.uv = uv;
        mesh.triangles = indices;
        mesh.RecalculateNormals();
        mesh.RecalculateBounds();
        meshes.Add(mesh);
        return mesh;
    }

    private void ConfigureShadows()
    {
        foreach (var light in FindObjectsByType<Light>())
        {
            if (light.type != LightType.Directional || light.gameObject.scene != gameObject.scene)
                continue;

            light.shadowCustomResolution = 2048;
            light.shadowNormalBias = 0.06f;
            light.shadowBias = 0.02f;
        }
    }

    private static Vector3 Corner(int axis, int plane, int u, int v) => axis switch
    {
        0 => ToWorld(plane, u, v),
        1 => ToWorld(v, plane, u),
        _ => ToWorld(u, v, plane)
    };

    private void OnDestroy()
    {
        if (Current == this) { Current = null; PlayArea = default; }
        foreach (var mesh in meshes) Destroy(mesh);
        Destroy(material);
        Destroy(palette);
    }
}
