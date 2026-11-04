using UnityEngine;

// Offsets exist only during this camera's render, so look input and movement
// never inherit a shake offset, including when the view switches after detonation.
[RequireComponent(typeof(Camera))]
public class ExplosionCameraShake : MonoBehaviour
{
    private Camera target;
    private float startedAt, duration, strength;
    private Vector3 originalPosition;
    private Quaternion originalRotation;
    private bool applied;

    public static void Attach(Camera camera)
    {
        if (camera != null && camera.GetComponent<ExplosionCameraShake>() == null)
            camera.gameObject.AddComponent<ExplosionCameraShake>();
    }

    public static void Play(Vector3 position, float range, float seconds, float strength = 1f)
    {
        foreach (var shake in FindObjectsByType<ExplosionCameraShake>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            float distance = Vector3.Distance(shake.transform.position, position);
            float attenuation = Mathf.Clamp01(1f - distance / Mathf.Max(1f, range));
            if (attenuation <= 0) continue;
            float remaining = shake.duration > 0 ? Mathf.Clamp01(1f - (Time.unscaledTime - shake.startedAt) / shake.duration) : 0;
            shake.strength = Mathf.Clamp01(shake.strength * remaining + attenuation * attenuation * strength);
            shake.startedAt = Time.unscaledTime;
            shake.duration = Mathf.Max(0.05f, seconds);
        }
    }

    private void OnEnable()
    {
        target = GetComponent<Camera>();
        Camera.onPreCull += BeforeCamera;
        Camera.onPostRender += AfterCamera;
    }

    private void OnDisable()
    {
        Camera.onPreCull -= BeforeCamera;
        Camera.onPostRender -= AfterCamera;
        RestorePose();
        strength = 0;
    }

    private void BeforeCamera(Camera camera)
    {
        if (camera != target) return;
        RestorePose();
        float age = Time.unscaledTime - startedAt;
        if (strength <= 0 || age >= duration) return;
        float fade = 1f - age / duration;
        float amplitude = strength * fade * fade;
        float t = Time.unscaledTime * 32f;
        Vector3 noise = new Vector3(Mathf.PerlinNoise(t, 13.7f), Mathf.PerlinNoise(t, 57.3f),
            Mathf.PerlinNoise(t, 91.1f)) * 2f - Vector3.one;
        originalPosition = transform.localPosition;
        originalRotation = transform.localRotation;
        applied = true;
        transform.localPosition = originalPosition + noise * (0.065f * amplitude);
        transform.localRotation = originalRotation * Quaternion.Euler(noise.y * 2.2f * amplitude,
            noise.x * 1.4f * amplitude, noise.z * 1.8f * amplitude);
    }

    private void AfterCamera(Camera camera)
    {
        if (camera == target) RestorePose();
    }

    private void RestorePose()
    {
        if (!applied) return;
        transform.localPosition = originalPosition;
        transform.localRotation = originalRotation;
        applied = false;
    }
}
