using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaDynamics.Common.TileYards;
using TerrariaDynamics.Content.Items;

namespace TerrariaDynamics.Common.Players;

public sealed class YardForcePlayer : ModPlayer
{
    internal const int SendIntervalTicks = 10;
    private Point requestedDirection;
    private Point lastSentDirection;
    private int lastSentSlot = -1;
    private ulong lastSentAt;

    private bool CanControl => Player.active && !Player.dead && !Player.noItems && !Player.CCed
        && Player.HeldItem.ModItem is YardForceTester && Main.mouseRight && Main.hasFocus
        && !Main.gameMenu && (Main.netMode != NetmodeID.SinglePlayer || !Main.gamePaused)
        && !Player.mouseInterface && !Main.mapFullscreen && !Main.blockInput
        && !Main.drawingPlayerChat && !Main.editSign && !Main.editChest;

    public override void OnEnterWorld()
    {
        requestedDirection = lastSentDirection = Point.Zero;
        lastSentSlot = -1;
        lastSentAt = 0;
    }

    public override void ProcessTriggers(TriggersSet triggersSet)
    {
        requestedDirection = Point.Zero;
        if (!CanControl)
            return;
        bool up = triggersSet.Up || Main.keyState.IsKeyDown(Keys.Up);
        bool down = triggersSet.Down || Main.keyState.IsKeyDown(Keys.Down);
        bool left = triggersSet.Left || Main.keyState.IsKeyDown(Keys.Left);
        bool right = triggersSet.Right || Main.keyState.IsKeyDown(Keys.Right);
        requestedDirection = new Point((right ? 1 : 0) - (left ? 1 : 0), (down ? 1 : 0) - (up ? 1 : 0));
    }

    public override void SetControls()
    {
        if (!CanControl)
            return;
        // Movement keybinds control the yards while the tester is engaged.
        Player.controlUp = Player.controlDown = Player.controlLeft = Player.controlRight = false;
    }

    public override void PostUpdate()
    {
        if (Main.netMode == NetmodeID.Server || Player.whoAmI != Main.myPlayer)
            return;
        if (!CanControl)
            requestedDirection = Point.Zero;
        if (requestedDirection == lastSentDirection
            && (requestedDirection == Point.Zero
                || (Player.selectedItem == lastSentSlot && Main.GameUpdateCount - lastSentAt < SendIntervalTicks)))
            return;
        YardSystem.Instance.SubmitTestForceInput(Player, requestedDirection);
        lastSentDirection = requestedDirection;
        lastSentSlot = Player.selectedItem;
        lastSentAt = Main.GameUpdateCount;
    }
}
