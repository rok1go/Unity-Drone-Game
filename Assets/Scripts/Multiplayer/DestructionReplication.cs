using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// Host-authoritative destruction. The host simulates blasts, collapses and impact fractures.
// Clients replay the exact structural result (which blocks became which rubble body) through a
// reliable ordered op stream, and follow the host's rubble poses through unreliable snapshots.
// Every peer therefore has the same holes, the same rubble and the same cover.
public sealed class DestructionReplication
{
    public const string OpsChannel = "hide-and-seek-destruction-v1";
    private const byte OpGroup = 1, OpRemove = 2, OpRest = 3, OpWindow = 4;
    private const int MaxOpsMessageBytes = 48 * 1024;
    private const int DebrisEntryBytes = 4 + 12 + 4;
    private const int DebrisHeaderBytes = 1 + 4 + 8 + 2;
    private const float PositionEpsilon = 0.003f, AngleEpsilon = 0.3f;

    private sealed class Track
    {
        public uint id;
        public Vector3 position;
        public Quaternion rotation;
        public bool moving = true;
    }

    private DestructibleBlock[] blocks = Array.Empty<DestructibleBlock>();
    private readonly Dictionary<DestructibleBlock, int> blockIndex = new Dictionary<DestructibleBlock, int>();
    private BreakableWindow[] windows = Array.Empty<BreakableWindow>();
    private readonly Dictionary<BreakableWindow, int> windowIndex = new Dictionary<BreakableWindow, int>();
    // Host: simulated rubble bodies. Client: kinematic replicas.
    private readonly Dictionary<DestructionDebris, Track> tracks = new Dictionary<DestructionDebris, Track>();
    private readonly Dictionary<uint, ReplicatedDebris> replicas = new Dictionary<uint, ReplicatedDebris>();
    private readonly List<byte[]> pendingOps = new List<byte[]>();
    private readonly List<DestructionDebris> stale = new List<DestructionDebris>();
    private readonly List<(uint id, Vector3 position, Quaternion rotation)> moving =
        new List<(uint, Vector3, Quaternion)>();
    private readonly StreamClock debrisClock = new StreamClock();
    private uint nextChunkId = 1;
    private bool applyingRemote;
    private int blastSoundFrame = -1;

    public bool Active { get; private set; }
    public bool Hosting { get; private set; }
    public string MapHash { get; private set; } = "";
    public event Action<int> WindowRequested;

    public DestructionReplication()
    {
        DestructionSystem.ChunkCreated += OnChunkCreated;
        DestructionSystem.BlocksRemoved += OnBlocksRemoved;
        BreakableWindow.Shattered += OnWindowShattered;
    }

    public void Dispose()
    {
        DestructionSystem.ChunkCreated -= OnChunkCreated;
        DestructionSystem.BlocksRemoved -= OnBlocksRemoved;
        BreakableWindow.Shattered -= OnWindowShattered;
        End();
    }

    // Indexes every destructible in the loaded map in a peer-independent order. Integer indices
    // replace string ids on the wire, and the hash proves all peers loaded the same map.
    public string BuildIndex()
    {
        End();
        blockIndex.Clear(); windowIndex.Clear();
        var foundBlocks = new List<DestructibleBlock>();
        var seen = new HashSet<string>();
        foreach (var block in UnityEngine.Object.FindObjectsByType<DestructibleBlock>(FindObjectsInactive.Include))
            if (!string.IsNullOrEmpty(block.StableId) && !block.IsDetached) foundBlocks.Add(block);
        foundBlocks.Sort((a, b) => string.CompareOrdinal(a.StableId, b.StableId));
        var uniqueBlocks = new List<DestructibleBlock>(foundBlocks.Count);
        foreach (var block in foundBlocks)
        {
            if (seen.Add(block.StableId)) uniqueBlocks.Add(block);
            else Debug.LogWarning("Duplicate destructible id is not replicated: " + block.StableId, block);
        }
        blocks = uniqueBlocks.ToArray();
        for (int i = 0; i < blocks.Length; i++) blockIndex[blocks[i]] = i;

        var foundWindows = new List<(string key, BreakableWindow window)>();
        foreach (var window in UnityEngine.Object.FindObjectsByType<BreakableWindow>(FindObjectsInactive.Include))
            foundWindows.Add((WindowKey(window), window));
        foundWindows.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        windows = new BreakableWindow[foundWindows.Count];
        for (int i = 0; i < windows.Length; i++)
        {
            windows[i] = foundWindows[i].window;
            windowIndex[windows[i]] = i;
        }

        ulong hash = 14695981039346656037UL;
        void Mix(string value)
        {
            foreach (char c in value) { hash ^= c; hash *= 1099511628211UL; }
            hash ^= 0xff; hash *= 1099511628211UL;
        }
        Mix(blocks.Length.ToString());
        foreach (var block in blocks) Mix(block.StableId);
        Mix(windows.Length.ToString());
        foreach (var pair in foundWindows) Mix(pair.key);
        MapHash = hash.ToString("x16");
        return MapHash;
    }

    private static string WindowKey(BreakableWindow window)
    {
        Vector3 p = window.transform.position;
        return $"{window.name}|{Mathf.RoundToInt(p.x * 100f)},{Mathf.RoundToInt(p.y * 100f)},{Mathf.RoundToInt(p.z * 100f)}";
    }

    public void Begin(bool hosting)
    {
        End();
        Active = true;
        Hosting = hosting;
    }

    public void End()
    {
        Active = false;
        Hosting = false;
        tracks.Clear(); replicas.Clear(); pendingOps.Clear();
        nextChunkId = 1;
        debrisClock.Reset();
    }

    // ---------------- Host: capture ----------------

    private void OnChunkCreated(DestructionDebris chunk, IReadOnlyList<DestructibleBlock> members)
    {
        if (!Active || !Hosting || chunk == null) return;
        uint id = nextChunkId++;
        tracks[chunk] = new Track { id = id, position = chunk.transform.position, rotation = chunk.transform.rotation };
        int count = 0;
        foreach (var block in members) if (block != null && blockIndex.ContainsKey(block)) count++;
        if (count == 0) return;
        // Split fragments come from moving rubble, whose pose differs slightly between peers.
        // Sending it pins the block's offset inside the new body exactly. Static blocks match already.
        bool withPose = members.Count == 1;
        var writer = new FastBufferWriter(32 + count * 32, Allocator.Temp, 1 << 20);
        try
        {
            writer.WriteValueSafe(OpGroup);
            writer.WriteValueSafe(id);
            writer.WriteValueSafe(chunk.transform.position);
            writer.WriteValueSafe(withPose);
            writer.WriteValueSafe(count);
            foreach (var block in members)
            {
                if (block == null || !blockIndex.TryGetValue(block, out int index)) continue;
                writer.WriteValueSafe(index);
                if (!withPose) continue;
                writer.WriteValueSafe(block.transform.position);
                writer.WriteValueSafe(block.transform.rotation);
            }
            pendingOps.Add(writer.ToArray());
        }
        finally { writer.Dispose(); }
    }

    private void OnBlocksRemoved(DestructionDebris chunk, IReadOnlyList<DestructibleBlock> removed)
    {
        if (!Active || !Hosting) return;
        var indices = new List<int>(removed.Count);
        foreach (var block in removed)
            if (block != null && blockIndex.TryGetValue(block, out int index)) indices.Add(index);
        if (indices.Count == 0) return;
        var writer = new FastBufferWriter(8 + indices.Count * 4, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(OpRemove);
            writer.WriteValueSafe(indices.Count);
            foreach (int index in indices) writer.WriteValueSafe(index);
            pendingOps.Add(writer.ToArray());
        }
        finally { writer.Dispose(); }
    }

    private void OnWindowShattered(BreakableWindow window, Vector3 source, float speed, bool fromBlast)
    {
        if (!Active || applyingRemote || window == null || !windowIndex.TryGetValue(window, out int index)) return;
        if (!Hosting)
        {
            // A client break is a prediction; the host validates it and replicates it to everyone.
            if (!fromBlast) WindowRequested?.Invoke(index);
            return;
        }
        var writer = new FastBufferWriter(32, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(OpWindow);
            writer.WriteValueSafe(index);
            writer.WriteValueSafe(source);
            writer.WriteValueSafe(speed);
            writer.WriteValueSafe(fromBlast);
            pendingOps.Add(writer.ToArray());
        }
        finally { writer.Dispose(); }
    }

    public bool TryGetWindowPosition(int index, out Vector3 position)
    {
        position = default;
        if (index < 0 || index >= windows.Length || windows[index] == null) return false;
        position = windows[index].transform.position;
        return true;
    }

    public void HostShatterWindow(int index, Vector3 source)
    {
        if (!Hosting || index < 0 || index >= windows.Length || windows[index] == null) return;
        if (windows[index].Shatter(source, 3.2f)) windows[index].PlayBreakSound(false);
    }

    // ---------------- Host: send ----------------

    public void CollectDebrisSnapshots(int generation, double time, List<byte[]> packets)
    {
        if (!Active || !Hosting) return;
        stale.Clear();
        moving.Clear();
        foreach (var pair in tracks)
        {
            var chunk = pair.Key;
            var track = pair.Value;
            if (chunk == null || chunk.Body == null) { stale.Add(chunk); continue; }
            var body = chunk.Body;
            Vector3 position = body.position;
            Quaternion rotation = body.rotation;
            if (!body.IsSleeping())
            {
                track.moving = true;
                if ((position - track.position).sqrMagnitude < PositionEpsilon * PositionEpsilon &&
                    Quaternion.Angle(rotation, track.rotation) < AngleEpsilon) continue;
                track.position = position; track.rotation = rotation;
                moving.Add((track.id, position, rotation));
            }
            else if (track.moving)
            {
                // Settled rubble gets one reliable, exact final pose; it then costs no bandwidth.
                track.moving = false;
                track.position = position; track.rotation = rotation;
                QueueRest(track.id, time, position, rotation);
            }
        }
        foreach (var chunk in stale) tracks.Remove(chunk);

        int perPacket = (NetSync.MaxUnreliableBytes - DebrisHeaderBytes) / DebrisEntryBytes;
        for (int start = 0; start < moving.Count; start += perPacket)
        {
            int count = Mathf.Min(perPacket, moving.Count - start);
            var writer = new FastBufferWriter(DebrisHeaderBytes + count * DebrisEntryBytes, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(NetSync.DebrisSnapshot);
                writer.WriteValueSafe(generation);
                writer.WriteValueSafe(time);
                writer.WriteValueSafe((ushort)count);
                for (int i = start; i < start + count; i++)
                {
                    writer.WriteValueSafe(moving[i].id);
                    writer.WriteValueSafe(moving[i].position);
                    writer.WriteValueSafe(NetSync.PackRotation(moving[i].rotation));
                }
                packets.Add(writer.ToArray());
            }
            finally { writer.Dispose(); }
        }
    }

    private void QueueRest(uint id, double time, Vector3 position, Quaternion rotation)
    {
        var writer = new FastBufferWriter(48, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(OpRest);
            writer.WriteValueSafe(id);
            writer.WriteValueSafe(time);
            writer.WriteValueSafe(position);
            writer.WriteValueSafe(rotation);
            pendingOps.Add(writer.ToArray());
        }
        finally { writer.Dispose(); }
    }

    public void CollectOpsMessages(int generation, List<byte[]> messages)
    {
        if (!Active || !Hosting || pendingOps.Count == 0) return;
        int index = 0;
        while (index < pendingOps.Count)
        {
            int start = index, size = 0;
            while (index < pendingOps.Count && (index == start || size + pendingOps[index].Length <= MaxOpsMessageBytes))
                size += pendingOps[index++].Length;
            var writer = new FastBufferWriter(size + 8, Allocator.Temp);
            try
            {
                writer.WriteValueSafe(generation);
                writer.WriteValueSafe((ushort)(index - start));
                for (int i = start; i < index; i++) writer.WriteBytesSafe(pendingOps[i]);
                messages.Add(writer.ToArray());
            }
            finally { writer.Dispose(); }
        }
        pendingOps.Clear();
    }

    // ---------------- Client: apply ----------------

    public void ApplyOps(FastBufferReader reader, int generation)
    {
        reader.ReadValueSafe(out int messageGeneration);
        if (!Active || Hosting || messageGeneration != generation) return;
        reader.ReadValueSafe(out ushort count);
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out byte op);
            switch (op)
            {
                case OpGroup: ApplyGroup(reader); break;
                case OpRemove: ApplyRemove(reader); break;
                case OpRest: ApplyRest(reader); break;
                case OpWindow: ApplyWindow(reader); break;
                default: Debug.LogWarning("Unknown destruction op " + op); return; // Stream is no longer parseable.
            }
        }
    }

    private void ApplyGroup(FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint id);
        reader.ReadValueSafe(out Vector3 root);
        reader.ReadValueSafe(out bool withPose);
        reader.ReadValueSafe(out int count);
        if (count < 0 || count > blocks.Length) throw new InvalidOperationException("Invalid destruction group.");
        var members = new List<DestructibleBlock>(count);
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out int index);
            Vector3 position = default;
            Quaternion rotation = Quaternion.identity;
            if (withPose) { reader.ReadValueSafe(out position); reader.ReadValueSafe(out rotation); }
            var block = index >= 0 && index < blocks.Length ? blocks[index] : null;
            if (block == null) continue;
            if (block.IsDetached)
            {
                var source = block.GetComponentInParent<DestructionDebris>();
                if (source != null) source.Release(block, out _);
            }
            if (withPose) block.transform.SetPositionAndRotation(position, rotation);
            members.Add(block);
        }
        if (members.Count == 0) return;
        var chunk = DestructionSystem.CreateReplicaChunk(members, root);
        var replica = chunk.gameObject.AddComponent<ReplicatedDebris>();
        replica.Initialize(debrisClock);
        replicas[id] = replica;
    }

    private void ApplyRemove(FastBufferReader reader)
    {
        reader.ReadValueSafe(out int count);
        if (count < 0 || count > blocks.Length) throw new InvalidOperationException("Invalid removal op.");
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out int index);
            if (index < 0 || index >= blocks.Length || blocks[index] == null) continue;
            var block = blocks[index];
            blocks[index] = null;
            var chunk = block.GetComponentInParent<DestructionDebris>();
            if (chunk != null) chunk.RemoveReplicatedBlock(block);
            else { block.gameObject.SetActive(false); UnityEngine.Object.Destroy(block.gameObject); }
        }
    }

    private void ApplyRest(FastBufferReader reader)
    {
        reader.ReadValueSafe(out uint id);
        reader.ReadValueSafe(out double time);
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out Quaternion rotation);
        if (replicas.TryGetValue(id, out var replica) && replica != null) replica.Rest(time, position, rotation);
    }

    private void ApplyWindow(FastBufferReader reader)
    {
        reader.ReadValueSafe(out int index);
        reader.ReadValueSafe(out Vector3 source);
        reader.ReadValueSafe(out float speed);
        reader.ReadValueSafe(out bool fromBlast);
        if (index < 0 || index >= windows.Length || windows[index] == null) return;
        var window = windows[index];
        applyingRemote = true;
        try
        {
            // A locally predicted break returns false here, so effects never play twice.
            if (!window.Shatter(source, speed, fromBlast)) return;
            if (!fromBlast) window.PlayBreakSound(false);
            else if (blastSoundFrame != Time.frameCount)
            {
                blastSoundFrame = Time.frameCount;
                window.PlayBreakSound(true);
            }
        }
        finally { applyingRemote = false; }
    }

    public void ReadDebrisSnapshot(FastBufferReader reader)
    {
        reader.ReadValueSafe(out double time);
        reader.ReadValueSafe(out ushort count);
        double now = NetSync.Now;
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out uint id);
            reader.ReadValueSafe(out Vector3 position);
            reader.ReadValueSafe(out uint rotation);
            if (!Active || Hosting || !replicas.TryGetValue(id, out var replica) || replica == null) continue;
            replica.Push(new NetPose { Time = time, Position = position, Rotation = NetSync.UnpackRotation(rotation) }, now);
        }
    }
}

// Client-side rubble body: kinematic, so it still blocks players and provides cover,
// but its motion comes from the host's simulation instead of a divergent local one.
[DisallowMultipleComponent]
public sealed class ReplicatedDebris : MonoBehaviour
{
    private PoseBuffer buffer;
    private double restTime = double.NegativeInfinity;

    public void Initialize(StreamClock clock)
    {
        buffer = new PoseBuffer(clock);
        enabled = false;
    }

    public void Push(in NetPose pose, double now)
    {
        if (buffer == null || pose.Time <= restTime) return;
        if (buffer.Push(pose, now)) enabled = true;
    }

    public void Rest(double time, Vector3 position, Quaternion rotation)
    {
        // Ignore a reliable rest pose that arrives after newer motion (the rubble was hit again).
        if (buffer == null || buffer.NewestTime > time) return;
        restTime = time;
        buffer.Clear();
        transform.SetPositionAndRotation(position, rotation);
        enabled = false;
    }

    private void Update()
    {
        if (buffer == null) { enabled = false; return; }
        double renderTime = buffer.Clock.RenderTime(NetSync.Now);
        if (!buffer.Sample(renderTime, out var pose, out _)) { enabled = false; return; }
        transform.SetPositionAndRotation(pose.Position, pose.Rotation);
        // Hold the last pose; the reliable rest op finalizes it, and new snapshots re-enable this.
        if (renderTime > buffer.NewestTime + 1.0) enabled = false;
    }
}
