using Microsoft.Xna.Framework;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ObjectData;
using TerrariaDynamics.Common.TileYards;
using TerrariaDynamics.Common.UI;

namespace TerrariaDynamics.Common.Circuitry;

/// <summary>
/// Yard-only wire receiver with a per-player keyboard binding. Shortcuts require
/// deep binding to the owning Yard. Concrete tiles configure placement and
/// artwork, and implement OnCircuitTriggered.
/// </summary>
public abstract class KeybindCircuitTile : ModTile
{
    /// <summary>Whether a keyboard activation also pulses connected vanilla wires.</summary>
    public virtual bool SendsWireSignalOnKeyPress => true;

    /// <summary>
    /// Additional keyboard eligibility after deep binding to the owning Yard has
    /// been checked. Override to change the default vanilla interaction distance.
    /// </summary>
    public virtual bool CanTriggerWithKey(Player player, int i, int j) =>
        player.IsInTileInteractionRange(i, j, TileReachCheckSettings.Simple);

    /// <summary>
    /// Response to an inactive-to-active transition, evaluated from wire and
    /// keyboard input once per update. Runs only in single player or on the
    /// server; i/j are the object's top-left tile address.
    /// </summary>
    protected abstract void OnCircuitTriggered(int i, int j);

    /// <summary>
    /// Continuous effect while active, once per authoritative physics update,
    /// after collision bodies have been refreshed and before simulation.
    /// This does not repeat OnCircuitTriggered or emit another wire pulse.
    /// </summary>
    protected virtual void OnCircuitActive(int i, int j, Yard yard) { }

    public sealed override bool RightClick(int i, int j)
    {
        if (Main.netMode == NetmodeID.Server)
            return false;
        return ModContent.GetInstance<CircuitKeybindUISystem>().Open(i, j);
    }

    public sealed override void HitWire(int i, int j)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;
        Rectangle bounds = GetCircuitBounds(i, j);
        if (Wiring.running) {
            for (int x = bounds.Left; x < bounds.Right; x++)
                for (int y = bounds.Top; y < bounds.Bottom; y++)
                    Wiring.SkipWire(x, y);
        }
        YardSystem.Instance.RecordCircuitWireInput(bounds.X, bounds.Y);
    }

    /// <summary>
    /// The last evaluated activation state of this placed object. State is
    /// authoritative in single player and on the server, and is not persisted.
    /// </summary>
    public bool IsActive(int i, int j) =>
        YardSystem.Instance.TryCreateCircuitTarget(i, j, out CircuitKeybindTarget target)
        && target.TileName == FullName
        && YardSystem.Instance.IsCircuitActive(target);

    /// <summary>
    /// Normalizes any cell of a TileObjectData multitile to its full bounds.
    /// Override for tiles with custom frame layouts.
    /// </summary>
    public virtual Rectangle GetCircuitBounds(int i, int j)
    {
        Tile tile = Main.tile[i, j];
        TileObjectData data = TileObjectData.GetTileData(tile);
        if (data == null)
            return new Rectangle(i, j, 1, 1);
        int column = tile.TileFrameX % data.CoordinateFullWidth / (data.CoordinateWidth + data.CoordinatePadding);
        int frameY = tile.TileFrameY % data.CoordinateFullHeight;
        int row = 0;
        while (row < data.Height - 1 && frameY >= data.CoordinateHeights[row] + data.CoordinatePadding) {
            frameY -= data.CoordinateHeights[row] + data.CoordinatePadding;
            row++;
        }
        return new Rectangle(i - column, j - row, data.Width, data.Height);
    }

    internal void CheckActivation(int i, int j, bool keyboardActive)
    {
        if (IsActive(i, j))
            TriggerCircuit(i, j, keyboardActive);
    }

    internal void UpdateActiveState(int i, int j, Yard yard)
    {
        if (IsActive(i, j) && !Main.tile[i, j].IsActuated)
            OnCircuitActive(i, j, yard);
    }

    private void TriggerCircuit(int i, int j, bool keyboardActive)
    {
        Rectangle bounds = GetCircuitBounds(i, j);
        YardSystem.Instance.ObserveCircuitTiles(bounds);
        OnCircuitTriggered(bounds.X, bounds.Y);
        if (Main.netMode == NetmodeID.Server)
            NetMessage.SendTileSquare(-1, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        // Emit only for the activation edge driven by a keyboard input, never
        // once per held tick or in response to an incoming wire pulse.
        if (keyboardActive && SendsWireSignalOnKeyPress)
            Wiring.TripWire(bounds.X, bounds.Y, bounds.Width, bounds.Height);
    }
}
