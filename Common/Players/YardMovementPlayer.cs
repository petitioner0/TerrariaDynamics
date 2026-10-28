using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Physics;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Players;

// Ordinary attachment is independent of grounding. Vanilla controls run with
// relative velocity; the published velocity includes this tick's carrier once.
public sealed class YardMovementPlayer : ModPlayer
{
    private const float SupportNormal = 0.64f;
    private const float Skin = 0.08f;
    private readonly List<Surface> surfaces = new();
    private Yard attachedYard;
    private YardCoordinates attachedPose;
    private Vector2 lastCarrier;
    private Vector2 appliedCarrier;
    private Vector2 plannedMovement;
    private Vector2 resolvedVelocity;
    private Yard touchedYard;
    private bool frameActive;
    private bool relativeVelocity;
    private bool movementProcessed;
    private YardPlayerAttachment remoteAttachment;
    private ulong remoteReceivedAt;
    private Yard remoteYard;
    private YardCoordinates remotePose;
    private Vector2 remoteCenter;
    private Guid lastSentYard;
    private bool lastSentStanding;
    private ulong lastSentAt;

    public Guid BoundYardId => Player.GetModPlayer<YardDeepBindingPlayer>().BoundYard?.Id
        ?? attachedYard?.Id ?? remoteYard?.Id ?? Guid.Empty;
    public bool IsStandingOnYard { get; private set; }

    internal Vector2 RelativeVelocityFor(Yard yard, YardCoordinates pose) =>
        ReferenceEquals(attachedYard, yard) && relativeVelocity ? Player.velocity
            : Player.velocity - yard.PlayerPointVelocity(pose, Player.Center);

    private Vector2 Size => new(Player.width, Player.height);
    private bool ControlsMovement => Main.netMode != NetmodeID.Server && Player.whoAmI == Main.myPlayer
        && Player.active && !Player.dead && !Player.ghost && !Main.gameMenu;

    public override void OnEnterWorld() => ResetAttachmentState();

    internal void ResetAttachmentState()
    {
        SuspendForRest();
        lastSentYard = Guid.Empty;
        lastSentStanding = false;
        lastSentAt = 0;
        frameActive = movementProcessed = false;
        surfaces.Clear();
    }

    public override void PlayerDisconnect() => ResetAttachmentState();

    internal void SuspendForRest()
    {
        ClearBinding();
        remoteAttachment = default;
        remoteYard = null;
        movementProcessed = false;
        appliedCarrier = Vector2.Zero;
        touchedYard = null;
    }

    internal void ResumeFromRest(Yard yard, YardCoordinates previousPose, Vector2 carrier)
    {
        // Rest can end inside vanilla's update.
        // Keep this tick's surfaces and movement phase ready for the handoff.
        SuspendForRest();
        if (!ControlsMovement || !yard.IsLoaded
            || !YardSystem.Instance.TryGet(yard.Id, out Yard current) || !ReferenceEquals(yard, current))
            return;
        // The exit velocity already includes the carrier. Retain an ordinary
        // contact binding so acquiring that same floor cannot add it a second
        // time. Furniture suspended in air still needs a real surface contact.
        bool contact = yard.PlayerCollision.TryContact(previousPose, Player.position, Size, out _)
            || yard.PlayerCollision.TryCast(previousPose, Player.position, Size, new Vector2(0, Player.gravDir),
                out _, false, Player.gravDir);
        if (!contact)
            return;
        attachedYard = yard;
        attachedPose = previousPose;
        lastCarrier = carrier;
        if (frameActive) {
            Player.velocity -= carrier;
            relativeVelocity = true;
        }
    }

    public override void UpdateDead()
    {
        ClearBinding();
        remoteAttachment = default;
        remoteYard = null;
        PublishAttachment();
    }

    public override void SyncPlayer(int toWho, int fromWho, bool newPlayer)
    {
        if (Main.netMode == NetmodeID.Server)
            YardSystem.Instance.SendPlayerAttachment(remoteAttachment with { Player = (byte)Player.whoAmI }, toWho, fromWho);
    }

    public override void PreUpdate()
    {
        frameActive = ControlsMovement;
        movementProcessed = relativeVelocity = false;
        appliedCarrier = Vector2.Zero;
        touchedYard = null;
        surfaces.Clear();
        if (!frameActive) {
            ClearBinding();
            remoteYard = null;
            if (!Player.GetModPlayer<YardDeepBindingPlayer>().IsBound)
                PrepareRemoteMovement();
            return;
        }
        foreach (Yard yard in YardSystem.Instance.Regions)
            if (yard.IsLoaded)
                surfaces.Add(new Surface(yard, yard.PlayerCoordinates));
        if (Player.GetModPlayer<YardDeepBindingPlayer>().IsBound) {
            ClearBinding();
            remoteYard = null;
            return;
        }
        // Validate against the previous pose before carrying: a fast-moving Yard
        // must not leave its rider behind merely because its new pose moved away.
        if (!IsCurrentBinding() || !YardCollisionGeometry.IntersectsBounds(attachedPose, Player.position, Size)) {
            ClearBinding();
            return;
        }
        Player.velocity -= lastCarrier;
        relativeVelocity = true;
    }

    public override void PreUpdateMovement()
    {
        YardRestingPlayer resting = Player.GetModPlayer<YardRestingPlayer>();
        if (resting.IsBound) {
            resting.PinMovement();
            return;
        }
        if (Player.GetModPlayer<YardDeepBindingPlayer>().IsBound)
            return;
        if (!frameActive) {
            if (remoteYard != null) {
                Player.Center = remoteCenter;
                Player.velocity = Vector2.Zero;
            }
            return;
        }
        if (!ControlsMovement)
            return;
        if (attachedYard != null && TryGetSurface(attachedYard, out Surface attached)) {
            Vector2 center = Player.Center;
            Vector2 carry = attached.Pose.LocalToWorld(attachedPose.WorldToLocal(center)) - center;
            // The player box remains upright. Carrier motion is translation at
            // the player's location, including the Yard's angular movement.
            carry = ClipCarrierAgainstYards(carry, attachedYard);
            appliedCarrier = MoveAgainstWorld(carry);
            Player.position += appliedCarrier;
        }

        Vector2 position = Player.position;
        Vector2 velocity = Player.velocity;
        ResolvePenetration(ref position, ref velocity);
        Player.position = position;
        if (IsStandingOnYard && Player.velocity.Y * Player.gravDir >= -0.01f
            && TryGetFloor(out _, out YardPlayerCast floor)) {
            // Ground controls specify horizontal speed. Projecting gravity
            // into the slope every tick can overpower walking acceleration
            // and makes even a stationary Yard impossible to walk uphill on.
            velocity.Y = -velocity.X * floor.Normal.X / floor.Normal.Y;
        }
        Vector2 remaining = velocity;
        Vector2 start = position;
        for (int iteration = 0; iteration < 5 && remaining.LengthSquared() > 0.000001f; iteration++) {
            if (!TryCast(position, remaining, null, out Surface surface, out YardPlayerCast hit)) {
                position += remaining;
                remaining = Vector2.Zero;
                break;
            }
            RememberContact(surface.Yard);
            position += remaining * hit.Fraction + hit.Normal * Skin;
            remaining *= 1 - hit.Fraction;
            RemoveInwardVelocity(ref remaining, hit.Normal);
            RemoveInwardVelocity(ref velocity, hit.Normal);
            if (hit.Normal.Y * Player.gravDir > SupportNormal)
                Player.jump = 0;
        }
        plannedMovement = position - start;
        resolvedVelocity = velocity;
        Player.velocity = plannedMovement;
        movementProcessed = true;
        // Vanilla now clips this movement against world tiles, handles slopes,
        // switches and borders, and updates the rest of the player normally.
    }

    public override void PostUpdate()
    {
        try {
            FinishUpdate();
        }
        finally {
            // Outside the player update, velocity includes the carrier again.
            frameActive = relativeVelocity = false;
        }
    }

    private void FinishUpdate()
    {
        if (Player.GetModPlayer<YardRestingPlayer>().IsBound) {
            Player.GetModPlayer<YardRestingPlayer>().FinishMovement();
            return;
        }
        if (Player.GetModPlayer<YardDeepBindingPlayer>().IsBound)
            return;
        if (!frameActive) {
            if (remoteYard != null) {
                Player.Center = remoteCenter;
                Player.velocity = remoteAttachment.RelativeVelocity + remoteYard.PlayerPointVelocity(remotePose, remoteCenter);
                IsStandingOnYard = remoteAttachment.Standing;
                if (IsStandingOnYard)
                    Player.fallStart = Player.fallStart2 = (int)(Player.position.Y / YardCoordinates.TileSize);
            }
            return;
        }
        if (!ControlsMovement) {
            ClearBinding();
            PublishAttachment();
            return;
        }
        if (!movementProcessed) {
            if (relativeVelocity)
                Player.velocity += lastCarrier;
            return;
        }
        Vector2 velocity = VelocityAfterWorldCollision();

        IsStandingOnYard = false;
        if (TryGetSupport(velocity, out Surface support)) {
            RememberContact(support.Yard);
            IsStandingOnYard = true;
            velocity.Y = 0;
            Player.fallStart = Player.fallStart2 = (int)(Player.position.Y / YardCoordinates.TileSize);
            Player.RefreshExtraJumps();
            Player.wingTime = Player.wingTimeMax;
            Player.rocketTime = Player.rocketTimeMax;
        }
        // Side/ceiling contacts also acquire attachment. Merely entering an air
        // hole or the rectangular retention volume does not acquire a Yard.
        if (touchedYard == null)
            foreach (Surface surface in surfaces)
                if (Near(surface, Player.position, Vector2.Zero)
                    && surface.Yard.PlayerCollision.TryContact(surface.Pose, Player.position, Size, out _)) {
                    RememberContact(surface.Yard);
                    break;
                }

        bool acquired = attachedYard == null && touchedYard != null;
        if (acquired)
            attachedYard = touchedYard;
        if (attachedYard != null && TryGetSurface(attachedYard, out Surface bound)
            && IsCurrentBinding() && YardCollisionGeometry.IntersectsBounds(bound.Pose, Player.position, Size)) {
            Vector2 carrier = acquired ? attachedYard.PlayerPointVelocity(bound.Pose, Player.Center) : appliedCarrier;
            Player.velocity = velocity + carrier;
            lastCarrier = carrier;
            attachedPose = bound.Pose;
        }
        else {
            // This velocity already includes inherited carrier motion. Clearing
            // the binding must not add it again on the following update.
            Player.velocity = velocity + appliedCarrier;
            ClearBinding();
        }
        PublishAttachment();
    }

    internal bool TryGetGroundedFrameVelocity(out Vector2 velocity)
    {
        velocity = Vector2.Zero;
        if (Player.GetModPlayer<YardDeepBindingPlayer>().IsBound)
            return false;
        if (frameActive && movementProcessed && ControlsMovement) {
            velocity = VelocityAfterWorldCollision();
            if (!TryGetSupport(velocity, out _))
                return false;
        }
        else if (remoteYard != null && remoteAttachment.Standing)
            velocity = remoteAttachment.RelativeVelocity;
        else
            return false;
        velocity.Y = 0;
        return true;
    }

    private Vector2 VelocityAfterWorldCollision()
    {
        Vector2 velocity = resolvedVelocity;
        // A partial landing displacement is not the final velocity. Preserve
        // any additional collision/velocity changes made by vanilla, though.
        if (MathF.Abs(Player.velocity.X - plannedMovement.X) > 0.001f)
            velocity.X = Player.velocity.X;
        if (MathF.Abs(Player.velocity.Y - plannedMovement.Y) > 0.001f)
            velocity.Y = Player.velocity.Y;
        return velocity;
    }

    private bool TryGetSupport(Vector2 velocity, out Surface support) =>
        TryGetFloor(out support, out YardPlayerCast foot)
        // Uphill motion rises in world Y while remaining tangent to the floor.
        // Only motion away from the support normal counts as leaving it.
        && Vector2.Dot(velocity, foot.Normal) <= 0.01f;

    private bool TryGetFloor(out Surface support, out YardPlayerCast foot) =>
        TryCast(Player.position, new Vector2(0, Player.gravDir), null, out support, out foot)
        && foot.Normal.Y * Player.gravDir < -SupportNormal;

    public override void ModifyDrawInfo(ref PlayerDrawSet drawInfo)
    {
        if (Main.netMode == NetmodeID.Server || Player.GetModPlayer<YardDeepBindingPlayer>().IsBound)
            return;
        // Draws may sample a few ms after movement. Align the local rider with
        // that displayed pose without changing its physics/network position.
        Vector2 center = Player.Center;
        if (ControlsMovement && IsCurrentBinding())
            drawInfo.Position += attachedYard.RenderCoordinates.LocalToWorld(attachedPose.WorldToLocal(center)) - center;
        else if (remoteYard != null && remoteYard.IsLoaded)
            drawInfo.Position += remoteYard.RenderCoordinates.LocalToWorld(remotePose.WorldToLocal(center)) - center;
    }

    internal void ApplyRemoteAttachment(YardPlayerAttachment state)
    {
        remoteAttachment = state;
        remoteReceivedAt = Main.GameUpdateCount;
        remoteYard = null;
    }

    private void PrepareRemoteMovement()
    {
        remoteYard = null;
        if (Player.whoAmI == Main.myPlayer || !Player.active || Player.dead || Player.ghost
            || remoteAttachment.YardId == Guid.Empty
            || !YardSystem.Instance.TryGet(remoteAttachment.YardId, out Yard yard) || !yard.IsLoaded)
            return;
        remoteYard = yard;
        remotePose = yard.PlayerCoordinates;
        float ticks = Math.Min(Main.GameUpdateCount - remoteReceivedAt, (ulong)YardPlayerAttachmentPacket.MaxPredictionTicks);
        remoteCenter = remotePose.LocalToWorld(remoteAttachment.LocalCenter) + remoteAttachment.RelativeVelocity * ticks;
        Player.Center = remoteCenter;
        Player.velocity = remoteAttachment.RelativeVelocity;
    }

    private void PublishAttachment()
    {
        if (Main.netMode != NetmodeID.MultiplayerClient || Player.whoAmI != Main.myPlayer)
            return;
        Guid id = attachedYard?.Id ?? Guid.Empty;
        bool changed = id != lastSentYard || IsStandingOnYard != lastSentStanding;
        if (!changed && (id == Guid.Empty || Main.GameUpdateCount - lastSentAt < YardPlayerAttachmentPacket.SendIntervalTicks))
            return;
        YardPlayerAttachment state = new((byte)Player.whoAmI, id,
            id == Guid.Empty ? Vector2.Zero : attachedPose.WorldToLocal(Player.Center),
            id == Guid.Empty ? Vector2.Zero : Player.velocity - lastCarrier, IsStandingOnYard);
        YardSystem.Instance.SendPlayerAttachment(state);
        lastSentYard = id;
        lastSentStanding = IsStandingOnYard;
        lastSentAt = Main.GameUpdateCount;
    }

    private void ResolvePenetration(ref Vector2 position, ref Vector2 velocity)
    {
        for (int iteration = 0; iteration < 8; iteration++) {
            YardPlayerContact deepest = default;
            Yard contactYard = null;
            foreach (Surface surface in surfaces) {
                if (!Near(surface, position, Vector2.Zero)
                    || !surface.Yard.PlayerCollision.TryContact(surface.Pose, position, Size,
                        out YardPlayerContact contact, penetrationOnly: true)
                    || contact.Separation >= deepest.Separation)
                    continue;
                deepest = contact;
                contactYard = surface.Yard;
            }
            if (contactYard == null)
                break;
            RememberContact(contactYard);
            Vector2 correction = deepest.Normal * (-deepest.Separation + Skin);
            Vector2 previous = Player.position;
            Player.position = position;
            correction = MoveAgainstWorld(correction);
            Player.position = previous;
            position += correction;
            RemoveInwardVelocity(ref velocity, deepest.Normal);
            if (correction.LengthSquared() < 0.000001f)
                break;
        }
    }

    private bool TryCast(Vector2 position, Vector2 movement, Yard excluded,
        out Surface closest, out YardPlayerCast hit)
    {
        closest = default;
        hit = new YardPlayerCast(Vector2.Zero, 1);
        bool found = false;
        foreach (Surface surface in surfaces) {
            if (ReferenceEquals(surface.Yard, excluded) || !Near(surface, position, movement)
                || !surface.Yard.PlayerCollision.TryCast(surface.Pose, position, Size, movement,
                    out YardPlayerCast candidate, Player.controlDown || Player.GoingDownWithGrapple, Player.gravDir)
                || (found && candidate.Fraction >= hit.Fraction))
                continue;
            closest = surface;
            hit = candidate;
            found = true;
        }
        return found;
    }

    private Vector2 ClipCarrierAgainstYards(Vector2 carrier, Yard excluded) =>
        TryCast(Player.position, carrier, excluded, out _, out YardPlayerCast hit) ? carrier * hit.Fraction : carrier;

    private Vector2 MoveAgainstWorld(Vector2 movement)
    {
        Vector2 position = Player.position;
        int steps = Math.Clamp((int)MathF.Ceiling(movement.Length() / 8), 1, 256);
        Vector2 step = movement / steps;
        bool up = Collision.up;
        bool down = Collision.down;
        try {
            for (int i = 0; i < steps; i++)
                position += Collision.TileCollision(position, step, Player.width, Player.height,
                    Player.controlDown, Player.controlDown, (int)Player.gravDir);
        }
        finally {
            Collision.up = up;
            Collision.down = down;
        }
        return position - Player.position;
    }

    private bool Near(Surface surface, Vector2 position, Vector2 movement)
    {
        Vector2 min = Vector2.Min(position, position + movement);
        Vector2 max = Vector2.Max(position + Size, position + Size + movement);
        return YardCollisionGeometry.IntersectsBounds(surface.Pose, min, max - min, 2);
    }

    private bool TryGetSurface(Yard yard, out Surface found)
    {
        foreach (Surface surface in surfaces)
            if (ReferenceEquals(surface.Yard, yard)) {
                found = surface;
                return true;
            }
        found = default;
        return false;
    }

    private bool IsCurrentBinding() => attachedYard != null && attachedYard.IsLoaded
        && YardSystem.Instance.TryGet(attachedYard.Id, out Yard current) && ReferenceEquals(attachedYard, current);

    private void RememberContact(Yard yard)
    {
        if (touchedYard == null || ReferenceEquals(yard, attachedYard))
            touchedYard = yard;
    }

    private void ClearBinding()
    {
        attachedYard = null;
        lastCarrier = Vector2.Zero;
        relativeVelocity = false;
        IsStandingOnYard = false;
    }

    private static void RemoveInwardVelocity(ref Vector2 velocity, Vector2 normal)
    {
        float inward = Vector2.Dot(velocity, normal);
        if (inward < 0)
            velocity -= normal * inward;
    }

    private readonly record struct Surface(Yard Yard, YardCoordinates Pose);
}
