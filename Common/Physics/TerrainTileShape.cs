namespace TerrariaDynamics.Common.Physics;

// Collision geometry only: tile types, frames, walls and liquids are not cached.
internal enum TerrainTileShape : byte
{
    None,
    Solid,
    HalfBlock,
    DownRight,
    DownLeft,
    UpRight,
    UpLeft
}
