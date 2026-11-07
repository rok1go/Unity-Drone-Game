using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// Rubble persists by default; timed cleanup is an explicit opt-in.
[RequireComponent(typeof(Rigidbody))]
public class DestructionDebris : MonoBehaviour
{
    private class Batch
    {
        public List<DestructibleBlock> blocks;
        public float expiresAt;
    }
    private readonly List<Batch> batches = new List<Batch>();
    public bool CleanupEnabled { get; private set; }
    public float DetachedAt { get; private set; }
    public Rigidbody Body { get; private set; }
    internal ExplosionDamage Damage { get; private set; }
    internal int BlockCount { get; private set; }
    private bool impactPending;
    private void Awake() => Body = GetComponent<Rigidbody>();
    private void OnEnable() { if (Body == null) Body = GetComponent<Rigidbody>(); DestructionSystem.Register(this); }
    private void OnDisable() => DestructionSystem.Unregister(this);
    public void Initialize(ExplosionDamage damage, List<DestructibleBlock> blocks, float? inheritedExpiry = null)
    {
        CleanupEnabled = damage.enableDebrisCleanup;
        DetachedAt = Time.time;
        Damage = damage;
        BlockCount += blocks.Count;
        batches.Add(new Batch { blocks = new List<DestructibleBlock>(blocks), expiresAt = inheritedExpiry ??
            (CleanupEnabled ? Time.time + Mathf.Max(0.25f, damage.debrisLifetime) : float.PositiveInfinity) });
    }

    internal bool Release(DestructibleBlock block, out float expiry)
    {
        foreach (var batch in batches)
        {
            if (!batch.blocks.Remove(block)) continue;
            BlockCount--;
            expiry = batch.expiresAt;
            return true;
        }
        expiry = 0;
        return false;
    }

    // Client replay of a host cleanup: drop one block, and the body once it is empty.
    internal void RemoveReplicatedBlock(DestructibleBlock block)
    {
        if (Release(block, out _)) { block.gameObject.SetActive(false); Destroy(block.gameObject); }
        if (BlockCount > 0) return;
        gameObject.SetActive(false);
        Destroy(gameObject);
    }

    private void OnCollisionEnter(Collision collision)
    {
        if (!DestructionSystem.Authority || collision.contactCount == 0) return;
        var contact = collision.GetContact(0);
        float speed = Mathf.Abs(Vector3.Dot(collision.relativeVelocity, contact.normal));
        if (Body != null) GameAudio.ContactImpact(this, Body, collision, SoundMaterialOf(this));
        if (impactPending || BlockCount < 2 || Time.time - DetachedAt < 0.15f) return;
        if (speed < (Damage.impactFractureSpeed > 0 ? Damage.impactFractureSpeed : 6f)) return;
        impactPending = true;
        StartCoroutine(FractureAfterContact(contact.point, speed));
    }

    // Rubble sounds like what it is made of: brick and concrete knock, plaster and wood thud.
    internal static SoundMaterial SoundMaterialOf(Component chunk)
    {
        var block = chunk.GetComponentInChildren<DestructibleBlock>();
        if (block != null && block.IsVehicle) return SoundMaterial.Metal;
        return GameAudio.MaterialFor(block != null ? block.Material : (byte)1);
    }

    private IEnumerator FractureAfterContact(Vector3 point, float speed)
    {
        Vector3 localPoint = transform.InverseTransformPoint(point);
        yield return new WaitForFixedUpdate();
        impactPending = false;
        DestructionSystem.FractureImpact(this, transform.TransformPoint(localPoint), speed);
    }
    private void Update()
    {
        if (!CleanupEnabled) return;
        bool removed = false;
        for (int i = batches.Count - 1; i >= 0; i--)
        {
            if (Time.time < batches[i].expiresAt) continue;
            DestructionSystem.RaiseBlocksRemoved(this, batches[i].blocks);
            foreach (var block in batches[i].blocks)
                if (block != null) { block.gameObject.SetActive(false); Destroy(block.gameObject); }
            BlockCount -= batches[i].blocks.Count;
            batches.RemoveAt(i); removed = true;
        }
        if (batches.Count == 0)
        { gameObject.SetActive(false); Destroy(gameObject); return; }
        if (removed && Body != null) DestructionSystem.RefreshBody(this);
    }
    internal void Wake()
    {
        // Normal PhysX sleep saves CPU while leaving bodies movable by collisions.
        // Never turn settled rubble into static obstacles or gate waking on a budget.
        if (Body != null) Body.WakeUp();
    }
}
