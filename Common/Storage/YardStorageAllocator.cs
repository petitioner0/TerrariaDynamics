using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Storage;

// Online, non-rotating rectangle packing: first fit in X-major/Y-minor order.
// Existing rectangles never move, preserving every live Tile handle.
internal static class YardStorageAllocator
{
    public const int StartX = 10000;
    public const int CoordinateLimit = short.MaxValue + 1;
    // One disposable air cell serves native neighbor reads outside an active
    // Yard. It is runtime-only and never belongs to a saved Yard rectangle.
    internal static Point? ReservedTile { get; set; }

    public static bool TryAllocate(Point size, IEnumerable<Rectangle> occupied,
        int playableWidth, int storageHeight, out Point origin)
    {
        origin = default;
        int left = Math.Max(StartX, playableWidth);
        int bottom = Math.Min(storageHeight, CoordinateLimit);
        if (playableWidth < 0 || size.X <= 0 || size.Y <= 0 || size.Y > bottom
            || (long)size.X * size.Y > Yard.MaxSelectionArea
            || (long)left + size.X > CoordinateLimit)
            return false;

        if (ReservedTile is Point reserved)
            occupied = occupied.Append(new Rectangle(reserved.X, reserved.Y, 1, 1));
        Rectangle[] rectangles = occupied.OrderBy(rect => rect.Top).ToArray();
        var columns = new SortedSet<int> { left };
        foreach (Rectangle rect in rectangles) {
            if (rect.Right >= left && (long)rect.Right + size.X <= CoordinateLimit)
                columns.Add(rect.Right);
        }

        // A newly available X can only occur at an occupied rectangle's right
        // edge. At that X, jump past overlapping Y intervals instead of scanning
        // every tile. Sorting once gives O(n^2) worst case, independent of area.
        foreach (int x in columns) {
            int y = 0;
            foreach (Rectangle rect in rectangles) {
                if (rect.Left >= x + size.X || rect.Right <= x)
                    continue;
                if ((long)y + size.Y <= rect.Top)
                    break;
                y = Math.Max(y, rect.Bottom);
                if ((long)y + size.Y > bottom)
                    break;
            }
            if ((long)y + size.Y <= bottom) {
                origin = new Point(x, y);
                return true;
            }
        }
        return false;
    }
}
