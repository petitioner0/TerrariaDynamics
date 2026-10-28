using System.IO;
using Microsoft.Xna.Framework;
using Terraria;
using Terraria.ID;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Physics;
using TerrariaDynamics.Common.Players;
using TerrariaDynamics.Content.Items;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem
{
    private const float TestForceAcceleration = PhysicsSystem.Gravity * 2;
    private const int ForceInputTimeoutTicks = YardForcePlayer.SendIntervalTicks * 3;
    private readonly TestForceInput[] testForceInputs = new TestForceInput[256];
    private readonly record struct TestForceInput(Point Direction, int ItemSlot, ulong ReceivedAt);

    internal void SubmitTestForceInput(Player player, Point direction)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient) {
            if (direction != Point.Zero && !IsSynchronized)
                return;
            var packet = Packet(YardPacketKind.RequestForceInput);
            packet.Write((byte)player.selectedItem);
            packet.Write((sbyte)direction.X);
            packet.Write((sbyte)direction.Y);
            packet.Send();
        }
        else if (Main.netMode == NetmodeID.SinglePlayer)
            SetTestForceInput(player.whoAmI, player.selectedItem, direction);
    }

    private void ReceiveTestForceInput(BinaryReader reader, int sender)
    {
        int slot = reader.ReadByte();
        var direction = new Point(reader.ReadSByte(), reader.ReadSByte());
        SetTestForceInput(sender, slot, direction);
    }

    private void SetTestForceInput(int playerId, int slot, Point direction)
    {
        if (direction.X is < -1 or > 1 || direction.Y is < -1 or > 1)
            throw new InvalidDataException("Invalid Yard force direction.");
        if ((uint)playerId >= Main.maxPlayers)
            return;
        lock (worldSaveLock) {
            // Stop input remains valid after switching away from the tester.
            if (direction == Point.Zero) {
                testForceInputs[playerId] = default;
                return;
            }
            Player player = Main.player[playerId];
            if (!CanApplyTestForce(player, slot)) {
                testForceInputs[playerId] = default;
                return;
            }
            testForceInputs[playerId] = new TestForceInput(direction, slot, Main.GameUpdateCount + 1);
        }
    }

    private static bool CanApplyTestForce(Player player, int slot) =>
        player.active && !player.dead && !player.noItems && !player.CCed
        && slot == player.selectedItem && (uint)slot < player.inventory.Length
        && player.inventory[slot].ModItem is YardForceTester;

    // Called exactly once before each authoritative step, under the save lock.
    // Packets update input only, so extra packets cannot multiply the force.
    private void ApplyTestForces()
    {
        ulong now = Main.GameUpdateCount + 1;
        for (int playerId = 0; playerId < Main.maxPlayers; playerId++) {
            TestForceInput input = testForceInputs[playerId];
            if (input.Direction == Point.Zero)
                continue;
            if (now - input.ReceivedAt > ForceInputTimeoutTicks
                || !CanApplyTestForce(Main.player[playerId], input.ItemSlot)) {
                testForceInputs[playerId] = default;
                continue;
            }
            Vector2 acceleration = Vector2.Normalize(new Vector2(input.Direction.X, input.Direction.Y))
                * TestForceAcceleration;
            foreach (Yard yard in regions.Values)
                if (yard.IsLoaded && yard.HasPhysicsBody)
                    yard.ApplyForceToCenter(acceleration * yard.PhysicsMass);
        }
    }
}
