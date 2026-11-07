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
    public int DroneStock = 3;

    public void Clamp()
    {
        Name = string.IsNullOrWhiteSpace(Name) ? "Прятки" : Name.Trim();
        if (Name.Length > 32) Name = Name.Substring(0, 32);
        MaxPlayers = Mathf.Clamp(MaxPlayers, 2, 8);
        HidingSeconds = Mathf.Clamp(HidingSeconds, 10, 120);
        HuntingSeconds = Mathf.Clamp(HuntingSeconds, 60, 600);
        DroneStock = Mathf.Clamp(DroneStock, 1, 12);
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
}

[Serializable]
public class PrototypeState
{
    public string Code;
    public PrototypeLobbySettings Settings;
    public PrototypeMember[] Members = Array.Empty<PrototypeMember>();
    public PrototypeActorState[] Actors = Array.Empty<PrototypeActorState>();
    public PrototypePhase Phase;
    public double PhaseEnds; // Netcode server time; the timer no longer needs a periodic resend.
    public int DroneStock, Generation;
    public bool DronesWon;
    public string ResultReason;
}

[Serializable]
public class PrototypeMessage
{
    public string Kind, Text, Name;
    public int Team, Generation, Index;
    public bool Ready;
    public PrototypeLobbySettings Settings;
    public PrototypePose Pose;
    public PrototypeState State;
    public ulong ActorId;
}
