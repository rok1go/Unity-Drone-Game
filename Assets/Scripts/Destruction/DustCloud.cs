using UnityEngine;

// Dust after blasts, collapses and heavy impacts: one world-space particle system of large, soft,
// slowly spreading puffs that block sight for a while and then settle. Every peer spawns the same
// clouds from the destruction events it already receives, so no particle data is sent.
public sealed class DustCloud : MonoBehaviour
{
    private static DustCloud instance;
    private ParticleSystem particles;
    private Material material;
    private Texture2D texture;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() => instance = null;

    // radius: rough size of the cloud in metres; density 0..1 how thick it is.
    public static void Spawn(Vector3 position, float radius, float density)
    {
        if (!Application.isPlaying || density <= 0f) return;
        if (instance == null) instance = new GameObject("DustCloud").AddComponent<DustCloud>();
        instance.Emit(position, radius, density);
    }

    private void Awake()
    {
        texture = SoftDot(64);
        material = new Material(Shader.Find("Sprites/Default")) { name = "Dust", mainTexture = texture };
        particles = gameObject.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = particles.main;
        main.loop = false;
        main.playOnAwake = false;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = 900;
        main.gravityModifier = -0.008f;    // warm dust lifts a little
        var emission = particles.emission;
        emission.enabled = false;
        var shape = particles.shape;
        shape.enabled = false;
        var size = particles.sizeOverLifetime;
        size.enabled = true;
        size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.55f, 1f, 1.6f));
        var colour = particles.colorOverLifetime;
        colour.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
            new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.08f), new GradientAlphaKey(0.75f, 0.55f),
                    new GradientAlphaKey(0f, 1f) });
        colour.color = gradient;
        var drag = particles.limitVelocityOverLifetime;
        drag.enabled = true;
        drag.drag = 1.6f;
        var renderer = GetComponent<ParticleSystemRenderer>();
        renderer.sharedMaterial = material;
        renderer.renderMode = ParticleSystemRenderMode.Billboard;
        renderer.sortMode = ParticleSystemSortMode.Distance;
        renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        renderer.receiveShadows = false;
        renderer.maxParticleSize = 3f;   // fraction of the screen: a puff in your face can fill it
        particles.Play();
    }

    private void Emit(Vector3 position, float radius, float density)
    {
        // Graphics setting: fewer, larger puffs on low effects.
        int quality = GameSettings.Current.Effects;
        float scale = quality == 0 ? 0.45f : quality == 1 ? 1f : 1.5f;
        int count = Mathf.Clamp(Mathf.RoundToInt(density * radius * 9f * scale), 4, 120);
        var emit = new ParticleSystem.EmitParams();
        for (int i = 0; i < count; i++)
        {
            Vector3 offset = Random.insideUnitSphere * radius * 0.6f;
            offset.y = Mathf.Abs(offset.y) * 0.5f;
            emit.position = position + offset;
            emit.velocity = (offset.normalized + Vector3.up * 0.3f) * Random.Range(0.5f, 2.5f) * Mathf.Sqrt(radius);
            emit.startSize = Random.Range(1.4f, 2.6f) * Mathf.Sqrt(radius) / (quality == 0 ? 0.8f : 1f);
            emit.startLifetime = Random.Range(7f, 13f);
            emit.rotation = Random.Range(0f, 360f);
            float grey = Random.Range(0.55f, 0.72f);
            emit.startColor = new Color(grey, grey * 0.96f, grey * 0.9f, Mathf.Lerp(0.25f, 0.6f, density));
            particles.Emit(emit, 1);
        }
    }

    // Radial falloff texture: a soft round puff.
    private static Texture2D SoftDot(int size)
    {
        var tex = new Texture2D(size, size, TextureFormat.RGBA32, false) { name = "Dust puff", wrapMode = TextureWrapMode.Clamp };
        var pixels = new Color32[size * size];
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                float dx = (x + 0.5f) / size * 2f - 1f, dy = (y + 0.5f) / size * 2f - 1f;
                float d = Mathf.Sqrt(dx * dx + dy * dy);
                float noise = Mathf.PerlinNoise(x * 0.15f, y * 0.15f) * 0.35f + 0.65f;
                byte a = (byte)(Mathf.Clamp01(1f - d) * Mathf.Clamp01(1f - d) * noise * 255f);
                pixels[y * size + x] = new Color32(255, 255, 255, a);
            }
        tex.SetPixels32(pixels);
        tex.Apply(false, true);
        return tex;
    }

    private void OnDestroy()
    {
        if (material != null) Destroy(material);
        if (texture != null) Destroy(texture);
    }
}
