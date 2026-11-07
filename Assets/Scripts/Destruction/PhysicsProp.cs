using System;
using System.Collections.Generic;
using UnityEngine;

// A loose physical object from the map: chair, table, crate, bin... It has a real mass (what a
// player can lift, how far it flies) and health (blasts and hard impacts break it).
// Only the destruction authority (offline, or the online host) simulates and damages props.
// Small items that fit in a pocket: E puts them into the 3-slot inventory (LMB still grabs them).
public enum PocketKind { None, Stone, Plank, Flashlight }

[DisallowMultipleComponent]
[RequireComponent(typeof(Rigidbody))]
public sealed class PhysicsProp : MonoBehaviour, IInteractable
{
    public PocketKind Pocket { get; private set; }
    public bool Stowed { get; private set; }
    // Host: stowed / dropped. The replication forwards it; clients apply it with SetStowed.
    public static event Action<PhysicsProp> StowChanged;
    public string Prompt => Pocket == PocketKind.None || Stowed || IsBroken || !MatchRules.Current.Items ? null :
        Inventory.HasFreeSlot ? "Взять: " + Label : "Инвентарь полон";
    public void Interact(Vector3 user) { if (Inventory.HasFreeSlot) Inventory.RequestPickup(this); }

    // Into a pocket: gone from the world, nobody holds it. Out of it: back at a pose with a velocity.
    public void SetStowed(bool stowed, Vector3 position, Quaternion rotation, Vector3 velocity, bool raise)
    {
        if (Body == null) return;
        Stowed = stowed;
        if (stowed) CarrySystem.ReleaseBody(Body);
        gameObject.SetActive(!stowed);
        if (!stowed)
        {
            Body.position = position; Body.rotation = rotation;
            transform.SetPositionAndRotation(position, rotation);
            if (!Body.isKinematic) { Body.linearVelocity = velocity; Body.angularVelocity = UnityEngine.Random.insideUnitSphere * 3f; Body.WakeUp(); }
        }
        if (raise) StowChanged?.Invoke(this);
    }
    private const float ImpactThreshold = 9f;      // m/s before a collision starts to hurt
    private const float ImpactDamagePerMetre = 9f;

    private static readonly List<PhysicsProp> all = new List<PhysicsProp>();
    public static IReadOnlyList<PhysicsProp> All => all;
    public static event Action<PhysicsProp> Broken;

    public int Kind { get; private set; }
    public string Label { get; private set; } = "Предмет";
    public float Health { get; private set; }
    public float MaxHealth { get; private set; }
    public Rigidbody Body { get; private set; }
    public bool IsBroken { get; private set; }
    private Vector3 spawnPosition;
    private Quaternion spawnRotation;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        all.Clear();
        Broken = null;
        StowChanged = null;
    }

    public void Initialize(int kind, string label, float mass, float health)
    {
        Kind = kind;
        Label = label;
        Pocket = label == "Камень" || label == "Странный камень" ? PocketKind.Stone : label == "Фонарик" ? PocketKind.Flashlight :
            label == "Короткая доска" || label == "Доска" ? PocketKind.Plank : PocketKind.None;
        SoundMaterial = GameAudio.MaterialFor(label);
        MaxHealth = Health = Mathf.Max(1f, health);
        Body = GetComponent<Rigidbody>();
        Body.mass = Mathf.Max(0.5f, mass);
        Body.linearDamping = 0.05f;
        Body.angularDamping = 0.2f;
        Body.interpolation = RigidbodyInterpolation.Interpolate;
        Body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        Body.maxDepenetrationVelocity = 1.5f;
        Body.solverIterations = 10; Body.solverVelocityIterations = 4;
        Body.maxAngularVelocity = 8f;
        spawnPosition = transform.position;
        spawnRotation = transform.rotation;
        Body.Sleep();
        if (!all.Contains(this)) all.Add(this);
    }

    private void OnDestroy() => all.Remove(this);

    // Every peer starts a match from the authored placement, so indices and poses agree.
    public void ResetToSpawn()
    {
        if (IsBroken) return;
        bool kinematic = Body.isKinematic;
        Body.isKinematic = false;
        Body.linearVelocity = Vector3.zero;
        Body.angularVelocity = Vector3.zero;
        Body.isKinematic = kinematic;
        Body.position = spawnPosition;
        Body.rotation = spawnRotation;
        transform.SetPositionAndRotation(spawnPosition, spawnRotation);
        Body.Sleep();
    }

    public void Damage(float amount)
    {
        if (IsBroken || !DestructionSystem.Authority || amount <= 0f) return;
        Health -= amount;
        if (Health <= 0f) Break();
    }

    public void Break()
    {
        if (IsBroken) return;
        Broken?.Invoke(this);
        BreakLocal();
    }

    // Also used by clients replaying the host's break.
    public void BreakLocal()
    {
        if (IsBroken) return;
        IsBroken = true;
        Health = 0f;
        GameAudio.Play(SoundMaterial == SoundMaterial.Wood ? SoundBank.WoodBreak : SoundBank.Impact(SoundMaterial, Body.mass > 25f),
            Body != null ? Body.worldCenterOfMass : transform.position, Mathf.Clamp(0.15f + Body.mass / 400f, 0.15f, 0.4f),
            UnityEngine.Random.Range(0.98f, 1.02f), SoundCategory.Effects, 1.5f, 35f);
        CarrySystem.ReleaseBody(Body);
        gameObject.SetActive(false);
        Destroy(gameObject);
    }

    public SoundMaterial SoundMaterial { get; private set; }
    private float nextScrape;
    private float slidingFor;
    private float stoodOnUntil;

    // Bodies (people, the drone) are not bulldozers: running or flying into furniture neither shoves it nor
    // breaks it. The object stays solid; whatever speed the contact gave it is taken away again. Moving things
    // on purpose is F, LMB or a blast. Held objects are left alone (their carrier steers them).
    private float shovedUntil;
    // An F shove from someone standing right against it must not be cancelled by that contact.
    public void Shoved() => shovedUntil = Time.time + 1.2f;

    private bool SettleAgainstBody(Collision collision)
    {
        var other = collision.rigidbody;
        if (other == null || (other.GetComponent<HumanController>() == null && other.GetComponent<DroneController>() == null))
            return false;
        if (CarrySystem.IsHeldAnywhere(Body) || Time.time < shovedUntil) return true;
        Vector3 v = Body.linearVelocity;
        Vector3 flat = new Vector3(v.x, 0f, v.z);
        if (flat.sqrMagnitude > 0.15f * 0.15f) Body.linearVelocity = flat.normalized * 0.15f + Vector3.up * Mathf.Min(v.y, 0f);
        Body.angularVelocity *= 0.3f;
        return true;
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!DestructionSystem.Authority || Body == null || Body.isKinematic) return;
        if (SettleAgainstBody(collision)) return;
        float speed = collision.relativeVelocity.magnitude;
        // A thrown stone (or anything hurled hard enough) goes through a window pane: the glass breaks
        // and the object keeps most of its flight. The break is replicated like any other.
        var pane = collision.collider.GetComponent<BreakableWindow>();
        if (pane != null && speed > 4f && Body.mass * speed > 3f)
        {
            Vector3 before = -collision.relativeVelocity;
            if (pane.Shatter(Body.worldCenterOfMass - before.normalized, Mathf.Clamp(speed * 0.5f, 3f, 8f)))
            {
                pane.PlayBreakSound(false);
                Body.linearVelocity = before * 0.7f;
                return;
            }
        }
        // A pane that holds rings as glass, whatever hit it (replicated like every contact sound).
        if (pane != null) { GameAudio.ContactImpact(this, Body, collision, SoundMaterial.Glass); return; }
        if (GameAudio.ContactImpact(this, Body, collision, SoundMaterial)) nextScrape = Time.time + 0.4f;
        // Something in a player's hands is being steered, not crashing: only throws and blasts hurt it.
        if (CarrySystem.IsHeldAnywhere(Body)) return;
        if (speed > ImpactThreshold) Damage((speed - ImpactThreshold) * ImpactDamagePerMetre);
    }

    // Dragging along the floor: a scrape now and then while it slides.
    private void OnCollisionStay(Collision collision)
    {
        if (!DestructionSystem.Authority || Body == null || Body.isKinematic || collision.contactCount == 0) return;
        // Every step of a lasting contact, not only between scrapes, or a steady push would still creep it along.
        bool body = SettleAgainstBody(collision);
        if (Time.time < nextScrape) return;
        if (body && collision.rigidbody.GetComponent<HumanController>() != null)
        {
            // Someone walking or jumping on a bed or table rocks it on its feet: that is not dragging furniture.
            if (collision.rigidbody.position.y > collision.GetContact(0).point.y - 0.1f) stoodOnUntil = Time.time + 0.5f;
            return;
        }
        if (collision.rigidbody != null && collision.rigidbody.GetComponent<DroneController>() != null) return;
        if (Time.time < stoodOnUntil && !CarrySystem.IsHeldAnywhere(Body)) { slidingFor = 0f; return; }
        // Only real travel scrapes: rocking and contact jitter move the contact point, not the body.
        if (new Vector2(Body.linearVelocity.x, Body.linearVelocity.z).sqrMagnitude < 0.6f * 0.6f) { slidingFor = 0f; return; }
        Vector3 normal = collision.GetContact(0).normal;
        if (Mathf.Abs(normal.y) < 0.7f) return;
        Vector3 ownVelocity = Body.GetPointVelocity(collision.GetContact(0).point);
        Vector3 otherVelocity = collision.rigidbody != null ? collision.rigidbody.GetPointVelocity(collision.GetContact(0).point) : Vector3.zero;
        float slide = Vector3.ProjectOnPlane(ownVelocity - otherVelocity, normal).magnitude;
        if (slide < 0.8f || Body.IsSleeping() || collision.impulse.magnitude < 0.1f) { slidingFor = 0f; return; }
        slidingFor += Time.fixedDeltaTime;
        if (slidingFor < 0.16f) return;
        nextScrape = Time.time + 0.65f;
        GameAudio.Slide(this, collision.GetContact(0).point, Body.mass, slide, SoundMaterial);
    }
    private void OnCollisionExit(Collision collision) => slidingFor = 0f;
}
