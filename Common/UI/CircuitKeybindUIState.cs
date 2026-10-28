using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Terraria;
using Terraria.GameContent.UI.Elements;
using Terraria.Localization;
using Terraria.ModLoader;
using Terraria.UI;
using TerrariaDynamics.Common.Circuitry;
using TerrariaDynamics.Common.Players;

namespace TerrariaDynamics.Common.UI;

internal sealed class CircuitKeybindUIState : UIState
{
    private UIPanel panel;
    private UIText bindingLabel;
    private UITextPanel<string> bindButton;
    private KeyboardState previousKeys;
    private CircuitKeybindTarget target;
    public bool IsListening { get; private set; }
    private CircuitKeybindPlayer Bindings => Main.LocalPlayer.GetModPlayer<CircuitKeybindPlayer>();

    internal static string Text(string key, params object[] args) =>
        Language.GetTextValue("Mods.TerrariaDynamics.CircuitKeybinds." + key, args);

    public override void OnInitialize()
    {
        panel = new UIPanel { HAlign = 0.5f, VAlign = 0.5f };
        panel.Width.Set(420, 0);
        panel.Height.Set(260, 0);
        panel.BackgroundColor = new Color(33, 43, 69, 240);
        Append(panel);

        var title = new UIText(Text("Title"), 1.1f) { HAlign = 0.5f };
        title.Top.Set(8, 0);
        panel.Append(title);

        bindingLabel = new UIText(string.Empty) { HAlign = 0.5f };
        bindingLabel.Top.Set(48, 0);
        panel.Append(bindingLabel);

        bindButton = Button("Bind", 83);
        bindButton.OnLeftClick += (_, _) => {
            // Ignore keys already held when the button is clicked.
            previousKeys = Keyboard.GetState();
            IsListening = true;
            Refresh();
        };
        Button("Clear", 132).OnLeftClick += (_, _) => {
            Bindings.SetBinding(target, Keys.None);
            CancelListening();
        };
        Button("Close", 181).OnLeftClick += (_, _) =>
            ModContent.GetInstance<CircuitKeybindUISystem>().Close();
    }

    private UITextPanel<string> Button(string key, float top)
    {
        var button = new UITextPanel<string>(Text(key)) { HAlign = 0.5f };
        button.Width.Set(370, 0);
        button.Height.Set(40, 0);
        button.Top.Set(top, 0);
        panel.Append(button);
        return button;
    }

    public void SetTarget(CircuitKeybindTarget newTarget)
    {
        target = newTarget;
        CancelListening();
    }

    public void CancelListening()
    {
        IsListening = false;
        previousKeys = Keyboard.GetState();
        Refresh();
    }

    private void Refresh()
    {
        Keys key = Bindings.GetBinding(target);
        bindingLabel.SetText(Text("CurrentKey", key == Keys.None ? Text("Unbound") : key.ToString()));
        bindButton.SetText(Text(IsListening ? "Listening" : "Bind"));
    }

    // Read raw keyboard input in UpdateUI so binding also works with vanilla
    // autopause. Player updates do not run while that pauses world updates.
    public bool CaptureKeyboard()
    {
        KeyboardState current = Keyboard.GetState();
        bool escape = current.IsKeyDown(Keys.Escape) && previousKeys.IsKeyUp(Keys.Escape);
        if (IsListening) {
            if (escape)
                CancelListening();
            else {
                foreach (Keys key in current.GetPressedKeys()) {
                    if (previousKeys.IsKeyUp(key) && CircuitKeybindPlayer.IsBindableKey(key)) {
                        Bindings.SetBinding(target, key);
                        CancelListening();
                        break;
                    }
                }
            }
        }
        else if (escape)
            return false;
        previousKeys = current;
        return true;
    }

    public override void Update(GameTime gameTime)
    {
        if (IsListening || panel.ContainsPoint(Main.MouseScreen))
            Main.LocalPlayer.mouseInterface = true;
        base.Update(gameTime);
    }
}
