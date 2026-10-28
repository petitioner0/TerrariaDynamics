using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaDynamics.Common.Rendering;

// Structure-local pixel pivot and displacement; independent of storage and camera.
internal readonly record struct YardSwayPose(Vector2 Pivot, Vector2 Displacement, float Rotation)
{
    public Vector2 Transform(Vector2 point)
    {
        Vector2 relative = point - Pivot;
        float sin = MathF.Sin(Rotation);
        float cos = MathF.Cos(Rotation);
        return Pivot + Displacement + new Vector2(relative.X * cos - relative.Y * sin,
            relative.X * sin + relative.Y * cos);
    }
}

internal static class YardSwayGeometry
{
    // Resolve the entire chain, not just the visible slice. Never follow neighbors
    // out of this Yard, even if foreign data happens to be adjacent to its storage.
    public static Rectangle FindChain(Tilemap map, Rectangle bounds, int x, int y, bool reverse)
    {
        int top = y;
        int bottom = y + 1;
        while (top > bounds.Top && IsChain(map[x, top - 1], reverse))
            top--;
        while (bottom < bounds.Bottom && IsChain(map[x, bottom], reverse))
            bottom++;
        return new Rectangle(x, top, 1, bottom - top);
    }

    private static bool IsChain(Tile tile, bool reverse) => tile.HasTile &&
        (reverse ? TileID.Sets.ReverseVineThreads[tile.TileType] : TileID.Sets.VineThreads[tile.TileType]);

    public static Rectangle RegularObject(int x, int y, Tile tile, int width, int height,
        int frameYBase = 0) => new(x - tile.TileFrameX / 18 % width,
            y - (tile.TileFrameY - frameYBase) / 18 % height, width, height);
}
