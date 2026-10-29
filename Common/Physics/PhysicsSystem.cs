using System;
using System.Collections.Generic;
using Box2D.NET;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Physics;

public sealed class PhysicsSystem : ModSystem
{
    public const float PixelsPerUnit = YardCoordinates.TileSize;
    public const float TimeStep = 1f / 60f;
    public const int SubStepCount = 4;
    // Vanilla: 0.4 px/tick² * 60² / 16 = 90 tiles/s².
    public const float Gravity = 90f;
    private static readonly Func<int, int, TerrainTileShape> ReadShape = ReadTileShape;
    private readonly WorldTileCollision terrain = new();
    private readonly List<B2Vec2> playerCenters = new();
    private readonly List<B2AABB> bodyTerrainBounds = new();
    private bool worldLoaded;

    public static PhysicsSystem Instance => ModContent.GetInstance<PhysicsSystem>();
    public B2WorldId WorldId { get; private set; }
    public bool IsWorldReady => worldLoaded && Main.netMode != NetmodeID.MultiplayerClient;
    public int TerrainChunkCount => terrain.ChunkCount;
    public int TerrainBodyCount => terrain.BodyCount;
    public int TerrainShapeCount => terrain.ShapeCount;

    public static B2Vec2 PixelsToPhysics(Vector2 position) =>
        new(position.X / PixelsPerUnit, position.Y / PixelsPerUnit);

    public static Vector2 PhysicsToPixels(B2Vec2 position) =>
        new(position.X * PixelsPerUnit, position.Y * PixelsPerUnit);

    public override void Load()
    {
        CreateWorld();
        Mod.Logger.Info("Created Box2D world: 1 unit = 1 tile, gravity = 90 tiles/s², time step = 1/60 s, substeps = 4.");
    }

    public override void ClearWorld() => ResetWorld();

    public override void OnWorldUnload() => ResetWorld();

    public override void PostWorldLoad()
    {
        if (Main.netMode != NetmodeID.MultiplayerClient) {
            // Match the ordinary player's BordersMovement limits. These are
            // world pixels, not backing-store dimensions or Yard coordinates.
            terrain.CreateBoundary(WorldId, new B2AABB {
                lowerBound = PixelsToPhysics(new Vector2(Main.leftWorld + 656, Main.topWorld + 656)),
                upperBound = PixelsToPhysics(new Vector2(Main.rightWorld - 672, Main.bottomWorld - 672))
            });
        }
        worldLoaded = true;
    }

    public override void Unload() => DestroyWorld();

    public override void PostUpdateEverything()
    {
        if (!IsWorldReady || Main.gameMenu || WorldGen.gen || WorldGen.IsGeneratingHardMode
            || (Main.netMode == NetmodeID.SinglePlayer && Main.gamePaused))
            return;

        playerCenters.Clear();
        for (int i = 0; i < Main.maxPlayers; i++) {
            Player player = Main.player[i];
            if (player.active)
                playerCenters.Add(PixelsToPhysics(player.Center));
        }
        // This final update hook sees tile changes from mining, wiring, networks
        // and Yard transfers before advancing the authoritative simulation.
        YardSystem.Instance.UpdatePhysics(this);
    }

    private void CreateWorld()
    {
        B2WorldDef definition = B2Types.b2DefaultWorldDef();
        definition.gravity = new B2Vec2(0, Gravity);
        definition.workerCount = 1;
        WorldId = B2Worlds.b2CreateWorld(definition);
        if (!B2Worlds.b2World_IsValid(WorldId))
            throw new InvalidOperationException("Box2D physics world could not be created.");
    }

    private void DestroyWorld()
    {
        worldLoaded = false;
        playerCenters.Clear();
        bodyTerrainBounds.Clear();
        terrain.Clear();
        // Destroying the world also destroys its bodies. Yard disposal tolerates
        // this happening before YardSystem invalidates the owning instances.
        if (B2Worlds.b2World_IsValid(WorldId))
            B2Worlds.b2DestroyWorld(WorldId);
        WorldId = default;
    }

    private void ResetWorld()
    {
        DestroyWorld();
        CreateWorld();
    }

    // Called by YardSystem under the save/transfer lock. The registry is stable
    // across both passes, and each Yard owns its body and state synchronization.
    internal void Step(IEnumerable<Yard> yards)
    {
        bodyTerrainBounds.Clear();
        foreach (Yard yard in yards)
            if (yard.TryGetPhysicsTerrainBounds(TimeStep, Gravity, out B2AABB bounds))
                bodyTerrainBounds.Add(bounds);
        terrain.Synchronize(WorldId, Main.maxTilesX, Main.maxTilesY, playerCenters, ReadShape, bodyTerrainBounds);
        B2Worlds.b2World_Step(WorldId, TimeStep, SubStepCount);
        foreach (Yard yard in yards)
            yard.SynchronizePhysicsState();
    }

    private static TerrainTileShape ReadTileShape(int x, int y) => TileCollisionRules.ShapeOf(Main.tile[x, y]);
}
