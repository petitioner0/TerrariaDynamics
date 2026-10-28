using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameInput;
using Terraria.ID;
using Terraria.ModLoader;
using Terraria.ModLoader.IO;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Common.TileYards;
using TerrariaDynamics.Common.UI;

namespace TerrariaDynamics.Common.Players;

public sealed class CircuitKeybindPlayer : ModPlayer
{
    internal const int SendIntervalTicks = 10;
    private Dictionary<CircuitKeybindTarget, Keys> bindings;
    private Dictionary<CircuitKeybindTarget, ulong> heldInputs;
    private HashSet<CircuitKeybindTarget> requestedInputs;

    public override void Initialize()
    {
        bindings = new Dictionary<CircuitKeybindTarget, Keys>();
        heldInputs = new Dictionary<CircuitKeybindTarget, ulong>();
        requestedInputs = new HashSet<CircuitKeybindTarget>();
    }

    public override void OnEnterWorld()
    {
        heldInputs.Clear();
        requestedInputs.Clear();
    }

    internal Keys GetBinding(CircuitKeybindTarget target) => bindings.GetValueOrDefault(target, Keys.None);

    internal void SetBinding(CircuitKeybindTarget target, Keys key)
    {
        if (key == Keys.None)
            bindings.Remove(target);
        else if (IsBindableKey(key))
            bindings[target] = key;
    }

    internal static bool IsBindableKey(Keys key) => key is not (Keys.None or Keys.Escape) && Enum.IsDefined(key);

    private bool CanReadInput => Player.active && !Player.dead && !Player.ghost && !Player.noItems && !Player.CCed
        && Main.hasFocus && !Main.gameMenu && !Main.gamePaused && !Main.blockInput
        && !Main.drawingPlayerChat && !Main.editSign && !Main.editChest && !Main.mapFullscreen
        && !Main.playerInventory && !Player.mouseInterface && !PlayerInput.WritingText
        && !ModContent.GetInstance<CircuitKeybindUISystem>().IsOpen;

    public override void PostUpdate()
    {
        if (Main.netMode == NetmodeID.Server || Player.whoAmI != Main.myPlayer)
            return;
        requestedInputs.Clear();
        if (CanReadInput && Player.GetModPlayer<YardDeepBindingPlayer>().BoundYard is Yard yard) {
            Guid worldId = Main.ActiveWorldFileData.UniqueId;
            foreach (var binding in bindings)
                if (binding.Key.WorldId == worldId && binding.Key.YardId == yard.Id
                    && Main.keyState.IsKeyDown(binding.Value))
                    requestedInputs.Add(binding.Key);
        }
        YardSystem system = YardSystem.Instance;
        foreach (CircuitKeybindTarget target in heldInputs.Keys.ToArray()) {
            if (requestedInputs.Contains(target))
                continue;
            system.SubmitCircuitKeyInput(target, Player, held: false);
            heldInputs.Remove(target);
        }
        foreach (CircuitKeybindTarget target in requestedInputs) {
            if (Main.netMode == NetmodeID.MultiplayerClient && heldInputs.TryGetValue(target, out ulong sentAt)
                && Main.GameUpdateCount - sentAt < SendIntervalTicks)
                continue;
            if (system.SubmitCircuitKeyInput(target, Player, held: true))
                heldInputs[target] = Main.GameUpdateCount;
        }
    }

    public override void UpdateDead()
    {
        if (Main.netMode == NetmodeID.Server || Player.whoAmI != Main.myPlayer)
            return;
        foreach (CircuitKeybindTarget target in heldInputs.Keys.ToArray())
            YardSystem.Instance.SubmitCircuitKeyInput(target, Player, held: false);
        heldInputs.Clear();
    }

    public override void SetControls()
    {
        if (Main.netMode == NetmodeID.Server || Player.whoAmI != Main.myPlayer
            || !ModContent.GetInstance<CircuitKeybindUISystem>().IsListening)
            return;
        Player.controlUp = Player.controlDown = Player.controlLeft = Player.controlRight = false;
        Player.controlJump = Player.controlUseItem = Player.controlUseTile = false;
        Player.controlHook = Player.controlMount = Player.controlThrow = Player.controlInv = false;
    }

    public override void SaveData(TagCompound tag)
    {
        var saved = new List<TagCompound>();
        foreach (var binding in bindings) {
            TagCompound entry = binding.Key.Save();
            entry["key"] = (int)binding.Value;
            saved.Add(entry);
        }
        if (saved.Count > 0)
            tag["circuitKeybinds"] = saved;
    }

    public override void LoadData(TagCompound tag)
    {
        bindings = new Dictionary<CircuitKeybindTarget, Keys>();
        foreach (TagCompound entry in tag.GetList<TagCompound>("circuitKeybinds")) {
            Keys key = (Keys)entry.GetInt("key");
            if (IsBindableKey(key) && CircuitKeybindTarget.TryLoad(entry, out CircuitKeybindTarget target))
                bindings[target] = key;
        }
    }
}
