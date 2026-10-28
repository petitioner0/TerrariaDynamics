using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.IO;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using Terraria.ObjectData;
using TerrariaDynamics.Common.Lighting;
using TerrariaDynamics.Common.Persistence;
using TerrariaDynamics.Common.Physics;
using TerrariaDynamics.Common.Rendering;
using TerrariaDynamics.Common.Storage;
using TerrariaDynamics.Common.WorldEditing;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem : ModSystem, IYardHost, IYardRegistry
{
    private readonly Dictionary<Guid, Yard> regions = new();
    private int originalStorageWidth;
    private object worldSaveLock;
    private YardObjectPersistence objectPersistence;

    public static YardSystem Instance => ModContent.GetInstance<YardSystem>();
    public IReadOnlyCollection<Yard> Regions => regions.Values;
    public int YardBodyCount => regions.Values.Count(yard => yard.HasPhysicsBody);
    public bool RenderEnabled { get; set; } = true;
    public int WorldRevision { get; private set; }

    public override void Load()
    {
        // Share the actual save lock: a snapshot must never contain pre-move world
        // tiles together with post-move yard tiles (or the opposite).
        worldSaveLock = typeof(WorldFile).GetField("IOLock", BindingFlags.NonPublic | BindingFlags.Static)
            ?.GetValue(null) ?? throw new NotSupportedException("WorldFile.IOLock was not found.");
        objectPersistence = new YardObjectPersistence(() => regions.Values);
        LoadInteractions();
        On_Player.PlayerFrame += FrameYardPlayer;
        if (!Main.dedServ) {
            On_Main.DoDraw += PrepareYardDraw;
            On_Main.DoDraw_WallsAndBlacks += DrawYardWalls;
            On_Main.DrawWires += DrawYardWires;
            On_Lighting.GetColor_int_int += GetYardLight;
        }
    }

    public override void ClearWorld()
    {
        ClearPlayerAttachments();
        ClearInstances();
        ClearNetwork();
        RenderEnabled = true;
    }

    private void ClearInstances()
    {
        WorldRevision++;
        YardLighting.Clear();
        Yard[] previous = regions.Values.ToArray();
        regions.Clear();
        foreach (Yard yard in previous)
            yard.Invalidate(removed: false);
    }

    public override void OnWorldUnload() => ClearWorld();

    public override void Unload()
    {
        ClearPlayerAttachments();
        On_Player.PlayerFrame -= FrameYardPlayer;
        UnloadInteractions();
        objectPersistence?.Dispose();
        objectPersistence = null;
        if (!Main.dedServ) {
            On_Main.DoDraw -= PrepareYardDraw;
            On_Main.DoDraw_WallsAndBlacks -= DrawYardWalls;
            On_Main.DrawWires -= DrawYardWires;
            On_Lighting.GetColor_int_int -= GetYardLight;
        }
        if (originalStorageWidth > 0 && Main.tile.Width > originalStorageWidth) {
            lock (worldSaveLock)
                TileBackingStore.Resize(ref Main.tile, originalStorageWidth);
        }
        originalStorageWidth = 0;
        ClearInstances();
        worldSaveLock = null;
    }

    public bool TryGet(Guid id, out Yard region) => regions.TryGetValue(id, out region);

    private static Color GetYardLight(On_Lighting.orig_GetColor_int_int orig, int x, int y) =>
        YardLighting.TryGetCachedColor(x, y, out Color color) ? color : orig(x, y);

    bool IYardHost.CanBeginReturn(Yard region, out string message)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) {
            message = Text("ServerAuthority");
            return false;
        }
        if (!region.IsLoaded) {
            message = Text("Unavailable");
            return false;
        }
        if (region.State == YardState.Returning) {
            message = Text("ReturnWaiting");
            return false;
        }
        if (!YardTileTransfer.TryGetReturnBounds(Main.tile, region, PlayableBounds, out _)) {
            message = Text("ReturnOutsideWorld");
            return false;
        }
        message = Text("ReturnWaiting");
        return true;
    }

    private static Rectangle PlayableBounds => new(0, 0, Main.maxTilesX, Main.maxTilesY);

    public override void PostUpdateEverything() => UpdateNetwork();

    internal void UpdatePhysics(PhysicsSystem physics)
    {
        if (!Monitor.TryEnter(worldSaveLock))
            return;
        try {
            UpdateYardMechanics();
            UpdateCircuitActivations();
            foreach (Yard yard in physicsRefresh)
                if (yard.IsLoaded)
                    yard.RefreshPhysics(physics.WorldId);
            physicsRefresh.Clear();
            UpdateActiveCircuits();
            ApplyTestForces();
            physics.Step(regions.Values);
            // Return stability uses this tick's physical pose and may remove a Yard.
            foreach (Yard yard in regions.Values.ToArray())
                yard.Update();
            PublishPhysicsTransforms();
        }
        finally {
            Monitor.Exit(worldSaveLock);
        }
    }

    string IYardHost.ReturnMovedMessage => Text("ReturnMoved");

    void IYardHost.ReportObserverError(Exception error) =>
        Mod.Logger.Warn("Yard observer failed after a lifecycle transition.", error);

    bool IYardHost.TryReturn(Yard region, out string message)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) {
            message = Text("ServerAuthority");
            return false;
        }
        if (WorldGen.gen || WorldGen.IsGeneratingHardMode || !Monitor.TryEnter(worldSaveLock)) {
            message = Text("WorldBusy");
            return false;
        }
        try {
            if (!region.IsLoaded) {
                message = Text("Unavailable");
                return false;
            }
            if (!YardTileTransfer.TryGetReturnBounds(Main.tile, region, PlayableBounds, out Rectangle target)) {
                message = Text("ReturnOutsideWorld");
                return false;
            }
            var objects = YardObjectTransfer.Prepare(Main.tile, region.Bounds, target.Location, Main.chest, Main.sign);
            objects.CloseUsers(Main.player);
            if (!YardTileTransfer.TryReturn(Main.tile, region, PlayableBounds, YardWorldRestorer.Clear,
                out Rectangle placedBounds, out int count, objects)) {
                message = Text("ReturnOutsideWorld");
                return false;
            }
            // Ownership changes only after the transfer commits. Cosmetic updates
            // must never leave a filled blueprint pointing at an already emptied Yard.
            MoveLocalCircuitBindings(region.Id, new Rectangle(0, 0, region.Width, region.Height),
                Guid.Empty, placedBounds.Location);
            regions.Remove(region.Id);
            YardLighting.Remove(region.Id);
            ClearReturnedBlueprints(region.Id);
            region.Invalidate(removed: true);
            WorldRevision++;
            RefreshReturnedArea(placedBounds);
            PublishReturned(region.Id, placedBounds);
            message = Text("Returned", count, placedBounds.X, placedBounds.Y);
            return true;
        }
        catch (Exception error) {
            Mod.Logger.Error("Yard return failed.", error);
            message = Text("ReturnFailed", error.Message);
            return false;
        }
        finally {
            Monitor.Exit(worldSaveLock);
        }
    }

    private void RefreshReturnedArea(Rectangle bounds)
    {
        // Preserve furniture/tree frames and avoid destructive support checks on
        // returned fragments. Reconnect ordinary blocks and walls after all writes.
        for (int x = bounds.Left - 1; x <= bounds.Right; x++) {
            for (int y = bounds.Top - 1; y <= bounds.Bottom; y++) {
                if (!WorldGen.InWorld(x, y))
                    continue;
                try {
                    Tile tile = Main.tile[x, y];
                    if (!tile.HasTile || !Main.tileFrameImportant[tile.TileType])
                        WorldGen.TileFrame(x, y, resetFrame: false, noBreak: true);
                    Framing.WallFrame(x, y, resetFrame: false);
                    if (tile.LiquidAmount > 0)
                        Liquid.AddWater(x, y);
                    if (Main.mapEnabled)
                        Main.Map.UpdateType(x, y);
                }
                catch (Exception error) {
                    Mod.Logger.Warn($"Yard returned, but world cell ({x}, {y}) could not be refreshed.", error);
                }
            }
        }
        Main.refreshMap = true;
        Main.renderCount = 99;
    }

    public bool TryCreate(Rectangle source, out Yard region, out string message)
    {
        region = null;
        if (Main.netMode == NetmodeID.MultiplayerClient) {
            message = Text("ServerAuthority");
            return false;
        }
        if (WorldGen.gen || WorldGen.IsGeneratingHardMode || !Monitor.TryEnter(worldSaveLock)) {
            message = Text("WorldBusy");
            return false;
        }
        try {
            if (source.Width <= 0 || source.Height <= 0 || source.X < 1 || source.Y < 1
                || (long)source.X + source.Width >= Main.maxTilesX - 1
                || (long)source.Y + source.Height >= Main.maxTilesY - 1) {
                message = Text("OutsideWorld");
                return false;
            }
            if ((long)source.Width * source.Height > Yard.MaxSelectionArea) {
                message = Text("TooLarge", Yard.MaxSelectionArea);
                return false;
            }
            if (!ValidateSelection(source, out int count, out message))
                return false;
            if (!Yard.TryAllocate(source, regions.Values, Main.maxTilesX, Main.tile.Height, out region)) {
                message = Text("YardFull");
                return false;
            }

            EnsureStorage(region.Bounds.Right);
            // The allocator knows our regions; this check also refuses foreign data.
            Rectangle storageBounds = region.Bounds;
            if (Main.chest.Any(chest => chest != null && storageBounds.Intersects(
                    YardObjectTransfer.ObjectBounds(Main.tile, chest.x, chest.y, chest: true)))
                || Main.sign.Any(sign => sign != null && storageBounds.Intersects(
                    YardObjectTransfer.ObjectBounds(Main.tile, sign.x, sign.y, chest: false)))) {
                message = Text("YardOccupied");
                region = null;
                return false;
            }
            for (int x = region.Bounds.Left; x < region.Bounds.Right; x++) {
                for (int y = region.Bounds.Top; y < region.Bounds.Bottom; y++) {
                    Tile target = Main.tile[x, y];
                    if (YardTileTransfer.HasContent(target)) {
                        message = Text("YardOccupied");
                        region = null;
                        return false;
                    }
                }
            }

            // Reserve the registry entry first; failed transfers roll back both sides.
            YardTreeAppearanceResolver.CaptureAppearances(region);
            YardLighting.CaptureContour(region, fromSource: true);
            var objects = YardObjectTransfer.Prepare(Main.tile, source, region.StorageOrigin, Main.chest, Main.sign);
            objects.CloseUsers(Main.player);
            region.Attach(this);
            regions.Add(region.Id, region);
            try {
                // Build physics before moving tiles: construction failure must not
                // leave a committed transfer without a usable blueprint binding.
                region.CreatePhysics(PhysicsSystem.Instance.WorldId, Main.tile, fromSource: true);
                region.TileCount = YardTileTransfer.Move(Main.tile, region, objects);
                region.MarkLoaded(region.TileCount);
            }
            catch {
                regions.Remove(region.Id);
                region.Invalidate(removed: true);
                throw;
            }
            MoveLocalCircuitBindings(Guid.Empty, source, region.Id, Point.Zero);
            WorldRevision++;
            RefreshYardFrames(region);
            RefreshSourceBorder(source);
            // Map cache refresh is cosmetic and must not undo a successful transfer.
            try {
                if (Main.mapEnabled) {
                    for (int x = source.Left; x < source.Right; x++)
                        for (int y = source.Top; y < source.Bottom; y++)
                            Main.Map.UpdateType(x, y);
                }
            }
            catch (Exception error) {
                Mod.Logger.Warn("Yard moved, but its old map area could not be refreshed.", error);
            }
            Main.refreshMap = true;
            Main.renderCount = 99;
            message = Text("Assembled", count, region.Width, region.Height,
                region.StorageOrigin.X, region.StorageOrigin.Y, region.Id);
            Mod.Logger.Info($"Assembled Yard {region.Id}: source={source}, storage={region.Bounds}, tiles={count}.");
            PublishCreated(region);
            return true;
        }
        catch (Exception error) {
            Mod.Logger.Error("Yard assembly failed.", error);
            region = null;
            message = Text("AssemblyFailed", error.Message);
            return false;
        }
        finally {
            Monitor.Exit(worldSaveLock);
        }
    }

    private void RefreshSourceBorder(Rectangle source)
    {
        // All source cells must be cleared before support checks run: framing can
        // break plants/furniture and spawn drops. These effects cannot be rolled
        // back with the Tile copy, so run only after the transfer has committed.
        // Visit the outside ring (including diagonals) once, never the Yard.
        for (int x = source.Left - 1; x <= source.Right; x++) {
            Refresh(x, source.Top - 1);
            Refresh(x, source.Bottom);
        }
        for (int y = source.Top; y < source.Bottom; y++) {
            Refresh(source.Left - 1, y);
            Refresh(source.Right, y);
        }

        void Refresh(int x, int y)
        {
            if (!WorldGen.InWorld(x, y))
                return;
            try {
                // Keep random frame variants, but allow normal support checks,
                // destruction, drops and any resulting vanilla neighbor updates.
                WorldGen.TileFrame(x, y, resetFrame: false, noBreak: false);
                Framing.WallFrame(x, y, resetFrame: false);
            }
            catch (Exception error) {
                // A failing mod frame hook must not orphan an already moved Yard
                // by making assembly fail before the blueprint receives its ID.
                Mod.Logger.Warn($"Yard moved, but source neighbor ({x}, {y}) could not be framed.", error);
            }
        }
    }

    private void RefreshYardFrames(Yard region)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;
        try {
            lock (worldSaveLock)
                YardTileFraming.Refresh(region.Bounds);
        }
        catch (Exception error) {
            // A cosmetic hook failure must not orphan a committed transfer or
            // reject an otherwise valid saved Yard. Keep its original frames.
            Mod.Logger.Warn($"Yard {region.Id} could not be reframed.", error);
        }
    }

    private static bool ValidateSelection(Rectangle source, out int count, out string message)
    {
        count = 0;
        message = "";
        foreach (TileEntity entity in TileEntity.ByID.Values) {
            Point16 position = entity.Position;
            TileObjectData data = WorldGen.InWorld(position.X, position.Y)
                ? TileObjectData.GetTileData(Main.tile[position.X, position.Y]) : null;
            var occupied = new Rectangle(position.X, position.Y, data?.Width ?? 1, data?.Height ?? 1);
            if (occupied.Intersects(source)) {
                message = Text("StatefulTile");
                return false;
            }
        }
        if (!YardObjectTransfer.ValidateSelection(Main.tile, source, Main.chest, Main.sign, out string error)) {
            message = Text(error);
            return false;
        }
        for (int x = source.Left; x < source.Right; x++) {
            for (int y = source.Top; y < source.Bottom; y++) {
                Tile tile = Main.tile[x, y];
                if (!YardTileTransfer.HasContent(tile))
                    continue;
                count++;
                // Furniture/type checks only apply to actual foreground blocks.
                if (!tile.HasTile)
                    continue;
                TileObjectData data = TileObjectData.GetTileData(tile);
                if (data != null) {
                    Point16 topLeft = TileObjectData.TopLeft(x, y);
                    if (!source.Contains(new Rectangle(topLeft.X, topLeft.Y, data.Width, data.Height))) {
                        message = Text("PartialFurniture");
                        return false;
                    }
                }
            }
        }
        if (count == 0) {
            message = Text("EmptySelection");
            return false;
        }
        return true;
    }

    private void EnsureStorage(int width)
    {
        if (originalStorageWidth == 0)
            originalStorageWidth = Main.tile.Width;
        TileBackingStore.EnsureWidth(ref Main.tile, width);
    }

    public override void SaveWorldData(TagCompound tag)
    {
        YardWorldData.Save(tag, regions.Values, region => region.IsLoaded
            ? YardTileCodec.Save(Main.tile, region) : region.PendingTiles);
    }

    public override void LoadWorldData(TagCompound tag)
    {
        ClearInstances();
        foreach (Yard region in YardWorldData.Load(tag, Main.maxTilesX, Main.maxTilesY, Main.tile.Height)) {
            region.Attach(this);
            regions.Add(region.Id, region);
        }
    }

    public override void PostWorldLoad()
    {
        if (regions.Count == 0) {
            objectPersistence.ValidateLoadedObjects();
            return;
        }
        try {
            EnsureStorage(regions.Values.Max(region => region.Bounds.Right));
        }
        catch (Exception error) {
            Mod.Logger.Error("Unable to expand yard storage. Original saved payloads are preserved.", error);
            objectPersistence.ValidateLoadedObjects();
            return;
        }
        foreach (Yard region in regions.Values) {
            try {
                region.TileCount = YardTileCodec.Load(Main.tile, region, region.PendingTiles);
                RefreshYardFrames(region);
                YardLighting.CaptureContour(region, fromSource: false);
                region.CreatePhysics(PhysicsSystem.Instance.WorldId, Main.tile, fromSource: false);
                region.MarkLoaded(region.TileCount);
            }
            catch (Exception error) {
                region.ReleasePhysics();
                // A missing content mod must not erase the original snapshot on save.
                Mod.Logger.Error($"Yard {region.Id} could not be loaded. Saved payload preserved.", error);
            }
        }
        objectPersistence.ValidateLoadedObjects();
    }

    public override void PostDrawTiles() => YardRenderer.Draw(this);

    private void PrepareYardDraw(On_Main.orig_DoDraw orig, Main self, GameTime gameTime)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient && !Main.gameMenu) {
            double time = YardRenderClock.Seconds;
            foreach (Yard yard in regions.Values)
                if (yard.IsLoaded)
                    yard.PrepareRenderPose(time);
        }
        ModContent.GetInstance<YardCameraSystem>().Draw(orig, self, gameTime);
    }

    private void DrawYardWalls(On_Main.orig_DoDraw_WallsAndBlacks orig, Main self)
    {
        orig(self);
        YardRenderer.DrawWalls(this);
    }

    private void DrawYardWires(On_Main.orig_DrawWires orig, Main self)
    {
        orig(self);
        YardRenderer.DrawWires(this);
    }

    internal static string Text(string key, params object[] args) =>
        Language.GetTextValue("Mods.TerrariaDynamics.Messages." + key, args);
}
