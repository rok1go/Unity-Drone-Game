using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public class RoundSurvivor
{
    public string Name;
    public float Seconds;
    public bool Alive;
}

// The end-of-round summary: computed where the round is decided (offline, or the host) and sent to every
// client inside the match state, so everybody sees the same numbers.
[Serializable]
public class RoundSummary
{
    public bool DronesWon;
    public RoundSurvivor[] Humans = Array.Empty<RoundSurvivor>();
    public int Kills, Explosions, Destroyed, Barricades;
    public string Longest = "";
}

// Counters for the round in progress. Only the authority counts; clients read the summary it publishes.
public static class RoundStats
{
    private sealed class Tracked
    {
        public HumanController human;
        public string name;
        public float diedAt = -1f;
    }

    private static readonly List<Tracked> humans = new List<Tracked>();
    private static float startedAt;
    private static int kills, explosions, destroyed, barricades;
    public static RoundSummary Last { get; private set; }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        humans.Clear(); Last = null;
        kills = explosions = destroyed = barricades = 0;
    }

    // After the other classes cleared their events (SubsystemRegistration), so these subscriptions survive.
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    private static void Init()
    {
        // Things broken by any cause count as destroyed environment.
        BreakableWindow.Shattered += (_, _, _, _) => { if (DestructionSystem.Authority) destroyed++; };
        PhysicsProp.Broken += _ => { if (DestructionSystem.Authority) destroyed++; };
        InteractiveDoor.Changed += (_, state) => { if (DestructionSystem.Authority && state == InteractiveDoor.DoorState.Broken) destroyed++; };
    }

    public static void Begin()
    {
        foreach (var t in humans) if (t.human != null) t.human.Eliminated -= OnEliminated;
        humans.Clear();
        kills = explosions = destroyed = barricades = 0;
        startedAt = Time.time;
        Last = null;
    }

    public static void Track(HumanController human, string name)
    {
        if (human == null || humans.Exists(t => t.human == human)) return;
        humans.Add(new Tracked { human = human, name = string.IsNullOrEmpty(name) ? "Игрок" : name });
        human.Eliminated += OnEliminated;
    }

    private static void OnEliminated(HumanController human)
    {
        kills++;
        var t = humans.Find(x => x.human == human);
        if (t != null) t.diedAt = Time.time;
    }

    public static void Explosion(int blocksDestroyed) { explosions++; destroyed += Mathf.Max(0, blocksDestroyed); }
    public static void Barricade() => barricades++;

    public static RoundSummary Finish(bool dronesWon)
    {
        float now = Time.time;
        var summary = new RoundSummary
        {
            DronesWon = dronesWon, Kills = kills, Explosions = explosions, Destroyed = destroyed, Barricades = barricades
        };
        var list = new List<RoundSurvivor>();
        RoundSurvivor best = null;
        foreach (var t in humans)
        {
            bool alive = t.human != null && !t.human.IsEliminated;
            var entry = new RoundSurvivor { Name = t.name, Alive = alive, Seconds = Mathf.Max(0f, (alive || t.diedAt < 0f ? now : t.diedAt) - startedAt) };
            list.Add(entry);
            if (best == null || entry.Seconds > best.Seconds) best = entry;
        }
        list.Sort((a, b) => b.Seconds.CompareTo(a.Seconds));
        summary.Humans = list.ToArray();
        summary.Longest = best != null ? best.Name : "";
        return Last = summary;
    }

    public static string Format(RoundSummary s)
    {
        if (s == null) return "";
        var text = new System.Text.StringBuilder();
        foreach (var h in s.Humans)
            text.Append(h.Name).Append(" — ").Append(Clock(h.Seconds)).Append(h.Alive ? "  (выжил)" : "").Append('\n');
        if (!string.IsNullOrEmpty(s.Longest)) text.Append("Дольше всех продержался: ").Append(s.Longest).Append('\n');
        text.Append("Найдено дроном: ").Append(s.Kills)
            .Append("   Взрывов: ").Append(s.Explosions)
            .Append("   Разрушено: ").Append(s.Destroyed)
            .Append("   Баррикад: ").Append(s.Barricades);
        return text.ToString();
    }

    private static string Clock(float seconds) => (int)(seconds / 60f) + ":" + ((int)seconds % 60).ToString("00");
}
