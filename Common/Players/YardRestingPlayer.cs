using System;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.GameContent;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Players;

// The native helpers retain posture, offsets, sleep timers and cancellation.
// Only their furniture lookup uses a stored Yard anchor instead of world Bottom.
public sealed class YardRestingPlayer : ModPlayer
{
    private Yard yard;
    private Point clickedTile;
    private Point anchorTile;
    private Vector2 localBottom;
    private YardCoordinates movementPose;
    private Vector2 lastCarrier;
    private ushort tileType;
    private short frameX;
    private short frameY;
    private YardRestKind kind;
    private int restDirection;
    private bool changingState;
    private YardPlayerRest pendingState;

    internal bool IsBound => yard != null;
    internal bool IsChangingState => changingState;
    internal YardRestKind Kind => kind;
    internal Point StorageAnchor => yard.Coordinates.LocalTileToStorage(anchorTile);
    internal YardPlayerRest State => new((byte)Player.whoAmI, yard?.Id ?? Guid.Empty,
        yard == null ? Point.Zero : clickedTile, kind, yard == null ? 0 : restDirection);

    public override void OnEnterWorld() => ResetRestingState();
    public override void PlayerDisconnect() => StopResting(notify: Main.netMode == NetmodeID.Server);
    public override void UpdateDead() => StopResting();

    public override void SyncPlayer(int toWho, int fromWho, bool newPlayer)
    {
        if (Main.netMode == NetmodeID.Server)
            YardSystem.Instance.SendPlayerRest(State, toWho, fromWho);
    }

    internal static bool TryGetTarget(Player player, Yard yard, Point local, YardRestKind kind,
        out Vector2 bottom, out int direction, out Vector2 offset, out ExtraSeatInfo details)
    {
        bottom = offset = Vector2.Zero;
        direction = 1;
        details = default;
        if (!yard.IsLoaded || local.X < 0 || local.Y < 0 || local.X >= yard.Width || local.Y >= yard.Height)
            return false;
        Point storage = yard.Coordinates.LocalTileToStorage(local);
        Tile tile = Main.tile[storage.X, storage.Y];
        if (!tile.HasTile || tile.IsActuated || tile.TileType >= TileID.Count)
            return false;
        bool found = kind == YardRestKind.Sleeping
            ? PlayerSleepingHelper.GetSleepingTargetInfo(storage.X, storage.Y, out direction, out bottom, out offset)
            : PlayerSittingHelper.GetSittingTargetInfo(player, storage.X, storage.Y,
                out direction, out bottom, out offset, out details);
        bottom -= new Vector2(yard.StorageOrigin.X * 16, yard.StorageOrigin.Y * 16);
        Point anchor = (bottom + new Vector2(0, -2)).ToTileCoordinates();
        if (!found || !float.IsFinite(bottom.X) || !float.IsFinite(bottom.Y)
            || (direction != -1 && direction != 1)
            || anchor.X < 0 || anchor.Y < 0 || anchor.X >= yard.Width || anchor.Y >= yard.Height)
            return false;
        Point storageAnchor = yard.Coordinates.LocalTileToStorage(anchor);
        Tile anchorTileData = Main.tile[storageAnchor.X, storageAnchor.Y];
        return anchorTileData.HasTile && !anchorTileData.IsActuated && anchorTileData.TileType == tile.TileType;
    }

    internal bool TryStart(Yard targetYard, Point local, YardRestKind targetKind, bool authoritativeEvent = false)
    {
        if (!TryGetTarget(Player, targetYard, local, targetKind,
            out Vector2 bottom, out int direction, out Vector2 offset, out ExtraSeatInfo details))
            return false;
        Point anchor = (bottom + new Vector2(0, -2)).ToTileCoordinates();
        if (!authoritativeEvent && ReferenceEquals(yard, targetYard) && kind == targetKind && anchorTile == anchor) {
            StopResting();
            return true;
        }
        YardCoordinates pose = targetYard.PlayerCoordinates;
        int occupants = 0;
        foreach (Player other in Main.player)
            if (other != null && other.active && other.whoAmI != Player.whoAmI
                && other.TryGetModPlayer(out YardRestingPlayer resting)
                && ReferenceEquals(resting.yard, targetYard) && resting.kind == targetKind && resting.anchorTile == anchor)
                occupants++;
        if (!authoritativeEvent) {
            Point storage = pose.LocalTileToStorage(local);
            int maxDistance = targetKind == YardRestKind.Sleeping
                ? PlayerSleepingHelper.BedSleepingMaxDistance : PlayerSittingHelper.ChairSittingMaxDistance;
            if (!Player.IsWithinSnappngRangeToTile(storage.X, storage.Y, maxDistance))
                return false;
            if (occupants >= 2)
                return false;
        }

        // StopVanityActions also invokes our stop hooks. Bind only after it has
        // finished so switching furniture does not clear the new anchor.
        changingState = true;
        try {
            Player.StopVanityActions();
            Player.RemoveAllGrapplingHooks();
            if (targetKind == YardRestKind.Sleeping)
                Player.RemoveAllFishingBobbers();
            if (Player.mount.Active)
                Player.mount.Dismount(Player);
            yard = targetYard;
            clickedTile = local;
            anchorTile = anchor;
            localBottom = bottom;
            movementPose = pose;
            kind = targetKind;
            restDirection = direction;
            Tile tile = Main.tile[StorageAnchor.X, StorageAnchor.Y];
            tileType = tile.TileType;
            frameX = tile.TileFrameX;
            frameY = tile.TileFrameY;
            Player.ChangeDir(direction);
            Player.gravDir = 1;
            PinMovement();
            lastCarrier = yard.PlayerPointVelocity(movementPose, Player.Bottom);
            if (kind == YardRestKind.Sitting) {
                Player.sitting.isSitting = true;
                Player.sitting.details = details;
                Player.sitting.offsetForSeat = offset;
                // The next native update registers this player. Registering at
                // entry too would count them twice when entry precedes that
                // update, causing a two-person seat to reject its second rider.
                Player.sitting.sittingIndex = Math.Min(occupants, 1);
            }
            else {
                Player.sleeping.timeSleeping = 0;
                Player.sleeping.SetIsSleepingAndAdjustPlayerRotation(Player, true);
                Player.sleeping.visualOffsetOfBedBase = offset;
                Player.sleeping.sleepingIndex = Math.Min(occupants, 1);
            }
        }
        finally {
            changingState = false;
        }
        Player.GetModPlayer<YardDeepBindingPlayer>().Bind(targetYard, localBottom);
        return true;
    }

    internal void ApplyState(YardPlayerRest state)
    {
        pendingState = default;
        if (state.Kind == YardRestKind.None) {
            StopResting(notify: false);
            return;
        }
        if (State == state && YardSystem.Instance.TryGet(state.YardId, out Yard current)
            && ReferenceEquals(yard, current))
            return;
        StopResting(notify: false);
        if (!YardSystem.Instance.TryGet(state.YardId, out Yard target) || !target.IsLoaded) {
            pendingState = state;
            return;
        }
        Player.ChangeDir(state.Direction);
        TryStart(target, state.LocalTile, state.Kind, authoritativeEvent: true);
    }

    public override void PreUpdate()
    {
        if (pendingState.YardId != Guid.Empty && YardSystem.Instance.TryGet(pendingState.YardId, out Yard pending)
            && pending.IsLoaded)
            ApplyState(pendingState);
        if (!IsBound)
            return;
        if (!Player.active || Player.dead || Player.ghost || !yard.IsLoaded
            || !YardSystem.Instance.TryGet(yard.Id, out Yard current) || !ReferenceEquals(yard, current)) {
            StopResting();
            return;
        }
        Tile tile = Main.tile[StorageAnchor.X, StorageAnchor.Y];
        if (!tile.HasTile || tile.IsActuated || tile.TileType != tileType
            || tile.TileFrameX != frameX || tile.TileFrameY != frameY) {
            StopResting();
            return;
        }
        // Vanilla PlayerControls can arrive between the authoritative rest event
        // and this update. The Yard event owns posture while the binding exists.
        if (Main.netMode == NetmodeID.MultiplayerClient && Player.whoAmI != Main.myPlayer)
            Player.ChangeDir(restDirection);
        if (kind == YardRestKind.Sitting)
            Player.sitting.isSitting = true;
        else
            Player.sleeping.SetIsSleepingAndAdjustPlayerRotation(Player, true);
        // FinishMovement publishes the carrier for networking. Vanilla must
        // update the seated/sleeping player with zero relative velocity.
        Player.velocity = Vector2.Zero;
    }

    internal void PinMovement()
    {
        if (!IsBound)
            return;
        YardCoordinates pose = yard.PlayerCoordinates;
        Vector2 bottom = pose.LocalToWorld(localBottom);
        // Furniture owns the anchor; Yard geometry does not constrain entry or
        // following. Keep the native player hitbox and damage handling intact.
        movementPose = pose;
        Player.Bottom = bottom;
        Player.velocity = Vector2.Zero;
        Player.fallStart = Player.fallStart2 = (int)(Player.position.Y / 16);
    }

    internal void FinishMovement()
    {
        PinMovement();
        if (IsBound) {
            Player.fullRotation = kind == YardRestKind.Sleeping ? -Player.direction * MathHelper.PiOver2 : 0;
            lastCarrier = yard.PlayerPointVelocity(movementPose, Player.Bottom);
            Player.velocity = lastCarrier;
        }
    }

    internal void NativeStopped()
    {
        if (!changingState && IsBound)
            StopResting();
    }

    internal void ResetRestingState()
    {
        pendingState = default;
        StopResting(notify: false);
    }

    internal void StopResting(bool notify = true)
    {
        pendingState = default;
        if (!IsBound || changingState)
            return;
        YardPlayerRest previous = State;
        Yard previousYard = yard;
        Vector2 carrier = yard.IsLoaded ? yard.PlayerPointVelocity(movementPose, Player.Bottom) : lastCarrier;
        Player.GetModPlayer<YardDeepBindingPlayer>().Unbind(previousYard);
        yard = null;
        kind = YardRestKind.None;
        changingState = true;
        try {
            Player.sitting.SitUp(Player, false);
            Player.sleeping.StopSleeping(Player, false);
            Player.fullRotation = 0;
            Player.velocity = carrier;
        }
        finally {
            changingState = false;
        }
        if (Main.netMode != NetmodeID.Server && Player.whoAmI == Main.myPlayer)
            Player.GetModPlayer<YardMovementPlayer>().ResumeFromRest(previousYard, movementPose, carrier);
        if (notify)
            YardSystem.Instance.EndPlayerRest(Player, previous);
    }

}
