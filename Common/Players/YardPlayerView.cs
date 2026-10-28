using System;
using Microsoft.Xna.Framework;
using Terraria;
using TerrariaDynamics.Common.Physics;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Players;

// A query view, not a second simulated player. All four corners are projected:
// rotating only the top-left would give doors and reach checks the wrong box.
internal readonly struct YardPlayerView
{
    public Vector2 Center { get; }
    public Vector2 HalfSize { get; }
    public Vector2 Velocity { get; }
    public Vector2 Input { get; }
    public Vector2 Gravity { get; }

    public YardPlayerView(Player player, Yard yard, YardCoordinates pose)
    {
        Center = pose.WorldToLocal(player.Center);
        float cos = MathF.Abs(MathF.Cos(pose.Rotation));
        float sin = MathF.Abs(MathF.Sin(pose.Rotation));
        HalfSize = new Vector2(cos * player.width + sin * player.height,
            sin * player.width + cos * player.height) / 2;
        Vector2 relative = player.GetModPlayer<YardMovementPlayer>().RelativeVelocityFor(yard, pose);
        Velocity = pose.WorldVectorToLocal(relative);
        Input = pose.WorldVectorToLocal(new Vector2((player.controlRight ? 1 : 0) - (player.controlLeft ? 1 : 0), 0));
        Gravity = pose.WorldVectorToLocal(new Vector2(0, player.gravDir));
    }

    public bool InReach(Point localTile, int rangeX, int rangeY)
    {
        // Yard collision stops the player a fraction of a pixel off the surface.
        // Keep that contact gap from excluding the last reachable tile row/column.
        float tolerance = YardCollisionGeometry.ContactTolerance / YardCoordinates.TileSize;
        Vector2 min = (Center - HalfSize) / YardCoordinates.TileSize - new Vector2(rangeX, rangeY);
        Vector2 max = (Center + HalfSize) / YardCoordinates.TileSize + new Vector2(rangeX - 1, rangeY - 2);
        return localTile.X >= min.X - tolerance && localTile.X <= max.X + tolerance
            && localTile.Y >= min.Y - tolerance && localTile.Y <= max.Y + tolerance;
    }
}
