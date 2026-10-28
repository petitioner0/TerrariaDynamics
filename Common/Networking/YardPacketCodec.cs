using System;
using System.IO;
using System.IO.Compression;
using Terraria.ModLoader.IO;

namespace TerrariaDynamics.Common.Networking;

internal enum YardPacketKind : byte
{
    RequestSnapshot,
    RequestCreate,
    RequestReturn,
    EventChunk,
    Result,
    PhysicsTransforms,
    RequestForceInput,
    PlayerAttachment,
    RequestTileInteraction,
    PlayerRest,
    RequestStopRest,
    CircuitKeyInput
}

internal enum YardEventKind : byte
{
    Snapshot,
    Created,
    Returned,
    ReturnState
}

// ModPacket has a UInt16 length. A maximum-size Yard snapshot alone exceeds it,
// so persistent V2 data travels compressed in ordered, bounded chunks.
internal static class YardPacketCodec
{
    public const byte ProtocolVersion = 6;
    public const int ChunkSize = 48 * 1024;
    public const int MaxPayloadSize = 128 * 1024 * 1024;

    public static byte[] Encode(TagCompound tag)
    {
        using var raw = new MemoryStream();
        TagIO.ToStream(tag, raw, compress: false);
        if (raw.Length > MaxPayloadSize)
            throw new InvalidDataException("Yard network snapshot is too large.");
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, leaveOpen: true)) {
            raw.Position = 0;
            raw.CopyTo(gzip);
        }
        if (stream.Length == 0 || stream.Length > MaxPayloadSize)
            throw new InvalidDataException("Yard network snapshot is too large.");
        return stream.ToArray();
    }

    public static TagCompound Decode(byte[] bytes)
    {
        using var compressed = new MemoryStream(bytes, writable: false);
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress);
        using var stream = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = gzip.Read(buffer, 0, buffer.Length)) != 0) {
            if (stream.Length + count > MaxPayloadSize)
                throw new InvalidDataException("Yard network snapshot expands beyond the limit.");
            stream.Write(buffer, 0, count);
        }
        stream.Position = 0;
        TagCompound tag = TagIO.FromStream(stream, false);
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Trailing Yard network data.");
        return tag;
    }

    public static Guid ReadId(BinaryReader reader)
    {
        byte[] bytes = reader.ReadBytes(16);
        if (bytes.Length != 16)
            throw new EndOfStreamException("Truncated Yard ID.");
        return new Guid(bytes);
    }
}

internal sealed class YardPacketAssembly
{
    private MemoryStream stream;
    private int transferId;
    private int total;
    public YardEventKind Kind { get; private set; }
    public long Revision { get; private set; }
    public bool IsReceiving => stream != null;

    public void Clear()
    {
        stream?.Dispose();
        stream = null;
    }

    public byte[] Read(BinaryReader reader)
    {
        int id = reader.ReadInt32();
        var kind = (YardEventKind)reader.ReadByte();
        long revision = reader.ReadInt64();
        int length = reader.ReadInt32();
        int offset = reader.ReadInt32();
        int count = reader.ReadUInt16();
        if (!Enum.IsDefined(kind) || revision < 0 || length <= 0 || length > YardPacketCodec.MaxPayloadSize
            || offset < 0 || offset >= length || count != Math.Min(YardPacketCodec.ChunkSize, length - offset))
            throw new InvalidDataException("Invalid Yard event chunk.");
        if (offset == 0) {
            if (stream != null)
                throw new InvalidDataException("An unfinished Yard event was replaced.");
            transferId = id;
            Kind = kind;
            Revision = revision;
            total = length;
            stream = new MemoryStream();
        }
        if (stream == null || id != transferId || kind != Kind || revision != Revision
            || length != total || stream.Length != offset)
            throw new InvalidDataException("Out-of-order Yard event chunk.");
        byte[] bytes = reader.ReadBytes(count);
        if (bytes.Length != count)
            throw new EndOfStreamException("Truncated Yard event chunk.");
        stream.Write(bytes, 0, bytes.Length);
        if (stream.Length != total)
            return null;
        byte[] complete = stream.ToArray();
        Clear();
        return complete;
    }
}
