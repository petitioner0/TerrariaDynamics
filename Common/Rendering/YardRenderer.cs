using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent;
using Terraria.GameContent.Drawing;
using Terraria.GameContent.UI;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaDynamics.Common.Players;
using TerrariaDynamics.Common.TileYards;
using TerrariaDynamics.Content.Tiles;

namespace TerrariaDynamics.Common.Rendering;

internal static class YardRenderer
{
    // Main.DoDraw calls SystemLoader.PostDrawTiles after ending the tile batch,
    // before projectiles and the main player pass. This is tML's existing render
    // patch point: own Begin/End, no camera swapping or extra IL patch needed.
    public static void Draw(YardSystem system)
    {
        if (Main.gameMenu || Main.dedServ)
            return;
        var selection = Main.LocalPlayer.GetModPlayer<BlueprintSelectionPlayer>();
        if ((!system.RenderEnabled || system.Regions.Count == 0) && !selection.HasSelection)
            return;

        Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp,
            DepthStencilState.None, Main.Rasterizer, null, Main.GameViewMatrix.TransformationMatrix);
        try {
            if (system.RenderEnabled) {
                foreach (Yard region in system.Regions) {
                    if (region.IsLoaded)
                        DrawRegion(region);
                }
            }
            if (selection.HasSelection && !Main.mapFullscreen) {
                Point cursor = Main.MouseWorld.ToTileCoordinates();
                cursor.X = Math.Clamp(cursor.X, 1, Main.maxTilesX - 2);
                cursor.Y = Math.Clamp(cursor.Y, 1, Main.maxTilesY - 2);
                Rectangle bounds = selection.RectangleTo(cursor);
                Color color = (long)bounds.Width * bounds.Height <= Yard.MaxSelectionArea
                    ? Color.Cyan : Color.OrangeRed;
                DrawOutline(bounds, color);
            }
        }
        finally {
            Main.spriteBatch.End();
        }
    }

    private static void DrawRegion(Yard region)
    {
        var context = new YardRenderContext(region);
        Rectangle visible = context.VisibleArea();
        YardSwayRenderer sway = null;
        for (int x = visible.Left; x < visible.Right; x++) {
            for (int y = visible.Top; y < visible.Bottom; y++) {
                Tile tile = context.TileAt(x, y);
                if (!tile.HasTile || !TileDrawing.IsVisible(tile))
                    continue;
                if (YardTreeGeometry.IsTree(tile.TileType)) {
                    YardTreeRenderer.DrawTrunk(context, tile, x, y);
                    continue;
                }
                sway ??= new YardSwayRenderer(context);
                if (sway.TryDraw(tile, x, y))
                    continue;
                DrawTile(context, tile, x, y);
            }
        }
        YardTreeRenderer.DrawFoliage(context, visible);
    }

    // Called after DoDraw_WallsAndBlacks, while its world SpriteBatch is active.
    // Draw directly after compositing the vanilla wall target, so this works with
    // both direct drawing and render targets, and never overlays foreground tiles.
    public static void DrawWalls(YardSystem system)
    {
        if (Main.gameMenu || Main.dedServ || !system.RenderEnabled)
            return;
        foreach (Yard region in system.Regions) {
            if (!region.IsLoaded)
                continue;
            var context = new YardRenderContext(region);
            Rectangle visible = context.VisibleArea();
            for (int x = visible.Left; x < visible.Right; x++) {
                for (int y = visible.Top; y < visible.Bottom; y++) {
                    Tile tile = context.TileAt(x, y);
                    if (tile.WallType == WallID.None || (tile.IsWallInvisible && !Main.ShouldShowInvisibleWalls()))
                        continue;
                    Main.instance.LoadWall(tile.WallType);
                    Texture2D texture = Main.instance.TilePaintSystem.TryGetWallAndRequestIfNotReady(tile.WallType, tile.WallColor)
                        ?? TextureAssets.Wall[tile.WallType].Value;
                    var frame = new Rectangle(tile.WallFrameX, tile.WallFrameY + Main.wallFrame[tile.WallType] * 180, 32, 32);
                    if (frame.X < 0 || frame.Y < 0 || frame.Right > texture.Width || frame.Bottom > texture.Height)
                        continue;
                    Color color = tile.IsWallFullbright ? Color.White : context.Light(x, y);
                    context.Draw(texture, context.LocalPixels(x, y) - new Vector2(8), frame, color);
                }
            }
        }
    }

    // Invoked only when Terraria itself draws its wire overlay. It inherits the
    // caller's SpriteBatch and respects mechanical-view visibility settings.
    public static void DrawWires(YardSystem system)
    {
        if (Main.gameMenu || Main.dedServ || !system.RenderEnabled)
            return;
        Player player = Main.LocalPlayer;
        foreach (Yard region in system.Regions) {
            if (!region.IsLoaded)
                continue;
            var context = new YardRenderContext(region);
            Rectangle visible = context.VisibleArea();
            for (int x = visible.Left; x < visible.Right; x++) {
                for (int y = visible.Top; y < visible.Bottom; y++) {
                    Point world = context.WorldCell(x, y);
                    if (!WorldGen.InWorld(world.X, world.Y) || !player.CanDoWireStuffHere(world.X, world.Y))
                        continue;
                    Tile tile = context.TileAt(x, y);
                    Vector2 position = context.LocalPixels(x, y);
                    int junction = tile.HasTile && tile.TileType == TileID.WirePipe ? (tile.TileFrameX / 18 + 1) * 72
                        : tile.HasTile && tile.TileType == TileID.PixelBox ? 72 : 0;
                    int drawn = 0;
                    if (!WiresUI.Settings.HideWires) {
                        for (int wire = 0; wire < 4; wire++) {
                            if (!HasWire(tile, wire))
                                continue;
                            Color color = WireColor(context, player, x, y, wire + 4);
                            if (color == Color.Transparent)
                                continue;
                            int mask = (WireAt(context, x, y - 1, wire) ? 1 : 0)
                                | (WireAt(context, x + 1, y, wire) ? 2 : 0)
                                | (WireAt(context, x, y + 1, wire) ? 4 : 0)
                                | (WireAt(context, x - 1, y, wire) ? 8 : 0);
                            var frame = new Rectangle(mask * 18, junction + wire * 18, 16, 16);
                            context.Draw(TextureAssets.WireNew.Value, position, frame, color * (1f / ++drawn));
                        }
                    }
                    if (tile.HasActuator) {
                        Color color = WireColor(context, player, x, y, 9);
                        if (WiresUI.Settings.HideWires)
                            color *= 0.5f;
                        context.Draw(TextureAssets.Actuator.Value, position, null, color);
                    }
                }
            }
        }
    }

    private static bool WireAt(YardRenderContext context, int x, int y, int wire) =>
        context.Coordinates.StorageBounds.Contains(x, y) && HasWire(context.TileAt(x, y), wire);

    private static bool HasWire(Tile tile, int wire) => wire switch {
        0 => tile.RedWire, 1 => tile.BlueWire, 2 => tile.GreenWire, _ => tile.YellowWire
    };

    private static Color WireColor(YardRenderContext context, Player player, int x, int y, int statusIndex)
    {
        int status = player.InfoAccMechShowWires ? player.builderAccStatus[statusIndex] : 1;
        return status switch {
            0 => Color.White, 2 => context.Light(x, y) * 0.5f,
            3 => Color.Transparent, _ => context.Light(x, y)
        };
    }

    internal static void DrawTile(YardRenderContext context, Tile tile, int yardX, int yardY,
        YardSwayPose? sway = null)
    {
        Main.instance.LoadTiles(tile.TileType);
        Texture2D texture = TextureAssets.Tile[tile.TileType].Value;
        // Trees have a separate renderer. Cactus variants and arbitrary SpecialDraw
        // still require their own adapters.
        Texture2D painted = Main.instance.TilePaintSystem.TryGetTileAndRequestIfNotReady(tile.TileType, 0, tile.TileColor);
        if (painted != null)
            texture = painted;
        // Circuit state is replicated separately from stored tile frames, so
        // switching artwork never changes placement direction or saved tiles.
        if (TileLoader.GetTile(tile.TileType) is YardThrusterTile thruster
            && thruster.ActiveTexture(yardX, yardY) is Texture2D activeTexture)
            texture = activeTexture;
        else if (TileLoader.GetTile(tile.TileType) is YardAnchorTile anchor
            && anchor.ActiveTexture(yardX, yardY) is Texture2D anchorTexture)
            texture = anchorTexture;

        short frameX = tile.TileFrameX;
        short frameY = tile.TileFrameY;
        // Prepare the cache before vanilla's helper performs its own glow reads.
        Color light = context.Light(yardX, yardY);
        Main.instance.TilesRenderer.GetTileDrawData(yardX, yardY, tile, tile.TileType,
            ref frameX, ref frameY, out int width, out int height, out int top, out int halfHeight,
            out int addX, out int addY, out SpriteEffects effects,
            out Texture2D glowTexture, out Rectangle glowSource, out Color glowColor);

        // Lighting comes from this Yard's local map, independent of display pose.
        Color color = tile.IsTileFullbright ? Color.White : light;
        if (tile.IsActuated)
            color *= 0.4f;
        Vector2 position = context.LocalPixels(yardX, yardY) + new Vector2(-(width - 16) / 2f, top + halfHeight);
        var source = new Rectangle(frameX + addX, frameY + addY, width, height - halfHeight);
        if (source.X < 0 || source.Y < 0 || source.Right > texture.Width || source.Bottom > texture.Height
            || source.Width <= 0 || source.Height <= 0)
            return;

        if (sway is YardSwayPose pose) {
            Vector2 origin = pose.Pivot - position;
            Vector2 anchor = pose.Pivot + pose.Displacement;
            context.Draw(texture, anchor, source, color, pose.Rotation, origin, effects);
            if (glowTexture != null && glowSource.Width > 0 && glowSource.Height > 0)
                context.Draw(glowTexture, anchor, glowSource, glowColor, pose.Rotation, origin, effects);
            return;
        }

        if (tile.Slope != SlopeType.Solid && width == 16 && height == 16) {
            // Clip to the four Terraria slope triangles, preserving the stored frame.
            for (int column = 0; column < 16; column += 2) {
                int cut = tile.Slope == SlopeType.SlopeDownLeft || tile.Slope == SlopeType.SlopeUpLeft
                    ? column : 14 - column;
                bool lowerTriangle = tile.Slope == SlopeType.SlopeDownLeft || tile.Slope == SlopeType.SlopeDownRight;
                int offsetY = lowerTriangle ? cut : 0;
                var slice = new Rectangle(source.X + column, source.Y + offsetY, 2, 16 - cut);
                context.Draw(texture, position + new Vector2(column, offsetY), slice, color, effects: effects);
            }
        }
        else
            context.Draw(texture, position, source, color, effects: effects);

        if (glowTexture != null && glowSource.Width > 0 && glowSource.Height > 0)
            context.Draw(glowTexture, position, glowSource, glowColor, effects: effects);
    }

    private static void DrawOutline(Rectangle tiles, Color color)
    {
        var rect = new Rectangle((int)(tiles.X * 16 - Main.screenPosition.X),
            (int)(tiles.Y * 16 - Main.screenPosition.Y), tiles.Width * 16, tiles.Height * 16);
        Texture2D pixel = TextureAssets.MagicPixel.Value;
        Main.spriteBatch.Draw(pixel, rect, color * 0.08f);
        Main.spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, rect.Width, 2), color);
        Main.spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Bottom - 2, rect.Width, 2), color);
        Main.spriteBatch.Draw(pixel, new Rectangle(rect.X, rect.Y, 2, rect.Height), color);
        Main.spriteBatch.Draw(pixel, new Rectangle(rect.Right - 2, rect.Y, 2, rect.Height), color);
    }
}
