using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Terraria;

namespace TerrariaDynamics.Common.Rendering;

// Separate native layer caches keep the enlarged world draw out of Terraria's
// normal-size targets. Swapping also preserves each set's cached world origins.
internal sealed class YardSceneTargets : IDisposable
{
    private readonly RenderTarget2D[] layers = new RenderTarget2D[9];
    private SceneState state = new() { RenderNow = true };
    private bool swapped;
    public RenderTarget2D Scene { get; }
    public int Size { get; }
    public int Margin { get; }
    public bool CachedTiles { get; }

    public YardSceneTargets(GraphicsDevice device, int size, int margin, bool cachedTiles)
    {
        Size = size;
        Margin = margin;
        CachedTiles = cachedTiles;
        try {
            Scene = Create(device, size);
            if (cachedTiles)
                for (int i = 0; i < layers.Length; i++)
                    layers[i] = Create(device, i < 7 ? size + margin * 2 : size);
        }
        catch {
            Dispose();
            throw;
        }
    }

    private static RenderTarget2D Create(GraphicsDevice device, int size) =>
        new(device, size, size, false, device.PresentationParameters.BackBufferFormat,
            DepthFormat.None, 0, RenderTargetUsage.PreserveContents);

    public void Swap(Main main)
    {
        Exchange(ref main.backWaterTarget, ref layers[0]);
        Exchange(ref Main.waterTarget, ref layers[1]);
        Exchange(ref main.tileTarget, ref layers[2]);
        Exchange(ref main.blackTarget, ref layers[3]);
        Exchange(ref main.tile2Target, ref layers[4]);
        Exchange(ref main.wallTarget, ref layers[5]);
        Exchange(ref main.backgroundTarget, ref layers[6]);
        Exchange(ref Main.screenTarget, ref layers[7]);
        Exchange(ref Main.screenTargetSwap, ref layers[8]);
        SceneState previous = SceneState.Read();
        if (swapped)
            previous.LastScreen = Main.screenPosition;
        state.Apply();
        state = previous;
        swapped = !swapped;
    }

    private static void Exchange(ref RenderTarget2D left, ref RenderTarget2D right) => (left, right) = (right, left);

    public void Dispose()
    {
        if (swapped)
            throw new InvalidOperationException("Restore native targets before disposing the Yard scene.");
        Scene?.Dispose();
        foreach (RenderTarget2D target in layers)
            target?.Dispose();
    }

    private struct SceneState
    {
        public Vector2 Water, Tiles, Tiles2, Walls, Background, LastScreen;
        public int RenderCount;
        public bool RenderNow;

        public static SceneState Read() => new() {
            Water = Main.sceneWaterPos, Tiles = Main.sceneTilePos, Tiles2 = Main.sceneTile2Pos,
            Walls = Main.sceneWallPos, Background = Main.sceneBackgroundPos,
            LastScreen = Main.screenLastPosition, RenderCount = Main.renderCount, RenderNow = Main.renderNow
        };

        public readonly void Apply()
        {
            Main.sceneWaterPos = Water;
            Main.sceneTilePos = Tiles;
            Main.sceneTile2Pos = Tiles2;
            Main.sceneWallPos = Walls;
            Main.sceneBackgroundPos = Background;
            Main.screenLastPosition = LastScreen;
            Main.renderCount = RenderCount;
            Main.renderNow = RenderNow;
        }
    }
}
