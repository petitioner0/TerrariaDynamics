using Terraria;
using Terraria.ID;

namespace TerrariaDynamics.Common.Physics;

internal static class TileCollisionRules
{
    public static TerrainTileShape ShapeOf(Tile tile)
    {
        if (!tile.HasTile || tile.IsActuated)
            return TerrainTileShape.None;
        ushort type = tile.TileType;
        if (!Main.tileSolid[type] || Main.tileSolidTop[type] || Main.tileRope[type] || TileID.Sets.Platforms[type])
            return TerrainTileShape.None;
        if (tile.IsHalfBlock)
            return TerrainTileShape.HalfBlock;
        return tile.Slope switch {
            SlopeType.SlopeDownRight => TerrainTileShape.DownRight,
            SlopeType.SlopeDownLeft => TerrainTileShape.DownLeft,
            SlopeType.SlopeUpRight => TerrainTileShape.UpRight,
            SlopeType.SlopeUpLeft => TerrainTileShape.UpLeft,
            _ => TerrainTileShape.Solid
        };
    }
}
