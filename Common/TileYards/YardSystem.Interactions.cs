using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Common.Lighting;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Players;
using TerrariaDynamics.Common.Storage;
using TerrariaDynamics.Common.UI;
using TerrariaDynamics.Common.WorldEditing;
using TerrariaDynamics.Content.Items;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem
{
    private static readonly MethodInfo UseTile = typeof(Player).GetMethod("TileInteractionsUse", BindingFlags.Instance | BindingFlags.NonPublic);
    private readonly Dictionary<Yard, YardWiringState> yardWiring = new();
    private readonly HashSet<Yard> physicsRefresh = new();
    private readonly ulong[] lastInteractions = new ulong[256];
    private Yard clickYard;
    private YardCoordinates clickPose;
    private bool circuitUiClickHeld;
    private bool traceCircuitUiClick;

    private void LoadInteractions()
    {
        if (UseTile == null)
            throw new NotSupportedException("Unsupported tModLoader tile interaction helper.");
        LoadTileAccess();
        On_Player.LookForTileInteractions += MapTileInteractions;
        On_Player.TileInteractionsUse += UseYardTile;
        On_Player.IsInTileInteractionRange += TileInteractionRange;
        On_Player.InInteractionRange += InteractionRange;
        On_Player.IsInInteractionRangeToMultiTileHitbox += ContainerRange;
        LoadResting();
        IL_Player.Update += PatchSignRange;
        On_Wiring.HitSwitch += HitYardSwitch;
        LoadTileNetworking();
        On_Liquid.AddWater += AddInteractionWater;
    }

    private void UnloadInteractions()
    {
        On_Player.LookForTileInteractions -= MapTileInteractions;
        On_Player.TileInteractionsUse -= UseYardTile;
        On_Player.IsInTileInteractionRange -= TileInteractionRange;
        On_Player.InInteractionRange -= InteractionRange;
        On_Player.IsInInteractionRangeToMultiTileHitbox -= ContainerRange;
        UnloadResting();
        IL_Player.Update -= PatchSignRange;
        On_Wiring.HitSwitch -= HitYardSwitch;
        UnloadTileNetworking();
        On_Liquid.AddWater -= AddInteractionWater;
        UnloadTileAccess();
        ClearInteractions();
    }

    private void ClearInteractions()
    {
        yardWiring.Clear();
        physicsRefresh.Clear();
        Array.Clear(lastInteractions);
        clickYard = null;
        circuitUiClickHeld = traceCircuitUiClick = false;
        interactionYard = null;
        interactionChanges = null;
        receivingTilePacket = false;
        YardStorageAllocator.ReservedTile = null;
    }

    internal bool TryStorageYard(int x, int y, out Yard found)
    {
        found = null;
        if (x < Main.maxTilesX)
            return false;
        foreach (Yard yard in regions.Values)
            if (yard.IsLoaded && yard.Bounds.Contains(x, y)) {
                found = yard;
                return true;
            }
        return false;
    }

    private void MapTileInteractions(On_Player.orig_LookForTileInteractions orig, Player player)
    {
        if (Main.netMode != NetmodeID.Server && player.whoAmI == Main.myPlayer) {
            bool held = Main.mouseRight || player.controlUseTile;
            traceCircuitUiClick = held && !circuitUiClickHeld;
            circuitUiClickHeld = held;
            if (traceCircuitUiClick)
                CircuitKeybindUISystem.Trace($"Click: mouseWorld={Main.MouseWorld}, playerCenter={player.Center}, "
                    + $"tileTarget=({Player.tileTargetX},{Player.tileTargetY}), heldItem={player.HeldItem.type}, "
                    + $"controlUseTile={player.controlUseTile}, releaseUseTile={player.releaseUseTile}, "
                    + $"render={RenderEnabled}, mouseInterface={player.mouseInterface}, map={Main.mapFullscreen}, "
                    + $"blockInput={Main.blockInput}, npcHover={Main.HoveringOverAnNPC}, paused={Main.gamePaused}");
        }
        if (Main.netMode == NetmodeID.Server || Main.gameMenu || player.whoAmI != Main.myPlayer
            || !RenderEnabled || player.mouseInterface || Main.mapFullscreen || Main.blockInput
            || Main.HoveringOverAnNPC || player.HeldItem.ModItem is Blueprint or YardForceTester) {
            if (traceCircuitUiClick)
                CircuitKeybindUISystem.Trace("Map skipped: input/UI guard or held Blueprint/YardForceTester; using world interaction.");
            orig(player);
            return;
        }
        Yard hit = null;
        Point target = default;
        // The last drawn Yard wins when visible tiles overlap. Air never masks
        // another Yard or a world tile behind it.
        foreach (Yard yard in regions.Values) {
            if (yard.IsLoaded && yard.RenderCoordinates.TryWorldToStorageTile(Main.MouseWorld, out Point cell)
                && Main.tile[cell.X, cell.Y].HasTile) {
                hit = yard;
                target = cell;
            }
        }
        if (hit == null) {
            if (traceCircuitUiClick)
                CircuitKeybindUISystem.Trace($"Hit: no Yard tile under mouse; loadedYards={regions.Values.Count(yard => yard.IsLoaded)}.");
            orig(player);
            return;
        }
        int x = Player.tileTargetX, y = Player.tileTargetY;
        bool smart = Main.SmartInteractShowingGenuine;
        bool other = Main.HasInteractibleObjectThatIsNotATile;
        clickYard = hit;
        clickPose = hit.RenderCoordinates;
        if (traceCircuitUiClick) {
            Tile tile = Main.tile[target.X, target.Y];
            YardPlayerView view = new(player, hit, clickPose);
            TileReachCheckSettings.Simple.GetRanges(player, out int rangeX, out int rangeY);
            CircuitKeybindUISystem.Trace($"Hit: yard={hit.Id}, storage={target}, local={clickPose.StorageToLocalTile(target)}, "
                + $"tile={TileLoader.GetTile(tile.TileType)?.FullName ?? tile.TileType.ToString()}, "
                + $"frame=({tile.TileFrameX},{tile.TileFrameY}), reach={InYardReach(player, hit, target, TileReachCheckSettings.Simple)}, "
                + $"playerLocal={view.Center}, halfSize={view.HalfSize}, range=({rangeX},{rangeY}), "
                + $"origin={clickPose.WorldOrigin}, rotation={clickPose.Rotation}, reachTolerancePixels=0.5");
        }
        try {
            Player.tileTargetX = target.X;
            Player.tileTargetY = target.Y;
            Main.SmartInteractShowingGenuine = false;
            Main.HasInteractibleObjectThatIsNotATile = false;
            orig(player);
            if (traceCircuitUiClick)
                CircuitKeybindUISystem.Trace($"Interaction returned: attempted={player.tileInteractAttempted}, "
                    + $"release={player.releaseUseTile}, happened={player.tileInteractionHappened}");
        }
        finally {
            Player.tileTargetX = x;
            Player.tileTargetY = y;
            Main.SmartInteractShowingGenuine = smart;
            Main.HasInteractibleObjectThatIsNotATile = other;
            clickYard = null;
        }
    }

    private bool InYardReach(Player player, Yard yard, Point storage, TileReachCheckSettings settings)
    {
        settings.GetRanges(player, out int rangeX, out int rangeY);
        YardCoordinates pose = ReferenceEquals(clickYard, yard) ? clickPose : yard.PlayerCoordinates;
        return new YardPlayerView(player, yard, pose).InReach(pose.StorageToLocalTile(storage), rangeX, rangeY);
    }

    private bool TileInteractionRange(On_Player.orig_IsInTileInteractionRange orig, Player player,
        int x, int y, TileReachCheckSettings settings) => TryStorageYard(x, y, out Yard yard)
        ? InYardReach(player, yard, new Point(x, y), settings) : orig(player, x, y, settings);

    private bool InteractionRange(On_Player.orig_InInteractionRange orig, Player player,
        int x, int y, TileReachCheckSettings settings) => TryStorageYard(x, y, out Yard yard)
        ? InYardReach(player, yard, new Point(x, y), settings) : orig(player, x, y, settings);

    private bool ContainerRange(On_Player.orig_IsInInteractionRangeToMultiTileHitbox orig,
        Player player, int x, int y)
    {
        if (!TryStorageYard(x, y, out Yard yard))
            return orig(player, x, y);
        TileObjectData data = TileObjectData.GetTileData(Main.tile[x, y]);
        int width = data?.Width ?? 2, height = data?.Height ?? 2;
        YardCoordinates pose = yard.PlayerCoordinates;
        Point local = pose.StorageToLocalTile(new Point(x, y));
        YardPlayerView view = new(player, yard, pose);
        Point closest = new(Math.Clamp((int)(view.Center.X / 16), local.X, local.X + width - 1),
            Math.Clamp((int)(view.Center.Y / 16), local.Y, local.Y + height - 1));
        TileReachCheckSettings.Simple.GetRanges(player, out int rangeX, out int rangeY);
        return view.InReach(closest, rangeX, rangeY);
    }

    // Sign range is inlined in Player.Update rather than using the reach hooks.
    // Replace just that intersection, keeping the rest of the player in world
    // coordinates throughout vanilla movement, networking and UI updates.
    private delegate bool SignIntersection(ref Rectangle signBounds, Rectangle playerBounds, Player player);

    private void PatchSignRange(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchLdfld<Sign>(nameof(Sign.y)))
            || !cursor.TryGotoNext(MoveType.Before, i => i.MatchCall<Rectangle>(nameof(Rectangle.Intersects))))
            throw new NotSupportedException("Unsupported tModLoader inlined sign range check.");
        cursor.Remove();
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<SignIntersection>(SignInRange);
    }

    private bool SignInRange(ref Rectangle signBounds, Rectangle playerBounds, Player player)
    {
        if (!TryStorageYard(signBounds.X / 16, signBounds.Y / 16, out Yard yard))
            return signBounds.Intersects(playerBounds);
        YardPlayerView view = new(player, yard, yard.PlayerCoordinates);
        Rectangle local = signBounds;
        local.Offset(-yard.StorageOrigin.X * 16, -yard.StorageOrigin.Y * 16);
        Rectangle reach = new((int)(view.Center.X - Player.tileRangeX * 16),
            (int)(view.Center.Y - Player.tileRangeY * 16), Player.tileRangeX * 32, Player.tileRangeY * 32);
        return local.Intersects(reach);
    }

    private static bool LocalInteraction(ushort type) => TileID.Sets.BasicChest[type]
        || TileID.Sets.BasicDresser[type] || type is TileID.Signs or TileID.Tombstones
        or TileID.AnnouncementBox or TileID.PiggyBank or TileID.Safes or TileID.DefendersForge or TileID.VoidVault
        or TileID.CrystalBall or TileID.BewitchingTable or TileID.SharpeningStation
        or TileID.AmmoBox or TileID.SliceOfCake or TileID.WarTable;

    private static bool LockedContainer(Point storage)
    {
        Tile tile = Main.tile[storage.X, storage.Y];
        if (!TileID.Sets.BasicChest[tile.TileType])
            return false;
        int x = storage.X - tile.TileFrameX / 18 % 2;
        int y = storage.Y - tile.TileFrameY / 18 % 2;
        return Chest.IsLocked(x, y);
    }

    private void UseYardTile(On_Player.orig_TileInteractionsUse orig, Player player, int x, int y)
    {
        if (traceCircuitUiClick)
            CircuitKeybindUISystem.Trace($"UseYardTile: cell=({x},{y}), attempted={player.tileInteractAttempted}, "
                + $"release={player.releaseUseTile}, actionScope={interactionYard != null}");
        if (interactionYard != null || !TryStorageYard(x, y, out Yard yard)) {
            orig(player, x, y);
            return;
        }
        ushort type = Main.tile[x, y].TileType;
        if (TileLoader.GetTile(type) is KeybindCircuitTile circuit) {
            // Binding UI is local to the player, including on multiplayer Yard
            // tiles. Only later keyboard activations are sent to the server.
            if (player.tileInteractAttempted && player.releaseUseTile
                && InYardReach(player, yard, new Point(x, y), TileReachCheckSettings.Simple)
                && circuit.RightClick(x, y)) {
                player.tileInteractionHappened = true;
                player.releaseUseTile = false;
                Main.mouseRightRelease = false;
            }
            return;
        }
        if (type >= TileID.Count)
            return;
        if (TileID.Sets.CanBeSatOnForPlayers[type] || TileID.Sets.CanBeSleptIn[type]) {
            UseRestingTile(player, yard, new Point(x, y));
            return;
        }
        if (LocalInteraction(type) && !LockedContainer(new Point(x, y))) {
            orig(player, x, y);
            return;
        }
        if (!TileID.Sets.HasOutlines[type] && !TileID.Sets.Torch[type]
            && !TileID.Sets.CanDropFromRightClick[type] && type != TileID.Bottles) {
            orig(player, x, y);
            return;
        }
        if (!player.tileInteractAttempted || !player.releaseUseTile || Main.gamePaused
            || !InYardReach(player, yard, new Point(x, y), TileReachCheckSettings.Simple))
            return;
        Point local = yard.Coordinates.StorageToLocalTile(new Point(x, y));
        Vector2 localMouse = (ReferenceEquals(clickYard, yard) ? clickPose : yard.PlayerCoordinates).WorldToLocal(Main.MouseWorld);
        if (Main.netMode == NetmodeID.MultiplayerClient) {
            if (!IsSynchronized)
                return;
            var packet = Packet(YardPacketKind.RequestTileInteraction);
            packet.Write(yard.Id.ToByteArray());
            packet.Write((short)local.X);
            packet.Write((short)local.Y);
            packet.Write(localMouse.X);
            packet.Write(localMouse.Y);
            packet.Send();
        }
        else
            ExecuteTileInteraction(player, yard, local);
        player.tileInteractionHappened = true;
        player.releaseUseTile = false;
        Main.mouseRightRelease = false;
    }

    private void UseRestingTile(Player player, Yard yard, Point storage)
    {
        if (!player.tileInteractAttempted || !player.releaseUseTile || Main.gamePaused
            || Terraria.GameContent.UI.WiresUI.Open || player.ownedProjectileCounts[651] > 0
            || !InYardReach(player, yard, storage, TileReachCheckSettings.Simple))
            return;
        Point local = yard.Coordinates.StorageToLocalTile(storage);
        if (Main.netMode == NetmodeID.MultiplayerClient) {
            if (!IsSynchronized)
                return;
            Vector2 mouse = (ReferenceEquals(clickYard, yard) ? clickPose : yard.PlayerCoordinates).WorldToLocal(Main.MouseWorld);
            var packet = Packet(YardPacketKind.RequestTileInteraction);
            packet.Write(yard.Id.ToByteArray());
            packet.Write((short)local.X);
            packet.Write((short)local.Y);
            packet.Write(mouse.X);
            packet.Write(mouse.Y);
            packet.Send();
        }
        else
            ExecutePlayerRest(player, yard, local);
        player.tileInteractionHappened = true;
        player.releaseUseTile = false;
        Main.mouseRightRelease = false;
    }

    private void ReceiveTileInteraction(BinaryReader reader, int sender)
    {
        Guid id = YardPacketCodec.ReadId(reader);
        Point local = new(reader.ReadInt16(), reader.ReadInt16());
        Vector2 mouse = new(reader.ReadSingle(), reader.ReadSingle());
        Player player = Main.player[sender];
        if (player.dead || player.ghost || !TryGet(id, out Yard yard) || !yard.IsLoaded
            || local.X < 0 || local.Y < 0 || local.X >= yard.Width || local.Y >= yard.Height
            || !float.IsFinite(mouse.X) || !float.IsFinite(mouse.Y)
            || mouse.X < local.X * 16 || mouse.X >= (local.X + 1) * 16
            || mouse.Y < local.Y * 16 || mouse.Y >= (local.Y + 1) * 16
            || !InYardReach(player, yard, yard.Coordinates.LocalTileToStorage(local), TileReachCheckSettings.Simple))
            return;
        ulong now = Main.GameUpdateCount + 1;
        if (lastInteractions[sender] != 0 && now - lastInteractions[sender] < 6)
            return;
        lastInteractions[sender] = now;
        ExecuteTileInteraction(player, yard, local);
    }

    private YardWiringState WiringFor(Yard yard)
    {
        if (!yardWiring.TryGetValue(yard, out YardWiringState state))
            yardWiring.Add(yard, state = new YardWiringState());
        return state;
    }

    private void ExecuteTileInteraction(Player player, Yard yard, Point local)
    {
        Point storage = yard.Coordinates.LocalTileToStorage(local);
        Tile tile = Main.tile[storage.X, storage.Y];
        if (!tile.HasTile || tile.TileType >= TileID.Count)
            return;
        if (TileID.Sets.CanBeSatOnForPlayers[tile.TileType] || TileID.Sets.CanBeSleptIn[tile.TileType]) {
            ExecutePlayerRest(player, yard, local);
            return;
        }
        bool attempted = player.tileInteractAttempted, release = player.releaseUseTile;
        var inventory = player.inventory.Select(item => (item.type, item.stack, item.prefix)).ToArray();
        try {
            RunTileAction(yard, origin => {
                player.tileInteractAttempted = player.releaseUseTile = true;
                Wiring.SetCurrentUser(player.whoAmI);
                UseTile.Invoke(player, new object[] { origin.X + local.X, origin.Y + local.Y });
            });
        }
        finally {
            player.tileInteractAttempted = attempted;
            player.releaseUseTile = release;
        }
        if (Main.netMode == NetmodeID.Server) {
            for (int i = 0; i < inventory.Length; i++)
                if (inventory[i].type != player.inventory[i].type || inventory[i].stack != player.inventory[i].stack
                    || inventory[i].prefix != player.inventory[i].prefix)
                    NetMessage.SendData(MessageID.SyncEquipment, number: player.whoAmI, number2: i);
            if (player.chest >= 0) {
                for (int i = 0; i < Chest.maxItems; i++)
                    NetMessage.SendData(MessageID.SyncChestItem, player.whoAmI, number: player.chest, number2: i);
                NetMessage.SendData(MessageID.SyncPlayerChest, player.whoAmI, number: player.chest);
            }
        }
    }

    private void RunTileAction(Yard yard, Action<Point> action)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
            throw new InvalidOperationException("Yard tile actions require authority.");
        RunStorageAction(yard, action, receiving: false);
    }

    private void RunStorageAction(Yard yard, Action<Point> action, bool receiving)
    {
        lock (worldSaveLock) {
            // Nested switch callbacks already use this Yard's native timer table.
            if (ReferenceEquals(interactionYard, yard)) {
                action(yard.StorageOrigin);
                return;
            }
            if (interactionYard != null)
                throw new InvalidOperationException("Yard tile actions cannot enter another Yard.");
            PrepareInteractionAirTile();
            interactionYard = yard;
            interactionChanges = new YardTileChanges();
            receivingTilePacket = receiving;
            try {
                using var mechanics = WiringFor(yard).Enter();
                action(yard.StorageOrigin);
            }
            finally {
                try {
                    // Publish partial writes even when the native action throws.
                    List<Point> unsent = interactionChanges.Complete(receiving, out int delta,
                        out bool collisionChanged, out bool changed);
                    yard.TileCount += delta;
                    if (changed)
                        RefreshChangedTiles(yard, collisionChanged);
                    if (!receiving && Main.netMode == NetmodeID.Server)
                        SendUnpublishedTiles(unsent);
                }
                finally {
                    interactionYard = null;
                    interactionChanges = null;
                    receivingTilePacket = false;
                }
            }
        }
    }

    private void RefreshChangedTiles(Yard yard, bool collisionChanged)
    {
        yard.InvalidatePlayerCollision();
        YardLighting.Remove(yard.Id);
        yard.LightContour = null;
        if (collisionChanged && Main.netMode != NetmodeID.MultiplayerClient)
            physicsRefresh.Add(yard);
    }

    private void HitYardSwitch(On_Wiring.orig_HitSwitch orig, int x, int y)
    {
        if (interactionYard != null) {
            orig(x, y);
            return;
        }
        if (!TryStorageYard(x, y, out Yard yard)) {
            orig(x, y);
            return;
        }
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;
        int user = YardWiringState.CurrentUser;
        Player player = (uint)user < Main.maxPlayers && Main.player[user].active ? Main.player[user] : null;
        if (Main.netMode == NetmodeID.Server && (player == null
            || !InYardReach(player, yard, new Point(x, y), TileReachCheckSettings.Simple)))
            return;
        Point local = yard.Coordinates.StorageToLocalTile(new Point(x, y));
        RunTileAction(yard, origin => orig(origin.X + local.X, origin.Y + local.Y));
    }

    private void UpdateYardMechanics()
    {
        foreach (var pair in yardWiring.ToArray()) {
            if (!pair.Key.IsLoaded) {
                yardWiring.Remove(pair.Key);
                continue;
            }
            if (pair.Value.Count > 0)
                RunTileAction(pair.Key, _ => Wiring.UpdateMech());
        }
    }

    private static void AddInteractionWater(On_Liquid.orig_AddWater orig, int x, int y)
    {
        if (x < Main.maxTilesX)
            orig(x, y);
    }

}
