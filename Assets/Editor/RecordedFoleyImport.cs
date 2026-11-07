using System;
using System.IO;
using UnityEditor;
using UnityEngine;

// Reproducible edits of the credited recordings. No generated oscillator/noise layers.
public static class RecordedFoleyImport
{
    [MenuItem("Drone Game/Rebuild recorded foley edits")]
    public static void Build()
    {
        for (int i = 0; i < 3; i++) Write("ChairDrag", "Drag" + (i + 1), 1.2f + i * 3f, 0.55f, 0.08f, false);
        Write("RadioStatic", "Receiver", 14f, 12f, 0.06f, true);
        Write("Rain", "Rain", 20f, 20f, 0.09f, true);
        AssetDatabase.Refresh();
    }
    private static void Write(string source, string output, float start, float seconds, float targetRms, bool loop)
    {
        var clip = AssetDatabase.LoadAssetAtPath<AudioClip>("Assets/AudioSources/" + source + "_Source.mp3");
        if (clip == null) throw new InvalidOperationException("Missing recording: " + source);
        clip.LoadAudioData();
        int frames = Mathf.Min((int)(seconds * clip.frequency), clip.samples - (int)(start * clip.frequency));
        var interleaved = new float[frames * clip.channels];
        if (!clip.GetData(interleaved, (int)(start * clip.frequency))) throw new InvalidOperationException("Could not decode " + source);
        var data = new float[frames];
        float lastIn = 0f, lastOut = 0f, a = Mathf.Exp(-2f * Mathf.PI * 70f / clip.frequency);
        for (int i = 0; i < frames; i++)
        {
            float sample = 0f;
            for (int c = 0; c < clip.channels; c++) sample += interleaved[i * clip.channels + c] / clip.channels;
            float filtered = a * (lastOut + sample - lastIn); lastIn = sample; lastOut = filtered; data[i] = filtered;
        }
        int fade = Mathf.Min(frames / 4, (int)(clip.frequency * (loop ? 0.15f : 0.015f)));
        if (loop)
        {
            for (int i = 0; i < fade; i++)
            {
                float blend = (float)i / fade;
                data[i] = data[frames - fade + i] * (1f - blend) + data[i] * blend;
            }
            Array.Resize(ref data, frames - fade);
        }
        else for (int i = 0; i < fade; i++) { data[i] *= (float)i / fade; data[frames - 1 - i] *= (float)i / fade; }
        double energy = 0; float peak = 0;
        foreach (float sample in data) { energy += sample * sample; peak = Mathf.Max(peak, Mathf.Abs(sample)); }
        float gain = Mathf.Min(targetRms / Mathf.Max(0.00001f, (float)Math.Sqrt(energy / data.Length)), 0.65f / Mathf.Max(0.00001f, peak));
        using var writer = new BinaryWriter(File.Create("Assets/Resources/Audio/Foley/" + output + ".wav"));
        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + data.Length * 2);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(clip.frequency); writer.Write(clip.frequency * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(data.Length * 2);
        foreach (float sample in data) writer.Write((short)Mathf.RoundToInt(Mathf.Clamp(sample * gain, -1f, 1f) * 32767f));
    }
}
