using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;

namespace TerrariaDynamics.Common.Storage;

internal static partial class TileBackingStore
{
    private static readonly FieldInfo HeightField = typeof(Tilemap).GetField(nameof(Tilemap.Height));

    // Vanilla framing rejects storage addresses outside maxTilesX/Y and reads its
    // neighbors through Main.tile. Give it a padded COPY instead of the packed
    // storage grid. This never changes real-world tile IDs or the physical stride.
    // The caller must hold WorldFile.IOLock and run synchronously, outside drawing.
    public static T WithIsolatedFramingGrid<T>(Rectangle bounds, int padding, Func<Rectangle, T> frame)
    {
        Tilemap originalMap = Main.tile;
        if (padding < 6 || bounds.Width < 1 || bounds.Height < 1
            || bounds.Left < 0 || bounds.Top < 0
            || (long)bounds.Right > originalMap.Width || (long)bounds.Bottom > originalMap.Height)
            throw new ArgumentOutOfRangeException(nameof(bounds));

        int width = checked(bounds.Width + padding * 2);
        int height = checked(bounds.Height + padding * 2);
        int length = checked(width * height);
        object scratchMap = originalMap; // Do NOT call Tilemap's destructive constructor.
        WidthField.SetValue(scratchMap, checked((ushort)width));
        HeightField.SetValue(scratchMap, checked((ushort)height));
        uint originalLength = (uint)LengthField.GetValue(null);
        int originalMaxX = Main.maxTilesX;
        int originalMaxY = Main.maxTilesY;
        bool originalMapEnabled = Main.mapEnabled;
        bool originalTileActions = WorldGen.noTileActions;
        HashSet<Type> originalBuffers = InitializedBuffers();
        var prepared = new List<BufferSwap>();
        try {
            // Copy ALL initialized ITileData, including other mods' data. The
            // surrounding cells start empty, even when another Yard abuts bounds.
            foreach (Type owner in originalBuffers) {
                prepared.Add(new BufferSwap(owner, length, (source, target) => {
                    for (int x = 0; x < bounds.Width; x++) {
                        Array.Copy(source, (bounds.X + x) * originalMap.Height + bounds.Y,
                            target, (x + padding) * height + padding, bounds.Height);
                    }
                }));
            }

            try {
                foreach (BufferSwap swap in prepared)
                    swap.Install();
                LengthField.SetValue(null, (uint)length);
                Main.tile = (Tilemap)scratchMap;
                Main.maxTilesX = width;
                Main.maxTilesY = height;
                Main.mapEnabled = false;
                WorldGen.noTileActions = true;
                return frame(new Rectangle(padding, padding, bounds.Width, bounds.Height));
            }
            finally {
                // Framing never commits TileData; the caller extracts frames.
                foreach (BufferSwap swap in prepared)
                    swap.Restore();
                LengthField.SetValue(null, originalLength);
                Main.tile = originalMap;
                Main.maxTilesX = originalMaxX;
                Main.maxTilesY = originalMaxY;
                Main.mapEnabled = originalMapEnabled;
                WorldGen.noTileActions = originalTileActions;

                // A callback can initialize a previously unused ITileData<T>.
                // Restore the real length and retain registration with empty data.
                foreach (Type owner in InitializedBuffers()) {
                    if (originalBuffers.Contains(owner))
                        continue;
                    var swap = new BufferSwap(owner, checked((int)originalLength), copy: null);
                    bool installed = false;
                    try {
                        swap.Install();
                        installed = true;
                    }
                    catch {
                        swap.Restore();
                        throw;
                    }
                    finally {
                        swap.Release(installed);
                    }
                }
            }
        }
        finally {
            foreach (BufferSwap swap in prepared)
                swap.Release(committed: false);
        }
    }
}
