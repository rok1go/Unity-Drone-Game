using UnityEngine;

[DisallowMultipleComponent]
public class DroneMotorAudio : MonoBehaviour
{
    [Range(0, 1)] public float maximumVolume = 0.55f;
    private static AudioClip motorLoop;
    private DroneController drone;
    private AudioSource source;
    private float rpm;

    private void Awake()
    {
        drone = GetComponent<DroneController>();
        if (motorLoop == null) motorLoop = Resources.Load<AudioClip>("Audio/DroneMotors");
        if (motorLoop != null && motorLoop.loadState == AudioDataLoadState.Unloaded) motorLoop.LoadAudioData();
        if (motorLoop == null) Debug.LogError("Drone motor sound is missing: Resources/Audio/DroneMotors.wav", this);
        var emitter = new GameObject("Motor Audio");
        emitter.transform.SetParent(transform, false);
        source = emitter.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.loop = true;
        source.clip = motorLoop;
        source.volume = 0;
        source.pitch = 0.8f;
        source.spatialBlend = 1;
        source.spread = 60;
        source.rolloffMode = AudioRolloffMode.Linear;
        source.minDistance = 2;
        source.maxDistance = 65;
        source.dopplerLevel = 0.15f;
        source.priority = 160;
    }

    private void LateUpdate()
    {
        if (drone == null || source == null || source.clip == null) return;
        if (source.clip.loadState == AudioDataLoadState.Unloaded) source.clip.LoadAudioData();
        if (source.clip.loadState != AudioDataLoadState.Loaded) return;
        bool running = drone.MotorsArmed && !drone.IsDestroyed;
        float target = running ? 0.2f + 0.8f * Mathf.Sqrt(drone.Throttle) : 0;
        rpm = Mathf.Lerp(rpm, target, 1f - Mathf.Exp(-Time.deltaTime * 7f));
        if (!running && rpm < 0.01f)
        {
            rpm = 0;
            source.volume = 0;
            if (source.isPlaying) source.Stop();
            return;
        }
        // The loop is a real FPV recording; keep pitch shifts modest to retain its character.
        source.pitch = Mathf.Lerp(0.8f, 1.25f, rpm);
        source.volume = maximumVolume * Mathf.Lerp(0.2f, 1f, rpm) * Mathf.Clamp01(rpm * 5f);
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
