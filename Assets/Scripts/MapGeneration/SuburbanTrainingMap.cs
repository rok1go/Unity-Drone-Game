using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Rendering;

[RequireComponent(typeof(MeshFilter), typeof(MeshRenderer), typeof(MeshCollider))]
public sealed class SuburbanTrainingMap : MonoBehaviour
{
    private const float VoxelSize = 0.165f;
    public static readonly Vector3 HumanSpawn = ToWorld(-35.5f, -65.5f, 1.2f);
    public static readonly Vector3 DroneSpawn = ToWorld(0f, 20f, 112.5f);
    private const int BlockSize = 6;
    private readonly List<Mesh> meshes = new List<Mesh>();
    private Texture2D palette;
    private Material material;

    public static SuburbanTrainingMap Create(Transform parent)
    {
        var data = Resources.Load<TextAsset>("SmallSuburbanTown");
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

    private void Build(byte[] bytes)
    {
        using var reader = new BinaryReader(new MemoryStream(bytes));
        palette = new Texture2D(256, 1, TextureFormat.RGBA32, false);
        palette.name = "Suburban town palette";
        palette.filterMode = FilterMode.Point;
        palette.wrapMode = TextureWrapMode.Clamp;
        palette.LoadRawTextureData(reader.ReadBytes(1024));
        palette.Apply(false, true);

        material = new Material(Shader.Find("Standard"));
        material.mainTexture = palette;
        material.SetFloat("_Glossiness", 0f);

        var staticMesh = ReadMesh(reader, Vector3.zero);
        GetComponent<MeshFilter>().sharedMesh = staticMesh;
        GetComponent<MeshCollider>().sharedMesh = staticMesh;
        var renderer = GetComponent<MeshRenderer>();
        renderer.sharedMaterial = material;
        renderer.shadowCastingMode = ShadowCastingMode.TwoSided;

        var debris = new GameObject("Debris").transform;
        debris.SetParent(transform, false);
        var structures = new Dictionary<int, BuildingStructure>();
        int blockCount = reader.ReadInt32();
        for (int i = 0; i < blockCount; i++)
        {
            int buildingId = reader.ReadInt16();
            int x = reader.ReadInt16();
            int z = reader.ReadInt16();
            int y = reader.ReadInt16();
            if (!structures.TryGetValue(buildingId, out var structure))
            {
                var building = new GameObject($"Building {buildingId}");
                building.transform.SetParent(transform, false);
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

        gameObject.AddComponent<MapRenderBatcher>().ShadowMode = ShadowCastingMode.TwoSided;
    }

    private void ReadWindows(BinaryReader reader)
    {
        const float glassThickness = 0.025f;
        int paneCount = reader.ReadInt32();
        if (paneCount == 0) return;

        var windows = new GameObject("Window glass").transform;
        windows.SetParent(transform, false);
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
            BreakableWindow.Create(windows, $"Window {buildingId} ({x}, {y}, {z})", center, orientation, size);
        }
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
        foreach (var light in FindObjectsByType<Light>(FindObjectsSortMode.None))
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
        foreach (var mesh in meshes) Destroy(mesh);
        Destroy(material);
        Destroy(palette);
    }
}
