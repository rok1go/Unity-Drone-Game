using UnityEngine;

// Short-lived blast concussion for the local human: muffled hearing with a ringing tone, a slight blur and
// a slow sway of the view. Strength comes from distance to the blast and the solid cover in between.
// A new blast raises the effect to the stronger of the two, never stacks it. Every peer computes it for
// its own player from the blast it already replays, so nothing extra is sent. "Reduce concussion
// effects" in the settings keeps only the sound.
[DisallowMultipleComponent]
[RequireComponent(typeof(Camera))]
public sealed class Concussion : MonoBehaviour
{
    private const float ReachFactor = 1.8f;     // felt up to 1.8x the blast's damage radius
    private const float RingFrequency = 3700f;
    private static AudioClip ringClip;
    private static Shader blurShader;
    public static Quaternion ViewOffset { get; private set; } = Quaternion.identity;
    public static float Level => active != null ? active.level : 0f;
    private static Concussion active;

    private float level, peak, duration, swayPhase;
    private AudioLowPassFilter muffle;
    private AudioSource ring;
    private Material blur;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics() { active = null; ViewOffset = Quaternion.identity; }

    public static void FromBlast(Vector3 blast, float radius)
    {
        HumanController local = null;
        foreach (var human in FindObjectsByType<HumanController>())
            if (human.IsControlled && !human.IsEliminated && human.viewCamera != null) { local = human; break; }
        if (local == null) return;
        Vector3 head = local.viewCamera.transform.position;
        Vector3 toBlast = blast - head;
        float distance = toBlast.magnitude;
        float reach = Mathf.Max(1f, radius * ReachFactor);
        if (distance >= reach) return;
        float strength = Mathf.Pow(1f - distance / reach, 1.3f);
        // Each wall or slab in between halves it; a blast in the next building is barely felt.
        if (distance > 0.3f)
            foreach (var hit in Physics.RaycastAll(head, toBlast / distance, distance - 0.3f, Physics.DefaultRaycastLayers,
                         QueryTriggerInteraction.Ignore))
                if (hit.rigidbody == null || hit.rigidbody.isKinematic) strength *= 0.5f;
        if (strength < 0.08f) return;
        var effect = local.viewCamera.GetComponent<Concussion>();
        if (effect == null) effect = local.viewCamera.gameObject.AddComponent<Concussion>();
        effect.Hit(Mathf.Clamp01(strength));
    }

    private void Hit(float strength)
    {
        active = this;
        enabled = true;
        if (strength <= level) return;
        level = peak = strength;
        duration = 2f + 5f * strength;          // 2 s for a distant thump, 7 s next to the blast
        if (muffle == null) muffle = gameObject.AddComponent<AudioLowPassFilter>();
        muffle.enabled = true;
        if (ring == null)
        {
            ring = gameObject.AddComponent<AudioSource>();
            ring.clip = RingClip();
            ring.loop = true; ring.spatialBlend = 0f; ring.playOnAwake = false; ring.priority = 10;
        }
        if (!ring.isPlaying) ring.Play();
    }

    // Tinnitus is a pure, slightly beating tone; it is the one sound here that is synthesised by nature.
    private static AudioClip RingClip()
    {
        if (ringClip != null) return ringClip;
        const int rate = 44100;
        var samples = new float[rate * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float t = i / (float)rate;
            // Two close partials beat at 1.5 Hz; the 2 s loop holds whole cycles of both, so it loops cleanly.
            samples[i] = 0.5f * (Mathf.Sin(2f * Mathf.PI * RingFrequency * t) + 0.6f * Mathf.Sin(2f * Mathf.PI * (RingFrequency + 1.5f) * t)) / 1.6f;
        }
        ringClip = AudioClip.Create("Concussion ring", samples.Length, 1, rate, false);
        ringClip.SetData(samples, 0);
        return ringClip;
    }

    private void Update()
    {
        level = Mathf.MoveTowards(level, 0f, Time.deltaTime * peak / Mathf.Max(0.5f, duration));
        bool visuals = !GameSettings.Current.ReducedConcussion;
        // Hearing returns gradually: heavily muffled at first, the ring fading faster than the muffling.
        if (muffle != null) muffle.cutoffFrequency = Mathf.Lerp(22000f, 650f, Mathf.Pow(level, 0.7f));
        if (ring != null) ring.volume = Mathf.Pow(level, 2f) * 0.16f * GameSettings.Volume(SoundCategory.Effects);
        if (visuals && level > 0.001f)
        {
            swayPhase += Time.deltaTime;
            float sway = level * level;
            ViewOffset = Quaternion.Euler(Mathf.Sin(swayPhase * 1.3f) * 1.6f * sway, Mathf.Sin(swayPhase * 0.9f) * 2.2f * sway,
                Mathf.Sin(swayPhase * 0.7f) * 3f * sway);
        }
        else ViewOffset = Quaternion.identity;
        if (level > 0f) return;
        if (muffle != null) muffle.enabled = false;
        if (ring != null) ring.Stop();
        ViewOffset = Quaternion.identity;
        enabled = false;
    }

    private void OnRenderImage(RenderTexture source, RenderTexture destination)
    {
        if (GameSettings.Current.ReducedConcussion || level < 0.02f || !LoadBlur())
        {
            Graphics.Blit(source, destination);
            return;
        }
        blur.SetFloat("_Spread", 1f + 4f * level);
        blur.SetFloat("_Amount", Mathf.Clamp01(level * 1.2f));
        Graphics.Blit(source, destination, blur);
    }

    private bool LoadBlur()
    {
        if (blur != null) return true;
        if (blurShader == null) blurShader = Resources.Load<Shader>("Shaders/ConcussionBlur");
        if (blurShader == null || !blurShader.isSupported) return false;
        blur = new Material(blurShader) { hideFlags = HideFlags.HideAndDontSave };
        return true;
    }

    // Losing control (death, switching to the drone) ends it at once.
    private void OnDisable()
    {
        level = 0f;
        if (muffle != null) muffle.enabled = false;
        if (ring != null) ring.Stop();
        if (active == this) { active = null; ViewOffset = Quaternion.identity; }
    }

    private void OnDestroy() { if (blur != null) Destroy(blur); }
}
