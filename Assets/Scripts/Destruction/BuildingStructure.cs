using System.Collections.Generic;
using UnityEngine;

// A six-neighbour flood fill from y=0 is the entire support model. It runs after changes only.
public class BuildingStructure : MonoBehaviour
{
    private static readonly Vector3Int[] neighbours = { Vector3Int.left, Vector3Int.right, Vector3Int.up,
        Vector3Int.down, new Vector3Int(0,0,1), new Vector3Int(0,0,-1) };
    private bool dirty;
    private ExplosionDamage lastDamage;
    public int SupportCheckCount { get; private set; }
    public int LastUnsupportedBlockCount { get; private set; }
    public void MarkDirty() => dirty = true;

    private void LateUpdate()
    {
        if (!dirty) return;
        if (lastDamage.maxActiveDebris == 0)
            lastDamage = new ExplosionDamage { maxActiveDebris = 128, enableDebrisCleanup = true, debrisLifetime = 12 };
        CheckSupport(lastDamage);
    }

    public int CheckSupport(ExplosionDamage damage)
    {
        if (!Application.isPlaying) return 0;
        dirty = false; lastDamage = damage; SupportCheckCount++;
        var grid = new Dictionary<Vector3Int, DestructibleBlock>();
        foreach (var block in GetComponentsInChildren<DestructibleBlock>())
            if (block.Structure == this && !block.IsDetached) grid[block.GridCell] = block;
        var supported = new HashSet<Vector3Int>();
        var queue = new Queue<Vector3Int>();
        foreach (var pair in grid)
            if (pair.Key.y == 0) { supported.Add(pair.Key); queue.Enqueue(pair.Key); }
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var step in neighbours)
            {
                var next = cell + step;
                if (grid.ContainsKey(next) && supported.Add(next)) queue.Enqueue(next);
            }
        }
        var remaining = new HashSet<Vector3Int>(grid.Keys); remaining.ExceptWith(supported);
        LastUnsupportedBlockCount = remaining.Count;
        int collapsed = remaining.Count;
        // One compound Rigidbody per unsupported connected group, not one per cube.
        while (remaining.Count > 0)
        {
            var iterator = remaining.GetEnumerator(); iterator.MoveNext(); Vector3Int first = iterator.Current;
            var group = new List<DestructibleBlock>();
            remaining.Remove(first); queue.Enqueue(first);
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue(); group.Add(grid[cell]);
                foreach (var step in neighbours)
                    if (remaining.Remove(cell + step)) queue.Enqueue(cell + step);
            }
            DestructionSystem.DetachGroup(group, damage, "CollapsedStructure");
        }
        return collapsed;
    }
}
