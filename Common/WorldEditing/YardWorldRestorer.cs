using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;

namespace TerrariaDynamics.Common.WorldEditing;

internal static class YardWorldRestorer
{
    // Reuse vanilla's item selection and mod drop hooks even when normal mining
    // refuses a protected tile. Resolve these before performing any destruction.
    private static readonly Action<int, int, Tile, bool, bool> DropTile =
        typeof(WorldGen).GetMethod("KillTile_DropItems", BindingFlags.Static | BindingFlags.NonPublic)
            .CreateDelegate<Action<int, int, Tile, bool, bool>>();
    private static readonly Action<int, int, Tile> DropWall =
        typeof(WorldGen).GetMethod("KillWall_DropItems", BindingFlags.Static | BindingFlags.NonPublic)
            .CreateDelegate<Action<int, int, Tile>>();

    public static void Clear(Rectangle bounds)
    {
        // Ensure both version-specific delegates are ready before dropping items.
        _ = DropTile;
        _ = DropWall;
        // Full chests normally refuse mining. Preserve their actual item instances
        // (prefixes and mod data included) as drops, then allow normal chest removal.
        foreach (Chest chest in Main.chest) {
            if (chest == null || !WorldGen.InWorld(chest.x, chest.y))
                continue;
            Tile tile = Main.tile[chest.x, chest.y];
            TileObjectData data = TileObjectData.GetTileData(tile);
            int width = data?.Width ?? (TileID.Sets.BasicDresser[tile.TileType] ? 3 : 2);
            if (!bounds.Intersects(new Rectangle(chest.x, chest.y, width, data?.Height ?? 2)))
                continue;
            foreach (Item item in chest.item) {
                if (item == null || item.IsAir)
                    continue;
                Item.NewItem(WorldGen.GetItemSource_FromTileBreak(chest.x, chest.y),
                    new Rectangle(chest.x * 16, chest.y * 16, width * 16, 32), item);
                item.TurnToAir();
            }
        }

        // Finish all breaking and its recursive furniture/support updates before
        // copying any incoming tiles. The entire rectangle, including air, returns.
        for (int x = bounds.Left; x < bounds.Right; x++) {
            for (int y = bounds.Top; y < bounds.Bottom; y++) {
                Tile tile = Main.tile[x, y];
                if (tile.HasTile) {
                    WorldGen.KillTile(x, y, fail: false, effectOnly: false, noItem: false);
                    if (tile.HasTile)
                        ForceRemainingTile(x, y, tile);
                }
                if (tile.WallType != WallID.None) {
                    WorldGen.KillWall(x, y, fail: false);
                    if (tile.WallType != WallID.None) {
                        DropWall(x, y, tile);
                        tile.WallType = WallID.None;
                    }
                }
                if (tile.RedWire) WorldGen.KillWire(x, y);
                if (tile.BlueWire) WorldGen.KillWire2(x, y);
                if (tile.GreenWire) WorldGen.KillWire3(x, y);
                if (tile.YellowWire) WorldGen.KillWire4(x, y);
                if (tile.HasActuator) WorldGen.KillActuator(x, y);
                tile.ClearEverything();
            }
        }
    }

    private static void ForceRemainingTile(int x, int y, Tile tile)
    {
        int type = tile.TileType;
        TileObjectData data = TileObjectData.GetTileData(tile);
        Point16 origin = data == null ? new Point16(x, y) : TileObjectData.TopLeft(x, y);
        Tile root = Main.tile[origin.X, origin.Y];
        int frameX = root.TileFrameX;
        int frameY = root.TileFrameY;
        DropTile(x, y, tile, true, true);
        // A protected multi-tile is dropped once, then removed as one object so
        // framing cannot produce an additional drop for each remaining segment.
        for (int dx = 0; dx < (data?.Width ?? 1); dx++) {
            for (int dy = 0; dy < (data?.Height ?? 1); dy++) {
                int cellX = origin.X + dx;
                int cellY = origin.Y + dy;
                if (!WorldGen.InWorld(cellX, cellY))
                    continue;
                Tile part = Main.tile[cellX, cellY];
                if (part.HasTile && part.TileType == type)
                    part.ClearTile();
            }
        }
        if (data != null)
            TileLoader.KillMultiTile(origin.X, origin.Y, frameX, frameY, type);
        if (Main.tileContainer[type] || TileID.Sets.BasicDresser[type]) {
            int chest = Chest.FindChest(origin.X, origin.Y);
            if (chest >= 0)
                Chest.DestroyChestDirect(origin.X, origin.Y, chest);
        }
        if (Main.tileSign[type])
            Sign.KillSign(origin.X, origin.Y);
        if (TileEntity.ByPosition.Remove(origin, out TileEntity entity))
            TileEntity.ByID.Remove(entity.ID);
        WorldGen.SquareTileFrame(x, y, resetFrame: false);
    }
}
