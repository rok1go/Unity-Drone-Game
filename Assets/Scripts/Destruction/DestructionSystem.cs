using System;
using System.Collections.Generic;
using Unity.Profiling;
using UnityEngine;

[Serializable]
public struct ExplosionDamage
{
    public Vector3 position;
    public float radius, impulse, debrisLifetime;
    public int maxFragments, maxActiveDebris;
    public bool enableDebrisCleanup;
    public PhysicsMaterial contactMaterial;
    public float impactFractureSpeed;
    public float humanDamage;
}

public static class DestructionSystem
{
    private static readonly HashSet<DestructionDebris> debris = new HashSet<DestructionDebris>();
    private static readonly ProfilerMarker ExplosionMarker = new ProfilerMarker("Slop.ApplyExplosion");
    private static double budgetStep = double.NaN;
    private static int bodiesCreated;
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
    private static void ResetState()
    {
        debris.Clear();
        LastDetachedIds = Array.Empty<string>();
        BlockDetached = null;
        budgetStep = double.NaN;
    }

    internal static bool HasStepCapacity()
    {
        if (budgetStep != Time.fixedTimeAsDouble)
        {
            budgetStep = Time.fixedTimeAsDouble;
            bodiesCreated = 0;
        }
        return bodiesCreated < 80;
    }
    internal static bool HasCapacity(int limit)
    {
        // Persistent sleeping rubble must not permanently consume the active budget.
        return ActiveDebrisCount < Mathf.Clamp(limit, 1, 256);
    }

    // Each newly disconnected structure gets its own body. Never weld unrelated rubble
    // to an older body: it makes distant pieces stick together and teleports inertia.
    // The blast-fragment budget is enforced before detachment; connected collapses
    // remain physical even when that budget has been reached.
    internal static DestructionDebris DetachGroup(List<DestructibleBlock> blocks, ExplosionDamage damage,
        string label, float? inheritedExpiry = null)
    {
        if (blocks.Count == 0 || !HasStepCapacity()) return null;
        bodiesCreated++;
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
            if (!inheritedExpiry.HasValue) BlockDetached?.Invoke(block.StableId, position);
        }
        chunk.Initialize(damage, blocks, inheritedExpiry);
        RefreshBody(chunk);
        chunk.Body.WakeUp();
        return chunk;
    }

    internal static void RefreshBody(DestructionDebris chunk)
    {
        var body = chunk.Body;
        body.mass = Mathf.Clamp(chunk.BlockCount * 0.7f, 0.3f, 100f);
        body.ResetCenterOfMass();
        body.ResetInertiaTensor();
    }

    private static DestructionDebris Split(DestructionDebris source, DestructibleBlock block)
    {
        if (source.BlockCount < 2 || !HasStepCapacity()) return null;
        Vector3 center = source.Body.worldCenterOfMass;
        Vector3 velocity = source.Body.linearVelocity;
        Vector3 angular = source.Body.angularVelocity;
        if (!source.Release(block, out float expiry)) return null;
        var child = DetachGroup(new List<DestructibleBlock> { block }, source.Damage, "SplitFragment", expiry);
        RefreshBody(source);
        source.Body.linearVelocity = velocity + Vector3.Cross(angular, source.Body.worldCenterOfMass - center);
        child.Body.linearVelocity = velocity + Vector3.Cross(angular, child.Body.worldCenterOfMass - center);
        child.Body.angularVelocity = angular;
        return child;
    }

    internal static void FractureImpact(DestructionDebris source, Vector3 point, float speed)
    {
        if (source.BlockCount < 2 || !HasCapacity(source.Damage.maxActiveDebris) || !HasStepCapacity()) return;
        var blocks = source.GetComponentsInChildren<DestructibleBlock>();
        Array.Sort(blocks, (a, b) => (a.transform.position - point).sqrMagnitude.CompareTo(
            (b.transform.position - point).sqrMagnitude));
        int available = Mathf.Clamp(source.Damage.maxActiveDebris, 1, 256) - ActiveDebrisCount;
        for (int i = 0; i < Mathf.Min(available, Mathf.Min(6, blocks.Length / 2)); i++)
        {
            var child = Split(source, blocks[i]);
            if (child == null) break;
            Vector3 away = child.Body.worldCenterOfMass - point + Vector3.up * 0.4f;
            child.Body.AddForce(away.normalized * Mathf.Min(3f, speed * 0.2f), ForceMode.VelocityChange);
        }
    }

    public static int ApplyExplosion(ExplosionDamage damage, Rigidbody ignoreBody = null)
    {
        if (!Application.isPlaying || damage.radius <= 0) return 0;
        using var sample = ExplosionMarker.Auto();
        var humanDamage = HumanExplosionDamage.Capture(damage, ignoreBody);
        Collider[] hits = Physics.OverlapSphere(damage.position, damage.radius, ~0, QueryTriggerInteraction.Ignore);
        var candidates = new Dictionary<DestructibleBlock, float>();
        foreach (var hit in hits)
        {
            var block = hit.GetComponent<DestructibleBlock>();
            if (block != null && (!block.IsDetached ||
                (hit.attachedRigidbody != null &&
                 hit.attachedRigidbody.TryGetComponent<DestructionDebris>(out var chunk) &&
                 chunk.BlockCount > 1)))
            {
                float distance = (hit.ClosestPoint(damage.position) - damage.position).sqrMagnitude;
                if (!candidates.TryGetValue(block, out float current) || distance < current)
                    candidates[block] = distance;
            }
        }
        var ordered = new List<DestructibleBlock>(candidates.Keys);
        ordered.Sort((a,b) =>
        {
            int compare = candidates[a].CompareTo(candidates[b]);
            return compare != 0 ? compare : string.CompareOrdinal(a.StableId,b.StableId);
        });
        int count = Mathf.Min(Mathf.Clamp(damage.maxFragments, 0, 128),
            Mathf.Clamp(damage.maxActiveDebris, 1, 256) - ActiveDebrisCount);
        var structures = new HashSet<BuildingStructure>();
        var ids = new List<string>();
        foreach (var block in ordered)
        {
            if (ids.Count >= count || !HasStepCapacity()) break;
            if (block.IsDetached)
            {
                var source = block.GetComponentInParent<DestructionDebris>();
                if (source != null && Split(source, block) != null)
                    ids.Add(block.StableId);
                continue;
            }
            if (block.Structure != null) structures.Add(block.Structure);
            ids.Add(block.StableId);
            DetachGroup(new List<DestructibleBlock> { block }, damage, "BlastFragment");
        }
        LastDetachedIds = ids.ToArray();
        foreach (var structure in structures) if (structure != null) structure.CheckSupport(damage);
        var bodies = new HashSet<Rigidbody>();
        // The same colliders stay in place during detachment. Their attached bodies can change.
        foreach (var hit in hits)
        {
            var target = hit.attachedRigidbody;
            if (target == null || target == ignoreBody || !bodies.Add(target)) continue;
            var chunk = target.GetComponent<DestructionDebris>();
            if (chunk != null) chunk.Wake();
            if (target.isKinematic) continue;
            target.AddExplosionForce(damage.impulse,damage.position,damage.radius,0.6f,ForceMode.Impulse);
        }
        humanDamage.Apply();
        return ids.Count;
    }
}
