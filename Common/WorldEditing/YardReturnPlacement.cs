using Microsoft.Xna.Framework;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.WorldEditing;

internal enum YardReturnStatus
{
    Waiting,
    Moved,
    Ready
}

// Stability is measured in world tiles, so sub-tile jitter and changes in visual
// rotation do not prevent returning a structure to its original upright layout.
internal sealed class YardReturnPlacement(YardCoordinates coordinates)
{
    public const int StableTicks = 12;
    public Point Anchor { get; } = coordinates.NearestWorldAnchorTile;
    private int elapsedTicks;

    public Vector2 WorldPosition => new(Anchor.X * (float)YardCoordinates.TileSize,
        Anchor.Y * (float)YardCoordinates.TileSize);

    public YardReturnStatus Tick(YardCoordinates current)
    {
        if (current.NearestWorldAnchorTile != Anchor)
            return YardReturnStatus.Moved;
        return ++elapsedTicks >= StableTicks ? YardReturnStatus.Ready : YardReturnStatus.Waiting;
    }
}
