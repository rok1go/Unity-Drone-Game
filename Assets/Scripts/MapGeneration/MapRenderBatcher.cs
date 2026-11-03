using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Preserve individual colliders and destruction while batching nearby static geometry.
public class MapRenderBatcher : MonoBehaviour
{
    private void Start()
    {
        var groups = new Dictionary<(Vector3Int, Material), MapRenderChunk>();
        foreach (var block in GetComponentsInChildren<DestructibleBlock>())
        {
            if (block.IsDetached) continue;
            var renderer = block.GetComponent<MeshRenderer>();
            var filter = block.GetComponent<MeshFilter>();
            if (renderer == null || !renderer.enabled || filter == null || filter.sharedMesh == null || renderer.sharedMaterial == null) continue;
            var cell = Vector3Int.FloorToInt(transform.InverseTransformPoint(block.transform.position) / 8f);
            var key = (cell, renderer.sharedMaterial);
            if (!groups.TryGetValue(key, out var chunk))
            {
                var obj = new GameObject("RenderChunk");
                obj.transform.SetParent(transform, false);
                obj.transform.localPosition = (Vector3)cell * 8f;
                chunk = obj.AddComponent<MapRenderChunk>();
                chunk.Initialize(renderer.sharedMaterial);
                groups.Add(key, chunk);
            }
            chunk.Add(filter);
            block.RenderChunk = chunk;
            renderer.enabled = false;
        }
        foreach (var chunk in groups.Values) chunk.Rebuild();
    }
}

public class MapRenderChunk : MonoBehaviour
{
    private readonly List<MeshFilter> sources = new List<MeshFilter>();
    private Mesh mesh;
    private MeshRenderer chunkRenderer;
    private bool dirty;

    public void Initialize(Material material)
    {
        mesh = new Mesh { name = "Map chunk (runtime)", indexFormat = IndexFormat.UInt32 };
        gameObject.AddComponent<MeshFilter>().sharedMesh = mesh;
        chunkRenderer = gameObject.AddComponent<MeshRenderer>();
        chunkRenderer.sharedMaterial = material;
        chunkRenderer.shadowCastingMode = ShadowCastingMode.On;
        chunkRenderer.receiveShadows = true;
    }

    public void Add(MeshFilter source) => sources.Add(source);

    public void Remove(MeshFilter source)
    {
        sources.Remove(source);
        dirty = true;
    }

    private void LateUpdate()
    {
        if (dirty) Rebuild();
    }

    public void Rebuild()
    {
        dirty = false;
        var combines = new List<CombineInstance>(sources.Count);
        Matrix4x4 toLocal = transform.worldToLocalMatrix;
        foreach (var source in sources)
            if (source != null && source.gameObject.activeInHierarchy)
                combines.Add(new CombineInstance { mesh = source.sharedMesh, transform = toLocal * source.transform.localToWorldMatrix });
        mesh.Clear();
        chunkRenderer.enabled = combines.Count > 0;
        if (combines.Count > 0) mesh.CombineMeshes(combines.ToArray(), true, true);
    }

    private void OnDestroy()
    {
        if (mesh != null) Destroy(mesh);
    }
}
