using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.WorldEditing;

internal static class YardTileTransfer
{
    // One definition shared by validation, transfer, persistence and destination checks.
    // Stale frame/type values on an otherwise empty tile are not standalone content.
    public static bool HasContent(Tile tile) => tile.HasTile || tile.WallType != WallID.None
        || tile.RedWire || tile.BlueWire || tile.GreenWire || tile.YellowWire
        || tile.HasActuator || tile.LiquidAmount != 0;

    public static bool TryGetReturnBounds(Tilemap map, Yard region, Rectangle playableBounds,
        out Rectangle placedBounds)
    {
        placedBounds = default;
        YardCoordinates coordinates = region.Coordinates;
        Point origin = coordinates.NearestWorldAnchorTile;
        if (origin.X < playableBounds.Left || origin.Y < playableBounds.Top
            || (long)origin.X + region.Width > playableBounds.Right
            || (long)origin.Y + region.Height > playableBounds.Bottom
            || origin.X < 0 || origin.Y < 0
            || (long)origin.X + region.Width > map.Width
            || (long)origin.Y + region.Height > map.Height) {
            return false;
        }
        placedBounds = new Rectangle(origin.X, origin.Y, region.Width, region.Height);
        if (placedBounds.Intersects(region.Bounds)) {
            return false;
        }
        return true;
    }

    // Destruction (and its item drops) is deliberately completed before any Yard
    // tile is written. A failed copy restores the Yard, but cannot undo drops.
    // clearWorld must empty the rectangle through the normal world break paths.
    public static bool TryReturn(Tilemap map, Yard region, Rectangle playableBounds,
        Action<Rectangle> clearWorld, out Rectangle placedBounds, out int count, YardObjectTransfer objects = null)
    {
        count = 0;
        if (!TryGetReturnBounds(map, region, playableBounds, out placedBounds))
            return false;
        YardCoordinates coordinates = region.Coordinates;
        var cells = new List<(Point Source, Point Destination)>();
        for (int x = region.Bounds.Left; x < region.Bounds.Right; x++) {
            for (int y = region.Bounds.Top; y < region.Bounds.Bottom; y++) {
                Point destination = coordinates.StorageTileToPlacementCell(x, y);
                if (HasContent(map[x, y]))
                    count++;
                cells.Add((new Point(x, y), destination));
            }
        }

        clearWorld(placedBounds);
        int attempted = 0;
        bool clearingSources = false;
        try {
            foreach (var cell in cells) {
                // Include a partially copied target if an ITileData callback fails.
                attempted++;
                map[cell.Destination.X, cell.Destination.Y].CopyFrom(map[cell.Source.X, cell.Source.Y]);
            }
            clearingSources = true;
            foreach (var cell in cells)
                map[cell.Source.X, cell.Source.Y].ClearEverything();
        }
        catch {
            if (clearingSources) {
                foreach (var cell in cells)
                    map[cell.Source.X, cell.Source.Y].CopyFrom(map[cell.Destination.X, cell.Destination.Y]);
            }
            for (int i = 0; i < attempted; i++)
                map[cells[i].Destination.X, cells[i].Destination.Y].ClearEverything();
            throw;
        }
        // A rendered door overhang has no outer column to transfer. Close it in
        // the destination so vanilla receives a complete door within the same
        // placement rectangle. Do this only after the transfer has committed.
        foreach (var cell in cells) {
            Tile tile = map[cell.Destination.X, cell.Destination.Y];
            if (YardDoorGeometry.OverhangDirection(placedBounds, cell.Destination.X, tile) == 0)
                continue;
            tile.TileType = TileID.ClosedDoor;
            tile.TileFrameX = (short)(tile.TileFrameX / 72 * 54);
        }
        objects?.Commit();
        return true;
    }

    // Destination must already be reserved and empty. Each populated cell moves
    // its complete Tile state, even when it has no foreground block.
    public static int Move(Tilemap map, Yard region, YardObjectTransfer objects = null)
    {
        YardCoordinates coordinates = region.Coordinates;
        Point Destination(Point source) => coordinates.LocalTileToStorage(
            new Point(source.X - region.Source.X, source.Y - region.Source.Y));
        var copied = new List<Point>();
        bool clearingSources = false;
        try {
            for (int x = region.Source.Left; x < region.Source.Right; x++) {
                for (int y = region.Source.Top; y < region.Source.Bottom; y++) {
                    Tile source = map[x, y];
                    if (!HasContent(source))
                        continue;
                    Point destination = Destination(new Point(x, y));
                    map[destination.X, destination.Y].CopyFrom(source);
                    copied.Add(new Point(x, y));
                }
            }
            clearingSources = true;
            foreach (Point source in copied)
                map[source.X, source.Y].ClearEverything();
            objects?.Commit();
            return copied.Count;
        }
        catch {
            if (clearingSources) {
                foreach (Point source in copied) {
                    Point destination = Destination(source);
                    map[source.X, source.Y].CopyFrom(map[destination.X, destination.Y]);
                }
            }
            for (int x = region.Bounds.Left; x < region.Bounds.Right; x++)
                for (int y = region.Bounds.Top; y < region.Bounds.Bottom; y++)
                    map[x, y].ClearEverything();
            throw;
        }
    }
}
