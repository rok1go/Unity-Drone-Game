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
    private const byte OpGroup = 1, OpRemove = 2, OpRest = 3, OpWindow = 4, OpDoor = 5, OpPropBreak = 6, OpShatter = 7, OpPhysicsSound = 8,
        OpStow = 9;
    // Props share the rubble snapshot stream; their ids carry this bit so they never clash with chunk ids.
    public const uint PropFlag = 0x80000000u;
    // Door leaves torn off by a blast: DoorFlag | door index. Chunk ids count up from 1 and never reach it.
    public const uint DoorFlag = 0x40000000u;
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
    private InteractiveDoor[] doors = Array.Empty<InteractiveDoor>();
    private readonly Dictionary<InteractiveDoor, int> doorIndex = new Dictionary<InteractiveDoor, int>();
    // Host: simulated rubble bodies. Client: kinematic replicas.
    private PhysicsProp[] props = Array.Empty<PhysicsProp>();
    private readonly Dictionary<PhysicsProp, int> propIndex = new Dictionary<PhysicsProp, int>();
    // Host: every simulated body that clients follow (rubble and props), and its id.
    private readonly Dictionary<Rigidbody, Track> tracks = new Dictionary<Rigidbody, Track>();
    private readonly Dictionary<uint, Rigidbody> bodies = new Dictionary<uint, Rigidbody>();
    private readonly Dictionary<uint, ReplicatedDebris> replicas = new Dictionary<uint, ReplicatedDebris>();
    private readonly List<byte[]> pendingOps = new List<byte[]>();
    private readonly List<Rigidbody> stale = new List<Rigidbody>();
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
    public event Action<int, InteractiveDoor.DoorState> DoorRequested;

    public DestructionReplication()
    {
        GameAudio.PhysicsPlayed += OnPhysicsSound;
        DestructionSystem.ChunkCreated += OnChunkCreated;
        DestructionSystem.BlocksRemoved += OnBlocksRemoved;
        DestructionSystem.BlocksShattered += OnBlocksShattered;
        BreakableWindow.Shattered += OnWindowShattered;
        InteractiveDoor.Changed += OnDoorChanged;
        PhysicsProp.Broken += OnPropBroken;
        PhysicsProp.StowChanged += OnPropStowed;
    }

    public void Dispose()
    {
        GameAudio.PhysicsPlayed -= OnPhysicsSound;
        DestructionSystem.ChunkCreated -= OnChunkCreated;
        DestructionSystem.BlocksRemoved -= OnBlocksRemoved;
        DestructionSystem.BlocksShattered -= OnBlocksShattered;
        BreakableWindow.Shattered -= OnWindowShattered;
        InteractiveDoor.Changed -= OnDoorChanged;
        PhysicsProp.Broken -= OnPropBroken;
        PhysicsProp.StowChanged -= OnPropStowed;
        End();
    }

    // Indexes every destructible in the loaded map in a peer-independent order. Integer indices
    // replace string ids on the wire, and the hash proves all peers loaded the same map.
    public string BuildIndex(Transform root = null)
    {
        End();
        blockIndex.Clear(); windowIndex.Clear(); doorIndex.Clear(); propIndex.Clear();
        var foundBlocks = new List<DestructibleBlock>();
        var seen = new HashSet<string>();
        foreach (var block in UnityEngine.Object.FindObjectsByType<DestructibleBlock>(FindObjectsInactive.Include))
            if (!string.IsNullOrEmpty(block.StableId) && !block.IsDetached && InRoot(block.transform, root))
                foundBlocks.Add(block);
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
            if (InRoot(window.transform, root)) foundWindows.Add((WindowKey(window), window));
        foundWindows.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        windows = new BreakableWindow[foundWindows.Count];
        for (int i = 0; i < windows.Length; i++)
        {
            windows[i] = foundWindows[i].window;
            windowIndex[windows[i]] = i;
        }

        var foundDoors = new List<(string key, InteractiveDoor door)>();
        foreach (var door in InteractiveDoor.All)
            if (door != null && InRoot(door.transform, root)) foundDoors.Add((DoorKey(door), door));
        foundDoors.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        doors = new InteractiveDoor[foundDoors.Count];
        for (int i = 0; i < doors.Length; i++)
        {
            doors[i] = foundDoors[i].door;
            doorIndex[doors[i]] = i;
        }

        var foundProps = new List<(string key, PhysicsProp prop)>();
        foreach (var prop in PhysicsProp.All)
            if (prop != null && InRoot(prop.transform, root)) foundProps.Add((PropKey(prop), prop));
        foundProps.Sort((a, b) => string.CompareOrdinal(a.key, b.key));
        props = new PhysicsProp[foundProps.Count];
        for (int i = 0; i < props.Length; i++)
        {
            props[i] = foundProps[i].prop;
            propIndex[props[i]] = i;
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
        Mix(doors.Length.ToString());
        foreach (var pair in foundDoors) Mix(pair.key);
        Mix(props.Length.ToString());
        foreach (var pair in foundProps) Mix(pair.key);
        if (PowerSystem.Instance != null && InRoot(PowerSystem.Instance.transform, root))
        {
            var devices = PowerSystem.Instance.GetComponentsInChildren<PoweredDevice>();
            Array.Sort(devices, (a, b) => a.Index.CompareTo(b.Index));
            Mix("power/" + devices.Length);
            foreach (var device in devices)
                Mix(device.Index + "/" + device.Building + "/" + device.transform.position.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }
        MapHash = hash.ToString("x16");
        return MapHash;
    }

    // Only the playable map is indexed, so a map being torn down this frame cannot skew indices.
    private static bool InRoot(Transform item, Transform root) => root == null || item.IsChildOf(root);

    private static string WindowKey(BreakableWindow window)
    {
        Vector3 p = window.transform.position;
        return $"{window.name}|{Mathf.RoundToInt(p.x * 100f)},{Mathf.RoundToInt(p.y * 100f)},{Mathf.RoundToInt(p.z * 100f)}";
    }

    private static string DoorKey(InteractiveDoor door)
    {
        Vector3 p = door.transform.position;
        return $"{door.name}|{Mathf.RoundToInt(p.x * 100f)},{Mathf.RoundToInt(p.y * 100f)},{Mathf.RoundToInt(p.z * 100f)}";
    }

    private static string PropKey(PhysicsProp prop)
    {
        Vector3 p = prop.transform.position;
        return $"{prop.name}|{Mathf.RoundToInt(p.x * 100f)},{Mathf.RoundToInt(p.y * 100f)},{Mathf.RoundToInt(p.z * 100f)}";
    }

    public void Begin(bool hosting)
    {
        End();
        Active = true;
        Hosting = hosting;
        for (int i = 0; i < props.Length; i++)
        {
            var prop = props[i];
            if (prop == null) continue;
            prop.ResetToSpawn();
            uint id = PropFlag | (uint)i;
            var body = prop.Body;
            if (hosting)
            {
                tracks[body] = new Track { id = id, position = body.position, rotation = body.rotation, moving = false };
                bodies[id] = body;
            }
            else
            {
                // Clients show the host's simulation of every prop.
                body.isKinematic = true;
                body.interpolation = RigidbodyInterpolation.None;
                if (!prop.TryGetComponent(out ReplicatedDebris replica)) replica = prop.gameObject.AddComponent<ReplicatedDebris>();
                replica.Initialize(debrisClock);
                replica.Id = id;
                replicas[id] = replica;
            }
        }
    }

    // Ids that name a carryable body the same way on every peer.
    public bool TryGetId(Rigidbody body, out uint id)
    {
        id = 0;
        if (body == null) return false;
        var prop = body.GetComponent<PhysicsProp>();
        if (prop != null && propIndex.TryGetValue(prop, out int index)) { id = PropFlag | (uint)index; return true; }
        if (Hosting && tracks.TryGetValue(body, out var track)) { id = track.id; return true; }
        var replica = body.GetComponent<ReplicatedDebris>();
        if (replica != null && replica.Id != 0) { id = replica.Id; return true; }
        return false;
    }

    public Rigidbody Resolve(uint id)
    {
        if ((id & PropFlag) != 0)
        {
            int index = (int)(id & ~PropFlag);
            return index < props.Length && props[index] != null ? props[index].Body : null;
        }
        if (Hosting) return bodies.TryGetValue(id, out var body) ? body : null;
        return replicas.TryGetValue(id, out var replica) && replica != null ? replica.GetComponent<Rigidbody>() : null;
    }

    public void End()
    {
        Active = false;
        Hosting = false;
        tracks.Clear(); bodies.Clear(); replicas.Clear(); pendingOps.Clear();
        nextChunkId = 1;
        debrisClock.Reset();
    }

    // ---------------- Host: capture ----------------

    private void OnChunkCreated(DestructionDebris chunk, IReadOnlyList<DestructibleBlock> members)
    {
        if (!Active || !Hosting || chunk == null) return;
        uint id = nextChunkId++;
        tracks[chunk.Body] = new Track { id = id, position = chunk.transform.position, rotation = chunk.transform.rotation };
        bodies[id] = chunk.Body;
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

    private void OnBlocksShattered(IReadOnlyList<DestructibleBlock> shattered, Vector3 blast, float impulse)
    {
        if (!Active || !Hosting) return;
        var indices = new List<int>(shattered.Count);
        foreach (var block in shattered)
            if (block != null && blockIndex.TryGetValue(block, out int index)) indices.Add(index);
        if (indices.Count == 0) return;
        var writer = new FastBufferWriter(24 + indices.Count * 4, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(OpShatter);
            writer.WriteValueSafe(blast);
            writer.WriteValueSafe(impulse);
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

    private void OnDoorChanged(InteractiveDoor door, InteractiveDoor.DoorState state)
    {
        if (!Active || applyingRemote || door == null || !doorIndex.TryGetValue(door, out int index)) return;
        if (!Hosting)
        {
            // Opening is predicted locally; only the host may tear a door off.
            if (state != InteractiveDoor.DoorState.Broken) DoorRequested?.Invoke(index, state);
            return;
        }
        if (state == InteractiveDoor.DoorState.Broken && door.BrokenBody != null)
        {
            // The leaf is now simulated here and followed by clients like any rubble body.
            uint id = DoorFlag | (uint)index;
            var body = door.BrokenBody;
            tracks[body] = new Track { id = id, position = body.position, rotation = body.rotation };
            bodies[id] = body;
        }
        QueueDoor(index, state);
    }

    private void QueueDoor(int index, InteractiveDoor.DoorState state)
    {
        var writer = new FastBufferWriter(8, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(OpDoor);
            writer.WriteValueSafe(index);
            writer.WriteValueSafe((sbyte)state);
            pendingOps.Add(writer.ToArray());
        }
        finally { writer.Dispose(); }
    }

    public bool TryGetDoorPosition(int index, out Vector3 position)
    {
        position = default;
        if (index < 0 || index >= doors.Length || doors[index] == null) return false;
        position = doors[index].transform.position;
        return true;
    }

    // A remote player's door request, checked against where they stand (a lock works only from inside).
    // A refused request re-sends the true state, which undoes the client's prediction.
    public void HostSetDoor(int index, InteractiveDoor.DoorState state, Vector3 user)
    {
        if (!Hosting || index < 0 || index >= doors.Length || doors[index] == null) return;
        var door = doors[index];
        if (door.Allows(state, user)) door.SetState(state);
        else QueueDoor(index, door.State);
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
            var body = pair.Key;
            var track = pair.Value;
            if (body == null) { stale.Add(body); continue; }
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
        foreach (var body in stale)
        {
            if (tracks.TryGetValue(body, out var gone)) bodies.Remove(gone.id);
            tracks.Remove(body);
        }

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
                case OpDoor: ApplyDoor(reader); break;
                case OpPropBreak: ApplyPropBreak(reader); break;
                case OpShatter: ApplyShatter(reader); break;
                case OpPhysicsSound: ApplyPhysicsSound(reader); break;
                case OpStow: ApplyStow(reader); break;
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
        replica.Id = id;
        replicas[id] = replica;
    }

    private void OnPhysicsSound(Vector3 position, SoundMaterial material, float volume, float pitch, bool sliding, bool heavy)
    {
        if (!Active || !Hosting) return;
        var writer = new FastBufferWriter(32, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(OpPhysicsSound); writer.WriteValueSafe(position);
            writer.WriteValueSafe((byte)material); writer.WriteValueSafe(volume); writer.WriteValueSafe(pitch);
            writer.WriteValueSafe(sliding); writer.WriteValueSafe(heavy); pendingOps.Add(writer.ToArray());
        }
        finally { writer.Dispose(); }
    }
    private void ApplyPhysicsSound(FastBufferReader reader)
    {
        reader.ReadValueSafe(out Vector3 position); reader.ReadValueSafe(out byte material);
        reader.ReadValueSafe(out float volume); reader.ReadValueSafe(out float pitch);
        reader.ReadValueSafe(out bool sliding); reader.ReadValueSafe(out bool heavy);
        GameAudio.ReplayPhysics(position, (SoundMaterial)material, volume, pitch, sliding, heavy);
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

    private void ApplyShatter(FastBufferReader reader)
    {
        reader.ReadValueSafe(out Vector3 blast);
        reader.ReadValueSafe(out float impulse);
        reader.ReadValueSafe(out int count);
        if (count < 0 || count > blocks.Length) throw new InvalidOperationException("Invalid shatter op.");
        var shattered = new List<DestructibleBlock>(count);
        for (int i = 0; i < count; i++)
        {
            reader.ReadValueSafe(out int index);
            if (index < 0 || index >= blocks.Length || blocks[index] == null) continue;
            var block = blocks[index];
            blocks[index] = null;
            // The host only shatters blocks still standing, and so does this replay.
            shattered.Add(block);
            if (!block.IsDetached) DestructionSystem.ShatterLocal(block, blast, impulse);
        }
        if (count > 0) DestructionSystem.ShatterEffects(blast, count, shattered);
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

    private void OnPropBroken(PhysicsProp prop)
    {
        if (!Active || !Hosting || prop == null || !propIndex.TryGetValue(prop, out int index)) return;
        var writer = new FastBufferWriter(8, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(OpPropBreak);
            writer.WriteValueSafe(index);
            pendingOps.Add(writer.ToArray());
        }
        finally { writer.Dispose(); }
    }

    // Host: an item went into a pocket (switched off everywhere) or came out at a pose with a velocity.
    private void OnPropStowed(PhysicsProp prop)
    {
        if (!Active || !Hosting || prop == null || !propIndex.TryGetValue(prop, out int index)) return;
        var writer = new FastBufferWriter(64, Allocator.Temp);
        try
        {
            writer.WriteValueSafe(OpStow);
            writer.WriteValueSafe(index);
            writer.WriteValueSafe(prop.Stowed);
            writer.WriteValueSafe(prop.transform.position);
            writer.WriteValueSafe(prop.transform.rotation);
            pendingOps.Add(writer.ToArray());
        }
        finally { writer.Dispose(); }
        // Its flight is followed through the rubble snapshots like any prop.
        if (!prop.Stowed && tracks.TryGetValue(prop.Body, out var track)) track.moving = true;
    }

    private void ApplyStow(FastBufferReader reader)
    {
        reader.ReadValueSafe(out int index);
        reader.ReadValueSafe(out bool stowed);
        reader.ReadValueSafe(out Vector3 position);
        reader.ReadValueSafe(out Quaternion rotation);
        if (index < 0 || index >= props.Length || props[index] == null) return;
        props[index].SetStowed(stowed, position, rotation, Vector3.zero, false);
    }

    private void ApplyPropBreak(FastBufferReader reader)
    {
        reader.ReadValueSafe(out int index);
        if (index >= 0 && index < props.Length && props[index] != null) props[index].BreakLocal();
    }

    private void ApplyDoor(FastBufferReader reader)
    {
        reader.ReadValueSafe(out int index);
        reader.ReadValueSafe(out sbyte state);
        if (index < 0 || index >= doors.Length || doors[index] == null) return;
        var door = doors[index];
        applyingRemote = true;
        try { door.SetState((InteractiveDoor.DoorState)state); }
        finally { applyingRemote = false; }
        if (door.BrokenBody != null && !replicas.ContainsKey(DoorFlag | (uint)index))
        {
            // The torn-off leaf follows the host's simulation, like rubble.
            var body = door.BrokenBody;
            body.isKinematic = true;
            body.interpolation = RigidbodyInterpolation.None;
            var replica = body.gameObject.AddComponent<ReplicatedDebris>();
            replica.Initialize(debrisClock);
            replica.Id = DoorFlag | (uint)index;
            replicas[replica.Id] = replica;
        }
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
    public uint Id { get; set; }
    private Rigidbody body;

    public void Initialize(StreamClock clock)
    {
        buffer = new PoseBuffer(clock);
        enabled = false;
        body = GetComponent<Rigidbody>();
        if (body != null) body.interpolation = RigidbodyInterpolation.Interpolate;
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

    private void FixedUpdate()
    {
        if (buffer == null) { enabled = false; return; }
        double renderTime = buffer.Clock.RenderTime(NetSync.Now);
        if (!buffer.Sample(renderTime, out var pose, out _)) { enabled = false; return; }
        if (body != null) { body.MovePosition(pose.Position); body.MoveRotation(pose.Rotation); }
        // Physics foley is replayed from the host's contact events, never guessed from interpolation.
        // Hold the last pose; the reliable rest op finalizes it, and new snapshots re-enable this.
        if (renderTime > buffer.NewestTime + 1.0) enabled = false;
    }

}
