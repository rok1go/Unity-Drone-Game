using System;
using Unity.Netcode;
using UnityEngine;

// Compact binary snapshots for high-rate data. Low-rate control stays in JSON messages.
public struct NetPose
{
    public const byte CrouchingFlag = 1, MotorsFlag = 2, AcroFlag = 4;
    // Sender-local timestamp. Receivers map it onto their own clock with StreamClock.
    public double Time;
    public Vector3 Position;
    public Quaternion Rotation, View;
    public byte Flags, Throttle;
    public float FlightSeconds;
}

public static class NetSync
{
    public const string SnapshotChannel = "hide-and-seek-snapshot-v1";
    public const byte ActorSnapshot = 1, OwnerPose = 2, DebrisSnapshot = 3;
    public const float SendInterval = 0.05f;          // 20 Hz
    public const double InterpolationDelay = 0.1;     // two send intervals absorb jitter and one lost packet
    // Unreliable packets must fit one transport datagram, including Netcode's message header.
    public const int MaxUnreliableBytes = 1100;

    public static double Now => Time.realtimeSinceStartupAsDouble;

    public static void WritePose(FastBufferWriter writer, in NetPose pose)
    {
        writer.WriteValueSafe(pose.Time);
        writer.WriteValueSafe(pose.Position);
        writer.WriteValueSafe(PackRotation(pose.Rotation));
        writer.WriteValueSafe(PackRotation(pose.View));
        writer.WriteValueSafe(pose.Flags);
        writer.WriteValueSafe(pose.Throttle);
        writer.WriteValueSafe(pose.FlightSeconds);
    }

    public static NetPose ReadPose(FastBufferReader reader)
    {
        var pose = new NetPose();
        reader.ReadValueSafe(out pose.Time);
        reader.ReadValueSafe(out pose.Position);
        reader.ReadValueSafe(out uint rotation); pose.Rotation = UnpackRotation(rotation);
        reader.ReadValueSafe(out uint view); pose.View = UnpackRotation(view);
        reader.ReadValueSafe(out pose.Flags);
        reader.ReadValueSafe(out pose.Throttle);
        reader.ReadValueSafe(out pose.FlightSeconds);
        return pose;
    }

    // Smallest-three encoding: 2 bits for the dropped component, 10 bits for each other one (~0.1° error).
    private const float ComponentRange = 0.70710678f;

    public static uint PackRotation(Quaternion q)
    {
        float length = Mathf.Sqrt(q.x * q.x + q.y * q.y + q.z * q.z + q.w * q.w);
        if (length < 0.0001f || float.IsNaN(length)) q = Quaternion.identity;
        else { q.x /= length; q.y /= length; q.z /= length; q.w /= length; }
        int largest = 0;
        float max = Mathf.Abs(q.x);
        for (int i = 1; i < 4; i++)
        {
            float value = Mathf.Abs(q[i]);
            if (value > max) { max = value; largest = i; }
        }
        float sign = q[largest] < 0f ? -1f : 1f;
        uint packed = (uint)largest << 30;
        int shift = 20;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            float normalized = (q[i] * sign / ComponentRange + 1f) * 0.5f;
            packed |= (uint)Mathf.Clamp(Mathf.RoundToInt(normalized * 1023f), 0, 1023) << shift;
            shift -= 10;
        }
        return packed;
    }

    public static Quaternion UnpackRotation(uint packed)
    {
        int largest = (int)(packed >> 30);
        var q = new Quaternion();
        float sum = 0f;
        int shift = 20;
        for (int i = 0; i < 4; i++)
        {
            if (i == largest) continue;
            float value = (((packed >> shift) & 1023u) / 1023f * 2f - 1f) * ComponentRange;
            q[i] = value;
            sum += value * value;
            shift -= 10;
        }
        q[largest] = Mathf.Sqrt(Mathf.Max(0f, 1f - sum));
        return q;
    }

    public static bool Finite(Vector3 v) =>
        !float.IsNaN(v.x) && !float.IsNaN(v.y) && !float.IsNaN(v.z) &&
        !float.IsInfinity(v.x) && !float.IsInfinity(v.y) && !float.IsInfinity(v.z);
}

// Maps a remote sender's clock onto the local one. The minimum observed offset approximates the
// fastest delivery; rendering InterpolationDelay behind it keeps a full pair of snapshots in the buffer.
public sealed class StreamClock
{
    private double offset;
    private bool initialized;

    public void Observe(double remoteTime, double localTime)
    {
        double sample = localTime - remoteTime;
        if (!initialized || sample < offset) { offset = sample; initialized = true; }
        else offset += (sample - offset) * 0.002; // Slow upward drift recovers from a route change.
    }

    public double RenderTime(double localTime) => localTime - offset - NetSync.InterpolationDelay;
    public void Reset() => initialized = false;
}

public sealed class PoseBuffer
{
    private readonly NetPose[] items = new NetPose[10];
    private readonly StreamClock clock;
    private int count;

    public PoseBuffer(StreamClock sharedClock = null) => clock = sharedClock ?? new StreamClock();
    public StreamClock Clock => clock;
    public int Count => count;
    public double NewestTime => count > 0 ? items[count - 1].Time : double.NegativeInfinity;
    public void Clear() => count = 0;

    public bool Push(in NetPose pose, double localTime)
    {
        if (count > 0 && pose.Time <= items[count - 1].Time) return false; // Late or duplicate datagram.
        clock.Observe(pose.Time, localTime);
        if (count == items.Length)
        {
            Array.Copy(items, 1, items, 0, count - 1);
            count--;
        }
        items[count++] = pose;
        return true;
    }

    public bool Sample(double renderTime, out NetPose pose, out Vector3 velocity)
    {
        velocity = Vector3.zero;
        pose = default;
        if (count == 0) return false;
        if (count == 1 || renderTime <= items[0].Time)
        {
            pose = items[0];
            return true;
        }
        int last = count - 1;
        if (renderTime >= items[last].Time)
        {
            // Hold the newest pose rather than extrapolating into walls.
            pose = items[last];
            velocity = Velocity(items[last - 1], items[last]);
            return true;
        }
        int index = 0;
        while (index < last - 1 && items[index + 1].Time <= renderTime) index++;
        NetPose a = items[index], b = items[index + 1];
        float t = (float)((renderTime - a.Time) / Math.Max(0.0001, b.Time - a.Time));
        pose = t < 0.5f ? a : b;
        pose.Time = renderTime;
        pose.Position = Vector3.LerpUnclamped(a.Position, b.Position, t);
        pose.Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, t);
        pose.View = Quaternion.Slerp(a.View, b.View, t);
        pose.FlightSeconds = Mathf.LerpUnclamped(a.FlightSeconds, b.FlightSeconds, t);
        velocity = Velocity(a, b);
        if (index > 0)
        {
            // Drop snapshots that can never be sampled again.
            Array.Copy(items, index, items, 0, count - index);
            count -= index;
        }
        return true;
    }

    private static Vector3 Velocity(in NetPose a, in NetPose b) =>
        (b.Position - a.Position) / (float)Math.Max(0.001, b.Time - a.Time);
}
