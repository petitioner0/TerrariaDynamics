using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent.Drawing;
using Terraria.ID;
using Terraria.ObjectData;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Rendering;

// Adapted from TileDrawing's grass/multitile/vine passes. Tile reads stay in the
// Yard; wind phase, player pushes, lighting and sprite pivots use display space.
// We do not enqueue distant coordinates into vanilla's global special-draw lists.
internal sealed class YardSwayRenderer(YardRenderContext context)
{
    private Yard region => context.Region;
    private readonly HashSet<Point> drawn = new();
    private TileDrawing Renderer => Main.instance.TilesRenderer;

    public bool TryDraw(Tile tile, int x, int y)
    {
        if (drawn.Contains(new Point(x, y)))
            return true;

        int type = tile.TileType;
        if (TileID.Sets.VineThreads[type] || TileID.Sets.ReverseVineThreads[type]) {
            DrawChain(x, y, TileID.Sets.ReverseVineThreads[type]);
            return true;
        }
        if (type == TileID.Cattail) {
            DrawCattail(x, y);
            return true;
        }
        if (type == TileID.LongMoss) {
            DrawDirectionalGrass(tile, x, y);
            return true;
        }
        if (TryGetObject(tile, x, y, out Rectangle bounds, out bool hanging)) {
            if (!region.Bounds.Contains(bounds))
                return false;
            DrawObject(bounds, type, hanging);
            return true;
        }
        if (!Renderer.ShouldSwayInWind(x, y, tile))
            return false;

        float wind = WindAt(new Rectangle(x, y, 1, 1), Renderer._grassWindCounter);
        wind += context.WindPush(x, y, 20, 0.35f).X;
        var pivot = context.LocalPixels(x, y) + new Vector2(8, 16);
        DrawCell(x, y, new YardSwayPose(pivot, new Vector2(wind, Math.Abs(wind)), wind * 0.1f));
        return true;
    }

    private void DrawDirectionalGrass(Tile tile, int x, int y)
    {
        float wind = WindAt(new Rectangle(x, y, 1, 1), Renderer._grassWindCounter);
        Vector2 push = context.WindPush(x, y, 20, 0.35f);
        Vector2 pivot = context.LocalPixels(x, y);
        Vector2 displacement;
        float rotation;
        switch (tile.TileFrameY / 54) {
            case 1: // ceiling
                pivot += new Vector2(8, 0);
                displacement = new Vector2(wind, -Math.Abs(wind));
                rotation = -wind - push.X;
                break;
            case 2: // left wall
                pivot += new Vector2(0, 8);
                displacement = new Vector2(-Math.Abs(wind), wind);
                rotation = wind + push.Y;
                break;
            case 3: // right wall
                pivot += new Vector2(16, 8);
                displacement = new Vector2(Math.Abs(wind), wind);
                rotation = -wind - push.Y;
                break;
            default:
                pivot += new Vector2(8, 16);
                displacement = new Vector2(wind, Math.Abs(wind));
                rotation = wind + push.X;
                break;
        }
        DrawCell(x, y, new YardSwayPose(pivot, displacement, rotation * 0.1f));
    }

    private void DrawCattail(int x, int y)
    {
        int top = y;
        int bottom = y + 1;
        while (top > region.Bounds.Top && IsCattail(x, top - 1))
            top--;
        while (bottom < region.Bounds.Bottom && IsCattail(x, bottom))
            bottom++;
        DrawObject(new Rectangle(x, top, 1, bottom - top), TileID.Cattail, false);
    }

    private bool IsCattail(int x, int y) => context.TileAt(x, y).HasTile
        && context.TileAt(x, y).TileType == TileID.Cattail;

    private float WindAt(Rectangle bounds, double counter)
    {
        for (int x = bounds.Left; x < bounds.Right; x++) {
            for (int y = bounds.Top; y < bounds.Bottom; y++) {
                Tile tile = context.TileAt(x, y);
                if (tile.LiquidAmount > 0 || !WallID.Sets.AllowsWind[tile.WallType])
                    return 0;
            }
        }
        return context.WindCycle(bounds.X, bounds.Y, counter);
    }

    private void DrawCell(int x, int y, YardSwayPose pose)
    {
        drawn.Add(new Point(x, y));
        Tile tile = context.TileAt(x, y);
        if (tile.HasTile && TileDrawing.IsVisible(tile))
            YardRenderer.DrawTile(context, tile, x, y, pose);
    }

    private void DrawObject(Rectangle bounds, int type, bool hanging)
    {
        // Cattails root in water; only their exposed top is used for wind blocking.
        Rectangle windBounds = type == TileID.Cattail ? new Rectangle(bounds.X, bounds.Y, 1, 1) : bounds;
        float wind = WindAt(windBounds, Renderer._sunflowerWindCounter);
        if (hanging && Main.SettingsEnabled_TilesSwayInWind)
            wind += context.ObjectWindPush(bounds);
        Vector2 pivot = context.LocalPixels(bounds.X, hanging ? bounds.Top : bounds.Bottom) + new Vector2(bounds.Width * 8, 0);
        float strength = hanging || type == TileID.Sunflower ? 0.07f : 0.15f;
        for (int x = bounds.Left; x < bounds.Right; x++) {
            for (int y = bounds.Top; y < bounds.Bottom; y++) {
                Tile tile = context.TileAt(x, y);
                if (!tile.HasTile || tile.TileType != type)
                    continue;
                float weight = hanging ? (float)(y - bounds.Top + 1) / bounds.Height
                    : Math.Max(0.1f, 1f - (float)(y - bounds.Top + 1) / bounds.Height);
                var displacement = new Vector2(0, Math.Abs(wind) * 2 * weight);
                DrawCell(x, y, new YardSwayPose(pivot, displacement,
                    wind * strength * weight * (hanging ? -1 : 1)));
            }
        }
    }

    private void DrawChain(int x, int y, bool reverse)
    {
        Rectangle bounds = YardSwayGeometry.FindChain(Main.tile, region.Bounds, x, y, reverse);
        int rootY = reverse ? bounds.Bottom - 1 : bounds.Top;
        int direction = reverse ? -1 : 1;
        Vector2 anchor = context.LocalPixels(x, reverse ? rootY + 1 : rootY) + new Vector2(8, 0);
        float wind = context.WindCycle(x, rootY, Renderer._vineWindCounter);
        float strength = MathHelper.Lerp(0.2f, 1f, Math.Abs(Main.WindForVisuals) / 1.2f);
        float bend = -0.08f * strength;
        int exposed = 0;
        float push = 0;
        float previousPush = 0;
        for (int i = 0; i < bounds.Height; i++) {
            int cellY = rootY + i * direction;
            Tile tile = context.TileAt(x, cellY);
            if (exposed >= 5)
                bend += 0.0075f * strength;
            if (exposed >= 2)
                bend += 0.0025f;
            if (WallID.Sets.AllowsWind[tile.WallType] && context.WindAtHeight(x, cellY))
                exposed++;
            float localPush = context.WindPush(x, cellY, reverse ? 40 : 20, reverse ? -0.004f : 0.01f).X;
            push = localPush != 0 || previousPush == 0 ? push - localPush : push * -0.78f;
            previousPush = localPush;
            float rotation = exposed * bend * wind * direction + push;
            Vector2 pivot = context.LocalPixels(x, reverse ? cellY + 1 : cellY) + new Vector2(8, 0);
            var pose = new YardSwayPose(pivot, anchor - pivot, rotation);
            DrawCell(x, cellY, pose);
            // The next segment starts exactly at this segment's transformed end.
            anchor = pose.Transform(pivot + new Vector2(0, direction * 16));
        }
    }

    private static bool TryGetObject(Tile tile, int x, int y, out Rectangle bounds, out bool hanging)
    {
        bounds = default;
        hanging = false;
        int width;
        int height;
        int frameYBase = 0;
        // Vanilla's special-draw classification, including objects without
        // TileObjectData (e.g. sunflowers).
        switch (tile.TileType) {
            case TileID.Sunflower: width = 2; height = 5; break;
            case TileID.LifeFruit: case TileID.PlanteraBulb: case TileID.AntlionLarva:
            case TileID.WeatherVane: case TileID.PlantDetritus2x2Echo:
                width = 2; height = 2; break;
            case TileID.PlantDetritus:
                width = tile.TileFrameY < 36 ? 3 : 2; height = 2;
                frameYBase = tile.TileFrameY < 36 ? 0 : 36; break;
            case TileID.OasisPlants when tile.TileFrameX < 270:
            case TileID.PlantDetritus3x2Echo: width = 3; height = 2; break;
            case TileID.PinWheel: width = 2; height = 3; break;
            case TileID.GolfCupFlag: width = 1; height = 2; break;
            case TileID.Chandeliers: width = height = 3; hanging = true; break;
            case TileID.Pigronata: width = 4; height = 3; hanging = true; break;
            case TileID.HangingLanterns: case TileID.FireflyinaBottle: case TileID.LightningBuginaBottle:
            case TileID.SoulBottles: case TileID.LavaflyinaBottle: case TileID.ShimmerflyinaBottle:
                width = 1; height = 2; hanging = true; break;
            case TileID.Banners: width = 1; height = 3; hanging = true; break;
            case TileID.ChineseLanterns: case TileID.DiscoBall: case TileID.BeeHive:
                width = height = 2; hanging = true; break;
            case TileID.WarTableBanner: case TileID.PotsSuspended: case TileID.BrazierSuspended:
                width = 2; height = 3; hanging = true; break;
            default:
                if (!TileID.Sets.MultiTileSway[tile.TileType])
                    return false;
                TileObjectData data = TileObjectData.GetTileData(tile);
                if (data == null || (data.AnchorTop.tileCount == 0 && data.AnchorBottom.tileCount == 0))
                    return false;
                var topLeft = TileObjectData.TopLeft(x, y);
                bounds = new Rectangle(topLeft.X, topLeft.Y, data.Width, data.Height);
                hanging = data.AnchorTop.tileCount > 0;
                return true;
        }
        bounds = YardSwayGeometry.RegularObject(x, y, tile, width, height, frameYBase);
        return true;
    }
}
