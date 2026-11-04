using UnityEngine;

/// <summary>Radius-matched shockwave, sparks and a brief flash; no external assets.</summary>
public class DroneExplosionEffect : MonoBehaviour
{
    private Light flash;
    private Transform shockwave;
    private Renderer shell;
    private MaterialPropertyBlock properties;
    private float elapsed, radius, duration, strength;
    private static AudioClip explosionSound;

    public static void PreloadAudio()
    {
        if (explosionSound == null) explosionSound = Resources.Load<AudioClip>("Audio/DroneExplosion");
        if (explosionSound != null && explosionSound.loadState == AudioDataLoadState.Unloaded)
            explosionSound.LoadAudioData();
    }

    public static void Play(Vector3 position, Material material, float radius, float duration, int particleCount, float strength = 1f)
    {
        radius = Mathf.Max(0.1f, radius); duration = Mathf.Max(0.3f, duration);
        var effect = new GameObject("DroneExplosion");
        effect.transform.position = position;
        var animation = effect.AddComponent<DroneExplosionEffect>();
        strength = Mathf.Clamp(strength, 0.15f, 2f);
        animation.strength = strength;
        PreloadAudio();
        if (explosionSound != null)
        {
            var audio = effect.AddComponent<AudioSource>();
            audio.playOnAwake = false;
            audio.clip = explosionSound;
            audio.volume = Mathf.Clamp01(0.9f * strength);
            audio.spatialBlend = 1f;
            audio.rolloffMode = AudioRolloffMode.Linear;
            audio.minDistance = 4f;
            audio.maxDistance = Mathf.Max(75f, radius * 12f);
            audio.dopplerLevel = 0;
            audio.Play();
        }
        ExplosionCameraShake.Play(position, Mathf.Max(25f, radius * 5f), 0.55f, strength);
        animation.radius = radius; animation.duration = duration;
        animation.properties = new MaterialPropertyBlock();
        var sphere = GameObject.CreatePrimitive(PrimitiveType.Sphere);
        sphere.name = "Shockwave"; sphere.transform.SetParent(effect.transform, false);
        sphere.GetComponent<Collider>().enabled = false;
        Destroy(sphere.GetComponent<Collider>());
        animation.shockwave = sphere.transform;
        animation.shell = sphere.GetComponent<Renderer>();
        animation.shell.sharedMaterial = material;
        animation.shell.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        animation.shell.receiveShadows = false;
        sphere.transform.localScale = Vector3.one * 0.1f;

        var particles = effect.AddComponent<ParticleSystem>();
        particles.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = particles.main;
        main.loop = false; main.duration = duration;
        main.startLifetime = new ParticleSystem.MinMaxCurve(duration * 0.6f, duration);
        main.startSpeed = new ParticleSystem.MinMaxCurve(radius / duration * 0.7f, radius / duration);
        main.startSize = new ParticleSystem.MinMaxCurve(radius * 0.025f, radius * 0.08f);
        main.startColor = new ParticleSystem.MinMaxGradient(new Color(1,0.3f,0.03f), new Color(1,0.85f,0.2f));
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        particleCount = Mathf.Clamp(Mathf.RoundToInt(particleCount * Mathf.Sqrt(strength)), 16, 512);
        main.maxParticles = particleCount;
        var emission = particles.emission; emission.rateOverTime = 0;
        emission.SetBursts(new[] { new ParticleSystem.Burst(0, (short)Mathf.Clamp(particleCount,16,512)) });
        var shape = particles.shape; shape.shapeType = ParticleSystemShapeType.Sphere; shape.radius = 0.05f;
        if (material != null) particles.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
        var fade = particles.colorOverLifetime; fade.enabled = true;
        var gradient = new Gradient();
        gradient.SetKeys(new[] {new GradientColorKey(Color.white,0),new GradientColorKey(new Color(1,0.15f,0),1)},
            new[] {new GradientAlphaKey(1,0),new GradientAlphaKey(0,1)});
        fade.color = gradient;
        animation.flash = effect.AddComponent<Light>();
        animation.flash.type = LightType.Point; animation.flash.color = new Color(1,0.55f,0.15f);
        animation.flash.range = radius; animation.flash.intensity = 12 * strength; animation.flash.shadows = LightShadows.None;
        particles.Play();
        Destroy(effect, Mathf.Max(duration + 0.1f, explosionSound != null ? explosionSound.length + 0.15f : 0));
    }

    private void Update()
    {
        elapsed += Time.deltaTime;
        if (flash != null) flash.intensity = 12 * strength * Mathf.Clamp01(1 - elapsed / 0.18f);
        float progress = Mathf.Clamp01(elapsed / (duration * 0.55f));
        shockwave.localScale = Vector3.one * Mathf.Max(0.01f, radius * 2 * progress);
        Color color = new Color(1,0.5f,0.12f,0.18f * (1 - progress));
        properties.SetColor("_Color", color); properties.SetColor("_BaseColor", color);
        shell.SetPropertyBlock(properties);
        if (progress >= 1) shell.enabled = false;
    }
}
