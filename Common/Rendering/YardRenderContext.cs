using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;
using Terraria.GameContent.Drawing;
using TerrariaDynamics.Common.Lighting;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Rendering;

// The bridge from stored cells to Yard lighting, world wind and screen drawing.
// Callers pass storage TILE coordinates to queries and LOCAL PIXELS to Draw.
internal readonly struct YardRenderContext(Yard region)
{
    public Yard Region { get; } = region;
    public YardCoordinates Coordinates { get; } = region.RenderCoordinates;
    public TileDrawing Renderer => Main.instance.TilesRenderer;
    public Tile TileAt(int x, int y) => Coordinates.StorageBounds.Contains(x, y)
        ? Main.tile[x, y] : throw new ArgumentOutOfRangeException(nameof(x), "Tile query outside Yard.");
    public Vector2 LocalPixels(int x, int y) => Coordinates.StorageToLocalPixels(x, y);
    public Point WorldCell(int x, int y) => Coordinates.StorageTileToWorldCell(x, y);

    public Rectangle VisibleArea()
    {
        Vector2 zoom = Main.GameViewMatrix.Zoom;
        Vector2 screenSize = new(Main.screenWidth, Main.screenHeight);
        Vector2 extra = new(Math.Max(0, screenSize.X / Math.Max(0.1f, zoom.X) - screenSize.X) / 2,
            Math.Max(0, screenSize.Y / Math.Max(0.1f, zoom.Y) - screenSize.Y) / 2);
        return Coordinates.VisibleStorageArea(Main.screenPosition - extra, Main.screenPosition + screenSize + extra);
    }

    public Color Light(int x, int y) => YardLighting.GetColor(Region, x, y);

    public bool WindAtHeight(int x, int y) => WorldGen.DoesWindBlowAtThisHeight(WorldCell(x, y).Y);
    public float WindCycle(int x, int y, double counter)
    {
        Point world = WorldCell(x, y);
        // World wind is horizontal. Project it onto the structure's local X axis.
        return Renderer.GetWindCycle(world.X, world.Y, counter) * MathF.Cos(Coordinates.Rotation);
    }

    public Vector2 WindPush(int x, int y, int duration, float force)
    {
        Point world = WorldCell(x, y);
        if (!Main.SettingsEnabled_TilesSwayInWind || !WorldGen.InWorld(world.X, world.Y))
            return Vector2.Zero;
        Renderer.Wind.GetWindTime(world.X, world.Y, duration, out int time, out int dx, out int dy);
        return Coordinates.WorldVectorToLocal(new Vector2(dx, dy) * (Math.Min(time, duration - time) * force));
    }

    public float ObjectWindPush(Rectangle storageBounds)
    {
        float strongest = 0;
        for (int x = storageBounds.Left; x < storageBounds.Right; x++) {
            for (int y = storageBounds.Top; y < storageBounds.Bottom; y++) {
                Point world = WorldCell(x, y);
                if (!WorldGen.InWorld(world.X, world.Y)) continue;
                float push = Renderer.GetWindGridPushComplex(world.X, world.Y, 60, 1.26f, 3, true);
                if (Math.Abs(push) > Math.Abs(strongest)) strongest = push;
            }
        }
        return strongest * MathF.Cos(Coordinates.Rotation);
    }

    public void Draw(Texture2D texture, Vector2 localPosition, Rectangle? source, Color color,
        float rotation = 0, Vector2 origin = default, SpriteEffects effects = SpriteEffects.None)
    {
        Main.spriteBatch.Draw(texture, Coordinates.LocalToWorld(localPosition) - Main.screenPosition,
            source, color, rotation + Coordinates.Rotation, origin, 1, effects, 0);
    }
}
