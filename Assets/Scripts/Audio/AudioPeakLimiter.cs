using UnityEngine;

// Final stereo-linked peak protection on the active listener; no extra playback sources.
[DisallowMultipleComponent]
public sealed class AudioPeakLimiter : MonoBehaviour
{
    private float gain = 1f;
    private float release;
    private void OnEnable() => release = 1f - Mathf.Exp(-1f / (AudioSettings.outputSampleRate * 0.12f));
    private void OnAudioFilterRead(float[] data, int channels)
    {
        for (int i = 0; i < data.Length; i += channels)
        {
            float peak = 0f;
            for (int c = 0; c < channels; c++) peak = Mathf.Max(peak, Mathf.Abs(data[i + c]));
            float wanted = peak > 0.78f ? 0.78f / peak : 1f;
            gain = wanted < gain ? wanted : gain + (wanted - gain) * release;
            for (int c = 0; c < channels; c++) data[i + c] *= gain;
        }
    }
}
