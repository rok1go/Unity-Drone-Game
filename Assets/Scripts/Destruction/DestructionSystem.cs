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
    // Only the authority (offline, or the online host) simulates destruction. Online clients replay it.
    public static bool Authority { get; set; } = true;
    private const float PropBlastDamage = 160f;   // at the centre of a blast, falling to zero at its edge
    public static event Action<DestructionDebris, IReadOnlyList<DestructibleBlock>> ChunkCreated;
    public static event Action<DestructionDebris, IReadOnlyList<DestructibleBlock>> BlocksRemoved;
    // Blocks pulverised by a blast at the given point. The host forwards these to clients.
    public static event Action<IReadOnlyList<DestructibleBlock>, Vector3, float> BlocksShattered;
    // Blast zones as fractions of the radius, divided by a block's toughness: inside the shatter zone a
    // block is pulverised (a hole), inside the tear zone it comes loose and is thrown; beyond, it holds.
    // With the drone's 7 m radius: concrete is holed out to ~2.4 m and torn to ~3.9 m, brick to 2.7 / 4.4 m,
    // plaster partitions, floors and wood to 3.5 / 5.9 m, so a hit clears rooms behind the wall too.
    private const float ShatterZone = 0.4f, TearZone = 0.66f;
    // Whatever the material, the blocks a drone actually strikes are always blown out.
    private const float DirectHitRadius = 1.4f;
    private const float ThrowSpeedPerImpulse = 0.85f;  // m/s of throw per unit of blast impulse at the core
    public static string[] LastDetachedIds { get; private set; } = Array.Empty<string>();
    internal static void Register(DestructionDebris item) => debris.Add(item);
    internal static void Unregister(DestructionDebris item) => debris.Remove(item);
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetState()
    {
        debris.Clear();
        LastDetachedIds = Array.Empty<string>();
        ChunkCreated = null;
        BlocksRemoved = null;
        BlocksShattered = null;
        Authority = true;
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
        body.maxDepenetrationVelocity = 2f;
        body.maxAngularVelocity = 8f;
        body.interpolation = RigidbodyInterpolation.Interpolate;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        var chunk = obj.AddComponent<DestructionDebris>();
        foreach (var block in blocks) block.DetachInto(chunk.transform, damage.contactMaterial);
        chunk.Initialize(damage, blocks, inheritedExpiry);
        RefreshBody(chunk);
        chunk.Body.WakeUp();
        if (blocks.Count > 3) CollapseEffects(obj.transform.position, blocks.Count);
        ChunkCreated?.Invoke(chunk, blocks);
        return chunk;
    }

    // Client replica of a host rubble body: same blocks, kinematic, driven by host snapshots.
    internal static DestructionDebris CreateReplicaChunk(List<DestructibleBlock> blocks, Vector3 root)
    {
        var obj = new GameObject("ReplicatedDebris");
        obj.transform.SetParent(blocks[0].DebrisParent, true);
        obj.transform.SetPositionAndRotation(root, Quaternion.identity);
        var body = obj.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.interpolation = RigidbodyInterpolation.None;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;
        var chunk = obj.AddComponent<DestructionDebris>();
        foreach (var block in blocks) block.DetachInto(chunk.transform, null);
        chunk.Initialize(new ExplosionDamage { maxActiveDebris = 128, enableDebrisCleanup = false }, blocks);
        RefreshBody(chunk);
        if (blocks.Count > 3) CollapseEffects(root, blocks.Count);
        return chunk;
    }

    internal static void RaiseBlocksRemoved(DestructionDebris chunk, List<DestructibleBlock> blocks)
    {
        if (blocks.Count > 0) BlocksRemoved?.Invoke(chunk, blocks);
    }

    // Sound and dust of a wall being blown through; every peer plays them from the same event.
    internal static void ShatterEffects(Vector3 blast, int blocks, IReadOnlyList<DestructibleBlock> shattered = null)
    {
        // A car caught in the blast: one crunch of torn sheet metal per vehicle, where it stands.
        if (shattered != null)
        {
            var cars = new HashSet<BuildingStructure>();
            foreach (var block in shattered)
                if (block != null && block.IsVehicle && cars.Add(block.Structure))
                    GameAudio.Play(SoundBank.CarCrash, block.transform.position, 0.8f, UnityEngine.Random.Range(0.95f, 1.05f),
                        SoundCategory.Effects, 3f, 70f);
        }
        GameAudio.Play(SoundBank.Crumble, blast, Mathf.Clamp01(0.5f + blocks * 0.05f), 1f, SoundCategory.Effects, 6f, 120f);
        DustCloud.Spawn(blast, Mathf.Clamp(1.5f + blocks * 0.25f, 2f, 6f), Mathf.Clamp01(0.4f + blocks * 0.05f));
    }

    // A section of building coming down: rumble and a cloud where it falls.
    private static void CollapseEffects(Vector3 position, int blocks)
    {
        GameAudio.Play(SoundBank.Crumble, position, 1f, UnityEngine.Random.Range(0.8f, 1f), SoundCategory.Effects, 5f, 100f);
        DustCloud.Spawn(position, Mathf.Clamp(1.5f + blocks * 0.1f, 2f, 7f), Mathf.Clamp01(0.3f + blocks * 0.02f));
    }

    // Also used by clients replaying the host's blast. Shards are cosmetic and local to each peer.
    internal static void ShatterLocal(DestructibleBlock block, Vector3 blast, float impulse)
    {
        if (block == null) return;
        BlockShards.Spawn(block, blast, impulse);
        block.Remove();
    }

    // Pushes only locally simulated bodies. Online clients use it for their own player after a host blast.
    public static void ApplyBlastForces(Vector3 position, float radius, float impulse, Rigidbody ignoreBody = null)
    {
        if (!Application.isPlaying || radius <= 0f) return;
        var bodies = new HashSet<Rigidbody>();
        foreach (var hit in Physics.OverlapSphere(position, radius, ~0, QueryTriggerInteraction.Ignore))
        {
            var target = hit.attachedRigidbody;
            if (target == null || target == ignoreBody || target.isKinematic || !bodies.Add(target)) continue;
            Push(target, position, radius, impulse);
        }
    }

    // A person is knocked back by a bounded change of speed (a stagger, at most 6 m/s at the centre), not by
    // the raw impulse, so the push does not depend on body mass or turn into a lethal fall by itself.
    private static void Push(Rigidbody target, Vector3 position, float radius, float impulse)
    {
        if (target.GetComponent<HumanController>() != null)
            target.AddExplosionForce(Mathf.Min(6f, impulse * 0.28f), position, radius, 0.3f, ForceMode.VelocityChange);
        else target.AddExplosionForce(impulse, position, radius, 0.6f, ForceMode.Impulse);
    }

    internal static void RefreshBody(DestructionDebris chunk)
    {
        var body = chunk.Body;
        // Real weight from the blocks' solid volume and material: a loose plank is liftable, a slab is not.
        float mass = 0f;
        foreach (var block in chunk.GetComponentsInChildren<DestructibleBlock>()) mass += block.Mass;
        body.mass = Mathf.Clamp(mass, 0.5f, 5000f);
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
        if (!Authority || source.BlockCount < 2 || !HasCapacity(source.Damage.maxActiveDebris) || !HasStepCapacity()) return;
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
        if (!Application.isPlaying || damage.radius <= 0 || !Authority) return 0;
        using var sample = ExplosionMarker.Auto();
        var humanDamage = HumanExplosionDamage.Capture(damage, ignoreBody);
        Collider[] hits = Physics.OverlapSphere(damage.position, damage.radius, ~0, QueryTriggerInteraction.Ignore);
        var candidates = new Dictionary<DestructibleBlock, float>();
        var propDamage = new Dictionary<PhysicsProp, float>();
        BreakableWindow nearestShatteredWindow = null;
        float nearestWindowDistance = float.PositiveInfinity;
        foreach (var hit in hits)
        {
            var door = hit.GetComponentInParent<InteractiveDoor>();
            if (door != null)
            {
                if (Vector3.Distance(hit.ClosestPoint(damage.position), damage.position) < damage.radius * 0.75f)
                    door.Break(damage.position, damage.impulse);
                continue;
            }
            if (hit.TryGetComponent<BreakableWindow>(out var window))
            {
                float distance = Vector3.Distance(hit.ClosestPoint(damage.position), damage.position);
                float strength = 1f - Mathf.Clamp01(distance / damage.radius);
                float speed = Mathf.Lerp(2.5f, Mathf.Clamp(damage.impulse * 0.3f, 3f, 9f), strength);
                if (window.Shatter(damage.position, speed, true) && distance < nearestWindowDistance)
                {
                    nearestShatteredWindow = window;
                    nearestWindowDistance = distance;
                }
                continue;
            }
            if (hit.attachedRigidbody != null && hit.attachedRigidbody.TryGetComponent<PhysicsProp>(out var prop))
            {
                float d = Vector3.Distance(hit.ClosestPoint(damage.position), damage.position);
                float share = 1f - Mathf.Clamp01(d / damage.radius);
                if (!propDamage.TryGetValue(prop, out float current) || share > current) propDamage[prop] = share;
                continue;
            }
            var block = hit.GetComponent<DestructibleBlock>();
            if (block == null) continue;
            float blockDistance = Vector3.Distance(hit.ClosestPoint(damage.position), damage.position);
            if (block.IsDetached)
            {
                // Loose rubble anywhere in the blast can be split further.
                if (hit.attachedRigidbody == null || !hit.attachedRigidbody.TryGetComponent<DestructionDebris>(out var chunk) ||
                    chunk.BlockCount < 2) continue;
            }
            else if (blockDistance > Mathf.Max(DirectHitRadius, damage.radius * TearZone / block.Toughness)) continue; // Tough material needs a closer hit.
            if (!candidates.TryGetValue(block, out float closest) || blockDistance < closest)
                candidates[block] = blockDistance;
        }
        var ordered = new List<DestructibleBlock>(candidates.Keys);
        ordered.Sort((a,b) =>
        {
            int compare = candidates[a].CompareTo(candidates[b]);
            return compare != 0 ? compare : string.CompareOrdinal(a.StableId,b.StableId);
        });
        // Clearing a local hole creates no rigid bodies and must still work when rubble is at its budget.
        int count = Mathf.Clamp(damage.maxFragments, 1, 1024);
        int fragmentSlots = Mathf.Max(0, Mathf.Clamp(damage.maxActiveDebris, 1, 256) - ActiveDebrisCount);
        int fragmentsCreated = 0;
        var structures = new HashSet<BuildingStructure>();
        var ids = new List<string>();
        var shattered = new List<DestructibleBlock>();
        var thrown = new Dictionary<Rigidbody, float>();   // fresh fragment -> its throw speed
        foreach (var block in ordered)
        {
            if (ids.Count >= count) break;
            float distance = candidates[block];
            if (block.IsDetached)
            {
                var source = block.GetComponentInParent<DestructionDebris>();
                if (source != null && fragmentsCreated < fragmentSlots && HasStepCapacity() && Split(source, block) != null)
                {
                    ids.Add(block.StableId);
                    fragmentsCreated++;
                }
                continue;
            }
            if (block.Structure != null) structures.Add(block.Structure);
            // The core of the blast punches a hole: the block turns into flying shards.
            if (distance <= Mathf.Max(DirectHitRadius, damage.radius * ShatterZone / block.Toughness))
            {
                ids.Add(block.StableId);
                shattered.Add(block);
                continue;
            }
            if (fragmentsCreated >= fragmentSlots || !HasStepCapacity()) continue;
            // Further out it is torn loose and thrown, harder the closer and the weaker it is.
            var fragment = DetachGroup(new List<DestructibleBlock> { block }, damage, "BlastFragment");
            if (fragment == null) continue;
            ids.Add(block.StableId); fragmentsCreated++;
            float strength = 1f - Mathf.Clamp01(distance / (damage.radius * TearZone / block.Toughness));
            thrown[fragment.Body] = Mathf.Min(9f, damage.impulse * ThrowSpeedPerImpulse * Mathf.Lerp(0.3f, 1f, strength) /
                Mathf.Sqrt(block.Toughness));
        }
        LastDetachedIds = ids.ToArray();
        if (shattered.Count > 0)
        {
            BlocksShattered?.Invoke(shattered, damage.position, damage.impulse);
            foreach (var block in shattered) ShatterLocal(block, damage.position, damage.impulse);
            ShatterEffects(damage.position, shattered.Count, shattered);
        }
        foreach (var structure in structures) if (structure != null) structure.CheckSupport(damage);
        PowerSystem.Instance?.BlastDamage(damage.position, damage.radius);
        foreach (var pair in thrown)
        {
            var body = pair.Key;
            if (body == null) continue;
            // Blocks weigh tens of kilograms, so a fixed impulse barely moves them: set the speed directly.
            Vector3 away = body.worldCenterOfMass - damage.position;
            Vector3 direction = (away.normalized + Vector3.up * 0.35f).normalized;
            body.linearVelocity = direction * pair.Value;
            body.angularVelocity = UnityEngine.Random.insideUnitSphere * pair.Value * 0.6f;
        }
        var bodies = new HashSet<Rigidbody>();
        // The same colliders stay in place during detachment. Their attached bodies can change.
        foreach (var hit in hits)
        {
            if (hit == null) continue;   // Shattered this blast.
            var target = hit.attachedRigidbody;
            if (target == null || target == ignoreBody || !bodies.Add(target)) continue;
            var chunk = target.GetComponent<DestructionDebris>();
            if (chunk != null) chunk.Wake();
            if (target.isKinematic || thrown.ContainsKey(target)) continue;
            Push(target, damage.position, damage.radius, damage.impulse);
        }
        if (nearestShatteredWindow != null) nearestShatteredWindow.PlayBreakSound(true);
        // Props take blast damage after the push, so a prop flung by this blast is still broken by it.
        foreach (var pair in propDamage)
            if (pair.Key != null) pair.Key.Damage(PropBlastDamage * pair.Value);
        humanDamage.Apply();
        return ids.Count;
    }
}
