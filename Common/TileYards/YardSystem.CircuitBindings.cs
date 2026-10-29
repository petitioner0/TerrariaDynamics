using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.DataStructures;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Common.Networking;
using TerrariaDynamics.Common.Players;

namespace TerrariaDynamics.Common.TileYards;

public sealed partial class YardSystem
{
    private readonly Dictionary<CircuitKeybindTarget, Keys> circuitBindings = new();
    internal IReadOnlyDictionary<CircuitKeybindTarget, Keys> CircuitBindings => circuitBindings;

    internal Keys GetCircuitBinding(CircuitKeybindTarget target) => circuitBindings.GetValueOrDefault(target, Keys.None);

    internal void RequestCircuitBinding(CircuitKeybindTarget target, Keys key)
    {
        if (Main.netMode == NetmodeID.Server || !IsSynchronized
            || key != Keys.None && !CircuitKeybindPlayer.IsBindableKey(key)
            || !TryResolveCircuitTarget(target, out _, out KeybindCircuitTile circuit, out _, allowWorld: true))
            return;
        if (Main.netMode == NetmodeID.MultiplayerClient) {
            var packet = Packet(YardPacketKind.RequestCircuitBinding);
            packet.Write(target.YardId.ToByteArray());
            packet.Write(target.Cell.X);
            packet.Write(target.Cell.Y);
            packet.Write(circuit.Type);
            packet.Write((int)key);
            packet.Send();
        }
        else
            SetCircuitBinding(target, key, Main.LocalPlayer);
    }

    private void ReceiveCircuitBindingRequest(BinaryReader reader, int sender)
    {
        Guid yardId = YardPacketCodec.ReadId(reader);
        Point cell = new(reader.ReadInt32(), reader.ReadInt32());
        int type = reader.ReadInt32();
        Keys key = (Keys)reader.ReadInt32();
        if (type < TileID.Count || type >= TileLoader.TileCount
            || TileLoader.GetTile(type) is not KeybindCircuitTile circuit)
            return;
        var target = new CircuitKeybindTarget(Main.ActiveWorldFileData.UniqueId, yardId, cell, circuit.FullName);
        SetCircuitBinding(target, key, Main.player[sender]);
    }

    private void SetCircuitBinding(CircuitKeybindTarget target, Keys key, Player player)
    {
        lock (worldSaveLock) {
            if (WorldGen.gen || WorldGen.IsGeneratingHardMode || !player.active || player.dead || player.ghost
                || key != Keys.None && !CircuitKeybindPlayer.IsBindableKey(key)
                || !TryResolveCircuitTarget(target, out Point storage, out _, out _, allowWorld: true)
                || !player.IsInTileInteractionRange(storage.X, storage.Y, TileReachCheckSettings.Simple)
                || GetCircuitBinding(target) == key)
                return;
            ApplyCircuitBinding(target, key);
            Publish(() => {
                TagCompound tag = target.Save();
                tag["key"] = (int)key;
                SendEvent(YardEventKind.CircuitBinding, tag);
            });
        }
    }

    private void ApplyCircuitBinding(CircuitKeybindTarget target, Keys key)
    {
        if (key == Keys.None)
            circuitBindings.Remove(target);
        else
            circuitBindings[target] = key;
        // A changed shared key cancels every player's previous held input.
        // Wire latches are independent and remain untouched.
        if (circuitActivations.TryGetValue(target, out CircuitActivationState state))
            state.KeyboardInputs.Clear();
    }

    private void ReceiveCircuitBinding(TagCompound tag)
    {
        Keys key = (Keys)tag.GetInt("key");
        if (!CircuitKeybindTarget.TryLoad(tag, out CircuitKeybindTarget target)
            || target.WorldId != Main.ActiveWorldFileData.UniqueId
            || key != Keys.None && !CircuitKeybindPlayer.IsBindableKey(key))
            throw new InvalidDataException("Invalid shared circuit binding.");
        // World tiles may not be streamed to this client yet. Keep the server's
        // binding even when its target cannot currently be resolved locally.
        ApplyCircuitBinding(target, key);
    }

    private void SaveCircuitBindings(TagCompound tag)
    {
        var saved = new List<TagCompound>();
        foreach (var binding in circuitBindings) {
            TagCompound entry = binding.Key.Save();
            entry["key"] = (int)binding.Value;
            saved.Add(entry);
        }
        tag["circuitKeybinds"] = saved;
    }

    private void LoadCircuitBindings(TagCompound tag)
    {
        circuitBindings.Clear();
        foreach (TagCompound entry in tag.GetList<TagCompound>("circuitKeybinds")) {
            Keys key = (Keys)entry.GetInt("key");
            if (CircuitKeybindPlayer.IsBindableKey(key) && CircuitKeybindTarget.TryLoad(entry, out CircuitKeybindTarget target)
                && target.WorldId == Main.ActiveWorldFileData.UniqueId)
                circuitBindings[target] = key;
        }
    }

    // The authority moves bindings when a transfer commits; clients repeat the
    // same move only while applying its ordered creation/return event.
    private void MoveCircuitBindings(Guid fromYard, Rectangle fromBounds, Guid toYard, Point toOrigin)
    {
        foreach (var binding in circuitBindings.ToArray()) {
            CircuitKeybindTarget target = binding.Key;
            if (target.YardId != fromYard || !fromBounds.Contains(target.Cell))
                continue;
            circuitBindings.Remove(target);
            Point cell = target.Cell - fromBounds.Location + toOrigin;
            circuitBindings[target with { YardId = toYard, Cell = cell }] = binding.Value;
        }
    }
}
