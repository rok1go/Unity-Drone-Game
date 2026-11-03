using System;
using System.Collections.Generic;
using UnityEngine;

[Serializable]
public struct ExplosionDamage
{
    public Vector3 position;
    public float radius, impulse, debrisLifetime;
    public int maxFragments, maxActiveDebris;
    public bool enableDebrisCleanup;
    public PhysicsMaterial contactMaterial;
}

public static class DestructionSystem
{
    private static readonly HashSet<DestructionDebris> debris = new HashSet<DestructionDebris>();
    public static int ActiveDebrisCount
    {
        get { int count = 0; foreach (var item in debris) if (item != null && item.Body != null && !item.Body.isKinematic && !item.Body.IsSleeping()) count++; return count; }
    }
    public static int TotalDebrisCount => debris.Count;
    public static event Action<string, Vector3> BlockDetached;
    public static string[] LastDetachedIds { get; private set; } = Array.Empty<string>();
    internal static void Register(DestructionDebris item) => debris.Add(item);
    internal static void Unregister(DestructionDebris item) => debris.Remove(item);
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetState() { debris.Clear(); LastDetachedIds = Array.Empty<string>(); BlockDetached = null; }
    internal static bool HasCapacity(int limit)
    {
        // Persistent sleeping rubble must not permanently consume the active budget.
        return ActiveDebrisCount < Mathf.Clamp(limit, 1, 256);
    }

    // Each newly disconnected structure gets its own body. Never weld unrelated rubble
    // to an older body: it makes distant pieces stick together and teleports inertia.
    // The blast-fragment budget is enforced before detachment; connected collapses
    // remain physical even when that budget has been reached.
    internal static DestructionDebris DetachGroup(List<DestructibleBlock> blocks, ExplosionDamage damage, string label)
    {
        if (blocks.Count == 0) return null;
        Transform owner = blocks[0].DebrisParent;
        var obj = new GameObject(label); obj.transform.SetParent(owner, true);
        Vector3 center = Vector3.zero; foreach (var block in blocks) center += block.transform.position;
        obj.transform.position = center / blocks.Count;
        var body = obj.AddComponent<Rigidbody>();
        body.useGravity = true; body.linearDamping = 0.02f; body.angularDamping = 0.1f;
        body.solverIterations = 8;
        body.solverVelocityIterations = 3;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        var chunk = obj.AddComponent<DestructionDebris>();
        foreach (var block in blocks)
        {
            Vector3 position = block.transform.position;
            block.DetachInto(chunk.transform, damage.contactMaterial);
            BlockDetached?.Invoke(block.StableId, position);
        }
        chunk.Body.mass = Mathf.Clamp(chunk.GetComponentsInChildren<Collider>().Length * 0.7f, 0.3f, 100f);
        chunk.Body.ResetCenterOfMass(); chunk.Body.ResetInertiaTensor();
        chunk.Initialize(damage, blocks); chunk.Body.WakeUp();
        return chunk;
    }

    public static int ApplyExplosion(ExplosionDamage damage, Rigidbody ignoreBody = null)
    {
        if (!Application.isPlaying || damage.radius <= 0) return 0;
        Collider[] hits = Physics.OverlapSphere(damage.position, damage.radius, ~0, QueryTriggerInteraction.Ignore);
        var candidates = new HashSet<DestructibleBlock>();
        foreach (var hit in hits)
        {
            var block = hit.GetComponent<DestructibleBlock>();
            if (block != null && !block.IsDetached) candidates.Add(block);
        }
        var ordered = new List<DestructibleBlock>(candidates);
        ordered.Sort((a,b) =>
        {
            int compare = (a.GetComponent<Collider>().ClosestPoint(damage.position)-damage.position).sqrMagnitude.CompareTo(
                (b.GetComponent<Collider>().ClosestPoint(damage.position)-damage.position).sqrMagnitude);
            return compare != 0 ? compare : string.CompareOrdinal(a.StableId,b.StableId);
        });
        int count = Mathf.Min(ordered.Count, Mathf.Clamp(damage.maxFragments,0,128));
        var structures = new HashSet<BuildingStructure>();
        var ids = new List<string>();
        for (int i = 0; i < count; i++)
        {
            if (!HasCapacity(damage.maxActiveDebris)) break;
            var block = ordered[i];
            if (block.Structure != null) structures.Add(block.Structure);
            ids.Add(block.StableId);
            DetachGroup(new List<DestructibleBlock> { block }, damage, "BlastFragment");
        }
        LastDetachedIds = ids.ToArray();
        foreach (var structure in structures) if (structure != null) structure.CheckSupport(damage);
        var bodies = new HashSet<Rigidbody>();
        // Includes collapse groups as well as the original blast fragments and crates.
        foreach (var hit in Physics.OverlapSphere(damage.position,damage.radius,~0,QueryTriggerInteraction.Ignore))
        {
            var target = hit.attachedRigidbody;
            if (target == null || target == ignoreBody || !bodies.Add(target)) continue;
            var chunk = target.GetComponent<DestructionDebris>();
            if (chunk != null) chunk.Wake();
            if (target.isKinematic) continue;
            target.AddExplosionForce(damage.impulse,damage.position,damage.radius,0.6f,ForceMode.Impulse);
        }
        return ids.Count;
    }
}
