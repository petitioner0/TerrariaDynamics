using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.Drawing;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Rendering;

internal static class YardTreeAppearanceResolver
{
    // Capture from the source BEFORE clearing it, including soil outside the
    // selection.
    public static void CaptureAppearances(Yard region)
    {
        Rectangle selected = region.Source;
        Rectangle search = new(0, 0, Main.maxTilesX, Main.maxTilesY);
        var captured = new Dictionary<Point, YardTreeAppearance>();
        for (int x = selected.Left; x < selected.Right; x++) {
            for (int y = selected.Top; y < selected.Bottom; y++) {
                Tile tile = Main.tile[x, y];
                if (!tile.HasTile || !YardTreeGeometry.IsTree(tile.TileType)) continue;
                Point local = new(x - selected.X, y - selected.Y);
                int originalX = region.Source.X + local.X;
                Point? ground = YardTreeGeometry.FindGround(Main.tile, search, x, y, tile);
                int trunk = tile.TileType == TileID.Trees
                    ? (ground is Point p ? TileDrawing.GetTreeVariant(p.X, p.Y) : -1)
                    : tile.TileType == TileID.PalmTree ? PalmBiome(ground, originalX) : 0;
                int baseFrame = WorldGen.GetTreeFrame(tile);
                int frame = baseFrame;
                GetFoliageStyle(tile, ground, originalX,
                    ref frame, out int style, out int width, out int height);
                if (tile.TileType == TileID.Trees && YardTreeGeometry.IsFoliage(tile) && ground.HasValue) {
                    // Native source-space resolution also supplies custom ModTree dimensions.
                    int nativeFrame = baseFrame;
                    int nativeStyle = 0;
                    if (WorldGen.GetCommonTreeFoliageData(x, y, YardTreeGeometry.TrunkX(x, tile) - x,
                        ref nativeFrame, ref nativeStyle, out _, out int nativeWidth, out int nativeHeight)) {
                        frame = nativeFrame; style = nativeStyle; width = nativeWidth; height = nativeHeight;
                    }
                }
                string modGround = null;
                if (ground is Point g) {
                    int groundType = Main.tile[g.X, g.Y].TileType;
                    if (tile.TileType == TileID.Trees && TileLoader.CanGrowModTree(groundType)
                        || tile.TileType == TileID.PalmTree && TileLoader.CanGrowModPalmTree(groundType))
                        modGround = TileLoader.GetTile(groundType)?.FullName;
                }
                captured.Add(local, new YardTreeAppearance(tile.TileType, trunk, style, frame - baseFrame, width, height, modGround ?? ""));
            }
        }
        region.TreeAppearances.Clear();
        foreach (var pair in captured) region.TreeAppearances.Add(pair.Key, pair.Value);
    }

    private static void GetFoliageStyle(Tile tile, Point? ground, int worldX,
        ref int frame, out int style, out int width, out int height)
    {
        style = 0;
        width = height = 80;
        if (tile.TileType >= TileID.TreeTopaz && tile.TileType <= TileID.TreeAmber) {
            style = 22 + tile.TileType - TileID.TreeTopaz;
            width = 116; height = 96;
            return;
        }
        if (tile.TileType == TileID.TreeAsh) {
            style = 31; width = 116; height = 96;
            return;
        }
        if (tile.TileType == TileID.VanityTreeSakura || tile.TileType == TileID.VanityTreeYellowWillow) {
            style = tile.TileType == TileID.VanityTreeSakura ? 29 : 30;
            width = 118; height = 96;
            return;
        }
        // A detached tree fragment has no soil to identify its biome; use the
        // local forest variant instead of scanning another Yard or the old world.
        int groundType = ground is Point p ? Main.tile[p.X, p.Y].TileType : TileID.Grass;
        int groundX = ground is Point g ? g.X : worldX;
        switch (groundType) {
            case TileID.Grass: case TileID.GolfGrass:
                style = YardTreeGeometry.ForestStyle(groundX, Main.treeX, WorldGen.TreeTops.GetTreeStyle);
                break;
            case TileID.CorruptGrass: case TileID.CorruptJungleGrass: style = 1; break;
            case TileID.CrimsonGrass: case TileID.CrimsonJungleGrass: style = 5; break;
            case TileID.MushroomGrass: style = 14; break;
            case TileID.JungleGrass:
                style = 2; width = 114; height = 96;
                if (WorldGen.TreeTops.GetTreeStyle(5) == 1) { style = 11; width = 116; }
                if (ground!.Value.Y > Main.worldSurface) { style = 13; width = 116; }
                break;
            case TileID.SnowBlock:
                style = 4;
                int snow = WorldGen.TreeTops.GetTreeStyle(6);
                if (snow == 0) style = worldX % 10 == 0 ? 18 : 12;
                if (snow is 2 or 3 or 32 or 4 or 42 or 5 or 7)
                    style = snow % 2 == 0 ? (worldX < Main.maxTilesX / 2 ? 16 : 17)
                        : (worldX > Main.maxTilesX / 2 ? 16 : 17);
                break;
            case TileID.HallowedGrass: case TileID.GolfGrassHallowed:
                style = WorldGen.GetHollowTreeFoliageStyle();
                height = 140;
                if (style == 19) width = 120;
                frame += 3 * (worldX % (style == 20 ? 6 : 3));
                break;
            default:
                // Custom tree textures use the same paint-system key as tML.
                if (TileLoader.CanGrowModTree(groundType)) style = groundType + 100;
                break;
        }
    }

    private static int PalmBiome(Point? ground, int worldX)
    {
        int type = ground is Point p ? Main.tile[p.X, p.Y].TileType : TileID.Sand;
        bool oasis = WorldGen.IsPalmOasisTree(worldX);
        if (TileLoader.CanGrowModPalmTree(type))
            return (8 + type) * (oasis ? -1 : 1);
        int biome = type switch { TileID.Crimsand => 1, TileID.Pearlsand => 2, TileID.Ebonsand => 3, _ => 0 };
        return biome + (oasis ? 4 : 0);
    }

}
