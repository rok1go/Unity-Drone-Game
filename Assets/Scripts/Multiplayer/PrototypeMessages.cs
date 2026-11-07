using System;
using UnityEngine;

public enum PrototypeTeam { Humans, Drones }
public enum PrototypePhase { Lobby, Loading, Hiding, Hunting, Finished }

[Serializable]
public class PrototypeLobbySettings
{
    public string Name = "Прятки";
    public int MaxPlayers = 8;
    public int HidingSeconds = 30;
    public int HuntingSeconds = 240;
    public int DroneStock; // 0 = unlimited
    public bool Suburb = true;   // voxel suburb instead of the simple generated map
    public TimeOfDay Time = TimeOfDay.Day;
    public Weather Weather = Weather.Clear;
    public int Density = 1;
    public bool Items = true, Electricity = true, Barricades = true, Carrying = true;
    public bool Prone = true, Vaulting = true, Leaning = true, FallDamage = true;
    public int RespawnSeconds = 3, HumanLives = 1;
    // Fall damage strength when FallDamage is on: 0 low (default), 1 normal, 2 high.
    public int FallDamageLevel;
    public float FallDamageScale => FallDamageLevel == 2 ? 1.6f : FallDamageLevel == 1 ? 1f : 0.45f;
    // Map seed: 0 picks a new random one every round. ResolvedSeed is what the host chose for this round.
    public int Seed, ResolvedSeed;
    // Town generator: 0 small (2 x 2 blocks), 1 medium (3 x 3); zoning 0 houses, 1 apartments, 2 mixed,
    // 3 random per block; at most this many apartment blocks.
    public int MapSize = 1, Zoning = 2, Apartments = 2;

    public static PrototypeLobbySettings Load()
    {
        try
        {
            var value = JsonUtility.FromJson<PrototypeLobbySettings>(PlayerPrefs.GetString("host.settings.v2", ""));
            if (value != null) { value.Clamp(); return value; }
        }
        catch (Exception) { }
        return new PrototypeLobbySettings();
    }

    public void Save() { PlayerPrefs.SetString("host.settings.v2", JsonUtility.ToJson(this)); PlayerPrefs.Save(); }

    public void Clamp()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Прятки" : Name.Trim();
        if (Name.Length > 32) Name = Name.Substring(0, 32);
        MaxPlayers = Mathf.Clamp(MaxPlayers, 2, 8);
        HidingSeconds = Mathf.Clamp(HidingSeconds, 10, 120);
        HuntingSeconds = Mathf.Clamp(HuntingSeconds, 60, 600);
        DroneStock = Mathf.Clamp(DroneStock, 0, 100);
        Density = Mathf.Clamp(Density, 0, 2);
        FallDamageLevel = Mathf.Clamp(FallDamageLevel, 0, 2);
        MapSize = Mathf.Clamp(MapSize, 0, 1); Zoning = Mathf.Clamp(Zoning, 0, 3); Apartments = Mathf.Clamp(Apartments, 0, 6);
        Seed = Mathf.Max(0, Seed);
        RespawnSeconds = Mathf.Clamp(RespawnSeconds, 0, 60);
        HumanLives = Mathf.Clamp(HumanLives, 1, 20);
        if (!Enum.IsDefined(typeof(TimeOfDay), Time)) Time = TimeOfDay.Day;
        if (!Enum.IsDefined(typeof(Weather), Weather)) Weather = Weather.Clear;
    }
}

[Serializable]
public class PrototypeMember
{
    public ulong Id;
    public string Name;
    public PrototypeTeam Team;
    public bool Ready;
}

// Rare, reliable pose (detonation point, blast point, server correction). Continuous poses use NetPose.
[Serializable]
public class PrototypePose
{
    public ulong Id;
    public Vector3 Position;
    public Quaternion Rotation;
}

[Serializable]
public class PrototypeActorState
{
    public ulong Id;
    public float Health;
    public bool Dead;
    public bool Flashlight;
}

[Serializable]
public class PrototypeState
{
    public int[] PowerStates;
    public string Code;
    public PrototypeLobbySettings Settings;
    public PrototypeMember[] Members = Array.Empty<PrototypeMember>();
    public PrototypeActorState[] Actors = Array.Empty<PrototypeActorState>();
    public PrototypePhase Phase;
    public double PhaseEnds; // Netcode server time; the timer no longer needs a periodic resend.
    public int DroneStock, Generation;
    public bool DronesWon;
    public string ResultReason;
    public RoundSummary Summary;
}

[Serializable]
public class PrototypeMessage
{
    public string Kind, Text, Name;
    public int Team, Generation, Index, Value;
    public bool Ready;
    public PrototypeLobbySettings Settings;
    public PrototypePose Pose;
    public PrototypeState State;
    public ulong ActorId;
}
