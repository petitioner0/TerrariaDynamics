using System;
using System.IO;
using Microsoft.Xna.Framework;

namespace TerrariaDynamics.Common.Networking;

internal enum YardRestKind : byte
{
    None,
    Sitting,
    Sleeping
}

internal readonly record struct YardPlayerRest(byte Player, Guid YardId, Point LocalTile, YardRestKind Kind, int Direction);

internal static class YardPlayerRestPacket
{
    public static void Write(BinaryWriter writer, YardPlayerRest state)
    {
        writer.Write(state.Player);
        writer.Write(state.YardId.ToByteArray());
        writer.Write((short)state.LocalTile.X);
        writer.Write((short)state.LocalTile.Y);
        writer.Write((byte)state.Kind);
        writer.Write((sbyte)state.Direction);
    }

    public static YardPlayerRest Read(BinaryReader reader)
    {
        byte player = reader.ReadByte();
        Guid yard = YardPacketCodec.ReadId(reader);
        Point tile = new(reader.ReadInt16(), reader.ReadInt16());
        var kind = (YardRestKind)reader.ReadByte();
        int direction = reader.ReadSByte();
        if (kind > YardRestKind.Sleeping || (kind == YardRestKind.None) != (yard == Guid.Empty)
            || tile.X < 0 || tile.Y < 0
            || (kind == YardRestKind.None ? direction != 0 : direction != -1 && direction != 1))
            throw new InvalidDataException("Invalid Yard resting state.");
        return new YardPlayerRest(player, yard, tile, kind, direction);
    }
}
