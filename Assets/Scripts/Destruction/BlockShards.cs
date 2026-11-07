using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

// Flying pieces of a block pulverised by a blast. Purely cosmetic: no colliders or rigidbodies, so they
// never push players, set off the drone or break props, and every peer can simulate its own. Each shard
// is a small cube in one of the block's palette colours, integrated by hand with one raycast per frame
// for bounces. A fixed pool caps the cost: the oldest shard is reused when it runs out.
public sealed class BlockShards : MonoBehaviour
{
    private const int PoolSize = 240;
    private const float Gravity = 9.81f;

    private sealed class Shard
    {
        public Transform transform;
        public MeshFilter filter;
        public Vector3 velocity, spinAxis;
        public float spin, size, life, age;
        public bool resting;
    }

    private static BlockShards instance;
    private readonly Shard[] pool = new Shard[PoolSize];
    private readonly Dictionary<float, Mesh> cubes = new Dictionary<float, Mesh>();
    private readonly List<Vector2> uvs = new List<Vector2>();
    private int next;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => instance = null;

    public static void Spawn(DestructibleBlock block, Vector3 blast, float impulse)
    {
        if (block == null || !Application.isPlaying) return;
        var filter = block.GetComponent<MeshFilter>();
        var renderer = block.GetComponent<MeshRenderer>();
        if (filter == null || filter.sharedMesh == null || renderer == null || renderer.sharedMaterial == null) return;
        if (instance == null)
        {
            var obj = new GameObject("BlockShards");
            instance = obj.AddComponent<BlockShards>();
        }
        instance.Emit(block, filter.sharedMesh, renderer.sharedMaterial, blast, impulse);
    }

    private void Emit(DestructibleBlock block, Mesh mesh, Material material, Vector3 blast, float impulse)
    {
        mesh.GetUVs(0, uvs);
        if (uvs.Count == 0) return;
        Bounds bounds = default;
        bool any = false;
        foreach (var box in block.GetComponents<BoxCollider>())
        {
            if (!any) { bounds = box.bounds; any = true; }
            else bounds.Encapsulate(box.bounds);
        }
        if (!any) return;
        // More solid blocks make more pieces; a thin partition only a few.
        int count = Mathf.Clamp(Mathf.RoundToInt(block.Mass / 7f), 3, 8);
        float speed = Mathf.Max(3f, impulse * 0.55f);
        for (int i = 0; i < count; i++)
        {
            var shard = Take(material);
            Vector3 point = new Vector3(Random.Range(bounds.min.x, bounds.max.x), Random.Range(bounds.min.y, bounds.max.y),
                Random.Range(bounds.min.z, bounds.max.z));
            Vector3 away = (point - blast).normalized + Vector3.up * 0.4f + Random.insideUnitSphere * 0.45f;
            shard.size = Random.Range(0.07f, 0.2f);
            shard.velocity = away.normalized * speed * Random.Range(0.45f, 1.15f);
            shard.spinAxis = Random.onUnitSphere;
            shard.spin = Random.Range(180f, 720f);
            shard.life = Random.Range(3.5f, 6f);
            shard.age = 0f;
            shard.resting = false;
            shard.filter.sharedMesh = Cube(uvs[Random.Range(0, uvs.Count)].x);
            shard.transform.SetPositionAndRotation(point, Random.rotation);
            shard.transform.localScale = Vector3.one * shard.size;
            shard.transform.gameObject.SetActive(true);
        }
        enabled = true;
    }

    private Shard Take(Material material)
    {
        var shard = pool[next];
        if (shard == null)
        {
            var obj = new GameObject("Shard");
            obj.transform.SetParent(transform, false);
            var renderer = obj.AddComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            shard = pool[next] = new Shard { transform = obj.transform, filter = obj.AddComponent<MeshFilter>() };
        }
        shard.transform.GetComponent<MeshRenderer>().sharedMaterial = material;
        next = (next + 1) % PoolSize;
        return shard;
    }

    // A unit cube whose every vertex samples one palette texel: the shard is that colour.
    private Mesh Cube(float u)
    {
        if (cubes.TryGetValue(u, out var mesh)) return mesh;
        var source = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
        mesh = Instantiate(source);
        mesh.name = "Shard";
        var coords = new Vector2[mesh.vertexCount];
        for (int i = 0; i < coords.Length; i++) coords[i] = new Vector2(u, 0.5f);
        mesh.uv = coords;
        cubes[u] = mesh;
        return mesh;
    }

    private void Update()
    {
        float dt = Mathf.Min(Time.deltaTime, 0.05f);
        int active = 0;
        foreach (var shard in pool)
        {
            if (shard == null || !shard.transform.gameObject.activeSelf) continue;
            shard.age += dt;
            if (shard.age >= shard.life)
            {
                shard.transform.gameObject.SetActive(false);
                continue;
            }
            active++;
            var t = shard.transform;
            // Fade out by shrinking over the last half second.
            float remaining = shard.life - shard.age;
            if (remaining < 0.5f) t.localScale = Vector3.one * shard.size * (remaining / 0.5f);
            if (shard.resting) continue;
            shard.velocity += Vector3.down * Gravity * dt;
            Vector3 step = shard.velocity * dt;
            float distance = step.magnitude;
            float half = shard.size * 0.5f;
            if (distance > 0f && Physics.Raycast(t.position, step / distance, out var hit, distance + half,
                    Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                t.position = hit.point + hit.normal * half;
                shard.velocity = Vector3.Reflect(shard.velocity, hit.normal) * 0.3f;
                shard.spin *= 0.4f;
                if (hit.normal.y > 0.6f && shard.velocity.magnitude < 0.8f) shard.resting = true;
            }
            else t.position += step;
            t.rotation = Quaternion.AngleAxis(shard.spin * dt, shard.spinAxis) * t.rotation;
        }
        if (active == 0) enabled = false;
    }

    private void OnDestroy()
    {
        foreach (var mesh in cubes.Values) if (mesh != null) Destroy(mesh);
    }
}
