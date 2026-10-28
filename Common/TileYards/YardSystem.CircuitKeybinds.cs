using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
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

    private sealed class CircuitActivationState
    {
        public readonly Dictionary<int, ulong> KeyboardInputs = new();
        public bool WirePending;
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

    // Configuration may address world tiles; activation callers deliberately
    // keep allowWorld=false so world keys/wires never enter the active state.
    private static void MoveLocalCircuitBindings(Guid fromYard, Rectangle fromBounds, Guid toYard, Point toOrigin)
    {
        if (Main.netMode != NetmodeID.Server)
            Main.LocalPlayer.GetModPlayer<CircuitKeybindPlayer>().MoveBindings(fromYard, fromBounds, toYard, toOrigin);
    }

    internal bool IsCircuitActive(CircuitKeybindTarget target) =>
        circuitActivations.TryGetValue(target, out CircuitActivationState state) && state.Active;

    private CircuitActivationState CircuitState(CircuitKeybindTarget target)
    {
        if (!circuitActivations.TryGetValue(target, out CircuitActivationState state))
            circuitActivations.Add(target, state = new CircuitActivationState());
        return state;
    }

    internal void RecordCircuitWireInput(int x, int y)
    {
        if (Main.netMode == NetmodeID.MultiplayerClient)
            return;
        lock (worldSaveLock) {
            if (!TryCreateCircuitTarget(x, y, out CircuitKeybindTarget target))
                return;
            CircuitActivationState state = CircuitState(target);
            // Vanilla wiring supplies pulses, not a persistent powered level or
            // a release callback. Consume this input at the next state check.
            state.WirePending = true;
            state.WireUser = YardWiringState.CurrentUser;
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
            packet.Write(held);
            packet.Send();
            return true;
        }
        return SetCircuitKeyInput(target, player, held);
    }

    private void ReceiveCircuitKeyInput(BinaryReader reader, int sender)
    {
        Guid yardId = YardPacketCodec.ReadId(reader);
        Point cell = new(reader.ReadInt32(), reader.ReadInt32());
        int type = reader.ReadInt32();
        bool held = reader.ReadBoolean();
        if (type < TileID.Count || type >= TileLoader.TileCount || TileLoader.GetTile(type) is not KeybindCircuitTile circuit)
            return;
        var target = new CircuitKeybindTarget(Main.ActiveWorldFileData.UniqueId, yardId, cell, circuit.FullName);
        SetCircuitKeyInput(target, Main.player[sender], held);
    }

    // Keep Yard ownership outside the tile's optional eligibility hook, so a
    // derived tile cannot enable shortcuts for players bound to another Yard.
    private static bool CanHoldCircuitKey(Player player, Yard yard) => player.active && !player.dead && !player.ghost
        && !player.noItems && !player.CCed
        && ReferenceEquals(player.GetModPlayer<YardDeepBindingPlayer>().BoundYard, yard);

    private bool SetCircuitKeyInput(CircuitKeybindTarget target, Player player, bool held)
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
            if (WorldGen.gen || WorldGen.IsGeneratingHardMode
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
                continue;
            }
            foreach (var input in state.KeyboardInputs.ToArray()) {
                Player player = Main.player[input.Key];
                if (Main.GameUpdateCount - input.Value > CircuitInputTimeoutTicks
                    || !CanHoldCircuitKey(player, yard) || !circuit.CanTriggerWithKey(player, storage.X, storage.Y))
                    state.KeyboardInputs.Remove(input.Key);
            }
            bool keyboardActive = state.KeyboardInputs.Count > 0;
            bool active = keyboardActive || state.WirePending;
            bool activated = active && !state.Active;
            state.Active = active;
            state.WirePending = false;
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

    internal void ObserveCircuitTiles(Rectangle bounds)
    {
        if (interactionYard != null && interactionYard.Bounds.Contains(bounds))
            interactionChanges?.Observe(bounds);
    }
}
