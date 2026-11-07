using UnityEngine;

// Motor sound emitted from the drone itself: a 3D source on the airframe with logarithmic falloff,
// a narrow spread so its direction is clear, Doppler, and occlusion (muffled behind walls). RPM
// follows throttle and airspeed. Replicas run the same component from the replicated throttle, so
// every peer hears each drone once, from where it is.
[DisallowMultipleComponent]
public class DroneMotorAudio : MonoBehaviour
{
    [Range(0, 1)] public float maximumVolume = 0.85f;
    // The pilot hears their own motors only as a faint hum, so footsteps, doors and furniture stay clear.
    // Other players hear this drone at full level; the gain applies to the local pilot's copy only.
    private const float OperatorGain = 0.03f;
    private static AudioClip motorLoop;
    private DroneController drone;
    private AudioSource source;
    private AudioLowPassFilter filter;
    private float rpm, muffle;

    private void Awake()
    {
        drone = GetComponent<DroneController>();
        if (motorLoop == null) motorLoop = Resources.Load<AudioClip>("Audio/DroneMotors");
        if (motorLoop != null && motorLoop.loadState == AudioDataLoadState.Unloaded) motorLoop.LoadAudioData();
        if (motorLoop == null) Debug.LogError("Drone motor sound is missing: Resources/Audio/DroneMotors.wav", this);
        // Cloning a running avatar also clones its emitter. Reuse it instead of adding a second loop.
        var existing = transform.Find("Motor Audio");
        var emitter = existing != null ? existing.gameObject : new GameObject("Motor Audio");
        emitter.transform.SetParent(transform, false);
        source = emitter.GetComponent<AudioSource>();
        if (source == null) source = emitter.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.clip = motorLoop;
        source.volume = 0;
        source.pitch = 0.8f;
        // Audible across a street, clearly louder close up, gone beyond ~140 m.
        GameAudio.Configure3D(source, 3f, 140f);
        source.dopplerLevel = 0.7f;
        source.priority = 40;
        filter = emitter.GetComponent<AudioLowPassFilter>();
        if (filter == null) filter = emitter.AddComponent<AudioLowPassFilter>();
        filter.cutoffFrequency = 22000f;
    }

    private void LateUpdate()
    {
        if (drone == null || source == null || source.clip == null) return;
        if (source.clip.loadState == AudioDataLoadState.Unloaded) source.clip.LoadAudioData();
        if (source.clip.loadState != AudioDataLoadState.Loaded) return;
        bool running = drone.MotorsArmed && !drone.IsDestroyed;
        float speed = drone.Velocity.magnitude;
        // Throttle sets the RPM; fast flight adds a little from the props biting into the airflow.
        float target = running ? Mathf.Clamp01(0.2f + 0.7f * Mathf.Sqrt(drone.Throttle) + speed * 0.008f) : 0;
        rpm = Mathf.Lerp(rpm, target, 1f - Mathf.Exp(-Time.deltaTime * 7f));
        if (!running && rpm < 0.01f)
        {
            rpm = 0;
            source.volume = 0;
            if (source.isPlaying) source.Stop();
            return;
        }
        // The pilot's own drone is not occluded by the airframe it sits on.
        var actor = drone.GetComponent<PrototypeNetworkActor>();
        bool own = drone.IsControlled || (actor != null && actor.IsLocal && MultiplayerPrototype.Instance.LocalIsDrone);
        if (own) { muffle = 0f; filter.cutoffFrequency = 22000f; }
        else GameAudio.Occlude(source, filter, ref muffle, Time.deltaTime);
        // The loop is a real FPV recording; keep pitch shifts modest to retain its character.
        source.pitch = Mathf.Lerp(0.8f, 1.3f, rpm);
        source.volume = maximumVolume * Mathf.Lerp(0.25f, 1f, rpm) * Mathf.Clamp01(rpm * 5f) *
            GameAudio.OcclusionGain(muffle) * GameSettings.Volume(SoundCategory.Effects) * GameAudio.MixHeadroom * (own ? OperatorGain : 1f);
        if (!source.isPlaying) source.Play();
    }

    private void OnDisable()
    {
        rpm = 0;
        if (source == null) return;
        source.Stop();
        source.volume = 0;
    }

    private void OnDestroy()
    {
        if (source != null) Destroy(source.gameObject);
    }
}
