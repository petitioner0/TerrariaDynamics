using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Drawing;
using Terraria.ID;

namespace TerrariaDynamics.Common.Rendering;

internal static class YardTreeRenderer
{
    private static bool TryAppearance(YardRenderContext context, Tile tile, int x, int y, out YardTreeAppearance appearance)
    {
        appearance = default;
        return context.Region.TreeAppearances.TryGetValue(context.Coordinates.StorageToLocalTile(new Point(x, y)), out var saved)
            && saved.TreeType == tile.TileType && saved.TryResolve(out appearance);
    }

    public static void DrawTrunk(YardRenderContext context, Tile tile, int x, int y)
    {
        if (tile.TileType == TileID.PalmTree && YardTreeGeometry.IsFoliage(tile))
            return;
        if (!TryAppearance(context, tile, x, y, out var appearance)) return;
        int style = appearance.TrunkStyle;
        int frameX = tile.TileFrameX;
        int frameY = tile.TileFrameY;
        int lean = 0;
        if (tile.TileType == TileID.Trees) {
            if (style < 7)
                frameX += 176 * (style + 1);
        }
        else if (tile.TileType == TileID.PalmTree) {
            frameY = 22 * (Math.Abs(style) >= 8 ? (style < 0 ? 1 : 0) : style);
            lean = tile.TileFrameY;
        }
        Main.instance.LoadTiles(tile.TileType);
        Texture2D texture = Main.instance.TilePaintSystem.TryGetTileAndRequestIfNotReady(tile.TileType, style, tile.TileColor)
            ?? TextureAssets.Tile[tile.TileType].Value;
        Draw(context, texture, context.LocalPixels(x, y) + new Vector2(-2 + lean, 0),
            new Rectangle(frameX, frameY, 20, 20), ColorAt(context, tile, x, y), 0, Vector2.Zero);
    }

    // Run after trunks, like vanilla's special tree pass. Never register Yard
    // coordinates in the world's special-position arrays or lighting engine.
    public static void DrawFoliage(YardRenderContext context, Rectangle visible)
    {
        for (int x = visible.Left; x < visible.Right; x++) {
            for (int y = visible.Top; y < visible.Bottom; y++) {
                Tile tile = context.TileAt(x, y);
                Vector2 local = context.LocalPixels(x, y);
                if (!tile.HasTile || !TileDrawing.IsVisible(tile))
                    continue;
                if (tile.TileType == TileID.MushroomTrees && tile.TileFrameX >= 36) {
                    int mushroomFrame = tile.TileFrameY == 18 ? 1 : tile.TileFrameY == 36 ? 2 : 0;
                    Draw(context, TextureAssets.ShroomCap.Value, local + new Vector2(-22, -26),
                        new Rectangle(mushroomFrame * 62, 0, 60, 42), ColorAt(context, tile, x, y), 0, Vector2.Zero);
                    continue;
                }
                if (!YardTreeGeometry.IsTree(tile.TileType) || !YardTreeGeometry.IsFoliage(tile))
                    continue;
                if (!TryAppearance(context, tile, x, y, out var appearance)) continue;
                float wind = tile.WallType == WallID.None
                    ? context.WindCycle(x, y, context.Renderer._treeWindCounter) : 0;
                Color color = ColorAt(context, tile, x, y);
                if (tile.TileType == TileID.PalmTree) {
                    DrawPalm(context, tile, appearance.TrunkStyle, x, y, wind, color);
                    continue;
                }

                int frame = WorldGen.GetTreeFrame(tile) + appearance.FrameOffset;
                int style = appearance.FoliageStyle;
                int width = appearance.CrownWidth;
                int height = appearance.CrownHeight;
                bool top = tile.TileFrameX == 22;
                Texture2D texture = FoliageTexture(top, style, 0, tile.TileColor);
                Rectangle source;
                Vector2 position;
                Vector2 origin;
                if (top) {
                    source = new Rectangle(frame * (width + 2), 0, width, height);
                    position = local + new Vector2(8 + wind * 2, 16 + Math.Abs(wind) * 2);
                    origin = new Vector2(width / 2, height);
                }
                else if (tile.TileFrameX == 44) {
                    source = new Rectangle(0, frame * 42, 40, 40);
                    position = local + new Vector2(16 + Math.Max(0, wind) + Math.Abs(wind) * 2, 12);
                    origin = new Vector2(40, 24);
                }
                else {
                    source = new Rectangle(42, frame * 42, 40, 40);
                    position = local + new Vector2(Math.Min(0, wind) - Math.Abs(wind) * 2, 18);
                    origin = new Vector2(0, 30);
                }
                float rotation = wind * (top ? 0.08f : 0.06f);
                Draw(context, texture, position, source, color, rotation, origin);
                if (tile.TileType == TileID.TreeAsh)
                    Draw(context, TextureAssets.GlowMask[top ? 316 : 317].Value, position, source,
                        tile.IsActuated ? Color.White * 0.4f : Color.White, rotation, origin);
            }
        }
    }

    private static void DrawPalm(YardRenderContext context, Tile tile, int biome, int x, int y, float wind, Color color)
    {
        bool oasis = biome is >= 4 and <= 7 || biome < 0;
        int width = oasis ? 114 : 80;
        int height = oasis ? 98 : 80;
        int textureIndex = oasis ? 21 : 15;
        int row = oasis ? (biome - 4) * 98 : biome * 82;
        if (Math.Abs(biome) >= 8) {
            row = 0;
            textureIndex = -2 * (Math.Abs(biome) - 8) - (oasis ? 1 : 0);
        }
        Texture2D texture = FoliageTexture(true, textureIndex, biome, tile.TileColor);
        int frame = (tile.TileFrameX - 88) / 22;
        Vector2 position = context.LocalPixels(x, y) + new Vector2(-(oasis ? 48 : 32) + tile.TileFrameY + width / 2 + wind * 2,
            16 + (oasis ? 2 : 0) + Math.Abs(wind) * 2);
        Draw(context, texture, position, new Rectangle(frame * (width + 2), row, width, height),
            color, wind * 0.08f, new Vector2(width / 2, height));
    }

    private static Texture2D FoliageTexture(bool top, int style, int variant, byte paint)
    {
        var system = Main.instance.TilePaintSystem;
        Texture2D painted = top ? system.TryGetTreeTopAndRequestIfNotReady(style, variant, paint)
            : system.TryGetTreeBranchAndRequestIfNotReady(style, variant, paint);
        var textures = top ? TextureAssets.TreeTop : TextureAssets.TreeBranch;
        return painted ?? textures[style >= 0 && style < textures.Length ? style : 0].Value;
    }

    private static Color ColorAt(YardRenderContext context, Tile tile, int x, int y)
    {
        Color color = tile.IsTileFullbright ? Color.White : context.Light(x, y);
        return tile.IsActuated ? color * 0.4f : color;
    }

    private static void Draw(YardRenderContext context, Texture2D texture, Vector2 position, Rectangle source, Color color,
        float rotation, Vector2 origin)
    {
        if (source.X < 0 || source.Y < 0 || source.Right > texture.Width || source.Bottom > texture.Height)
            return;
        context.Draw(texture, position, source, color, rotation, origin);
    }
}
