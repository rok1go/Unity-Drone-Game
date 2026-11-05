using UnityEngine;

public class PrototypeNetworkActor : MonoBehaviour
{
    public ulong OwnerId { get; private set; }
    public PrototypeTeam Team { get; private set; }
    public HumanController Human { get; private set; }
    public DroneController Drone { get; private set; }
    public bool IsLocal => MultiplayerPrototype.Instance != null && OwnerId == MultiplayerPrototype.Instance.LocalId;
    public bool Dead => Team == PrototypeTeam.Humans ? Human.IsEliminated : Drone.IsDestroyed;
    public Camera View => Team == PrototypeTeam.Humans ? Human.viewCamera : Drone.viewCamera;
    private Rigidbody body;
    private PrototypePose target;

    public void Configure(ulong id, PrototypeTeam team)
    {
        OwnerId = id; Team = team;
        Human = GetComponent<HumanController>(); Drone = GetComponent<DroneController>();
        body = GetComponent<Rigidbody>();
        if (Human != null) { Human.SetControlled(false); Human.IsNetworkReplica = !IsLocal; }
        if (Drone != null) { Drone.SetControlled(false); Drone.IsNetworkReplica = !IsLocal; }
        if (!IsLocal)
        {
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.isKinematic = true;
        }
        if (View != null)
        {
            View.enabled = false;
            var audio = View.GetComponent<AudioListener>();
            if (audio != null) audio.enabled = false;
        }
    }

    public PrototypePose Capture()
    {
        return new PrototypePose
        {
            Id = OwnerId, Position = transform.position, Rotation = transform.rotation,
            ViewRotation = View != null ? View.transform.rotation : transform.rotation,
            Velocity = body != null && !body.isKinematic ? body.linearVelocity : target != null ? target.Velocity : Vector3.zero,
            Health = Human != null ? Human.Health : 0f, Dead = Dead,
            Crouching = Human != null && Human.IsCrouching,
            Throttle = Drone != null ? Drone.Throttle : 0f,
            Motors = Drone != null && Drone.MotorsArmed,
            FlightSeconds = Drone != null ? Drone.FlightSeconds : 0f,
            FlightMode = Drone != null ? (int)Drone.flightMode : 0
        };
    }

    public void ReceiveOwnerPose(PrototypePose pose)
    {
        target = pose;
        transform.SetPositionAndRotation(pose.Position, pose.Rotation);
        body.position = pose.Position; body.rotation = pose.Rotation;
        ApplyAppearance(pose);
    }

    public void ReceiveServerPose(PrototypePose pose)
    {
        bool wasDead = Dead;
        if (Human != null) Human.ApplyNetworkHealth(pose.Health);
        if (Drone != null)
        {
            if (pose.Dead && !wasDead) Drone.ApplyNetworkDestroyed(false);
            else if (!pose.Dead && wasDead)
            {
                Drone.SetFlightLocked(false);
                Drone.ResetDrone();
                if (!IsLocal) { body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative; body.isKinematic = true; }
            }
        }
        target = pose;
        if (IsLocal)
        {
            if (!pose.Dead && (wasDead || Vector3.Distance(transform.position, pose.Position) > 3f))
            {
                body.position = pose.Position; transform.position = pose.Position;
                if (!body.isKinematic) body.linearVelocity = pose.Velocity;
            }
        }
        else ApplyAppearance(pose);
    }

    private void ApplyAppearance(PrototypePose pose)
    {
        if (Human != null) Human.ApplyRemoteStance(pose.Crouching);
        if (Drone != null) Drone.ApplyRemoteTelemetry(pose.Throttle, pose.FlightSeconds, pose.Motors, pose.FlightMode);
        if (View != null) View.transform.rotation = pose.ViewRotation;
    }

    private void Update()
    {
        if (IsLocal || target == null || Dead || MultiplayerPrototype.Instance.IsHost) return;
        float blend = 1f - Mathf.Exp(-18f * Time.deltaTime);
        transform.SetPositionAndRotation(Vector3.Lerp(transform.position, target.Position, blend),
            Quaternion.Slerp(transform.rotation, target.Rotation, blend));
        if (View != null) View.transform.rotation = target.ViewRotation;
    }
}
