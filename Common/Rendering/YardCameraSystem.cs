using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using Terraria;
using Terraria.GameInput;
using Terraria.Graphics;
using Terraria.Graphics.Capture;
using Terraria.Graphics.Effects;
using Terraria.ModLoader;
using TerrariaDynamics.Common.Players;
using TerrariaDynamics.Common.TileYards;

namespace TerrariaDynamics.Common.Rendering;

// Render an unrotated, diagonal-sized world, then rotate the complete scene.
// This includes native tile caches, skies, water, particles and scene filters.
// UI is drawn afterwards with normal dimensions; world UI uses the same view.
public sealed class YardCameraSystem : ModSystem
{
    private readonly List<ILHook> matrixHooks = new();
    private YardSceneTargets targets;
    private YardDeepBindingPlayer binding;
    private uint bindingRevision;
    private Vector2 anchor;
    private float rotation;
    private Point screenSize;
    private bool capturing;
    private bool rotateOverlays;
    private bool drawingMap;
    private SpriteViewMatrix originalGameView;
    private SpriteViewMatrix originalBackgroundView;
    private RenderTargetBinding[] destination;
    private Viewport destinationViewport;
    private On_Main.orig_DrawMap deferredMap;
    private GameTime deferredMapTime;
    private static readonly FieldInfo OriginalMouseX = InputField("_originalMouseX");
    private static readonly FieldInfo OriginalMouseY = InputField("_originalMouseY");
    private static readonly FieldInfo OriginalLastMouseX = InputField("_originalLastMouseX");
    private static readonly FieldInfo OriginalLastMouseY = InputField("_originalLastMouseY");

    private static FieldInfo InputField(string name) => typeof(PlayerInput)
        .GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new NotSupportedException($"Unsupported tModLoader mouse cache: {name}.");

    private bool ViewActive => binding != null && binding.Revision == bindingRevision && binding.IsBound
        && !Main.gameMenu && !Main.mapFullscreen && Main.LocalPlayer.active
        && !Main.LocalPlayer.dead && !Main.LocalPlayer.ghost;

    public override void Load()
    {
        if (Main.dedServ)
            return;
        On_Main.DoDraw_UpdateCameraPosition += PrepareScene;
        IL_Main.DoDraw += PatchScene;
        On_FilterManager.EndCapture += FinishFilters;
        On_PlayerInput.SetZoom_MouseInWorld += MapWorldMouse;
        On_PlayerInput.SetZoom_Scaled += KeepSceneDimensions;
        On_PlayerInput.SetZoom_Unscaled += KeepUnscaledSceneDimensions;
        On_Main.DrawMap += DrawMap;
        On_Main.DrawInterface += DrawInterface;
        foreach (string property in new[] { nameof(SpriteViewMatrix.ZoomMatrix), nameof(SpriteViewMatrix.TransformationMatrix) })
            matrixHooks.Add(new ILHook(typeof(SpriteViewMatrix).GetProperty(property).GetMethod, PatchWorldOverlayMatrix));
    }

    public override void Unload()
    {
        if (Main.dedServ)
            return;
        On_Main.DoDraw_UpdateCameraPosition -= PrepareScene;
        IL_Main.DoDraw -= PatchScene;
        On_FilterManager.EndCapture -= FinishFilters;
        On_PlayerInput.SetZoom_MouseInWorld -= MapWorldMouse;
        On_PlayerInput.SetZoom_Scaled -= KeepSceneDimensions;
        On_PlayerInput.SetZoom_Unscaled -= KeepUnscaledSceneDimensions;
        On_Main.DrawMap -= DrawMap;
        On_Main.DrawInterface -= DrawInterface;
        foreach (ILHook hook in matrixHooks)
            hook.Dispose();
        matrixHooks.Clear();
        ClearWorld();
    }

    public override void ClearWorld()
    {
        RestoreScene();
        binding = null;
        rotateOverlays = false;
        deferredMap = null;
        YardSceneTargets previous = targets;
        targets = null;
        if (previous != null)
            Main.QueueMainThreadAction(previous.Dispose);
    }

    public override void OnWorldUnload() => ClearWorld();

    internal void Draw(On_Main.orig_DoDraw orig, Main main, GameTime gameTime)
    {
        rotateOverlays = false;
        deferredMap = null;
        try {
            orig(main, gameTime);
        }
        catch {
            binding = null;
            throw;
        }
        finally {
            // Exceptions and early returns may bypass the normal composite.
            RestoreScene();
            rotateOverlays = false;
            deferredMap = null;
        }
    }

    private void PrepareScene(On_Main.orig_DoDraw_UpdateCameraPosition orig)
    {
        orig();
        Main main = Main.instance;
        if (Main.gameMenu || Main.mapFullscreen || CaptureManager.Instance.Active
            || !Main.LocalPlayer.active || Main.LocalPlayer.dead || Main.LocalPlayer.ghost
            || !Main.LocalPlayer.TryGetModPlayer(out YardDeepBindingPlayer current) || !current.IsBound) {
            binding = null;
            targets?.Dispose();
            targets = null;
            return;
        }

        binding = current;
        bindingRevision = current.Revision;
        YardCoordinates pose = current.BoundYard.RenderCoordinates;
        anchor = pose.LocalToWorld(current.LocalAnchor);
        rotation = pose.Rotation;
        screenSize = new Point(Main.screenWidth, Main.screenHeight);
        // The square contains every rotation of the viewport. Keep its size
        // stable as the Yard turns, avoiding render-target allocation per frame.
        int size = ((int)MathF.Ceiling(new Vector2(screenSize.X, screenSize.Y).Length()) + 19) / 16 * 16;
        bool cached = !Main.drawToScreen;
        GraphicsDevice device = main.GraphicsDevice;
        if (targets == null || targets.Size != size || targets.Margin != Main.offScreenRange
            || targets.CachedTiles != cached || targets.Scene.IsDisposed || targets.Scene.IsContentLost) {
            targets?.Dispose();
            targets = new YardSceneTargets(device, size, Main.offScreenRange, cached);
        }
        destination = device.GetRenderTargets();
        destinationViewport = device.Viewport;
        originalGameView = Main.GameViewMatrix;
        originalBackgroundView = Main.BackgroundViewMatrix;
        targets.Swap(main);
        capturing = true;
        Main.screenWidth = Main.screenHeight = size;
        Main.screenPosition = anchor - new Vector2(size / 2f);
        Main.GameViewMatrix = SceneView(device, originalGameView, size);
        Main.BackgroundViewMatrix = SceneView(device, originalBackgroundView, size);
        MapMouse();
    }

    private static SpriteViewMatrix SceneView(GraphicsDevice device, SpriteViewMatrix original, int size)
    {
        var view = new SpriteViewMatrix(device) { Zoom = original.Zoom, Effects = original.Effects };
        view.SetViewportOverride(new Viewport(0, 0, size, size));
        return view;
    }

    private void PatchScene(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchCall<Main>("UpdateAtmosphereTransparencyToSkyColor")))
            throw new NotSupportedException("Unsupported tModLoader world scene start.");
        cursor.EmitDelegate<Action>(() => {
            if (capturing)
                Main.instance.GraphicsDevice.SetRenderTarget(targets.Scene);
        });
        // Enlarging the scene must not increase the user's minimum zoom.
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchLdsfld<Main>(nameof(Main.screenWidth))))
            throw new NotSupportedException("Unsupported tModLoader horizontal camera zoom.");
        cursor.EmitDelegate<Func<int, int>>(width => capturing ? screenSize.X : width);
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchLdsfld<Main>(nameof(Main.screenHeight))))
            throw new NotSupportedException("Unsupported tModLoader vertical camera zoom.");
        cursor.EmitDelegate<Func<int, int>>(height => capturing ? screenSize.Y : height);
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchCall(typeof(SystemLoader), nameof(SystemLoader.ModifyTransformMatrix))))
            throw new NotSupportedException("Unsupported tModLoader camera transform setup.");
        cursor.EmitDelegate<Action>(MapMouse);

        int end = -1;
        while (cursor.TryGotoNext(MoveType.After, i => i.MatchCallvirt<FilterManager>(nameof(FilterManager.EndCapture))))
            end = cursor.Index;
        if (end < 0)
            throw new NotSupportedException("Unsupported tModLoader world scene end.");
        cursor.Goto(end);
        if (!cursor.TryGotoNext(MoveType.Before, i => i.MatchLdsfld<Main>(nameof(Main.hideUI))))
            throw new NotSupportedException("Unsupported tModLoader world/UI boundary.");
        cursor.MoveAfterLabels();
        cursor.EmitDelegate<Action>(CompositeScene);
        if (!cursor.TryGotoNext(MoveType.After, i => i.MatchCall<Main>("DrawItemTextPopups")))
            throw new NotSupportedException("Unsupported tModLoader world text boundary.");
        // The following zoom batch contains a screen-wide black fade, not a
        // world overlay. Interface layers re-enable the view when drawn below.
        cursor.EmitDelegate<Action>(() => rotateOverlays = false);
    }

    private void FinishFilters(On_FilterManager.orig_EndCapture orig, FilterManager self,
        RenderTarget2D finalTexture, RenderTarget2D screenTarget, RenderTarget2D screenTargetSwap, Color clearColor) =>
        orig(self, capturing && finalTexture == null ? targets.Scene : finalTexture, screenTarget, screenTargetSwap, clearColor);

    private void CompositeScene()
    {
        if (!capturing)
            return;
        Vector2 pivot = Vector2.Transform(anchor - Main.screenPosition, Main.GameViewMatrix.TransformationMatrix);
        RestoreScene();
        Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.Opaque, SamplerState.PointClamp,
            DepthStencilState.None, RasterizerState.CullNone);
        Main.spriteBatch.Draw(targets.Scene, new Vector2(screenSize.X, screenSize.Y) / 2, null,
            Color.White, -rotation, pivot, 1, SpriteEffects.None, 0);
        Main.spriteBatch.End();
        rotateOverlays = true;
        if (deferredMap != null) {
            // The overlay map is called within the world pass by vanilla. Draw
            // it after compositing so all map/UI modes retain world north.
            Main.spriteBatch.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend);
            DrawMap(deferredMap, Main.instance, deferredMapTime);
            Main.spriteBatch.End();
            deferredMap = null;
        }
    }

    private void RestoreScene()
    {
        if (!capturing)
            return;
        capturing = false;
        originalGameView.Zoom = Main.GameViewMatrix.Zoom;
        originalGameView.Effects = Main.GameViewMatrix.Effects;
        originalBackgroundView.Zoom = Main.BackgroundViewMatrix.Zoom;
        originalBackgroundView.Effects = Main.BackgroundViewMatrix.Effects;
        Main.GameViewMatrix = originalGameView;
        Main.BackgroundViewMatrix = originalBackgroundView;
        targets.Swap(Main.instance);
        Main.screenWidth = screenSize.X;
        Main.screenHeight = screenSize.Y;
        Main.screenPosition = anchor - new Vector2(screenSize.X, screenSize.Y) / 2;
        Main.renderNow = true; // Normal targets may be far behind after exiting.
        GraphicsDevice device = Main.instance.GraphicsDevice;
        device.SetRenderTargets(destination);
        device.Viewport = destinationViewport;
        MapMouse();
    }

    private void PatchWorldOverlayMatrix(ILContext il)
    {
        var cursor = new ILCursor(il);
        if (!cursor.TryGotoNext(MoveType.Before, i => i.OpCode == OpCodes.Ret))
            throw new NotSupportedException("Unsupported tModLoader view matrix getter.");
        cursor.Emit(OpCodes.Ldarg_0);
        cursor.EmitDelegate<Func<Matrix, SpriteViewMatrix, Matrix>>((matrix, view) => {
            if (!rotateOverlays || !ViewActive || !ReferenceEquals(view, Main.GameViewMatrix))
                return matrix;
            Vector2 center = new(screenSize.X / 2f, screenSize.Y / 2f);
            return matrix * Matrix.CreateTranslation(-center.X, -center.Y, 0)
                * Matrix.CreateRotationZ(-rotation) * Matrix.CreateTranslation(center.X, center.Y, 0);
        });
    }

    private void DrawMap(On_Main.orig_DrawMap orig, Main main, GameTime gameTime)
    {
        if (capturing) {
            deferredMap = orig;
            deferredMapTime = gameTime;
            return;
        }
        bool previous = rotateOverlays;
        bool previousMap = drawingMap;
        rotateOverlays = false;
        drawingMap = true;
        try {
            orig(main, gameTime);
        }
        finally {
            rotateOverlays = previous;
            drawingMap = previousMap;
        }
    }

    private void DrawInterface(On_Main.orig_DrawInterface orig, Main main, GameTime gameTime)
    {
        bool previous = rotateOverlays;
        rotateOverlays = ViewActive;
        try {
            orig(main, gameTime);
        }
        finally {
            rotateOverlays = previous;
        }
    }

    private void KeepSceneDimensions(On_PlayerInput.orig_SetZoom_Scaled orig, float scale)
    {
        orig(scale);
        if (capturing)
            Main.screenWidth = Main.screenHeight = targets.Size;
    }

    private void KeepUnscaledSceneDimensions(On_PlayerInput.orig_SetZoom_Unscaled orig)
    {
        orig();
        if (capturing)
            Main.screenWidth = Main.screenHeight = targets.Size;
    }

    private void MapWorldMouse(On_PlayerInput.orig_SetZoom_MouseInWorld orig)
    {
        orig();
        MapMouse();
    }

    private void MapMouse()
    {
        if (!ViewActive || drawingMap)
            return;
        Vector2 Map(FieldInfo x, FieldInfo y)
        {
            Vector2 pixel = new((int)x.GetValue(null), (int)y.GetValue(null));
            Vector2 center = new(screenSize.X / 2f, screenSize.Y / 2f);
            // The scene is rotated after its native zoom. Undo in reverse order.
            Vector2 delta = Vector2.Transform(pixel - center, Matrix.CreateRotationZ(rotation));
            Vector2 world = anchor + delta / Main.GameViewMatrix.Zoom;
            Vector2 mouse = world - Main.screenPosition;
            if (Main.LocalPlayer.gravDir == -1)
                mouse.Y = Main.screenHeight - mouse.Y;
            return mouse;
        }
        Vector2 current = Map(OriginalMouseX, OriginalMouseY);
        Vector2 previous = Map(OriginalLastMouseX, OriginalLastMouseY);
        Main.mouseX = (int)MathF.Round(current.X);
        Main.mouseY = (int)MathF.Round(current.Y);
        Main.lastMouseX = (int)MathF.Round(previous.X);
        Main.lastMouseY = (int)MathF.Round(previous.Y);
    }
}
