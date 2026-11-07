using UnityEngine;
using UnityEngine.InputSystem;

// A dynamic capsule is used instead of CharacterController so platforms can lift it
// through normal PhysX contacts. Input never owns gravity or the collider lifecycle.
// Stances: standing, crouching and prone (a short, narrow capsule that fits under beds and cars).
// Space vaults over or climbs onto low obstacles along a path checked for room in advance; Q / E
// lean around corners without letting the camera pass through walls.
[RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
public class HumanController : MonoBehaviour
{
    public enum Stance : byte { Standing, Crouching, Prone }

    public Camera viewCamera;
    [Min(0)] public float walkSpeed = 3.5f;
    [Min(0)] public float sprintSpeed = 6f;
    [Min(0)] public float crouchSpeed = 1.8f;
    [Min(0)] public float crawlSpeed = 0.9f;
    [Min(0)] public float jumpHeight = 2f;
    [Min(0.1f)] public float gravity = 20f;
    [Min(1)] public float standingHeight = 1.8f;
    [Min(0.6f)] public float crouchingHeight = 1.1f;
    [Min(0.3f)] public float proneHeight = 0.46f;
    [Header("Physical movement")]
    [Min(0.1f)] public float movementAcceleration = 25f;
    [Range(0, 1)] public float airControl = 0.25f;
    [Range(0f, 0.4f)] public float stepHeight = 0.35f;
    [Header("Leaning")]
    [Range(0.1f, 0.6f)] public float leanDistance = 0.38f;
    [Range(0f, 25f)] public float leanRoll = 12f;
    [Header("Health")]
    [Min(1f)] public float maxHealth = 100f;
    [Tooltip("Body mass in kg. Props, rubble and held objects push against this; a near-weightless body is shoved around by everything.")]
    [Min(30f)] public float bodyMass = 75f;
    public float Health { get; private set; }
    public float LastDamage { get; private set; }
    public float LastDamageTime { get; private set; } = float.NegativeInfinity;
    public bool IsControlled { get; private set; }
    public bool IsNetworkReplica { get; set; }
    public Stance CurrentStance { get; private set; }
    public bool IsCrouching => CurrentStance == Stance.Crouching;
    public bool IsProne => CurrentStance == Stance.Prone;
    public bool IsGrounded { get; private set; }
    public bool IsEliminated { get; private set; }
    public bool IsVaulting => vaultStep >= 0;
    public bool CanVault { get; private set; }
    // -1 full left .. +1 full right; replicas get it from the network.
    public float Lean { get; private set; }
    public event System.Action<HumanController> Eliminated;
    // The local player landed hard: damage to apply (offline) or report to the host (online).
    public static event System.Action<HumanController, float> FallDamaged;
    public float CurrentHeight => capsule != null ? capsule.height : standingHeight;
    private Rigidbody body;
    private CapsuleCollider capsule;
    private float standingRadius;
    private Vector2 moveInput;
    private bool sprintInput, crouchInput, proneInput, jumpRequested, jumped, interactConsumed;
    private float leanInput;
    private Vector3 windowTravelDirection;
    private float windowTravelEnd;
    private float pitch, yaw, stepCameraOffset, fallSpeed, bob, nextVaultCheck;
    private bool wasGrounded;
    private float airTime, airPeak, landingSpeed, lastAirVertical, lastLanding = -10f;
    private PhysicsMaterial playerContact;
    private readonly RaycastHit[] movementHits = new RaycastHit[32];
    private readonly Vector3[] vaultPath = new Vector3[3];
    private int vaultStep = -1, vaultLength;
    private float vaultTime;
    private Vector3 spawnPosition;
    private Quaternion spawnRotation;
    private readonly Collider[] headroomHits = new Collider[16];
    private readonly RaycastHit[] groundHits = new RaycastHit[16];

    private void Awake()
    {
        body = GetComponent<Rigidbody>(); capsule = GetComponent<CapsuleCollider>();
        standingRadius = capsule.radius;
        body.isKinematic = false; body.useGravity = true;
        body.mass = Mathf.Max(30f, bodyMass);
        body.constraints = RigidbodyConstraints.FreezeRotation;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.solverIterations = 12; body.solverVelocityIterations = 4;
        body.maxDepenetrationVelocity = 1.5f;
        playerContact = new PhysicsMaterial("Player contact") { dynamicFriction = 0f, staticFriction = 0f,
            frictionCombine = PhysicsMaterialCombine.Minimum, bounciness = 0f, bounceCombine = PhysicsMaterialCombine.Minimum };
        capsule.sharedMaterial = playerContact;
        yaw = body.rotation.eulerAngles.y;
        spawnPosition = body.position;
        spawnRotation = body.rotation;
        RestoreHealth();
    }

    public void SetControlled(bool controlled)
    {
        if (IsControlled && !(controlled && !IsEliminated)) PlayerCarry.ControlLost(this);
        IsControlled = controlled && !IsEliminated;
        moveInput = Vector2.zero; sprintInput = false; jumpRequested = false;
        jumped = false; leanInput = 0f;
        windowTravelDirection = Vector3.zero;
        vaultStep = -1;
        // Keep the current stance when parked, so switching under a low ceiling is safe.
        crouchInput = IsCrouching;
        proneInput = IsProne;
        if (viewCamera == null) return;
        viewCamera.enabled = IsControlled;
        var listener = viewCamera.GetComponent<AudioListener>();
        if (listener != null) listener.enabled = IsControlled;
    }

    public void ApplyDamage(float amount)
    {
        if (IsEliminated || amount <= 0f || float.IsNaN(amount) || float.IsInfinity(amount)) return;
        LastDamage = Mathf.Min(Health, amount);
        LastDamageTime = Time.unscaledTime;
        Health = Mathf.Max(0f, Health - amount);
        if (Health <= 0f) Eliminate();
    }

    public void ApplyNetworkHealth(float health)
    {
        if (IsEliminated && health > 0f) ResetHuman();
        if (IsEliminated) return;
        health = Mathf.Clamp(health, 0f, maxHealth);
        if (health < Health) { LastDamage = Health - health; LastDamageTime = Time.unscaledTime; }
        Health = health;
        if (Health <= 0f) Eliminate();
    }

    public void ApplyRemoteStance(Stance stance, float lean)
    {
        if (!IsNetworkReplica) return;
        Lean = lean;
        if (stance != CurrentStance) SetStance(stance);
    }

    public void RestoreHealth()
    {
        if (IsEliminated) return;
        Health = Mathf.Max(1f, maxHealth);
        LastDamage = 0f;
        LastDamageTime = float.NegativeInfinity;
    }

    public void Eliminate()
    {
        if (IsEliminated) return;
        IsEliminated = true;
        Health = 0f;
        SetControlled(false);
        gameObject.SetActive(false);
        Eliminated?.Invoke(this);
    }

    public void SetTrainingSpawn(Vector3 position, Quaternion rotation)
    {
        spawnPosition = position;
        spawnRotation = rotation;
        ResetHuman();
    }

    public void SetSpawn(Vector3 position, Quaternion rotation)
    {
        spawnPosition = position;
        spawnRotation = rotation;
    }

    // Back at the spawn point with full health: a new round, or a new life in the same one.
    public void ResetHuman()
    {
        IsEliminated = false;
        RestoreHealth();
        if (!body.isKinematic)
        {
            body.linearVelocity = Vector3.zero;
            body.angularVelocity = Vector3.zero;
        }
        body.position = spawnPosition;
        body.rotation = spawnRotation;
        transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        yaw = spawnRotation.eulerAngles.y;
        pitch = 0;
        stepCameraOffset = 0f;
        IsGrounded = false;
        fallSpeed = 0f;
        airTime = 0f; landingSpeed = 0f; lastAirVertical = 0f; wasGrounded = false;
        airPeak = spawnPosition.y;
        Lean = 0f;
        vaultStep = -1;
        SetStance(Stance.Standing);
        if (viewCamera != null) viewCamera.transform.localRotation = Quaternion.identity;
        SetControlled(false);
        gameObject.SetActive(true);
        Physics.SyncTransforms();
    }

    private void Update()
    {
        moveInput = Vector2.zero; sprintInput = false; leanInput = 0f;
        bool active = IsControlled && Cursor.lockState == CursorLockMode.Locked && MultiplayerPrototype.Instance?.MenuOpen != true;
        // Grabbing physics objects (mouse buttons), hints and highlight: also lets go when the menu opens.
        if (IsControlled && viewCamera != null)
            PlayerCarry.Tick(this, new Ray(viewCamera.transform.position, viewCamera.transform.forward), active);
        if (!active) return;
        var settings = GameSettings.Current;
        moveInput = Vector2.ClampMagnitude(new Vector2(
            (GameSettings.Held(GameAction.MoveRight) ? 1 : 0) - (GameSettings.Held(GameAction.MoveLeft) ? 1 : 0),
            (GameSettings.Held(GameAction.MoveForward) ? 1 : 0) - (GameSettings.Held(GameAction.MoveBack) ? 1 : 0)), 1);
        sprintInput = GameSettings.Held(GameAction.Sprint) && !IsProne;
        if (MatchRules.Current.Prone && GameSettings.Pressed(GameAction.Prone)) proneInput = !proneInput;
        // Crouch while prone means "get up to a crouch".
        if (GameSettings.Pressed(GameAction.Crouch) && proneInput) proneInput = false;
        crouchInput = GameSettings.Held(GameAction.Crouch);
        if (GameSettings.Pressed(GameAction.Jump))
        {
            if (proneInput) proneInput = false;
            else jumpRequested = true;
        }
        var view = new Ray(viewCamera.transform.position, viewCamera.transform.forward);
        // E acts on what is under the crosshair (or places a barricade); with nothing there, holding it leans right.
        if (GameSettings.Pressed(GameAction.Interact))
            interactConsumed = Interaction.Press(view, body.position);
        Interaction.Hold(GameSettings.Held(GameAction.Interact), body.position);
        if (!GameSettings.Held(GameAction.Interact)) interactConsumed = false;
        // F gives what is under the crosshair one physical shove; G throws the selected inventory item.
        if (MatchRules.Current.Carrying && GameSettings.Pressed(GameAction.Push)) PlayerCarry.TryPush(this, view);
        Inventory.Tick(this, view);
        bool rightHeld = GameSettings.Held(GameAction.LeanRight) &&
            (GameSettings.KeyFor(GameAction.LeanRight) != GameSettings.KeyFor(GameAction.Interact) || !interactConsumed);
        if (MatchRules.Current.Leaning && (!sprintInput || moveInput.sqrMagnitude < 0.01f))
            leanInput = (rightHeld ? 1f : 0f) - (GameSettings.Held(GameAction.LeanLeft) ? 1f : 0f);
        Vector2 look = Mouse.current != null && !PlayerCarry.Rotating ? Mouse.current.delta.ReadValue() : Vector2.zero;
        float sensitivity = settings.HumanSensitivity * (Mathf.Abs(Lean) > 0.3f ? settings.LeanSensitivity : 1f);
        yaw += look.x * sensitivity;
        pitch = Mathf.Clamp(pitch - look.y * sensitivity * (settings.InvertHuman ? -1f : 1f), IsProne ? -45f : -85f, 85f);
        if (Time.unscaledTime >= nextVaultCheck)
        {
            nextVaultCheck = Time.unscaledTime + 0.1f;
            CanVault = !IsVaulting && IsGrounded && !IsProne && PlanVault(false);
            IsHidden = CheckHidden();
        }
    }

    private void FixedUpdate()
    {
        if (IsNetworkReplica) return;
        Quaternion heading = Quaternion.Euler(0, yaw, 0);
        body.MoveRotation(heading);
        if (IsVaulting) { ContinueVault(); return; }
        UpdateStance();
        ResolvePenetrations();
        IsGrounded = false;
        Rigidbody support = null;
        Vector3 supportPoint = body.position;
        float nearest = float.PositiveInfinity;
        float radius = capsule.radius * 0.95f;
        int count = Physics.SphereCastNonAlloc(body.position + Vector3.up * (capsule.radius + 0.08f), radius,
            Vector3.down, groundHits, 0.18f, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var hit = groundHits[i];
            if (hit.rigidbody == body || hit.normal.y < 0.6f || hit.distance >= nearest) continue;
            // Something in someone's hands is not ground: standing on it would let a player lift themselves.
            if (hit.rigidbody != null && PlayerCarry.CarrierCount(hit.rigidbody) > 0) continue;
            nearest = hit.distance; support = hit.rigidbody; supportPoint = hit.point; IsGrounded = true;
        }
        Vector3 supportVelocity = support != null ? support.GetPointVelocity(supportPoint) : Vector3.zero;
        TrackFall(supportVelocity);
        float speed = IsProne ? crawlSpeed : IsCrouching ? crouchSpeed : sprintInput ? sprintSpeed : walkSpeed;
        // Carrying something heavy slows you down (up to 45% at the 40 kg limit).
        float load = PlayerCarry.LoadFactor(this);
        speed *= Mathf.Lerp(1f, 0.55f, load);
        Vector3 requested = heading * new Vector3(moveInput.x, 0, moveInput.y) * speed;
        Vector3 desired = ConstrainMovement(requested);
        if (IsGrounded && body.linearVelocity.y <= 0f && windowTravelDirection == Vector3.zero)
            jumped = false;
        if (IsControlled && crouchInput && jumped && TryTraverseWindow(desired))
        {
            jumpRequested = false;
            return;
        }
        windowTravelDirection = Vector3.zero;
        // Space in front of a fence, sill or table climbs over it instead of jumping.
        if (jumpRequested && IsGrounded && !IsProne && PlanVault(true))
        {
            jumpRequested = false;
            return;
        }
        // Steps use the requested direction: the constraint below removes motion into a riser.
        if (IsControlled && IsGrounded && !jumpRequested && stepHeight > 0f && requested.sqrMagnitude > 0.001f && !IsProne)
            TryStepUp(requested);
        // Relative ground movement follows a dynamic platform without braking it to world zero.
        Vector3 delta = desired + supportVelocity - body.linearVelocity; delta.y = 0;
        if (IsGrounded || (IsControlled && moveInput.sqrMagnitude > 0))
            body.AddForce(Vector3.ClampMagnitude(delta / Time.fixedDeltaTime,
                movementAcceleration * (IsGrounded ? 1 : airControl)), ForceMode.Acceleration);
        if (jumpRequested && IsGrounded && CurrentStance == Stance.Standing)
        {
            jumped = true;
            float relativeY = body.linearVelocity.y - supportVelocity.y;
            body.AddForce(Vector3.up * (Mathf.Sqrt(2 * gravity * jumpHeight * Mathf.Lerp(1f, 0.5f, load)) - relativeY),
                ForceMode.VelocityChange);
        }
        jumpRequested = false;
        // Preserve the existing configurable gravity; native gravity stays enabled for both roles.
        body.AddForce(Vector3.down * (gravity + Physics.gravity.y), ForceMode.Acceleration);
    }

    // ---------------- stances ----------------

    private void UpdateStance()
    {
        Stance wanted = proneInput ? Stance.Prone : crouchInput ? Stance.Crouching : Stance.Standing;
        // Standing up (or out of prone) only where there is room above; otherwise stay low.
        if (wanted == Stance.Standing && CurrentStance != Stance.Standing && !HasRoom(standingHeight, standingRadius))
            wanted = HasRoom(crouchingHeight, standingRadius) ? Stance.Crouching : CurrentStance;
        if (wanted == Stance.Crouching && CurrentStance == Stance.Prone && !HasRoom(crouchingHeight, standingRadius))
            wanted = Stance.Prone;
        if (wanted == CurrentStance) return;
        SetStance(wanted);
        if (wanted != Stance.Prone) proneInput = false;
    }

    private void SetStance(Stance stance)
    {
        CurrentStance = stance;
        capsule.radius = stance == Stance.Prone ? Mathf.Min(standingRadius, proneHeight * 0.48f) : standingRadius;
        capsule.height = stance == Stance.Prone ? proneHeight : stance == Stance.Crouching ? crouchingHeight : standingHeight;
        capsule.center = Vector3.up * capsule.height / 2f;
    }

    private bool HasRoom(float height, float radius) => HasCapsuleClearance(body.position, height, radius);

    // ---------------- falling ----------------

    private void TrackFall(Vector3 supportVelocity)
    {
        if (!IsGrounded)
        {
            if (wasGrounded || airTime == 0f) airPeak = body.position.y;
            airPeak = Mathf.Max(airPeak, body.position.y);
            airTime += Time.fixedDeltaTime;
            lastAirVertical = body.linearVelocity.y;
        }
        else
        {
            if (!wasGrounded && airTime >= 0.25f && airPeak - body.position.y > SafeDrop && Time.time - lastLanding > 0.5f)
            {
                // The body's own speed into the ground just before touching it. A contact reading can only lower
                // it (a soft or sloped landing), never raise it: rubble or a prop moving under the feet is not a fall.
                float falling = Mathf.Max(0f, supportVelocity.y - lastAirVertical);
                float impact = landingSpeed > 0f ? Mathf.Min(landingSpeed, falling) : falling;
                float damage = LandingDamage(impact);
                lastLanding = Time.time;
                damage *= MatchRules.Current.FallDamageScale;
                if (IsControlled && MatchRules.Current.FallDamage && damage >= 1f)
                {
                    if (MultiplayerPrototype.OnlineMatch) FallDamaged?.Invoke(this, damage);
                    else ApplyDamage(damage);
                }
            }
            airTime = 0f; landingSpeed = 0f; airPeak = body.position.y;
        }
        wasGrounded = IsGrounded;
    }

    // Gravity here is 20 m/s², so speed maps to drop height as v = sqrt(40 h). Up to ~4 m (12.5 m/s) is free:
    // jumps, stairs, a storey down. Then damage grows with the square of the excess speed:
    // 6 m ≈ 9, 9 m ≈ 42, 12 m ≈ 88, 15 m ≈ 144 (lethal).
    public const float SafeLandingSpeed = 12.5f;
    private const float SafeDrop = 2.5f;
    public static float LandingDamage(float speed)
    {
        float excess = Mathf.Max(0f, speed - SafeLandingSpeed);
        return excess * excess;
    }

    private void OnCollisionEnter(Collision collision) => ReadContact(collision);
    private void OnCollisionStay(Collision collision) => ReadContact(collision);
    private void ReadContact(Collision collision)
    {
        if (IsNetworkReplica || collision.contactCount == 0) return;
        for (int i = 0; i < collision.contactCount; i++)
        {
            var contact = collision.GetContact(i);
            if (contact.normal.y > 0.6f && airTime > 0.12f)
                landingSpeed = Mathf.Max(landingSpeed, Mathf.Max(0f, -Vector3.Dot(collision.relativeVelocity, contact.normal)));
            var other = collision.rigidbody;
            if (other == null || !CarrySystem.IsCarryable(other) || Mathf.Abs(contact.normal.y) > 0.6f) continue;
            // A held object or a kinematic network replica cannot launch its carrier sideways.
            float away = Vector3.Dot(body.linearVelocity, contact.normal);
            if (away > 2.5f) body.linearVelocity -= contact.normal * (away - 2.5f);
        }
    }

    private Vector3 ConstrainMovement(Vector3 desired)
    {
        if (desired.sqrMagnitude < 0.001f) return desired;
        float radius = capsule.radius - 0.015f;
        int count = Physics.CapsuleCastNonAlloc(body.position + Vector3.up * capsule.radius,
            body.position + Vector3.up * (capsule.height - capsule.radius), radius, desired.normalized,
            movementHits, desired.magnitude * Time.fixedDeltaTime + 0.025f, ~0, QueryTriggerInteraction.Ignore);
        for (int i = 0; i < count; i++)
        {
            var hit = movementHits[i];
            if (hit.rigidbody == body || hit.normal.y > 0.65f) continue;
            // Already touching (distance 0, normal = -direction): ResolvePenetrations handles it; treating it
            // as a wall would stop all motion in a tight doorway.
            if (hit.distance <= 0f) continue;
            // A riser low enough to step onto is the step-up's job, not a wall.
            if (IsGrounded && hit.point.y - body.position.y <= stepHeight + 0.02f) continue;
            var other = hit.rigidbody;
            float into = Vector3.Dot(desired, hit.normal);
            if (into >= 0f) continue;
            // Walking never shoves furniture (that is F); only trifles (a cone, a basket) are brushed aside.
            // Held objects stay solid so a carrier cannot walk into what they hold.
            if (other != null && !other.isKinematic && other.mass < TrifleMass && CarrySystem.IsCarryable(other) &&
                PlayerCarry.CarrierCount(other) == 0)
            {
                other.AddForceAtPosition(Vector3.ClampMagnitude(-hit.normal * -into * other.mass * 4f, 25f), hit.point, ForceMode.Force);
                continue;
            }
            desired -= hit.normal * into;
        }
        desired.y = 0f;
        return desired;
    }

    private const float PushableMass = 80f;   // a wedged piece lighter than ~120 kg is eased out of the body
    private const float TrifleMass = 3f;
    private float pinnedFor;

    // Only fixed geometry is resolved by hand: PhysX already separates moving bodies, and doing both made
    // the player jitter and get shoved around on beds, tables and rubble.
    private static bool IsFixed(Collider other) => other.attachedRigidbody == null || other.attachedRigidbody.isKinematic;

    private void ResolvePenetrations()
    {
        int count = Physics.OverlapCapsuleNonAlloc(body.position + Vector3.up * capsule.radius,
            body.position + Vector3.up * (capsule.height - capsule.radius), capsule.radius,
            headroomHits, ~0, QueryTriggerInteraction.Ignore);
        Vector3 correction = Vector3.zero;
        float deepest = 0f;
        for (int i = 0; i < count; i++)
        {
            var other = headroomHits[i];
            if (other.attachedRigidbody == body || other.transform.IsChildOf(transform)) continue;
            if (!Physics.ComputePenetration(capsule, body.position, body.rotation, other, other.transform.position,
                    other.transform.rotation, out var direction, out float depth) || depth <= 0.008f) continue;
            deepest = Mathf.Max(deepest, depth);
            if (IsFixed(other)) correction += direction * Mathf.Min(depth + 0.002f, 0.03f);
            else if (other.attachedRigidbody.mass < PushableMass * 1.5f && PlayerCarry.CarrierCount(other.attachedRigidbody) == 0)
                // A loose piece wedged into the body is eased out of it instead of pinning the player.
                other.attachedRigidbody.AddForce(-direction * Mathf.Min(depth * 400f, 6f), ForceMode.VelocityChange);
        }
        // Wedged for a while (between rubble and a wall, under a fallen slab): find free space nearby.
        pinnedFor = deepest > 0.04f ? pinnedFor + Time.fixedDeltaTime : 0f;
        if (pinnedFor > 0.6f && TryFreeFromPinch()) { pinnedFor = 0f; return; }
        correction = Vector3.ClampMagnitude(correction, 0.04f);
        if (correction.sqrMagnitude < 0.000001f) return;
        // Never create a new overlap or deepen an overlap with a wall on the escape side.
        foreach (var other in Physics.OverlapCapsule(body.position + correction + Vector3.up * capsule.radius,
                     body.position + correction + Vector3.up * (capsule.height - capsule.radius), capsule.radius,
                     ~0, QueryTriggerInteraction.Ignore))
        {
            if (other.attachedRigidbody == body || other.transform.IsChildOf(transform) || !IsFixed(other)) continue;
            bool before = Physics.ComputePenetration(capsule, body.position, body.rotation, other, other.transform.position,
                other.transform.rotation, out _, out float oldDepth);
            bool after = Physics.ComputePenetration(capsule, body.position + correction, body.rotation, other, other.transform.position,
                other.transform.rotation, out _, out float newDepth);
            if (after && (!before || newDepth > oldDepth + 0.001f)) return;
        }
        body.position += correction;
    }
    private static readonly float[] pinchSteps = { 0.12f, 0.25f, 0.4f, 0.6f };

    // A lower stance first, then the nearest free spot within 0.6 m that is reachable from the body's
    // centre without crossing solid geometry, so the escape never passes through a wall.
    private bool TryFreeFromPinch()
    {
        Vector3 feet = body.position;
        if (CurrentStance == Stance.Standing && HasCapsuleClearance(feet, crouchingHeight, standingRadius))
        { crouchInput = true; SetStance(Stance.Crouching); return true; }
        if (CurrentStance != Stance.Prone && MatchRules.Current.Prone &&
            HasCapsuleClearance(feet, proneHeight, Mathf.Min(standingRadius, proneHeight * 0.48f)))
        { proneInput = true; SetStance(Stance.Prone); return true; }
        Vector3 centre = feet + Vector3.up * capsule.height * 0.5f;
        for (int s = 0; s < pinchSteps.Length; s++)
            for (int k = 0; k < 10; k++)
            {
                Vector3 offset = k < 8
                    ? Quaternion.Euler(0f, k * 45f + yaw, 0f) * Vector3.forward * pinchSteps[s]
                    : Vector3.up * pinchSteps[s] * (k == 8 ? 1f : 0.5f);
                Vector3 candidate = feet + offset;
                if (!HasCapsuleClearance(candidate, capsule.height, capsule.radius)) continue;
                if (Physics.Linecast(centre, candidate + Vector3.up * capsule.height * 0.5f, out var blocked, ~0,
                        QueryTriggerInteraction.Ignore) && blocked.rigidbody != body && IsFixed(blocked.collider)) continue;
                body.position = candidate;
                body.linearVelocity = Vector3.zero;
                return true;
            }
        return false;
    }

    // Inside a wardrobe with its leaves shut: solid on every side within arm's reach, a roof right overhead,
    // and a closed cupboard leaf among the walls. A wardrobe that is blown open or apart no longer counts.
    public bool IsHidden { get; private set; }
    private static readonly Vector3[] hideDirections = { Vector3.forward, Vector3.back, Vector3.left, Vector3.right };
    private bool CheckHidden()
    {
        Vector3 chest = body.position + Vector3.up * Mathf.Min(1.1f, capsule.height * 0.6f);
        if (!Physics.Raycast(chest, Vector3.up, 1.4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) return false;
        bool leaf = false;
        foreach (var direction in hideDirections)
        {
            if (!Physics.Raycast(chest, direction, out var hit, 0.95f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                return false;
            var door = hit.collider.GetComponentInParent<InteractiveDoor>();
            if (door != null && door.Noun != "дверь" && (door.State == InteractiveDoor.DoorState.Closed || door.IsLocked)) leaf = true;
        }
        return leaf;
    }

    private void OnDestroy() { if (playerContact != null) Destroy(playerContact); }

    // ---------------- vaulting ----------------

    // Finds an obstacle 0.4-1.35 m high right in front, and a path over it (thin: fence, sill) or onto
    // it (thick: table, wall stub) where a crouched body fits all the way. `start` begins the move.
    private bool PlanVault(bool start)
    {
        if (!MatchRules.Current.Vaulting) return false;
        Vector3 forward = Quaternion.Euler(0, yaw, 0) * Vector3.forward;
        Vector3 feet = body.position;
        float radius = standingRadius;
        if (!Physics.Raycast(feet + Vector3.up * 0.45f, forward, out var face, radius + 0.6f, ~0, QueryTriggerInteraction.Ignore) ||
            face.rigidbody == body || face.normal.y > 0.3f) return false;
        // Things people carry or that fly around are not climbing frames.
        if (face.rigidbody != null && !face.rigidbody.isKinematic && face.rigidbody.mass < 60f) return false;
        float reach = face.distance + 0.2f;
        Vector3 probe = feet + forward * reach + Vector3.up * 1.6f;
        if (!Physics.Raycast(probe, Vector3.down, out var top, 1.5f, ~0, QueryTriggerInteraction.Ignore) || top.normal.y < 0.7f)
            return false;
        float height = top.point.y - feet.y;
        if (height < 0.4f || height > 1.35f) return false;
        Vector3 raised = feet + Vector3.up * (height + 0.06f);
        // Rise in place, then cross the top.
        if (!HasCapsuleClearance(raised, crouchingHeight, radius)) return false;
        // Thickness: look back at the obstacle from beyond it.
        Vector3 beyond = feet + forward * (reach + 1.4f) + Vector3.up * (height - 0.08f);
        float thickness = 1.4f;
        if (Physics.Raycast(beyond, -forward, out var back, 1.4f, ~0, QueryTriggerInteraction.Ignore)) thickness = 1.4f - back.distance;
        int length;
        if (thickness < 0.7f)
        {
            Vector3 over = raised + forward * (face.distance + thickness + radius + 0.12f);
            if (!Physics.Raycast(over, Vector3.down, out var floor, height + 1.2f, ~0, QueryTriggerInteraction.Ignore) ||
                floor.normal.y < 0.6f) return false;
            Vector3 landing = new Vector3(over.x, floor.point.y + 0.03f, over.z);
            if (!HasTravelClearance(raised, over - raised, crouchingHeight, radius) ||
                !HasCapsuleClearance(landing, crouchingHeight, radius)) return false;
            vaultPath[0] = raised; vaultPath[1] = over; vaultPath[2] = landing; length = 3;
        }
        else
        {
            Vector3 onto = raised + forward * (face.distance + radius + 0.15f);
            if (!HasTravelClearance(raised, onto - raised, crouchingHeight, radius)) return false;
            vaultPath[0] = raised; vaultPath[1] = onto; length = 2;
        }
        if (!start) return true;
        vaultLength = length;
        vaultStep = 0;
        vaultTime = 0f;
        SetStance(Stance.Crouching);
        return true;
    }

    // Moves along the planned path at a steady speed; gives up if something blocks it.
    private void ContinueVault()
    {
        vaultTime += Time.fixedDeltaTime;
        Vector3 target = vaultPath[vaultStep];
        Vector3 offset = target - body.position;
        float speed = vaultStep == 0 ? 3.2f : 2.8f;
        if (offset.magnitude < 0.04f || vaultTime > 1.6f)
        {
            vaultStep++;
            if (vaultStep >= vaultLength || vaultTime > 1.6f)
            {
                vaultStep = -1;
                body.linearVelocity = Vector3.zero;
                crouchInput = false;
                return;
            }
            return;
        }
        body.linearVelocity = Vector3.ClampMagnitude(offset / Time.fixedDeltaTime, speed);
        body.AddForce(-Physics.gravity, ForceMode.Acceleration);
    }

    private bool HasTravelClearance(Vector3 feet, Vector3 displacement, float height, float radius)
    {
        float distance = displacement.magnitude;
        if (distance < 0.001f) return true;
        return !Physics.CapsuleCast(feet + Vector3.up * radius, feet + Vector3.up * (height - radius), radius - 0.02f,
            displacement / distance, out _, distance, ~0, QueryTriggerInteraction.Ignore);
    }

    // ---------------- camera ----------------

    private void LateUpdate()
    {
        stepCameraOffset *= Mathf.Exp(-22f * Time.deltaTime);
        if (!IsNetworkReplica)
        {
            // Lean smoothly, but never further than the wall beside the head allows.
            float target = leanInput;
            if (Mathf.Abs(target) > 0.01f && viewCamera != null)
            {
                Vector3 head = transform.position + Vector3.up * (CurrentHeight - 0.18f);
                Vector3 side = transform.right * Mathf.Sign(target);
                if (Physics.SphereCast(head, 0.14f, side, out var wall, leanDistance, ~0, QueryTriggerInteraction.Ignore) &&
                    wall.rigidbody != body)
                    target = Mathf.Sign(target) * Mathf.Clamp01((wall.distance - 0.04f) / leanDistance);
            }
            Lean = Mathf.MoveTowards(Lean, target, Time.deltaTime * 5f);
        }
        if (viewCamera == null) return;
        // A light head bob while walking on the ground.
        float horizontal = new Vector2(body.linearVelocity.x, body.linearVelocity.z).magnitude;
        if (IsControlled && IsGrounded && horizontal > 0.5f) bob += Time.deltaTime * horizontal * 2.2f;
        float bobOffset = IsControlled && IsGrounded ? Mathf.Sin(bob * Mathf.PI) * Mathf.Min(horizontal, 6f) * 0.006f : 0f;
        viewCamera.transform.localPosition = new Vector3(Lean * leanDistance, CurrentHeight - 0.18f - stepCameraOffset + bobOffset -
            Mathf.Abs(Lean) * 0.05f, 0f);
        if (IsControlled)
        {
            viewCamera.transform.localRotation = Quaternion.Euler(pitch, 0f, -Lean * leanRoll) * Concussion.ViewOffset;
            GameSettings.ApplyCamera(viewCamera, false);
        }
    }

    // ---------------- steps and windows ----------------

    private void TryStepUp(Vector3 movement)
    {
        const float clearance = 0.02f;
        Vector3 direction = movement.normalized;
        Vector3 feet = body.position;
        float reach = capsule.radius + movement.magnitude * Time.fixedDeltaTime;
        if (!Physics.Raycast(feet + Vector3.up * 0.06f, direction, out var riser, reach,
                ~0, QueryTriggerInteraction.Ignore) || riser.normal.y > 0.2f)
            return;

        Vector3 landing = riser.point + direction * 0.08f;
        landing.y = feet.y + stepHeight + clearance;
        if (!Physics.Raycast(landing, Vector3.down, out var top, stepHeight, ~0, QueryTriggerInteraction.Ignore) ||
            top.normal.y < 0.6f)
            return;

        float rise = top.point.y - feet.y;
        Vector3 raisedFeet = feet + Vector3.up * (rise + clearance);
        if (!HasCapsuleClearance(raisedFeet, capsule.height, capsule.radius)) return;

        float radius = capsule.radius;
        if (Physics.CapsuleCast(raisedFeet + Vector3.up * radius,
                raisedFeet + Vector3.up * (capsule.height - radius), radius - 0.01f,
                direction, out _, movement.magnitude * Time.fixedDeltaTime, ~0, QueryTriggerInteraction.Ignore))
            return;

        body.position = raisedFeet;
        stepCameraOffset += rise + clearance;
    }

    private bool TryTraverseWindow(Vector3 movement)
    {
        if (movement.sqrMagnitude < 0.001f) return false;
        Vector3 direction = movement.normalized;
        if (windowTravelDirection == Vector3.zero && !TryEnterWindow(direction)) return false;
        if (Vector3.Dot(direction, windowTravelDirection) < 0.25f) return false;

        float remaining = windowTravelEnd - Vector3.Dot(body.position, windowTravelDirection);
        if (remaining <= 0.025f)
        {
            jumped = false;
            return false;
        }

        const float passSpeed = 3.5f;
        float distance = Mathf.Min(passSpeed * Time.fixedDeltaTime, remaining);
        Vector3 advance = windowTravelDirection * distance;
        if (!HasTravelClearance(body.position, advance, capsule.height, capsule.radius)) return false;
        body.linearVelocity = advance / Time.fixedDeltaTime;
        body.AddForce(-Physics.gravity, ForceMode.Acceleration);
        return true;
    }

    private bool TryEnterWindow(Vector3 movement)
    {
        const float reach = 0.78f;
        const float maxHeightAdjustment = 0.20f;
        const float frameMargin = 0.055f;
        const float exitDistance = 0.64f;
        Vector3 feet = body.position;
        foreach (var window in BreakableWindow.BrokenWindows)
        {
            Transform pane = window.transform;
            Vector3 relative = feet - pane.position;
            float depth = Vector3.Dot(relative, pane.forward);
            if (Mathf.Abs(depth) > reach) continue;
            Vector3 direction = depth < 0f ? pane.forward : -pane.forward;
            if (Vector3.Dot(movement, direction) < 0.55f) continue;

            Vector3 size = pane.lossyScale;
            if (Mathf.Abs(Vector3.Dot(relative, pane.right)) + capsule.radius + frameMargin > size.x * 0.5f)
                continue;
            float minimumY = pane.position.y - size.y * 0.5f + frameMargin;
            float maximumY = pane.position.y + size.y * 0.5f - capsule.height - frameMargin;
            if (minimumY > maximumY) continue;
            Vector3 lift = Vector3.up * (Mathf.Clamp(feet.y, minimumY, maximumY) - feet.y);
            if (Mathf.Abs(lift.y) > maxHeightAdjustment) continue;
            Vector3 alignedFeet = feet + lift;
            Vector3 across = direction * (exitDistance + Mathf.Abs(depth));
            if (!HasCapsuleClearance(alignedFeet, capsule.height, capsule.radius) ||
                !HasTravelClearance(feet, lift, capsule.height, capsule.radius) ||
                !HasTravelClearance(alignedFeet, across, capsule.height, capsule.radius))
                continue;

            // The player supplies the jump and stance. Keep their feet in the opening
            // while moving across its narrow vertical clearance.
            body.position = alignedFeet;
            stepCameraOffset += lift.y;
            windowTravelDirection = direction;
            windowTravelEnd = Vector3.Dot(pane.position, direction) + exitDistance;
            return true;
        }
        return false;
    }

    private bool HasCapsuleClearance(Vector3 feet, float height, float radius)
    {
        int count = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * radius,
            feet + Vector3.up * (height - radius), radius - 0.01f,
            headroomHits, ~0, QueryTriggerInteraction.Ignore);
        if (count == headroomHits.Length) return false;
        for (int i = 0; i < count; i++)
            if (headroomHits[i].attachedRigidbody != body && !headroomHits[i].transform.IsChildOf(transform)) return false;
        return true;
    }
}
