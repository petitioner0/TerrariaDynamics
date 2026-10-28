using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.DataStructures;
using Terraria.GameInput;
using Terraria.ModLoader;
using Terraria.UI;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.UI;

[Autoload(Side = ModSide.Client)]
public sealed class CircuitKeybindUISystem : ModSystem
{
    private UserInterface userInterface;
    private CircuitKeybindUIState state;
    private CircuitKeybindTarget target;
    private GameTime lastUpdate = new();
    private bool suppressKeyboardUntilReleased;
    public bool IsOpen => userInterface?.CurrentState != null;
    public bool IsListening => IsOpen && state.IsListening;

    public override void Load() => On_PlayerInput.UpdateInput += SuppressBindingInput;

    private void SuppressBindingInput(On_PlayerInput.orig_UpdateInput orig)
    {
        orig();
        if (IsListening)
            suppressKeyboardUntilReleased = true;
        else if (Keyboard.GetState().GetPressedKeys().Length == 0)
            suppressKeyboardUntilReleased = false;
        if (!suppressKeyboardUntilReleased)
            return;
        // The UI reads the raw keyboard itself. Keep the captured press out of
        // vanilla controls (including quick heal, hotbar, map and chat) until it
        // is released, while preserving mouse input for UI buttons.
        Main.keyState = Main.oldKeyState = default;
        ClearKeyboardTriggers(PlayerInput.Triggers.Current);
        ClearKeyboardTriggers(PlayerInput.Triggers.JustPressed);
        ClearKeyboardTriggers(PlayerInput.Triggers.JustReleased);
    }

    private static void ClearKeyboardTriggers(TriggersSet triggers)
    {
        foreach (string key in triggers.KeyStatus.Keys.ToArray())
            if (key is not ("MouseLeft" or "MouseRight" or "MouseMiddle" or "MouseXButton1" or "MouseXButton2"))
                triggers.KeyStatus[key] = false;
    }

    public override void PostSetupContent()
    {
        userInterface = new UserInterface();
        state = new CircuitKeybindUIState();
        state.Activate();
    }

    internal bool Open(int i, int j)
    {
        if (Main.gameMenu || Main.LocalPlayer.dead || Main.blockInput || Main.drawingPlayerChat
            || Main.editSign || Main.editChest || Main.mapFullscreen
            || !Main.LocalPlayer.IsInTileInteractionRange(i, j, TileReachCheckSettings.Simple)
            || !YardSystem.Instance.TryCreateCircuitTarget(i, j, out target))
            return false;
        state.SetTarget(target);
        userInterface.SetState(state);
        return true;
    }

    public void Close() => userInterface?.SetState(null);

    public override void OnWorldUnload()
    {
        Close();
        suppressKeyboardUntilReleased = false;
    }

    public override void Unload()
    {
        On_PlayerInput.UpdateInput -= SuppressBindingInput;
        Close();
        userInterface = null;
        state = null;
    }

    public override void UpdateUI(GameTime gameTime)
    {
        lastUpdate = gameTime;
        if (!IsOpen)
            return;
        if (Main.gameMenu || Main.LocalPlayer.dead || Main.LocalPlayer.ghost
            || Main.drawingPlayerChat || Main.editSign || Main.editChest || Main.mapFullscreen
            || !YardSystem.Instance.TryResolveCircuitTarget(target, out Point storage, out _, out _)
            || !Main.LocalPlayer.IsInTileInteractionRange(storage.X, storage.Y, TileReachCheckSettings.Simple)) {
            Close();
            return;
        }
        if (!Main.hasFocus)
            state.CancelListening();
        else if (!state.CaptureKeyboard()) {
            Close();
            return;
        }
        userInterface.Update(gameTime);
    }

    public override void ModifyInterfaceLayers(List<GameInterfaceLayer> layers)
    {
        if (!IsOpen)
            return;
        int index = layers.FindIndex(layer => layer.Name == "Vanilla: Mouse Text");
        layers.Insert(index < 0 ? layers.Count : index, new LegacyGameInterfaceLayer(
            "TerrariaDynamics: Circuit key binding", () => {
                if (IsOpen)
                    userInterface.Draw(Main.spriteBatch, lastUpdate);
                return true;
            }, InterfaceScaleType.UI));
    }
}
