using System;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
public class DroneController : MonoBehaviour
{
    public Camera viewCamera;
    public enum FlightMode { Angle, Acro }
    [Header("FPV flight")]
    public FlightMode flightMode = FlightMode.Angle;
    [Range(15, 70)] public float maximumTilt = 31.5f;
    [Tooltip("Time constant for smoothing pitch/roll input in seconds.")]
    [Range(0.05f, 0.5f)] public float tiltInputSmoothing = 0.165f;
    [Range(1.2f, 5f)] public float thrustToWeight = 2.4f;
    [Range(90, 720)] public float acroRate = 300f;
    [Range(45, 360)] public float yawRate = 150f;
    [Range(2, 12)] public float levelingResponse = 3.5f;
    [Range(4, 30)] public float rateResponse = 14f;
    [Header("Keyboard altitude assist (ANGLE)")]
    [Min(0.5f)] public float keyboardClimbSpeed = 4f;
    [Min(0.5f)] public float keyboardDescentSpeed = 3f;
    [Range(0.5f, 5f)] public float altitudeHoldResponse = 2f;
    [Range(1f, 10f)] public float verticalResponse = 5f;
    [Range(1f, 8f)] public float throttleResponse = 3f;
    [Range(0.005f, 0.1f)] public float airDrag = 0.025f;
    [Range(0, 0.8f)] public float stickExpo = 0.35f;
    [Header("ANGLE hover assist")]
    [Tooltip("Horizontal velocity damping when the movement stick/buttons are released (per second).")]
    [Range(0f, 10f)] public float hoverBraking = 3.5f;
    [Tooltip("Maximum assisted braking acceleration in metres per second squared.")]
    [Min(0f)] public float maxHoverBrakeAcceleration = 9f;
    [Tooltip("Time to release the bank command when movement input returns to centre.")]
    [Range(0.02f, 0.5f)] public float tiltReleaseSmoothing = 0.06f;
    [Header("FPV camera")]
    [Range(0.02f, 0.5f)] public float cameraMouseSensitivity = 0.12f;
    [Range(0, 40)] public float cameraUptilt = 15f;
    [Range(70, 120)] public float cameraFieldOfView = 100f;
    [Header("Self destruct")]
    [Min(0.1f)] public float explosionRadius = 7f;
    [Min(0)] public float explosionImpulse = 21f;
    [Tooltip("Damage to an exposed human at the blast centre; decreases to zero at the radius edge.")]
    [Min(0f)] public float maximumHumanDamage = 150f;
    [Tooltip("Minimum total relative collision speed to detonate, in metres per second (35 km/h by default).")]
    [Min(1f)] public float impactDetonationSpeed = 35f / 3.6f;
    [Tooltip("Also require this much speed into the surface, in metres per second, so light glancing contacts do not detonate.")]
    [Min(0f)] public float minimumImpactNormalSpeed = 4f;
    public Material explosionMaterial;
    [Header("Destruction budget")]
    [Range(1, 128)] public int maxFragmentsPerExplosion = 80;
    [Range(1, 256)] public int maxActiveDebris = 128;
    [Tooltip("Optional timed removal. Leave disabled to keep rubble on the map.")]
    public bool enableDebrisCleanup = false;
    [Min(0.25f)] public float debrisLifetime = 12f;
    public PhysicsMaterial debrisContactMaterial;
    [Min(0.5f)] public float impactFractureSpeed = 6f;
    [Header("Explosion visuals")]
    [Range(0.3f, 4f)] public float explosionEffectDuration = 1.4f;
    [Range(16, 512)] public int explosionParticleCount = 160;
    public bool IsControlled { get; private set; }
    public bool IsNetworkReplica { get; set; }
    private bool authoritativeDetonation;
    public bool IsFlightLocked { get; private set; }
    public bool IsArmed { get; private set; }
    public bool IsDestroyed { get; private set; }
    public bool MotorsArmed { get; private set; }
    public float Throttle { get; private set; }
    public float HoverThrottle => 1f / Mathf.Max(1.2f, thrustToWeight);
    public float FlightSeconds { get; private set; }
    public float DetonationRemaining => 0f;
    public Vector3 HomePosition => spawnPosition;
    public Vector3 StickInput => new Vector3(pitchInput, yawInput, rollInput);
    public Vector3 Velocity => body != null && !body.isKinematic ? body.linearVelocity : NetworkVelocity;
    // Interpolated velocity of a network replica, so a spectator's OSD shows real speed.
    public Vector3 NetworkVelocity { get; set; }
    public event Action Exploded;
    private Rigidbody body;
    private float pitchInput, rollInput, yawInput;
    private float verticalInput, altitudeTarget;
    private float cameraPitch, mouseYawRate;
    private Vector2 smoothedTiltInput;
    private bool changingAltitude;
    private bool gamepadInputActive;
    private Vector3 spawnPosition;
    private Quaternion spawnRotation;

    private void Awake()
    {
        DroneExplosionEffect.PreloadAudio();
        if (GetComponent<DroneMotorAudio>() == null) gameObject.AddComponent<DroneMotorAudio>();
        body = GetComponent<Rigidbody>();
        spawnPosition = body.position;
        spawnRotation = body.rotation;
        body.isKinematic = false;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.useGravity = true;
        body.constraints = RigidbodyConstraints.None;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.linearDamping = 0.02f;
        body.angularDamping = 0.05f;
        body.maxAngularVelocity = 16f;
        body.solverIterations = 8;
        body.solverVelocityIterations = 3;
        if (viewCamera != null)
        {
            CenterCamera();
            viewCamera.fieldOfView = cameraFieldOfView;
        }
        SetControlled(false);
    }

    public void SetControlled(bool controlled)
    {
        if (body == null) body = GetComponent<Rigidbody>();
        IsControlled = controlled && !IsDestroyed && !IsFlightLocked;
        pitchInput = rollInput = yawInput = verticalInput = 0;
        smoothedTiltInput = Vector2.zero;
        mouseYawRate = 0;
        gamepadInputActive = false;
        SetMotorsArmed(IsControlled);
        if (viewCamera != null)
        {
            viewCamera.enabled = IsControlled;
            var listener = viewCamera.GetComponent<AudioListener>();
            if (listener != null) listener.enabled = IsControlled;
        }
    }

    private void Update()
    {
        if (IsNetworkReplica) return;
        pitchInput = rollInput = yawInput = verticalInput = 0;
        mouseYawRate = 0;
        if (MotorsArmed && !IsDestroyed) FlightSeconds += Time.deltaTime;
        if (!IsControlled || Cursor.lockState != CursorLockMode.Locked)
        {
            if (gamepadInputActive) altitudeTarget = body.position.y;
            gamepadInputActive = false;
            return;
        }
        var keyboard = Keyboard.current;
        Vector2 look = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        // Horizontal mouse turns the drone; vertical mouse only looks up/down.
        mouseYawRate = Mathf.Clamp(look.x * cameraMouseSensitivity / Mathf.Max(0.001f, Time.deltaTime), -yawRate, yawRate);
        cameraPitch = Mathf.Clamp(cameraPitch - look.y * cameraMouseSensitivity, -85f, 85f);
        if (viewCamera != null)
            viewCamera.transform.localRotation = Quaternion.Euler(cameraPitch, 0, 0);
        bool keyboardFlight = false;
        if (keyboard != null)
        {
            pitchInput = (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0);
            rollInput = (keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0);
            verticalInput = (keyboard.spaceKey.isPressed ? 1 : 0) -
                (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed ? 1 : 0);
            keyboardFlight = verticalInput != 0 || pitchInput != 0 || rollInput != 0;
            if (keyboardFlight) gamepadInputActive = false;
            if (keyboard.cKey.wasPressedThisFrame) CenterCamera();
            if (keyboard.vKey.wasPressedThisFrame) ToggleFlightMode();
            if (keyboard.bKey.wasPressedThisFrame) SetMotorsArmed(!MotorsArmed);
            if (keyboard.fKey.wasPressedThisFrame) BeginSelfDestruct();
        }
        if (IsDestroyed) return;
        var pad = Gamepad.current;
        if (pad != null)
        {
            Vector2 right = pad.rightStick.ReadValue(), left = pad.leftStick.ReadValue();
            if (!keyboardFlight && right.sqrMagnitude + left.sqrMagnitude > 0.01f) gamepadInputActive = true;
            if (gamepadInputActive)
            {
                pitchInput = right.y; rollInput = right.x; yawInput = left.x;
                if (MotorsArmed) Throttle = Mathf.Clamp01((left.y + 1f) * 0.5f);
            }
            if (pad.buttonSouth.wasPressedThisFrame) SetMotorsArmed(!MotorsArmed);
            if (pad.buttonWest.wasPressedThisFrame) ToggleFlightMode();
            if (pad.buttonNorth.wasPressedThisFrame) BeginSelfDestruct();
        }
        if (IsDestroyed) return;
        else gamepadInputActive = false;
        // Circular stick range: diagonal input has the same strength as one axis.
        Vector2 tilt = Vector2.ClampMagnitude(new Vector2(rollInput, pitchInput), 1f);
        float magnitude = tilt.magnitude;
        if (magnitude > 0.0001f) tilt *= Expo(magnitude) / magnitude;
        rollInput = tilt.x; pitchInput = tilt.y; yawInput = Expo(yawInput);
    }

    public void SetFlightLocked(bool locked)
    {
        if (body == null) body = GetComponent<Rigidbody>();
        if (locked)
        {
            SetControlled(false);
            if (!body.isKinematic)
            {
                body.linearVelocity = Vector3.zero;
                body.angularVelocity = Vector3.zero;
            }
            // ContinuousDynamic is not supported on a parked kinematic body.
            body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
            body.isKinematic = true;
        }
        else
        {
            body.isKinematic = IsNetworkReplica;
            body.collisionDetectionMode = IsNetworkReplica ? CollisionDetectionMode.ContinuousSpeculative : CollisionDetectionMode.ContinuousDynamic;
        }
        IsFlightLocked = locked;
    }

    private float Expo(float value)
    {
        value = Mathf.Clamp(value, -1, 1);
        return Mathf.Lerp(value, value * value * value, stickExpo);
    }

    public void CenterCamera()
    {
        cameraPitch = -cameraUptilt;
        if (viewCamera != null)
            viewCamera.transform.localRotation = Quaternion.Euler(cameraPitch, 0, 0);
    }

    public void ToggleFlightMode()
    {
        flightMode = flightMode == FlightMode.Angle ? FlightMode.Acro : FlightMode.Angle;
        altitudeTarget = body.position.y;
        changingAltitude = false;
    }

    public void SetMotorsArmed(bool armed)
    {
        MotorsArmed = armed && IsControlled && !IsDestroyed;
        Throttle = MotorsArmed ? HoverThrottle : 0;
        altitudeTarget = body != null ? body.position.y : transform.position.y;
        changingAltitude = false;
        if (!MotorsArmed) smoothedTiltInput = Vector2.zero;
    }

    private void FixedUpdate()
    {
        if (IsDestroyed || IsFlightLocked || IsNetworkReplica) return;
        // Passive air resistance also applies with the motors off.
        body.AddForce(-body.linearVelocity * body.linearVelocity.magnitude * airDrag, ForceMode.Acceleration);
        if (!MotorsArmed) return;
        Vector2 requestedTilt = Vector2.ClampMagnitude(new Vector2(rollInput, pitchInput), 1f);
        bool releasingTilt = flightMode == FlightMode.Angle && requestedTilt.sqrMagnitude < 0.0001f;
        float smoothing = releasingTilt ? Mathf.Min(tiltInputSmoothing, tiltReleaseSmoothing) : tiltInputSmoothing;
        float blend = 1f - Mathf.Exp(-Time.fixedDeltaTime / Mathf.Max(0.01f, smoothing));
        smoothedTiltInput = Vector2.Lerp(smoothedTiltInput, requestedTilt, blend);
        if (releasingTilt)
        {
            // Brake only at neutral input so small stick movements stay responsive.
            Vector3 horizontalVelocity = Vector3.ProjectOnPlane(body.linearVelocity, Vector3.up);
            float damping = (1f - Mathf.Exp(-Mathf.Max(0f, hoverBraking) * Time.fixedDeltaTime)) / Time.fixedDeltaTime;
            Vector3 braking = Vector3.ClampMagnitude(-horizontalVelocity * damping,
                Mathf.Max(0f, maxHoverBrakeAcceleration));
            body.AddForce(braking, ForceMode.Acceleration);
        }
        if (!gamepadInputActive)
        {
            float targetThrottle;
            if (flightMode == FlightMode.Angle)
            {
                float verticalSpeed;
                if (Mathf.Abs(verticalInput) > 0.01f)
                {
                    altitudeTarget = body.position.y;
                    changingAltitude = true;
                    verticalSpeed = verticalInput > 0 ? keyboardClimbSpeed : -keyboardDescentSpeed;
                }
                else
                {
                    if (changingAltitude) altitudeTarget = body.position.y;
                    changingAltitude = false;
                    verticalSpeed = Mathf.Clamp((altitudeTarget - body.position.y) * altitudeHoldResponse,
                        -keyboardDescentSpeed, keyboardClimbSpeed);
                }
                // Adjust real rotor thrust to hold height, including lift lost when banking.
                float gravity = Physics.gravity.magnitude;
                float lift = gravity + (verticalSpeed - body.linearVelocity.y) * verticalResponse;
                float upright = Mathf.Max(0.2f, Vector3.Dot(transform.up, Vector3.up));
                targetThrottle = Mathf.Clamp01(lift / Mathf.Max(0.01f, gravity * thrustToWeight * upright));
            }
            else
            {
                // Spring-centred keyboard gas in ACRO; no accumulated throttle value.
                targetThrottle = verticalInput > 0 ? 1f : verticalInput < 0 ? 0f : HoverThrottle;
                altitudeTarget = body.position.y;
            }
            Throttle = Mathf.MoveTowards(Throttle, targetThrottle, throttleResponse * Time.fixedDeltaTime);
        }
        else altitudeTarget = body.position.y;
        body.AddForce(transform.up * (Physics.gravity.magnitude * thrustToWeight * Throttle), ForceMode.Acceleration);
        Vector3 targetRates;
        float commandedYaw = Mathf.Clamp(yawInput * yawRate + mouseYawRate, -yawRate, yawRate) * Mathf.Deg2Rad;
        if (flightMode == FlightMode.Angle)
        {
            // Limit the combined bank angle, rather than applying two Euler tilts.
            float strength = smoothedTiltInput.magnitude;
            float angle = strength * maximumTilt * Mathf.Deg2Rad;
            float horizontal = strength > 0.0001f ? Mathf.Sin(angle) / strength : 0f;
            Vector3 localUp = new Vector3(smoothedTiltInput.x * horizontal, Mathf.Cos(angle),
                smoothedTiltInput.y * horizontal);
            Vector3 targetUp = Quaternion.Euler(0, transform.eulerAngles.y, 0) * localUp;
            Quaternion.FromToRotation(transform.up, targetUp).ToAngleAxis(out float errorAngle, out Vector3 axis);
            Vector3 correction = errorAngle > 0.01f
                ? transform.InverseTransformDirection(axis) * (Mathf.DeltaAngle(0, errorAngle) * Mathf.Deg2Rad * levelingResponse)
                : Vector3.zero;
            targetRates = new Vector3(correction.x, commandedYaw, correction.z);
        }
        else targetRates = new Vector3(smoothedTiltInput.y * acroRate * Mathf.Deg2Rad,
            commandedYaw, -smoothedTiltInput.x * acroRate * Mathf.Deg2Rad);
        Vector3 localRates = transform.InverseTransformDirection(body.angularVelocity);
        body.AddRelativeTorque(Vector3.ClampMagnitude((targetRates - localRates) * rateResponse, 80f), ForceMode.Acceleration);
    }

    public void BeginSelfDestruct()
    {
        if (!IsControlled || IsArmed || IsDestroyed) return;
        Detonate();
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (IsDestroyed || IsFlightLocked || IsNetworkReplica) return;
        // Require both fast travel and a meaningful hit into the surface, rather than a scrape.
        if (collision.relativeVelocity.sqrMagnitude < impactDetonationSpeed * impactDetonationSpeed) return;
        for (int i = 0; i < collision.contactCount; i++)
        {
            float normalSpeed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, collision.GetContact(i).normal));
            if (normalSpeed < minimumImpactNormalSpeed) continue;
            Detonate();
            return;
        }
    }

    private void Detonate()
    {
        if (IsDestroyed) return;
        if (!authoritativeDetonation && MultiplayerPrototype.Instance != null &&
            MultiplayerPrototype.Instance.InterceptDetonation(this)) return;
        // Latch before applying damage: one collision can report several contacts.
        IsDestroyed = true;
        IsArmed = false;
        Vector3 position = body.position;
        DestructionSystem.ApplyExplosion(new ExplosionDamage
        {
            position = position, radius = explosionRadius, impulse = explosionImpulse,
            maxFragments = maxFragmentsPerExplosion, maxActiveDebris = maxActiveDebris, debrisLifetime = debrisLifetime,
            enableDebrisCleanup = enableDebrisCleanup, contactMaterial = debrisContactMaterial,
            impactFractureSpeed = impactFractureSpeed, humanDamage = maximumHumanDamage
        }, body);
        DroneExplosionEffect.Play(position, explosionMaterial, explosionRadius, explosionEffectDuration,
            explosionParticleCount, explosionImpulse / 21f);
        SetControlled(false);
        gameObject.SetActive(false);
        Exploded?.Invoke();
        if (MultiplayerPrototype.Instance != null) MultiplayerPrototype.Instance.DroneExploded(this);
    }

    public void DetonateAuthoritatively()
    {
        authoritativeDetonation = true;
        try { Detonate(); }
        finally { authoritativeDetonation = false; }
    }

    public void ApplyNetworkDestroyed(bool effect)
    {
        if (IsDestroyed) return;
        IsDestroyed = true; IsArmed = false;
        if (effect) DroneExplosionEffect.Play(transform.position, explosionMaterial, explosionRadius,
            explosionEffectDuration, explosionParticleCount, explosionImpulse / 21f);
        SetControlled(false); gameObject.SetActive(false); Exploded?.Invoke();
    }

    public void ApplyRemoteTelemetry(float throttle, float seconds, bool motors, int mode)
    {
        if (!IsNetworkReplica) return;
        Throttle = Mathf.Clamp01(throttle); FlightSeconds = Mathf.Max(0f, seconds);
        MotorsArmed = motors; flightMode = (FlightMode)Mathf.Clamp(mode, 0, 1);
    }

    public void SetTrainingSpawn(Vector3 position, Quaternion rotation)
    {
        spawnPosition = position;
        spawnRotation = rotation;
        ResetDrone();
    }

    public void ResetDrone()
    {
        StopAllCoroutines();
        if (body == null) body = GetComponent<Rigidbody>();
        IsDestroyed = false; IsArmed = false;
        FlightSeconds = 0;
        SetControlled(false);
        bool parked = body.isKinematic;
        if (parked) body.isKinematic = false;
        body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
        body.position = spawnPosition; body.rotation = spawnRotation;
        transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        body.isKinematic = parked;
        CenterCamera();
        gameObject.SetActive(true);
        Physics.SyncTransforms();
    }
}

