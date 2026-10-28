using System;
using System.IO;
using Microsoft.Xna.Framework;

namespace TerrariaDynamics.Common.Networking;

internal readonly record struct YardPhysicsPose(Guid Id, Vector2 Position, float Rotation,
    Vector2 Velocity, float AngularVelocity);

internal static class YardPhysicsPacket
{
    public const int SendIntervalTicks = 3;
    public const double SendIntervalSeconds = SendIntervalTicks / 60d;
    // 40 bytes per Yard plus framing remains below ModPacket's UInt16 limit.
    public const int MaxPoses = 1024;

    public static void Write(BinaryWriter writer, long revision, ulong sequence, ReadOnlySpan<YardPhysicsPose> poses)
    {
        ValidateHeader(revision, sequence, poses.Length);
        writer.Write(revision);
        writer.Write(sequence);
        writer.Write((ushort)poses.Length);
        foreach (YardPhysicsPose pose in poses) {
            Validate(pose);
            writer.Write(pose.Id.ToByteArray());
            writer.Write(pose.Position.X); writer.Write(pose.Position.Y);
            writer.Write(pose.Rotation);
            writer.Write(pose.Velocity.X); writer.Write(pose.Velocity.Y);
            writer.Write(pose.AngularVelocity);
        }
    }

    public static YardPhysicsPose[] Read(BinaryReader reader, out long revision, out ulong sequence)
    {
        revision = reader.ReadInt64();
        sequence = reader.ReadUInt64();
        int count = reader.ReadUInt16();
        ValidateHeader(revision, sequence, count);
        var poses = new YardPhysicsPose[count];
        for (int i = 0; i < count; i++) {
            byte[] id = reader.ReadBytes(16);
            if (id.Length != 16)
                throw new EndOfStreamException("Truncated Yard physics ID.");
            var pose = new YardPhysicsPose(new Guid(id), new Vector2(reader.ReadSingle(), reader.ReadSingle()),
                reader.ReadSingle(), new Vector2(reader.ReadSingle(), reader.ReadSingle()), reader.ReadSingle());
            Validate(pose);
            poses[i] = pose;
        }
        return poses;
    }

    private static void ValidateHeader(long revision, ulong sequence, int count)
    {
        if (revision < 0 || sequence == 0 || count < 1 || count > MaxPoses)
            throw new InvalidDataException("Invalid Yard physics packet header.");
    }

    private static void Validate(YardPhysicsPose pose)
    {
        if (pose.Id == Guid.Empty || !float.IsFinite(pose.Position.X) || !float.IsFinite(pose.Position.Y)
            || !float.IsFinite(pose.Rotation) || !float.IsFinite(pose.Velocity.X)
            || !float.IsFinite(pose.Velocity.Y) || !float.IsFinite(pose.AngularVelocity))
            throw new InvalidDataException("Invalid Yard physics pose.");
    }
}
