using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;

namespace TerrariaDynamics.Common.Rendering;

// Arrival times use a monotonic clock. Render slightly behind the received
// stream so ordinary network jitter has two known poses to interpolate between.
internal sealed class YardPoseInterpolator
{
    public const double DelaySeconds = 0.1;
    private const double ResetGapSeconds = 0.5;
    private const int MaxSnapshots = 16;
    private readonly List<Snapshot> snapshots = new(MaxSnapshots);
    private ulong lastSequence;

    private readonly record struct Snapshot(double Time, Vector2 Position, float Rotation);

    public void Reset(Vector2 position, float rotation, double time)
    {
        Validate(position, rotation, time);
        snapshots.Clear();
        snapshots.Add(new Snapshot(time, position, rotation));
        lastSequence = 0;
    }

    public bool Push(ulong sequence, Vector2 position, float rotation, double time)
    {
        Validate(position, rotation, time);
        if (sequence == 0 || sequence <= lastSequence)
            return false;
        if (snapshots.Count == 0 || time - snapshots[^1].Time > ResetGapSeconds)
            Reset(position, rotation, time);
        else {
            if (time < snapshots[^1].Time)
                throw new ArgumentOutOfRangeException(nameof(time), "Snapshot clock must be monotonic.");
            var snapshot = new Snapshot(time, position, rotation);
            if (time == snapshots[^1].Time)
                snapshots[^1] = snapshot;
            else {
                if (snapshots.Count == MaxSnapshots)
                    snapshots.RemoveAt(0);
                snapshots.Add(snapshot);
            }
        }
        lastSequence = sequence;
        return true;
    }

    public void Sample(double time, out Vector2 position, out float rotation)
    {
        if (!double.IsFinite(time) || snapshots.Count == 0)
            throw new ArgumentOutOfRangeException(nameof(time));
        double renderTime = time - DelaySeconds;
        while (snapshots.Count > 2 && snapshots[1].Time <= renderTime)
            snapshots.RemoveAt(0);
        Snapshot first = snapshots[0];
        Snapshot last = snapshots[^1];
        if (renderTime <= first.Time) {
            position = first.Position;
            rotation = first.Rotation;
        }
        else if (renderTime >= last.Time) {
            // Do not predict motion through terrain when packets stop arriving.
            position = last.Position;
            rotation = last.Rotation;
        }
        else {
            Snapshot next = snapshots[1];
            float amount = (float)((renderTime - first.Time) / (next.Time - first.Time));
            position = Vector2.Lerp(first.Position, next.Position, amount);
            float angle = MathF.IEEERemainder(next.Rotation - first.Rotation, MathF.Tau);
            rotation = first.Rotation + angle * amount;
        }
    }

    private static void Validate(Vector2 position, float rotation, double time)
    {
        if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)
            || !float.IsFinite(rotation) || !double.IsFinite(time))
            throw new ArgumentOutOfRangeException(nameof(position));
    }
}
