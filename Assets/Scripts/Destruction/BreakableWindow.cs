using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

[DisallowMultipleComponent]
[RequireComponent(typeof(BoxCollider), typeof(MeshRenderer))]
public sealed class BreakableWindow : MonoBehaviour, IInteractable
{
    private const float InteractionDistance = 2.6f;
    private const float InteractionSpeed = 3.2f;
    private static BreakableWindow prefab;
    private static AudioClip breakSound;
    private static readonly HashSet<BreakableWindow> brokenWindows = new();
    internal static IEnumerable<BreakableWindow> BrokenWindows => brokenWindows;
    // Window, source, speed, fromBlast. Raised for every successful break so multiplayer can replicate it.
    public static event System.Action<BreakableWindow, Vector3, float, bool> Shattered;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        Shattered = null;
        brokenWindows.Clear();
    }
    private BoxCollider paneCollider;
    private MeshRenderer paneRenderer;

    private void OnEnable()
    {
        paneCollider = GetComponent<BoxCollider>();
        paneRenderer = GetComponent<MeshRenderer>();
        if (!paneCollider.enabled) brokenWindows.Add(this);
    }

    private void OnDisable() => brokenWindows.Remove(this);

    public static BreakableWindow Create(Transform parent, string id, Vector3 localPosition,
        Quaternion localRotation, Vector3 size)
    {
        if (prefab == null) prefab = Resources.Load<BreakableWindow>("Props/WindowGlass");
        if (prefab == null)
            throw new System.InvalidOperationException("The window glass prefab is missing from Resources/Props.");

        var window = Instantiate(prefab, parent, false);
        window.name = id;
        window.transform.SetLocalPositionAndRotation(localPosition, localRotation);
        window.transform.localScale = size;
        return window;
    }

    public static bool TryGetTarget(Ray view, out BreakableWindow window)
    {
        window = null;
        return Physics.Raycast(view, out var hit, InteractionDistance, Physics.DefaultRaycastLayers,
                   QueryTriggerInteraction.Ignore) &&
               hit.collider.TryGetComponent(out window) && window.isActiveAndEnabled;
    }

    public string Prompt => isActiveAndEnabled && paneCollider.enabled ? "Разбить стекло" : null;

    public void Interact(Vector3 user)
    {
        if (Shatter(user + Vector3.up * 1.5f, InteractionSpeed)) PlayBreakSound(false);
    }

    internal bool Shatter(Vector3 source, float speed, bool fromBlast = false)
    {
        if (!isActiveAndEnabled || !paneCollider.enabled) return false;

        Vector3 center = paneCollider.bounds.center;
        Vector3 away = center - source;
        if (away.sqrMagnitude < 0.0001f) away = transform.forward;
        paneCollider.enabled = false;
        paneRenderer.enabled = false;
        brokenWindows.Add(this);

        GlassShardEffect.Emit(this, paneRenderer.sharedMaterial, away.normalized, speed, fromBlast);
        Shattered?.Invoke(this, source, speed, fromBlast);
        return true;
    }

    internal void PlayBreakSound(bool fromBlast)
    {
        breakSound = SoundBank.WindowBreak;
        GameAudio.Play(breakSound, transform.position, fromBlast ? 0.95f : 0.45f, Random.Range(0.97f, 1.03f),
            SoundCategory.Effects, fromBlast ? 5f : 2f, fromBlast ? 90f : 40f);
        // The shards land a moment later, on the floor below the frame.
        if (isActiveAndEnabled) StartCoroutine(ShardsLand(fromBlast));
    }

    private System.Collections.IEnumerator ShardsLand(bool fromBlast)
    {
        yield return new WaitForSeconds(Random.Range(0.28f, 0.42f));
        Vector3 floor = transform.position - Vector3.up * transform.lossyScale.y * 0.5f;
        GameAudio.Play(SoundBank.GlassShards, floor, fromBlast ? 0.35f : 0.22f, Random.Range(0.95f, 1.05f),
            SoundCategory.Effects, 1.5f, fromBlast ? 45f : 25f);
    }

    // Struck but holding: a dull knock on the pane.
    internal void PlayKnock(float strength)
    {
        GameAudio.Play(SoundBank.GlassKnock, transform.position, Mathf.Clamp(0.08f + strength * 0.04f, 0.08f, 0.3f),
            Random.Range(0.95f, 1.05f), SoundCategory.Effects, 1.5f, 25f);
    }
}

internal sealed class GlassShardEffect : MonoBehaviour
{
    private static GlassShardEffect current;
    private ParticleSystem particles;
    private Mesh shardMesh;
    private Material shardMaterial;

    internal static void Emit(BreakableWindow window, Material material, Vector3 direction,
        float speed, bool fromBlast)
    {
        Scene scene = window.gameObject.scene;
        if (current == null || current.gameObject.scene != scene)
        {
            var host = new GameObject("Window glass shards");
            SceneManager.MoveGameObjectToScene(host, scene);
            current = host.AddComponent<GlassShardEffect>();
            current.Initialize(material);
        }

        Transform pane = window.transform;
        if (fromBlast)
            current.StartCoroutine(current.EmitAfterFlash(pane.localToWorldMatrix, direction, speed));
        else
            current.EmitBurst(pane.localToWorldMatrix, direction, speed, false);
    }

    private IEnumerator EmitAfterFlash(Matrix4x4 pane, Vector3 direction, float speed)
    {
        yield return new WaitForSeconds(0.17f);
        EmitBurst(pane, direction, speed, true);
    }

    private void EmitBurst(Matrix4x4 pane, Vector3 direction, float speed, bool fromBlast)
    {
        Vector3 normal = pane.MultiplyVector(Vector3.forward).normalized;
        Vector3 towardBlast = Vector3.Dot(normal, direction) > 0f ? -normal : normal;
        int count = fromBlast ? 18 : 12;
        for (int i = 0; i < count; i++)
        {
            Vector3 point = new Vector3(Random.Range(-0.46f, 0.46f), Random.Range(-0.46f, 0.46f), 0f);
            Vector3 travel = direction;
            if (fromBlast)
            {
                // Send some shards out past each face of the frame, where they remain visible.
                Vector3 side = i % 3 == 0 ? -towardBlast : towardBlast;
                travel = (side + direction * 0.35f).normalized;
            }
            particles.Emit(new ParticleSystem.EmitParams
            {
                position = pane.MultiplyPoint3x4(point),
                velocity = (travel + Random.insideUnitSphere * 0.65f) * Random.Range(speed * 0.45f, speed),
                rotation3D = Random.insideUnitSphere * 180f,
                angularVelocity3D = Random.insideUnitSphere * 500f,
                startLifetime = fromBlast ? Random.Range(0.9f, 1.4f) : Random.Range(0.55f, 1.05f),
                startSize = Random.Range(0.09f, 0.2f)
            }, 1);
        }
    }

    private void Initialize(Material material)
    {
        particles = gameObject.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = particles.main;
        main.loop = true;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.startRotation3D = true;
        main.gravityModifier = 0.85f;
        main.maxParticles = 1536;
        var emission = particles.emission;
        emission.enabled = false;
        var shape = particles.shape;
        shape.enabled = false;

        shardMesh = new Mesh { name = "Triangular glass shard" };
        shardMesh.vertices = new[]
        {
            new Vector3(-0.5f, -0.3f, -0.04f), new Vector3(0.52f, -0.16f, -0.04f),
            new Vector3(-0.18f, 0.63f, -0.04f), new Vector3(-0.5f, -0.3f, 0.04f),
            new Vector3(0.52f, -0.16f, 0.04f), new Vector3(-0.18f, 0.63f, 0.04f)
        };
        shardMesh.triangles = new[]
        {
            0, 2, 1, 3, 4, 5,
            0, 1, 4, 0, 4, 3,
            1, 2, 5, 1, 5, 4,
            2, 0, 3, 2, 3, 5
        };
        shardMesh.RecalculateNormals();

        var renderer = GetComponent<ParticleSystemRenderer>();
        renderer.renderMode = ParticleSystemRenderMode.Mesh;
        renderer.mesh = shardMesh;
        // Give the small facets more contrast than the intact transparent pane.
        shardMaterial = new Material(material) { name = "Window shard facets" };
        shardMaterial.SetFloat("_Mode", 0f);
        shardMaterial.SetInt("_SrcBlend", (int)BlendMode.One);
        shardMaterial.SetInt("_DstBlend", (int)BlendMode.Zero);
        shardMaterial.SetInt("_ZWrite", 1);
        shardMaterial.DisableKeyword("_ALPHAPREMULTIPLY_ON");
        shardMaterial.SetOverrideTag("RenderType", "Opaque");
        shardMaterial.renderQueue = (int)RenderQueue.Geometry;
        shardMaterial.color = new Color(0.52f, 0.76f, 0.87f, 1f);
        shardMaterial.EnableKeyword("_EMISSION");
        shardMaterial.SetColor("_EmissionColor", new Color(0.09f, 0.18f, 0.24f));
        renderer.sharedMaterial = shardMaterial;
        renderer.shadowCastingMode = ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        particles.Play();
    }

    private void OnDestroy()
    {
        if (shardMesh != null) Destroy(shardMesh);
        if (shardMaterial != null) Destroy(shardMaterial);
    }
}
