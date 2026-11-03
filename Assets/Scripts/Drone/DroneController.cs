using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

[RequireComponent(typeof(Rigidbody), typeof(BoxCollider))]
public class DroneController : MonoBehaviour
{
    public Camera viewCamera;
    [Header("Flight")]
    [Min(0)] public float moveSpeed = 7f;
    [Min(0)] public float verticalSpeed = 4f;
    [Min(0.1f)] public float acceleration = 12f;
    [Min(0.1f)] public float braking = 8f;
    [Min(0.01f)] public float mouseSensitivity = 0.12f;
    [Header("Self destruct")]
    [Min(0.1f)] public float explosionRadius = 7f;
    [Min(0)] public float explosionImpulse = 18f;
    [Min(0)] public float selfDestructDelay = 1f;
    public Material explosionMaterial;
    [Header("Destruction budget")]
    [Range(1, 128)] public int maxFragmentsPerExplosion = 80;
    [Range(1, 256)] public int maxActiveDebris = 128;
    public bool enableDebrisCleanup = true;
    [Min(0.25f)] public float debrisLifetime = 12f;
    public PhysicsMaterial debrisContactMaterial;
    [Header("Explosion visuals")]
    [Range(0.3f, 4f)] public float explosionEffectDuration = 1.4f;
    [Range(16, 512)] public int explosionParticleCount = 160;
    public bool IsControlled { get; private set; }
    public bool IsArmed { get; private set; }
    public bool IsDestroyed { get; private set; }
    public Vector3 Velocity => body != null && !body.isKinematic ? body.linearVelocity : Vector3.zero;
    public event Action Exploded;
    private Rigidbody body;
    private Vector2 moveInput;
    private float liftInput, yaw, pitch;
    private Vector3 spawnPosition;
    private Quaternion spawnRotation;

    private void Awake()
    {
        body = GetComponent<Rigidbody>();
        spawnPosition = body.position;
        spawnRotation = body.rotation;
        yaw = spawnRotation.eulerAngles.y;
        // Arcade hover: no gravity or simulated rotors. Collisions are still physical.
        body.isKinematic = false;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
        body.useGravity = false;
        body.constraints = RigidbodyConstraints.FreezeRotation;
        SetControlled(false);
    }

    public void SetControlled(bool controlled)
    {
        if (body == null) body = GetComponent<Rigidbody>();
        IsControlled = controlled && !IsDestroyed;
        moveInput = Vector2.zero; liftInput = 0;
        // Role switching changes input and camera only; the dynamic body keeps its momentum.
        if (viewCamera != null)
        {
            viewCamera.enabled = IsControlled;
            var listener = viewCamera.GetComponent<AudioListener>();
            if (listener != null) listener.enabled = IsControlled;
        }
    }

    private void Update()
    {
        moveInput = Vector2.zero; liftInput = 0;
        if (!IsControlled || Cursor.lockState != CursorLockMode.Locked) return;
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        moveInput = Vector2.ClampMagnitude(new Vector2((keyboard.dKey.isPressed ? 1 : 0) - (keyboard.aKey.isPressed ? 1 : 0),
            (keyboard.wKey.isPressed ? 1 : 0) - (keyboard.sKey.isPressed ? 1 : 0)), 1);
        liftInput = (keyboard.spaceKey.isPressed ? 1 : 0) -
            (keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed ? 1 : 0);
        Vector2 look = Mouse.current != null ? Mouse.current.delta.ReadValue() : Vector2.zero;
        yaw += look.x * mouseSensitivity;
        pitch = Mathf.Clamp(pitch - look.y * mouseSensitivity, -85, 85);
        if (viewCamera != null) viewCamera.transform.localRotation = Quaternion.Euler(pitch, 0, 0);
        if (keyboard.fKey.wasPressedThisFrame) BeginSelfDestruct();
    }

    private void FixedUpdate()
    {
        if (IsDestroyed) return;
        Quaternion heading = Quaternion.Euler(0, yaw, 0);
        body.MoveRotation(heading);
        Vector3 target = heading * new Vector3(moveInput.x, 0, moveInput.y) * moveSpeed + Vector3.up * liftInput * verticalSpeed;
        float rate = target.sqrMagnitude > 0 ? acceleration : braking;
        Vector3 force = Vector3.ClampMagnitude((target - body.linearVelocity) / Time.fixedDeltaTime, rate);
        body.AddForce(force, ForceMode.Acceleration);
    }

    public void BeginSelfDestruct()
    {
        if (!IsControlled || IsArmed || IsDestroyed) return;
        IsArmed = true;
        StartCoroutine(DetonateAfterDelay());
    }

    private IEnumerator DetonateAfterDelay()
    {
        yield return new WaitForSeconds(selfDestructDelay);
        Vector3 position = body.position;
        DestructionSystem.ApplyExplosion(new ExplosionDamage
        {
            position = position, radius = explosionRadius, impulse = explosionImpulse,
            maxFragments = maxFragmentsPerExplosion, maxActiveDebris = maxActiveDebris, debrisLifetime = debrisLifetime,
            enableDebrisCleanup = enableDebrisCleanup, contactMaterial = debrisContactMaterial
        }, body);
        DroneExplosionEffect.Play(position, explosionMaterial, explosionRadius, explosionEffectDuration, explosionParticleCount);
        IsDestroyed = true; IsArmed = false;
        SetControlled(false);
        gameObject.SetActive(false);
        Exploded?.Invoke();
    }

    public void ResetDrone()
    {
        StopAllCoroutines();
        if (body == null) body = GetComponent<Rigidbody>();
        IsDestroyed = false; IsArmed = false;
        SetControlled(false);
        body.linearVelocity = Vector3.zero; body.angularVelocity = Vector3.zero;
        body.position = spawnPosition; body.rotation = spawnRotation;
        transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        yaw = spawnRotation.eulerAngles.y; pitch = 0;
        if (viewCamera != null) viewCamera.transform.localRotation = Quaternion.identity;
        gameObject.SetActive(true);
        Physics.SyncTransforms();
    }
}

