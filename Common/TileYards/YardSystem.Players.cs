using System;
using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Players;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem
{
    private static void FrameYardPlayer(On_Player.orig_PlayerFrame orig, Player player)
    {
        if (!player.TryGetModPlayer(out YardMovementPlayer movement)
            || !movement.TryGetGroundedFrameVelocity(out Vector2 velocity)) {
            orig(player);
            return;
        }
        // PlayerFrame precedes ModPlayer.PostUpdate, where Yard grounding is
        // committed. Animate the current support without changing displacement.
        Vector2 previousVelocity = player.velocity;
        try {
            player.velocity = velocity;
            orig(player);
        }
        finally {
            player.velocity = previousVelocity;
        }
    }

    private static void ClearPlayerAttachments()
    {
        foreach (Player player in Main.player)
            if (player != null) {
                if (player.TryGetModPlayer(out YardRestingPlayer resting))
                    resting.ResetRestingState();
                if (player.TryGetModPlayer(out YardDeepBindingPlayer deepBinding))
                    deepBinding.Clear();
                if (player.TryGetModPlayer(out YardMovementPlayer movement))
                    movement.ResetAttachmentState();
            }
    }

    internal void SendPlayerAttachment(YardPlayerAttachment state, int toWho = -1, int ignore = -1)
    {
        if (Main.netMode == NetmodeID.SinglePlayer)
            return;
        var packet = Packet(YardPacketKind.PlayerAttachment);
        YardPlayerAttachmentPacket.Write(packet, state);
        packet.Send(toWho, ignore);
    }

    private void ReceivePlayerAttachment(BinaryReader reader, int sender)
    {
        YardPlayerAttachment state = YardPlayerAttachmentPacket.Read(reader);
        if (state.Player >= Main.maxPlayers)
            throw new InvalidDataException("Invalid Yard rider player.");
        if (Main.netMode == NetmodeID.Server) {
            if (state.Player != sender)
                throw new InvalidDataException("A client cannot change another Yard rider.");
            if (Main.player[sender].GetModPlayer<YardDeepBindingPlayer>().IsBound)
                return;
            if (state.YardId != Guid.Empty) {
                Player player = Main.player[sender];
                float margin = new Vector2(player.width, player.height).Length() / 2 + 2;
                if (!TryGet(state.YardId, out Yard yard) || !yard.IsLoaded
                    || state.LocalCenter.X < -margin || state.LocalCenter.Y < -margin
                    || state.LocalCenter.X > yard.Width * YardCoordinates.TileSize + margin
                    || state.LocalCenter.Y > yard.Height * YardCoordinates.TileSize + margin) {
                    // A return/removal can race an in-flight attachment update.
                    state = new YardPlayerAttachment(state.Player, Guid.Empty, Vector2.Zero, Vector2.Zero, false);
                }
            }
            Main.player[state.Player].GetModPlayer<YardMovementPlayer>().ApplyRemoteAttachment(state);
            SendPlayerAttachment(state, ignore: sender);
        }
        else if (state.Player != Main.myPlayer)
            Main.player[state.Player].GetModPlayer<YardMovementPlayer>().ApplyRemoteAttachment(state);
    }
}
