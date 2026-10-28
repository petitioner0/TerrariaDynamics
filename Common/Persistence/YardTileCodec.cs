using System;
using System.Collections.Generic;
using System.IO;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaDynamics.Common.TileYards;
using TerrariaDynamics.Common.WorldEditing;

namespace TerrariaDynamics.Common.Persistence;

/// <summary>
/// Sparse, versioned snapshot of tML's standard Tile state. Runtime authority is
/// always Main.tile in the yard; snapshots serve persistence and lifecycle sync.
/// Tile/wall palettes use content names so mod load order cannot change their IDs.
/// Custom ITileData persistence and TileEntities are outside this first prototype.
/// </summary>
internal static class YardTileCodec
{
    private const int Version = 2;
    private const ushort HasTileFlag = 2048;

    public static byte[] Save(Tilemap map, Yard region)
    {
        var tilePalette = new Dictionary<ushort, int>();
        var wallPalette = new Dictionary<ushort, int>();
        var cells = new List<(int Index, Tile Tile)>();
        var bounds = region.Bounds;
        for (int x = 0; x < region.Width; x++) {
            for (int y = 0; y < region.Height; y++) {
                Tile tile = map[bounds.X + x, bounds.Y + y];
                if (!YardTileTransfer.HasContent(tile))
                    continue;
                tilePalette.TryAdd(tile.HasTile ? tile.TileType : (ushort)TileID.Dirt, tilePalette.Count);
                wallPalette.TryAdd(tile.WallType, wallPalette.Count);
                cells.Add((x * region.Height + y, tile));
            }
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(Version);
        WritePalette(writer, tilePalette, false);
        WritePalette(writer, wallPalette, true);
        writer.Write(cells.Count);
        foreach (var cell in cells) {
            writer.Write(cell.Index);
            Tile tile = cell.Tile;
            writer.Write(tilePalette[tile.HasTile ? tile.TileType : (ushort)TileID.Dirt]);
            writer.Write(wallPalette[tile.WallType]);
            TileState.Capture(tile).Write(writer);
        }
        return stream.ToArray();
    }

    public static int Load(Tilemap map, Yard region, byte[] bytes, bool clearAll = false)
    {
        using var stream = new MemoryStream(bytes, false);
        using var reader = new BinaryReader(stream);
        int version = reader.ReadInt32();
        if (version != Version)
            throw new InvalidDataException("Unsupported yard tile format.");
        ushort[] tiles = ReadPalette(reader, false);
        ushort[] walls = ReadPalette(reader, true);
        int count = reader.ReadInt32();
        int area = checked(region.Width * region.Height);
        if (count < 0 || count > area || area > Yard.MaxSelectionArea)
            throw new InvalidDataException("Invalid yard tile count.");

        // Resolve and validate the ENTIRE payload before changing any tiles.
        var decoded = new (int Index, ushort Type, ushort Wall, TileState State)[count];
        var seen = new HashSet<int>();
        for (int i = 0; i < count; i++) {
            int index = reader.ReadInt32();
            int type = reader.ReadInt32();
            int wall = reader.ReadInt32();
            if ((uint)index >= area || !seen.Add(index)
                || (uint)type >= tiles.Length || (uint)wall >= walls.Length)
                throw new InvalidDataException("Invalid or duplicate yard tile address.");
            decoded[i] = (index, tiles[type], walls[wall], TileState.Read(reader));
        }
        if (stream.Position != stream.Length)
            throw new InvalidDataException("Unexpected trailing yard data.");

        if (clearAll)
            for (int x = region.Bounds.Left; x < region.Bounds.Right; x++)
                for (int y = region.Bounds.Top; y < region.Bounds.Bottom; y++)
                    map[x, y].ClearEverything();
        foreach (var cell in decoded) {
            Tile tile = map[region.Bounds.X + cell.Index / region.Height,
                region.Bounds.Y + cell.Index % region.Height];
            tile.ClearEverything();
            cell.State.Apply(tile);
            tile.TileType = cell.Type;
            tile.WallType = cell.Wall;
        }
        return count;
    }

    private static void WritePalette(BinaryWriter writer, Dictionary<ushort, int> palette, bool wall)
    {
        writer.Write(palette.Count);
        // Dictionary insertion order is not part of the file format.
        var ordered = new ushort[palette.Count];
        foreach (var pair in palette)
            ordered[pair.Value] = pair.Key;
        foreach (ushort id in ordered) {
            bool vanilla = id < (wall ? WallID.Count : TileID.Count);
            writer.Write(vanilla);
            if (vanilla)
                writer.Write(id);
            else
                writer.Write(wall ? WallLoader.GetWall(id).FullName : TileLoader.GetTile(id).FullName);
        }
    }

    private static ushort[] ReadPalette(BinaryReader reader, bool wall)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > ushort.MaxValue + 1)
            throw new InvalidDataException("Invalid yard content palette.");
        var result = new ushort[count];
        for (int i = 0; i < count; i++) {
            if (reader.ReadBoolean()) {
                ushort id = reader.ReadUInt16();
                if (id >= (wall ? WallID.Count : TileID.Count))
                    throw new InvalidDataException("Invalid vanilla content ID.");
                result[i] = id;
            }
            else {
                string name = reader.ReadString();
                if (wall && ModContent.TryFind(name, out ModWall modWall))
                    result[i] = checked((ushort)modWall.Type);
                else if (!wall && ModContent.TryFind(name, out ModTile modTile))
                    result[i] = checked((ushort)modTile.Type);
                else
                    throw new InvalidDataException($"Missing yard content: {name}. Saved payload preserved.");
            }
        }
        return result;
    }

    private struct TileState
    {
        public short FrameX, FrameY;
        public int WallFrameX, WallFrameY;
        public byte TileColor, WallColor, TileFrameNumber, WallFrameNumber, Slope, LiquidAmount, LiquidType;
        public ushort Flags;

        public static TileState Capture(Tile tile) => new() {
            FrameX = tile.TileFrameX, FrameY = tile.TileFrameY,
            WallFrameX = tile.WallFrameX, WallFrameY = tile.WallFrameY,
            TileColor = tile.TileColor, WallColor = tile.WallColor,
            TileFrameNumber = (byte)tile.TileFrameNumber, WallFrameNumber = (byte)tile.WallFrameNumber,
            Slope = (byte)tile.Slope, LiquidAmount = tile.LiquidAmount, LiquidType = (byte)tile.LiquidType,
            Flags = (ushort)((tile.IsHalfBlock ? 1 : 0) | (tile.IsActuated ? 2 : 0)
                | (tile.HasActuator ? 4 : 0) | (tile.RedWire ? 8 : 0) | (tile.BlueWire ? 16 : 0)
                | (tile.GreenWire ? 32 : 0) | (tile.YellowWire ? 64 : 0)
                | (tile.IsTileInvisible ? 128 : 0) | (tile.IsWallInvisible ? 256 : 0)
                | (tile.IsTileFullbright ? 512 : 0) | (tile.IsWallFullbright ? 1024 : 0)
                | (tile.HasTile ? HasTileFlag : 0))
        };

        public void Write(BinaryWriter writer)
        {
            writer.Write(FrameX); writer.Write(FrameY);
            writer.Write(WallFrameX); writer.Write(WallFrameY);
            writer.Write(TileColor); writer.Write(WallColor);
            writer.Write(TileFrameNumber); writer.Write(WallFrameNumber);
            writer.Write(Slope); writer.Write(LiquidAmount); writer.Write(LiquidType); writer.Write(Flags);
        }

        public static TileState Read(BinaryReader reader)
        {
            var state = new TileState {
                FrameX = reader.ReadInt16(), FrameY = reader.ReadInt16(),
                WallFrameX = reader.ReadInt32(), WallFrameY = reader.ReadInt32(),
                TileColor = reader.ReadByte(), WallColor = reader.ReadByte(),
                TileFrameNumber = reader.ReadByte(), WallFrameNumber = reader.ReadByte(),
                Slope = reader.ReadByte(), LiquidAmount = reader.ReadByte(), LiquidType = reader.ReadByte(),
                Flags = reader.ReadUInt16()
            };
            if (state.Slope > 4 || state.LiquidType > 3 || state.TileFrameNumber > 3
                || state.WallFrameNumber > 3 || (state.Flags & ~4095) != 0)
                throw new InvalidDataException("Invalid yard tile state.");
            return state;
        }

        public void Apply(Tile tile)
        {
            tile.HasTile = (Flags & HasTileFlag) != 0;
            tile.TileFrameX = FrameX; tile.TileFrameY = FrameY;
            tile.WallFrameX = WallFrameX; tile.WallFrameY = WallFrameY;
            tile.TileColor = TileColor; tile.WallColor = WallColor;
            tile.TileFrameNumber = TileFrameNumber; tile.WallFrameNumber = WallFrameNumber;
            tile.Slope = (SlopeType)Slope; tile.LiquidAmount = LiquidAmount; tile.LiquidType = LiquidType;
            tile.IsHalfBlock = (Flags & 1) != 0; tile.IsActuated = (Flags & 2) != 0;
            tile.HasActuator = (Flags & 4) != 0; tile.RedWire = (Flags & 8) != 0;
            tile.BlueWire = (Flags & 16) != 0; tile.GreenWire = (Flags & 32) != 0;
            tile.YellowWire = (Flags & 64) != 0; tile.IsTileInvisible = (Flags & 128) != 0;
            tile.IsWallInvisible = (Flags & 256) != 0; tile.IsTileFullbright = (Flags & 512) != 0;
            tile.IsWallFullbright = (Flags & 1024) != 0;
            // Liquid update-queue flags are transient. Yard liquids are frozen for now.
            tile.SkipLiquid = false; tile.CheckingLiquid = false;
        }
    }
}
