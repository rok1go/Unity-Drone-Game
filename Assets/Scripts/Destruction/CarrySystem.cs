using System;
using System.Collections.Generic;
using UnityEngine;

// Players grab physics objects (props and loose rubble) and move them with real forces, and can fix
// a held object to whatever it touches. Runs only where physics is authoritative: offline, or on the
// online host for every player.
//
// Each grip pulls its own grab point towards a hold point in front of the player's eyes with a
// damped spring, applied with AddForceAtPosition. A player's pull is capped by their strength, so
// weight classes fall out of the physics: light things snap to hand, medium ones lag and sway, and
// anything heavier than one player can lift stays down until a second player takes the other end.
public sealed class CarrySystem : MonoBehaviour
{
    public const float PlayerStrength = 40f;   // kg one player can hold up
    public const float EffortMass = 15f;       // above this a single player visibly strains
    public const int MaxCarriers = 2;
    public const float MaxMass = PlayerStrength * MaxCarriers + 40f;   // 120 kg: heavier things never move
    public const float Reach = 2.8f;
    public const ulong OfflineCarrier = ulong.MaxValue;
    private const float Stiffness = 55f;       // spring, 1/s^2
    private const float Damping = 13f;         // ~critical for the stiffness above
    private const float StrengthMargin = 1.35f;// pull beyond holding the weight still: acceleration headroom
    private const float SnagDistance = 2.2f;
    private const float SnagTime = 0.7f;
    private const float AttachGap = 0.07f;     // metres: closer than this counts as touching
    private const float HeldAngularDamping = 5f;
    public const float MinHold = 1.1f;

    // The carrier's facing about the vertical: a held object turns with you, not with your head's pitch.
    public static Quaternion Heading(Transform eye)
    {
        Vector3 forward = Vector3.ProjectOnPlane(eye.forward, Vector3.up);
        return forward.sqrMagnitude > 0.0001f ? Quaternion.LookRotation(forward, Vector3.up) : Quaternion.identity;
    }

    // Wheel (distance) and RMB-drag (orientation) of what this carrier holds.
    public void SetHold(ulong carrier, float distance, Quaternion relative, bool turning = false)
    {
        if (!grips.TryGetValue(carrier, out var grip)) return;
        grip.turning = turning;
        grip.distance = Mathf.Clamp(distance, MinHold, Reach);
        if (relative.x * relative.x + relative.y * relative.y + relative.z * relative.z + relative.w * relative.w > 0.5f)
            grip.relative = Quaternion.Normalize(relative);
    }

    public static int RequiredCarriers(float mass) => mass <= PlayerStrength ? 1 : mass <= MaxMass ? 2 : 99;

    private sealed class Grip
    {
        public ulong carrier;
        public Rigidbody body;
        public Transform eye;
        public Collider[] carrierColliders;
        public CapsuleCollider carrierBody;
        public Vector3 localAnchor;
        public float distance, stuck;
        public Quaternion relative;      // orientation relative to the carrier's heading
        public bool turning;             // RMB-drag in progress: the object is posed by hand, not simulated
    }

    private sealed class Held
    {
        public readonly List<Grip> grips = new List<Grip>();
        public Collider[] colliders;
        public float linearDamping, angularDamping, maxDepenetration;
        public CollisionDetectionMode detection;
        public float reach;    // horizontal extent of the object around a grip point
    }

    private sealed class Attachment
    {
        public FixedJoint joint;
        public Collider anchor;        // what it is fixed to
        public bool anchoredToWorld;   // static geometry: breaks if that geometry starts to move
    }

    private static CarrySystem instance;
    private readonly Dictionary<ulong, Grip> grips = new Dictionary<ulong, Grip>();
    private readonly Dictionary<Rigidbody, Held> held = new Dictionary<Rigidbody, Held>();
    private readonly Dictionary<Rigidbody, Attachment> attachments = new Dictionary<Rigidbody, Attachment>();
    private readonly List<ulong> drop = new List<ulong>();
    private readonly List<Rigidbody> detach = new List<Rigidbody>();
    private static readonly Collider[] nearby = new Collider[24];

    // Carrier, body (null when they let go). The host forwards this to remote players.
    public static event Action<ulong, Rigidbody> CarryChanged;
    // Body, number of players holding it: drives the "players 1/2" hint everywhere.
    public static event Action<Rigidbody, int> GripsChanged;

    public static CarrySystem Instance
    {
        get
        {
            if (instance == null)
            {
                var obj = new GameObject("CarrySystem");
                DontDestroyOnLoad(obj);
                instance = obj.AddComponent<CarrySystem>();
            }
            return instance;
        }
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        instance = null;
        CarryChanged = null;
        GripsChanged = null;
    }

    public static bool IsCarryable(Rigidbody body) =>
        body != null && (body.GetComponent<PhysicsProp>() != null || body.GetComponent<DestructionDebris>() != null);

    public static string Describe(Rigidbody body)
    {
        var prop = body.GetComponent<PhysicsProp>();
        return prop != null ? prop.Label : "Обломок";
    }

    public static bool TryFindTarget(Ray view, out Rigidbody body, out Vector3 point)
    {
        body = null;
        point = default;
        if (!Physics.Raycast(view, out var hit, Reach, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            return false;
        body = hit.rigidbody;
        point = hit.point;
        return IsCarryable(body);
    }

    public static bool IsHeldAnywhere(Rigidbody body) => instance != null && instance.held.ContainsKey(body);
    public bool IsCarrying(ulong carrier) => grips.ContainsKey(carrier);
    public Rigidbody Carried(ulong carrier) => grips.TryGetValue(carrier, out var g) ? g.body : null;
    public int CarrierCount(Rigidbody body) => body != null && held.TryGetValue(body, out var h) ? h.grips.Count : 0;
    public bool IsAttached(Rigidbody body) => body != null && attachments.ContainsKey(body);

    // ---------------- grabbing ----------------

    public bool Grab(ulong carrier, Transform eye, Collider[] carrierColliders, Rigidbody body, Vector3 localAnchor)
    {
        if (eye == null || body == null || !IsCarryable(body) || body.isKinematic || grips.ContainsKey(carrier) ||
            body.mass > MaxMass)
            return false;
        held.TryGetValue(body, out var existing);
        if (existing != null && existing.grips.Count >= MaxCarriers) return false;
        Vector3 anchor = body.transform.TransformPoint(localAnchor);
        if (Vector3.Distance(eye.position, anchor) > Reach + 0.6f) return false;
        if (CursedStone.Trigger(body)) return false;
        Detach(body);   // Taking hold of a fixed object frees it.
        if (existing == null)
        {
            existing = new Held { colliders = body.GetComponentsInChildren<Collider>(),
                                  linearDamping = body.linearDamping, angularDamping = body.angularDamping,
                                  maxDepenetration = body.maxDepenetrationVelocity, detection = body.collisionDetectionMode };
            Bounds bounds = new Bounds(body.worldCenterOfMass, Vector3.zero);
            foreach (var c in existing.colliders) if (c != null && c.enabled && !c.isTrigger) bounds.Encapsulate(c.bounds);
            existing.reach = Mathf.Min(new Vector2(bounds.extents.x, bounds.extents.z).magnitude, 0.9f);
            held[body] = existing;
            body.angularDamping = HeldAngularDamping;
            // Swept contacts while steered by hand: a held object stops at walls and doors instead of tunnelling.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        }
        // Gentle separation: an overlap is resolved without launching whoever it overlaps.
        body.maxDepenetrationVelocity = 1f;
        body.solverIterations = 12; body.solverVelocityIterations = 4;
        var grip = new Grip
        {
            carrier = carrier, body = body, eye = eye, carrierColliders = carrierColliders, localAnchor = localAnchor,
            carrierBody = Array.Find(carrierColliders ?? Array.Empty<Collider>(), c => c is CapsuleCollider) as CapsuleCollider,
            distance = Mathf.Clamp(Vector3.Distance(eye.position, anchor), MinHold, Reach),
            relative = Quaternion.Inverse(Heading(eye)) * body.rotation
        };
        grips[carrier] = grip;
        existing.grips.Add(grip);
        body.WakeUp();
        CarryChanged?.Invoke(carrier, body);
        GripsChanged?.Invoke(body, existing.grips.Count);
        return true;
    }

    // Letting go keeps the object's momentum: swing and release to throw.
    public void Release(ulong carrier)
    {
        if (!grips.TryGetValue(carrier, out var grip)) return;
        grips.Remove(carrier);
        int remaining = 0;
        if (grip.body != null && held.TryGetValue(grip.body, out var h))
        {
            h.grips.Remove(grip);
            remaining = h.grips.Count;
            if (remaining == 0)
            {
                held.Remove(grip.body);
                grip.body.angularDamping = h.angularDamping;
                grip.body.linearDamping = h.linearDamping;
                grip.body.maxDepenetrationVelocity = h.maxDepenetration;
                grip.body.useGravity = true;
                if (!grip.body.isKinematic) grip.body.collisionDetectionMode = h.detection;
            }
        }
        CarryChanged?.Invoke(carrier, null);
        if (grip.body != null) GripsChanged?.Invoke(grip.body, remaining);
    }

    public static void ReleaseBody(Rigidbody body)
    {
        if (instance == null || body == null) return;
        if (instance.held.TryGetValue(body, out var h))
            foreach (var grip in h.grips.ToArray()) instance.Release(grip.carrier);
        instance.Detach(body);
    }

    public void ReleaseAll()
    {
        foreach (var carrier in new List<ulong>(grips.Keys)) Release(carrier);
    }

    // ---------------- shoving (F) ----------------

    public const float PushReach = 2.4f;
    private const float PushImpulse = 140f;    // N·s of one shove: a 6 kg chair slides off, a 90 kg wardrobe creeps

    // One shove along the pusher's horizontal view, applied at the height of the centre of mass so it slides
    // rather than topples. Friction and anything behind it decide how far it goes; two players add up.
    public static bool Push(Rigidbody body, Vector3 point, Vector3 direction)
    {
        if (!DestructionSystem.Authority || body == null || body.isKinematic || !IsCarryable(body) ||
            IsHeldAnywhere(body)) return false;
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f) return false;
        direction.Normalize();
        float speed = Mathf.Clamp(PushImpulse / body.mass, 0.25f, 3.2f);
        Vector3 at = new Vector3(point.x, body.worldCenterOfMass.y, point.z);
        body.WakeUp();
        if (body.TryGetComponent<PhysicsProp>(out var shovedProp)) shovedProp.Shoved();
        body.AddForceAtPosition(direction * speed * body.mass, at, ForceMode.Impulse);
        return true;
    }

    // ---------------- fixing in place ----------------

    // The nearest surface the object touches that it could be fixed to, if any.
    public static bool TryFindContact(Rigidbody body, out Collider other, out Vector3 point)
    {
        other = null;
        point = default;
        if (body == null) return false;
        float best = AttachGap;
        foreach (var own in body.GetComponentsInChildren<Collider>())
        {
            if (own == null || !own.enabled) continue;
            Bounds bounds = own.bounds;
            int count = Physics.OverlapBoxNonAlloc(bounds.center, bounds.extents + Vector3.one * AttachGap, nearby,
                Quaternion.identity, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
            for (int i = 0; i < count; i++)
            {
                var candidate = nearby[i];
                var rb = candidate.attachedRigidbody;
                if (candidate == own || rb == body || (rb != null && rb.GetComponent<HumanController>() != null) ||
                    (rb != null && rb.GetComponent<DroneController>() != null))
                    continue;
                Vector3 onOther = candidate.ClosestPoint(bounds.center);
                Vector3 onOwn = own.ClosestPoint(onOther);
                float gap = Vector3.Distance(onOther, onOwn);
                if (gap > best) continue;
                best = gap;
                other = candidate;
                point = (onOther + onOwn) * 0.5f;
            }
        }
        return other != null;
    }

    // Fix the carrier's object to what it touches. Fails in mid-air.
    public static bool TryFindFixing(Rigidbody body, float reach, out Collider surface, out Vector3 point, out Vector3 shift)
    {
        surface = null; point = shift = default;
        if (body == null) return false;
        float best = reach;
        foreach (var own in body.GetComponentsInChildren<Collider>())
        {
            if (!own.enabled || own.isTrigger) continue;
            var bounds = own.bounds;
            foreach (var candidate in Physics.OverlapBox(bounds.center, bounds.extents + Vector3.one * reach,
                         Quaternion.identity, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            {
                var rb = candidate.attachedRigidbody;
                if (rb == body || (rb != null && !rb.isKinematic)) continue;
                if (rb != null && (rb.GetComponent<HumanController>() != null || rb.GetComponent<DroneController>() != null)) continue;
                Vector3 there = candidate.ClosestPoint(bounds.center);
                Vector3 here = own.ClosestPoint(there);
                float gap = Vector3.Distance(here, there);
                if (gap > best) continue;
                best = gap; surface = candidate; point = there; shift = there - here;
            }
        }
        return surface != null;
    }

    public bool QuickBarricade(ulong carrier)
    {
        if (!MatchRules.Current.Barricades || !grips.TryGetValue(carrier, out var grip) ||
            !TryFindFixing(grip.body, 0.4f, out _, out _, out var shift)) return false;
        var body = grip.body;
        Vector3 previous = body.position;
        body.position += shift;
        body.transform.position = body.position;
        Physics.SyncTransforms();
        if (Attach(carrier))
        {
            GameAudio.Play(SoundBank.Hammer, body.worldCenterOfMass, 0.65f);
            return true;
        }
        body.position = previous; body.transform.position = previous;
        return false;
    }

    public bool Attach(ulong carrier)
    {
        if (!MatchRules.Current.Barricades) return false;
        if (!grips.TryGetValue(carrier, out var grip) || grip.body == null) return false;
        var body = grip.body;
        if (!TryFindContact(body, out var other, out var fixing)) return false;
        ReleaseBody(body);
        var target = other.attachedRigidbody;
        bool toWorld = target == null || target.isKinematic;
        var joint = body.gameObject.AddComponent<FixedJoint>();
        joint.anchor = body.transform.InverseTransformPoint(fixing);
        joint.connectedBody = toWorld ? null : target;
        // A blast or a hard shove tears it off; heavier pieces are fixed more firmly.
        joint.breakForce = 3000f + body.mass * 60f;
        joint.breakTorque = 2000f + body.mass * 40f;
        joint.enableCollision = false;
        attachments[body] = new Attachment { joint = joint, anchor = other, anchoredToWorld = toWorld };
        RoundStats.Barricade();
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        return true;
    }

    public void Detach(Rigidbody body)
    {
        if (body == null || !attachments.TryGetValue(body, out var a)) return;
        attachments.Remove(body);
        if (a.joint != null) Destroy(a.joint);
    }

    // ---------------- simulation ----------------

    // Looking down must not pull the object into its own carrier: the hold point stays in front of the
    // carrier's capsule by the object's own horizontal size, so it is held at the feet, not inside them.
    private static Vector3 OutsideCarrier(Grip grip, Held h, Vector3 target)
    {
        var capsule = grip.carrierBody;
        if (capsule == null || !capsule.enabled) return target;
        Bounds b = capsule.bounds;
        if (target.y > b.max.y + h.reach || target.y < b.min.y - h.reach) return target;
        Vector3 axis = new Vector3(b.center.x, 0f, b.center.z);
        Vector3 flat = new Vector3(target.x, 0f, target.z) - axis;
        float keep = capsule.radius + h.reach + 0.08f;
        if (flat.sqrMagnitude >= keep * keep) return target;
        Vector3 forward = Vector3.ProjectOnPlane(grip.eye.forward, Vector3.up);
        Vector3 direction = flat.sqrMagnitude > 0.0025f ? flat.normalized :
            forward.sqrMagnitude > 0.0001f ? forward.normalized : grip.eye.parent != null ? grip.eye.parent.forward : Vector3.forward;
        Vector3 pushed = axis + direction * keep;
        return new Vector3(pushed.x, target.y, pushed.z);
    }

    private void FixedUpdate()
    {
        float dt = Time.fixedDeltaTime;
        drop.Clear();
        foreach (var pair in held)
        {
            var body = pair.Key;
            var h = pair.Value;
            if (body == null || body.isKinematic)
            {
                foreach (var g in h.grips) drop.Add(g.carrier);
                continue;
            }
            // Being turned with RMB: no physics on it at all. It hangs where it is, without gravity or momentum,
            // and takes exactly the orientation the mouse gives; physics resumes the moment RMB is let go.
            bool posed = h.grips.Count == 1 && h.grips[0].turning && h.grips[0].eye != null;
            if (body.useGravity == posed) body.useGravity = !posed;
            if (posed)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
                body.MoveRotation(Heading(h.grips[0].eye) * h.grips[0].relative);
                continue;
            }
            float share = body.mass / h.grips.Count;
            foreach (var grip in h.grips)
            {
                if (grip.eye == null || !grip.eye.gameObject.activeInHierarchy)
                {
                    drop.Add(grip.carrier);
                    continue;
                }
                Vector3 anchor = body.transform.TransformPoint(grip.localAnchor);
                Vector3 target = grip.eye.position + grip.eye.forward * grip.distance;
                // The view ray cannot pull a hand point to the far side of a wall or closed door.
                foreach (var hit in Physics.RaycastAll(grip.eye.position, grip.eye.forward, grip.distance,
                             Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    if (hit.rigidbody == body || (hit.rigidbody != null && hit.rigidbody.GetComponent<HumanController>() != null)) continue;
                    float distance = Vector3.Dot(target - grip.eye.position, grip.eye.forward);
                    if (hit.distance < distance) target = grip.eye.position + grip.eye.forward * Mathf.Max(0.15f, hit.distance - 0.12f);
                }
                target = OutsideCarrier(grip, h, target);
                Vector3 offset = target - anchor;
                grip.stuck = offset.magnitude > SnagDistance ? grip.stuck + dt : 0f;
                if (grip.stuck > SnagTime)
                {
                    drop.Add(grip.carrier);
                    continue;
                }
                // Spring-damper on this grip's share of the mass, plus holding up that share against gravity,
                // capped at what one person can exert.
                Vector3 velocity = body.GetPointVelocity(anchor);
                Vector3 acceleration = Vector3.ClampMagnitude(offset * Stiffness - velocity * Damping, 14f);
                Vector3 force = (acceleration - Physics.gravity) * share;
                force = Vector3.ClampMagnitude(force, PlayerStrength * -Physics.gravity.y * StrengthMargin);
                body.AddForceAtPosition(force, anchor, ForceMode.Force);
            }
            if (body.linearVelocity.sqrMagnitude > 8f * 8f) body.linearVelocity = Vector3.ClampMagnitude(body.linearVelocity, 8f);
            // One carrier also steers its orientation: it turns with them and follows RMB-drag. A spring on the
            // angular velocity, weaker for heavy things, so it still bumps and stops against walls.
            if (h.grips.Count == 1 && h.grips[0].eye != null)
            {
                var g = h.grips[0];
                Quaternion target = Heading(g.eye) * g.relative;
                (target * Quaternion.Inverse(body.rotation)).ToAngleAxis(out float angle, out Vector3 axis);
                if (angle > 180f) angle -= 360f;
                float stiffness = Mathf.Lerp(9f, 3f, Mathf.Clamp01(body.mass / PlayerStrength));
                Vector3 wanted = float.IsFinite(axis.x) ? axis * (angle * Mathf.Deg2Rad * stiffness) : Vector3.zero;
                wanted = Vector3.ClampMagnitude(wanted, 6f);
                body.AddTorque(Vector3.ClampMagnitude(wanted - body.angularVelocity, 30f * dt * stiffness), ForceMode.VelocityChange);
            }
        }
        foreach (var carrier in drop) Release(carrier);
        Inventory.HostUpdate();

        detach.Clear();
        foreach (var pair in attachments)
        {
            var a = pair.Value;
            // Joint broken by force, the anchor destroyed, or the wall it was fixed to came loose.
            if (pair.Key == null || a.joint == null || a.anchor == null || !a.anchor.enabled ||
                (a.anchoredToWorld && a.anchor.attachedRigidbody != null && !a.anchor.attachedRigidbody.isKinematic))
                detach.Add(pair.Key);
        }
        foreach (var body in detach) Detach(body);
    }
}
