using System;
using System.IO;
using Microsoft.Xna.Framework;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Players;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem
{
    private void LoadResting()
    {
        On_Player.IsWithinSnappngRangeToTile += RestingTileRange;
        On_Player.TileInteractionsMouseOver += RestingMouseOver;
        IL_PlayerSittingHelper.UpdateSitting += PatchRestingAnchor;
        IL_PlayerSittingHelper.TryGetSittingBlock += PatchRestingAnchor;
        IL_PlayerSleepingHelper.UpdateState += PatchRestingAnchor;
        On_PlayerSittingHelper.SitUp += StopYardSitting;
        On_PlayerSleepingHelper.StopSleeping += StopYardSleeping;
    }

    private void UnloadResting()
    {
        On_Player.IsWithinSnappngRangeToTile -= RestingTileRange;
        On_Player.TileInteractionsMouseOver -= RestingMouseOver;
        IL_PlayerSittingHelper.UpdateSitting -= PatchRestingAnchor;
        IL_PlayerSittingHelper.TryGetSittingBlock -= PatchRestingAnchor;
        IL_PlayerSleepingHelper.UpdateState -= PatchRestingAnchor;
        On_PlayerSittingHelper.SitUp -= StopYardSitting;
        On_PlayerSleepingHelper.StopSleeping -= StopYardSleeping;
    }

    private void RestingMouseOver(On_Player.orig_TileInteractionsMouseOver orig, Player player, int x, int y)
    {
        orig(player, x, y);
        if (TryStorageYard(x, y, out _) && TileID.Sets.CanBeSleptIn[Main.tile[x, y].TileType]
            && Player.IsHoveringOverABottomSideOfABed(x, y))
            player.cursorItemIconEnabled = false;
    }

    private bool RestingTileRange(On_Player.orig_IsWithinSnappngRangeToTile orig,
        Player player, int x, int y, int distance)
    {
        if (!TryStorageYard(x, y, out Yard yard))
            return orig(player, x, y, distance);
        YardCoordinates pose = ReferenceEquals(clickYard, yard) ? clickPose : yard.PlayerCoordinates;
        Vector2 bottom = pose.WorldToLocal(player.Bottom) - new Vector2(0, 16);
        Vector2 tile = pose.StorageToLocalPixels(x, y) + new Vector2(8);
        return Vector2.DistanceSquared(bottom, tile) <= distance * (float)distance;
    }

    private static void PatchRestingAnchor(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchCall(typeof(Utils), nameof(Utils.ToTileCoordinates))))
            throw new NotSupportedException($"Unsupported tModLoader resting lookup: {il.Method.FullName}.");
        cursor.Emit(OpCodes.Ldarg_1);
        cursor.EmitDelegate<Func<Point, Player, Point>>((worldTile, player) => {
            return player.TryGetModPlayer(out YardRestingPlayer resting) && resting.IsBound
                ? resting.StorageAnchor : worldTile;
        });
    }

    private static bool IgnoreRemoteRestStop(YardRestingPlayer resting, Player player) =>
        resting.IsBound && !resting.IsChangingState
        && Main.netMode == NetmodeID.MultiplayerClient && player.whoAmI != Main.myPlayer;

    private static void StopYardSitting(On_PlayerSittingHelper.orig_SitUp orig,
        ref PlayerSittingHelper helper, Player player, bool multiplayerBroadcast)
    {
        if (!player.TryGetModPlayer(out YardRestingPlayer resting)) {
            orig(ref helper, player, multiplayerBroadcast);
            return;
        }
        if (resting.Kind == YardRestKind.Sitting && IgnoreRemoteRestStop(resting, player))
            return;
        orig(ref helper, player, multiplayerBroadcast);
        if (resting.Kind == YardRestKind.Sitting)
            resting.NativeStopped();
    }

    private static void StopYardSleeping(On_PlayerSleepingHelper.orig_StopSleeping orig,
        ref PlayerSleepingHelper helper, Player player, bool multiplayerBroadcast)
    {
        if (!player.TryGetModPlayer(out YardRestingPlayer resting)) {
            orig(ref helper, player, multiplayerBroadcast);
            return;
        }
        if (resting.Kind == YardRestKind.Sleeping && IgnoreRemoteRestStop(resting, player))
            return;
        orig(ref helper, player, multiplayerBroadcast);
        if (resting.Kind == YardRestKind.Sleeping)
            resting.NativeStopped();
    }

    private void ExecutePlayerRest(Player player, Yard yard, Point local)
    {
        Point storage = yard.Coordinates.LocalTileToStorage(local);
        Tile tile = Main.tile[storage.X, storage.Y];
        YardRestKind kind = TileID.Sets.CanBeSleptIn[tile.TileType] ? YardRestKind.Sleeping : YardRestKind.Sitting;
        // A mobile bed cannot store a vanilla world spawn coordinate. Keep the
        // native head/foot distinction and accept only its sleeping side.
        if (kind == YardRestKind.Sleeping && Player.IsHoveringOverABottomSideOfABed(storage.X, storage.Y))
            return;
        YardRestingPlayer resting = player.GetModPlayer<YardRestingPlayer>();
        bool changed = resting.TryStart(yard, local, kind);
        if (Main.netMode == NetmodeID.Server)
            SendPlayerRest(resting.State, changed ? -1 : player.whoAmI);
    }

    internal void SendPlayerRest(YardPlayerRest state, int toWho = -1, int ignore = -1)
    {
        if (Main.netMode != NetmodeID.Server)
            return;
        var packet = Packet(YardPacketKind.PlayerRest);
        YardPlayerRestPacket.Write(packet, state);
        packet.Send(toWho, ignore);
    }

    internal void EndPlayerRest(Player player, YardPlayerRest previous)
    {
        if (Main.netMode == NetmodeID.Server)
            SendPlayerRest(player.GetModPlayer<YardRestingPlayer>().State);
        else if (Main.netMode == NetmodeID.MultiplayerClient && player.whoAmI == Main.myPlayer && IsSynchronized) {
            var packet = Packet(YardPacketKind.RequestStopRest);
            YardPlayerRestPacket.Write(packet, previous);
            packet.Send();
        }
        if (Main.netMode != NetmodeID.Server && player.whoAmI == Main.myPlayer)
            NetMessage.SendData(MessageID.PlayerControls, number: player.whoAmI);
    }

    private void ReceiveStopRest(BinaryReader reader, int sender)
    {
        YardPlayerRest previous = YardPlayerRestPacket.Read(reader);
        if (previous.Player != sender || previous.Kind == YardRestKind.None)
            throw new InvalidDataException("A client cannot stop another Yard resting player.");
        YardRestingPlayer resting = Main.player[sender].GetModPlayer<YardRestingPlayer>();
        if (resting.State == previous)
            resting.StopResting();
    }

    private static void ReceivePlayerRest(BinaryReader reader)
    {
        YardPlayerRest state = YardPlayerRestPacket.Read(reader);
        if (state.Player >= Main.maxPlayers)
            throw new InvalidDataException("Invalid Yard resting player.");
        Player player = Main.player[state.Player];
        YardRestingPlayer resting = player.GetModPlayer<YardRestingPlayer>();
        if (resting.IsBound || state.Kind != YardRestKind.None)
            player.GetModPlayer<YardMovementPlayer>().SuspendForRest();
        resting.ApplyState(state);
        if (state.Player == Main.myPlayer)
            NetMessage.SendData(MessageID.PlayerControls, number: state.Player);
    }
}
