using System;
using UnityEngine;

/// <summary>Deterministic primitive map: modular shells, three simple interiors, no gameplay.</summary>
[DisallowMultipleComponent]
public class ProceduralMapGenerator : MonoBehaviour
{
    private enum BuildingType { House, Warehouse, Office }

    [Header("Map")]
    public int seed = 12345;
    [Tooltip("Ground width and length in world units.")]
    public Vector2 mapSize = new Vector2(60, 60);
    [Range(1, 16)] public int buildingCount = 4;
    [Tooltip("Minimum clear space between footprints, measured in blocks.")]
    [Min(1)] public float buildingSpacing = 2;

    [Header("Buildings")]
    [Tooltip("Width, wall height, depth in blocks. Width/depth use odd numbers, minimum 11 for accessible rooms.")]
    public Vector3Int buildingDimensions = new Vector3Int(11, 5, 11);
    [Min(0.25f)] public float blockSize = 1;
    [Tooltip("Turn off and regenerate to inspect interiors. Affects roof geometry and colliders.")]
    public bool generateRoofs = true;

    [Header("Interior lighting")]
    public bool enableInteriorLights = true;
    [Range(0, 6)] public int maxInteriorLights = 6;
    [Range(0.2f, 3f)] public float interiorLightIntensity = 1.2f;

    [Header("Solid colors")]
    public Material groundMaterial;
    public Material wallMaterial;
    public Material alternateWallMaterial;
    public Material roofMaterial;

    [SerializeField, HideInInspector] private GameObject generatedMap;

    [ContextMenu("Regenerate Map")]
    public void RegenerateMap()
    {
        ClampSettings();
        int columns = Mathf.CeilToInt(Mathf.Sqrt(buildingCount));
        int rows = Mathf.CeilToInt((float)buildingCount / columns);
        float cellWidth = mapSize.x / columns;
        float cellDepth = mapSize.y / rows;
        // A square reservation also contains a rectangular building after any quarter-turn.
        float footprint = Mathf.Max(buildingDimensions.x, buildingDimensions.z) * blockSize;
        float reservation = footprint + buildingSpacing * blockSize;
        if (cellWidth < reservation || cellDepth < reservation)
        {
            Debug.LogWarning($"Map too small: each placement cell needs at least {reservation:F1} world units. Increase Map Size or reduce count/dimensions/spacing.", this);
            return; // Keep the old map when the new settings cannot fit.
        }

        var random = new System.Random(seed); // Does not alter UnityEngine.Random's global state.
        int[] slots = new int[columns * rows];
        for (int i = 0; i < slots.Length; i++) slots[i] = i;
        for (int i = slots.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            int temp = slots[i]; slots[i] = slots[j]; slots[j] = temp;
        }
#if UNITY_EDITOR
        int undoGroup = -1;
        if (!Application.isPlaying)
        {
            UnityEditor.Undo.IncrementCurrentGroup();
            undoGroup = UnityEditor.Undo.GetCurrentGroup();
            UnityEditor.Undo.SetCurrentGroupName("Regenerate Map");
            UnityEditor.Undo.RecordObject(this, "Regenerate Map");
        }
#endif
        ClearGeneratedContent();
        generatedMap = Group("GeneratedMap", transform).gameObject;
        generatedMap.AddComponent<GeneratedMapRoot>().owner = this;
        CreateBox("Ground", generatedMap.transform, new Vector3(0, -0.25f, 0),
            new Vector3(mapSize.x, 0.5f, mapSize.y), groundMaterial);
        Group("Debris", generatedMap.transform);
        Transform buildings = Group("Buildings", generatedMap.transform);
        for (int i = 0; i < buildingCount; i++)
        {
            int slot = slots[i];
            float x = -mapSize.x / 2 + (slot % columns + 0.5f) * cellWidth;
            float z = -mapSize.y / 2 + (slot / columns + 0.5f) * cellDepth;
            x += ((float)random.NextDouble() - 0.5f) * (cellWidth - reservation);
            z += ((float)random.NextDouble() - 0.5f) * (cellDepth - reservation);
            var type = (BuildingType)random.Next(3);
            int rotation = random.Next(4) * 90;
            CreateBuilding(buildings, i, type, new Vector3(x, 0, z), rotation);
        }
        CreateInteriorLighting(buildings);
        if (enableInteriorLights)
            generatedMap.AddComponent<InteriorLightBudget>().maximumActive = maxInteriorLights;
#if UNITY_EDITOR
        if (!Application.isPlaying)
        {
            UnityEditor.Undo.RegisterCreatedObjectUndo(generatedMap, "Regenerate Map");
            UnityEditor.EditorUtility.SetDirty(this);
            UnityEditor.SceneManagement.EditorSceneManager.MarkSceneDirty(gameObject.scene);
            UnityEditor.Undo.CollapseUndoOperations(undoGroup);
        }
#endif
    }

    private void ClearGeneratedContent()
    {
        var oldRoots = new System.Collections.Generic.HashSet<GameObject>();
        if (generatedMap != null) oldRoots.Add(generatedMap);
        // Recover even if the hidden serialized reference was lost or an old root was moved.
        foreach (var marker in UnityEngine.Object.FindObjectsByType<GeneratedMapRoot>(FindObjectsInactive.Include))
            if (marker.owner == this && marker.gameObject.scene == gameObject.scene) oldRoots.Add(marker.gameObject);
        foreach (Transform child in transform)
            if (child.name == "GeneratedMap")
            {
                var marker = child.GetComponent<GeneratedMapRoot>();
                if (marker == null || marker.owner == this) oldRoots.Add(child.gameObject);
            }
        // Migration for the older unmarked, scene-root GeneratedMap representation.
        int generators = 0;
        foreach (var generator in UnityEngine.Object.FindObjectsByType<ProceduralMapGenerator>(FindObjectsInactive.Include))
            if (generator.gameObject.scene == gameObject.scene) generators++;
        if (generators == 1)
            foreach (var root in gameObject.scene.GetRootGameObjects())
                if (root.name == "GeneratedMap" && root.GetComponent<GeneratedMapRoot>() == null) oldRoots.Add(root);
        foreach (var root in oldRoots)
        {
            if (root == null) continue;
#if UNITY_EDITOR
            if (!Application.isPlaying)
            {
                UnityEditor.Undo.DestroyObjectImmediate(root);
                continue;
            }
#endif
            root.SetActive(false); // Physics and lights stop before deferred Play Mode destruction.
            Destroy(root);
        }
        generatedMap = null;
    }

    private void CreateBuilding(Transform parent, int index, BuildingType type, Vector3 position, int rotation)
    {
        Transform building = Group($"Building_{index + 1:00}_{type}", parent);
        building.gameObject.AddComponent<BuildingStructure>();
        building.localPosition = position;
        building.localRotation = Quaternion.Euler(0, rotation, 0);
        Transform structure = Group("Structure", building);
        Transform exterior = Group("ExteriorWalls", structure);
        Transform interior = Group("InteriorWalls", structure);
        Transform roof = Group("Roof", structure);
        Transform cover = Group("Cover", building);
        Transform decor = Group("Decorations", building);
        Transform spaces = Group("Spaces", building); // Named room/corridor markers for inspection.
        int w = buildingDimensions.x, h = buildingDimensions.y, d = buildingDimensions.z;
        int cx = w / 2, cz = d / 2;
        Material walls = type == BuildingType.Office ? alternateWallMaterial : wallMaterial;
        if (walls == null) walls = wallMaterial;

        for (int y = 0; y < h; y++)
        for (int z = 0; z < d; z++)
        for (int x = 0; x < w; x++)
        {
            bool edge = x == 0 || x == w - 1 || z == 0 || z == d - 1;
            if (!edge) continue;
            bool door = z == 0 && (x == cx || x == cx + 1) && y < (type == BuildingType.Warehouse ? 3 : 2);
            bool window = y == (type == BuildingType.Warehouse ? h - 2 : 2) &&
                (((z == 0 || z == d - 1) && (x == 2 || x == w - 3)) ||
                 ((x == 0 || x == w - 1) && (z == 2 || z == d - 3 || (type == BuildingType.Office && z == cz))));
            if (!door && !window) Block($"Wall_{x}_{y}_{z}", exterior, x, y, z, walls);
        }
        if (generateRoofs)
            for (int z = 0; z < d; z++)
            for (int x = 0; x < w; x++) Block($"Roof_{x}_{z}", roof, x, h, z, roofMaterial);

        if (type == BuildingType.House)
        {
            // A living room opens into two rear rooms through separate two-block doors.
            for (int x = 1; x < w - 1; x++)
                WallColumn(interior, x, cz, x == 2 || x == 3 || x == w - 4 || x == w - 3, walls);
            for (int z = cz + 1; z < d - 1; z++) WallColumn(interior, cx, z, false, walls);
            Space(spaces, "LivingRoom", cx, 2);
            Space(spaces, "Bedroom", cx - 1, d - 3);
            Space(spaces, "StorageRoom", w - 3, d - 3);
            Furniture("Wardrobe", cover, 2, d - 2, 2, 3, 1);
            Furniture("StorageCabinet", cover, w - 3, d - 2, 2, 3, 1);
            Furniture("Sofa", cover, 2, 2, 2, 1.5f, 1);
            Furniture("Bed", decor, 1.5f, d - 4, 2, 0.6f, 2);
        }
        else if (type == BuildingType.Warehouse)
        {
            // Open stock area, central aisle and a rear store room with a wide entrance.
            for (int x = 1; x < w - 1; x++) WallColumn(interior, x, d - 4, x == cx || x == cx + 1, walls);
            Space(spaces, "StorageHall", cx, 3);
            Space(spaces, "RearStoreRoom", cx, d - 2);
            for (int z = 3; z < d - 5; z += 3)
            {
                // Solid stock-filled racks: real opaque collider volumes, not decorative outlines.
                Furniture($"StockedRack_Left_{z}", cover, 2, z, 1, 3, 2);
                Furniture($"StockedRack_Right_{z}", cover, w - 3, z, 1, 3, 2);
            }
            Furniture("Crates", cover, 2, d - 2, 2, 2.5f, 1);
            Furniture("WorkBench", decor, w - 3, d - 2, 2, 1, 1);
        }
        else
        {
            // Three-block corridor, four offices; side-wall doorways connect every room.
            for (int z = 1; z < d - 1; z++)
            {
                bool door = z == 2 || z == 3 || z == d - 4 || z == d - 3;
                WallColumn(interior, cx - 2, z, door, walls);
                WallColumn(interior, cx + 2, z, door, walls);
            }
            for (int x = 1; x < w - 1; x++)
                if (x < cx - 2 || x > cx + 2) WallColumn(interior, x, cz, false, walls);
            Space(spaces, "Corridor", cx, cz);
            foreach (int x in new[] { 1, w - 2 })
            foreach (int z in new[] { 2, d - 3 })
            {
                Space(spaces, $"Office_{x}_{z}", x == 1 ? 2 : w - 3, z);
                Furniture($"FilingCabinet_{x}_{z}", cover, x, z, 1, 3, 1);
                Furniture($"Desk_{x}_{z}", decor, x, z + (z < cz ? 1 : -1), 1, 1, 1);
            }
        }
    }

    private void CreateInteriorLighting(Transform buildings)
    {
        if (!enableInteriorLights) return;
        // Separate random stream: lighting never changes the seeded map layout.
        var random = new System.Random(unchecked(seed ^ 0x51A7));
        int activeCount = 0;
        // Round-robin room order gives every building a light before extra rooms consume the cap.
        for (int roomIndex = 0; roomIndex < 5; roomIndex++)
        foreach (Transform building in buildings)
        {
            Transform rooms = building.Find("Spaces");
            if (roomIndex >= rooms.childCount) continue;
            Transform lighting = building.Find("Lighting");
            if (lighting == null) lighting = Group("Lighting", building);
            Transform room = rooms.GetChild(roomIndex);
            bool house = building.name.EndsWith("House");
            bool warehouse = building.name.EndsWith("Warehouse");
            Vector3 ceiling = room.localPosition + Vector3.up * (buildingDimensions.y - 0.12f) * blockSize;
            var fixture = CreateBox("Fixture_" + room.name, lighting, ceiling,
                new Vector3(warehouse ? 1.5f : 0.6f, 0.16f, 0.6f) * blockSize, roofMaterial);
            fixture.GetComponent<Collider>().enabled = false;
            Transform lamp = Group("Lamp", fixture.transform);
            // Parent is scaled geometry: world placement/rotation keep the spotlight predictable.
            lamp.position = building.TransformPoint(ceiling - Vector3.up * 0.25f * blockSize);
            lamp.rotation = building.rotation * Quaternion.Euler(90, 0, 0);
            var light = lamp.gameObject.AddComponent<Light>();
            light.type = LightType.Spot;
            light.color = house ? new Color(1f, 0.76f, 0.50f) : warehouse
                ? new Color(0.68f, 0.83f, 1f) : new Color(1f, 0.96f, 0.90f);
            light.intensity = interiorLightIntensity * (0.75f + (float)random.NextDouble() * 0.6f);
            light.range = Mathf.Max(buildingDimensions.y * 2f, buildingDimensions.x) * blockSize;
            light.spotAngle = warehouse ? 125f : 115f;
            light.innerSpotAngle = 65f;
            light.renderMode = LightRenderMode.ForcePixel;
            light.shadows = LightShadows.None;
            light.shadowResolution = UnityEngine.Rendering.LightShadowResolution.Low;
            light.shadowBias = 0.02f;
            light.shadowNormalBias = 0.2f;
            light.enabled = activeCount++ < maxInteriorLights;
        }
    }
    private void WallColumn(Transform parent, int x, int z, bool door, Material material)
    {
        for (int y = 0; y < buildingDimensions.y; y++)
            if (!door || y >= 2) Block($"Partition_{x}_{y}_{z}", parent, x, y, z, material);
    }

    private Vector3 GridPosition(float x, float y, float z) =>
        new Vector3(x - (buildingDimensions.x - 1) / 2f, y, z - (buildingDimensions.z - 1) / 2f) * blockSize;

    private void Block(string label, Transform parent, int x, int y, int z, Material material)
    {
        var block = MakeDestructible(CreateBox(label, parent, GridPosition(x, y + 0.5f, z), Vector3.one * blockSize, material));
        block.SetStructure(parent.GetComponentInParent<BuildingStructure>(), new Vector3Int(x,y,z));
    }

    private void Furniture(string label, Transform parent, float x, float z, float width, float height, float depth) =>
        MakeDestructible(CreateBox(label, parent, GridPosition(x, height / 2, z), new Vector3(width, height, depth) * blockSize, roofMaterial));

    private DestructibleBlock MakeDestructible(GameObject block)
    {
        string path = block.name;
        for (Transform p = block.transform.parent; p != generatedMap.transform; p = p.parent)
            path = p.name + "/" + path;
        var destructible = block.AddComponent<DestructibleBlock>();
        destructible.Initialize(seed + "/" + path, generatedMap.transform.Find("Debris"));
        return destructible;
    }

    private void Space(Transform parent, string label, int x, int z) =>
        Group(label, parent).localPosition = GridPosition(x, 0, z);

    private static Transform Group(string label, Transform parent)
    {
        Transform group = new GameObject(label).transform;
        group.SetParent(parent, false);
        return group;
    }

    private static GameObject CreateBox(string label, Transform parent, Vector3 position, Vector3 size, Material material)
    {
        GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
        cube.name = label;
        cube.transform.SetParent(parent, false);
        cube.transform.localPosition = position;
        cube.transform.localScale = size;
        if (material != null) cube.GetComponent<Renderer>().sharedMaterial = material;
        return cube;
    }

    private void OnValidate() => ClampSettings();
    private void ClampSettings()
    {
        mapSize = new Vector2(Mathf.Max(1, mapSize.x), Mathf.Max(1, mapSize.y));
        buildingCount = Mathf.Clamp(buildingCount, 1, 16);
        buildingDimensions = new Vector3Int(OddSize(buildingDimensions.x), Mathf.Max(4, buildingDimensions.y), OddSize(buildingDimensions.z));
        blockSize = Mathf.Max(0.25f, blockSize);
        buildingSpacing = Mathf.Max(1, buildingSpacing);
    }
    private static int OddSize(int value) => Mathf.Max(11, value) | 1;
}




