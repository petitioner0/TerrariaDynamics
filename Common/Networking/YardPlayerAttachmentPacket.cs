using System;
using System.IO;
using Microsoft.Xna.Framework;

namespace TerrariaDynamics.Common.Networking;

internal readonly record struct YardPlayerAttachment(byte Player, Guid YardId,
    Vector2 LocalCenter, Vector2 RelativeVelocity, bool Standing);

internal static class YardPlayerAttachmentPacket
{
    public const int SendIntervalTicks = 3;
    public const int MaxPredictionTicks = 6;

    public static void Write(BinaryWriter writer, YardPlayerAttachment state)
    {
        writer.Write(state.Player);
        writer.Write(state.YardId.ToByteArray());
        writer.Write(state.LocalCenter.X);
        writer.Write(state.LocalCenter.Y);
        writer.Write(state.RelativeVelocity.X);
        writer.Write(state.RelativeVelocity.Y);
        writer.Write(state.Standing);
    }

    public static YardPlayerAttachment Read(BinaryReader reader)
    {
        byte player = reader.ReadByte();
        Guid yard = YardPacketCodec.ReadId(reader);
        Vector2 center = new(reader.ReadSingle(), reader.ReadSingle());
        Vector2 velocity = new(reader.ReadSingle(), reader.ReadSingle());
        bool standing = reader.ReadBoolean();
        if (!float.IsFinite(center.X) || !float.IsFinite(center.Y)
            || !float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y)
            || MathF.Abs(center.X) > 16777216 || MathF.Abs(center.Y) > 16777216
            || MathF.Abs(velocity.X) > 65536 || MathF.Abs(velocity.Y) > 65536)
            throw new InvalidDataException("Invalid Yard player attachment coordinates.");
        return new YardPlayerAttachment(player, yard, center, velocity, standing);
    }
}
