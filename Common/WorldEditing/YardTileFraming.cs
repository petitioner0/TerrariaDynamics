using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaDynamics.Common.Storage;

namespace TerrariaDynamics.Common.WorldEditing;

internal static class YardTileFraming
{
    // Beyond vanilla's five-cell guard; divisible by the common frame pattern
    // periods. The Yard allocator does not need to reserve any extra storage.
    private const int Padding = 12;

    public static void Refresh(Rectangle storageBounds)
    {
        FrameData[] frames = TileBackingStore.WithIsolatedFramingGrid(storageBounds, Padding, bounds => {
            for (int x = bounds.Left; x < bounds.Right; x++) {
                for (int y = bounds.Top; y < bounds.Bottom; y++) {
                    Tile tile = Main.tile[x, y];
                    // Furniture, trees and other frame-important objects keep
                    // their style/segment frames, including unsupported fragments.
                    if (ShouldReframe(tile))
                        WorldGen.TileFrame(x, y, resetFrame: false, noBreak: true);
                    Framing.WallFrame(x, y, resetFrame: false);
                }
            }

            var result = new FrameData[bounds.Width * bounds.Height];
            for (int x = 0; x < bounds.Width; x++) {
                for (int y = 0; y < bounds.Height; y++) {
                    Tile tile = Main.tile[bounds.X + x, bounds.Y + y];
                    result[x * bounds.Height + y] = new FrameData {
                        TileFrameX = tile.TileFrameX,
                        TileFrameY = tile.TileFrameY,
                        TileFrameNumber = tile.TileFrameNumber,
                        WallFrameX = tile.WallFrameX,
                        WallFrameY = tile.WallFrameY,
                        WallFrameNumber = tile.WallFrameNumber
                    };
                }
            }
            return result;
        });

        // Main.tile now refers to the real world again. Copy back frames only;
        // framing must never destroy/mutate the captured tiles or custom ITileData.
        for (int x = 0; x < storageBounds.Width; x++) {
            for (int y = 0; y < storageBounds.Height; y++) {
                Tile tile = Main.tile[storageBounds.X + x, storageBounds.Y + y];
                FrameData frame = frames[x * storageBounds.Height + y];
                if (ShouldReframe(tile)) {
                    tile.TileFrameX = frame.TileFrameX;
                    tile.TileFrameY = frame.TileFrameY;
                    tile.TileFrameNumber = frame.TileFrameNumber;
                }
                tile.WallFrameX = frame.WallFrameX;
                tile.WallFrameY = frame.WallFrameY;
                tile.WallFrameNumber = frame.WallFrameNumber;
            }
        }
    }

    // Platforms use frame-important style rows, but their horizontal connections
    // still need to end at the Yard boundary. Vanilla framing preserves the row.
    private static bool ShouldReframe(Tile tile) => tile.HasTile
        && (!Main.tileFrameImportant[tile.TileType] || TileID.Sets.Platforms[tile.TileType]);

    private struct FrameData
    {
        public short TileFrameX;
        public short TileFrameY;
        public int TileFrameNumber;
        public int WallFrameX;
        public int WallFrameY;
        public int WallFrameNumber;
    }
}
