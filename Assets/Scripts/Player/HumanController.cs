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
    [Min(0)] public float jumpHeight = 0.8f;
    [Min(0.1f)] public float gravity = 20f;
    [Min(0.01f)] public float mouseSensitivity = 0.12f;
    [Min(1)] public float standingHeight = 1.8f;
    [Min(0.6f)] public float crouchingHeight = 1.1f;
    [Header("Physical movement")]
    [Min(0.1f)] public float movementAcceleration = 25f;
    [Range(0, 1)] public float airControl = 0.25f;
    public bool IsControlled { get; private set; }
    public bool IsCrouching { get; private set; }
    public bool IsGrounded { get; private set; }
    public float CurrentHeight => capsule != null ? capsule.height : standingHeight;
    private Rigidbody body;
    private CapsuleCollider capsule;
    private Vector2 moveInput;
    private bool sprintInput, crouchInput, jumpRequested;
    private float pitch, yaw;
    private readonly Collider[] headroomHits = new Collider[16];
    private readonly RaycastHit[] groundHits = new RaycastHit[16];

    private void Awake()
    {
        body = GetComponent<Rigidbody>(); capsule = GetComponent<CapsuleCollider>();
        body.isKinematic = false; body.useGravity = true;
        body.constraints = RigidbodyConstraints.FreezeRotation;
        yaw = body.rotation.eulerAngles.y;
    }

    public void SetControlled(bool controlled)
    {
        IsControlled = controlled;
        moveInput = Vector2.zero; sprintInput = false; jumpRequested = false;
        // Keep the current stance when parked, so switching under a low ceiling is safe.
        crouchInput = IsCrouching;
        if (viewCamera == null) return;
        viewCamera.enabled = controlled;
        var listener = viewCamera.GetComponent<AudioListener>();
        if (listener != null) listener.enabled = controlled;
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
        if (viewCamera != null) viewCamera.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
    }

    private void FixedUpdate()
    {
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
        // Relative ground movement follows a dynamic platform without braking it to world zero.
        Vector3 delta = desired + supportVelocity - body.linearVelocity; delta.y = 0;
        if (IsGrounded || (IsControlled && moveInput.sqrMagnitude > 0))
            body.AddForce(Vector3.ClampMagnitude(delta / Time.fixedDeltaTime,
                movementAcceleration * (IsGrounded ? 1 : airControl)), ForceMode.Acceleration);
        if (jumpRequested && IsGrounded && !crouch)
        {
            float relativeY = body.linearVelocity.y - supportVelocity.y;
            body.AddForce(Vector3.up * (Mathf.Sqrt(2 * gravity * jumpHeight) - relativeY), ForceMode.VelocityChange);
        }
        jumpRequested = false;
        // Preserve the existing configurable gravity; native gravity stays enabled for both roles.
        body.AddForce(Vector3.down * (gravity + Physics.gravity.y), ForceMode.Acceleration);
    }

    private void LateUpdate()
    {
        if (viewCamera != null) viewCamera.transform.localPosition = Vector3.up * (CurrentHeight - 0.18f);
    }

    private bool CanStand()
    {
        float radius = capsule.radius;
        int count = Physics.OverlapCapsuleNonAlloc(body.position + Vector3.up * radius,
            body.position + Vector3.up * (standingHeight - radius), radius - 0.01f,
            headroomHits, ~0, QueryTriggerInteraction.Ignore);
        if (count == headroomHits.Length) return false;
        for (int i = 0; i < count; i++)
            if (headroomHits[i].attachedRigidbody != body && !headroomHits[i].transform.IsChildOf(transform)) return false;
        return true;
    }
}
