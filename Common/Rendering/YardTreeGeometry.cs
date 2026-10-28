using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;

namespace TerrariaDynamics.Common.Rendering;

internal static class YardTreeGeometry
{
    public static bool IsTree(int type) => type == TileID.Trees || type == TileID.PalmTree
        || type >= TileID.TreeTopaz && type <= TileID.TreeAmber
        || type == TileID.VanityTreeSakura || type == TileID.VanityTreeYellowWillow || type == TileID.TreeAsh;

    public static bool IsFoliage(Tile tile) => tile.TileType == TileID.PalmTree
        ? tile.TileFrameX is 88 or 110 or 132
        : tile.TileFrameY >= 198 && tile.TileFrameX is 22 or 44 or 66;

    public static int TrunkX(int x, Tile tile)
    {
        if (tile.TileType == TileID.PalmTree)
            return x;
        int fx = tile.TileFrameX;
        int fy = tile.TileFrameY;
        if (fx == 66 && fy <= 45) return x + 1;
        if (fx == 88 && fy >= 66 && fy <= 110) return x - 1;
        if (fy >= 198) return fx == 44 ? x + 1 : fx == 66 ? x - 1 : x;
        if (fy >= 132) return fx == 22 ? x - 1 : fx == 44 ? x + 1 : x;
        return x;
    }

    public static Point? FindGround(Tilemap map, Rectangle bounds, int x, int y, Tile tile)
    {
        int trunkX = TrunkX(x, tile);
        if (trunkX < bounds.Left || trunkX >= bounds.Right)
            return null;
        for (int floorY = y; floorY < Math.Min(bounds.Bottom, y + 100); floorY++) {
            Tile candidate = map[trunkX, floorY];
            if (candidate.HasTile && candidate.TileType != tile.TileType)
                return new Point(trunkX, floorY);
        }
        return null;
    }

    public static int ForestStyle(int displayX, int[] treeX, Func<int, int> styleAt)
    {
        int region = displayX <= treeX[0] ? 0 : displayX <= treeX[1] ? 1 : displayX <= treeX[2] ? 2 : 3;
        int style = styleAt(region);
        return style == 0 ? 0 : style == 5 ? 10 : 5 + style;
    }
}
