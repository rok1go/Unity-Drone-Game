using System.Collections.Generic;
using UnityEngine;

public enum SoundKind { Impact, Step, Slide, Door, Break, Collapse, Switch, Explosion, Other }
public enum StepSurface { Wood, Concrete, Metal, Glass, Grass, Soft }

// Recorded foley only. Provenance and licenses are in AUDIO_CREDITS.md.
public static class SoundBank
{
    private static readonly Dictionary<string, AudioClip> clips = new Dictionary<string, AudioClip>();
    private static readonly Dictionary<AudioClip, SoundKind> kinds = new Dictionary<AudioClip, SoundKind>();
    private static readonly Dictionary<string, int> lastVariant = new Dictionary<string, int>();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset() { clips.Clear(); kinds.Clear(); lastVariant.Clear(); }
    private static AudioClip Clip(string name, SoundKind kind)
    {
        if (!clips.TryGetValue(name, out var clip))
        {
            clip = Resources.Load<AudioClip>("Audio/Foley/" + name);
            clips[name] = clip;
            if (clip != null) kinds[clip] = kind;
        }
        return clip;
    }
    private static AudioClip Pick(string prefix, int count, SoundKind kind, bool padded = true)
    {
        lastVariant.TryGetValue(prefix, out int last);
        int index = count <= 1 ? 0 : (last + 1 + Random.Range(0, count - 1)) % count;
        lastVariant[prefix] = index;
        return Clip(prefix + (padded ? index.ToString("000") : (index + 1).ToString()), kind);
    }
    public static SoundKind Kind(AudioClip clip) => clip != null && kinds.TryGetValue(clip, out var kind) ? kind :
        clip != null && clip.name.Contains("Explosion") ? SoundKind.Explosion : SoundKind.Other;
    public static AudioClip Impact(SoundMaterial material, bool heavy) => Pick(material switch
    {
        SoundMaterial.Metal => "impactMetal_" + (heavy ? "heavy_" : "light_"),
        SoundMaterial.Soft => "impactSoft_" + (heavy ? "heavy_" : "medium_"),
        SoundMaterial.Stone => "impactMining_",
        SoundMaterial.Glass => "impactGlass_light_",
        _ => "impactWood_" + (heavy ? "heavy_" : "light_")
    }, 5, SoundKind.Impact);
    public static AudioClip Step(bool hard, int variant) => Clip("footstep_" + (hard ? "concrete_" : "wood_") +
        (variant % 5).ToString("000"), SoundKind.Step);
    // A footfall on a surface. Metal is a recorded light metal knock (stairs, car roofs, appliances); broken
    // glass is a recorded glass crunch; grass and earth use the soft carpet takes, which carry no hard click.
    public static AudioClip Step(StepSurface surface, int variant) => surface switch
    {
        StepSurface.Concrete => Clip("footstep_concrete_" + (variant % 5).ToString("000"), SoundKind.Step),
        // A sole on thin sheet metal (car roofs, appliances) is a dull, hollow thud: the board takes pitched down.
        StepSurface.Metal => Clip("footstep_wood_" + (variant % 5).ToString("000"), SoundKind.Step),
        StepSurface.Glass => Clip("impactGlass_medium_" + (variant % 5).ToString("000"), SoundKind.Step),
        StepSurface.Grass or StepSurface.Soft => Clip("footstep_carpet_" + (variant % 5).ToString("000"), SoundKind.Step),
        _ => Clip("footstep_wood_" + (variant % 5).ToString("000"), SoundKind.Step)
    };
    public static AudioClip SoftStep(int variant) => Clip("footstep_carpet_" + (variant % 5).ToString("000"), SoundKind.Step);
    public static AudioClip Glass => WindowBreak;

    // Recorded takes dropped into Resources/Audio/Foley under these names replace the fallbacks without code
    // changes (see AUDIO_CREDITS.md for the CC0 recordings they are meant for).
    private static AudioClip Optional(string name, SoundKind kind, System.Func<AudioClip> fallback)
    {
        var clip = Clip(name, kind);
        return clip != null ? clip : fallback();
    }
    private static AudioClip loadedGlassBreak;

    // A pane smashing: the recorded window break (Resources/Audio/GlassBreak), not a single impact take.
    public static AudioClip WindowBreak => Optional("WindowBreak", SoundKind.Break, () =>
    {
        if (loadedGlassBreak == null) loadedGlassBreak = Resources.Load<AudioClip>("Audio/GlassBreak");
        if (loadedGlassBreak != null) kinds[loadedGlassBreak] = SoundKind.Break;
        return loadedGlassBreak != null ? loadedGlassBreak : Clip("impactGlass_heavy_000", SoundKind.Break);
    });
    // Shards landing a moment after the break.
    public static AudioClip GlassShards => Optional("GlassShardsFall", SoundKind.Other, () => Pick("impactGlass_light_", 5, SoundKind.Other));
    // Something striking a pane that holds.
    public static AudioClip GlassKnock => Pick("impactGlass_medium_", 5, SoundKind.Impact);
    // Vehicles: doors, body hits, a car being smashed.
    public static AudioClip CarDoorOpen => Optional("CarDoorOpen", SoundKind.Door, () => Switch);
    public static AudioClip CarDoorClose => Optional("CarDoorClose", SoundKind.Door, () => Pick("impactMetal_medium_", 5, SoundKind.Door));
    public static AudioClip CarHit => Optional("CarImpact", SoundKind.Impact, () => Pick("impactMetal_heavy_", 5, SoundKind.Impact));
    public static AudioClip CarCrash => Optional("CarCrash", SoundKind.Collapse, () => Pick("impactMetal_heavy_", 5, SoundKind.Collapse));
    public static AudioClip DoorOpen => Clip("qubodup-DoorOpen0" + Random.Range(1, 4), SoundKind.Door);
    public static AudioClip DoorClose => Clip("qubodup-DoorClose0" + Random.Range(1, 4), SoundKind.Door);
    public static AudioClip DoorBreak => Clip("impactPlank_medium_000", SoundKind.Break);
    public static AudioClip WoodBreak => Pick("impactPlank_medium_", 5, SoundKind.Break);
    public static AudioClip Crumble => Clip("Rubble", SoundKind.Collapse);
    public static AudioClip Debris => Clip("Rubble", SoundKind.Collapse);
    public static AudioClip Hammer => Clip("chop", SoundKind.Impact);
    public static AudioClip Switch => Clip("metalClick", SoundKind.Switch);
    public static AudioClip Scrape => Pick("Drag", 3, SoundKind.Slide, false);
    public static AudioClip Land => Clip("footstep_carpet_004", SoundKind.Step);
    public static AudioClip Rustle => Pick("cloth", 4, SoundKind.Step, false);
    public static AudioClip TvLoop => Clip("Receiver", SoundKind.Other);
    public static AudioClip RadioLoop => Clip("Receiver", SoundKind.Other);
    public static AudioClip RainLoop => Clip("Rain", SoundKind.Other);
}
