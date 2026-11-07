using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaDynamics.Common.WorldEditing;

// An outward-open edge door keeps its hinge column in storage. Its outer
// column is derived from that state, without owning any neighboring cells.
internal static class YardDoorGeometry
{
    public static int OverhangDirection(Rectangle bounds, int x, Tile tile)
    {
        if (!tile.HasTile || tile.TileType != TileID.OpenDoor)
            return 0;
        int frame = tile.TileFrameX % 72;
        if (x == bounds.Left && frame == 54)
            return -1;
        return x == bounds.Right - 1 && frame == 0 ? 1 : 0;
    }

    public static bool TryGetOverhangHinge(Tilemap map, Rectangle bounds, int x, int y, out Point hinge)
    {
        hinge = default;
        if (y < bounds.Top || y >= bounds.Bottom)
            return false;
        int direction = x == bounds.Left - 1 ? -1 : x == bounds.Right ? 1 : 0;
        if (direction == 0)
            return false;
        hinge = new Point(x - direction, y);
        return OverhangDirection(bounds, hinge.X, map[hinge.X, hinge.Y]) == direction;
    }
}
