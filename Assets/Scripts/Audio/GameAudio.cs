using System;
using System.Collections.Generic;
using UnityEngine;

public enum SoundMaterial { Wood, Metal, Soft, Stone, Glass }

// Positional one-shot sounds for the whole game: a small pool of 3D AudioSources, logarithmic
// distance falloff, and occlusion (a sound behind a wall is quieter and muffled). Every peer plays
// sounds from events it already simulates or replays, so nothing extra goes over the network.
public sealed class GameAudio : MonoBehaviour
{
    private const int PoolSize = 16;
    private const float OccludedVolume = 0.5f, OccludedCutoff = 1100f;

    private sealed class Voice
    {
        public AudioSource source;
        public AudioLowPassFilter filter;
        public float started, baseVolume;
        public SoundCategory category;
        public SoundKind kind;
    }

    private static GameAudio instance;
    private readonly Voice[] voices = new Voice[PoolSize];
    private readonly Dictionary<(SoundKind, Vector3Int), float> recent = new Dictionary<(SoundKind, Vector3Int), float>();
    public static event Action<Vector3, SoundMaterial, float, float, bool, bool> PhysicsPlayed;
    public static int ActiveVoices { get; private set; }
    public const float MixHeadroom = 0.55f;
    private static AudioListener listener;
    private static float nextListenerSearch;
    private static float physicsWindow;
    private static int physicsEvents;
    private static readonly Dictionary<(bool, Vector3Int), float> physicsRecent = new Dictionary<(bool, Vector3Int), float>();

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        instance = null;
        listener = null;
        nextListenerSearch = 0f;
        PhysicsPlayed = null; lastImpact.Clear(); ActiveVoices = 0;
        physicsRecent.Clear(); physicsWindow = 0; physicsEvents = 0;
    }

    private static GameAudio Instance
    {
        get
        {
            if (instance != null) return instance;
            var obj = new GameObject("GameAudio");
            DontDestroyOnLoad(obj);
            instance = obj.AddComponent<GameAudio>();
            for (int i = 0; i < PoolSize; i++)
            {
                var child = new GameObject("Voice");
                child.transform.SetParent(obj.transform, false);
                var source = child.AddComponent<AudioSource>();
                source.playOnAwake = false;
                Configure3D(source, 2f, 45f);
                var filter = child.AddComponent<AudioLowPassFilter>();
                filter.cutoffFrequency = 22000f;
                instance.voices[i] = new Voice { source = source, filter = filter };
            }
            return instance;
        }
    }

    // Shared 3D setup: real logarithmic falloff, narrow spread (so direction is audible), light Doppler.
    public static void Configure3D(AudioSource source, float minDistance, float maxDistance)
    {
        source.spatialBlend = 1f;
        source.rolloffMode = AudioRolloffMode.Logarithmic;
        source.minDistance = minDistance;
        source.maxDistance = maxDistance;
        source.spread = 20f;
        source.dopplerLevel = 0.4f;
    }

    public static Vector3? ListenerPosition
    {
        get
        {
            if ((listener == null || !listener.isActiveAndEnabled) && Time.unscaledTime >= nextListenerSearch)
            {
                nextListenerSearch = Time.unscaledTime + 0.25f;
                listener = null;
                foreach (var candidate in FindObjectsByType<AudioListener>())
                    if (candidate.isActiveAndEnabled)
                    {
                        listener = candidate;
                        if (listener.GetComponent<AudioPeakLimiter>() == null) listener.gameObject.AddComponent<AudioPeakLimiter>();
                        break;
                    }
            }
            return listener != null && listener.isActiveAndEnabled ? listener.transform.position : (Vector3?)null;
        }
    }

    // True when solid geometry lies between the listener and the point.
    public static bool Occluded(Vector3 position)
    {
        var ear = ListenerPosition;
        if (ear == null) return false;
        Vector3 delta = position - ear.Value;
        float distance = delta.magnitude;
        if (distance < 0.6f) return false;
        // Start a little away from both ends so the emitter's own collider does not count.
        return Physics.Raycast(ear.Value, delta / distance, distance - 0.5f, Physics.DefaultRaycastLayers,
            QueryTriggerInteraction.Ignore);
    }

    // Applies occlusion to a looping source owned by something else (drone motor, TV, rain).
    public static void Occlude(AudioSource source, AudioLowPassFilter filter, ref float muffle, float dt)
    {
        float target = Occluded(source.transform.position) ? 1f : 0f;
        muffle = Mathf.MoveTowards(muffle, target, dt * 3f);
        if (filter != null) filter.cutoffFrequency = Mathf.Lerp(22000f, OccludedCutoff, muffle);
    }

    public static float OcclusionGain(float muffle) => Mathf.Lerp(1f, OccludedVolume, muffle);

    public static void Play(AudioClip clip, Vector3 position, float volume, float pitch = 1f,
        SoundCategory category = SoundCategory.Effects, float minDistance = 2f, float maxDistance = 45f)
    {
        if (clip == null || volume <= 0.01f || !Application.isPlaying) return;
        var ear = ListenerPosition;
        // Far beyond hearing: skip the voice entirely.
        if (ear != null && (position - ear.Value).sqrMagnitude > maxDistance * maxDistance) return;
        Instance.Emit(clip, position, volume, pitch, category, minDistance, maxDistance);
    }

    // Flat, non-positional (menu clicks, the local player's own breath).
    public static void PlayFlat(AudioClip clip, float volume, SoundCategory category = SoundCategory.Interface)
    {
        if (clip == null || !Application.isPlaying) return;
        if (!Instance.Allow(clip, Vector3.zero)) return;
        var voice = Instance.Take();
        voice.source.spatialBlend = 0f;
        voice.filter.cutoffFrequency = 22000f;
        Instance.Begin(voice, clip, volume, 1f, category);
    }

    private void Emit(AudioClip clip, Vector3 position, float volume, float pitch, SoundCategory category,
        float minDistance, float maxDistance)
    {
        if (!Allow(clip, position)) return;
        var voice = Take();
        voice.source.transform.position = position;
        Configure3D(voice.source, minDistance, maxDistance);
        bool occluded = Occluded(position);
        voice.filter.cutoffFrequency = occluded ? OccludedCutoff : 22000f;
        Begin(voice, clip, volume * (occluded ? OccludedVolume : 1f), pitch, category);
    }

    private bool Allow(AudioClip clip, Vector3 position)
    {
        SoundKind kind = SoundBank.Kind(clip);
        int count = 0;
        foreach (var voice in voices)
            if (voice != null && voice.source.isPlaying && voice.kind == kind) count++;
        int limit = kind switch { SoundKind.Slide => 2, SoundKind.Collapse => 2, SoundKind.Explosion => 2,
            SoundKind.Step => 6, SoundKind.Impact => 4, _ => 3 };
        if (count >= limit) return false;
        var key = (kind, Vector3Int.FloorToInt(position / 1.5f));
        float interval = kind switch { SoundKind.Slide => 0.45f, SoundKind.Collapse => 0.3f, SoundKind.Impact => 0.09f, _ => 0.055f };
        if (recent.TryGetValue(key, out float time) && Time.unscaledTime - time < interval) return false;
        if (recent.Count > 512) recent.Clear();
        recent[key] = Time.unscaledTime;
        return true;
    }

    private void Begin(Voice voice, AudioClip clip, float volume, float pitch, SoundCategory category)
    {
        _ = ListenerPosition;
        voice.baseVolume = Mathf.Clamp(volume, 0f, 0.85f) * MixHeadroom;
        voice.kind = SoundBank.Kind(clip);
        voice.category = category;
        voice.started = Time.unscaledTime;
        voice.source.clip = clip;
        // Wide enough for a pitched-down thud or crunch, narrow enough that a recording never sounds synthetic.
        voice.source.pitch = Mathf.Clamp(pitch, 0.75f, 1.15f);
        voice.source.volume = voice.baseVolume * GameSettings.Volume(category);
        voice.source.Play();
    }

    // A free voice, else the quietest of the oldest ones.
    private Voice Take()
    {
        Voice best = null;
        float bestScore = float.PositiveInfinity;
        foreach (var voice in voices)
        {
            if (!voice.source.isPlaying) return voice;
            float score = voice.baseVolume - (Time.unscaledTime - voice.started) * 0.5f;
            if (score < bestScore) { bestScore = score; best = voice; }
        }
        best.source.Stop();
        best.source.spatialBlend = 1f;
        return best;
    }

    private void OnEnable() => GameSettings.Changed += RefreshVolumes;
    private void OnDisable() => GameSettings.Changed -= RefreshVolumes;
    private void LateUpdate()
    {
        ActiveVoices = 0;
        foreach (var voice in voices) if (voice != null && voice.source.isPlaying) ActiveVoices++;
        // A short release prevents a pile of impacts from summing into a volume jump.
        float gain = ActiveVoices <= 4 ? 1f : Mathf.Sqrt(4f / ActiveVoices);
        foreach (var voice in voices)
            if (voice != null && voice.source.isPlaying)
                voice.source.volume = voice.baseVolume * GameSettings.Volume(voice.category) * gain;
    }

    private void RefreshVolumes()
    {
        foreach (var voice in voices)
            if (voice != null && voice.source.isPlaying)
                voice.source.volume = voice.baseVolume * GameSettings.Volume(voice.category);
    }

    // ---------------- convenience: sounds driven by physics ----------------

    private static readonly Dictionary<UnityEngine.Object, float> lastImpact = new Dictionary<UnityEngine.Object, float>();

    // A collision of `mass` kg at `speed` m/s. Small contacts are ignored, and one body sounds at most
    // every 0.12 s, so resting or jittering objects stay silent.
    public static void Impact(UnityEngine.Object owner, Vector3 position, float mass, float speed, SoundMaterial material)
    {
        if (speed < 1.8f) return;
        var id = owner;
        float now = Time.time;
        if (lastImpact.TryGetValue(id, out float last) && now - last < 0.12f) return;
        lastImpact[id] = now;
        if (lastImpact.Count > 512) lastImpact.Clear();
        float energy = Mathf.Clamp01(Mathf.Log10(1f + mass * speed * speed * 0.5f) / 4f);
        float volume = Mathf.Lerp(0.04f, 0.48f, energy);
        // Heavier things ring lower.
        float pitch = Mathf.Clamp(1.04f - Mathf.Log10(1f + mass) * 0.025f, 0.94f, 1.04f) * UnityEngine.Random.Range(0.98f, 1.02f);
        PlayPhysics(position, material, volume, pitch, false, mass > 25f);
    }

    // Collision pairs speak once, on the physics authority. Foot contacts belong to HumanVisual.
    public static bool ContactImpact(Component owner, Rigidbody body, Collision collision, SoundMaterial material)
    {
        if (!DestructionSystem.Authority || body == null || collision.contactCount == 0) return false;
        var other = collision.rigidbody;
        if (other != null && (other.GetComponent<HumanController>() != null || other.GetComponent<DroneController>() != null)) return false;
        if (other != null && (other.GetComponent<PhysicsProp>() != null || other.GetComponent<DestructionDebris>() != null) &&
            body.GetEntityId().CompareTo(other.GetEntityId()) > 0) return false;
        var contact = collision.GetContact(0);
        float normalSpeed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
        float reducedMass = other == null || other.isKinematic ? body.mass : body.mass * other.mass / (body.mass + other.mass);
        float impulse = collision.impulse.magnitude;
        if (normalSpeed < 1.8f || impulse < Mathf.Max(0.3f, reducedMass * 0.35f)) return false;
        // Hitting a parked car sounds like its sheet metal, whatever struck it.
        if (collision.collider.TryGetComponent<DestructibleBlock>(out var struck) && struck.IsVehicle) material = SoundMaterial.Metal;
        Impact(owner, contact.point, reducedMass, normalSpeed, material);
        return true;
    }

    public static void Slide(UnityEngine.Object owner, Vector3 position, float mass, float speed, SoundMaterial material)
    {
        if (material == SoundMaterial.Soft || speed < 0.8f) return;
        if (lastImpact.TryGetValue(owner, out float last) && Time.time - last < 0.35f) return;
        // Friction stays a background texture: quiet at walking pace, capped well below impacts.
        float volume = Mathf.Clamp(0.02f + Mathf.Sqrt(mass) * Mathf.Min(speed, 3f) * 0.005f, 0.025f, 0.1f);
        PlayPhysics(position, material, volume, UnityEngine.Random.Range(0.98f, 1.02f), true, mass > 25f);
    }
    private static void PlayPhysics(Vector3 position, SoundMaterial material, float volume, float pitch, bool slide, bool heavy)
    {
        if (!DestructionSystem.Authority) return;
        if (Time.unscaledTime - physicsWindow >= 0.05f) { physicsWindow = Time.unscaledTime; physicsEvents = 0; }
        if (physicsEvents >= 4) return;
        var key = (slide, Vector3Int.FloorToInt(position / 1.5f));
        if (physicsRecent.TryGetValue(key, out float time) && Time.unscaledTime - time < (slide ? 0.5f : 0.1f)) return;
        if (physicsRecent.Count > 512) physicsRecent.Clear();
        physicsRecent[key] = Time.unscaledTime; physicsEvents++;
        PhysicsPlayed?.Invoke(position, material, volume, pitch, slide, heavy);
        ReplayPhysics(position, material, volume, pitch, slide, heavy);
    }
    public static void ReplayPhysics(Vector3 position, SoundMaterial material, float volume, float pitch, bool slide, bool heavy)
    {
        Play(slide ? SoundBank.Scrape : SoundBank.Impact(material, heavy), position, volume, pitch, SoundCategory.Effects,
            1f, slide ? 14f : 40f);
        if (!slide && heavy) ImpactDust(position, material, volume);
    }

    private static float dustWindow;
    private static int dustInWindow;

    // A heavy piece landing hard kicks up a little dust: more for masonry than for wood, nothing for soft
    // things or light knocks. Every peer gets it from the same replicated impact, at most a few per second.
    private static void ImpactDust(Vector3 position, SoundMaterial material, float volume)
    {
        if (material == SoundMaterial.Soft || material == SoundMaterial.Glass || material == SoundMaterial.Metal) return;
        float strength = Mathf.InverseLerp(0.22f, 0.48f, volume) * (material == SoundMaterial.Stone ? 1f : 0.55f);
        if (strength <= 0.05f) return;
        if (Time.unscaledTime - dustWindow > 0.5f) { dustWindow = Time.unscaledTime; dustInWindow = 0; }
        if (dustInWindow >= 3) return;
        dustInWindow++;
        DustCloud.Spawn(position, Mathf.Lerp(0.8f, 2.2f, strength), Mathf.Lerp(0.15f, 0.45f, strength));
    }

    public static SoundMaterial MaterialFor(string label)
    {
        if (string.IsNullOrEmpty(label)) return SoundMaterial.Wood;
        string l = label.ToLowerInvariant();
        if (l.Contains("холодильник") || l.Contains("стиральн") || l.Contains("сушильн") || l.Contains("стеллаж") ||
            l.Contains("бочка") || l.Contains("бак") || l.Contains("монитор") || l.Contains("телевизор") ||
            l.Contains("беговая") || l.Contains("гантел") || l.Contains("автомат") || l.Contains("лампа") ||
            l.Contains("радио")) return SoundMaterial.Metal;
        if (l.Contains("кресло") || l.Contains("диван") || l.Contains("кровать") || l.Contains("корзина") ||
            l.Contains("растение") || l.Contains("вешалка")) return SoundMaterial.Soft;
        if (l.Contains("конус")) return SoundMaterial.Soft;
        return SoundMaterial.Wood;
    }

    public static SoundMaterial MaterialFor(byte blockMaterial) =>
        blockMaterial >= 2 ? SoundMaterial.Stone : SoundMaterial.Wood;
}
