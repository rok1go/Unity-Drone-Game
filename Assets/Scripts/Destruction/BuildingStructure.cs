using System.Collections.Generic;
using UnityEngine;

// Support model, run after changes only. A block is held up by a chain of blocks to the ground (y=0); resting
// on a block below costs nothing, but every sideways or hanging step along the chain is a span. Each block
// remembers the span it had in the intact building (a floor in the middle of a big room has a long one),
// and gives way once damage makes its shortest span longer than that plus what its material allows.
// So a slab or wall that lost the columns under it comes down locally, and the rest of the building stands.
public class BuildingStructure : MonoBehaviour
{
    private Dictionary<Vector3Int, int> baseline;
    private static readonly Vector3Int[] neighbours = { Vector3Int.left, Vector3Int.right, Vector3Int.up,
        Vector3Int.down, new Vector3Int(0,0,1), new Vector3Int(0,0,-1) };
    private bool dirty;
    private double lastCheckStep = double.NaN;
    private ExplosionDamage lastDamage;
    public int SupportCheckCount { get; private set; }
    public int LastUnsupportedBlockCount { get; private set; }
    public void MarkDirty() => dirty = true;

    private void Start()
    {
        if (baseline == null) baseline = Spans(Grid());
    }

    private Dictionary<Vector3Int, DestructibleBlock> Grid()
    {
        var grid = new Dictionary<Vector3Int, DestructibleBlock>();
        foreach (var block in GetComponentsInChildren<DestructibleBlock>())
            if (block.Structure == this && !block.IsDetached) grid[block.GridCell] = block;
        return grid;
    }

    // Shortest span from the ground for every connected block (0-1 BFS: up is free, other steps cost one).
    private static Dictionary<Vector3Int, int> Spans(Dictionary<Vector3Int, DestructibleBlock> grid)
    {
        var span = new Dictionary<Vector3Int, int>();
        var deque = new LinkedList<Vector3Int>();
        foreach (var pair in grid)
            if (pair.Key.y == 0) { span[pair.Key] = 0; deque.AddLast(pair.Key); }
        while (deque.Count > 0)
        {
            var cell = deque.First.Value; deque.RemoveFirst();
            int here = span[cell];
            foreach (var step in neighbours)
            {
                var next = cell + step;
                if (!grid.ContainsKey(next)) continue;
                int cost = step == Vector3Int.up ? 0 : 1;
                if (span.TryGetValue(next, out int known) && known <= here + cost) continue;
                span[next] = here + cost;
                if (cost == 0) deque.AddFirst(next); else deque.AddLast(next);
            }
        }
        return span;
    }

    private void LateUpdate()
    {
        if (!dirty || lastCheckStep == Time.fixedTimeAsDouble || !DestructionSystem.HasStepCapacity()) return;
        if (lastDamage.maxActiveDebris == 0)
            lastDamage = new ExplosionDamage { maxActiveDebris = 128, enableDebrisCleanup = false };
        CheckSupport(lastDamage);
    }

    public int CheckSupport(ExplosionDamage damage)
    {
        if (!Application.isPlaying) return 0;
        if (!DestructionSystem.Authority) { dirty = false; return 0; } // Collapses arrive from the host.
        lastDamage = damage;
        if (!DestructionSystem.HasStepCapacity())
        {
            dirty = true;
            return 0;
        }
        dirty = false; SupportCheckCount++;
        lastCheckStep = Time.fixedTimeAsDouble;
        var grid = Grid();
        if (baseline == null) baseline = Spans(grid);
        var spans = Spans(grid);
        var supported = new HashSet<Vector3Int>();
        foreach (var pair in spans)
        {
            int original = baseline.TryGetValue(pair.Key, out int b) ? b : pair.Value;
            if (pair.Value <= original + grid[pair.Key].Span) supported.Add(pair.Key);
        }
        // A block can only stand on blocks that stand: drop anything whose every chain runs through a failed one.
        if (supported.Count < spans.Count) supported = Reachable(grid, supported);
        var queue = new Queue<Vector3Int>();
        var remaining = new HashSet<Vector3Int>(grid.Keys); remaining.ExceptWith(supported);
        LastUnsupportedBlockCount = remaining.Count;
        int collapsed = remaining.Count;
        // Falling sections break into pieces of up to 2x2x2 blocks; a big collapse into coarser 4x4x4 ones,
        // so the number of bodies (and the cost) stays bounded.
        int pieceSize = remaining.Count > 160 ? 4 : 2;
        while (remaining.Count > 0)
        {
            var iterator = remaining.GetEnumerator(); iterator.MoveNext(); Vector3Int first = iterator.Current;
            // A falling section comes apart into pieces instead of sliding down as one rigid wall.
            var piece = Piece(first, pieceSize);
            var group = new List<DestructibleBlock>();
            remaining.Remove(first); queue.Enqueue(first);
            while (queue.Count > 0)
            {
                var cell = queue.Dequeue(); group.Add(grid[cell]);
                foreach (var step in neighbours)
                    if (Piece(cell + step, pieceSize) == piece && remaining.Remove(cell + step)) queue.Enqueue(cell + step);
            }
            if (DestructionSystem.DetachGroup(group, damage, "CollapsedStructure") == null)
            {
                dirty = true;
                break;
            }
        }
        return collapsed;
    }

    private static Vector3Int Piece(Vector3Int cell, int size) => new Vector3Int(
        Mathf.FloorToInt(cell.x / (float)size), Mathf.FloorToInt(cell.y / (float)size), Mathf.FloorToInt(cell.z / (float)size));

    // Blocks connected to the ground through blocks that still hold.
    private static HashSet<Vector3Int> Reachable(Dictionary<Vector3Int, DestructibleBlock> grid, HashSet<Vector3Int> holding)
    {
        var reached = new HashSet<Vector3Int>();
        var queue = new Queue<Vector3Int>();
        foreach (var cell in holding)
            if (cell.y == 0) { reached.Add(cell); queue.Enqueue(cell); }
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            foreach (var step in neighbours)
            {
                var next = cell + step;
                if (holding.Contains(next) && reached.Add(next)) queue.Enqueue(next);
            }
        }
        return reached;
    }
}
