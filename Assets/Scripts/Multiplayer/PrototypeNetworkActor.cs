using UnityEngine;

// One player's avatar. The owner simulates it; every other peer renders it from a snapshot buffer
// slightly in the past, which is smooth under jitter and never extrapolates through walls.
public class PrototypeNetworkActor : MonoBehaviour
{
    public ulong OwnerId { get; private set; }
    public PrototypeTeam Team { get; private set; }
    public HumanController Human { get; private set; }
    public DroneController Drone { get; private set; }
    public bool IsLocal => MultiplayerPrototype.Instance != null && OwnerId == MultiplayerPrototype.Instance.LocalId;
    public bool Dead => Team == PrototypeTeam.Humans ? Human.IsEliminated : Drone.IsDestroyed;
    public Camera View => Team == PrototypeTeam.Humans ? Human.viewCamera : Drone.viewCamera;
    // Newest pose received from the owner (host relays it unmodified, so it is not delayed twice).
    public NetPose LatestPose { get; private set; }
    public bool HasPose { get; private set; }
    private Rigidbody body;
    private readonly PoseBuffer buffer = new PoseBuffer();

    public void Configure(ulong id, PrototypeTeam team)
    {
        OwnerId = id; Team = team;
        Human = GetComponent<HumanController>(); Drone = GetComponent<DroneController>();
        body = GetComponent<Rigidbody>();
        if (Human != null) { Human.SetControlled(false); Human.IsNetworkReplica = !IsLocal; }
        if (Drone != null) { Drone.SetControlled(false); Drone.IsNetworkReplica = !IsLocal; }
        if (!IsLocal) MakeReplicaBody();
        if (View != null)
        {
            View.enabled = false;
            var audio = View.GetComponent<AudioListener>();
            if (audio != null) audio.enabled = false;
        }
    }

    public void MakeReplicaBody()
    {
        if (IsLocal) return;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        body.isKinematic = true;
        // The snapshot buffer already interpolates; Rigidbody interpolation would fight it.
        body.interpolation = RigidbodyInterpolation.None;
    }

    public PrototypePose Capture() =>
        new PrototypePose { Id = OwnerId, Position = transform.position, Rotation = transform.rotation };

    public NetPose CaptureNet(double time)
    {
        byte flags = 0;
        if (Human != null && Human.IsCrouching) flags |= NetPose.CrouchingFlag;
        if (Drone != null && Drone.MotorsArmed) flags |= NetPose.MotorsFlag;
        if (Drone != null && Drone.flightMode == DroneController.FlightMode.Acro) flags |= NetPose.AcroFlag;
        return new NetPose
        {
            Time = time, Position = transform.position, Rotation = transform.rotation,
            View = View != null ? View.transform.rotation : transform.rotation,
            Flags = flags,
            Throttle = Drone != null ? (byte)Mathf.RoundToInt(Mathf.Clamp01(Drone.Throttle) * 255f) : (byte)0,
            FlightSeconds = Drone != null ? Drone.FlightSeconds : 0f
        };
    }

    public void PushRemotePose(in NetPose pose)
    {
        if (IsLocal || !buffer.Push(pose, NetSync.Now)) return;
        LatestPose = pose;
        HasPose = true;
    }

    public void ResetNetPose()
    {
        buffer.Clear();
        buffer.Clock.Reset();
        HasPose = false;
    }

    // Authoritative one-off placement (detonation point). Drops buffered motion before it.
    public void SnapTo(Vector3 position, Quaternion rotation)
    {
        ResetNetPose();
        transform.SetPositionAndRotation(position, rotation);
        body.position = position; body.rotation = rotation;
    }

    // The host rejected an impossible move; return the local avatar to the accepted position.
    public void ApplyCorrection(Vector3 position)
    {
        if (!IsLocal || Dead) return;
        body.position = position; transform.position = position;
        if (!body.isKinematic) body.linearVelocity = Vector3.zero;
    }

    public void ReceiveServerState(PrototypeActorState state)
    {
        bool wasDead = Dead;
        if (Human != null) Human.ApplyNetworkHealth(state.Health);
        if (Drone == null) return;
        if (state.Dead && !wasDead) Drone.ApplyNetworkDestroyed(false);
        else if (!state.Dead && wasDead)
        {
            Drone.SetFlightLocked(false);
            Drone.ResetDrone();
            MakeReplicaBody();
            ResetNetPose();
        }
    }

    private void Update()
    {
        if (IsLocal || Dead || buffer.Count == 0) return;
        if (!buffer.Sample(buffer.Clock.RenderTime(NetSync.Now), out var pose, out var velocity)) return;
        transform.SetPositionAndRotation(pose.Position, pose.Rotation);
        if (View != null) View.transform.rotation = pose.View;
        if (Human != null && Human.IsCrouching != ((pose.Flags & NetPose.CrouchingFlag) != 0))
            Human.ApplyRemoteStance((pose.Flags & NetPose.CrouchingFlag) != 0);
        if (Drone != null)
        {
            Drone.ApplyRemoteTelemetry(pose.Throttle / 255f, pose.FlightSeconds, (pose.Flags & NetPose.MotorsFlag) != 0,
                (pose.Flags & NetPose.AcroFlag) != 0 ? (int)DroneController.FlightMode.Acro : (int)DroneController.FlightMode.Angle);
            Drone.NetworkVelocity = velocity;
        }
    }
}
