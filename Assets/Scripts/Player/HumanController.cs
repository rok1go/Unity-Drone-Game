using UnityEngine;
using UnityEngine.InputSystem;

// A dynamic capsule is used instead of CharacterController so platforms can lift it
// through normal PhysX contacts. Input never owns gravity or the collider lifecycle.
[RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
public class HumanController : MonoBehaviour
{
    public Camera viewCamera;
    [Min(0)] public float walkSpeed = 3.5f;
    [Min(0)] public float sprintSpeed = 6f;
    [Min(0)] public float crouchSpeed = 1.8f;
    [Min(0)] public float jumpHeight = 2f;
    [Min(0.1f)] public float gravity = 20f;
    [Min(0.01f)] public float mouseSensitivity = 0.12f;
    [Min(1)] public float standingHeight = 1.8f;
    [Min(0.6f)] public float crouchingHeight = 1.1f;
    [Header("Physical movement")]
    [Min(0.1f)] public float movementAcceleration = 25f;
    [Range(0, 1)] public float airControl = 0.25f;
    [Range(0f, 0.4f)] public float stepHeight = 0.35f;
    [Header("Health")]
    [Min(1f)] public float maxHealth = 100f;
    public float Health { get; private set; }
    public float LastDamage { get; private set; }
    public float LastDamageTime { get; private set; } = float.NegativeInfinity;
    public bool IsControlled { get; private set; }
    public bool IsNetworkReplica { get; set; }
    public bool IsCrouching { get; private set; }
    public bool IsGrounded { get; private set; }
    public bool IsEliminated { get; private set; }
    public event System.Action<HumanController> Eliminated;
    public float CurrentHeight => capsule != null ? capsule.height : standingHeight;
    private Rigidbody body;
    private CapsuleCollider capsule;
    private Vector2 moveInput;
    private bool sprintInput, crouchInput, jumpRequested, jumped;
    private Vector3 windowTravelDirection;
    private float windowTravelEnd;
    private float pitch, yaw, stepCameraOffset;
    private Vector3 spawnPosition;
    private Quaternion spawnRotation;
    private readonly Collider[] headroomHits = new Collider[16];
    private readonly RaycastHit[] groundHits = new RaycastHit[16];

    private void Awake()
    {
        body = GetComponent<Rigidbody>(); capsule = GetComponent<CapsuleCollider>();
        body.isKinematic = false; body.useGravity = true;
        body.constraints = RigidbodyConstraints.FreezeRotation;
        yaw = body.rotation.eulerAngles.y;
        spawnPosition = body.position;
        spawnRotation = body.rotation;
        RestoreHealth();
    }

    public void SetControlled(bool controlled)
    {
        IsControlled = controlled && !IsEliminated;
        moveInput = Vector2.zero; sprintInput = false; jumpRequested = false;
        jumped = false;
        windowTravelDirection = Vector3.zero;
        // Keep the current stance when parked, so switching under a low ceiling is safe.
        crouchInput = IsCrouching;
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
        if (IsEliminated) return;
        health = Mathf.Clamp(health, 0f, maxHealth);
        if (health < Health) { LastDamage = Health - health; LastDamageTime = Time.unscaledTime; }
        Health = health;
        if (Health <= 0f) Eliminate();
    }

    public void ApplyRemoteStance(bool crouching)
    {
        if (!IsNetworkReplica) return;
        IsCrouching = crouching;
        capsule.height = crouching ? crouchingHeight : standingHeight;
        capsule.center = Vector3.up * capsule.height / 2f;
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

    // Local development reset; the round system will own respawns later.
    public void ResetHuman()
    {
        IsEliminated = false;
        RestoreHealth();
        body.linearVelocity = Vector3.zero;
        body.angularVelocity = Vector3.zero;
        body.position = spawnPosition;
        body.rotation = spawnRotation;
        transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        yaw = spawnRotation.eulerAngles.y;
        pitch = 0;
        stepCameraOffset = 0f;
        IsCrouching = false;
        IsGrounded = false;
        capsule.height = standingHeight;
        capsule.center = Vector3.up * standingHeight / 2;
        if (viewCamera != null) viewCamera.transform.localRotation = Quaternion.identity;
        SetControlled(false);
        gameObject.SetActive(true);
        Physics.SyncTransforms();
    }

    private void Update()
    {
        moveInput = Vector2.zero; sprintInput = false;
        if (!IsControlled || Cursor.lockState != CursorLockMode.Locked) return;
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        moveInput = Vector2.ClampMagnitude(new Vector2((keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0),
            (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0)), 1);
        sprintInput = keyboard.leftShiftKey.isPressed || keyboard.rightShiftKey.isPressed;
        crouchInput = keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed;
        jumpRequested |= keyboard.spaceKey.wasPressedThisFrame;
        Vector2 look = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        yaw += look.x * mouseSensitivity;
        pitch = Mathf.Clamp(pitch - look.y * mouseSensitivity, -85, 85);
        if (viewCamera != null)
        {
            viewCamera.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
            if (keyboard.eKey.wasPressedThisFrame)
                BreakableWindow.TryInteract(new Ray(viewCamera.transform.position, viewCamera.transform.forward));
        }
    }

    private void FixedUpdate()
    {
        if (IsNetworkReplica) return;
        bool crouch = crouchInput || (IsCrouching && !CanStand());
        IsCrouching = crouch;
        capsule.height = crouch ? crouchingHeight : standingHeight;
        capsule.center = Vector3.up * capsule.height / 2;
        Quaternion heading = Quaternion.Euler(0, yaw, 0);
        body.MoveRotation(heading);
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
            nearest = hit.distance; support = hit.rigidbody; supportPoint = hit.point; IsGrounded = true;
        }
        Vector3 supportVelocity = support != null ? support.GetPointVelocity(supportPoint) : Vector3.zero;
        float speed = crouch ? crouchSpeed : sprintInput ? sprintSpeed : walkSpeed;
        Vector3 desired = heading * new Vector3(moveInput.x, 0, moveInput.y) * speed;
        if (IsGrounded && body.linearVelocity.y <= 0f && windowTravelDirection == Vector3.zero)
            jumped = false;
        if (IsControlled && crouchInput && jumped && TryTraverseWindow(desired))
        {
            jumpRequested = false;
            return;
        }
        windowTravelDirection = Vector3.zero;
        if (IsControlled && IsGrounded && !jumpRequested && stepHeight > 0f && desired.sqrMagnitude > 0.001f)
            TryStepUp(desired);
        // Relative ground movement follows a dynamic platform without braking it to world zero.
        Vector3 delta = desired + supportVelocity - body.linearVelocity; delta.y = 0;
        if (IsGrounded || (IsControlled && moveInput.sqrMagnitude > 0))
            body.AddForce(Vector3.ClampMagnitude(delta / Time.fixedDeltaTime,
                movementAcceleration * (IsGrounded ? 1 : airControl)), ForceMode.Acceleration);
        if (jumpRequested && IsGrounded && !crouch)
        {
            jumped = true;
            float relativeY = body.linearVelocity.y - supportVelocity.y;
            body.AddForce(Vector3.up * (Mathf.Sqrt(2 * gravity * jumpHeight) - relativeY), ForceMode.VelocityChange);
        }
        jumpRequested = false;
        // Preserve the existing configurable gravity; native gravity stays enabled for both roles.
        body.AddForce(Vector3.down * (gravity + Physics.gravity.y), ForceMode.Acceleration);
    }

    private void LateUpdate()
    {
        stepCameraOffset *= Mathf.Exp(-22f * Time.deltaTime);
        if (viewCamera != null)
            viewCamera.transform.localPosition = Vector3.up * (CurrentHeight - 0.18f - stepCameraOffset);
    }

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
        if (!HasCapsuleClearance(raisedFeet, capsule.height)) return;

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
        if (!HasWindowTravelClearance(body.position, advance)) return false;
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
            if (!HasCapsuleClearance(alignedFeet, capsule.height) ||
                !HasWindowTravelClearance(feet, lift) || !HasWindowTravelClearance(alignedFeet, across))
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

    private bool HasWindowTravelClearance(Vector3 feet, Vector3 displacement)
    {
        float distance = displacement.magnitude;
        if (distance < 0.001f) return true;
        float radius = capsule.radius;
        return !Physics.CapsuleCast(feet + Vector3.up * radius,
            feet + Vector3.up * (capsule.height - radius), radius - 0.01f,
            displacement / distance, out _, distance, ~0, QueryTriggerInteraction.Ignore);
    }

    private bool CanStand() => HasCapsuleClearance(body.position, standingHeight);

    private bool HasCapsuleClearance(Vector3 feet, float height)
    {
        float radius = capsule.radius;
        int count = Physics.OverlapCapsuleNonAlloc(feet + Vector3.up * radius,
            feet + Vector3.up * (height - radius), radius - 0.01f,
            headroomHits, ~0, QueryTriggerInteraction.Ignore);
        if (count == headroomHits.Length) return false;
        for (int i = 0; i < count; i++)
            if (headroomHits[i].attachedRigidbody != body && !headroomHits[i].transform.IsChildOf(transform)) return false;
        return true;
    }
}
