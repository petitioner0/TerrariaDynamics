using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ObjectData;

namespace TerrariaDynamics.Common.WorldEditing;

// The vanilla registries retain ownership of inventory/text. Prepare before any
// Tile writes; Commit only after those writes succeed, while holding the save lock.
internal sealed class YardObjectTransfer
{
    private readonly List<(Chest Chest, Point Position)> chests = new();
    private readonly List<(Sign Sign, Point Position)> signs = new();

    public static bool ValidateSelection(Tilemap map, Rectangle source, Chest[] chestRegistry,
        Sign[] signRegistry, out string error)
    {
        var chestCells = new HashSet<Point>();
        var signCells = new HashSet<Point>();
        error = "PartialFurniture";
        foreach (Chest chest in chestRegistry) {
            if (chest != null && !Include(chest.x, chest.y, true, chestCells)) return false;
        }
        foreach (Sign sign in signRegistry) {
            if (sign != null && !Include(sign.x, sign.y, false, signCells)) return false;
        }
        error = "MissingContainerData";
        for (int x = source.Left; x < source.Right; x++) {
            for (int y = source.Top; y < source.Bottom; y++) {
                Tile tile = map[x, y];
                if (!tile.HasTile) continue;
                if ((Main.tileContainer[tile.TileType] || TileID.Sets.BasicDresser[tile.TileType])
                    && !chestCells.Contains(new Point(x, y))
                    || Main.tileSign[tile.TileType] && !signCells.Contains(new Point(x, y)))
                    return false;
            }
        }
        error = "";
        return true;

        bool Include(int x, int y, bool chest, HashSet<Point> cells)
        {
            Rectangle bounds = ObjectBounds(map, x, y, chest);
            if (!bounds.Intersects(source)) return true;
            if (!source.Contains(bounds)) return false;
            for (int tx = bounds.Left; tx < bounds.Right; tx++)
                for (int ty = bounds.Top; ty < bounds.Bottom; ty++) cells.Add(new Point(tx, ty));
            return true;
        }
    }

    public static YardObjectTransfer Prepare(Tilemap map, Rectangle source, Point destination,
        Chest[] chestRegistry, Sign[] signRegistry)
    {
        var transfer = new YardObjectTransfer();
        Point delta = destination - source.Location;
        foreach (Chest chest in chestRegistry) {
            if (chest == null) continue;
            Rectangle bounds = ObjectBounds(map, chest.x, chest.y, chest: true);
            if (!bounds.Intersects(source)) continue;
            if (!source.Contains(bounds))
                throw new InvalidDataException("Select the whole chest or dresser.");
            transfer.chests.Add((chest, bounds.Location + delta));
        }
        foreach (Sign sign in signRegistry) {
            if (sign == null) continue;
            Rectangle bounds = ObjectBounds(map, sign.x, sign.y, chest: false);
            if (!bounds.Intersects(source)) continue;
            if (!source.Contains(bounds))
                throw new InvalidDataException("Select the whole sign.");
            transfer.signs.Add((sign, bounds.Location + delta));
        }
        return transfer;
    }

    internal static Rectangle ObjectBounds(Tilemap map, int x, int y, bool chest)
    {
        if (x < 0 || y < 0 || x >= map.Width || y >= map.Height)
            return new Rectangle(x, y, 2, 2);
        Tile tile = map[x, y];
        TileObjectData data = tile.HasTile ? TileObjectData.GetTileData(tile) : null;
        return new Rectangle(x, y, data?.Width ?? (chest && TileID.Sets.BasicDresser[tile.TileType] ? 3 : 2),
            data?.Height ?? 2);
    }

    public void CloseUsers(Player[] players)
    {
        foreach (Player player in players) {
            if (player == null) continue;
            if (player.chest >= 0 && player.chest < Main.chest.Length
                && chests.Exists(entry => ReferenceEquals(entry.Chest, Main.chest[player.chest])))
                player.chest = -1;
            if (player.sign >= 0 && player.sign < Main.sign.Length
                && signs.Exists(entry => ReferenceEquals(entry.Sign, Main.sign[player.sign]))) {
                player.sign = -1;
                if (player.whoAmI == Main.myPlayer) Main.editSign = false;
            }
        }
    }

    public void Commit()
    {
        foreach (var entry in chests) {
            entry.Chest.x = entry.Position.X;
            entry.Chest.y = entry.Position.Y;
        }
        foreach (var entry in signs) {
            entry.Sign.x = entry.Position.X;
            entry.Sign.y = entry.Position.Y;
        }
    }
}
