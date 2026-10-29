using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.ID;
using Terraria.ModLoader;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Players;
using TerrariaDynamics.Common.WorldEditing;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem
{
    private const int CircuitInputTimeoutTicks = CircuitKeybindPlayer.SendIntervalTicks * 3;
    private readonly Dictionary<CircuitKeybindTarget, CircuitActivationState> circuitActivations = new();
    private CircuitKeybindTarget? circuitWireSource;

    private sealed class CircuitActivationState
    {
        public readonly Dictionary<int, ulong> KeyboardInputs = new();
        public bool WireActive;
        public int WireUser = 255;
        public bool Active;
    }

    internal bool TryCreateCircuitTarget(int x, int y, out CircuitKeybindTarget target, bool allowWorld = false)
    {
        target = default;
        bool inYard = TryStorageYard(x, y, out Yard yard);
        if (!inYard && (!allowWorld || !PlayableBounds.Contains(x, y)))
            return false;
        Tile tile = Main.tile[x, y];
        if (!tile.HasTile || TileLoader.GetTile(tile.TileType) is not KeybindCircuitTile circuit)
            return false;
        Rectangle bounds = circuit.GetCircuitBounds(x, y);
        if (!bounds.Contains(x, y) || !ValidCircuitBounds(bounds, inYard ? yard.Bounds : PlayableBounds))
            return false;
        Point cell = inYard ? yard.Coordinates.StorageToLocalTile(bounds.Location) : bounds.Location;
        target = new CircuitKeybindTarget(Main.ActiveWorldFileData.UniqueId, yard?.Id ?? Guid.Empty, cell, circuit.FullName);
        return true;
    }

    internal bool TryResolveCircuitTarget(CircuitKeybindTarget target, out Point storage,
        out KeybindCircuitTile circuit, out Yard yard, bool allowWorld = false)
    {
        storage = target.Cell;
        circuit = null;
        yard = null;
        if (target.WorldId != Main.ActiveWorldFileData.UniqueId)
            return false;
        Rectangle area;
        if (target.YardId == Guid.Empty) {
            if (!allowWorld || !PlayableBounds.Contains(storage))
                return false;
            area = PlayableBounds;
        }
        else {
            if (!TryGet(target.YardId, out yard) || !yard.IsLoaded
                || target.Cell.X < 0 || target.Cell.Y < 0 || target.Cell.X >= yard.Width || target.Cell.Y >= yard.Height)
                return false;
            storage = yard.Coordinates.LocalTileToStorage(target.Cell);
            area = yard.Bounds;
        }
        Tile tile = Main.tile[storage.X, storage.Y];
        if (!tile.HasTile || TileLoader.GetTile(tile.TileType) is not KeybindCircuitTile found || found.FullName != target.TileName)
            return false;
        Rectangle bounds = found.GetCircuitBounds(storage.X, storage.Y);
        if (bounds.Location != storage || !ValidCircuitBounds(bounds, area))
            return false;
        circuit = found;
        return true;
    }

    private static bool ValidCircuitBounds(Rectangle bounds, Rectangle area) =>
        bounds.Width is > 0 and <= byte.MaxValue && bounds.Height is > 0 and <= byte.MaxValue && area.Contains(bounds);

    internal bool IsCircuitActive(CircuitKeybindTarget target) =>
        circuitActivations.TryGetValue(target, out CircuitActivationState state) && state.Active;

    private CircuitActivationState CircuitState(CircuitKeybindTarget target)
    {
        if (!circuitActivations.TryGetValue(target, out CircuitActivationState state))
            circuitActivations.Add(target, state = new CircuitActivationState());
        return state;
    }

    private void SendCircuitActivation(CircuitKeybindTarget target, bool active, int client = -1)
    {
        if (Main.netMode != NetmodeID.Server)
            return;
        var packet = Packet(YardPacketKind.CircuitActivation);
        packet.Write(networkRevision);
        packet.Write(target.YardId.ToByteArray());
        packet.Write(target.Cell.X);
        packet.Write(target.Cell.Y);
        packet.Write(target.TileName);
        packet.Write(active);
        packet.Send(client);
    }

    private void ReceiveCircuitActivation(BinaryReader reader)
    {
        long revision = reader.ReadInt64();
        Guid yardId = YardPacketCodec.ReadId(reader);
        Point cell = new(reader.ReadInt32(), reader.ReadInt32());
        string tileName = reader.ReadString();
        bool active = reader.ReadBoolean();
        if (!IsSynchronized || revision < networkRevision)
            return;
        if (revision > networkRevision) {
            RequestSnapshot();
            return;
        }
        var target = new CircuitKeybindTarget(Main.ActiveWorldFileData.UniqueId, yardId, cell, tileName);
        if (!active)
            circuitActivations.Remove(target);
        else if (TryResolveCircuitTarget(target, out _, out _, out _))
            CircuitState(target).Active = true;
    }

    internal void RecordCircuitWireInput(int x, int y)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;
        lock (worldSaveLock) {
            if (!TryCreateCircuitTarget(x, y, out CircuitKeybindTarget target) || target == circuitWireSource)
                return;
            CircuitActivationState state = CircuitState(target);
            // Each received pulse toggles a latched input. Multiple pulses
            // before the next state check still toggle independently.
            state.WireActive = !state.WireActive;
            state.WireUser = YardWiringState.CurrentUser;
        }
    }

    internal void SendCircuitWireSignal(Rectangle bounds)
    {
        if (!TryCreateCircuitTarget(bounds.X, bounds.Y, out CircuitKeybindTarget target))
            return;
        CircuitKeybindTarget? previousSource = circuitWireSource;
        try {
            // A keyboard-generated pulse controls connected receivers without
            // latching the emitting device on after its key is released.
            circuitWireSource = target;
            Wiring.TripWire(bounds.X, bounds.Y, bounds.Width, bounds.Height);
        }
        finally {
            circuitWireSource = previousSource;
        }
    }

    internal bool SubmitCircuitKeyInput(CircuitKeybindTarget target, Player player, bool held)
    {
        if (player == null || (uint)player.whoAmI >= Main.maxPlayers)
            return false;
        if (Main.netMode == NetmodeID.MultiplayerClient) {
            if (player.whoAmI != Main.myPlayer || held && !IsSynchronized
                || !ModContent.TryFind(target.TileName, out ModTile tile) || tile is not KeybindCircuitTile circuit)
                return false;
            if (held && (!TryResolveCircuitTarget(target, out Point storage, out _, out Yard yard)
                || !CanHoldCircuitKey(player, yard)
                || !circuit.CanTriggerWithKey(player, storage.X, storage.Y)))
                return false;
            var packet = Packet(YardPacketKind.CircuitKeyInput);
            packet.Write(target.YardId.ToByteArray());
            packet.Write(target.Cell.X);
            packet.Write(target.Cell.Y);
            packet.Write(circuit.Type);
            packet.Write((int)GetCircuitBinding(target));
            packet.Write(held);
            packet.Send();
            return true;
        }
        return SetCircuitKeyInput(target, player, held, GetCircuitBinding(target));
    }

    private void ReceiveCircuitKeyInput(BinaryReader reader, int sender)
    {
        Guid yardId = YardPacketCodec.ReadId(reader);
        Point cell = new(reader.ReadInt32(), reader.ReadInt32());
        int type = reader.ReadInt32();
        Keys key = (Keys)reader.ReadInt32();
        bool held = reader.ReadBoolean();
        if (type < TileID.Count || type >= TileLoader.TileCount || TileLoader.GetTile(type) is not KeybindCircuitTile circuit)
            return;
        var target = new CircuitKeybindTarget(Main.ActiveWorldFileData.UniqueId, yardId, cell, circuit.FullName);
        SetCircuitKeyInput(target, Main.player[sender], held, key);
    }

    // Keep Yard ownership outside the tile's optional eligibility hook, so a
    // derived tile cannot enable shortcuts for players bound to another Yard.
    private static bool CanHoldCircuitKey(Player player, Yard yard) => player.active && !player.dead && !player.ghost
        && !player.noItems && !player.CCed
        && ReferenceEquals(player.GetModPlayer<YardDeepBindingPlayer>().BoundYard, yard);

    private bool SetCircuitKeyInput(CircuitKeybindTarget target, Player player, bool held, Keys key)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient || (uint)player.whoAmI >= Main.maxPlayers)
            return false;
        lock (worldSaveLock) {
            // Releases remain valid after losing deep binding, death, moving
            // out of range or removing the tile. They can only remove this user.
            if (!held) {
                if (circuitActivations.TryGetValue(target, out CircuitActivationState state))
                    state.KeyboardInputs.Remove(player.whoAmI);
                return true;
            }
            if (WorldGen.gen || WorldGen.IsGeneratingHardMode || key == Keys.None || GetCircuitBinding(target) != key
                || !TryResolveCircuitTarget(target, out Point storage, out KeybindCircuitTile circuit, out Yard yard)
                || !CanHoldCircuitKey(player, yard)
                || !circuit.CanTriggerWithKey(player, storage.X, storage.Y))
                return false;
            CircuitState(target).KeyboardInputs[player.whoAmI] = Main.GameUpdateCount;
            return true;
        }
    }

    // Called once before the authoritative physics step, after Yard timers have
    // produced their native wire pulses. Inputs never invoke a response directly.
    private void UpdateCircuitActivations()
    {
        foreach (var entry in circuitActivations.ToArray()) {
            CircuitKeybindTarget target = entry.Key;
            CircuitActivationState state = entry.Value;
            if (!TryResolveCircuitTarget(target, out Point storage, out KeybindCircuitTile circuit, out Yard yard)) {
                circuitActivations.Remove(target);
                if (state.Active)
                    SendCircuitActivation(target, false);
                continue;
            }
            foreach (var input in state.KeyboardInputs.ToArray()) {
                Player player = Main.player[input.Key];
                if (Main.GameUpdateCount - input.Value > CircuitInputTimeoutTicks
                    || !CanHoldCircuitKey(player, yard) || !circuit.CanTriggerWithKey(player, storage.X, storage.Y))
                    state.KeyboardInputs.Remove(input.Key);
            }
            bool keyboardActive = state.KeyboardInputs.Count > 0;
            bool active = keyboardActive || state.WireActive;
            bool activated = active && !state.Active;
            if (active != state.Active)
                SendCircuitActivation(target, active);
            state.Active = active;
            if (!active) {
                circuitActivations.Remove(target);
                continue;
            }
            if (!activated)
                continue;
            int user = keyboardActive ? state.KeyboardInputs.Keys.First() : state.WireUser;
            void Activate()
            {
                int previousUser = YardWiringState.CurrentUser;
                try {
                    Wiring.SetCurrentUser(user);
                    circuit.CheckActivation(storage.X, storage.Y, keyboardActive);
                }
                finally {
                    Wiring.SetCurrentUser(previousUser);
                }
            }
            RunTileAction(yard, _ => Activate());
        }
    }

    private void UpdateActiveCircuits()
    {
        foreach (var entry in circuitActivations.ToArray()) {
            if (entry.Value.Active && TryResolveCircuitTarget(entry.Key, out Point storage,
                out KeybindCircuitTile circuit, out Yard yard))
                RunTileAction(yard, _ => circuit.UpdateActiveState(storage.X, storage.Y, yard));
        }
    }

    private void UpdateCircuitBodyTypes()
    {
        var staticYards = new HashSet<Yard>();
        foreach (var entry in circuitActivations) {
            if (entry.Value.Active && TryResolveCircuitTarget(entry.Key, out Point storage,
                out KeybindCircuitTile circuit, out Yard yard)
                && circuit.KeepsYardStaticWhileActive && !Main.tile[storage.X, storage.Y].IsActuated)
                staticYards.Add(yard);
        }
        // Recompute from live devices so release, removal and actuation all
        // restore dynamics, without one device cancelling another's request.
        foreach (Yard yard in regions.Values)
            yard.SetPhysicsStatic(staticYards.Contains(yard));
    }

    internal void ObserveCircuitTiles(Rectangle bounds)
    {
        if (interactionYard != null && interactionYard.Bounds.Contains(bounds))
            interactionChanges?.Observe(bounds);
    }
}
