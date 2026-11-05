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

[Serializable]
public class PrototypePose
{
    public ulong Id;
    public Vector3 Position, Velocity;
    public Quaternion Rotation, ViewRotation;
    public float Health, Throttle, FlightSeconds;
    public bool Dead, Crouching, Motors;
    public int FlightMode;
}

[Serializable]
public class PrototypeState
{
    public string Code;
    public PrototypeLobbySettings Settings;
    public PrototypeMember[] Members = Array.Empty<PrototypeMember>();
    public PrototypePose[] Actors = Array.Empty<PrototypePose>();
    public PrototypePhase Phase;
    public float Remaining;
    public int DroneStock, Generation;
    public bool DronesWon;
    public string ResultReason;
}

[Serializable]
public class PrototypeMessage
{
    public string Kind, Text, Name;
    public int Team, Generation;
    public bool Ready;
    public PrototypeLobbySettings Settings;
    public PrototypePose Pose;
    public PrototypeState State;
    public string[] Blocks;
    public ulong ActorId;
}
