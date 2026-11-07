using UnityEngine;

// Resolved match rules, shared by the controllers, map loader and interaction systems.
public static class MatchRules
{
    public static PrototypeLobbySettings Current { get; private set; } = new PrototypeLobbySettings();
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void Reset() => Current = new PrototypeLobbySettings();
    public static void Apply(PrototypeLobbySettings settings)
    {
        Current = JsonUtility.FromJson<PrototypeLobbySettings>(JsonUtility.ToJson(settings));
        Current.Clamp();
        // Offline rounds resolve their own seed; online ones arrive with the host's.
        if (Current.ResolvedSeed == 0) Current.ResolvedSeed = Current.Seed != 0 ? Current.Seed : new System.Random().Next(1, int.MaxValue);
        Current.Time = MatchEnvironment.Resolve(Current.Time, new System.Random());
        MatchEnvironment.Apply(Current.Time, Current.Weather);
    }
}
