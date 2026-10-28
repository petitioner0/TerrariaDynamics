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
    private bool loggedDraw;
    public bool IsOpen => userInterface?.CurrentState != null;
    public bool IsListening => IsOpen && state.IsListening;

    internal static void Trace(string message) => ModContent.GetInstance<CircuitKeybindUISystem>().Mod.Logger.Info(
        $"[CircuitUI tick={Main.GameUpdateCount}] {message}");

    private static string InputState => $"menu={Main.gameMenu}, dead={Main.LocalPlayer.dead}, ghost={Main.LocalPlayer.ghost}, "
        + $"blockInput={Main.blockInput}, chat={Main.drawingPlayerChat}, sign={Main.editSign}, chest={Main.editChest}, "
        + $"map={Main.mapFullscreen}, focus={Main.hasFocus}";

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
        Trace("Diagnostics ready; UI state initialized.");
    }

    internal bool Open(int i, int j)
    {
        Trace($"Open requested: cell=({i},{j}), initialized={userInterface != null && state != null}; {InputState}");
        if (Main.gameMenu || Main.LocalPlayer.dead || Main.blockInput || Main.drawingPlayerChat
            || Main.editSign || Main.editChest || Main.mapFullscreen) {
            Trace("Open rejected: input/UI guard.");
            return false;
        }
        if (!Main.LocalPlayer.IsInTileInteractionRange(i, j, TileReachCheckSettings.Simple)) {
            Trace("Open rejected: outside interaction range.");
            return false;
        }
        if (!YardSystem.Instance.TryCreateCircuitTarget(i, j, out target, allowWorld: true)) {
            Trace("Open rejected: cannot create target (requires a valid circuit tile in the world or a loaded Yard).");
            return false;
        }
        state.SetTarget(target);
        userInterface.SetState(state);
        loggedDraw = false;
        Trace($"Open succeeded: yard={target.YardId}, cell={target.Cell}, tile={target.TileName}, isOpen={IsOpen}");
        return true;
    }

    public void Close(string reason = "explicit close")
    {
        if (IsOpen)
            Trace($"Close: {reason}; {InputState}");
        userInterface?.SetState(null);
    }

    public override void OnWorldUnload()
    {
        Close("world unload");
        suppressKeyboardUntilReleased = false;
    }

    public override void Unload()
    {
        On_PlayerInput.UpdateInput -= SuppressBindingInput;
        Close("mod unload");
        userInterface = null;
        state = null;
    }

    public override void UpdateUI(GameTime gameTime)
    {
        lastUpdate = gameTime;
        if (!IsOpen)
            return;
        if (Main.gameMenu || Main.LocalPlayer.dead || Main.LocalPlayer.ghost
            || Main.drawingPlayerChat || Main.editSign || Main.editChest || Main.mapFullscreen) {
            Close("input/UI guard during UpdateUI");
            return;
        }
        if (!YardSystem.Instance.TryResolveCircuitTarget(target, out Point storage, out _, out _, allowWorld: true)) {
            Close("target no longer resolves");
            return;
        }
        if (!Main.LocalPlayer.IsInTileInteractionRange(storage.X, storage.Y, TileReachCheckSettings.Simple)) {
            Close($"outside interaction range at {storage}, playerCenter={Main.LocalPlayer.Center}");
            return;
        }
        if (!Main.hasFocus)
            state.CancelListening();
        else if (!state.CaptureKeyboard()) {
            Close("Escape");
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
                if (IsOpen) {
                    userInterface.Draw(Main.spriteBatch, lastUpdate);
                    if (!loggedDraw) {
                        Trace($"UI drawn: screen={Main.screenWidth}x{Main.screenHeight}, uiScale={Main.UIScale}");
                        loggedDraw = true;
                    }
                }
                return true;
            }, InterfaceScaleType.UI));
    }
}
