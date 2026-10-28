using System;
using System.Reflection;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.Graphics.Light;
using Terraria.ID;
using Terraria.Utilities;

namespace TerrariaDynamics.Common.Lighting;

// Version-specific adapter: reuse intrinsic emitters, not ExportTo/GetTileLight,
// which include normal-world bounds and surface/underworld environment lighting.
internal sealed class YardLightScanner
{
    private delegate void ApplyEmitter(Tile tile, int x, int y, ref FastRandom random, ref Vector3 color);
    private delegate void ApplyLiquid(Tile tile, ref Vector3 color);
    private readonly TileLightScanner scanner = new();
    private readonly ApplyEmitter tileLight;
    private readonly ApplyEmitter wallLight;
    private readonly ApplyLiquid liquidLight;
    private readonly Func<Tile, LightMaskMode> tileMask;
    private static readonly FieldInfo ShowInvisible = typeof(TileLightScanner)
        .GetField("_drawInvisibleWalls", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new NotSupportedException("Unsupported tModLoader light scanner visibility field.");

    public YardLightScanner()
    {
        tileLight = Bind<ApplyEmitter>("ApplyTileLight");
        wallLight = Bind<ApplyEmitter>("ApplyWallLight");
        liquidLight = Bind<ApplyLiquid>("ApplyLiquidLight");
        tileMask = Bind<Func<Tile, LightMaskMode>>("GetTileMask");
    }

    private T Bind<T>(string name) where T : Delegate =>
        (typeof(TileLightScanner).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new NotSupportedException($"Unsupported tModLoader light scanner: {name}."))
        .CreateDelegate<T>(scanner);

    public void SetVisibility(bool showInvisible) => ShowInvisible.SetValue(scanner, showInvisible);

    public YardLightSample Sample(Tilemap tiles, Rectangle bounds, int localX, int localY, ulong tick)
    {
        int x = bounds.X + localX;
        int y = bounds.Y + localY;
        if (!bounds.Contains(x, y))
            throw new ArgumentOutOfRangeException(nameof(localX));
        Tile tile = tiles[x, y];
        var random = new FastRandom(tick).WithModifier(localX, localY);
        Vector3 emission = Vector3.Zero;
        wallLight(tile, x, y, ref random, ref emission);
        if (tile.HasTile && HasEmitterAnchor(tile, bounds, x, y))
            tileLight(tile, x, y, ref random, ref emission);
        liquidLight(tile, ref emission);
        return new YardLightSample(emission, tileMask(tile));
    }

    // Vanilla's Christmas tree emitter reads its root via Main.tile. Reject a
    // truncated/malformed root rather than reading another Yard or invalid memory.
    internal static bool HasEmitterAnchor(Tile tile, Rectangle bounds, int x, int y) =>
        tile.TileType != TileID.ChristmasTree || tile.TileFrameX >= 10
        || bounds.Contains(x - tile.TileFrameX, y - tile.TileFrameY);
}
