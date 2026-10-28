using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaDynamics.Common.Physics;
using TerrariaDynamics.Common.Rendering;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class Yard
{
    private YardCollisionGeometry playerCollision;

    internal YardCollisionGeometry PlayerCollision
    {
        get {
            if (playerCollision == null) {
                var shapes = new TerrainTileShape[Width * Height];
                var platforms = new TerrainTileShape[shapes.Length];
                for (int y = 0; y < Height; y++)
                    for (int x = 0; x < Width; x++) {
                        Tile tile = Main.tile[StorageOrigin.X + x, StorageOrigin.Y + y];
                        shapes[y * Width + x] = TileCollisionRules.ShapeOf(tile);
                        if (tile.HasTile && !tile.IsActuated
                            && (TileID.Sets.Platforms[tile.TileType]
                                || (Main.tileSolidTop[tile.TileType] && tile.TileFrameY == 0)))
                            platforms[y * Width + x] = tile.IsHalfBlock ? TerrainTileShape.HalfBlock : tile.Slope switch {
                                SlopeType.SlopeDownRight => TerrainTileShape.DownRight,
                                SlopeType.SlopeDownLeft => TerrainTileShape.DownLeft,
                                _ => TerrainTileShape.Solid
                            };
                    }
                playerCollision = new YardCollisionGeometry(Width, Height, shapes, platforms);
            }
            return playerCollision;
        }
    }

    internal void InvalidatePlayerCollision() => playerCollision = null;

    // Movement samples the same delayed stream as rendering, without changing
    // the authoritative pose or depending on whether a draw happened this tick.
    internal YardCoordinates PlayerCoordinates
    {
        get {
            if (Main.netMode != NetmodeID.MultiplayerClient || renderInterpolator == null)
                return Coordinates;
            renderInterpolator.Sample(YardRenderClock.Seconds, out Vector2 position, out float rotation);
            return new YardCoordinates(StorageOrigin, new Point(Width, Height), position, rotation);
        }
    }

    internal Vector2 PlayerPointVelocity(YardCoordinates pose, Vector2 point)
    {
        Vector2 radius = point - pose.LocalToWorld(PlayerCollision.LocalCenterOfMass);
        return (LinearVelocity + new Vector2(-radius.Y, radius.X) * AngularVelocity) * PhysicsSystem.TimeStep;
    }
}
