using System.Collections.Generic;
using UnityEngine;

// Rubble persists by default; timed cleanup is an explicit opt-in.
[RequireComponent(typeof(Rigidbody))]
public class DestructionDebris : MonoBehaviour
{
    private class Batch
    {
        public DestructibleBlock[] blocks;
        public float expiresAt;
    }
    private readonly List<Batch> batches = new List<Batch>();
    public bool CleanupEnabled { get; private set; }
    public float DetachedAt { get; private set; }
    public Rigidbody Body { get; private set; }
    private void Awake() => Body = GetComponent<Rigidbody>();
    private void OnEnable() { if (Body == null) Body = GetComponent<Rigidbody>(); DestructionSystem.Register(this); }
    private void OnDisable() => DestructionSystem.Unregister(this);
    public void Initialize(ExplosionDamage damage, List<DestructibleBlock> blocks)
    {
        CleanupEnabled = damage.enableDebrisCleanup;
        DetachedAt = Time.time;
        batches.Add(new Batch {blocks = blocks.ToArray(), expiresAt = CleanupEnabled
            ? Time.time + Mathf.Max(0.25f, damage.debrisLifetime) : float.PositiveInfinity});
    }
    private void Update()
    {
        if (!CleanupEnabled) return;
        bool removed = false;
        for (int i = batches.Count - 1; i >= 0; i--)
        {
            if (Time.time < batches[i].expiresAt) continue;
            foreach (var block in batches[i].blocks)
                if (block != null) { block.gameObject.SetActive(false); Destroy(block.gameObject); }
            batches.RemoveAt(i); removed = true;
        }
        if (batches.Count == 0)
        { gameObject.SetActive(false); Destroy(gameObject); return; }
        if (removed && Body != null)
        { Body.ResetCenterOfMass(); Body.ResetInertiaTensor(); }
    }
    internal void Wake()
    {
        // Normal PhysX sleep saves CPU while leaving bodies movable by collisions.
        // Never turn settled rubble into static obstacles or gate waking on a budget.
        if (Body != null) Body.WakeUp();
    }
}
