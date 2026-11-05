using System.Collections.Generic;
using UnityEngine;

// Snapshot cover before destruction, then resolve it after the same blast opens holes.
public sealed class HumanExplosionDamage
{
    private const float FullProtectionThickness = 2.5f;
    private const float WallTransmissionPerMetre = 0.5f;
    private const float DebrisTransmissionPerMetre = 0.8f;

    private struct Barrier
    {
        public Collider collider;
        public DestructibleBlock block;
        public bool wasDetached;
        public Vector2 interval;
        public bool IsRubble => wasDetached || (collider != null &&
            collider.GetComponentInParent<DestructionDebris>() != null);

        public bool StillProtects => collider != null && collider.enabled && collider.gameObject.activeInHierarchy &&
            (block == null || wasDetached || !block.IsDetached);
    }

    private sealed class Target
    {
        public HumanController human;
        public readonly List<Exposure> paths = new List<Exposure>(3);
    }

    private sealed class Exposure
    {
        public float damage;
        public readonly List<Barrier> barriers = new List<Barrier>();

        public float Transmission()
        {
            var solid = new List<Vector2>();
            var rubble = new List<Vector2>();
            foreach (var barrier in barriers)
            {
                if (!barrier.StillProtects) continue;
                (barrier.IsRubble ? rubble : solid).Add(barrier.interval);
            }
            float solidThickness = CoveredLength(solid);
            if (solidThickness >= FullProtectionThickness) return 0f;
            return Mathf.Pow(WallTransmissionPerMetre, solidThickness) *
                Mathf.Pow(DebrisTransmissionPerMetre, CoveredLength(rubble));
        }
    }

    private readonly List<Target> targets = new List<Target>();

    public static HumanExplosionDamage Capture(ExplosionDamage damage, Rigidbody source)
    {
        var result = new HumanExplosionDamage();
        if (MultiplayerPrototype.OnlineMatch && !MultiplayerPrototype.Instance.IsHost) return result;
        foreach (var human in Object.FindObjectsByType<HumanController>())
        {
            if (human.IsEliminated || !human.TryGetComponent<CapsuleCollider>(out var capsule)) continue;
            Bounds bounds = capsule.bounds;
            var target = new Target { human = human };
            // Head, torso and lower body: an exposed part can be hit through a real opening.
            for (int i = -1; i <= 1; i++)
            {
                Vector3 point = bounds.center + Vector3.up * (bounds.extents.y * 0.65f * i);
                Vector3 toBlast = damage.position - point;
                float distance = toBlast.magnitude;
                if (distance > damage.radius) continue;
                var exposure = new Exposure
                {
                    damage = Mathf.Max(0f, damage.humanDamage) * Mathf.Clamp01(1f - distance / damage.radius)
                };
                if (distance > 0.001f)
                {
                    // Cast from the victim so a blast touching/inside a wall still detects that wall.
                    foreach (var hit in Physics.RaycastAll(point, toBlast / distance, distance,
                        ~0, QueryTriggerInteraction.Ignore))
                    {
                        if ((source != null && hit.rigidbody == source) ||
                            hit.collider.GetComponentInParent<HumanController>() != null) continue;
                        var block = hit.collider.GetComponent<DestructibleBlock>();
                        Vector2 interval = CoverInterval(hit.collider, point, toBlast / distance, distance);
                        if (interval.y <= interval.x) continue;
                        exposure.barriers.Add(new Barrier
                        {
                            collider = hit.collider, block = block, wasDetached = block != null && block.IsDetached,
                            interval = interval
                        });
                    }
                }
                target.paths.Add(exposure);
            }
            if (target.paths.Count > 0) result.targets.Add(target);
        }
        return result;
    }

    public void Apply()
    {
        foreach (var target in targets)
        {
            if (target.human == null || target.human.IsEliminated) continue;
            float strongestDamage = 0f;
            foreach (var path in target.paths)
            {
                strongestDamage = Mathf.Max(strongestDamage, path.damage * path.Transmission());
            }
            // Multiple exposed body samples still count as one hit from this blast.
            target.human.ApplyDamage(strongestDamage);
        }
    }

    private static Vector2 CoverInterval(Collider collider, Vector3 origin, Vector3 direction, float distance)
    {
        Vector3 halfSize;
        if (collider is BoxCollider box)
        {
            // Local-space slab intersections preserve world-distance parameters, including rotated/scaled walls.
            origin = box.transform.InverseTransformPoint(origin) - box.center;
            direction = box.transform.InverseTransformVector(direction);
            halfSize = box.size * 0.5f;
        }
        else
        {
            // Conservative cover estimate for non-box props.
            Bounds bounds = collider.bounds;
            origin -= bounds.center;
            halfSize = bounds.extents;
        }
        float enter = 0f, exit = distance;
        for (int axis = 0; axis < 3; axis++)
        {
            if (Mathf.Abs(direction[axis]) < 0.000001f)
            {
                if (Mathf.Abs(origin[axis]) > halfSize[axis]) return Vector2.zero;
                continue;
            }
            float a = (-halfSize[axis] - origin[axis]) / direction[axis];
            float b = (halfSize[axis] - origin[axis]) / direction[axis];
            enter = Mathf.Max(enter, Mathf.Min(a, b));
            exit = Mathf.Min(exit, Mathf.Max(a, b));
            if (exit <= enter) return Vector2.zero;
        }
        return new Vector2(enter, exit);
    }

    private static float CoveredLength(List<Vector2> intervals)
    {
        if (intervals.Count == 0) return 0f;
        intervals.Sort((a, b) => a.x.CompareTo(b.x));
        float total = 0f, start = intervals[0].x, end = intervals[0].y;
        for (int i = 1; i < intervals.Count; i++)
        {
            if (intervals[i].x <= end + 0.001f) end = Mathf.Max(end, intervals[i].y);
            else
            {
                total += end - start;
                start = intervals[i].x;
                end = intervals[i].y;
            }
        }
        // Union overlapping colliders so a shared block edge does not count the wall twice.
        return total + end - start;
    }
}
